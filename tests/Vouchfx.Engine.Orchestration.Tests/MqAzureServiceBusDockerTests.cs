// Vouchfx.Engine.Orchestration.Tests — mq-publish.azureservicebus + mq-expect.azureservicebus
// Docker integration tests.
//
// Exercises the full topology path for the mq-publish.azureservicebus and
// mq-expect.azureservicebus providers with a live Azure Service Bus Emulator started via
// SuiteTopology (EnvironmentMapper's "azureservicebus" entry).  Tests carry
// [Trait("requires","docker")] and are excluded from the non-Docker CI filter.
//
// Topology (two containers via EnvironmentMapper):
//   <name>-sqledge  — mcr.microsoft.com/mssql/server:2022-latest (SQL persistence sidecar)
//   <name>          — mcr.microsoft.com/azure-messaging/servicebus-emulator:latest
//     AMQP port 5672  → DiscoveredServices[DepName] = connection string
//     HTTP port 5300  → /health (emulator health gate)
//
// Test flow:
//   1. Start SuiteTopology with a single "azureservicebus" dependency ("asb") with
//      a "orders" queue declared in Extra.
//   2. Obtain connection string from DiscoveredServices["asb"].
//   3. Emit + compile + run an mq-publish.azureservicebus step → verify Pass.
//   4. Emit + compile + run an mq-expect.azureservicebus step (matching payloadContains) → verify Pass.
//   5. Re-run mq-expect with a non-matching criterion → verify Fail.
//   6. Verify EnvironmentError when the conn key is absent (no topology needed).
//   7. Teardown — SuiteTopology.DisposeAsync tears down both containers (§4.5).
//
// Non-destructive peek note:
//   mq-expect.azureservicebus uses PeekMessagesAsync (non-destructive).  The published
//   message is NOT consumed during the expect step, so no seed/resend is needed between
//   the pass and fail expect tests when they use distinct queues.
//
// ASB emulator startup note:
//   SQL Server 2022 + the ASB emulator together can take 90–180 seconds to reach a
//   healthy state on a loaded CI runner.  StartupTimeout is 300 s to stay safe.
//
// Run with: dotnet test --filter "requires=docker&FullyQualifiedName~MqAzureServiceBusDocker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.AzureServiceBus;
using Vouchfx.Steps.MqPublish.AzureServiceBus;
using Xunit;
using Xunit.Abstractions;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end execution tests for the <c>mq-publish.azureservicebus</c> and
/// <c>mq-expect.azureservicebus</c> Core providers.
/// Requires a running Docker daemon with the Azure Service Bus Emulator images available.
/// </summary>
/// <remarks>
/// This test class carries <c>[Trait("requires","docker")]</c> on every method so it is
/// excluded from the non-Docker CI filter (<c>dotnet test --filter "requires!=docker"</c>).
/// The test assembly already carries <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c>
/// and <c>Aspire.AppHost.Sdk</c>, which embed the <c>dcpclipath</c> metadata required by
/// <see cref="SuiteTopology.StartAsync"/>.
/// </remarks>
public sealed class MqAzureServiceBusDockerTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>
    /// Startup timeout: the SQL Server 2022 sidecar + ASB emulator together can take
    /// 90–180 seconds on a loaded runner.  300 s gives ample headroom.
    /// </summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(300);

    /// <summary>Logical name of the Azure Service Bus dependency under test.</summary>
    private const string DepName = "asb";

    /// <summary>Name of the queue pre-declared in the emulator Config.json.</summary>
    private const string TestQueue = "orders";

    public MqAzureServiceBusDockerTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Compile-time metadata references for the emitted CSX body.
    /// Azure.Messaging.ServiceBus, Azure.Core, System.Text.Json,
    /// System.Text.RegularExpressions, System.Globalization, and System.Uri
    /// are not in the default TPA subset so they must be supplied explicitly.
    /// Azure.Core is a transitive dependency of Azure.Messaging.ServiceBus and is
    /// resolved from the test assembly's output directory (where NuGet copies all
    /// transitive package assemblies during build).
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = BuildAdditionalRefs();

    private static string[] BuildAdditionalRefs()
    {
        var testDir = System.IO.Path.GetDirectoryName(
            typeof(MqAzureServiceBusDockerTests).Assembly.Location)!;
        var azureCorePath = System.IO.Path.Combine(testDir, "Azure.Core.dll");
        return new[]
        {
            typeof(Azure.Messaging.ServiceBus.ServiceBusClient).Assembly.Location,
            typeof(Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient).Assembly.Location,
            azureCorePath,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
            typeof(System.Uri).Assembly.Location,
        };
    }

    /// <summary>Minimal <see cref="ICompileContext"/> for emit calls inside tests.</summary>
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

    /// <summary>
    /// Builds the Aspire environment spec for a single Azure Service Bus dependency.
    /// The <c>Extra</c> node declares the "orders" queue so EnvironmentMapper generates
    /// it in the Config.json — avoiding the first-message auto-create latency.
    /// </summary>
    private static EnvironmentSpec BuildEnv() =>
        new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                [DepName] = new DependencySpec(
                    Type: "azureservicebus",
                    Version: null,
                    Extra: new YamlMappingNode
                    {
                        {
                            "queues",
                            new YamlSequenceNode(new YamlScalarNode(TestQueue))
                        },
                    }),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>
    /// Emits and runs an <c>mq-publish.azureservicebus</c> fragment.
    /// Returns the <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private static async Task<StepOutcome> RunPublishAsync(
        MqPublishAzureServiceBusModel model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var provider = new MqPublishAzureServiceBusProvider();
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
    /// Emits and runs an <c>mq-expect.azureservicebus</c> fragment.
    /// Returns the <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private static async Task<StepOutcome> RunExpectAsync(
        MqExpectAzureServiceBusModel model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var provider = new MqExpectAzureServiceBusProvider();
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
    /// <see cref="Verdict.Pass"/> when the ASB emulator is reachable.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqPublishAzureServiceBus_ReachableBroker_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr),
            $"DiscoveredServices['{DepName}'] must be a non-empty ASB connection string.");
        _output.WriteLine($"ASB connection string: {connStr}");

        var model = new MqPublishAzureServiceBusModel(
            Target: DepName,
            Queue: TestQueue,
            Topic: null,
            Payload: "{\"event\":\"order-created\",\"orderId\":\"100\"}",
            Properties: null);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunPublishAsync(model, "pub-pass", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    /// <summary>
    /// After publishing a message, an <c>mq-expect.azureservicebus</c> step with a
    /// matching <c>expectPayloadContains</c> criterion yields <see cref="Verdict.Pass"/>.
    /// Uses a distinct queue to avoid interference from the publish-only test.
    /// PeekMessagesAsync is non-destructive so the message is still present for the
    /// subsequent non-matching test on the same queue without re-publishing.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqExpectAzureServiceBus_MessagePresent_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));
        _output.WriteLine($"ASB connection string: {connStr}");

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        // Publish a message first.
        var publishModel = new MqPublishAzureServiceBusModel(
            Target: DepName,
            Queue: TestQueue,
            Topic: null,
            Payload: "{\"event\":\"order-shipped\",\"id\":\"42\"}",
            Properties: null);

        var pubOutcome = await RunPublishAsync(publishModel, "pub-ship", vars);
        _output.WriteLine($"Publish Verdict: {pubOutcome.Verdict}, Observation: {pubOutcome.Observation}");
        Assert.Equal(Verdict.Pass, pubOutcome.Verdict);

        // Now expect the message via non-destructive peek.
        var expectModel = new MqExpectAzureServiceBusModel(
            Target: DepName,
            Queue: TestQueue,
            Topic: null,
            Subscription: null,
            ExpectPayloadContains: "order-shipped",
            ExpectProperties: null);

        var outcome = await RunExpectAsync(expectModel, "expect-pass", vars);

        _output.WriteLine($"Expect Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    /// <summary>
    /// When the published message does not contain the expected substring, the step
    /// yields <see cref="Verdict.Fail"/> (the RETRY runner re-invokes — this helper
    /// never writes Inconclusive, §7/§12.1).
    /// The non-destructive peek (PeekMessagesAsync) means the message from the previous
    /// test is still present; we search for a substring that isn't in it.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqExpectAzureServiceBus_NoMatchingMessage_ReturnsFail()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        // Publish a message that does NOT contain the expected substring.
        var publishModel = new MqPublishAzureServiceBusModel(
            Target: DepName,
            Queue: TestQueue,
            Topic: null,
            Payload: "{\"event\":\"payment-received\"}",
            Properties: null);

        var pubOutcome = await RunPublishAsync(publishModel, "pub-payment", vars);
        Assert.Equal(Verdict.Pass, pubOutcome.Verdict);

        // Expect a substring that is NOT in any published message.
        var expectModel = new MqExpectAzureServiceBusModel(
            Target: DepName,
            Queue: TestQueue,
            Topic: null,
            Subscription: null,
            ExpectPayloadContains: "THIS-SUBSTRING-WILL-NEVER-MATCH",
            ExpectProperties: null);

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
    public async Task MqPublishAzureServiceBus_AbsentConnKey_ReturnsEnvironmentError()
    {
        var model = new MqPublishAzureServiceBusModel(
            Target: "missing-dep",
            Queue: TestQueue,
            Topic: null,
            Payload: "hello",
            Properties: null);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);

        var outcome = await RunPublishAsync(model, "pub-env-error", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }
}
