// Platform.Engine.Orchestration.Tests — http.soap Docker integration tests.
//
// Exercises the full topology + real-network path for the http.soap provider against
// traefik/whoami (the same image + health-check convention MetricsAssertPrometheusDockerTests
// and Sprint 4/M2's http.rest docker coverage already use — see EnvironmentMapper's generic
// image-service branch, WithHttpHealthCheck("/")).
//
// DOCUMENTED SCOPE DECISION (cheapest honest approach, per the task brief): traefik/whoami
// answers ANY method/path with a small plain-text request summary — it does NOT echo the
// posted body verbatim (so it cannot serve an XPath-over-echoed-envelope assertion), nor does
// it return a SOAP envelope or a SOAP Fault. This docker suite therefore proves the LIVE
// network path genuinely works (a real POST, with a real SOAP envelope body and a real
// SOAPAction header, against a real container, over a real Aspire-orchestrated topology) via
// STATUS-based assertions only. The XML parsing / fault-detection / XPath-assertion /
// XPath-capture logic is exhaustively and deterministically covered by
// HttpSoapEmitTests.cs's local-HttpListener harness, which has full control over the response
// envelope shape (success, Fault, non-XML) — exactly mirroring how MetricsAssertPrometheus's
// docker suite and local-HttpListener suite divide responsibilities.
//
// Run with: dotnet test --filter "requires=docker&FullyQualifiedName~HttpSoapDocker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
using Platform.Engine.Abstractions;
using Platform.Engine.Authoring.Model;
using Platform.Engine.Compilation;
using Platform.Engine.Orchestration;
using Platform.Sdk;
using Platform.Steps.Http.Soap;
using Xunit;
using Xunit.Abstractions;

namespace Platform.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end execution tests for the <c>http.soap</c> Core provider. Requires a
/// running Docker daemon with the <c>traefik/whoami</c> image available.
/// </summary>
public sealed class HttpSoapDockerTests
{
    private readonly ITestOutputHelper _output;

    private const string AppHostAssemblyName = "Platform.Engine.Orchestration.Tests";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);
    private const string ServiceName = "sut";

    private const string SoapEnvelope = """
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body>
            <GetUser xmlns="http://example.com/users">
              <UserId>42</UserId>
            </GetUser>
          </soap:Body>
        </soap:Envelope>
        """;

    public HttpSoapDockerTests(ITestOutputHelper output) => _output = output;

    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(System.Net.Http.HttpClient).Assembly.Location,
        typeof(System.Xml.XmlDocument).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Uri).Assembly.Location,
    };

    private sealed class StubCompileContext : ICompileContext
    {
        public StubCompileContext(string stepId) => StepId = stepId;
        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    private static EnvironmentSpec BuildEnv() =>
        new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                [ServiceName] = new ServiceSpec(
                    Image: "traefik/whoami:latest",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: 80,
                    Env: null),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    private static async Task<StepOutcome> RunStepAsync(HttpSoapModel model, string stepId, Dictionary<string, object?> vars)
    {
        var provider = new HttpSoapProvider();
        var fragment = provider.Emit(model, new StubCompileContext(stepId));

        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Vars must contain outcome key '{outcomeKey}'. Keys: [{string.Join(", ", vars.Keys)}]");
        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }

    private static async Task<StepOutcome> RunStepWithRetryAsync(
        HttpSoapModel model, string stepId, Dictionary<string, object?> vars, long timeoutMs = 10_000)
    {
        var provider = new HttpSoapProvider();
        var fragment = provider.Emit(model, new StubCompileContext(stepId));

        var plan = new StepCompilePlan(stepId, fragment, Retry: true, TimeoutMs: timeoutMs, PollIntervalMs: null);
        var assembled = CsxAssembler.Assemble(new[] { plan });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }

    // ── Test cases ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A real SOAP POST (envelope + SOAPAction header) against a real, live container, asserting
    /// the default expectation (status 200, no fault) — proves the transport/HTTP-client/
    /// content-type wiring works end-to-end.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task HttpSoap_LiveSoapPost_DefaultExpectation_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var baseUrl = suite.DiscoveredServices[ServiceName] as string;
        Assert.False(string.IsNullOrWhiteSpace(baseUrl));
        _output.WriteLine($"whoami base URL: {baseUrl}");

        var model = new HttpSoapModel(
            Target: ServiceName,
            Path: "/",
            Action: "http://example.com/GetUser",
            Envelope: SoapEnvelope,
            Expect: null); // default: status 200, no fault expected

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Service(ServiceName)] = baseUrl,
        };

        var outcome = await RunStepAsync(model, "live-soap-default", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.Contains("\"status\":200", outcome.Observation!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unexpected status against the same live container yields <see cref="Verdict.Fail"/>
    /// — proving live status-mismatch detection, not just the local-HttpListener path.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task HttpSoap_LiveSoapPost_UnexpectedStatus_ReturnsFail()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var baseUrl = suite.DiscoveredServices[ServiceName] as string;
        Assert.False(string.IsNullOrWhiteSpace(baseUrl));

        var model = new HttpSoapModel(
            Target: ServiceName,
            Path: "/",
            Action: null,
            Envelope: SoapEnvelope,
            Expect: new SoapExpectation(Status: 404, Fault: null, Xpath: null)); // whoami answers 200

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Service(ServiceName)] = baseUrl,
        };

        var outcome = await RunStepAsync(model, "live-soap-status-mismatch", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("\"status\":200", outcome.Observation!, StringComparison.Ordinal);
        Assert.Contains("\"expected\":404", outcome.Observation!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The SAME live SOAP POST, compiled through the engine-owned RETRY wrapper (§7), still
    /// yields <see cref="Verdict.Pass"/> — http.soap steps may legitimately use RETRY exactly
    /// like http.rest.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task HttpSoap_RetryWrapped_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var baseUrl = suite.DiscoveredServices[ServiceName] as string;
        Assert.False(string.IsNullOrWhiteSpace(baseUrl));

        var model = new HttpSoapModel(
            Target: ServiceName,
            Path: "/",
            Action: null,
            Envelope: SoapEnvelope,
            Expect: null);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Service(ServiceName)] = baseUrl,
        };

        var outcome = await RunStepWithRetryAsync(model, "live-soap-retry", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);

        var attemptsKey = VarKeys.Attempts(CsxFragment.SanitiseId("live-soap-retry"));
        Assert.True(vars.ContainsKey(attemptsKey), $"Expected attempts timeline at '{attemptsKey}'.");
    }

    /// <summary>
    /// When the <c>svc::</c> key is absent from <c>Vars</c>, the step outcome must be
    /// <see cref="Verdict.EnvironmentError"/> (§12.1). No topology needed.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task HttpSoap_AbsentServiceKey_ReturnsEnvironmentError()
    {
        var model = new HttpSoapModel(
            Target: "missing-service",
            Path: "/",
            Action: null,
            Envelope: SoapEnvelope,
            Expect: null);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);

        var outcome = await RunStepAsync(model, "svc-env-error", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }
}
