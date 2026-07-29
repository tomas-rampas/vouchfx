// Vouchfx.Engine.Planning.Tests — PlanExportTests (M3 Planner, T1 scope).
//
// Covers: REQ-001 (public entry point), REQ-003 (inventory), REQ-009 (events file/dir/
// omitted), EDGE-001 (empty history), EDGE-003 (one malformed suite, and all malformed),
// EDGE-004 (corrupt event lines), EDGE-007 (foreign scenario observation), EDGE-009 (empty
// suite folder).
//
// T1 SCOPE NOTE: CoverageGapAnalyser / VocabularyGapAnalyser / HistoryHealthAnalyser are
// still T1 stubs (they always return no findings) — a later todo (T2/T3/T4) fills them in.
// Every assertion below is therefore scoped to what T1 actually computes: successful/failed
// BuildPlan outcomes and the REQ-003 inventory section (suites, services, dependencies, step
// types, run count, timestamps, skipped/unmatched counts, unanalysable suites). Acceptance
// language that depends on FINDING content (e.g. REQ-009's "every declared step reported
// never-run") is intentionally NOT asserted here — it is exercised once T2 lands, in T6's
// acceptance suite.

using System.Diagnostics.CodeAnalysis;
using Vouchfx.Engine.Planning.Report;
using Xunit;

namespace Vouchfx.Engine.Planning.Tests;

// Type-scoped suppressions (not a shared GlobalSuppressions.cs — see the T0 architecture
// note: a shared suppression file would be a three-way contention point for T2/T3/T4's own
// test files). Mirrors the wording of the sibling test projects' GlobalSuppressions.cs.
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "xUnit test methods use Given_When_Then underscore convention.")]
[SuppressMessage(
    "Performance",
    "CA1861:Avoid constant arrays as arguments",
    Justification = "Inline literal arrays are the readable form for test assertions.")]
public sealed class PlanExportTests
{
    // ── REQ-001 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildPlan_WithFixtureSuiteAndEvents_ReturnsSchemaVersionedReport()
    {
        var registry = PlannerTestFixtures.BuildCoreRegistry();
        var request = new PlanRequest(
            PlannerTestFixtures.FixtureRoot("ingest/basic-suites"),
            PlannerTestFixtures.FixtureRoot("ingest/basic-suites-events"));

        var report = PlanExport.BuildPlan(request, registry, engineVersion: "1.2.3-test");

        Assert.NotNull(report);
        Assert.Equal(PlanExport.PlanSchemaVersion, report.SchemaVersion);
        Assert.Equal("1.2.3-test", report.EngineVersion);
        Assert.NotNull(report.Inventory);
        Assert.NotNull(report.Findings);
    }

