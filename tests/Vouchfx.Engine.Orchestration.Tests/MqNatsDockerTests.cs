// Vouchfx.Engine.Orchestration.Tests — mq-publish.nats + mq-expect.nats Docker
// integration tests.
//
// Exercises the full topology path for the mq-publish.nats and mq-expect.nats
// providers with a live NATS JetStream container started via SuiteTopology (the
// AppHost fixture with Aspire.AppHost.Sdk and WithJetStream() — FIX B1).
// Tests carry [Trait("requires","docker")] and are excluded from the no-docker
// CI filter.
//
// Test flow:
//   1. Start a SuiteTopology with a single "nats" dependency ("bus"), WithJetStream().
//   2. Obtain the NATS URL from DiscoveredServices and stage it under VarKeys.Connection("bus").
//   3. Emit + compile + run an mq-publish.nats step → verify Pass.
//   4. Emit + compile + run an mq-expect.nats step (matching criteria) → verify Pass.
//   5. Re-run mq-expect.nats with a non-matching criterion → verify Fail.
//   6. Verify EnvironmentError when the conn key is absent (no topology needed).
//   7. Teardown — SuiteTopology.DisposeAsync tears down the container (§4.5).
//
// Ordered-consumer note (FIX M2):
//   mq-expect.nats uses CreateOrderedConsumerAsync which creates a truly ephemeral
//   consumer.  The test does NOT need to clean up durable consumers between runs;
//   the ordered consumer is automatically discarded when the NatsConnection is disposed.
//
// Run with: dotnet test --filter "requires=docker&FullyQualifiedName~MqNatsDocker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.Nats;
using Vouchfx.Steps.MqPublish.Nats;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end execution tests for the <c>mq-publish.nats</c> and
/// <c>mq-expect.nats</c> Core providers.
/// Requires a running Docker daemon with the <c>nats:2.14</c> image available
/// (the default image for Aspire.Hosting.Nats 13.4.2).
/// </summary>
/// <remarks>
/// This test class carries <c>[Trait("requires","docker")]</c> on every method so it is
/// excluded from the non-Docker CI filter (<c>dotnet test --filter "requires!=docker"</c>).
/// The test assembly already carries <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c>
/// and <c>Aspire.AppHost.Sdk</c>, which embed the <c>dcpclipath</c> metadata required by
/// <see cref="SuiteTopology.StartAsync"/>.
/// </remarks>
public sealed class MqNatsDockerTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>
    /// Startup timeout: NATS starts very quickly (~2 s) but we allow 120 s to be
    /// consistent with other Docker tests and safe on loaded CI runners.
    /// </summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Logical name of the NATS dependency under test.</summary>
    private const string DepName = "bus";

    public MqNatsDockerTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Compile-time metadata references for the emitted CSX body.  NATS.Client.Core,
    /// NATS.Client.JetStream, NATS.Client.Abstractions, JsonPath.Net, System.Text.Json,
    /// System.Text.RegularExpressions, and System.Globalization are not in the default
    /// TPA subset so they must be supplied explicitly.
    /// NATS.Client.Abstractions is a transitive dependency of NATS.Client.JetStream;
    /// it is resolved from the test assembly's output directory (where NuGet copies all
    /// transitive package assemblies during build).
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = BuildAdditionalRefs();

    private static string[] BuildAdditionalRefs()
    {
        var testDir = System.IO.Path.GetDirectoryName(typeof(MqNatsDockerTests).Assembly.Location)!;
        var abstractionsPath = System.IO.Path.Combine(testDir, "NATS.Client.Abstractions.dll");
        return new[]
        {
            typeof(NATS.Client.Core.NatsConnection).Assembly.Location,
            typeof(NATS.Client.JetStream.NatsJSContext).Assembly.Location,
            abstractionsPath,
            typeof(Json.Path.JsonPath).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
            typeof(System.Uri).Assembly.Location,
        };
    }

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

    /// <summary>Builds the Aspire environment spec for a single NATS dependency.</summary>
    private static EnvironmentSpec BuildEnv() =>
        new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                [DepName] = new DependencySpec(Type: "nats", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>
    /// Emits and runs an <c>mq-publish.nats</c> fragment.
    /// Returns the <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private static async Task<StepOutcome> RunPublishAsync(
        MqPublishNatsModel model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var provider = new MqPublishNatsProvider();
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
    /// Emits and runs an <c>mq-expect.nats</c> fragment.
    /// Returns the <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private static async Task<StepOutcome> RunExpectAsync(
        MqExpectNatsModel model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var provider = new MqExpectNatsProvider();
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

    // ── Test cases ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Publishing a message via the emitted CSX fragment yields
    /// <see cref="Verdict.Pass"/> when the NATS broker is reachable and JetStream
    /// is enabled (FIX B1: WithJetStream() on the EnvironmentMapper NATS entry).
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqPublishNats_ReachableBroker_JetStreamEnabled_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var natsUrl = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(natsUrl),
            $"DiscoveredServices['{DepName}'] must be a non-empty NATS URL.");
        _output.WriteLine($"NATS URL: {natsUrl}");

        var model = new MqPublishNatsModel(
            Target: DepName,
            Subject: "orders.created",
            Stream: null,
            Payload: "{\"event\":\"order-created\",\"orderId\":\"100\"}");

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = natsUrl,
        };

        var outcome = await RunPublishAsync(model, "pub-pass", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    /// <summary>
    /// After publishing a message, an <c>mq-expect.nats</c> step with a matching
    /// <c>payloadContains</c> criterion yields <see cref="Verdict.Pass"/>.
    /// Proves the ordered-consumer scan (FIX M2) retrieves the retained message.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqExpectNats_MessagePresent_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var natsUrl = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(natsUrl));
        _output.WriteLine($"NATS URL: {natsUrl}");

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = natsUrl,
        };

        // Publish a message first.
        const string testSubject = "ship.events";
        var publishModel = new MqPublishNatsModel(
            Target: DepName,
            Subject: testSubject,
            Stream: null,
            Payload: "{\"event\":\"order-shipped\",\"id\":\"42\"}");

        var pubOutcome = await RunPublishAsync(publishModel, "pub-ship", vars);
        _output.WriteLine($"Publish Verdict: {pubOutcome.Verdict}, Observation: {pubOutcome.Observation}");
        Assert.Equal(Verdict.Pass, pubOutcome.Verdict);

        // Now expect the message.
        var expectModel = new MqExpectNatsModel(
            Target: DepName,
            Subject: testSubject,
            Stream: null,
            Match: new NatsMatch(
                PayloadContains: "order-shipped",
                Json: null));

        var outcome = await RunExpectAsync(expectModel, "expect-pass", vars);

        _output.WriteLine($"Expect Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    /// <summary>
    /// When the published message does not contain the expected substring, the step
    /// yields <see cref="Verdict.Fail"/> (the RETRY runner re-invokes — this helper never
    /// writes Inconclusive, §7/§12.1).
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqExpectNats_NoMatchingMessage_ReturnsFail()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var natsUrl = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(natsUrl));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = natsUrl,
        };

        // Publish a message that does NOT contain the expected substring.
        const string testSubject = "payment.events";
        var publishModel = new MqPublishNatsModel(
            Target: DepName,
            Subject: testSubject,
            Stream: null,
            Payload: "{\"event\":\"payment-received\"}");

        var pubOutcome = await RunPublishAsync(publishModel, "pub-payment", vars);
        Assert.Equal(Verdict.Pass, pubOutcome.Verdict);

        // Expect a substring that is NOT in the published message.
        var expectModel = new MqExpectNatsModel(
            Target: DepName,
            Subject: testSubject,
            Stream: null,
            Match: new NatsMatch(
                PayloadContains: "order-shipped",   // Deliberately wrong
                Json: null));

        var outcome = await RunExpectAsync(expectModel, "expect-fail", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    /// <summary>
    /// When the <c>conn::</c> key is absent from <c>Vars</c>, the step outcome must be
    /// <see cref="Verdict.EnvironmentError"/> (§12.1: infrastructure failure, never
    /// conflated with a test failure).  No topology needed — this is a unit-level check.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqPublishNats_AbsentConnKey_ReturnsEnvironmentError()
    {
        var model = new MqPublishNatsModel(
            Target: "missing-dep",
            Subject: "any.subject",
            Stream: null,
            Payload: "hello");

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);

        var outcome = await RunPublishAsync(model, "pub-env-error", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }
}
