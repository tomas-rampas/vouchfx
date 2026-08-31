// Vouchfx.Cli.Tests — WatchSession tests (S08-C-01, watch mode). No Docker.
//
// WatchSession is the testable core of `vouchfx run <file> --watch`: it owns ONE topology
// across re-runs and decides, on each file change, whether to RE-USE the kept topology (the
// topology fingerprint the compile seam returned is unchanged) or REBUILD it.  These tests
// exercise that decision with FAKES for the build-topology + run + compile seams, so no
// FileSystemWatcher and no container is ever touched.
//
// The fingerprint is OPAQUE to the session — it compares two strings and nothing more — so these
// tests hand it arbitrary tokens. What the production seam actually digests (the `environment`
// block AND every other input the topology is built from, including the resource names the steps
// target) is TopologyFingerprintTests' subject, and the wiring that carries it is
// WatchPreTopologyGateTests'.
//
// The acceptance is test #2: a file change with an UNCHANGED fingerprint re-runs WITHOUT a
// topology rebuild (build count stays 1); test #3 proves a CHANGED fingerprint rebuilds.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Cli.Watch;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class WatchSessionTests
{
    // A fake topology handle: each build mints a new instance with a unique id, so a test
    // can assert whether a NEW topology was built (id changed) or the SAME one re-used.
    private sealed class FakeTopology
    {
        private static int s_next;
        public int Id { get; } = Interlocked.Increment(ref s_next);
    }

    // Records every build / run / dispose against the fake seams so a test can assert counts
    // and ordering without any real I/O.
    private sealed class Recorder
    {
        public int BuildCount;
        public int DisposeCount;
        public readonly List<int> RunsAgainstTopologyId = new();

        // The resetAndReseed flag the run seam was invoked with, in run order (S08-T10, B2):
        // FALSE on the first run against a just-built topology, TRUE on a reuse run.
        public readonly List<bool> RunResetAndReseed = new();
        public readonly List<string> Reports = new();
    }

    // Builds a WatchSession wired to fake seams.  `compile` maps a file-content string to a
    // WatchCompileResult (success → a topology fingerprint + an opaque compiled payload, here the
    // content itself; failure → an error message), letting a test feed successive file contents and
    // control the reuse decision directly.
    private static WatchSession<FakeTopology> BuildSession(
        Recorder rec,
        Func<string, WatchCompileResult> compile)
    {
        return new WatchSession<FakeTopology>(
            compile: compile,
            buildTopologyAsync: (_, _) =>
            {
                rec.BuildCount++;
                return Task.FromResult(new FakeTopology());
            },
            runAgainstTopologyAsync: (topology, _, resetAndReseed, _) =>
            {
                rec.RunsAgainstTopologyId.Add(topology.Id);
                rec.RunResetAndReseed.Add(resetAndReseed);
                return Task.CompletedTask;
            },
            disposeTopologyAsync: _ =>
            {
                rec.DisposeCount++;
                return Task.CompletedTask;
            },
            report: rec.Reports.Add);
    }

    // A compile seam that always succeeds with the given fingerprint, threading the raw content
    // through as the opaque compiled payload.
    private static Func<string, WatchCompileResult> CompileWithFingerprint(
        Func<string, string> fingerprintOf) =>
        content => WatchCompileResult.Success(fingerprintOf(content), compiled: content);

    [Fact]
    public async Task FirstRun_BuildsTopologyOnce_AndRunsScenario()
    {
        var rec = new Recorder();
        // The fingerprint is constant — irrelevant for the first run.
        var session = BuildSession(rec, CompileWithFingerprint(_ => "env-A"));

        await session.OnChangeAsync("first content", CancellationToken.None);

        Assert.Equal(1, rec.BuildCount);
        Assert.Single(rec.RunsAgainstTopologyId);
        Assert.Equal(0, rec.DisposeCount);
    }

    [Fact]
    public async Task UnchangedEnvironment_ReusesTopology_WithoutRebuilding()
    {
        // ACCEPTANCE: two edits, SAME environment block (different steps) → the second edit
        // re-runs against the KEPT topology.  Build count stays 1; the same topology id is
        // used for both runs; the old topology is NEVER disposed mid-session.
        var rec = new Recorder();
        var session = BuildSession(rec, CompileWithFingerprint(_ => "env-A"));

        await session.OnChangeAsync("steps: v1", CancellationToken.None);
        await session.OnChangeAsync("steps: v2 (same env)", CancellationToken.None);

        Assert.Equal(1, rec.BuildCount); // NO rebuild on the unchanged-env re-run.
        Assert.Equal(2, rec.RunsAgainstTopologyId.Count);
        Assert.Equal(rec.RunsAgainstTopologyId[0], rec.RunsAgainstTopologyId[1]); // same topology.
        Assert.Equal(0, rec.DisposeCount); // kept topology not disposed between runs.
    }

    [Fact]
    public async Task ChangedEnvironment_DisposesOldTopology_AndRebuilds()
    {
        var rec = new Recorder();
        // The fingerprint follows the content: "A" → env-A, anything else → env-B.
        var session = BuildSession(
            rec,
            CompileWithFingerprint(content => content == "A" ? "env-A" : "env-B"));

        await session.OnChangeAsync("A", CancellationToken.None);
        await session.OnChangeAsync("B", CancellationToken.None);

        Assert.Equal(2, rec.BuildCount); // env changed → second build.
        Assert.Equal(2, rec.RunsAgainstTopologyId.Count);
        Assert.NotEqual(rec.RunsAgainstTopologyId[0], rec.RunsAgainstTopologyId[1]); // different topology.
        Assert.Equal(1, rec.DisposeCount); // old topology disposed before the rebuild.
    }

    [Fact]
    public async Task CompileError_OnReRun_IsReported_AndKeepsWatching_WithoutDisposingTopology()
    {
        var rec = new Recorder();
        // First content compiles; second content fails to compile (e.g. a typo just saved).
        var session = BuildSession(
            rec,
            content => content == "good"
                ? WatchCompileResult.Success("env-A", compiled: content)
                : WatchCompileResult.Failure("Parse / AST error: boom"));

        await session.OnChangeAsync("good", CancellationToken.None);
        await session.OnChangeAsync("bad", CancellationToken.None);

        // The error surfaced as a report…
        Assert.Contains(rec.Reports, r => r.Contains("Parse / AST error: boom", StringComparison.Ordinal));
        // …the loop did NOT crash and did NOT tear down the kept topology…
        Assert.Equal(1, rec.BuildCount);
        Assert.Equal(0, rec.DisposeCount);
        // …and only the first (good) content actually ran.
        Assert.Single(rec.RunsAgainstTopologyId);

        // A subsequent GOOD save re-runs against the still-alive topology (no rebuild).
        await session.OnChangeAsync("good", CancellationToken.None);
        Assert.Equal(1, rec.BuildCount);
        Assert.Equal(2, rec.RunsAgainstTopologyId.Count);
        Assert.Equal(rec.RunsAgainstTopologyId[0], rec.RunsAgainstTopologyId[1]);
    }

    [Fact]
    public async Task FirstRun_CompileError_ReportsAndBuildsNothing()
    {
        var rec = new Recorder();
        var session = BuildSession(
            rec,
            _ => WatchCompileResult.Failure("Schema invalid"));

        await session.OnChangeAsync("anything", CancellationToken.None);

        Assert.Contains(rec.Reports, r => r.Contains("Schema invalid", StringComparison.Ordinal));
        Assert.Equal(0, rec.BuildCount);
        Assert.Empty(rec.RunsAgainstTopologyId);
        Assert.Equal(0, rec.DisposeCount);
    }

    /// <summary>
    /// A REFUSED save — one a pre-topology gate rejected and whose diagnostic and event pair the
    /// compile seam has already rendered — builds nothing, runs nothing, leaves the kept topology
    /// alone, and reports NOTHING further (#370).
    /// </summary>
    /// <remarks>
    /// The last clause is the one that needs a test: <c>WatchCompileResult.Refused</c> carries a
    /// null <c>Error</c>, and the pre-#370 body was an unconditional <c>_report(compileResult.
    /// Error!)</c>. Left unguarded that either throws on the bang or prints an empty line under the
    /// rendered report, and either way the author's primary feedback channel gains noise that no
    /// other assertion here would notice. Everything else in this arm mirrors the failure case
    /// above, because "report and keep watching" is unchanged by where the refusal now happens.
    /// </remarks>
    [Fact]
    public async Task RefusedSave_IsNotReportedAgain_AndLeavesTheKeptTopologyAlone()
    {
        var rec = new Recorder();
        var session = BuildSession(
            rec,
            content => content == "good"
                ? WatchCompileResult.Success("env-A", compiled: content)
                : WatchCompileResult.Refused());

        await session.OnChangeAsync("good", CancellationToken.None);
        await session.OnChangeAsync("refused", CancellationToken.None);

        Assert.Empty(rec.Reports);
        Assert.Equal(1, rec.BuildCount);
        Assert.Equal(0, rec.DisposeCount);
        Assert.Single(rec.RunsAgainstTopologyId);

        // …and the next good save still re-uses the topology the refusal did not disturb.
        await session.OnChangeAsync("good", CancellationToken.None);
        Assert.Equal(1, rec.BuildCount);
        Assert.Equal(2, rec.RunsAgainstTopologyId.Count);
        Assert.Equal(rec.RunsAgainstTopologyId[0], rec.RunsAgainstTopologyId[1]);
    }

    [Fact]
    public async Task DisposeAsync_TearsDownTheKeptTopology()
    {
        var rec = new Recorder();
        var session = BuildSession(rec, CompileWithFingerprint(_ => "env-A"));

        await session.OnChangeAsync("content", CancellationToken.None);
        Assert.Equal(0, rec.DisposeCount);

        await session.DisposeAsync();
        Assert.Equal(1, rec.DisposeCount);

        // Idempotent: a second dispose is a no-op.
        await session.DisposeAsync();
        Assert.Equal(1, rec.DisposeCount);
    }

    [Fact]
    public async Task FirstRunIsBuild_RunsWithoutResetReseed_ReuseRunResetsAndReseeds()
    {
        // B2 (S08-T10): the FIRST run after a build must NOT reset/reseed (the just-built+seeded
        // topology already carries the fresh seed and no prior writes — a reset would truncate
        // the seed before run #1, diverging from a plain `vouchfx run` and breaking
        // schema-via-script.csharp).  A REUSE run (same env, kept topology with the previous
        // run's writes) MUST reset+reseed to restore the freshly-built baseline.
        var rec = new Recorder();
        var session = BuildSession(rec, CompileWithFingerprint(_ => "env-A"));

        await session.OnChangeAsync("steps: v1", CancellationToken.None); // build → first run.
        await session.OnChangeAsync("steps: v2 (same env)", CancellationToken.None); // reuse run.

        // Build path: NO rebuild between runs (the acceptance), AND the run seam saw the flags
        // in order: false (build/first run), then true (reuse run).
        Assert.Equal(1, rec.BuildCount);
        Assert.Equal(new[] { false, true }, rec.RunResetAndReseed);
    }

    [Fact]
    public async Task ChangedEnvironment_RebuildRun_DoesNotResetReseed()
    {
        // A changed environment REBUILDS the topology; that rebuild path is a fresh build (the
        // new topology was just built + seeded), so its run must NOT reset/reseed either — same
        // reasoning as the very first build (B2).
        var rec = new Recorder();
        var session = BuildSession(
            rec,
            CompileWithFingerprint(content => content == "A" ? "env-A" : "env-B"));

        await session.OnChangeAsync("A", CancellationToken.None); // build env-A → first run.
        await session.OnChangeAsync("B", CancellationToken.None); // env changed → rebuild → run.

        Assert.Equal(2, rec.BuildCount); // env changed → second build.
        // BOTH runs followed a build, so BOTH must skip reset/reseed.
        Assert.Equal(new[] { false, false }, rec.RunResetAndReseed);
    }
}
