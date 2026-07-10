// Vouchfx.Engine.Orchestration — WebhookCaptureBuffer (S07-F-01a, §5).
//
// A thread-safe append-and-snapshot store of CapturedWebhookRequest for ONE host-owned
// ephemeral webhook listener.  The listener (Kestrel, Default ALC) appends one entry per
// inbound request from its request-handling pipeline (concurrent); a later assertion step
// reads an immutable snapshot through the typed ScriptGlobalVariables.Webhooks accessor.
//
// Memory-model note (§5): this object lives wholly in the Default ALC, owned by the
// runner/topology.  Nothing static holds it; it is reached by the script only by-reference
// through the WebhookCaptureAccessor wrapped in ScriptGlobalVariables.  Clear() resets it
// per scenario so a capture from scenario A cannot satisfy scenario B in a shared topology.

using System.Collections.Generic;
using Vouchfx.Engine.Abstractions.Webhooks;

namespace Vouchfx.Engine.Orchestration.HostResources;

/// <summary>
/// A thread-safe append-and-snapshot buffer of <see cref="CapturedWebhookRequest"/> for a
/// single host-owned ephemeral webhook listener (S07-F-01a).
/// </summary>
/// <remarks>
/// <para>
/// The owning <see cref="WebhookListener"/> appends one entry per inbound request from its
/// Kestrel request-handling pipeline, which may run multiple requests concurrently — so
/// <see cref="Append"/> is guarded by a lock.  A later <c>webhook-listen.http</c> assertion
/// step reads an immutable <see cref="Snapshot"/> through the typed
/// <c>ScriptGlobalVariables.Webhooks</c> accessor; the snapshot is a copy, so readers are
/// never affected by concurrent appends.
/// </para>
/// <para>
/// <strong>Bounded (memory-DoS mitigation):</strong> the buffer caps the number of retained
/// requests at <see cref="MaxBufferedRequests"/>.  Once full, further <see cref="Append"/>
/// calls DROP the new entry rather than evicting the oldest — a flood of forged/late callbacks
/// cannot displace the early legitimate callbacks the assertion is waiting on, and cannot grow
/// host memory without bound.
/// </para>
/// <para>
/// <strong>Memory model (§5):</strong> the buffer lives in the Default
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, owned by the runner/topology.
/// Nothing static holds it; the emitted script reaches it only by-reference through the
/// accessor.  <see cref="Clear"/> resets it between scenarios so a webhook captured in one
/// scenario cannot satisfy an assertion in another within a shared topology.
/// </para>
/// </remarks>
public sealed class WebhookCaptureBuffer
{
    /// <summary>
    /// The maximum number of captured requests this buffer retains.  Appends beyond this cap
    /// are dropped (the early legitimate callbacks are preserved) so a flood cannot exhaust
    /// host memory.  Webhook scenarios assert over a handful of callbacks, so 1000 is ample
    /// headroom for legitimate traffic while bounding the worst case.
    /// </summary>
    public const int MaxBufferedRequests = 1000;

    private readonly object _gate = new();
    private readonly List<CapturedWebhookRequest> _items = new();

    /// <summary>
    /// Appends <paramref name="request"/> to the buffer, unless the buffer has already reached
    /// <see cref="MaxBufferedRequests"/> in which case the request is silently DROPPED.
    /// Thread-safe: safe to call concurrently from multiple in-flight listener requests.
    /// </summary>
    /// <remarks>
    /// When full, the buffer drops the <em>new</em> entry rather than evicting the oldest: the
    /// early callbacks are the ones a <c>webhook-listen.http</c> assertion needs to retain, and
    /// a later flood of forged callbacks must not be able to displace them.
    /// </remarks>
    /// <param name="request">The captured request to append; must not be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the request was stored; <see langword="false"/> when the
    /// buffer was full and the request was dropped.
    /// </returns>
    /// <exception cref="System.ArgumentNullException">
    /// Thrown when <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    public bool Append(CapturedWebhookRequest request)
    {
        System.ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_items.Count >= MaxBufferedRequests)
            {
                // Buffer full: drop the NEW entry (retain the early legitimate callbacks).
                return false;
            }

            _items.Add(request);
            return true;
        }
    }

    /// <summary>
    /// Returns an immutable snapshot of the buffer's contents in capture order.
    /// </summary>
    /// <returns>
    /// A read-only copy of the captured requests; unaffected by subsequent
    /// <see cref="Append"/> or <see cref="Clear"/> calls.  Never <see langword="null"/>.
    /// </returns>
    public IReadOnlyList<CapturedWebhookRequest> Snapshot()
    {
        lock (_gate)
        {
            return _items.ToArray();
        }
    }

    /// <summary>
    /// Gets the number of requests currently captured.  Thread-safe.
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

    /// <summary>
    /// Empties the buffer.  Called per scenario (on the isolation teardown seam) so a
    /// webhook captured in one scenario cannot satisfy an assertion in another within a
    /// shared topology (S07-F-01a).
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }
}
