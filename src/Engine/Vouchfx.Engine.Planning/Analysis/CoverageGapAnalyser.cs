// Vouchfx.Engine.Planning.Analysis — CoverageGapAnalyser (M3 Planner, REQ-004, REQ-008,
// EDGE-002).
//
// Declared-vs-exercised coverage gaps:
//   - suite-never-run: a discovered suite whose ScenarioId never appears among the
//     history's matched scenario executions;
//   - step-never-exercised: a declared step id with no step-completed observation
//     attributed to it via the (ScenarioId, StepId) key RunCorrelator produces;
//   - dependency-not-asserted: REQ-004 bullet 3, subject to the precedence table against
//     REQ-005 (VocabularyGapAnalyser, T3): emitted only when at least one declared step in
//     THIS suite targets the dependency and none of those targeting steps belongs to an
//     asserting family (StepFamilyRoles.IsAsserting). A dependency with ZERO targeting
//     steps is deliberately never reported here — that is T3's vocabulary-gap territory
//     (REQ-004's precedence paragraph) — so the "zero" and "some-but-none-asserting" rows
//     of the table stay mutually exclusive by construction.
//
// Suite-identity ambiguity (REQ-008 / EDGE-002, flag F-2's "implement exactly this" rule):
//   1. >=2 declared suites share a ScenarioId (PlanEventHistory.Collisions) -> ONE
//      suite-identity-ambiguous finding per colliding id (not per suite — the spec's own
//      phrasing draws this contrast against "every coverage finding for those suites",
//      which IS per suite). The finding is not scoped to a single suite (Suite is null,
//      matching PlanFinding.Suite's documented null-semantics for a multi-suite finding);
//      its Detail names every colliding path. Every coverage finding (suite-never-run,
//      step-never-exercised, dependency-not-asserted) emitted for a suite in the collision
//      carries Ambiguous=true / AmbiguityReason=ScenarioIdCollision.
//   2. Zero match, but the run's recorded file resolves under the analysed root and no
//      longer exists on disk (PlanEventHistory.RenamedFileAmbiguities) -> one
//      suite-identity-ambiguous finding per entry, naming the recorded (missing) path in
//      Detail. No declared suite matches this case by definition, so there is no coverage
//      finding to stamp — this is the ONLY thing reported for it.
//   3. Zero match otherwise is already folded into PlanEventHistory.UnmatchedObservationCount
//      by RunCorrelator (EDGE-007) — nothing further to do here.
//   4. A step id shared across two suites under DISTINCT runIds is never ambiguous: the
//      (ScenarioId, StepId) observation key already disambiguates by construction, since each
//      suite's own ScenarioId differs (or, when suites share a ScenarioId, that case is
//      already rule 1's territory) — no separate handling is required.
//
// Emits SuggestedTypes empty / SuggestedStepId null on every finding — HandOffHints.Enrich
// (T3) fills them on every REQ-004/REQ-005 gap finding afterwards; suite-identity-ambiguous
// is deliberately not a gap kind (PlanFindingKinds.IsGap) and never receives a hint.

using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Planning.Ingest;
using Vouchfx.Engine.Planning.Report;

namespace Vouchfx.Engine.Planning.Analysis;

/// <summary>REQ-004 declared-vs-exercised coverage-gap analyser, plus REQ-008 suite-identity ambiguity.</summary>
internal static class CoverageGapAnalyser
{
    /// <summary>
    /// Analyses <paramref name="inputs"/> for REQ-004 coverage gaps and REQ-008
    /// suite-identity-ambiguity findings.
    /// </summary>
    /// <param name="inputs">The fully-assembled, correlated Planner inputs.</param>
    internal static IReadOnlyList<PlanFinding> Analyse(PlanInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var findings = new List<PlanFinding>();

        // Every ScenarioId that participates in a rule-1 collision, so per-suite coverage
        // findings for a colliding suite can be stamped Ambiguous=true.
        var collidingScenarioIds = new HashSet<string>(
            inputs.History.Collisions.Select(c => c.ScenarioId),
            StringComparer.Ordinal);

        foreach (var suite in inputs.Suites)
        {
            var isColliding = collidingScenarioIds.Contains(suite.ScenarioId);
            var ambiguityReason = isColliding ? PlanAmbiguityReasons.ScenarioIdCollision : null;

            AnalyseSuiteNeverRun(suite, inputs, isColliding, ambiguityReason, findings);
            AnalyseStepsNeverExercised(suite, inputs, isColliding, ambiguityReason, findings);
            AnalyseDependenciesNotAsserted(suite, isColliding, ambiguityReason, findings);
        }

        AnalyseScenarioIdCollisions(inputs, findings);
        AnalyseRenamedFileAmbiguities(inputs, findings);

        return findings;
    }

