// DbAssertMongodbProvider Docker-gated end-to-end execution tests.
//
// Mirrors DbAssertSqlServerDockerTests.cs.  Proves that the emitted CSX fragment
// executes correctly against a real MongoDB container started via SuiteTopology
// (the AppHost fixture with Aspire.AppHost.Sdk).
//
// Three verdict scenarios are exercised:
//   Pass          — filter matches the seeded document and count/field expectations are met.
//   Fail          — filter matches documents but count expectation is wrong.
//   EnvironmentError — connection key absent from Vars.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~DbAssertMongodbDocker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
//
// Design notes:
//   • The test starts a SuiteTopology with a single MongoDB dependency ("testmongo").
//   • EnvironmentMapper wires the dependency as builder.AddMongoDB("testmongo").AddDatabase("testmongodb"),
//     producing a connection string whose URI path is "/testmongodb".
//   • After startup, the conn string is read from DiscoveredServices and staged in
//     Vars under VarKeys.Connection("testmongo").
//   • A seed MongoClient inserts { orderId: 1, status: "SHIPPED" } into testmongodb.orders.
//   • The provider emits a fragment; CsxAssembler + CompileOnce + RunIsolatedAsync execute it.
//   • MongoDB.Driver and MongoDB.Bson are added to additionalReferencePaths (compile-ref only,
//     never collectible ALC — §5 memory-model invariant).
//   • Do NOT run this test locally or in CI unless Docker is available and
//     mongo:7 is pre-pulled (or Docker Hub is reachable).
using Platform.Engine.Abstractions;
using Platform.Engine.Authoring.Model;
using Platform.Engine.Compilation;
using Platform.Engine.Orchestration;
using Platform.Sdk;
using Platform.Steps.DbAssert.Mongodb;
using Xunit;
using Xunit.Abstractions;

