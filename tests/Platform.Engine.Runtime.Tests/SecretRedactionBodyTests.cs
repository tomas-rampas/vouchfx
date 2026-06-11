// S07-B-02b — redaction proof extended to the http.rest BODY (non-docker, §17).
//
// Two complementary guarantees, both without Docker:
//
//  A. PROVENANCE + RENDER redaction (event-derivation layer, mirrors SecretRedactionTests):
//     An http.rest step whose BODY carries `${secret:env/<name>}` must
//       • emit a SubstitutionRef with secretDerived:true carrying the reference label only;
//       • never place the resolved VALUE in any buffered event line;
//       • never surface the VALUE through TerminalRenderer.
//     This is the S07-B-02b gap fix: before it, the body was omitted from
//     CollectSubstitutableTexts, so a body secret produced NO provenance entry (under-
//     reported) and bypassed the pre-compile secret-validation pass.
//
//  B. NO-WRITE-BACK redaction (compile+run layer):
//     A real http.rest step with a secret in its BODY is compiled and executed against an
//     in-process recording responder.  The responder proves the secret WAS revealed into the
//     request body (so body substitution genuinely works), while the test proves the revealed
//     VALUE is absent from EVERY Vars entry after the run — the revealed value is a transient
//     feeding the request sink, never written back (§17, SecretHelper "transient feeding a
//     sink" contract).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;
using Platform.Engine.Abstractions.Secrets;
using Platform.Engine.Authoring;
using Platform.Engine.Authoring.Ast;
using Platform.Engine.Compilation;
using Platform.Engine.Reporting;
using Platform.Sdk;
using Platform.Steps.HttpRest;
using Xunit;

namespace Platform.Engine.Runtime.Tests;

/// <summary>
/// S07-B-02b: proves secret redaction holds for the http.rest <c>body</c> field — at the
/// provenance/render layer and at the compile+run (no-write-back) layer (§17).
/// </summary>
public sealed class SecretRedactionBodyTests
{
    private const string SecretValue = "body-secret-value-7q9";

