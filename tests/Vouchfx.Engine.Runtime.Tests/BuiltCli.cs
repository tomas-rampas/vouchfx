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

using System;
using System.Buffers;
using System.IO;
using Vouchfx.TestSupport;
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
    /// The repository root, anchored on <c>vouchfx.sln</c> (#551).
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="Vouchfx.TestSupport.RepoRoot.Resolve()"/> — the one answer to
    /// "where is the repo" every test project in the solution now shares — rather than deriving
    /// its own fixed-depth walk from this assembly's build output, which is what this method used
    /// to do. Kept as a named wrapper (rather than inlining the call at each of
    /// <see cref="Resolve"/> and <see cref="RelativeToRepoRoot(string)"/>) purely so neither of those two
    /// — nor <c>BuiltCliTests</c>, which calls this directly — needed to change when the walk
    /// itself moved.
    /// </remarks>
    internal static string ResolveRepoRoot() => RepoRoot.Resolve();

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
    /// A path inside the repository keeps its useful repository-relative form, even when the
    /// checkout itself is under the system temp directory. Otherwise a path under that directory
    /// renders as <c>&lt;temp&gt;/</c> followed by its normalised tail: climbing to a common ancestor
    /// could otherwise expose the user profile containing temp (#591).
    /// </para>
    /// <para>
    /// Other paths outside the repository keep the tail <see cref="Path.GetRelativePath"/> can
    /// produce, without spelling the repository's ancestry. A rooted answer, including a normalised
    /// cross-volume answer, is withheld behind <see cref="OutsideRepositoryVolumeMarker"/>.
    /// <see cref="Render"/> retains the equality guard too, so Windows-style answers can be tested
    /// on Linux, where those strings are not rooted.
    /// </para>
    /// </remarks>
    internal static string RelativeToRepoRoot(string absolutePath) =>
        RelativeToRepoRoot(absolutePath, ResolveRepoRoot(), Path.GetTempPath());

    /// <summary>The same rendering with explicit roots, for host-independent privacy fixtures.</summary>
    internal static string RelativeToRepoRoot(string absolutePath, string repoRoot, string tempRoot)
    {
        var relative = Path.GetRelativePath(repoRoot, absolutePath);
        if (IsWithinRoot(absolutePath, relative))
        {
            return Render(absolutePath, relative);
        }

        var tempRelative = Path.GetRelativePath(tempRoot, absolutePath);
        if (IsWithinRoot(absolutePath, tempRelative))
        {
            return TempDirectoryMarker + "/" + tempRelative.Replace('\\', '/');
        }

        return Render(absolutePath, relative);
    }

    private static bool IsWithinRoot(string absolutePath, string relative) =>
        !string.Equals(relative, absolutePath, StringComparison.Ordinal)
        && !Path.IsPathRooted(relative)
        && relative != ".."
        && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>The system temp directory's withheld host prefix.</summary>
    internal const string TempDirectoryMarker = "<temp>";

    /// <summary>
    /// What <see cref="RelativeToRepoRoot(string)"/> prints for a path <see cref="Path.GetRelativePath"/>
    /// could not relativise (a different volume from the checkout, Windows only).
    /// </summary>
    internal const string OutsideRepositoryVolumeMarker = "<outside the repository volume>";

    private static readonly SearchValues<char> s_separators = SearchValues.Create("\\/");

    /// <summary>
    /// The rendering step of <see cref="RelativeToRepoRoot(string)"/>: <paramref name="relative"/> is
    /// <see cref="Path.GetRelativePath"/>'s answer for <paramref name="absolutePath"/> against the
    /// repository root. Pure, so the cross-volume answer can be tested where it cannot be produced.
    /// </summary>
    internal static string Render(string absolutePath, string relative)
    {
        if (!string.Equals(relative, absolutePath, StringComparison.Ordinal) && !Path.IsPathRooted(relative))
        {
            return relative.Replace('\\', '/');
        }

        // GetRelativePath returned a rooted target (possibly normalised), or the unchanged
        // Windows-style answer on Linux: no shared root can produce a relative prefix. Print only
        // the last segment, behind a marker that says why. Both separators are split on deliberately:
        // on Linux the backslash is not a separator, and this branch renders the Windows answer.
        var trimmed = relative.TrimEnd('\\', '/');
        var lastSeparator = trimmed.AsSpan().LastIndexOfAny(s_separators);
        var leaf = lastSeparator < 0 ? trimmed : trimmed[(lastSeparator + 1)..];

        return OutsideRepositoryVolumeMarker + "/" + leaf;
    }
}
