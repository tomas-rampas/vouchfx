// S04-A-01 follow-up chunk Docker-gated proof: ElasticsearchScenarioIsolation deletes documents
// from an Elasticsearch dependency's indices between scenarios via _delete_by_query, while
// preserving the index mapping (pinning delete-by-query-not-index-delete, §17/§12.1
// semantics-preserving reset).
//
// Test proves:
//   • Topology built once via SuiteTopology.StartAsync (single elasticsearch dependency).
//   • An index with an explicit mapping (a 'status' keyword field) is created before any
//     scenario runs.
//   • Scenario: indexes two documents with ?refresh=true; asserts a match_all search returns
//     2 hits.
//   • ElasticsearchScenarioIsolation.EndScenarioAsync issues _delete_by_query — the search
//     must return 0 hits afterwards AND the index mapping must still be present (the index
//     itself was never deleted).
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~ElasticsearchResetProof"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
//
// Design notes (mirrors CacheAssertElasticsearchDockerTests.cs):
//   • Raw System.Net.Http.HttpClient throughout for setup/assertions — no typed ES client
//     exists in this engine's dependency set (§19); this test targets the isolation seam
//     itself, not the cache-assert provider pipeline, so no CSX compile/run is needed.
//   • ES stability env vars (discovery.type=single-node, xpack.security.enabled=false) are
//     applied by EnvironmentMapper so the container starts without TLS/auth.
//   • NOTE: Do NOT run this test locally or in CI unless Docker is available and the
//     elasticsearch image is reachable. The startup timeout is generous — ES is slower to
//     start than Redis or MongoDB, particularly on first pull.

