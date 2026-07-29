// Vouchfx.Cli.Tests — `plan` subcommand (M3 Planner / planner-coverage-and-gap-report,
// REQ-001, REQ-002, REQ-010, EDGE-008, EDGE-009). No Docker.
//
// Two halves, deliberately separated:
//
//   1. ExitCodeFor(...) — the REQ-010 verdict-to-exit-code mapping, exercised against
//      hand-built SYNTHETIC PlanReportDocuments, exactly as ExitCodes.FromVerdict's own
//      tests exercise it against a bare Verdict rather than a real suite run. This isolates
//      the exit-code MAPPING itself from the (now real) analysers' behaviour; the mapping
//      firing end-to-end through a REAL PlanExport.BuildPlan call is proven in Execute(...)
//      below (Execute_ValidSuite_FailOnGapTrue_ReturnsGapsFound_WhenSuiteHasGenuineGaps /
//      Execute_FullyCoveredSuite_FailOnGapTrue_ReturnsSuccess).
//
//   2. Execute(...) — the Docker-free orchestration: usage errors (EDGE-009, a bad path),
//      incomplete catalogue metadata (a deliberately broken registry built in this file,
//      mirroring tests/Vouchfx.Engine.Compilation.Tests/CatalogueExportTests.cs:199-211),
//      --output (EDGE-008 and byte-identical-to-stdout), and the --json <-> PlanExport.
//      SerializePlan parity (REQ-001 CLI<->library parity). Every fixture is a temp
//      directory built at runtime (mirrors ScaffoldCommandTests / SchemaCommandTests) —
//      never a Content-included fixture — so this file needs no csproj edit.

