// Vouchfx.Engine.Planning.Analysis — HistoryHealthAnalyser (M3 Planner, REQ-006, flag F-8).
//
// Classifies per-step history health against PlanThresholds: step-stale, step-flaky,
// step-fragile, step-inconclusive-prone. Three rules the spec calls out as easy to get
// wrong (pinned here, and in the T0 architecture note, so a reviewer can check both):
//
//   1. "Now" is ALWAYS inputs.History.LastTimestamp (the maximum ts across the analysed
//      history) — NEVER wall-clock time (DateTimeOffset.UtcNow/Now). Two analyses of the
//      same input at different real-world moments MUST produce byte-identical output.
//
//   2. Only step-completed.verdict counts toward these tallies — never step-attempt.outcome.
//      This is additionally enforced structurally one layer down: PlanEventHistory's
//      StepObservations (RunCorrelator's output) is built exclusively from
//      EventHistoryReader's StepCompletions list, which itself contains only
//      successfully-typed `step-completed` events — a `step-attempt` line is recognised
//      (it still counts toward the history's timestamp range and run-id set) but is never
//      turned into a PlanStepObservation. A step-attempt outcome is therefore not even
//      reachable from PlanInputs. Counting attempts would flag every RETRY step as flaky
//      the moment one poll failed before succeeding, inverting the intent.
//
//   3. Iterate DECLARED steps (inputs.Suites[*].Ast.Steps) — never
//      PlanEventHistory.StepObservations.Keys. An observation may reference a step id since
//      deleted from a still-declared suite; a finding naming a nonexistent step would be
//      wrong.
//
// Emits no REQ-007 hand-off hints: HandOffHints.Enrich (T3) only fills SuggestedTypes /
// SuggestedStepId on findings whose kind is a PlanFindingKinds.IsGap gap, and all four kinds
// this analyser emits are deliberately outside IsGap. A history-health finding is also not
// Target/TargetKind-scoped — it reports a step's own observed history, not a service or
// dependency seam — so both stay null.
//
// MINOR fix-round: the Ambiguous / AmbiguityReason markers ARE stamped here (this analyser
// does not itself classify suite identity — PlanInputs.History.Collisions, RunCorrelator's
// output, already does that): when a suite's ScenarioId participates in a REQ-008 rule-1
// scenario-id collision, the StepObservations keyed under that ScenarioId are the MERGED
// history of every colliding suite, not any one suite's own, so presenting them unmarked
// would silently claim merged data as one suite's history — the same caveat
// CoverageGapAnalyser's own coverage findings already carry for a colliding suite.

using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Planning.Ingest;
using Vouchfx.Engine.Planning.Report;

namespace Vouchfx.Engine.Planning.Analysis;

