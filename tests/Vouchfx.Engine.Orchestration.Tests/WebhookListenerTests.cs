// Tests for S07-F-01a (Orchestration layer): the host-owned ephemeral webhook listener,
// its capture buffer, and the concrete capture accessor.
//
// Covers (all in-process, NO Docker):
//   • WebhookCaptureBuffer: concurrent appends + snapshot read; Clear empties it; snapshot
//     is an isolated copy.
//   • WebhookListener: start it; POST and GET to its URL from the same process (HttpClient);
//     assert the buffer captured method/path/headers/body; DisposeAsync stops it (the port
//     is released / the URL no longer answers).
//   • WebhookCaptureAccessor: GetCaptured returns a registered listener's requests; empty for
//     an unknown name; empty over an empty buffer map.
//
// The listener binds Kestrel to 0.0.0.0:0 and the test dials the loopback URL it reports.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions.Webhooks;
using Vouchfx.Engine.Orchestration.HostResources;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Non-docker, in-process unit tests for <see cref="WebhookCaptureBuffer"/>,
/// <see cref="WebhookListener"/>, and <see cref="WebhookCaptureAccessor"/> (S07-F-01a).
/// </summary>
public sealed class WebhookListenerTests
{
    // ── WebhookCaptureBuffer ──────────────────────────────────────────────────

    [Fact]
    public void Buffer_AppendThenSnapshot_ReturnsAppendedRequests()
    {
        var buffer = new WebhookCaptureBuffer();

        buffer.Append(MakeRequest("POST", "/a"));
        buffer.Append(MakeRequest("GET", "/b"));

        var snapshot = buffer.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal("/a", snapshot[0].Path);
        Assert.Equal("/b", snapshot[1].Path);
    }

    [Fact]
    public void Buffer_Append_NullRequest_Throws()
    {
        var buffer = new WebhookCaptureBuffer();

        Assert.Throws<ArgumentNullException>(() => buffer.Append(null!));
    }

    [Fact]
    public async Task Buffer_ConcurrentAppends_AllCaptured()
    {
        var buffer = new WebhookCaptureBuffer();
        const int writers = 16;
        // Keep the total below MaxBufferedRequests so this test exercises CONCURRENCY (every
        // append accepted), not the drop-on-cap path (covered separately).
        const int perWriter = 50;

        var tasks = new Task[writers];
        for (int w = 0; w < writers; w++)
        {
            int writerId = w;
            tasks[w] = Task.Run(() =>
            {
                for (int i = 0; i < perWriter; i++)
                {
                    buffer.Append(MakeRequest("POST", $"/w{writerId}/{i}"));
                }
            });
        }

        await Task.WhenAll(tasks);

        Assert.Equal(writers * perWriter, buffer.Count);
        Assert.Equal(writers * perWriter, buffer.Snapshot().Count);
    }

