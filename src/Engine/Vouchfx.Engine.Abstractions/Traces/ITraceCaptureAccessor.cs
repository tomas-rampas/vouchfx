// Vouchfx.Engine.Abstractions — ITraceCaptureAccessor + NullTraceCaptureAccessor
// (Phase C, trace-expect.otlp, §5).
//
// ITraceCaptureAccessor is THE type the emitted CSX names at runtime: the script reaches the
// host-owned OTLP span capture buffers ONLY through the instance property
// ScriptGlobalVariables.Traces, and that property's type is ITraceCaptureAccessor. Mirrors the
// ISecretAccessor + NullSecretAccessor and IWebhookCaptureAccessor + NullWebhookCaptureAccessor
// interface+null-object pattern exactly (S07-F-01a precedent).
//
// The accessor is READ-ONLY: the script observes spans exported to a host-owned ephemeral OTLP
// receiver (which lives in the Default ALC, owned by the topology/runner). The script never
// starts, stops, or mutates a receiver — it only snapshots what has already been captured,
// keyed by the receiver's logical name.

using System.Collections.Generic;

namespace Vouchfx.Engine.Abstractions.Traces;

/// <summary>
/// The execution-time, read-only entry point the emitted script uses to observe the spans
/// captured by a host-owned ephemeral OTLP/HTTP receiver (Phase C, <c>trace-expect.otlp</c>,
/// §5).
/// </summary>
/// <remarks>
/// <para>
/// Reached from the script only via the instance property
/// <c>ScriptGlobalVariables.Traces</c>; never via a static (which would root the collectible
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, §5). The receiver and its capture
/// buffer are owned by the topology/runner in the Default ALC; this accessor merely projects a
/// read-only <em>snapshot</em> of one receiver's buffer to the script, keyed by the receiver's
/// logical variable name.
/// </para>
/// <para>
/// This is a read-only surface by design: it exposes no method to start, stop, or clear a
/// receiver. Lifecycle (start at topology-up, dispose at teardown) is the host's
/// responsibility, performed in the Default ALC.
/// </para>
/// </remarks>
public interface ITraceCaptureAccessor
{
    /// <summary>
    /// Returns a read-only snapshot of the spans captured so far by the receiver registered
    /// under <paramref name="receiverVarName"/>.
    /// </summary>
    /// <param name="receiverVarName">
    /// The logical name of the receiver, as declared by the provider's
    /// <c>HostResourceRequirement.VarName</c> and staged at <c>svc::&lt;VarName&gt;</c>.
    /// </param>
    /// <returns>
    /// A read-only list of <see cref="CapturedSpan"/> in capture order; an empty list when the
    /// receiver has captured nothing yet, or when no receiver is registered under
    /// <paramref name="receiverVarName"/>. Never <see langword="null"/>.
    /// </returns>
    IReadOnlyList<CapturedSpan> GetCaptured(string receiverVarName);

    /// <summary>
    /// Returns the TOTAL number of spans ever received by the receiver registered under
    /// <paramref name="receiverVarName"/>, including any already evicted by its ring-buffer
    /// cap (<c>TraceCaptureBuffer.MaxBufferedSpans</c>).
    /// </summary>
    /// <remarks>
    /// This is the diagnostic that lets a consumer distinguish "the receiver's buffer is
    /// saturated (a flood of spans, possibly evicting the one this step is looking for) from
    /// "the SUT genuinely exported very few spans, none matching" — both look identical from
    /// <see cref="GetCaptured"/> alone once the buffer is full (it always reports exactly
    /// <c>MaxBufferedSpans</c> entries). <c>GetTotalReceived(name) - GetCaptured(name).Count</c>
    /// is the number of spans evicted so far for that receiver.
    /// </remarks>
    /// <param name="receiverVarName">
    /// The logical name of the receiver, as declared by the provider's
    /// <c>HostResourceRequirement.VarName</c> and staged at <c>svc::&lt;VarName&gt;</c>.
    /// </param>
    /// <returns>
    /// The total count of spans ever appended; <c>0</c> when no receiver is registered under
    /// <paramref name="receiverVarName"/>.
    /// </returns>
    long GetTotalReceived(string receiverVarName);
}

/// <summary>
/// An <see cref="ITraceCaptureAccessor"/> for runs with no OTLP receivers configured:
/// <see cref="GetCaptured"/> always returns an empty list.
/// </summary>
/// <remarks>
/// <para>
/// Backs the legacy <c>ScriptGlobalVariables</c> constructors so existing call sites and tests
/// keep compiling and behave safely — a run that declares no <c>trace-expect.otlp</c> step
/// never pays for a receiver, and the <c>Traces</c> member stays this null accessor (the
/// off-hot-path default).
/// </para>
/// <para>
/// This is a stateless singleton, exactly like <c>NullWebhookCaptureAccessor.Instance</c>: the
/// shared instance lives on this plain abstraction type — NOT on the host↔script boundary type
/// <c>ScriptGlobalVariables</c>, which must declare no static state or it would root the
/// collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/> and defeat the memory
/// model (§5).
/// </para>
/// </remarks>
public sealed class NullTraceCaptureAccessor : ITraceCaptureAccessor
{
    /// <summary>
    /// A shared, stateless instance for the legacy <c>ScriptGlobalVariables</c> constructors to
    /// reuse instead of allocating a new accessor per call.
    /// </summary>
    public static readonly NullTraceCaptureAccessor Instance = new();

    private static readonly IReadOnlyList<CapturedSpan> Empty =
        System.Array.Empty<CapturedSpan>();

    private NullTraceCaptureAccessor()
    {
    }

    /// <inheritdoc />
    /// <returns>Always an empty list.</returns>
    public IReadOnlyList<CapturedSpan> GetCaptured(string receiverVarName) => Empty;

    /// <inheritdoc />
    /// <returns>Always <c>0</c>.</returns>
    public long GetTotalReceived(string receiverVarName) => 0;
}
