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
        _output.WriteLine($"Mongo conn string: {connStr}");

        await CreateIndexAsync(connStr!);
        _output.WriteLine("Explicit index created on 'status'.");

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

        _output.WriteLine(
            "Reset-proof PASS: documents deleted and explicit index preserved across two " +
            "independent reset round-trips.");
    }
}
