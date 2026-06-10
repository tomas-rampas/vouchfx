// Vouchfx.Cli — ChangeSetException (S07-C-02).
//
// Raised by GitChangeSet when the change-set cannot be computed: git is not installed, the
// directory is not a repository, or the --changed-since ref is bad. The CLI catches this at
// the `run` boundary and maps it to a usage error (exit 2) with the message printed — a
// mis-specified selection is the user's input mistake, not an engine crash.

namespace Vouchfx.Cli.Selection;

/// <summary>
/// Thrown when a git-backed change-set cannot be computed (git unavailable, not a repo, or
/// a bad ref).  Surfaced by the CLI as a usage error rather than an unhandled crash.
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
