// S07-B-02a — HttpRestProvider request BODY tests (non-docker).
//
// Brings the http.rest request body into scope. Before this task the body was
// parsed-but-dropped (Bind hardcoded Body: null), so POST/PUT scenarios could not
// send a payload. These tests prove:
//
//   1. Bind: a YAML scalar body binds to its literal string; a YAML mapping body
//      binds to a serialised JSON string. {placeholder} / ${secret:...} tokens
//      survive verbatim into the template (NOT resolved at bind time).
//   2. Emit lint: a POST with a body containing {placeholder} + ${secret:env/X}
//      emits the bodyTemplate as a RAW literal (not pre-resolved) and passes it to
//      the helper; no 'using var'; helper byte-identical across instances.
//   3. Emit lint: a GET with no body emits the bare 'null' bodyTemplate literal.
//   4. Execution (compile-round-trip, no Docker): a POST whose body templates a
//      captured var + an ${secret:env/...} reference resolves both at runtime —
//      the placeholder from Vars and the secret from the SecretAccessor — and the
//      resolved body is what the responder actually receives. The body value is
//      NOT echoed via any var (reference-only secret handling, §17).
//   5. Execution: a missing secret in the body → EnvironmentError with a
//      reference-only observation (source/path coordinates, no secret value).
//   6. Execution: a GET with no body still works unchanged (no content sent).
//
// The in-process responder is an extension of the JSON responder in
// HttpRestCaptureTests: it ECHOES the inbound request body back as the response
// body AND records it for direct assertion, so the sent body can be observed
// without Docker.
using System.Net;
using System.Net.Sockets;
using System.Text;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// S07-B-02a: non-docker tests for the <see cref="HttpRestProvider"/> request body.
/// </summary>
public sealed class HttpRestBodyTests
{
    // ── Stubs ──────────────────────────────────────────────────────────────────

    /// <summary>Marker <see cref="IBindingContext"/> stub for Bind tests.</summary>
    private sealed class StubBindingContext : IBindingContext { }

    /// <summary>
    /// <see cref="ICompileContext"/> stub; optionally carries a JSONPath capture map.
    /// </summary>
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

    // Additional Roslyn metadata references for the emitted CSX body.
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

    // ── 1. Bind: scalar body and mapping body ─────────────────────────────────

    /// <summary>
    /// A YAML scalar <c>body</c> (a raw / inline JSON string) binds to its literal
    /// string, and any <c>{placeholder}</c> / <c>${secret:...}</c> token survives
    /// verbatim (no bind-time resolution).
    /// </summary>
    [Fact]
    public void Bind_ScalarBody_BindsToLiteralStringTemplate()
    {
        const string yaml = """
            target: svc
            method: POST
            path: /orders
            body: '{"id":"{orderId}","token":"${secret:env/API}"}'
            """;
        var model = BindYaml(yaml);

        Assert.Equal(
            "{\"id\":\"{orderId}\",\"token\":\"${secret:env/API}\"}",
            model.Body);
    }

    /// <summary>
    /// A YAML mapping <c>body</c> binds to a serialised JSON string; nested
    /// placeholder / secret tokens survive verbatim as JSON string values.
    /// </summary>
    [Fact]
    public void Bind_MappingBody_BindsToJsonStringTemplate()
    {
        const string yaml = """
            target: svc
            method: POST
            path: /orders
            body:
              id: "{orderId}"
              token: "${secret:env/API}"
              quantity: 3
              active: true
            """;
        var model = BindYaml(yaml);

        Assert.NotNull(model.Body);

        // The structured body is serialised to JSON; the tokens are preserved as
        // string values and the typed scalars keep their JSON types.
        var node = System.Text.Json.Nodes.JsonNode.Parse(model.Body!);
        Assert.NotNull(node);
        Assert.Equal("{orderId}", (string?)node!["id"]);
        Assert.Equal("${secret:env/API}", (string?)node["token"]);
        Assert.Equal(3, (int)node["quantity"]!);
        Assert.True((bool)node["active"]!);
    }

