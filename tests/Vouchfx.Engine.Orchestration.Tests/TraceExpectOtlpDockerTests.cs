// Vouchfx.Engine.Orchestration.Tests — trace-expect.otlp Docker integration tests.
//
// Exercises the full REAL-network path for the trace-expect.otlp provider: a real
// SuiteTopology (Aspire + Docker, mirroring MetricsAssertPrometheusDockerTests.cs exactly, one
// unrelated "app" service present to prove the receiver co-exists cleanly with a live
// orchestrated topology and its teardown), a REAL OtlpReceiver (Kestrel, bound 0.0.0.0:0), and
// a REAL TraceCaptureBuffer/TraceCaptureAccessor.
//
// FULL-FIDELITY VARIANT — documented decision: a tiny, pre-instrumented, off-the-shelf image
// that reliably exports OTLP/HTTP JSON spans to an ARBITRARY receiver URL supplied at container
// start does not exist the way traefik/whoami/node-exporter serve http.rest/metrics-assert's
// needs — OpenTelemetry auto-instrumentation demo images are large, bespoke to their own
// backend wiring, and not designed to be repointed via a single env var at an ephemeral
// per-test port without extra configuration plumbing. Mirroring the task's own guidance and the
// Sprint 7 webhook capstone's "HONEST disclosure" precedent (Sprint07CapstoneTests.cs), this
// suite instead has the TEST itself post a synthetic OTLP/HTTP JSON export payload directly to
// the receiver's staged URL via HttpClient — exercising the REAL receiver, REAL buffer, REAL
// accessor, and REAL emitted-CSX scan end-to-end without needing an instrumented SUT container.
// This is judged sufficient v1 coverage; the docker-gated WebhookListenerTests/OtlpReceiverTests
// already prove the Kestrel bind/parse mechanics over loopback in isolation.
//
// Run with: dotnet test --filter "requires=docker&FullyQualifiedName~TraceExpectOtlpDocker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Traces;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.HostResources;
using Vouchfx.Sdk;
using Vouchfx.Steps.TraceExpect.Otlp;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end execution tests for the <c>trace-expect.otlp</c> Core provider.
/// Requires a running Docker daemon with the <c>traefik/whoami</c> image available.
/// </summary>
public sealed class TraceExpectOtlpDockerTests
{
    private readonly ITestOutputHelper _output;

    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);
    private const string ServiceName = "app";
    private const string ReceiverName = "traces";

    public TraceExpectOtlpDockerTests(ITestOutputHelper output) => _output = output;

    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
    };

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

    private static string BuildOtlpJsonPayload(string traceId, string spanId, string name, string serviceName) =>
        "{\"resourceSpans\":[{" +
        "\"resource\":{\"attributes\":[{\"key\":\"service.name\",\"value\":{\"stringValue\":\"" + serviceName + "\"}}]}," +
        "\"scopeSpans\":[{\"spans\":[{" +
        "\"traceId\":\"" + traceId + "\"," +
        "\"spanId\":\"" + spanId + "\"," +
        "\"parentSpanId\":\"\"," +
        "\"name\":\"" + name + "\"," +
        "\"status\":{\"code\":\"STATUS_CODE_OK\"}" +
        "}]}]}]}";

    /// <summary>
    /// Emits, compiles, and runs a <c>trace-expect.otlp</c> fragment (IMMEDIATE), returning the
    /// <see cref="StepOutcome"/> the emitted helper writes.
    /// </summary>
    private static async Task<StepOutcome> RunStepAsync(
        TraceExpectOtlpModel model,
        string stepId,
        ITraceCaptureAccessor traces)
    {
        var provider = new TraceExpectOtlpProvider();
        var fragment = provider.Emit(model, new StubCompileContext(stepId));

        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(
            vars,
            new Dictionary<string, object>(StringComparer.Ordinal),
            NullSecretAccessor.Instance,
            Vouchfx.Engine.Abstractions.Webhooks.NullWebhookCaptureAccessor.Instance,
            traces);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Vars must contain outcome key '{outcomeKey}'. Keys: [{string.Join(", ", vars.Keys)}]");
        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }

    private static async Task<StepOutcome> RunStepWithRetryAsync(
        TraceExpectOtlpModel model,
        string stepId,
        ITraceCaptureAccessor traces,
        long timeoutMs = 10_000)
    {
        var provider = new TraceExpectOtlpProvider();
        var fragment = provider.Emit(model, new StubCompileContext(stepId));

        var plan = new StepCompilePlan(stepId, fragment, Retry: true, TimeoutMs: timeoutMs, PollIntervalMs: null);
        var assembled = CsxAssembler.Assemble(new[] { plan });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(
            vars,
            new Dictionary<string, object>(StringComparer.Ordinal),
            NullSecretAccessor.Instance,
            Vouchfx.Engine.Abstractions.Webhooks.NullWebhookCaptureAccessor.Instance,
            traces);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }

    // ── Test cases ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A synthetic OTLP/HTTP JSON export posted to a REAL, live <see cref="OtlpReceiver"/>
    /// (started alongside a real orchestrated topology) is captured by the REAL
    /// <see cref="TraceCaptureBuffer"/> and matched by the emitted helper via the REAL
    /// <see cref="TraceCaptureAccessor"/>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task TraceExpectOtlp_SyntheticExportMatchesFullCriteria_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var buffer = new TraceCaptureBuffer();
        await using var receiver = await OtlpReceiver.StartAsync(buffer);
        _output.WriteLine($"OTLP receiver URL: {receiver.Url}");

        using var client = new HttpClient();
        var payload = BuildOtlpJsonPayload(
            traceId: "4bf92f3577b34da6a3ce929d0e0e4736",
            spanId: "00f067aa0ba902b7",
            name: "POST /orders",
            serviceName: "orders-api");
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        var exportResponse = await client.PostAsync(receiver.Url + "/v1/traces", content);
        Assert.Equal(System.Net.HttpStatusCode.OK, exportResponse.StatusCode);

        var traces = new TraceCaptureAccessor(
            new Dictionary<string, TraceCaptureBuffer>(StringComparer.Ordinal) { [ReceiverName] = buffer });

        var model = new TraceExpectOtlpModel(
            Receiver: ReceiverName,
            Match: new TraceMatch(
                TraceId: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", // full traceparent
                Service: "orders-api",
                SpanName: "POST /orders",
                Attributes: null));

        var outcome = await RunStepAsync(model, "assert-order-span", traces);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    /// <summary>
    /// A criterion that no captured span satisfies yields <see cref="Verdict.Fail"/> — the
    /// helper never writes Inconclusive from the scan itself (§7/§12.1).
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task TraceExpectOtlp_NoMatchingSpan_ReturnsFail()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var buffer = new TraceCaptureBuffer();
        await using var receiver = await OtlpReceiver.StartAsync(buffer);

        using var client = new HttpClient();
        var payload = BuildOtlpJsonPayload(
            traceId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            spanId: "bbbbbbbbbbbbbbbb",
            name: "GET /health",
            serviceName: "unrelated-service");
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        await client.PostAsync(receiver.Url + "/v1/traces", content);

        var traces = new TraceCaptureAccessor(
            new Dictionary<string, TraceCaptureBuffer>(StringComparer.Ordinal) { [ReceiverName] = buffer });

        var model = new TraceExpectOtlpModel(
            Receiver: ReceiverName,
            Match: new TraceMatch(
                TraceId: "4bf92f3577b34da6a3ce929d0e0e4736",
                Service: null,
                SpanName: null,
                Attributes: null));

        var outcome = await RunStepAsync(model, "assert-no-match", traces);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("\"matched\":false", outcome.Observation!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The SAME synthetic export, compiled through the engine-owned RETRY wrapper (§7), still
    /// yields <see cref="Verdict.Pass"/> — proving this family's canonical <c>verifyMode:
    /// RETRY</c> mode round-trips against a real, live receiver end to end.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task TraceExpectOtlp_RetryWrapped_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var buffer = new TraceCaptureBuffer();
        await using var receiver = await OtlpReceiver.StartAsync(buffer);

        using var client = new HttpClient();
        var payload = BuildOtlpJsonPayload(
            traceId: "cafebabecafebabecafebabecafebabe",
            spanId: "0011223344556677",
            name: "POST /checkout",
            serviceName: "checkout-api");
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        await client.PostAsync(receiver.Url + "/v1/traces", content);

        var traces = new TraceCaptureAccessor(
            new Dictionary<string, TraceCaptureBuffer>(StringComparer.Ordinal) { [ReceiverName] = buffer });

        var model = new TraceExpectOtlpModel(
            Receiver: ReceiverName,
            Match: new TraceMatch(
                TraceId: "cafebabecafebabecafebabecafebabe",
                Service: "checkout-api",
                SpanName: "POST /checkout",
                Attributes: null));

        var outcome = await RunStepWithRetryAsync(model, "assert-retry", traces);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }
}