    [Fact]
    public void SerializePlan_ProducesParsableJson()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("ingest/basic-suites"),
            PlannerTestFixtures.FixtureRoot("ingest/basic-suites-events"));

        var json = PlanExport.SerializePlan(report);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("schemaVersion", out _));
        Assert.True(doc.RootElement.TryGetProperty("inventory", out _));
        Assert.True(doc.RootElement.TryGetProperty("findings", out _));
    }

    // ── REQ-003 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Inventory_ReflectsDeclaredSuitesServicesDependenciesAndHistory()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("ingest/basic-suites"),
            PlannerTestFixtures.FixtureRoot("ingest/basic-suites-events"));

        var inventory = report.Inventory;

        Assert.Equal(2, inventory.Suites.Count);
        var checkout = Assert.Single(inventory.Suites, s => s.Path == "checkout.e2e.yaml");
        Assert.Equal("checkout-flow", checkout.ScenarioId);
        Assert.Equal("checkout-flow", checkout.Name);
        Assert.Equal(2, checkout.StepCount);

        var refund = Assert.Single(inventory.Suites, s => s.Path == "refund.e2e.yaml");
        Assert.Equal("refund", refund.ScenarioId);
        Assert.Null(refund.Name);
        Assert.Equal(1, refund.StepCount);

        Assert.Equal(new[] { "api" }, inventory.Services);

        Assert.Equal(2, inventory.Dependencies.Count);
        Assert.Contains(inventory.Dependencies, d => d.Name == "orders-db" && d.Type == "postgres");
        Assert.Contains(inventory.Dependencies, d => d.Name == "events" && d.Type == "kafka");

        Assert.Equal(
            new[] { "db-assert.postgres", "http.rest", "mq-publish.kafka" },
            inventory.StepTypes);

        Assert.Equal(3, inventory.RunCount);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), inventory.FirstEventTs);
        Assert.Equal(new DateTimeOffset(2026, 1, 3, 0, 5, 0, TimeSpan.Zero), inventory.LastEventTs);
        Assert.Equal(0, inventory.SkippedEventLines);
        Assert.Equal(0, inventory.UnmatchedObservations);
        Assert.Empty(inventory.UnanalysableSuites);
    }

    // ── REQ-009 ──────────────────────────────────────────────────────────────────

    [Fact]
    public void EventsPath_AsDirectory_Succeeds()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("ingest/basic-suites"),
            PlannerTestFixtures.FixtureRoot("ingest/basic-suites-events"));

        Assert.NotNull(report);
        Assert.Equal(3, report.Inventory.RunCount);
    }

    [Fact]
    public void EventsPath_AsSingleFile_Succeeds()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("ingest/basic-suites"),
            Path.Combine(PlannerTestFixtures.FixtureRoot("ingest/basic-suites-events"), "history.jsonl"));

        Assert.NotNull(report);
        Assert.Equal(3, report.Inventory.RunCount);
    }

    [Fact]
    public void EventsPath_Omitted_Succeeds()
    {
        var report = PlannerTestFixtures.Plan(PlannerTestFixtures.FixtureRoot("ingest/basic-suites"));

        Assert.NotNull(report);
        Assert.Equal(0, report.Inventory.RunCount);
    }

    [Fact]
    public void EventsPath_AsDirectoryOfTwoRunFiles_ComputesAcrossBoth()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("ingest/basic-suites"),
            PlannerTestFixtures.FixtureRoot("ingest/two-run-files"));

        Assert.Equal(2, report.Inventory.RunCount);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), report.Inventory.FirstEventTs);
        Assert.Equal(new DateTimeOffset(2026, 2, 2, 0, 5, 0, TimeSpan.Zero), report.Inventory.LastEventTs);
    }

    // ── EDGE-001 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyOrAbsentHistory_IsSuccessfulWithZeroRunCountAndNoHistoryHealthFindings()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("ingest/basic-suites"),
            eventsPath: null);

        Assert.Equal(0, report.Inventory.RunCount);
        Assert.Null(report.Inventory.FirstEventTs);
        Assert.Null(report.Inventory.LastEventTs);
        Assert.DoesNotContain(
            report.Findings,
            f => f.Kind is PlanFindingKinds.StepStale or PlanFindingKinds.StepFlaky
                or PlanFindingKinds.StepFragile or PlanFindingKinds.StepInconclusiveProne);
    }

    // ── EDGE-003 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void OneMalformedSuite_IsRecordedAndRemainingSuiteStillAnalysed()
    {
        var report = PlannerTestFixtures.Plan(PlannerTestFixtures.FixtureRoot("ingest/one-malformed"));

        Assert.Single(report.Inventory.Suites);
        Assert.Equal("good-suite", report.Inventory.Suites[0].ScenarioId);

        var unanalysable = Assert.Single(report.Inventory.UnanalysableSuites);
        Assert.Equal("bad.e2e.yaml", unanalysable.Path);
        Assert.False(string.IsNullOrWhiteSpace(unanalysable.Error));
    }

    [Fact]
    public void EverySuiteMalformed_YieldsZeroAnalysableSuitesAndParseErrorCountEqualsFileCount()
    {
        var report = PlannerTestFixtures.Plan(PlannerTestFixtures.FixtureRoot("ingest/all-malformed"));

        Assert.Empty(report.Inventory.Suites);
        Assert.Equal(2, report.Inventory.UnanalysableSuites.Count);
    }

    // ── EDGE-004 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CorruptEventLines_AreSkippedAndCounted()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("ingest/basic-suites"),
            PlannerTestFixtures.FixtureRoot("ingest/corrupt-events"));

        Assert.Equal(2, report.Inventory.SkippedEventLines);
    }

    // ── EDGE-007 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ForeignScenarioObservation_ProducesNoFindingNamingItAndDoesNotCrash()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("ingest/basic-suites"),
            PlannerTestFixtures.FixtureRoot("ingest/foreign-scenario-events"));

        Assert.DoesNotContain(report.Findings, f =>
            f.Suite == "unrelated-suite" || f.Detail.Contains("unrelated-suite", StringComparison.Ordinal));
        Assert.True(report.Inventory.UnmatchedObservations > 0);
    }

    // ── M2 regression (fix-round) ───────────────────────────────────────────────
    // A scenario-started event carrying a `file` that resolves under the analysed root but
    // no longer exists on disk is a REQ-008 rule-2 renamed-file ambiguity. Both PlanInputs.cs
    // and the frozen PlanReportDocument.cs XML docs say UnmatchedObservations excludes
    // observations already classified as a suite-identity ambiguity — before the fix, a
    // step-completed event sharing that same runId was double-counted as unmatched anyway.

    [Fact]
    public void RenamedFileAmbiguity_StepCompletedSharingItsRunId_IsNotCountedAsUnmatched()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("ingest/basic-suites"),
            PlannerTestFixtures.FixtureRoot("ingest/renamed-file-events"));

        Assert.Equal(0, report.Inventory.UnmatchedObservations);
    }

    // ── NEW-1 regression (fix-round-2) ──────────────────────────────────────────
    // An oversized event-history file is a configuration problem, not corrupt data — it must
    // throw PlanInputException (loud, EDGE-009's philosophy) rather than being silently
    // folded into SkippedEventLines, which is frozen documentation for a LINE count and would
    // otherwise let an entire dropped history read as "one bad line, everything else fine".

    [Fact]
    public void OversizedEventHistoryFile_ThrowsPlanInputException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vfx-planner-oversized-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var eventsFile = Path.Combine(tempDir, "huge.jsonl");

            // Content is irrelevant: the cap is checked via FileInfo.Length BEFORE any line
            // is read, so a zero-filled byte array over the 64 MiB cap is sufficient.
            File.WriteAllBytes(eventsFile, new byte[65 * 1024 * 1024]);

            var ex = Assert.Throws<PlanInputException>(() => PlannerTestFixtures.Plan(
                PlannerTestFixtures.FixtureRoot("ingest/basic-suites"),
                tempDir));

            Assert.Contains("64 MiB", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ── EDGE-009 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptySuiteFolder_ThrowsPlanInputException()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "vfx-planner-empty-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            var ex = Assert.Throws<PlanInputException>(() => PlannerTestFixtures.Plan(emptyDir));
            Assert.Contains(emptyDir, ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }
}
