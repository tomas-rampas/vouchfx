// Vouchfx.Engine.Orchestration — WebhookListener (S07-F-01a, §5).
//
// A minimal host-owned ephemeral HTTP listener backing the webhook-listen.http family.
//
// Kestrel (not HttpListener) by deliberate choice:
//   • Binds 0.0.0.0:0 (ephemeral, ALL interfaces) WITHOUT a Windows admin URL-ACL.
//     A containerised SUT must later reach the host via host.docker.internal, which
//     resolves to a non-loopback host IP; HttpListener on a non-loopback prefix requires
//     an elevated `netsh http add urlacl` reservation, whereas Kestrel binds 0.0.0.0
//     directly with no such reservation.
//   • The host executables that consume this library (the CLI runner, the Aspire-host
//     test projects, all <IsAspireHost>true</IsAspireHost>) already carry the ASP.NET Core
//     shared framework, so Kestrel resolves at runtime; the Orchestration csproj declares
//     <FrameworkReference Include="Microsoft.AspNetCore.App" /> so it resolves at compile time.
//
// Lifecycle (§5): the listener + its buffer live in the Default ALC, owned by the
// topology/runner.  The emitted script (collectible ALC) never sees this object — it only
// reads captures through ScriptGlobalVariables.Webhooks.  IAsyncDisposable stops it cleanly.
//
// SECURITY POSTURE (unauthenticated-exposure trade-off, S07 hardening):
//   The listener binds 0.0.0.0 BY NECESSITY — a containerised SUT reaches the host via
//   host.docker.internal, which resolves to a non-loopback host IP, so a loopback-only bind
//   would make the listener unreachable from the container (that is the E2 capstone scenario).
//   Binding all interfaces on an ephemeral port leaves the listener unauthenticated and, in
//   principle, reachable by anything on the host's network for the lifetime of the scenario.
//   The mitigations that make that acceptable for an ephemeral test window are:
//     • UNGUESSABLE URL PATH TOKEN — every request must arrive under a random first path
//       segment generated per listener at StartAsync (Guid "n", 32 hex chars).  A network
//       scanner that finds the port but not the token gets a 404 and CANNOT forge a captured
//       request → no false Pass from an off-network onlooker.  The token is staged INTO the
//       URL handed to the SUT, so legitimate callbacks carry it transparently; the captured
//       Path has the token prefix STRIPPED so authors match the SUT-facing path without
//       knowing the token.
//     • EPHEMERAL PORT + PER-SCENARIO LIFETIME — the port is OS-assigned and the listener is
//       disposed at scenario teardown, so the exposure window is short and unpredictable.
//     • BOUNDED BODY + BUFFER — Kestrel caps the request body at 1 MiB (413 before the body is
//       read) and concurrent connections at 100; the capture buffer caps retained requests
//       (WebhookCaptureBuffer.MaxBufferedRequests).  Together these bound the memory-DoS a
//       flood of large/forged callbacks could inflict.
//   NOTE: captured-secret-egress redaction (not writing sensitive captured headers/bodies into
//   observations) is the responsibility of the consuming webhook-listen.http PROVIDER, not this
//   host listener.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vouchfx.Engine.Abstractions.Webhooks;

namespace Vouchfx.Engine.Orchestration.HostResources;

