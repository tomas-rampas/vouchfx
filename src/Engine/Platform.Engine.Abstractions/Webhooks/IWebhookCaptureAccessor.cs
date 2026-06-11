// Platform.Engine.Abstractions — IWebhookCaptureAccessor + NullWebhookCaptureAccessor
// (S07-F-01a, §5).
//
// IWebhookCaptureAccessor is THE type the emitted CSX names at runtime: the script
// reaches the host-owned webhook capture buffers ONLY through the instance property
// ScriptGlobalVariables.Webhooks, and that property's type is IWebhookCaptureAccessor.
// Mirrors the ISecretAccessor + NullSecretAccessor interface+null-object pattern.
//
// The accessor is READ-ONLY: the script observes inbound requests captured by a
// host-owned ephemeral listener (which lives in the Default ALC, owned by the
// topology/runner).  The script never starts, stops, or mutates a listener — it only
// snapshots what has already been captured, keyed by the listener's logical name.

using System.Collections.Generic;

namespace Platform.Engine.Abstractions.Webhooks;

/// <summary>
/// The execution-time, read-only entry point the emitted script uses to observe the
/// inbound HTTP requests captured by a host-owned ephemeral webhook listener
/// (S07-F-01a, §5).
/// </summary>
/// <remarks>
/// <para>
/// Reached from the script only via the instance property
/// <c>ScriptGlobalVariables.Webhooks</c>; never via a static (which would root the
/// collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, §5).  The
/// listener and its capture buffer are owned by the topology/runner in the Default
/// ALC; this accessor merely projects a read-only <em>snapshot</em> of one listener's
/// buffer to the script, keyed by the listener's logical variable name.
/// </para>
/// <para>
/// This is a read-only surface by design: it exposes no method to start, stop, or
/// clear a listener.  Lifecycle (start at topology-up, clear per scenario, dispose at
/// teardown) is the host's responsibility, performed in the Default ALC.
/// </para>
/// </remarks>
public interface IWebhookCaptureAccessor
{
    /// <summary>
    /// Returns a read-only snapshot of the requests captured so far by the listener
    /// registered under <paramref name="listenerVarName"/>.
    /// </summary>
    /// <param name="listenerVarName">
    /// The logical name of the listener, as declared by the provider's
    /// <c>HostResourceRequirement.VarName</c> and staged at <c>svc::&lt;VarName&gt;</c>.
    /// </param>
    /// <returns>
    /// A read-only list of <see cref="CapturedWebhookRequest"/> in capture order; an
    /// empty list when the listener has captured nothing yet, or when no listener is
    /// registered under <paramref name="listenerVarName"/>.  Never <see langword="null"/>.
    /// </returns>
    IReadOnlyList<CapturedWebhookRequest> GetCaptured(string listenerVarName);
}

/// <summary>
/// An <see cref="IWebhookCaptureAccessor"/> for runs with no webhook listeners
/// configured: <see cref="GetCaptured"/> always returns an empty list.
/// </summary>
/// <remarks>
/// <para>
/// Backs the legacy <c>ScriptGlobalVariables</c> constructors so existing call sites
/// and tests keep compiling and behave safely — a run that declares no
/// <c>webhook-listener</c> host resource never pays for a listener, and the
/// <c>Webhooks</c> member stays this null accessor (the off-hot-path default).
/// </para>
/// <para>
/// This is a stateless singleton.  Like <c>NullSecretAccessor.Instance</c>, the shared
/// instance lives on this plain abstraction type — NOT on the host↔script boundary type
/// <c>ScriptGlobalVariables</c>, which must declare no static state or it would root the
/// collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/> and defeat the
/// memory model (§5).
/// </para>
/// </remarks>
public sealed class NullWebhookCaptureAccessor : IWebhookCaptureAccessor
{
    /// <summary>
    /// A shared, stateless instance for the legacy <c>ScriptGlobalVariables</c>
    /// constructors to reuse instead of allocating a new accessor per call.
    /// </summary>
    public static readonly NullWebhookCaptureAccessor Instance = new();

    private static readonly IReadOnlyList<CapturedWebhookRequest> Empty =
        System.Array.Empty<CapturedWebhookRequest>();

    private NullWebhookCaptureAccessor()
    {
    }

    /// <inheritdoc />
    /// <returns>Always an empty list.</returns>
    public IReadOnlyList<CapturedWebhookRequest> GetCaptured(string listenerVarName) => Empty;
}
