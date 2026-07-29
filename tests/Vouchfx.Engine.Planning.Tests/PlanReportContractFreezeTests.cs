// Vouchfx.Engine.Planning.Tests — PlanReportContractFreezeTests (M3 Planner, REQ-011).
//
// Freezes the v1 plan-report wire shape: serialises a FIXED, hand-built PlanReportDocument
// (never the live pipeline output — see the rejected "fixture-driven golden" alternative in
// the T0 architecture note: a pipeline-derived golden would churn every time T2/T3/T4 land a
// new finding family, guaranteeing a three-way merge conflict on the one file whose whole
// purpose is to make changes deliberate) through PlanExport.SerializePlan and asserts
// byte-for-byte (newline-normalised) identity against Golden/plan-report-document.v1.json.
//
// ADDITIVE-ONLY POLICY: evolution within v1 must only ADD new optional properties or new
// PlanFindingKinds tokens (with corresponding golden updates). Renaming, removing, or
// retyping a frozen property or kind token is a breaking change and must fail this gate.
//
// REGENERATION:
//   VOUCHFX_REGEN_PLAN_CONTRACT=1 dotnet test tests/Vouchfx.Engine.Planning.Tests \
//     --filter "FullyQualifiedName~PlanReportContractFreezeTests"
//   Rewrites Golden/plan-report-document.v1.json from the freshly-serialised fixture.
//   Review the diff (must be additive only), then commit.

using System.Diagnostics.CodeAnalysis;
using Vouchfx.Engine.Planning.Report;
using Xunit;

namespace Vouchfx.Engine.Planning.Tests;

// Type-scoped suppressions (not a shared GlobalSuppressions.cs — see PlanExportTests.cs).
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "xUnit test methods use Given_When_Then underscore convention.")]
[SuppressMessage(
    "Performance",
    "CA1861:Avoid constant arrays as arguments",
    Justification = "Inline literal arrays are the readable form for test fixtures.")]
public sealed class PlanReportContractFreezeTests
{
    private const string RegenEnvVar = "VOUCHFX_REGEN_PLAN_CONTRACT";

