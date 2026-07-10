// DbAssertSqlServerProvider Docker-gated end-to-end execution tests.
//
// Mirrors DbAssertPostgresDockerTests.cs.  Proves that the emitted CSX fragment
// executes correctly against a real SQL Server container started via SuiteTopology
// (the AppHost fixture with Aspire.AppHost.Sdk).
//
// Three verdict scenarios are exercised:
//   Pass          — query returns the expected row count and/or column value.
//   Fail          — query returns an unexpected row count (assertion mismatch).
//   EnvironmentError — connection key absent from Vars.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~DbAssertSqlServerDocker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
//
// Design notes:
//   • The test starts a SuiteTopology with a single SQL Server dependency ("testdb").
//   • After startup, the conn string is read from DiscoveredServices and staged in
//     Vars under VarKeys.Connection("testdb").
//   • A seed SqlConnection creates the test table + inserts a row.
//   • The provider emits a fragment; CsxAssembler + CompileOnce + RunIsolatedAsync
//     execute it.
//   • Microsoft.Data.SqlClient is added to additionalReferencePaths (compile-ref only,
//     never collectible ALC).
//   • NOTE: Do NOT run this test locally or in CI unless Docker is available and
//     mcr.microsoft.com/mssql/server:2022-latest is pre-pulled.
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.SqlServer;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end execution tests for <see cref="DbAssertSqlServerProvider"/>.
/// Requires a running Docker daemon with the SQL Server 2022 image available.
/// </summary>
/// <remarks>
/// This test class carries <c>[Trait("requires","docker")]</c> on every method so it is
/// excluded from the non-Docker CI filter (<c>dotnet test --filter "requires!=docker"</c>).
/// The test assembly already carries <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c>
/// and <c>Aspire.AppHost.Sdk</c>, which embed the <c>dcpclipath</c> metadata required by
/// <see cref="SuiteTopology.StartAsync"/>.
/// </remarks>
public sealed class DbAssertSqlServerDockerTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>Generous startup timeout: SQL Server is slower to start than Postgres.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(240);

    /// <summary>Logical name of the SQL Server dependency under test.</summary>
    private const string DepName = "testdb";

    public DbAssertSqlServerDockerTests(ITestOutputHelper output) => _output = output;

    // ── Shared compile-reference paths ────────────────────────────────────────

    /// <summary>
    /// Additional Roslyn metadata references for the emitted CSX body.
    /// Microsoft.Data.SqlClient and System.Text.Json are not in the default TPA
    /// subset so they must be supplied explicitly.  These are compile-time references
    /// only; at runtime the assemblies resolve from the Default ALC.
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(Microsoft.Data.SqlClient.SqlConnection).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
    };

    // ── Test helpers ─────────────────────────────────────────────────────────

    /// <summary>Minimal <see cref="ICompileContext"/> for emit tests.</summary>
    private sealed class StubCompileContext : ICompileContext
    {
        public StubCompileContext(string stepId) => StepId = stepId;

        /// <inheritdoc />
        public string StepId { get; }

        /// <inheritdoc />
        public string SuiteNamespace => "Generated";

        /// <inheritdoc />
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <inheritdoc />
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Builds the Aspire environment spec for a single SQL Server dependency.
    /// </summary>
    private static EnvironmentSpec BuildEnv() =>
        new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                [DepName] = new DependencySpec(Type: "sqlserver", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>
    /// Creates the seed table and inserts one test row via a direct SqlConnection.
    /// </summary>
    private static async Task SeedAsync(string connStr)
    {
        var conn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
        try
        {
            await conn.OpenAsync().ConfigureAwait(false);
            var cmd = conn.CreateCommand();
            try
            {
                cmd.CommandText =
                    "IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'orders') " +
                    "CREATE TABLE orders (id INT PRIMARY KEY, status NVARCHAR(50) NOT NULL); " +
                    "DELETE FROM orders; " +
                    "INSERT INTO orders (id, status) VALUES (1, 'SHIPPED');";
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

    /// <summary>
    /// Emits the fragment for a <c>db-assert.sqlserver</c> step, assembles it,
    /// compiles it once, and executes it with the supplied <c>Vars</c> dictionary.
    /// Returns the <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private static async Task<StepOutcome> RunStepAsync(
        DbAssertSqlServerModel model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var provider = new DbAssertSqlServerProvider();
        var ctx = new StubCompileContext(stepId);
        var fragment = provider.Emit(model, ctx);

        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource,
            additionalReferencePaths: s_additionalRefs);

        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var safeId = CsxFragment.SanitiseId(stepId);
        var outcomeKey = VarKeys.Outcome(safeId);

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Vars must contain outcome key '{outcomeKey}' after RunIsolatedAsync. " +
            $"Actual keys: [{string.Join(", ", vars.Keys)}]");

        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }

    // ── Test cases ────────────────────────────────────────────────────────────

    /// <summary>
    /// When the seeded row matches <c>expect.rowCount=1</c> and
    /// <c>expect.row["status"]="SHIPPED"</c>, the step outcome must be
    /// <see cref="Verdict.Pass"/>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_MatchingRowCountAndColumn_ReturnsPass()
    {
        var env = BuildEnv();
        await using var suite = await SuiteTopology.StartAsync(
            environment: env,
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr),
            $"DiscoveredServices['{DepName}'] must be a non-empty connection string.");
        _output.WriteLine($"SQL Server conn string: {connStr}");

        await SeedAsync(connStr!);
        _output.WriteLine("Seed: orders table created and populated.");

        var model = new DbAssertSqlServerModel(
            Target: DepName,
            Query: "SELECT id, status FROM orders WHERE id = 1",
            Parameters: null,
            Expect: new SqlServerExpectation(
                RowCount: 1,
                Row: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["status"] = "SHIPPED",
                }));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "assert-row", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, DurationMs: {outcome.DurationMs}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);
    }

    /// <summary>
    /// When the query returns 1 row but <c>expect.rowCount=99</c>, the step
    /// outcome must be <see cref="Verdict.Fail"/> with a row-count mismatch observation.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_RowCountMismatch_ReturnsFail()
    {
        var env = BuildEnv();
        await using var suite = await SuiteTopology.StartAsync(
            environment: env,
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));
        await SeedAsync(connStr!);

        var model = new DbAssertSqlServerModel(
            Target: DepName,
            Query: "SELECT id FROM orders",
            Parameters: null,
            Expect: new SqlServerExpectation(RowCount: 99, Row: null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DepName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "assert-rowcount-fail", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("rowCount", outcome.Observation, StringComparison.Ordinal);
    }

    /// <summary>
    /// When the <c>conn::</c> key is absent from <c>Vars</c>, the step outcome
    /// must be <see cref="Verdict.EnvironmentError"/> (§12.1: infrastructure failure,
    /// never conflated with a test failure).
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_AbsentConnKey_ReturnsEnvironmentError()
    {
        // No topology needed — the EnvironmentError is detected before any network call.
        var model = new DbAssertSqlServerModel(
            Target: "missing-dep",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new SqlServerExpectation(RowCount: 1, Row: null));

        // Deliberately absent: no VarKeys.Connection("missing-dep") in Vars.
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);

        var outcome = await RunStepAsync(model, "assert-env-error", vars);

        _output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("error", outcome.Observation, StringComparison.OrdinalIgnoreCase);
    }
}
