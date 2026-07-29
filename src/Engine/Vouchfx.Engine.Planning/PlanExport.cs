// Vouchfx.Engine.Planning — PlanExport (M3 Planner, REQ-001, REQ-013).
//
// Public library API: the whole coverage-and-gap analysis, over a declared suite set, an
// optional event history, and a frozen StepKindRegistry — the sibling, in spirit, of
// EngineExport.BuildCatalogue / SerializeCatalogue in Vouchfx.Engine.Compilation.Schema.
// In-process hosts (MCP, custom tooling) call this directly; they never need to shell out or
// reflect over CLI internals.
//
// Deterministic and read-only (REQ-013): no model API is called on this path, and the
// suite folder is never written to, modified, or deleted. The only file the Planner writes
// is its own optional --output report, at the CLI layer — never here.

using System.Text.Json;
using System.Text.Json.Serialization;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Planning.Analysis;
using Vouchfx.Engine.Planning.Ingest;
using Vouchfx.Engine.Planning.Report;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Planning;

/// <summary>
/// Public export entry points for the vouchfx Planner's coverage-and-gap report, over any
/// frozen <see cref="StepKindRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Package: <c>Vouchfx.Engine.Planning</c>. The Planner intersects three sources: the
/// declared universe (an <c>.e2e.yaml</c> suite folder), the exercised reality (a JSON
/// Lines event history), and the available vocabulary (the live step catalogue reachable
/// through the registry supplied to <see cref="BuildPlan"/>) — and emits a
/// schema-versioned coverage-and-gap report. It never writes a suite file, never calls a
/// model API, and never claims coverage it cannot evidence from its declared inputs.
/// </para>
/// </remarks>
public static class PlanExport
{
    /// <summary>The plan-report wire-schema version stamped on every <see cref="PlanReportDocument"/>.</summary>
    public const int PlanSchemaVersion = 1;

    /// <summary>
    /// Shared <see cref="JsonSerializerOptions"/> for plan-report documents: camelCase
    /// property names and indented output. Also registers <see cref="VerdictJsonConverter"/>
    /// for forward compatibility — no current <see cref="PlanReportDocument"/> property is
    /// typed <see cref="Verdict"/> (<see cref="PlanStepHistory"/>'s tallies are plain
    /// <see cref="int"/> counts), but the converter is kept registered so a future additive
    /// property of that type serialises with the same canonical wire tokens the rest of the
    /// engine uses, without a separate options instance.
    /// </summary>
    public static JsonSerializerOptions PlanJsonOptions { get; } = CreatePlanJsonOptions();

    private static JsonSerializerOptions CreatePlanJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Converters = { new VerdictJsonConverter() },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>
    /// Runs the full Planner analysis for <paramref name="request"/> against
    /// <paramref name="registry"/> and returns the complete coverage-and-gap report.
    /// </summary>
    /// <param name="request">The suite path, optional event history path, and optional threshold overrides.</param>
    /// <param name="registry">The frozen provider registry — the "available vocabulary" source.</param>
    /// <param name="engineVersion">Optional host/engine version stamp written into the report root. May be <see langword="null"/>.</param>
    /// <returns>The complete, deterministic <see cref="PlanReportDocument"/>.</returns>
    /// <exception cref="PlanInputException">
    /// Thrown when <paramref name="request"/>'s suite path does not exist, names a file that
    /// is not a <c>.e2e.yaml</c> scenario, or discovers zero suites at all (EDGE-009); or when
    /// <paramref name="request"/>'s effective thresholds are out of range (MINOR fix-round —
    /// see <see cref="ValidateThresholds"/>).
    /// </exception>
    /// <exception cref="CatalogueExportException">
    /// Thrown when a registered step type in <paramref name="registry"/> lacks a schema
    /// fragment (incomplete catalogue metadata) — the fail-closed guard that makes the
    /// "available vocabulary" source real, mirroring <c>vouchfx list</c> / <c>vouchfx
    /// schema</c>. The CLI maps this to exit 3.
    /// </exception>
    public static PlanReportDocument BuildPlan(
        PlanRequest request,
        StepKindRegistry registry,
        string? engineVersion = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(registry);

        var thresholds = request.Thresholds ?? PlanThresholds.Defaults;
        ValidateThresholds(thresholds);

        // Ordering is deliberate, not incidental (reviewed and agreed): SuiteSetLoader.Load
        // runs BEFORE EngineExport.BuildCatalogue — usage-error-wins. A bad suite path is the
        // CALLER's actionable mistake (EDGE-009); an incomplete catalogue is engine-build
        // insurance the caller cannot fix at all. No acceptance criterion collides: EDGE-009
        // tests an empty folder against a GOOD registry, REQ-010's exit-3 path tests a GOOD
        // folder against a BROKEN registry — the two can never race for the same invocation.
        var loadResult = SuiteSetLoader.Load(request.SuitePath, registry);

        // REQ-005 / REQ-007 need the live step catalogue — the "available vocabulary" source
        // — to classify vocabulary gaps and validate hand-off hints against the CURRENT
        // registration. Fails closed exactly like EngineExport.BuildCatalogue's own contract;
        // the CLI maps CatalogueExportException to exit 3, the same class of failure as
        // `vouchfx list` / `vouchfx schema`.
        var catalogue = EngineExport.BuildCatalogue(registry, engineVersion);

        var rawHistory = EventHistoryReader.Read(request.EventsPath);
        var correlatedHistory = RunCorrelator.Correlate(loadResult.Suites, loadResult.AnalysedRoot, rawHistory);

        var inputs = new PlanInputs(
            Registry: registry,
            Catalogue: catalogue,
            Thresholds: thresholds,
            AnalysedRoot: loadResult.AnalysedRoot,
            Suites: loadResult.Suites,
            UnanalysableSuites: loadResult.UnanalysableSuites,
            History: correlatedHistory);

        return PlanPipeline.Run(inputs, engineVersion);
    }

