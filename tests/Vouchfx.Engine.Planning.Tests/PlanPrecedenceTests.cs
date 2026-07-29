// Vouchfx.Engine.Planning.Tests — PlanPrecedenceTests (M3 Planner, REQ-004/REQ-005
// precedence table, T6 scope).
//
// The one cross-analyser proof that deliberately lives in NEITHER CoverageGapAnalyserTests
// (T2) NOR VocabularyGapAnalyserTests (T3): the spec's corrected precedence table (flag F-3
// in the T0 architecture note) says a ZERO-step dependency is reported exactly ONCE, total,
// across the two analysers (REQ-005 only), while a dependency with SOME non-asserting
// coverage is reported TWICE (REQ-004's dependency-not-asserted AND REQ-005's
// dependency-missing-step-type, independently). Either analyser's own test file can be green
// in isolation while the PAIR is wrong — e.g. a change that made CoverageGapAnalyser ALSO
// fire on a zero-step dependency (double-reporting the zero-step row), or that made
// VocabularyGapAnalyser stop firing on the row-2 case (silently losing the "you already ship
// mq-expect.kafka" hint) — so this file asserts the TOTAL finding count across both kinds,
// not just "analyser X's own kind is present/absent" the way CoverageGapAnalyserTests.
// ZeroStepDependency_NeverProducesDependencyNotAsserted and
// VocabularyGapAnalyserTests.KafkaDependency_PublishedButNeverConsumed_... do individually.
//
// Fixture (Fixtures/acceptance/precedence/suite.e2e.yaml + …-events/history.jsonl): one suite
// declares two kafka dependencies —
//   - "unused-queue": ZERO declared steps target it (the precedence rule's first row).
//   - "events": targeted by exactly one step, "publish-order-event"
//     (mq-publish.kafka — a NON-asserting family), and by no OTHER step (the precedence
//     rule's second row: "≥1, none of an asserting family").
// REQ-004/REQ-005 precedence itself does not depend on history at all (VocabularyGapAnalyser
// never reads it, and CoverageGapAnalyser's dependency-not-asserted rule only reads declared
// steps) — but the fixture's OWN event history exercises "publish-order-event" (a matching
// step-completed event) precisely so that step does NOT also earn its own
// step-never-exercised finding naming "events" as its resolved target: without that history,
// an absent-history run would additionally attribute a THIRD, unrelated finding to Target ==
// "events" (step-never-exercised, from the step's own declared `target`), which would make
// this file's "exactly two" assertion fail for a reason that has nothing to do with the
// REQ-004/REQ-005 precedence rule under test.

using System.Diagnostics.CodeAnalysis;
using Vouchfx.Engine.Planning.Report;
using Xunit;

namespace Vouchfx.Engine.Planning.Tests;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "xUnit test methods use Given_When_Then underscore convention.")]
public sealed class PlanPrecedenceTests
{
    private static PlanReportDocument Report() =>
        PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("acceptance/precedence"),
            PlannerTestFixtures.FixtureRoot("acceptance/precedence-events"));

    // ── Precedence table row 1: a ZERO-step dependency is reported exactly ONCE, total,
    // across REQ-004 + REQ-005 (as a REQ-005 vocabulary gap, never as REQ-004's
    // dependency-not-asserted). ──────────────────────────────────────────────────────────

    [Fact]
    public void ZeroStepDependency_YieldsExactlyOneFindingTotalAcrossBothAnalysers()
    {
        var report = Report();

        var findingsForUnusedQueue = report.Findings.Where(f => f.Target == "unused-queue").ToList();

        var finding = Assert.Single(findingsForUnusedQueue);
        Assert.Equal(PlanFindingKinds.DependencyMissingStepType, finding.Kind);
        Assert.Equal(PlanTargetKinds.Dependency, finding.TargetKind);
        Assert.Contains("mq-expect.kafka", finding.SuggestedTypes);

        // Never also reported as REQ-004's dependency-not-asserted — that is precisely the
        // precedence rule this test exists to lock down.
        Assert.DoesNotContain(
            report.Findings,
            f => f.Kind == PlanFindingKinds.DependencyNotAsserted && f.Target == "unused-queue");
    }

    // ── Precedence table row 2: a PUBLISHED-BUT-NEVER-CONSUMED kafka dependency yields
    // exactly TWO findings — one from each analyser, each carrying its own hint. ───────────

    [Fact]
    public void PublishedButNeverConsumedKafkaDependency_YieldsExactlyTwoFindingsOneOfEachKind()
    {
        var report = Report();

        var findingsForEvents = report.Findings.Where(f => f.Target == "events").ToList();
        Assert.Equal(2, findingsForEvents.Count);

        var dependencyNotAsserted = Assert.Single(
            findingsForEvents, f => f.Kind == PlanFindingKinds.DependencyNotAsserted);
        Assert.Equal(PlanTargetKinds.Dependency, dependencyNotAsserted.TargetKind);
        Assert.Equal("suite.e2e.yaml", dependencyNotAsserted.Suite);

        var dependencyMissingStepType = Assert.Single(
            findingsForEvents, f => f.Kind == PlanFindingKinds.DependencyMissingStepType);
        Assert.Equal(PlanTargetKinds.Dependency, dependencyMissingStepType.TargetKind);
        Assert.Contains("mq-expect.kafka", dependencyMissingStepType.SuggestedTypes);

        // The two findings answer different questions and so must carry independent hints
        // (both non-empty — REQ-007 — even though they name the same target).
        Assert.NotEmpty(dependencyNotAsserted.SuggestedTypes);
        Assert.NotEmpty(dependencyMissingStepType.SuggestedTypes);
    }
}
