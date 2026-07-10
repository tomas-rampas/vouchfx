// Vouchfx.Engine.Abstractions — CapturedSpan (Phase C, trace-expect.otlp, §5, §17).
//
// The pure-data record describing a single OTLP span captured by the host-owned OTLP/HTTP
// receiver. It is the read-only payload a later trace-expect.otlp assertion step observes
// through ScriptGlobalVariables.Traces.GetCaptured.
//
// Memory-model note (§5): this is an immutable managed record with no static state.
// Instances are minted in the Default ALC by the host receiver and read (never mutated) by
// the emitted script through the typed accessor; nothing here roots the collectible
// AssemblyLoadContext back into the Default context. Mirrors CapturedWebhookRequest exactly
// (S07-F-01a precedent).

using System.Collections.Generic;

namespace Vouchfx.Engine.Abstractions.Traces;

/// <summary>
/// An immutable snapshot of a single OTLP span captured by the host-owned OTLP/HTTP receiver
/// (Phase C, <c>trace-expect.otlp</c>).
/// </summary>
/// <remarks>
/// <para>
/// The host receiver (running in the Default <see cref="System.Runtime.Loader.AssemblyLoadContext"/>)
/// appends one of these per span found in an inbound OTLP/HTTP JSON export request. A later
/// <c>trace-expect.otlp</c> assertion step reads the buffer's snapshot through the typed
/// instance accessor <c>ScriptGlobalVariables.Traces</c> — exactly as it reaches the webhook
/// capture buffer through <c>ScriptGlobalVariables.Webhooks</c> — and never through any static
/// handle (§5).
/// </para>
/// <para>
/// The record is intentionally pure data: it carries no live handle to the receiver, the
/// buffer, or any network object, so it can never root the collectible script context.
/// <see cref="Attributes"/> is a stringified, read-only snapshot taken at capture time — every
/// OTLP attribute value kind (<c>stringValue</c>/<c>intValue</c>/<c>boolValue</c>/
/// <c>doubleValue</c>) is coerced to its invariant-culture string form so the assertion side
/// (and any future capture support) never needs to branch on the OTLP wire value kind.
/// </para>
/// </remarks>
/// <param name="TraceId">
/// The 32-hex-character W3C trace id the span belongs to, exactly as received on the wire
/// (lower-case hex, no dashes).
/// </param>
/// <param name="SpanId">
/// The 16-hex-character span id, exactly as received on the wire.
/// </param>
/// <param name="ParentSpanId">
/// The 16-hex-character parent span id, or an empty string for a root span (no parent).
/// </param>
/// <param name="Name">
/// The span's name (operation name), e.g. <c>"POST /orders"</c>.
/// </param>
/// <param name="ServiceName">
/// The exporting resource's <c>service.name</c> attribute, or an empty string when the
/// resource carried no such attribute.
/// </param>
/// <param name="Attributes">
/// A read-only snapshot of the span's own attributes, keyed by attribute key, every value
/// stringified (invariant culture) regardless of its original OTLP value kind.
/// </param>
/// <param name="StatusCode">
/// The span's OTLP status code as received (e.g. <c>"STATUS_CODE_OK"</c>,
/// <c>"STATUS_CODE_ERROR"</c>, <c>"STATUS_CODE_UNSET"</c>), or an empty string when the span
/// carried no status.
/// </param>
/// <param name="At">
/// The instant the receiver finished parsing the export request that carried this span, in
/// UTC.
/// </param>
public sealed record CapturedSpan(
    string TraceId,
    string SpanId,
    string ParentSpanId,
    string Name,
    string ServiceName,
    IReadOnlyDictionary<string, string> Attributes,
    string StatusCode,
    System.DateTimeOffset At);