    /// <summary>
    /// A step with no <c>body</c> key binds to a <see langword="null"/> body.
    /// </summary>
    [Fact]
    public void Bind_NoBody_BindsToNull()
    {
        const string yaml = """
            target: svc
            method: GET
            path: /orders
            """;
        var model = BindYaml(yaml);

        Assert.Null(model.Body);
    }

    // ── 2. Emit lint: POST body is a RAW literal passed to the helper ─────────

    /// <summary>
    /// A POST with a body containing a <c>{placeholder}</c> and a
    /// <c>${secret:env/X}</c> reference must emit the body as a RAW literal (the
    /// tokens survive verbatim, NOT pre-resolved) and pass it to the helper.
    /// </summary>
    [Fact]
    public void Emit_PostWithBody_EmitsRawTemplateLiteral_NotPreResolved()
    {
        const string bodyTemplate = "{\"id\":\"{orderId}\",\"token\":\"${secret:env/API}\"}";

        var provider = new HttpRestProvider();
        var model = new HttpRestModel(
            Target: "svc",
            Method: "POST",
            Path: "/orders",
            Headers: null,
            Body: bodyTemplate,
            Expect: new HttpExpect(201));
        var fragment = provider.Emit(model, new StubCtx("post-step"));

        var block = fragment.StatementBlock;

        // The RAW template tokens must be present verbatim in the emitted block
        // (proof the body is NOT pre-resolved at emit time).
        Assert.Contains("{orderId}", block, StringComparison.Ordinal);
        Assert.Contains("${secret:env/API}", block, StringComparison.Ordinal);

        // The helper resolves the body via Secret_Helpers.ResolveTemplate at runtime.
        var helperSource = string.Join("\n", fragment.RequiredHelpers);
        Assert.Contains(
            "Secret_Helpers.ResolveTemplate(secrets, vars, bodyTemplate)",
            helperSource,
            StringComparison.Ordinal);
        Assert.Contains("bodyTemplate", helperSource, StringComparison.Ordinal);
        Assert.Contains("application/json", helperSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// No <c>using var</c> anywhere in a POST-with-body fragment (§13.3.1).
    /// </summary>
    [Fact]
    public void Emit_PostWithBody_NoUsingVar()
    {
        var provider = new HttpRestProvider();
        var model = new HttpRestModel(
            "svc", "POST", "/orders", null, "{\"a\":1}", new HttpExpect(201));
        var fragment = provider.Emit(model, new StubCtx("post-step"));

        var full = fragment.StatementBlock +
                   "\n" +
                   string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", full, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>HttpRest_Helpers</c> source is byte-identical whether or not a step
    /// declares a body — the body is plumbed as a parameter, never as per-step
    /// helper interpolation (§13.3.1 dedup rule).
    /// </summary>
    [Fact]
    public void Emit_BodyAndNoBody_HelpersAreByteIdentical()
    {
        var provider = new HttpRestProvider();
        var withBody = provider.Emit(
            new HttpRestModel("svc", "POST", "/a", null, "{\"x\":1}", null),
            new StubCtx("step-a"));
        var noBody = provider.Emit(
            new HttpRestModel("svc", "GET", "/b", null, null, null),
            new StubCtx("step-b"));

        var helperWith = withBody.RequiredHelpers.First(h =>
            h.Contains("HttpRest_Helpers", StringComparison.Ordinal));
        var helperNo = noBody.RequiredHelpers.First(h =>
            h.Contains("HttpRest_Helpers", StringComparison.Ordinal));

        Assert.Equal(helperNo, helperWith, StringComparer.Ordinal);
    }

    // ── 3. Emit lint: GET without body emits bare 'null' ──────────────────────

    /// <summary>
    /// A GET with no body emits the bare <c>null</c> bodyTemplate literal — the
    /// helper receives <c>null</c> and sends no content.
    /// </summary>
    [Fact]
    public void Emit_GetWithoutBody_EmitsBareNullBodyLiteral()
    {
        var provider = new HttpRestProvider();
        var model = new HttpRestModel("svc", "GET", "/orders", null, null, null);
        var fragment = provider.Emit(model, new StubCtx("get-step"));

        // The ExecuteAsync call must pass a bare 'null' for the body argument; the
        // literal must NOT be the quoted string "null".
        Assert.Contains("null,", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("\"null\"", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 4. Execution: POST body templates a captured var + a secret ───────────

    /// <summary>
    /// A POST whose body templates a captured var <c>{orderId}</c> and an
    /// <c>${secret:env/...}</c> reference must resolve BOTH at runtime — the
    /// placeholder from <c>Vars</c> and the secret from the
    /// <see cref="SecretAccessor"/> — and the resolved body must be exactly what the
    /// responder receives.  The secret value is fed only to the content sink and is
    /// never written back to any var (§17).
    /// </summary>
    [Fact]
    public async Task Execute_PostBody_ResolvesPlaceholderAndSecret_AtRuntime()
    {
        var envName = "VOUCHFX_BODY_SECRET_" + Guid.NewGuid().ToString("N");
        const string secretValue = "s3cr3t-body-token";

        Environment.SetEnvironmentVariable(envName, secretValue);
        var port = FindFreePort();
        var (baseUrl, responder) = StartEchoResponder(port);
        try
        {
            var bodyTemplate =
                $"{{\"id\":\"{{orderId}}\",\"token\":\"${{secret:env/{envName}}}\"}}";

            var provider = new HttpRestProvider();
            var model = new HttpRestModel(
                Target: "svc",
                Method: "POST",
                Path: "/orders",
                Headers: null,
                Body: bodyTemplate,
                Expect: new HttpExpect(Status: 200));

            var fragment = provider.Emit(model, new StubCtx("post-run"));
            var assembled = CsxAssembler.Assemble(new[] { ("post-run", fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_refs);

            // The captured var threaded forward from a prior step.
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
                ["orderId"] = "order-42",
            };

            // Real env-backed secret accessor.
            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            var globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                accessor);

            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            // Outcome is Pass (200 echo).
            var safeId = CsxFragment.SanitiseId("post-run");
            var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(safeId)]);
            Assert.Equal(Verdict.Pass, outcome.Verdict);

            // The responder received the FULLY RESOLVED body: placeholder filled from
            // Vars, secret revealed from the accessor.
            var received = responder.LastBody;
            Assert.Equal(
                "{\"id\":\"order-42\",\"token\":\"" + secretValue + "\"}",
                received);

            // The secret value must NOT have leaked into any var or the observation.
            foreach (var (k, v) in vars)
            {
                if (v is string sv)
                {
                    Assert.DoesNotContain(secretValue, sv, StringComparison.Ordinal);
                }
            }
            Assert.NotNull(outcome.Observation);
            Assert.DoesNotContain(secretValue, outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            responder.Dispose();
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── 5. Execution: missing secret in body → EnvironmentError ───────────────

    /// <summary>
    /// A missing secret referenced in the body must be caught inside the helper's
    /// guarded region and written as a per-step
    /// <see cref="Verdict.EnvironmentError"/> with a reference-only observation: the
    /// source/path coordinates appear, but no secret value (there is none) and the
    /// request must never be sent.
    /// </summary>
    [Fact]
    public async Task Execute_PostBody_MissingSecret_ReturnsEnvironmentError_ReferenceOnly()
    {
        var envName = "VOUCHFX_BODY_MISSING_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, null);  // guaranteed absent

        var port = FindFreePort();
        var (baseUrl, responder) = StartEchoResponder(port);
        try
        {
            var bodyTemplate = $"{{\"token\":\"${{secret:env/{envName}}}\"}}";

            var provider = new HttpRestProvider();
            var model = new HttpRestModel(
                Target: "svc",
                Method: "POST",
                Path: "/orders",
                Headers: null,
                Body: bodyTemplate,
                Expect: new HttpExpect(Status: 200));

            var fragment = provider.Emit(model, new StubCtx("missing-secret-body"));
            var assembled = CsxAssembler.Assemble(
                new[] { ("missing-secret-body", fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_refs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
            };
            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            var globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                accessor);

            // Must NOT throw — the exception is contained inside the step.
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var safeId = CsxFragment.SanitiseId("missing-secret-body");
            var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(safeId)]);
            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);

            // Reference-only observation: the source/path coordinates appear; the
            // exception message is deliberately excluded (§17).
            Assert.NotNull(outcome.Observation);
            Assert.Contains("secret resolution failed", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains(envName, outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains("\"source\":\"env\"", outcome.Observation!, StringComparison.Ordinal);

            // The request was never sent — the responder recorded no body.
            Assert.Null(responder.LastBody);
        }
        finally
        {
            responder.Dispose();
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── 6. Execution: GET without body unchanged ──────────────────────────────

    /// <summary>
    /// A GET with no body still works unchanged: the request is sent with no
    /// content and the outcome is <see cref="Verdict.Pass"/>.
    /// </summary>
    [Fact]
    public async Task Execute_GetWithoutBody_SendsNoContent_Unchanged()
    {
        var port = FindFreePort();
        var (baseUrl, responder) = StartEchoResponder(port);
        try
        {
            var provider = new HttpRestProvider();
            var model = new HttpRestModel(
                Target: "svc",
                Method: "GET",
                Path: "/health",
                Headers: null,
                Body: null,
                Expect: new HttpExpect(Status: 200));

            var fragment = provider.Emit(model, new StubCtx("get-run"));
            var assembled = CsxAssembler.Assemble(new[] { ("get-run", fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_refs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("svc")] = baseUrl,
            };
            var globals = new ScriptGlobalVariables(vars);

            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var safeId = CsxFragment.SanitiseId("get-run");
            var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(safeId)]);
            Assert.Equal(Verdict.Pass, outcome.Verdict);

            // No content was sent (empty body recorded by the responder).
            Assert.Equal(string.Empty, responder.LastBody);
        }
        finally
        {
            responder.Dispose();
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a single-step YAML mapping and binds it through the provider.
    /// </summary>
    private static HttpRestModel BindYaml(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        return new HttpRestProvider().Bind(root, new StubBindingContext());
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// In-process HTTP responder that records the inbound request body and echoes it
    /// back as a 200 response.  Lets a test assert the exact body the helper sent
    /// without Docker.
    /// </summary>
    private sealed class EchoResponder : IDisposable
    {
        private readonly Action _dispose;

        /// <summary>The most recently received request body (null if none yet).</summary>
        public volatile string? LastBody;

        public EchoResponder(Action dispose) => _dispose = dispose;

        public void Dispose() => _dispose();
    }

    private static (string BaseUrl, EchoResponder Responder) StartEchoResponder(int port)
    {
        var prefix = $"http://localhost:{port}/";
        var hl = new HttpListener();
        hl.Prefixes.Add(prefix);
        hl.Start();

        var cts = new CancellationTokenSource();
        var responder = new EchoResponder(() =>
        {
            cts.Cancel();
            try { hl.Stop(); } catch { /* already stopped */ }
            try { hl.Close(); } catch { /* already closed */ }
            cts.Dispose();
        });

        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                HttpListenerContext? hctx = null;
                try
                {
                    hctx = await hl.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }

                if (hctx is null) continue;

                string body;
                using (var reader = new StreamReader(
                    hctx.Request.InputStream, hctx.Request.ContentEncoding))
                {
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                responder.LastBody = body;

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(body);
                    hctx.Response.StatusCode = 200;
                    hctx.Response.ContentType = "application/json";
                    hctx.Response.ContentLength64 = bytes.Length;
                    hctx.Response.OutputStream.Write(bytes);
                    hctx.Response.Close();
                }
                catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
                {
                    // The responder's Dispose() cancels cts BEFORE calling hl.Stop()/Close(), but
                    // that teardown can still race a response this loop is actively writing (see
                    // the equivalent raw-Thread guard in
                    // MailExpectSmtpEmitTests.StartMockMailpit). This loop runs inside a
                    // fire-and-forget Task, so an unhandled exception here would not itself crash
                    // the host — contained anyway so teardown stays deterministic rather than
                    // relying on the TPL's swallow-unobserved-exception behaviour.
                }
            }
        }, cts.Token);

        return (prefix.TrimEnd('/'), responder);
    }
}
