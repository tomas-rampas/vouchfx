// S06 Capstone — non-docker compile-proof twin (Sprint 6, Milestone M3 path).
//
// The docker-gated twin (Sprint06CapstoneTests, Orchestration/Runtime test project)
// runs the SAME two scenarios against a REAL kafka + schema-registry topology and
// proves the live Avro publish → RETRY-expect → Pass / never-satisfied → Inconclusive
// guarantees.  This twin proves — without any container — that BOTH Sprint 6 capstone
// YAMLs are well-formed end-to-end up to (NOT including) topology start:
//
//   1. The front end ACCEPTS each YAML: DocumentValidator.Validate(yaml, registry)
//      reports IsValid == true (the avro step fields + the schemaRegistry dependency
//      flag pass JSON-Schema validation), AstBuilder.Build succeeds, and the expect
//      step parses as VerifyMode.Retry with its declared timeout.
//   2. ProviderPipeline.Compile returns a non-null assembled script whose CSX contains:
//      (a) RetryRunner.PollAsync — the RETRY expect step was wrapped in the engine-owned
//          poll loop (authors never write Thread.Sleep, §7);
//      (b) the Avro publish helper class (MqPublishKafka_Helpers);
//      (c) the Avro expect helper class (MqExpectKafka_Helpers);
//      (d) the inline Avro record discriminator (the publish step took the Avro path).
//   3. The resource plan declares the 'events' kafka dependency for BOTH the publish
//      and the expect step (the contributed ResourceRequirement Family=="kafka").
//
// It mirrors M2EndToEndCompileTests / Sprint04CapstoneCompileTests: parse each .e2e.yaml
// through the front end, build the frozen StepKindRegistry over the two Kafka provider
// assemblies, validate, build the AST, and call ProviderPipeline.Compile — asserting on
// the PipelineResult and the assembled CSX, never starting a topology.
//
// Why this lives in Platform.Engine.Runtime.Tests (and not the Orchestration test
// project): ProviderPipeline, PipelineResult, and ResourcePlanEntry are INTERNAL to
// Platform.Engine.Runtime and visible here via InternalsVisibleTo
// (Platform.Engine.Runtime.csproj → Platform.Engine.Runtime.Tests).  The docker capstone
// (Sprint06CapstoneTests) uses only the PUBLIC ScenarioRunner.RunAsync, so it shares this
// project too — exactly as the M2 capstone pair (M2EndToEndTests + M2EndToEndCompileTests)
// already does.

using System;
using Platform.Engine.Abstractions;
using Platform.Engine.Authoring;
using Platform.Engine.Compilation.Schema;
using Platform.Engine.Runtime;
using Platform.Sdk;
using Platform.Steps.MqExpect.Kafka;
using Platform.Steps.MqPublish.Kafka;
using Xunit;

namespace Platform.Engine.Runtime.Tests;

