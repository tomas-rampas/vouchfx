// Vouchfx.Engine.Planning.Tests — VocabularyGapAnalyserTests (M3 Planner, REQ-005, REQ-007).
//
// Exercises the whole Planner pipeline (PlannerTestFixtures.Plan) rather than
// VocabularyGapAnalyser.Analyse directly, so these tests also prove PlanPipeline wires the
// analyser correctly and that PlanExport.BuildPlan's public entry point surfaces its
// findings. No event history is supplied: REQ-005 vocabulary gaps are a purely
// declared-vs-catalogue analysis and never consult PlanInputs.History.

using System.Diagnostics.CodeAnalysis;
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
public sealed class VocabularyGapAnalyserTests
{
    [Fact]
    public void KafkaDependency_PublishedButNeverConsumed_YieldsFindingNamingMqExpectKafka()
    {
        var report = PlannerTestFixtures.Plan(PlannerTestFixtures.FixtureRoot("vocabulary/kafka-published-only"));

        var finding = Assert.Single(
            report.Findings,
            f => f.Kind == PlanFindingKinds.DependencyMissingStepType);

        Assert.Equal("events", finding.Target);
        Assert.Equal(PlanTargetKinds.Dependency, finding.TargetKind);
        Assert.Contains("mq-expect.kafka", finding.SuggestedTypes);
        Assert.False(string.IsNullOrEmpty(finding.SuggestedStepId));
        Assert.DoesNotContain(report.Findings, f => f.Kind == PlanFindingKinds.ServiceMissingHttpStep);
    }

    [Fact]
    public void RedisDependency_AssertedViaOneOfTwoCandidates_YieldsNoFinding()
    {
        // F-9 / REQ-005's zero-of-N rule: redis has TWO candidates (cache-assert.redis,
        // mq-expect.redis). This fixture asserts it via cache-assert.redis only — under
        // "any-missing" the dependency would still be flagged for lacking mq-expect.redis,
        // which would be pure noise. The zero-of-N rule requires NONE of the candidates be
        // used before a finding fires, so this fixture must yield zero VOCABULARY findings.
        //
        // No event history is supplied, so CoverageGapAnalyser (T2, now real) legitimately
        // adds its own suite-never-run/step-never-exercised findings for this fixture's
        // lone unrun suite — that is REQ-004 territory, not this REQ-005 test's concern, so
        // the assertion is scoped to the two vocabulary-gap kinds this analyser owns
        // (mirroring FullyCoveredDependencyAndService_YieldNoVocabularyFindings below).
        var report = PlannerTestFixtures.Plan(PlannerTestFixtures.FixtureRoot("vocabulary/redis-partial-coverage"));

        Assert.DoesNotContain(
            report.Findings,
            f => f.Kind == PlanFindingKinds.DependencyMissingStepType
                || f.Kind == PlanFindingKinds.ServiceMissingHttpStep);
    }

    // ── MAJOR fix-round regression pin (REQ-005 amended): evaluation scope is per DECLARING
    // suite, never aggregated by dependency/service name across the analysed set. The
    // existing PlanPrecedenceTests fixture (and every other fixture in this file) is
    // single-suite and structurally cannot detect this class of bug — a step in one suite
    // masking a completely unexercised same-named dependency declared in ANOTHER suite. ────

