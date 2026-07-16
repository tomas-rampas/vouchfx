// S04-A-01 + S04-A-02 Docker-gated proof: Respawn resets Postgres state between scenarios.
//
// Test proves:
//   • Topology built once via SuiteTopology.StartAsync.
//   • Scenario 1 (script.csharp): inserts a row into the 'orders' table, asserts COUNT == 1.
//   • Respawn reset occurs between scenarios via RespawnRelationalIsolation (Postgres kind).
//   • Scenario 2 (script.csharp): asserts COUNT == 0, proving Respawn wiped scenario 1's insert.
//
// This is the core A-01 + A-02 reset-proof: without Respawn, scenario 2 would see 1 row and fail.
// This is the Postgres non-regression gate for the RespawnPostgresIsolation → RespawnRelationalIsolation
// generalisation — the assertions are unchanged; only the isolation type name/ctor shape moved.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~RespawnResetProof"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"

using System.Text;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated proof that <see cref="RespawnRelationalIsolation"/> resets Postgres
/// state between scenarios (S04-A-01 + S04-A-02).
/// </summary>
/// <remarks>
/// <para>
/// The topology is built <em>once</em> via <see cref="SuiteTopology.StartAsync"/>.
/// <see cref="RespawnRelationalIsolation"/> (with <see cref="RelationalStoreKind.Postgres"/>)
/// is used to bracket each scenario so that mutations made in scenario 1 are completely
/// wiped before scenario 2 runs.
/// </para>
/// <para>
/// The test uses the <c>script.csharp</c> provider to emit inline C# that issues
/// Npgsql commands directly against the staged connection string.  No YAML / full
/// ScenarioRunner is required here — this is a targeted proof of the isolation seam.
/// </para>
/// </remarks>
public sealed class RespawnResetProofTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>Generous startup timeout: allows image pulls on first run.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Logical name of the Postgres dependency under test.</summary>
    private const string DepName = "ordersdb";

    public RespawnResetProofTests(ITestOutputHelper output) => _output = output;

    // ── Shared compile-reference paths ────────────────────────────────────────

    /// <summary>
    /// Additional Roslyn metadata references for emitted CSX bodies that use Npgsql.
    /// Compile-time only — assemblies resolve from the Default ALC at runtime (§5).
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(Npgsql.NpgsqlConnection).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the <see cref="EnvironmentSpec"/> for a single Postgres dependency.
    /// </summary>
    private static EnvironmentSpec BuildEnv() =>
        new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                [DepName] = new DependencySpec(Type: "postgres", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>
    /// Creates the test schema (orders table with zero rows) via a direct Npgsql connection.
    /// Called once after the topology is healthy, before scenario execution begins.
    /// </summary>
    private static async Task SeedSchemaAsync(string connStr)
    {
        var conn = new Npgsql.NpgsqlConnection(connStr);
        try
        {
            await conn.OpenAsync().ConfigureAwait(false);
            var cmd = conn.CreateCommand();
            try
            {
                // Create the table and ensure it starts empty.
                cmd.CommandText =
                    "CREATE TABLE IF NOT EXISTS orders " +
                    "    (id SERIAL PRIMARY KEY, status TEXT NOT NULL);" +
                    "TRUNCATE TABLE orders;";
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
    /// Emits, assembles, compiles, and runs a <c>script.csharp</c> fragment
    /// with the supplied inline C# <paramref name="code"/>.  Returns the
    /// <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private static async Task<StepOutcome> RunScriptStepAsync(
        string stepId,
        string code,
        Dictionary<string, object?> vars)
    {
        var provider = new ScriptCsharpProvider();
        var model = new ScriptCsharpModel(Code: code, File: null);

        // StubCompileContext mirrors the one in DbAssertPostgresDockerTests.
        var ctx = new StubCtx(stepId);
        var fragment = provider.Emit(model, ctx);

        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });

        // Full TPA list + Npgsql for the inline connection code.
        var tpaPaths = (
            (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(s_additionalRefs)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource,
            additionalReferencePaths: tpaPaths);

        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals).ConfigureAwait(false);

        var safeId = CsxFragment.SanitiseId(stepId);
        var outcomeKey = VarKeys.Outcome(safeId);

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Vars must contain outcome key '{outcomeKey}' after RunIsolatedAsync.");

        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }

    /// <summary>Minimal <see cref="ICompileContext"/> stub.</summary>
    private sealed class StubCtx : ICompileContext
    {
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public StubCtx(string stepId) => StepId = stepId;
        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    // ── Test ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Core Respawn reset-proof:
    /// <list type="bullet">
    ///   <item>Topology built once (one Postgres container).</item>
    ///   <item>Schema seeded: <c>orders</c> table, zero rows.</item>
    ///   <item>Scenario 1: INSERTs one row; asserts COUNT == 1 → Pass.</item>
    ///   <item><see cref="RespawnRelationalIsolation.EndScenarioAsync"/> resets the DB.</item>
    ///   <item>Scenario 2: asserts COUNT == 0 → Pass (proving Respawn wiped scenario 1's data).</item>
    /// </list>
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task RespawnIsolation_Scenario2_SeesResetState_AfterScenario1Insert()
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

        _output.WriteLine($"Postgres conn string: {connStr}");

        // ── Seed the schema (table only, no rows) ─────────────────────────────
        await SeedSchemaAsync(connStr!);
        _output.WriteLine("Seed: orders table created (empty).");

        // ── Construct isolation ────────────────────────────────────────────────
        await using var isolation = new RespawnRelationalIsolation(DepName, RelationalStoreKind.Postgres, connStr!);

        // ─────────────────────────────────────────────────────────────────────
        // SCENARIO 1 — INSERT a row, assert COUNT == 1
        // ─────────────────────────────────────────────────────────────────────
        await isolation.BeginScenarioAsync(CancellationToken.None);
        _output.WriteLine("Scenario 1: BeginScenarioAsync called (initial reset).");

        // Build the Npgsql connection string as a C# string literal for the inline code.
        var connStrLiteral = System.Text.Json.JsonSerializer.Serialize(connStr);

        // Build the inline C# body using Append to avoid CA1305 (locale-sensitive
        // interpolated AppendLine).  The connection string literal is already
        // JSON-serialised (escaping handled by JsonSerializer.Serialize above).
        var insertCode = new StringBuilder();
        insertCode.Append("// Scenario 1: insert one row.\n");
        insertCode.Append("var conn1 = new Npgsql.NpgsqlConnection(").Append(connStrLiteral).Append(");\n");
        insertCode.Append("conn1.Open();\n");
        insertCode.Append("try {\n");
        insertCode.Append("    var cmd1 = conn1.CreateCommand();\n");
        insertCode.Append("    try {\n");
        insertCode.Append("        cmd1.CommandText = \"INSERT INTO orders (status) VALUES ('PENDING');\";\n");
        insertCode.Append("        cmd1.ExecuteNonQuery();\n");
        insertCode.Append("        var cmdCount = conn1.CreateCommand();\n");
        insertCode.Append("        try {\n");
        insertCode.Append("            cmdCount.CommandText = \"SELECT COUNT(*) FROM orders;\";\n");
        insertCode.Append("            var count1 = (long)cmdCount.ExecuteScalar()!;\n");
        insertCode.Append("            if (count1 != 1L) throw new Exception(\"Expected 1 row after INSERT, got \" + count1 + \".\");\n");
        insertCode.Append("        } finally { cmdCount.Dispose(); }\n");
        insertCode.Append("    } finally { cmd1.Dispose(); }\n");
        insertCode.Append("} finally { conn1.Dispose(); }\n");

        var s1Vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var s1Outcome = await RunScriptStepAsync("scenario1-insert", insertCode.ToString(), s1Vars);

        _output.WriteLine(
            $"Scenario 1 verdict: {s1Outcome.Verdict}, observation: {s1Outcome.Observation}");
        Assert.Equal(Verdict.Pass, s1Outcome.Verdict);

        // EndScenarioAsync — resets the DB (this is the key call under test).
        await isolation.EndScenarioAsync(CancellationToken.None);
        _output.WriteLine("Scenario 1: EndScenarioAsync called — Respawn reset executed.");

        // ─────────────────────────────────────────────────────────────────────
        // SCENARIO 2 — assert COUNT == 0 (Respawn must have wiped scenario 1's row)
        // ─────────────────────────────────────────────────────────────────────
        await isolation.BeginScenarioAsync(CancellationToken.None);
        _output.WriteLine("Scenario 2: BeginScenarioAsync called.");

        var assertZeroCode = new StringBuilder();
        assertZeroCode.Append("// Scenario 2: verify table is empty after Respawn reset.\n");
        assertZeroCode.Append("var conn2 = new Npgsql.NpgsqlConnection(").Append(connStrLiteral).Append(");\n");
        assertZeroCode.Append("conn2.Open();\n");
        assertZeroCode.Append("try {\n");
        assertZeroCode.Append("    var cmd2 = conn2.CreateCommand();\n");
        assertZeroCode.Append("    try {\n");
        assertZeroCode.Append("        cmd2.CommandText = \"SELECT COUNT(*) FROM orders;\";\n");
        assertZeroCode.Append("        var count2 = (long)cmd2.ExecuteScalar()!;\n");
        assertZeroCode.Append("        if (count2 != 0L) throw new Exception(\"Expected 0 rows after Respawn reset, got \" + count2 + \".  Scenario 1 data was NOT wiped.\");\n");
        assertZeroCode.Append("    } finally { cmd2.Dispose(); }\n");
        assertZeroCode.Append("} finally { conn2.Dispose(); }\n");

        var s2Vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var s2Outcome = await RunScriptStepAsync("scenario2-assert-zero", assertZeroCode.ToString(), s2Vars);

        _output.WriteLine(
            $"Scenario 2 verdict: {s2Outcome.Verdict}, observation: {s2Outcome.Observation}");

        Assert.Equal(Verdict.Pass, s2Outcome.Verdict);

        await isolation.EndScenarioAsync(CancellationToken.None);

        _output.WriteLine(
            "Reset-proof PASS: Scenario 2 sees 0 rows — Respawn successfully wiped " +
            "Scenario 1's INSERT between scenarios.");
    }
}