    private static PlanReportDocument BuildCanonicalFixture()
    {
        var lastObserved = new DateTimeOffset(2026, 1, 3, 0, 5, 0, TimeSpan.Zero);

        var findings = new List<PlanFinding>
        {
            new(
                Kind: PlanFindingKinds.DependencyMissingStepType,
                Suite: null,
                StepId: null,
                Target: "orders-db",
                TargetKind: PlanTargetKinds.Dependency,
                SuggestedTypes: new[] { "db-assert.postgres" },
                SuggestedStepId: "assert-orders-db",
                Ambiguous: false,
                AmbiguityReason: null,
                History: null,
                Detail: "Dependency 'orders-db' (postgres) has no analysed step of a candidate asserting type."),
            new(
                Kind: PlanFindingKinds.DependencyNotAsserted,
                Suite: "checkout.e2e.yaml",
                StepId: null,
                Target: "events",
                TargetKind: PlanTargetKinds.Dependency,
                SuggestedTypes: new[] { "mq-expect.kafka" },
                SuggestedStepId: "assert-events",
                Ambiguous: false,
                AmbiguityReason: null,
                History: null,
                Detail: "Dependency 'events' is targeted by a declared step but by no step of an asserting family."),
            new(
                Kind: PlanFindingKinds.ServiceMissingHttpStep,
                Suite: null,
                StepId: null,
                Target: "api",
                TargetKind: PlanTargetKinds.Service,
                SuggestedTypes: new[] { "http.rest" },
                SuggestedStepId: "assert-api",
                Ambiguous: false,
                AmbiguityReason: null,
                History: null,
                Detail: "Service 'api' has no http.* step targeting it."),
            new(
                Kind: PlanFindingKinds.StepFlaky,
                Suite: "checkout.e2e.yaml",
                StepId: "create-order",
                Target: null,
                TargetKind: null,
                SuggestedTypes: Array.Empty<string>(),
                SuggestedStepId: null,
                Ambiguous: false,
                AmbiguityReason: null,
                History: new PlanStepHistory(3, 2, 0, 0, 2, lastObserved, 1),
                Detail: "Step 'create-order' passed 3 and failed 2 times across 2 distinct runs."),
            new(
                Kind: PlanFindingKinds.StepFragile,
                Suite: "checkout.e2e.yaml",
                StepId: "assert-order-row",
                Target: null,
                TargetKind: null,
                SuggestedTypes: Array.Empty<string>(),
                SuggestedStepId: null,
                Ambiguous: false,
                AmbiguityReason: null,
                History: new PlanStepHistory(1, 0, 2, 0, 2, lastObserved, 1),
                Detail: "Step 'assert-order-row' recorded 2 environment errors."),
            new(
                Kind: PlanFindingKinds.StepInconclusiveProne,
                Suite: "checkout.e2e.yaml",
                StepId: "create-order",
                Target: null,
                TargetKind: null,
                SuggestedTypes: Array.Empty<string>(),
                SuggestedStepId: null,
                Ambiguous: false,
                AmbiguityReason: null,
                History: new PlanStepHistory(1, 0, 0, 2, 2, lastObserved, 1),
                Detail: "Step 'create-order' recorded 2 inconclusive outcomes."),
            new(
                Kind: PlanFindingKinds.StepNeverExercised,
                Suite: "refund.e2e.yaml",
                StepId: "publish-refund-event",
                Target: null,
                TargetKind: null,
                SuggestedTypes: new[] { "mq-publish.kafka" },
                SuggestedStepId: "publish-refund-event",
                Ambiguous: false,
                AmbiguityReason: null,
                History: null,
                Detail: "Step 'publish-refund-event' has no step event attributed to it."),
            new(
                Kind: PlanFindingKinds.StepStale,
                Suite: "checkout.e2e.yaml",
                StepId: "create-order",
                Target: null,
                TargetKind: null,
                SuggestedTypes: Array.Empty<string>(),
                SuggestedStepId: null,
                Ambiguous: false,
                AmbiguityReason: null,
                History: new PlanStepHistory(1, 0, 0, 0, 1, lastObserved, 45),
                Detail: "Step 'create-order' was last observed 45 days before the newest event."),
            new(
                Kind: PlanFindingKinds.SuiteIdentityAmbiguous,
                Suite: "checkout.e2e.yaml",
                StepId: null,
                Target: null,
                TargetKind: null,
                SuggestedTypes: Array.Empty<string>(),
                SuggestedStepId: null,
                Ambiguous: true,
                AmbiguityReason: PlanAmbiguityReasons.ScenarioIdCollision,
                History: null,
                Detail: "Scenario id 'checkout-flow' is declared by two suites: checkout.e2e.yaml, checkout-v2.e2e.yaml."),
            new(
                // Fix-round B2: an 11th canonical finding is REQUIRED (not decorative) —
                // before this, "history-references-missing-file" appeared in NO golden
                // anywhere, so a typo in that frozen token could not fail any CI gate.
                Kind: PlanFindingKinds.SuiteIdentityAmbiguous,
                Suite: "refund.e2e.yaml",
                StepId: null,
                Target: null,
                TargetKind: null,
                SuggestedTypes: Array.Empty<string>(),
                SuggestedStepId: null,
                Ambiguous: true,
                AmbiguityReason: PlanAmbiguityReasons.HistoryReferencesMissingFile,
                History: null,
                Detail: "History references a file for scenario id 'refund' that no longer exists on disk."),
            new(
                Kind: PlanFindingKinds.SuiteNeverRun,
                Suite: "refund.e2e.yaml",
                StepId: null,
                Target: null,
                TargetKind: null,
                SuggestedTypes: Array.Empty<string>(),
                SuggestedStepId: null,
                Ambiguous: false,
                AmbiguityReason: null,
                History: null,
                Detail: "Suite 'refund.e2e.yaml' never appears in the analysed event history."),
        };

        var inventory = new PlanInventory(
            Suites: new[]
            {
                new PlanSuiteEntry("checkout.e2e.yaml", "checkout-flow", "checkout-flow", 2),
                new PlanSuiteEntry("refund.e2e.yaml", "refund", null, 1),
            },
            Services: new[] { "api" },
            Dependencies: new[]
            {
                new PlanDependencyEntry("events", "kafka"),
                new PlanDependencyEntry("orders-db", "postgres"),
            },
            StepTypes: new[] { "db-assert.postgres", "http.rest", "mq-publish.kafka" },
            RunCount: 3,
            FirstEventTs: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LastEventTs: lastObserved,
            SkippedEventLines: 2,
            UnmatchedObservations: 1,
            UnanalysableSuites: new[] { new PlanUnanalysableSuite("bad.e2e.yaml", "Parse / AST error: a step is missing the required 'id' field.") },
            // Fix-round-2 NEW-6: this Reason string is a hand-written literal, NOT derived by
            // calling DependencyKindStepMap.DescribeUnmappable — it happens to match that
            // method's current case-(b) output, but a future rewording there would not be
            // caught here (Reason is frozen as prose, not as a token; see PlanFindingKinds'
            // freeze for what IS token-frozen). That divergence is acceptable: this fixture's
            // job is pinning the wire SHAPE (a Reason property exists, is a string, sits at
            // this JsonPropertyOrder), not pinning DescribeUnmappable's exact wording.
            UnmappableDependencies: new[] { new PlanUnmappableDependency("legacy-cache", "cassandra", "Dependency kind is not recognised by the engine's dependency-kind vocabulary.") });

        return new PlanReportDocument(
            SchemaVersion: PlanExport.PlanSchemaVersion,
            EngineVersion: "1.2.3-golden",
            Thresholds: new PlanThresholdsReport(StaleDays: 30, FlakyMinRuns: 2, FragileMinEnvErrors: 2, InconclusiveMin: 2),
            Inventory: inventory,
            Findings: findings);
    }

