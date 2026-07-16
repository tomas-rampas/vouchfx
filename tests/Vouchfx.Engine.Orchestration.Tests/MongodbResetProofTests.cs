// S04-A-01 follow-up chunk Docker-gated proof: MongoScenarioIsolation deletes documents from
// a MongoDB dependency's target database between scenarios while preserving an explicit index
// (pinning DeleteMany-not-drop, §17/§12.1 semantics-preserving reset).
//
// Test proves:
//   • Topology built once via SuiteTopology.StartAsync (single mongodb dependency).
//   • An explicit index on 'status' is created before any scenario runs.
//   • Scenario 1: inserts two documents; asserts count == 2.
//   • MongoScenarioIsolation.EndScenarioAsync deletes all documents from the 'orders'
//     collection — the explicit index must still exist afterwards (DeleteMany, not
//     drop-and-recreate).
//   • Scenario 2: asserts count == 0, then inserts again, resets again, and asserts count == 0
//     a second time — proving the collection enumeration is re-derived on every
//     EndScenarioAsync (mirrors why RespawnRelationalIsolation re-creates its checkpoint).
//   • A view over 'orders' is also created before any scenario runs (review finding #2): the
//     reset must complete without throwing despite the view's presence, AND the view must still
//     exist afterwards — pinning that MongoScenarioIsolation SKIPS views (a view holds no data
//     of its own; DeleteMany against a view fails outright with CommandNotSupportedOnView).
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~MongodbResetProof"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
//
// Design notes (mirrors SqlServerResetProofTests.cs / MySqlResetProofTests.cs):
//   • Direct MongoDB.Driver connections are used for setup/assertions — this test targets the
//     isolation seam itself, not the db-assert provider pipeline, so no CSX compile/run is
//     needed.
//   • NOTE: Do NOT run this test locally or in CI unless Docker is available and mongo:7 is
//     pre-pulled (or Docker Hub is reachable).

