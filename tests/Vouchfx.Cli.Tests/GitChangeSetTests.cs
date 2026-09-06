// Vouchfx.Cli.Tests — GitChangeSet unit tests (S07-C-02). No Docker, no real git.
//
// GitChangeSet shells out to git behind IProcessRunner. These tests inject a fake runner
// that returns canned `git rev-parse` / `git diff` / `git status` output (and the error
// cases) so the parsing, path-resolution and error-mapping are exercised WITHOUT a real
// repository. One OPTIONAL smoke test runs against the actual repo when git is available.
//
// Since #499 a second collaborator is injected alongside the runner: the locator that resolves
// `git` to a rooted path. Every row below supplies a fake one, because the resolution happens
// before any call reaches the runner and REFUSES the change-set when it finds nothing — without
// the injection each parsing row would silently acquire a dependency on the host having git
// installed. The PATH search itself is exercised directly, against a temporary directory, by the
// LocateOnPath rows at the bottom of the file.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vouchfx.Cli.Selection;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class GitChangeSetTests
{
    private const string RepoRoot = "/repo";

    // A scripted IProcessRunner: maps the git subcommand (first argument) to a canned result.
    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly Dictionary<string, ProcessResult> _byVerb = new();
        private readonly Exception? _refusal;

        public FakeProcessRunner()
        {
        }

        private FakeProcessRunner(Exception refusal) => _refusal = refusal;

        public List<(string FileName, IReadOnlyList<string> Args, string WorkingDirectory)> Calls
        {
            get;
        } = new();

        /// <summary>
        /// A runner that refuses every call with <paramref name="refusal"/>.
        /// </summary>
        /// <remarks>
        /// Takes the exception rather than a flag per failure mode: IProcessRunner now has THREE
        /// throwing outcomes (a launch failure, a timeout, and a failed output capture), and every
        /// one of them must be mapped by GitChangeSet.RunGit — an unmapped one escapes as an
        /// unhandled crash, because RunCommand catches ChangeSetException and nothing else. A
        /// parameterised refusal keeps adding cover for the next one a one-line change rather than
        /// a fourth boolean, which the third outcome then proved by costing exactly one row.
        /// </remarks>
        public static FakeProcessRunner Refusing(Exception refusal) => new(refusal);

        public FakeProcessRunner With(string verb, int exit, string stdout = "", string stderr = "")
        {
            _byVerb[verb] = new ProcessResult(exit, stdout, stderr);
            return this;
        }

        // The token is accepted and ignored: a canned runner has nothing to cancel, and every row
        // here calls the constructor with the default (CancellationToken.None). What cancellation
        // does to a REAL child is SystemProcessRunnerTests' row 5, which needs a live process to
        // say anything true about it.
        public ProcessResult Run(
            string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments, workingDirectory));

            if (_refusal is not null)
            {
                throw _refusal;
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

    // A rooted path no host has. It stands in for whatever the real PATH search would have found,
    // so a row can assert what GitChangeSet DOES with the resolved path without depending on the
    // host having git — and, because it is a recognisable literal, a row can also assert that it
    // does not leak into a user-facing message.
    private static readonly string FakeGitDirectory =
        OperatingSystem.IsWindows() ? @"C:\vouchfx-fake-bin" : "/vouchfx-fake-bin";

    private static readonly string FakeGitPath =
        Path.Combine(FakeGitDirectory, OperatingSystem.IsWindows() ? "git.exe" : "git");

    private static GitChangeSet NewChangeSet(string changedSinceRef, IProcessRunner runner) =>
        new(changedSinceRef, RepoRoot, runner, () => FakeGitPath);

    // ---- Diff parsing -----------------------------------------------------------------

    [Fact]
    public void Diff_ResolvesRepoRelativePaths_ToAbsolute()
    {
        var runner = Runner(diffOutput: "orders/place.e2e.yaml\nbilling/charge.e2e.yaml\n");
        var changeSet = NewChangeSet("main", runner);

        Assert.True(changeSet.IsChanged(Abs("orders/place.e2e.yaml")));
        Assert.True(changeSet.IsChanged(Abs("billing/charge.e2e.yaml")));
        Assert.False(changeSet.IsChanged(Abs("unchanged.e2e.yaml")));
    }

    [Fact]
    public void Diff_UsesThreeDotRangeAgainstHead()
    {
        var runner = Runner(diffOutput: "a.e2e.yaml\n");
        _ = NewChangeSet("release/1.2", runner);

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
        _ = NewChangeSet("main", runner);

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
        var changeSet = NewChangeSet("main", runner);

        Assert.True(changeSet.IsChanged(Abs("committed/x.e2e.yaml")));
        Assert.True(changeSet.IsChanged(Abs("orders/modified.e2e.yaml")));
        Assert.True(changeSet.IsChanged(Abs("staged/added.e2e.yaml")));
        Assert.True(changeSet.IsChanged(Abs("new/untracked.e2e.yaml")));
    }

    [Fact]
    public void Status_Rename_TakesDestinationPath()
    {
        var runner = Runner(statusOutput: "R  old/name.e2e.yaml -> new/name.e2e.yaml\n");
        var changeSet = NewChangeSet("main", runner);

        Assert.True(changeSet.IsChanged(Abs("new/name.e2e.yaml")));
    }

    [Fact]
    public void IsChanged_NormalisesBackslashPath()
    {
        var runner = Runner(diffOutput: "orders/place.e2e.yaml\n");
        var changeSet = NewChangeSet("main", runner);

        // A Windows-style absolute path with backslashes must still resolve to the same key.
        var backslashPath = Abs("orders/place.e2e.yaml").Replace('/', '\\');
        Assert.True(changeSet.IsChanged(backslashPath));
    }

    [Fact]
    public void IsChanged_DirectoryEntry_CoversFilesBeneath()
    {
        // git can report a directory-level change (e.g. a submodule); files under it count.
        var runner = Runner(diffOutput: "orders\n");
        var changeSet = NewChangeSet("main", runner);

        Assert.True(changeSet.IsChanged(Abs("orders/nested/x.e2e.yaml")));
        Assert.False(changeSet.IsChanged(Abs("ordersX/x.e2e.yaml"))); // prefix, not a dir
    }

    // ---- Error mapping ----------------------------------------------------------------

    [Fact]
    public void GitNotInstalled_SurfacesChangeSetException_NotCrash()
    {
        var runner = FakeProcessRunner.Refusing(new ProcessLaunchException("git not found on PATH"));

        var ex = Assert.Throws<ChangeSetException>(
            () => NewChangeSet("main", runner));
        Assert.Contains("git", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A wedged git (#481) surfaces as a <see cref="ChangeSetException"/> like every other runner
    /// failure, so the CLI still exits 2 rather than crashing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the row that stops the timeout fix from being a REGRESSION. <c>RunGit</c> caught
    /// only <see cref="ProcessLaunchException"/> before #481; adding a second throwing outcome to
    /// <see cref="IProcessRunner"/> without mapping it here would have converted a hang into an
    /// unhandled crash — strictly worse than the hang it replaced, and invisible until a customer
    /// hit it.
    /// </para>
    /// <para>
    /// The exit code is deliberately unchanged at 2 (usage error): whether selection-infrastructure
    /// failure deserves a code of its own belongs to issues #480 and #466-B, and this fix must not
    /// answer it quietly.
    /// </para>
    /// </remarks>
    [Fact]
    public void GitTimesOut_SurfacesChangeSetException_NamingTheBudget()
    {
        var runner = FakeProcessRunner.Refusing(
            new ProcessTimeoutException("'git' exceeded its budget.", System.TimeSpan.FromSeconds(90)));

        var ex = Assert.Throws<ChangeSetException>(
            () => NewChangeSet("main", runner));

        // The budget and the operation, both named: an operator reading this line needs to know
        // that a ceiling was hit (not that git is missing) and which call hit it.
        Assert.Contains("90s", ex.Message, System.StringComparison.Ordinal);
        Assert.Contains("repository-root lookup", ex.Message, System.StringComparison.Ordinal);
        Assert.IsType<ProcessTimeoutException>(ex.InnerException);
    }

    /// <summary>
    /// A read that faults mid-capture (#481) surfaces as a <see cref="ChangeSetException"/> too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The third throwing outcome, and the one that was previously an unhandled crash rather than
    /// a bad message. <c>Task.WhenAll</c> faults as soon as either read faults and
    /// <c>Task.WhenAny</c> returns the first task to reach ANY terminal state, so a faulted read
    /// won the runner's race exactly like a successful one and then resurfaced at the await of the
    /// captured text — as a raw <see cref="IOException"/>, which <c>RunGit</c> did not catch. The
    /// runner now converts it to <see cref="ProcessCaptureException"/>; this row is the half that
    /// pins the mapping.
    /// </para>
    /// <para>
    /// It is not <see cref="ProcessLaunchException"/> precisely so this message does not ask an
    /// operator whether git is on PATH for a git that started and then failed part-way, so the row
    /// asserts the mapped message names the READ rather than the PATH.
    /// </para>
    /// </remarks>
    [Fact]
    public void GitOutputCaptureFails_SurfacesChangeSetException_NotCrash()
    {
        var runner = FakeProcessRunner.Refusing(
            new ProcessCaptureException(
                "Reading the output of 'git' failed: The pipe has been ended.",
                new IOException("The pipe has been ended.")));

        var ex = Assert.Throws<ChangeSetException>(
            () => NewChangeSet("main", runner));

        Assert.Contains("read the output", ex.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("repository-root lookup", ex.Message, System.StringComparison.Ordinal);
        Assert.DoesNotContain("PATH", ex.Message, System.StringComparison.Ordinal);
        Assert.IsType<ProcessCaptureException>(ex.InnerException);
    }

    [Fact]
    public void NotARepository_SurfacesChangeSetException()
    {
        // rev-parse fails (not inside a work tree).
        var runner = new FakeProcessRunner()
            .With("rev-parse", exit: 128, stderr: "fatal: not a git repository");

        var ex = Assert.Throws<ChangeSetException>(
            () => NewChangeSet("main", runner));
        Assert.Contains("not a git repository", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BadRef_SurfacesChangeSetException_WithGitDiagnostic()
    {
        var runner = new FakeProcessRunner()
            .With("rev-parse", exit: 0, stdout: RepoRoot + "\n")
            .With("diff", exit: 128, stderr: "fatal: bad revision 'nope'");

        var ex = Assert.Throws<ChangeSetException>(
            () => NewChangeSet("nope", runner));
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
            () => NewChangeSet(maliciousRef, runner));

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

    // ---- Which binary is launched (#499) ----------------------------------------------

    /// <summary>
    /// Every git call is launched by a ROOTED file name, never the bare name <c>git</c>.
    /// </summary>
    /// <remarks>
    /// This is the whole of #499 expressed as an assertion. A bare, unqualified name is not a
    /// <c>PATH</c> lookup on Windows; the OS applies its own search order, whose FIRST entry is the
    /// calling executable's own directory — measured on this host: an impostor <c>git.exe</c>
    /// dropped beside the caller is launched in preference to the real git on <c>PATH</c>, which
    /// for a dotnet global tool means one user-writable file in <c>~/.dotnet/tools</c>. A rooted
    /// name is taken literally by both <c>CreateProcess</c> and <c>execve</c>, so there is no
    /// search to lose. GitChangeSet's header records what was probed and did NOT reproduce.
    /// </remarks>
    [Fact]
    public void EveryGitCall_IsLaunchedByARootedPath_NotTheBareName()
    {
        var runner = Runner();
        _ = NewChangeSet("main", runner);

        Assert.Equal(3, runner.Calls.Count); // rev-parse, diff, status
        Assert.All(runner.Calls, call =>
        {
            Assert.True(
                Path.IsPathRooted(call.FileName),
                $"git was launched as '{call.FileName}', which is not rooted.");
            Assert.Equal(FakeGitPath, call.FileName);
        });
    }

    /// <summary>
    /// The executable is resolved ONCE per change-set, not once per git invocation.
    /// </summary>
    /// <remarks>
    /// Three calls follow one resolution. Beyond the wasted filesystem probes, a per-call search
    /// would let the answer change underneath a single change-set — the diff and the status could
    /// be computed by two different binaries.
    /// </remarks>
    [Fact]
    public void GitExecutable_IsResolvedOncePerChangeSet()
    {
        var runner = Runner();
        var resolutions = 0;

        _ = new GitChangeSet(
            "main",
            RepoRoot,
            runner,
            () =>
            {
                resolutions++;
                return FakeGitPath;
            });

        Assert.Equal(1, resolutions);
        Assert.Equal(3, runner.Calls.Count);
    }

    /// <summary>
    /// A git that is not on <c>PATH</c> is refused before anything is launched, as a
    /// <see cref="ChangeSetException"/> — the same outcome, and therefore the same exit code 2, as
    /// the launch failure it replaces.
    /// </summary>
    /// <remarks>
    /// There is deliberately no fallback to the bare name: falling back is precisely the
    /// search-order hole the resolution closes, so "not found" has to be a refusal. The exit code
    /// is unchanged on purpose — whether selection-infrastructure failure deserves one of its own
    /// belongs to issues #480 and #466-B.
    /// </remarks>
    [Fact]
    public void GitNotOnPath_IsRefused_BeforeAnythingIsLaunched()
    {
        var runner = Runner();

        var ex = Assert.Throws<ChangeSetException>(
            () => new GitChangeSet("main", RepoRoot, runner, () => null));

        Assert.Contains(
            "Is git installed and on PATH?", ex.Message, System.StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
    }

    /// <summary>
    /// A launch failure does not disclose the resolved git path, driven by a REAL
    /// <see cref="System.Diagnostics.Process"/> launch failure rather than a hand-built exception.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The first two assertions are the point of the row.</strong> An earlier version of
    /// this test constructed the <see cref="ProcessLaunchException"/> itself, with a path-free
    /// <see cref="IOException"/> inside it, and then asserted that the mapping added no path — so
    /// it pinned a property of its own fixture and would have passed against a mapping that
    /// disclosed everything the BCL actually hands it. This row instead makes
    /// <see cref="SystemProcessRunner"/> fail for real against a rooted, unlaunchable path and
    /// asserts the raw failure DOES name that path, in both the runner's message and its inner
    /// one, before asserting the mapped message does not. The pattern is #488's, recorded in
    /// CHANGELOG.md: assert the raw BCL failure names the path first, so the assertion that
    /// matters cannot degrade into a vacuous pass.
    /// </para>
    /// <para>
    /// Measured, and it is why the mapping now carries no reason clause at all: .NET composes BOTH
    /// the executable path AND the working directory into the <c>Win32Exception</c> message, so
    /// <c>InnerException.Message</c> is the SOURCE of the leak rather than a path-free half of it.
    /// Measured on Windows; the assertion is safe to run everywhere because
    /// <c>Process.Unix.cs</c>'s <c>ForkAndExecProcess</c> reaches the SAME
    /// <c>CreateExceptionForErrorStartingProcess(message, errno, resolvedFilename, cwd)</c> helper
    /// on its failure paths (read from the dotnet/runtime release/8.0 source, not measured here).
    /// </para>
    /// <para>
    /// Nothing is launched: the candidate is a rooted path under the temp directory that is
    /// deliberately never created, so the failure happens inside <c>CreateProcess</c>/<c>execve</c>
    /// and the row leaves no child, no file and no directory behind. The working directory is the
    /// temp directory itself, which exists on every host — a non-existent one would fail for a
    /// second reason and blur what is being measured.
    /// </para>
    /// </remarks>
    [Fact]
    public void LaunchFailure_DoesNotDiscloseTheResolvedPath()
    {
        var hostDirectory = Path.Combine(
            Path.GetTempPath(), "vouchfx-absent-git-" + Guid.NewGuid().ToString("N"));
        var absentGit = Path.Combine(
            hostDirectory, OperatingSystem.IsWindows() ? "git.exe" : "git");
        Assert.False(Directory.Exists(hostDirectory)); // nothing is created, so nothing is left.

        // A short budget: the launch fails inside CreateProcess/execve, so no wait is ever
        // entered and the ceiling only bounds a pathological host.
        var runner = new SystemProcessRunner(System.TimeSpan.FromSeconds(10));

        // (1) The raw BCL failure DOES name the resolved path — otherwise (3) proves nothing.
        var raw = Assert.Throws<ProcessLaunchException>(
            () => runner.Run(absentGit, new[] { "rev-parse" }, Path.GetTempPath()));

        Assert.Contains(absentGit, raw.Message, System.StringComparison.Ordinal);

        // (2) ...and so does the INNER exception, which is the composed Win32Exception. This is
        // the assertion that retires the claim that taking the inner message "structurally cannot
        // carry the path": it carries the executable path AND the working directory.
        var inner = Assert.IsAssignableFrom<System.ComponentModel.Win32Exception>(raw.InnerException);
        Assert.Contains(absentGit, inner.Message, System.StringComparison.Ordinal);
        Assert.Contains(
            Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            inner.Message,
            System.StringComparison.Ordinal);

        // (3) The mapped, user-facing message names neither.
        var mapped = Assert.Throws<ChangeSetException>(
            () => new GitChangeSet("main", Path.GetTempPath(), runner, () => absentGit));

        Assert.Contains(
            "Is git installed and on PATH?", mapped.Message, System.StringComparison.Ordinal);

        // The repo's shared property assertion (#357/#375/#473) rather than a DoesNotContain on
        // this one literal: it also refuses any OTHER rooted token the mapping might later grow.
        HostPathDisclosure.AssertNoAbsoluteHostPath(
            "the change-set launch-failure message", mapped.Message, hostDirectory);
    }

    // ---- The PATH search itself (#499) ------------------------------------------------

    /// <summary>
    /// A throwaway directory holding one file named the way this platform names an executable.
    /// </summary>
    /// <remarks>
    /// Real files rather than a mocked filesystem, because what is under test IS the filesystem
    /// probe: on Windows the PATHEXT candidate must exist, on POSIX it must carry an execute bit.
    /// The directory is removed on every path so a run leaves nothing behind.
    /// </remarks>
    private sealed class LocatorFixture : IDisposable
    {
        public LocatorFixture(string name = "git", bool executable = true, string? parentDirectory = null)
        {
            DirectoryPath = Path.Combine(
                parentDirectory ?? Path.GetTempPath(),
                "vouchfx-locate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);

            ExecutablePath = Path.Combine(
                DirectoryPath, OperatingSystem.IsWindows() ? name + ".exe" : name);
            File.WriteAllText(ExecutablePath, string.Empty);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    ExecutablePath,
                    executable
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        public string DirectoryPath { get; }

        public string ExecutablePath { get; }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    [Fact]
    public void LocateOnPath_FindsTheExecutable_InAFullyQualifiedEntry()
    {
        using var fixture = new LocatorFixture();

        var located = GitChangeSet.LocateOnPath("git", fixture.DirectoryPath, ".EXE");

        Assert.Equal(fixture.ExecutablePath, located, ignoreCase: OperatingSystem.IsWindows());
    }

    /// <summary>
    /// Windows candidates come from <c>PATHEXT</c>, and the fallback covers a stripped environment.
    /// </summary>
    [Fact]
    public void LocateOnPath_Windows_TriesPathExtCandidates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // PATHEXT has no meaning here; the POSIX rows cover this platform.
        }

        using var fixture = new LocatorFixture();

        // The file is `git.exe`, so a PATHEXT without .EXE must not find it...
        Assert.Null(GitChangeSet.LocateOnPath("git", fixture.DirectoryPath, ".COM;.BAT"));

        // ...one that lists .EXE must, whether it is configured or comes from the fallback.
        Assert.NotNull(GitChangeSet.LocateOnPath("git", fixture.DirectoryPath, ".COM;.EXE;.BAT"));
        Assert.NotNull(GitChangeSet.LocateOnPath("git", fixture.DirectoryPath, pathExtVariable: null));
    }

    /// <summary>
    /// On POSIX a file without an execute bit is not a candidate.
    /// </summary>
    [Fact]
    public void LocateOnPath_Posix_RequiresAnExecuteBit()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows has no execute bit; PATHEXT membership is the test there.
        }

        using var executable = new LocatorFixture();
        using var notExecutable = new LocatorFixture(executable: false);

        Assert.NotNull(GitChangeSet.LocateOnPath("git", executable.DirectoryPath, null));
        Assert.Null(GitChangeSet.LocateOnPath("git", notExecutable.DirectoryPath, null));
    }

    /// <summary>
    /// An entry that is not fully qualified — empty, or relative — is SKIPPED, not resolved.
    /// </summary>
    /// <remarks>
    /// An empty PATH element means "the current directory" on some platforms and a relative one
    /// resolves against it, so honouring either would reopen the very hole #499 closed, one
    /// indirection further along. The control assertion is what makes the negative meaningful:
    /// the same directory, spelt absolutely, does resolve.
    /// </remarks>
    [Fact]
    public void LocateOnPath_SkipsEntriesThatAreNotFullyQualified()
    {
        // Under the CURRENT directory, not the temp root, and that is not incidental: a relative
        // spelling of a directory only exists when it shares a volume with the current one, and on
        // this maintainer's machine temp is on C: while the working tree is on D:. Rooting the
        // fixture here makes the negative assertion below run on every host rather than skip on
        // Windows. The directory is the test's own output directory and is removed in Dispose.
        using var fixture = new LocatorFixture(parentDirectory: Directory.GetCurrentDirectory());

        Assert.NotNull(GitChangeSet.LocateOnPath("git", fixture.DirectoryPath, ".EXE"));

        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), fixture.DirectoryPath);
        Assert.False(Path.IsPathFullyQualified(relative), relative);

        // The empty entry means "the current directory" and the relative one resolves against it;
        // the same directory that resolved above must NOT resolve when spelt either way.
        var entries = string.Empty + Path.PathSeparator + relative;
        Assert.Null(GitChangeSet.LocateOnPath("git", entries, ".EXE"));
    }

    [Fact]
    public void LocateOnPath_ReturnsNull_WhenNoEntryHoldsTheExecutable()
    {
        using var fixture = new LocatorFixture();

        Assert.Null(GitChangeSet.LocateOnPath("no-such-tool", fixture.DirectoryPath, ".EXE"));
        Assert.Null(GitChangeSet.LocateOnPath("git", pathVariable: null, ".EXE"));
        Assert.Null(GitChangeSet.LocateOnPath("git", string.Empty, ".EXE"));
    }

    /// <summary>
    /// The production locator either finds a real, fully qualified git or reports nothing.
    /// </summary>
    /// <remarks>
    /// Deliberately tolerant of a host without git — the point it pins is that the search NEVER
    /// yields something unrooted, which is the property the whole fix rests on.
    /// </remarks>
    [Fact]
    public void LocateGitOnPath_YieldsAFullyQualifiedPath_OrNothing()
    {
        var located = GitChangeSet.LocateGitOnPath();
        if (located is null)
        {
            return; // No git on this host; the refusal is covered by its own row.
        }

        Assert.True(Path.IsPathFullyQualified(located), located);
        Assert.True(File.Exists(located), located);
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
