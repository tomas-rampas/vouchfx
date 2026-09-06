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
// git exit (bad ref, not a repo), a launch failure (git not installed) or a timeout (a wedged
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
// applies ITS OWN search order to the unqualified name. What that search order buys an attacker
// is stated below at the two different confidence levels the evidence supports — the first is
// MEASURED, the second was probed and did NOT reproduce.
//
// MEASURED, on this host (Windows 11, net8.0, UseShellExecute = false). An impostor `git.exe`
// placed in the APPLICATION LOAD DIRECTORY — beside the calling executable — is launched in
// preference to the real git on `PATH`: the probe's child printed the impostor's marker, exit 0,
// while the identical call with the impostor removed printed `git version 2.54.0.windows.1`. That
// directory is the first entry in the documented search order, and it is not hypothetical for a
// tool installed as a dotnet global tool: everything in `~/.dotnet/tools` is writable by the user,
// and one file dropped there takes over every git call this file makes. That precedence — the
// application load directory ahead of `PATH` — is the whole of what this change removes.
//
// NOT CLOSED, and saying so is the point of the paragraph. An attacker-writable directory sitting
// EARLIER IN `PATH` than git's own still wins, because the search below takes the first `PATH`
// match and launches that. Nothing here re-orders or vets `PATH`; the change moves the resolution
// from "whatever Windows searches" to "`PATH`, in order, and nothing else", which is strictly
// smaller but is not empty.
//
// PROBED AND DID NOT REPRODUCE — the CURRENT-DIRECTORY story this header used to tell. The claim
// was that `ProcessStartInfo.WorkingDirectory` is derived from the discovery root, so a `git.exe`
// committed into an untrusted repository won. It is wrong on paper and wrong in measurement.
// On paper: the documented search order names the CURRENT DIRECTORY OF THE CALLING PROCESS, while
// `WorkingDirectory` sets `lpCurrentDirectory` FOR THE CHILD and takes no part in resolving the
// command line's module name. In measurement, on this host, neither spelling won — with the
// impostor present in the plant directory and absent from the application load directory, both
// `WorkingDirectory = <plant dir>` and `Directory.SetCurrentDirectory(<plant dir>)` launched the
// real git. So that scenario is NOT what this fix closes, and the past tense it was written in
// ("ran instead of the real git") was never earned.
//
// A ROOTED `ProcessStartInfo.FileName` removes the whole question rather than answering it: both
// `CreateProcess` and `execve` take a rooted path literally and search nothing. That is worth
// doing for the measured hazard alone, and it makes the unmeasured one moot as a side effect.
//
// This is a DIFFERENT hazard from the two guards already here, and neither addressed it. The
// leading-dash refusal plus `--end-of-options` defends git's own OPTION PARSING; `ArgumentList`
// defends against SHELL quoting. Which BINARY is resolved was covered by neither.
//
// The search lives in this file rather than in SystemProcessRunner because it is git-specific
// (PATHEXT candidates, the "is git installed" diagnostic) and that runner deliberately carries no
// git knowledge — the same reason it has no environment seam, which is #500. It runs ONCE per
// change-set: three git calls, one resolution.

