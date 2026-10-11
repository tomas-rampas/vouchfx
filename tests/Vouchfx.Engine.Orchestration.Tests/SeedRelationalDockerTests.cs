using System.Data.Common;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

public sealed class SeedRelationalDockerTests
{
    [Theory]
    [Trait("requires", "docker")]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public async Task ReseedAsync_TwoSequentialScenarios_NonIdempotentFixtureRestoresBaseline(string kind)
    {
        var baseDir = NewDirectory();
        var dependency = "seed" + kind + Guid.NewGuid().ToString("n")[..8];
        try
        {
            await File.WriteAllTextAsync(Path.Combine(baseDir, "reference.sql"),
                "CREATE TABLE seed_parent (id INT PRIMARY KEY);" +
                "CREATE TABLE seed_child (id INT PRIMARY KEY, parent_id INT REFERENCES seed_parent(id));" +
                "INSERT INTO seed_parent (id) VALUES (1);" +
                "INSERT INTO seed_child (id, parent_id) VALUES (1, 1);");
            await File.WriteAllTextAsync(Path.Combine(baseDir, "view.sql"),
                "CREATE VIEW seed_view AS SELECT id FROM seed_parent;");
            var files = new List<string> { "reference.sql", "view.sql" };
            if (kind == "sqlserver")
            {
                await File.WriteAllTextAsync(Path.Combine(baseDir, "function.sql"),
                    "CREATE FUNCTION dbo.seed_value() RETURNS INT WITH SCHEMABINDING AS BEGIN RETURN (SELECT COUNT(*) FROM dbo.seed_parent); END;");
                await File.WriteAllTextAsync(Path.Combine(baseDir, "objects.sql"),
                    "CREATE SEQUENCE seed_sequence START WITH 1;" +
                    "CREATE SYNONYM seed_alias FOR dbo.seed_parent;" +
                    "CREATE TYPE seed_label FROM VARCHAR(50) NOT NULL;");
                files.Add("function.sql");
                files.Add("objects.sql");
                // The alias must be committed before the next batch compiles its use.
                await File.WriteAllTextAsync(Path.Combine(baseDir, "tabletype.sql"),
                    "CREATE TYPE seed_table_type AS TABLE (id INT NOT NULL PRIMARY KEY, label dbo.seed_label NOT NULL);");
                files.Add("tabletype.sql");
            }

            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                [dependency] = new DependencySeed(files),
            });
            await using var suite = await StartAsync(kind, dependency, seed, baseDir);
            var connectionString = (string)suite.DiscoveredServices[dependency];
            var mysqlDefinition = kind == "mysql"
                ? await MySqlDefinitionAsync(connectionString, changeOptions: true)
                : null;
            await using var isolation = new RespawnRelationalIsolation(
                dependency, ScenarioIsolationFactory.MapRelationalKind(kind)!.Value, connectionString);

            // Both scenario invocations use the same healthy SuiteTopology and real database.
            Assert.Equal(1L, await CountAsync(kind, connectionString));
            if (kind == "sqlserver")
                await AssertSqlServerTypeDependenciesAsync(connectionString);
            await isolation.EndScenarioAsync(CancellationToken.None);
            Assert.Equal(0L, await CountAsync(kind, connectionString));
            await suite.ReseedAsync(CancellationToken.None);
            Assert.Equal(1L, await CountAsync(kind, connectionString));
            if (kind == "sqlserver")
                await AssertSqlServerTypeDependenciesAsync(connectionString);
            if (mysqlDefinition is not null)
                Assert.Equal(mysqlDefinition, await MySqlDefinitionAsync(connectionString, changeOptions: false));
            await isolation.EndScenarioAsync(CancellationToken.None);
            await suite.ReseedAsync(CancellationToken.None);
            Assert.Equal(1L, await CountAsync(kind, connectionString));
            if (kind == "sqlserver")
                await AssertSqlServerTypeDependenciesAsync(connectionString);