/// <summary>
/// A host-owned ephemeral HTTP listener (Kestrel) that captures every inbound request into a
/// <see cref="WebhookCaptureBuffer"/> and responds <c>204 No Content</c>, backing the
/// <c>webhook-listen.http</c> family (S07-F-01a).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Binding:</strong> the listener binds <c>http://0.0.0.0:0</c> — port 0 lets the OS
/// assign a free ephemeral port, and <c>0.0.0.0</c> (all interfaces) makes the listener
/// reachable from a containerised system under test via <c>host.docker.internal</c> without a
/// Windows admin URL-ACL reservation.  The actual bound URL (with the OS-assigned port and the
/// unguessable token segment, reported as <c>http://127.0.0.1:&lt;port&gt;/&lt;token&gt;/</c>
/// for in-process callers) is exposed via <see cref="Url"/> after <see cref="StartAsync"/>
/// completes.
/// </para>
/// <para>
/// <strong>Token gate (forged-callback mitigation):</strong> because the bind is on all
/// interfaces and unauthenticated, every listener generates a random first path segment (a
/// Guid in <c>"n"</c> form) at start.  Only requests whose path begins with <c>/&lt;token&gt;/</c>
/// are captured; any other request — no token, wrong token, or a bare scanner probe to
/// <c>/</c> — receives <c>404 Not Found</c> and is NOT buffered.  An onlooker who finds the
/// port but not the token therefore cannot inject a captured request.
/// </para>
/// <para>
/// <strong>Capture:</strong> for a token-matched request the listener accepts ANY method and
/// ANY (post-token) path.  It records the method, the <em>token-stripped</em> path+query
/// (so an author matches <c>/orders/created</c> without knowing the token), headers
/// (multi-valued joined with <c>", "</c>), the body decoded as UTF-8 (bounded to 1 MiB by
/// Kestrel; an oversize body is rejected <c>413</c> before it is read and is NOT buffered),
/// and the UTC capture instant, appending a <see cref="CapturedWebhookRequest"/> to the
/// supplied <see cref="WebhookCaptureBuffer"/> (itself bounded — see
/// <see cref="WebhookCaptureBuffer.MaxBufferedRequests"/>).  It then responds
/// <c>204 No Content</c>.
/// </para>
/// <para>
/// <strong>Memory model (§5):</strong> the listener and its buffer live in the Default
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, owned by the topology/runner.  The
/// emitted script never references this type; it reads captures only by-reference through
/// <c>ScriptGlobalVariables.Webhooks</c>.  <see cref="DisposeAsync"/> stops the Kestrel host
/// cleanly and releases the port.
/// </para>
/// </remarks>
public sealed class WebhookListener : IAsyncDisposable
{
    // The number of bytes a webhook callback body may carry before Kestrel rejects it 413.
    // Webhook callbacks are small JSON payloads; 1 MiB is generous headroom while bounding the
    // memory a single malicious/large request can force the host to read.
    private const long MaxRequestBodyBytes = 1 * 1024 * 1024;

    // Cap concurrent connections so a flood cannot exhaust host sockets/threads.
    private const long MaxConcurrentConnections = 100;

    private readonly WebApplication _app;
    private bool _disposed;

