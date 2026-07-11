// CacheAssertRedisProvider Docker-gated end-to-end execution tests.
//
// Mirrors DbAssertSqlServerDockerTests.cs.  Proves that the emitted CSX fragment executes
// correctly against a real Redis container started via SuiteTopology (the AppHost fixture
// with Aspire.AppHost.Sdk).
//
// Three verdict scenarios are exercised across a couple of operations:
//   Pass             — the read returns the expected value / presence.
//   Fail             — the read returns an unexpected value (assertion mismatch).
//   EnvironmentError — connection key absent from Vars.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~CacheAssertRedisDocker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
//
// Design notes:
//   • The test starts a SuiteTopology with a single Redis dependency ("cache").
//   • After startup, the conn string is read from DiscoveredServices and staged in Vars
//     under VarKeys.Connection("cache").
//   • A seed ConnectionMultiplexer sets the fixture keys.
//   • The provider emits a fragment; CsxAssembler + CompileOnce + RunIsolatedAsync execute it.
//   • StackExchange.Redis is added to additionalReferencePaths (compile-ref only, never
//     loaded into the collectible ALC, §5).
//   • NOTE: Do NOT run this test locally or in CI unless Docker is available and redis:7
//     is pre-pulled.
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Sdk;
using Vouchfx.Steps.CacheAssert.Redis;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end execution tests for <see cref="CacheAssertRedisProvider"/>.
/// Requires a running Docker daemon with a Redis image available.
/// </summary>
/// <remarks>
/// This test class carries <c>[Trait("requires","docker")]</c> on every method so it is
/// excluded from the non-Docker CI filter (<c>dotnet test --filter "requires!=docker"</c>).
/// The test assembly already carries <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c>
/// and <c>Aspire.AppHost.Sdk</c>, which embed the <c>dcpclipath</c> metadata required by
/// <see cref="SuiteTopology.StartAsync"/>.
/// </remarks>
public sealed class CacheAssertRedisDockerTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>Startup timeout: Redis starts quickly, but leave generous headroom.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Logical name of the Redis dependency under test.</summary>
    private const string DepName = "cache";

    public CacheAssertRedisDockerTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Compile-time metadata references for the emitted CSX body.  StackExchange.Redis,
    /// System.Text.Json, System.Globalization and System.Text.RegularExpressions are not
    /// in the default TPA subset so they must be supplied explicitly.  These are compile-time
    /// references only; at runtime the assemblies resolve from the Default ALC.
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(StackExchange.Redis.ConnectionMultiplexer).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
    };

    /// <summary>Minimal <see cref="ICompileContext"/> for emit tests.</summary>
    private sealed class StubCompileContext : ICompileContext
    {
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public StubCompileContext(string stepId) => StepId = stepId;

        /// <inheritdoc />
        public string StepId { get; }

        /// <inheritdoc />
        public string SuiteNamespace => "Generated";

        /// <inheritdoc />
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <inheritdoc />
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
    /// Seeds fixture data via a direct <see cref="StackExchange.Redis.ConnectionMultiplexer"/>:
    /// a string key, a hash, a list, and a set.
    /// </summary>
    private static async Task SeedAsync(string connStr)
    {
        var mux = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(connStr).ConfigureAwait(false);
        try
        {
            var db = mux.GetDatabase();
            await db.StringSetAsync("user:42", "active").ConfigureAwait(false);
            await db.HashSetAsync(
                "user:42:profile",
                new[] { new StackExchange.Redis.HashEntry("status", "verified") }).ConfigureAwait(false);
            await db.ListRightPushAsync("queue", new StackExchange.Redis.RedisValue[] { "a", "b", "c" }).ConfigureAwait(false);
            await db.SetAddAsync("tags", new StackExchange.Redis.RedisValue[] { "x", "y" }).ConfigureAwait(false);
        }
        finally
        {
            mux.Dispose();
        }
    }

    /// <summary>
    /// Emits the fragment for a <c>cache-assert.redis</c> step, assembles it, compiles it
    /// once, and executes it with the supplied <c>Vars</c> dictionary.  Returns the
    /// <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private static async Task<StepOutcome> RunStepAsync(
        CacheAssertRedisModel model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var provider = new CacheAssertRedisProvider();
        var fragment = provider.Emit(model, new StubCompileContext(stepId));

        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Vars must contain outcome key '{outcomeKey}' after RunIsolatedAsync. " +
            $"Actual keys: [{string.Join(", ", vars.Keys)}]");

        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }

    // ── Test cases ────────────────────────────────────────────────────────────

    /// <summary>
    /// A GET whose value matches the seeded value yields <see cref="Verdict.Pass"/>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_GetMatchingValue_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr),
            $"DiscoveredServices['{DepName}'] must be a non-empty connection string.");
        _output.WriteLine($"Redis conn string: {connStr}");

        await SeedAsync(connStr!);

        var model = new CacheAssertRedisModel(
            Target: DepName,
            Key: "user:42",
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: "active", Exists: null, Length: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "get-pass", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    /// <summary>
    /// An HGET whose hash-field value matches the seeded value yields
    /// <see cref="Verdict.Pass"/>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_HGetMatchingValue_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));
        await SeedAsync(connStr!);

        var model = new CacheAssertRedisModel(
            Target: DepName,
            Key: "user:42:profile",
            Operation: RedisOp.HGet,
            Field: "status",
            Expect: new RedisExpectation(Value: "verified", Exists: null, Length: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "hget-pass", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    /// <summary>
    /// An LLEN whose length matches the seeded list length yields
    /// <see cref="Verdict.Pass"/>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_LLenMatchingLength_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));
        await SeedAsync(connStr!);

        var model = new CacheAssertRedisModel(
            Target: DepName,
            Key: "queue",
            Operation: RedisOp.LLen,
            Field: null,
            Expect: new RedisExpectation(Value: null, Exists: null, Length: 3L));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "llen-pass", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    /// <summary>
    /// A GET whose expected value differs from the seeded value yields
    /// <see cref="Verdict.Fail"/> with a value-mismatch observation.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_GetValueMismatch_ReturnsFail()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));
        await SeedAsync(connStr!);

        var model = new CacheAssertRedisModel(
            Target: DepName,
            Key: "user:42",
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: "inactive", Exists: null, Length: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "get-fail", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("value", outcome.Observation!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An EXISTS on an absent key expected present yields <see cref="Verdict.Fail"/>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_ExistsMismatch_ReturnsFail()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));
        await SeedAsync(connStr!);

        var model = new CacheAssertRedisModel(
            Target: DepName,
            Key: "no-such-key",
            Operation: RedisOp.Exists,
            Field: null,
            Expect: new RedisExpectation(Value: null, Exists: true, Length: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "exists-fail", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("exists", outcome.Observation!, StringComparison.Ordinal);
    }

    /// <summary>
    /// When the <c>conn::</c> key is absent from <c>Vars</c>, the step outcome must be
    /// <see cref="Verdict.EnvironmentError"/> (§12.1: infrastructure failure, never
    /// conflated with a test failure).
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_AbsentConnKey_ReturnsEnvironmentError()
    {
        // No topology needed — the EnvironmentError is detected before any network call.
        var model = new CacheAssertRedisModel(
            Target: "missing-dep",
            Key: "k",
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: "v", Exists: null, Length: null));

        // Deliberately absent: no VarKeys.Connection("missing-dep") in Vars.
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);

        var outcome = await RunStepAsync(model, "env-error", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("error", outcome.Observation!, StringComparison.OrdinalIgnoreCase);
    }
}
