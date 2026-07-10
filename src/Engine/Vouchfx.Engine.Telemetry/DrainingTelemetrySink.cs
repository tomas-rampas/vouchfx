// Vouchfx.Engine.Telemetry — DrainingTelemetrySink (S12-G-01, Phase A).
//
// An ITelemetrySink DECORATOR that wraps the local outbox sink with a best-effort HTTP
// drain.  Its contract preserves S10-G-04 EXACTLY:
//
//   SendAsync(evt):
//     (1) await _local.SendAsync(evt)         // UNCHANGED append — the regression guarantee
//     (2) TryDrain()                           // wrapped fail-silent; gated on config + back-off
//
// CRITERION 6 (no S10-G-04 regression): step (1) is the *first* thing SendAsync does and
// is *never* gated, short-circuited, or wrapped away — it is the identical call the bare
// LocalFileTelemetrySink makes, so the local append happens on EVERY SendAsync regardless
// of HTTP configuration or outcome.  When the endpoint is UNCONFIGURED the decorator still
// runs a local-only cap pass (no network), so a never-online install's outbox is still
// bounded; the append behaviour the existing 42 tests assert is untouched because those
// tests exercise the bare local sink, and this decorator delegates the append to that same
// sink without altering it.
//
// THE DRAIN (step 2), wholly inside one try/catch that swallows everything:
//   • skip the NETWORK drain unless TelemetryTransportOptions.IsConfigured AND
//     now >= drainState.NextAttemptUtc (the cross-run exponential back-off — never hammer a
//     down endpoint every run).  Even when backed off (or unconfigured) we still run a
//     local-only cap pass under the lock, so the outbox is bounded against a down endpoint
//     regardless of how long the back-off window lasts (criterion 7 — symmetric);
//   • under OutboxStore's exclusive lock, snapshot the outbox, batch (<=500 lines / a body
//     cap), POST each batch with an Idempotency-Key = SHA-256 hex of the batch bytes;
//   • on the FIRST non-2xx, STOP and keep that batch + all later batches as undelivered;
//   • on success, continue; afterwards the store rewrites the outbox to the undelivered
//     lines and enforces the size/age/line cap (oldest-first);
//   • on ALL-delivered → reset the back-off; on ANY failure (or a fault) → advance it.
//
// Any fault anywhere in the drain leaves the outbox intact (OutboxStore only rewrites on a
// clean path) and is swallowed — telemetry must be invisible to the run.

using System.Security.Cryptography;
using System.Text;

namespace Vouchfx.Engine.Telemetry;

/// <summary>
/// An <see cref="ITelemetrySink"/> decorator that appends locally (unchanged) and then,
/// best-effort, drains the outbox over HTTP with cross-run back-off (S12-G-01).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Criterion 6 (no regression):</strong> <see cref="SendAsync"/> awaits the inner
/// local sink FIRST and unconditionally, so the local append is byte-for-byte the same as
/// the bare <see cref="LocalFileTelemetrySink"/> in every configuration and on every HTTP
/// outcome.  The drain is a separate, fully fail-silent step layered AFTER the append.
/// </para>
/// <para>
/// <strong>Back-off:</strong> the next-attempt instant and failure count persist across
/// runs (<see cref="OutboxDrainState"/>), so a hard-down endpoint backs off geometrically
/// instead of being retried every run.  The clock and jitter are injected for deterministic
/// tests.
/// </para>
/// </remarks>
public sealed class DrainingTelemetrySink : ITelemetrySink
{
    /// <summary>The maximum number of outbox lines per HTTP batch.</summary>
    public const int MaxBatchLines = 500;

    /// <summary>The maximum batch body size in bytes (~1 MB) before a batch is cut short.</summary>
    public const long MaxBatchBytes = 1L * 1024 * 1024;

    /// <summary>
    /// The OVERALL post-run drain budget (~15s).  A single multi-batch drain can walk many
    /// batches (up to the line cap), and a healthy-but-slow endpoint that returns 2xx near
    /// the per-attempt timeout on EVERY batch could otherwise add tens of seconds of
    /// post-verdict latency.  This bounds the whole drain regardless of batch count: when it
    /// elapses the drain stops cleanly, persists progress (the undelivered remainder carries
    /// to the next run), and — because some batches DID deliver — does NOT advance the
    /// back-off.
    /// </summary>
    public static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(15);

    private readonly ITelemetrySink _local;
    private readonly IOutboxHttpClient? _http;
    private readonly OutboxStore _outbox;
    private readonly ITelemetryPaths _paths;
    private readonly OutboxCap _cap;
    private readonly bool _httpConfigured;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<double> _jitter;
    private readonly TimeSpan _drainBudget;

