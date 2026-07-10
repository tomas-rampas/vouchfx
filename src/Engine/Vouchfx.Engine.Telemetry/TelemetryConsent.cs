// Vouchfx.Engine.Telemetry — TelemetryConsent (S10-G-04).
//
// The tri-state consent enum + the on-disk store record.  Consent is OPT-IN: the
// only state in which anything is ever collected or sent is Enabled, and that state
// is reachable ONLY by the user explicitly running `vouchfx telemetry enable`.

using System.Text.Json.Serialization;

namespace Vouchfx.Engine.Telemetry;

/// <summary>
/// The user's telemetry consent decision — a tri-state so "never asked" is distinct
/// from an explicit "no".
/// </summary>
public enum TelemetryConsent
{
    /// <summary>
    /// The user has not yet made a decision.  This is the DEFAULT and the state a
    /// missing or corrupt store reads back as.  Nothing is ever collected or sent
    /// while Undecided — only the one-time first-run notice may be shown.
    /// </summary>
    Undecided = 0,

    /// <summary>
    /// The user explicitly opted IN (<c>telemetry enable</c>).  This is the ONLY
    /// state in which a telemetry event may be built and written to a sink.
    /// </summary>
    Enabled = 1,

    /// <summary>
    /// The user explicitly opted OUT (<c>telemetry disable</c>).  Nothing is ever
    /// collected or sent; the install id and outbox are deleted on transition.
    /// </summary>
    Disabled = 2,
}

/// <summary>
/// The persisted telemetry state (the on-disk shape of
/// <c>{configDir}/vouchfx/telemetry.json</c>).
/// </summary>
/// <remarks>
/// <para>
/// This record carries ONLY consent/lifecycle bookkeeping — never any run data.
/// The <see cref="InstallId"/> is minted ONLY when the user enables telemetry and is
/// deleted when they disable it, so an opted-out or undecided install carries no
/// identifier at all.
/// </para>
/// </remarks>
public sealed record TelemetryState
{
    /// <summary>
    /// The current schema version of the persisted store, so a future field
    /// addition can be migrated rather than misread.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// The user's consent decision.  Defaults to <see cref="TelemetryConsent.Undecided"/>
    /// — the safe default a missing/corrupt store also reads back as.
    /// </summary>
    [JsonPropertyName("consent")]
    public TelemetryConsent Consent { get; init; } = TelemetryConsent.Undecided;

    /// <summary>
    /// Whether the one-time first-run notice has already been shown.  Set the first
    /// time the notice is written so it never nags the user again.
    /// </summary>
    [JsonPropertyName("noticeShown")]
    public bool NoticeShown { get; init; }

    /// <summary>
    /// The opaque install identifier, minted ONLY on enable and removed on disable.
    /// <see langword="null"/> whenever consent is not <see cref="TelemetryConsent.Enabled"/>.
    /// </summary>
    [JsonPropertyName("installId")]
    public Guid? InstallId { get; init; }
}
