// Platform.Engine.Orchestration — TraceCaptureBuffer (Phase C, trace-expect.otlp, §5).
//
// A thread-safe append-and-snapshot RING BUFFER of CapturedSpan for ONE host-owned ephemeral
// OTLP/HTTP receiver.  The receiver (Kestrel, Default ALC) appends spans parsed from each
// inbound OTLP export request from its request-handling pipeline (concurrent); a later
// trace-expect.otlp assertion step reads an immutable snapshot through the typed
// ScriptGlobalVariables.Traces accessor.  Mirrors WebhookCaptureBuffer (S07-F-01a precedent)
// with one deliberate difference: this buffer is a RING (oldest-out) rather than
// drop-the-newest, documented on MaxBufferedSpans below.
//
// Memory-model note (§5): this object lives wholly in the Default ALC, owned by the
// runner/topology.  Nothing static holds it; it is reached by the script only by-reference
// through the TraceCaptureAccessor wrapped in ScriptGlobalVariables.

using System.Collections.Generic;
using Platform.Engine.Abstractions.Traces;

namespace Platform.Engine.Orchestration.HostResources;

/// <summary>
/// A thread-safe append-and-snapshot RING BUFFER of <see cref="CapturedSpan"/> for a single
/// host-owned ephemeral OTLP/HTTP receiver (Phase C, <c>trace-expect.otlp</c>).
/// </summary>
/// <remarks>
/// <para>
/// The owning <see cref="OtlpReceiver"/> appends spans parsed from each inbound OTLP export
/// request from its Kestrel request-handling pipeline, which may run multiple requests
/// concurrently — so <see cref="Append"/> is guarded by a lock.  A later
/// <c>trace-expect.otlp</c> assertion step reads an immutable <see cref="Snapshot"/> through
/// the typed <c>ScriptGlobalVariables.Traces</c> accessor; the snapshot is a copy, so readers
/// are never affected by concurrent appends.
/// </para>
/// <para>
/// <strong>Bounded, RING-BUFFER policy (memory-DoS mitigation — deliberately different from
/// <c>WebhookCaptureBuffer</c>'s drop-the-newest policy):</strong> the buffer caps the number
/// of retained spans at <see cref="MaxBufferedSpans"/>.  Once full, a further
/// <see cref="Append"/> call EVICTS the OLDEST span and appends the new one.  This is the
/// opposite policy from <c>WebhookCaptureBuffer</c> (which drops the new entry) because a real
/// OpenTelemetry SDK batches and exports spans continuously and automatically for the whole
/// lifetime of a scenario's SUT process — an author has no control over export cadence the way
/// a webhook caller is a single deliberate callback. Retaining only the MOST RECENT spans keeps
/// the buffer relevant to the business transaction the current scenario is asserting against,
/// rather than pinning early, possibly-unrelated boot-time spans forever. 10,000 spans is ample
/// headroom for a single scenario's transaction while bounding worst-case memory.
/// </para>
/// <para>
/// <strong>Memory model (§5):</strong> the buffer lives in the Default
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, owned by the runner/topology.
/// Nothing static holds it; the emitted script reaches it only by-reference through the
/// accessor.
/// </para>
/// </remarks>
public sealed class TraceCaptureBuffer
{
    /// <summary>
    /// The maximum number of captured spans this buffer retains.  Once full, the OLDEST span
    /// is evicted to make room for each new append (ring buffer) — see the class remarks for
    /// why this differs from <c>WebhookCaptureBuffer.MaxBufferedRequests</c>'s drop-the-newest
    /// policy.
    /// </summary>
    public const int MaxBufferedSpans = 10_000;

    private readonly object _gate = new();
    private readonly Queue<CapturedSpan> _items = new();

    /// <summary>
    /// Appends <paramref name="span"/> to the buffer.  When the buffer has already reached
    /// <see cref="MaxBufferedSpans"/>, the OLDEST span is evicted first (ring-buffer,
    /// oldest-out).  Thread-safe: safe to call concurrently from multiple in-flight receiver
    /// requests.
    /// </summary>
    /// <param name="span">The captured span to append; must not be <see langword="null"/>.</param>
    /// <exception cref="System.ArgumentNullException">
    /// Thrown when <paramref name="span"/> is <see langword="null"/>.
    /// </exception>
    public void Append(CapturedSpan span)
    {
        System.ArgumentNullException.ThrowIfNull(span);
        lock (_gate)
        {
            if (_items.Count >= MaxBufferedSpans)
            {
                // Ring buffer: evict the OLDEST span to make room for the new one (see the
                // class remarks for why this differs from WebhookCaptureBuffer's policy).
                _items.Dequeue();
            }

            _items.Enqueue(span);
        }
    }

    /// <summary>
    /// Returns an immutable snapshot of the buffer's contents in capture order (oldest first).
    /// </summary>
    /// <returns>
    /// A read-only copy of the captured spans; unaffected by subsequent <see cref="Append"/>
    /// calls.  Never <see langword="null"/>.
    /// </returns>
    public IReadOnlyList<CapturedSpan> Snapshot()
    {
        lock (_gate)
        {
            return _items.ToArray();
        }
    }

    /// <summary>
    /// Gets the number of spans currently captured.  Thread-safe.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }
}