    /// <summary>
    /// Creates a draining decorator over <paramref name="local"/>.
    /// </summary>
    /// <param name="local">The inner local outbox sink (its append behaviour is preserved).</param>
    /// <param name="http">
    /// The HTTP transport, or <see langword="null"/> when the endpoint is unconfigured (the
    /// decorator then runs a local-only cap pass and never makes a network call).
    /// </param>
    /// <param name="paths">The on-disk path seam (outbox + drain-state).</param>
    /// <param name="cap">The outbox ceilings to enforce.</param>
    /// <param name="now">
    /// The UTC clock (inject a fixed value in tests; defaults to the real clock).
    /// </param>
    /// <param name="jitter">
    /// The back-off jitter source returning a fraction in <c>[0,1)</c> (inject a fixed value
    /// in tests; defaults to a thread-safe random).
    /// </param>
    /// <param name="drainBudget">
    /// The overall per-run drain budget; defaults to <see cref="DrainBudget"/> (~15s).  A
    /// non-null, non-positive value disables the budget.  Injectable so a test can drive the
    /// budget-expiry path deterministically without a real multi-second wait.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="local"/> or <paramref name="paths"/> is null.</exception>
    public DrainingTelemetrySink(
        ITelemetrySink local,
        IOutboxHttpClient? http,
        ITelemetryPaths paths,
        OutboxCap cap,
        Func<DateTimeOffset>? now = null,
        Func<double>? jitter = null,
        TimeSpan? drainBudget = null)
    {
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _http = http;
        _httpConfigured = http is not null;
        _cap = cap;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _jitter = jitter ?? DefaultJitter;
        _drainBudget = drainBudget ?? DrainBudget;
        _outbox = new OutboxStore(paths, _now);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Appends to the local outbox FIRST and unconditionally (the regression guarantee),
    /// then attempts a best-effort, fully fail-silent drain.  Only
    /// <see cref="ArgumentNullException"/> (a programming error) and a caller-requested
    /// <see cref="OperationCanceledException"/> can surface — never an HTTP/IO fault.
    /// </remarks>
    public async Task SendAsync(
        TelemetryEvent telemetryEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);

        // (1) UNCHANGED local append — happens on every call, before and independent of any
        // drain.  This is the criterion-6 guarantee: the decorator never gates this away.
        await _local.SendAsync(telemetryEvent, cancellationToken).ConfigureAwait(false);

        // (2) Best-effort drain / cap, wholly fail-silent.
        await TryDrainAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TryDrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            // No endpoint configured → still bound local growth (criterion 7) but never
            // touch the network.  A local-only cap pass under the same lock.
            if (!_httpConfigured)
            {
                _outbox.EnforceCap(_cap);
                return;
            }

            // Cross-run back-off gate: skip the network drain when we are still inside the
            // back-off window from a prior failure.  Reading the state never throws.
            var drainState = OutboxDrainState.Read(_paths.DrainStatePath);
            var now = _now();
            if (!drainState.MayAttempt(now))
            {
                // Still backed off: skip the NETWORK drain, but still enforce the cap so the
                // outbox is bounded even against a down endpoint.  Without this a
                // high-frequency caller could grow the outbox past the cap for up to the full
                // back-off window (<=6h) before the next allowed drain.  This is a local-only
                // operation symmetric with the unconfigured path above (lock + read + a
                // possible rewrite, NO network); it skips fail-silently on lock contention.
                _outbox.EnforceCap(_cap);
                return;
            }

            var drainResult = BatchDrainResult.AllDelivered;

            // Bound the WHOLE drain by the overall budget: a linked CTS that cancels after
            // _drainBudget, so a healthy-but-slow endpoint cannot walk every batch and add
            // tens of seconds of post-run latency.  The budget token feeds the per-batch
            // sends; when it fires mid-drain we stop cleanly and persist progress.
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_drainBudget > TimeSpan.Zero)
            {
                budgetCts.CancelAfter(_drainBudget);
            }

            var outcome = await _outbox.DrainUnderLockAsync(
                _cap,
                async (snapshot, ct) =>
                {
                    var (undelivered, result) = await DrainSnapshotAsync(
                            snapshot, cancellationToken, ct)
                        .ConfigureAwait(false);
                    drainResult = result;
                    return undelivered;
                },
                budgetCts.Token).ConfigureAwait(false);

            // Only touch the back-off when a drain actually ran (the lock was taken and there
            // were lines).  A skip (lock held) or nothing-to-do leaves the state as-is so a
            // contended run does not perturb the back-off schedule.
            if (outcome.Drained)
            {
                switch (drainResult)
                {
                    case BatchDrainResult.AllDelivered:
                        // Clean drain: reset the back-off (next attempt allowed immediately).
                        OutboxDrainState.Reset().Write(_paths.DrainStatePath);
                        break;
                    case BatchDrainResult.Failed:
                        // A non-2xx: advance the exponential back-off.
                        drainState.AfterFailure(now, _jitter()).Write(_paths.DrainStatePath);
                        break;
                    case BatchDrainResult.BudgetExpired:
                        // The overall budget elapsed mid-drain.  Some batches DID deliver and
                        // the undelivered remainder has been persisted by the store rewrite, so
                        // this is NOT a hard failure — leave the back-off untouched and let the
                        // next run pick up where this one stopped.
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Honour cooperative cancellation — do not swallow a caller-requested cancel.
            throw;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or NotSupportedException
                or ArgumentException
                or InvalidOperationException
                or System.Net.Http.HttpRequestException
                or OperationCanceledException)
        {
            // ANY drain fault leaves the outbox intact (the store only rewrites on the clean
            // path) and is swallowed — telemetry must never disrupt the run.  The local
            // append above already succeeded, so the event is not lost.
        }
    }

