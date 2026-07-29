// Vouchfx.Engine.Planning.Report — PlanReportDocument + inventory records
// (M3 Planner, REQ-003, REQ-011). Frozen v1 wire shape — see PlanReportContractFreezeTests.
//
// Positional records with explicit JsonPropertyOrder, mirroring the StepCatalogueDocument
// idiom in Vouchfx.Engine.Compilation.Schema.StepCatalogue.cs so a reader who knows one
// knows the other.

using System.Text.Json.Serialization;

namespace Vouchfx.Engine.Planning.Report;

/// <summary>
/// The top-level coverage-and-gap report document exported by
/// <see cref="Vouchfx.Engine.Planning.PlanExport.BuildPlan"/>.
/// </summary>
/// <param name="SchemaVersion">
/// The plan-report wire-schema version (currently
/// <see cref="Vouchfx.Engine.Planning.PlanExport.PlanSchemaVersion"/>).
/// </param>
/// <param name="EngineVersion">
/// Optional informational engine / host version stamp. May be <see langword="null"/> when
/// the host does not supply one; serialised as JSON <c>null</c>, never omitted.
/// </param>
/// <param name="Thresholds">
/// The EFFECTIVE history-health thresholds used for this analysis (defaults or caller
/// overrides), echoed here — a document-identity field, not part of the REQ-003 inventory
/// fence — so a reader can tell why a step was flagged stale/flaky/fragile without
/// re-deriving the defaults.
/// </param>
/// <param name="Inventory">The REQ-003 inventory section.</param>
/// <param name="Findings">
/// Every coverage gap, vocabulary gap, history-health, and suite-identity-ambiguity finding,
/// in the EDGE-005 deterministic order (kind, then target, then suite path, then step id;
/// all <see cref="StringComparer.Ordinal"/>, nulls last).
/// </param>
public sealed record PlanReportDocument(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] string? EngineVersion,
    [property: JsonPropertyOrder(2)] PlanThresholdsReport Thresholds,
    [property: JsonPropertyOrder(3)] PlanInventory Inventory,
    [property: JsonPropertyOrder(4)] IReadOnlyList<PlanFinding> Findings);

/// <summary>
/// The effective history-health thresholds used for one analysis (REQ-006), echoed on the
/// report document.
/// </summary>
/// <param name="StaleDays">See <see cref="Vouchfx.Engine.Planning.PlanThresholds.StaleDays"/>.</param>
/// <param name="FlakyMinRuns">See <see cref="Vouchfx.Engine.Planning.PlanThresholds.FlakyMinRuns"/>.</param>
/// <param name="FragileMinEnvErrors">See <see cref="Vouchfx.Engine.Planning.PlanThresholds.FragileMinEnvErrors"/>.</param>
/// <param name="InconclusiveMin">See <see cref="Vouchfx.Engine.Planning.PlanThresholds.InconclusiveMin"/>.</param>
public sealed record PlanThresholdsReport(
    [property: JsonPropertyOrder(0)] int StaleDays,
    [property: JsonPropertyOrder(1)] int FlakyMinRuns,
    [property: JsonPropertyOrder(2)] int FragileMinEnvErrors,
    [property: JsonPropertyOrder(3)] int InconclusiveMin);

