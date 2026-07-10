// S05-D-01 — Milestone-M2 capstone, DOCKER-GATED end-to-end proof (MVP §8.2).
//
// The M2 exit criterion: a single .e2e.yaml using http.rest + db-assert.postgres +
// script.csharp compiles and runs end-to-end against a REAL local topology with —
//   • reference data SEEDED (a CREATE TABLE + INSERT applied before step 1),
//   • a credential supplied via ${secret:env/…} resolved AT EXECUTION TIME,
//   • captured state threaded between steps (the whoami $.hostname),
//   • the registry discovering each provider reflectively,
//   • the terminal renderer showing per-step verdicts,
// and the secret VALUE appearing NOWHERE in the output.
//
// This is the LIVE no-leak proof deferred from G-01 (the non-docker twin
// M2EndToEndCompileTests proves the same pipeline COMPILES without containers; the
// SecretResolutionPipelineTests prove no value is baked into the IL).
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~M2EndToEnd"
// Excluded from non-docker CI:  dotnet test --filter "requires!=docker"
//
// Design notes:
//   • This test assembly carries <IsAspireHost>true</IsAspireHost> + Aspire.AppHost.Sdk,
//     embedding the dcpclipath metadata SuiteTopology.StartAsync requires (R-1,
//     CLAUDE.md §Aspire).  appHostAssemblyName therefore points at THIS assembly.
//   • The seed SQL is written to a temp directory passed as seedBaseDirectory, so the
//     test owns the files and removes them in finally.
//   • A unique GUID-suffixed env-var name isolates the secret so parallel xUnit runs
//     cannot collide on the shared process environment (matches
//     SecretResolutionPipelineTests).
//   • The whoami capture reuses the working Sprint-4 approach: GET /api → $.hostname.

using System;
using System.IO;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Postgres;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.Script.Csharp;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Docker-gated Milestone-M2 capstone (S05-D-01): proves the full pipeline runs
/// end-to-end against a real seeded + reset topology with an execution-time secret,
/// per-step verdicts in the rendered output, and a no-leak guarantee for the secret
/// value.
/// </summary>
public sealed class M2EndToEndTests
{
    private readonly ITestOutputHelper _output;

    public M2EndToEndTests(ITestOutputHelper output) => _output = output;

    /// <summary>Short name of THIS assembly — it carries the DCP metadata.</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";

    /// <summary>The secret value the credential resolves to at execution time.</summary>
    private const string SecretValue = "m2-live-secret-bearer-token-9c2e4f";

    /// <summary>Step ids, asserted to appear in the rendered per-step output.</summary>
    private const string FetchStepId = "fetch-id";
    private const string InsertStepId = "insert-row";
    private const string AssertStepId = "assert-row";

    // ── Provider assemblies: all three Core providers, discovered reflectively ────
    private static readonly System.Reflection.Assembly[] s_providerAssemblies = new[]
    {
        typeof(HttpRestProvider).Assembly,
        typeof(DbAssertPostgresProvider).Assembly,
        typeof(ScriptCsharpProvider).Assembly,
    };

    // ── Seed SQL: a reference table + a known reference row (seeded before step 1) ─
    // The db-assert JOIN below resolves a hostname's region code to a region name via
    // this seeded table, so the assertion passes ONLY when the seed was applied.
    private const string ReferenceSql =
        "CREATE TABLE reference_regions (code TEXT PRIMARY KEY, region_name TEXT NOT NULL);\n" +
        "INSERT INTO reference_regions (code, region_name) VALUES ('emea', 'emea');\n" +
        "CREATE TABLE captured_hosts (hostname TEXT PRIMARY KEY, region TEXT NOT NULL);\n";

