// Vouchfx.Engine.Orchestration.Tests — metrics-assert.prometheus Docker integration
// tests.
//
// Exercises the full topology path for the metrics-assert.prometheus provider against
// a REAL Prometheus text-exposition endpoint: quay.io/prometheus/node-exporter, started
// as an environment.services entry (the "SUT" from this provider's point of view — see
// MetricsAssertPrometheusModel's file header for why this family uses Target+Path
// instead of a raw Url).
//
// Image choice: quay.io/prometheus/node-exporter is a tiny (~20 MB), single-binary
// image that serves TWO endpoints out of the box with no configuration:
//   • "/"        — a small HTML landing page, 200 OK — satisfies EnvironmentMapper's
//                  generic image-service health check (WithHttpHealthCheck("/")).
//   • "/metrics" — the real Prometheus text exposition this provider scrapes.
// Pinned to v1.8.2 (a stable published tag) for reproducibility; bump deliberately if a
// future run needs a newer node_exporter feature.
//
// Metrics asserted (both are node_exporter built-ins, present on every instance,
// regardless of host OS internals — the process always self-reports them):
//   • node_exporter_build_info{...} 1   — a "build info" gauge every Go-based exporter
//     publishes (no label filter: on a freshly started instance exactly one series
//     exists, so this ALSO proves the "exactly one match required" happy path with an
//     author who did not bother to narrow by label).
//   • node_scrape_collector_success{collector="cpu"} 1 — proves LABEL-subset matching
//     against real live data (the sample carries additional labels only 'collector' is
//     asserted here).
//
// Test flow:
//   1. Start a SuiteTopology with a single "sut" service (node-exporter, no dependency).
//   2. Stage the discovered base URL under VarKeys.Service("sut").
//   3. Emit + compile + run a metrics-assert.prometheus step (IMMEDIATE) → verify Pass.
//   4. Emit + compile + run a step with a real, always-present labelled metric → Pass.
//   5. Emit + compile + run a step naming a bogus metric → Fail ("found":false).
//   6. Compile the SAME model through the engine-owned RETRY wrapper
//      (StepCompilePlan Retry:true) → verify Pass, proving the family's canonical
//      verifyMode: RETRY mode round-trips against a live container end to end.
//   7. Teardown — SuiteTopology.DisposeAsync tears down the container (§4.5).
//
// Run with: dotnet test --filter "requires=docker&FullyQualifiedName~MetricsAssertPrometheusDocker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Sdk;
using Vouchfx.Steps.MetricsAssert.Prometheus;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end execution tests for the <c>metrics-assert.prometheus</c>
/// Core provider.  Requires a running Docker daemon with the
/// <c>quay.io/prometheus/node-exporter</c> image available.
/// </summary>
/// <remarks>
/// This test class carries <c>[Trait("requires","docker")]</c> on every method that starts a
/// <see cref="SuiteTopology"/>, so those are excluded from the non-Docker CI filter
/// (<c>dotnet test --filter "requires!=docker"</c>); the one method that never touches Docker
/// (the absent-service-key fail-fast check) deliberately omits the trait so it still runs
/// there. The test assembly already carries <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c>
/// and <c>Aspire.AppHost.Sdk</c>, which embed the <c>dcpclipath</c> metadata required by
/// <see cref="SuiteTopology.StartAsync"/>.
/// </remarks>
public sealed class MetricsAssertPrometheusDockerTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>Startup timeout: node_exporter is a tiny static binary; it starts fast.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Logical name of the SUT service under test.</summary>
    private const string ServiceName = "sut";

    /// <summary>Pinned node_exporter image (see the file header for the port/health rationale).</summary>
    private const string NodeExporterImage = "quay.io/prometheus/node-exporter:v1.8.2";

    /// <summary>node_exporter's default listen port.</summary>
    private const int NodeExporterPort = 9100;

    public MetricsAssertPrometheusDockerTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Compile-time metadata references for the emitted CSX body.  JsonPath.Net,
    /// System.Text.Json, System.Text.RegularExpressions, System.Globalization, and
    /// System.Uri are not in the default TPA subset so they must be supplied explicitly.
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(System.Net.Http.HttpClient).Assembly.Location,
        typeof(Json.Path.JsonPath).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Uri).Assembly.Location,
    };

    /// <summary>Minimal <see cref="ICompileContext"/> for emit calls inside tests.</summary>
    private sealed class StubCompileContext : ICompileContext
    {
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public StubCompileContext(string stepId) => StepId = stepId;
        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    /// <summary>Builds the Aspire environment spec for the node_exporter SUT service.</summary>
    private static EnvironmentSpec BuildEnv() =>
        new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                [ServiceName] = new ServiceSpec(
                    Image: NodeExporterImage,
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: NodeExporterPort,
                    Env: null),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>
    /// Emits and runs a <c>metrics-assert.prometheus</c> fragment in IMMEDIATE mode.
    /// Returns the <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private static async Task<StepOutcome> RunStepAsync(
        MetricsAssertPrometheusModel model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var provider = new MetricsAssertPrometheusProvider();
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

    /// <summary>
    /// Emits and runs a <c>metrics-assert.prometheus</c> fragment wrapped in the
    /// engine-owned RETRY polling loop (§7), proving the family's canonical
    /// <c>verifyMode: RETRY</c> mode compiles and round-trips against a live container.
    /// </summary>
    private static async Task<StepOutcome> RunStepWithRetryAsync(
        MetricsAssertPrometheusModel model,
        string stepId,
        Dictionary<string, object?> vars,
        long timeoutMs = 10_000)
    {
        var provider = new MetricsAssertPrometheusProvider();
        var fragment = provider.Emit(model, new StubCompileContext(stepId));

        var plan = new StepCompilePlan(stepId, fragment, Retry: true, TimeoutMs: timeoutMs, PollIntervalMs: null);
        var assembled = CsxAssembler.Assemble(new[] { plan });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Vars must contain outcome key '{outcomeKey}'. Keys: [{string.Join(", ", vars.Keys)}]");
        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }

    // ── Test cases ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Scraping <c>node_exporter_build_info</c> (no label filter — exactly one series
    /// exists on a freshly started instance) with <c>expect.value: 1</c> yields
    /// <see cref="Verdict.Pass"/>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MetricsAssertPrometheus_BuildInfoMetric_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var baseUrl = suite.DiscoveredServices[ServiceName] as string;
        Assert.False(string.IsNullOrWhiteSpace(baseUrl),
            $"DiscoveredServices['{ServiceName}'] must be a non-empty base URL.");
        _output.WriteLine($"node_exporter base URL: {baseUrl}");

        var model = new MetricsAssertPrometheusModel(
            Target: ServiceName,
            Path: "/metrics",
            Metric: "node_exporter_build_info",
            Labels: null,
            Expect: new MetricsExpectation(Value: "1", Min: null, Max: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Service(ServiceName)] = baseUrl,
        };

        var outcome = await RunStepAsync(model, "build-info-pass", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    /// <summary>
    /// Scraping <c>node_scrape_collector_success{collector="cpu"}</c> proves label-subset
    /// matching against real live data: the sample carries additional node_exporter
    /// labels, only <c>collector</c> is asserted here, and the value must be exactly 1
    /// (the cpu collector always succeeds in a container).
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MetricsAssertPrometheus_LabelledCollectorSuccessMetric_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var baseUrl = suite.DiscoveredServices[ServiceName] as string;
        Assert.False(string.IsNullOrWhiteSpace(baseUrl));

        var model = new MetricsAssertPrometheusModel(
            Target: ServiceName,
            Path: "/metrics",
            Metric: "node_scrape_collector_success",
            Labels: new Dictionary<string, string> { ["collector"] = "cpu" },
            Expect: new MetricsExpectation(Value: "1", Min: null, Max: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Service(ServiceName)] = baseUrl,
        };

        var outcome = await RunStepAsync(model, "collector-success-pass", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    /// <summary>
    /// A metric name that does not exist on the real endpoint yields
    /// <see cref="Verdict.Fail"/> with a <c>"found":false</c> observation.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MetricsAssertPrometheus_UnknownMetric_ReturnsFail()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var baseUrl = suite.DiscoveredServices[ServiceName] as string;
        Assert.False(string.IsNullOrWhiteSpace(baseUrl));

        var model = new MetricsAssertPrometheusModel(
            Target: ServiceName,
            Path: "/metrics",
            Metric: "this_metric_does_not_exist_anywhere",
            Labels: null,
            Expect: new MetricsExpectation(Value: "1", Min: null, Max: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Service(ServiceName)] = baseUrl,
        };

        var outcome = await RunStepAsync(model, "unknown-metric-fail", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("found", outcome.Observation!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The SAME real scrape, compiled through the engine-owned RETRY wrapper
    /// (<c>verifyMode: RETRY</c>'s compiled form), still yields
    /// <see cref="Verdict.Pass"/> — proving this family's canonical asynchronous mode
    /// (post-stimulus convergence polling) round-trips against a live container, not
    /// just the IMMEDIATE path exercised above.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MetricsAssertPrometheus_RetryWrapped_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var baseUrl = suite.DiscoveredServices[ServiceName] as string;
        Assert.False(string.IsNullOrWhiteSpace(baseUrl));

        var model = new MetricsAssertPrometheusModel(
            Target: ServiceName,
            Path: "/metrics",
            Metric: "node_exporter_build_info",
            Labels: null,
            Expect: new MetricsExpectation(Value: "1", Min: null, Max: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Service(ServiceName)] = baseUrl,
        };

        var outcome = await RunStepWithRetryAsync(model, "build-info-retry", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);

        // The RETRY wrapper records at least one attempt in the per-step timeline.
        var attemptsKey = VarKeys.Attempts(CsxFragment.SanitiseId("build-info-retry"));
        Assert.True(vars.ContainsKey(attemptsKey), $"Expected attempts timeline at '{attemptsKey}'.");
    }

    /// <summary>
    /// When the <c>svc::</c> key is absent from <c>Vars</c>, the step outcome must be
    /// <see cref="Verdict.EnvironmentError"/> (§12.1).  No topology needed.
    /// </summary>
    // No [Trait("requires","docker")] here — unlike its siblings above, this test
    // never calls SuiteTopology.StartAsync (no container, no Docker daemon touch);
    // it only exercises the emitted fragment's fail-fast path when the `svc::` key
    // is absent from Vars, so it belongs in the default (non-Docker) CI filter.
    [Fact]
    public async Task MetricsAssertPrometheus_AbsentServiceKey_ReturnsEnvironmentError()
    {
        var model = new MetricsAssertPrometheusModel(
            Target: "missing-service",
            Path: "/metrics",
            Metric: "orders_total",
            Labels: null,
            Expect: new MetricsExpectation(Value: "1", Min: null, Max: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);

        var outcome = await RunStepAsync(model, "svc-env-error", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }
}
