// Vouchfx.Engine.Planning.Tests — HandOffHintTests (M3 Planner, REQ-007).
//
// Two complementary test styles:
//   - whole-pipeline tests over the same vocabulary/** fixtures VocabularyGapAnalyserTests
//     uses, proving every REAL gap finding VocabularyGapAnalyser emits already carries
//     complete, deterministic, YAML-legal hints. These fixtures supply no event history, so
//     CoverageGapAnalyser (now real, not a T1 stub) also legitimately contributes its own
//     suite-never-run/step-never-exercised findings for the same report — the whole-pipeline
//     tests below account for those explicitly rather than assuming the vocabulary gap is
//     the report's only finding;
//   - direct HandOffHints.Enrich unit tests against a hand-built PlanInputs (via the same
//     internal Ingest seam RunCorrelatorTests already exercises), proving the enrichment
//     logic itself for the finding shapes CoverageGapAnalyser (T2) emits un-filled.

using System.Diagnostics.CodeAnalysis;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Planning.Analysis;
using Vouchfx.Engine.Planning.Ingest;
using Vouchfx.Engine.Planning.Report;
using Xunit;

namespace Vouchfx.Engine.Planning.Tests;

// Type-scoped suppressions (not a shared GlobalSuppressions.cs — see PlanExportTests.cs /
// PlanReportContractFreezeTests.cs, which establish the same pattern for this project).
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "xUnit test methods use Given_When_Then underscore convention.")]
[SuppressMessage(
    "Performance",
    "CA1861:Avoid constant arrays as arguments",
    Justification = "Inline literal arrays are the readable form for test assertions.")]
public sealed class HandOffHintTests
{
    // ── Whole-pipeline: every real gap finding already carries complete hints ───

    [Theory]
    [InlineData("vocabulary/kafka-published-only")]
    [InlineData("vocabulary/mailpit-no-steps")]
    [InlineData("vocabulary/service-no-http")]
    public void EveryGapFinding_InARealReport_HasNonEmptySuggestedTypesIdAndTarget(string fixtureRelativePath)
    {
        // None of these three fixtures supplies an event history, so — now that
        // CoverageGapAnalyser is real, not a T1 stub — every one of them ALSO legitimately
        // yields its own suite-never-run finding (its lone suite never appears in an absent
        // history) alongside the REQ-005 vocabulary gap the fixture was built to exercise.
        // suite-never-run is REQ-007's first documented hint exemption (no single
        // service/dependency/step a scaffold call could act on) and is asserted separately
        // below, not folded into the blanket per-finding loop.
        var registry = PlannerTestFixtures.BuildCoreRegistry();
        var report = PlannerTestFixtures.Plan(PlannerTestFixtures.FixtureRoot(fixtureRelativePath));

        var gapFindings = report.Findings.Where(f => PlanFindingKinds.IsGap(f.Kind)).ToList();
        Assert.NotEmpty(gapFindings);

        foreach (var finding in gapFindings)
        {
            if (finding.Kind == PlanFindingKinds.SuiteNeverRun)
            {
                Assert.Empty(finding.SuggestedTypes);
                Assert.Null(finding.SuggestedStepId);
                continue;
            }

            Assert.NotEmpty(finding.SuggestedTypes);
            foreach (var type in finding.SuggestedTypes)
            {
                Assert.True(registry.TryGet(type, out _), $"Suggested type '{type}' is not present in the current registration.");
            }

            Assert.False(string.IsNullOrEmpty(finding.SuggestedStepId));
            Assert.Matches("^[A-Za-z_][A-Za-z0-9_-]*$", finding.SuggestedStepId);

            // BLOCKER fix-round: step-never-exercised now carries Target/TargetKind
            // whenever the step's OWN declared `target` resolves and classifies against its
            // suite's environment (CoverageGapAnalyser.AnalyseStepsNeverExercised) — a host
            // going gap -> scaffold must be able to bind the step back to the seam it
            // addressed, exactly like every other gap kind. The exemption below is therefore
            // narrowed from "kind is step-never-exercised" to "this step declares no
            // resolvable, classifiable target at all" (e.g. "run-script" in
            // vocabulary/service-no-http, a script.csharp step with no `target` field) —
            // never merely because of its kind (see Enrich_StepScopedFindingWithNoTarget_
            // SuggestsTheStepsOwnType below, and PlanFinding.Target's own doc, which gives
            // suite-never-run only as an EXAMPLE null-Target case, not an exhaustive list).
            // Every other gap kind here is target-scoped and must always carry the target
            // name.
            if (finding.Kind == PlanFindingKinds.StepNeverExercised && finding.Target is null)
            {
                continue;
            }

            Assert.NotNull(finding.Target);
        }
    }

    // ── MAJOR fix-round: REQ-007's SECOND documented hint exemption — a
    // dependency-not-asserted finding whose dependency kind has no REQ-005 candidate type at
    // all (an unmappable kind) must stay un-enriched rather than fabricate a suggestion, yet
    // remains a genuine gap for --fail-on-gap purposes. Previously covered only for the
    // vocabulary-gap side (VocabularyGapAnalyserTests.UnknownDependencyKind_...), never for a
    // dependency that also has a targeting, non-asserting step. ───────────────────────────

