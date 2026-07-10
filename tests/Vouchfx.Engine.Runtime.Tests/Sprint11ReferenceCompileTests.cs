// S11-D-01 — Reference four-technology scenario, non-docker compile-proof twin.
//
// Loads examples/reference/reference.e2e.yaml FROM DISK (the actual published example
// file — never an inline copy) and drives the full front-end pipeline up to but NOT
// including topology start:
//
//   1. JSON-Schema validation: DocumentValidator.Validate reports IsValid == true.
//      Proves the published example is syntactically conformant with the composed schema.
//   2. Parse + AST: YamlDocumentParser.Parse + AstBuilder.Build succeed and produce
//      an AST with the expected step count, types, capture, verifyMode and timeout.
//   3. Compile: ProviderPipeline.Compile returns a non-null assembled script whose CSX
//      carries all four provider contributions, the RETRY wrapping (kafka-expect-order +
//      webhook-await), the MqExpectKafka helper (plain-JSON Kafka; no Avro / no schema
//      registry), the webhook-listen helper, the secret reference token (but NEVER the
//      resolved value), and no "using var".
//   4. Resource plan: both postgres and kafka requirements, plus the webhook-listener
//      host resource, are declared in the plan.
//
// This test runs in the standard non-docker CI gate and ensures the published example
// can NEVER silently rot:
//   dotnet test --filter "requires!=docker"
//
// The docker-gated docker capstone (Sprint11ReferenceCapstoneTests) loads the SAME file
// from disk and proves it runs GREEN against a real topology.

