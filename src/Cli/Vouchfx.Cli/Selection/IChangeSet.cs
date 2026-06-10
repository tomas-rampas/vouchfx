// Vouchfx.Cli — IChangeSet (S07-C-02).
//
// Abstraction over "which scenario files changed since a git ref". The git shell-out lives
// behind this interface (GitChangeSet) so that:
//   • the per-scenario selection check is a cheap set lookup (the changed set is computed
//     ONCE, up front, not re-run per file), and
//   • unit tests inject a fake without needing a real git repository or Docker.

namespace Vouchfx.Cli.Selection;

/// <summary>
/// A precomputed set of changed scenario files, queried per absolute path during selection.
/// </summary>
/// <remarks>
/// Implementations resolve the change-set once (e.g. <see cref="GitChangeSet"/> shells out
/// to git a single time on construction); <see cref="IsChanged"/> is then a pure lookup.
/// </remarks>
internal interface IChangeSet
{
    /// <summary>
    /// Returns whether the file at <paramref name="absolutePath"/> is in (or under) the
    /// change-set.
    /// </summary>
    /// <param name="absolutePath">
    /// The scenario's absolute path.  Separator normalisation is the implementation's
    /// responsibility so callers can pass an OS-native path.
    /// </param>
    /// <returns><see langword="true"/> when the file is considered changed.</returns>
    bool IsChanged(string absolutePath);
}

/// <summary>
/// The no-op change-set used when <c>--changed-since</c> is absent: every file is
/// "changed", so the change-set dimension imposes no constraint.
/// </summary>
internal sealed class NullChangeSet : IChangeSet
{
    /// <summary>The shared, stateless instance.</summary>
    public static NullChangeSet Instance { get; } = new();

    private NullChangeSet()
    {
    }

    /// <inheritdoc />
    public bool IsChanged(string absolutePath) => true;
}
