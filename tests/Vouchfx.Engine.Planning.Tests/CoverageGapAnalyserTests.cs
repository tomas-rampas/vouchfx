// Vouchfx.Engine.Planning.Tests — CoverageGapAnalyserTests (M3 Planner, REQ-004, T2 scope).
//
// Exercises Analysis/CoverageGapAnalyser.cs through the public PlanExport.BuildPlan entry
// point (via PlannerTestFixtures.Plan), the same way PlanExportTests.cs exercises T1's scope.
// VocabularyGapAnalyser, HistoryHealthAnalyser and HandOffHints.Enrich are all real now that
// every workstream has landed, so a fixture can legitimately surface OTHER analysers'
// findings alongside CoverageGapAnalyser's own (e.g. a zero-step or row-2-double-report
// dependency also earns a REQ-005 vocabulary-gap finding). Assertions below are therefore
// scoped to the specific finding kinds CoverageGapAnalyser itself owns (REQ-004:
// suite-never-run, step-never-exercised, dependency-not-asserted) rather than assuming the
// whole report — VocabularyGapAnalyser's and HistoryHealthAnalyser's OWN correctness remain
// their own test files' concern; the cross-analyser PRECEDENCE assertion (that a zero-step
// dependency is reported by exactly one analyser, not both) belongs to a later todo's
// PlanPrecedenceTests.cs.

using System.Diagnostics.CodeAnalysis;
using Vouchfx.Engine.Planning.Report;
using Xunit;

namespace Vouchfx.Engine.Planning.Tests;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "xUnit test methods use Given_When_Then underscore convention.")]
public sealed class CoverageGapAnalyserTests
{
    private static readonly string[] HttpRestTypes = new[] { "http.rest" };
    private static readonly string[] MqExpectKafkaTypes = new[] { "mq-expect.kafka" };

    // ── REQ-004: exact expected finding set, naming suite/step/dependency ────────