    // Drain the snapshot batch-by-batch.  Returns the lines that remain UNDELIVERED plus a
    // tri-state result so the caller can decide what to do with the back-off:
    //   • AllDelivered  — every batch acked 2xx; nothing remains.
    //   • Failed        — a batch returned a non-2xx; keep that batch + everything after it
    //                     (the back-off advances).
    //   • BudgetExpired — the overall drain budget elapsed mid-send; keep the in-flight batch
    //                     + everything after it (the back-off is left UNTOUCHED — some batches
    //                     delivered, so this is not a hard failure).
    // In every stop case we preserve order and never drop a line we did not confirm.  The
    // `callerToken` is the run's cancellation (propagated); `budgetToken` is the bounded
    // budget (caught and turned into BudgetExpired rather than thrown).
    private async Task<(IReadOnlyList<string> Undelivered, BatchDrainResult Result)>
        DrainSnapshotAsync(
            IReadOnlyList<string> snapshot,
            CancellationToken callerToken,
            CancellationToken budgetToken)
    {
        if (_http is null)
        {
            // Defensive: TryDrainAsync only calls this when configured.
            return (snapshot, BatchDrainResult.AllDelivered);
        }

        var index = 0;
        while (index < snapshot.Count)
        {
            // The start index of the batch we are ABOUT to send — captured before the await so
            // a budget-cancel mid-send slices from exactly this batch onward.
            var batchStart = index;
            var batch = NextBatch(snapshot, ref index);
            if (batch.Count == 0)
            {
                break;
            }

            var key = ComputeIdempotencyKey(batch);

            DrainAck ack;
            try
            {
                ack = await _http.PostBatchAsync(batch, key, budgetToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                budgetToken.IsCancellationRequested && !callerToken.IsCancellationRequested)
            {
                // The OVERALL budget elapsed (not a caller-requested cancel): stop cleanly and
                // keep this in-flight batch + all remaining lines as undelivered.  Re-sending
                // a batch that may have reached the server is safe — it carries the same
                // idempotency key and the backend de-duplicates it.
                return (Slice(snapshot, batchStart), BatchDrainResult.BudgetExpired);
            }

            if (!ack.Delivered)
            {
                // Stop at the first non-2xx: keep this batch and all remaining lines.
                return (Slice(snapshot, batchStart), BatchDrainResult.Failed);
            }
        }

        // Every batch delivered: nothing remains.
        return (Array.Empty<string>(), BatchDrainResult.AllDelivered);
    }

    // Slice `snapshot` from `start` (inclusive) to the end into a fresh list — the undelivered
    // remainder preserved in original order.
    private static List<string> Slice(IReadOnlyList<string> snapshot, int start)
    {
        var rest = new List<string>(snapshot.Count - start);
        for (var i = start; i < snapshot.Count; i++)
        {
            rest.Add(snapshot[i]);
        }

        return rest;
    }

    // Carve the next batch out of `snapshot` starting at `index`, bounded by MaxBatchLines
    // and MaxBatchBytes.  Always takes at least one line (even an oversized one) so a single
    // very-large line cannot wedge the drain.
    private static List<string> NextBatch(IReadOnlyList<string> snapshot, ref int index)
    {
        var batch = new List<string>(Math.Min(MaxBatchLines, snapshot.Count - index));
        long bytes = 0;

        while (index < snapshot.Count && batch.Count < MaxBatchLines)
        {
            var line = snapshot[index];
            var size = Encoding.UTF8.GetByteCount(line) + 1; // +1 for the '\n' joiner

            if (batch.Count > 0 && bytes + size > MaxBatchBytes)
            {
                break;
            }

            batch.Add(line);
            bytes += size;
            index++;
        }

        return batch;
    }

    // Idempotency-Key = lowercase SHA-256 hex of the EXACT batch bytes (lines joined by
    // '\n', UTF-8).  Stable for identical content, so a batch re-sent after a partial
    // failure carries the same key and the backend can de-duplicate it.
    private static string ComputeIdempotencyKey(IReadOnlyList<string> batch)
    {
        var body = string.Join('\n', batch);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // Default jitter: a thread-safe random fraction in [0,1).  Injected in tests so the
    // back-off instant is exactly computable; never called on the deterministic path.
    private static double DefaultJitter() => Random.Shared.NextDouble();

    // The outcome of a batch-by-batch drain, deciding how the back-off is updated.
    private enum BatchDrainResult
    {
        // Every batch acked 2xx: reset the back-off.
        AllDelivered,

        // A batch returned a non-2xx: advance the exponential back-off.
        Failed,

        // The overall drain budget elapsed mid-send: persist progress but leave the back-off
        // untouched (some batches delivered — this is not a hard failure).
        BudgetExpired,
    }
}
