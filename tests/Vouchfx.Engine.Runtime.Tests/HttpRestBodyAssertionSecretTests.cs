// #558 — http.rest response-body assertions and secrets (§17), non-docker.
//
// The expected values of `expect.json` and `expect.bodyContains` are templates the provider
// resolves at step-execution time, exactly like the path, headers and body. That makes them
// part of ScenarioRunner.CollectSubstitutableTexts, whose remarks call lock-step with the
// provider load-bearing, and this file proves each consumer of that scan sees them:
//   • provenance      — a secret reference lights up secretDerived by LABEL, a placeholder
//                       names the step that captured it, and a JSONPath KEY is never scanned;
//   • pre-compile     — an unknown secret source is refused before anything is compiled;
//   • envelope        — the reference is hashed into the reproducibility envelope;
//   • compile + run   — the revealed value is a comparison operand only: the step compares
//                       against it, and neither the observation nor any Vars entry ever holds it.
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Reproducibility;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// #558: secret handling for the <c>http.rest</c> response-body assertions' expected values.
/// </summary>
public sealed class HttpRestBodyAssertionSecretTests
{
    private const string SecretValue = "body-assert-secret-value-4k8";

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

    /// <summary>
    /// A suite whose second step asserts on the body with a secret reference, a placeholder the
    /// FIRST step captures, a placeholder nothing captures, and a JSONPath KEY that merely looks
    /// like it holds a placeholder.
    /// </summary>
    private static string Suite(string source, string name) =>
        $$"""
        environment:
          services:
            api:
              image: traefik/whoami
        steps:
          - id: create-order
            type: http.rest
            target: api
            method: POST
            path: /orders
            capture:
              orderId: "$.id"
          - id: check-order
            type: http.rest
            target: api
            method: GET
            path: /orders/1
            expect:
              status: 200
              json:
                "$.id": "{orderId}"
                "$.audit.caller": "${secret:{{source}}/{{name}}}"
                "$['{notAPlaceholder}']": { exists: false }
              bodyContains: "Hostname: {hostname}"
        """;

    // ── provenance ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The expected values feed step provenance: the secret reference lights up
    /// <c>secretDerived</c> carrying its LABEL only, <c>{orderId}</c> names the step that
    /// captured it, <c>{hostname}</c> names none — and the JSONPath key
    /// <c>$['{notAPlaceholder}']</c> contributes nothing, because keys are used verbatim and
    /// never resolved.
    /// </summary>
    [Fact]
    public void ExpectedValues_FeedProvenance_KeysDoNot()
    {
        var ast = BuildAst(Suite("env", "API_CLIENT_ID"));
        var node = ast.Steps[1];

        var subs = ScenarioRunner.DeriveSubstitutionProvenance(
            node, ScenarioRunner.BuildCaptureOriginMap(ast.Steps));

        Assert.NotNull(subs);
        var secret = Assert.Single(subs!, s => s.SecretDerived);
        Assert.Equal("env/API_CLIENT_ID", secret.Placeholder);
        Assert.Null(secret.OriginStepId);

        var orderId = Assert.Single(subs!, s => s.Placeholder == "orderId");
        Assert.False(orderId.SecretDerived);
        Assert.Equal("create-order", orderId.OriginStepId);

        var hostname = Assert.Single(subs!, s => s.Placeholder == "hostname");
        Assert.Null(hostname.OriginStepId);

        Assert.DoesNotContain(subs!, s => s.Placeholder == "notAPlaceholder");
    }

    // ── pre-compile secret validation ─────────────────────────────────────────────

    /// <summary>
    /// A secret reference naming an unknown source in an expected value is refused by the
    /// engine's pre-compile pass, naming the step — before any container starts.
    /// </summary>
    [Fact]
    public void UnknownSecretSourceInAnExpectedValue_IsRefusedBeforeCompile()
    {
        var ast = BuildAst(Suite("nosuchsource", "API_CLIENT_ID"));

        Assert.True(ScenarioRunner.TryValidateSecretReferences(ast, out var error, out _));
        Assert.NotNull(error);
        Assert.Contains("step 'check-order'", error!, StringComparison.Ordinal);
        Assert.Contains("names an unknown source 'nosuchsource'", error, StringComparison.Ordinal);
    }

    /// <summary>The same pass accepts the suite once the source is a known one.</summary>
    [Fact]
    public void KnownSecretSourceInAnExpectedValue_PassesPreCompileValidation()
    {
        var ast = BuildAst(Suite("env", "API_CLIENT_ID"));

        Assert.False(ScenarioRunner.TryValidateSecretReferences(ast, out var error, out _));
        Assert.Null(error);
    }

    // ── reproducibility envelope ──────────────────────────────────────────────────

