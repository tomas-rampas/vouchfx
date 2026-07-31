// SeedApplier `sql` seed generalisation — Docker-gated empirical proof for SQL Server.
//
// SeedApplierDockerTests.cs already proves the sql seed kind end-to-end against a
// real Postgres container. This file extends the SAME proof to SQL Server now that
// ApplyDependencyAsync dispatches on RelationalStoreKind instead of being
// Postgres-only.
//
// Beyond the happy path, this test EMPIRICALLY verifies the transactional-DDL claim
// documented in SeedApplier.cs (ApplyDependencyAsync): SQL Server supports fully
// transactional DDL, so a CREATE TABLE inside the per-file transaction is undone by
// a rollback exactly like any DML statement. Rather than assert this from
// documentation, the divergence probe below drives a real mixed DDL/DML failure
// against a live container and inspects the resulting state directly.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~SeedApplierSqlServerDocker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
//
// Design notes:
//   • Unlike SeedApplierDockerTests' BrokenSeedSql test (which lets StartAsync
//     itself throw and tear the topology down), this test needs the container to
//     survive the seed failure so its state can be inspected afterward. It starts
//     the topology WITHOUT a seed, then calls SeedApplier.ApplyAsync directly
//     (internal, InternalsVisibleTo) against the still-live discovered connection —
//     the same entry point SeedApplierDispatchTests exercises without Docker.
//   • NOTE: Do NOT run this test locally or in CI unless Docker is available and
//     mcr.microsoft.com/mssql/server:2022-latest is pre-pulled.

using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated proof that the <c>sql</c> seed kind works against a real SQL Server
/// dependency (sql seed generalisation beyond Postgres). Requires a running Docker
/// daemon with the SQL Server 2022 image available.
/// </summary>
public sealed class SeedApplierSqlServerDockerTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>Generous startup timeout: SQL Server is slower to start than Postgres.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(240);

    /// <summary>Logical name of the SQL Server dependency under test.</summary>
    private const string DepName = "orders-db";

    public SeedApplierSqlServerDockerTests(ITestOutputHelper output) => _output = output;

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
    /// Combines the ordering guarantee and the transactional-DDL divergence probe
    /// into ONE topology start, to minimise container overhead.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task SqlSeed_OrderedFiles_ThenBrokenFixture_DdlRollsBackWithTheWholeFile()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "vouchfx-seed-sqlserver-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(baseDir);
        try
        {
            // Arrange — three fixtures. Files 1+2 prove declared-order execution:
            // file 2's INSERT targets the table file 1 creates, so it can only
            // succeed if file 1's CREATE TABLE already committed first. File 3 is
            // the divergence probe: CREATE TABLE, then two INSERTs of the same
            // primary key — the second fails, forcing a rollback of the whole
            // file's transaction.
            await File.WriteAllTextAsync(
                Path.Combine(baseDir, "01-create.sql"),
                "CREATE TABLE order_probe (id INT PRIMARY KEY, note NVARCHAR(50) NOT NULL);");
            await File.WriteAllTextAsync(
                Path.Combine(baseDir, "02-insert.sql"),
                "INSERT INTO order_probe (id, note) VALUES (1, 'first');");
            await File.WriteAllTextAsync(
                Path.Combine(baseDir, "03-divergence.sql"),
                "CREATE TABLE divergence_probe (id INT PRIMARY KEY);" +
                "INSERT INTO divergence_probe (id) VALUES (1);" +
                "INSERT INTO divergence_probe (id) VALUES (1);");

            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                [DepName] = new DependencySeed(new List<string> { "01-create.sql", "02-insert.sql", "03-divergence.sql" }),
            });

            // Act — start the topology with NO seed so it survives what follows.
            await using var suite = await SuiteTopology.StartAsync(
                environment: BuildEnv(),
                appHostAssemblyName: AppHostAssemblyName,
                startupTimeout: StartupTimeout);

            var connStr = suite.DiscoveredServices[DepName] as string;
            Assert.False(string.IsNullOrWhiteSpace(connStr));

            var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
                SeedApplier.ApplyAsync(
                    seed,
                    discoveredServices: suite.DiscoveredServices,
                    dependencyTypes: new Dictionary<string, string>(StringComparer.Ordinal) { [DepName] = "sqlserver" },
                    seedBaseDirectory: baseDir,
                    brokerSink: null,
                    documentSink: null,
                    ct: CancellationToken.None));

            _output.WriteLine($"Kind: {ex.Info.Kind}, Resource: {ex.Info.ResourceName}, Detail: {ex.Info.Detail}");
            Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
            Assert.Contains("03-divergence.sql", ex.Info.Detail, StringComparison.Ordinal);

            // §12.1: an environment error, never a Fail.
            var evt = EnvironmentErrorEvents.Create(ex.Info, "run", DateTimeOffset.UnixEpoch);
            Assert.Equal(Verdict.EnvironmentError, evt.Verdict);
            Assert.NotEqual(Verdict.Fail, evt.Verdict);

            // Assert — ordering: files 1+2 committed BEFORE file 3 ran and failed.
            Assert.Equal("first", await ReadNoteAsync(connStr!));
            _output.WriteLine("Ordering proof: order_probe.note = 'first' — files 1+2 applied in declared order.");

            // Assert — SQL Server divergence probe: transactional DDL means the
            // WHOLE of file 3 (including its CREATE TABLE) rolled back.
            var divergenceTableExists = await TableExistsAsync(connStr!, "divergence_probe");
            _output.WriteLine($"Divergence probe: divergence_probe exists = {divergenceTableExists}");
            Assert.False(
                divergenceTableExists,
                "SQL Server supports transactional DDL: the rollback triggered by the " +
                "failed 2nd insert must undo the CREATE TABLE from the same file too.");
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    private static async Task<string?> ReadNoteAsync(string connStr)
    {
        var conn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
        await using (conn.ConfigureAwait(false))
        {
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = "SELECT note FROM order_probe WHERE id = 1";
                return (string?)await cmd.ExecuteScalarAsync();
            }
        }
    }

    private static async Task<bool> TableExistsAsync(string connStr, string tableName)
    {
        var conn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
        await using (conn.ConfigureAwait(false))
        {
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = @n";
                cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@n", tableName));
                var count = (int)(await cmd.ExecuteScalarAsync())!;
                return count > 0;
            }
        }
    }
}