    [Fact]
    public void MixedFixture_ProducesExactlyTheExpectedThreeKindsOfFinding()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("coverage/mixed-gaps"),
            PlannerTestFixtures.FixtureRoot("coverage/mixed-gaps-events"));

        // suite-never-run: "shipping.e2e.yaml" never appears in the history at all. REQ-007's
        // documented hint exemption: no single service/dependency/step a scaffold call could
        // act on, so it stays un-enriched.
        var suiteNeverRun = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.SuiteNeverRun);
        Assert.Equal("shipping.e2e.yaml", suiteNeverRun.Suite);
        Assert.False(suiteNeverRun.Ambiguous);
        Assert.Null(suiteNeverRun.AmbiguityReason);
        Assert.Empty(suiteNeverRun.SuggestedTypes);
        Assert.Null(suiteNeverRun.SuggestedStepId);

        // step-never-exercised: "checkout.e2e.yaml"'s never-called-step has no observation —
        // HandOffHints.Enrich (T3, now real) fills it from the step's OWN already-registered
        // type/id, since re-running an existing step is the remedy, not scaffolding a new one.
        var checkoutNeverExercised = Assert.Single(
            report.Findings,
            f => f.Kind == PlanFindingKinds.StepNeverExercised && f.Suite == "checkout.e2e.yaml");
        Assert.Equal("never-called-step", checkoutNeverExercised.StepId);
        Assert.Equal(HttpRestTypes, checkoutNeverExercised.SuggestedTypes);
        Assert.Equal("never-called-step", checkoutNeverExercised.SuggestedStepId);
        // BLOCKER fix-round: "never-called-step" declares `target: api`, and checkout.e2e.yaml
        // DOES declare `environment.services.api` — the target resolves AND classifies, so
        // this finding must name it, letting a host bind the scaffolded step back to the
        // service the original step addressed (REQ-007's whole reason for existing).
        Assert.Equal("api", checkoutNeverExercised.Target);
        Assert.Equal(PlanTargetKinds.Service, checkoutNeverExercised.TargetKind);

        // ...and "shipping.e2e.yaml"'s only step is ALSO never-exercised (a suite that never
        // ran reports every one of its steps as never-exercised too — both findings coexist).
        var shippingNeverExercised = Assert.Single(
            report.Findings,
            f => f.Kind == PlanFindingKinds.StepNeverExercised && f.Suite == "shipping.e2e.yaml");
        Assert.Equal("ship-order", shippingNeverExercised.StepId);
        Assert.Equal(HttpRestTypes, shippingNeverExercised.SuggestedTypes);
        Assert.Equal("ship-order", shippingNeverExercised.SuggestedStepId);
        // BLOCKER fix-round: "ship-order" ALSO declares `target: api`, but shipping.e2e.yaml
        // declares NO `environment` block at all — the target cannot classify against
        // anything, so Target/TargetKind correctly stay null (never a fabricated binding to
        // a service this suite never even declares).
        Assert.Null(shippingNeverExercised.Target);
        Assert.Null(shippingNeverExercised.TargetKind);

        // dependency-not-asserted: "events" (kafka) is targeted only by mq-publish.kafka, a
        // non-asserting family — "orders-db" (postgres) is asserted by db-assert.postgres and
        // must NOT be reported. HandOffHints.Enrich fills this one from the kafka mapping.
        var dependencyNotAsserted = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.DependencyNotAsserted);
        Assert.Equal("checkout.e2e.yaml", dependencyNotAsserted.Suite);
        Assert.Equal("events", dependencyNotAsserted.Target);
        Assert.Equal(PlanTargetKinds.Dependency, dependencyNotAsserted.TargetKind);
        Assert.Equal(MqExpectKafkaTypes, dependencyNotAsserted.SuggestedTypes);
        Assert.Equal("assert-events", dependencyNotAsserted.SuggestedStepId);
        Assert.DoesNotContain(
            report.Findings,
            f => f.Kind == PlanFindingKinds.DependencyNotAsserted && f.Target == "orders-db");

        // REQ-004/REQ-005 precedence row 2 ("≥1 declared step, none asserting") deliberately
        // yields TWO findings for the same dependency (per the spec's precedence table): the
        // dependency-not-asserted finding above from CoverageGapAnalyser, and this
        // independent REQ-005 vocabulary-gap finding from VocabularyGapAnalyser (T3, now
        // real) — "events" also has zero of its candidate types (mq-expect.kafka) used
        // against it. Asserting its presence keeps this test's "exactly the expected
        // finding set" promise honest now that every analyser is real; VocabularyGapAnalyser's
        // OWN correctness remains VocabularyGapAnalyserTests's concern (see file header).
        var vocabularyGap = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.DependencyMissingStepType);
        Assert.Equal("events", vocabularyGap.Target);
        Assert.Equal(PlanTargetKinds.Dependency, vocabularyGap.TargetKind);
        Assert.Equal(MqExpectKafkaTypes, vocabularyGap.SuggestedTypes);
        Assert.Equal("assert-events", vocabularyGap.SuggestedStepId);

        // Exactly these five findings — nothing else (no collisions/renamed-file entries in
        // this fixture, and the single Pass-only run is nowhere near any REQ-006 threshold).
        Assert.Equal(5, report.Findings.Count);
    }

    // ── REQ-004 acceptance: a fully-exercised fixture produces none of the three kinds ──

    [Fact]
    public void FullyExercisedFixture_ProducesNoCoverageGapFindings()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("coverage/fully-exercised"),
            PlannerTestFixtures.FixtureRoot("coverage/fully-exercised-events"));

        Assert.Empty(report.Findings);
    }

    // ── REQ-004/REQ-005 precedence (T2's half): a zero-step dependency is NEVER reported
    // here — only VocabularyGapAnalyser (T3) reports it, exactly once. ─────────────────

    [Fact]
    public void ZeroStepDependency_NeverProducesDependencyNotAsserted()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("coverage/zero-step-dependency"),
            PlannerTestFixtures.FixtureRoot("coverage/zero-step-dependency-events"));

        // "cache" (redis) has zero declared steps targeting it — the precedence rule reserves
        // it exclusively for REQ-005 (T3), so REQ-004 must never name it.
        Assert.DoesNotContain(
            report.Findings,
            f => f.Kind == PlanFindingKinds.DependencyNotAsserted && f.Target == "cache");

        // MAJOR fix-round: the spec's LITERAL clause — "a zero-step dependency yields exactly
        // one finding TOTAL ACROSS REQ-004 AND REQ-005" — needs a count over BOTH analysers'
        // output naming "cache", not just proof that REQ-004's own kind is absent (the
        // assertion above). Redis has two REQ-005 candidates (cache-assert.redis,
        // mq-expect.redis); zero are used, so VocabularyGapAnalyser's zero-of-N rule fires
        // exactly once for it.
        Assert.Equal(1, report.Findings.Count(f => f.Target == "cache"));

        // "queue" (kafka) DOES have a declared step targeting it (mq-publish.kafka, a
        // non-asserting family) — this is the "≥1, none asserting" row, which REQ-004 DOES
        // own, proving the fixture is not simply empty by coincidence.
        var queueFinding = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.DependencyNotAsserted);
        Assert.Equal("queue", queueFinding.Target);

        // The suite ran and every declared step was exercised, so no other COVERAGE
        // finding (REQ-004's three kinds, this analyser's own scope) should appear
        // alongside it. "cache" (zero-step) and "queue" (row-2 double-report) both ALSO
        // surface as REQ-005 vocabulary-gap findings via VocabularyGapAnalyser (T3, now
        // real) — that analyser's correctness is VocabularyGapAnalyserTests's concern (see
        // this file's header comment), not this coverage-only assertion's.
        Assert.Single(
            report.Findings,
            f => f.Kind == PlanFindingKinds.SuiteNeverRun
                || f.Kind == PlanFindingKinds.StepNeverExercised
                || f.Kind == PlanFindingKinds.DependencyNotAsserted);
    }
}