using System.Text;
using System.Text.Json;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated proof that <see cref="ElasticsearchScenarioIsolation"/> deletes documents from
/// an Elasticsearch dependency's indices between scenarios while preserving index mappings.
/// </summary>
public sealed class ElasticsearchResetProofTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>
    /// Startup timeout: Elasticsearch is significantly slower than Redis/MongoDB — 180 s gives
    /// generous headroom including image pull on first run.
    /// </summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Logical name of the Elasticsearch dependency under test.</summary>
    private const string DepName = "resetsearch";

    /// <summary>Index name used by this test.</summary>
    private const string IndexName = "orders";

    public ElasticsearchResetProofTests(ITestOutputHelper output) => _output = output;

    private static EnvironmentSpec BuildEnv() =>
        new(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                [DepName] = new DependencySpec(Type: "elasticsearch", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>
    /// Creates the index with an explicit mapping (a 'status' keyword field), simulating a SUT
    /// that maps its indices once at startup — then blocks until the index's primary shard is
    /// active. Index creation returns before shard allocation completes; on a saturated host
    /// (e.g. a CI runner deep into a serialised docker suite) the first write can otherwise
    /// arrive while the shard is still INITIALISING and be rejected with a 503.
    /// </summary>
    private static async Task CreateIndexWithMappingAsync(string baseUrl)
    {
        var http = new HttpClient();
        try
        {
            var body = "{\"mappings\":{\"properties\":{\"status\":{\"type\":\"keyword\"}}}}";
            var response = await http.PutAsync(
                $"{baseUrl}/{IndexName}",
                new StringContent(body, Encoding.UTF8, "application/json")).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // Server-side wait: returns as soon as the index reaches yellow (primary active),
            // or after 60 s with "timed_out": true and a 408 — EnsureSuccessStatusCode then
            // fails the arrange phase with a precise signal instead of a downstream 503.
            var health = await http.GetAsync(
                $"{baseUrl}/_cluster/health/{IndexName}?wait_for_status=yellow&timeout=60s")
                .ConfigureAwait(false);
            health.EnsureSuccessStatusCode();
        }
        finally
        {
            http.Dispose();
        }
    }

    /// <summary>Indexes two documents, each with ?refresh=true for immediate visibility.</summary>
    private static async Task IndexDocumentsAsync(string baseUrl)
    {
        var http = new HttpClient();
        try
        {
            var doc1 = await http.PostAsync(
                $"{baseUrl}/{IndexName}/_doc?refresh=true",
                new StringContent(
                    "{\"status\":\"pending\"}", Encoding.UTF8, "application/json")).ConfigureAwait(false);
            doc1.EnsureSuccessStatusCode();

            var doc2 = await http.PostAsync(
                $"{baseUrl}/{IndexName}/_doc?refresh=true",
                new StringContent(
                    "{\"status\":\"shipped\"}", Encoding.UTF8, "application/json")).ConfigureAwait(false);
            doc2.EnsureSuccessStatusCode();
        }
        finally
        {
            http.Dispose();
        }
    }

    /// <summary>Returns the match_all hit count via a fresh HttpClient.</summary>
    private static async Task<long> CountHitsAsync(string baseUrl)
    {
        var http = new HttpClient();
        try
        {
            var response = await http.PostAsync(
                $"{baseUrl}/{IndexName}/_search",
                new StringContent(
                    "{\"query\":{\"match_all\":{}}}", Encoding.UTF8, "application/json")).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("hits").GetProperty("total").GetProperty("value").GetInt64();
        }
        finally
        {
            http.Dispose();
        }
    }

    /// <summary>Returns whether the explicit 'status' mapping is still present on the index.</summary>
    private static async Task<bool> MappingExistsAsync(string baseUrl)
    {
        var http = new HttpClient();
        try
        {
            var response = await http.GetAsync($"{baseUrl}/{IndexName}/_mapping").ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(IndexName, out var indexEl)
                && indexEl.TryGetProperty("mappings", out var mappingsEl)
                && mappingsEl.TryGetProperty("properties", out var propertiesEl)
                && propertiesEl.TryGetProperty("status", out _);
        }
        finally
        {
            http.Dispose();
        }
    }

    /// <summary>
    /// Core Elasticsearch reset-proof: <c>_delete_by_query</c> clears all documents while the
    /// index mapping survives (the index itself is never deleted).
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task ElasticsearchIsolation_DeletesDocuments_PreservesMapping()
    {
        var env = BuildEnv();

        // ── Build topology once ────────────────────────────────────────────────
        await using var suite = await SuiteTopology.StartAsync(
            environment: env,
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var esUrl = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(esUrl),
            $"DiscoveredServices['{DepName}'] must be a non-empty URL string.");
        var baseUrl = esUrl!.TrimEnd('/');
        // Deliberately NOT logging the endpoint URL — redacted for consistency with the
        // other proof tests, even though Elasticsearch security is disabled here (§17).
        _output.WriteLine("Elasticsearch endpoint discovered (redacted).");

        await CreateIndexWithMappingAsync(baseUrl);
        _output.WriteLine("Index created with an explicit mapping on 'status'.");

        await IndexDocumentsAsync(baseUrl);
        var count1 = await CountHitsAsync(baseUrl);
        _output.WriteLine($"After indexing: hits={count1}");
        Assert.Equal(2L, count1);
        Assert.True(await MappingExistsAsync(baseUrl), "Mapping must exist before the reset.");

        await using var isolation = new ElasticsearchScenarioIsolation(DepName, esUrl!);

        await isolation.BeginScenarioAsync(CancellationToken.None);
        await isolation.EndScenarioAsync(CancellationToken.None);
        _output.WriteLine("EndScenarioAsync completed — _delete_by_query removed all documents.");

        var countAfterReset = await CountHitsAsync(baseUrl);
        Assert.Equal(0L, countAfterReset);
        Assert.True(
            await MappingExistsAsync(baseUrl),
            "Index mapping must survive the reset (_delete_by_query, not an index delete).");

        _output.WriteLine(
            "Reset-proof PASS: documents deleted via _delete_by_query, mapping preserved.");
    }
}