    /// <summary>
    /// The reference in an expected value is hashed into the reproducibility envelope — the
    /// REFERENCE text, never a value (§17).
    /// </summary>
    [Fact]
    public void SecretInAnExpectedValue_IsHashedIntoTheEnvelope()
    {
        var ast = BuildAst(Suite("env", "API_CLIENT_ID"));

        var envelope = ScenarioRunner.BuildReproducibilityEnvelope(ast, seedBaseDirectory: null);

        var digest = Assert.Single(envelope.SecretReferences);
        Assert.Equal("env", digest.Source);
        Assert.Equal(ReproducibilityEnvelope.HashReference("${secret:env/API_CLIENT_ID}"), digest.ReferenceHash);
    }

    // ── compile + run: compared at run time, never observed ───────────────────────

    /// <summary>
    /// Compiled and run against an in-process responder that ECHOES the secret value in its
    /// body: the entry comparing against the reference passes (so the value was revealed and
    /// compared), the two that do not hold fail — and the observation carries the REFERENCE
    /// text, while neither it nor any Vars entry holds the value (§17). The response here
    /// contains the value too, which is the case the kind-only observation exists for.
    /// </summary>
    [Fact]
    public async Task SecretInAnExpectedValue_IsComparedAtRunTime_AndNeverObserved()
    {
        var envName = "VOUCHFX_BODY_ASSERT_" + Guid.NewGuid().ToString("N");
        var reference = "${secret:env/" + envName + "}";
        Environment.SetEnvironmentVariable(envName, SecretValue);

        using var responder = EchoResponder.Start(
            "{\"token\":\"" + SecretValue + "\",\"other\":\"" + SecretValue + "\"}");
        try
        {
            var model = new HttpRestModel(
                Target: "api",
                Method: "GET",
                Path: "/tokens/1",
                Headers: null,
                Body: null,
                Expect: new HttpExpect(
                    Status: 200,
                    Json: new[]
                    {
                        new HttpJsonAssertion("$.token", reference, null),
                        new HttpJsonAssertion("$.other", reference + "-suffix", null),
                    },
                    BodyContains: reference + "-absent"));

            const string stepId = "check-token";
            var fragment = new HttpRestProvider().Emit(model, new StubCtx(stepId));
            var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource, additionalReferencePaths: s_executionRefs);

            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("api")] = responder.BaseUrl,
            };
            var globals = new ScriptGlobalVariables(
                vars, new Dictionary<string, object>(StringComparer.Ordinal), accessor);

            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(CsxFragment.SanitiseId(stepId))]);
            var observation = outcome.Observation ?? string.Empty;

            // Two of three failed: the first entry held, so the value WAS revealed and compared.
            Assert.Equal(Verdict.Fail, outcome.Verdict);
            Assert.True(
                observation.Contains("\"failed\":2,\"of\":3", StringComparison.Ordinal),
                "Expected two of the three body assertions to fail.");

            // The observation names the reference, never the value.
            Assert.True(
                observation.Contains(envName + "}-suffix", StringComparison.Ordinal),
                "The observation must carry the expected value as its reference text.");
            Assert.True(
                !observation.Contains(SecretValue, StringComparison.Ordinal),
                "A revealed secret value reached the step observation.");

            // No Vars entry, key or value, holds the value either.
            foreach (var (key, value) in vars)
            {
                Assert.True(
                    !key.Contains(SecretValue, StringComparison.Ordinal)
                    && !(value?.ToString() ?? string.Empty).Contains(SecretValue, StringComparison.Ordinal),
                    "A revealed secret value was written back to Vars.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── stubs ─────────────────────────────────────────────────────────────────────

    private sealed class StubCtx : ICompileContext
    {
        public StubCtx(string stepId) => StepId = stepId;

        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public string StepId { get; }

        public string SuiteNamespace => "Generated";

        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    /// <summary>
    /// A raw-socket responder answering every request 200 with one fixed JSON body.
    /// </summary>
    private sealed class EchoResponder : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly byte[] _body;

        private EchoResponder(string body)
        {
            _body = Encoding.UTF8.GetBytes(body);
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUrl = "http://127.0.0.1:"
                + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
            _ = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl { get; }

        public static EchoResponder Start(string body) => new(body);

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch (SocketException) { /* already stopped */ }
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();

                // A GET carries no body: read the head up to the blank line, then answer.
                var head = new List<byte>();
                var one = new byte[1];
                while (!(head.Count >= 4
                         && head[^4] == (byte)'\r' && head[^3] == (byte)'\n'
                         && head[^2] == (byte)'\r' && head[^1] == (byte)'\n'))
                {
                    if (await stream.ReadAsync(one, CancellationToken.None).ConfigureAwait(false) == 0)
                        return;
                    head.Add(one[0]);
                }

                var responseHead =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/json; charset=utf-8\r\n" +
                    "Content-Length: " + _body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                    "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(responseHead), CancellationToken.None).ConfigureAwait(false);
                await stream.WriteAsync(_body, CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // The client went away, or teardown raced this response.
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}
