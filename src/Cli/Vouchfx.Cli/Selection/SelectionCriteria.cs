// Vouchfx.Cli — SelectionCriteria (S07-C-02).
//
// The strongly-typed, immutable bundle of test-selection filters parsed from the `run`
// command's --tag / --owner / --path / --changed-since flags. It drives the runner's
// test-selection language (BP §16): `metadata` (tags/owner) has NO execution effect — it
// only narrows WHICH discovered scenarios are handed to the runner.
//
// Semantics (see ScenarioSelector): AND across the four dimensions, OR within each
// dimension. An empty Tags / Owners list and a null PathGlob / ChangedSinceRef each mean
// "this dimension imposes no constraint".

namespace Vouchfx.Cli.Selection;

/// <summary>
/// The composable set of filters applied to discovered scenarios before they reach the
/// runner.  Selection is AND-across-dimensions, OR-within-a-dimension (see
/// <see cref="ScenarioSelector"/>).
/// </summary>
/// <param name="Tags">
/// Tag filter (OR within): a scenario matches when it carries <em>any</em> of these tags.
/// Empty ⇒ no tag constraint.  Matching is case-insensitive (ordinal).
/// </param>
/// <param name="Owners">
/// Owner filter (OR within): a scenario matches when its <c>metadata.owner</c> is one of
/// these.  Empty ⇒ no owner constraint.  Matching is case-insensitive (ordinal).
/// </param>
/// <param name="PathGlob">
/// Optional glob (<c>*</c>/<c>**</c>/<c>?</c>) matched against the scenario's absolute,
/// separator-normalised (<c>/</c>) path.  <see langword="null"/> ⇒ no path constraint.
/// </param>
/// <param name="ChangedSinceRef">
/// Optional git ref; when set, only scenarios whose file changed since that ref (per the
/// injected <see cref="IChangeSet"/>) match.  <see langword="null"/> ⇒ no change-set
/// constraint.
/// </param>
public sealed record SelectionCriteria(
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Owners,
    string? PathGlob,
    string? ChangedSinceRef)
{
    /// <summary>
    /// The criteria that select everything: no tag, owner, path or change-set constraint.
    /// </summary>
    public static SelectionCriteria None { get; } =
        new(Array.Empty<string>(), Array.Empty<string>(), PathGlob: null, ChangedSinceRef: null);

    /// <summary>
    /// <see langword="true"/> when at least one of the metadata dimensions
    /// (<see cref="Tags"/> or <see cref="Owners"/>) imposes a constraint.
    /// </summary>
    /// <remarks>
    /// Names the parse-failure rule's precondition (see <see cref="ScenarioSelector"/>): a
    /// scenario with no metadata to match is excluded as soon as a metadata filter is active
    /// and included otherwise.  It is NOT a claim that every parse failure has no metadata —
    /// a document that parsed and was refused only by <c>AstBuilder</c> bound its
    /// <c>metadata</c> block, discovery retains it, and the selector matches on that (issue
    /// #411).
    /// </remarks>
    public bool HasMetadataFilter => Tags.Count > 0 || Owners.Count > 0;

    /// <summary>
    /// <see langword="true"/> when no dimension imposes a constraint (equivalent to
    /// <see cref="None"/>): every discovered scenario is selected.
    /// </summary>
    public bool IsEmpty =>
        Tags.Count == 0 && Owners.Count == 0 && PathGlob is null && ChangedSinceRef is null;
}
