// S08 Parallel Capstone — non-docker compile-proof twin (Sprint 8, T1).
//
// The docker-gated twin (Sprint08ParallelCapstoneTests, same project) runs TWO Postgres
// scenarios CONCURRENTLY via the real ParallelSuiteRunner.RunParallelAsync (maxConcurrency: 2),
// each owning its OWN topology, and proves row-isolation-by-construction (each scenario inserts
// its own sentinel row and asserts ONLY its row exists) plus declaration-order output.  This
// twin proves — without any container — that BOTH parallel-capstone scenarios are well-formed
// end-to-end up to (NOT including) topology start: each parses, JSON-Schema-validates, builds an
// AST, and compiles through ProviderPipeline.Compile with the expected db-assert.postgres +
// script.csharp pipeline.
//
// It mirrors Sprint07CapstoneCompileTests: build the frozen StepKindRegistry over the Core
// provider assemblies, validate, build the AST, and call ProviderPipeline.Compile — asserting on
// the PipelineResult and the assembled CSX, never starting a topology.

using System;
using System.Linq;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Postgres;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Non-docker proof that BOTH Sprint 8 parallel-capstone scenarios (the two sentinel-row Postgres
/// scenarios) parse, JSON-Schema-validate, build an AST, and compile through
/// <see cref="ProviderPipeline.Compile"/> — without any container or topology.
/// </summary>
public sealed class Sprint08ParallelCapstoneCompileTests
{
    private static readonly System.Reflection.Assembly[] s_providerAssemblies = new[]
    {
        typeof(HttpRestProvider).Assembly,
        typeof(DbAssertPostgresProvider).Assembly,
        typeof(ScriptCsharpProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    private const string SuiteNamespace = "VouchfxGenerated";

    // ── Capstone YAMLs (byte-identical to the docker twin Sprint08ParallelCapstoneTests) ────

    /// <summary>
    /// Scenario A: a script.csharp step inserts sentinel row A into its OWN fresh topology's
    /// Postgres, then a db-assert.postgres step asserts EXACTLY one row exists carrying tag 'A'.
    /// </summary>
    internal const string ScenarioAYaml = """
        metadata:
          name: parallel-capstone-A
          owner: vouchfx-core
          tags: [s8-parallel, scenario-a]
          description: Inserts sentinel row A into its own topology and asserts only row A exists.

        environment:
          dependencies:
            shop:
              type: postgres

        steps:
          - id: seed-A
            type: script.csharp
            code: |
              var cs = (string)Vars["conn::shop"];
              var conn = new Npgsql.NpgsqlConnection(cs);
              await conn.OpenAsync();
              try
              {
                var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE IF NOT EXISTS sentinels (tag TEXT PRIMARY KEY); INSERT INTO sentinels (tag) VALUES ('A');";
                await cmd.ExecuteNonQueryAsync();
              }
              finally { await conn.DisposeAsync(); }

          - id: assert-only-A
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

    /// <summary>
    /// Scenario B: the structural mirror of A but with sentinel tag 'B'.  Proves row-isolation by
    /// construction — if the two shared a database, each scenario's <c>rowCount: 1</c> assertion
    /// would fail (two rows), so a Pass from BOTH is the isolation proof.
    /// </summary>
    internal const string ScenarioBYaml = """
        metadata:
          name: parallel-capstone-B
          owner: vouchfx-core
          tags: [s8-parallel, scenario-b]
          description: Inserts sentinel row B into its own topology and asserts only row B exists.

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
                tag: B
        """;

    [Theory]
    [InlineData(nameof(ScenarioAYaml))]
    [InlineData(nameof(ScenarioBYaml))]
    public void Compile_ParallelCapstoneScenario_ProducesScriptThenDbAssertPipeline(string which)
    {
        var yaml = which == nameof(ScenarioAYaml) ? ScenarioAYaml : ScenarioBYaml;

        // ── 1. JSON-Schema validation accepts the YAML (no topology) ───────────────
        var validation = DocumentValidator.Validate(yaml, s_registry);
        Assert.True(
            validation.IsValid,
            $"Parallel capstone YAML '{which}' must pass schema validation. Errors: " +
            string.Join(" | ", validation.Errors.Select(e => e.Message)));

        // ── 2. Parse + build the AST ───────────────────────────────────────────────
        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, s_registry);

        Assert.Equal(2, ast.Steps.Count);
        Assert.Equal("script.csharp", ast.Steps[0].CanonicalType);
        Assert.Equal("db-assert.postgres", ast.Steps[1].CanonicalType);

        // The 'shop' postgres dependency is declared.
        Assert.NotNull(ast.Environment);
        Assert.NotNull(ast.Environment!.Dependencies);
        Assert.True(ast.Environment.Dependencies!.ContainsKey("shop"));

        // Both scenarios carry the shared selection tag (a `--tag s8-parallel` run picks both).
        Assert.NotNull(ast.Metadata);
        Assert.Contains("s8-parallel", ast.Metadata!.Tags!, StringComparer.OrdinalIgnoreCase);

        // ── 3. Compile through the provider pipeline ───────────────────────────────
        var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);

        Assert.Null(result.Failure);
        Assert.NotNull(result.Assembled);
        var src = result.Assembled!.CsxSource;
        Assert.False(string.IsNullOrWhiteSpace(src));

        // The db-assert.postgres helper is spliced in (the assertion step), and the §13.3.1
        // 'using var' prohibition holds across the whole assembled source.
        Assert.Contains("DbAssertPostgres_Helpers", src, StringComparison.Ordinal);
        Assert.DoesNotContain("using var", src, StringComparison.Ordinal);

        // The postgres resource requirement named 'shop' is in the plan for the assert step.
        var postgresEntry = Assert.Single(
            result.ResourcePlan,
            e => string.Equals(e.Requirement.Family, "postgres", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("shop", postgresEntry.Requirement.Name);
    }

    [Fact]
    public void BothParallelScenarios_ShareTheS8Tag_ButCarryDistinctSecondaryTags()
    {
        var aAst = AstBuilder.Build(YamlDocumentParser.Parse(ScenarioAYaml), s_registry);
        var bAst = AstBuilder.Build(YamlDocumentParser.Parse(ScenarioBYaml), s_registry);

        Assert.Contains("s8-parallel", aAst.Metadata!.Tags!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("s8-parallel", bAst.Metadata!.Tags!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("scenario-a", aAst.Metadata!.Tags!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("scenario-b", bAst.Metadata!.Tags!, StringComparer.OrdinalIgnoreCase);
    }
}
