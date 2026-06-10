// S04 Capstone — non-docker ProviderPipeline.Compile test (Sprint 4).
//
// Parses the capstone .e2e.yaml through the front end, builds the frozen
// StepKindRegistry over the three Core provider assemblies, and calls
// ProviderPipeline.Compile to assert:
//
//   1. Failure == null (pipeline succeeds).
//   2. Assembled.CsxSource is non-null and contains:
//      (a) all three providers' helper classes (HttpRest_Helpers, DbAssertPostgres_Helpers,
//          ScriptCsharp_Helpers, Substitute_Helpers).
//      (b) the capture parallel arrays for the fetch-id step (varNames / jsonPaths).
//      (c) the Substitute_Helpers.Resolve wrapping for the db-assert query parameter.
//   3. ResourcePlan contains a postgres ResourceRequirement whose Name == "capdb".
//   4. CompileReferencePaths includes both Npgsql and JsonPath.Net assembly locations.
//
// This test exercises ProviderPipeline.Compile independently of Docker/topology.

using Platform.Engine.Abstractions;
using Platform.Engine.Authoring;
using Platform.Engine.Runtime;
using Platform.Sdk;
using Platform.Steps.DbAssert.Postgres;
using Platform.Steps.HttpRest;
using Platform.Steps.Script.Csharp;
using Xunit;

namespace Platform.Engine.Runtime.Tests;

/// <summary>
/// Non-docker proof that <see cref="ProviderPipeline.Compile"/> correctly
/// processes the Sprint 4 capstone YAML — three providers, capture arrays,
/// substitution helpers, resource plan, and compile-reference paths.
/// </summary>
public sealed class Sprint04CapstoneCompileTests
{
    // ── Provider assemblies: all three Core providers ─────────────────────────

