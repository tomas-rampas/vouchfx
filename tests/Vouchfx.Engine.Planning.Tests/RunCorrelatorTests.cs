// Vouchfx.Engine.Planning.Tests — RunCorrelatorTests (M3 Planner, fix-round-2 NEW-4).
//
// Exercises Ingest/RunCorrelator.cs directly (InternalsVisibleTo) because the NEW-4 fix has
// no observable effect through the public PlanReportDocument: UnmatchedObservations
// deliberately does NOT distinguish "correctly attributed" from "silently dropped as an
// ambiguity" (see RunCorrelator's own M2 comment) — both read as 0 either way, regardless of
// HistoryHealthAnalyser (now real, not a stub) since neither of those two readings would
// change any of ITS tallies either. The only way to prove a reused runId's step-completed
// event is no longer silently dropped is to inspect PlanEventHistory.StepObservations
// directly.
//
// A real runId can never legitimately appear on two scenario-started events (blueprint
// §16.2: one runId per scenario execution), so this is a fixture-only edge case — but T2 is
// about to author exactly this class of fixture for its own suite-identity-ambiguity tests.

using System.Diagnostics.CodeAnalysis;
using Vouchfx.Engine.Planning.Ingest;
using Xunit;

namespace Vouchfx.Engine.Planning.Tests;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "xUnit test methods use Given_When_Then underscore convention.")]
public sealed class RunCorrelatorTests
{
    [Fact]
    public void RunIdLaterMatchingADeclaredSuite_StepCompletedIsNotSilentlyDropped()
    {
        var registry = PlannerTestFixtures.BuildCoreRegistry();
        var suiteRoot = PlannerTestFixtures.FixtureRoot("ingest/basic-suites");
        var loadResult = SuiteSetLoader.Load(suiteRoot, registry);
        var rawHistory = EventHistoryReader.Read(PlannerTestFixtures.FixtureRoot("ingest/reused-runid-events"));

        // The fixture's runId "run-shared" appears on two scenario-started events, in this
        // order: (1) scenarioId "renamed-elsewhere" with a missing `file` — a rule-2
        // ambiguity; (2) scenarioId "checkout-flow" — a genuine declared match. Per the
        // documented "last-processed-event-per-runId wins" semantics, the declared match
        // (processed second) must win: the step-completed event sharing that runId is
        // attributed to (checkout-flow, create-order), not silently dropped.
        var history = RunCorrelator.Correlate(loadResult.Suites, loadResult.AnalysedRoot, rawHistory);

        Assert.True(
            history.StepObservations.ContainsKey(("checkout-flow", "create-order")),
            "The step-completed event sharing the reused runId was silently dropped instead of being attributed to its later declared match.");
    }
}
