// Vouchfx.Cli.Tests — GitChangeSet unit tests (S07-C-02). No Docker.
//
// GitChangeSet shells out to git behind IProcessRunner. These tests inject a fake runner
// that returns canned `git rev-parse` / `git diff` / `git status` output (and the error
// cases) so the parsing, path-resolution and error-mapping are exercised WITHOUT a real
// repository. TWO rows do launch a real git, and only one of them is optional: the smoke
// test against this repo no-ops when git is unavailable, while the locator-equivalence row
// launches `git --version` unconditionally as its reference — bounded, and answering "does
// not launch" if that probe times out.
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
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Cli.Selection;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class GitChangeSetTests
{
    private const string RepoRoot = "/repo";

    // The ceiling on the bare-name reference probe below. A ceiling, not a latency target:
    // `git --version` returns in milliseconds, and the only decision this makes is how long a
    // wedged one may hold the run before the probe gives up and answers "does not launch".
    private const int BareNameProbeBudgetMilliseconds = 30_000;

    // A scripted IProcessRunner: maps the git subcommand (first argument) to a canned result.
    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly Dictionary<string, ProcessResult> _byVerb = new();
        private readonly Exception? _refusal;

        public FakeProcessRunner()
        {
        }

        private FakeProcessRunner(Exception refusal) => _refusal = refusal;

        public List<(string FileName,
                     IReadOnlyList<string> Args,
                     string WorkingDirectory,
                     IReadOnlyDictionary<string, string>? Environment)> Calls
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
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((fileName, arguments, workingDirectory, environment));

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

        // The git subcommand is the first argument that is not a GIT-LEVEL option preceding it —
        // a `-c key=value` config override, or a flag such as `--no-optional-locks` (e.g.
        // `git --no-optional-locks -c core.quotepath=false status --porcelain` → "status").
        //
        // The `--`-prefixed skip is not cosmetic: without it the status call's leading
        // `--no-optional-locks` is returned as the verb, every `With("status", ...)` script stops
        // matching, and each such row silently falls through to the default empty success — a whole
        // class of rows passing while asserting nothing.
        private static string SubcommandOf(IReadOnlyList<string> arguments)
        {
            for (var i = 0; i < arguments.Count; i++)
            {
                if (arguments[i] == "-c")
                {
                    i++; // skip the `key=value` operand that follows `-c`
                    continue;
                }

                if (arguments[i].StartsWith("--", System.StringComparison.Ordinal))
                {
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

    /// <summary>
    /// The status argv, character for character: the quote-path override, the optional-lock
    /// refusal, and both of them BEFORE the subcommand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Position is the assertion, not decoration.</strong> <c>--no-optional-locks</c> is a
    /// git-level option; spelt after <c>status</c> git parses it as a status option and refuses the
    /// call, which would turn every <c>--changed-since</c> run into exit 2. A sequence equality is
    /// what pins the ordering — a <c>Contains</c> would pass against the broken spelling.
    /// </para>
    /// <para>
    /// What the flag is FOR is a concurrency hazard rather than an argument:
    /// <c>RealGit_NoOptionalLocks_ChangesNoStatusOutput</c> is the row that measures a real git
    /// accepting it in this position and answering identically with and without it.
    /// </para>
    /// </remarks>
    [Fact]
    public void Status_PrefixesQuotePathFalse_AndRefusesOptionalLocks()
    {
        var runner = Runner();
        _ = NewChangeSet("main", runner);

        var statusCall = Assert.Single(
            runner.Calls, c => c.Args.Count > 0 && c.Args.Contains("status"));
        Assert.Equal(
            new[] { "--no-optional-locks", "-c", "core.quotepath=false", "status", "--porcelain" },
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
    /// failure deserves a code of its own is an open question, and this fix must not answer it
    /// quietly.
    /// </para>
    /// <para>
    /// <strong>That question is open and UNFILED, which is a change from what this comment used to
    /// say.</strong> It cited issues #480 and #466-B, and neither reaches it. #466 closed on a
    /// different axis — how <c>ParallelSuiteRunner</c>'s slot catch-all CLASSIFIES an unexpected
    /// engine throw — and #480's answer is narrower still: a provider or engine defect never exits
    /// 0. A git that could not be run is neither. Until somebody files it, a <c>--changed-since</c>
    /// git failure is a usage error and exits 2, and there is no issue to read for the reasoning.
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

    // ---- Path disclosure in the selection diagnostics (#498) ---------------------------

    /// <summary>
    /// #498, site 2 — the no-repository-root refusal names the CONCEPT, not the host directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The refusal used to read <c>git did not report a repository root for '&lt;absolute
    /// directory&gt;'</c>. That directory is resolved by the CLI from the discovery path, so it is
    /// a host path in an author-facing diagnostic — the class #357 closed and #375/#473/#488
    /// widened.
    /// </para>
    /// <para>
    /// The second assertion is the half that stops the fix from being a redaction: a message that
    /// merely dropped the directory would leave the author knowing something failed and nothing
    /// about what to do. It must still say which condition was not met.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoRepositoryRoot_NamesTheConcept_NotTheWorkingDirectory()
    {
        // rev-parse succeeds and prints nothing but whitespace: the shape that reaches the refusal.
        var runner = new FakeProcessRunner().With("rev-parse", exit: 0, stdout: "   \n");

        var ex = Assert.Throws<ChangeSetException>(() => NewChangeSet("main", runner));

        HostPathDisclosure.AssertNoAbsoluteHostPath(
            "the no-repository-root refusal", ex.Message, RepoRoot);

        Assert.Contains("git working tree", ex.Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// #498, site 3 — git's stderr is relayed with its absolute paths substituted, not verbatim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// git routinely names host paths on the failure paths this selection reaches: the dubious
    /// ownership refusal below is the canonical one, and it is the whole message, so DROPPING the
    /// stderr would take the diagnosis with it. Substitution keeps the sentence and removes the
    /// path — which is why the row asserts BOTH halves rather than only the absence.
    /// </para>
    /// <para>
    /// The predicate is the shared one (<see cref="HostPathDisclosure"/>) rather than a
    /// <c>DoesNotContain</c> on this literal, so it also refuses any other rooted token a future
    /// relay might carry.
    /// </para>
    /// </remarks>
    [Fact]
    public void NonZeroGitExit_SubstitutesAbsolutePathsInStderr_AndKeepsTheDiagnosis()
    {
        const string HostRepository = "/host/private/checkout";

        var runner = new FakeProcessRunner()
            .With("rev-parse", exit: 0, stdout: RepoRoot + "\n")
            .With(
                "diff",
                exit: 128,
                stderr: $"fatal: detected dubious ownership in repository at '{HostRepository}'");

        var ex = Assert.Throws<ChangeSetException>(() => NewChangeSet("main", runner));

        HostPathDisclosure.AssertNoAbsoluteHostPath(
            "the non-zero git exit diagnostic", ex.Message, HostRepository);

        Assert.Contains("dubious ownership", ex.Message, System.StringComparison.Ordinal);
        Assert.Contains("exit 128", ex.Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The same substitution covers the stdout fallback, which a git that writes its refusal to the
    /// wrong stream would otherwise leak through untouched.
    /// </summary>
    [Fact]
    public void NonZeroGitExit_SubstitutesAbsolutePaths_InTheStdoutFallback()
    {
        const string HostRepository = "/host/private/checkout";

        var runner = new FakeProcessRunner()
            .With("rev-parse", exit: 0, stdout: RepoRoot + "\n")
            .With("diff", exit: 128, stdout: $"could not read '{HostRepository}/.git/config'");

        var ex = Assert.Throws<ChangeSetException>(() => NewChangeSet("main", runner));

        HostPathDisclosure.AssertNoAbsoluteHostPath(
            "the non-zero git exit stdout fallback", ex.Message, HostRepository);

        Assert.Contains("could not read", ex.Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A relative path in git's output is NOT substituted — the half that keeps the rule from
    /// degrading into blanket redaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>error: pathspec '...'</c> names text the author typed or a repo-relative file, and both
    /// are exactly what a diagnostic is supposed to name (#357's rule is "declared, not resolved").
    /// A substitution that could not tell the two apart would be no better than dropping the relay.
    /// </para>
    /// <para>
    /// <strong>SAID OUT LOUD: this assertion passes against the DEFECT too.</strong> Revert the
    /// substitution entirely and a raw relay still contains <c>tests/orders.e2e.yaml</c>, so this
    /// row is green on a message that leaks every host path git names. It is an OVER-REDACTION
    /// guard and nothing else — the rows above it are what can see the leak, and this one exists so
    /// that closing the leak cannot be done by blanket redaction. Same shape as
    /// <c>StdinEofShutdownDiagnosticTests</c>' exit-code assertion, and labelled for the same
    /// reason: an unlabelled row of this kind reads as coverage it does not provide.
    /// </para>
    /// </remarks>
    [Fact]
    public void NonZeroGitExit_LeavesRelativePathsAndRefsAlone()
    {
        var runner = new FakeProcessRunner()
            .With("rev-parse", exit: 0, stdout: RepoRoot + "\n")
            .With(
                "diff",
                exit: 1,
                stderr: "error: pathspec 'tests/orders.e2e.yaml' did not match any file known to git");

        var ex = Assert.Throws<ChangeSetException>(() => NewChangeSet("main", runner));

        Assert.Contains(
            "tests/orders.e2e.yaml", ex.Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The substitution rule itself, including the residue its own documentation admits to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The QUOTED path with spaces is the case with teeth, and it is the default Windows
    /// shape.</strong> git quotes with <c>'</c>, which is itself a token separator, so before the
    /// rule became quote-aware <c>'C:\Users\John Smith\src\repo'</c> substituted
    /// <c>C:\Users\John</c> and left <c>Smith\src\repo</c> standing. A profile name of two words is
    /// not exotic; neither is <c>C:\Program Files\Git</c>.
    /// </para>
    /// <para>
    /// The UNQUOTED case is the residue that remains: split by the token rule, so <c>C:\Program</c>
    /// is substituted and <c>Files\Git\x</c> survives. git quotes the paths it names, so this is now
    /// the narrower of the two — but it is still a stated limitation, and pinning it means a later
    /// change shows up as a moved expectation instead of a quiet widening or narrowing.
    /// </para>
    /// <para>
    /// The apostrophe row is what keeps the quote-awareness from mispairing: a quote opens a span
    /// only at the start or after a separator, so the <c>'</c> in <c>couldn't</c> is an ordinary
    /// character and the path's own quotes still pair with each other.
    /// </para>
    /// <para>
    /// The Windows-shaped cases are asserted only on Windows:
    /// <see cref="Path.IsPathRooted(string)"/> reads a drive letter on that platform alone, so a
    /// POSIX run would be asserting a coincidence.
    /// </para>
    /// </remarks>
    [Fact]
    public void SubstituteAbsolutePaths_ReplacesRootedTokensOnly()
    {
        Assert.Equal(
            "fatal: could not read '<path>'",
            GitChangeSet.SubstituteAbsolutePaths("fatal: could not read '/etc/gitconfig'"));

        // Relative paths, refs and ordinary prose are untouched.
        Assert.Equal(
            "error: pathspec 'tests/a.e2e.yaml' did not match origin/main",
            GitChangeSet.SubstituteAbsolutePaths(
                "error: pathspec 'tests/a.e2e.yaml' did not match origin/main"));

        // A trailing full stop survives the substitution of the token it follows.
        Assert.Equal(
            "at <path>.",
            GitChangeSet.SubstituteAbsolutePaths("at /var/lib/x."));

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(
            @"in repository at '<path>'",
            GitChangeSet.SubstituteAbsolutePaths(@"in repository at 'C:\src\repo'"));

        // The default Windows shape: a QUOTED path containing spaces is one token, so nothing of
        // it survives. Both spellings git uses, since rev-parse answers with forward slashes.
        Assert.Equal(
            "fatal: detected dubious ownership in repository at '<path>'",
            GitChangeSet.SubstituteAbsolutePaths(
                "fatal: detected dubious ownership in repository at "
                + "'C:/Users/John Smith/src/repo'"));
        Assert.Equal(
            @"in repository at '<path>'",
            GitChangeSet.SubstituteAbsolutePaths(@"in repository at 'C:\Program Files\Git\repo'"));

        // An apostrophe mid-word does not open a span, so the path's own quotes still pair.
        Assert.Equal(
            @"error: couldn't read '<path>'",
            GitChangeSet.SubstituteAbsolutePaths(@"error: couldn't read 'C:\Users\John Smith\x'"));

        // The documented residue: an UNQUOTED path with a space loses only its rooted head.
        Assert.Equal(
            @"<path> Files\Git\x",
            GitChangeSet.SubstituteAbsolutePaths(@"C:\Program Files\Git\x"));
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
    /// search to lose. A SECOND term also beats <c>PATH</c> — the calling process's own current
    /// directory, which for <c>cd untrusted-repo &amp;&amp; vouchfx run . --changed-since main</c>
    /// is the repository under test. <c>GitChangeSet</c>'s header carries both measurements, the
    /// environment variable that made the second one look unreproducible, and what remains open.
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

    // ---- The confined git environment (#500) ------------------------------------------

    /// <summary>
    /// #500 — every git call is handed a CONFINED environment, and all three are handed the SAME
    /// one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wiring assertion, independent of any real git: a confinement that is built correctly and
    /// then not passed to the runner closes nothing. The planted name is secret-SHAPED rather than
    /// a real secret because what is being asserted is the filter's default — anything not named is
    /// absent — not a special case for the word "secret".
    /// </para>
    /// <para>
    /// "The same one" matters for the same reason the executable is resolved once: three calls that
    /// disagreed about what git can see would be one logical operation computed under two
    /// configurations.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryGitCall_ReceivesTheSameConfinedEnvironment()
    {
        const string SecretName = "VOUCHFX_TEST_CHANGESET_SECRET";
        Environment.SetEnvironmentVariable(SecretName, "not-for-git");
        try
        {
            var runner = Runner();
            _ = NewChangeSet("main", runner);

            Assert.Equal(3, runner.Calls.Count); // rev-parse, diff, status

            var first = runner.Calls[0].Environment;
            Assert.NotNull(first);
            Assert.Contains("PATH", first!.Keys, System.StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(SecretName, first.Keys, System.StringComparer.OrdinalIgnoreCase);

            Assert.All(runner.Calls, call => Assert.Same(first, call.Environment));
        }
        finally
        {
            // null, not "": an empty value DELETES on net8, which would make the cleanup
            // indistinguishable from a set-to-empty and hide a leak in a later row.
            Environment.SetEnvironmentVariable(SecretName, null);
        }
    }

    /// <summary>
    /// The filter itself: allow-listed names and <c>GIT_</c>-prefixed names survive, nothing else
    /// does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exercised against a hand-built source rather than the process's own environment, so the row
    /// asserts the RULE rather than whatever the host happens to have set.
    /// </para>
    /// <para>
    /// <strong>NOT a <c>[Theory]</c> over the platform flag, and it used to be one for
    /// nothing.</strong>
    /// Every key here is spelt canonically, so <c>OrdinalIgnoreCase</c> and <c>Ordinal</c> return
    /// the same answer and the parameter changed no outcome — two rows asserting one thing. What is
    /// worth asserting is that the allow-list CONTENT does not depend on the platform, so both
    /// values are passed here and the same expectation is claimed for each. The case rule itself is
    /// pinned by <c>ConfineEnvironment_FollowsThePlatformCaseRule</c>, whose source IS spelt
    /// lower-case and which is therefore the row that can see a comparer change.
    /// </para>
    /// <para>
    /// <c>XDG_CONFIG_HOME</c>, <c>ProgramData</c> and <c>LD_LIBRARY_PATH</c> are listed because the
    /// first two decide WHICH git config is read — and therefore, through
    /// <c>status.showUntrackedFiles</c> and <c>core.excludesFile</c>, which scenarios
    /// <c>--changed-since</c> selects — while the third decides whether a custom-prefix git loads
    /// at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void ConfineEnvironment_KeepsTheAllowListAndTheGitPrefix_AndNothingElse()
    {
        var source = new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin",
            ["LD_LIBRARY_PATH"] = "/opt/git/lib",
            ["HOME"] = "/home/dev",
            ["XDG_CONFIG_HOME"] = "/home/dev/.config",
            ["ProgramData"] = @"C:\ProgramData",
            ["TMPDIR"] = "/tmp",
            ["GIT_SSH_COMMAND"] = "ssh -i key",
            ["AWS_SECRET_ACCESS_KEY"] = "leak-me",
            ["SSH_AUTH_SOCK"] = "/run/agent",
            ["HTTPS_PROXY"] = "http://proxy:3128",
            ["LANG"] = "en_GB.UTF-8",
        };

        var expected = new[]
        {
            "GIT_SSH_COMMAND",
            "HOME",
            "LD_LIBRARY_PATH",
            "PATH",
            "ProgramData",
            "TMPDIR",
            "XDG_CONFIG_HOME",
        };

        foreach (var windows in new[] { true, false })
        {
            var confined = GitChangeSet.ConfineEnvironment(source, windows);

            Assert.Equal(
                expected,
                confined.Keys.OrderBy(k => k, System.StringComparer.Ordinal).ToArray());
        }
    }

    /// <summary>
    /// Names compare case-INSENSITIVELY under the Windows rule and case-SENSITIVELY otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both arms run on every host — the rule is a pure string decision, so pinning it does not
    /// need the platform it describes, and no blocking CI lane is Windows (#366).
    /// </para>
    /// <para>
    /// The Windows arm is not pedantry: Windows resolves variable names case-insensitively, so an
    /// operator's <c>git_ssh_command</c> IS <c>GIT_SSH_COMMAND</c> to git. Dropping it there would
    /// silently change behaviour that existed before the confinement.
    /// </para>
    /// </remarks>
    [Fact]
    public void ConfineEnvironment_FollowsThePlatformCaseRule()
    {
        var source = new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["path"] = "/usr/bin",
            ["git_ssh_command"] = "ssh -i key",
        };

        Assert.Equal(2, GitChangeSet.ConfineEnvironment(source, windows: true).Count);
        Assert.Empty(GitChangeSet.ConfineEnvironment(source, windows: false));
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
    /// is an open and UNFILED question. This comment used to cite issues #480 and #466-B; neither
    /// answers it (see <see cref="GitTimesOut_SurfacesChangeSetException_NamingTheBudget"/>'s
    /// remarks for why).
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
    /// THE ONE LOAD-BEARING ASSUMPTION, named so the next reader knows what to re-check: that
    /// <c>Process.Unix.cs</c>'s <c>ResolvePath</c> short-circuits on a ROOTED filename and hands
    /// it back unchanged. If it ever normalised or re-searched instead, <c>resolvedFilename</c>
    /// would no longer be the string this row passed in, and assertion (2) would fail on Linux
    /// while proving nothing about disclosure. This row passes a rooted path, so that branch is
    /// the only one it can take.
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
    /// probe: on Windows the candidate must exist under exactly the name the search composes, on
    /// POSIX it must carry an execute bit. The <c>extension</c> argument (including its dot;
    /// defaulting to this platform's own) exists so a row can plant a name the search must NOT
    /// compose — a <c>.cmd</c> shim. The <c>mode</c> argument overrides the <c>executable</c>
    /// shorthand, so a row can plant a mode no boolean names — the group/other-execute-only file
    /// #509 is about.
    /// The directory is removed on every path so a run leaves nothing behind.
    /// </remarks>
    private sealed class LocatorFixture : IDisposable
    {
        public LocatorFixture(
            string name = "git",
            bool executable = true,
            string? parentDirectory = null,
            string? extension = null,
            UnixFileMode? mode = null)
        {
            DirectoryPath = Path.Combine(
                parentDirectory ?? Path.GetTempPath(),
                "vouchfx-locate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);

            ExecutablePath = Path.Combine(
                DirectoryPath,
                name + (extension ?? (OperatingSystem.IsWindows() ? ".exe" : string.Empty)));
            File.WriteAllText(ExecutablePath, string.Empty);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    ExecutablePath,
                    mode ?? (executable
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite));
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

        var located = GitChangeSet.LocateOnPath("git", fixture.DirectoryPath);

        Assert.Equal(fixture.ExecutablePath, located, ignoreCase: OperatingSystem.IsWindows());
    }

    /// <summary>
    /// A <c>git.cmd</c> or <c>git.bat</c> is NOT a candidate, and that is a security property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this row exists.</strong> A batch shim launched with
    /// <c>UseShellExecute = false</c> still runs through <c>cmd.exe</c>, and cmd's parser re-reads
    /// the arguments that <c>ArgumentList</c> quoted for <c>CreateProcess</c>. Measured on this
    /// host, net8.0: <c>Process.Start</c> against a rooted <c>git.cmd</c> with
    /// <c>ArgumentList = ["diff", "\"&amp;echo INJECTED&amp;\""]</c> made the child print
    /// <c>INJECTED</c>. Since <c>--changed-since</c> reaches <c>ArgumentList</c> verbatim, a shim
    /// candidate would make this class a command-execution sink. The OS search it replaces never
    /// had that reach either — measured in the same probe, a bare-name launch with a <c>PATH</c>
    /// directory holding only <c>git.cmd</c> threw <c>Win32Exception … The system cannot find the
    /// file specified</c>, so the OS appends <c>.exe</c> and nothing else.
    /// </para>
    /// <para>
    /// The control assertions are what make the two negatives mean something: the same search
    /// against a real <c>git.exe</c> DOES resolve, and a shim in an EARLIER entry does not shadow
    /// a real git in a later one.
    /// </para>
    /// </remarks>
    [Fact]
    public void LocateOnPath_Windows_DoesNotSelectABatchShim()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // A .cmd is not an executable off Windows; there is nothing to refuse.
        }

        using var shim = new LocatorFixture(extension: ".cmd");
        using var batch = new LocatorFixture(extension: ".bat");
        using var real = new LocatorFixture();

        Assert.Null(GitChangeSet.LocateOnPath("git", shim.DirectoryPath));
        Assert.Null(GitChangeSet.LocateOnPath("git", batch.DirectoryPath));

        // Control: the same search finds a real git.exe...
        Assert.Equal(
            real.ExecutablePath,
            GitChangeSet.LocateOnPath("git", real.DirectoryPath),
            ignoreCase: true);

        // ...and the shim, listed FIRST, does not shadow it.
        var entries = shim.DirectoryPath + Path.PathSeparator + real.DirectoryPath;
        Assert.Equal(
            real.ExecutablePath,
            GitChangeSet.LocateOnPath("git", entries),
            ignoreCase: true);
    }

    /// <summary>
    /// The Windows candidate is exactly <c>git.exe</c> — asserted on EVERY platform.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this row exists alongside the one above.</strong> That row plants a real
    /// <c>.cmd</c>/<c>.bat</c> on disk, so it early-returns off Windows, and every blocking CI lane
    /// is <c>ubuntu-latest</c> (#366) — leaving the name this seam composes unasserted on the lane
    /// that actually blocks. This row takes the platform as an argument instead of reading it, so
    /// it runs everywhere.
    /// </para>
    /// <para>
    /// <strong>It pins the name, and states its own limit rather than implying more.</strong> A
    /// <c>PATHEXT</c>-style widening would be a loop over several extensions inside
    /// <see cref="GitChangeSet.LocateOnPath(string, string?)"/>, leaving
    /// <see cref="GitChangeSet.CandidateFileName(string, bool)"/> returning <c>git.exe</c>
    /// untouched — so it would pass this row and early-return on the Windows-only one. Nothing in
    /// this file gates the coupling between the two, and no source census was added for it; the
    /// residual is recorded on <c>CandidateFileName</c> itself.
    /// </para>
    /// </remarks>
    [Fact]
    public void CandidateFileName_UnderTheWindowsRule_IsExactlyTheExeName()
    {
        Assert.Equal("git.exe", GitChangeSet.CandidateFileName("git", windows: true));
        Assert.Equal("git", GitChangeSet.CandidateFileName("git", windows: false));
    }

    /// <summary>
    /// On POSIX a file without an execute bit is not a candidate.
    /// </summary>
    [Fact]
    public void LocateOnPath_Posix_RequiresAnExecuteBit()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows has no execute bit; the `.exe` name is the test there.
        }

        using var executable = new LocatorFixture();
        using var notExecutable = new LocatorFixture(executable: false);

        Assert.NotNull(GitChangeSet.LocateOnPath("git", executable.DirectoryPath));
        Assert.Null(GitChangeSet.LocateOnPath("git", notExecutable.DirectoryPath));
    }

    /// <summary>
    /// On POSIX the question is whether THIS caller may execute the file, not whether anyone may
    /// (#509).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The shadowing assertion is the one with teeth.</strong> The old test accepted a
    /// file when ANY of the three execute bits was set, and <c>LocateOnPath</c> returns the FIRST
    /// match — so a file this caller cannot launch, sitting in an earlier entry, ended the search
    /// and produced exit 2 on a host holding a runnable git further along. Same harm shape as the
    /// whitespace trim <c>LocateOnPath_TakesEntriesVerbatim_NeitherUnquotingNorTrimming</c>
    /// records: an entry the operating system would have passed over shadows a legitimate one.
    /// </para>
    /// <para>
    /// <strong>The plant is a file this caller OWNS with no user-execute bit</strong>
    /// (<c>0611</c> — <c>UserRead | UserWrite | GroupExecute | OtherExecute</c>, the mode the
    /// assertions below actually set), which is what makes the row reproducible without a second
    /// uid: POSIX picks the permission class by ownership and stops there, so the owner is judged on
    /// the user bits alone and the group/other ones do not rescue it. Measured in
    /// <c>mcr.microsoft.com/dotnet/sdk:8.0</c> as uid 1000: mode bits said executable,
    /// <c>access(X_OK)</c> said not, and <c>Process.Start</c> refused it with "Permission denied".
    /// </para>
    /// <para>
    /// The owner triad has to be the non-executable one for the divergence to exist. An earlier
    /// draft of this remark, and of <c>GitChangeSet</c>'s matching paragraph, said <c>0311</c>;
    /// <c>3</c> is write+execute, so for a file the caller owns <c>access(X_OK)</c> returns 0 and
    /// the stated measurement is not producible at all.
    /// </para>
    /// <para>
    /// <strong>Root is skipped rather than asserted against, and the two skips are different
    /// claims.</strong> Off POSIX there is no execute bit to set. As root <c>access(X_OK)</c>
    /// succeeds on any execute bit AND so does the launch — measured in the same image — so there
    /// is no divergence to pin, not a divergence being tolerated. The root probe reads a file it
    /// has just stripped to mode <c>0000</c>: root succeeds, every other uid gets
    /// <see cref="UnauthorizedAccessException"/>. <see cref="Environment.UserName"/> would not do
    /// — measured EMPTY for a uid with no passwd entry, which is the shape a container runs in.
    /// </para>
    /// <para>
    /// The control assertion carries one environmental assumption worth naming: it needs the
    /// temporary directory to be exec-mountable, because unlike the mode-bit test it replaces,
    /// <c>access(X_OK)</c> answers for the mount as well as the file.
    /// </para>
    /// </remarks>
    [Fact]
    public void LocateOnPath_Posix_RefusesAFileThisCallerMayNotExecute()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // No execute bit and no access(2); the `.exe` name is the whole test there.
        }

        if (HasRootsReach())
        {
            return; // root may execute it and CAN launch it: there is nothing here to diverge.
        }

        using var unrunnable = new LocatorFixture(
            mode: UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        using var runnable = new LocatorFixture();

        // The superseded test said YES to this file, so without this line the row could pass
        // against a plant that simply carried no execute bit at all.
        Assert.NotEqual(
            UnixFileMode.None,
            File.GetUnixFileMode(unrunnable.ExecutablePath)
                & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute));

        Assert.Null(GitChangeSet.LocateOnPath("git", unrunnable.DirectoryPath));

        // Control: a file this caller CAN execute still resolves.
        Assert.NotNull(GitChangeSet.LocateOnPath("git", runnable.DirectoryPath));

        // ...and the unrunnable entry, listed FIRST, does not end the search.
        var entries = unrunnable.DirectoryPath + Path.PathSeparator + runnable.DirectoryPath;
        Assert.Equal(runnable.ExecutablePath, GitChangeSet.LocateOnPath("git", entries));
    }

    /// <summary>
    /// Whether this caller bypasses POSIX permission checks, i.e. is root.
    /// </summary>
    /// <returns><see langword="true"/> when a mode-<c>0000</c> file it owns still reads.</returns>
    [UnsupportedOSPlatform("windows")]
    private static bool HasRootsReach()
    {
        var probe = Path.Combine(
            Path.GetTempPath(), "vouchfx-root-probe-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(probe, "x");

        try
        {
            File.SetUnixFileMode(probe, UnixFileMode.None);
            _ = File.ReadAllText(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            // Restored before the delete so the row leaves nothing behind on a host whose
            // temporary directory it does not own outright.
            File.SetUnixFileMode(probe, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(probe);
        }
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

        Assert.NotNull(GitChangeSet.LocateOnPath("git", fixture.DirectoryPath));

        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), fixture.DirectoryPath);
        Assert.False(Path.IsPathFullyQualified(relative), relative);

        // The empty entry means "the current directory" and the relative one resolves against it;
        // the same directory that resolved above must NOT resolve when spelt either way.
        var entries = string.Empty + Path.PathSeparator + relative;
        Assert.Null(GitChangeSet.LocateOnPath("git", entries));
    }

    /// <summary>
    /// An entry is taken VERBATIM: neither unquoted nor trimmed of surrounding whitespace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both normalisations are <c>cmd.exe</c> behaviours rather than <c>CreateProcess</c> ones, so
    /// either resolves an entry the OS search this replaces does not. Measured on Windows 11 build
    /// 26200.9168 / net8.0, with a bare-name <c>Process.Start</c> (<c>UseShellExecute = false</c>)
    /// against a directory holding one real executable: spelt plainly it LAUNCHED; spelt with a
    /// leading space, a trailing space, a leading tab, or wrapped in literal quotes it was NOT
    /// FOUND in all four cases. The five assertions below are those five answers.
    /// </para>
    /// <para>
    /// <strong>The shadowing assertion is the one with teeth.</strong> A <c>.Trim()</c> here used
    /// to resolve all three whitespace spellings, so a leading-space entry written FIRST — the
    /// shape a <c>PATH=%PATH%; C:\tools</c> hand-edit leaves behind — beat a real git in a later
    /// entry, promoting a directory Windows ignores to the highest-priority one in this search.
    /// Cross-platform on purpose, and by two different mechanisms: the quoted and leading-whitespace
    /// spellings are not fully qualified on either platform, while the trailing-whitespace one is,
    /// and is refused one step later because it composes to <c>&lt;dir&gt; /git</c>, which no
    /// platform holds. So the row runs on the blocking ubuntu lane rather than skipping there.
    /// </para>
    /// </remarks>
    [Fact]
    public void LocateOnPath_TakesEntriesVerbatim_NeitherUnquotingNorTrimming()
    {
        using var fixture = new LocatorFixture();
        using var real = new LocatorFixture();

        // Control: the same directory, spelt plainly, DOES resolve.
        Assert.NotNull(GitChangeSet.LocateOnPath("git", fixture.DirectoryPath));

        Assert.Null(GitChangeSet.LocateOnPath("git", "\"" + fixture.DirectoryPath + "\""));
        Assert.Null(GitChangeSet.LocateOnPath("git", " " + fixture.DirectoryPath));
        Assert.Null(GitChangeSet.LocateOnPath("git", fixture.DirectoryPath + " "));
        Assert.Null(GitChangeSet.LocateOnPath("git", "\t" + fixture.DirectoryPath));

        // ...and a whitespace-decorated entry listed FIRST does not shadow a plain later one.
        var entries = " " + fixture.DirectoryPath + Path.PathSeparator + real.DirectoryPath;
        Assert.Equal(
            real.ExecutablePath,
            GitChangeSet.LocateOnPath("git", entries),
            ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void LocateOnPath_ReturnsNull_WhenNoEntryHoldsTheExecutable()
    {
        using var fixture = new LocatorFixture();

        Assert.Null(GitChangeSet.LocateOnPath("no-such-tool", fixture.DirectoryPath));
        Assert.Null(GitChangeSet.LocateOnPath("git", pathVariable: null));
        Assert.Null(GitChangeSet.LocateOnPath("git", string.Empty));
    }

    /// <summary>
    /// The production locator yields a real, fully qualified git wherever the operating system's
    /// own bare-name launch finds one — and never yields something unrooted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The second assertion is the functional-regression gate, and until it was added
    /// nothing was one.</strong> This row used to early-return when the locator reported nothing,
    /// and the only other row that runs a real git —
    /// <see cref="RealGit_AgainstThisRepo_DoesNotThrow"/> — catches
    /// <see cref="ChangeSetException"/> and moves on. That catch predates #499 but its scope
    /// widened with it: it used to swallow "<c>Process.Start</c> could not find git" and now also
    /// swallows "OUR search could not find git". So a narrowing that refused a genuinely installed
    /// git went green on every lane, on a change whose whole premise is replacing the operating
    /// system's search with this one. <c>git</c> is present on <c>ubuntu-latest</c> — <c>actions/checkout</c>
    /// requires it — so the assertion reddens the blocking lane rather than skipping there.
    /// </para>
    /// <para>
    /// <strong>The reference is deliberately the operating system's own answer, and it is a wider
    /// one, so a red here has three shapes and only the first is a defect in the resolver.</strong>
    /// (1) A genuine narrowing — the regression this row exists to catch. (2) A <c>git</c> reachable
    /// ONLY through one of the terms the header of <see cref="GitChangeSet"/> enumerates as dropped,
    /// deliberately not re-listed here: a second copy of that list is exactly what drifts out of step
    /// with it. Whichever term it is, a git reachable only that way is worth a red build in its own
    /// right. (3) A <c>PATH</c> ENTRY THIS RESOLVER
    /// DELIBERATELY SKIPS WHILE THE OS HONOURS IT, holding the host's only git: a RELATIVE entry,
    /// refused on both platforms by <c>LocateOnPath</c>'s <c>Path.IsPathFullyQualified</c> guard,
    /// and an EMPTY element, which that same guard skips and which the OS reads as the current
    /// directory on some platforms — see <c>LocateOnPath</c>'s own remarks, which are where that
    /// scope is stated. Shape (3) is a design
    /// decision rather than a defect, and it is listed so that a
    /// red does not send a reader hunting for a planted git. No attempt is made to subtract the
    /// dropped terms; doing so would mean mutating this process's <c>PATH</c>, which races every
    /// other row in the assembly.
    /// </para>
    /// </remarks>
    [Fact]
    public void LocateGitOnPath_FindsAnyGitTheOperatingSystemWouldLaunch()
    {
        var located = GitChangeSet.LocateGitOnPath();

        if (located is not null)
        {
            Assert.True(Path.IsPathFullyQualified(located), located);
            Assert.True(File.Exists(located), located);
        }

        if (BareNameGitLaunches())
        {
            Assert.NotNull(located);
        }
    }

    /// <summary>
    /// Whether this host launches <c>git --version</c> from the bare name, by the operating
    /// system's own search — the reference the row above compares against.
    /// </summary>
    /// <remarks>
    /// <strong>BOUNDED, AND A TIMEOUT COUNTS AS "DOES NOT LAUNCH".</strong> <c>git --version</c> is
    /// not the #392 shape — it spawns nothing that could hold the inherited pipes — but this is a
    /// child wait in the same assembly as the rows that exist to prove unbounded child waits wedge
    /// the CLI, and an unbounded one here would hang the blocking lane with no diagnostic: no
    /// assertion message, no failing row, just a job that never returns. The reads are started
    /// before the wait, because a child that fills a redirected pipe blocks otherwise, and they are
    /// ABANDONED rather than awaited on the timeout path — the same contract
    /// <see cref="IProcessRunner"/> documents, for the same reason. Returning <see langword="false"/>
    /// on a timeout costs only the strength of the row above (it stops asserting on a host that
    /// cannot answer the reference question), never a false green on a real narrowing.
    /// </remarks>
    private static bool BareNameGitLaunches()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git", // BARE, so the OS performs the search this file replaces.
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--version");

        try
        {
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            // Started before the wait: a child that fills a redirected pipe blocks otherwise.
            var drain = Task.WhenAll(
                process.StandardOutput.ReadToEndAsync(),
                process.StandardError.ReadToEndAsync());

            if (!process.WaitForExit(BareNameProbeBudgetMilliseconds))
            {
                ChildProcess.KillTreeQuietly(process);
                ObserveQuietly(drain);
                return false;
            }

            ObserveQuietly(drain);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // No git this host can launch from the bare name.
        }
    }

    /// <summary>
    /// Swallows a faulted read so an abandoned capture cannot surface later as an unobserved task
    /// exception attributed to whichever row happens to be running.
    /// </summary>
    private static void ObserveQuietly(Task task) =>
        _ = task.ContinueWith(
            static faulted => _ = faulted.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

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

    /// <summary>
    /// #500 — all three real git subcommands succeed under the confined environment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The allow-list is an ASSERTION about what git needs, and only a real git can check
    /// it.</strong> Every other row in this section proves the filter builds what it was told to
    /// build; none of them can say whether that set is SUFFICIENT. A variable git quietly needs —
    /// <c>SystemRoot</c> is the sharp example, without which a Windows child fails inside Win32
    /// initialisation — turns <c>--changed-since</c> into exit 2 on a user's machine and nothing in
    /// this file would have noticed.
    /// </para>
    /// <para>
    /// The three argument vectors are the ones the constructor issues, character for character, so
    /// this row moves when they do rather than testing a paraphrase of them.
    /// </para>
    /// <para>
    /// <strong>It no-ops when git is unavailable or the assembly is outside a work tree</strong>,
    /// matching the smoke row above, which means it proves nothing on a host without git. Stated
    /// rather than hidden: this is evidence from the maintainer's host, not a CI gate. What it is
    /// NOT allowed to do is swallow a failure once git IS available — a confined environment that
    /// breaks a subcommand must be red, which is why there is no <c>catch</c> here.
    /// </para>
    /// <para>
    /// Read-only: <c>rev-parse</c>, <c>diff</c> and <c>status</c> mutate no repository state, and
    /// <c>HEAD...HEAD</c> is deliberately an empty range.
    /// </para>
    /// </remarks>
    [Fact]
    public void RealGit_ThreeSubcommands_SucceedUnderTheConfinedEnvironment()
    {
        var repoDir = FindRepoRoot(System.AppContext.BaseDirectory);
        var gitExecutable = GitChangeSet.LocateGitOnPath();
        if (repoDir is null || gitExecutable is null)
        {
            return; // No git, or not inside a work tree — nothing to measure.
        }

        var confined = GitChangeSet.ConfinedGitEnvironment();

        var subcommands = new[]
        {
            new[] { "rev-parse", "--show-toplevel" },
            new[] { "diff", "--name-only", "--end-of-options", "HEAD...HEAD" },
            new[]
            {
                "--no-optional-locks", "-c", "core.quotepath=false", "status", "--porcelain",
            },
        };

        foreach (var arguments in subcommands)
        {
            var result = SystemProcessRunner.Instance.Run(
                gitExecutable, arguments, repoDir, confined);

            Assert.True(
                result.ExitCode == 0,
                $"git {arguments[0]} exited {result.ExitCode} under the confined environment: "
                + result.StandardError.Trim());
        }

        // rev-parse must have produced a root, not merely exited 0: an empty answer is the shape
        // ResolveRepoRoot refuses, and it would mean the confinement had changed what git sees.
        var root = SystemProcessRunner.Instance.Run(
            gitExecutable, new[] { "rev-parse", "--show-toplevel" }, repoDir, confined);
        Assert.NotEmpty(root.StandardOutput.Trim());
    }

    /// <summary>
    /// A real git accepts <c>--no-optional-locks</c> where the constructor puts it, and answers the
    /// SAME porcelain status with it as without it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two claims, and the first is the one a wrong spelling breaks.</strong>
    /// <c>--no-optional-locks</c> is a git-level option: after the subcommand git parses it as a
    /// <c>status</c> option and exits non-zero, which the argv row above cannot detect because a
    /// fake runner parses nothing. Exit 0 from a real git is what establishes the position.
    /// </para>
    /// <para>
    /// The second claim is that the flag changes no OUTPUT. It suppresses the opportunistic
    /// rewrite of <c>.git/index</c> — which is the point, since that write is what a concurrent git
    /// in the same working tree turns into exit 128 — and suppressing it must not change which
    /// paths are reported, or <c>--changed-since</c> would select a different set of scenarios.
    /// </para>
    /// <para>
    /// <strong>Quiescence is CHECKED rather than assumed, because this reads a live working
    /// tree.</strong> The plain form is run twice, on either side of the flagged one; if those two
    /// disagree the tree moved under the row (a build writing into it, an editor saving) and the
    /// equality claim is skipped rather than failed. Without that, the row would be flaky by
    /// construction and its first red would be read as a git regression.
    /// </para>
    /// <para>
    /// It no-ops without git or outside a work tree, exactly like the two rows above, so it is
    /// evidence from a host that has git rather than a CI gate.
    /// </para>
    /// </remarks>
    [Fact]
    public void RealGit_NoOptionalLocks_ChangesNoStatusOutput()
    {
        var repoDir = FindRepoRoot(System.AppContext.BaseDirectory);
        var gitExecutable = GitChangeSet.LocateGitOnPath();
        if (repoDir is null || gitExecutable is null)
        {
            return; // No git, or not inside a work tree — nothing to measure.
        }

        var confined = GitChangeSet.ConfinedGitEnvironment();

        ProcessResult Status(params string[] arguments) =>
            SystemProcessRunner.Instance.Run(gitExecutable, arguments, repoDir, confined);

        var before = Status("-c", "core.quotepath=false", "status", "--porcelain");
        var flagged = Status(
            "--no-optional-locks", "-c", "core.quotepath=false", "status", "--porcelain");
        var after = Status("-c", "core.quotepath=false", "status", "--porcelain");

        Assert.Equal(0, before.ExitCode);
        Assert.Equal(0, after.ExitCode);

        // The position claim: a git-level option spelt after `status` is refused outright.
        Assert.True(
            flagged.ExitCode == 0,
            "git refused `--no-optional-locks` in the position GitChangeSet puts it (exit "
            + $"{flagged.ExitCode}): {flagged.StandardError.Trim()}");

        if (!string.Equals(
            before.StandardOutput, after.StandardOutput, System.StringComparison.Ordinal))
        {
            return; // The working tree moved between the probes; there is no parity to claim.
        }

        Assert.Equal(before.StandardOutput, flagged.StandardOutput);
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