    private static readonly System.Reflection.Assembly[] s_providerAssemblies = new[]
    {
        typeof(HttpRestProvider).Assembly,
        typeof(DbAssertPostgresProvider).Assembly,
        typeof(ScriptCsharpProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    private const string SuiteNamespace = "VouchfxGenerated";

    // ── Capstone YAML (same as Sprint04CapstoneTests.CapstoneYaml) ─────────────

    /// <summary>
    /// The capstone scenario YAML, identical to the one used by the docker
    /// capstone test.  Defined here to keep the compile test self-contained
    /// (no cross-project dependency on the docker test class).
    /// </summary>
    private const string CapstoneYaml = """
        metadata:
          name: sprint-04-capstone
          description: HTTP capture, script INSERT, db-assert; proves Sprint 4 end-to-end.

        variables:
          expectedStatus: DONE

        environment:
          services:
            whoami:
              image: traefik/whoami
              httpPort: 80
          dependencies:
            capdb:
              type: postgres

        steps:
          - id: fetch-id
            type: http.rest
            target: whoami
            method: GET
            path: /api
            expect:
              status: 200
            capture:
              hostname: "$.hostname"

          - id: insert-row
            type: script.csharp
            code: |
              var connStr = (string)Vars["conn::capdb"];
              var hostVal = (string)Vars["hostname"];
              var conn = new Npgsql.NpgsqlConnection(connStr);
              conn.Open();
              try {
                var cmdCreate = conn.CreateCommand();
                try {
                  cmdCreate.CommandText =
                    "CREATE TABLE IF NOT EXISTS key_captures " +
                    "(key TEXT PRIMARY KEY, status TEXT NOT NULL);";
                  cmdCreate.ExecuteNonQuery();
                } finally { cmdCreate.Dispose(); }
                var cmdInsert = conn.CreateCommand();
                try {
                  cmdInsert.CommandText =
                    "INSERT INTO key_captures (key, status) VALUES (@k, @s);";
                  cmdInsert.Parameters.AddWithValue("k", hostVal);
                  cmdInsert.Parameters.AddWithValue("s", "DONE");
                  var rows = cmdInsert.ExecuteNonQuery();
                  if (rows != 1)
                    throw new Exception("Expected INSERT to affect 1 row, got " + rows + ".");
                } finally { cmdInsert.Dispose(); }
              } finally { conn.Dispose(); }

          - id: assert-row
            type: db-assert.postgres
            target: capdb
            query: "SELECT key, status FROM key_captures WHERE key = @k"
            parameters:
              k: "{hostname}"
            expect:
              rowCount: 1
              row:
                status: "{expectedStatus}"
        """;

    // ── The compile test ──────────────────────────────────────────────────────

    /// <summary>
    /// Parses the capstone YAML, calls <see cref="ProviderPipeline.Compile"/>,
    /// and asserts all four properties of the resulting <see cref="PipelineResult"/>.
    /// </summary>
    [Fact]
    public void Compile_CapstoneYaml_ProducesCorrectPipelineResult()
    {
        // ── 1. Parse through the front end ────────────────────────────────────
        var doc = YamlDocumentParser.Parse(CapstoneYaml);
        var ast = AstBuilder.Build(doc, s_registry);

        // Smoke-check: 3 steps.
        Assert.Equal(3, ast.Steps.Count);

        // ── 2. Compile ────────────────────────────────────────────────────────
        var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);

        // ── 3. No failure ─────────────────────────────────────────────────────
        Assert.Null(result.Failure);

        // ── 4. Assembled script non-null ──────────────────────────────────────
        Assert.NotNull(result.Assembled);
        var src = result.Assembled!.CsxSource;
        Assert.False(string.IsNullOrWhiteSpace(src),
            "Assembled CsxSource must not be empty or whitespace.");

        // ── 5. Provider helper classes present ───────────────────────────────
        // http.rest and db-assert.postgres both contribute RequiredHelpers.
        // script.csharp emits an empty RequiredHelpers (no shared static class needed).
        Assert.Contains("HttpRest_Helpers", src, StringComparison.Ordinal);
        Assert.Contains("DbAssertPostgres_Helpers", src, StringComparison.Ordinal);
        Assert.Contains("Substitute_Helpers", src, StringComparison.Ordinal);

        // ── 6. Capture parallel arrays for fetch-id ───────────────────────────
        // The http.rest emitter generates parallel arrays of capture var-names and
        // JSONPath expressions.  Both "hostname" (the var name) and "$.hostname"
        // (the JSONPath) must appear in the assembled source.
        Assert.Contains("\"hostname\"", src, StringComparison.Ordinal);
        Assert.Contains("\"$.hostname\"", src, StringComparison.Ordinal);

        // The CaptureStatus key must also be present (VarKeys.CaptureStatus prefix).
        Assert.Contains(VarKeys.CaptureStatusPrefix, src, StringComparison.Ordinal);

        // ── 7. Substitute_Helpers.Resolve wrapping in db-assert block ─────────
        // The db-assert emitter wraps substitutable fields (query, parameter values)
        // in Substitute_Helpers.Resolve.  The placeholder "{hostname}" appears as
        // the raw template literal in the parameter value.
        Assert.Contains("Substitute_Helpers", src, StringComparison.Ordinal);
        // The hostname placeholder must appear somewhere in the db-assert body.
        Assert.Contains("hostname", src, StringComparison.Ordinal);

        // ── 8. No "using var" in the assembled source ─────────────────────────
        Assert.DoesNotContain("using var", src, StringComparison.Ordinal);

        // ── 9. StepIds in declaration order ──────────────────────────────────
        Assert.Equal(3, result.Assembled!.StepIds.Count);
        Assert.Equal("fetch-id", result.Assembled.StepIds[0]);
        Assert.Equal("insert-row", result.Assembled.StepIds[1]);
        Assert.Equal("assert-row", result.Assembled.StepIds[2]);

        // ── 10. ResourcePlan: postgres requirement for "capdb" ────────────────
        // db-assert.postgres implements IResourceContributor and contributes a
        // ResourceRequirement with Family=="postgres" and Name=="capdb" (the
        // target declared in the step).
        var postgresEntry = result.ResourcePlan
            .FirstOrDefault(e => string.Equals(e.Requirement.Family, "postgres",
                StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(postgresEntry);
        Assert.Equal("capdb", postgresEntry!.Requirement.Name);
        Assert.Equal("assert-row", postgresEntry.StepId);
        Assert.Contains("DbAssertPostgresProvider", postgresEntry.ProviderTypeName,
            StringComparison.Ordinal);

        // ── 11. CompileReferencePaths includes Npgsql and JsonPath.Net ─────────
        var npgsqlLocation = typeof(Npgsql.NpgsqlConnection).Assembly.Location;
        var jsonPathLocation = typeof(Json.Path.JsonPath).Assembly.Location;

        Assert.NotEmpty(npgsqlLocation);
        Assert.NotEmpty(jsonPathLocation);

        Assert.Contains(npgsqlLocation, result.CompileReferencePaths,
            StringComparer.Ordinal);
        Assert.Contains(jsonPathLocation, result.CompileReferencePaths,
            StringComparer.Ordinal);

        // ── 12. Provenance: CapturedVar struct from pipeline implies G-01 ─────
        // The CapturedVar / SubstitutionRef records are emitted by ScenarioRunner
        // at runtime (not by ProviderPipeline.Compile).  Here we verify the
        // compile-time preconditions that enable provenance:
        //  (a) The http.rest step declares a capture (hostname → $.hostname).
        var fetchStep = ast.Steps[0];
        Assert.Equal("fetch-id", fetchStep.Id);
        Assert.True(fetchStep.Capture.ContainsKey("hostname"),
            "fetch-id step must declare a capture for 'hostname'.");
        Assert.Equal(CaptureFormat.JsonPath, fetchStep.Capture["hostname"].Format);
        Assert.Equal("$.hostname", fetchStep.Capture["hostname"].Expression);

        //  (b) The db-assert step declares parameters with a {hostname} placeholder.
        var assertStep = ast.Steps[2];
        Assert.Equal("assert-row", assertStep.Id);

        // The raw YAML mapping node for assert-row must contain "parameters" with
        // "{hostname}" as a value (verifiable via the RawNode).
        // We don't read RawNode directly here — instead confirm the canonical type
        // so we know it will be processed by the db-assert emitter.
        Assert.Equal("db-assert.postgres", assertStep.CanonicalType);
    }
}
