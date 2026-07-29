// Vouchfx.Engine.Planning.Analysis — VocabularyGapAnalyser (M3 Planner, REQ-005, REQ-007).
//
// STUB (T1) — filled in by a later todo (T3), alongside DependencyKindStepMap.cs and
// HandOffHints.cs. Final signature; already registered in PlanPipeline.Run — do NOT edit
// PlanPipeline.cs to wire this in further.
//
// Future scope (T3): declared-vs-available vocabulary gaps — dependency-missing-step-type
// under the zero-of-N candidate rule (DependencyKindStepMap.TryGetCandidates), and
// service-missing-http-step (a declared service with no http.* step targeting it). Emits
// SuggestedTypes empty on every gap finding; HandOffHints.Enrich (also T3) fills them.

using Vouchfx.Engine.Planning.Ingest;
using Vouchfx.Engine.Planning.Report;

namespace Vouchfx.Engine.Planning.Analysis;

/// <summary>REQ-005 declared-vs-available vocabulary-gap analyser.</summary>
internal static class VocabularyGapAnalyser
{
    /// <summary>Analyses <paramref name="inputs"/> for vocabulary gaps. Stub: always empty.</summary>
    internal static IReadOnlyList<PlanFinding> Analyse(PlanInputs inputs) => Array.Empty<PlanFinding>();
}
