// S07 Capstone — non-docker compile-proof twin (Sprint 7, Milestone M3 path).
//
// The docker-gated twin (Sprint07CapstoneTests, same project) runs the SAME two
// scenarios against a REAL postgres topology + the host-owned webhook listener and
// proves the live flow: an http.rest POST hands the callback URL to a SUT (here a
// script.csharp-simulated SUT — see that file's header for why), the SUT calls back,
// webhook-listen.http RETRY captures it → Pass; and a wrong-expected db-assert.postgres
// renders a relational expected-vs-observed diff → Fail.  This twin proves — without any
// container — that BOTH Sprint 7 capstone YAMLs are well-formed end-to-end up to (NOT
// including) topology start, and exercises the three S7 feature surfaces directly:
//
//   1. The front end ACCEPTS each YAML: DocumentValidator.Validate(yaml, registry)
//      reports IsValid == true (the webhook-listen.http listener+match fields, the
//      http.rest XPath capture, and the db-assert.postgres expect block all pass the
//      composed schema), AstBuilder.Build succeeds, and the webhook step parses as
//      VerifyMode.Retry with its declared timeout.
//   2. ProviderPipeline.Compile returns a non-null assembled script whose CSX contains:
//      (a) RetryRunner.PollAsync — the RETRY webhook-listen step was wrapped in the
//          engine-owned poll loop (authors never write Thread.Sleep, §7);
//      (b) WebhookListenHttp_Helpers + Webhooks.GetCaptured — the listener-assert path;
//      (c) the http.rest XPath capture (kind "xpath" emitted in the capture-kinds array)
//          and the LATER step's {ref} substitution spliced verbatim;
//      (d) the db-assert.postgres helper + its failing expectation in the plan.
//   3. The webhook step contributes a "webhook-listener" HOST resource (HostResourcePlan);
//      the db-assert step contributes the "events"-style postgres ResourceRequirement.
//   4. DbAssertPostgresProvider implements IStepDiffRenderer and renders a relational
//      diff (column/expected/actual table) from the EXACT Fail observation its helper
//      emits — the render that the live twin asserts appears under the failed step.
//
// It mirrors M2EndToEndCompileTests / Sprint06CapstoneCompileTests: parse each .e2e.yaml
// through the front end, build the frozen StepKindRegistry over the SIX Core provider
// assemblies, validate, build the AST, and call ProviderPipeline.Compile — asserting on
// the PipelineResult and the assembled CSX, never starting a topology.
//
// Why this lives in Platform.Engine.Runtime.Tests (and not the Orchestration test
// project): ProviderPipeline, PipelineResult, ResourcePlanEntry, and HostResourcePlan are
// INTERNAL to Platform.Engine.Runtime and visible here via InternalsVisibleTo
// (Platform.Engine.Runtime.csproj → Platform.Engine.Runtime.Tests).  The docker capstone
// (Sprint07CapstoneTests) uses only the PUBLIC ScenarioRunner.RunAsync, so it shares this
// project too — exactly as the M2 / Sprint-6 capstone pairs already do.
//
// Selection-by-tag (Sprint 7 exit criterion) is a CLI concern: ScenarioSelector is
// internal to Vouchfx.Cli and is unreachable from this project, and the selection
// language has its own dedicated tests (Vouchfx.Cli.Tests/ScenarioSelectorTests).  This
// companion therefore proves only that BOTH capstone scenarios carry the shared selection
// tag in their AST metadata (so a `--tag s7-capstone` run would pick exactly these two),
// and leaves the selection-mechanics proof to the CLI tests.

using System;
using System.Linq;
using Platform.Engine.Abstractions;
using Platform.Engine.Authoring;
using Platform.Engine.Compilation.Schema;
using Platform.Engine.Runtime;
using Platform.Sdk;
using Platform.Steps.DbAssert.Postgres;
using Platform.Steps.HttpRest;
using Platform.Steps.Script.Csharp;
using Platform.Steps.WebhookListen.Http;
using Xunit;

