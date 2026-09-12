// Vouchfx.Cli — GitChangeSet (S07-C-02).
//
// Computes "which scenario files changed since <ref>" ONCE by shelling out to git (behind
// IProcessRunner so it is unit-testable without a real repo), then answers IsChanged as a
// pure set lookup. Two sources are merged:
//   • `git diff --name-only <ref>...HEAD` — files changed in commits since the merge-base
//     of <ref> and HEAD (the three-dot form, so it is "what this branch changed", not
//     "everything that diverged on <ref>").
//   • `git status --porcelain`             — the dirty working tree (staged + unstaged +
//     untracked), so an as-yet-uncommitted scenario edit is still selected.
//
// git prints repo-relative, forward-slash paths; we resolve them against the repo root to
// absolute, normalise separators to '/', and store them in a case-tolerant set. A non-zero
// git exit (bad ref, not a repo), a launch failure (a git that was found and would not start,
// which since #499 is a DIFFERENT refusal from "no git on PATH") or a timeout (a wedged
// git, or one whose grandchild holds the capture pipes open — #481/#392) is wrapped in a
// ChangeSetException, which the CLI maps to a usage error (exit 2) — NEVER a crash. A CANCELLED
// call is the exception to that rule and propagates as OperationCanceledException: an operator's
// Ctrl+C is not a usage error, and the token exists so that it reaches the runner's tree-kill
// instead of the process being force-killed with that cleanup unrun.
//
// GIT IS LAUNCHED BY ABSOLUTE PATH, RESOLVED OFF `PATH` ONLY (#499).
// ──────────────────────────────────────────────────────────────────
// The bare name "git" used to be handed to the runner, and on Windows that is NOT a PATH lookup.
// .NET starts a process with `lpApplicationName = null`, putting everything in the command line
// (`Process.Windows.cs`, "we don't need this since all the info is in commandLine"), so Windows
// applies ITS OWN search order to the unqualified name. TWO of that order's terms are reachable
// from here, and BOTH were measured to beat `PATH` on a default host.
//
// Every measurement below was taken on Windows 11 build 26200.9168, net8.0, from a console app
// calling `Process.Start` with `UseShellExecute = false` — never through a shell, for the reason
// the re-probe warning below records. The impostor rows use a bare `FileName` against a planted
// `git.exe` that prints a marker. Two positive controls run in the same harness, so that a "not
// found" is never mistaken for a harness that cannot see an impostor: placing the plant directory
// ON `PATH` runs the marker, and removing the impostor again runs the real git. The `.cmd` pair
// further down uses a rooted `FileName` for its second half and was RE-TAKEN in that same
// console-app harness, rather than carried over from the earlier one.
//
// (1) THE APPLICATION LOAD DIRECTORY — beside the calling executable. An impostor there ran in
// preference to the real git on `PATH` (marker, exit 0); with the impostor removed, the identical
// call printed `git version 2.54.0.windows.1`. That is not hypothetical for a tool installed as a
// dotnet global tool: everything in `~/.dotnet/tools` is writable by the user, and one file
// dropped there takes over every git call this file makes. That same directory is also ON `PATH`
// for a global tool, which is why the NOT CLOSED paragraph below narrows this term rather than
// closing it.
//
// (2) THE CALLING PROCESS'S CURRENT DIRECTORY, which also beats `PATH`, on a host where
// `NoDefaultCurrentDirectoryInExePath` is absent — see the next paragraph, because that condition
// is the whole reason this term was once written off. With the impostor present only in that
// directory and no git on `PATH` at all, the marker ran (exit 0); with the real git added to
// `PATH`, the marker STILL ran. `cd untrusted-repo && vouchfx run . --changed-since main` gives
// the CLI exactly that current directory — the process inherits the shell's, and nothing in this
// CLI ever calls `Directory.SetCurrentDirectory` — so a `git.exe` committed at the root of the
// repository under test won, needing no write access to the user's profile, which makes it at
// least as reachable as (1).
//
// AN EARLIER REVISION OF THIS HEADER RECORDED (2) AS "PROBED AND DID NOT REPRODUCE", AND THAT
// NON-REPRODUCTION WAS AN ARTEFACT OF THE MEASURING ENVIRONMENT. `NoDefaultCurrentDirectoryInExePath`
// suppresses the current-directory term, and it is NOT a Windows default: measured on this host,
// `Machine=''` and `User=''`, while `Process='1'` in the environment this repository's tooling runs
// under. So every probe that inherited that environment was measuring a host with the term already
// switched off. (Which link in the chain sets it was not established; Git Bash was ruled out —
// launched from a parent without the variable, it does not add it.) Clearing it in the CALLING
// process flips the row. The child's environment block is not consulted at all: measured, SETTING
// the variable in `psi.Environment` while the caller's block lacked it did not re-suppress the
// term, so a fix or a probe applied there changes nothing.
//
// BEFORE CONCLUDING ANYTHING ABOUT (2), ASSERT THAT `[Environment]::CurrentDirectory` IS THE PLANT
// DIRECTORY. Measured: after `Set-Location 'C:\Windows\System32'`, `$PWD` read
// `C:\Windows\System32` while `[Environment]::CurrentDirectory` still read the shell's start
// directory. PowerShell's `Set-Location` moves `$PWD` but NOT the Win32 process current directory,
// and the latter is the one `CreateProcess` searches. A probe that changes directory that way
// therefore plants its impostor somewhere that was never the calling process's current directory,
// and reports NOT FOUND correctly without ever exercising this term — which is to say it
// reproduces the PRE-CORRECTION answer and puts the reader one step from deleting the paragraph
// above for a second time. The instruction is the assertion, not the harness: a console app gets
// this wrong just as easily.
//
// ONE HALF OF THE OLD CORRECTION STANDS, restated because it is easy to re-break: the original
// filing named the wrong MECHANISM. `ProcessStartInfo.WorkingDirectory` sets `lpCurrentDirectory`
// FOR THE CHILD and takes no part in resolving the command line's module name. The term that wins
// is the calling process's own current directory, which this CLI inherits from the shell rather
// than sets. The mechanism was wrong; the substance was right.
//
// A ROOTED `ProcessStartInfo.FileName` removes the whole question rather than answering it: both
// `CreateProcess` and `execve` take a rooted path literally and search nothing. So this change
// removes the search entirely — every term of it, including the application load directory, the
// calling process's current directory, and the system and Windows directories — and puts `PATH`,
// in order, in their place.
//
// NOT CLOSED, AND `~/.dotnet/tools` IS ONE OF THE DIRECTORIES IT IS NOT CLOSED AGAINST. An
// attacker-writable directory sitting EARLIER IN `PATH` than git's own still wins, because the
// search below takes the first `PATH` match and launches that. For a global tool the install
// directory from (1) is such a directory BY CONSTRUCTION — it has to be on `PATH` for the shell to
// find `vouchfx` at all — so the drop in (1) is NARROWED here, from an unconditional win to a
// `PATH`-ORDER-DEPENDENT one, and not refused. Nothing here re-orders or vets `PATH`; the change
// moves the resolution from "whatever Windows searches" to "`PATH`, in order, and nothing else",
// which is strictly smaller but is not empty.
//
// THE ONLY WINDOWS CANDIDATE IS `git.exe`, AND WIDENING THAT IS A SHELL-INJECTION SINK. The
// resolution replaces the OS search, so its candidate set must not be larger than the one it
// replaces. Measured on this host, net8.0: with a `PATH` directory holding only `git.cmd`, the
// bare-name launch threw `Win32Exception … The system cannot find the file specified` — the OS
// appends `.exe` and nothing else. Measured in the same probe: `Process.Start` on a rooted
// `git.cmd` with `UseShellExecute = false` DOES launch, through `cmd.exe`, and cmd's parser then
// re-reads the arguments — `ArgumentList = ["diff", "\"&echo INJECTED&\""]` made the child print
// `INJECTED`. `ArgumentList` quotes for `CreateProcess`, not for cmd, so a candidate set including
// `.CMD`/`.BAT` would turn `--changed-since` into command execution wherever git is installed as a
// shim. A wider set also changes WHICH git wins in two more ways: `.COM` precedes `.EXE` in the
// default `PATHEXT`, so it would shadow a sibling `git.exe` in the SAME directory, and a `git.cmd`
// in an earlier `PATH` directory would beat a real `git.exe` in a later one. So: `.exe` on
// Windows, the bare name plus an execute-bit check on POSIX, and no `PATHEXT` at all.
//
// This is a DIFFERENT hazard from the two guards already here, and neither addressed it. The
// leading-dash refusal plus `--end-of-options` defends git's own OPTION PARSING; `ArgumentList`
// defends against SHELL quoting. Which BINARY is resolved was covered by neither.
//
// The search lives in this file rather than in SystemProcessRunner because it is git-specific (the
// candidate name, the "is git installed" diagnostic) and that runner deliberately carries no git
// knowledge. It runs ONCE per change-set: three git calls, one resolution.
//
// THE GIT CHILD'S ENVIRONMENT IS CONFINED HERE, FOR THE SAME REASON (#500).
// ────────────────────────────────────────────────────────────────────────
// SystemProcessRunner grew an environment parameter; WHICH variables git needs is knowledge that
// belongs to this file, so ConfineEnvironment below is what builds the block and the runner stays
// git-agnostic. It too is computed ONCE per change-set and handed to all three calls.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Vouchfx.Cli.Selection;

/// <summary>
/// An <see cref="IChangeSet"/> backed by git, computed once on construction.
/// </summary>
/// <remarks>
/// <para>
/// The git shell-out is funnelled through an injected <see cref="IProcessRunner"/> so unit
/// tests can supply canned <c>git diff</c> / <c>git status</c> output (and exercise the
/// error paths) without a real repository.
/// </para>
/// <para>
/// Paths are stored normalised to <c>/</c> separators and compared with
/// <see cref="StringComparison.OrdinalIgnoreCase"/> so a Windows scenario path (with
/// <c>\</c>) matches git's forward-slash output regardless of drive-letter casing.
/// </para>
/// </remarks>
internal sealed class GitChangeSet : IChangeSet
{
    private readonly HashSet<string> _changed;

