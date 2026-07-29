// Vouchfx.Engine.Planning.Ingest — PlanInputs (M3 Planner).
//
// The single, fully-assembled input every analyser (Analysis/*) reads. Internal: keeping
// the ingest model internal means T2/T3/T4's analyser work never changes this assembly's
// PUBLIC surface, so the REQ-011 golden (a synthetic Report-namespace document) is immune
// to how the analysers are implemented.

using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Planning.Report;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Planning.Ingest;

/// <summary>
/// A single declared, successfully-parsed suite plus its derived scenario identity.
/// </summary>
/// <param name="AbsolutePath">The suite file's absolute path on disk.</param>
/// <param name="RelativePath">
/// The suite's path relative to the analysed root, <c>/</c>-separated regardless of host OS.
/// </param>
/// <param name="ScenarioId">
/// <c>metadata.name</c> when present (and not empty/whitespace-only), else the filename
/// stem — computed character-for-character identically to the CLI's
/// <c>RunCommand.ScenarioName</c>, including the <c>OrdinalIgnoreCase</c> <c>.e2e.yaml</c>
/// suffix strip (REQ-008, flag F-19).
/// </param>
/// <param name="MetadataName">The suite's raw <c>metadata.name</c>, or <see langword="null"/> when absent.</param>
/// <param name="Ast">The suite's fully-built <see cref="ScenarioAst"/>.</param>
internal sealed record PlanSuite(
    string AbsolutePath,
    string RelativePath,
    string ScenarioId,
    string? MetadataName,
    ScenarioAst Ast);

/// <summary>A single <c>step-completed</c> observation attributed to a scenario/step pair.</summary>
/// <param name="RunId">The <c>runId</c> the observation was recorded under.</param>
/// <param name="Verdict">The step's final verdict (never an attempt outcome — REQ-006).</param>
/// <param name="Timestamp">The event's wall-clock timestamp.</param>
internal sealed record PlanStepObservation(string RunId, Verdict Verdict, DateTimeOffset Timestamp);

/// <summary>A single <c>scenario-started</c> event (one per scenario execution).</summary>
/// <param name="RunId">The distinct <c>runId</c> minted for this execution.</param>
/// <param name="ScenarioId">The event's recorded <c>scenarioId</c>.</param>
/// <param name="File">The event's recorded <c>file</c>, or <see langword="null"/> when absent (F-1: always null for every history this engine version produces).</param>
/// <param name="Timestamp">The event's wall-clock timestamp.</param>
internal sealed record PlanScenarioRun(string RunId, string ScenarioId, string? File, DateTimeOffset Timestamp);

/// <summary>Two or more declared suites that derive the same <see cref="PlanSuite.ScenarioId"/> (REQ-008 rule 1).</summary>
/// <param name="ScenarioId">The colliding scenario id.</param>
/// <param name="SuitePaths">The relative paths of every suite sharing it, ordinal-sorted.</param>
internal sealed record PlanScenarioIdCollision(string ScenarioId, IReadOnlyList<string> SuitePaths);

/// <summary>A history run whose recorded <c>file</c> resolves under the analysed root but no longer exists on disk (REQ-008 rule 2).</summary>
/// <param name="ScenarioId">The run's recorded <c>scenarioId</c>.</param>
/// <param name="RecordedFile">The run's recorded <c>file</c> value, verbatim.</param>
/// <param name="RunIds">Every <c>runId</c> observed for this scenario id, ordinal-sorted.</param>
internal sealed record PlanRenamedFileAmbiguity(string ScenarioId, string RecordedFile, IReadOnlyList<string> RunIds);

