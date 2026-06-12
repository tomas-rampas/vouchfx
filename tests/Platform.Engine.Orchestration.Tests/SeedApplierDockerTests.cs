// S05-A-01 — SeedApplier Docker-gated end-to-end tests.
//
// Proves that declarative environment.seed SQL is applied AFTER the topology is
// healthy and BEFORE the first step, inside the same health-gated lifecycle:
//   • Happy path: a real Postgres dependency + a valid seed SQL file → a seeded
//     reference row is queryable immediately after SuiteTopology.StartAsync
//     returns (i.e. before any step runs).
//   • Broken seed: a SQL file with a syntax error → SuiteTopology.StartAsync
//     throws OrchestrationException (Provision kind) and the topology is disposed
//     (Environment error, §12.1 — never a Fail).
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~SeedApplierDocker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
//
// Design notes:
//   • The test assembly carries <IsAspireHost>true</IsAspireHost> + Aspire.AppHost.Sdk,
//     which embed the dcpclipath metadata SuiteTopology.StartAsync requires.
//   • SQL fixtures are written to a temp directory passed as seedBaseDirectory, so
//     the test owns the files and cleans them up.

using Platform.Engine.Abstractions;
using Platform.Engine.Authoring.Model;
using Platform.Engine.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace Platform.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end tests for <see cref="SeedApplier"/> wired into
/// <see cref="SuiteTopology.StartAsync"/> (S05-A-01).  Requires a running Docker
/// daemon.
/// </summary>
public sealed class SeedApplierDockerTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Platform.Engine.Orchestration.Tests";

    /// <summary>Generous startup timeout: allows image pulls on first run.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Logical name of the Postgres dependency under test.</summary>
    private const string DepName = "orders-db";

    public SeedApplierDockerTests(ITestOutputHelper output) => _output = output;

    private static EnvironmentSpec BuildEnv(SeedSpec? seed) =>
        new(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                [DepName] = new DependencySpec(Type: "postgres", Version: null, Extra: null),
            },
            Seed: seed,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>
    /// Happy path: a valid seed SQL file is applied before the first step, and the
    /// seeded reference row is queryable as soon as <see cref="SuiteTopology.StartAsync"/>
    /// returns.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task ValidSeed_AppliedBeforeFirstStep_RowIsQueryable()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "vouchfx-seed-ok-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(baseDir);
        try
        {
            // Arrange — a seed SQL fixture that creates a reference table and inserts a row.
            var sqlPath = Path.Combine(baseDir, "reference.sql");
            await File.WriteAllTextAsync(
                sqlPath,
                "CREATE TABLE countries (code TEXT PRIMARY KEY, name TEXT NOT NULL);" +
                "INSERT INTO countries (code, name) VALUES ('GB', 'United Kingdom');");

            var sqlFiles = new List<string> { "reference.sql" };
            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                [DepName] = new DependencySeed(sqlFiles),
            });

            // Act — seed runs INSIDE StartAsync, before this method gets control back.
            await using var suite = await SuiteTopology.StartAsync(
                environment: BuildEnv(seed),
                appHostAssemblyName: AppHostAssemblyName,
                startupTimeout: StartupTimeout,
                seedBaseDirectory: baseDir);

            var connStr = suite.DiscoveredServices[DepName] as string;
            Assert.False(string.IsNullOrWhiteSpace(connStr));

            // Assert — the seeded row is present (proving seed ran before step 1).
            var conn = new Npgsql.NpgsqlConnection(connStr);
            await using (conn.ConfigureAwait(false))
            {
                await conn.OpenAsync();
                var cmd = conn.CreateCommand();
                await using (cmd.ConfigureAwait(false))
                {
                    cmd.CommandText = "SELECT name FROM countries WHERE code = 'GB'";
                    var name = (string?)await cmd.ExecuteScalarAsync();
                    _output.WriteLine($"Seeded row: GB → {name}");
                    Assert.Equal("United Kingdom", name);
                }
            }
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    /// <summary>
    /// Watch-mode re-seed proof (S08-T10, B2): the reuse path resets the kept topology
    /// (Respawn truncates user tables — INCLUDING the seed rows) and then RE-APPLIES the seed
    /// via <see cref="SuiteTopology.ReseedAsync"/>, so a reuse run sees the SAME seeded baseline
    /// a fresh <c>vouchfx run</c> would.  This pins the load-bearing half of B2 against a real
    /// Postgres container: build+seed → seed present → reset (seed gone) → reseed → seed back.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task ReseedAsync_AfterRespawnReset_RestoresTheSeededBaseline()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "vouchfx-reseed-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(baseDir);
        try
        {
            // Arrange — a seed SQL fixture: a reference table with one seeded row.
            await File.WriteAllTextAsync(
                Path.Combine(baseDir, "reference.sql"),
                "CREATE TABLE countries (code TEXT PRIMARY KEY, name TEXT NOT NULL);" +
                "INSERT INTO countries (code, name) VALUES ('GB', 'United Kingdom');");

            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                [DepName] = new DependencySeed(new List<string> { "reference.sql" }),
            });

            // ── BUILD path: StartAsync applies the seed once (the first watch run's baseline).
            await using var suite = await SuiteTopology.StartAsync(
                environment: BuildEnv(seed),
                appHostAssemblyName: AppHostAssemblyName,
                startupTimeout: StartupTimeout,
                seedBaseDirectory: baseDir);

            var connStr = suite.DiscoveredServices[DepName] as string;
            Assert.False(string.IsNullOrWhiteSpace(connStr));

            // The first watch run (build path) sees the seed — no reset/reseed needed.
            Assert.Equal(1L, await CountCountriesAsync(connStr!));
            _output.WriteLine("Build path: seed present (1 row) — first watch run sees the seed.");

            // ── REUSE path: reset (Respawn) clears the seed rows too, then ReseedAsync restores.
            await using var isolation = new RespawnPostgresIsolation(connStr!);
            await isolation.EndScenarioAsync(CancellationToken.None);

            // After the reset alone, the seed row is gone (the "Respawn-truncates-seed" behaviour).
            Assert.Equal(0L, await CountCountriesAsync(connStr!));
            _output.WriteLine("Reuse path: post-Respawn the seed is truncated (0 rows) — as documented.");

            // ReseedAsync re-applies the seed, restoring the freshly-built baseline.
            await suite.ReseedAsync(CancellationToken.None);
            Assert.Equal(1L, await CountCountriesAsync(connStr!));
            _output.WriteLine("Reuse path: after ReseedAsync the seed is back (1 row) — baseline restored.");
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    /// <summary>
    /// Counts the rows in the seeded <c>countries</c> table over a fresh Npgsql connection.
    /// </summary>
    private static async Task<long> CountCountriesAsync(string connStr)
    {
        var conn = new Npgsql.NpgsqlConnection(connStr);
        await using (conn.ConfigureAwait(false))
        {
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = "SELECT COUNT(*) FROM countries";
                return (long)(await cmd.ExecuteScalarAsync())!;
            }
        }
    }

    /// <summary>
    /// Broken seed: a SQL file with a syntax error makes
    /// <see cref="SuiteTopology.StartAsync"/> throw an
    /// <see cref="OrchestrationException"/> (Provision kind) — an Environment error
    /// (§12.1), never a Fail.  The topology is disposed on the failure path.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task BrokenSeedSql_StartAsync_ThrowsOrchestrationException()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "vouchfx-seed-bad-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(baseDir);
        try
        {
            // Arrange — a deliberately malformed SQL statement.
            var sqlPath = Path.Combine(baseDir, "broken.sql");
            await File.WriteAllTextAsync(sqlPath, "THIS IS NOT VALID SQL;");

            var sqlFiles = new List<string> { "broken.sql" };
            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                [DepName] = new DependencySeed(sqlFiles),
            });

            // Act + Assert — StartAsync wraps the Npgsql error as an Environment error.
            var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
                SuiteTopology.StartAsync(
                    environment: BuildEnv(seed),
                    appHostAssemblyName: AppHostAssemblyName,
                    startupTimeout: StartupTimeout,
                    seedBaseDirectory: baseDir));

            _output.WriteLine($"Kind: {ex.Info.Kind}, Resource: {ex.Info.ResourceName}, Detail: {ex.Info.Detail}");
            Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
            Assert.Contains("broken.sql", ex.Info.Detail, StringComparison.Ordinal);

            // Verdict-mapping contract: Environment error, never Fail.
            var evt = EnvironmentErrorEvents.Create(ex.Info, "run", DateTimeOffset.UnixEpoch);
            Assert.Equal(Verdict.EnvironmentError, evt.Verdict);
            Assert.NotEqual(Verdict.Fail, evt.Verdict);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }
}
