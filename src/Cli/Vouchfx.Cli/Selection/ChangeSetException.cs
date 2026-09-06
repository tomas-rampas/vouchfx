// Vouchfx.Cli — ChangeSetException (S07-C-02).
//
// Raised by GitChangeSet when the change-set cannot be computed: git is not installed, the
// directory is not a repository, the --changed-since ref is bad, a git call outlasts the
// runner's per-call process budget, or its output capture fails. The CLI catches this at the
// `run` boundary and maps it to a usage error (exit 2) with the message printed. Exit 2 is
// retained for every one of those causes, and that is deliberate rather than a claim that a
// wedged git or a broken capture pipe is the user's input mistake: whether a failure of the
// selection infrastructure deserves an exit code of its own belongs to issues #480 and #466-B
// (see the remarks on GitChangeSet.RunGit).

namespace Vouchfx.Cli.Selection;

/// <summary>
/// Thrown when a git-backed change-set cannot be computed: git unavailable, not a repo, a bad
/// ref, a git call that outlasts the process budget, or a failed output capture.  Surfaced by
/// the CLI as a usage error rather than an unhandled crash.
/// </summary>
[System.Serializable]
internal sealed class ChangeSetException : Exception
{
    /// <summary>Initialises a new instance with a user-facing message.</summary>
    public ChangeSetException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance with a message and the underlying cause.</summary>
    public ChangeSetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
