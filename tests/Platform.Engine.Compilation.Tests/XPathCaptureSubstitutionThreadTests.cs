// S07-B-02b — XPath-captured value threads forward into {placeholder} substitution (no Docker).
//
// PROVES: a value captured via the NEW XPath capture source (S07-B-01b) participates in
// {placeholder} substitution EXACTLY like a JSONPath-captured value.  Two http.rest steps
// run in ONE assembled CSX submission so they share the same Vars dictionary:
//
//   step 1 (capture-xml) — GET an XML body, capture /order/id via XPath into Vars["orderRef"].
//   step 2 (use-ref)     — GET "/lookup/{orderRef}"; the helper resolves {orderRef} via
//                          Secret_Helpers.ResolveTemplate (the same single-pass resolver
//                          every substituting provider uses), so the request path contains
//                          the XPath-captured value.
//
// The in-process responder RECORDS every request path it receives, so the test asserts the
// second request's path was "/lookup/order-xml-7" — i.e. the XPath-captured value threaded
// forward through {placeholder} substitution.  A Vars-level assertion (Vars["orderRef"]) is
// also made so the capture itself is unambiguous.
//
// Non-docker: an in-process HttpListener serves the XML body and the lookup, mirroring the
// responder in HttpRestXPathCaptureTests but recording request paths.
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.HttpRest;
using Xunit;

namespace Platform.Engine.Compilation.Tests;