/// <summary>
/// The REQ-003 inventory section: a fixed, non-extensible list of structural counts and
/// names derived purely from the declared suites and the analysed event history.
/// </summary>
/// <param name="Suites">Every discovered suite (path and scenario identity).</param>
/// <param name="Services">
/// The distinct service NAMES declared across every suite's <c>environment.services</c>
/// block, ordinal-sorted. Name only — the DSL gives services <c>image</c>/<c>project</c>,
/// not a technology-kind token.
/// </param>
/// <param name="Dependencies">
/// The distinct dependency name+type pairs declared across every suite's
/// <c>environment.dependencies</c> block, ordinal-sorted by name.
/// </param>
/// <param name="StepTypes">
/// The distinct dotted step types declared across every suite, ordinal-sorted.
/// </param>
/// <param name="RunCount">
/// The number of distinct scenario executions (distinct <c>runId</c>s) observed in the
/// analysed history. Each scenario execution mints its own <c>runId</c> (blueprint §16.2),
/// so a single <c>vouchfx run</c> invocation over three suites yields <c>3</c>, not <c>1</c>.
/// </param>
/// <param name="FirstEventTs">
/// The earliest event timestamp in the analysed history, or <see langword="null"/> when the
/// history is empty or absent.
/// </param>
/// <param name="LastEventTs">
/// The latest event timestamp in the analysed history — REQ-006's "now" for stale-step
/// classification — or <see langword="null"/> when the history is empty or absent.
/// </param>
/// <param name="SkippedEventLines">
/// The count of event-history lines that were not parseable JSON, or were valid JSON but
/// not a recognisable event (EDGE-004), so a reader never mistakes a truncated history for
/// genuine non-coverage. An event source that could not be read at all (a transient I/O
/// race — a directory that could not be enumerated, or a file that became unreadable
/// mid-read) counts as a single skipped entry here too; an OVERSIZED event-history file is
/// a distinct, louder failure (a configuration problem, not corrupt data) and is rejected
/// with an exception rather than folded into this count.
/// </param>
/// <param name="UnmatchedObservations">
/// The count of history observations (scenario or step events) that could not be attributed
/// to any currently-declared suite and were not classified as a suite-identity ambiguity —
/// e.g. a run of a suite outside the analysed path (EDGE-007).
/// </param>
/// <param name="UnanalysableSuites">
/// Suites that were discovered under the analysed path but failed to parse (EDGE-003), each
/// with its captured error message. The analysis still proceeds over every other suite.
/// </param>
/// <param name="UnmappableDependencies">
/// Declared dependencies whose kind has no REQ-005 candidate step type — either because the
/// kind is explicitly known to have no asserting/observing Core provider yet, or because the
/// kind is not recognised by the engine's dependency-kind vocabulary at all (REQ-007). These
/// intentionally never produce a gap finding (a hint-less finding would violate REQ-007);
/// they are recorded here instead.
/// </param>
public sealed record PlanInventory(
    [property: JsonPropertyOrder(0)] IReadOnlyList<PlanSuiteEntry> Suites,
    [property: JsonPropertyOrder(1)] IReadOnlyList<string> Services,
    [property: JsonPropertyOrder(2)] IReadOnlyList<PlanDependencyEntry> Dependencies,
    [property: JsonPropertyOrder(3)] IReadOnlyList<string> StepTypes,
    [property: JsonPropertyOrder(4)] int RunCount,
    [property: JsonPropertyOrder(5)] DateTimeOffset? FirstEventTs,
    [property: JsonPropertyOrder(6)] DateTimeOffset? LastEventTs,
    [property: JsonPropertyOrder(7)] int SkippedEventLines,
    [property: JsonPropertyOrder(8)] int UnmatchedObservations,
    [property: JsonPropertyOrder(9)] IReadOnlyList<PlanUnanalysableSuite> UnanalysableSuites,
    [property: JsonPropertyOrder(10)] IReadOnlyList<PlanUnmappableDependency> UnmappableDependencies);

/// <summary>A single discovered suite's structural identity.</summary>
/// <param name="Path">
/// The suite's path relative to the analysed root, with <c>/</c> separators regardless of
/// host OS, so goldens are platform-independent.
/// </param>
/// <param name="ScenarioId">
/// The suite's derived scenario identity: <c>metadata.name</c> when present, else the
/// filename stem (character-for-character identical to the CLI's
/// <c>RunCommand.ScenarioName</c> derivation) — the sole correlation key against the event
/// history (REQ-008).
/// </param>
/// <param name="Name">
/// The suite's <c>metadata.name</c> verbatim, or <see langword="null"/> when the suite
/// declares no <c>metadata</c> section or no <c>name</c>.
/// </param>
/// <param name="StepCount">The number of steps declared in this suite.</param>
public sealed record PlanSuiteEntry(
    [property: JsonPropertyOrder(0)] string Path,
    [property: JsonPropertyOrder(1)] string ScenarioId,
    [property: JsonPropertyOrder(2)] string? Name,
    [property: JsonPropertyOrder(3)] int StepCount);

/// <summary>A single declared dependency's name and kind.</summary>
/// <param name="Name">The dependency's logical name (the map key under <c>environment.dependencies</c>).</param>
/// <param name="Type">The dependency's declared <c>type</c> token, e.g. <c>postgres</c>, <c>kafka</c>.</param>
public sealed record PlanDependencyEntry(
    [property: JsonPropertyOrder(0)] string Name,
    [property: JsonPropertyOrder(1)] string Type);

/// <summary>A discovered suite that failed to parse (EDGE-003).</summary>
/// <param name="Path">The suite's path relative to the analysed root, <c>/</c>-separated.</param>
/// <param name="Error">The captured parse / AST-build error message.</param>
public sealed record PlanUnanalysableSuite(
    [property: JsonPropertyOrder(0)] string Path,
    [property: JsonPropertyOrder(1)] string Error);

/// <summary>A declared dependency whose kind has no REQ-005 candidate step type (REQ-007).</summary>
/// <param name="Name">The dependency's logical name.</param>
/// <param name="Type">The dependency's declared <c>type</c> token.</param>
/// <param name="Reason">
/// A structural, human-readable explanation of why the kind is unmappable — either that it
/// is explicitly known to have no asserting/observing Core provider yet, or that it is not
/// recognised by the engine's dependency-kind vocabulary at all.
/// </param>
public sealed record PlanUnmappableDependency(
    [property: JsonPropertyOrder(0)] string Name,
    [property: JsonPropertyOrder(1)] string Type,
    [property: JsonPropertyOrder(2)] string Reason);