    [Fact]
    public void Buffer_Clear_EmptiesBuffer()
    {
        var buffer = new WebhookCaptureBuffer();
        buffer.Append(MakeRequest("POST", "/a"));
        buffer.Append(MakeRequest("POST", "/b"));

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public void Buffer_Snapshot_IsIsolatedCopy()
    {
        var buffer = new WebhookCaptureBuffer();
        buffer.Append(MakeRequest("POST", "/a"));

        var snapshot = buffer.Snapshot();
        buffer.Append(MakeRequest("POST", "/b"));

        // The earlier snapshot must not see the later append.
        Assert.Single(snapshot);
    }

    [Fact]
    public void Buffer_Append_BeyondCap_DropsNewEntriesAndKeepsEarlyOnes()
    {
        var buffer = new WebhookCaptureBuffer();
        const int cap = WebhookCaptureBuffer.MaxBufferedRequests;

        // Fill exactly to the cap: every append must be accepted.
        for (int i = 0; i < cap; i++)
        {
            Assert.True(buffer.Append(MakeRequest("POST", $"/early/{i}")));
        }

        Assert.Equal(cap, buffer.Count);

        // Appends beyond the cap are DROPPED (return false) and do not grow the buffer.
        for (int i = 0; i < 50; i++)
        {
            Assert.False(buffer.Append(MakeRequest("POST", $"/late/{i}")));
        }

        var snapshot = buffer.Snapshot();

        // Count is capped, and the EARLY (legitimate) entries are the ones retained.
        Assert.Equal(cap, snapshot.Count);
        Assert.Equal("/early/0", snapshot[0].Path);
        Assert.Equal($"/early/{cap - 1}", snapshot[cap - 1].Path);
        Assert.DoesNotContain(snapshot, r => r.Path.StartsWith("/late/", StringComparison.Ordinal));
    }

    // ── WebhookListener ───────────────────────────────────────────────────────

    [Fact]
    public async Task Listener_CapturesPostMethodPathHeadersBody()
    {
        var buffer = new WebhookCaptureBuffer();
        await using var listener = await WebhookListener.StartAsync(buffer);

        Assert.StartsWith("http://", listener.Url, StringComparison.Ordinal);

        // The staged URL carries a non-empty unguessable token as its first path segment and
        // ends with a trailing slash, i.e. http://127.0.0.1:<port>/<token>/.
        var tokenSegment = TokenSegmentOf(listener.Url);
        Assert.False(string.IsNullOrEmpty(tokenSegment));
        Assert.EndsWith("/", listener.Url, StringComparison.Ordinal);

        using var client = new HttpClient();
        using var content = new StringContent(
            "{\"order\":42}", Encoding.UTF8, "application/json");
        content.Headers.Add("X-Vouchfx-Test", "alpha");

        var response = await client.PostAsync(
            listener.Url + "callbacks/order?id=7", content);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var captured = buffer.Snapshot();
        Assert.Single(captured);

        var req = captured[0];
        Assert.Equal("POST", req.Method);
        // The token prefix is STRIPPED from the captured (SUT-facing) path.
        Assert.Equal("/callbacks/order?id=7", req.Path);
        Assert.Equal("{\"order\":42}", req.Body);
        Assert.True(req.Headers.ContainsKey("X-Vouchfx-Test"));
        Assert.Equal("alpha", req.Headers["X-Vouchfx-Test"]);
        Assert.True(req.At <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Listener_CapturesGetRequest()
    {
        var buffer = new WebhookCaptureBuffer();
        await using var listener = await WebhookListener.StartAsync(buffer);

        using var client = new HttpClient();
        var response = await client.GetAsync(listener.Url + "/ping");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var captured = buffer.Snapshot();
        Assert.Single(captured);
        Assert.Equal("GET", captured[0].Method);
        Assert.Equal("/ping", captured[0].Path);
        Assert.Equal(string.Empty, captured[0].Body);
    }

    [Fact]
    public async Task Listener_RequestWithoutToken_Is404AndNotCaptured()
    {
        var buffer = new WebhookCaptureBuffer();
        await using var listener = await WebhookListener.StartAsync(buffer);

        // Dial the authority directly (port only, no token) — a network scanner probing "/".
        var authority = AuthorityOf(listener.Url);

        using var client = new HttpClient();

        // Bare root probe: no token → 404, not captured.
        var rootProbe = await client.GetAsync(authority + "/");
        Assert.Equal(HttpStatusCode.NotFound, rootProbe.StatusCode);

        // Wrong token → 404, not captured.
        var wrongToken = await client.PostAsync(
            authority + "/wrongtoken/orders/created",
            new StringContent("{\"forged\":true}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, wrongToken.StatusCode);

        // A path that merely shares the token as a PREFIX (not a full segment) → 404.
        var tokenSegment = TokenSegmentOf(listener.Url);
        var prefixProbe = await client.GetAsync(authority + "/" + tokenSegment + "extra/x");
        Assert.Equal(HttpStatusCode.NotFound, prefixProbe.StatusCode);

        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public async Task Listener_RequestWithToken_IsCapturedWithTokenStripped()
    {
        var buffer = new WebhookCaptureBuffer();
        await using var listener = await WebhookListener.StartAsync(buffer);

        using var client = new HttpClient();

        // The SUT calls back under the staged tokenised URL.
        var response = await client.PostAsync(
            listener.Url + "orders/created",
            new StringContent("{\"id\":7}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var captured = buffer.Snapshot();
        Assert.Single(captured);
        // The author matches "/orders/created" WITHOUT knowing the token.
        Assert.Equal("/orders/created", captured[0].Path);
        Assert.Equal("{\"id\":7}", captured[0].Body);
    }

    [Fact]
    public async Task Listener_OversizeBody_IsRejectedAndNotCaptured()
    {
        var buffer = new WebhookCaptureBuffer();
        await using var listener = await WebhookListener.StartAsync(buffer);

        using var client = new HttpClient();

        // A normal small body is captured.
        var small = await client.PostAsync(
            listener.Url + "small",
            new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, small.StatusCode);

        // A body larger than the 1 MiB Kestrel limit (Content-Length declared) is rejected
        // BEFORE the body is read.  Kestrel may return a clean 413 or reset the connection mid
        // upload; either way the request must NOT be captured and must NOT be a 204.
        var oversize = new string('x', (2 * 1024 * 1024) + 1); // 2 MiB + 1
        HttpStatusCode? statusIfClean = null;
        try
        {
            var bigResponse = await client.PostAsync(
                listener.Url + "big",
                new StringContent(oversize, Encoding.UTF8, "text/plain"));
            statusIfClean = bigResponse.StatusCode;
        }
        catch (HttpRequestException)
        {
            // Connection reset on an over-limit upload — acceptable; the body was rejected.
        }

        if (statusIfClean is not null)
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, statusIfClean);
        }

        // The oversize request was never buffered; only the small one was captured.
        var captured = buffer.Snapshot();
        Assert.Single(captured);
        Assert.Equal("/small", captured[0].Path);
    }

    [Fact]
    public async Task Listener_CapturesMultipleRequestsInOrder()
    {
        var buffer = new WebhookCaptureBuffer();
        await using var listener = await WebhookListener.StartAsync(buffer);

        using var client = new HttpClient();
        await client.GetAsync(listener.Url + "/first");
        await client.GetAsync(listener.Url + "/second");
        await client.GetAsync(listener.Url + "/third");

        var captured = buffer.Snapshot();
        Assert.Equal(3, captured.Count);
        Assert.Equal("/first", captured[0].Path);
        Assert.Equal("/second", captured[1].Path);
        Assert.Equal("/third", captured[2].Path);
    }

    [Fact]
    public async Task Listener_AfterDispose_UrlNoLongerAnswers()
    {
        var buffer = new WebhookCaptureBuffer();
        var listener = await WebhookListener.StartAsync(buffer);
        var url = listener.Url;

        await listener.DisposeAsync();

        // After disposal the port is released; a dial must fail (connection refused)
        // rather than succeed.  Use a short timeout so the test does not hang.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.GetAsync(url + "/after-dispose", cts.Token);
        });
    }

    [Fact]
    public async Task Listener_DisposeAsync_IsIdempotent()
    {
        var buffer = new WebhookCaptureBuffer();
        var listener = await WebhookListener.StartAsync(buffer);

        await listener.DisposeAsync();
        await listener.DisposeAsync(); // second call must not throw
    }

    [Fact]
    public async Task Listener_StartAsync_NullBuffer_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await WebhookListener.StartAsync(null!));
    }

    // ── WebhookCaptureAccessor ────────────────────────────────────────────────

    [Fact]
    public void Accessor_GetCaptured_ReturnsRegisteredListenerRequests()
    {
        var buffer = new WebhookCaptureBuffer();
        buffer.Append(MakeRequest("POST", "/x"));

        var accessor = new WebhookCaptureAccessor(
            new Dictionary<string, WebhookCaptureBuffer>(StringComparer.Ordinal)
            {
                ["orders-callback"] = buffer,
            });

        var captured = accessor.GetCaptured("orders-callback");

        Assert.Single(captured);
        Assert.Equal("/x", captured[0].Path);
    }

    [Fact]
    public void Accessor_GetCaptured_UnknownName_ReturnsEmpty()
    {
        var accessor = new WebhookCaptureAccessor(
            new Dictionary<string, WebhookCaptureBuffer>(StringComparer.Ordinal)
            {
                ["known"] = new WebhookCaptureBuffer(),
            });

        Assert.Empty(accessor.GetCaptured("unknown"));
        Assert.Empty(accessor.GetCaptured(string.Empty));
    }

    [Fact]
    public void Accessor_GetCaptured_ReflectsLiveBufferAppends()
    {
        var buffer = new WebhookCaptureBuffer();
        var accessor = new WebhookCaptureAccessor(
            new Dictionary<string, WebhookCaptureBuffer>(StringComparer.Ordinal)
            {
                ["live"] = buffer,
            });

        Assert.Empty(accessor.GetCaptured("live"));

        buffer.Append(MakeRequest("POST", "/late"));

        // The accessor projects the SAME buffer instance, so a later append is visible.
        Assert.Single(accessor.GetCaptured("live"));
    }

    [Fact]
    public void Accessor_NullBuffers_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new WebhookCaptureAccessor(null!));
    }

    [Fact]
    public void Accessor_EmptyBufferMap_AllLookupsEmpty()
    {
        var accessor = new WebhookCaptureAccessor(
            new Dictionary<string, WebhookCaptureBuffer>(StringComparer.Ordinal));

        Assert.Empty(accessor.GetCaptured("anything"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CapturedWebhookRequest MakeRequest(string method, string path) =>
        new(
            Method: method,
            Path: path,
            Headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Body: string.Empty,
            At: DateTimeOffset.UtcNow);

    /// <summary>
    /// Returns the scheme+authority of a listener URL (e.g. <c>http://127.0.0.1:54321</c>),
    /// dropping the token path segment.
    /// </summary>
    private static string AuthorityOf(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>
    /// Returns the first path segment (the unguessable token) of a listener URL of the form
    /// <c>http://127.0.0.1:54321/&lt;token&gt;/</c>.
    /// </summary>
    private static string TokenSegmentOf(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        return uri.AbsolutePath.Trim('/');
    }
}