/// <summary>
/// S07-B-02b: proves an XPath-captured value threads forward into a later step's
/// <c>{placeholder}</c> substitution exactly like a JSONPath-captured value.
/// </summary>
public sealed class XPathCaptureSubstitutionThreadTests
{
    private static readonly IReadOnlyList<string> s_refs = new[]
    {
        typeof(System.Net.Http.HttpClient).Assembly.Location,
        typeof(System.Net.HttpStatusCode).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.Json.Nodes.JsonNode).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Uri).Assembly.Location,
        typeof(Json.Path.JsonPath).Assembly.Location,
        typeof(System.Xml.XmlDocument).Assembly.Location,
    };

    /// <summary>
    /// Full compile-round-trip: step 1 captures <c>/order/id</c> via XPath into
    /// <c>Vars["orderRef"]</c>; step 2 substitutes <c>{orderRef}</c> into its request path
    /// <c>/lookup/{orderRef}</c>.  The responder records the path it was called with, and the
    /// test asserts the second request hit <c>/lookup/order-xml-7</c> — the XPath-captured
    /// value threaded forward through the shared <c>{placeholder}</c> resolver.
    /// </summary>
    [Fact]
    public async Task XPathCapturedValue_ThreadsForward_IntoPlaceholderSubstitution()
    {
        const string xmlBody = "<order><id>order-xml-7</id><status>shipped</status></order>";
        var port = FindFreePort();
        var (baseUrl, requestPaths, responder) = StartRecordingResponder(xmlBody, "application/xml", port);
        try
        {
            var provider = new HttpRestProvider();

            // ── step 1: capture /order/id via XPath into Vars["orderRef"] ──
            var captureModel = new HttpRestModel(
                Target: "svc",
                Method: "GET",
                Path: "/order",
                Headers: null,
                Body: null,
                Expect: new HttpExpect(Status: 200));
            var captureCtx = new StubCtx(
                "capture-xml",
                new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
                {
                    ["orderRef"] = new CaptureExpr(CaptureFormat.XPath, "/order/id"),
                });
            var captureFragment = provider.Emit(captureModel, captureCtx);

            // ── step 2: substitute {orderRef} into the request path ──
            var useModel = new HttpRestModel(
                Target: "svc",
                Method: "GET",
                Path: "/lookup/{orderRef}",
                Headers: null,
                Body: null,
                Expect: new HttpExpect(Status: 200));
            var useCtx = new StubCtx(
                "use-ref",
                new Dictionary<string, CaptureExpr>(StringComparer.Ordinal));
            var useFragment = provider.Emit(useModel, useCtx);

            // Assemble BOTH steps into one submission so they share Vars (the engine's model).
            var assembled = CsxAssembler.Assemble(new[]
            {
                ("capture-xml", captureFragment),
                ("use-ref", useFragment),
            });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_refs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
            };
            var globals = new ScriptGlobalVariables(vars);
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            // ── Vars-level proof: the XPath capture landed ──
            Assert.True(vars.ContainsKey("orderRef"),
                $"Expected Vars to contain 'orderRef'. Keys: [{string.Join(", ", vars.Keys)}]");
            Assert.Equal("order-xml-7", vars["orderRef"]);

            // ── Wire-level proof: step 2's path substituted the captured value ──
            // The first recorded path is step 1 ("/order"); the second is step 2's resolved
            // "/lookup/order-xml-7" — the {orderRef} placeholder was filled from the XPath
            // capture by the same single-pass resolver JSONPath captures flow through.
            Assert.Contains("/lookup/order-xml-7", requestPaths,
                StringComparer.Ordinal);

            // Both steps passed (the responder returns 200 for every path).
            var useOutcome = Assert.IsType<StepOutcome>(
                vars[VarKeys.Outcome(CsxFragment.SanitiseId("use-ref"))]);
            Assert.Equal(Verdict.Pass, useOutcome.Verdict);
        }
        finally
        {
            responder.Dispose();
        }
    }

    // ── stub compile context (format-aware, mirrors HttpRestXPathCaptureTests) ────

    private sealed class StubCtx : ICompileContext
    {
        public StubCtx(string stepId, IReadOnlyDictionary<string, CaptureExpr> captureExprs)
        {
            StepId = stepId;
            CaptureExprs = captureExprs;
            Captures = captureExprs.ToDictionary(
                kv => kv.Key, kv => kv.Value.Expression, StringComparer.Ordinal);
        }

        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; }
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; }
    }

    // ── recording responder ───────────────────────────────────────────────────────

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Starts an in-process HTTP responder that returns 200 OK with <paramref name="body"/>
    /// for every request and RECORDS each request's raw path into the returned concurrent
    /// collection, so the test can assert which paths the steps actually requested.
    /// </summary>
    private static (string BaseUrl, ConcurrentBag<string> RequestPaths, IDisposable Responder)
        StartRecordingResponder(string body, string contentType, int port)
    {
        var prefix = $"http://localhost:{port}/";
        var paths = new ConcurrentBag<string>();
        var hl = new HttpListener();
        hl.Prefixes.Add(prefix);
        hl.Start();

        var cts = new CancellationTokenSource();
        var listener = hl;
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                HttpListenerContext? hctx = null;
                try
                {
                    hctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }

                if (hctx is not null)
                {
                    // Record the path-and-query the step actually requested.
                    paths.Add(hctx.Request.Url?.AbsolutePath ?? hctx.Request.RawUrl ?? string.Empty);

                    hctx.Response.StatusCode = 200;
                    hctx.Response.ContentType = contentType;
                    hctx.Response.ContentLength64 = bodyBytes.Length;
                    hctx.Response.OutputStream.Write(bodyBytes);
                    hctx.Response.Close();
                }
            }
        }, cts.Token);

        var disposed = false;
        var responder = new CallbackDisposable(() =>
        {
            if (!disposed)
            {
                disposed = true;
                cts.Cancel();
                try { listener.Stop(); } catch { /* already stopped */ }
                try { listener.Close(); } catch { /* already closed */ }
                cts.Dispose();
            }
        });

        return (prefix.TrimEnd('/'), paths, responder);
    }

    private sealed class CallbackDisposable : IDisposable
    {
        private readonly Action _dispose;
        public CallbackDisposable(Action dispose) => _dispose = dispose;
        public void Dispose() => _dispose();
    }
}
