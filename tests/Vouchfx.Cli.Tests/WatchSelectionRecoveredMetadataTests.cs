// Vouchfx.Cli.Tests — `--watch` selection vs issue #411's recovered metadata. No Docker.
//
// WHY THIS FILE EXISTS: issue #411 widened ScenarioSelector's metadata read from the built AST to
// `Ast?.Metadata ?? RecoveredMetadata`, and selection runs in RunCommand BEFORE the watch branch.
// WatchRunner.RunAsync refuses any selection whose count is not 1, so a broken sibling carrying the
// filter's own tag — newly matchable — took a FILTERED watch from 1 to 2 and turned a run that
// worked into exit 2. Measured on the built CLI at 3c8f6d5: `run <dir> --tag smoke --watch` over a
// directory pairing one good `smoke` file with one `smoke` file that parses and fails AstBuilder
// printed "--watch requires exactly one .e2e.yaml file (the selection resolved to 2)" and exited 2.
//
// Both directions are pinned here, because only the pair is evidence:
//   • FILTERED (the regression): the recovery must NOT reach watch's selection, so the good file is
//     watched and the loop runs.
//   • UNFILTERED (the control): a two-file directory resolved to 2 and exited 2 BEFORE #411 too —
//     a parse-failure is included there because no metadata filter is active, not because anything
//     was recovered — so that answer must stay exactly as it was.
//
// NO DOCKER, and the mechanism is deliberate rather than lucky: the watched file's `environment`
// carries a `${conn:...}` reference to an undeclared dependency, which EnvironmentMapper.Map
// rejects EAGERLY as Step 1 of SuiteTopology.StartAsync — before DCP is reached and before any
// container is created. WatchRunner's catch-all reports it and KEEPS WATCHING, so the loop is
// genuinely alive when the test cancels it. Any regression that made the watch path reach DCP would
// surface as this test timing out rather than as a silent Docker dependency.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Cli;
using Vouchfx.Cli.Selection;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class WatchSelectionRecoveredMetadataTests : IDisposable
{
    /// <summary>
    /// Tagged <c>smoke</c>, parses cleanly, and is then refused by <c>AstBuilder.Build</c> for an
    /// unregistered step type — failure class 4, the one class whose <c>metadata</c> block issue
    /// #411 recovers.
    /// </summary>
    private const string BrokenButTaggedScenario =
        "metadata:\n"
        + "  tags: [smoke]\n"
        + "steps:\n"
        + "  - id: x\n"
        + "    type: not-a-real-provider\n";

    /// <summary>
    /// Tagged <c>smoke</c> and genuinely runnable as far as the AST is concerned — the file a
    /// filtered watch is supposed to watch. Its <c>env:</c> names a dependency that does not exist,
    /// so <c>EnvironmentMapper.Map</c> throws before DCP and the watch loop reports and keeps
    /// watching without a container.
    /// </summary>
    private const string GoodTaggedScenarioThatCannotBuildATopology =
        "metadata:\n"
        + "  tags: [smoke]\n"
        + "environment:\n"
        + "  services:\n"
        + "    app:\n"
        + "      image: traefik/whoami:latest\n"
        + "      env:\n"
        + "        DB: \"${conn:nope}\"\n"
        + "steps:\n"
        + "  - id: ping\n"
        + "    type: http.rest\n"
        + "    target: app\n"
        + "    method: GET\n"
        + "    path: /\n"
        + "    expect:\n"
        + "      status: 200\n";

    private readonly string _root;

    public WatchSelectionRecoveredMetadataTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "vouchfx-watch-selection-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);

        // Ordinal-sorted discovery order puts the broken file first, so a selection that wrongly
        // includes it would also be the one WatchRunner reports on.
        File.WriteAllText(Path.Combine(_root, "a-broken.e2e.yaml"), BrokenButTaggedScenario);
        File.WriteAllText(
            Path.Combine(_root, "b-good.e2e.yaml"), GoodTaggedScenarioThatCannotBuildATopology);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the test.
        }
    }

    private static SelectionCriteria Criteria(params string[] tags) =>
        new(tags, Array.Empty<string>(), PathGlob: null, ChangedSinceRef: null);

    private Task<int> ExecuteWatchAsync(
        SelectionCriteria criteria, TextWriter output, CancellationToken cancellationToken) =>
        RunCommand.ExecuteAsync(
            path: _root,
            criteria: criteria,
            parallel: null,
            watch: true,
            failOnEnvironmentError: false,
            failOnInconclusive: false,
            htmlReportPath: null,
            junitReportPath: null,
            eventsReportPath: null,
            eventsStreamPath: null,
            decorate: false,
            output: output,
            telemetryHook: null,
            cancellationToken: cancellationToken);

    /// <summary>
    /// THE REGRESSION, in the direction it broke: a metadata-filtered watch over a directory whose
    /// broken file carries the very tag being filtered on must still resolve to the ONE good file
    /// and enter the watch loop.
    /// </summary>
    [Fact]
    public async Task FilteredWatch_WithABrokenSiblingCarryingTheSameTag_WatchesTheGoodFile()
    {
        // StringWriter is not thread-safe and RunAsync writes from its own continuations while the
        // poll loop below reads — same wrapping as WatchRunnerParseFailureTests, same shared lock.
        var sw = new StringWriter();
        var output = TextWriter.Synchronized(sw);
        using var cts = new CancellationTokenSource();

        var runTask = ExecuteWatchAsync(Criteria("smoke"), output, cts.Token);

        // Wait for the INITIAL run to have completed (the topology-build refusal is written after
        // it), not merely for the banner — cancelling mid-run would surface as an OperationCanceled
        // escaping RunAsync rather than the clean loop exit this test is about.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            string snapshot;
            lock (output)
            {
                snapshot = sw.ToString();
            }

            if (snapshot.Contains("--watch: error during run", StringComparison.Ordinal))
            {
                break;
            }

            if (snapshot.Contains("--watch requires exactly one", StringComparison.Ordinal))
            {
                Assert.Fail(
                    "the filtered watch selection resolved to more than one file — issue #411's "
                    + "recovered metadata reached the watch path (regression):\n" + snapshot);
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail(
                    "timed out waiting for the watch loop's initial run to report; the watch path "
                    + "may have reached DCP instead of failing at EnvironmentMapper.Map:\n"
                    + snapshot);
            }

            await Task.Delay(10);
        }

        cts.Cancel();
        var exitCode = await runTask;

        string rendered;
        lock (output)
        {
            rendered = sw.ToString();
        }

        // The loop ran and stopped cleanly — never the usage error the regression produced.
        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.DoesNotContain("--watch requires exactly one", rendered, StringComparison.Ordinal);

        // And it watched the GOOD file, not the broken one.
        Assert.Contains("Watching '", rendered, StringComparison.Ordinal);
        Assert.Contains("b-good.e2e.yaml", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("a-broken.e2e.yaml", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CONTROL, and it is what makes the row above a scoping rather than a widening: with NO
    /// metadata filter the parse-failure was always included — the recovery is not what puts it in
    /// the selection — so a two-file directory resolved to 2 and exited 2 before issue #411 and must
    /// still do so.
    /// </summary>
    [Fact]
    public async Task UnfilteredWatch_OverTwoFiles_StillRefusesAsAUsageError()
    {
        var output = new StringWriter();

        var exitCode = await ExecuteWatchAsync(
            Criteria(), output, CancellationToken.None);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains(
            "--watch requires exactly one .e2e.yaml file (the selection resolved to 2)",
            output.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The scoping is exactly one caller wide: the SAME directory and the SAME filter under the
    /// `run` path still sees the broken file, which is the whole of issue #411's fix on the
    /// selection side. Asserted at the selection tier rather than by running the suite, because the
    /// `run` path would need Docker.
    /// </summary>
    [Fact]
    public void TheRunPathStillSeesTheBrokenFileUnderTheSameFilter()
    {
        var discovered = ScenarioDiscovery.Discover(_root, ProviderRegistryFactory.BuildCoreRegistry());
        Assert.Equal(2, discovered.Count);

        var forRun = RunCommand.SelectScenarios(
            discovered, Criteria("smoke"), _root, matchRecoveredMetadata: true);
        Assert.Equal(2, forRun.Count);

        var forWatch = RunCommand.SelectScenarios(
            discovered, Criteria("smoke"), _root, matchRecoveredMetadata: false);
        var watched = Assert.Single(forWatch);
        Assert.EndsWith("b-good.e2e.yaml", watched.AbsolutePath, StringComparison.Ordinal);
    }
}
