// Vouchfx.Engine.Planning.Tests — SuiteIdentityAmbiguityTests (M3 Planner, REQ-008,
// EDGE-002, T2 scope).
//
// Exercises CoverageGapAnalyser's suite-identity-ambiguity handling (flag F-2's four-rule
// disambiguation, "implement exactly this") through the public PlanExport.BuildPlan entry
// point. Rule 3 (zero match, no file / file outside root -> unmatched observation, no
// finding) is already covered by T1's PlanExportTests.ForeignScenarioObservation_* — this
// file covers rules 1, 2 and 4, plus the interleaved-parallel-run attribution proof.

using System.Diagnostics.CodeAnalysis;
using Vouchfx.Engine.Planning.Report;
using Xunit;

namespace Vouchfx.Engine.Planning.Tests;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "xUnit test methods use Given_When_Then underscore convention.")]
public sealed class SuiteIdentityAmbiguityTests
{
    private static readonly string[] CollidingSuitePaths = { "suite-one.e2e.yaml", "suite-two.e2e.yaml" };

    // ── REQ-008 rule 1 / EDGE-002 acceptance 1: two suites sharing a scenarioId ──

    [Fact]
    public void TwoSuitesSharingAScenarioId_ProduceAnAmbiguityFindingAndMarkTheirCoverageFindings()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("coverage/scenario-id-collision"));

        // Exactly one suite-identity-ambiguous finding per colliding scenarioId (not one per
        // suite) — the finding is not scoped to a single suite, so Suite is null; Detail
        // names every colliding path.
        var ambiguity = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.SuiteIdentityAmbiguous);
        Assert.True(ambiguity.Ambiguous);
        Assert.Equal(PlanAmbiguityReasons.ScenarioIdCollision, ambiguity.AmbiguityReason);
        Assert.Null(ambiguity.Suite);
        Assert.Contains("shared-name", ambiguity.Detail, StringComparison.Ordinal);
        Assert.Contains("suite-one.e2e.yaml", ambiguity.Detail, StringComparison.Ordinal);
        Assert.Contains("suite-two.e2e.yaml", ambiguity.Detail, StringComparison.Ordinal);

        // MAJOR fix-round: the colliding suite paths are now ALSO machine-readable via
        // RelatedSuites (additive), not only inside Detail's prose — ordinal-sorted, mirroring
        // PlanScenarioIdCollision.SuitePaths.
        Assert.Equal(CollidingSuitePaths, ambiguity.RelatedSuites);

        // Both colliding suites never ran (no history at all was supplied) — both
        // suite-never-run findings must carry the ambiguity marker.
        var suiteNeverRun = report.Findings
            .Where(f => f.Kind == PlanFindingKinds.SuiteNeverRun)
            .ToList();
        Assert.Equal(2, suiteNeverRun.Count);
        Assert.All(suiteNeverRun, f => Assert.True(f.Ambiguous));
        Assert.All(suiteNeverRun, f => Assert.Equal(PlanAmbiguityReasons.ScenarioIdCollision, f.AmbiguityReason));
        Assert.Contains(suiteNeverRun, f => f.Suite == "suite-one.e2e.yaml");
        Assert.Contains(suiteNeverRun, f => f.Suite == "suite-two.e2e.yaml");

        // Their declared steps are also never-exercised, and likewise marked ambiguous.
        var stepNeverExercised = report.Findings
            .Where(f => f.Kind == PlanFindingKinds.StepNeverExercised)
            .ToList();
        Assert.Equal(2, stepNeverExercised.Count);
        Assert.All(stepNeverExercised, f => Assert.True(f.Ambiguous));
    }

    // ── REQ-008 rule 2 / EDGE-002's rename acceptance: history references a since-renamed
    // file under the analysed root ───────────────────────────────────────────────────────

    [Fact]
    public void HistoryReferencingASinceRenamedFile_ProducesAnAmbiguityFinding()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("coverage/renamed-file-ambiguity"),
            PlannerTestFixtures.FixtureRoot("coverage/renamed-file-ambiguity-events"));

        var ambiguity = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.SuiteIdentityAmbiguous);
        Assert.True(ambiguity.Ambiguous);
        Assert.Equal(PlanAmbiguityReasons.HistoryReferencesMissingFile, ambiguity.AmbiguityReason);
        Assert.Contains("old-scenario", ambiguity.Detail, StringComparison.Ordinal);
        Assert.Contains("old-suite.e2e.yaml", ambiguity.Detail, StringComparison.Ordinal);

        // MAJOR fix-round: a rule-2 renamed-file ambiguity relates no suite at all (zero
        // declared suites matched, by definition) — unlike a rule-1 collision, RelatedSuites
        // stays empty here.
        Assert.Empty(ambiguity.RelatedSuites);

        // The one currently-declared suite ("current.e2e.yaml") is a completely separate,
        // non-colliding suite: it never appears in this history either (its own scenarioId
        // "current-scenario" is never referenced), so it gets its OWN suite-never-run
        // finding — but that finding must NOT be marked ambiguous, proving the marker is
        // scoped only to suites genuinely implicated in a collision, not "any ambiguity
        // present anywhere in the report".
        var suiteNeverRun = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.SuiteNeverRun);
        Assert.Equal("current.e2e.yaml", suiteNeverRun.Suite);
        Assert.False(suiteNeverRun.Ambiguous);
        Assert.Null(suiteNeverRun.AmbiguityReason);
    }

    // ── REQ-008 rule 4 / EDGE-002 acceptance 2: the SAME step id in two suites under
    // DISTINCT runIds must NOT be treated as ambiguous — this proves runId keying, not
    // positional bracketing. ──────────────────────────────────────────────────────────

    [Fact]
    public void SameStepIdInTwoSuitesUnderDistinctRunIds_MarkerIsAbsent()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("coverage/distinct-runid-same-stepid"),
            PlannerTestFixtures.FixtureRoot("coverage/distinct-runid-same-stepid-events"));

        // Both suites' "common-step" was correctly attributed via its own suite's runId, so
        // there is nothing left to report: no never-run, no never-exercised, no ambiguity.
        Assert.Empty(report.Findings);
    }

    // ── Interleaved parallel-run stream: each step is attributed to the scenario sharing
    // its runId, never by file position. ─────────────────────────────────────────────────

    [Fact]
    public void InterleavedParallelRunStream_EachStepIsAttributedByItsOwnRunId()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("coverage/interleaved-parallel-runs"),
            PlannerTestFixtures.FixtureRoot("coverage/interleaved-parallel-runs-events"));

        // The history file interleaves run-a's and run-b's events (scenario-started for
        // both appear before either step-completed, and the two scenario-completed lines
        // are reversed relative to their step-completed counterparts). A positional-window
        // implementation would misattribute at least one of these; exact-runId correlation
        // must attribute both suites' "step-1" correctly, leaving no coverage gap at all.
        Assert.Empty(report.Findings);
    }
}