namespace Platform.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end execution tests for <see cref="DbAssertMongodbProvider"/>.
/// Requires a running Docker daemon with a MongoDB image available.
/// </summary>
/// <remarks>
/// This test class carries <c>[Trait("requires","docker")]</c> on every method so it is
/// excluded from the non-Docker CI filter (<c>dotnet test --filter "requires!=docker"</c>).
/// The test assembly already carries <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c>
/// and <c>Aspire.AppHost.Sdk</c>, which embed the <c>dcpclipath</c> metadata required by
/// <see cref="SuiteTopology.StartAsync"/>.
/// </remarks>
public sealed class DbAssertMongodbDockerTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Platform.Engine.Orchestration.Tests";

    /// <summary>
    /// Startup timeout for MongoDB.  120 s is generous for docker pull + container start
    /// of mongo:7 on CI (typically completes in 20-40 s when the image is cached).
    /// </summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Logical name of the MongoDB dependency under test.</summary>
    private const string DepName = "testmongo";

    /// <summary>
    /// Database name as produced by EnvironmentMapper for dependency name "testmongo":
    /// builder.AddMongoDB("testmongo").AddDatabase("testmongodb") → URI path "/testmongodb".
    /// </summary>
    private const string DbName = DepName + "db";

    /// <summary>Collection to insert seed documents into and query against.</summary>
    private const string CollectionName = "orders";

    public DbAssertMongodbDockerTests(ITestOutputHelper output) => _output = output;

    // ── Shared compile-reference paths ────────────────────────────────────────

    /// <summary>
    /// Additional Roslyn metadata references for the emitted CSX body.
    /// MongoDB.Driver and MongoDB.Bson are required by the emitted helper class.
    /// These are compile-time references only; at runtime the assemblies resolve
    /// from the Default ALC (§5 memory-model invariant).
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(MongoDB.Driver.MongoClient).Assembly.Location,
        typeof(MongoDB.Bson.BsonDocument).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
    };

    // ── Test helpers ─────────────────────────────────────────────────────────

    /// <summary>Minimal <see cref="ICompileContext"/> for emit tests.</summary>
    private sealed class StubCompileContext : ICompileContext
    {
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

    /// <summary>
    /// Builds the Aspire environment spec for a single MongoDB dependency.
    /// </summary>
    private static EnvironmentSpec BuildEnv() =>
        new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                [DepName] = new DependencySpec(Type: "mongodb", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>
    /// Seeds the test database by inserting one document via a direct MongoClient.
    /// Explicit Dispose() in finally (§13.3.1 — no using var).
    /// </summary>
    private static async Task SeedAsync(string connStr)
    {
        MongoDB.Driver.MongoClient? client = null;
        try
        {
            client = new MongoDB.Driver.MongoClient(connStr);
            var db = client.GetDatabase(DbName);
            var coll = db.GetCollection<MongoDB.Bson.BsonDocument>(CollectionName);

            // Drop and recreate to guarantee a known state for repeated runs.
            await coll.DeleteManyAsync(MongoDB.Bson.BsonDocument.Parse("{}")).ConfigureAwait(false);

            var doc = MongoDB.Bson.BsonDocument.Parse("{\"orderId\": 1, \"status\": \"SHIPPED\"}");
            await coll.InsertOneAsync(doc).ConfigureAwait(false);
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>
    /// Emits the fragment for a <c>db-assert.mongodb</c> step, assembles it,
    /// compiles it once, and executes it with the supplied <c>Vars</c> dictionary.
    /// Returns the <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private static async Task<StepOutcome> RunStepAsync(
        DbAssertMongodbModel model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var provider = new DbAssertMongodbProvider();
        var ctx = new StubCompileContext(stepId);
        var fragment = provider.Emit(model, ctx);

        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource,
            additionalReferencePaths: s_additionalRefs);

        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var safeId = CsxFragment.SanitiseId(stepId);
        var outcomeKey = VarKeys.Outcome(safeId);

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Vars must contain outcome key '{outcomeKey}' after RunIsolatedAsync. " +
            $"Actual keys: [{string.Join(", ", vars.Keys)}]");

        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }

    // ── Test cases ────────────────────────────────────────────────────────────

    /// <summary>
    /// When the seeded document matches <c>expect.count=1</c> and
    /// <c>expect.document["status"]="SHIPPED"</c>, the step outcome must be
    /// <see cref="Verdict.Pass"/>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_MatchingCountAndField_ReturnsPass()
    {
        var env = BuildEnv();
        await using var suite = await SuiteTopology.StartAsync(
            environment: env,
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr),
            $"DiscoveredServices['{DepName}'] must be a non-empty connection string.");
        _output.WriteLine($"MongoDB conn string: {connStr}");

        await SeedAsync(connStr!);
        _output.WriteLine("Seed: orders collection populated with {orderId:1, status:\"SHIPPED\"}.");

        var model = new DbAssertMongodbModel(
            Target: DepName,
            Collection: CollectionName,
            Filter: "{\"orderId\": 1}",
            Expect: new MongoExpectation(
                Count: 1L,
                Document: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["status"] = "SHIPPED",
                }));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "assert-mongo", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, DurationMs: {outcome.DurationMs}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);
    }

    /// <summary>
    /// When the seeded collection has 1 document but <c>expect.count=99</c>, the step
    /// outcome must be <see cref="Verdict.Fail"/> with a count-mismatch observation.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_CountMismatch_ReturnsFail()
    {
        var env = BuildEnv();
        await using var suite = await SuiteTopology.StartAsync(
            environment: env,
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));
        await SeedAsync(connStr!);

        var model = new DbAssertMongodbModel(
            Target: DepName,
            Collection: CollectionName,
            Filter: "{}",
            Expect: new MongoExpectation(Count: 99L, Document: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "assert-mongo-count-fail", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("count", outcome.Observation, StringComparison.Ordinal);
    }

    /// <summary>
    /// When the <c>conn::</c> key is absent from <c>Vars</c>, the step outcome
    /// must be <see cref="Verdict.EnvironmentError"/> (§12.1: infrastructure failure,
    /// never conflated with a test failure).
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_AbsentConnKey_ReturnsEnvironmentError()
    {
        // No topology needed — the EnvironmentError is detected before any network call.
        var model = new DbAssertMongodbModel(
            Target: "missing-dep",
            Collection: CollectionName,
            Filter: "{}",
            Expect: new MongoExpectation(Count: 1L, Document: null));

        // Deliberately absent: no VarKeys.Connection("missing-dep") in Vars.
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);

        var outcome = await RunStepAsync(model, "assert-mongo-env-error", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("error", outcome.Observation, StringComparison.OrdinalIgnoreCase);
    }
}