using System.Globalization;

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
    /// <summary>The only candidate suffix off Windows: the bare name.</summary>
    private static readonly string[] PosixSuffixes = { string.Empty };

    /// <summary>The candidate suffixes used when <c>PATHEXT</c> is unset or unusable.</summary>
    private static readonly string[] DefaultWindowsSuffixes = { ".EXE", ".COM", ".BAT", ".CMD" };

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
        // The refusal deliberately reuses the launch-failure wording and therefore the launch
        // failure's OUTCOME: a ChangeSetException, which the CLI maps to exit 2. Whether
        // selection-infrastructure failure deserves a code of its own is issues #480 and #466-B;
        // a fix for a binary-resolution defect does not get to answer it in passing.
        var gitExecutable = (gitExecutableLocator ?? LocateGitOnPath)()
            ?? throw new ChangeSetException(GitUnavailable("the change-set computation"));

        var repoRoot = ResolveRepoRoot(gitExecutable, workingDirectory, processRunner, cancellationToken);

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // (1) Committed changes between the merge-base of <ref> and HEAD, and HEAD.
        // `--end-of-options` forces every following token to be a revision/path, never an
        // option — defence-in-depth so even a dash-leading value that slipped past the guard
        // above cannot be (mis)parsed by git as a flag. (git 2.24+, 2019.)
        var diff = RunGit(
            gitExecutable,
            processRunner,
            workingDirectory,
            $"diff for ref '{changedSinceRef}'",
            cancellationToken,
            "diff", "--name-only", "--end-of-options", $"{changedSinceRef}...HEAD");
        AddPaths(changed, repoRoot, diff.StandardOutput, status: false);

        // (2) The dirty working tree (staged, unstaged, untracked). `-c core.quotepath=false`
        // disables git's C-style octal-escaping of non-ASCII bytes, so a path such as
        // "tëst.e2e.yaml" is emitted verbatim (UTF-8) and matches the on-disk file. (The
        // Unquote step below still handles the remaining `\"`/`\\` escapes for paths whose
        // names contain a quote or backslash.)
        var status = RunGit(
            gitExecutable,
            processRunner,
            workingDirectory,
            "working-tree status",
            cancellationToken,
            "-c", "core.quotepath=false", "status", "--porcelain");
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
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var result = RunGit(
            gitExecutable,
            processRunner,
            workingDirectory,
            "repository-root lookup",
            cancellationToken,
            "rev-parse", "--show-toplevel");

        var root = result.StandardOutput.Trim();
        if (root.Length == 0)
        {
            throw new ChangeSetException(
                $"git did not report a repository root for '{workingDirectory}'.");
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
    /// selection-infrastructure failure deserves an exit code of its own belongs to issues #480
    /// and #466-B, and answering it here — quietly, in a bug fix — would change the CLI's
    /// documented exit-code contract as a side effect of stopping a hang.
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
        string operation,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        ProcessResult result;
        try
        {
            result = processRunner.Run(gitExecutable, arguments, workingDirectory, cancellationToken);
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
            // localises. GitUnavailable already tells the author the one thing they can act on,
            // so the clause is dropped instead. The exception is still chained, so the full
            // detail remains available to a debugger and to anything that walks InnerException.
            throw new ChangeSetException(GitUnavailable(operation), ex);
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
            throw new ChangeSetException(
                $"Could not read the output of git {operation}: {ex.InnerException?.Message ?? ex.Message}. A change-set cannot be computed from a partial capture, so selection is refused rather than narrowed.",
                ex);
        }

        if (result.ExitCode != 0)
        {
            var detail = result.StandardError.Trim();
            if (detail.Length == 0)
            {
                detail = result.StandardOutput.Trim();
            }

            throw new ChangeSetException(
                $"git {operation} failed (exit {result.ExitCode}): "
                + (detail.Length > 0 ? detail : "no diagnostic output."));
        }

        return result;
    }

    /// <summary>
    /// The one wording for "git could not be run", shared by the <c>PATH</c>-resolution refusal
    /// and by the runner's launch failure so that the two cannot drift apart.
    /// </summary>
    /// <param name="operation">What was being attempted, in the caller's own vocabulary.</param>
    /// <returns>The message, deliberately naming no path — see <see cref="RunGit"/>.</returns>
    private static string GitUnavailable(string operation) =>
        $"Could not run git for {operation}. Is git installed and on PATH?";

    /// <summary>
    /// Locates the git executable on this process's <c>PATH</c>, returning a fully qualified path
    /// or <see langword="null"/> when no entry holds one.
    /// </summary>
    /// <returns>A fully qualified path to git, or <see langword="null"/>.</returns>
    internal static string? LocateGitOnPath() =>
        LocateOnPath(
            "git",
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT"));

    /// <summary>
    /// Searches <paramref name="pathVariable"/> — and nothing else — for an executable called
    /// <paramref name="name"/>, returning the first fully qualified match.
    /// </summary>
    /// <param name="name">The extension-less executable name, e.g. <c>git</c>.</param>
    /// <param name="pathVariable">The raw <c>PATH</c> value to search.</param>
    /// <param name="pathExtVariable">The raw <c>PATHEXT</c> value; ignored off Windows.</param>
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
    /// <strong>Windows candidates come from <c>PATHEXT</c>; POSIX ones are the bare name plus an
    /// execute-bit check.</strong> Windows has no execute bit — membership of <c>PATHEXT</c> IS
    /// the executability test there, which is why the extension-less name is not a candidate on
    /// that platform. The POSIX check accepts any of the three execute bits rather than computing
    /// what the effective user may actually run; that is the same approximation <c>which</c> makes,
    /// and erring towards "found" here costs at worst a launch failure that is already mapped.
    /// </para>
    /// <para>
    /// Takes the two variables as arguments rather than reading the environment so that the search
    /// can be exercised against a temporary directory: mutating this process's <c>PATH</c> from a
    /// test would race every other test in the assembly.
    /// </para>
    /// </remarks>
    internal static string? LocateOnPath(string name, string? pathVariable, string? pathExtVariable)
    {
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        var suffixes = ExecutableSuffixes(pathExtVariable);

        foreach (var rawEntry in pathVariable.Split(Path.PathSeparator))
        {
            var entry = rawEntry.Trim();

            // A Windows PATH entry may be quoted ("C:\Program Files\Git\cmd"); the OS strips those
            // quotes before searching, so a resolver that did not would report a perfectly usable
            // git as absent. On POSIX a double quote is a legal filename character, so it is left
            // alone there.
            if (OperatingSystem.IsWindows())
            {
                entry = entry.Trim('"');
            }

            if (entry.Length == 0 || !Path.IsPathFullyQualified(entry))
            {
                continue;
            }

            foreach (var suffix in suffixes)
            {
                var candidate = Path.Combine(entry, name + suffix);
                if (IsExecutableFile(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>The candidate name suffixes to try, in order, for the current platform.</summary>
    /// <param name="pathExtVariable">The raw <c>PATHEXT</c> value; ignored off Windows.</param>
    /// <returns>One suffix per candidate; the empty string means "the bare name".</returns>
    /// <remarks>
    /// The Windows fallback list is used only when <c>PATHEXT</c> is unset or holds nothing usable,
    /// so that a stripped environment reports git as present rather than as missing. Entries that
    /// do not begin with <c>.</c> are dropped: they would compose into names such as
    /// <c>gitEXE</c>, which is not what the caller meant by them.
    /// </remarks>
    private static string[] ExecutableSuffixes(string? pathExtVariable)
    {
        if (!OperatingSystem.IsWindows())
        {
            return PosixSuffixes;
        }

        var configured = (pathExtVariable ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static extension => extension.StartsWith('.'))
            .ToArray();

        return configured.Length > 0 ? configured : DefaultWindowsSuffixes;
    }

    /// <summary>
    /// Reports whether <paramref name="candidate"/> is an existing file this platform would run.
    /// </summary>
    /// <param name="candidate">The fully qualified candidate path.</param>
    /// <returns><see langword="true"/> when the file exists and is executable.</returns>
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

        try
        {
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