            // An already-cancelled reset must not remove the current baseline.
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => suite.ReseedAsync(cancellation.Token));
            Assert.Equal(1L, await CountAsync(kind, connectionString));
            if (kind == "sqlserver")
                await AssertSqlServerTypeDependenciesAsync(connectionString);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Theory]
    [Trait("requires", "docker")]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public async Task SqlSeed_DriverEchoesFixtureCanary_ReportDetailWithholdsIt(string kind)
    {
        var baseDir = NewDirectory();
        var dependency = "seed" + kind + Guid.NewGuid().ToString("n")[..8];
        try
        {
            var canary = "seedprivate" + Guid.NewGuid().ToString("n");
            await File.WriteAllTextAsync(Path.Combine(baseDir, "broken.sql"), "SELECT * FROM " + canary + ";");
            await using var suite = await StartAsync(kind, dependency, seed: null, baseDir);
            var error = await Assert.ThrowsAsync<OrchestrationException>(() => SeedApplier.ApplyAsync(
                Seed(dependency, "broken.sql"), suite.DiscoveredServices,
                new Dictionary<string, string>(StringComparer.Ordinal) { [dependency] = kind },
                baseDir, pathDisclosures: null, CancellationToken.None));

            Assert.Equal(OrchestrationErrorKind.Provision, error.Info.Kind);
            var driver = Assert.IsAssignableFrom<DbException>(error.InnerException);
            // Boolean assertions intentionally keep the canary and raw driver text out of
            // test failures. This proves a real echoing driver, separately from path refusal.
            Assert.True(driver.Message.Contains(canary, StringComparison.Ordinal), "The driver control must actually echo the fixture canary.");
            Assert.True(error.Info.Detail?.Contains(canary, StringComparison.Ordinal) == false, "Seed report detail must withhold the fixture canary.");
            Assert.Contains("broken.sql", error.Info.Detail, StringComparison.Ordinal);
            Assert.Contains(dependency, error.Info.Detail, StringComparison.Ordinal);
            Assert.Contains(driver.GetType().Name, error.Info.Detail, StringComparison.Ordinal);
            var report = EnvironmentErrorEvents.Create(error.Info, "run", DateTimeOffset.UnixEpoch);
            Assert.True(report.Detail?.Contains(canary, StringComparison.Ordinal) == false, "The environment-error event must withhold the fixture canary.");
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "vouchfx-seed-relational-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static SeedSpec Seed(string dependency, string file) => new(
        new Dictionary<string, DependencySeed>(StringComparer.Ordinal) { [dependency] = new DependencySeed(new[] { file }) });

    private static Task<SuiteTopology> StartAsync(string kind, string dependency, SeedSpec? seed, string baseDir) =>
        SuiteTopology.StartAsync(
            new EnvironmentSpec(Services: null,
                Dependencies: new Dictionary<string, DependencySpec>(StringComparer.Ordinal)
                {
                    [dependency] = new DependencySpec(Type: kind, Version: null, Extra: null),
                },
                Seed: seed, ImageRegistry: null, ImagePullPolicy: null),
            appHostAssemblyName: "Vouchfx.Engine.Orchestration.Tests",
            startupTimeout: TimeSpan.FromSeconds(240), seedBaseDirectory: baseDir);

    private static async Task<long> CountAsync(string kind, string connectionString)
    {
        await using DbConnection connection = kind switch
        {
            "postgres" => new Npgsql.NpgsqlConnection(connectionString),
            "sqlserver" => new Microsoft.Data.SqlClient.SqlConnection(connectionString),
            "mysql" => new MySqlConnector.MySqlConnection(connectionString),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM seed_child";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task AssertSqlServerTypeDependenciesAsync(string connectionString)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.table_types tt " +
                              "JOIN sys.columns c ON c.object_id = tt.type_table_object_id " +
                              "JOIN sys.types alias ON alias.user_type_id = c.user_type_id " +
                              "WHERE SCHEMA_NAME(tt.schema_id) = 'dbo' AND tt.name = 'seed_table_type' " +
                              "AND c.name = 'label' AND SCHEMA_NAME(alias.schema_id) = 'dbo' " +
                              "AND alias.name = 'seed_label' AND alias.is_table_type = 0;";
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        command.CommandText = "SELECT COUNT(*) FROM sys.types WHERE is_user_defined = 1;";
        Assert.Equal(2, Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<string> MySqlDefinitionAsync(string connectionString, bool changeOptions)
    {
        await using var connection = new MySqlConnector.MySqlConnection(connectionString);
        await connection.OpenAsync();
        var identifier = "`" + connection.Database.Replace("`", "``", StringComparison.Ordinal) + "`";
        await using var command = connection.CreateCommand();
        if (changeOptions)
        {
            // Change the database, not the seed fixture: reapplying the fixture must not be
            // able to hide a reset that silently reverted these character-set options.
            command.CommandText = "ALTER DATABASE " + identifier + " CHARACTER SET latin1 COLLATE latin1_bin";
            await command.ExecuteNonQueryAsync();
        }

        command.CommandText = "SHOW CREATE DATABASE " + identifier;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetString(1);
    }
}
