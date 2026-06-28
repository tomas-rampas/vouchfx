// Typed event record and factory for the "environment-error" event type in the
// vouchfx structured JSON Lines event stream (§14.4, §12.1).
//
// Wire-format contract — FLAT shape:
//   All fields (envelope-shared + payload-specific) are siblings at the root
//   JSON object level.  There is no nested "payload" object.  This mirrors the
//   pattern established by StepCompletedEvent and ScenarioCompletedEvent in
//   Platform.Engine.Abstractions/Events/EventPayloads.cs.
//
// Verdict invariant:
//   EnvironmentErrorEvent.Verdict is ALWAYS Verdict.EnvironmentError.  The
//   field is present on the wire (serialised as "ENV_ERROR" via
//   VerdictJsonConverter) so renderers that key on the verdict token can route
//   the event without inspecting the type discriminator.  It is NEVER "FAIL".

using System.Text.Json.Serialization;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;

namespace Platform.Engine.Orchestration;

// ---------------------------------------------------------------------------
// EnvironmentErrorEvent
// ---------------------------------------------------------------------------

/// <summary>
/// Emitted when the orchestration layer encounters an infrastructure failure
/// that prevents the scenario from running at all (§12.1, §14.4, type
/// <see cref="EventTypes.EnvironmentError"/>).
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Verdict"/> field is always <see cref="Verdict.EnvironmentError"/>
/// — serialised as <c>"ENV_ERROR"</c> by <c>VerdictJsonConverter</c>.  It is
/// <em>never</em> <c>Fail</c>.  Conflating an infra failure with a test
/// failure destroys trust in the tool (CLAUDE.md hard invariant).
/// </para>
/// <para>
/// The wire shape is flat: all fields are siblings at the root JSON object
/// level, consistent with the other typed event records in
/// <c>Platform.Engine.Abstractions</c>.  Unknown fields added in future engine
/// versions will be captured in <c>EventEnvelope.Extra</c> by renderers that
/// only know <see cref="Platform.Engine.Abstractions.Events.EventEnvelope"/>,
/// satisfying the §14 forward-compatibility guarantee.
/// </para>
/// </remarks>
public sealed record EnvironmentErrorEvent
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
    /// Event-type discriminator.  Always
    /// <see cref="EventTypes.EnvironmentError"/> (<c>"environment-error"</c>).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = EventTypes.EnvironmentError;

    /// <summary>
    /// Wall-clock timestamp at which the engine emitted this event.
    /// </summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Identifier of the run this event belongs to.
    /// </summary>
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    /// <summary>
    /// Verdict for this event.  Always <see cref="Verdict.EnvironmentError"/>
    /// (<c>"ENV_ERROR"</c> on the wire).  Hard-coded — this event is
    /// <em>never</em> a <c>Fail</c>.
    /// </summary>
    [JsonPropertyName("verdict")]
    public Verdict Verdict { get; init; } = Verdict.EnvironmentError;

    /// <summary>
    /// The <see cref="OrchestrationErrorKind"/> name (e.g. <c>"ImagePull"</c>,
    /// <c>"HealthGate"</c>, <c>"Discovery"</c>, <c>"Provision"</c>).
    /// </summary>
    [JsonPropertyName("errorKind")]
    public required string ErrorKind { get; init; }

    /// <summary>
    /// The name of the Aspire resource that failed (e.g. <c>"appdb"</c>,
    /// <c>"web"</c>).
    /// </summary>
    [JsonPropertyName("resourceName")]
    public required string ResourceName { get; init; }

    /// <summary>
    /// The registry hostname parsed from the image reference
    /// (e.g. <c>"docker.io"</c>, <c>"registry.example.com:5000"</c>).
    /// Omitted from the wire when <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("registryHost")]
    public string? RegistryHost { get; init; }

    /// <summary>
    /// A short token describing the authentication/connectivity outcome
    /// during an image-pull attempt.  Possible values:
    /// <list type="bullet">
    ///   <item><c>"unauthenticated"</c> — the registry returned 401 or rejected credentials.</item>
    ///   <item><c>"access-denied"</c> — the registry returned 403 / forbidden.</item>
    ///   <item><c>"rate-limited"</c> — the registry rejected the pull due to rate limiting
    ///     (<c>toomanyrequests</c> / 429); the canonical Docker Hub cold-runner failure.</item>
    ///   <item><c>"anonymous"</c> — the pull was attempted without credentials and failed
    ///     for a non-auth reason (image not found, manifest missing, etc.).</item>
    ///   <item><see langword="null"/> — the registry host was unreachable (DNS failure,
    ///     connection refused, network timeout); no auth was attempted.  Also
    ///     <see langword="null"/> for non-ImagePull kinds (HealthGate / Discovery /
    ///     Provision).</item>
    /// </list>
    /// Omitted from the wire when <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("authStatus")]
    public string? AuthStatus { get; init; }

    /// <summary>
    /// A trimmed, single-line summary of the underlying exception message,
    /// capped for display in event streams and logs.  Omitted from the wire
    /// when <see langword="null"/>.
    /// </summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

