// Vouchfx.Engine.Orchestration.Tests — http.rest response-body assertions (#558), Docker lane.
//
// Exercises expect.json and expect.bodyContains over a real Aspire-orchestrated topology against
// traefik/whoami — the image the http.soap and metrics-assert Docker suites already use, and
// the service the MCP acceptance drill needed body assertions for. whoami answers `/api` with a
// JSON summary of the request and `/` with the same summary as plain text beginning
// "Hostname: <hostname>", so one container serves both assertion families.
//
// SCOPE: these rows assert only facts about whoami's responses that do not depend on the
// request echoing a body (whether whoami echoes a POST body is contested — HttpSoapDockerTests'
// header says it does not — so no row relies on it). Every verdict and observation shape is
// covered deterministically, without Docker, by HttpRestBodyAssertionTests in
// Vouchfx.Engine.Compilation.Tests; this suite proves the live network path.
//
// Run with: dotnet test --filter "requires=docker&FullyQualifiedName~HttpRestBodyAssertionDocker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end tests for the <c>http.rest</c> response-body assertions. Requires a
/// running Docker daemon with the <c>traefik/whoami</c> image available.
/// </summary>
public sealed class HttpRestBodyAssertionDockerTests
{
    private readonly ITestOutputHelper _output;

    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);
    private const string ServiceName = "whoami";

    public HttpRestBodyAssertionDockerTests(ITestOutputHelper output) => _output = output;

    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
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

    private sealed class StubCompileContext : ICompileContext
    {
        public StubCompileContext(string stepId, IReadOnlyDictionary<string, CaptureExpr>? captures = null)
        {
            StepId = stepId;
            CaptureExprs = captures ?? new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
            Captures = CaptureExprs.ToDictionary(kv => kv.Key, kv => kv.Value.Expression, StringComparer.Ordinal);
        }

        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public string StepId { get; }

        public string SuiteNamespace => "Generated";

        public IReadOnlyDictionary<string, string> Captures { get; }

        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; }
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

    private static async Task<StepOutcome> RunStepAsync(
        HttpRestModel model,
        string stepId,
        Dictionary<string, object?> vars,
        IReadOnlyDictionary<string, CaptureExpr>? captures = null)
    {
        var fragment = new HttpRestProvider().Emit(model, new StubCompileContext(stepId, captures));

        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, new ScriptGlobalVariables(vars));

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        Assert.True(vars.ContainsKey(outcomeKey), $"Vars must contain outcome key '{outcomeKey}'.");
        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }

    private static HttpRestModel Get(string path, HttpExpect expect) =>
        new(Target: ServiceName, Method: "GET", Path: path, Headers: null, Body: null, Expect: expect);

    // ── Test cases ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The drill's own flow, live: JSON facts about whoami's <c>/api</c> response hold, the
    /// hostname is captured once they do, and the captured value then threads into a
    /// <c>bodyContains</c> over the plain-text <c>/</c> page.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task HttpRest_LiveWhoami_JsonFactsHold_AndACaptureFeedsBodyContains()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var baseUrl = suite.DiscoveredServices[ServiceName] as string;
        Assert.False(string.IsNullOrWhiteSpace(baseUrl));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Service(ServiceName)] = baseUrl,
        };

        var api = await RunStepAsync(
            Get("/api", new HttpExpect(200, new[]
            {
                new HttpJsonAssertion("$.hostname", null, true),
                new HttpJsonAssertion("$.method", "GET", null),
                new HttpJsonAssertion("$.url", "/api", null),
            })),
            "whoami-api",
            vars,
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
            {
                ["hostname"] = new CaptureExpr(CaptureFormat.JsonPath, "$.hostname"),
            });

        _output.WriteLine($"/api verdict: {api.Verdict}, observation: {api.Observation}");
        Assert.Equal(Verdict.Pass, api.Verdict);
        Assert.False(string.IsNullOrEmpty(vars["hostname"] as string));

        var text = await RunStepAsync(
            Get("/", new HttpExpect(200, null, "Hostname: {hostname}")),
            "whoami-text",
            vars);

        _output.WriteLine($"/ verdict: {text.Verdict}, observation: {text.Observation}");
        Assert.Equal(Verdict.Pass, text.Verdict);
    }

    /// <summary>
    /// A fact that does not hold against the live response is Fail with the body observation:
    /// the author's path and expected text, and the KIND of the node found.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task HttpRest_LiveWhoami_MismatchIsFail_WithTheBodyObservation()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Service(ServiceName)] = suite.DiscoveredServices[ServiceName] as string,
        };

        var outcome = await RunStepAsync(
            Get("/api", new HttpExpect(200, new[] { new HttpJsonAssertion("$.method", "POST", null) })),
            "whoami-mismatch",
            vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        using var doc = JsonDocument.Parse(outcome.Observation!);
        var first = doc.RootElement.GetProperty("body").GetProperty("first");
        Assert.Equal("mismatch", first.GetProperty("reason").GetString());
        Assert.Equal("POST", first.GetProperty("expected").GetString());
        Assert.Equal("string", first.GetProperty("actualKind").GetString());
    }

    /// <summary>
    /// A JSON assertion over whoami's plain-text <c>/</c> page fails as <c>notJson</c> — the
    /// service answered and the status held, so it is Fail, never an environment error.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task HttpRest_LiveWhoami_JsonAssertionOnATextPage_IsNotJson()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Service(ServiceName)] = suite.DiscoveredServices[ServiceName] as string,
        };

        var outcome = await RunStepAsync(
            Get("/", new HttpExpect(200, new[] { new HttpJsonAssertion("$.hostname", null, true) })),
            "whoami-not-json",
            vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("\"reason\":\"notJson\"", outcome.Observation!, StringComparison.Ordinal);
    }
}
