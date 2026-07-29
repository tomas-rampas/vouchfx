// Vouchfx.Engine.Planning.Analysis — HandOffHints (M3 Planner, REQ-007).
//
// STUB (T1) — filled in by a later todo (T3), alongside DependencyKindStepMap.cs and
// VocabularyGapAnalyser.cs. Final signature; already registered in PlanPipeline.Run — do NOT
// edit PlanPipeline.cs to wire this in further.
//
// Future scope (T3): enrich every REQ-004/REQ-005 gap finding that lacks hints with
// SuggestedTypes (must exist in the registry) and a deterministic, YAML-legal
// SuggestedStepId. Leaves history-health and suite-identity-ambiguity findings untouched
// (they are permitted to omit hints). Splitting hint enrichment from the gap analysers
// themselves removes the only genuine cross-analyser data dependency: CoverageGapAnalyser's
// dependency-not-asserted finding needs the same candidate-type list
// VocabularyGapAnalyser's DependencyKindStepMap owns, so T2 emits SuggestedTypes empty and
// this enricher (owned by the same todo as the map) fills them in afterwards.

using Vouchfx.Engine.Planning.Ingest;
using Vouchfx.Engine.Planning.Report;

namespace Vouchfx.Engine.Planning.Analysis;

/// <summary>REQ-007 hand-off-hint enrichment over every gap finding that lacks one.</summary>
internal static class HandOffHints
{
    /// <summary>
    /// Returns <paramref name="findings"/> with REQ-004/REQ-005 gap findings enriched with
    /// suggested step types and a suggested step id. Stub: returns its input unchanged.
    /// </summary>
    internal static IReadOnlyList<PlanFinding> Enrich(IReadOnlyList<PlanFinding> findings, PlanInputs inputs) =>
        findings;
}