    /// <summary>
    /// The M2 capstone YAML, identical in shape to the non-docker compile-proof
    /// (M2EndToEndCompileTests.BuildM2Yaml) so both twins exercise the same scenario.
    /// </summary>
    private static string BuildM2Yaml(string envName) =>
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
    /// Full M2 end-to-end run: seed a reference table, supply a credential via
    /// <c>${secret:env/…}</c>, capture the whoami hostname, INSERT it via
    /// <c>script.csharp</c>, and assert the seeded reference row JOINs the inserted row
    /// — all in one scenario against a real topology.  Asserts <see cref="Verdict.Pass"/>,
    /// per-step verdicts in the rendered output, and that the secret VALUE leaks NOWHERE.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task M2Capstone_SeedSecretCaptureScriptDbAssert_Pass_NoSecretLeak()
    {
        // ── Unique, isolated secret env var (parallel-run safe) ───────────────────
        var envName = "VOUCHFX_M2_LIVE_SECRET_" + Guid.NewGuid().ToString("N");
        var referenceToken = $"${{secret:env/{envName}}}";

        // ── Temp seed base directory with the reference SQL fixture ───────────────
        var baseDir = Path.Combine(
            Path.GetTempPath(), "vouchfx-m2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        var fixturesDir = Path.Combine(baseDir, "fixtures");
        Directory.CreateDirectory(fixturesDir);
        var sqlRelative = "fixtures/reference.sql";
        await File.WriteAllTextAsync(Path.Combine(baseDir, sqlRelative), ReferenceSql);

        Environment.SetEnvironmentVariable(envName, SecretValue);

        var sw = new StringWriter();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var yaml = BuildM2Yaml(envName);

            // ── Run the full pipeline against the real seeded + reset topology ────
            var verdict = await ScenarioRunner.RunAsync(
                yamlText: yaml,
                scenarioName: "m2-capstone",
                providerAssemblies: s_providerAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: sw,
                seedBaseDirectory: baseDir,
                cancellationToken: cts.Token);

            var rendered = sw.ToString();
            _output.WriteLine("=== Terminal output ===");
            _output.WriteLine(rendered);
            _output.WriteLine($"Verdict: {verdict}");

            // ── 1. The scenario passes end-to-end ─────────────────────────────────
            Assert.Equal(Verdict.Pass, verdict);

            // ── 2. Per-step verdicts rendered: each step id + PASS ────────────────
            // The TerminalRenderer prints one "  step '<id>': PASS (… ms)" line per step.
            Assert.Contains(FetchStepId, rendered, StringComparison.Ordinal);
            Assert.Contains(InsertStepId, rendered, StringComparison.Ordinal);
            Assert.Contains(AssertStepId, rendered, StringComparison.Ordinal);
            Assert.Contains("PASS", rendered, StringComparison.Ordinal);
            // Each step's completion line carries the PASS verdict token; assert every
            // step id appears on a line that also carries PASS.
            AssertStepRenderedPass(rendered, FetchStepId);
            AssertStepRenderedPass(rendered, InsertStepId);
            AssertStepRenderedPass(rendered, AssertStepId);

            // ── 3. DEFENCE-IN-DEPTH: terminal renderer never surfaces the secret VALUE (§17) ─
            // Scanning the rendered terminal output for the secret VALUE is a
            // defence-in-depth check, NOT the load-bearing no-leak proof.  The
            // TerminalRenderer emits a step id + verdict + duration, a scenario/attempt
            // summary, and (since S08-G-02) a per-step captured-variable provenance thread
            // that surfaces captures and {placeholder}/${secret:…} substitutions.  Crucially
            // it never writes a resolved VALUE: a captured value is shown by name + JSONPath
            // only, and a secret-derived substitution is shown as its REFERENCE path with the
            // value "(redacted)".  So the VALUE being absent here confirms the renderer
            // surfaces no secret value — useful, but near-tautological as a leak proof.  The
            // load-bearing no-leak guarantees live elsewhere: (a) the IL/source no-bake
            // assertions in SecretResolutionPipelineTests (the value is never compiled into
            // the CSX source or the emitted IL), and (b) the reproducibility-envelope value-
            // absence check in §4 below (the emitted envelope carries the secret REFERENCE
            // hash, never the resolved value).
            Assert.DoesNotContain(SecretValue, rendered, StringComparison.Ordinal);
            // The S08-G-02 captured-variable provenance renderer
            // (TerminalRenderer.RenderProvenanceThread) intentionally surfaces the secret
            // REFERENCE — its authored ${secret:…} path (here env/<envName>) — in the
            // per-step provenance thread, with the RESOLVED VALUE shown as "(redacted)".
            // This is §17-compliant: the reference is the non-sensitive, authored form
            // (the reproducibility envelope HASHES the reference, never redacts it); only
            // the resolved value is the secret, and its absence is the load-bearing
            // invariant asserted by the rendered-output value-absence check above (and in
            // the reproducibility envelope at §4 below).  So
            // rather than asserting the reference never appears (it now legitimately does,
            // redacted), we assert it appears ONLY in redacted provenance form:
            //   (a) POSITIVE — the reference is surfaced with the value redacted; and
            //   (b) DEFENCE-IN-DEPTH — no render path ever emits the bare reference
            //       WITHOUT the "(redacted)" marker (so no current/future path can leak a
            //       value in its place, or alongside it).
            // The deeper no-bake guarantees still live in SecretResolutionPipelineTests
            // (the value is never compiled into the CSX source or emitted IL), and the
            // envelope value-absence check in §4 below proves the emitted envelope carries
            // only the reference hash, never the resolved value.

            // (a) POSITIVE: the provenance line surfaces the reference, value redacted.
            var redactedProvenance = referenceToken + " (redacted)";
            Assert.Contains(redactedProvenance, rendered, StringComparison.Ordinal);

            // (b) DEFENCE-IN-DEPTH: every rendered line carrying the reference MUST also
            // carry the "(redacted)" marker — the reference is never shown bare.
            var renderedLines = rendered.Split('\n');
            foreach (var line in renderedLines)
            {
                if (line.Contains(referenceToken, StringComparison.Ordinal))
                {
                    Assert.Contains("(redacted)", line, StringComparison.Ordinal);
                }
            }
            // Internal engine key prefixes must never leak into user-visible output.
            Assert.DoesNotContain(VarKeys.ConnectionsPrefix, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(VarKeys.CaptureStatusPrefix, rendered, StringComparison.Ordinal);

            // ── 4. Reproducibility-envelope: reference hash present, value absent ──
            // RunAsync renders the buffer but does not expose the raw JSON Lines event
            // buffer, so we reconstruct the envelope via the SAME internal the runner
            // uses (ScenarioRunner.BuildReproducibilityEnvelope) over the SAME AST + seed
            // base directory, then wrap it in the SAME ReproducibilityEnvelopeEvent via
            // the SAME EventStreamJson.ToLine — exactly as the runner buffers it
            // (mirrors ReproducibilityEnvelopeRunnerTests).  This proves the envelope the
            // run emits carries the secret REFERENCE hash but not the value.
            // This reconstruction is a faithful PROXY for the envelope the live run
            // emits: the runner builds its envelope by calling the SAME internal over the
            // SAME AST + seed base directory (ScenarioRunner ~line 982), so asserting over
            // this reconstruction is equivalent to asserting over the runner's own
            // envelope.  We reconstruct rather than read the emitted line because RunAsync
            // currently exposes no event-buffer/sink to assert over the actual emitted
            // JSON Lines.
            // TODO (follow-up): expose an event-sink/callback on RunAsync/RunSuiteAsync so
            // a future test can assert over the actual buffered event lines, closing this
            // proxy gap and the related limitation noted in the Sprint-4 capstone.
            var registry = StepKindRegistry.BuildAndFreeze(s_providerAssemblies);
            var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry);
            var envelope = ScenarioRunner.BuildReproducibilityEnvelope(ast, baseDir);
            var envelopeLine = EventStreamJson.ToLine(new ReproducibilityEnvelopeEvent
            {
                RunId = "m2-capstone-envelope",
                Timestamp = DateTimeOffset.UtcNow,
                ScenarioId = "m2-capstone",
                EnvSchemaVersion = envelope.SchemaVersion,
                SecretReferences = envelope.SecretReferences,
                Fixtures = envelope.Fixtures,
            });

            var evt = EventStreamJson.FromLine<ReproducibilityEnvelopeEvent>(envelopeLine);
            Assert.Equal(EventTypes.ReproducibilityEnvelope, evt.Type);

            // The envelope records the secret REFERENCE (one digest, source "env") with a
            // reference hash — and never the resolved value.
            var secretDigest = Assert.Single(evt.SecretReferences);
            Assert.Equal("env", secretDigest.Source);
            Assert.False(string.IsNullOrWhiteSpace(secretDigest.ReferenceHash),
                "The envelope must carry a non-empty secret reference hash.");
            Assert.DoesNotContain(SecretValue, envelopeLine, StringComparison.Ordinal);

            // The seeded fixture is recorded with a content hash, never its raw body.
            var fixtureDigest = Assert.Single(evt.Fixtures);
            Assert.Equal(sqlRelative, fixtureDigest.Reference);
            Assert.False(string.IsNullOrWhiteSpace(fixtureDigest.ContentHash),
                "The envelope must carry a non-empty seed fixture content hash.");
            Assert.DoesNotContain("reference_regions", envelopeLine, StringComparison.Ordinal);

            _output.WriteLine(
                "M2 PASS: seed + secret + capture + script + db-assert composed; " +
                "secret value leaked nowhere; reproducibility envelope carries the " +
                "reference hash, not the value.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
            try { Directory.Delete(baseDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Asserts that the rendered output contains a line carrying both
    /// <paramref name="stepId"/> and the <c>PASS</c> verdict token — i.e. the step's
    /// completed line shows a PASS verdict.
    /// </summary>
    private static void AssertStepRenderedPass(string rendered, string stepId)
    {
        var hasPassLine = rendered
            .Split('\n')
            .Any(line =>
                line.Contains(stepId, StringComparison.Ordinal) &&
                line.Contains("PASS", StringComparison.Ordinal));

        Assert.True(hasPassLine,
            $"Expected a rendered line containing step '{stepId}' and verdict PASS.\n" +
            $"Actual output:\n{rendered}");
    }
}
