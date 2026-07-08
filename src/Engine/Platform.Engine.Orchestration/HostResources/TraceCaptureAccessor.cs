// Platform.Engine.Orchestration — TraceCaptureAccessor (Phase C, trace-expect.otlp, §5).
//
// The concrete ITraceCaptureAccessor that the runner constructs in the Default ALC and passes
// by-reference into ScriptGlobalVariables.Traces.  It wraps the per-receiver TraceCaptureBuffer
// instances, keyed by the receiver's logical VarName, and projects an immutable snapshot of one
// receiver's buffer to the emitted script on demand.  Mirrors WebhookCaptureAccessor exactly
// (S07-F-01a precedent).
//
// Memory-model note (§5): this object and the buffers it holds live wholly in the Default ALC,
// owned by the runner/topology.  Nothing static holds it; the emitted script reaches it only
// by-reference via ScriptGlobalVariables.Traces.  An unknown name yields an empty list (never a
// throw), mirroring NullTraceCaptureAccessor's safe-empty contract.

using System;
using System.Collections.Generic;
using Platform.Engine.Abstractions.Traces;

namespace Platform.Engine.Orchestration.HostResources;

/// <summary>
/// The concrete <see cref="ITraceCaptureAccessor"/> the runner passes into
/// <c>ScriptGlobalVariables.Traces</c>: it wraps the per-receiver <see cref="TraceCaptureBuffer"/>
/// instances, keyed by each receiver's logical <c>VarName</c>, and projects an immutable
/// snapshot of one buffer to the emitted script (Phase C).
/// </summary>
/// <remarks>
/// <para>
/// Built in the Default <see cref="System.Runtime.Loader.AssemblyLoadContext"/> by the runner
/// at topology-up — once per scenario run — and handed across the §5 boundary by-reference
/// (exactly like the webhook-capture accessor).  The buffers it holds are the same instances
/// the host-owned <see cref="OtlpReceiver"/>s append to; reads therefore see live captures.
/// </para>
/// <para>
/// <see cref="GetCaptured"/> returns an empty list for an unknown name rather than throwing,
/// matching <see cref="NullTraceCaptureAccessor"/>: a step asserting against a receiver that
/// was never registered observes "no spans" and fails its assertion cleanly, rather than
/// crashing the run.
/// </para>
/// </remarks>
public sealed class TraceCaptureAccessor : ITraceCaptureAccessor
{
    private static readonly IReadOnlyList<CapturedSpan> Empty =
        Array.Empty<CapturedSpan>();

    private readonly IReadOnlyDictionary<string, TraceCaptureBuffer> _buffers;

    /// <summary>
    /// Initialises a new accessor over the given per-receiver buffer map.
    /// </summary>
    /// <param name="buffers">
    /// A map from receiver logical name (<c>HostResourceRequirement.VarName</c>) to its capture
    /// buffer; must not be <see langword="null"/>.  May be empty (no receivers), in which case
    /// every lookup returns an empty list.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="buffers"/> is <see langword="null"/>.
    /// </exception>
    public TraceCaptureAccessor(IReadOnlyDictionary<string, TraceCaptureBuffer> buffers)
    {
        _buffers = buffers ?? throw new ArgumentNullException(nameof(buffers));
    }

    /// <inheritdoc />
    public IReadOnlyList<CapturedSpan> GetCaptured(string receiverVarName)
    {
        if (!string.IsNullOrEmpty(receiverVarName) &&
            _buffers.TryGetValue(receiverVarName, out var buffer))
        {
            return buffer.Snapshot();
        }

        return Empty;
    }
}
