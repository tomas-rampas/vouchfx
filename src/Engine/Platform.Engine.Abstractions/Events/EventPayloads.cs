// Typed event-payload records for the vouchfx structured JSON Lines event
// stream (§14.4).
//
// Wire-format contract — FLAT shape:
//   All fields (envelope-shared + payload-specific) are siblings at the root
//   JSON object level.  There is no nested "payload" object.  This matches
//   the §14.4 examples where stepId, attempt, verdict, etc. are direct
//   siblings of type/runId/ts.
//
// Each record therefore repeats the envelope-shared fields (v, schemaVersion,
// type, ts, runId, correlationIds) with the same [JsonPropertyName] wire names
// and defaults as EventEnvelope, PLUS its own typed fields.  The type default
// is set to the matching EventTypes constant so callers never have to set it.
//
// Forward compatibility:
//   Because the wire shape is flat, a renderer that only knows EventEnvelope
//   will capture the payload-specific fields in EventEnvelope.Extra via
//   [JsonExtensionData], satisfying the §14 "renderers tolerate unknown fields"
//   guarantee.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Engine.Abstractions.Events;

// ---------------------------------------------------------------------------
// VerdictCounts — nested object inside ScenarioCompletedEvent
// ---------------------------------------------------------------------------

/// <summary>
/// Per-verdict step counts reported at scenario completion (§14.4).
/// </summary>
/// <remarks>
/// Wire keys are lower-camel: <c>pass</c>, <c>fail</c>, <c>envError</c>,
/// <c>inconclusive</c>.
/// </remarks>
public sealed record VerdictCounts
{
    /// <summary>Number of steps that passed.</summary>
    [JsonPropertyName("pass")]
    public int Pass { get; init; }

    /// <summary>Number of steps that failed their assertions.</summary>
    [JsonPropertyName("fail")]
    public int Fail { get; init; }

    /// <summary>Number of steps that terminated with an environment error.</summary>
    [JsonPropertyName("envError")]
    public int EnvError { get; init; }

    /// <summary>Number of steps whose outcome was inconclusive.</summary>
    [JsonPropertyName("inconclusive")]
    public int Inconclusive { get; init; }
}

// ---------------------------------------------------------------------------
// ScenarioStartedEvent
// ---------------------------------------------------------------------------

/// <summary>
/// Emitted when a scenario begins execution (§14.4, type
/// <see cref="EventTypes.ScenarioStarted"/>).
/// </summary>
/// <remarks>
/// Wire shape is flat: <c>scenarioId</c>, <c>file</c>, and
/// <c>contentHash</c> are siblings of the envelope fields at the root JSON
/// object level.
/// </remarks>
public sealed record ScenarioStartedEvent
{
    /// <summary>
    /// Envelope schema generation.  Currently <c>1</c>.
    /// </summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>
    /// Human-readable schema version string, e.g. <c>"v1"</c>.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    /// <summary>
    /// Event-type discriminator.  Defaults to
    /// <see cref="EventTypes.ScenarioStarted"/> (<c>"scenario-started"</c>).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = EventTypes.ScenarioStarted;

    /// <summary>Wall-clock timestamp at which the engine emitted this event.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Identifier of the run this event belongs to.
    /// </summary>
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    /// <summary>
    /// Optional correlation identifiers (e.g. trace/span).  Omitted from the
    /// wire when <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("correlationIds")]
    public IReadOnlyDictionary<string, string>? CorrelationIds { get; init; }

    /// <summary>
    /// Unique identifier of the scenario being executed.
    /// </summary>
    [JsonPropertyName("scenarioId")]
    public required string ScenarioId { get; init; }

    /// <summary>
    /// Path to the <c>.e2e.yaml</c> file that defines this scenario.
    /// Omitted from the wire when <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("file")]
    public string? File { get; init; }

    /// <summary>
    /// SHA-256 content hash of the <c>.e2e.yaml</c> file, used to detect
    /// mutations between runs.  Omitted from the wire when <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; init; }
}

// ---------------------------------------------------------------------------
// StepStartedEvent
// ---------------------------------------------------------------------------