    [Fact]
    public void PlanReportDocument_MatchesGolden_ByteForByte()
    {
        var actual = PlanExport.SerializePlan(BuildCanonicalFixture());

        if (IsRegenRequested())
        {
            var repoRoot = FindRepoRoot();
            var goldenPath = Path.Combine(
                repoRoot, "tests", "Vouchfx.Engine.Planning.Tests", "Golden",
                "plan-report-document.v1.json");
            // N7: write \n-only line endings regardless of host OS, so a checkout on a
            // different platform (or a future run on this one) never introduces a spurious
            // CRLF/LF diff against the committed golden.
            File.WriteAllText(goldenPath, Normalise(actual));
            return;
        }

        var golden = ReadGolden();
        Assert.Equal(Normalise(golden), Normalise(actual));
    }

    [Fact]
    public void EveryFrozenWireTokenSet_AppearsInTheGolden()
    {
        var golden = ReadGolden();

        // B2: all three frozen wire-token families — a finding kind, target kind, or
        // ambiguity reason typo'd by a future analyser must fail this gate. Before this fix,
        // PlanTargetKinds/PlanAmbiguityReasons did not exist as named constants at all, and
        // "history-references-missing-file" appeared in NO golden, so nothing could catch it.
        foreach (var kind in PlanFindingKinds.All)
        {
            Assert.Contains($"\"{kind}\"", golden, StringComparison.Ordinal);
        }

        foreach (var targetKind in PlanTargetKinds.All)
        {
            Assert.Contains($"\"{targetKind}\"", golden, StringComparison.Ordinal);
        }

        foreach (var reason in PlanAmbiguityReasons.All)
        {
            Assert.Contains($"\"{reason}\"", golden, StringComparison.Ordinal);
        }
    }

    private static bool IsRegenRequested()
    {
        var value = Environment.GetEnvironmentVariable(RegenEnvVar);
        return !string.IsNullOrEmpty(value)
            && (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (no ancestor of "
            + $"'{AppContext.BaseDirectory}' contains 'vouchfx.sln').");
    }

    private static string ReadGolden()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", "plan-report-document.v1.json");
        Assert.True(
            File.Exists(path),
            $"Golden file not found at '{path}'. The freeze gate requires "
            + "Golden/plan-report-document.v1.json to be committed and copied to output.");
        return File.ReadAllText(path);
    }

    private static string Normalise(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
}
