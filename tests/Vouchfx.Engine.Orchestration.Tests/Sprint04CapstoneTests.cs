// S04 Capstone — Sprint 4 integration proof (docker-gated).
//
// Proves that all three Sprint 4 features compose end-to-end:
//   1. http.rest      GET /api from traefik/whoami → capture $.hostname → Vars["hostname"].
//   2. script.csharp  Opens Npgsql via the staged conn string; INSERTs one row keyed by
//                     the captured hostname (PRIMARY KEY constraint).
//   3. db-assert.postgres  Asserts rowCount==1 WHERE key = @k (k={hostname}), and
//                          status = {expectedStatus} (from variables block).
//
// Run as a 2-scenario suite against a single build-once topology.
//
// WHY RESPAWN IS LOAD-BEARING:
//   Both scenarios INSERT into key_captures using the SAME hostname (the container hostname
//   is constant within one topology lifetime) as the PRIMARY KEY.  If Respawn did NOT wipe
//   the row between scenarios, the second scenario's INSERT would throw a primary-key
//   violation → script step throws → scenario 2 receives Inconclusive, making
//   suite Verdict != Pass.  The suite Verdict == Pass therefore proves the reset fired.
//
// PROVENANCE ASSERTIONS (G-01):
//   The structured CapturedVar / SubstitutionRef assertions live in Sprint04CapstoneCompileTests
//   (non-docker, same namespace) which calls ProviderPipeline.Compile directly and inspects
//   the assembled CSX source.
//
//   Here we assert:
//   (a) the rendered terminal output contains "fetch-id" and "assert-row" (steps were run).
//   (b) internal engine key prefixes (conn::, __capture_status::) are absent from the output.
//   (c) no JSON property named "value" appears in the output.
//
// INTEGRATION GAP NOTE:
//   ScenarioRunner does not expose the raw JSON Lines event buffer externally; it only
//   writes rendered terminal output to the TextWriter.  Full structured CapturedVar /
//   SubstitutionRef assertions therefore require ProviderPipeline.Compile (non-docker)
//   rather than inspection of the live event stream.  This is reported as a known
//   limitation of the current ScenarioRunner API (no per-event callback / event sink).
//
// Run with: dotnet test --filter "requires=docker&FullyQualifiedName~Sprint04Capstone"

using System.IO;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Postgres;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.Script.Csharp;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Sprint 4 integration capstone: wires HTTP capture → script.csharp INSERT →
/// db-assert.postgres across two scenarios to prove Respawn reset and end-to-end
/// composition of all three Sprint 4 providers (S04-B-02/B-03/G-01/A-02).
/// </summary>
public sealed class Sprint04CapstoneTests
{
    private readonly ITestOutputHelper _output;

    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Step id of the http.rest capture step.</summary>
    private const string FetchStepId = "fetch-id";

    /// <summary>Step id of the db-assert step.</summary>
    private const string AssertStepId = "assert-row";

    public Sprint04CapstoneTests(ITestOutputHelper output) => _output = output;

    // ── Provider assemblies ───────────────────────────────────────────────────

    private static readonly System.Reflection.Assembly[] s_providerAssemblies = new[]
    {
        typeof(HttpRestProvider).Assembly,
        typeof(DbAssertPostgresProvider).Assembly,
        typeof(ScriptCsharpProvider).Assembly,
    };

    // ── Capstone YAML ─────────────────────────────────────────────────────────

