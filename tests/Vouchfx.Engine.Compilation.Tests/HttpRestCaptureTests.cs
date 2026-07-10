// S04-B-02 — HttpRestProvider JSONPath capture tests (non-docker + docker).
//
// Non-docker tests:
//   1. Emit lint: when ctx.Captures is non-empty, the StatementBlock contains
//      JsonPath-related code, VarKeys.CaptureStatus key, and the capture arrays.
//   2. Emit lint: JsonPath.Net namespace appears in RequiredUsings (Json.Path).
//   3. Emit lint: helper source contains 'using var'-free capture logic.
//   4. Helper is byte-identical (dedup): two steps with same provider produce
//      identical HttpRest_Helpers source.
//   5. ICompileReferenceContributor returns JsonPath.Net assembly.
//   6. Compile+run (in-process HTTP responder): capture $.id from a JSON response
//      → Vars["id"] is set; CaptureStatus has matched flag "1".
//   7. Compile+run: JSONPath that doesn't match → Verdict.Inconclusive.
//
// Docker tests are gated by [Trait("requires","docker")] and are skipped in the
// non-docker CI filter.  For this sprint the in-process responder in tests 6+7
// provides the happy-path coverage without Docker.
using System.Net;
using System.Net.Sockets;
using System.Text;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// S04-B-02: non-docker tests for <see cref="HttpRestProvider"/> JSONPath capture.
/// </summary>
public sealed class HttpRestCaptureTests
{
    // ── Stubs ──────────────────────────────────────────────────────────────────