    // ── REQ-004 bullet 1: suite-never-run ────────────────────────────────────────

    private static void AnalyseSuiteNeverRun(
        PlanSuite suite,
        PlanInputs inputs,
        bool isColliding,
        string? ambiguityReason,
        List<PlanFinding> findings)
    {
        var hasRun = inputs.History.RunIdsByScenarioId.TryGetValue(suite.ScenarioId, out var runIds)
            && runIds.Count > 0;

        if (hasRun)
        {
            return;
        }

        findings.Add(new PlanFinding(
            Kind: PlanFindingKinds.SuiteNeverRun,
            Suite: suite.RelativePath,
            StepId: null,
            Target: null,
            TargetKind: null,
            SuggestedTypes: Array.Empty<string>(),
            SuggestedStepId: null,
            Ambiguous: isColliding,
            AmbiguityReason: ambiguityReason,
            History: null,
            RelatedSuites: Array.Empty<string>(),
            Detail: $"Suite '{suite.RelativePath}' never appears in the analysed event history."));
    }

    // ── REQ-004 bullet 2: step-never-exercised ───────────────────────────────────

    private static void AnalyseStepsNeverExercised(
        PlanSuite suite,
        PlanInputs inputs,
        bool isColliding,
        string? ambiguityReason,
        List<PlanFinding> findings)
    {
        foreach (var step in suite.Ast.Steps)
        {
            var key = (suite.ScenarioId, step.Id);
            if (inputs.History.StepObservations.ContainsKey(key))
            {
                continue;
            }

            // BLOCKER fix-round: a step-never-exercised finding names the service/dependency
            // its own declared `target` addresses whenever that target resolves (REQ-004's
            // two exclusions — a {placeholder}/${secret:...} reference, never substituted —
            // still apply via StepTargetResolver.ResolveTarget) AND classifies against THIS
            // suite's declared environment. Without this, a host going gap -> scaffold has no
            // way to bind the scaffolded step back to the seam the original step addressed —
            // exactly the re-derivation REQ-007 exists to eliminate. Both stay null when the
            // target is unresolvable (placeholder/secret) or unclassifiable (no matching
            // environment.services/dependencies entry in this suite, e.g. no environment
            // block declared at all) — never a crash, never a fabricated target.
            string? target = null;
            string? targetKind = null;
            var resolvedTarget = StepTargetResolver.ResolveTarget(step);
            if (resolvedTarget is not null
                && StepTargetResolver.TryClassifyTarget(suite, resolvedTarget, out var classifiedKind))
            {
                target = resolvedTarget;
                targetKind = classifiedKind;
            }

            findings.Add(new PlanFinding(
                Kind: PlanFindingKinds.StepNeverExercised,
                Suite: suite.RelativePath,
                StepId: step.Id,
                Target: target,
                TargetKind: targetKind,
                SuggestedTypes: Array.Empty<string>(),
                SuggestedStepId: null,
                Ambiguous: isColliding,
                AmbiguityReason: ambiguityReason,
                History: null,
                RelatedSuites: Array.Empty<string>(),
                Detail: $"Step '{step.Id}' has no step event attributed to it."));
        }
    }

    // ── REQ-004 bullet 3: dependency-not-asserted (precedence table row 2 only) ──

