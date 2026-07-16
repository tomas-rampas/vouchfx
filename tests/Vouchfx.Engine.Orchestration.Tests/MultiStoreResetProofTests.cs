// S04-A-01 follow-up chunk Docker-gated proof: the REAL end-to-end seam
// (SuiteTopology.StartAsync + ScenarioIsolationFactory.Create) resets EVERY resettable
// dependency of a mixed-store topology through ONE EndScenarioAsync call on the returned
// composite isolation — proving the factory/composite dispatch chunk 1 established now covers
// a relational store AND a document/cache store together, not just relational-on-relational.
//
// Test proves:
//   • A single topology declares BOTH a postgres AND a redis dependency.
//   • ScenarioIsolationFactory.Create (given the topology's real DependencyNames /
//     DependencyTypes / DiscoveredServices) yields a CompositeScenarioIsolation with exactly
//     two children.
//   • A row is written to postgres AND a key is set in redis.
//   • ONE EndScenarioAsync call on the composite resets BOTH stores.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~MultiStoreResetProof"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
//
// Design notes:
//   • Postgres + Redis are the two cheapest stores to start together (no five-store docker
//     topology here — a wider composite is proven WITHOUT Docker in
//     ScenarioIsolationFactoryTests.FiveStoreTopology_ReturnsCompositeWithFiveChildrenInDeclarationOrder,
//     avoiding the DCP per-resource-startup-watchdog flake risk a five-container topology would
//     add here).
//   • Direct Npgsql / StackExchange.Redis connections are used for setup/assertions — this test
//     targets the isolation seam itself, not any step-provider pipeline, so no CSX compile/run
//     is needed.
//   • A no-docker composite-failure case (a composite whose second child throws
//     OrchestrationException surfaces the failing child's ResourceName) is ALREADY covered by
//     CompositeScenarioIsolationTests.EndScenarioAsync_MiddleChildThrows_EarlierRanLaterDidNot
//     (three recording fakes, the middle/second one throwing) — not duplicated here.
//   • NOTE: Do NOT run this test locally or in CI unless Docker is available and the
//     postgres/redis images are pre-pulled (or Docker Hub is reachable).

