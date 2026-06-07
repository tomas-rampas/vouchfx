// Event-stream envelope for the vouchfx structured JSON Lines stream (§14.4).
// This type is the sole substrate consumed by every renderer (terminal, HTML,
// JUnit XML, dashboard) and the Healer agent — see §14.1.
//
// Design constraints:
//   • Zero third-party dependencies: Platform.Engine.Abstractions is a leaf
//     library; only in-box System.Text.Json is permitted.
//   • Forward compatibility: unknown fields must be preserved in Extra so that
//     older renderers remain useful against newer engine output without
//     modification — the core §14 "renderers tolerate unknown fields" guarantee.
//   • Wire names are fixed by [JsonPropertyName] to match the §14.4 examples
//     exactly ("v", "ts", "runId", "schemaVersion", …); no naming policy is
//     applied that would alter these names.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Engine.Abstractions.Events;

/// <summary>
/// Versioned envelope that wraps every event emitted to the structured JSON Lines
/// stream (§14.4).  Per-event-type payload (stepId, attempt, verdict, …) rides in
/// <see cref="Extra"/> as extension data, keeping the envelope open for forward
/// evolution without breaking existing renderers.
/// </summary>
/// <remarks>
/// All renderers MUST tolerate unknown keys: they may arrive as new event types
/// are introduced in later engine releases.  Deserialisation into this record
/// captures those keys verbatim in <see cref="Extra"/> so they survive a
/// re-serialisation unchanged.
/// </remarks>
public sealed record EventEnvelope
{
    /// <summary>
    /// Envelope schema generation.  Currently <c>1</c>.  A change here signals
    /// a breaking structural change to the envelope itself (not to individual
    /// event types).
    /// </summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    /// <summary>
    /// Human-readable schema version string, e.g. <c>"v1"</c>.  Carried
    /// alongside <see cref="Version"/> for the benefit of consumers that prefer
    /// a string tag over an integer.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";

    /// <summary>
    /// Event-type discriminator, e.g. <c>"suite-started"</c>,
    /// <c>"step-attempt"</c>, <c>"step-completed"</c>.  Use the constants on
    /// <see cref="EventTypes"/> at call sites for type-safety.
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Wall-clock timestamp at which the engine emitted this event.
    /// </summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Identifier of the run this event belongs to.  Stable across all events
    /// in a single engine invocation and used to correlate the stream with
    /// external observability data.
    /// </summary>
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    /// <summary>
    /// Optional map of correlation identifiers (e.g. <c>"trace"</c>,
    /// <c>"span"</c>) that allow the event to be linked to entries in the
    /// observability stack (§12).  Omitted from the wire when <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("correlationIds")]
    public IReadOnlyDictionary<string, string>? CorrelationIds { get; init; }

    /// <summary>
    /// Extension-data bag that captures every JSON property not mapped to a
    /// typed field above.  This is the mechanism that makes renderers tolerant
    /// of unknown fields emitted by a newer engine release: the property is
    /// round-tripped verbatim, so a renderer can still re-serialise an envelope
    /// it only partially understands.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonElement"/> values preserve the full fidelity of the
    /// original JSON — nested objects, arrays, and all scalar types — so rich
    /// per-event payloads (observation diffs, provider lists, …) are lossless.
    /// </remarks>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extra { get; init; }
}