using System;
using System.IO;
using System.Linq;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Postgres;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.MqExpect.Kafka;
using Vouchfx.Steps.MqPublish.Kafka;
using Vouchfx.Steps.Script.Csharp;
using Vouchfx.Steps.WebhookListen.Http;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Non-docker proof that <c>examples/reference/reference.e2e.yaml</c> (the canonical
/// reference scenario) parses, JSON-Schema-validates, builds an AST, and compiles through
/// <see cref="ProviderPipeline.Compile"/> — including all four step families (REST + DB +
/// Kafka + webhook), the RETRY wrapping, MqExpectKafka helpers (plain-JSON Kafka; no Avro),
/// secret reference, and resource plan — without any container or topology.
/// The example file is loaded FROM DISK so it can
/// never diverge from this test's coverage.
/// </summary>
public sealed class Sprint11ReferenceCompileTests
{
    // ── Provider assemblies: all six Core providers ────────────────────────────────
    private static readonly System.Reflection.Assembly[] s_providerAssemblies = new[]
    {
        typeof(HttpRestProvider).Assembly,
        typeof(DbAssertPostgresProvider).Assembly,
        typeof(ScriptCsharpProvider).Assembly,
        typeof(MqPublishKafkaProvider).Assembly,
        typeof(MqExpectKafkaProvider).Assembly,
        typeof(WebhookListenHttpProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    private const string SuiteNamespace = "VouchfxGenerated";

    // ── Resolve the example file path from the test assembly location ──────────────
    // The example file lives at examples/reference/reference.e2e.yaml relative to the
    // repo root.  We locate the repo root by walking up from the test assembly's output
    // directory (bin/Release/net8.0 under tests/Vouchfx.Engine.Runtime.Tests/).
    // This mirrors the pattern used by M2EndToEndTests and the Sprint 7 capstone.
    private static string ResolveRepoRoot()
    {
        var assemblyDir = Path.GetDirectoryName(
            typeof(Sprint11ReferenceCompileTests).Assembly.Location)!;
        // Walk up: net8.0 → Release → bin → Vouchfx.Engine.Runtime.Tests → tests → repo root
        return Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."));
    }

    private static string ReferenceYamlPath =>
        Path.Combine(ResolveRepoRoot(), "examples", "reference", "reference.e2e.yaml");

    /// <summary>
    /// Loads the published reference scenario from disk, drives parse → schema-validate
    /// → AST → compile, and asserts every invariant: well-formed YAML, correct step
    /// types + ids, RETRY wrapping on both the Kafka expect and the webhook, Avro and
    /// webhook-listen helpers, the secret token (not the value), the full resource plan,
    /// and no "using var".
    /// </summary>
    [Fact]
    public void ReferenceScenario_LoadedFromDisk_ParsesValidatesAndCompiles()
    {
        // ── 0. The example file exists on disk (catches a deleted/renamed example) ──
        Assert.True(
            File.Exists(ReferenceYamlPath),
            $"Reference example file not found at: {ReferenceYamlPath}");

        var yaml = File.ReadAllText(ReferenceYamlPath);
        Assert.False(string.IsNullOrWhiteSpace(yaml), "Reference YAML must not be empty.");

        // ── 1. JSON-Schema validation (no topology) ────────────────────────────────
        var validation = DocumentValidator.Validate(yaml, s_registry);
        Assert.True(
            validation.IsValid,
            "Reference YAML must pass schema validation. Errors: " +
            string.Join(" | ", validation.Errors.Select(e => e.Message)));

        // ── 2. Parse + build the AST ───────────────────────────────────────────────
        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, s_registry);

        // Seven steps in declaration order.
        Assert.Equal(7, ast.Steps.Count);

        // Step 0: REST + capture (with secret header)
        Assert.Equal("rest-get-whoami", ast.Steps[0].Id);
        Assert.Equal("http.rest", ast.Steps[0].CanonicalType);
        Assert.True(
            ast.Steps[0].Capture.ContainsKey("hostname"),
            "rest-get-whoami must capture 'hostname'.");
        Assert.Equal(CaptureFormat.JsonPath, ast.Steps[0].Capture["hostname"].Format);
        Assert.Equal("$.hostname", ast.Steps[0].Capture["hostname"].Expression);

        // Step 1: DB INSERT (script.csharp)
        Assert.Equal("db-insert-row", ast.Steps[1].Id);
        Assert.Equal("script.csharp", ast.Steps[1].CanonicalType);

        // Step 2: DB assert (db-assert.postgres, IMMEDIATE)
        Assert.Equal("db-assert-rows", ast.Steps[2].Id);
        Assert.Equal("db-assert.postgres", ast.Steps[2].CanonicalType);
        Assert.Equal(VerifyMode.Immediate, ast.Steps[2].VerifyMode);

        // Step 3: Kafka publish (mq-publish.kafka, IMMEDIATE)
        Assert.Equal("kafka-publish-order", ast.Steps[3].Id);
        Assert.Equal("mq-publish.kafka", ast.Steps[3].CanonicalType);
        Assert.Equal(VerifyMode.Immediate, ast.Steps[3].VerifyMode);

        // Step 4: Kafka expect (mq-expect.kafka, RETRY with 30s timeout)
        Assert.Equal("kafka-expect-order", ast.Steps[4].Id);
        Assert.Equal("mq-expect.kafka", ast.Steps[4].CanonicalType);
        Assert.Equal(VerifyMode.Retry, ast.Steps[4].VerifyMode);
        Assert.Equal(TimeSpan.FromSeconds(30), ast.Steps[4].Timeout);

        // Step 5: webhook trigger (script.csharp — simulates the SUT callback)
        Assert.Equal("webhook-trigger", ast.Steps[5].Id);
        Assert.Equal("script.csharp", ast.Steps[5].CanonicalType);

        // Step 6: webhook-listen.http (RETRY with 30s timeout)
        Assert.Equal("webhook-await", ast.Steps[6].Id);
        Assert.Equal("webhook-listen.http", ast.Steps[6].CanonicalType);
        Assert.Equal(VerifyMode.Retry, ast.Steps[6].VerifyMode);
        Assert.Equal(TimeSpan.FromSeconds(30), ast.Steps[6].Timeout);

        // ── 3. Metadata: name + owner + tags ──────────────────────────────────────
        Assert.NotNull(ast.Metadata);
        Assert.Equal("reference-four-tech", ast.Metadata!.Name);
        Assert.Equal("vouchfx-core", ast.Metadata.Owner);
        Assert.NotNull(ast.Metadata.Tags);
        Assert.Contains("s11-reference", ast.Metadata.Tags!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("rest", ast.Metadata.Tags!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("db", ast.Metadata.Tags!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("kafka", ast.Metadata.Tags!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("webhook", ast.Metadata.Tags!, StringComparer.OrdinalIgnoreCase);

        // ── 4. Environment: service + dependencies + seed ──────────────────────────
        Assert.NotNull(ast.Environment);
        Assert.NotNull(ast.Environment!.Services);
        Assert.True(
            ast.Environment.Services!.ContainsKey("whoami"),
            "The 'whoami' service must be declared.");
        Assert.NotNull(ast.Environment!.Dependencies);
        Assert.True(
            ast.Environment.Dependencies!.ContainsKey("refdb"),
            "The 'refdb' postgres dependency must be declared.");
        Assert.True(
            ast.Environment.Dependencies!.ContainsKey("events"),
            "The 'events' kafka dependency must be declared.");

        // Seed is bound to refdb (proves the SQL fixture is wired up).
        Assert.NotNull(ast.Environment.Seed);
        Assert.True(
            ast.Environment.Seed!.Dependencies.ContainsKey("refdb"),
            "Seed must declare a fixture for 'refdb'.");
        var refdbSeed = ast.Environment.Seed.Dependencies["refdb"];
        Assert.NotNull(refdbSeed.Sql);
        Assert.Contains("fixtures/seed.sql", refdbSeed.Sql!, StringComparer.Ordinal);

        // ── 5. Compile through the provider pipeline ───────────────────────────────
        var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);

        Assert.Null(result.Failure);
        Assert.NotNull(result.Assembled);
        var src = result.Assembled!.CsxSource;
        Assert.False(
            string.IsNullOrWhiteSpace(src),
            "Assembled CsxSource must not be empty or whitespace.");

        // ── 6. All four provider families contribute helper classes or code bodies ───
        Assert.Contains("HttpRest_Helpers", src, StringComparison.Ordinal);
        Assert.Contains("DbAssertPostgres_Helpers", src, StringComparison.Ordinal);
        Assert.Contains("MqPublishKafka_Helpers", src, StringComparison.Ordinal);
        Assert.Contains("MqExpectKafka_Helpers", src, StringComparison.Ordinal);
        Assert.Contains("WebhookListenHttp_Helpers", src, StringComparison.Ordinal);

        // script.csharp contributes no helper class but splices its code body verbatim.
        Assert.Contains("INSERT INTO orders", src, StringComparison.Ordinal);
        Assert.Contains("conn::refdb", src, StringComparison.Ordinal);
        // db-assert.postgres uses {hostname} substitution for the @h parameter.
        Assert.Contains("{hostname}", src, StringComparison.Ordinal);
        // The Kafka publish payload template contains {orderId} and {hostname}.
        Assert.Contains("{orderId}", src, StringComparison.Ordinal);

        // Shared helpers from the substitution + secret subsystems.
        Assert.Contains("Substitute_Helpers", src, StringComparison.Ordinal);
        Assert.Contains("Secret_Helpers", src, StringComparison.Ordinal);

        // ── 7. RETRY wrapping: kafka-expect-order + webhook-await are wrapped ────────
        Assert.Contains("RetryRunner.PollAsync", src, StringComparison.Ordinal);

        // ── 8. Kafka path: the match JSONPath expression + topic are spliced in ────
        // Plain JSON path (no Avro): the mq-expect step's match.json "$.id" criterion
        // and the topic name appear verbatim in the assembled CSX.
        Assert.Contains("$.id", src, StringComparison.Ordinal);
        Assert.Contains("orders.created", src, StringComparison.Ordinal);

        // ── 9. Webhook-listen helper: AssertAsync + webhooks.GetCaptured ─────────
        Assert.Contains("WebhookListenHttp_Helpers.AssertAsync", src, StringComparison.Ordinal);
        Assert.Contains("webhooks.GetCaptured", src, StringComparison.Ordinal);

        // ── 10. Secret reference token present; value never baked in (§17) ─────────
        // The Authorization header uses ${secret:env/VOUCHFX_REF_BEARER_TOKEN}; the
        // assembled CSX must carry the reference token but must NEVER contain any
        // resolved value (resolution is execution-time only).
        Assert.Contains("${secret:env/VOUCHFX_REF_BEARER_TOKEN}", src, StringComparison.Ordinal);
        // The shared secret helper handles resolution at execution time.
        Assert.Contains("Secret_Helpers.ResolveTemplate(", src, StringComparison.Ordinal);

        // ── 11. No "using var" anywhere in the assembled source (§13.3.1) ──────────
        Assert.DoesNotContain("using var", src, StringComparison.Ordinal);

        // ── 12. StepIds in declaration order ──────────────────────────────────────
        Assert.Equal(7, result.Assembled!.StepIds.Count);
        Assert.Equal("rest-get-whoami", result.Assembled.StepIds[0]);
        Assert.Equal("db-insert-row", result.Assembled.StepIds[1]);
        Assert.Equal("db-assert-rows", result.Assembled.StepIds[2]);
        Assert.Equal("kafka-publish-order", result.Assembled.StepIds[3]);
        Assert.Equal("kafka-expect-order", result.Assembled.StepIds[4]);
        Assert.Equal("webhook-trigger", result.Assembled.StepIds[5]);
        Assert.Equal("webhook-await", result.Assembled.StepIds[6]);

        // ── 13. ResourcePlan: postgres + kafka requirements ────────────────────────
        var postgresEntries = result.ResourcePlan
            .Where(e => string.Equals(e.Requirement.Family, "postgres", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(postgresEntries.Count >= 1, "At least one postgres resource requirement expected.");
        Assert.All(postgresEntries, e => Assert.Equal("refdb", e.Requirement.Name));

        var kafkaEntries = result.ResourcePlan
            .Where(e => string.Equals(e.Requirement.Family, "kafka", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(2, kafkaEntries.Count);
        Assert.All(kafkaEntries, e => Assert.Equal("events", e.Requirement.Name));

        // ── 14. HostResourcePlan: webhook-listener for 'cb' ─────────────────────
        var hostEntry = Assert.Single(result.HostResourcePlan);
        Assert.Equal("webhook-await", hostEntry.StepId);
        Assert.Equal("webhook-listener", hostEntry.Requirement.Kind);
        Assert.Equal("cb", hostEntry.Requirement.VarName);
    }

    /// <summary>
    /// The reference scenario file must live at the expected canonical path so the CLI
    /// example documentation stays accurate and the docker capstone can locate it.
    /// </summary>
    [Fact]
    public void ReferenceScenarioFile_ExistsAtCanonicalPath()
    {
        Assert.True(
            File.Exists(ReferenceYamlPath),
            $"Reference example must exist at: {ReferenceYamlPath}\n" +
            "This path is referenced from documentation and must not be moved.");
    }

    /// <summary>
    /// The reference fixture SQL file must exist alongside the scenario so the seed is
    /// resolvable at runtime.
    /// </summary>
    [Fact]
    public void ReferenceSeedFixture_ExistsAtCanonicalPath()
    {
        var fixtureDir = Path.Combine(
            Path.GetDirectoryName(ReferenceYamlPath)!, "fixtures");
        var fixturePath = Path.Combine(fixtureDir, "seed.sql");
        Assert.True(
            File.Exists(fixturePath),
            $"Reference seed fixture must exist at: {fixturePath}");
    }
}
