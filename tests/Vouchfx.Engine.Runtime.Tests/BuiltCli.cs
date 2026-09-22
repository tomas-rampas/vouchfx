// The single resolver for the built `vouchfx` CLI assembly used by every test in this project
// that invokes the CLI as a child process.
//
// It replaces four copies of the same walk — one per defining class — that had drifted: three
// derived the configuration from the test assembly's own output path, while the fourth hard-coded
// `Release` and so failed every Debug run of the docker lane (#530).
//
// RelativeToRepoRoot (#552) is the same fix applied to every OTHER absolute path these docker-lane
// tests splice into an Assert message or an ITestOutputHelper line — the built CLI's own path was
// only the first offender the sweep in #530 caught.

using System.IO;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Locates the built <c>vouchfx</c> CLI assembly beside this test assembly's own configuration.
/// </summary>
internal static class BuiltCli
{
    /// <summary>
    /// Returns the absolute path of the built CLI assembly for the configuration this test
    /// assembly was itself built in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The configuration is read from this assembly's own output path rather than assumed, so a
    /// Debug test run drives the Debug CLI. A missing artefact FAILS with the command that
    /// produces it — never skips: a silently-skipped drill is indistinguishable from a passing one.
    /// CI cannot reach that failure, because every job that runs a caller builds the whole
    /// solution — <c>Vouchfx.Cli</c> included — in Release before its first test step. In
    /// <c>.github/workflows/build.yml</c>, the blocking <c>build</c> job (which runs the callers
    /// carrying no <c>requires=docker</c> trait) has its <c>Build (Release, 0-warning gate)</c>
    /// step before <c>Unit tests (requires!=docker)</c>, and the <c>integration</c> job (which
    /// runs the docker-gated ones) has <c>Build (Release)</c> before
    /// <c>Integration tests (requires=docker)</c>. So it is a local-run guard.
    /// </para>
    /// <para>
    /// The failure message names the artefact RELATIVE to the repository root, never the absolute
    /// path this method returns: these assertions print into public CI job logs, and an absolute
    /// path there publishes the layout of whatever host ran the job.
    /// </para>
    /// </remarks>
    internal static string Resolve()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(BuiltCli).Assembly.Location)!;

        // …/tests/<project>/bin/<configuration>/net8.0 — the configuration is the name of this
        // directory's parent, i.e. the assembly file's grandparent.
        var configuration = Path.GetFileName(Path.GetDirectoryName(assemblyDirectory))!;

        var repoRoot = ResolveRepoRoot();

        var cli = Path.Combine(
            repoRoot, "src", "Cli", "Vouchfx.Cli", "bin", configuration, "net8.0", "vouchfx.dll");

        Assert.True(
            File.Exists(cli),
            $"The built CLI was not found at '{RelativeToRepoRoot(cli)}'. Build the solution first: "
            + $"dotnet build vouchfx.sln -c {configuration}");

        return cli;
    }

    /// <summary>
    /// Walks up from this test assembly's own build output to the repository root.
    /// </summary>
    /// <remarks>
    /// Walk up: net8.0 → &lt;configuration&gt; → bin → &lt;project&gt; → tests → repo root. The
    /// same shape ExamplesCompileTests.ResolveRepoRoot and Sprint11ReferenceCompileTests use — kept
    /// here as the one place <see cref="Resolve"/> and <see cref="RelativeToRepoRoot"/> both derive
    /// it from, so the two can never disagree about where the root is.
    /// </remarks>
    internal static string ResolveRepoRoot()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(BuiltCli).Assembly.Location)!;

        return Path.GetFullPath(
            Path.Combine(assemblyDirectory, "..", "..", "..", "..", ".."));
    }

    /// <summary>
    /// Renders <paramref name="absolutePath"/> relative to the repository root, for splicing into
    /// an <c>Assert</c> message or an <c>ITestOutputHelper</c> line.
    /// </summary>
    /// <remarks>
    /// These print into public CI job logs on the docker lane, and an absolute path there
    /// publishes the layout of whatever host ran the job (#498 class; #530; #552). Derived from the
    /// probed path, never spelled a second time, so the message can never name somewhere other than
    /// where the probe looked. Forward slashes so it reads identically on both platforms.
    /// <para>
    /// Also correct for a path OUTSIDE the repository (for example one under the system temp
    /// directory, where every drill's materialised suite lands): <see cref="Path.GetRelativePath"/>
    /// climbs back up to the nearest common ancestor with content-free <c>..</c> segments — they
    /// never spell out the repository's own ancestry — and prints only <paramref name="absolutePath"/>'s
    /// tail beyond that ancestor.
    /// </para>
    /// </remarks>
    internal static string RelativeToRepoRoot(string absolutePath) =>
        Path.GetRelativePath(ResolveRepoRoot(), absolutePath).Replace('\\', '/');
}
