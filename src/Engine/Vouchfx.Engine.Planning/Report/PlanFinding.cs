// Vouchfx.Engine.Planning.Report — PlanFinding + PlanStepHistory (M3 Planner, REQ-004
// through REQ-008, REQ-011). Frozen v1 wire shape — see PlanReportContractFreezeTests.
//
// One flat finding record with a `kind` discriminator, deliberately not a polymorphic
// [JsonDerivedType] hierarchy: the whole event-stream contract in this repo is already
// flat-with-nullables for exactly the same reason (EventPayloads.cs: "Wire-format contract
// — FLAT shape"). A single PlanFinding type also makes the EDGE-005 deterministic sort
// (kind, then target, then suite, then step id, then ambiguity reason, then detail — see
// PlanPipeline.Sort's remarks for why the sort needs all six) a single OrderBy/ThenBy chain
// over one type.

using System.Text.Json.Serialization;

namespace Vouchfx.Engine.Planning.Report;

/// <summary>
/// A single coverage gap, vocabulary gap, history-health, or suite-identity-ambiguity
/// finding.
/// </summary>
/// <param name="Kind">
/// The finding-kind discriminator — one of the frozen tokens in
/// <see cref="PlanFindingKinds"/>.
/// </param>
/// <param name="Suite">
/// The relative path of the suite this finding concerns, or <see langword="null"/> when the
/// finding is not scoped to a single suite. MAJOR fix-round: a
/// <see cref="PlanFindingKinds.DependencyMissingStepType"/> or
/// <see cref="PlanFindingKinds.ServiceMissingHttpStep"/> finding is now ALWAYS scoped to its
/// declaring suite (never <see langword="null"/>) — a dependency/service is declared inside
/// exactly one suite's <c>environment</c>, so its vocabulary coverage is judged within that
/// suite alone, and two suites sharing a same-named dependency/service now stay
/// distinguishable by this field. <see langword="null"/> is reserved for finding kinds with
/// no single matching suite at all
/// — today only <see cref="PlanFindingKinds.SuiteIdentityAmbiguous"/> (a scenario-id collision
/// between two declared suites, or history referencing a since-renamed file).
/// </param>
/// <param name="StepId">
/// The step id this finding concerns, or <see langword="null"/> when the finding is not
/// step-scoped (e.g. <c>suite-never-run</c>, a vocabulary gap, or a suite-identity
/// ambiguity).
/// </param>
/// <param name="Target">
/// The service or dependency NAME this finding concerns, or <see langword="null"/> when the
/// finding has no single target (e.g. <c>suite-never-run</c>).
/// </param>
/// <param name="TargetKind">
/// <see cref="PlanTargetKinds.Service"/> or <see cref="PlanTargetKinds.Dependency"/> when
/// <see cref="Target"/> is set; otherwise <see langword="null"/>.
/// </param>
/// <param name="SuggestedTypes">
/// REQ-007 hand-off hint: one or more dotted step types, present in the current
/// registration, that would close this gap. Empty for finding kinds that are not gaps (see
/// <see cref="PlanFindingKinds.IsGap"/>).
/// </param>
/// <param name="SuggestedStepId">
/// REQ-007 hand-off hint: a deterministic, YAML-legal suggested step id for a scaffolded
/// step closing this gap. <see langword="null"/> for finding kinds that are not gaps.
/// </param>
/// <param name="Ambiguous">
/// REQ-008 / EDGE-002 marker: <see langword="true"/> when this finding's suite identity
/// could not be resolved unambiguously against the event history (a scenario-id collision
/// between two declared suites, or history referencing a since-renamed file).
/// <see langword="false"/> otherwise, including for every non-coverage finding kind.
/// </param>
/// <param name="AmbiguityReason">
/// <see cref="PlanAmbiguityReasons.ScenarioIdCollision"/> or
/// <see cref="PlanAmbiguityReasons.HistoryReferencesMissingFile"/> when
/// <see cref="Ambiguous"/> is <see langword="true"/>; otherwise <see langword="null"/>.
/// </param>
/// <param name="History">
/// REQ-006 evidence for a history-health finding (pass/fail/env-error/inconclusive counts,
/// distinct runs, last-observed timestamp, age in days). <see langword="null"/> for gap and
/// ambiguity finding kinds.
/// </param>
/// <param name="Detail">
/// A short, human-readable explanation of the finding. Composed <strong>only</strong> from
/// suite paths, step ids, dotted step types, service/dependency names, dependency-kind
/// tokens declared in <c>environment.dependencies.*.type</c>, verdict counts, timestamps,
/// and threshold numbers (EDGE-006) — never from an observation payload, a captured value,
/// or an environment (secret-bearing) value, so a secret can never reach the report through
/// this field. A dependency-kind token is a reference (the DSL's own declared vocabulary,
/// already surfaced in REQ-003's inventory), not a resolved value, so it carries no secret
/// risk despite being read from the suite's YAML.
/// </param>
/// <param name="RelatedSuites">
/// Fix-round MAJOR (additive, REQ-011-compatible): the relative paths of every OTHER suite
/// this finding's ambiguity concerns, machine-readably — today populated only for a
/// <see cref="PlanFindingKinds.SuiteIdentityAmbiguous"/> finding raised by a scenario-id
/// collision (REQ-008 rule 1), where it duplicates <see cref="Detail"/>'s prose as a
/// structured list so an MCP consumer never has to regex a sentence. Empty for every other
/// finding kind, including a rule-2 renamed-file ambiguity (no declared suite matches that
/// case at all, so there is no suite to relate) and every gap/history-health kind.
/// </param>
public sealed record PlanFinding(
    [property: JsonPropertyOrder(0)] string Kind,
    [property: JsonPropertyOrder(1)] string? Suite,
    [property: JsonPropertyOrder(2)] string? StepId,
    [property: JsonPropertyOrder(3)] string? Target,
    [property: JsonPropertyOrder(4)] string? TargetKind,
    [property: JsonPropertyOrder(5)] IReadOnlyList<string> SuggestedTypes,
    [property: JsonPropertyOrder(6)] string? SuggestedStepId,
    [property: JsonPropertyOrder(7)] bool Ambiguous,
    [property: JsonPropertyOrder(8)] string? AmbiguityReason,
    [property: JsonPropertyOrder(9)] PlanStepHistory? History,
    [property: JsonPropertyOrder(10)] string Detail,
    [property: JsonPropertyOrder(11)] IReadOnlyList<string> RelatedSuites);