    [Fact]
    public void DependencyNotAssertedFinding_ForAnUnmappableDependencyKind_StaysUnenrichedButRemainsAGap()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("vocabulary/unknown-kind-targeted"));

        var finding = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.DependencyNotAsserted);

        Assert.Equal("legacy-cache", finding.Target);
        Assert.Equal(PlanTargetKinds.Dependency, finding.TargetKind);
        Assert.Empty(finding.SuggestedTypes);
        Assert.Null(finding.SuggestedStepId);

        // Still a genuine gap (REQ-007: "both remain gaps for --fail-on-gap purposes; they
        // are exempt only from the hint requirement") — checked directly, never re-derived.
        Assert.True(PlanFindingKinds.IsGap(finding.Kind));

        // REQ-005's own vocabulary-gap analyser must not ALSO report this dependency: its
        // kind has no candidate at all, so PlanPipeline.BuildInventory records it as
        // unmappable instead (REQ-007), never as a second, equally hint-less finding.
        Assert.DoesNotContain(
            report.Findings,
            f => f.Kind == PlanFindingKinds.DependencyMissingStepType && f.Target == "legacy-cache");
        var unmappable = Assert.Single(report.Inventory.UnmappableDependencies, d => d.Name == "legacy-cache");
        Assert.Equal("cassandra", unmappable.Type);
    }

    [Fact]
    public void SuggestedStepIds_AreDeterministic_AcrossTwoIndependentAnalyses()
    {
        // No event history is supplied, so CoverageGapAnalyser (now real) also yields its
        // own suite-never-run/step-never-exercised/dependency-not-asserted findings for
        // this fixture's lone suite — scope the determinism check to the specific REQ-005
        // vocabulary-gap finding this fixture was built to exercise, matching
        // VocabularyGapAnalyserTests.KafkaDependency_PublishedButNeverConsumed_
        // YieldsFindingNamingMqExpectKafka's own scoping of the identical fixture.
        var suiteDir = PlannerTestFixtures.FixtureRoot("vocabulary/kafka-published-only");

        var first = PlannerTestFixtures.Plan(suiteDir);
        var second = PlannerTestFixtures.Plan(suiteDir);

        var firstFinding = Assert.Single(
            first.Findings, f => f.Kind == PlanFindingKinds.DependencyMissingStepType);
        var secondFinding = Assert.Single(
            second.Findings, f => f.Kind == PlanFindingKinds.DependencyMissingStepType);

        Assert.Equal(firstFinding.SuggestedStepId, secondFinding.SuggestedStepId);
        Assert.Equal(firstFinding.SuggestedTypes, secondFinding.SuggestedTypes);
    }

    // ── Direct HandOffHints.Enrich unit tests ────────────────────────────────────

    [Fact]
    public void Enrich_DependencyScopedFindingLackingHints_FillsCandidatesFromTheMap()
    {
        var inputs = BuildInputs("vocabulary/kafka-published-only");
        var unfilled = UnfilledFinding(
            PlanFindingKinds.DependencyNotAsserted,
            suite: "suite.e2e.yaml",
            stepId: null,
            target: "events",
            targetKind: PlanTargetKinds.Dependency);

        var enriched = Assert.Single(HandOffHints.Enrich(new[] { unfilled }, inputs));

        Assert.Equal(new[] { "mq-expect.kafka" }, enriched.SuggestedTypes);
        Assert.Equal("assert-events", enriched.SuggestedStepId);
    }

    [Fact]
    public void Enrich_ServiceScopedFindingLackingHints_SuggestsHttpRest()
    {
        var inputs = BuildInputs("vocabulary/service-no-http");
        var unfilled = UnfilledFinding(
            PlanFindingKinds.ServiceMissingHttpStep,
            suite: null,
            stepId: null,
            target: "web",
            targetKind: PlanTargetKinds.Service);

        var enriched = Assert.Single(HandOffHints.Enrich(new[] { unfilled }, inputs));

        Assert.Equal(new[] { "http.rest" }, enriched.SuggestedTypes);
        Assert.Equal("assert-web", enriched.SuggestedStepId);
    }

    [Fact]
    public void Enrich_StepScopedFindingWithNoTarget_SuggestsTheStepsOwnType()
    {
        // step-never-exercised (T2's kind): the step already exists with a known,
        // registered type — the suggestion is to run THAT step, not to scaffold a new one.
        var inputs = BuildInputs("vocabulary/fully-covered");
        var unfilled = UnfilledFinding(
            PlanFindingKinds.StepNeverExercised,
            suite: "suite.e2e.yaml",
            stepId: "create-order",
            target: null,
            targetKind: null);

        var enriched = Assert.Single(HandOffHints.Enrich(new[] { unfilled }, inputs));

        Assert.Equal(new[] { "http.rest" }, enriched.SuggestedTypes);
        Assert.Equal("create-order", enriched.SuggestedStepId);
    }

    [Fact]
    public void Enrich_SuiteScopedFindingWithNoTargetOrStep_IsLeftUnenriched()
    {
        // suite-never-run: no single service/dependency/step a scaffold call could act on.
        var inputs = BuildInputs("vocabulary/fully-covered");
        var unfilled = UnfilledFinding(
            PlanFindingKinds.SuiteNeverRun,
            suite: "suite.e2e.yaml",
            stepId: null,
            target: null,
            targetKind: null);

        var enriched = Assert.Single(HandOffHints.Enrich(new[] { unfilled }, inputs));

        Assert.Empty(enriched.SuggestedTypes);
        Assert.Null(enriched.SuggestedStepId);
    }

    [Fact]
    public void Enrich_HistoryHealthFinding_IsPermittedToOmitHints()
    {
        // REQ-007 explicitly permits history-health findings to omit hints: they are
        // outside PlanFindingKinds.IsGap by design, so Enrich must never touch them even
        // though this one happens to carry a StepId.
        var inputs = BuildInputs("vocabulary/fully-covered");
        var unfilled = UnfilledFinding(
            PlanFindingKinds.StepStale,
            suite: "suite.e2e.yaml",
            stepId: "create-order",
            target: null,
            targetKind: null,
            history: new PlanStepHistory(1, 0, 0, 0, 1, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 45));

        var enriched = Assert.Single(HandOffHints.Enrich(new[] { unfilled }, inputs));

        Assert.Empty(enriched.SuggestedTypes);
        Assert.Null(enriched.SuggestedStepId);
    }

    [Fact]
    public void Enrich_FindingThatAlreadyCarriesHints_IsReturnedUnchanged()
    {
        var inputs = BuildInputs("vocabulary/service-no-http");
        var alreadyFilled = new PlanFinding(
            Kind: PlanFindingKinds.ServiceMissingHttpStep,
            Suite: null,
            StepId: null,
            Target: "web",
            TargetKind: PlanTargetKinds.Service,
            SuggestedTypes: new[] { "http.rest" },
            SuggestedStepId: "assert-web",
            Ambiguous: false,
            AmbiguityReason: null,
            History: null,
            Detail: "already enriched",
            RelatedSuites: Array.Empty<string>());

        var enriched = Assert.Single(HandOffHints.Enrich(new[] { alreadyFilled }, inputs));

        Assert.Same(alreadyFilled, enriched);
    }

    [Theory]
    [InlineData("events")]
    [InlineData("orders-db")]
    [InlineData("Weird Name!With.Chars")]
    public void BuildSuggestedStepId_IsDeterministicAndYamlLegal(string targetName)
    {
        var first = HandOffHints.BuildSuggestedStepId(targetName);
        var second = HandOffHints.BuildSuggestedStepId(targetName);

        Assert.Equal(first, second);
        Assert.Matches("^[A-Za-z_][A-Za-z0-9_-]*$", first);
    }

    // ── Test helpers ──────────────────────────────────────────────────────────────

    private static PlanFinding UnfilledFinding(
        string kind,
        string? suite,
        string? stepId,
        string? target,
        string? targetKind,
        PlanStepHistory? history = null) =>
        new(
            Kind: kind,
            Suite: suite,
            StepId: stepId,
            Target: target,
            TargetKind: targetKind,
            SuggestedTypes: Array.Empty<string>(),
            SuggestedStepId: null,
            Ambiguous: false,
            AmbiguityReason: null,
            History: history,
            Detail: "test fixture finding",
            RelatedSuites: Array.Empty<string>());

    /// <summary>
    /// Assembles a real <see cref="PlanInputs"/> from a fixture suite directory, using the
    /// same internal Ingest seam <c>RunCorrelatorTests</c> already exercises directly
    /// (<see cref="SuiteSetLoader"/>, <see cref="EventHistoryReader"/>,
    /// <see cref="RunCorrelator"/>) plus <see cref="EngineExport.BuildCatalogue"/> — the exact
    /// sequence <c>PlanExport.BuildPlan</c> itself runs. No event history: every fixture this
    /// file uses is analysed with an absent history (REQ-009), which is irrelevant here since
    /// <see cref="HandOffHints.Enrich"/> never reads <see cref="PlanInputs.History"/>.
    /// </summary>
    private static PlanInputs BuildInputs(string fixtureRelativePath)
    {
        var registry = PlannerTestFixtures.BuildCoreRegistry();
        var suiteDir = PlannerTestFixtures.FixtureRoot(fixtureRelativePath);
        var loadResult = SuiteSetLoader.Load(suiteDir, registry);
        var catalogue = EngineExport.BuildCatalogue(registry);
        var history = RunCorrelator.Correlate(loadResult.Suites, loadResult.AnalysedRoot, EventHistoryReader.Read(null));

        return new PlanInputs(
            Registry: registry,
            Catalogue: catalogue,
            Thresholds: PlanThresholds.Defaults,
            AnalysedRoot: loadResult.AnalysedRoot,
            Suites: loadResult.Suites,
            UnanalysableSuites: loadResult.UnanalysableSuites,
            History: history);
    }
}
