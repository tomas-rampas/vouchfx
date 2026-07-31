// SeedApplier `sql` seed generalisation — Docker-gated empirical proof for MySQL.
//
// Mirrors SeedApplierSqlServerDockerTests.cs. Proves the sql seed kind works
// end-to-end against a real MySQL container now that ApplyDependencyAsync
// dispatches on RelationalStoreKind instead of being Postgres-only.
//
// This is the important one: it EMPIRICALLY proves the divergence documented in
// SeedApplier.cs (ApplyDependencyAsync) — MySQL implicitly commits DDL statements
// (verified against the MySQL reference manual's "Statements That Cause an
// Implicit Commit") and cannot roll them back, unlike Postgres and SQL Server. The
// probe drives a real mixed DDL/DML failure against a live container and asserts
// the resulting state directly, rather than trusting the documentation comment.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~SeedApplierMysqlDocker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
//
// Design notes:
//   • As with the SQL Server variant, the topology is started WITHOUT a seed so it
//     survives the deliberately-broken seed call, then SeedApplier.ApplyAsync is
//     invoked directly (internal, InternalsVisibleTo) so the container's resulting
//     state can be inspected afterward.
//   • NOTE: Do NOT run this test locally or in CI unless Docker is available and
//     the mysql:9.7 image is pre-pulled (Aspire.Hosting.MySql 13.4.2 default).

using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated proof that the <c>sql</c> seed kind works against a real MySQL
/// dependency (sql seed generalisation beyond Postgres). Requires a running Docker
/// daemon with the MySQL image available.
/// </summary>
public sealed class SeedApplierMysqlDockerTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>Generous startup timeout: allows time for MySQL container image pull + init.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(240);

    /// <summary>Logical name of the MySQL dependency under test.</summary>
    private const string DepName = "orders-db";

    public SeedApplierMysqlDockerTests(ITestOutputHelper output) => _output = output;

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

    /// <summary>
    /// Combines the ordering guarantee and the implicit-commit divergence probe
    /// into ONE topology start, to minimise container overhead.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task SqlSeed_OrderedFiles_ThenBrokenFixture_DdlSurvivesTheRollback()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "vouchfx-seed-mysql-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(baseDir);
        try
        {
            // Arrange — identical fixture set to the SQL Server test (same SQL is
            // valid on both dialects), so the ONLY variable between the two test
            // runs is the driver/engine — an apples-to-apples comparison.
            await File.WriteAllTextAsync(
                Path.Combine(baseDir, "01-create.sql"),
                "CREATE TABLE order_probe (id INT PRIMARY KEY, note VARCHAR(50) NOT NULL);");
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
                    dependencyTypes: new Dictionary<string, string>(StringComparer.Ordinal) { [DepName] = "mysql" },
                    seedBaseDirectory: baseDir,
                    brokerSink: null,
                    documentSink: null,
                    ct: CancellationToken.None));

            _output.WriteLine($"Kind: {ex.Info.Kind}, Resource: {ex.Info.ResourceName}, Detail: {ex.Info.Detail}");
            Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
            Assert.Contains("03-divergence.sql", ex.Info.Detail, StringComparison.Ordinal);

            // §12.1: an environment error, never a Fail — MATCHES SQL Server/Postgres
            // even though the underlying driver behaviour below diverges.
            var evt = EnvironmentErrorEvents.Create(ex.Info, "run", DateTimeOffset.UnixEpoch);
            Assert.Equal(Verdict.EnvironmentError, evt.Verdict);
            Assert.NotEqual(Verdict.Fail, evt.Verdict);

            // Assert — ordering: files 1+2 committed BEFORE file 3 ran and failed.
            Assert.Equal("first", await ReadNoteAsync(connStr!));
            _output.WriteLine("Ordering proof: order_probe.note = 'first' — files 1+2 applied in declared order.");

            // Assert — the MySQL divergence: unlike SQL Server, MySQL implicitly
            // commits a DDL statement the moment it runs, so the CREATE TABLE in
            // file 3 SURVIVES even though the file as a whole reports failure.
            var divergenceTableExists = await TableExistsAsync(connStr!, "divergence_probe");
            _output.WriteLine($"Divergence probe: divergence_probe exists = {divergenceTableExists}");
            Assert.True(
                divergenceTableExists,
                "MySQL implicitly commits DDL statements (verified against the MySQL " +
                "reference manual): CREATE TABLE cannot be rolled back, so it survives " +
                "even though the file's later INSERT failed and the seed reports Provision.");

            // The DML in the same file DOES roll back — MySQL implicitly starts a
            // fresh transaction bracket after the DDL's implicit commit, and that
            // fresh transaction (wrapping the successful first insert) is the one
            // our rollback-on-dispose undoes.
            var rowCount = await CountDivergenceRowsAsync(connStr!);
            _output.WriteLine($"Divergence probe: divergence_probe row count = {rowCount}");
            Assert.Equal(0, rowCount);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    private static async Task<string?> ReadNoteAsync(string connStr)
    {
        var conn = new MySqlConnector.MySqlConnection(connStr);
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
        var conn = new MySqlConnector.MySqlConnection(connStr);
        await using (conn.ConfigureAwait(false))
        {
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText =
                    "SELECT COUNT(*) FROM information_schema.tables " +
                    "WHERE table_schema = DATABASE() AND table_name = @n";
                cmd.Parameters.Add(new MySqlConnector.MySqlParameter("@n", tableName));
                var count = (long)(await cmd.ExecuteScalarAsync())!;
                return count > 0;
            }
        }
    }

    private static async Task<long> CountDivergenceRowsAsync(string connStr)
    {
        var conn = new MySqlConnector.MySqlConnection(connStr);
        await using (conn.ConfigureAwait(false))
        {
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = "SELECT COUNT(*) FROM divergence_probe";
                return (long)(await cmd.ExecuteScalarAsync())!;
            }
        }
    }
}
