// Platform.Engine.Telemetry — IOutboxHttpClient + DrainAck (S12-G-01, Phase A).
//
// The HTTP-transport seam for draining the local outbox.  It is deliberately narrow —
// one batch POST and one forget POST — so it is trivially mockable in tests (over a
// fake HttpMessageHandler) and the DrainingTelemetrySink depends only on this contract,
// never on HttpClient directly.
//
// The seam takes the RAW outbox NDJSON lines (the exact bytes the local sink wrote) and
// forwards them verbatim — it never re-serialises a TelemetryEvent, so it physically
// cannot widen what is sent beyond the allowlist that produced those lines.

namespace Platform.Engine.Telemetry;

/// <summary>
/// The result of a single batch POST: whether the server accepted it (a 2xx) and how
/// many lines it acknowledged (S12-G-01).
/// </summary>
/// <param name="Delivered">
/// <see langword="true"/> when the server returned any 2xx status — the batch is then
/// considered delivered and its lines may be dropped from the outbox.
/// </param>
/// <param name="AcceptedCount">
/// The number of lines the server reported it accepted.  Advisory only in v1 (the
/// decorator treats a 2xx as "the whole batch is delivered"); carried so a future
/// backend can report partial acceptance without a contract change.
/// </param>
public readonly record struct DrainAck(bool Delivered, int AcceptedCount)
{
    /// <summary>A non-delivered acknowledgement (a non-2xx response or a transport fault).</summary>
    public static DrainAck NotDelivered => new(false, 0);
}

/// <summary>
/// The HTTP transport that drains batches of outbox lines to the telemetry backend and
/// issues the GDPR-style "forget" request (S12-G-01).
/// </summary>
/// <remarks>
/// Implementations forward the RAW NDJSON outbox lines verbatim (never re-serialising a
/// <see cref="TelemetryEvent"/>), so the privacy allowlist that produced those lines is
/// the only thing that can ever be on the wire.  All methods are best-effort by
/// contract: a network fault surfaces as a non-delivered <see cref="DrainAck"/> (for a
/// batch) or a swallowed no-op (for forget), never as a fault that disrupts the run.
/// </remarks>
public interface IOutboxHttpClient
{
    /// <summary>
    /// POSTs a batch of outbox lines to the ingest endpoint as
    /// <c>application/x-ndjson</c>, with the supplied idempotency key.
    /// </summary>
    /// <param name="ndjsonLines">
    /// The RAW outbox lines to send (byte-identical to what the local sink wrote); joined
    /// by <c>\n</c> into the request body.  Never re-serialised.
    /// </param>
    /// <param name="idempotencyKey">
    /// A stable key for the batch (the SHA-256 hex of the batch bytes) so a re-sent batch
    /// after a partial failure is de-duplicated by the backend.
    /// </param>
    /// <param name="cancellationToken">Propagated to the HTTP send.</param>
    /// <returns>
    /// A <see cref="DrainAck"/> whose <see cref="DrainAck.Delivered"/> is <see langword="true"/>
    /// only on a 2xx.  Never throws for an ordinary transport/HTTP fault — it returns
    /// <see cref="DrainAck.NotDelivered"/>.
    /// </returns>
    Task<DrainAck> PostBatchAsync(
        IReadOnlyList<string> ndjsonLines,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Issues a best-effort "forget" request for the supplied install id (called when the
    /// user disables telemetry), so the backend can erase any data linked to that install.
    /// </summary>
    /// <param name="installId">The install id to forget.</param>
    /// <param name="cancellationToken">Propagated to the HTTP send.</param>
    /// <returns>A task that completes when the request finishes or is abandoned (fail-silent).</returns>
    Task ForgetAsync(Guid installId, CancellationToken cancellationToken);
}