/// <summary>
/// Non-docker proof that BOTH Sprint 6 capstone YAMLs (the happy-path Avro
/// publish → RETRY-expect scenario and the never-satisfied RETRY scenario) parse,
/// JSON-Schema-validate, build an AST, and compile through
/// <see cref="ProviderPipeline.Compile"/> — including the RETRY wrapping and both
/// Avro Kafka provider contributions — without any container or topology.
/// </summary>
public sealed class Sprint06CapstoneCompileTests
{
    // ── Provider assemblies: the two Sprint 6 Kafka providers ──────────────────────
    private static readonly System.Reflection.Assembly[] s_providerAssemblies = new[]
    {
        typeof(MqPublishKafkaProvider).Assembly,
        typeof(MqExpectKafkaProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    private const string SuiteNamespace = "VouchfxGenerated";

    // ── Capstone YAMLs (byte-identical to the docker twin Sprint06CapstoneTests) ───

    /// <summary>
    /// The HAPPY-PATH capstone scenario: an Avro publish (IMMEDIATE) of an Order record
    /// to <c>orders.created</c> through the live schema registry, followed by a RETRY
    /// Avro expect that polls until it decodes a message whose <c>$.id</c> matches the
    /// published <c>orderId</c>.  Live, this proves: Avro publish → schema auto-registered
    /// → RETRY-expect polls → decodes the Avro message → matches → Pass.
    /// </summary>
    internal const string HappyPathYaml = """
        metadata:
          name: sprint-06-capstone
          description: Avro publish then RETRY Avro expect over a live schema registry; proves Sprint 6 end-to-end.

        variables:
          orderId: ord-7F3A

        environment:
          dependencies:
            events:
              type: kafka
              schemaRegistry: true

        steps:
          - id: publish-order
            type: mq-publish.kafka
            target: events
            topic: orders.created
            payload: "{orderId}"
            verifyMode: IMMEDIATE
            avro:
              schemaRegistry: events
              subject: orders.created-value
              schema: |
                {"type":"record","name":"Order","fields":[{"name":"id","type":"string"}]}
              record:
                id: "{orderId}"

          - id: expect-order
            type: mq-expect.kafka
            target: events
            topic: orders.created
            verifyMode: RETRY
            timeout: 30s
            avro:
              schemaRegistry: events
            match:
              json:
                "$.id": "{orderId}"
        """;

    /// <summary>
    /// The NEVER-SATISFIED capstone scenario: the same Avro publish of <c>orderId</c>
    /// (so the topic exists and the broker / registry are exercised), followed by a RETRY
    /// Avro expect with a SHORT timeout whose <c>$.id</c> match selects a value that is
    /// never published (<c>missingId</c>).  Live, this proves a sustained RETRY non-match
    /// becomes <see cref="Verdict.Inconclusive"/> on timeout — NOT <see cref="Verdict.Fail"/>
    /// (§12.1): a never-satisfied RETRY does not break CI.
    /// </summary>
    internal const string NeverSatisfiedYaml = """
        metadata:
          name: sprint-06-capstone-never
          description: Avro publish then a RETRY Avro expect that never matches; proves never-satisfied RETRY → Inconclusive (not Fail).

        variables:
          orderId: ord-7F3A
          missingId: ord-NEVER

        environment:
          dependencies:
            events:
              type: kafka
              schemaRegistry: true

        steps:
          - id: publish-order
            type: mq-publish.kafka
            target: events
            topic: orders.created
            payload: "{orderId}"
            verifyMode: IMMEDIATE
            avro:
              schemaRegistry: events
              subject: orders.created-value
              schema: |
                {"type":"record","name":"Order","fields":[{"name":"id","type":"string"}]}
              record:
                id: "{orderId}"

          - id: expect-missing
            type: mq-expect.kafka
            target: events
            topic: orders.created
            verifyMode: RETRY
            timeout: 2s
            avro:
              schemaRegistry: events
            match:
              json:
                "$.id": "{missingId}"
        """;

    // ── The compile tests ──────────────────────────────────────────────────────────

    /// <summary>
    /// Parses, validates, builds, and compiles the HAPPY-PATH capstone YAML, asserting
    /// the front end accepts it (no topology), the expect step is a RETRY with the parsed
    /// 30 s timeout, the assembled CSX carries the RETRY wrapping plus both Avro Kafka
    /// helpers, and the resource plan declares the <c>events</c> kafka dependency for both
    /// steps.
    /// </summary>
    [Fact]
    public void Compile_HappyPathCapstoneYaml_ProducesRetryWrappedAvroPipeline()
    {
        AssertCapstoneCompiles(
            yaml: HappyPathYaml,
            expectStepId: "expect-order",
            expectedTimeout: TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Parses, validates, builds, and compiles the NEVER-SATISFIED capstone YAML.  The
    /// front end is identical in shape to the happy path (the only differences — a SHORT
    /// 2 s timeout and a <c>$.id</c> match against a never-published value — are authoring
    /// details, not compile-time concerns), so the same end-to-end well-formedness holds:
    /// RETRY wrapping, both Avro helpers, and the <c>events</c> kafka resource plan.
    /// </summary>
    [Fact]
    public void Compile_NeverSatisfiedCapstoneYaml_ProducesRetryWrappedAvroPipeline()
    {
        AssertCapstoneCompiles(
            yaml: NeverSatisfiedYaml,
            expectStepId: "expect-missing",
            expectedTimeout: TimeSpan.FromSeconds(2));
    }

    // ── Shared assertion body ──────────────────────────────────────────────────────

    /// <summary>
    /// Drives the full non-docker front end over <paramref name="yaml"/> and asserts the
    /// shared Sprint 6 capstone invariants: schema validity, RETRY-mode + timeout parsing
    /// on the expect step, a successful compile, the RETRY-wrapped Avro CSX, and the
    /// <c>events</c> kafka resource plan.
    /// </summary>
    private static void AssertCapstoneCompiles(
        string yaml, string expectStepId, TimeSpan expectedTimeout)
    {
        // ── 1. JSON-Schema validation accepts the YAML (no topology) ───────────────
        // This proves the avro publish/expect step fields AND the schemaRegistry
        // dependency flag pass the composed schema (root + both provider fragments).
        var validation = DocumentValidator.Validate(yaml, s_registry);
        Assert.True(
            validation.IsValid,
            "Capstone YAML must pass schema validation. Errors: " +
            string.Join(" | ", System.Linq.Enumerable.Select(
                validation.Errors, e => e.Message)));

        // ── 2. Parse + build the AST ───────────────────────────────────────────────
        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, s_registry);

        // Smoke-check: 2 steps (publish then expect), in declaration order.
        Assert.Equal(2, ast.Steps.Count);
        Assert.Equal("publish-order", ast.Steps[0].Id);
        Assert.Equal("mq-publish.kafka", ast.Steps[0].CanonicalType);
        Assert.Equal("mq-expect.kafka", ast.Steps[1].CanonicalType);

        // The publish step defaults / declares IMMEDIATE; the expect step is RETRY with
        // its declared timeout parsed into a TimeSpan.
        var publishStep = ast.Steps[0];
        var expectStep = ast.Steps[1];
        Assert.Equal(VerifyMode.Immediate, publishStep.VerifyMode);
        Assert.Equal(expectStepId, expectStep.Id);
        Assert.Equal(VerifyMode.Retry, expectStep.VerifyMode);
        Assert.Equal(expectedTimeout, expectStep.Timeout);

        // ── 3. The 'events' kafka dependency is declared on the AST environment ─────
        Assert.NotNull(ast.Environment);
        Assert.NotNull(ast.Environment!.Dependencies);
        Assert.True(
            ast.Environment.Dependencies!.ContainsKey("events"),
            "The 'events' kafka dependency must be declared in environment.dependencies.");

        // ── 4. Compile through the provider pipeline ───────────────────────────────
        var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);

        // ── 5. No failure; assembled script non-null ───────────────────────────────
        Assert.Null(result.Failure);
        Assert.NotNull(result.Assembled);
        var src = result.Assembled!.CsxSource;
        Assert.False(
            string.IsNullOrWhiteSpace(src),
            "Assembled CsxSource must not be empty or whitespace.");

        // ── 6. RETRY wrapping: the expect step is wrapped in the engine-owned poll ──
        // CsxAssembler wraps a RETRY step's block in an attempt local function plus a
        // RetryRunner.PollAsync(...) call so the engine owns the backoff timeline (§7).
        Assert.Contains("RetryRunner.PollAsync", src, StringComparison.Ordinal);

        // ── 7. Both Avro Kafka provider helper classes are present ─────────────────
        // The publish step contributes MqPublishKafka_Helpers (Avro publish path) and the
        // expect step contributes MqExpectKafka_Helpers (Avro expect path).  Both classes
        // are provider-id-prefixed (§13.3.1) and spliced into the same Roslyn submission.
        Assert.Contains("MqPublishKafka_Helpers", src, StringComparison.Ordinal);
        Assert.Contains("MqExpectKafka_Helpers", src, StringComparison.Ordinal);

        // The shared substitution + secret helpers are appended by both providers.
        Assert.Contains("Substitute_Helpers", src, StringComparison.Ordinal);
        Assert.Contains("Secret_Helpers", src, StringComparison.Ordinal);

        // ── 8. The publish step took the Avro path (inline schema spliced verbatim) ─
        // The avro 'subject' / inline record schema is emitted as a JSON-escaped literal
        // into the publish call site; the record type name "Order" proves the Avro branch.
        Assert.Contains("orders.created-value", src, StringComparison.Ordinal);
        Assert.Contains("Order", src, StringComparison.Ordinal);

        // ── 9. The expect step's JSONPath match expression is spliced verbatim ──────
        // The match { json: { "$.id": "{…}" } } criterion emits the "$.id" JSONPath as a
        // raw template literal (the {placeholder} is resolved at execution time, not here).
        Assert.Contains("$.id", src, StringComparison.Ordinal);

        // ── 10. No "using var" anywhere in the assembled source (§13.3.1) ──────────
        Assert.DoesNotContain("using var", src, StringComparison.Ordinal);

        // ── 11. StepIds in declaration order ───────────────────────────────────────
        Assert.Equal(2, result.Assembled!.StepIds.Count);
        Assert.Equal("publish-order", result.Assembled.StepIds[0]);
        Assert.Equal(expectStepId, result.Assembled.StepIds[1]);

        // ── 12. ResourcePlan: a kafka requirement named 'events' for BOTH steps ─────
        // Both providers implement IResourceContributor and contribute a
        // ResourceRequirement with Family=="kafka" and Name=="events" (their target).
        var kafkaEntries = System.Linq.Enumerable.ToList(
            System.Linq.Enumerable.Where(
                result.ResourcePlan,
                e => string.Equals(
                    e.Requirement.Family, "kafka", StringComparison.OrdinalIgnoreCase)));

        Assert.Equal(2, kafkaEntries.Count);
        Assert.All(kafkaEntries, e => Assert.Equal("events", e.Requirement.Name));

        var publishEntry = System.Linq.Enumerable.Single(
            kafkaEntries, e => string.Equals(e.StepId, "publish-order", StringComparison.Ordinal));
        var expectEntry = System.Linq.Enumerable.Single(
            kafkaEntries, e => string.Equals(e.StepId, expectStepId, StringComparison.Ordinal));

        Assert.Contains("MqPublishKafkaProvider", publishEntry.ProviderTypeName, StringComparison.Ordinal);
        Assert.Contains("MqExpectKafkaProvider", expectEntry.ProviderTypeName, StringComparison.Ordinal);
    }
}