/// <summary>
/// Emitted when a step begins its first (or only) attempt (§14.4, type
/// <see cref="EventTypes.StepStarted"/>).
/// </summary>
/// <remarks>
/// Wire shape is flat: <c>stepId</c>, <c>kind</c>, <c>verifyMode</c>, and
/// <c>timeoutMs</c> are siblings of the envelope fields.
/// </remarks>
public sealed record StepStartedEvent
{
    /// <summary>Envelope schema generation.  Currently <c>1</c>.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>Human-readable schema version string, e.g. <c>"v1"</c>.</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    /// <summary>
    /// Event-type discriminator.  Defaults to
    /// <see cref="EventTypes.StepStarted"/> (<c>"step-started"</c>).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = EventTypes.StepStarted;

    /// <summary>Wall-clock timestamp at which the engine emitted this event.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Identifier of the run this event belongs to.</summary>
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    /// <summary>
    /// Optional correlation identifiers.  Omitted from the wire when
    /// <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("correlationIds")]
    public IReadOnlyDictionary<string, string>? CorrelationIds { get; init; }

    /// <summary>Unique identifier of the step within its scenario.</summary>
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }

    /// <summary>
    /// Step-type discriminator, e.g. <c>"http.rest"</c>,
    /// <c>"mq-publish.kafka"</c>.  Omitted from the wire when
    /// <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>
    /// Verify mode (<c>"IMMEDIATE"</c> or <c>"RETRY"</c>).  Omitted from
    /// the wire when <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("verifyMode")]
    public string? VerifyMode { get; init; }

    /// <summary>
    /// Step-level timeout in milliseconds.  Omitted from the wire when
    /// <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("timeoutMs")]
    public long? TimeoutMs { get; init; }
}

// ---------------------------------------------------------------------------
// StepAttemptEvent
// ---------------------------------------------------------------------------

/// <summary>
/// Emitted for every individual attempt of a step (§14.4, type
/// <see cref="EventTypes.StepAttempt"/>).
/// </summary>
/// <remarks>
/// RETRY steps emit one <c>step-attempt</c> per polling cycle, making the
/// polling timeline renderable without re-running the suite (§14.5).
/// Wire shape is flat: <c>stepId</c>, <c>attempt</c>, <c>tMs</c>,
/// <c>outcome</c>, and <c>observation</c> are siblings of the envelope fields.
/// </remarks>
public sealed record StepAttemptEvent
{
    /// <summary>Envelope schema generation.  Currently <c>1</c>.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>Human-readable schema version string, e.g. <c>"v1"</c>.</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    /// <summary>
    /// Event-type discriminator.  Defaults to
    /// <see cref="EventTypes.StepAttempt"/> (<c>"step-attempt"</c>).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = EventTypes.StepAttempt;

    /// <summary>Wall-clock timestamp at which the engine emitted this event.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Identifier of the run this event belongs to.</summary>
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    /// <summary>
    /// Optional correlation identifiers.  Omitted from the wire when
    /// <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("correlationIds")]
    public IReadOnlyDictionary<string, string>? CorrelationIds { get; init; }

    /// <summary>Unique identifier of the step within its scenario.</summary>
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }

    /// <summary>
    /// One-based attempt counter.  The first attempt is <c>1</c>.
    /// </summary>
    [JsonPropertyName("attempt")]
    public int Attempt { get; init; }

    /// <summary>
    /// Elapsed wall-clock time for this attempt in milliseconds.
    /// </summary>
    [JsonPropertyName("tMs")]
    public long TMs { get; init; }

    /// <summary>
    /// Outcome of this individual attempt.  <see langword="null"/> when the
    /// engine has not yet resolved an outcome (e.g. a mid-RETRY poll).
    /// Omitted from the wire when <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("outcome")]
    public Verdict? Outcome { get; init; }

    /// <summary>
    /// Provider-supplied observation data (diff, matched count, raw response,
    /// etc.) captured during this attempt.  Omitted from the wire when
    /// <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("observation")]
    public JsonElement? Observation { get; init; }
}

// ---------------------------------------------------------------------------
// CapturedVar + SubstitutionRef — provenance metadata for G-01
// ---------------------------------------------------------------------------

