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
// ChangeSetException, which the CLI maps to a usage error (exit 2) — NEVER a crash.

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
    private readonly HashSet<string> _changed;

    /// <summary>
    /// Builds the change-set by running git in <paramref name="workingDirectory"/>.
    /// </summary>
    /// <param name="changedSinceRef">
    /// The git ref to diff against (e.g. <c>main</c>, a tag, or a SHA).
    /// </param>
    /// <param name="workingDirectory">A directory inside the working tree to run git in.</param>
    /// <param name="processRunner">The seam used to invoke git.</param>
    /// <exception cref="ChangeSetException">
    /// Thrown when git is unavailable, the directory is not a repository, or the ref is bad.
    /// </exception>
    public GitChangeSet(string changedSinceRef, string workingDirectory, IProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(processRunner);

        // Argument-injection guard: a leading-dash ref (e.g. "--output=...") would be parsed
        // by git as an OPTION, not a revision, even when passed via ArgumentList (which only
        // prevents *shell* injection, not git's own option parsing). Reject such refs — and
        // null/empty/whitespace — BEFORE any git call that splices the ref into its argv.
        if (string.IsNullOrWhiteSpace(changedSinceRef) || changedSinceRef.StartsWith('-'))
        {
            throw new ChangeSetException(
                $"Invalid git ref '{changedSinceRef}': must not start with '-'.");
        }

        var repoRoot = ResolveRepoRoot(workingDirectory, processRunner);

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // (1) Committed changes between the merge-base of <ref> and HEAD, and HEAD.
        // `--end-of-options` forces every following token to be a revision/path, never an
        // option — defence-in-depth so even a dash-leading value that slipped past the guard
        // above cannot be (mis)parsed by git as a flag. (git 2.24+, 2019.)
        var diff = RunGit(
            processRunner,
            workingDirectory,
            $"diff for ref '{changedSinceRef}'",
            "diff", "--name-only", "--end-of-options", $"{changedSinceRef}...HEAD");
        AddPaths(changed, repoRoot, diff.StandardOutput, status: false);

        // (2) The dirty working tree (staged, unstaged, untracked). `-c core.quotepath=false`
        // disables git's C-style octal-escaping of non-ASCII bytes, so a path such as
        // "tëst.e2e.yaml" is emitted verbatim (UTF-8) and matches the on-disk file. (The
        // Unquote step below still handles the remaining `\"`/`\\` escapes for paths whose
        // names contain a quote or backslash.)
        var status = RunGit(
            processRunner,
            workingDirectory,
            "working-tree status",
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
    private static string ResolveRepoRoot(string workingDirectory, IProcessRunner processRunner)
    {
        var result = RunGit(
            processRunner,
            workingDirectory,
            "repository-root lookup",
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
    /// <strong>Every exception <see cref="IProcessRunner.Run"/> documents is mapped here, and that
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
    /// </remarks>
    private static ProcessResult RunGit(
        IProcessRunner processRunner,
        string workingDirectory,
        string operation,
        params string[] arguments)
    {
        ProcessResult result;
        try
        {
            result = processRunner.Run("git", arguments, workingDirectory);
        }
        catch (ProcessLaunchException ex)
        {
            throw new ChangeSetException(
                $"Could not run git for {operation}. Is git installed and on PATH? ({ex.Message})",
                ex);
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