namespace Platform.Engine.Runtime.Tests;

/// <summary>
/// Non-docker proof that BOTH Sprint 7 capstone YAMLs (the webhook-callback +
/// XPath-thread scenario and the relational-diff scenario) parse, JSON-Schema-validate,
/// build an AST, and compile through <see cref="ProviderPipeline.Compile"/> — including
/// the RETRY wrapping of the <c>webhook-listen.http</c> step, the <c>http.rest</c> XPath
/// capture + forward substitution, the <c>db-assert.postgres</c> failing expectation, and
/// the <see cref="IStepDiffRenderer"/> relational diff — without any container or topology.
/// </summary>
public sealed class Sprint07CapstoneCompileTests
{
    // ── Provider assemblies: the SIX Core providers (incl. webhook-listen.http) ─────
    // The capstone wires http.rest + webhook-listen.http + db-assert.postgres +
    // script.csharp; the two Kafka providers are included so the registry the compile
    // companion freezes is the SAME six-provider registry the engine ships, proving the
    // full Core set composes without a kind collision.
    private static readonly System.Reflection.Assembly[] s_providerAssemblies = new[]
    {
        typeof(HttpRestProvider).Assembly,
        typeof(WebhookListenHttpProvider).Assembly,
        typeof(DbAssertPostgresProvider).Assembly,
        typeof(ScriptCsharpProvider).Assembly,
        typeof(Platform.Steps.MqPublish.Kafka.MqPublishKafkaProvider).Assembly,
        typeof(Platform.Steps.MqExpect.Kafka.MqExpectKafkaProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    private const string SuiteNamespace = "VouchfxGenerated";

    /// <summary>The shared selection tag both capstone scenarios carry.</summary>
    internal const string CapstoneTag = "s7-capstone";

    // ── Capstone YAMLs (byte-identical to the docker twin Sprint07CapstoneTests) ────

    /// <summary>
    /// The WEBHOOK + XPATH-THREAD capstone scenario (Pass, live).  Three steps:
    /// <list type="number">
    ///   <item>
    ///     <c>fetch-ref</c> (<c>http.rest</c> GET, IMMEDIATE): GETs an XML document from the
    ///     SUT and captures <c>//order/id</c> via an <b>XPath</b> capture into
    ///     <c>Vars["ref"]</c> — the S07 XPath-capture surface.
    ///   </item>
    ///   <item>
    ///     <c>trigger-callback</c> (<c>http.rest</c> POST, IMMEDIATE): POSTs to the SUT a
    ///     body carrying BOTH the captured <c>{ref}</c> (proving the XPath value threads
    ///     FORWARD into a later step) and the host listener's callback URL <c>{cb}</c> (the
    ///     plain-key staging of the listener, so substitution can reach it).
    ///   </item>
    ///   <item>
    ///     <c>await-callback</c> (<c>webhook-listen.http</c>, RETRY): asserts an inbound
    ///     webhook arrived at the <c>cb</c> listener whose path carries the threaded
    ///     <c>{ref}</c> — the engine wraps this idempotent scan in RetryRunner.PollAsync.
    ///   </item>
    /// </list>
    /// The <c>target: cb</c> on the POST step is the listener's <c>svc::cb</c> staging — an
    /// http.rest step can dial the listener like any discovered service, and the listener
    /// binds 0.0.0.0 so a real containerised SUT could call back over host.docker.internal
    /// (the live twin documents what stays Docker-only).
    /// </summary>
    internal const string WebhookYaml = """
        metadata:
          name: sprint-07-capstone-webhook
          owner: vouchfx-core
          tags: [s7-capstone, webhook]
          description: http.rest XPath capture threads {ref} forward; an inbound webhook is captured by a webhook-listen.http RETRY step. Proves S7 webhook + XPath end-to-end.

        environment:
          services:
            sut:
              image: traefik/whoami
              httpPort: 80

        steps:
          - id: fetch-ref
            type: http.rest
            target: sut
            method: GET
            path: /orders/latest.xml
            verifyMode: IMMEDIATE
            capture:
              ref:
                xpath: "//order/id"

          - id: trigger-callback
            type: http.rest
            target: cb
            method: POST
            path: /trigger
            verifyMode: IMMEDIATE
            body:
              callbackUrl: "{cb}"
              orderRef: "{ref}"

          - id: await-callback
            type: webhook-listen.http
            listener: cb
            verifyMode: RETRY
            timeout: 30s
            match:
              method: POST
              path: "/callbacks/{ref}"
        """;

    /// <summary>
    /// The RELATIONAL-DIFF capstone scenario (Fail, live).  A single
    /// <c>db-assert.postgres</c> step whose <c>expect.row</c> selects a column value the
    /// seeded row does NOT carry, so the step Fails with a column-mismatch observation and
    /// the engine renders a relational expected-vs-observed diff under it via the
    /// provider's <see cref="IStepDiffRenderer"/> (the S07 diff surface).
    /// </summary>
    internal const string DiffYaml = """
        metadata:
          name: sprint-07-capstone-diff
          owner: vouchfx-core
          tags: [s7-capstone, diff]
          description: A failing db-assert.postgres renders a relational expected-vs-observed diff. Proves the S7 IStepDiffRenderer surface.

        environment:
          dependencies:
            shop:
              type: postgres

        steps:
          - id: assert-status
            type: db-assert.postgres
            target: shop
            query: >-
              SELECT status FROM orders WHERE id = 1
            verifyMode: IMMEDIATE
            expect:
              rowCount: 1
              row:
                status: SHIPPED
        """;

    // ── The compile tests ──────────────────────────────────────────────────────────

    /// <summary>
    /// Parses, validates, builds, and compiles the WEBHOOK + XPATH-THREAD capstone YAML,
    /// asserting the front end accepts it (no topology), the webhook step is a RETRY with
    /// the parsed 30 s timeout, the XPath capture parses as <see cref="CaptureFormat.XPath"/>,
    /// the assembled CSX carries the RETRY wrapping plus the webhook-listen helper, the
    /// "xpath" capture kind, the forward <c>{ref}</c> substitution, and the listener
    /// contributes a <c>webhook-listener</c> host resource.
    /// </summary>
    [Fact]
    public void Compile_WebhookCapstoneYaml_ProducesRetryWebhookXPathPipeline()
    {
        // ── 1. JSON-Schema validation accepts the YAML (no topology) ───────────────
        var validation = DocumentValidator.Validate(WebhookYaml, s_registry);
        Assert.True(
            validation.IsValid,
            "Webhook capstone YAML must pass schema validation. Errors: " +
            string.Join(" | ", validation.Errors.Select(e => e.Message)));

        // ── 2. Parse + build the AST ───────────────────────────────────────────────
        var doc = YamlDocumentParser.Parse(WebhookYaml);
        var ast = AstBuilder.Build(doc, s_registry);

        // Smoke-check: 3 steps (XPath fetch → POST trigger → webhook assert), in order.
        Assert.Equal(3, ast.Steps.Count);
        Assert.Equal("fetch-ref", ast.Steps[0].Id);
        Assert.Equal("http.rest", ast.Steps[0].CanonicalType);
        Assert.Equal("trigger-callback", ast.Steps[1].Id);
        Assert.Equal("http.rest", ast.Steps[1].CanonicalType);
        Assert.Equal("await-callback", ast.Steps[2].Id);
        Assert.Equal("webhook-listen.http", ast.Steps[2].CanonicalType);

        // The fetch step declares an XPATH capture into 'ref' (the S07 XPath surface).
        var fetchStep = ast.Steps[0];
        Assert.True(
            fetchStep.Capture.ContainsKey("ref"),
            "fetch-ref must declare a 'ref' capture.");
        Assert.Equal(CaptureFormat.XPath, fetchStep.Capture["ref"].Format);
        Assert.Equal("//order/id", fetchStep.Capture["ref"].Expression);

        // The webhook step is RETRY with the parsed 30 s timeout (engine-owned poll, §7).
        var webhookStep = ast.Steps[2];
        Assert.Equal(VerifyMode.Retry, webhookStep.VerifyMode);
        Assert.Equal(TimeSpan.FromSeconds(30), webhookStep.Timeout);

        // Metadata carries the shared selection tag (a `--tag s7-capstone` run picks this).
        Assert.NotNull(ast.Metadata);
        Assert.NotNull(ast.Metadata!.Tags);
        Assert.Contains(CapstoneTag, ast.Metadata.Tags!, StringComparer.OrdinalIgnoreCase);

        // ── 3. Compile through the provider pipeline ───────────────────────────────
        var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);

        Assert.Null(result.Failure);
        Assert.NotNull(result.Assembled);
        var src = result.Assembled!.CsxSource;
        Assert.False(
            string.IsNullOrWhiteSpace(src),
            "Assembled CsxSource must not be empty or whitespace.");

        // ── 4. RETRY wrapping: the webhook step is wrapped in the engine-owned poll ──
        // CsxAssembler wraps a RETRY step's block in an attempt local function plus a
        // RetryRunner.PollAsync(...) call so the engine owns the backoff timeline (§7).
        Assert.Contains("RetryRunner.PollAsync", src, StringComparison.Ordinal);

        // ── 5. The webhook-listen.http provider contributions are present ──────────
        // The provider-id-prefixed helper class + the host-listener capture read.  The
        // emitted block passes the ScriptGlobalVariables.Webhooks accessor into
        // WebhookListenHttp_Helpers.AssertAsync, whose body reads the captured-request
        // snapshot via 'webhooks.GetCaptured(...)' (the §13.3.1 reader for the listener).
        Assert.Contains("WebhookListenHttp_Helpers", src, StringComparison.Ordinal);
        Assert.Contains("webhooks.GetCaptured", src, StringComparison.Ordinal);
        // The AssertAsync call site passes the engine's Webhooks accessor global.
        Assert.Contains("WebhookListenHttp_Helpers.AssertAsync", src, StringComparison.Ordinal);
        // The http.rest helper is also present (both fetch + trigger steps contribute it).
        Assert.Contains("HttpRest_Helpers", src, StringComparison.Ordinal);
        // The shared substitution + secret helpers are appended by the providers.
        Assert.Contains("Substitute_Helpers", src, StringComparison.Ordinal);
        Assert.Contains("Secret_Helpers", src, StringComparison.Ordinal);

        // ── 6. The XPath capture took the XPath path (kind "xpath" + expression) ────
        // http.rest emits three parallel capture arrays; the 'ref' capture's kind token
        // is the fixed "xpath" literal and its expression is spliced verbatim.
        Assert.Contains("\"xpath\"", src, StringComparison.Ordinal);
        Assert.Contains("//order/id", src, StringComparison.Ordinal);
        Assert.Contains("\"ref\"", src, StringComparison.Ordinal);

        // ── 7. The {ref} XPath value threads FORWARD: substituted in two later steps ─
        // The POST body carries {ref}, and the webhook match path carries {ref}; both are
        // emitted as RAW template text (resolved at execution time against Vars, not here).
        Assert.Contains("{ref}", src, StringComparison.Ordinal);
        // The listener callback URL {cb} also threads forward into the POST body.
        Assert.Contains("{cb}", src, StringComparison.Ordinal);

        // ── 8. No "using var" anywhere in the assembled source (§13.3.1) ───────────
        Assert.DoesNotContain("using var", src, StringComparison.Ordinal);

        // ── 9. StepIds in declaration order ────────────────────────────────────────
        Assert.Equal(3, result.Assembled!.StepIds.Count);
        Assert.Equal("fetch-ref", result.Assembled.StepIds[0]);
        Assert.Equal("trigger-callback", result.Assembled.StepIds[1]);
        Assert.Equal("await-callback", result.Assembled.StepIds[2]);

        // ── 10. HostResourcePlan: the webhook step contributes a webhook-listener ──
        // The webhook-listen.http provider implements IHostResourceContributor and yields
        // a "webhook-listener" host resource named by its listener ("cb"); this is what
        // makes the runner stand up the ephemeral listener before step 1 (S07-F-01a).
        var hostEntry = Assert.Single(result.HostResourcePlan);
        Assert.Equal("await-callback", hostEntry.StepId);
        Assert.Equal("webhook-listener", hostEntry.Requirement.Kind);
        Assert.Equal("cb", hostEntry.Requirement.VarName);
    }

    /// <summary>
    /// Parses, validates, builds, and compiles the RELATIONAL-DIFF capstone YAML, asserting
    /// the front end accepts it (no topology), the db-assert step is present in the plan
    /// with the postgres resource requirement, and the assembled CSX carries the db-assert
    /// helper.  Then proves the provider's <see cref="IStepDiffRenderer"/> renders a
    /// relational expected-vs-observed table from the EXACT column-mismatch observation the
    /// helper emits — the diff the live twin asserts appears under the failed step.
    /// </summary>
    [Fact]
    public void Compile_DiffCapstoneYaml_ProducesDbAssertPipelineAndRendersRelationalDiff()
    {
        // ── 1. JSON-Schema validation accepts the YAML (no topology) ───────────────
        var validation = DocumentValidator.Validate(DiffYaml, s_registry);
        Assert.True(
            validation.IsValid,
            "Diff capstone YAML must pass schema validation. Errors: " +
            string.Join(" | ", validation.Errors.Select(e => e.Message)));

        // ── 2. Parse + build the AST ───────────────────────────────────────────────
        var doc = YamlDocumentParser.Parse(DiffYaml);
        var ast = AstBuilder.Build(doc, s_registry);

        Assert.Single(ast.Steps);
        Assert.Equal("assert-status", ast.Steps[0].Id);
        Assert.Equal("db-assert.postgres", ast.Steps[0].CanonicalType);

        // The 'shop' postgres dependency is declared in environment.dependencies.
        Assert.NotNull(ast.Environment);
        Assert.NotNull(ast.Environment!.Dependencies);
        Assert.True(
            ast.Environment.Dependencies!.ContainsKey("shop"),
            "The 'shop' postgres dependency must be declared in environment.dependencies.");

        // Metadata carries the shared selection tag.
        Assert.NotNull(ast.Metadata);
        Assert.Contains(CapstoneTag, ast.Metadata!.Tags!, StringComparer.OrdinalIgnoreCase);

        // ── 3. Compile through the provider pipeline ───────────────────────────────
        var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);

        Assert.Null(result.Failure);
        Assert.NotNull(result.Assembled);
        var src = result.Assembled!.CsxSource;
        Assert.False(string.IsNullOrWhiteSpace(src));

        // The db-assert.postgres helper + the failing expectation are spliced in.
        Assert.Contains("DbAssertPostgres_Helpers", src, StringComparison.Ordinal);
        // The expect.row value (SHIPPED) is the wrong-expected value that the live row
        // will not carry, producing the column-mismatch Fail observation.
        Assert.Contains("SHIPPED", src, StringComparison.Ordinal);
        Assert.DoesNotContain("using var", src, StringComparison.Ordinal);

        // ── 4. ResourcePlan: a postgres requirement named 'shop' for the step ──────
        var postgresEntry = Assert.Single(
            result.ResourcePlan,
            e => string.Equals(
                e.Requirement.Family, "postgres", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("shop", postgresEntry.Requirement.Name);
        Assert.Equal("assert-status", postgresEntry.StepId);
        Assert.Contains(
            "DbAssertPostgresProvider", postgresEntry.ProviderTypeName, StringComparison.Ordinal);

        // ── 5. The provider implements IStepDiffRenderer (the S07-G-01 contract) ───
        var provider = new DbAssertPostgresProvider();
        Assert.IsAssignableFrom<IStepDiffRenderer>(provider);

        // ── 6. The renderer draws a RELATIONAL diff from the EXACT Fail observation ─
        // This is the column-mismatch observation shape DbAssertPostgres_Helpers emits when
        // expect.row { status: SHIPPED } does not match the live first row (status PENDING).
        // The live twin's terminal renderer feeds this same observation to this same
        // renderer to draw the diff under the failed step; here we render it directly.
        using var obsDoc = System.Text.Json.JsonDocument.Parse(
            """{"column":"status","expected":"SHIPPED","actual":"PENDING"}""");
        var observation = obsDoc.RootElement;

        Assert.True(
            provider.CanRender(observation),
            "The provider must recognise its own column-mismatch observation as renderable.");

        var diff = provider.RenderDiff(observation);
        Assert.NotNull(diff);

        // The rendered diff is a small relational table naming the column with both the
        // expected and observed values — column / expected / actual headers plus the data.
        Assert.Contains("column", diff!, StringComparison.Ordinal);
        Assert.Contains("expected", diff, StringComparison.Ordinal);
        Assert.Contains("actual", diff, StringComparison.Ordinal);
        Assert.Contains("status", diff, StringComparison.Ordinal);
        Assert.Contains("SHIPPED", diff, StringComparison.Ordinal);
        Assert.Contains("PENDING", diff, StringComparison.Ordinal);
    }

    /// <summary>
    /// The frozen registry that backs the capstone must include all SIX Core providers,
    /// proving the full Core step set composes without a kind collision — the registry a
    /// real `--tag s7-capstone` run would build, and the registry both capstone scenarios
    /// validate and compile against.
    /// </summary>
    [Fact]
    public void Registry_IncludesAllSixCoreProviders()
    {
        string[] expected =
        {
            "http.rest",
            "webhook-listen.http",
            "db-assert.postgres",
            "script.csharp",
            "mq-publish.kafka",
            "mq-expect.kafka",
        };

        foreach (var kind in expected)
        {
            Assert.True(
                s_registry.TryGet(kind, out _),
                $"The frozen Core registry must include the '{kind}' provider.");
        }
    }

    /// <summary>
    /// Selection proof (compile-companion scope): BOTH capstone scenarios carry the shared
    /// <c>s7-capstone</c> tag in their AST metadata, so a tag selection over the two would
    /// pick exactly these two files.  The selection-language MECHANICS (the composable
    /// AND/OR matcher, path/owner/change-set dimensions) live in
    /// Vouchfx.Cli.Tests/ScenarioSelectorTests; ScenarioSelector is internal to Vouchfx.Cli
    /// and unreachable here, so this companion proves only the metadata both scenarios
    /// expose to that selector.
    /// </summary>
    [Fact]
    public void BothCapstoneScenarios_CarryTheSharedSelectionTag()
    {
        var webhookAst = AstBuilder.Build(YamlDocumentParser.Parse(WebhookYaml), s_registry);
        var diffAst = AstBuilder.Build(YamlDocumentParser.Parse(DiffYaml), s_registry);

        Assert.Contains(CapstoneTag, webhookAst.Metadata!.Tags!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(CapstoneTag, diffAst.Metadata!.Tags!, StringComparer.OrdinalIgnoreCase);

        // The two scenarios are distinguishable by their per-scenario second tag, so a
        // narrower `--tag webhook` / `--tag diff` selection would pick exactly one each.
        Assert.Contains("webhook", webhookAst.Metadata!.Tags!, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("diff", diffAst.Metadata!.Tags!, StringComparer.OrdinalIgnoreCase);
    }
}