/// <summary>
/// Records the provenance of a single JSONPath capture operation that was
/// declared in a step's <c>capture</c> block (S04-G-01, DSL §3).
/// </summary>
/// <remarks>
/// <para>
/// This record carries only metadata — the captured VALUE is deliberately
/// absent.  This is secret-safe by construction (§17): a captured value may
/// derive from a secret reference and must never appear in the event stream.
/// </para>
/// </remarks>
/// <param name="Name">
/// The author-supplied variable name declared in the YAML <c>capture</c> block
/// (e.g. <c>"orderId"</c>).
/// </param>
/// <param name="Path">
/// The JSONPath expression used to extract the value from the step's response
/// body (e.g. <c>"$.id"</c>).
/// </param>
/// <param name="Matched">
/// <see langword="true"/> when the JSONPath expression matched at least one
/// node in the response body; <see langword="false"/> when the expression
/// yielded no match (which also sets the step verdict to
/// <c>Inconclusive</c> with reason <c>upstream-capture-unmet</c>).
/// </param>
public sealed record CapturedVar(
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("path")] string Path,
    [property: System.Text.Json.Serialization.JsonPropertyName("matched")] bool Matched);

/// <summary>
/// Records the provenance of a single <c>{placeholder}</c> substitution that
/// was detected at compile time in a substitutable field of a step
/// (S04-G-01, DSL §3, S04-B-03).
/// </summary>
/// <remarks>
/// <para>
/// Provenance is derived <em>at compile time</em> from the field text (which
/// placeholder names appear) — not at runtime — so no value ever flows into
/// this record.  This is secret-safe by construction (§17).
/// </para>
/// </remarks>
/// <param name="Placeholder">
/// The placeholder name as it appeared in the template (e.g. <c>"orderId"</c>
/// for the token <c>{orderId}</c>).
/// </param>
/// <param name="OriginStepId">
/// The step identifier that first captured the variable (i.e. the step whose
/// <c>capture</c> map declares <paramref name="Placeholder"/> as a key).
/// <see langword="null"/> when the variable originates from the <c>variables</c>
/// block or is otherwise not traceable to a prior capture.
/// </param>
/// <param name="SecretDerived">
/// <see langword="true"/> when the placeholder name resolves to a value that
/// was obtained from a secret reference (<c>${secret:…}</c>).  Always
/// <see langword="false"/> in the current MVP (secret resolution is not yet
/// implemented for this sprint); the flag is present so the wire format is
/// stable for future sprints.
/// </param>
public sealed record SubstitutionRef(
    [property: System.Text.Json.Serialization.JsonPropertyName("placeholder")] string Placeholder,
    [property: System.Text.Json.Serialization.JsonPropertyName("originStepId")] string? OriginStepId,
    [property: System.Text.Json.Serialization.JsonPropertyName("secretDerived")] bool SecretDerived);

// ---------------------------------------------------------------------------
// StepCompletedEvent
// ---------------------------------------------------------------------------

/// <summary>
/// Emitted when a step is fully resolved — by success, exhausted retries,
/// timeout, or environment error (§14.4, type
/// <see cref="EventTypes.StepCompleted"/>).
/// </summary>
/// <remarks>
/// Wire shape is flat: <c>stepId</c>, <c>verdict</c>, and <c>durationMs</c>
/// are siblings of the envelope fields.
/// </remarks>
public sealed record StepCompletedEvent
{
    /// <summary>Envelope schema generation.  Currently <c>1</c>.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>Human-readable schema version string, e.g. <c>"v1"</c>.</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    /// <summary>
    /// Event-type discriminator.  Defaults to
    /// <see cref="EventTypes.StepCompleted"/> (<c>"step-completed"</c>).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = EventTypes.StepCompleted;

    /// <summary>Wall-clock timestamp at which the engine emitted this event.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Identifier of the run this event belongs to.</summary>
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    /// <summary>
    /// Optional correlation identifiers.  Omitted from the wire when
    /// <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("correlationIds")]
    public IReadOnlyDictionary<string, string>? CorrelationIds { get; init; }

