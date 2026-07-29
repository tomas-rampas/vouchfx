// Vouchfx.Engine.Planning.Tests — HistoryHealthAnalyserTests (M3 Planner, REQ-006, T4).
//
// Exercises HistoryHealthAnalyser exclusively through the public PlanExport.BuildPlan entry
// point (via PlannerTestFixtures.Plan) — the analyser itself stays internal, and PlanPipeline
// (frozen, T1) is the only thing that wires it into a PlanReportDocument.
//
// Fixture design (Fixtures/history/default-thresholds + …-events): a single suite declares
// four steps, one per REQ-006 kind, and one hand-authored event history pins every
// timestamp so that "now" (inputs.History.LastTimestamp — REQ-006's rule 1) is a fully
// controlled value: the last step-completed event, at 2024-03-10T00:05:00Z. Every date is
// deliberately far from the real wall-clock date this suite runs on, so a regression that
// swapped in DateTimeOffset.UtcNow/Now would blow every exact-count assertion below (ages of
// hundreds of days instead of the fixture's controlled 38/5/2/0), not just the dedicated
// wall-clock-independence test.
//   - stale-step:        one Pass, last observed 38 days before "now" (default threshold 30).
//   - flaky-step:        one Pass + one Fail across 2 distinct runs (default threshold 2).
//   - fragile-step:      two EnvironmentError verdicts across 2 distinct runs (default threshold 2).
//   - inconclusive-step: two Inconclusive verdicts across 2 distinct runs (default threshold 2).
// Each step is designed to trip exactly one kind — e.g. flaky-step's last observation is
// only 5 days old (well under the 30-day stale threshold) — so the "exactly one finding per
// kind" assertions are not accidentally satisfied by cross-contamination between steps.

using System.Diagnostics.CodeAnalysis;
using Vouchfx.Engine.Planning.Report;
using Xunit;

namespace Vouchfx.Engine.Planning.Tests;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "xUnit test methods use Given_When_Then underscore convention.")]
public sealed class HistoryHealthAnalyserTests
{
    private static readonly string[] HistoryHealthKinds =
    {
        PlanFindingKinds.StepStale,
        PlanFindingKinds.StepFlaky,
        PlanFindingKinds.StepFragile,
        PlanFindingKinds.StepInconclusiveProne,
    };

    private static string SuitePath(string fixture) => PlannerTestFixtures.FixtureRoot($"history/{fixture}");

    private static string EventsPath(string fixture) => PlannerTestFixtures.FixtureRoot($"history/{fixture}-events");

    private static bool IsHistoryHealthKind(string kind) => kind is PlanFindingKinds.StepStale
        or PlanFindingKinds.StepFlaky
        or PlanFindingKinds.StepFragile
        or PlanFindingKinds.StepInconclusiveProne;

    // ── REQ-006 acceptance: exactly one finding of each kind at default thresholds ──────────

    [Fact]
    public void Analyse_DefaultThresholds_YieldsExactlyOneFindingOfEachKind()
    {
        var report = PlannerTestFixtures.Plan(SuitePath("default-thresholds"), EventsPath("default-thresholds"));

        AssertExactlyOne(report, PlanFindingKinds.StepStale, "stale-step");
        AssertExactlyOne(report, PlanFindingKinds.StepFlaky, "flaky-step");
        AssertExactlyOne(report, PlanFindingKinds.StepFragile, "fragile-step");
        AssertExactlyOne(report, PlanFindingKinds.StepInconclusiveProne, "inconclusive-step");

        var historyFindings = report.Findings.Where(f => IsHistoryHealthKind(f.Kind)).ToArray();
        Assert.Equal(4, historyFindings.Length);

        foreach (var finding in historyFindings)
        {
            // REQ-006 evidence + REQ-007's explicit exemption for history-health findings.
            Assert.NotNull(finding.History);
            Assert.Empty(finding.SuggestedTypes);
            Assert.Null(finding.SuggestedStepId);
            Assert.False(finding.Ambiguous);
            Assert.Null(finding.AmbiguityReason);
            Assert.Null(finding.Target);
            Assert.Null(finding.TargetKind);
            Assert.Equal("history-health.e2e.yaml", finding.Suite);
        }
    }

    [Fact]
    public void Analyse_StaleFinding_CarriesTheObservedTallyAsEvidence()
    {
        var report = PlannerTestFixtures.Plan(SuitePath("default-thresholds"), EventsPath("default-thresholds"));

        var stale = Assert.Single(report.Findings, f => f.Kind == PlanFindingKinds.StepStale);

        Assert.NotNull(stale.History);
        Assert.Equal(1, stale.History!.PassCount);
        Assert.Equal(0, stale.History.FailCount);
        Assert.Equal(0, stale.History.EnvErrorCount);
        Assert.Equal(0, stale.History.InconclusiveCount);
        Assert.Equal(1, stale.History.DistinctRuns);
        Assert.Equal(38, stale.History.AgeDays);
        Assert.Equal(new DateTimeOffset(2024, 2, 1, 0, 5, 0, TimeSpan.Zero), stale.History.LastObserved);
    }