/// <summary>
/// The correlated event history: every scenario execution, its step-completed observations
/// keyed by (scenarioId, stepId), and the REQ-008 ambiguity classification — the output of
/// <see cref="RunCorrelator"/>.
/// </summary>
/// <param name="Runs">Every <c>scenario-started</c> event successfully parsed from the history.</param>
/// <param name="RunIdsByScenarioId">
/// Every distinct <c>runId</c> observed for each <c>scenarioId</c> that matched at least one
/// declared suite (directly, or via a scenario-id collision).
/// </param>
/// <param name="StepObservations">
/// Every <c>step-completed</c> observation, keyed by the (scenarioId, stepId) pair it was
/// attributed to via its <c>runId</c>.
/// </param>
/// <param name="SkippedLines">EDGE-004: the count of unparseable or unrecognisable event lines.</param>
/// <param name="UnmatchedObservationCount">
/// EDGE-007 / REQ-008 rule 3: the count of scenario or step observations that could not be
/// attributed to any currently-declared suite and were not classified as an ambiguity.
/// </param>
/// <param name="FirstTimestamp">The earliest timestamp across every recognised event, or <see langword="null"/> when the history is empty.</param>
/// <param name="LastTimestamp">The latest timestamp across every recognised event — REQ-006 "now" — or <see langword="null"/> when the history is empty.</param>
/// <param name="Collisions">REQ-008 rule 1: declared suites sharing a scenario id.</param>
/// <param name="RenamedFileAmbiguities">REQ-008 rule 2: history runs referencing a since-renamed file.</param>
/// <param name="RunCount">
/// The number of distinct <c>runId</c>s observed across EVERY recognised event in the
/// history (not just <c>scenario-started</c>) — REQ-003's run count, and F-15's "each
/// scenario execution mints its own runId" definition.
/// </param>
internal sealed record PlanEventHistory(
    IReadOnlyList<PlanScenarioRun> Runs,
    IReadOnlyDictionary<string, IReadOnlyList<string>> RunIdsByScenarioId,
    IReadOnlyDictionary<(string ScenarioId, string StepId), IReadOnlyList<PlanStepObservation>> StepObservations,
    int SkippedLines,
    int UnmatchedObservationCount,
    DateTimeOffset? FirstTimestamp,
    DateTimeOffset? LastTimestamp,
    IReadOnlyList<PlanScenarioIdCollision> Collisions,
    IReadOnlyList<PlanRenamedFileAmbiguity> RenamedFileAmbiguities,
    int RunCount);

/// <summary>
/// Everything the three analysers (<c>Analysis/*</c>) and <c>HandOffHints</c> read: the
/// frozen registry, the effective thresholds, the declared suite set (with parse failures
/// captured separately), and the correlated event history.
/// </summary>
/// <param name="Registry">The frozen provider registry supplied by the caller.</param>
/// <param name="Catalogue">
/// The shape-level step catalogue built from <see cref="Registry"/> via
/// <see cref="EngineExport.BuildCatalogue"/> — the "available vocabulary" source REQ-005's
/// vocabulary-gap analyser and REQ-007's hand-off-hint validation read against the CURRENT
/// registration. <see cref="Vouchfx.Engine.Planning.PlanExport.BuildPlan"/> builds this
/// fail-closed (propagating <see cref="CatalogueExportException"/>) before assembling
/// <see cref="PlanInputs"/>, exactly like <c>vouchfx list</c> / <c>vouchfx schema</c>.
/// </param>
/// <param name="Thresholds">The effective REQ-006 thresholds (defaults or caller overrides).</param>
/// <param name="AnalysedRoot">The absolute path of the analysed suite root (a directory, or a single file's containing directory).</param>
/// <param name="Suites">Every successfully-parsed declared suite.</param>
/// <param name="UnanalysableSuites">Every discovered suite that failed to parse (EDGE-003).</param>
/// <param name="History">The correlated event history.</param>
internal sealed record PlanInputs(
    StepKindRegistry Registry,
    StepCatalogueDocument Catalogue,
    PlanThresholds Thresholds,
    string AnalysedRoot,
    IReadOnlyList<PlanSuite> Suites,
    IReadOnlyList<PlanUnanalysableSuite> UnanalysableSuites,
    PlanEventHistory History);