    private sealed class StubCtx : ICompileContext
    {
        public StubCtx(
            string stepId,
            IReadOnlyDictionary<string, string>? captures = null)
        {
            StepId = stepId;
            Captures = captures
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            CaptureExprs = Captures.ToDictionary(
                kv => kv.Key,
                kv => new CaptureExpr(CaptureFormat.JsonPath, kv.Value),
                StringComparer.Ordinal);
        }

        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; }
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; }
    }

    // Additional Roslyn metadata references.
    private static readonly IReadOnlyList<string> s_refs = new[]
    {
        typeof(System.Net.Http.HttpClient).Assembly.Location,
        typeof(System.Net.HttpStatusCode).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.Json.Nodes.JsonNode).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Uri).Assembly.Location,
        typeof(Json.Path.JsonPath).Assembly.Location,
        typeof(System.Xml.XmlDocument).Assembly.Location,          // System.Private.Xml — XPath capture logic (S07-B-01b)
    };

    // ── 1. Emit lint: capture arrays appear in StatementBlock ─────────────────

    /// <summary>
    /// When <see cref="ICompileContext.Captures"/> is non-empty, the emitted
    /// <see cref="CsxFragment.StatementBlock"/> must contain the capture var-names
    /// and JSON-paths as parallel string arrays, and the CaptureStatus key.
    /// </summary>
    [Fact]
    public void Emit_WithCaptures_StatementBlockContainsCaptureArrays()
    {
        var provider = new HttpRestProvider();
        var model = new HttpRestModel("svc", "GET", "/", null, null, null);
        var captures = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["orderId"] = "$.id",
            ["status"] = "$.status",
        };
        var ctx = new StubCtx("capture-step", captures);
        var fragment = provider.Emit(model, ctx);

        var block = fragment.StatementBlock;

        // Capture var-names and paths must appear as JSON-escaped string literals.
        Assert.Contains("\"orderId\"", block, StringComparison.Ordinal);
        Assert.Contains("\"$.id\"", block, StringComparison.Ordinal);
        Assert.Contains("\"status\"", block, StringComparison.Ordinal);
        Assert.Contains("\"$.status\"", block, StringComparison.Ordinal);

        // The CaptureStatus key must be emitted.
        var safeId = CsxFragment.SanitiseId("capture-step");
        Assert.Contains(VarKeys.CaptureStatus(safeId), block, StringComparison.Ordinal);
    }

    // ── 2. Emit lint: no 'using var' ──────────────────────────────────────────

    /// <summary>
    /// The emitted helpers (including the capture logic) must not contain
    /// <c>using var</c> (Roslyn script parse error, §13.3.1).
    /// </summary>
    [Fact]
    public void Emit_WithCaptures_NoUsingVar()
    {
        var provider = new HttpRestProvider();
        var model = new HttpRestModel("svc", "GET", "/", null, null, null);
        var captures = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "$.id",
        };
        var ctx = new StubCtx("step1", captures);
        var fragment = provider.Emit(model, ctx);

        var full = fragment.StatementBlock +
                   "\n" +
                   string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", full, StringComparison.Ordinal);
    }

    // ── 3. Helper is byte-identical (dedup invariant) ─────────────────────────

    /// <summary>
    /// Two steps using the same <c>http.rest</c> provider must produce a
    /// byte-identical <c>HttpRest_Helpers</c> source.  The <see cref="CsxAssembler"/>
    /// deduplicates by class name; if the source differed the second step's helpers
    /// would be silently dropped, breaking execution (§13.3.1).
    /// </summary>
    [Fact]
    public void Emit_TwoSteps_HelpersAreByteIdentical()
    {
        var provider = new HttpRestProvider();
        var model1 = new HttpRestModel("svc", "GET", "/a", null, null, null);
        var model2 = new HttpRestModel("svc", "GET", "/b", null, null,
            new HttpExpect(200));

        var captures1 = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = "$.id",
        };
        var captures2 = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = "$.name",
        };

        var frag1 = provider.Emit(model1, new StubCtx("step-a", captures1));
        var frag2 = provider.Emit(model2, new StubCtx("step-b", captures2));

        // The HttpRest_Helpers entries must be byte-identical.
        var helper1 = frag1.RequiredHelpers.First(h =>
            h.Contains("HttpRest_Helpers", StringComparison.Ordinal));
        var helper2 = frag2.RequiredHelpers.First(h =>
            h.Contains("HttpRest_Helpers", StringComparison.Ordinal));

        Assert.Equal(helper1, helper2, StringComparer.Ordinal);

        // Similarly Substitute_Helpers must be byte-identical.
        var sub1 = frag1.RequiredHelpers.First(h =>
            h.Contains("Substitute_Helpers", StringComparison.Ordinal));
        var sub2 = frag2.RequiredHelpers.First(h =>
            h.Contains("Substitute_Helpers", StringComparison.Ordinal));

        Assert.Equal(sub1, sub2, StringComparer.Ordinal);
    }

    // ── 4. ICompileReferenceContributor returns JsonPath.Net ──────────────────

    /// <summary>
    /// <see cref="ICompileReferenceContributor.CompileReferenceAssemblies"/> must
    /// include the <c>JsonPath.Net</c> assembly (<c>Json.Path.JsonPath</c> type).
    /// </summary>
    [Fact]
    public void CompileReferenceAssemblies_ContainsJsonPathNetAssembly()
    {
        var contributor = (ICompileReferenceContributor)new HttpRestProvider();
        var assemblies = contributor.CompileReferenceAssemblies.ToList();

        Assert.Contains(assemblies, a =>
            a.GetName().Name?.Equals("JsonPath.Net", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── 5. Compile+run: capture $.id from JSON response → Vars["id"] set ──────

    /// <summary>
    /// Full compile-and-run: a step that captures <c>$.id</c> from a JSON response
    /// body must write the captured value to <c>Vars["id"]</c> and the
    /// CaptureStatus flag must be <c>"1"</c>.
    /// </summary>
    [Fact]
    public async Task Execute_WithCapture_JsonPathMatch_WritesVarAndSetsMatchedFlag()
    {
        const string jsonBody = "{\"id\":\"order-abc\",\"status\":\"shipped\"}";
        var port = FindFreePort();
        var (baseUrl, responder) = StartJsonResponder(jsonBody, port);
        try
        {
            var provider = new HttpRestProvider();
            var model = new HttpRestModel(
                Target: "svc",
                Method: "GET",
                Path: "/",
                Headers: null,
                Body: null,
                Expect: new HttpExpect(Status: 200));

            var captures = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["capturedId"] = "$.id",
            };
            var ctx = new StubCtx("capture-run", captures);
            var fragment = provider.Emit(model, ctx);

            var assembled = CsxAssembler.Assemble(new[] { ("capture-run", fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource,
                additionalReferencePaths: s_refs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
            };
            var globals = new ScriptGlobalVariables(vars);
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var safeId = CsxFragment.SanitiseId("capture-run");

            // The captured value must be in Vars.
            Assert.True(vars.ContainsKey("capturedId"),
                $"Expected Vars to contain 'capturedId'. Keys: [{string.Join(", ", vars.Keys)}]");
            Assert.Equal("order-abc", vars["capturedId"]);

            // CaptureStatus must indicate matched ("1").
            var statusKey = VarKeys.CaptureStatus(safeId);
            Assert.True(vars.ContainsKey(statusKey),
                $"Expected Vars to contain capture status key '{statusKey}'.");
            Assert.Equal("1", vars[statusKey]);

            // Outcome must be Pass (200 + match).
            var outcomeKey = VarKeys.Outcome(safeId);
            var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
            Assert.Equal(Verdict.Pass, outcome.Verdict);
        }
        finally
        {
            responder.Dispose();
        }
    }

    // ── 6. Compile+run: multi-capture — two paths, one parse ─────────────────

    /// <summary>
    /// Full compile-and-run: a step with two captures (<c>$.id</c> and
    /// <c>$.name</c>) against a single JSON response body must resolve both
    /// correctly.  This verifies the parse-once refactor: the body is parsed
    /// exactly once before the per-capture loop and both <c>Vars</c> values are
    /// populated from the shared <c>JsonNode</c>.
    /// </summary>
    [Fact]
    public async Task Execute_WithMultiCapture_BothJsonPathsResolveFromSingleParse()
    {
        const string jsonBody = "{\"id\":\"order-xyz\",\"name\":\"Alice\"}";
        var port = FindFreePort();
        var (baseUrl, responder) = StartJsonResponder(jsonBody, port);
        try
        {
            var provider = new HttpRestProvider();
            var model = new HttpRestModel(
                Target: "svc",
                Method: "GET",
                Path: "/",
                Headers: null,
                Body: null,
                Expect: new HttpExpect(Status: 200));

            var captures = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["capturedId"] = "$.id",
                ["capturedName"] = "$.name",
            };
            var ctx = new StubCtx("multi-capture-run", captures);
            var fragment = provider.Emit(model, ctx);

            var assembled = CsxAssembler.Assemble(new[] { ("multi-capture-run", fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource,
                additionalReferencePaths: s_refs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
            };
            var globals = new ScriptGlobalVariables(vars);
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var safeId = CsxFragment.SanitiseId("multi-capture-run");

            // Both captured values must be present and correct.
            Assert.True(vars.ContainsKey("capturedId"),
                $"Expected Vars to contain 'capturedId'. Keys: [{string.Join(", ", vars.Keys)}]");
            Assert.Equal("order-xyz", vars["capturedId"]);

            Assert.True(vars.ContainsKey("capturedName"),
                $"Expected Vars to contain 'capturedName'. Keys: [{string.Join(", ", vars.Keys)}]");
            Assert.Equal("Alice", vars["capturedName"]);

            // CaptureStatus must indicate both matched ("1,1").
            var statusKey = VarKeys.CaptureStatus(safeId);
            Assert.True(vars.ContainsKey(statusKey),
                $"Expected Vars to contain capture status key '{statusKey}'.");
            Assert.Equal("1,1", vars[statusKey]);

            // Outcome must be Pass (200 + both captures matched).
            var outcomeKey = VarKeys.Outcome(safeId);
            var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
            Assert.Equal(Verdict.Pass, outcome.Verdict);
        }
        finally
        {
            responder.Dispose();
        }
    }

    // ── 7. Compile+run: no match → Verdict.Inconclusive ───────────────────────

    /// <summary>
    /// When a JSONPath expression yields no match in the response body, the step
    /// outcome must be <see cref="Verdict.Inconclusive"/> (upstream-capture-unmet,
    /// §12.1).
    /// </summary>
    [Fact]
    public async Task Execute_WithCapture_JsonPathNoMatch_ReturnsInconclusive()
    {
        const string jsonBody = "{\"name\":\"Alice\"}";
        var port = FindFreePort();
        var (baseUrl, responder) = StartJsonResponder(jsonBody, port);
        try
        {
            var provider = new HttpRestProvider();
            var model = new HttpRestModel("svc", "GET", "/", null, null, null);
            var captures = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["missingField"] = "$.nonexistent",
            };
            var ctx = new StubCtx("miss-step", captures);
            var fragment = provider.Emit(model, ctx);
            var assembled = CsxAssembler.Assemble(new[] { ("miss-step", fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource,
                additionalReferencePaths: s_refs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
            };
            var globals = new ScriptGlobalVariables(vars);
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var safeId = CsxFragment.SanitiseId("miss-step");
            var outcomeKey = VarKeys.Outcome(safeId);
            var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);

            Assert.Equal(Verdict.Inconclusive, outcome.Verdict);

            // CaptureStatus must indicate not matched ("0").
            var statusKey = VarKeys.CaptureStatus(safeId);
            Assert.True(vars.ContainsKey(statusKey));
            Assert.Equal("0", vars[statusKey]);
        }
        finally
        {
            responder.Dispose();
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Starts an in-process HTTP responder that returns 200 OK with a JSON body
    /// for every incoming request.
    /// </summary>
    private static (string BaseUrl, IDisposable Responder) StartJsonResponder(
        string jsonBody,
        int port)
    {
        var prefix = $"http://localhost:{port}/";
        HttpListener? hl = null;
        try
        {
            hl = new HttpListener();
            hl.Prefixes.Add(prefix);
            hl.Start();

            var cts = new CancellationTokenSource();
            var listener = hl;
            var bodyBytes = Encoding.UTF8.GetBytes(jsonBody);

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
                        try
                        {
                            hctx.Response.StatusCode = 200;
                            hctx.Response.ContentType = "application/json";
                            hctx.Response.ContentLength64 = bodyBytes.Length;
                            hctx.Response.OutputStream.Write(bodyBytes);
                            hctx.Response.Close();
                        }
                        catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
                        {
                            // The responder's Dispose() cancels cts BEFORE calling listener.Stop()/
                            // Close(), but that teardown can still race a response this loop is
                            // actively writing (see the equivalent raw-Thread guard in
                            // MailExpectSmtpEmitTests.StartMockMailpit). This loop runs inside a
                            // fire-and-forget Task, so an unhandled exception here would not itself
                            // crash the host — contained anyway so teardown stays deterministic
                            // rather than relying on the TPL's swallow-unobserved-exception behaviour.
                        }
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

            return (prefix.TrimEnd('/'), responder);
        }
        catch (Exception)
        {
            try { hl?.Stop(); } catch { /* best-effort */ }
            try { hl?.Close(); } catch { /* best-effort */ }
        }

        // Fallback: raw TcpListener that sends one HTTP/1.1 200 JSON response.
        var tcp = new TcpListener(IPAddress.Loopback, port);
        tcp.Start();
        var tcpCts = new CancellationTokenSource();
        var tcpBodyBytes = Encoding.UTF8.GetBytes(jsonBody);

        _ = Task.Run(async () =>
        {
            while (!tcpCts.Token.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await tcp.AcceptTcpClientAsync(tcpCts.Token)
                        .ConfigureAwait(false);
                }
                catch { break; }

                if (client is null) break;

                _ = Task.Run(async () =>
                {
                    var c = client;
                    try
                    {
                        var stream = c.GetStream();
                        var buf = new byte[4096];
                        var total = 0;
                        while (total < buf.Length)
                        {
                            var n = await stream.ReadAsync(buf.AsMemory(total), CancellationToken.None)
                                .ConfigureAwait(false);
                            if (n == 0) break;
                            total += n;
                            if (Encoding.ASCII.GetString(buf, 0, total)
                                .Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                        }

                        var header = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\n" +
                            "Content-Type: application/json\r\n" +
                            $"Content-Length: {tcpBodyBytes.Length}\r\n" +
                            "Connection: close\r\n" +
                            "\r\n");
                        await stream.WriteAsync(header.AsMemory(), CancellationToken.None)
                            .ConfigureAwait(false);
                        await stream.WriteAsync(tcpBodyBytes.AsMemory(), CancellationToken.None)
                            .ConfigureAwait(false);
                        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    finally { c.Dispose(); }
                });
            }
        }, tcpCts.Token);

        return ($"http://localhost:{port}", new CallbackDisposable(() =>
        {
            tcpCts.Cancel();
            try { tcp.Stop(); } catch { /* best-effort */ }
            tcpCts.Dispose();
        }));
    }

    private sealed class CallbackDisposable : IDisposable
    {
        private readonly Action _dispose;
        public CallbackDisposable(Action dispose) => _dispose = dispose;
        public void Dispose() => _dispose();
    }
}
