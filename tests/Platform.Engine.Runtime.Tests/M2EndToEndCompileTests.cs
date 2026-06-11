// S05-D-01 — Milestone-M2 capstone, NON-DOCKER compile-proof twin (MVP §8.2).
//
// The docker-gated twin (M2EndToEndTests, same task) runs the SAME scenario against
// a real seeded + reset topology and proves the live no-leak guarantee.  This twin
// proves — without any container — that the full M2 pipeline COMPILES end-to-end:
// three Core providers (http.rest + db-assert.postgres + script.csharp), an
// execution-time-resolved ${secret:env/…} header, a captured non-secret field threaded
// forward, a substituted db-assert query, the postgres resource plan, and a bound seed
// fixture.
//
// It mirrors Sprint04CapstoneCompileTests: parse the M2 .e2e.yaml through the front
// end, build the frozen StepKindRegistry over the three Core provider assemblies, call
// ProviderPipeline.Compile, and assert on the PipelineResult and the assembled CSX.
//
// M2 additions over the Sprint-4 capstone (the secrets + seed work this proves):
//   • The http.rest step carries an Authorization header whose value is a
//     ${secret:env/…} reference.  The assembled CSX must (a) contain the reference
//     TOKEN verbatim, (b) NOT contain the secret VALUE (resolution is execution-time,
//     never compile-time — §17), and (c) plumb the header value through
//     Secret_Helpers.ResolveTemplate(secrets, vars, …) inside the http.rest helper.
//   • The scenario declares environment.seed with a real SQL fixture; the AST must
//     bind a SeedSpec naming that file (the "reference data seeded" requirement).
//
// Why this is non-docker: ProviderPipeline.Compile + AstBuilder need no topology; the
// secret env var is set only to PROVE a careless compile-time read would leak it — the
// invariant is that it is NEVER read at compile time, so the value must be absent from
// the assembled source.