    private WebhookListener(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    /// <summary>
    /// Gets the bound base URL of the listener (with the OS-assigned ephemeral port and the
    /// unguessable per-listener token path segment), e.g.
    /// <c>http://127.0.0.1:54321/3f2a…d1/</c>.  Available only after <see cref="StartAsync"/>
    /// returns.  This full tokenised URL is what is staged for the SUT to call back to.
    /// </summary>
    /// <remarks>
    /// The listener binds <c>0.0.0.0</c> so it is reachable from a container via
    /// <c>host.docker.internal:&lt;port&gt;</c>; this property reports the loopback form for
    /// in-process callers (the port is what matters for staging into <c>Vars</c>).  The trailing
    /// <c>/&lt;token&gt;/</c> segment is the unguessable gate: only callbacks under it are
    /// captured (see the class remarks).
    /// </remarks>
    public string Url { get; }

    /// <summary>
    /// Starts an ephemeral Kestrel listener bound to <c>http://0.0.0.0:0</c> that captures every
    /// inbound request into <paramref name="buffer"/> and responds <c>204 No Content</c>.
    /// </summary>
    /// <param name="buffer">
    /// The per-listener capture buffer the inbound requests are appended to; must not be
    /// <see langword="null"/>.  Owned by the caller (the runner/topology) in the Default ALC.
    /// </param>
    /// <param name="cancellationToken">
    /// Propagated to the underlying host start.
    /// </param>
    /// <returns>
    /// A started <see cref="WebhookListener"/> whose <see cref="Url"/> is populated with the
    /// OS-assigned port.
    /// </returns>
    /// <exception cref="System.ArgumentNullException">
    /// Thrown when <paramref name="buffer"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when the started host reports no bound address.
    /// </exception>
    public static async Task<WebhookListener> StartAsync(
        WebhookCaptureBuffer buffer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        // Unguessable per-listener path token: a network scanner that finds the (0.0.0.0) port
        // but not this token cannot inject a captured request.  Guid "n" → 32 lowercase hex
        // chars, generated fresh per listener; ample for an ephemeral test window.
        var token = Guid.NewGuid().ToString("n");

        // Slim builder: no MVC, no static files, no config providers we don't need — just the
        // minimal Kestrel host required to accept and capture raw requests.
        var builder = WebApplication.CreateSlimBuilder();

        // Quieten the host: this is an internal capture sink, not an application server.
        builder.Logging.ClearProviders();

        // Bind 0.0.0.0:0 — all interfaces, OS-assigned ephemeral port (no Windows URL-ACL) —
        // and bound the request body (1 MiB) and concurrent connections (100) so a flood of
        // large/forged callbacks cannot exhaust host memory.  Kestrel returns 413 on an
        // oversize body BEFORE the body is read.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Any, 0);
            options.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
            options.Limits.MaxConcurrentConnections = MaxConcurrentConnections;
        });

        var app = builder.Build();

