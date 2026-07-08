// Platform.Engine.Orchestration.Tests — mq-publish.rabbitmq + mq-expect.rabbitmq Docker
// integration tests.
//
// Exercises the full topology path for the mq-publish.rabbitmq and mq-expect.rabbitmq
// providers with a live RabbitMQ container started via SuiteTopology (the AppHost fixture
// with Aspire.AppHost.Sdk).  Tests carry [Trait("requires","docker")] and are excluded from
// the no-docker CI filter.
//
// Test flow (per test):
//   1. Start a SuiteTopology with a single "rabbitmq" dependency ("rmq").
//   2. Obtain the AMQP URI from DiscoveredServices and stage it under VarKeys.Connection("rmq").
//   3. Declare the target queue via a direct RabbitMQ.Client 7.x connection (setup only).
//   4. Emit + compile + run an mq-publish.rabbitmq step → verify Pass.
//   5. Emit + compile + run an mq-expect.rabbitmq step → verify Pass (matching message found).
//   6. Re-run mq-expect.rabbitmq with a non-matching criterion → verify Fail.
//   7. Teardown — SuiteTopology.DisposeAsync tears down the container (§4.5).
//
// RabbitMQ.Client 7.x note:
//   IConnection and IChannel are IAsyncDisposable only — disposed via
//   await DisposeAsync().ConfigureAwait(false) in a finally block everywhere in this file.
//
// Run with: dotnet test --filter "requires=docker&FullyQualifiedName~MqRabbitmqDocker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
using Platform.Engine.Abstractions;
using Platform.Engine.Authoring.Model;
using Platform.Engine.Compilation;
using Platform.Engine.Orchestration;
using Platform.Sdk;
using Platform.Steps.MqExpect.Rabbitmq;
using Platform.Steps.MqPublish.Rabbitmq;
using Xunit;
using Xunit.Abstractions;