/// <summary>
/// REQ-006 evidence attached to a history-health finding: the raw <c>step-completed</c>
/// tallies that justified the classification.
/// </summary>
/// <param name="PassCount">The number of <c>Pass</c> <c>step-completed</c> verdicts observed.</param>
/// <param name="FailCount">The number of <c>Fail</c> <c>step-completed</c> verdicts observed.</param>
/// <param name="EnvErrorCount">The number of <c>EnvironmentError</c> <c>step-completed</c> verdicts observed.</param>
/// <param name="InconclusiveCount">The number of <c>Inconclusive</c> <c>step-completed</c> verdicts observed.</param>
/// <param name="DistinctRuns">The number of distinct <c>runId</c>s this step was observed under.</param>
/// <param name="LastObserved">The timestamp of the most recent <c>step-completed</c> event for this step.</param>
/// <param name="AgeDays">
/// The whole number of days between <see cref="LastObserved"/> and the analysed history's
/// newest event timestamp (REQ-006 "now"), or <see langword="null"/> when not applicable.
/// </param>
public sealed record PlanStepHistory(
    [property: JsonPropertyOrder(0)] int PassCount,
    [property: JsonPropertyOrder(1)] int FailCount,
    [property: JsonPropertyOrder(2)] int EnvErrorCount,
    [property: JsonPropertyOrder(3)] int InconclusiveCount,
    [property: JsonPropertyOrder(4)] int DistinctRuns,
    [property: JsonPropertyOrder(5)] DateTimeOffset? LastObserved,
    [property: JsonPropertyOrder(6)] int? AgeDays);
