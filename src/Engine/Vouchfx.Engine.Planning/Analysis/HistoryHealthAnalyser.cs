// Vouchfx.Engine.Planning.Analysis — HistoryHealthAnalyser (M3 Planner, REQ-006, flag F-8).
//
// STUB (T1) — filled in by a later todo (T4). Final signature; already registered in
// PlanPipeline.Run — do NOT edit PlanPipeline.cs to wire this in further.
//
// Future scope (T4): per-step history-health classification against PlanThresholds —
// step-stale, step-flaky, step-fragile, step-inconclusive-prone. "Now" MUST be
// PlanInputs.History.LastTimestamp (the maximum event ts in the analysed history), never
// wall-clock time. Only step-completed.verdict counts toward these tallies — never
// step-attempt.outcome (flag F-8): a RETRY step emits one step-attempt per polling cycle, so
// counting attempts would flag every RETRY step as flaky the moment one poll failed before
// succeeding. Emits no REQ-007 hand-off hints (history-health findings are permitted to omit
// them) and populates PlanStepHistory on every finding it emits.

using Vouchfx.Engine.Planning.Ingest;
using Vouchfx.Engine.Planning.Report;

namespace Vouchfx.Engine.Planning.Analysis;

/// <summary>REQ-006 per-step history-health analyser (stale / flaky / fragile / inconclusive-prone).</summary>
internal static class HistoryHealthAnalyser
{
    /// <summary>Analyses <paramref name="inputs"/> for history-health findings. Stub: always empty.</summary>
    internal static IReadOnlyList<PlanFinding> Analyse(PlanInputs inputs) => Array.Empty<PlanFinding>();
}