namespace Platform.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end execution tests for the <c>mq-publish.rabbitmq</c> and
/// <c>mq-expect.rabbitmq</c> Core providers.
/// Requires a running Docker daemon with the RabbitMQ management image available — the exact
/// tag is Aspire.Hosting.RabbitMQ's own pinned default (currently <c>rabbitmq:4.3-management</c>;
/// see the pre-pull comment in <c>.github/workflows/build.yml</c>), not hardcoded here.
/// </summary>
/// <remarks>
/// This test class carries <c>[Trait("requires","docker")]</c> on every method so it is
/// excluded from the non-Docker CI filter (<c>dotnet test --filter "requires!=docker"</c>).
/// The test assembly already carries <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c>
/// and <c>Aspire.AppHost.Sdk</c>, which embed the <c>dcpclipath</c> metadata required by
/// <see cref="SuiteTopology.StartAsync"/>.
/// </remarks>
public sealed class MqRabbitmqDockerTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Platform.Engine.Orchestration.Tests";

    /// <summary>Startup timeout: RabbitMQ typically starts in ~10 s; 120 s is generous.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Logical name of the RabbitMQ dependency under test.</summary>
    private const string DepName = "rmq";

    /// <summary>Name of the test queue used across all tests in this file.</summary>
    private const string TestQueue = "vouchfx-test-q";

    public MqRabbitmqDockerTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Compile-time metadata references for the emitted CSX body.  RabbitMQ.Client,
    /// JsonPath.Net, System.Text.Json, and System.Text.RegularExpressions are not in
    /// the default TPA subset so they must be supplied explicitly.
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(RabbitMQ.Client.ConnectionFactory).Assembly.Location,
        typeof(Json.Path.JsonPath).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        // System.Globalization.CultureInfo is needed for the scanned count in the
        // mq-expect observation: scanned.ToString(CultureInfo.InvariantCulture).
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        // System.Uri is forwarded to System.Private.Uri; Roslyn does not resolve it
        // automatically from System.Runtime so it must be listed explicitly.
        typeof(System.Uri).Assembly.Location,
    };

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

    /// <summary>Builds the Aspire environment spec for a single RabbitMQ dependency.</summary>
    private static EnvironmentSpec BuildEnv() =>
        new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                [DepName] = new DependencySpec(Type: "rabbitmq", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>
    /// Declares the test queue and publishes <paramref name="count"/> copies of
    /// <paramref name="payload"/> using a direct RabbitMQ.Client 7.x connection (test setup).
    /// IConnection and IChannel are IAsyncDisposable in 7.x; disposed via await DisposeAsync()
    /// in the finally block.
    /// </summary>
    private static async Task SeedAsync(string amqpUri, string payload, int count = 1)
    {
        var factory = new RabbitMQ.Client.ConnectionFactory { Uri = new Uri(amqpUri) };
        RabbitMQ.Client.IConnection? conn = null;
        RabbitMQ.Client.IChannel? channel = null;
        try
        {
            conn = await factory.CreateConnectionAsync(CancellationToken.None)
                .ConfigureAwait(false);
            channel = await conn.CreateChannelAsync(cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            // durable: true — modern RabbitMQ (4.1+) rejects a transient (durable:false),
            // non-exclusive queue declaration outright (code=541 INTERNAL_ERROR,
            // "Feature `transient_nonexcl_queues` is deprecated ... not permitted anymore by
            // default"); the feature is slated for full removal in a future major version
            // regardless of configuration. A durable, non-exclusive, non-auto-delete queue is
            // also the exact shape docs/troubleshooting.md recommends authors use for their own
            // SUT/seed queue provisioning ("RabbitMQ: message not in queue" — "Use durable
            // queues"), so this test setup now matches the documented guidance rather than
            // pre-dating it.
            await channel.QueueDeclareAsync(
                queue: TestQueue,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            var body = System.Text.Encoding.UTF8.GetBytes(payload).AsMemory();
            for (int i = 0; i < count; i++)
            {
                await channel.BasicPublishAsync(
                    exchange: string.Empty,
                    routingKey: TestQueue,
                    mandatory: false,
                    basicProperties: new RabbitMQ.Client.BasicProperties(),
                    body: body,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            if (channel is not null)
            {
                try { await channel.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            if (conn is not null)
            {
                try { await conn.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
    }

    /// <summary>
    /// Emits and runs an <c>mq-publish.rabbitmq</c> fragment.
    /// Returns the <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private static async Task<StepOutcome> RunPublishAsync(
        MqPublishRabbitmqModel model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var provider = new MqPublishRabbitmqProvider();
        var fragment = provider.Emit(model, new StubCompileContext(stepId));

        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
        // mq-expect provider additionally needs JsonPath.Net; include it for consistency.
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
    /// Emits and runs an <c>mq-expect.rabbitmq</c> fragment.
    /// Returns the <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private static async Task<StepOutcome> RunExpectAsync(
        MqExpectRabbitmqModel model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var provider = new MqExpectRabbitmqProvider();
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
    /// <see cref="Verdict.Pass"/> when the broker is reachable.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqPublishRabbitmq_ReachableBroker_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var amqpUri = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(amqpUri),
            $"DiscoveredServices['{DepName}'] must be a non-empty AMQP URI.");
        _output.WriteLine($"RabbitMQ AMQP URI: {amqpUri}");

        // Declare the queue so the publish has somewhere to route the message.
        await SeedAsync(amqpUri!, payload: string.Empty, count: 0);

        var model = new MqPublishRabbitmqModel(
            Target: DepName,
            Exchange: null,
            RoutingKey: TestQueue,
            Payload: "{\"event\":\"order-created\",\"id\":\"100\"}",
            Headers: null);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = amqpUri,
        };

        var outcome = await RunPublishAsync(model, "pub-pass", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    /// <summary>
    /// After publishing a message to the queue, an <c>mq-expect.rabbitmq</c> step
    /// with matching <c>payloadContains</c> criterion yields <see cref="Verdict.Pass"/>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqExpectRabbitmq_MessagePresent_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var amqpUri = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(amqpUri));

        const string testPayload = "{\"event\":\"order-shipped\",\"id\":\"42\"}";
        await SeedAsync(amqpUri!, testPayload);

        var model = new MqExpectRabbitmqModel(
            Target: DepName,
            Queue: TestQueue,
            Match: new RabbitmqMatch(
                PayloadContains: "order-shipped",
                Headers: null,
                Json: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = amqpUri,
        };

        var outcome = await RunExpectAsync(model, "expect-pass", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    /// <summary>
    /// When the queued message does not contain the expected substring, the step
    /// yields <see cref="Verdict.Fail"/> (RETRY runner re-invokes — this helper never
    /// writes Inconclusive, §7/§12.1).
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqExpectRabbitmq_NoMatchingMessage_ReturnsFail()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var amqpUri = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(amqpUri));

        // Publish a message that does NOT contain the expected substring.
        const string testPayload = "{\"event\":\"payment-received\"}";
        await SeedAsync(amqpUri!, testPayload);

        var model = new MqExpectRabbitmqModel(
            Target: DepName,
            Queue: TestQueue,
            Match: new RabbitmqMatch(
                PayloadContains: "order-shipped",  // Deliberately wrong — message says "payment-received"
                Headers: null,
                Json: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = amqpUri,
        };

        var outcome = await RunExpectAsync(model, "expect-fail", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    /// <summary>
    /// When the <c>conn::</c> key is absent from <c>Vars</c>, the step outcome must be
    /// <see cref="Verdict.EnvironmentError"/> (§12.1: infrastructure failure, never
    /// conflated with a test failure).  No topology needed.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqPublishRabbitmq_AbsentConnKey_ReturnsEnvironmentError()
    {
        var model = new MqPublishRabbitmqModel(
            Target: "missing-dep",
            Exchange: null,
            RoutingKey: "q",
            Payload: "hello",
            Headers: null);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);

        var outcome = await RunPublishAsync(model, "pub-env-error", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    /// <summary>
    /// Regression test for the drain-loop head-of-queue starvation bug (B1).
    /// Seeds a NON-MATCHING message first, then the MATCHING message.  The drain
    /// loop must advance past the non-matching head to find the matching message
    /// behind it and yield <see cref="Verdict.Pass"/>.
    /// <para>
    /// With the old in-loop <c>BasicNackAsync(requeue:true)</c> pattern this test
    /// returned <see cref="Verdict.Fail"/>: the non-matching head was requeued
    /// immediately and re-fetched on every subsequent <c>BasicGet</c> call, so the
    /// loop never advanced to the matching message. The fix uses a peek-and-hold
    /// approach: unmatched delivery tags are collected and requeued in bulk AFTER
    /// the drain exits, ensuring each call to <c>BasicGet</c> returns a DISTINCT message.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqExpectRabbitmq_MatchingMessageBehindNonMatchingHead_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var amqpUri = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(amqpUri));

        // Enqueue a non-matching message first (will sit at the head of the queue).
        await SeedAsync(amqpUri!, "{\"event\":\"payment-received\"}");

        // Enqueue the matching message second (sits behind the non-matching head).
        await SeedAsync(amqpUri!, "{\"event\":\"order-shipped\",\"id\":\"7\"}");

        var model = new MqExpectRabbitmqModel(
            Target: DepName,
            Queue: TestQueue,
            Match: new RabbitmqMatch(
                PayloadContains: "order-shipped",   // Only the second message matches.
                Headers: null,
                Json: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = amqpUri,
        };

        var outcome = await RunExpectAsync(model, "expect-behind-head", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        // Must be Pass — the drain must have advanced past the non-matching head.
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    /// <summary>
    /// An mq-expect.rabbitmq step with a JSONPath criterion matches a message whose
    /// body is valid JSON and the path selects the expected value.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqExpectRabbitmq_JsonPathCriterion_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var amqpUri = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(amqpUri));

        const string testPayload = "{\"order\":{\"id\":\"99\",\"status\":\"fulfilled\"}}";
        await SeedAsync(amqpUri!, testPayload);

        var model = new MqExpectRabbitmqModel(
            Target: DepName,
            Queue: TestQueue,
            Match: new RabbitmqMatch(
                PayloadContains: null,
                Headers: null,
                Json: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["$.order.status"] = "fulfilled",
                }));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = amqpUri,
        };

        var outcome = await RunExpectAsync(model, "expect-jsonpath", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }
}
