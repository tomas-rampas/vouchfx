// Vouchfx.Engine.Compilation.MemoryHarness — ProbeTraceAccessor (Phase C, §5).
//
// A tiny, in-harness stub ITraceCaptureAccessor used by the Phase-C extension of the §5
// closure leak gate. It seeds a small, fixed IReadOnlyList<CapturedSpan> (two spans with
// attributes) so the closure probe CSX can exercise the NEW ScriptGlobalVariables.Traces read
// path — Traces.GetCaptured(name) returning CapturedSpan records — across the collectible
// AssemblyLoadContext. Mirrors ProbeWebhookAccessor exactly (S07-E1 precedent).
//
// Memory-model note (§5):
//   The REAL OTLP receiver + TraceCaptureBuffer are host-owned and live in the Default ALC
//   (Orchestration) — they are NEVER touched by the CSX, so the probe must NOT reference them.
//   The closure read path the script names is only the Abstractions-side graph
//   (ITraceCaptureAccessor + CapturedSpan). This stub mirrors that: it is a by-reference
//   Default-ALC instance (it lives in the harness, exactly like the real accessor lives in the
//   host), holds an immutable pre-seeded snapshot, and has no static or mutable state.

using System;
using System.Collections.Generic;
using Vouchfx.Engine.Abstractions.Traces;

namespace Vouchfx.Engine.Compilation.MemoryHarness;

/// <summary>
/// A stub <see cref="ITraceCaptureAccessor"/> for the closure leak gate. Returns a fixed,
/// pre-seeded snapshot of <see cref="CapturedSpan"/>s for the probe receiver name and an empty
/// list for every other name.
/// </summary>
/// <remarks>
/// <para>
/// Used by <see cref="MemoryProbe.RunClosureAsync"/> to drive the Phase-C
/// <c>ScriptGlobalVariables.Traces</c> read path inside the collectible
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>: the probe CSX calls
/// <c>Traces.GetCaptured("probe")</c> and iterates the returned records, so any pin of the
/// collectible context through the accessor or the <see cref="CapturedSpan"/> record graph
/// would surface as a per-cycle heap growth.
/// </para>
/// <para>
/// This is deliberately the <em>Abstractions-side</em> graph only. It does NOT reference the
/// host-owned <c>TraceCaptureBuffer</c> / receiver in Orchestration (those live in the Default
/// ALC and are never named by the CSX, §5). The instance is created once and reused across all
/// iterations: it carries an immutable seed and no mutable or static state, so reuse is safe.
/// </para>
/// </remarks>
public sealed class ProbeTraceAccessor : ITraceCaptureAccessor
{
    /// <summary>The receiver name the probe CSX queries (<c>Traces.GetCaptured("probe")</c>).</summary>
    public const string ProbeReceiverName = "probe";

    private static readonly IReadOnlyList<CapturedSpan> Empty =
        Array.Empty<CapturedSpan>();

    // Concrete array, not IReadOnlyList (CA1859): the field is only ever the array built
    // in the constructor. `Empty` deliberately stays interface-typed so GetCaptured's
    // conditional still yields IReadOnlyList<CapturedSpan>, the interface's return type.
    private readonly CapturedSpan[] _seed;

    /// <summary>
    /// Creates the stub with a small fixed seed: two captured spans, each carrying
    /// attributes, so the probe iterates real record graphs.
    /// </summary>
    public ProbeTraceAccessor()
    {
        _seed = new[]
        {
            new CapturedSpan(
                TraceId: "4bf92f3577b34da6a3ce929d0e0e4736",
                SpanId: "00f067aa0ba902b7",
                ParentSpanId: string.Empty,
                Name: "POST /orders",
                ServiceName: "probe-service",
                Attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["http.method"] = "POST",
                    ["http.status_code"] = "201",
                },
                StatusCode: "STATUS_CODE_OK",
                At: new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero)),
            new CapturedSpan(
                TraceId: "4bf92f3577b34da6a3ce929d0e0e4736",
                SpanId: "a1b2c3d4e5f60718",
                ParentSpanId: "00f067aa0ba902b7",
                Name: "INSERT orders",
                ServiceName: "probe-service",
                Attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["db.system"] = "postgres",
                },
                StatusCode: "STATUS_CODE_OK",
                At: new DateTimeOffset(2026, 7, 8, 12, 0, 1, TimeSpan.Zero)),
        };
    }

    /// <inheritdoc />
    /// <returns>
    /// The two-element pre-seeded snapshot for <see cref="ProbeReceiverName"/>; an empty list
    /// for any other receiver name. Never <see langword="null"/>.
    /// </returns>
    public IReadOnlyList<CapturedSpan> GetCaptured(string receiverVarName)
        => string.Equals(receiverVarName, ProbeReceiverName, StringComparison.Ordinal)
            ? _seed
            : Empty;

    /// <inheritdoc />
    /// <returns>
    /// The two-element seed count for <see cref="ProbeReceiverName"/> (no eviction ever occurs
    /// against this fixed stub seed); <c>0</c> for any other receiver name.
    /// </returns>
    public long GetTotalReceived(string receiverVarName)
        => string.Equals(receiverVarName, ProbeReceiverName, StringComparison.Ordinal)
            ? _seed.Length
            : 0;
}
