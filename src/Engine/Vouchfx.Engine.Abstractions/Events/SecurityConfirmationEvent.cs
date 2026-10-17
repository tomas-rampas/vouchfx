using System.Text.Json.Serialization;

namespace Vouchfx.Engine.Abstractions.Events;

/// <summary>One successful build-time security probe, with declared and observed facts.</summary>
/// <remarks>No certificate path, password, declaration digest or free-form probe detail is carried.</remarks>
public sealed record SecurityConfirmationEvent
{
    /// <summary>Event envelope generation.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;
    /// <summary>Event schema generation.</summary>
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "v1";
    /// <summary>Event discriminator.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = EventTypes.SecurityConfirmation;
    /// <summary>When this record was emitted; replay does not measure probe freshness.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }
    /// <summary>Run or shared-topology correlation identifier.</summary>
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }
    /// <summary>The declared secured target name.</summary>
    [JsonPropertyName("targetName")]
    public required string TargetName { get; init; }
    /// <summary>Service or declared dependency kind.</summary>
    [JsonPropertyName("targetKind")]
    public required string TargetKind { get; init; }
    /// <summary>The authored TLS or mTLS profile.</summary>
    [JsonPropertyName("declaredProfile")]
    public required string DeclaredProfile { get; init; }
    /// <summary>The authored endpoint selector.</summary>
    [JsonPropertyName("declaredEndpoint")]
    public required string DeclaredEndpoint { get; init; }
    /// <summary>The host-side address reached by the probe.</summary>
    [JsonPropertyName("observedAddress")]
    public required string ObservedAddress { get; init; }
    /// <summary>The TLS protocol negotiated by the probe.</summary>
    [JsonPropertyName("observedProtocol")]
    public required string ObservedProtocol { get; init; }
    /// <summary>Whether a client certificate was resolved; this alone is not identity acceptance.</summary>
    [JsonPropertyName("clientIdentityResolved")]
    public required bool ClientIdentityResolved { get; init; }
    /// <summary>The existing probe level: TransportConfirmed or AuthenticatedRoundTrip. For TLS, no client authentication is implied.</summary>
    [JsonPropertyName("level")]
    public required string Level { get; init; }
    /// <summary>True when replaying a build-time probe without re-probing.</summary>
    [JsonPropertyName("replayed")]
    public required bool Replayed { get; init; }
}
