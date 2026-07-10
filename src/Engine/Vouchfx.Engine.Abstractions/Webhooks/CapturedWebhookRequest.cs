// Vouchfx.Engine.Abstractions — CapturedWebhookRequest (S07-F-01a, §5).
//
// The pure-data record describing a single inbound HTTP request captured by a
// host-owned ephemeral webhook listener.  It is the read-only payload a later
// assertion step observes through ScriptGlobalVariables.Webhooks.GetCaptured.
//
// Memory-model note (§5): this is an immutable managed record with no static
// state.  Instances are minted in the Default ALC by the host listener and read
// (never mutated) by the emitted script through the typed accessor; nothing here
// roots the collectible AssemblyLoadContext back into the Default context.

using System;
using System.Collections.Generic;

namespace Vouchfx.Engine.Abstractions.Webhooks;

/// <summary>
/// An immutable snapshot of a single inbound HTTP request captured by a host-owned
/// ephemeral webhook listener (S07-F-01a).
/// </summary>
/// <remarks>
/// <para>
/// The host listener (running in the Default
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>) appends one of these per
/// inbound request to its per-listener buffer.  A later <c>webhook-listen.http</c>
/// assertion step reads the buffer's snapshot through the typed instance accessor
/// <c>ScriptGlobalVariables.Webhooks</c> — exactly as it reaches the secrets subsystem
/// through <c>ScriptGlobalVariables.Secrets</c> — and never through any static handle
/// (§5).
/// </para>
/// <para>
/// The record is intentionally pure data: it carries no live handle to the listener,
/// the buffer, or any network object, so it can never root the collectible script
/// context.  <see cref="Headers"/> is exposed as an
/// <see cref="IReadOnlyDictionary{TKey, TValue}"/> snapshot taken at capture time.
/// </para>
/// </remarks>
/// <param name="Method">
/// The HTTP method of the inbound request (e.g. <c>"POST"</c>, <c>"GET"</c>),
/// upper-cased as received on the wire.
/// </param>
/// <param name="Path">
/// The request path and query exactly as received (e.g. <c>"/callbacks/order?id=7"</c>).
/// </param>
/// <param name="Headers">
/// A read-only snapshot of the request headers, keyed by header name.  Multi-valued
/// headers are joined with <c>", "</c> into a single string value.
/// </param>
/// <param name="Body">
/// The request body decoded as a UTF-8 string; empty when the request had no body.
/// </param>
/// <param name="At">
/// The instant the listener finished reading the request, in UTC.
/// </param>
public sealed record CapturedWebhookRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    DateTimeOffset At);