    /// <summary>
    /// Builds the change-set by running git in <paramref name="workingDirectory"/>.
    /// </summary>
    /// <param name="changedSinceRef">
    /// The git ref to diff against (e.g. <c>main</c>, a tag, or a SHA).
    /// </param>
    /// <param name="workingDirectory">A directory inside the working tree to run git in.</param>
    /// <param name="processRunner">The seam used to invoke git.</param>
    /// <param name="gitExecutableLocator">
    /// Overrides the <c>PATH</c> search that finds the git executable, returning a rooted path or
    /// <see langword="null"/> for "not found". Defaults to <see cref="LocateGitOnPath"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the git calls. Threaded through so a Ctrl+C during a wedged <c>--changed-since</c>
    /// reaches the runner's cleanup rather than waiting out the per-call budget; it surfaces as
    /// <see cref="OperationCanceledException"/>, NOT as a <see cref="ChangeSetException"/>.
    /// </param>
    /// <exception cref="ChangeSetException">
    /// Thrown when git is unavailable, the directory is not a repository, the ref is bad, a git
    /// call outlasts the per-call process budget, or its output capture fails.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="cancellationToken"/> is signalled during a git call.
    /// </exception>
    /// <remarks>
    /// <strong><paramref name="gitExecutableLocator"/> exists for the tests, and this says so
    /// rather than dressing it up.</strong> Production passes nothing. Without it every unit test
    /// in this class — including the dozen that only exercise PARSING against a canned runner that
    /// launches nothing — would depend on the host having a real git installed, because the
    /// resolution below happens before any call reaches the injected <see cref="IProcessRunner"/>
    /// and refuses the whole change-set when it fails. The alternative seam, mutating the process's
    /// <c>PATH</c> from a test, races every other test in the assembly.
    /// </remarks>
    public GitChangeSet(
        string changedSinceRef,
        string workingDirectory,
        IProcessRunner processRunner,
        Func<string?>? gitExecutableLocator = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(processRunner);

        // Argument-injection guard: a leading-dash ref (e.g. "--output=...") would be parsed
        // by git as an OPTION, not a revision, even when passed via ArgumentList (which only
        // prevents *shell* injection, not git's own option parsing). Reject such refs — and
        // null/empty/whitespace — BEFORE any git call that splices the ref into its argv.
        //
        // FIRST, ahead of the executable resolution below, because it is the cheaper refusal and
        // the one that depends on nothing outside this process: a malformed ref must be reported
        // as a malformed ref even on a host with no git at all.
        if (string.IsNullOrWhiteSpace(changedSinceRef) || changedSinceRef.StartsWith('-'))
        {
            throw new ChangeSetException(
                $"Invalid git ref '{changedSinceRef}': must not start with '-'.");
        }

        // ONCE per change-set, not once per git call (#499): three invocations follow and they all
        // launch this same rooted path. A miss is refused here rather than degraded to the bare
        // name — falling back to "git" is precisely the search-order hole this resolution closes.
        //
        // The refusal reuses the launch failure's OUTCOME — a ChangeSetException, which the CLI
        // maps to exit 2 — but no longer its WORDING: this site has no candidate and the launch
        // site has one, so "is git installed and on PATH?" is the actionable question here and a
        // misdirection there. See GitNotStartable. Whether
        // selection-infrastructure failure deserves a code of its own is an open, unfiled question
        // — see RunGit's remarks — and a fix for a binary-resolution defect does not get to answer
        // it in passing.
        var gitExecutable = (gitExecutableLocator ?? LocateGitOnPath)()
            ?? throw new ChangeSetException(GitUnavailable("the change-set computation"));

        // ONCE per change-set, like the resolution above and for the same reason: the three calls
        // below are one logical operation and must not disagree about what git can see (#500).
        var gitEnvironment = ConfinedGitEnvironment();

        var repoRoot = ResolveRepoRoot(
            gitExecutable, workingDirectory, gitEnvironment, processRunner, cancellationToken);

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // (1) Committed changes between the merge-base of <ref> and HEAD, and HEAD.
        // `--end-of-options` forces every following token to be a revision/path, never an
        // option — defence-in-depth so even a dash-leading value that slipped past the guard
        // above cannot be (mis)parsed by git as a flag. (git 2.24+, 2019.)
        var diff = RunGit(
            gitExecutable,
            processRunner,
            workingDirectory,
            gitEnvironment,
            $"diff for ref '{changedSinceRef}'",
            cancellationToken,
            // See the status call below for why every call carries this.
            "--no-optional-locks",
            "diff", "--name-only", "--end-of-options", $"{changedSinceRef}...HEAD");
        AddPaths(changed, repoRoot, diff.StandardOutput, status: false);

        // (2) The dirty working tree (staged, unstaged, untracked). `-c core.quotepath=false`
        // disables git's C-style octal-escaping of non-ASCII bytes, so a path such as
        // "tëst.e2e.yaml" is emitted verbatim (UTF-8) and matches the on-disk file. (The
        // Unquote step below still handles the remaining `\"`/`\\` escapes for paths whose
        // names contain a quote or backslash.)
        //
        // `--no-optional-locks` IS ABOUT WHAT VOUCHFX TAKES, NOT ABOUT WHAT IT SURVIVES, and this
        // is the canonical statement of it — the other two call sites point here.
        //
        // WHAT AN EARLIER DRAFT CLAIMED, AND WHY IT IS RETRACTED. It said `git status` takes
        // `.git/index.lock` to persist its opportunistic index refresh, so a concurrent git in the
        // same working tree made THIS call exit 128 and refused the whole change-set. git has no
        // such failure mode: INFERRED from git's own source, `cmd_status` takes the index lock
        // through `repo_hold_locked_index` WITHOUT `LOCK_DIE_ON_ERROR`, so a lock it cannot get is
        // silently skipped along with the refresh. That mechanism is read rather than observed;
        // what was MEASURED is the black-box behaviour it predicts, by review on this host (git
        // 2.54.0.windows.1) with an `index.lock` planted in a temp repository: plain `status
        // --porcelain` still answered `?? b.txt` at exit 0, with and without a stat-dirty tracked
        // file, and identically with the flag. The measurement stands on its own; the inference
        // only explains it. The retraction is recorded rather than quietly deleted, the same way
        // this branch handles the `0311`->`0611` correction: a rationale that names a failure mode
        // the tool does not have is how a later reader deletes the flag as useless.
        //
        // THE REAL REASON IS THE DIRECTION GIT'S OWN DOCUMENTATION GIVES. `GIT_OPTIONAL_LOCKS` is
        // documented for a caller that "does not want to cause lock contention with other
        // operations on the repository" — the aggressor is US. A `--changed-since` run is a
        // read-only query about which scenarios to execute; it has no business taking a lock in an
        // operator's working tree, however briefly, and the refreshed stat cache it declines to
        // persist is something nothing here reads. Same conclusion, opposite direction, and only
        // this one survives contact with git.
        //
        // SO IT GOES ON ALL THREE CALLS. Under "take no lock we do not need" the flag costs nothing
        // anywhere, and the previous scoping rested on an INFERENCE that `rev-parse
        // --show-toplevel` and a two-commit diff take no index lock — unmeasured, and load-bearing
        // only while the rationale was about surviving a lock rather than declining one. Applying
        // it uniformly deletes the inference instead of labelling it.
        //
        // IT IS A GIT-LEVEL OPTION AND MUST PRECEDE THE SUBCOMMAND. Spelt after `status` it is
        // parsed as a status option and refused. What is MEASURED here is that a real git accepts
        // it in that position and answers the SAME porcelain status with it as without it — see
        // GitChangeSetTests' parity row.
        var status = RunGit(
            gitExecutable,
            processRunner,
            workingDirectory,
            gitEnvironment,
            "working-tree status",
            cancellationToken,
            "--no-optional-locks", "-c", "core.quotepath=false", "status", "--porcelain");
        AddPaths(changed, repoRoot, status.StandardOutput, status: true);

        _changed = changed;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns <see langword="true"/> when the file is itself changed.  Directory-style
    /// containment (a changed entry that is a directory prefix of the scenario) is also
    /// honoured, since git can report renamed/added directories or submodule paths.
    /// </remarks>
    public bool IsChanged(string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(absolutePath);

        // Canonicalise separators to the OS-native form BEFORE Path.GetFullPath. On Linux,
        // Path.GetFullPath treats '\' as a LITERAL filename character (not a separator), so a
        // Windows-style absolute path ("\repo\orders\x") would not be recognised as rooted —
        // GetFullPath would then prepend the cwd and keep the literal backslashes, so the
        // result never matches a '/'-normalised change-set key. ToOsSeparators makes both '\'
        // and '/' behave as separators on every OS so the path round-trips correctly here.
        var normalised = Normalise(Path.GetFullPath(ToOsSeparators(absolutePath)));
        if (_changed.Contains(normalised))
        {
            return true;
        }

        // "in or under" — a changed directory entry covers scenarios beneath it.
        foreach (var entry in _changed)
        {
            if (normalised.StartsWith(entry + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the absolute repository root so repo-relative git output can be made
    /// absolute.  A non-repository directory surfaces as a <see cref="ChangeSetException"/>.
    /// </summary>
    private static string ResolveRepoRoot(
        string gitExecutable,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var result = RunGit(
            gitExecutable,
            processRunner,
            workingDirectory,
            environment,
            "repository-root lookup",
            cancellationToken,
            // See the constructor's status call for why every call carries this.
            "--no-optional-locks",
            "rev-parse", "--show-toplevel");

        var root = result.StandardOutput.Trim();
        if (root.Length == 0)
        {
            // NAMES THE CONCEPT, NOT THE RESOLVED DIRECTORY (#498). This used to interpolate
            // `workingDirectory`, which the CLI resolves from the discovery path — an absolute host
            // path in an author-facing diagnostic, which #357's rule (widened by #375/#473/#488)
            // forbids. Replacing it with nothing would have been a redaction the author cannot act
            // on, so the sentence names the CONDITION that was not met instead: the caller knows
            // which path they passed to `vouchfx run`, and what they do not know is that git
            // answered without a root.
            throw new ChangeSetException(
                "git did not report a repository root, so the discovery path is not inside a git "
                + "working tree. --changed-since needs one.");
        }

        // ToOsSeparators is defensive: git's rev-parse output is already OS-native, but
        // canonicalising separators keeps every Path.GetFullPath call site in this file
        // consistent against the Linux backslash-as-literal-char behaviour (see IsChanged).
        return Normalise(Path.GetFullPath(ToOsSeparators(root)));
    }

    /// <summary>
    /// Runs a git subcommand, mapping a launch failure, a timeout, a failed output capture, or a
    /// non-zero exit to a <see cref="ChangeSetException"/> with the captured stderr for diagnosis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every FAILURE <see cref="IProcessRunner.Run"/> documents is mapped here, and that
    /// is a correctness requirement rather than tidiness.</strong> This is the only catch between
    /// the runner and <c>RunCommand</c>, which handles <see cref="ChangeSetException"/> and nothing
    /// else. An unmapped runner exception therefore does not degrade to a worse message — it
    /// escapes as an unhandled crash, which for the timeout case would convert a hang into a
    /// stack trace and be strictly worse than the hang it replaced. The claim is scoped to the
    /// documented set on purpose: a fake runner in a test can throw anything, and the three catches
    /// below are checked against <see cref="IProcessRunner"/>'s <c>exception</c> tags, not against
    /// every type an arbitrary implementation might invent.
    /// </para>
    /// <para>
    /// <strong>All three map to the SAME exception, so the CLI still exits 2 (usage error).</strong>
    /// That is deliberate and is NOT an assertion that a wedged git is a usage mistake: whether
    /// selection-infrastructure failure deserves an exit code of its own is an open question, and
    /// answering it here — quietly, in a bug fix — would change the CLI's documented exit-code
    /// contract as a side effect of stopping a hang.
    /// </para>
    /// <para>
    /// <strong>THAT QUESTION IS OPEN AND UNFILED, WHICH IS A CHANGE FROM WHAT THIS COMMENT USED TO
    /// SAY.</strong> It attributed the question to issues #480 and #466-B, and neither reaches it.
    /// #466 closed on a different axis — how <c>ParallelSuiteRunner</c>'s slot catch-all CLASSIFIES
    /// an unexpected engine throw — and #480's answer is narrower still: a provider or engine
    /// defect never exits 0, which says nothing about a git that could not be run. So there is no
    /// issue to read for the reasoning, and the exit code stays 2 by inertia rather than by a
    /// decision anybody recorded. This is the canonical statement of it; the other two sites that
    /// used to carry the same citation point here.
    /// </para>
    /// <para>
    /// <strong><see cref="OperationCanceledException"/> is the one documented outcome that must
    /// NOT be mapped, and the three catches are typed narrowly so that it cannot be.</strong> It
    /// is the caller withdrawing rather than a mistake in what they typed, so mapping it would
    /// print a usage message and exit 2 for a Ctrl+C. A <c>catch (Exception)</c>, or a filter loose
    /// enough to admit it, would do exactly that; the narrowness here is load-bearing rather than
    /// stylistic. It propagates untouched to the CLI's existing cancellation path.
    /// </para>
    /// </remarks>
    private static ProcessResult RunGit(
        string gitExecutable,
        IProcessRunner processRunner,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        string operation,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        ProcessResult result;
        try
        {
            result = processRunner.Run(
                gitExecutable, arguments, workingDirectory, environment, cancellationToken);
        }
        catch (ProcessLaunchException ex)
        {
            // NO REASON CLAUSE AT ALL, and the deletion is the fix rather than a simplification.
            // Both candidate sources for one carry the host path. MEASURED on this host (net8.0,
            // Windows) by starting a rooted, non-existent git: Process.Start throws
            // System.ComponentModel.Win32Exception whose own message is
            //
            //     An error occurred trying to start process '<resolved git path>' with working
            //     directory '<discovery root>'. The system cannot find the file specified.
            //
            // SystemProcessRunner wraps THAT as the inner exception of the ProcessLaunchException
            // and quotes the file name again in the outer one, so `ex.Message` and
            // `ex.InnerException?.Message` both name a path — the inner one names two, and since
            // #499 the first of them is where git lives on this host. Host paths do not go into
            // user-facing diagnostics (#375/#473/#488).
            //
            // Scrubbing or sentence-splitting would keep the operating system's reason, at the
            // cost of a rule that has to stay correct against a message .NET composes and
            // localises. GitNotStartable already tells the author the one thing they can act on,
            // so the clause is dropped instead. The exception is still chained, so the full
            // detail remains available to a debugger and to anything that walks InnerException.
            //
            // ITS OWN WORDING, NOT GitUnavailable's, AND THE DISTINCTION IS THE USEFUL PART. This
            // arm is reached only AFTER the locator has returned a candidate, so "is git installed
            // and on PATH?" asks a question already answered yes — and sends the reader looking in
            // the one place that is not the problem. What failed is the START of a candidate that
            // was found, and IsExecutableFile has already excluded some of what a reader would
            // guess: File.Exists is false for a directory on either platform, and on POSIX it
            // resolves the symlink, so a broken one never reaches here — after which access(X_OK)
            // has answered "this caller may run it" as well. On WINDOWS neither of those last two
            // holds: File.Exists is TRUE for a symlink whose target is missing (measured, net8.0
            // on this host), and the Windows arm asks no permission question at all. So the
            // reachable causes are a file that is no loadable image for this machine (ENOEXEC, the
            // wrong architecture, something merely NAMED git.exe), a broken symlink or an
            // execute-denying ACL on Windows, the mode-bit fallback on a runtime with no libc.so
            // accepting a file somebody ELSE may execute, and a candidate replaced between the
            // resolution and the launch.
            throw new ChangeSetException(GitNotStartable(operation), ex);
        }
        catch (ProcessTimeoutException ex)
        {
            throw new ChangeSetException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"git {operation} did not complete within the {ex.Budget.TotalSeconds:0.###}s process budget, so its direct child was killed. Any process that child had already left behind is beyond the runner's reach and may still be running. A change-set cannot be computed from a partial capture, so selection is refused rather than narrowed."),
                ex);
        }
        catch (ProcessCaptureException ex)
        {
            // The inner exception rather than ex.Message: the runner's own message already names
            // the executable, and repeating it here would read as two nested failures.
            //
            // SUBSTITUTED, because that inner message is the BCL's and this one IS printed — the
            // same #498 class as the launch-failure splice, reached on a different path. A pipe
            // read faults with an IOException whose text the engine did not write and cannot
            // constrain, so it goes through the same scrub as git's own output below.
            throw new ChangeSetException(
                $"Could not read the output of git {operation}: {SubstituteAbsolutePaths(ex.InnerException?.Message ?? ex.Message)}. A change-set cannot be computed from a partial capture, so selection is refused rather than narrowed.",
                ex);
        }

        if (result.ExitCode != 0)
        {
            var detail = SubstituteAbsolutePaths(result.StandardError.Trim());
            if (detail.Length == 0)
            {
                detail = SubstituteAbsolutePaths(result.StandardOutput.Trim());
            }

            throw new ChangeSetException(
                $"git {operation} failed (exit {result.ExitCode}): "
                + (detail.Length > 0 ? detail : "no diagnostic output."));
        }

        return result;
    }

    /// <summary>
    /// The wording for "no git was found at all" — the <c>PATH</c>-resolution refusal, and only
    /// that.
    /// </summary>
    /// <param name="operation">What was being attempted, in the caller's own vocabulary.</param>
    /// <returns>The message, deliberately naming no path — see <see cref="RunGit"/>.</returns>
    /// <remarks>
    /// It used to be shared with the launch failure, on the reasoning that two wordings for "git
    /// could not be run" would drift apart. They are not the same failure: this one is raised
    /// BEFORE anything is launched, because the search over <c>PATH</c> produced no candidate, so
    /// its question is the actionable one. <see cref="GitNotStartable"/> is raised only AFTER a
    /// candidate has been produced, where the same question misdirects.
    /// </remarks>
    private static string GitUnavailable(string operation) =>
        $"Could not run git for {operation}. Is git installed and on PATH?";

    /// <summary>
    /// The wording for "a git was found and the operating system would not start it".
    /// </summary>
    /// <param name="operation">What was being attempted, in the caller's own vocabulary.</param>
    /// <returns>The message, deliberately naming no path — see <see cref="RunGit"/>.</returns>
    /// <remarks>
    /// PATH-FREE, exactly as <see cref="GitUnavailable"/> is (#498): the candidate is the one thing
    /// a reader would want named and is precisely the host path that may not be disclosed, so the
    /// message describes WHICH candidate it means rather than spelling it. That is #357's rule in
    /// its usual shape: name the declared thing and the concept it resolves against, never the
    /// resolution. "The first git on <c>PATH</c>" is the concept because
    /// <see cref="LocateGitOnPath"/> returns the first fully qualified match and nothing else; a
    /// test may inject another locator, and nothing user-facing goes through one.
    /// </remarks>
    private static string GitNotStartable(string operation) =>
        $"Could not start git for {operation}. A git executable was found on PATH, but the "
        + "operating system refused to start it. Is the first git on PATH a valid executable this "
        + "user may run?";

    /// <summary>
    /// What replaces an absolute host path in relayed text.
    /// </summary>
    /// <remarks>
    /// Its own characters are token separators in the assertion that polices this rule
    /// (<c>HostPathDisclosure</c>), so the placeholder cannot itself be read as a path reference.
    /// </remarks>
    private const string PathPlaceholder = "<path>";

    /// <summary>
    /// The separators that bound a token, matching the shared disclosure assertion's set exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><c>=</c> IS A SEPARATOR, AND WITHOUT IT A ROOTED PATH BEHIND A PREFIX ESCAPED
    /// WHOLE.</strong> <c>cwd=/home/runner/work/x</c> is ONE token beginning <c>c</c>, so
    /// <see cref="Path.IsPathRooted(string)"/> is <see langword="false"/> for it and the path was
    /// relayed verbatim — the whole path, not a residue of one. The shape is not hypothetical: the
    /// stderr relayed here carries whatever a repository-chosen helper wrote to it
    /// (<c>core.fsmonitor</c>, a <c>filter.*.clean</c> command, a <c>.git/hooks/*</c> script), and
    /// a helper that echoes its own environment prints exactly this <c>KEY=/path</c> form.
    /// </para>
    /// <para>
    /// <strong><c>`</c> IS ONE, AND IT CLOSES A MEASURED WHOLE-PATH LEAK.</strong> GNU tooling of
    /// the backtick-apostrophe quoting era — older <c>make</c>, an autoconf-generated
    /// <c>configure</c>, older coreutils — writes <c>`/abs/path'</c>, and a <c>.git/hooks/*</c>
    /// script that shells out to one puts that on the stderr relayed here. That quoting style is
    /// inferred from those tools rather than measured on this host; what IS measured is what this
    /// scan did with it. MEASURED before the addition: <c>cannot open `/etc/gitconfig'</c> came
    /// back verbatim — the whole path — because the token began <c>`</c> and was therefore not
    /// rooted. It costs the tail of a path whose own name carries a backtick: <c>/opt/a`b/c</c> is
    /// <c>&lt;path&gt;`b/c</c>, measured — which is the same trade <c>=</c> makes, paid here for a
    /// shape that occurs in real output.
    /// </para>
    /// <para>
    /// <strong><c>:</c> IS DELIBERATELY NOT ONE.</strong> It would split <c>C:\Users\x</c> at the
    /// drive colon into <c>C</c> — one character, below the two-character floor, so skipped — and
    /// <c>\Users\x</c>. MEASURED on Windows: every expectation here becomes <c>C:&lt;path&gt;</c>.
    /// On POSIX <c>\Users\x</c> is not rooted at all and the whole path survives, which is worse
    /// than the residue the exclusion concedes (inferred from
    /// <see cref="Path.IsPathRooted(string)"/>'s documented Unix behaviour, not measured — no lane
    /// here is POSIX).
    /// </para>
    /// <para>
    /// <strong>THE RESIDUE IS A CLASS, AND IT IS NOW ENUMERATED RATHER THAN SAMPLED.</strong> Any
    /// character this set does not contain glues a rooted path to a prefix into ONE token, which
    /// <see cref="Path.IsPathRooted(string)"/> reads as relative. <c>error:/home/x</c> is the
    /// instance that matters, because <c>:</c> is git's own prefix punctuation — but
    /// <c>user@/home/x</c> and <c>ref#/home/x</c> survive identically, and naming one character as
    /// though the list were complete is how this comment read while four review rounds each found
    /// a different one. The answer for EVERY non-alphanumeric ASCII character is recorded by
    /// <c>GitChangeSetTests.SubstituteAbsolutePaths_PrefixGlue_IsSubstitutedOrDocumentedResidue</c>
    /// — that row asserts the exact output per character and carries the reason for each of the
    /// nineteen residues. In short: <c>/</c> and <c>\</c> ARE the path separators; <c>:</c> is the
    /// paragraph above; <c>- . _ ~ + $ % # @ ^ { } !</c> occur inside real paths, where making one
    /// a separator leaves the tail standing (measured); <c>| ? *</c> are illegal in a Windows
    /// filename but legal in a POSIX one and close no shape anyone has named. Closing the class
    /// outright needs a rule that is not a separator character at all — scan for a path separator
    /// and ask whether the run from there is rooted behind a non-alphanumeric predecessor — which
    /// cannot live in the three shared <c>char[]</c> arrays this set is held to. Not taken.
    /// </para>
    /// </remarks>
    private static readonly char[] TokenSeparators =
        { ' ', '\t', '\r', '\n', '"', '\'', '<', '>', '&', ';', ',', '(', ')', '[', ']', '=', '`' };

    /// <summary>
    /// The separators <see cref="IsOnePathWholly"/> alone splits on — the whitespace members of
    /// <see cref="TokenSeparators"/>, and deliberately nothing else.
    /// </summary>
    /// <remarks>
    /// Not a second copy of the shared rule set: the parity row polices by EQUALITY the three
    /// arrays the scan and the disclosure gate share, and this one belongs to neither. It is held
    /// to that row as a SUBSET of <see cref="TokenSeparators"/> and nothing more — a whitespace
    /// character added there and not here would make the one-path test stop splitting where the
    /// scan does, which leaks nothing and decides wrongly in silence.
    /// <see cref="IsOnePathWholly"/> carries the measurement that says why it is narrower.
    /// </remarks>
    private static readonly char[] WhitespaceSeparators = { ' ', '\t', '\r', '\n' };

    private static readonly char[] PathSeparators = { '\\', '/' };

    /// <summary>
    /// Trimmed from the END only — trimming <c>.</c> from the front would turn a relative
    /// <c>./x</c> into the rooted-looking <c>/x</c> and substitute a path that is not one.
    /// </summary>
    private static readonly char[] TrailingPunctuation = { '.', ':' };

    /// <summary>
    /// Replaces every absolute host path in <paramref name="text"/> with
    /// <see cref="PathPlaceholder"/>, leaving everything else verbatim.
    /// </summary>
    /// <param name="text">Text the engine did not write: git's stderr, or a BCL message.</param>
    /// <returns>The same text with its rooted path tokens substituted.</returns>
    /// <remarks>
    /// <para>
    /// <strong>SUBSTITUTION, and choosing it over the alternatives is the point (#498).</strong>
    /// git's stderr is the only account of WHY a subcommand failed — the engine knows the exit code
    /// and nothing else — and it routinely names host paths: <c>fatal: detected dubious ownership
    /// in repository at '...'</c> is entirely a path, while <c>fatal: ambiguous argument 'nope':
    /// unknown revision</c> contains none and is the most useful message this file can print.
    /// DROPPING the relay takes the second with the first. TRUNCATING it addresses length, which is
    /// not the defect: the dubious-ownership line is short and wholly a disclosure. DESCRIBING it —
    /// replacing git's words with the engine's — means classifying git's output by substring, which
    /// is a rule that has to stay correct against text this file does not own. Substituting the
    /// paths and keeping the sentence is the only one of the four that removes the disclosure
    /// WITHOUT removing the diagnosis, and it is the treatment #375 chose for the same problem
    /// (<c>SecurityPathDisclosureLedger</c>, which is not used here because it is seeded from the
    /// security material a topology produced, and selection runs before any topology exists — so at
    /// this point there is nothing to seed it with).
    /// </para>
    /// <para>
    /// <strong>The per-token PREDICATE is the shared assertion's, deliberately.</strong> A token,
    /// trailing <c>.</c>/<c>:</c> trimmed, is substituted when it is at least two characters,
    /// contains a path separator, and is <see cref="Path.IsPathRooted(string)"/>. Those are exactly
    /// the conditions <c>Vouchfx.TestSupport.HostPathDisclosure</c>'s rooted-token scan refuses on,
    /// in the same order — it spells them as a negated early exit rather than a conjunction, so the
    /// two read differently and decide identically. The three rule arrays behind them are held
    /// equal STRUCTURALLY rather than by this sentence, by
    /// <c>GitChangeSetTests.SubstitutionTokenRules_AreTheSharedDisclosureGates</c>; a request to
    /// "edit both" is the arrangement under which the gate's own two former copies diverged.
    /// </para>
    /// <para>
    /// <strong>THE GATE IS STRICTER OVERALL, AND THAT ASYMMETRY IS DELIBERATE.</strong> This used to
    /// claim the two "cannot drift into disagreeing about what a disclosure is", which is not what
    /// is true. <c>AssertNoAbsoluteHostPath</c> refuses on THREE checks: (a) the host directory as a
    /// raw substring, deliberately catching it even where no token boundary exists, (b) its
    /// JSON-escaped form, and (c) the rooted-token scan. Only (c) is implemented here, because (a)
    /// and (b) need a host directory known in advance — which a gate has and a relay does not. The
    /// gate therefore refuses strings this method would pass, which fails SAFE; what the old
    /// sentence would have justified is deleting (a) and (b) from a future gate as redundant, and
    /// they are not.
    /// </para>
    /// <para>
    /// <strong>The TOKENISATION is this method's own, and is stricter than the gate's in one
    /// case.</strong> A span opened by <c>'</c> or <c>"</c> is taken whole, up to the matching close
    /// on the same line, WHEN the span is one path by <see cref="IsOnePathWholly"/> — so a quoted
    /// path CONTAINING SPACES is one token here and several in the gate. That is the common Windows
    /// shape rather than an exotic one (below), and the gate catching the pieces anyway — through
    /// (a), or through whichever piece is still rooted — is why the divergence costs nothing. A
    /// span that is NOT one path is re-scanned token by token rather than emitted, which is the
    /// only reason this tokenisation is never WEAKER than the plain scan.
    /// </para>
    /// <para>
    /// <strong>WHAT IT STILL DOES NOT CATCH, stated rather than implied.</strong> An UNQUOTED path
    /// containing a space is split: <c>C:\Program Files\Git\x</c> loses <c>C:\Program</c> to the
    /// placeholder and leaves <c>Files\Git\x</c> standing, because the remainder is not rooted. git
    /// quotes the paths it names, so this is the narrower residue it looks like — but it is a
    /// residue, and a relayed message this file does not own may not quote. A QUOTED path with a
    /// space leaves the same residue whenever its span is prose rather than one path (<c>'cannot
    /// run hook pre-commit in /home/john smith/hooks'</c>), since the fallback scan splits on the
    /// space exactly as the unquoted case does. It is also platform-relative:
    /// <see cref="Path.IsPathRooted(string)"/> reads a drive letter only on Windows, so a
    /// Windows-shaped path would survive on a POSIX host. That costs nothing in practice, since the
    /// text being scrubbed was produced by a child of THIS process on THIS host.
    /// </para>
    /// <para>
    /// <strong>WHERE IT OVER-REACHES LESS THAN IT DID — narrowed, not removed.</strong> A span that
    /// BEGINS rooted and continues in prose is rooted as a whole, so the first quote-aware draft
    /// replaced <c>'/etc/gitconfig is unreadable, and /tmp/x too'</c> with a bare
    /// <c>&lt;path&gt;</c> — wider than any residue, since it deleted a sentence the operator
    /// needed. The second rooted token is what refuses the whole-span treatment there; see
    /// <see cref="IsOnePathWholly"/>.
    /// </para>
    /// <para>
    /// The residue that treatment leaves, stated rather than implied: a span that begins rooted and
    /// carries prose but NO second path is still collapsed whole, so
    /// <c>at '/home/john smith/x is gone'</c> becomes <c>at '&lt;path&gt;'</c> and the words
    /// <c>is gone</c> go with it. The second rooted token is the only signal here, and that span
    /// holds none. It is the narrow case — git puts its prose outside the quotes it wraps a path in
    /// — but it is a case, and a relayed message this file does not own may quote differently.
    /// </para>
    /// <para>
    /// <strong>WHY THE QUOTED SPAN IS ONE TOKEN — the default Windows shape, not an edge
    /// case.</strong>
    /// git quotes with <c>'</c>, which is itself a <see cref="TokenSeparators"/> member, so under a
    /// plain token scan <c>fatal: detected dubious ownership in repository at
    /// 'C:/Users/John Smith/src/repo'</c> substituted <c>C:/Users/John</c> and left
    /// <c>Smith/src/repo</c> standing. A user profile carrying a space is the Windows default for
    /// anyone whose account name is two words, and <c>C:\Program Files\Git\…</c> is git's own
    /// install location; the residue was the common case rather than the rare one.
    /// </para>
    /// <para>
    /// A quote opens a span only at the start of the text or after a
    /// <see cref="TokenSeparators"/> member, which is what keeps an apostrophe inside a word
    /// (<c>couldn't</c>) from pairing with the quote that opens the path later on the same line. An
    /// unmatched quote, and one mid-word, are emitted as ordinary separators — never allowed to
    /// swallow the remainder — and the search for a close is bounded to the line, so an apostrophe
    /// cannot reach across a multi-line stderr. Mispairing degrades to the pre-quote behaviour
    /// (the head substituted, the tail standing); it never widens what is substituted.
    /// </para>
    /// <para>
    /// It stays LINEAR. Each close-quote search scans forward only and stops at the line end, and a
    /// search that finds nothing proves the rest of that line holds no further quote of the same
    /// character — so at most two failed scans per line, each bounded by that line. The re-scan of
    /// a span that is not one path adds a bounded constant rather than a recursion to reason about:
    /// a span holds no further instance of its own opening quote, so it can nest at most one level
    /// deeper before no quote is left to open a span at all (<see cref="AppendQuotedSpan"/>).
    /// </para>
    /// <para>
    /// One over-reach is accepted knowingly: on Windows a ref spelt <c>/weird</c> is rooted, so a
    /// message quoting it back is substituted. Refs of that shape are pathological, and the
    /// alternative — a second rule distinguishing refs from paths — is the classifying-by-substring
    /// this method rejects above.
    /// </para>
    /// </remarks>
    internal static string SubstituteAbsolutePaths(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = new StringBuilder(text.Length);
        AppendSubstituted(builder, text);
        return builder.ToString();
    }

    /// <summary>
    /// Scans one stretch of text, appending each token substituted or verbatim.
    /// </summary>
    /// <param name="builder">The output under construction.</param>
    /// <param name="text">The stretch to scan — the whole relay, or one quoted span of it.</param>
    /// <remarks>
    /// Separate from <see cref="SubstituteAbsolutePaths"/> because a quoted span that is NOT one
    /// path is re-scanned by this same method; see <see cref="AppendQuotedSpan"/> for why, and for
    /// why the nesting that implies is bounded at two.
    /// </remarks>
    private static void AppendSubstituted(StringBuilder builder, string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (Array.IndexOf(TokenSeparators, text[index]) >= 0)
            {
                // A quote is a separator that can also OPEN a span — see the remarks for why a
                // quoted path with spaces has to be one token. `index` is the opener's own
                // position, so `index == 0 || previous is a separator` is the "not mid-word" test.
                var close = text[index] is '\'' or '"'
                    && (index == 0 || Array.IndexOf(TokenSeparators, text[index - 1]) >= 0)
                    ? MatchingQuoteOnThisLine(text, index)
                    : -1;

                builder.Append(text[index]);
                index++;

                if (close < 0)
                {
                    continue;
                }

                AppendQuotedSpan(builder, text[index..close]);
                builder.Append(text[close]);
                index = close + 1;
                continue;
            }

            var end = index;
            while (end < text.Length && Array.IndexOf(TokenSeparators, text[end]) < 0)
            {
                end++;
            }

            AppendToken(builder, text[index..end]);
            index = end;
        }
    }

    /// <summary>
    /// Appends the text between a pair of quotes: whole when it is ONE path, re-scanned when it
    /// is not.
    /// </summary>
    /// <param name="builder">The output under construction.</param>
    /// <param name="span">The span, quotes excluded.</param>
    /// <remarks>
    /// <para>
    /// <strong>THE FALLBACK IS THE POINT.</strong> Taking every span whole made a quoted sentence
    /// that merely CONTAINS a path — <c>error: cannot run hook 'pre-commit in /home/john
    /// smith/repo/hooks'</c> — emit the path verbatim, because the span as a whole is not rooted
    /// and so failed the predicate with no second chance. That is strictly worse than the
    /// per-token scan the quote-awareness replaced, which at least substituted the rooted head.
    /// Re-scanning the span is what restores it.
    /// </para>
    /// <para>
    /// <strong>AND THE WHOLE-SPAN CASE IS NARROWER THAN "STARTS ROOTED".</strong> A span that
    /// BEGINS with a path and continues in prose — <c>'/etc/gitconfig is unreadable, and /tmp/x
    /// too'</c> — IS <see cref="Path.IsPathRooted(string)"/>, so taking it whole replaced the
    /// sentence with <c>&lt;path&gt;</c> and swallowed the second path's existence along with the
    /// words. A SECOND rooted WHITESPACE-separated token inside the span is the signal that the
    /// span is a sentence naming paths rather than one path containing spaces: a sentence naming
    /// two paths puts whitespace between them, while a path that continues after a space almost
    /// never resumes with a path separator. "Almost" is the honest word — a directory whose name
    /// ends in a space would produce one — and <see cref="IsOnePathWholly"/> states why the
    /// separator set for that test is whitespace ALONE rather than the scan's own.
    /// </para>
    /// <para>
    /// THE RECURSION IS BOUNDED AT TWO, and by the tokenisation rather than by a counter.
    /// <see cref="MatchingQuoteOnThisLine"/> returns the FIRST close, so a span opened by
    /// <c>'</c> contains no further <c>'</c> — only a <c>"</c> can open inside it, and that
    /// nested span then contains neither quote character. So the third scan opens no span at all,
    /// and each scan runs over a strictly shorter string than its caller's.
    /// </para>
    /// </remarks>
    private static void AppendQuotedSpan(StringBuilder builder, string span)
    {
        if (IsOnePathWholly(span))
        {
            AppendToken(builder, span);
            return;
        }

        AppendSubstituted(builder, span);
    }

    /// <summary>
    /// Whether a quoted span is ONE absolute host path rather than prose that names one.
    /// </summary>
    /// <param name="span">The span, quotes excluded.</param>
    /// <returns><see langword="true"/> when the span may be substituted whole.</returns>
    /// <remarks>
    /// <para>
    /// Two conditions, and the second is what keeps the whole-span treatment off a sentence: the
    /// span is itself an absolute host path, AND no WHITESPACE-separated token after its first is
    /// one — the separator set is this method's own, and the paragraph below is why. The first
    /// condition alone accepts <c>/etc/gitconfig is unreadable, and /tmp/x too</c>; the second
    /// rejects it. <c>C:\Users\John Smith\src\repo</c> — the shape the quote-awareness exists for
    /// — splits into <c>C:\Users\John</c> and <c>Smith\src\repo</c>, only the first of which is
    /// rooted, so it is unaffected.
    /// </para>
    /// <para>
    /// <strong>WHITESPACE ALONE, and the scan's own set was MEASURED wrong here.</strong> Splitting
    /// on <see cref="TokenSeparators"/> made every path carrying a non-whitespace separator look
    /// like a sentence, because the fragment after that separator still begins with a path
    /// separator and is therefore rooted. Measured on this host:
    /// <c>'C:\Program Files (x86)\Git\bin\sh.exe'</c> split into <c>C:\Program</c>, <c>Files</c>,
    /// <c>x86</c> and <c>\Git\bin\sh.exe</c> — the last one rooted — so the span was refused and
    /// re-scanned into <c>'&lt;path&gt; Files (x86)&lt;path&gt;'</c>, and
    /// <c>'/opt/git (stable)/bin/sh'</c> into <c>'&lt;path&gt; (stable)&lt;path&gt;'</c>. No path
    /// text escapes either way, but <c>C:\Program Files (x86)\Git</c> is where 32-bit
    /// Git-for-Windows installs, so the shape is a default rather than an oddity. On whitespace
    /// the first splits into <c>C:\Program</c>, <c>Files</c> and <c>(x86)\Git\bin\sh.exe</c>, none
    /// of the later ones rooted, and the span is taken whole again.
    /// </para>
    /// <para>
    /// Nothing the second condition exists for is given up: a sentence naming two paths separates
    /// them with whitespace, so <c>/etc/gitconfig is unreadable, and /tmp/x too</c> still splits
    /// <c>/tmp/x</c> out and is still refused. What the narrower set concedes is a span whose two
    /// paths are separated by punctuation ALONE — <c>/etc/x(/tmp/y)</c> — which is now taken
    /// whole; both halves are paths there, so the one placeholder deletes no prose.
    /// </para>
    /// <para>
    /// This is the INNER tokenisation only. <see cref="AppendSubstituted"/> keeps the shared
    /// <see cref="TokenSeparators"/> set, which is what
    /// <c>GitChangeSetTests.SubstitutionTokenRules_AreTheSharedDisclosureGates</c> holds against
    /// the disclosure gate's.
    /// </para>
    /// </remarks>
    private static bool IsOnePathWholly(string span)
    {
        if (!IsAbsoluteHostPath(span.TrimEnd(TrailingPunctuation)))
        {
            return false;
        }

        var first = true;
        foreach (var token in span.Split(
            WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!first && IsAbsoluteHostPath(token.TrimEnd(TrailingPunctuation)))
            {
                return false;
            }

            first = false;
        }

        return true;
    }

    /// <summary>
    /// Appends one token, substituted when it is an absolute host path.
    /// </summary>
    /// <param name="builder">The output under construction.</param>
    /// <param name="token">The token, separators already stripped by the caller.</param>
    /// <remarks>
    /// The trimmed tail is put back so a substituted token does not swallow the sentence's
    /// punctuation along with the path.
    /// </remarks>
    private static void AppendToken(StringBuilder builder, string token)
    {
        var candidate = token.TrimEnd(TrailingPunctuation);

        builder.Append(IsAbsoluteHostPath(candidate)
            ? PathPlaceholder + token[candidate.Length..]
            : token);
    }

    /// <summary>
    /// Finds the closing quote that matches the one at <paramref name="opening"/>, searching no
    /// further than the end of that line.
    /// </summary>
    /// <param name="text">The text being scanned.</param>
    /// <param name="opening">The index of the opening quote character.</param>
    /// <returns>The index of the close, or <c>-1</c> when the line holds none.</returns>
    /// <remarks>
    /// Bounded to the line so an apostrophe on one line of a multi-line stderr cannot pair with the
    /// quote that opens a path on the next one and hide it from the substitution.
    /// </remarks>
    private static int MatchingQuoteOnThisLine(string text, int opening)
    {
        var quote = text[opening];
        for (var i = opening + 1; i < text.Length; i++)
        {
            if (text[i] == quote)
            {
                return i;
            }

            if (text[i] is '\r' or '\n')
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>
    /// The predicate behind <see cref="SubstituteAbsolutePaths"/>, kept as one named test.
    /// </summary>
    private static bool IsAbsoluteHostPath(string candidate) =>
        candidate.Length >= 2
        && candidate.IndexOfAny(PathSeparators) >= 0
        && Path.IsPathRooted(candidate);

    /// <summary>
    /// The environment variable names a local git invocation is given, beyond the <c>GIT_</c>
    /// pass-through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>WHAT WAS MEASURED, AND IT IS LESS THAN THE LIST.</strong> Three mutation drills were
    /// run on this host (Windows 11 build 26200.9168, net8.0, git 2.54.0.windows.1) against
    /// <c>RealGit_ThreeSubcommands_SucceedUnderTheConfinedEnvironment</c>, which executes the three
    /// real subcommands. Deleting <c>SystemRoot</c>: still exit 0. Deleting <c>SystemRoot</c> AND
    /// <c>windir</c>: still exit 0. Deleting <c>PATH</c>: still exit 0. So NONE of the three is
    /// required by <c>rev-parse --show-toplevel</c>, <c>diff --name-only</c> or
    /// <c>status --porcelain</c> on this host — <c>git.exe</c> serves all three internally and
    /// looks nothing up. They are kept as DEFENCE, not as measured necessity, and this paragraph
    /// says so rather than asserting a need the drill contradicts.
    /// </para>
    /// <para>
    /// The defensive case for each: <c>PATH</c> because git dispatches non-builtin subcommands and
    /// its Windows shell helpers through it, and because a future call here need not be one of
    /// today's three. <c>HOME</c> (POSIX) and <c>USERPROFILE</c>/<c>HOMEDRIVE</c>/<c>HOMEPATH</c>
    /// (Windows) because that is where git looks for the user's configuration, and a git that
    /// cannot find it behaves DIFFERENTLY rather than failing loudly — which is the failure mode a
    /// drill cannot see. <c>SystemRoot</c>/<c>windir</c> because Win32 APIs outside the paths these
    /// three subcommands take are documented to need them.
    /// <c>TMP</c>/<c>TEMP</c>/<c>TMPDIR</c> because git writes temporary objects.
    /// </para>
    /// <para>
    /// <strong><c>XDG_CONFIG_HOME</c> AND <c>ProgramData</c> ARE THE REST OF "WHICH CONFIG GIT
    /// READS", and the <c>HOME</c> rationale above stops one step short of them.</strong> git reads
    /// <c>$XDG_CONFIG_HOME/git/config</c> ahead of <c>~/.gitconfig</c>, and on Windows the system
    /// config lives under <c>%ProgramData%\Git\config</c>. Dropping either changes
    /// <c>status.showUntrackedFiles</c>, <c>core.excludesFile</c> and their neighbours — and since
    /// <c>--changed-since</c> decides WHICH SCENARIOS RUN, that is a silently different test
    /// selection rather than a visible failure. It is the same failure mode <c>HOME</c> is forwarded
    /// for, reached through the two config paths <c>HOME</c> does not cover.
    /// </para>
    /// <para>
    /// <strong><c>LD_LIBRARY_PATH</c> fails LOUDLY instead, and is forwarded anyway.</strong> A git
    /// installed under a custom prefix — Nix, conda, a hand-built one — does not load without it, so
    /// dropping it turns a working host into exit 2. Loud is better than silent, but it is still a
    /// regression this confinement would have introduced and nothing else here would have caught.
    /// </para>
    /// <para>
    /// None of the three is a plausible secret carrier, and all are operator-controlled in exactly
    /// the way <c>PATH</c> — already forwarded, and already a code-selection variable — is.
    /// </para>
    /// <para>
    /// <c>TMPDIR</c> is the POSIX spelling of the same thing as <c>TMP</c>/<c>TEMP</c> and is here
    /// for symmetry rather than by a separate decision: without it the POSIX lane would get no
    /// temporary directory at all while the Windows lane got one, which is an inconsistency rather
    /// than a policy. It is the one entry NOT in the set the fix was specified with, and it is
    /// called out so the addition is visible rather than smuggled.
    /// </para>
    /// <para>
    /// <strong>NOT forwarded, and each absence is deliberate:</strong> the proxy variables and
    /// <c>SSH_AUTH_SOCK</c>, because <c>--changed-since</c> makes no network call — it runs
    /// <c>rev-parse --show-toplevel</c>, <c>diff --name-only</c> and <c>status --porcelain</c>, all
    /// local. <c>LANG</c>/<c>LC_*</c>, so git answers in the C locale — which makes the stderr
    /// relayed by <see cref="SubstituteAbsolutePaths"/> deterministic rather than host-dependent.
    /// (Inferred from git's documented gettext behaviour; not measured here.)
    /// </para>
    /// <para>
    /// Everything not named above is dropped, which is the default this exists to impose. That is a
    /// statement about the RULE, not a claim that the exceptions have all been thought of: this
    /// paragraph used to read "and everything else, which is the whole point", which asserted
    /// completeness over a set nobody had enumerated — and the three entries added above were each
    /// found after it. Adding a variable here is a decision to be argued at the site, not a gap in
    /// a closed list.
    /// </para>
    /// </remarks>
    private static readonly string[] GitEnvironmentAllowList =
    {
        "PATH",
        "LD_LIBRARY_PATH",
        "HOME",
        "XDG_CONFIG_HOME",
        "USERPROFILE",
        "HOMEDRIVE",
        "HOMEPATH",
        "ProgramData",
        "SystemRoot",
        "windir",
        "TMP",
        "TEMP",
        "TMPDIR",
    };

    /// <summary>The prefix whose variables are forwarded wholesale.</summary>
    private const string GitVariablePrefix = "GIT_";

    /// <summary>
    /// Builds the environment block this process's git children are given.
    /// </summary>
    /// <returns>The allow-listed variables plus every <c>GIT_</c>-prefixed one set.</returns>
    /// <remarks>
    /// <para>
    /// <strong>THE EXPOSURE THIS CLOSES (#500).</strong> Until this existed, every git child
    /// inherited vouchfx's environment in full — including whatever <c>${secret:env/NAME}</c>
    /// reads, since <c>env</c> is one of the two MVP secret sources (blueprint §17). git executes
    /// repository-influenced code through <c>core.fsmonitor</c>, <c>.git/hooks/*</c> and
    /// <c>credential.helper</c> in its <c>!shell</c> form, all of which live in local <c>.git</c>
    /// config rather than in cloned content. So a secret loaded for the SUITE was readable by a
    /// helper the REPOSITORY UNDER TEST chose.
    /// </para>
    /// <para>
    /// <strong>What this removes is INHERITANCE, not ACCESS.</strong> Repository-chosen code runs
    /// at the same uid as vouchfx, so it can read the parent's environment directly —
    /// <c>/proc/&lt;ppid&gt;/environ</c> on Linux,
    /// <c>OpenProcess(PROCESS_VM_READ)</c> and the PEB on Windows. Against code that is
    /// already executing this is a speed bump rather than a boundary; what it buys is that the
    /// secret is no longer handed over by default, to every helper, without anyone choosing to.
    /// </para>
    /// <para>
    /// <strong>THE RESIDUAL IS REAL AND IS NOT CLOSED BY THIS.</strong> The <c>GIT_</c>
    /// pass-through is a hole with a shape: a secret stored in a <c>GIT_</c>-named variable still
    /// reaches the child, and <c>GIT_SSH_COMMAND</c> and <c>GIT_EXTERNAL_DIFF</c> remain
    /// code-execution vectors — git runs their values. What makes the trade narrow enough to take
    /// is WHO can set them: the repository under test cannot, only the operator of the process
    /// running vouchfx can, so the pass-through cannot be reached by the attacker the paragraph
    /// above describes. It is a deliberate, approved trade, and stating it as closed would be
    /// false. Forwarding the prefix at all is what keeps an operator's own <c>GIT_DIR</c>,
    /// <c>GIT_CONFIG_GLOBAL</c> or <c>GIT_SSH_COMMAND</c> working, which is behaviour that existed
    /// before this confinement and that removing would be a silent regression rather than a
    /// hardening.
    /// </para>
    /// </remarks>
    internal static IReadOnlyDictionary<string, string> ConfinedGitEnvironment() =>
        ConfineEnvironment(CurrentEnvironment(), OperatingSystem.IsWindows());

    /// <summary>
    /// Filters <paramref name="source"/> down to the confined set.
    /// </summary>
    /// <param name="source">The environment to filter, as name/value pairs.</param>
    /// <param name="windows">Whether variable names compare case-insensitively.</param>
    /// <returns>The confined block.</returns>
    /// <remarks>
    /// <para>
    /// Split out of <see cref="ConfinedGitEnvironment"/>, and takes the platform as an argument for
    /// the same reason <see cref="CandidateFileName(string, bool)"/> does: the Windows rule is then
    /// assertable off Windows, and no blocking CI lane is Windows (#366).
    /// </para>
    /// <para>
    /// <strong>The case rule is not cosmetic.</strong> Windows resolves variable names
    /// case-insensitively, so an operator's <c>git_ssh_command</c> is the same variable to git as
    /// <c>GIT_SSH_COMMAND</c>; a case-sensitive filter there would drop it and change behaviour
    /// this confinement is not meant to change. POSIX resolves them case-sensitively, where
    /// <c>path</c> and <c>PATH</c> are two variables and conflating them would ADD one the caller
    /// never asked for.
    /// </para>
    /// <para>
    /// The indexer rather than <c>Add</c>: under the case-insensitive comparer a source holding
    /// both <c>Path</c> and <c>PATH</c> — which a hand-built map in a test can — must resolve to
    /// one entry rather than throw.
    /// </para>
    /// </remarks>
    internal static IReadOnlyDictionary<string, string> ConfineEnvironment(
        IEnumerable<KeyValuePair<string, string>> source,
        bool windows)
    {
        ArgumentNullException.ThrowIfNull(source);

        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var comparer = StringComparer.FromComparison(comparison);
        var allowed = new HashSet<string>(GitEnvironmentAllowList, comparer);
        var confined = new Dictionary<string, string>(comparer);

        foreach (var variable in source)
        {
            if (allowed.Contains(variable.Key)
                || variable.Key.StartsWith(GitVariablePrefix, comparison))
            {
                confined[variable.Key] = variable.Value;
            }
        }

        return confined;
    }

    /// <summary>
    /// This process's environment as name/value pairs.
    /// </summary>
    /// <remarks>
    /// Entries whose name or value is not a string are skipped. The non-generic
    /// <see cref="System.Collections.IDictionary"/> that
    /// <see cref="Environment.GetEnvironmentVariables()"/> returns types both as
    /// <see cref="object"/>, and a block the runtime cannot express as strings is not one this
    /// file can forward meaningfully.
    /// </remarks>
    private static IEnumerable<KeyValuePair<string, string>> CurrentEnvironment()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                yield return new KeyValuePair<string, string>(name, value);
            }
        }
    }

    /// <summary>
    /// Locates the git executable on this process's <c>PATH</c>, returning a fully qualified path
    /// or <see langword="null"/> when no entry holds one.
    /// </summary>
    /// <returns>A fully qualified path to git, or <see langword="null"/>.</returns>
    internal static string? LocateGitOnPath() =>
        LocateOnPath("git", Environment.GetEnvironmentVariable("PATH"));

    /// <summary>
    /// Searches <paramref name="pathVariable"/> — and nothing else — for an executable called
    /// <paramref name="name"/>, returning the first fully qualified match.
    /// </summary>
    /// <param name="name">The extension-less executable name, e.g. <c>git</c>.</param>
    /// <param name="pathVariable">The raw <c>PATH</c> value to search.</param>
    /// <returns>A fully qualified path, or <see langword="null"/> when nothing matched.</returns>
    /// <remarks>
    /// <para>
    /// <strong>A NON-ROOTED ENTRY IS SKIPPED, NOT RESOLVED, AND THAT IS THE POINT OF THE METHOD.</strong>
    /// <c>PATH</c> is itself an ordered list that may contain a relative entry, and an EMPTY element
    /// means "the current directory" on some platforms. Resolving either against the process's
    /// current directory would put back — one indirection further along — the ambient-directory
    /// term this whole resolution exists to remove from the answer. Skipping is cheap and the
    /// entries it skips are not ones a correctly installed git occupies. The test is
    /// <see cref="Path.IsPathFullyQualified(string)"/> rather than <see cref="Path.IsPathRooted(string)"/>
    /// because the latter accepts the Windows drive-relative form <c>C:dir</c>, which resolves
    /// against that drive's current directory and is therefore not rooted in any useful sense.
    /// </para>
    /// <para>
    /// <strong>AN ENTRY IS USED VERBATIM — NEITHER UNQUOTED NOR TRIMMED.</strong> Both are
    /// <c>cmd.exe</c> behaviours rather than <c>CreateProcess</c> ones, so either would resolve an
    /// entry the OS search this replaces does not. Measured on this host (Windows 11 build
    /// 26200.9168, net8.0), by a bare-name <c>Process.Start</c> with <c>UseShellExecute = false</c>
    /// from a console app, against a directory holding one real executable: the entry spelt plainly
    /// LAUNCHED; spelt with a leading space, a trailing space, a leading tab, or wrapped in literal
    /// quotes it was NOT FOUND in all four cases. This search returns exactly those five answers.
    /// Trimming was the live defect rather than a hypothetical one: it made a leading-space entry
    /// resolve, and a leading-space entry written FIRST — the shape <c>PATH=%PATH%; C:\tools</c>
    /// leaves behind — then shadowed a real git in a later entry, promoting a directory Windows
    /// ignores into the highest-priority one here.
    /// </para>
    /// <para>
    /// <strong>ONE CANDIDATE PER ENTRY, AND ON WINDOWS IT IS <c>.exe</c> — NOT <c>PATHEXT</c>.</strong>
    /// The header records the two measurements behind that: the OS search this replaces appends
    /// only <c>.exe</c>, and a <c>.cmd</c>/<c>.bat</c> candidate would be launched through
    /// <c>cmd.exe</c>, whose parser re-reads arguments that <c>ArgumentList</c> quoted for
    /// <c>CreateProcess</c> — turning the caller's ref into command execution. A host whose only
    /// git is a shim therefore reports "not found", exactly as it did before #499 introduced this
    /// search at all. POSIX takes the bare name plus an execute-permission check — see the next
    /// paragraph for what that check does and does not establish.
    /// </para>
    /// <para>
    /// <strong>THE POSIX EXECUTE TEST ASKS THE C LIBRARY, NOT THE MODE BITS.</strong>
    /// <see cref="IsExecutableFile"/> calls <c>access(2)</c> with <c>X_OK</c> — the same question
    /// <c>which</c>, <c>test -x</c> and <c>command -v</c> ask, and the one whose answer
    /// <c>execve</c> then acts on. It used to accept a file when ANY of the user, group or other
    /// execute bits was set, whoever was running, and that is a strictly wider test than the
    /// kernel's: a root-owned <c>0700</c> <c>git</c> in an earlier entry was taken, the search
    /// STOPPED THERE because it returns the first match, the launch failed on permission
    /// (<c>EACCES</c>), and <c>RunGit</c> mapped that to a <c>ChangeSetException</c> — exit 2 on a
    /// host where a later entry held a runnable git. That is the same harm shape as the whitespace
    /// trim the AN ENTRY IS USED VERBATIM paragraph above records deleting: an entry the operating
    /// system would have passed over shadowed a legitimate later one. MEASURED, in
    /// <c>mcr.microsoft.com/dotnet/sdk:8.0</c> (glibc, net8.0) as uid 1000 against a file this
    /// caller owns with mode <c>0611</c> — the mode the row plants, <c>UserRead | UserWrite |
    /// GroupExecute | OtherExecute</c>: the mode-bit test said executable, <c>access(X_OK)</c> said
    /// not, and <c>Process.Start</c> refused it with <c>Win32Exception</c> "Permission denied" — so
    /// the narrower answer is the one that matches the launch. The owner triad has to be the one
    /// WITHOUT an execute bit for that divergence to exist at all: POSIX selects the permission
    /// class by ownership and stops, so an owner-executable mode would make <c>access(X_OK)</c>
    /// return 0 and there would be nothing to measure. As root, in the same image,
    /// <c>access(X_OK)</c> succeeds for that file and so does the launch; root's wider reach is the
    /// kernel's, not this search's.
    /// </para>
    /// <para>
    /// <strong>Two limits, stated rather than implied.</strong> <c>access(2)</c> resolves against
    /// the REAL uid/gid, not the effective pair; the two differ only for a set-uid or set-gid
    /// image, which this CLI is not, so for every caller that reaches here they are the same
    /// answer. <c>faccessat(…, AT_EACCESS)</c> is the effective-uid form and is deliberately not
    /// used: its flag constant differs between platforms (and between libcs), which trades a
    /// distinction that cannot arise here for a portability hazard that can. Second, the interop
    /// is a fallback away from the old behaviour rather than a replacement of it — a runtime
    /// where <c>libc</c> or the symbol cannot be found degrades to the mode-bit test, whose
    /// residual (accepting a file somebody ELSE may execute) is the defect above, narrowed to
    /// hosts where the P/Invoke does not resolve at all.
    /// </para>
    /// <para>
    /// <strong>"A runtime where it does not resolve" MEANS ALPINE, and naming it is the
    /// point.</strong>
    /// musl ships no <c>libc.so</c> for the loader to bind <c>[DllImport("libc")]</c> against, so an
    /// Alpine container is the concrete host on which <c>EffectiveExecutePermission</c> returns
    /// <see langword="null"/> and #509 quietly reverts to the wider mode-bit test. (INFERRED from
    /// musl's library naming; not measured — no lane here runs Alpine.) Calling that "an exotic
    /// runtime", as this used to, made a mainstream container image sound like a curiosity.
    /// </para>
    /// <para>
    /// <strong>And the row that would notice SELF-SKIPS AS ROOT.</strong>
    /// <c>LocateOnPath_Posix_RefusesAFileThisCallerMayNotExecute</c> returns early when the caller
    /// has root's reach, because root diverges from nothing. A Linux container running as root —
    /// still the default for many images — therefore verifies the P/Invoke path not at all, and does
    /// so without anything going red. Both limits are properties of where this is RUN, so neither is
    /// closable from inside this file.
    /// </para>
    /// <para>
    /// Takes <c>PATH</c> as an argument rather than reading the environment so that the search can
    /// be exercised against a temporary directory: mutating this process's <c>PATH</c> from a test
    /// would race every other test in the assembly.
    /// </para>
    /// </remarks>
    internal static string? LocateOnPath(string name, string? pathVariable)
    {
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        var fileName = CandidateFileName(name, OperatingSystem.IsWindows());

        foreach (var entry in pathVariable.Split(Path.PathSeparator))
        {
            if (entry.Length == 0 || !Path.IsPathFullyQualified(entry))
            {
                continue;
            }

            var candidate = Path.Combine(entry, fileName);
            if (IsExecutableFile(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The one file name looked for in each <c>PATH</c> entry.
    /// </summary>
    /// <param name="name">The extension-less executable name, e.g. <c>git</c>.</param>
    /// <param name="windows">Whether the Windows rule applies.</param>
    /// <returns><c>name.exe</c> under the Windows rule; <paramref name="name"/> unchanged otherwise.</returns>
    /// <remarks>
    /// Split out of <see cref="LocateOnPath(string, string?)"/>, which takes the platform from
    /// <see cref="OperatingSystem.IsWindows"/>, so that the Windows rule is assertable OFF Windows.
    /// The filesystem half of the search is not: a <c>.cmd</c> carries no execute bit on Linux, so
    /// the row that plants a shim and watches it be refused can only run on Windows — and no
    /// blocking CI lane is Windows (#366), which would leave a security property pinned by a row
    /// that cannot redden the gate. This seam is what a cross-platform row asserts against; the
    /// coupling between it and the search itself is covered only by the Windows-only row.
    /// </remarks>
    internal static string CandidateFileName(string name, bool windows) =>
        windows ? name + ".exe" : name;

    /// <summary>
    /// Reports whether <paramref name="candidate"/> is an existing file this caller can run.
    /// </summary>
    /// <param name="candidate">The fully qualified candidate path.</param>
    /// <returns><see langword="true"/> when the file exists and this caller may run it.</returns>
    /// <remarks>
    /// The POSIX arm asks <c>access(2)</c> rather than reading mode bits, for the reason
    /// <see cref="LocateOnPath(string, string?)"/>'s THE POSIX EXECUTE TEST paragraph records:
    /// a file this caller cannot execute, accepted here, ENDS the search and refuses a host that
    /// holds a runnable git further along <c>PATH</c>. The mode-bit read survives only as the
    /// degraded answer for a runtime where the P/Invoke does not resolve.
    /// </remarks>
    private static bool IsExecutableFile(string candidate)
    {
        // File.Exists is false for a directory and for a malformed path, so it also stands in for
        // the argument validation this method would otherwise need.
        if (!File.Exists(candidate))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        var permitted = EffectiveExecutePermission(candidate);
        if (permitted is not null)
        {
            return permitted.Value;
        }

        try
        {
            // The wider, pre-#509 test, reached only when the C library could not be asked. It
            // accepts a file somebody ELSE may execute; that residual is the price of degrading
            // rather than refusing every candidate on an exotic runtime.
            var mode = File.GetUnixFileMode(candidate);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file whose mode cannot be read is not a file we are willing to launch.
            return false;
        }
    }

    /// <summary>
    /// Asks the C library whether this caller may execute <paramref name="candidate"/>.
    /// </summary>
    /// <param name="candidate">The fully qualified candidate path, known to exist.</param>
    /// <returns>
    /// The <c>access(2)</c> answer, or <see langword="null"/> when the P/Invoke could not be
    /// resolved on this runtime — which is the caller's signal to fall back, NOT a refusal.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A non-zero return is treated as "not permitted" without reading <c>errno</c>. The caller
    /// has already established the file exists, so the reachable failure is <c>EACCES</c>; the
    /// remaining ones (<c>ENOENT</c> from a race, <c>ELOOP</c>, <c>ENOTDIR</c>) all describe a
    /// candidate that would not launch either, so classifying them would change no answer.
    /// </para>
    /// <para>
    /// The path is marshalled as an explicit NUL-terminated UTF-8 <c>byte[]</c> rather than as a
    /// <see cref="string"/>. UTF-8 is what a Unix <c>CharSet.Ansi</c> marshals to anyway, so the
    /// encoding is unchanged; doing it here keeps the signature free of string marshalling, which
    /// on this code base is what would otherwise pull in either a <c>CharSet</c> the security
    /// analysers argue about or the <c>AllowUnsafeBlocks</c> that source-generated string
    /// marshalling requires.
    /// </para>
    /// </remarks>
    private static bool? EffectiveExecutePermission(string candidate)
    {
        var pathname = new byte[Encoding.UTF8.GetByteCount(candidate) + 1];
        Encoding.UTF8.GetBytes(candidate, pathname);

        try
        {
            return NativeMethods.Access(pathname, NativeMethods.ExecuteOk) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The one P/Invoke in this code base, kept in the shape the interop analysers expect.
    /// </summary>
    private static class NativeMethods
    {
        /// <summary>
        /// <c>X_OK</c> — the execute-permission bit of <c>access(2)</c>'s mode argument. Fixed at
        /// 1 by POSIX and identical on every libc this CLI can run against.
        /// </summary>
        internal const int ExecuteOk = 1;

        /// <summary>
        /// <c>int access(const char *pathname, int mode)</c>.
        /// </summary>
        /// <param name="pathname">A NUL-terminated UTF-8 path.</param>
        /// <param name="mode">The permission mask, here <see cref="ExecuteOk"/>.</param>
        /// <returns>0 when permitted; -1 otherwise, with <c>errno</c> set.</returns>
        [DllImport("libc", EntryPoint = "access")]
        internal static extern int Access(byte[] pathname, int mode);
    }

    /// <summary>
    /// Parses newline-separated git path output (from <c>diff --name-only</c> or
    /// <c>status --porcelain</c>) and adds each entry, resolved to an absolute normalised
    /// path, to <paramref name="changed"/>.
    /// </summary>
    private static void AddPaths(HashSet<string> changed, string repoRoot, string output, bool status)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var relative = status ? StripStatusPrefix(line) : line;
            if (relative is null || relative.Length == 0)
            {
                continue;
            }

            // git output is repo-relative with forward slashes; make it absolute. The combined
            // path mixes the OS-native repoRoot with git's forward slashes, so canonicalise
            // separators before GetFullPath for the same reason as IsChanged (on Linux a stray
            // '\' would be kept as a literal filename char instead of acting as a separator).
            var absolute = Normalise(Path.GetFullPath(ToOsSeparators(Path.Combine(repoRoot, relative))));
            changed.Add(absolute);
        }
    }

    /// <summary>
    /// Strips the two-column XY status code (and the rename arrow form) from a
    /// <c>git status --porcelain</c> line, returning the (possibly post-rename) path.
    /// </summary>
    /// <remarks>
    /// A porcelain line is <c>XY&lt;space&gt;path</c>; for a rename it is
    /// <c>R  old -&gt; new</c>.  We take the destination path (after <c>-&gt;</c>) as the
    /// changed file, and unquote git's C-style quoting of paths with special characters.
    /// </remarks>
    private static string? StripStatusPrefix(string line)
    {
        // The status code occupies columns 0–1; the path begins at column 3.
        if (line.Length < 4)
        {
            return null;
        }

        var path = line[3..];

        var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
        if (arrow >= 0)
        {
            path = path[(arrow + 4)..];
        }

        return Unquote(path);
    }

    /// <summary>
    /// Undoes git's C-style path quoting (a path containing special bytes is wrapped in
    /// double quotes with backslash escapes).  An unquoted path is returned verbatim.
    /// </summary>
    private static string Unquote(string path)
    {
        if (path.Length < 2 || path[0] != '"' || path[^1] != '"')
        {
            return path;
        }

        var inner = path[1..^1];
        return inner.Replace("\\\"", "\"", StringComparison.Ordinal)
                    .Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    /// <summary>Normalises path separators to <c>/</c> for cross-platform comparison.</summary>
    private static string Normalise(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Rewrites both <c>\</c> and <c>/</c> to the OS-native directory separator so the result
    /// can be handed to <see cref="Path.GetFullPath(string)"/> on any platform.
    /// </summary>
    /// <remarks>
    /// On Linux, <see cref="Path.GetFullPath(string)"/> treats <c>\</c> as a literal filename
    /// character rather than a separator, so a Windows-style path such as
    /// <c>\repo\orders\x.e2e.yaml</c> is seen as relative (it does not start with <c>/</c>),
    /// gets the current directory prepended, and keeps its literal backslashes — never matching
    /// a <c>/</c>-normalised change-set key. Canonicalising to the native separator FIRST makes
    /// both slash styles act as separators on every OS. Do not "simplify" this away.
    /// </remarks>
    private static string ToOsSeparators(string path) =>
        path.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);
}