    [Fact]
    public void TwoSuitesDeclaringTheSameDependencyName_OneCoveredOneNot_AreJudgedIndependently()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("vocabulary/two-suites-same-dependency-name"));

        // Under the OLD name-aggregated implementation, suite-b-covered.e2e.yaml's
        // cache-assert.redis step would make BOTH suites' "cache" dependency read as
        // covered, so this finding would never fire at all — the exact bug this fixture
        // pins down. Suite is now REQUIRED to disambiguate which suite's "cache" is
        // uncovered, since both suites declare a dependency of that name.
        var finding = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.DependencyMissingStepType);
        Assert.Equal("cache", finding.Target);
        Assert.Equal(PlanTargetKinds.Dependency, finding.TargetKind);
        Assert.Equal("suite-a-uncovered.e2e.yaml", finding.Suite);
        Assert.Contains("cache-assert.redis", finding.SuggestedTypes);
        Assert.Contains("mq-expect.redis", finding.SuggestedTypes);
        Assert.False(string.IsNullOrEmpty(finding.SuggestedStepId));

        // suite-b-covered.e2e.yaml's OWN declaration of "cache" must never itself be reported
        // — its cache-assert.redis step genuinely covers it, independently of suite-a's gap.
        Assert.DoesNotContain(
            report.Findings,
            f => f.Kind == PlanFindingKinds.DependencyMissingStepType && f.Suite == "suite-b-covered.e2e.yaml");
    }

    [Fact]
    public void MailpitDependency_WithNoSteps_YieldsFindingNamingMailExpectSmtp()
    {
        // The non-matching-token case REQ-005's acceptance specifically requires: the
        // dependency `type` token "mailpit" does not textually match its asserting
        // provider's dotted type "mail-expect.smtp" at all, proving the mapping is a real
        // table lookup and not string-derivation from the catalogue provider token.
        //
        // No event history is supplied, so this fixture's lone suite also legitimately
        // yields its own suite-never-run finding (REQ-004, CoverageGapAnalyser) alongside
        // the REQ-005 vocabulary gap this test targets — scope to the kind under test
        // rather than assuming it is the report's only finding.
        var report = PlannerTestFixtures.Plan(PlannerTestFixtures.FixtureRoot("vocabulary/mailpit-no-steps"));

        var finding = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.DependencyMissingStepType);
        Assert.Equal("mailbox", finding.Target);
        Assert.Equal(PlanTargetKinds.Dependency, finding.TargetKind);
        Assert.Contains("mail-expect.smtp", finding.SuggestedTypes);
        Assert.False(string.IsNullOrEmpty(finding.SuggestedStepId));
    }

    [Fact]
    public void Service_WithNoHttpStep_YieldsFindingSuggestingHttpRest()
    {
        // No event history is supplied, so this fixture's lone suite also legitimately
        // yields its own suite-never-run and step-never-exercised findings (REQ-004,
        // CoverageGapAnalyser) alongside the REQ-005 vocabulary gap this test targets —
        // scope to the kind under test rather than assuming it is the report's only
        // finding.
        var report = PlannerTestFixtures.Plan(PlannerTestFixtures.FixtureRoot("vocabulary/service-no-http"));

        var finding = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.ServiceMissingHttpStep);
        Assert.Equal("web", finding.Target);
        Assert.Equal(PlanTargetKinds.Service, finding.TargetKind);
        Assert.Equal(new[] { "http.rest" }, finding.SuggestedTypes);
        Assert.False(string.IsNullOrEmpty(finding.SuggestedStepId));
    }

    [Fact]
    public void UnknownDependencyKind_YieldsZeroGapFindingsAndOneUnmappableInventoryEntry()
    {
        // `type: cassandra` parses cleanly (root-language-schema.json does not enum-constrain
        // environment.dependencies.*.type, and neither the parser nor AstBuilder validates
        // the kind), so this is reachable today — REQ-007's case (b). No gap finding may
        // name it (a hint-less finding would violate REQ-007); PlanPipeline's frozen
        // BuildInventory records it as unmappable instead, purely from
        // DependencyKindStepMap.TryGetCandidates returning false for it.
        //
        // No event history is supplied, so this fixture's lone suite (unrelated to the
        // dependency) legitimately yields its own suite-never-run finding (REQ-004,
        // CoverageGapAnalyser) — the REQ-007/REQ-005 guarantee under test is specifically
        // that no finding NAMES the unmappable dependency, not that the report is empty.
        var report = PlannerTestFixtures.Plan(PlannerTestFixtures.FixtureRoot("vocabulary/unknown-kind"));

        Assert.DoesNotContain(report.Findings, f => f.Target == "legacy-cache");

        var unmappable = Assert.Single(report.Inventory.UnmappableDependencies);
        Assert.Equal("legacy-cache", unmappable.Name);
        Assert.Equal("cassandra", unmappable.Type);
        Assert.False(string.IsNullOrEmpty(unmappable.Reason));
    }

    [Fact]
    public void FullyCoveredDependencyAndService_YieldNoVocabularyFindings()
    {
        var report = PlannerTestFixtures.Plan(PlannerTestFixtures.FixtureRoot("vocabulary/fully-covered"));

        Assert.DoesNotContain(report.Findings, f =>
            f.Kind == PlanFindingKinds.DependencyMissingStepType
            || f.Kind == PlanFindingKinds.ServiceMissingHttpStep);
        Assert.Empty(report.Inventory.UnmappableDependencies);
    }
}
