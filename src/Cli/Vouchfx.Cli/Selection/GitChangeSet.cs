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
// git exit (bad ref, not a repo) or a launch failure (git not installed) is wrapped in a
// ChangeSetException, which the CLI maps to a usage error (exit 2) — NEVER a crash.

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

        var normalised = Normalise(Path.GetFullPath(absolutePath));
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

        return Normalise(Path.GetFullPath(root));
    }

    /// <summary>
    /// Runs a git subcommand, mapping a launch failure or non-zero exit to a
    /// <see cref="ChangeSetException"/> with the captured stderr for diagnosis.
    /// </summary>
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

            // git output is repo-relative with forward slashes; make it absolute.
            var absolute = Normalise(Path.GetFullPath(Path.Combine(repoRoot, relative)));
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
}