    private static void AnalyseDependenciesNotAsserted(
        PlanSuite suite,
        bool isColliding,
        string? ambiguityReason,
        List<PlanFinding> findings)
    {
        var dependencies = suite.Ast.Environment?.Dependencies;
        if (dependencies is null)
        {
            return;
        }

        foreach (var dependencyName in dependencies.Keys)
        {
            var targetingSteps = suite.Ast.Steps
                .Where(step => TargetsDependency(suite, step, dependencyName))
                .ToList();

            if (targetingSteps.Count == 0)
            {
                // Precedence rule (REQ-004/REQ-005): a zero-step dependency is exclusively
                // T3's vocabulary-gap territory, reported once there — never here.
                continue;
            }

            var hasAssertingStep = targetingSteps.Any(step => StepFamilyRoles.IsAsserting(step.Kind.Family));
            if (hasAssertingStep)
            {
                continue;
            }

            findings.Add(new PlanFinding(
                Kind: PlanFindingKinds.DependencyNotAsserted,
                Suite: suite.RelativePath,
                StepId: null,
                Target: dependencyName,
                TargetKind: PlanTargetKinds.Dependency,
                SuggestedTypes: Array.Empty<string>(),
                SuggestedStepId: null,
                Ambiguous: isColliding,
                AmbiguityReason: ambiguityReason,
                History: null,
                RelatedSuites: Array.Empty<string>(),
                Detail: $"Dependency '{dependencyName}' is targeted by a declared step but by no step of an asserting family."));
        }
    }

    private static bool TargetsDependency(PlanSuite suite, StepNode step, string dependencyName)
    {
        var target = StepTargetResolver.ResolveTarget(step);
        return target is not null
            && string.Equals(target, dependencyName, StringComparison.Ordinal)
            && StepTargetResolver.TryClassifyTarget(suite, target, out var targetKind)
            && targetKind == PlanTargetKinds.Dependency;
    }

    // ── REQ-008 rule 1: scenario-id collisions ───────────────────────────────────

    private static void AnalyseScenarioIdCollisions(PlanInputs inputs, List<PlanFinding> findings)
    {
        foreach (var collision in inputs.History.Collisions)
        {
            var suitesList = string.Join(", ", collision.SuitePaths);

            findings.Add(new PlanFinding(
                Kind: PlanFindingKinds.SuiteIdentityAmbiguous,
                Suite: null,
                StepId: null,
                Target: null,
                TargetKind: null,
                SuggestedTypes: Array.Empty<string>(),
                SuggestedStepId: null,
                Ambiguous: true,
                AmbiguityReason: PlanAmbiguityReasons.ScenarioIdCollision,
                History: null,
                // MAJOR fix-round (additive, REQ-011-compatible): the colliding suite paths,
                // machine-readably — Detail already names them in prose, but an MCP consumer
                // should not have to regex a sentence to find them. Already ordinal-sorted
                // (RunCorrelator.Correlate).
                RelatedSuites: collision.SuitePaths,
                Detail: $"Scenario id '{collision.ScenarioId}' is declared by {collision.SuitePaths.Count} suites: {suitesList}."));
        }
    }

    // ── REQ-008 rule 2: history references a since-renamed file ─────────────────

    private static void AnalyseRenamedFileAmbiguities(PlanInputs inputs, List<PlanFinding> findings)
    {
        foreach (var renamed in inputs.History.RenamedFileAmbiguities)
        {
            findings.Add(new PlanFinding(
                Kind: PlanFindingKinds.SuiteIdentityAmbiguous,
                Suite: null,
                StepId: null,
                Target: null,
                TargetKind: null,
                SuggestedTypes: Array.Empty<string>(),
                SuggestedStepId: null,
                Ambiguous: true,
                AmbiguityReason: PlanAmbiguityReasons.HistoryReferencesMissingFile,
                History: null,
                // No declared suite matches a rule-2 renamed-file ambiguity by definition
                // (that is precisely why it is rule 2, not rule 1) — there is no suite to
                // relate, so this stays empty, unlike the rule-1 collision finding above.
                RelatedSuites: Array.Empty<string>(),
                Detail: $"History references a file for scenario id '{renamed.ScenarioId}' that no longer exists on disk: '{renamed.RecordedFile}'."));
        }
    }
}