using System.Text;
using System.Text.Json;
using Vouchfx.Cli;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Planning;
using Vouchfx.Engine.Planning.Report;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class PlanCommandTests
{
    // ── Suite fixtures (temp directories, built at runtime — no Content fixture) ──────

    private const string ValidSuiteYaml = """
        metadata:
          name: sample-suite
        steps:
          - id: get-root
            type: http.rest
            target: api
            method: GET
            path: /
        """;

    private static string CreateSuiteDir(string yaml = ValidSuiteYaml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sample.e2e.yaml"), yaml, Encoding.UTF8);
        return dir;
    }

    private static void DeleteBestEffort(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Synthetic PlanReportDocument / PlanFinding builders (no real BuildPlan call) ──

    private static PlanFinding MakeFinding(
        string kind,
        string? target = "db",
        string? targetKind = PlanTargetKinds.Dependency,
        PlanStepHistory? history = null) => new(
            Kind: kind,
            Suite: "sample.e2e.yaml",
            StepId: null,
            Target: target,
            TargetKind: targetKind,
            SuggestedTypes: PlanFindingKinds.IsGap(kind) ? new[] { "db-assert.postgres" } : Array.Empty<string>(),
            SuggestedStepId: PlanFindingKinds.IsGap(kind) ? "assert-db" : null,
            Ambiguous: false,
            AmbiguityReason: null,
            History: history,
            Detail: $"synthetic {kind} finding for test purposes",
            RelatedSuites: Array.Empty<string>());

    private static PlanReportDocument MakeDocument(params PlanFinding[] findings) => new(
        SchemaVersion: PlanExport.PlanSchemaVersion,
        EngineVersion: "test",
        Thresholds: new PlanThresholdsReport(30, 2, 2, 2),
        Inventory: new PlanInventory(
            Suites: Array.Empty<PlanSuiteEntry>(),
            Services: Array.Empty<string>(),
            Dependencies: Array.Empty<PlanDependencyEntry>(),
            StepTypes: Array.Empty<string>(),
            RunCount: 0,
            FirstEventTs: null,
            LastEventTs: null,
            SkippedEventLines: 0,
            UnmatchedObservations: 0,
            UnanalysableSuites: Array.Empty<PlanUnanalysableSuite>(),
            UnmappableDependencies: Array.Empty<PlanUnmappableDependency>()),
        Findings: findings);

    // ── 1. ExitCodeFor — the REQ-010 verdict-to-exit-code matrix over synthetic documents ──

    [Theory]
    [InlineData(PlanFindingKinds.SuiteNeverRun)]
    [InlineData(PlanFindingKinds.StepNeverExercised)]
    [InlineData(PlanFindingKinds.DependencyNotAsserted)]
    [InlineData(PlanFindingKinds.DependencyMissingStepType)]
    [InlineData(PlanFindingKinds.ServiceMissingHttpStep)]
    public void ExitCodeFor_GapFindingPresent_FailOnGapFalse_ReturnsSuccess(string gapKind)
    {
        // "success-with-gaps → 0": gaps are data; --fail-on-gap was NOT passed.
        Assert.True(PlanFindingKinds.IsGap(gapKind));
        var document = MakeDocument(MakeFinding(gapKind));

        Assert.Equal(ExitCodes.Success, PlanCommand.ExitCodeFor(document, failOnGap: false));
    }

    [Theory]
    [InlineData(PlanFindingKinds.SuiteNeverRun)]
    [InlineData(PlanFindingKinds.StepNeverExercised)]
    [InlineData(PlanFindingKinds.DependencyNotAsserted)]
    [InlineData(PlanFindingKinds.DependencyMissingStepType)]
    [InlineData(PlanFindingKinds.ServiceMissingHttpStep)]
    public void ExitCodeFor_GapFindingPresent_FailOnGapTrue_ReturnsGapsFound(string gapKind)
    {
        // "--fail-on-gap with gaps → 5".
        var document = MakeDocument(MakeFinding(gapKind));

        Assert.Equal(ExitCodes.GapsFound, PlanCommand.ExitCodeFor(document, failOnGap: true));
        Assert.Equal(5, ExitCodes.GapsFound);
    }

    [Theory]
    [InlineData(PlanFindingKinds.StepStale)]
    [InlineData(PlanFindingKinds.StepFlaky)]
    [InlineData(PlanFindingKinds.StepFragile)]
    [InlineData(PlanFindingKinds.StepInconclusiveProne)]
    [InlineData(PlanFindingKinds.SuiteIdentityAmbiguous)]
    public void ExitCodeFor_OnlyNonGapFindingPresent_FailOnGapTrue_ReturnsSuccess(string nonGapKind)
    {
        // "--fail-on-gap with no gaps → 0": history-health and suite-identity-ambiguity
        // findings are deliberately outside PlanFindingKinds.IsGap and must never trip
        // --fail-on-gap on their own.
        Assert.False(PlanFindingKinds.IsGap(nonGapKind));
        var document = MakeDocument(MakeFinding(
            nonGapKind,
            target: null,
            targetKind: null,
            history: nonGapKind == PlanFindingKinds.SuiteIdentityAmbiguous
                ? null
                : new PlanStepHistory(1, 1, 0, 0, 2, DateTimeOffset.UtcNow, 5)));

        Assert.Equal(ExitCodes.Success, PlanCommand.ExitCodeFor(document, failOnGap: true));
    }

    [Fact]
    public void ExitCodeFor_NoFindingsAtAll_FailOnGapTrue_ReturnsSuccess()
    {
        var document = MakeDocument();

        Assert.Equal(ExitCodes.Success, PlanCommand.ExitCodeFor(document, failOnGap: true));
    }

    [Fact]
    public void ExitCodeFor_MixedGapAndNonGapFindings_FailOnGapTrue_ReturnsGapsFound()
    {
        var document = MakeDocument(
            MakeFinding(PlanFindingKinds.StepStale, target: null, targetKind: null,
                history: new PlanStepHistory(1, 0, 0, 0, 1, DateTimeOffset.UtcNow, 40)),
            MakeFinding(PlanFindingKinds.DependencyMissingStepType));

        Assert.Equal(ExitCodes.GapsFound, PlanCommand.ExitCodeFor(document, failOnGap: true));
    }

    // ── 2. Execute — Docker-free orchestration ────────────────────────────────────────

    [Fact]
    public void Execute_ValidSuite_NoFailOnGap_ReturnsSuccess()
    {
        var dir = CreateSuiteDir();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = PlanCommand.Execute(
                dir, eventsPath: null, json: false, outputPath: null,
                failOnGap: false, PlanThresholds.Defaults, stdout, stderr);

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Equal(string.Empty, stderr.ToString());
            Assert.Contains("Suites analysed:", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteBestEffort(dir);
        }
    }

    [Fact]
    public void Execute_ValidSuite_FailOnGapTrue_ReturnsGapsFound_WhenSuiteHasGenuineGaps()
    {
        // T2/T3/T4's analysers are all real now (no longer T1 stubs): a suite analysed with
        // no event history genuinely never ran and its declared step is genuinely never
        // exercised — two real REQ-004 gap findings — so --fail-on-gap promotes the exit
        // code to 5. The --fail-on-gap => 5 MAPPING itself is proven above against a
        // synthetic document; this proves the same mapping fires end-to-end through a REAL
        // BuildPlan call, replacing the former "analysers are stubs" placeholder assertion.
        var dir = CreateSuiteDir();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = PlanCommand.Execute(
                dir, eventsPath: null, json: false, outputPath: null,
                failOnGap: true, PlanThresholds.Defaults, stdout, stderr);

            Assert.Equal(ExitCodes.GapsFound, exit);
            Assert.Equal(5, ExitCodes.GapsFound);
        }
        finally
        {
            DeleteBestEffort(dir);
        }
    }

    [Fact]
    public void Execute_FullyCoveredSuite_FailOnGapTrue_ReturnsSuccess()
    {
        // The mirror image of the test above: a suite whose declared step IS exercised by
        // the supplied event history produces zero gap findings (no environment section at
        // all, so VocabularyGapAnalyser has no service/dependency name to check either), so
        // --fail-on-gap has nothing to promote and exits 0 — proving --fail-on-gap is
        // genuinely gap-conditional end-to-end, not merely "always 5 now that the analysers
        // are real".
        const string suiteYaml = """
            metadata:
              name: fully-covered-suite
            steps:
              - id: get-root
                type: http.rest
                target: api
                method: GET
                path: /
            """;
        const string eventsJsonl = """
            {"v":1,"schemaVersion":"v1","type":"scenario-started","ts":"2026-01-01T00:00:00Z","runId":"run-1","scenarioId":"fully-covered-suite"}
            {"v":1,"schemaVersion":"v1","type":"step-completed","ts":"2026-01-01T00:01:00Z","runId":"run-1","stepId":"get-root","verdict":"PASS","durationMs":50}
            {"v":1,"schemaVersion":"v1","type":"scenario-completed","ts":"2026-01-01T00:02:00Z","runId":"run-1","scenarioId":"fully-covered-suite","verdict":"PASS","counts":{"pass":1,"fail":0,"envError":0,"inconclusive":0}}
            """;

        var dir = CreateSuiteDir(suiteYaml);
        var eventsDir = Path.Combine(Path.GetTempPath(), "vouchfx-plan-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(eventsDir);
        var eventsPath = Path.Combine(eventsDir, "history.jsonl");
        File.WriteAllText(eventsPath, eventsJsonl, Encoding.UTF8);
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = PlanCommand.Execute(
                dir, eventsPath, json: false, outputPath: null,
                failOnGap: true, PlanThresholds.Defaults, stdout, stderr);

            Assert.Equal(ExitCodes.Success, exit);
        }
        finally
        {
            DeleteBestEffort(dir);
            DeleteBestEffort(eventsDir);
        }
    }

    // ── MINOR fix-round: threshold validation — "--stale-days -1 marks everything stale" ──

    [Fact]
    public void Execute_NegativeStaleDays_ReturnsUsageError()
    {
        var dir = CreateSuiteDir();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var thresholds = new PlanThresholds(StaleDays: -1);

            var exit = PlanCommand.Execute(
                dir, eventsPath: null, json: false, outputPath: null,
                failOnGap: false, thresholds, stdout, stderr);

            Assert.Equal(ExitCodes.UsageError, exit);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Contains("StaleDays", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteBestEffort(dir);
        }
    }

    [Fact]
    public void Execute_EmptySuiteFolder_ReturnsUsageErrorNamingPath_Edge009()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-plan-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = PlanCommand.Execute(
                dir, eventsPath: null, json: false, outputPath: null,
                failOnGap: false, PlanThresholds.Defaults, stdout, stderr);

            Assert.Equal(ExitCodes.UsageError, exit);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Contains(dir, stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteBestEffort(dir);
        }
    }

    [Fact]
    public void Execute_NonexistentSuitePath_ReturnsUsageError()
    {
        var missing = Path.Combine(
            Path.GetTempPath(), "vouchfx-plan-nonexistent-" + Guid.NewGuid().ToString("N"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = PlanCommand.Execute(
            missing, eventsPath: null, json: false, outputPath: null,
            failOnGap: false, PlanThresholds.Defaults, stdout, stderr);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Contains(missing, stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_IncompleteCatalogueMetadata_ReturnsEnvironmentError()
    {
        // Mirrors tests/Vouchfx.Engine.Compilation.Tests/CatalogueExportTests.cs:199-211
        // exactly: a provider implementing ONLY IStepProvider (no IStepBinder<T>) records a
        // null SchemaFragment, so EngineExport.BuildCatalogue (called inside
        // PlanExport.BuildPlan, AFTER SuiteSetLoader.Load succeeds against a real suite
        // folder) fails closed with CatalogueExportException. No csproj edit, no shared
        // fixture — the broken registry is built right here via
        // StepKindRegistry.BuildAndFreeze(IStepProvider[]).
        var dir = CreateSuiteDir();
        try
        {
            var brokenRegistry = StepKindRegistry.BuildAndFreeze(new IStepProvider[]
            {
                new IncompleteNoFragmentProvider(),
            });

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = PlanCommand.Execute(
                dir, eventsPath: null, json: false, outputPath: null,
                failOnGap: false, PlanThresholds.Defaults, stdout, stderr, brokenRegistry);

            Assert.Equal(ExitCodes.EnvironmentError, exit);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Contains("incomplete.none", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteBestEffort(dir);
        }
    }

    [Fact]
    public void Execute_Json_StdoutParsesAndEqualsPlanExportSerializePlan()
    {
        // REQ-001 CLI<->library parity: the SAME inputs through PlanExport.BuildPlan +
        // SerializePlan directly must produce byte-identical text to what --json printed.
        var dir = CreateSuiteDir();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var thresholds = PlanThresholds.Defaults;

            var exit = PlanCommand.Execute(
                dir, eventsPath: null, json: true, outputPath: null,
                failOnGap: false, thresholds, stdout, stderr);

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Equal(string.Empty, stderr.ToString());

            var stdoutText = stdout.ToString();
            using (var doc = JsonDocument.Parse(stdoutText))
            {
                Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            }

            var registry = ProviderRegistryFactory.BuildCoreRegistry();
            var expectedDocument = PlanExport.BuildPlan(
                new PlanRequest(dir, null, thresholds), registry, CliJsonContract.EngineVersion);
            var expectedJson = PlanExport.SerializePlan(expectedDocument);

            // MINOR fix-round: stdout now gets a trailing newline (WriteLine, not Write —
            // mirrors SchemaCommand) so the shell prompt never lands attached to the closing
            // brace. --output's file content (asserted byte-identical with NO added newline
            // in Execute_Output_WritesByteIdenticalJsonRegardlessOfJsonFlag below) is
            // deliberately unaffected — only stdout changed.
            Assert.Equal(expectedJson + Environment.NewLine, stdoutText);
        }
        finally
        {
            DeleteBestEffort(dir);
        }
    }

    [Fact]
    public void Execute_Output_WritesByteIdenticalJsonRegardlessOfJsonFlag()
    {
        var dir = CreateSuiteDir();
        var outDir = Path.Combine(Path.GetTempPath(), "vouchfx-plan-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            var outputPath = Path.Combine(outDir, "report.json");
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var thresholds = PlanThresholds.Defaults;

            // json:false — proves --output writes the JSON document regardless of --json.
            var exit = PlanCommand.Execute(
                dir, eventsPath: null, json: false, outputPath,
                failOnGap: false, thresholds, stdout, stderr);

            Assert.Equal(ExitCodes.Success, exit);
            Assert.True(File.Exists(outputPath));
            // stdout got the human summary (no --json), not the JSON document.
            Assert.DoesNotContain("\"schemaVersion\"", stdout.ToString(), StringComparison.Ordinal);

            var registry = ProviderRegistryFactory.BuildCoreRegistry();
            var expectedDocument = PlanExport.BuildPlan(
                new PlanRequest(dir, null, thresholds), registry, CliJsonContract.EngineVersion);
            var expectedJson = PlanExport.SerializePlan(expectedDocument);

            Assert.Equal(expectedJson, File.ReadAllText(outputPath));
        }
        finally
        {
            DeleteBestEffort(dir);
            DeleteBestEffort(outDir);
        }
    }

    [Fact]
    public void Execute_OutputParentMissing_ReturnsUsageErrorAndWritesNoPartialFile_Edge008()
    {
        var dir = CreateSuiteDir();
        try
        {
            var missingParent = Path.Combine(
                Path.GetTempPath(),
                "vouchfx-plan-missing-" + Guid.NewGuid().ToString("N"),
                "nested",
                "report.json");

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = PlanCommand.Execute(
                dir, eventsPath: null, json: false, missingParent,
                failOnGap: false, PlanThresholds.Defaults, stdout, stderr);

            Assert.Equal(ExitCodes.UsageError, exit);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Contains("parent directory", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(missingParent));
        }
        finally
        {
            DeleteBestEffort(dir);
        }
    }

    [Fact]
    public void Execute_EventsPathOmitted_StillSucceeds_Req009()
    {
        var dir = CreateSuiteDir();
        try
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = PlanCommand.Execute(
                dir, eventsPath: null, json: false, outputPath: null,
                failOnGap: false, PlanThresholds.Defaults, stdout, stderr);

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Contains("Runs analysed:           0", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteBestEffort(dir);
        }
    }

    // ── Test-only provider — mirrors CatalogueExportTests.IncompleteNoFragmentProvider ──

    /// <summary>
    /// Implements <see cref="IStepProvider"/> only — no <c>IStepBinder&lt;T&gt;</c>, so the
    /// registry records a <see langword="null"/> <c>SchemaFragment</c> (the fail-closed
    /// export target <see cref="EngineExport.BuildCatalogue"/> rejects).
    /// </summary>
    [StepProvider]
    private sealed class IncompleteNoFragmentProvider : IStepProvider
    {
        public IncompleteNoFragmentProvider() { }

        public StepKindId Kind { get; } = new("incomplete", "none");

        public ProviderMetadata Metadata { get; } = new(
            Version: "0.0.0-test",
            MinEngineVersion: "1.0.0",
            License: "Apache-2.0",
            Authors: new[] { "test-only" });
    }
}