/// <summary>REQ-006 per-step history-health analyser (stale / flaky / fragile / inconclusive-prone).</summary>
internal static class HistoryHealthAnalyser
{
    /// <summary>
    /// Analyses <paramref name="inputs"/> for per-step history-health findings: a declared
    /// step whose observed <c>step-completed</c> verdicts are stale, flaky, fragile, or
    /// inconclusive-prone against <see cref="PlanInputs.Thresholds"/>.
    /// </summary>
    /// <param name="inputs">The fully-assembled, correlated Planner inputs.</param>
    internal static IReadOnlyList<PlanFinding> Analyse(PlanInputs inputs)
    {
        var findings = new List<PlanFinding>();
        var now = inputs.History.LastTimestamp;
        var thresholds = inputs.Thresholds;

        // MINOR fix-round: a scenario-id collision (REQ-008 rule 1) means the observations
        // keyed under this ScenarioId are the MERGED history of every colliding suite, not
        // any one suite's own — presenting that as unambiguously "this suite's history"
        // would be misleading. Mirrors CoverageGapAnalyser's identical collidingScenarioIds
        // computation (kept local to each file rather than shared, since PlanInputs.History
        // already exposes Collisions directly to any analyser that needs it).
        var collidingScenarioIds = new HashSet<string>(
            inputs.History.Collisions.Select(c => c.ScenarioId),
            StringComparer.Ordinal);

        // Rule 3: iterate DECLARED steps, never StepObservations.Keys.
        foreach (var suite in inputs.Suites)
        {
            var isColliding = collidingScenarioIds.Contains(suite.ScenarioId);

            foreach (var step in suite.Ast.Steps)
            {
                if (!inputs.History.StepObservations.TryGetValue((suite.ScenarioId, step.Id), out var observations)
                    || observations.Count == 0)
                {
                    // No step-completed evidence at all for this declared step — nothing to
                    // classify here. (A step that was never exercised at all is
                    // CoverageGapAnalyser's step-never-exercised concern, not this
                    // analyser's.)
                    continue;
                }

                var passCount = 0;
                var failCount = 0;
                var envErrorCount = 0;
                var inconclusiveCount = 0;
                DateTimeOffset? lastObserved = null;
                var distinctRuns = new HashSet<string>(StringComparer.Ordinal);

                foreach (var observation in observations)
                {
                    distinctRuns.Add(observation.RunId);

                    switch (observation.Verdict)
                    {
                        case Verdict.Pass:
                            passCount++;
                            break;
                        case Verdict.Fail:
                            failCount++;
                            break;
                        case Verdict.EnvironmentError:
                            envErrorCount++;
                            break;
                        case Verdict.Inconclusive:
                            inconclusiveCount++;
                            break;
                    }

                    if (lastObserved is null || observation.Timestamp > lastObserved)
                    {
                        lastObserved = observation.Timestamp;
                    }
                }

                // Rule 1: "now" is inputs.History.LastTimestamp — never wall-clock. Guarded
                // defensively (never crash) even though, in practice, at least one
                // step-completed observation existing above guarantees LastTimestamp is set.
                //
                // MINOR fix-round: the STALE-THRESHOLD COMPARISON below is evaluated on the
                // exact, un-truncated day span (ageDaysExact), never on the truncated int —
                // truncating first and then testing "> StaleDays" would make "more than 30
                // days" actually mean "at least 31 WHOLE days" (a step observed 30.9 days ago
                // truncates to 30, and 30 > 30 is false, silently missing a step that IS more
                // than 30 days stale). AgeDays itself, exposed on the report, stays a
                // whole-number-of-days value (its own XML doc's documented contract) — only
                // the boundary CHECK needs the exact span.
                double? ageDaysExact = lastObserved.HasValue && now.HasValue
                    ? (now.Value - lastObserved.Value).TotalDays
                    : null;
                int? ageDays = ageDaysExact.HasValue ? (int)ageDaysExact.Value : null;

                var history = new PlanStepHistory(
                    PassCount: passCount,
                    FailCount: failCount,
                    EnvErrorCount: envErrorCount,
                    InconclusiveCount: inconclusiveCount,
                    DistinctRuns: distinctRuns.Count,
                    LastObserved: lastObserved,
                    AgeDays: ageDays);

                if (ageDaysExact.HasValue && ageDaysExact.Value > thresholds.StaleDays)
                {
                    findings.Add(BuildFinding(
                        PlanFindingKinds.StepStale,
                        suite.RelativePath,
                        step,
                        history,
                        isColliding,
                        $"Step '{step.Id}' ({step.CanonicalType}) in suite '{suite.RelativePath}' was last "
                        + $"observed {ageDays!.Value} day(s) before the newest event in the analysed history "
                        + $"(stale threshold: {thresholds.StaleDays} day(s))."));
                }

                if (passCount >= 1 && failCount >= 1 && distinctRuns.Count >= thresholds.FlakyMinRuns)
                {
                    findings.Add(BuildFinding(
                        PlanFindingKinds.StepFlaky,
                        suite.RelativePath,
                        step,
                        history,
                        isColliding,
                        $"Step '{step.Id}' ({step.CanonicalType}) in suite '{suite.RelativePath}' shows "
                        + $"{passCount} Pass and {failCount} Fail verdict(s) across {distinctRuns.Count} "
                        + $"distinct run(s) (flaky threshold: {thresholds.FlakyMinRuns} run(s))."));
                }

                if (envErrorCount >= thresholds.FragileMinEnvErrors)
                {
                    findings.Add(BuildFinding(
                        PlanFindingKinds.StepFragile,
                        suite.RelativePath,
                        step,
                        history,
                        isColliding,
                        $"Step '{step.Id}' ({step.CanonicalType}) in suite '{suite.RelativePath}' shows "
                        + $"{envErrorCount} EnvironmentError verdict(s) (fragile threshold: "
                        + $"{thresholds.FragileMinEnvErrors})."));
                }

                if (inconclusiveCount >= thresholds.InconclusiveMin)
                {
                    findings.Add(BuildFinding(
                        PlanFindingKinds.StepInconclusiveProne,
                        suite.RelativePath,
                        step,
                        history,
                        isColliding,
                        $"Step '{step.Id}' ({step.CanonicalType}) in suite '{suite.RelativePath}' shows "
                        + $"{inconclusiveCount} Inconclusive verdict(s) (inconclusive-prone threshold: "
                        + $"{thresholds.InconclusiveMin})."));
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// Builds a single history-health <see cref="PlanFinding"/>: no target, no REQ-007
    /// hand-off hint — this analyser only ever reports a step's own observed history
    /// (EDGE-006: <paramref name="detail"/> must be composed only from suite paths, step ids,
    /// dotted step types, verdict counts, and threshold numbers). <paramref name="isColliding"/>
    /// (MINOR fix-round) stamps the REQ-008 ambiguity marker when this suite's ScenarioId
    /// participates in a scenario-id collision — the observations tallied above are then the
    /// MERGED history of every colliding suite, not any one suite's own, exactly the same
    /// caveat CoverageGapAnalyser's coverage findings already carry for a colliding suite.
    /// </summary>
    private static PlanFinding BuildFinding(
        string kind,
        string suiteRelativePath,
        StepNode step,
        PlanStepHistory history,
        bool isColliding,
        string detail) =>
        new(
            Kind: kind,
            Suite: suiteRelativePath,
            StepId: step.Id,
            Target: null,
            TargetKind: null,
            SuggestedTypes: Array.Empty<string>(),
            SuggestedStepId: null,
            Ambiguous: isColliding,
            AmbiguityReason: isColliding ? PlanAmbiguityReasons.ScenarioIdCollision : null,
            History: history,
            RelatedSuites: Array.Empty<string>(),
            Detail: detail);
}