    /// <summary>
    /// The capstone <c>.e2e.yaml</c> string shared by both suite scenarios.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Step 1 (<c>fetch-id</c>): GET <c>/api</c> from <c>traefik/whoami</c>; captures
    /// <c>$.hostname</c> → <c>Vars["hostname"]</c>.
    /// </para>
    /// <para>
    /// Step 2 (<c>insert-row</c>): <c>script.csharp</c> reads <c>Vars["conn::capdb"]</c>
    /// and the captured hostname, creates the <c>key_captures</c> table (idempotent),
    /// and INSERTs one row.  The PRIMARY KEY on <c>key</c> ensures that a second INSERT
    /// with the same hostname throws unless Respawn reset the table.
    /// </para>
    /// <para>
    /// Step 3 (<c>assert-row</c>): <c>db-assert.postgres</c> with
    /// <c>parameters: { k: "{hostname}" }</c> and <c>expect.row.status: "{expectedStatus}"</c>
    /// exercises substitution from a captured variable AND from the <c>variables</c> block
    /// simultaneously.
    /// </para>
    /// </remarks>
    internal const string CapstoneYaml = """
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

    // ── Docker capstone: 2-scenario suite, Respawn reset load-bearing ─────────

    /// <summary>
    /// Runs the capstone 3-step chain as a 2-scenario suite via
    /// <see cref="ScenarioRunner.RunSuiteAsync"/>.  Both scenarios must Pass,
    /// which is only possible when Respawn reset fires between them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Respawn proof:</b> the <c>traefik/whoami</c> container returns the same
    /// hostname for all requests within one topology lifetime.  The <c>script.csharp</c>
    /// step INSERTs that hostname as the PRIMARY KEY.  Without Respawn, scenario 2's
    /// INSERT would throw a primary-key violation (duplicate key), causing the script
    /// step to throw → scenario 2 Inconclusive → suite Verdict != Pass.  The
    /// <c>Assert.Equal(Verdict.Pass, result.Verdict)</c> assertion is therefore a
    /// direct proof that Respawn fired.
    /// </para>
    /// <para>
    /// <b>No-value-leak check:</b> the rendered terminal output must not contain
    /// internal engine key prefixes (<c>conn::</c>, <c>__capture_status::</c>) or
    /// raw JSON <c>"value"</c> properties.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Sprint04Capstone_RunSuiteAsync_BothScenariosPass_RespawnLoadBearing()
    {
        // ── Parse YAML through the front end (proves authoring layer) ─────────
        var registry = StepKindRegistry.BuildAndFreeze(s_providerAssemblies);
        var doc = YamlDocumentParser.Parse(CapstoneYaml);
        var ast = AstBuilder.Build(doc, registry);

        // Two copies of the same scenario AST.
        var scenarios = new[] { ast, ast };
        var scenarioNames = new[] { "capstone-s1", "capstone-s2" };
        var yamlTexts = new[] { CapstoneYaml, CapstoneYaml };

        var sw = new StringWriter();
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        SuiteResult result;
        try
        {
            result = await ScenarioRunner.RunSuiteAsync(
                scenarios: scenarios,
                scenarioNames: scenarioNames,
                yamlTexts: yamlTexts,
                providerAssemblies: s_providerAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: sw,
                cancellationToken: cts.Token);
        }
        finally
        {
            cts.Dispose();
        }

        var rendered = sw.ToString();
        _output.WriteLine("=== Terminal output ===");
        _output.WriteLine(rendered);
        _output.WriteLine($"Suite verdict: {result.Verdict}");

        foreach (var (name, verdict) in result.ScenarioVerdicts)
        {
            _output.WriteLine($"  Scenario '{name}': {verdict}");
        }

        // ── Suite-level verdict: both scenarios must Pass ─────────────────────
        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.Equal(2, result.ScenarioVerdicts.Count);
        Assert.Equal(Verdict.Pass, result.ScenarioVerdicts[0].Verdict);
        Assert.Equal(Verdict.Pass, result.ScenarioVerdicts[1].Verdict);

        _output.WriteLine(
            "Respawn proof: both scenarios Pass → Respawn reset fired between them.");

        // ── Rendered output contains expected step IDs ─────────────────────────
        Assert.Contains(FetchStepId, rendered, StringComparison.Ordinal);
        Assert.Contains(AssertStepId, rendered, StringComparison.Ordinal);
        Assert.Contains("PASS", rendered, StringComparison.OrdinalIgnoreCase);

        // ── No value leak in terminal output ──────────────────────────────────
        // Internal engine key prefixes must not appear in user-visible output.
        Assert.DoesNotContain(VarKeys.ConnectionsPrefix, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(VarKeys.CaptureStatusPrefix, rendered, StringComparison.Ordinal);

        // No JSON "value" property (captured/substituted values are never emitted).
        Assert.DoesNotContain("\"value\"", rendered, StringComparison.Ordinal);

        _output.WriteLine("No-value-leak assertion: PASS.");
    }
}
