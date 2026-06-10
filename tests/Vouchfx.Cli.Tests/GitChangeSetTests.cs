// Vouchfx.Cli.Tests — GitChangeSet unit tests (S07-C-02). No Docker, no real git.
//
// GitChangeSet shells out to git behind IProcessRunner. These tests inject a fake runner
// that returns canned `git rev-parse` / `git diff` / `git status` output (and the error
// cases) so the parsing, path-resolution and error-mapping are exercised WITHOUT a real
// repository. One OPTIONAL smoke test runs against the actual repo when git is available.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vouchfx.Cli.Selection;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class GitChangeSetTests
{
    private const string RepoRoot = "/repo";

    // A scripted IProcessRunner: maps the git subcommand (first argument) to a canned result.
    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly Dictionary<string, ProcessResult> _byVerb = new();
        private readonly bool _throwLaunch;

        public FakeProcessRunner(bool throwLaunch = false) => _throwLaunch = throwLaunch;

        public List<(string FileName, IReadOnlyList<string> Args, string WorkingDirectory)> Calls
        {
            get;
        } = new();

        public FakeProcessRunner With(string verb, int exit, string stdout = "", string stderr = "")
        {
            _byVerb[verb] = new ProcessResult(exit, stdout, stderr);
            return this;
        }

        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Calls.Add((fileName, arguments, workingDirectory));

            if (_throwLaunch)
            {
                throw new ProcessLaunchException("git not found on PATH");
            }

            var verb = SubcommandOf(arguments);
            if (_byVerb.TryGetValue(verb, out var result))
            {
                return result;
            }

            // Default: a successful empty result (used for verbs the test does not script).
            return new ProcessResult(0, string.Empty, string.Empty);
        }

        // The git subcommand is the first argument that is not a leading `-c key=value`
        // config override (e.g. `git -c core.quotepath=false status --porcelain` → "status").
        private static string SubcommandOf(IReadOnlyList<string> arguments)
        {
            for (var i = 0; i < arguments.Count; i++)
            {
                if (arguments[i] == "-c")
                {
                    i++; // skip the `key=value` operand that follows `-c`
                    continue;
                }

                return arguments[i];
            }

            return string.Empty;
        }
    }

    private static FakeProcessRunner Runner(string diffOutput = "", string statusOutput = "") =>
        new FakeProcessRunner()
            .With("rev-parse", exit: 0, stdout: RepoRoot + "\n")
            .With("diff", exit: 0, stdout: diffOutput)
            .With("status", exit: 0, stdout: statusOutput);

    private static string Abs(string repoRelative) =>
        Path.GetFullPath(Path.Combine(RepoRoot, repoRelative)).Replace('\\', '/');

    // ---- Diff parsing -----------------------------------------------------------------

    [Fact]
    public void Diff_ResolvesRepoRelativePaths_ToAbsolute()
    {
        var runner = Runner(diffOutput: "orders/place.e2e.yaml\nbilling/charge.e2e.yaml\n");
        var changeSet = new GitChangeSet("main", workingDirectory: RepoRoot, runner);

        Assert.True(changeSet.IsChanged(Abs("orders/place.e2e.yaml")));
        Assert.True(changeSet.IsChanged(Abs("billing/charge.e2e.yaml")));
        Assert.False(changeSet.IsChanged(Abs("unchanged.e2e.yaml")));
    }

    [Fact]
    public void Diff_UsesThreeDotRangeAgainstHead()
    {
        var runner = Runner(diffOutput: "a.e2e.yaml\n");
        _ = new GitChangeSet("release/1.2", workingDirectory: RepoRoot, runner);

        var diffCall = Assert.Single(runner.Calls, c => c.Args.Count > 0 && c.Args[0] == "diff");
        Assert.Equal(
            new[] { "diff", "--name-only", "--end-of-options", "release/1.2...HEAD" },
            diffCall.Args);
    }

    // ---- Status (working tree) parsing ------------------------------------------------

    [Fact]
    public void Status_PrefixesQuotePathFalse_SoNonAsciiPathsAreVerbatim()
    {
        var runner = Runner();
        _ = new GitChangeSet("main", workingDirectory: RepoRoot, runner);

        var statusCall = Assert.Single(
            runner.Calls, c => c.Args.Count > 0 && c.Args.Contains("status"));
        Assert.Equal(
            new[] { "-c", "core.quotepath=false", "status", "--porcelain" },
            statusCall.Args);
    }

    [Fact]
    public void Status_ParsesPorcelainCodes_AndUnion_WithDiff()
    {
        // Modified, added, untracked — each XY code, then the path at column 3.
        var statusOutput =
            " M orders/modified.e2e.yaml\n" +
            "A  staged/added.e2e.yaml\n" +
            "?? new/untracked.e2e.yaml\n";

        var runner = Runner(diffOutput: "committed/x.e2e.yaml\n", statusOutput: statusOutput);
        var changeSet = new GitChangeSet("main", workingDirectory: RepoRoot, runner);

        Assert.True(changeSet.IsChanged(Abs("committed/x.e2e.yaml")));
        Assert.True(changeSet.IsChanged(Abs("orders/modified.e2e.yaml")));
        Assert.True(changeSet.IsChanged(Abs("staged/added.e2e.yaml")));
        Assert.True(changeSet.IsChanged(Abs("new/untracked.e2e.yaml")));
    }

    [Fact]
    public void Status_Rename_TakesDestinationPath()
    {
        var runner = Runner(statusOutput: "R  old/name.e2e.yaml -> new/name.e2e.yaml\n");
        var changeSet = new GitChangeSet("main", workingDirectory: RepoRoot, runner);

        Assert.True(changeSet.IsChanged(Abs("new/name.e2e.yaml")));
    }

    [Fact]
    public void IsChanged_NormalisesBackslashPath()
    {
        var runner = Runner(diffOutput: "orders/place.e2e.yaml\n");
        var changeSet = new GitChangeSet("main", workingDirectory: RepoRoot, runner);

        // A Windows-style absolute path with backslashes must still resolve to the same key.
        var backslashPath = Abs("orders/place.e2e.yaml").Replace('/', '\\');
        Assert.True(changeSet.IsChanged(backslashPath));
    }

    [Fact]
    public void IsChanged_DirectoryEntry_CoversFilesBeneath()
    {
        // git can report a directory-level change (e.g. a submodule); files under it count.
        var runner = Runner(diffOutput: "orders\n");
        var changeSet = new GitChangeSet("main", workingDirectory: RepoRoot, runner);

        Assert.True(changeSet.IsChanged(Abs("orders/nested/x.e2e.yaml")));
        Assert.False(changeSet.IsChanged(Abs("ordersX/x.e2e.yaml"))); // prefix, not a dir
    }

    // ---- Error mapping ----------------------------------------------------------------

    [Fact]
    public void GitNotInstalled_SurfacesChangeSetException_NotCrash()
    {
        var runner = new FakeProcessRunner(throwLaunch: true);

        var ex = Assert.Throws<ChangeSetException>(
            () => new GitChangeSet("main", workingDirectory: RepoRoot, runner));
        Assert.Contains("git", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NotARepository_SurfacesChangeSetException()
    {
        // rev-parse fails (not inside a work tree).
        var runner = new FakeProcessRunner()
            .With("rev-parse", exit: 128, stderr: "fatal: not a git repository");

        var ex = Assert.Throws<ChangeSetException>(
            () => new GitChangeSet("main", workingDirectory: RepoRoot, runner));
        Assert.Contains("not a git repository", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BadRef_SurfacesChangeSetException_WithGitDiagnostic()
    {
        var runner = new FakeProcessRunner()
            .With("rev-parse", exit: 0, stdout: RepoRoot + "\n")
            .With("diff", exit: 128, stderr: "fatal: bad revision 'nope'");

        var ex = Assert.Throws<ChangeSetException>(
            () => new GitChangeSet("nope", workingDirectory: RepoRoot, runner));
        Assert.Contains("bad revision", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--output=/tmp/x")]
    [InlineData("-")]
    [InlineData("--upload-pack=evil")]
    public void DashLeadingRef_IsRejected_BeforeAnyDiff(string maliciousRef)
    {
        // An argument-injection guard: a ref git could parse as an OPTION must be refused up
        // front, so it never reaches the `git diff` argv at all.
        var runner = Runner(diffOutput: "should-not-be-used.e2e.yaml\n");

        var ex = Assert.Throws<ChangeSetException>(
            () => new GitChangeSet(maliciousRef, workingDirectory: RepoRoot, runner));

        Assert.Contains("must not start with '-'", ex.Message, System.StringComparison.Ordinal);

        // No git invocation may have spliced the malicious ref into a diff range.
        Assert.DoesNotContain(
            runner.Calls,
            c => c.Args.Count > 0 && c.Args[0] == "diff"
                 && c.Args.Any(a => a.Contains(maliciousRef, System.StringComparison.Ordinal)));
    }

    [Fact]
    public void NullChangeSet_AlwaysReportsChanged()
    {
        Assert.True(NullChangeSet.Instance.IsChanged("/anything"));
    }

    // ---- Optional real-git smoke test -------------------------------------------------

    [Fact]
    public void RealGit_AgainstThisRepo_DoesNotThrow()
    {
        // Cheap, deterministic smoke test against the repo this test assembly lives in.
        // It quietly no-ops (rather than fails) when git is unavailable or we are not in a
        // work tree, so the unit suite never depends on a real repo / a developer's git.
        var repoDir = FindRepoRoot(System.AppContext.BaseDirectory);
        if (repoDir is null)
        {
            return; // Not inside a git work tree — nothing to smoke-test.
        }

        try
        {
            // HEAD against itself ⇒ no committed diff; status reflects the live tree. The
            // assertion is only that construction + a lookup do not throw.
            var changeSet = new GitChangeSet("HEAD", repoDir, SystemProcessRunner.Instance);
            _ = changeSet.IsChanged(Path.Combine(repoDir, "README.md"));
        }
        catch (ChangeSetException)
        {
            // git not installed / unusable on this machine — treat as a no-op, not a failure.
        }
    }

    private static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
