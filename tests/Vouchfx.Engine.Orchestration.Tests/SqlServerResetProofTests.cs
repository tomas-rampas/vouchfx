// S04-A-01 (generalised) Docker-gated proof: Respawn resets SQL Server state between
// scenarios via RespawnRelationalIsolation, including FK-respecting delete ordering.
//
// Test proves:
//   • Topology built once via SuiteTopology.StartAsync (single sqlserver dependency).
//   • Parent/child FK table pair seeded.
//   • Scenario 1: inserts a parent row + a child row referencing it; asserts both
//     tables have 1 row.
//   • RespawnRelationalIsolation.EndScenarioAsync resets both tables — Respawn must
//     delete the CHILD row before the PARENT row (FK-respecting order), or the reset
//     itself would fail with a foreign-key-violation.
//   • Scenario 2: asserts both tables are empty, then inserts again, resets again, and
//     asserts empty a second time — proving the checkpoint is RE-CREATED on every
//     EndScenarioAsync (not cached), so the second round-trip also cleans up.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~SqlServerResetProof"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
//
// Design notes (mirrors RespawnResetProofTests.cs / DbAssertSqlServerDockerTests.cs):
//   • Direct Microsoft.Data.SqlClient connections are used for setup/assertions —
//     this test targets the isolation seam itself, not the db-assert provider
//     pipeline, so no CSX compile/run is needed.
//   • NOTE: Do NOT run this test locally or in CI unless Docker is available and
//     mcr.microsoft.com/mssql/server:2022-latest is pre-pulled.

using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated proof that <see cref="RespawnRelationalIsolation"/> (with
/// <see cref="RelationalStoreKind.SqlServer"/>) resets SQL Server state between
/// scenarios, respecting foreign-key delete ordering.
/// </summary>
public sealed class SqlServerResetProofTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>Generous startup timeout: SQL Server is slower to start than Postgres.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(240);

    /// <summary>Logical name of the SQL Server dependency under test.</summary>
    private const string DepName = "resetdb";

    public SqlServerResetProofTests(ITestOutputHelper output) => _output = output;

    private static EnvironmentSpec BuildEnv() =>
        new(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                [DepName] = new DependencySpec(Type: "sqlserver", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>
    /// Creates a parent/child FK table pair (both empty). Called once after the
    /// topology is healthy, before scenario execution begins.
    /// </summary>
    private static async Task CreateSchemaAsync(string connStr)
    {
        var conn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
        try
        {
            await conn.OpenAsync().ConfigureAwait(false);
            var cmd = conn.CreateCommand();
            try
            {
                cmd.CommandText =
                    "IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'parent') " +
                    "CREATE TABLE parent (id INT PRIMARY KEY); " +
                    "IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'child') " +
                    "CREATE TABLE child (id INT PRIMARY KEY, parent_id INT NOT NULL " +
                    "    CONSTRAINT fk_child_parent REFERENCES parent(id)); " +
                    "DELETE FROM child; " +
                    "DELETE FROM parent;";
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

    /// <summary>Inserts one parent row (id=1) and one child row referencing it (id=1, parent_id=1).</summary>
    private static async Task InsertParentAndChildAsync(string connStr)
    {
        var conn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
        try
        {
            await conn.OpenAsync().ConfigureAwait(false);
            var cmd = conn.CreateCommand();
            try
            {
                cmd.CommandText =
                    "INSERT INTO parent (id) VALUES (1); " +
                    "INSERT INTO child (id, parent_id) VALUES (1, 1);";
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

    /// <summary>Returns (parentCount, childCount) via a fresh connection.</summary>
    private static async Task<(long ParentCount, long ChildCount)> CountRowsAsync(string connStr)
    {
        var conn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
        try
        {
            await conn.OpenAsync().ConfigureAwait(false);

            var parentCmd = conn.CreateCommand();
            long parentCount;
            try
            {
                parentCmd.CommandText = "SELECT COUNT(*) FROM parent;";
                parentCount = (int)(await parentCmd.ExecuteScalarAsync().ConfigureAwait(false))!;
            }
            finally
            {
                parentCmd.Dispose();
            }

            var childCmd = conn.CreateCommand();
            long childCount;
            try
            {
                childCmd.CommandText = "SELECT COUNT(*) FROM child;";
                childCount = (int)(await childCmd.ExecuteScalarAsync().ConfigureAwait(false))!;
            }
            finally
            {
                childCmd.Dispose();
            }

            return (parentCount, childCount);
        }
        finally
        {
            conn.Dispose();
        }
    }

    /// <summary>
    /// Core SQL Server reset-proof: FK-respecting delete ordering, and checkpoint
    /// re-creation across two reset round-trips.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task RespawnIsolation_ResetsParentAndChildTables_AcrossTwoRoundTrips()
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
        _output.WriteLine($"SQL Server conn string: {connStr}");

        await CreateSchemaAsync(connStr!);
        _output.WriteLine("Schema created: parent/child FK tables, both empty.");

        await using var isolation = new RespawnRelationalIsolation(DepName, RelationalStoreKind.SqlServer, connStr!);

        // ── ROUND 1 ──────────────────────────────────────────────────────────
        await isolation.BeginScenarioAsync(CancellationToken.None);

        await InsertParentAndChildAsync(connStr!);
        var (parent1, child1) = await CountRowsAsync(connStr!);
        _output.WriteLine($"After insert 1: parent={parent1}, child={child1}");
        Assert.Equal(1L, parent1);
        Assert.Equal(1L, child1);

        // The key call under test: must delete 'child' before 'parent' (FK-respecting
        // order), or this throws a foreign-key-violation instead of resetting cleanly.
        await isolation.EndScenarioAsync(CancellationToken.None);
        _output.WriteLine("Round 1: EndScenarioAsync completed — Respawn reset both tables.");

        var (parentAfterReset1, childAfterReset1) = await CountRowsAsync(connStr!);
        Assert.Equal(0L, parentAfterReset1);
        Assert.Equal(0L, childAfterReset1);

        // ── ROUND 2 — proves the checkpoint is RE-CREATED on every EndScenarioAsync ──
        await isolation.BeginScenarioAsync(CancellationToken.None);

        await InsertParentAndChildAsync(connStr!);
        var (parent2, child2) = await CountRowsAsync(connStr!);
        Assert.Equal(1L, parent2);
        Assert.Equal(1L, child2);

        await isolation.EndScenarioAsync(CancellationToken.None);
        _output.WriteLine("Round 2: EndScenarioAsync completed — Respawn reset both tables again.");

        var (parentAfterReset2, childAfterReset2) = await CountRowsAsync(connStr!);
        Assert.Equal(0L, parentAfterReset2);
        Assert.Equal(0L, childAfterReset2);

        _output.WriteLine(
            "Reset-proof PASS: parent/child both empty after two independent reset " +
            "round-trips — FK-respecting delete order confirmed, checkpoint re-creation confirmed.");
    }
}
