// Vouchfx.Engine.Planning — DependencyKindStepMap (M3 Planner, REQ-005, REQ-007, flag F-4).
//
// STUB (T1) — the concrete mapping table is filled in by a later todo (T3). This file's
// public shape (namespace, type name, member signatures, and XML docs) is final and MUST
// NOT change: PlanPipeline.cs (frozen) already calls TryGetCandidates to classify every
// declared dependency as mappable or PlanUnmappableDependency (REQ-007), and
// VocabularyGapAnalyser.cs (also a T1 stub, filled by the same later todo) will call it to
// emit REQ-005 findings. While this stub returns no candidates for any kind, EVERY declared
// dependency is (correctly, if unexcitingly) classified unmappable — the moment the real
// mapping table lands here, PlanPipeline's inventory classification and
// VocabularyGapAnalyser's findings both narrow automatically, with no edit required to
// either caller.
//
// The dependency-kind -> candidate-step-type relation MUST stay an explicit,
// engine-maintained mapping, never string equality between the dependency `type` token and
// the catalogue `provider` token: pure derivation is provably unsound (mailpit ->
// mail-expect.smtp and minio -> storage-assert.s3 are shipped Core kinds whose tokens do not
// match the KnownDependencyKinds token).

using Vouchfx.Engine.Compilation.Scaffold;

namespace Vouchfx.Engine.Planning;

/// <summary>
/// The engine-maintained, CI-guarded mapping from a dependency kind (a
/// <c>Vouchfx.Engine.Compilation.Scaffold.KnownDependencyKinds</c> token) to the dotted step
/// types that assert or observe it (REQ-005).
/// </summary>
/// <remarks>
/// <para>
/// Pure derivation from the dependency <c>type</c> token is unsound for shipped kinds
/// (<c>mailpit</c> → <c>mail-expect.smtp</c>, <c>minio</c> → <c>storage-assert.s3</c>), so
/// this mapping is explicit rather than computed, and is guarded against drift by two CI
/// tests: one asserting every mapped step type still exists in the current registration,
/// and one asserting every <see cref="KindsWithoutAssertingProvider"/>-or-mapped kind covers
/// every entry in <c>KnownDependencyKinds.All</c> — so adding a dependency kind forces a
/// mapping decision in the same change.
/// </para>
/// <para>
/// TODO(T3): populate <see cref="CandidateStepTypes"/> and
/// <see cref="KindsWithoutAssertingProvider"/> with the real mapping table. This stub
/// intentionally returns no candidates for any kind.
/// </para>
/// </remarks>
public static class DependencyKindStepMap
{
    /// <summary>
    /// Dependency kind (a <c>KnownDependencyKinds</c> token, matched
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>) to the dotted step types that assert
    /// or observe it, ordinal-sorted. Empty until T3 populates the real mapping table.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> CandidateStepTypes { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Dependency kinds explicitly known to have no asserting/observing Core provider yet.
    /// Every entry must carry an inline comment justifying it (REQ-007 case (a)). Empty
    /// today (and until T3 lands) — every shipped Core dependency kind maps to at least one
    /// candidate step type.
    /// </summary>
    public static IReadOnlySet<string> KindsWithoutAssertingProvider { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Attempts to look up the candidate step types for <paramref name="kind"/>.
    /// </summary>
    /// <param name="kind">A dependency kind token, or <see langword="null"/>.</param>
    /// <param name="candidates">
    /// When this method returns <see langword="true"/>, the non-empty, ordinal-sorted list
    /// of candidate dotted step types; otherwise an empty list.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="kind"/> has at least one registered
    /// candidate step type; <see langword="false"/> otherwise (including when
    /// <paramref name="kind"/> is <see langword="null"/> or unmapped).
    /// </returns>
    public static bool TryGetCandidates(string? kind, out IReadOnlyList<string> candidates)
    {
        if (kind is not null && CandidateStepTypes.TryGetValue(kind, out var found) && found.Count > 0)
        {
            candidates = found;
            return true;
        }

        candidates = Array.Empty<string>();
        return false;
    }

    /// <summary>
    /// Describes why <paramref name="kind"/> is unmappable (REQ-007), for callers that have
    /// already established via <see cref="TryGetCandidates"/> that it has no candidate step
    /// type. Lives here (not in <c>PlanPipeline</c>) so the reason text sits beside the
    /// mapping table it describes, in the file this todo owns.
    /// </summary>
    /// <param name="kind">A dependency kind token with no candidate step type.</param>
    /// <returns>
    /// REQ-007 case (a) when <paramref name="kind"/> is in
    /// <see cref="KindsWithoutAssertingProvider"/>; REQ-007 case (b) — genuinely consulting
    /// <see cref="KnownDependencyKinds.All"/>, not inferred from the absence of an
    /// exemption — when <paramref name="kind"/> is not recognised by the engine's
    /// dependency-kind vocabulary at all; otherwise a drift fallback naming a kind that is
    /// known and not exempted yet still carries no candidate, a state the two
    /// <c>DependencyKindStepMapDriftTests</c> CI gates exist to make unreachable.
    /// </returns>
    public static string DescribeUnmappable(string kind)
    {
        if (KindsWithoutAssertingProvider.Contains(kind))
        {
            return "No asserting/observing Core provider exists yet for this dependency kind.";
        }

        if (!KnownDependencyKinds.Contains(kind))
        {
            return "Dependency kind is not recognised by the engine's dependency-kind vocabulary.";
        }

        return "Dependency kind is recognised by the engine but is not yet mapped to a "
            + "candidate step type (a mapping-table gap; file an issue).";
    }
}