using Npgsql;
using StackExchange.Redis;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated proof that <see cref="ScenarioIsolationFactory.Create"/>, driven by a REAL
/// <see cref="SuiteTopology"/>, resets every resettable dependency of a mixed relational +
/// document/cache-store topology through one <see cref="IScenarioIsolation.EndScenarioAsync"/>
/// call.
/// </summary>
public sealed class MultiStoreResetProofTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>Generous startup timeout for a two-dependency topology.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Logical name of the Postgres dependency under test.</summary>
    private const string PostgresDepName = "resetpg";

    /// <summary>Logical name of the Redis dependency under test.</summary>
    private const string RedisDepName = "resetcache";

    public MultiStoreResetProofTests(ITestOutputHelper output) => _output = output;

    private static EnvironmentSpec BuildEnv() =>
        new(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                [PostgresDepName] = new DependencySpec(Type: "postgres", Version: null, Extra: null),
                [RedisDepName] = new DependencySpec(Type: "redis", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>Creates the 'orders' table (empty).</summary>
    private static async Task CreatePostgresSchemaAsync(string connStr)
    {
        var conn = new NpgsqlConnection(connStr);
        try
        {
            await conn.OpenAsync().ConfigureAwait(false);
            var cmd = conn.CreateCommand();
            try
            {
                cmd.CommandText =
                    "CREATE TABLE IF NOT EXISTS orders (id INT PRIMARY KEY); DELETE FROM orders;";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally
            {
                cmd.Dispose();
            }
        }
        finally
        {
            conn.Dispose();
        }
    }

    /// <summary>Inserts one row (id=1) into 'orders'.</summary>
    private static async Task InsertPostgresRowAsync(string connStr)
    {
        var conn = new NpgsqlConnection(connStr);
        try
        {
            await conn.OpenAsync().ConfigureAwait(false);
            var cmd = conn.CreateCommand();
            try
            {
                cmd.CommandText = "INSERT INTO orders (id) VALUES (1);";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            finally
            {
                cmd.Dispose();
            }
        }
        finally
        {
            conn.Dispose();
        }
    }

    /// <summary>Counts the rows in 'orders' via a fresh connection.</summary>
    private static async Task<long> CountPostgresRowsAsync(string connStr)
    {
        var conn = new NpgsqlConnection(connStr);
        try
        {
            await conn.OpenAsync().ConfigureAwait(false);
            var cmd = conn.CreateCommand();
            try
            {
                cmd.CommandText = "SELECT COUNT(*) FROM orders;";
                return (long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))!;
            }
            finally
            {
                cmd.Dispose();
            }
        }
        finally
        {
            conn.Dispose();
        }
    }

    /// <summary>Sets one string key.</summary>
    private static async Task SetRedisKeyAsync(string connStr)
    {
        var mux = await ConnectionMultiplexer.ConnectAsync(connStr).ConfigureAwait(false);
        try
        {
            await mux.GetDatabase().StringSetAsync("k1", "v1").ConfigureAwait(false);
        }
        finally
        {
            mux.Dispose();
        }
    }

    /// <summary>
    /// Returns DBSIZE for the database the connection string designates
    /// (<c>DefaultDatabase ?? 0</c> — the same index the resetter flushes), via a
    /// fresh multiplexer.
    /// </summary>
    private static async Task<long> CountRedisKeysAsync(string connStr)
    {
        var options = ConfigurationOptions.Parse(connStr);
        var databaseIndex = options.DefaultDatabase ?? 0;
        var mux = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
        try
        {
            var server = mux.GetServer(mux.GetEndPoints()[0]);
            return await server.DatabaseSizeAsync(databaseIndex).ConfigureAwait(false);
        }
        finally
        {
            mux.Dispose();
        }
    }

    /// <summary>
    /// Core multi-store reset-proof: the factory (driven by a real topology) resets BOTH
    /// postgres and redis through a single composite <c>EndScenarioAsync</c> call.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task CompositeIsolation_ResetsPostgresAndRedis_ThroughRealFactorySeam()
    {
        var env = BuildEnv();

        // ── Build topology once ────────────────────────────────────────────────
        await using var suite = await SuiteTopology.StartAsync(
            environment: env,
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var pgConnStr = suite.DiscoveredServices[PostgresDepName] as string;
        var redisConnStr = suite.DiscoveredServices[RedisDepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(pgConnStr),
            $"DiscoveredServices['{PostgresDepName}'] must be a non-empty connection string.");
        Assert.False(string.IsNullOrWhiteSpace(redisConnStr),
            $"DiscoveredServices['{RedisDepName}'] must be a non-empty connection string.");

        await CreatePostgresSchemaAsync(pgConnStr!);
        _output.WriteLine("Postgres schema created: orders table, empty.");

        // ── The REAL seam: build the isolation through the factory, from the real topology ──
        var isolation = ScenarioIsolationFactory.Create(
            suite.DependencyNames, suite.DependencyTypes, suite.DiscoveredServices);

        var composite = Assert.IsType<CompositeScenarioIsolation>(isolation);
        Assert.Equal(2, composite.Children.Count);
        _output.WriteLine("Factory yielded a CompositeScenarioIsolation with 2 children (postgres + redis).");

        await using var _ = composite;

        await composite.BeginScenarioAsync(CancellationToken.None);

        await InsertPostgresRowAsync(pgConnStr!);
        await SetRedisKeyAsync(redisConnStr!);

        Assert.Equal(1L, await CountPostgresRowsAsync(pgConnStr!));
        Assert.Equal(1L, await CountRedisKeysAsync(redisConnStr!));
        _output.WriteLine("Both stores populated: 1 postgres row, 1 redis key.");

        // ONE EndScenarioAsync call resets BOTH stores.
        await composite.EndScenarioAsync(CancellationToken.None);

        Assert.Equal(0L, await CountPostgresRowsAsync(pgConnStr!));
        Assert.Equal(0L, await CountRedisKeysAsync(redisConnStr!));

        _output.WriteLine(
            "Reset-proof PASS: ONE composite EndScenarioAsync call reset BOTH postgres and " +
            "redis, driven entirely through the real SuiteTopology + ScenarioIsolationFactory seam.");
    }
}
