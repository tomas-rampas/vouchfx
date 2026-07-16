// S04-A-01 (generalised) Docker-gated proof: Respawn resets MySQL state between
// scenarios via RespawnRelationalIsolation.
//
// Test proves:
//   • Topology built once via SuiteTopology.StartAsync (single mysql dependency).
//   • Scenario 1: inserts a row into the 'orders' table, asserts COUNT == 1.
//   • RespawnRelationalIsolation.EndScenarioAsync resets the DB — proving the
//     defensive SchemasToInclude scoping (parsed from the connection string's
//     Database) correctly targets the seeded database's tables.
//   • Scenario 2: asserts COUNT == 0, then inserts again, resets again, and asserts
//     COUNT == 0 a second time — proving the checkpoint is RE-CREATED on every
//     EndScenarioAsync (not cached).
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~MySqlResetProof"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
//
// Design notes (mirrors RespawnResetProofTests.cs / DbAssertMysqlDockerTests.cs):
//   • Direct MySqlConnector connections are used for setup/assertions — this test
//     targets the isolation seam itself, not the db-assert provider pipeline, so no
//     CSX compile/run is needed.
//   • NOTE: Do NOT run this test locally or in CI unless Docker is available and the
//     mysql:9.7 image is pre-pulled (Aspire.Hosting.MySql 13.4.2 default).

using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated proof that <see cref="RespawnRelationalIsolation"/> (with
/// <see cref="RelationalStoreKind.MySql"/>) resets MySQL state between scenarios.
/// </summary>
public sealed class MySqlResetProofTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>Generous startup timeout: allows time for MySQL container image pull + init.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(240);

    /// <summary>Logical name of the MySQL dependency under test.</summary>
    private const string DepName = "resetdb";

    public MySqlResetProofTests(ITestOutputHelper output) => _output = output;

    private static EnvironmentSpec BuildEnv() =>
        new(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                [DepName] = new DependencySpec(Type: "mysql", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>Creates the 'orders' table (empty). Called once after the topology is healthy.</summary>
    private static async Task CreateSchemaAsync(string connStr)
    {
        var conn = new MySqlConnector.MySqlConnection(connStr);
        try
        {
            await conn.OpenAsync().ConfigureAwait(false);
            var cmd = conn.CreateCommand();
            try
            {
                cmd.CommandText =
                    "CREATE TABLE IF NOT EXISTS orders " +
                    "(id INT PRIMARY KEY, status VARCHAR(50) NOT NULL); " +
                    "DELETE FROM orders;";
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
    private static async Task InsertOrderAsync(string connStr)
    {
        var conn = new MySqlConnector.MySqlConnection(connStr);
        try
        {
            await conn.OpenAsync().ConfigureAwait(false);
            var cmd = conn.CreateCommand();
            try
            {
                cmd.CommandText = "INSERT INTO orders (id, status) VALUES (1, 'PENDING');";
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
    private static async Task<long> CountOrdersAsync(string connStr)
    {
        var conn = new MySqlConnector.MySqlConnection(connStr);
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

    /// <summary>
    /// Core MySQL reset-proof: checkpoint re-creation across two independent reset
    /// round-trips, scoped correctly via the connection string's parsed Database name.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task RespawnIsolation_ResetsOrdersTable_AcrossTwoRoundTrips()
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
        _output.WriteLine("MySQL connection string discovered (redacted).");

        await CreateSchemaAsync(connStr!);
        _output.WriteLine("Schema created: orders table, empty.");

        await using var isolation = new RespawnRelationalIsolation(DepName, RelationalStoreKind.MySql, connStr!);

        // ── ROUND 1 ──────────────────────────────────────────────────────────
        await isolation.BeginScenarioAsync(CancellationToken.None);

        await InsertOrderAsync(connStr!);
        var count1 = await CountOrdersAsync(connStr!);
        _output.WriteLine($"After insert 1: count={count1}");
        Assert.Equal(1L, count1);

        await isolation.EndScenarioAsync(CancellationToken.None);
        _output.WriteLine("Round 1: EndScenarioAsync completed — Respawn reset the orders table.");

        var countAfterReset1 = await CountOrdersAsync(connStr!);
        Assert.Equal(0L, countAfterReset1);

        // ── ROUND 2 — proves the checkpoint is RE-CREATED on every EndScenarioAsync ──
        await isolation.BeginScenarioAsync(CancellationToken.None);

        await InsertOrderAsync(connStr!);
        var count2 = await CountOrdersAsync(connStr!);
        Assert.Equal(1L, count2);

        await isolation.EndScenarioAsync(CancellationToken.None);
        _output.WriteLine("Round 2: EndScenarioAsync completed — Respawn reset the orders table again.");

        var countAfterReset2 = await CountOrdersAsync(connStr!);
        Assert.Equal(0L, countAfterReset2);

        _output.WriteLine(
            "Reset-proof PASS: orders table empty after two independent reset " +
            "round-trips — MySQL schema scoping and checkpoint re-creation confirmed.");
    }
}
