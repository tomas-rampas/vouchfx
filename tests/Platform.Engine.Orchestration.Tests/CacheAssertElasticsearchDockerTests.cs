// CacheAssertElasticsearchProvider Docker-gated end-to-end execution tests.
//
// Mirrors CacheAssertRedisDockerTests.cs.  Proves that the emitted CSX fragment executes
// correctly against a real Elasticsearch container started via SuiteTopology (the AppHost
// fixture with Aspire.AppHost.Sdk).
//
// Three verdict scenarios are exercised:
//   Pass             — query matches expected count (and optional field values).
//   Fail             — query returns an unexpected count.
//   EnvironmentError — connection key absent from Vars.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~CacheAssertElasticsearchDocker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
//
// Design notes:
//   • The test starts a SuiteTopology with a single elasticsearch dependency ("search").
//   • After startup, the connection string (URL) is read from DiscoveredServices and staged
//     in Vars under VarKeys.Connection("search").
//   • A seed step POSTs documents to the index via a plain BCL HttpClient.
//   • The provider emits a fragment; CsxAssembler + CompileOnce + RunIsolatedAsync execute it.
//   • ES uses only BCL HttpClient and System.Text.Json — no additional NuGet packages needed
//     in compile-time references beyond those already in the BCL.
//   • ES stability env vars (discovery.type=single-node, xpack.security.enabled=false) are
//     applied by EnvironmentMapper (Phase 7) so the container starts without TLS/auth.
//   • NOTE: Do NOT run locally or in CI unless Docker is available and elasticsearch image
//     is reachable.  The HealthGate timeout is 120 s — ES is slower to start than Redis.
//   • After POSTing documents, a POST to /{index}/_refresh ensures the seeded data is
//     visible to the _search endpoint before the step runs (ES has near-real-time semantics
//     with a 1 s default refresh interval; we call _refresh explicitly to avoid flakiness).
using Platform.Engine.Abstractions;
using Platform.Engine.Authoring.Model;
using Platform.Engine.Compilation;
using Platform.Engine.Orchestration;
using Platform.Sdk;
using Platform.Steps.CacheAssert.Elasticsearch;
using Xunit;
using Xunit.Abstractions;