// ---------------------------------------------------------------------------
// EnvironmentErrorEvents (factory)
// ---------------------------------------------------------------------------

/// <summary>
/// Factory for creating and serialising <see cref="EnvironmentErrorEvent"/>
/// records from a structured <see cref="OrchestrationErrorInfo"/>.
/// </summary>
public static class EnvironmentErrorEvents
{
    /// <summary>
    /// Creates a fully populated <see cref="EnvironmentErrorEvent"/> from a
    /// classified <see cref="OrchestrationErrorInfo"/>.
    /// </summary>
    /// <param name="info">
    /// The structured orchestration failure description produced by
    /// <see cref="OrchestrationErrorClassifier.Classify"/>.
    /// </param>
    /// <param name="runId">
    /// The identifier of the current engine run.
    /// </param>
    /// <param name="timestamp">
    /// The wall-clock time at which the event is being emitted.  Pass an
    /// explicit value (rather than calling <c>DateTimeOffset.UtcNow</c>
    /// inside this method) so callers control the clock and tests remain
    /// deterministic.
    /// </param>
    /// <returns>
    /// A new <see cref="EnvironmentErrorEvent"/> whose
    /// <see cref="EnvironmentErrorEvent.Verdict"/> is always
    /// <see cref="Verdict.EnvironmentError"/>.
    /// </returns>
    public static EnvironmentErrorEvent Create(
        OrchestrationErrorInfo info,
        string runId,
        DateTimeOffset timestamp) =>
        new()
        {
            RunId = runId,
            Timestamp = timestamp,
            Verdict = Verdict.EnvironmentError,
            ErrorKind = info.Kind.ToString(),
            ResourceName = info.ResourceName,
            RegistryHost = info.RegistryHost,
            AuthStatus = info.AuthStatus,
            Detail = string.IsNullOrWhiteSpace(info.Detail) ? null : info.Detail,
        };

    /// <summary>
    /// Serialises an <see cref="EnvironmentErrorEvent"/> to a compact single-line
    /// JSON string suitable for appending to the structured event stream (§14.4).
    /// </summary>
    /// <param name="info">
    /// The structured orchestration failure description.
    /// </param>
    /// <param name="runId">
    /// The identifier of the current engine run.
    /// </param>
    /// <param name="timestamp">
    /// The wall-clock time at which the event is being emitted.
    /// </param>
    /// <returns>
    /// A compact JSON object string with no embedded newline characters,
    /// produced via <see cref="EventStreamJson.ToLine{T}"/> so it uses the
    /// shared options and <c>VerdictJsonConverter</c> (verdict →
    /// <c>"ENV_ERROR"</c>).
    /// </returns>
    public static string ToLine(
        OrchestrationErrorInfo info,
        string runId,
        DateTimeOffset timestamp) =>
        EventStreamJson.ToLine(Create(info, runId, timestamp));
}