    /// <summary>Unique identifier of the step within its scenario.</summary>
    [JsonPropertyName("stepId")]
    public required string StepId { get; init; }

    /// <summary>
    /// Final verdict for the step (§12.1).  Serialised to its canonical wire
    /// token via <see cref="VerdictJsonConverter"/>.
    /// </summary>
    [JsonPropertyName("verdict")]
    public required Verdict Verdict { get; init; }

    /// <summary>
    /// Total wall-clock duration of all attempts combined, in milliseconds.
    /// </summary>
    [JsonPropertyName("durationMs")]
    public required long DurationMs { get; init; }

    /// <summary>
    /// Per-capture provenance records for this step (S04-G-01).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One entry per element in the step's <c>capture</c> block.
    /// <see langword="null"/> (and omitted from the wire) when the step
    /// declares no <c>capture</c> entries.
    /// </para>
    /// <para>
    /// No captured VALUE is ever included — this is secret-safe by construction (§17).
    /// </para>
    /// <para>
    /// Renderers that do not understand this field will ignore it via
    /// <c>[JsonExtensionData]</c> on <see cref="EventEnvelope"/> (§14 forward-compat).
    /// </para>
    /// </remarks>
    [JsonPropertyName("captured")]
    public IReadOnlyList<CapturedVar>? Captured { get; init; }

    /// <summary>
    /// Compile-time substitution provenance records for this step (S04-G-01).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One entry per distinct <c>{placeholder}</c> token found in any
    /// substitutable field of the step at compile time.
    /// <see langword="null"/> (and omitted from the wire) when no substitutable
    /// fields contain placeholders.
    /// </para>
    /// <para>
    /// No runtime VALUE is ever included — provenance is derived at compile time
    /// and is secret-safe by construction (§17).
    /// </para>
    /// <para>
    /// Renderers that do not understand this field will ignore it via
    /// <c>[JsonExtensionData]</c> on <see cref="EventEnvelope"/> (§14 forward-compat).
    /// </para>
    /// </remarks>
    [JsonPropertyName("substitutions")]
    public IReadOnlyList<SubstitutionRef>? Substitutions { get; init; }
}

// ---------------------------------------------------------------------------
// ScenarioCompletedEvent
// ---------------------------------------------------------------------------

/// <summary>
/// Emitted when a scenario finishes execution (§14.4, type
/// <see cref="EventTypes.ScenarioCompleted"/>).
/// </summary>
/// <remarks>
/// Wire shape is flat: <c>scenarioId</c>, <c>verdict</c>, and <c>counts</c>
/// are siblings of the envelope fields.  <c>counts</c> is a nested object
/// with its own wire keys (<c>pass</c>, <c>fail</c>, <c>envError</c>,
/// <c>inconclusive</c>).
/// </remarks>
public sealed record ScenarioCompletedEvent
{
    /// <summary>Envelope schema generation.  Currently <c>1</c>.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>Human-readable schema version string, e.g. <c>"v1"</c>.</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    /// <summary>
    /// Event-type discriminator.  Defaults to
    /// <see cref="EventTypes.ScenarioCompleted"/> (<c>"scenario-completed"</c>).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = EventTypes.ScenarioCompleted;

    /// <summary>Wall-clock timestamp at which the engine emitted this event.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Identifier of the run this event belongs to.</summary>
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    /// <summary>
    /// Optional correlation identifiers.  Omitted from the wire when
    /// <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("correlationIds")]
    public IReadOnlyDictionary<string, string>? CorrelationIds { get; init; }

    /// <summary>
    /// Unique identifier of the scenario that completed.
    /// </summary>
    [JsonPropertyName("scenarioId")]
    public required string ScenarioId { get; init; }

    /// <summary>
    /// Aggregate verdict for the scenario (§12.1).  Serialised to its
    /// canonical wire token via <see cref="VerdictJsonConverter"/>.
    /// </summary>
    [JsonPropertyName("verdict")]
    public required Verdict Verdict { get; init; }

    /// <summary>
    /// Per-verdict step counts for this scenario.
    /// </summary>
    [JsonPropertyName("counts")]
    public required VerdictCounts Counts { get; init; }
}