using System;
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
/// Non-docker proof that <see cref="ProviderPipeline.Compile"/> processes the M2
/// capstone YAML (S05-D-01) end-to-end: three providers, an execution-time secret
/// header, capture arrays, db-assert substitution, the postgres resource plan, and a
/// bound seed fixture — all without a container.
/// </summary>
public sealed class M2EndToEndCompileTests
{
    // ── Provider assemblies: all three Core providers exercised by M2 ─────────────
    private static readonly System.Reflection.Assembly[] s_providerAssemblies = new[]
    {
        typeof(HttpRestProvider).Assembly,
        typeof(DbAssertPostgresProvider).Assembly,
        typeof(ScriptCsharpProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    private const string SuiteNamespace = "VouchfxGenerated";

    // The secret VALUE that, if ever read at compile time, would leak into the
    // assembled source / IL.  The whole point of the M2 secret model is that it is
    // NEVER read at compile time (§17), so this string must be absent from the source.
    private const string SecretValue = "m2-compile-secret-7f1a";

    /// <summary>
    /// Builds the M2 capstone YAML with a unique GUID-suffixed env-var name for the
    /// secret reference, so concurrent test runs cannot collide on the shared process
    /// environment (matches SecretResolutionPipelineTests).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Step 1 (<c>fetch-id</c>): GET <c>/api</c> from <c>traefik/whoami</c> WITH an
    /// <c>Authorization: Bearer ${secret:env/…}</c> header (resolved at execution time);
    /// captures the NON-SECRET <c>$.hostname</c> field → <c>Vars["hostname"]</c>.  The
    /// echoed secret value is never captured into a var.
    /// </para>
    /// <para>
    /// Step 2 (<c>insert-row</c>): <c>script.csharp</c> INSERTs a row into
    /// <c>captured_hosts</c> keyed by the captured hostname (substituted via
    /// <c>{hostname}</c>), alongside the seeded reference table.
    /// </para>
    /// <para>
    /// Step 3 (<c>assert-row</c>): <c>db-assert.postgres</c> asserts BOTH the seeded
    /// reference row AND the script-inserted row are present via a JOIN, exercising
    /// seed + capture + script + db-assert together.
    /// </para>
    /// </remarks>
    internal static string BuildM2Yaml(string envName) =>
        $$"""
        metadata:
          name: m2-end-to-end-capstone
          description: M2 capstone — seed + secret header + capture + script + db-assert end-to-end.

        variables:
          expectedRegion: emea

        environment:
          services:
            whoami:
              image: traefik/whoami
              httpPort: 80
          dependencies:
            m2db:
              type: postgres
          seed:
            m2db:
              sql: [ "fixtures/reference.sql" ]

        steps:
          - id: fetch-id
            type: http.rest
            target: whoami
            method: GET
            path: /api
            headers:
              Authorization: "Bearer ${secret:env/{{envName}}}"
            expect:
              status: 200
            capture:
              hostname: "$.hostname"

          - id: insert-row
            type: script.csharp
            code: |
              var connStr = (string)Vars["conn::m2db"];
              var hostVal = (string)Vars["hostname"];
              var conn = new Npgsql.NpgsqlConnection(connStr);
              conn.Open();
              try {
                var cmd = conn.CreateCommand();
                try {
                  cmd.CommandText =
                    "INSERT INTO captured_hosts (hostname, region) VALUES (@h, @r);";
                  cmd.Parameters.AddWithValue("h", hostVal);
                  cmd.Parameters.AddWithValue("r", "emea");
                  var rows = cmd.ExecuteNonQuery();
                  if (rows != 1)
                    throw new Exception("Expected INSERT to affect 1 row, got " + rows + ".");
                } finally { cmd.Dispose(); }
              } finally { conn.Dispose(); }

          - id: assert-row
            type: db-assert.postgres
            target: m2db
            query: >-
              SELECT ch.hostname, r.region_name
              FROM captured_hosts ch
              JOIN reference_regions r ON r.code = ch.region
              WHERE ch.hostname = @h
            parameters:
              h: "{hostname}"
            expect:
              rowCount: 1
              row:
                region_name: "{expectedRegion}"
        """;

    /// <summary>
    /// Parses the M2 capstone YAML, calls <see cref="ProviderPipeline.Compile"/>, and
    /// asserts the pipeline succeeds and the assembled CSX carries every M2 feature:
    /// three provider contributions, the execution-time secret header, capture arrays,
    /// db-assert substitution, the postgres resource plan, and the bound seed fixture.
    /// </summary>
    [Fact]
    public void Compile_M2Yaml_ProducesCorrectPipelineResult()
    {
        var envName = "VOUCHFX_M2_COMPILE_" + Guid.NewGuid().ToString("N");
        var referenceToken = $"${{secret:env/{envName}}}";

        // Set the env var so a careless compile-time read WOULD leak the value into the
        // assembled source.  The invariant is that it is NEVER read at compile time, so
        // the value must be absent below.
        Environment.SetEnvironmentVariable(envName, SecretValue);
        try
        {
            // ── 1. Parse through the front end ────────────────────────────────────
            var doc = YamlDocumentParser.Parse(BuildM2Yaml(envName));
            var ast = AstBuilder.Build(doc, s_registry);

            // Smoke-check: 3 steps in declaration order.
            Assert.Equal(3, ast.Steps.Count);

            // ── 2. The seed binds (reference data seeded requirement) ─────────────
            // The AST must carry a SeedSpec for the m2db dependency naming the SQL file.
            Assert.NotNull(ast.Environment);
            Assert.NotNull(ast.Environment!.Seed);
            var seed = ast.Environment.Seed!;
            Assert.True(seed.Dependencies.ContainsKey("m2db"),
                "Seed must declare reference data for the 'm2db' dependency.");
            var m2dbSeed = seed.Dependencies["m2db"];
            Assert.NotNull(m2dbSeed.Sql);
            Assert.Contains("fixtures/reference.sql", m2dbSeed.Sql!, StringComparer.Ordinal);

            // ── 3. Compile ────────────────────────────────────────────────────────
            var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);

            // ── 4. No failure; assembled script non-null ─────────────────────────
            Assert.Null(result.Failure);
            Assert.NotNull(result.Assembled);
            var src = result.Assembled!.CsxSource;
            Assert.False(string.IsNullOrWhiteSpace(src),
                "Assembled CsxSource must not be empty or whitespace.");

            // ── 5. All three provider contributions present ──────────────────────
            // http.rest + db-assert.postgres contribute provider-id-prefixed helper
            // classes; script.csharp contributes no helper class but splices its code
            // body verbatim — proven by the INSERT statement text appearing in the CSX.
            Assert.Contains("HttpRest_Helpers", src, StringComparison.Ordinal);
            Assert.Contains("DbAssertPostgres_Helpers", src, StringComparison.Ordinal);
            Assert.Contains("INSERT INTO captured_hosts", src, StringComparison.Ordinal);

            // The shared substitution + secret helpers must both be present.
            Assert.Contains("Substitute_Helpers", src, StringComparison.Ordinal);
            Assert.Contains("Secret_Helpers", src, StringComparison.Ordinal);

            // ── 6. Secret header: reference token present, value ABSENT (§17) ─────
            // The Authorization header value is emitted as the RAW reference TOKEN, with
            // resolution deferred to execution time.  The secret VALUE must never be
            // baked into the assembled source — even though the env var is set.
            Assert.Contains(referenceToken, src, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretValue, src, StringComparison.Ordinal);

            // The http.rest helper resolves header values through
            // Secret_Helpers.ResolveTemplate(secrets, vars, …) — proving the header is
            // wrapped for execution-time secret + placeholder resolution in one pass.
            Assert.Contains("Secret_Helpers.ResolveTemplate(", src, StringComparison.Ordinal);

            // The Authorization header NAME must be plumbed verbatim into the request.
            Assert.Contains("Authorization", src, StringComparison.Ordinal);

            // ── 7. Capture arrays for the NON-SECRET field ───────────────────────
            // The http.rest emitter generates parallel arrays of capture var-names and
            // JSONPath expressions.  Both the var name and the JSONPath must appear; the
            // captured field is the non-secret $.hostname (never the echoed secret).
            Assert.Contains("\"hostname\"", src, StringComparison.Ordinal);
            Assert.Contains("\"$.hostname\"", src, StringComparison.Ordinal);
            Assert.Contains(VarKeys.CaptureStatusPrefix, src, StringComparison.Ordinal);

            // The fetch-id step must declare the capture; it must NOT capture the
            // Authorization / secret value into any var.
            var fetchStep = ast.Steps[0];
            Assert.Equal("fetch-id", fetchStep.Id);
            Assert.True(fetchStep.Capture.ContainsKey("hostname"),
                "fetch-id must capture the non-secret 'hostname' field.");
            Assert.Equal(CaptureFormat.JsonPath, fetchStep.Capture["hostname"].Format);
            Assert.Equal("$.hostname", fetchStep.Capture["hostname"].Expression);
            Assert.DoesNotContain("Authorization", fetchStep.Capture.Keys, StringComparer.Ordinal);
            Assert.DoesNotContain(
                "Authorization",
                fetchStep.Capture.Values.Select(e => e.Expression),
                StringComparer.Ordinal);

            // ── 8. db-assert query with substitution ─────────────────────────────
            // The db-assert emitter wraps the substitutable parameter value in
            // Substitute_Helpers.Resolve.  The {hostname} placeholder threads the
            // captured value into the parameter; the {expectedRegion} placeholder
            // threads the variables-block constant into the expect.row value.
            Assert.Contains("Substitute_Helpers.Resolve(Vars,", src, StringComparison.Ordinal);
            Assert.Contains("{hostname}", src, StringComparison.Ordinal);
            Assert.Contains("{expectedRegion}", src, StringComparison.Ordinal);
            // The JOIN query that asserts BOTH the seeded reference row and the inserted
            // row must be spliced into the db-assert block.
            Assert.Contains("reference_regions", src, StringComparison.Ordinal);

            // ── 9. No "using var" anywhere in the assembled source (§13.3.1) ──────
            Assert.DoesNotContain("using var", src, StringComparison.Ordinal);

            // ── 10. StepIds in declaration order ─────────────────────────────────
            Assert.Equal(3, result.Assembled!.StepIds.Count);
            Assert.Equal("fetch-id", result.Assembled.StepIds[0]);
            Assert.Equal("insert-row", result.Assembled.StepIds[1]);
            Assert.Equal("assert-row", result.Assembled.StepIds[2]);

            // ── 11. ResourcePlan: postgres requirement for "m2db" ────────────────
            var postgresEntry = result.ResourcePlan
                .FirstOrDefault(e => string.Equals(e.Requirement.Family, "postgres",
                    StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(postgresEntry);
            Assert.Equal("m2db", postgresEntry!.Requirement.Name);
            Assert.Equal("assert-row", postgresEntry.StepId);
            Assert.Contains("DbAssertPostgresProvider", postgresEntry.ProviderTypeName,
                StringComparison.Ordinal);

            // ── 12. CompileReferencePaths includes Npgsql and JsonPath.Net ────────
            var npgsqlLocation = typeof(Npgsql.NpgsqlConnection).Assembly.Location;
            var jsonPathLocation = typeof(Json.Path.JsonPath).Assembly.Location;

            Assert.NotEmpty(npgsqlLocation);
            Assert.NotEmpty(jsonPathLocation);
            Assert.Contains(npgsqlLocation, result.CompileReferencePaths, StringComparer.Ordinal);
            Assert.Contains(jsonPathLocation, result.CompileReferencePaths, StringComparer.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }
}