using MongoDB.Bson;
using MongoDB.Driver;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated proof that <see cref="MongoScenarioIsolation"/> deletes documents from a
/// MongoDB dependency's target database between scenarios while preserving collections and
/// their indexes.
/// </summary>
public sealed class MongodbResetProofTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>
    /// Startup timeout for MongoDB. 120 s is generous for docker pull + container start of
    /// mongo:7 on CI (typically completes in 20-40 s when the image is cached).
    /// </summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Logical name of the MongoDB dependency under test.</summary>
    private const string DepName = "resetmongo";

    /// <summary>
    /// Database name as produced by EnvironmentMapper for dependency name "resetmongo":
    /// builder.AddMongoDB("resetmongo").AddDatabase("resetmongodb").
    /// </summary>
    private const string DbName = DepName + "db";

    /// <summary>Collection to insert documents into and reset.</summary>
    private const string CollectionName = "orders";

    /// <summary>Name of the explicit index created before any scenario runs.</summary>
    private const string IndexName = "status_idx";

    /// <summary>Name of the view over 'orders' created before any scenario runs (finding #2).</summary>
    private const string ViewName = "orders_view";

    public MongodbResetProofTests(ITestOutputHelper output) => _output = output;

    private static EnvironmentSpec BuildEnv() =>
        new(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                [DepName] = new DependencySpec(Type: "mongodb", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>
    /// Creates an explicit ascending index on 'status', simulating a SUT that indexes its
    /// collections once at startup.
    /// </summary>
    private static async Task CreateIndexAsync(string connStr)
    {
        var client = new MongoClient(connStr);
        try
        {
            var db = client.GetDatabase(DbName);
            var coll = db.GetCollection<BsonDocument>(CollectionName);
            var keys = Builders<BsonDocument>.IndexKeys.Ascending("status");
            var model = new CreateIndexModel<BsonDocument>(
                keys, new CreateIndexOptions { Name = IndexName });
            await coll.Indexes.CreateOneAsync(model).ConfigureAwait(false);
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>Inserts two documents into 'orders'.</summary>
    private static async Task InsertDocumentsAsync(string connStr)
    {
        var client = new MongoClient(connStr);
        try
        {
            var db = client.GetDatabase(DbName);
            var coll = db.GetCollection<BsonDocument>(CollectionName);
            await coll.InsertManyAsync(new[]
            {
                BsonDocument.Parse("{\"orderId\": 1, \"status\": \"PENDING\"}"),
                BsonDocument.Parse("{\"orderId\": 2, \"status\": \"SHIPPED\"}"),
            }).ConfigureAwait(false);
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>Counts the documents in 'orders' via a fresh client.</summary>
    private static async Task<long> CountDocumentsAsync(string connStr)
    {
        var client = new MongoClient(connStr);
        try
        {
            var db = client.GetDatabase(DbName);
            var coll = db.GetCollection<BsonDocument>(CollectionName);
            return await coll.CountDocumentsAsync(new BsonDocument()).ConfigureAwait(false);
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// Creates a view over 'orders' via the raw <c>create</c> command — the simplest possible
    /// view, avoiding the driver's generic pipeline-definition API. Pins review finding #2:
    /// <see cref="MongoScenarioIsolation"/> must SKIP views during reset (a view holds no data
    /// of its own; <c>DeleteMany</c> against a view fails outright).
    /// </summary>
    private static async Task CreateViewAsync(string connStr)
    {
        var client = new MongoClient(connStr);
        try
        {
            var db = client.GetDatabase(DbName);
            var command = new BsonDocument
            {
                { "create", ViewName },
                { "viewOn", CollectionName },
                { "pipeline", new BsonArray() },
            };
            await db.RunCommandAsync<BsonDocument>(command).ConfigureAwait(false);
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// Returns whether the view still exists (via <c>ListCollectionsAsync</c>, filtered to
    /// <c>type == "view"</c>) — proving the reset did not attempt (and fail) a DeleteMany
    /// against it, nor drop it.
    /// </summary>
    private static async Task<bool> ViewExistsAsync(string connStr)
    {
        var client = new MongoClient(connStr);
        try
        {
            var db = client.GetDatabase(DbName);

            var collectionInfos = new List<BsonDocument>();
            using (var cursor = await db.ListCollectionsAsync().ConfigureAwait(false))
            {
                while (await cursor.MoveNextAsync().ConfigureAwait(false))
                {
                    collectionInfos.AddRange(cursor.Current);
                }
            }

            return collectionInfos.Any(info =>
                info["name"].AsString == ViewName &&
                info.TryGetValue("type", out var type) &&
                type.AsString == "view");
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>Returns whether the explicit index still exists on 'orders'.</summary>
    private static async Task<bool> IndexExistsAsync(string connStr)
    {
        var client = new MongoClient(connStr);
        try
        {
            var db = client.GetDatabase(DbName);
            var coll = db.GetCollection<BsonDocument>(CollectionName);

            var indexes = new List<BsonDocument>();
            using (var cursor = await coll.Indexes.ListAsync().ConfigureAwait(false))
            {
                while (await cursor.MoveNextAsync().ConfigureAwait(false))
                {
                    indexes.AddRange(cursor.Current);
                }
            }

            return indexes.Any(ix => ix["name"].AsString == IndexName);
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// Core MongoDB reset-proof: documents are deleted, the explicit index survives, across two
    /// independent reset round-trips.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MongoIsolation_DeletesDocuments_PreservesIndex_AcrossTwoRoundTrips()
    {
        var env = BuildEnv();

        // ── Build topology once ────────────────────────────────────────────────
        await using var suite = await SuiteTopology.StartAsync(
            environment: env,
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr),
            $"DiscoveredServices['{DepName}'] must be a non-empty connection string.");
        // Deliberately NOT logging the connection string — Aspire-provisioned strings
        // carry credentials, and test output lands in CI logs/artifacts (§17).
        _output.WriteLine("Mongo connection string discovered (redacted).");

        await CreateIndexAsync(connStr!);
        _output.WriteLine("Explicit index created on 'status'.");

        await CreateViewAsync(connStr!);
        _output.WriteLine("View 'orders_view' created over 'orders' (review finding #2).");

        await using var isolation = new MongoScenarioIsolation(DepName, connStr!);

        // ── ROUND 1 ──────────────────────────────────────────────────────────
        await isolation.BeginScenarioAsync(CancellationToken.None);

        await InsertDocumentsAsync(connStr!);
        var count1 = await CountDocumentsAsync(connStr!);
        _output.WriteLine($"After insert 1: count={count1}");
        Assert.Equal(2L, count1);

        await isolation.EndScenarioAsync(CancellationToken.None);
        _output.WriteLine("Round 1: EndScenarioAsync completed — documents deleted.");

        var countAfterReset1 = await CountDocumentsAsync(connStr!);
        Assert.Equal(0L, countAfterReset1);
        Assert.True(
            await IndexExistsAsync(connStr!),
            "Explicit index must survive the reset (DeleteMany, not drop).");
        Assert.True(
            await ViewExistsAsync(connStr!),
            "The view must survive the reset — EndScenarioAsync must SKIP it, not attempt " +
            "(and fail) a DeleteMany against it (review finding #2).");

        // ── ROUND 2 — proves the collection enumeration is RE-DERIVED on every call ──
        await isolation.BeginScenarioAsync(CancellationToken.None);

        await InsertDocumentsAsync(connStr!);
        var count2 = await CountDocumentsAsync(connStr!);
        Assert.Equal(2L, count2);

        await isolation.EndScenarioAsync(CancellationToken.None);
        _output.WriteLine("Round 2: EndScenarioAsync completed — documents deleted again.");

        var countAfterReset2 = await CountDocumentsAsync(connStr!);
        Assert.Equal(0L, countAfterReset2);
        Assert.True(await IndexExistsAsync(connStr!));
        Assert.True(await ViewExistsAsync(connStr!));

        _output.WriteLine(
            "Reset-proof PASS: documents deleted, explicit index preserved, and the view " +
            "(never DeleteMany'd nor dropped) survived, across two independent reset " +
            "round-trips.");
    }
}