    private static readonly System.Reflection.Assembly[] s_providerAssemblies =
    {
        typeof(HttpRestProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    private static readonly IReadOnlyList<string> s_executionRefs = new[]
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

    private static ScenarioAst BuildAst(string yaml) =>
        AstBuilder.Build(YamlDocumentParser.Parse(yaml), s_registry);

    // ── A. body secret → secretDerived provenance, value in no event line ─────────

    /// <summary>
    /// An http.rest step whose <c>body</c> contains <c>${secret:env/&lt;name&gt;}</c> must
    /// emit a <c>secretDerived:true</c> substitution carrying the reference label, and the
    /// resolved VALUE must appear in NONE of the buffered event lines (§17).  This locks the
    /// S07-B-02b fix that added <c>body</c> to the compile-time substitutable-text scan.
    /// </summary>
    [Fact]
    public void BodySecretReference_EmitsSecretDerivedSubstitution_NoValueInEvents()
    {
        var envName = "VOUCHFX_BODY_REDACTION_" + Guid.NewGuid().ToString("N");
        var referenceLabel = $"env/{envName}";

        Environment.SetEnvironmentVariable(envName, SecretValue);
        try
        {
            var yaml =
                $$"""
                environment:
                  services:
                    api:
                      image: traefik/whoami
                steps:
                  - id: post-with-body-secret
                    type: http.rest
                    target: api
                    method: POST
                    path: /api
                    body: "{\"token\":\"${secret:env/{{envName}}}\"}"
                    expect:
                      status: 200
                """;

            var ast = BuildAst(yaml);
            var node = Assert.Single(ast.Steps);

            var line = EmitStepCompletedLine(ast, node);
            var evt = EventStreamJson.FromLine<StepCompletedEvent>(line);

            Assert.NotNull(evt.Substitutions);
            var secretEntry = Assert.Single(evt.Substitutions!, s => s.SecretDerived);
            Assert.True(secretEntry.SecretDerived);
            Assert.Equal(referenceLabel, secretEntry.Placeholder);
            Assert.Null(secretEntry.OriginStepId);

            Assert.Contains("\"secretDerived\":true", line, StringComparison.Ordinal);
            Assert.Contains(referenceLabel, line, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretValue, line, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    /// <summary>
    /// Rendering the body-secret step's event buffer through <see cref="TerminalRenderer"/>
    /// must never surface the resolved secret value (§17).  The buffer is hand-built and
    /// carries only the reference-label provenance, so a passing render is the redaction proof.
    /// </summary>
    [Fact]
    public void BodySecretValue_NeverAppearsInTerminalOutput()
    {
        var envName = "VOUCHFX_BODY_TERM_" + Guid.NewGuid().ToString("N");

        Environment.SetEnvironmentVariable(envName, SecretValue);
        try
        {
            var yaml =
                $$"""
                environment:
                  services:
                    api:
                      image: traefik/whoami
                steps:
                  - id: post-with-body-secret
                    type: http.rest
                    target: api
                    method: POST
                    path: /api
                    body: "{\"token\":\"${secret:env/{{envName}}}\"}"
                    expect:
                      status: 200
                """;

            var ast = BuildAst(yaml);
            var node = Assert.Single(ast.Steps);

            var buffer = new List<string>
            {
                EventStreamJson.ToLine(new ScenarioStartedEvent
                {
                    RunId = "run-body-redaction",
                    Timestamp = DateTimeOffset.UtcNow,
                    ScenarioId = "body-secret-redaction",
                }),
                EventStreamJson.ToLine(new StepStartedEvent
                {
                    RunId = "run-body-redaction",
                    Timestamp = DateTimeOffset.UtcNow,
                    StepId = node.Id,
                    Kind = node.CanonicalType,
                    VerifyMode = "IMMEDIATE",
                }),
                EmitStepCompletedLine(ast, node),
                EventStreamJson.ToLine(new ScenarioCompletedEvent
                {
                    RunId = "run-body-redaction",
                    Timestamp = DateTimeOffset.UtcNow,
                    ScenarioId = "body-secret-redaction",
                    Verdict = Verdict.EnvironmentError,
                    Counts = new VerdictCounts { EnvError = 1 },
                }),
            };

            var sw = new StringWriter();
            TerminalRenderer.Render(buffer, sw);
            var rendered = sw.ToString();

            Assert.DoesNotContain(SecretValue, rendered, StringComparison.Ordinal);
            Assert.Contains("post-with-body-secret", rendered, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── B. compile+run: secret revealed into the request body, NOT written back to Vars ──

    /// <summary>
    /// A real http.rest POST whose <c>body</c> carries a secret is compiled and run against an
    /// in-process recording responder.  The responder proves the revealed secret reached the
    /// request body (body substitution works end-to-end), while the test proves the revealed
    /// VALUE appears in NO <c>Vars</c> entry after the run — the revealed value is a transient
    /// feeding the request sink and is never written back (§17).
    /// </summary>
    [Fact]
    public async Task BodySecret_RevealedIntoRequest_ButNeverWrittenBackToVars()
    {
        var envName = "VOUCHFX_BODY_NOWRITEBACK_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, SecretValue);

        var port = FindFreePort();
        var (baseUrl, requestBodies, responder) = StartRecordingResponder("OK", "text/plain", port);
        try
        {
            var provider = new HttpRestProvider();
            var model = new HttpRestModel(
                Target: "api",
                Method: "POST",
                Path: "/ingest",
                Headers: null,
                Body: $"{{\"token\":\"${{secret:env/{envName}}}\"}}",
                Expect: new HttpExpect(Status: 200));

            const string stepId = "post-secret-body";
            var fragment = provider.Emit(model, new StubCtx(stepId));

            var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_executionRefs);

            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("api")] = baseUrl,
            };
            var globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                accessor);

            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            // The step passed (responder returns 200) — body substitution + send happened.
            var safeId = CsxFragment.SanitiseId(stepId);
            var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(safeId)]);
            Assert.Equal(Verdict.Pass, outcome.Verdict);

            // (a) the secret WAS revealed into the request body (proves the reveal path runs).
            var receivedBody = Assert.Single(requestBodies);
            Assert.Contains(SecretValue, receivedBody, StringComparison.Ordinal);

            // (b) the revealed VALUE is absent from EVERY Vars entry (key AND value) — the
            //     transient never threads back into shared state (§17).
            foreach (var (key, value) in vars)
            {
                Assert.DoesNotContain(SecretValue, key, StringComparison.Ordinal);
                var asText = value?.ToString() ?? string.Empty;
                Assert.DoesNotContain(SecretValue, asText, StringComparison.Ordinal);
            }

            // (c) the step outcome observation also carries no value (defence in depth).
            Assert.DoesNotContain(SecretValue, outcome.Observation ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            responder.Dispose();
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── runner-mirroring provenance emission (no topology) ────────────────────────

    private static string EmitStepCompletedLine(ScenarioAst ast, StepNode node)
    {
        var captureOriginMap = ScenarioRunner.BuildCaptureOriginMap(ast.Steps);
        var substitutions = ScenarioRunner.DeriveSubstitutionProvenance(node, captureOriginMap);

        IReadOnlyList<CapturedVar>? captured = null;
        if (node.Capture.Count > 0)
        {
            captured = node.Capture
                .Select(kv => new CapturedVar(kv.Key, kv.Value.Expression, Matched: true))
                .ToList();
        }

        return EventStreamJson.ToLine(new StepCompletedEvent
        {
            RunId = "run-body-redaction",
            Timestamp = DateTimeOffset.UtcNow,
            StepId = node.Id,
            Verdict = Verdict.EnvironmentError,
            DurationMs = 0L,
            Captured = captured,
            Substitutions = substitutions,
        });
    }

    // ── stub compile context + recording responder ───────────────────────────────

    private sealed class StubCtx : ICompileContext
    {
        public StubCtx(string stepId) => StepId = stepId;

        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
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
    /// In-process responder that returns 200 OK and RECORDS each request's body, so the test
    /// can confirm the revealed secret reached the request sink.
    /// </summary>
    private static (string BaseUrl, System.Collections.Concurrent.ConcurrentBag<string> Bodies, IDisposable Responder)
        StartRecordingResponder(string responseBody, string contentType, int port)
    {
        var prefix = $"http://localhost:{port}/";
        var bodies = new System.Collections.Concurrent.ConcurrentBag<string>();
        var hl = new HttpListener();
        hl.Prefixes.Add(prefix);
        hl.Start();

        var cts = new CancellationTokenSource();
        var listener = hl;
        var responseBytes = Encoding.UTF8.GetBytes(responseBody);

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
                    using (var reader = new StreamReader(hctx.Request.InputStream, Encoding.UTF8))
                    {
                        bodies.Add(await reader.ReadToEndAsync().ConfigureAwait(false));
                    }

                    hctx.Response.StatusCode = 200;
                    hctx.Response.ContentType = contentType;
                    hctx.Response.ContentLength64 = responseBytes.Length;
                    hctx.Response.OutputStream.Write(responseBytes);
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

        return (prefix.TrimEnd('/'), bodies, responder);
    }

    private sealed class CallbackDisposable : IDisposable
    {
        private readonly Action _dispose;
        public CallbackDisposable(Action dispose) => _dispose = dispose;
        public void Dispose() => _dispose();
    }
}
