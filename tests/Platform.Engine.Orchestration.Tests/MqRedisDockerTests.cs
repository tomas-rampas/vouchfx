// Platform.Engine.Orchestration.Tests — mq-publish.redis + mq-expect.redis Docker
// integration tests.
//
// Exercises the full topology path for the mq-publish.redis and mq-expect.redis
// providers with a live Redis container started via SuiteTopology (the AppHost
// fixture with Aspire.AppHost.Sdk), mirroring MqNatsDockerTests.cs exactly with Redis
// Streams (XADD/XRANGE) in place of NATS JetStream.
// Tests that start a topology carry [Trait("requires","docker")] and are excluded from the
// no-docker CI filter; the one test with no topology (step 6 below) omits the trait.
//
// Test flow:
//   1. Start a SuiteTopology with a single "redis" dependency ("cache").
//   2. Obtain the Redis connection string from DiscoveredServices and stage it under
//      VarKeys.Connection("cache").
//   3. Emit + compile + run an mq-publish.redis step → verify Pass.
//   4. Emit + compile + run an mq-expect.redis step (matching criteria) → verify Pass.
//   5. Re-run mq-expect.redis with a non-matching criterion → verify Fail.
//   6. Verify EnvironmentError when the conn key is absent (no topology needed).
//   7. Teardown — SuiteTopology.DisposeAsync tears down the container (§4.5).
//
// Shared-stream note: mq-expect.redis scans the WHOLE stream via XRANGE - + on every
// attempt (no DeliverPolicy-style filtering).  Each test below therefore uses a
// DISTINCT stream key so entries from one test never satisfy another's assertion.
//
// Run with: dotnet test --filter "requires=docker&FullyQualifiedName~MqRedisDocker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
using Platform.Engine.Abstractions;
using Platform.Engine.Authoring.Model;
using Platform.Engine.Compilation;
using Platform.Engine.Orchestration;
using Platform.Sdk;
using Platform.Steps.MqExpect.Redis;
using Platform.Steps.MqPublish.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Platform.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end execution tests for the <c>mq-publish.redis</c> and
/// <c>mq-expect.redis</c> Core providers.
/// Requires a running Docker daemon with a Redis image available.
/// </summary>
/// <remarks>
/// This test class carries <c>[Trait("requires","docker")]</c> on every method that starts a
/// <see cref="SuiteTopology"/>, so those are excluded from the non-Docker CI filter
/// (<c>dotnet test --filter "requires!=docker"</c>); the one method that never touches Docker
/// (the absent-conn-key fail-fast check) deliberately omits the trait so it still runs there.
/// The test assembly already carries <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c>
/// and <c>Aspire.AppHost.Sdk</c>, which embed the <c>dcpclipath</c> metadata required by
/// <see cref="SuiteTopology.StartAsync"/>.
/// </remarks>
public sealed class MqRedisDockerTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Platform.Engine.Orchestration.Tests";

    /// <summary>Startup timeout: Redis starts quickly, but leave generous headroom.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Logical name of the Redis dependency under test.</summary>
    private const string DepName = "cache";

    public MqRedisDockerTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Compile-time metadata references for the emitted CSX body.  StackExchange.Redis,
    /// JsonPath.Net, System.Text.Json, System.Text.RegularExpressions, and
    /// System.Globalization are not in the default TPA subset so they must be supplied
    /// explicitly.
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(StackExchange.Redis.ConnectionMultiplexer).Assembly.Location,
        typeof(Json.Path.JsonPath).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
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

    /// <summary>Builds the Aspire environment spec for a single Redis dependency.</summary>
    private static EnvironmentSpec BuildEnv() =>
        new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                [DepName] = new DependencySpec(Type: "redis", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>
    /// Emits and runs an <c>mq-publish.redis</c> fragment.
    /// Returns the <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private static async Task<StepOutcome> RunPublishAsync(
        MqPublishRedisModel model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var provider = new MqPublishRedisProvider();
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
    /// Emits and runs an <c>mq-expect.redis</c> fragment.
    /// Returns the <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private static async Task<StepOutcome> RunExpectAsync(
        MqExpectRedisModel model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var provider = new MqExpectRedisProvider();
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
    /// <see cref="Verdict.Pass"/> when Redis is reachable — the entry is appended to
    /// the stream via <c>XADD</c>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqPublishRedis_ReachableBroker_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr),
            $"DiscoveredServices['{DepName}'] must be a non-empty Redis connection string.");
        _output.WriteLine($"Redis conn string: {connStr}");

        var model = new MqPublishRedisModel(
            Target: DepName,
            Stream: "orders-created",
            Payload: "{\"event\":\"order-created\",\"orderId\":\"100\"}");

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
    /// After publishing a message, an <c>mq-expect.redis</c> step with a matching
    /// <c>payloadContains</c> criterion yields <see cref="Verdict.Pass"/>.  Proves the
    /// <c>XRANGE - +</c> scan retrieves the entry appended via <c>XADD</c>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqExpectRedis_MessagePresent_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));
        _output.WriteLine($"Redis conn string: {connStr}");

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        // Publish a message first, on a stream distinct from every other test case.
        const string testStream = "ship-events";
        var publishModel = new MqPublishRedisModel(
            Target: DepName,
            Stream: testStream,
            Payload: "{\"event\":\"order-shipped\",\"id\":\"42\"}");

        var pubOutcome = await RunPublishAsync(publishModel, "pub-ship", vars);
        _output.WriteLine($"Publish Verdict: {pubOutcome.Verdict}, Observation: {pubOutcome.Observation}");
        Assert.Equal(Verdict.Pass, pubOutcome.Verdict);

        // Now expect the message.
        var expectModel = new MqExpectRedisModel(
            Target: DepName,
            Stream: testStream,
            Match: new RedisMatch(
                PayloadContains: "order-shipped",
                Json: null));

        var outcome = await RunExpectAsync(expectModel, "expect-pass", vars);

        _output.WriteLine($"Expect Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    /// <summary>
    /// A JSONPath criterion in <c>match.json</c> also matches the JSON-encoded payload
    /// carried under the canonical <c>payload</c> stream field.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqExpectRedis_JsonPathMatch_ReturnsPass()
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

        const string testStream = "billing-events";
        var publishModel = new MqPublishRedisModel(
            Target: DepName,
            Stream: testStream,
            Payload: "{\"userId\":\"u-7\",\"status\":\"PENDING\"}");

        var pubOutcome = await RunPublishAsync(publishModel, "pub-billing", vars);
        Assert.Equal(Verdict.Pass, pubOutcome.Verdict);

        var expectModel = new MqExpectRedisModel(
            Target: DepName,
            Stream: testStream,
            Match: new RedisMatch(
                PayloadContains: null,
                Json: new Dictionary<string, string>
                {
                    ["$.userId"] = "u-7",
                    ["$.status"] = "PENDING",
                }));

        var outcome = await RunExpectAsync(expectModel, "expect-json-pass", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    /// <summary>
    /// When the published message does not contain the expected substring, the step
    /// yields <see cref="Verdict.Fail"/> (the RETRY runner re-invokes — this helper
    /// never writes Inconclusive, §7/§12.1).
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqExpectRedis_NoMatchingMessage_ReturnsFail()
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
        const string testStream = "payment-events";
        var publishModel = new MqPublishRedisModel(
            Target: DepName,
            Stream: testStream,
            Payload: "{\"event\":\"payment-received\"}");

        var pubOutcome = await RunPublishAsync(publishModel, "pub-payment", vars);
        Assert.Equal(Verdict.Pass, pubOutcome.Verdict);

        // Expect a substring that is NOT in the published message.
        var expectModel = new MqExpectRedisModel(
            Target: DepName,
            Stream: testStream,
            Match: new RedisMatch(
                PayloadContains: "order-shipped",   // Deliberately wrong
                Json: null));

        var outcome = await RunExpectAsync(expectModel, "expect-fail", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    /// <summary>
    /// Scanning a stream with no entries at all (XRANGE on a never-published key)
    /// yields <see cref="Verdict.Fail"/>, not an error — a missing key is not an
    /// infrastructure failure in Redis Streams semantics.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MqExpectRedis_EmptyStream_ReturnsFail()
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

        var expectModel = new MqExpectRedisModel(
            Target: DepName,
            Stream: "never-published-stream",
            Match: new RedisMatch(PayloadContains: "anything", Json: null));

        var outcome = await RunExpectAsync(expectModel, "expect-empty", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    /// <summary>
    /// When the <c>conn::</c> key is absent from <c>Vars</c>, the step outcome must be
    /// <see cref="Verdict.EnvironmentError"/> (§12.1: infrastructure failure, never
    /// conflated with a test failure).  No topology needed — this is a unit-level check.
    /// </summary>
    // No [Trait("requires","docker")] here — unlike its siblings above, this test
    // never calls SuiteTopology.StartAsync (no container, no Docker daemon touch);
    // it only exercises the emitted fragment's fail-fast path when the `conn::` key
    // is absent from Vars, so it belongs in the default (non-Docker) CI filter.
    [Fact]
    public async Task MqPublishRedis_AbsentConnKey_ReturnsEnvironmentError()
    {
        var model = new MqPublishRedisModel(
            Target: "missing-dep",
            Stream: "any-stream",
            Payload: "hello");

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);

        var outcome = await RunPublishAsync(model, "pub-env-error", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }
}