    /// <summary>Serialises <paramref name="document"/> with <see cref="PlanJsonOptions"/>.</summary>
    /// <param name="document">The report document to serialise.</param>
    /// <returns>Indented, camelCase JSON text.</returns>
    public static string SerializePlan(PlanReportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, PlanJsonOptions);
    }

    /// <summary>
    /// MINOR fix-round: rejects an out-of-range <see cref="PlanThresholds"/> before it ever
    /// reaches <see cref="HistoryHealthAnalyser"/>. Every threshold is a bare
    /// <see cref="int"/> with no range enforced at the CLI parse layer, so e.g.
    /// <c>--stale-days -1</c> would otherwise silently mark every step with ANY observed
    /// history as stale (a negative "more than N days" test is vacuously true), and a
    /// count threshold of <c>0</c> degenerates identically ("at least 0" is vacuously true
    /// for a step with zero matching observations too) — both defeat the "documented
    /// defaults + override flags" contract (REQ-006) by turning a typo'd flag into silent,
    /// wildly wrong output rather than a loud, actionable failure.
    /// </summary>
    /// <param name="thresholds">The effective (default-or-override) thresholds for this analysis.</param>
    /// <exception cref="PlanInputException">
    /// Thrown when <see cref="PlanThresholds.StaleDays"/> is negative, or any of
    /// <see cref="PlanThresholds.FlakyMinRuns"/>, <see cref="PlanThresholds.FragileMinEnvErrors"/>,
    /// <see cref="PlanThresholds.InconclusiveMin"/> is less than <c>1</c>.
    /// </exception>
    private static void ValidateThresholds(PlanThresholds thresholds)
    {
        if (thresholds.StaleDays < 0)
        {
            throw new PlanInputException(
                $"Invalid Planner threshold: StaleDays must be zero or greater (received {thresholds.StaleDays}). "
                + "The CLI maps this from --stale-days.");
        }

        if (thresholds.FlakyMinRuns < 1)
        {
            throw new PlanInputException(
                $"Invalid Planner threshold: FlakyMinRuns must be at least 1 (received {thresholds.FlakyMinRuns}). "
                + "The CLI maps this from --flaky-min-runs.");
        }

        if (thresholds.FragileMinEnvErrors < 1)
        {
            throw new PlanInputException(
                $"Invalid Planner threshold: FragileMinEnvErrors must be at least 1 (received {thresholds.FragileMinEnvErrors}). "
                + "The CLI maps this from --fragile-min-env-errors.");
        }

        if (thresholds.InconclusiveMin < 1)
        {
            throw new PlanInputException(
                $"Invalid Planner threshold: InconclusiveMin must be at least 1 (received {thresholds.InconclusiveMin}). "
                + "The CLI maps this from --inconclusive-min.");
        }
    }
}
