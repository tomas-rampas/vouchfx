// S08 Parallel Capstone — Sprint 8 scenario-parallelism integration proof (DOCKER-GATED).
//
// Proves ParallelSuiteRunner.RunParallelAsync composes end-to-end against REAL topologies with
// the topology-per-scenario slot model:
//
//   Test 1 — row-isolation BY CONSTRUCTION + declaration-order output (both Pass):
//     TWO Postgres scenarios run CONCURRENTLY (maxConcurrency: 2), each owning its OWN topology.
//     Scenario A inserts sentinel row 'A' into its database and asserts EXACTLY one row (tag 'A')
//     exists; scenario B does the same with 'B'.  If the two shared one database, each scenario's
//     `rowCount: 1` assertion would see TWO rows and FAIL — so a Pass from BOTH is the proof that
//     fresh-topology-per-scenario gives clean-database isolation with NO RespawnPostgresIsolation
//     (containers, not a shared connection, provide the isolation).  The rendered report presents
//     scenario A's block BEFORE scenario B's (declaration order), regardless of which finished
//     first — the determinism guarantee.
//
//   Test 2 — complete-all, no fail-fast (A Pass, B Fail → aggregate Fail, A still completed):
//     Scenario A is the same passing sentinel-A scenario; scenario B inserts 'B' but asserts the
//     WRONG tag ('A'), so its db-assert Fails.  The per-scenario verdicts are (A: Pass, B: Fail),
//     the aggregate is Fail, and crucially A STILL completed with Pass — B failing did not cancel
//     its sibling (complete-all semantics).
//
// This test assembly carries <IsAspireHost>true</IsAspireHost> + Aspire.AppHost.Sdk (its .csproj),
// embedding the dcpclipath metadata SuiteTopology.StartAsync requires (R-1, CLAUDE.md §Aspire);
// appHostAssemblyName therefore points at THIS assembly.  Each scenario builds its OWN topology,
// so this is the first capstone that stands up TWO topologies at once (maxConcurrency: 2).
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~Sprint08Parallel"
// Excluded from non-docker CI:  dotnet test --filter "requires!=docker"
//
// The non-docker twin (Sprint08ParallelCapstoneCompileTests, same project) proves the SAME
// scenarios COMPILE end-to-end (parse → validate → AST → ProviderPipeline.Compile) without any
// container.

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Postgres;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.Script.Csharp;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Docker-gated Sprint 8 capstone: runs two Postgres scenarios CONCURRENTLY via
/// <see cref="ParallelSuiteRunner.RunParallelAsync"/> (topology-per-scenario) to prove
/// (1) row-isolation by construction + declaration-order output, and (2) complete-all
/// semantics (a failing scenario does not cancel its passing sibling).
/// </summary>
public sealed class Sprint08ParallelCapstoneTests
{
    private readonly ITestOutputHelper _output;

    public Sprint08ParallelCapstoneTests(ITestOutputHelper output) => _output = output;

    /// <summary>Short name of THIS assembly — it carries the DCP metadata.</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";

    // ── Provider assemblies: the Core providers the capstone wires, discovered reflectively ──
    private static readonly System.Reflection.Assembly[] s_providerAssemblies = new[]
    {
        typeof(HttpRestProvider).Assembly,
        typeof(DbAssertPostgresProvider).Assembly,
        typeof(ScriptCsharpProvider).Assembly,
    };

    // Reuse the byte-identical YAMLs proven to compile by the non-docker twin.
    private const string ScenarioAYaml = Sprint08ParallelCapstoneCompileTests.ScenarioAYaml;
    private const string ScenarioBYaml = Sprint08ParallelCapstoneCompileTests.ScenarioBYaml;

    /// <summary>
    /// Scenario B', the FAILING variant for Test 2: inserts sentinel 'B' but asserts the WRONG
    /// tag ('A'), so the db-assert.postgres step Fails (the row exists, the value differs → a
    /// column-mismatch Fail, NOT an EnvironmentError).
    /// </summary>
    private const string ScenarioBFailYaml = """
        metadata:
          name: parallel-capstone-B-fail
          owner: vouchfx-core
          tags: [s8-parallel, scenario-b-fail]
          description: Inserts sentinel B but asserts tag A, so the db-assert Fails (complete-all proof).

        environment:
          dependencies:
            shop:
              type: postgres

        steps:
          - id: seed-B
            type: script.csharp
            code: |
              var cs = (string)Vars["conn::shop"];
              var conn = new Npgsql.NpgsqlConnection(cs);
              await conn.OpenAsync();
              try
              {
                var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE IF NOT EXISTS sentinels (tag TEXT PRIMARY KEY); INSERT INTO sentinels (tag) VALUES ('B');";
                await cmd.ExecuteNonQueryAsync();
              }
              finally { await conn.DisposeAsync(); }

          - id: assert-only-B
            type: db-assert.postgres
            target: shop
            query: >-
              SELECT tag FROM sentinels
            verifyMode: IMMEDIATE
            expect:
              rowCount: 1
              row:
                tag: A
        """;

    // ── Test 1: row-isolation by construction + declaration-order output (both Pass) ─────────