    // ── REQ-006 acceptance: thresholds are genuinely wired, not hard-coded ──────────────────

    [Fact]
    public void Analyse_StaleDaysRaisedPastFixtureAge_YieldsZeroStaleFindings()
    {
        var thresholds = new PlanThresholds(StaleDays: 38);
        var report = PlannerTestFixtures.Plan(SuitePath("default-thresholds"), EventsPath("default-thresholds"), thresholds);

        Assert.DoesNotContain(report.Findings, f => f.Kind == PlanFindingKinds.StepStale);
        Assert.Equal(3, report.Findings.Count(f => IsHistoryHealthKind(f.Kind)));
    }

    [Fact]
    public void Analyse_FlakyMinRunsRaisedPastFixtureRuns_YieldsZeroFlakyFindings()
    {
        var thresholds = new PlanThresholds(FlakyMinRuns: 3);
        var report = PlannerTestFixtures.Plan(SuitePath("default-thresholds"), EventsPath("default-thresholds"), thresholds);

        Assert.DoesNotContain(report.Findings, f => f.Kind == PlanFindingKinds.StepFlaky);
        Assert.Equal(3, report.Findings.Count(f => IsHistoryHealthKind(f.Kind)));
    }

    [Fact]
    public void Analyse_FragileMinEnvErrorsRaisedPastFixtureCount_YieldsZeroFragileFindings()
    {
        var thresholds = new PlanThresholds(FragileMinEnvErrors: 3);
        var report = PlannerTestFixtures.Plan(SuitePath("default-thresholds"), EventsPath("default-thresholds"), thresholds);

        Assert.DoesNotContain(report.Findings, f => f.Kind == PlanFindingKinds.StepFragile);
        Assert.Equal(3, report.Findings.Count(f => IsHistoryHealthKind(f.Kind)));
    }

    [Fact]
    public void Analyse_InconclusiveMinRaisedPastFixtureCount_YieldsZeroInconclusiveProneFindings()
    {
        var thresholds = new PlanThresholds(InconclusiveMin: 3);
        var report = PlannerTestFixtures.Plan(SuitePath("default-thresholds"), EventsPath("default-thresholds"), thresholds);

        Assert.DoesNotContain(report.Findings, f => f.Kind == PlanFindingKinds.StepInconclusiveProne);
        Assert.Equal(3, report.Findings.Count(f => IsHistoryHealthKind(f.Kind)));
    }

    // ── REQ-006 acceptance: "now" is history-derived, never wall-clock ──────────────────────

    [Fact]
    public async Task Analyse_TwoRunsAtDifferentWallClockTimes_ProduceIdenticalOutput()
    {
        var suitePath = SuitePath("default-thresholds");
        var eventsPath = EventsPath("default-thresholds");

        var first = PlannerTestFixtures.Plan(suitePath, eventsPath);
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        var second = PlannerTestFixtures.Plan(suitePath, eventsPath);

        Assert.Equal(PlanExport.SerializePlan(first), PlanExport.SerializePlan(second));
    }

    // ── flag F-8 / rule 2: step-attempt.outcome must never count toward the tallies ─────────

    [Fact]
    public void Analyse_RetryStepWithFailedAttemptsThatEventuallyPasses_YieldsNoHistoryHealthFinding()
    {
        var report = PlannerTestFixtures.Plan(SuitePath("retry-noise"), EventsPath("retry-noise"));

        Assert.DoesNotContain(report.Findings, f => IsHistoryHealthKind(f.Kind));
    }

    // ── rule 3: iterate declared steps, never StepObservations.Keys ─────────────────────────

    [Fact]
    public void Analyse_ObservationReferencingAnUndeclaredStep_NeverProducesAFindingNamingIt()
    {
        var report = PlannerTestFixtures.Plan(SuitePath("phantom-step"), EventsPath("phantom-step"));

        // "ghost-step" shows one Pass and one Fail across two distinct runs in the fixture
        // history — a textbook step-flaky pattern — but it is not a declared step in
        // phantom-step.e2e.yaml (only "current-step" is). An implementation that iterated
        // StepObservations.Keys instead of the declared suite's steps would wrongly emit a
        // step-flaky finding naming it.
        Assert.DoesNotContain(report.Findings, f => IsHistoryHealthKind(f.Kind));
        Assert.DoesNotContain(report.Findings, f => f.StepId == "ghost-step");
    }

    private static void AssertExactlyOne(PlanReportDocument report, string kind, string expectedStepId)
    {
        var finding = Assert.Single(report.Findings, f => f.Kind == kind);
        Assert.Equal(expectedStepId, finding.StepId);
    }
}