namespace Platform.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end execution tests for <see cref="CacheAssertElasticsearchProvider"/>.
/// Requires a running Docker daemon with the Elasticsearch image available.
/// </summary>
/// <remarks>
/// Every test method carries <c>[Trait("requires","docker")]</c> so it is excluded from the
/// non-Docker CI filter (<c>dotnet test --filter "requires!=docker"</c>).
/// The test assembly carries <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c> and
/// <c>Aspire.AppHost.Sdk</c>, which embed the <c>dcpclipath</c> metadata required by
/// <see cref="SuiteTopology.StartAsync"/>.
/// </remarks>
public sealed class CacheAssertElasticsearchDockerTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Platform.Engine.Orchestration.Tests";

    /// <summary>
    /// Startup timeout: Elasticsearch is significantly slower than Redis — 120 s gives
    /// generous headroom including image pull on first run.
    /// </summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Logical name of the Elasticsearch dependency under test.</summary>
    private const string DepName = "search";

    /// <summary>Index name used by all tests in this class.</summary>
    private const string IndexName = "orders";

    public CacheAssertElasticsearchDockerTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Compile-time metadata references for the emitted CSX body.  The ES provider uses only
    /// BCL HttpClient and System.Text.Json — both are already in the test project's
    /// transitive closure, so we just need their explicit locations for the Roslyn compiler.
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(System.Net.Http.HttpClient).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Uri).Assembly.Location,
    };

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

    /// <summary>Builds the Aspire environment spec for a single Elasticsearch dependency.</summary>
    private static EnvironmentSpec BuildEnv() =>
        new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                [DepName] = new DependencySpec(Type: "elasticsearch", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>
    /// Seeds two documents into the <see cref="IndexName"/> index and calls
    /// <c>/{index}/_refresh</c> to make them immediately visible to subsequent _search calls.
    /// Uses a plain <c>HttpClient</c> (BCL only — no typed ES client).
    /// </summary>
    private async Task SeedAsync(string esUrl)
    {
        var baseUrl = esUrl.TrimEnd('/');
        var http = new System.Net.Http.HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        try
        {
            // First ensure the index exists (PUT /{index}).
            var indexResp = await http.PutAsync(
                $"{baseUrl}/{IndexName}",
                new System.Net.Http.StringContent(
                    "{\"settings\":{\"number_of_shards\":1,\"number_of_replicas\":0}}",
                    System.Text.Encoding.UTF8, "application/json"))
                .ConfigureAwait(false);
            // 200 OK (already exists) or 400 (index exists) are both acceptable.
            _output.WriteLine($"Index create: {(int)indexResp.StatusCode}");

            // Seed document 1.
            var doc1 = await http.PostAsync(
                $"{baseUrl}/{IndexName}/_doc",
                new System.Net.Http.StringContent(
                    "{\"id\":\"ord-001\",\"status\":\"fulfilled\",\"region\":\"EU\"}",
                    System.Text.Encoding.UTF8, "application/json"))
                .ConfigureAwait(false);
            _output.WriteLine($"Seed doc1: {(int)doc1.StatusCode} {await doc1.Content.ReadAsStringAsync().ConfigureAwait(false)}");
            doc1.EnsureSuccessStatusCode();

            // Seed document 2.
            var doc2 = await http.PostAsync(
                $"{baseUrl}/{IndexName}/_doc",
                new System.Net.Http.StringContent(
                    "{\"id\":\"ord-002\",\"status\":\"pending\",\"region\":\"US\"}",
                    System.Text.Encoding.UTF8, "application/json"))
                .ConfigureAwait(false);
            _output.WriteLine($"Seed doc2: {(int)doc2.StatusCode} {await doc2.Content.ReadAsStringAsync().ConfigureAwait(false)}");
            doc2.EnsureSuccessStatusCode();

            // Force a refresh so the documents are immediately visible to _search.
            // IMPORTANT: _refresh does not accept a request body at all — Elasticsearch
            // 8.17.3 rejects ANY body (even "{}") with
            // illegal_argument_exception: "request [POST /{index}/_refresh] does not
            // support having a body" (400), verified directly against a live container.
            // Passing null content sends no body and no Content-Type header.
            var refresh = await http.PostAsync(
                $"{baseUrl}/{IndexName}/_refresh",
                content: null)
                .ConfigureAwait(false);
            _output.WriteLine($"Refresh: {(int)refresh.StatusCode}");
            refresh.EnsureSuccessStatusCode();
        }
        finally
        {
            http.Dispose();
        }
    }

    /// <summary>
    /// Emits the fragment for a <c>cache-assert.elasticsearch</c> step, assembles it,
    /// compiles it once, and executes it with the supplied <c>Vars</c> dictionary.
    /// Returns the <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private static async Task<StepOutcome> RunStepAsync(
        CacheAssertElasticsearchModel model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var provider = new CacheAssertElasticsearchProvider();
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
    /// A match_all query with min-count=1 against the seeded index (2 docs) yields
    /// <see cref="Verdict.Pass"/>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_MatchAllWithMinCount_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var esUrl = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(esUrl),
            $"DiscoveredServices['{DepName}'] must be a non-empty URL string.");
        _output.WriteLine($"Elasticsearch URL: {esUrl}");

        await SeedAsync(esUrl!);

        var model = new CacheAssertElasticsearchModel(
            Target: DepName,
            Index: IndexName,
            Query: null,   // default match_all
            Expect: new EsExpectation(Count: null, MinCount: 1, Fields: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = esUrl,
        };

        var outcome = await RunStepAsync(model, "es-matchall-pass", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    /// <summary>
    /// An exact-count assertion matching the number of seeded documents yields
    /// <see cref="Verdict.Pass"/>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_ExactCountMatch_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var esUrl = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(esUrl));
        await SeedAsync(esUrl!);

        var model = new CacheAssertElasticsearchModel(
            Target: DepName,
            Index: IndexName,
            Query: null,
            Expect: new EsExpectation(Count: 2, MinCount: 1, Fields: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = esUrl,
        };

        var outcome = await RunStepAsync(model, "es-exactcount-pass", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    /// <summary>
    /// A term-query filtering for the "fulfilled" document, combined with a field assertion
    /// on the <c>status</c> field, yields <see cref="Verdict.Pass"/>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_TermQueryWithFieldAssertion_ReturnsPass()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var esUrl = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(esUrl));
        await SeedAsync(esUrl!);

        var model = new CacheAssertElasticsearchModel(
            Target: DepName,
            Index: IndexName,
            Query: "{\"query\":{\"term\":{\"status.keyword\":\"fulfilled\"}}}",
            Expect: new EsExpectation(
                Count: 1,
                MinCount: 1,
                Fields: new[] { new EsFieldAssertion("status", "fulfilled") }));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = esUrl,
        };

        var outcome = await RunStepAsync(model, "es-term-field-pass", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    /// <summary>
    /// An exact-count assertion that exceeds the actual number of seeded documents yields
    /// <see cref="Verdict.Fail"/> with a count-mismatch observation.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_ExactCountMismatch_ReturnsFail()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var esUrl = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(esUrl));
        await SeedAsync(esUrl!);

        var model = new CacheAssertElasticsearchModel(
            Target: DepName,
            Index: IndexName,
            Query: null,
            Expect: new EsExpectation(Count: 99, MinCount: 1, Fields: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = esUrl,
        };

        var outcome = await RunStepAsync(model, "es-count-fail", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        // Observation must contain the expected count or the word "matched".
        Assert.Contains("matched", outcome.Observation!, StringComparison.Ordinal);
    }

    /// <summary>
    /// When the <c>conn::</c> key is absent from <c>Vars</c>, the step outcome must be
    /// <see cref="Verdict.EnvironmentError"/> (§12.1: infrastructure failure, never
    /// conflated with a test failure).  No topology needed — the error is detected before
    /// any network call.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_AbsentConnKey_ReturnsEnvironmentError()
    {
        var model = new CacheAssertElasticsearchModel(
            Target: "missing-dep",
            Index: IndexName,
            Query: null,
            Expect: new EsExpectation(Count: null, MinCount: 1, Fields: null));

        // Deliberately absent: no VarKeys.Connection("missing-dep") in Vars.
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);

        var outcome = await RunStepAsync(model, "es-env-error", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("error", outcome.Observation!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A field assertion where the first hit's <c>_source</c> field does not match the
    /// expected value yields <see cref="Verdict.Fail"/> with a field-mismatch observation.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_FieldAssertionMismatch_ReturnsFail()
    {
        await using var suite = await SuiteTopology.StartAsync(
            environment: BuildEnv(),
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var esUrl = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(esUrl));
        await SeedAsync(esUrl!);

        // We assert status == "cancelled" but the seeded doc has "fulfilled" or "pending".
        var model = new CacheAssertElasticsearchModel(
            Target: DepName,
            Index: IndexName,
            Query: "{\"query\":{\"term\":{\"status.keyword\":\"fulfilled\"}}}",
            Expect: new EsExpectation(
                Count: 1,
                MinCount: 1,
                Fields: new[] { new EsFieldAssertion("status", "cancelled") }));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = esUrl,
        };

        var outcome = await RunStepAsync(model, "es-field-fail", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("matched", outcome.Observation!, StringComparison.Ordinal);
    }
}