    /// <summary>
    /// Runs scenarios A and B CONCURRENTLY (maxConcurrency: 2), each owning its own Postgres
    /// topology.  Asserts BOTH pass (proving each scenario saw ONLY its own sentinel row — no
    /// row-bleed across the two databases) and that scenario A's rendered block precedes
    /// scenario B's (declaration-order determinism).
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Sprint08Parallel_TwoScenariosOwnTopologies_NoRowBleed_DeclarationOrderOutput()
    {
        var sw = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));

        var asts = new[]
        {
            BuildAst(ScenarioAYaml),
            BuildAst(ScenarioBYaml),
        };
        var names = new[] { "parallel-capstone-A", "parallel-capstone-B" };
        var yamls = new[] { ScenarioAYaml, ScenarioBYaml };

        var result = await ParallelSuiteRunner.RunParallelAsync(
            scenarios: asts,
            scenarioNames: names,
            yamlTexts: yamls,
            providerAssemblies: s_providerAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw,
            maxConcurrency: 2,
            seedBaseDirectory: null,
            cancellationToken: cts.Token);

        var rendered = sw.ToString();
        _output.WriteLine("=== Terminal output ===");
        _output.WriteLine(rendered);
        _output.WriteLine($"Aggregate: {result.Verdict}");

        // ── 1. Both scenarios pass → each saw ONLY its own row (no row-bleed) ─────────
        // If the two scenarios had shared a database, each `rowCount: 1` assertion would see
        // BOTH sentinel rows and FAIL.  A Pass from both is the isolation-by-construction proof.
        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.Equal(2, result.ScenarioVerdicts.Count);
        Assert.Equal(("parallel-capstone-A", Verdict.Pass), result.ScenarioVerdicts[0]);
        Assert.Equal(("parallel-capstone-B", Verdict.Pass), result.ScenarioVerdicts[1]);

        // ── 2. Declaration-order output: scenario A's block precedes scenario B's ─────
        // The renderer emits "Scenario '<name>' started" per scenario; A must appear before B
        // regardless of which scenario's topology finished first (determinism).
        var idxA = rendered.IndexOf("parallel-capstone-A", StringComparison.Ordinal);
        var idxB = rendered.IndexOf("parallel-capstone-B", StringComparison.Ordinal);
        Assert.True(idxA >= 0, "Scenario A must appear in the rendered output.");
        Assert.True(idxB >= 0, "Scenario B must appear in the rendered output.");
        Assert.True(idxA < idxB, "Scenario A's block must precede scenario B's (declaration order).");

        // ── 3. No internal engine key prefixes leak into the rendered output ─────────
        Assert.DoesNotContain(VarKeys.ConnectionsPrefix, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(VarKeys.CaptureStatusPrefix, rendered, StringComparison.Ordinal);

        _output.WriteLine(
            "Row-isolation proof: two concurrent scenarios each saw only their own sentinel → both Pass.");
    }

    // ── Test 2: complete-all — A Pass, B Fail → aggregate Fail, A still completed ─────────────

    /// <summary>
    /// Runs the passing scenario A alongside the FAILING scenario B' (asserts the wrong tag),
    /// CONCURRENTLY.  Asserts the per-scenario verdicts are (A: Pass, B: Fail), the aggregate is
    /// Fail, and — the complete-all guarantee — scenario A STILL completed with Pass (B failing
    /// did not cancel its sibling).
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Sprint08Parallel_OneScenarioFails_SiblingStillCompletes_AggregateFail()
    {
        var sw = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));

        var asts = new[]
        {
            BuildAst(ScenarioAYaml),
            BuildAst(ScenarioBFailYaml),
        };
        var names = new[] { "parallel-capstone-A", "parallel-capstone-B-fail" };
        var yamls = new[] { ScenarioAYaml, ScenarioBFailYaml };

        var result = await ParallelSuiteRunner.RunParallelAsync(
            scenarios: asts,
            scenarioNames: names,
            yamlTexts: yamls,
            providerAssemblies: s_providerAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw,
            maxConcurrency: 2,
            seedBaseDirectory: null,
            cancellationToken: cts.Token);

        var rendered = sw.ToString();
        _output.WriteLine("=== Terminal output ===");
        _output.WriteLine(rendered);
        _output.WriteLine($"Aggregate: {result.Verdict}");

        // ── 1. Per-scenario verdicts: A Pass, B Fail (in declaration order) ──────────
        Assert.Equal(2, result.ScenarioVerdicts.Count);
        Assert.Equal(("parallel-capstone-A", Verdict.Pass), result.ScenarioVerdicts[0]);
        Assert.Equal(("parallel-capstone-B-fail", Verdict.Fail), result.ScenarioVerdicts[1]);

        // ── 2. Aggregate is Fail (Fail outranks Pass) ───────────────────────────────
        Assert.Equal(Verdict.Fail, result.Verdict);

        // ── 3. Complete-all: scenario A still completed with PASS (not cancelled) ────
        // The passing sibling's block must be present in the rendered output with a PASS — B's
        // failure did not fail-fast the run.
        Assert.Contains("parallel-capstone-A", rendered, StringComparison.Ordinal);
        Assert.Contains("PASS", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FAIL", rendered, StringComparison.OrdinalIgnoreCase);

        _output.WriteLine(
            "Complete-all proof: B failed, A still completed Pass, aggregate Fail.");
    }

    private static Vouchfx.Engine.Authoring.Ast.ScenarioAst BuildAst(string yaml)
    {
        var registry = StepKindRegistry.BuildAndFreeze(s_providerAssemblies);
        var doc = Vouchfx.Engine.Authoring.YamlDocumentParser.Parse(yaml);
        return Vouchfx.Engine.Authoring.AstBuilder.Build(doc, registry);
    }
}
