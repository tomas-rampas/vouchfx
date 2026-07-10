// Vouchfx.Engine.Compilation.MemoryHarness — ProbeWebhookAccessor (S07-E1, §5).
//
// A tiny, in-harness stub IWebhookCaptureAccessor used by the Sprint-7 extension of the
// §5 closure leak gate.  It seeds a small, fixed IReadOnlyList<CapturedWebhookRequest>
// (two requests with headers + a body) so the closure probe CSX can exercise the NEW
// ScriptGlobalVariables.Webhooks read path — Webhooks.GetCaptured(name) returning
// CapturedWebhookRequest records — across the collectible AssemblyLoadContext.
//
// Memory-model note (§5):
//   The REAL webhook listener + WebhookCaptureBuffer are host-owned and live in the
//   Default ALC (Orchestration) — they are NEVER touched by the CSX, so the probe must
//   NOT reference them.  The closure read path the script names is only the
//   Abstractions-side graph (IWebhookCaptureAccessor + CapturedWebhookRequest).  This
//   stub mirrors that: it is a by-reference Default-ALC instance (it lives in the
//   harness, exactly like the real accessor lives in the host), holds an immutable
//   pre-seeded snapshot, and has no static or mutable state.  The script observes its
//   records by-reference through the typed accessor — nothing here roots the collectible
//   context back into the Default context.

using System;
using System.Collections.Generic;
using Vouchfx.Engine.Abstractions.Webhooks;

namespace Vouchfx.Engine.Compilation.MemoryHarness;

/// <summary>
/// A stub <see cref="IWebhookCaptureAccessor"/> for the closure leak gate.  Returns a
/// fixed, pre-seeded snapshot of <see cref="CapturedWebhookRequest"/>s for the probe
/// listener name and an empty list for every other name.
/// </summary>
/// <remarks>
/// <para>
/// Used by <see cref="MemoryProbe.RunClosureAsync"/> to drive the Sprint-7
/// <c>ScriptGlobalVariables.Webhooks</c> read path inside the collectible
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>: the probe CSX calls
/// <c>Webhooks.GetCaptured("probe")</c> and iterates the returned records, so any pin of
/// the collectible context through the accessor or the <see cref="CapturedWebhookRequest"/>
/// record graph would surface as a per-cycle heap growth.
/// </para>
/// <para>
/// This is deliberately the <em>Abstractions-side</em> graph only.  It does NOT reference
/// the host-owned <c>WebhookCaptureBuffer</c> / listener in Orchestration (those live in
/// the Default ALC and are never named by the CSX, §5).  The instance is created once and
/// reused across all iterations: it carries an immutable seed and no mutable or static
/// state, so reuse is safe and matches how the real host-owned accessor is a single
/// long-lived Default-ALC instance.
/// </para>
/// </remarks>
public sealed class ProbeWebhookAccessor : IWebhookCaptureAccessor
{
    /// <summary>
    /// The listener name the probe CSX queries (<c>Webhooks.GetCaptured("probe")</c>).
    /// </summary>
    public const string ProbeListenerName = "probe";

    private static readonly IReadOnlyList<CapturedWebhookRequest> Empty =
        Array.Empty<CapturedWebhookRequest>();

    private readonly IReadOnlyList<CapturedWebhookRequest> _seed;

    /// <summary>
    /// Creates the stub with a small fixed seed: two captured requests, each carrying
    /// headers and a non-empty body, so the probe iterates real record graphs.
    /// </summary>
    public ProbeWebhookAccessor()
    {
        _seed = new[]
        {
            new CapturedWebhookRequest(
                Method: "POST",
                Path: "/callbacks/order?id=7",
                Headers: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Content-Type"] = "application/json",
                    ["X-Probe"] = "1",
                },
                Body: "{\"event\":\"order.created\",\"id\":7}",
                At: new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero)),
            new CapturedWebhookRequest(
                Method: "POST",
                Path: "/callbacks/order?id=8",
                Headers: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Content-Type"] = "application/json",
                    ["X-Probe"] = "2",
                },
                Body: "{\"event\":\"order.shipped\",\"id\":8}",
                At: new DateTimeOffset(2026, 6, 10, 12, 0, 1, TimeSpan.Zero)),
        };
    }

    /// <inheritdoc />
    /// <returns>
    /// The two-element pre-seeded snapshot for <see cref="ProbeListenerName"/>; an empty
    /// list for any other listener name.  Never <see langword="null"/>.
    /// </returns>
    public IReadOnlyList<CapturedWebhookRequest> GetCaptured(string listenerVarName)
        => string.Equals(listenerVarName, ProbeListenerName, StringComparison.Ordinal)
            ? _seed
            : Empty;
}
