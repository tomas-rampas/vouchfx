// Platform.Engine.Orchestration — WebhookCaptureAccessor (S07-F-01a, §5).
//
// The concrete IWebhookCaptureAccessor that the runner constructs in the Default ALC and
// passes by-reference into ScriptGlobalVariables.Webhooks.  It wraps the per-listener
// WebhookCaptureBuffer instances, keyed by the listener's logical VarName, and projects an
// immutable snapshot of one listener's buffer to the emitted script on demand.
//
// Memory-model note (§5): this object and the buffers it holds live wholly in the Default
// ALC, owned by the runner/topology.  Nothing static holds it; the emitted script reaches it
// only by-reference via ScriptGlobalVariables.Webhooks.  An unknown name yields an empty list
// (never a throw), mirroring NullWebhookCaptureAccessor's safe-empty contract.

using System;
using System.Collections.Generic;
using Platform.Engine.Abstractions.Webhooks;

namespace Platform.Engine.Orchestration.HostResources;

/// <summary>
/// The concrete <see cref="IWebhookCaptureAccessor"/> the runner passes into
/// <c>ScriptGlobalVariables.Webhooks</c>: it wraps the per-listener
/// <see cref="WebhookCaptureBuffer"/> instances, keyed by each listener's logical
/// <c>VarName</c>, and projects an immutable snapshot of one buffer to the emitted script
/// (S07-F-01a).
/// </summary>
/// <remarks>
/// <para>
/// Built in the Default <see cref="System.Runtime.Loader.AssemblyLoadContext"/> by the runner
/// at topology-up — once per scenario run — and handed across the §5 boundary by-reference
/// (exactly like the secret accessor).  The buffers it holds are the same instances the
/// host-owned <see cref="WebhookListener"/>s append to; reads therefore see live captures.
/// </para>
/// <para>
/// <see cref="GetCaptured"/> returns an empty list for an unknown name rather than throwing,
/// matching <see cref="NullWebhookCaptureAccessor"/>: a step asserting against a listener that
/// was never registered observes "no requests" and fails its assertion cleanly, rather than
/// crashing the run.
/// </para>
/// </remarks>
public sealed class WebhookCaptureAccessor : IWebhookCaptureAccessor
{
    private static readonly IReadOnlyList<CapturedWebhookRequest> Empty =
        Array.Empty<CapturedWebhookRequest>();

    private readonly IReadOnlyDictionary<string, WebhookCaptureBuffer> _buffers;

    /// <summary>
    /// Initialises a new accessor over the given per-listener buffer map.
    /// </summary>
    /// <param name="buffers">
    /// A map from listener logical name (<c>HostResourceRequirement.VarName</c>) to its capture
    /// buffer; must not be <see langword="null"/>.  May be empty (no listeners), in which case
    /// every lookup returns an empty list.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="buffers"/> is <see langword="null"/>.
    /// </exception>
    public WebhookCaptureAccessor(IReadOnlyDictionary<string, WebhookCaptureBuffer> buffers)
    {
        _buffers = buffers ?? throw new ArgumentNullException(nameof(buffers));
    }

    /// <inheritdoc />
    public IReadOnlyList<CapturedWebhookRequest> GetCaptured(string listenerVarName)
    {
        if (!string.IsNullOrEmpty(listenerVarName) &&
            _buffers.TryGetValue(listenerVarName, out var buffer))
        {
            return buffer.Snapshot();
        }

        return Empty;
    }
}
