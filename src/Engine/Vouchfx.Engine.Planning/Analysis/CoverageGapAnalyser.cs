// Vouchfx.Engine.Planning.Analysis — CoverageGapAnalyser (M3 Planner, REQ-004, REQ-008,
// EDGE-002).
//
// STUB (T1) — filled in by a later todo (T2). Final signature; already registered in
// PlanPipeline.Run — do NOT edit PlanPipeline.cs to wire this in further.
//
// Future scope (T2): declared-vs-exercised coverage gaps — suite-never-run,
// step-never-exercised, dependency-not-asserted (using StepFamilyRoles for the
// asserting-family check and the REQ-004/REQ-005 precedence table) — plus the
// suite-identity-ambiguous finding and Ambiguous/AmbiguityReason markers derived from
// PlanInputs.History's collision/renamed-file facts (REQ-008 / EDGE-002). Emits
// SuggestedTypes empty on every gap finding; HandOffHints.Enrich (T3) fills them.

using Vouchfx.Engine.Planning.Ingest;
using Vouchfx.Engine.Planning.Report;

namespace Vouchfx.Engine.Planning.Analysis;

/// <summary>REQ-004 declared-vs-exercised coverage-gap analyser, plus REQ-008 suite-identity ambiguity.</summary>
internal static class CoverageGapAnalyser
{
    /// <summary>Analyses <paramref name="inputs"/> for coverage gaps. Stub: always empty.</summary>
    internal static IReadOnlyList<PlanFinding> Analyse(PlanInputs inputs) => Array.Empty<PlanFinding>();
}
