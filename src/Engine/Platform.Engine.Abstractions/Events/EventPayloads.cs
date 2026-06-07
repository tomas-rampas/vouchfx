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