        // Terminal middleware: token-gate, then capture ANY method / ANY (post-token) path,
        // respond 204.  Non-token requests get 404 and are NOT buffered.
        app.Run(async context => await CaptureAsync(context, buffer, token).ConfigureAwait(false));

        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var url = ResolveBoundUrl(app, token);
        return new WebhookListener(app, url);
    }

    /// <summary>
    /// Token-gates a single inbound request and, if it carries the listener's unguessable token
    /// as its first path segment, captures it (with the token prefix stripped) into
    /// <paramref name="buffer"/> and responds <c>204</c>.  A request without the correct token —
    /// or one whose body exceeds the Kestrel limit — is NOT buffered.
    /// </summary>
    private static async Task CaptureAsync(
        HttpContext context,
        WebhookCaptureBuffer buffer,
        string token)
    {
        var request = context.Request;

        // ── Token gate ─────────────────────────────────────────────────────────────────────
        // The SUT-facing path is whatever follows "/<token>".  A request that does not begin
        // with the token (no token, wrong token, a bare scanner probe to "/") is rejected 404
        // and never buffered, so an onlooker who does not know the token cannot forge a capture.
        if (!TryStripToken(request.Path, token, out var sutPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // ── Body read (bounded + guarded) ──────────────────────────────────────────────────
        // Kestrel enforces MaxRequestBodySize: reading the body throws BadHttpRequestException
        // (HTTP 413) when the client sends more than the limit.  Guard the read so an oversize
        // (or aborted) request responds cleanly and is NOT buffered, rather than throwing out of
        // the handler.  RequestAborted is still plumbed so a stalled client cancels the read.
        string body;
        try
        {
            using var reader = new StreamReader(
                request.Body,
                System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex)
        {
            // Oversize / malformed body: respond with the status Kestrel determined (413 for an
            // over-limit body) and do NOT buffer the request.
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = ex.StatusCode;
            }

            return;
        }
        catch (OperationCanceledException)
        {
            // Client aborted (RequestAborted fired): nothing to capture, nothing to send.
            return;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            // Multi-valued headers are joined with ", " into a single string value.
            headers[header.Key] = string.Join(", ", header.Value.ToArray());
        }

        if (request.QueryString.HasValue)
        {
            // Preserve the query string after the token-stripped path.
            sutPath += request.QueryString.Value;
        }

        buffer.Append(new CapturedWebhookRequest(
            Method: request.Method.ToUpperInvariant(),
            Path: sutPath,
            Headers: headers,
            Body: body,
            At: DateTimeOffset.UtcNow));

        // 204 No Content: the SUT only needs an ack that the callback was received.
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    /// <summary>
    /// Returns <see langword="true"/> and the SUT-facing path (the request path with the
    /// leading <c>/&lt;token&gt;</c> segment removed) when <paramref name="path"/> begins with
    /// the listener's token segment; otherwise <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// A callback to <c>/&lt;token&gt;/orders/created</c> yields <c>"/orders/created"</c>; a
    /// callback to exactly <c>/&lt;token&gt;</c> or <c>/&lt;token&gt;/</c> yields <c>"/"</c>.
    /// The match on the token segment is ordinal and case-sensitive (the token is hex).
    /// </remarks>
    private static bool TryStripToken(PathString path, string token, out string sutPath)
    {
        sutPath = "/";

        if (!path.HasValue)
        {
            return false;
        }

        // PathString.StartsWithSegments performs a true segment match: "/<token>" matches
        // "/<token>" and "/<token>/..." but NOT "/<token>extra".  remainder is the SUT-facing
        // tail ("/orders/created", or "" when the request was exactly "/<token>").
        var tokenSegment = new PathString("/" + token);
        if (!path.StartsWithSegments(
                tokenSegment,
                StringComparison.Ordinal,
                out var remainder))
        {
            return false;
        }

        var tail = remainder.HasValue ? remainder.Value! : "/";

        // The staged Url ends with "/<token>/"; callers append the SUT path as "<Url>orders" or,
        // conventionally, "<Url>/orders" which produces a leading "//" in the request path.
        // Collapse any such leading double-slash so the captured SUT-facing path is canonical
        // ("/orders/created"), matching what the author writes WITHOUT knowing the token.
        while (tail.StartsWith("//", StringComparison.Ordinal))
        {
            tail = tail[1..];
        }

        sutPath = tail.Length == 0 ? "/" : tail;
        return true;
    }

    /// <summary>
    /// Reads the host's bound address (from <see cref="IServerAddressesFeature"/>), rewrites the
    /// wildcard host to loopback so in-process callers can dial it directly, and appends the
    /// unguessable <paramref name="token"/> as the first path segment.
    /// </summary>
    private static string ResolveBoundUrl(WebApplication app, string token)
    {
        var addressesFeature = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();

        var bound = addressesFeature?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The webhook listener started but reported no bound address.");

        // Kestrel reports the wildcard bind as e.g. "http://0.0.0.0:54321" or
        // "http://[::]:54321".  Rewrite the host part to 127.0.0.1 so in-process HttpClient
        // calls (and the staged svc:: URL) target a dialable loopback address; the OS-assigned
        // PORT is the load-bearing part and is preserved.  A containerised SUT reaches the same
        // port via host.docker.internal:<port> (deferred to the E2 Docker capstone).
        string authority;
        if (Uri.TryCreate(bound, UriKind.Absolute, out var uri))
        {
            var builder = new UriBuilder(uri) { Host = "127.0.0.1" };
            // Normalise: drop the default-port marker and any trailing slash inconsistency.
            authority = builder.Uri.GetLeftPart(UriPartial.Authority);
        }
        else
        {
            authority = bound.TrimEnd('/');
        }

        // Append the unguessable token as the first path segment, with a trailing slash so the
        // SUT's callback path appends cleanly (e.g. "<authority>/<token>/orders/created").
        return authority + "/" + token + "/";
    }

    /// <summary>
    /// Stops the Kestrel host and releases the bound port.  Idempotent — safe to call multiple
    /// times.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await _app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort stop: disposal must not throw on teardown.  A failed StopAsync still
            // proceeds to DisposeAsync below, which releases the host and the port.
        }

        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
