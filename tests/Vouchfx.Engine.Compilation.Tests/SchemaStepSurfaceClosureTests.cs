// Typo-closing change (branch feat/close-step-surface) — permanent
// characterisation + acceptance suite for the closed step-level property
// surface: $defs/step now closes with "unevaluatedProperties": false instead
// of "additionalProperties": true (root-language-schema.json:143), and every
// one of the 25 Core provider JsonSchemaFragments had its own top-level
// "additionalProperties": true REMOVED (never added alongside — see finding 2
// below) so the parent's unevaluatedProperties has something to evaluate.
//
// ORIGIN: this file began as UnevaluatedPropertiesSpikeTests — a throwaway
// spike that de-risked the 26-file change BEFORE it was made, by mutating a
// copy of the real composed schema purely in memory. It proved three things,
// preserved below as this permanent suite's own findings:
//
//   1. unevaluatedProperties: false on $defs/step correctly closes the step
//      surface through the composer's allOf -> if/then discriminator chain
//      in JsonSchema.Net 9.2.1: annotations from a provider's own 'properties'
//      keyword, declared inside an if/then branch, propagate up to the
//      parent's unevaluatedProperties exactly as JSON Schema 2020-12 §11.3
//      requires.
//   2. $defs/step used to carry its OWN "additionalProperties": true
//      (root-language-schema.json:143) as a SIBLING of where
//      "unevaluatedProperties": false would land. Per the JSON Schema 2020-12
//      spec text, unevaluatedProperties has no effect in the SAME schema
//      object as a sibling additionalProperties — so ADDING the new keyword
//      without REMOVING the old one is a silent, total no-op. The real change
//      REPLACES the key; it never adds alongside it. The same trap applies
//      one level down: if even ONE provider fragment kept its own top-level
//      additionalProperties: true in its 'then' branch, that fragment's own
//      'properties' + 'additionalProperties: true' would mark EVERY property
//      as evaluated for THAT provider's steps, reopening the hole for that
//      provider alone — which is why all 25 fragments' top-level
//      additionalProperties: true were removed, not only $defs/step's.
//   3. unevaluatedProperties does not recurse: it is scoped to the object it
//      is declared on. Nested blocks (expect/match/avro) stay open — closing
//      those is a deliberate, separate, later change.
//
// Now that root-language-schema.json and all 25 provider fragments carry the
// real fix, this class is promoted from spike to PERMANENT suite. Finding 2
// stays alive as an actual regression guard: since production code no longer
// contains the "naive fix" mistake to observe directly, the guard re-creates
// it on an in-memory MUTATED COPY of the real composed schema, so a
// JsonSchema.Net upgrade that silently changed this spec-mandated
// same-object-cancellation behaviour would be caught here. Everything else
// (findings 1 and 3, plus the full acceptance surface — a typo rejected with
// an actionable message, every one of the 25 Core provider types still
// validating, common step fields still validating, a single typo surviving
// noise filtering as exactly one error) now exercises the REAL production
// schema/validator path directly, via DocumentValidator — the actual
// pre-compile gate an author's suite hits. SchemaComposer.Validate is used
// only where a test needs the raw EvaluationResults tree or the composed
// JsonSchema directly, which DocumentValidator's typed SchemaValidationResult
// does not expose.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.CacheAssert.Elasticsearch;
using Vouchfx.Steps.CacheAssert.Redis;
using Vouchfx.Steps.DbAssert.Dynamodb;
using Vouchfx.Steps.DbAssert.Mongodb;
using Vouchfx.Steps.DbAssert.Mysql;
using Vouchfx.Steps.DbAssert.Postgres;
using Vouchfx.Steps.DbAssert.SqlServer;
using Vouchfx.Steps.Http.Soap;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.MailExpect.Smtp;
using Vouchfx.Steps.MetricsAssert.Prometheus;
using Vouchfx.Steps.MqExpect.AzureServiceBus;
using Vouchfx.Steps.MqExpect.Kafka;
using Vouchfx.Steps.MqExpect.Nats;
using Vouchfx.Steps.MqExpect.Rabbitmq;
using Vouchfx.Steps.MqExpect.Redis;
using Vouchfx.Steps.MqPublish.AzureServiceBus;
using Vouchfx.Steps.MqPublish.Kafka;
using Vouchfx.Steps.MqPublish.Nats;
using Vouchfx.Steps.MqPublish.Rabbitmq;
using Vouchfx.Steps.MqPublish.Redis;
using Vouchfx.Steps.Script.Csharp;
using Vouchfx.Steps.StorageAssert.S3;
using Vouchfx.Steps.TraceExpect.Otlp;
using Vouchfx.Steps.WebhookListen.Http;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Permanent characterisation + acceptance suite for the closed step-level
/// property surface (<c>$defs/step</c>: <c>"unevaluatedProperties": false</c>).
/// See the file header for the full brief and the three findings this class
/// exists to guard.
/// </summary>
public sealed class SchemaStepSurfaceClosureTests
{
    // Same OutputFormat the real composer/validator use (SchemaComposer's own
    // private _options), so the flat-Details walk SchemaErrorCollector expects
    // is present — needed only by the tests below that inspect the raw
    // EvaluationResults tree directly rather than going through
    // DocumentValidator/SchemaComposer.Validate.
    private static readonly EvaluationOptions s_options = new()
    {
        OutputFormat = OutputFormat.List,
    };

    /// <summary>
    /// The Core provider assemblies that compose the full v1 schema — a
    /// deliberate, independent duplicate of <c>SchemaFreezeTests.CoreProviderAssemblies()</c>
    /// / <c>SchemaAcceptedCorpusTests.CoreProviderAssemblies()</c> (itself
    /// mirroring <c>Vouchfx.Cli.ProviderRegistryFactory</c>), kept as its own
    /// copy so this file has zero compile-time coupling to those gates.
    /// </summary>
    private static Assembly[] CoreProviderAssemblies() => new[]
    {
        typeof(HttpRestProvider).Assembly,            // http.rest
        typeof(DbAssertPostgresProvider).Assembly,    // db-assert.postgres
        typeof(DbAssertSqlServerProvider).Assembly,   // db-assert.sqlserver
        typeof(DbAssertMongodbProvider).Assembly,     // db-assert.mongodb
        typeof(DbAssertMysqlProvider).Assembly,       // db-assert.mysql
        typeof(ScriptCsharpProvider).Assembly,        // script.csharp
        typeof(MqPublishKafkaProvider).Assembly,      // mq-publish.kafka
        typeof(MqExpectKafkaProvider).Assembly,       // mq-expect.kafka
        typeof(WebhookListenHttpProvider).Assembly,   // webhook-listen.http
        typeof(MailExpectSmtpProvider).Assembly,      // mail-expect.smtp
        typeof(CacheAssertRedisProvider).Assembly,    // cache-assert.redis
        typeof(MqPublishRabbitmqProvider).Assembly,   // mq-publish.rabbitmq
        typeof(MqExpectRabbitmqProvider).Assembly,    // mq-expect.rabbitmq
        typeof(MqPublishNatsProvider).Assembly,       // mq-publish.nats
        typeof(MqExpectNatsProvider).Assembly,        // mq-expect.nats
        typeof(CacheAssertElasticsearchProvider).Assembly, // cache-assert.elasticsearch
        typeof(MqPublishAzureServiceBusProvider).Assembly, // mq-publish.azureservicebus
        typeof(MqExpectAzureServiceBusProvider).Assembly,  // mq-expect.azureservicebus
        typeof(MqPublishRedisProvider).Assembly,      // mq-publish.redis
        typeof(MqExpectRedisProvider).Assembly,       // mq-expect.redis
        typeof(MetricsAssertPrometheusProvider).Assembly,  // metrics-assert.prometheus
        typeof(DbAssertDynamodbProvider).Assembly,    // db-assert.dynamodb
        typeof(StorageAssertS3Provider).Assembly,     // storage-assert.s3
        typeof(TraceExpectOtlpProvider).Assembly,     // trace-expect.otlp
        typeof(HttpSoapProvider).Assembly,             // http.soap
    };

    private static StepKindRegistry FullRegistry() =>
        StepKindRegistry.BuildAndFreeze(CoreProviderAssemblies());

    // ── Acceptance: the typo from the objective's own example is rejected, precisely and usefully ──

    /// <summary>
    /// The make-or-break acceptance test named directly in the objective: a
    /// 'target' field typo'd as 'taget' on an otherwise-valid http.rest step
    /// must be rejected, located exactly at the offending property, with a
    /// message an author can act on immediately (naming the property AND the
    /// step's own type — see <see cref="SchemaErrorCollector"/>).
    /// </summary>
    [Fact]
    public void UnknownTopLevelKeyOnStep_IsRejected_WithActionableMessage()
    {
        var registry = FullRegistry();

        const string yaml = """
            steps:
              - id: call-api
                type: http.rest
                target: orders-api
                method: GET
                path: /health
                taget: oops
            """;

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.False(result.IsValid, "Expected the 'taget' typo to be rejected.");

        var located = result.Errors.Where(e => e.InstanceLocation == "/steps/0/taget").ToList();
        Assert.True(located.Count > 0,
            $"Expected an error located exactly at '/steps/0/taget'; got: {Dump(result.Errors)}");

        // The message must name the offending property AND the step's own
        // type — "on step type 'X'" is what makes it immediately actionable,
        // not merely technically located.
        Assert.All(located, e =>
        {
            Assert.Contains("taget", e.Message, StringComparison.Ordinal);
            Assert.Contains("http.rest", e.Message, StringComparison.Ordinal);
        });
    }

    // ── Acceptance: every one of the 25 Core provider types still validates ──

    /// <summary>
    /// One minimal-but-realistic, schema-valid single-step document per
    /// registered Core provider type, built from each provider's own
    /// <c>SchemaFragment</c> 'required' list (read directly from source — see
    /// the individual providers under <c>src/Providers/Core</c>). This is the
    /// make-or-break coverage set: a mistake in the 26-file change would
    /// reject SOME or ALL real suites of that provider's type, and only
    /// exercising every type — not a representative handful — proves that
    /// didn't happen.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> MinimalValidStepDocuments =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["http.rest"] = """
                steps:
                  - id: s1
                    type: http.rest
                    target: orders-api
                    method: GET
                    path: /health
                """,
            ["db-assert.postgres"] = """
                steps:
                  - id: s1
                    type: db-assert.postgres
                    target: orders-db
                    query: SELECT 1
                    expect:
                      rowCount: 1
                """,
            ["db-assert.sqlserver"] = """
                steps:
                  - id: s1
                    type: db-assert.sqlserver
                    target: orders-db
                    query: SELECT 1
                    expect:
                      rowCount: 1
                """,
            ["db-assert.mongodb"] = """
                steps:
                  - id: s1
                    type: db-assert.mongodb
                    target: orders-db
                    collection: orders
                    filter: "{}"
                    expect:
                      count: 1
                """,
            ["db-assert.mysql"] = """
                steps:
                  - id: s1
                    type: db-assert.mysql
                    target: orders-db
                    query: SELECT 1
                    expect:
                      rowCount: 1
                """,
            ["script.csharp"] = """
                steps:
                  - id: s1
                    type: script.csharp
                    code: "// noop"
                """,
            ["mq-publish.kafka"] = """
                steps:
                  - id: s1
                    type: mq-publish.kafka
                    target: orders-kafka
                    topic: orders
                    payload: "{\"id\":1}"
                """,
            ["mq-expect.kafka"] = """
                steps:
                  - id: s1
                    type: mq-expect.kafka
                    target: orders-kafka
                    topic: orders
                    match:
                      key: order-1
                """,
            ["webhook-listen.http"] = """
                steps:
                  - id: s1
                    type: webhook-listen.http
                    listener: orders-webhook
                    match:
                      method: POST
                """,
            ["mail-expect.smtp"] = """
                steps:
                  - id: s1
                    type: mail-expect.smtp
                    target: mailpit
                    expect:
                      match:
                        to: customer@example.com
                """,
            ["cache-assert.redis"] = """
                steps:
                  - id: s1
                    type: cache-assert.redis
                    target: cache
                    key: orders:1
                    operation: get
                    expect:
                      value: "1"
                """,
            ["mq-publish.rabbitmq"] = """
                steps:
                  - id: s1
                    type: mq-publish.rabbitmq
                    target: orders-rabbitmq
                    routingKey: orders
                    payload: "{\"id\":1}"
                """,
            ["mq-expect.rabbitmq"] = """
                steps:
                  - id: s1
                    type: mq-expect.rabbitmq
                    target: orders-rabbitmq
                    queue: orders
                    match:
                      payloadContains: id
                """,
            ["mq-publish.nats"] = """
                steps:
                  - id: s1
                    type: mq-publish.nats
                    target: orders-nats
                    subject: orders
                    payload: "{\"id\":1}"
                """,
            ["mq-expect.nats"] = """
                steps:
                  - id: s1
                    type: mq-expect.nats
                    target: orders-nats
                    subject: orders
                    match:
                      payloadContains: id
                """,
            ["cache-assert.elasticsearch"] = """
                steps:
                  - id: s1
                    type: cache-assert.elasticsearch
                    target: search
                    index: orders
                    expect:
                      min-count: 1
                """,
            ["mq-publish.azureservicebus"] = """
                steps:
                  - id: s1
                    type: mq-publish.azureservicebus
                    target: orders-asb
                    queue: orders
                    payload: "{\"id\":1}"
                """,
            ["mq-expect.azureservicebus"] = """
                steps:
                  - id: s1
                    type: mq-expect.azureservicebus
                    target: orders-asb
                    queue: orders
                    expectPayloadContains: order
                """,
            ["mq-publish.redis"] = """
                steps:
                  - id: s1
                    type: mq-publish.redis
                    target: orders-redis
                    stream: orders
                    payload: "{\"id\":1}"
                """,
            ["mq-expect.redis"] = """
                steps:
                  - id: s1
                    type: mq-expect.redis
                    target: orders-redis
                    stream: orders
                    match:
                      payloadContains: id
                """,
            ["metrics-assert.prometheus"] = """
                steps:
                  - id: s1
                    type: metrics-assert.prometheus
                    target: orders-api
                    metric: orders_processed_total
                    expect:
                      min: "0"
                """,
            ["db-assert.dynamodb"] = """
                steps:
                  - id: s1
                    type: db-assert.dynamodb
                    target: orders-table
                    table: orders
                    key: "{\"orderId\":\"1\"}"
                    expect:
                      exists: true
                """,
            ["storage-assert.s3"] = """
                steps:
                  - id: s1
                    type: storage-assert.s3
                    target: orders-bucket
                    bucket: orders
                    key: receipts/1.json
                    expect:
                      exists: true
                """,
            ["trace-expect.otlp"] = """
                steps:
                  - id: s1
                    type: trace-expect.otlp
                    receiver: orders-otlp
                    match:
                      traceId: "0af7651916cd43dd8448eb211c80319c"
                """,
            ["http.soap"] = """
                steps:
                  - id: s1
                    type: http.soap
                    target: orders-soap
                    path: /soap/orders
                    envelope: "<soap:Envelope></soap:Envelope>"
                """,
        };

    public static IEnumerable<object[]> MinimalValidStepPerProviderType() =>
        MinimalValidStepDocuments.Select(kv => new object[] { kv.Key, kv.Value });

    [Theory]
    [MemberData(nameof(MinimalValidStepPerProviderType))]
    public void MinimalValidStep_OfEveryProviderType_StillValidates(string typeKey, string yaml)
    {
        var registry = FullRegistry();

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.True(result.IsValid, $"{typeKey}: expected valid; got: {Dump(result.Errors)}");
    }

    /// <summary>
    /// Guards the exhaustiveness of <see cref="MinimalValidStepDocuments"/>
    /// itself: its keys must be EXACTLY the registered Core provider type
    /// keys, no more and no fewer. Without this, a provider silently missing
    /// from the fixture set would just never run its Theory case — this test
    /// makes that failure loud instead of invisible (mirrors the
    /// discover-at-least-one-file guards in <c>SchemaAcceptedCorpusTests</c>).
    /// </summary>
    [Fact]
    public void MinimalValidStepDocuments_CoversExactlyTheRegisteredCoreProviders()
    {
        var registry = FullRegistry();
        var expectedKeys = registry.All
            .Select(p => $"{p.Kind.Family}.{p.Kind.Provider}")
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        var actualKeys = MinimalValidStepDocuments.Keys
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expectedKeys, actualKeys);
    }

    // ── Acceptance: common step fields still validate ──────────────────────────

    /// <summary>
    /// Every common field declared directly on <c>$defs/step</c> itself (id,
    /// type, description, capture, verifyMode, timeout, continueOnFailure)
    /// must still validate — these are exactly the fields
    /// <c>unevaluatedProperties: false</c> must recognise as EVALUATED (via
    /// <c>$defs/step</c>'s own 'properties') so it never flags them as
    /// unknown.
    /// </summary>
    [Fact]
    public void CommonStepFields_AllPresent_StillValidate()
    {
        var registry = FullRegistry();

        const string yaml = """
            steps:
              - id: call-api
                type: http.rest
                description: smoke check
                target: orders-api
                method: GET
                path: /health
                capture:
                  traceId: $.headers.trace
                verifyMode: IMMEDIATE
                timeout: 30s
                continueOnFailure: false
            """;

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.True(result.IsValid, $"Expected valid; got: {Dump(result.Errors)}");
    }

    // ── Acceptance: a single typo survives noise filtering as exactly one error ──

    /// <summary>
    /// Re-proves, against the real production schema/validator path (no
    /// in-memory mutation needed any more), the #259-style concern this
    /// collector already guards for 'required'/'enum' violations: a single
    /// genuine typo must not resurface as one error per registered provider
    /// (25 at full Core scale) once <see cref="SchemaErrorCollector.IsIfDiscriminatorNoise"/>
    /// has done its job.
    /// </summary>
    /// <remarks>
    /// Generalised (feat/close-remaining-surfaces, combined #337 peer review
    /// + gatekeeper findings) from a single <c>http.rest</c> <c>[Fact]</c>
    /// into a <c>[Theory]</c> over all 25 registered providers, reusing
    /// <see cref="MinimalValidStepDocuments"/> — this is the test that WOULD
    /// have caught the original composite-branch-noise bug: on
    /// <c>script.csharp</c> specifically, a valid <c>code:</c> plus this same
    /// typo used to report ONLY the phantom <c>[required] file</c> branch
    /// noise from its frozen <c>oneOf</c> (never touched here — see
    /// <c>SchemaErrorCollector</c>'s composite-branch-noise fix), silently
    /// swallowing the genuine <c>unevaluatedProperties</c>/'taget' message
    /// the single-provider version of this test could never have exposed.
    /// </remarks>
    [Theory]
    [MemberData(nameof(MinimalValidStepPerProviderType))]
    public void SingleTypo_YieldsExactlyOneError_NotOnePerProvider(string typeKey, string validYaml)
    {
        var registry = FullRegistry();

        // Append a typo'd property as a sibling of the step's own fields.
        // Every MinimalValidStepDocuments entry shares the identical
        // step-property indent (4 spaces) — a structural consequence of
        // C#'s raw-string-literal dedent rule applying uniformly across the
        // one dictionary literal they are all defined in.
        var yaml = validYaml + "\n    taget: oops";

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.False(result.IsValid, $"{typeKey}: expected the 'taget' typo to be rejected.");
        var onlyError = Assert.Single(result.Errors);
        Assert.Equal("/steps/0/taget", onlyError.InstanceLocation);
        Assert.Contains("taget", onlyError.Message, StringComparison.Ordinal);
        Assert.Contains(typeKey, onlyError.Message, StringComparison.Ordinal);

        // The one surviving error may not reference another provider's own
        // <family>.<provider> discriminator key — the black-box noise check
        // used by SchemaErrorCollectionAtScaleTests, re-run here for
        // unevaluatedProperties.
        var otherTypeKeys = registry.All
            .Select(p => $"{p.Kind.Family}.{p.Kind.Provider}")
            .Where(key => key != typeKey)
            .ToList();

        foreach (var key in otherTypeKeys)
        {
            Assert.False(onlyError.Message.Contains(key, StringComparison.Ordinal),
                $"{typeKey}: found discriminator-noise referencing '{key}': msg='{onlyError.Message}'");
        }
    }

    // ── Regression: a genuine defect must not cascade into false "unknown property" reports ──

    /// <summary>
    /// A step missing a required field, but otherwise carrying SEVERAL
    /// perfectly valid fields, must report ONLY the missing-field violation —
    /// not that violation plus one spurious "unknown property" per valid
    /// field. Regression coverage for the cascade fixed in
    /// <see cref="SchemaErrorCollector.SuppressUnevaluatedPropertiesCascade"/>:
    /// unevaluatedProperties only collects a subschema's annotations when
    /// that subschema's application succeeds AS A WHOLE, so once http.rest's
    /// own <c>then</c> branch fails outright (missing <c>path</c>), its
    /// <c>properties</c> annotation for every OTHER field it declares —
    /// <c>target</c>, <c>method</c>, plus the optional <c>expect</c> block —
    /// would, without the fix, never propagate either, and each would
    /// resurface as a false "unknown property" finding alongside the one
    /// genuine defect.
    /// </summary>
    [Fact]
    public void StepMissingRequiredField_WithSeveralOtherValidFields_ReportsOnlyTheMissingFieldError()
    {
        var registry = FullRegistry();

        const string yaml = """
            steps:
              - id: call-api
                type: http.rest
                target: orders-api
                method: GET
                expect:
                  status: 200
            """;

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.False(result.IsValid, "Expected invalid: http.rest requires 'path'.");

        var onlyError = Assert.Single(result.Errors);
        Assert.Contains("required", onlyError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path", onlyError.Message, StringComparison.Ordinal);

        // None of the valid fields (target, method, expect) may be reported
        // as an unknown/unevaluated property.
        Assert.DoesNotContain("unevaluatedProperties", onlyError.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// In a three-step document, step 1 (index 0) carries a genuine defect
    /// (a missing required field) and step 3 (index 2) carries ONLY a typo.
    /// Both must be reported, each against its own step — the suppression
    /// is scoped per-step, not document-wide: a genuine defect on one step
    /// must never hide an unrelated typo on a DIFFERENT step.
    /// </summary>
    [Fact]
    public void StepWithRealError_AndUnrelatedStepWithSoleTypo_BothReported_EachAgainstTheRightStep()
    {
        var registry = FullRegistry();

        const string yaml = """
            steps:
              - id: first-step
                type: http.rest
                target: orders-api
                method: GET
              - id: second-step
                type: http.rest
                target: orders-api
                method: GET
                path: /health
              - id: third-step
                type: http.rest
                target: orders-api
                method: GET
                path: /health
                taget: oops
            """;

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);

        // Step 1's genuine "missing required 'path'" violation — never
        // suppressed, and not accompanied by any false-positive entry for
        // its own valid 'target'/'method' fields.
        var firstStepErrors = result.Errors.Where(e => e.InstanceLocation.StartsWith("/steps/0", StringComparison.Ordinal)).ToList();
        var firstStepError = Assert.Single(firstStepErrors);
        Assert.Contains("required", firstStepError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path", firstStepError.Message, StringComparison.Ordinal);

        // Step 3's sole typo — untouched by step 1's unrelated defect.
        var thirdStepErrors = result.Errors.Where(e => e.InstanceLocation.StartsWith("/steps/2", StringComparison.Ordinal)).ToList();
        var thirdStepError = Assert.Single(thirdStepErrors);
        Assert.Equal("/steps/2/taget", thirdStepError.InstanceLocation);
        Assert.Contains("taget", thirdStepError.Message, StringComparison.Ordinal);
        Assert.Contains("http.rest", thirdStepError.Message, StringComparison.Ordinal);

        // Step 2 is fully valid: it must contribute nothing.
        Assert.DoesNotContain(result.Errors, e => e.InstanceLocation.StartsWith("/steps/1", StringComparison.Ordinal));
    }

    // ── Boundary, now closed: nested blocks no longer accept unknown keys ──────
    //
    // T3a2 (feat/fragment-completeness): the three tests below used to document
    // that unevaluatedProperties does NOT recurse into nested blocks (avro/
    // match/expect stayed open even after the step-root typo-closing change).
    // That was always a deliberate, honestly-documented GAP, not a design
    // decision — these three tests were written as the TARGETS for the follow-up
    // change that closes it. mq-publish.kafka's 'avro' and mq-expect.kafka's
    // 'match' each REPLACE their own "additionalProperties": true with false
    // (a replacement, not merely a removal — the same same-object-cancellation
    // trap the step-root closure's own finding 2 warns about applies one level
    // down too); http.rest's 'expect' goes from "no additionalProperties
    // keyword at all" (unconstrained) to an explicit false. All three now
    // reject an unknown key with a plain additionalProperties violation.

    /// <summary>
    /// mq-publish.kafka's nested 'avro' block now closes with
    /// <c>"additionalProperties": false</c> (previously true) — an unknown key
    /// alongside a real field is rejected, located exactly at the offending
    /// property.
    /// </summary>
    [Fact]
    public void NestedUnknownKey_InsideAvroBlock_MqPublishKafka_IsRejected()
    {
        var registry = FullRegistry();

        const string yaml = """
            steps:
              - id: publish-order
                type: mq-publish.kafka
                target: orders-kafka
                topic: orders
                payload: "{\"id\":1}"
                avro:
                  schemaRegistry: orders-kafka
                  subject: orders-value
                  schema: "{\"type\":\"record\",\"name\":\"Order\",\"fields\":[]}"
                  record:
                    id: "1"
                  unknownAvroKey: typo
            """;

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.False(result.IsValid, "Expected the closed 'avro' block to reject an unknown key.");
        var located = result.Errors.Where(e => e.InstanceLocation == "/steps/0/avro/unknownAvroKey").ToList();
        Assert.True(located.Count > 0,
            $"Expected an error located exactly at '/steps/0/avro/unknownAvroKey'; got: {Dump(result.Errors)}");
        Assert.All(located, e => Assert.Contains("[additionalProperties]", e.Message, StringComparison.Ordinal));
    }

    /// <summary>
    /// Same probe as the avro test, against mq-expect.kafka's nested 'match'
    /// block — now closes with <c>"additionalProperties": false</c>
    /// (previously true).
    /// </summary>
    [Fact]
    public void NestedUnknownKey_InsideMatchBlock_MqExpectKafka_IsRejected()
    {
        var registry = FullRegistry();

        const string yaml = """
            steps:
              - id: expect-order
                type: mq-expect.kafka
                target: orders-kafka
                topic: orders
                match:
                  key: order-1
                  unknownMatchKey: typo
            """;

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.False(result.IsValid, "Expected the closed 'match' block to reject an unknown key.");
        var located = result.Errors.Where(e => e.InstanceLocation == "/steps/0/match/unknownMatchKey").ToList();
        Assert.True(located.Count > 0,
            $"Expected an error located exactly at '/steps/0/match/unknownMatchKey'; got: {Dump(result.Errors)}");
        Assert.All(located, e => Assert.Contains("[additionalProperties]", e.Message, StringComparison.Ordinal));
    }

    /// <summary>
    /// http.rest's nested 'expect' block was a third, DIFFERENT shape of
    /// "open": it declared NO additionalProperties keyword at all (neither
    /// true nor false). It now closes with an explicit
    /// <c>"additionalProperties": false</c>.
    /// </summary>
    [Fact]
    public void NestedUnknownKey_InsideExpectBlock_HttpRest_IsRejected()
    {
        var registry = FullRegistry();

        const string yaml = """
            steps:
              - id: call-api
                type: http.rest
                target: orders-api
                method: GET
                path: /health
                expect:
                  status: 200
                  unknownExpectKey: typo
            """;

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.False(result.IsValid, "Expected the closed 'expect' block to reject an unknown key.");
        var located = result.Errors.Where(e => e.InstanceLocation == "/steps/0/expect/unknownExpectKey").ToList();
        Assert.True(located.Count > 0,
            $"Expected an error located exactly at '/steps/0/expect/unknownExpectKey'; got: {Dump(result.Errors)}");
        Assert.All(located, e => Assert.Contains("[additionalProperties]", e.Message, StringComparison.Ordinal));
    }

    // ── Finding 1, re-proved directly against the REAL (already-fixed) schema ──

    /// <summary>
    /// Direct proof, against the real composed schema (no mutation), that
    /// annotation propagation through allOf/if/then genuinely works: the raw
    /// evaluation tree contains a node whose <c>EvaluationPath</c> ends in
    /// <c>/unevaluatedProperties</c> (the keyword genuinely fired) and whose
    /// <c>InstanceLocation</c> is EXACTLY <c>/steps/0/taget</c> — the
    /// offending property, pinpointed, not just "the step object is
    /// invalid". Guards the JsonSchema.Net library behaviour finding 1
    /// depends on, independent of <see cref="SchemaErrorCollector"/>'s own
    /// filtering/formatting.
    /// </summary>
    [Fact]
    public void RealComposedSchema_UnevaluatedPropertiesKeyword_FiresAtExactPropertyLocation()
    {
        var registry = FullRegistry();
        var schema = SchemaComposer.ComposeSchema(registry);

        const string instance = """
            {
              "steps": [
                {
                  "id": "call-api",
                  "type": "http.rest",
                  "target": "orders-api",
                  "method": "GET",
                  "path": "/health",
                  "taget": "oops"
                }
              ]
            }
            """;

        using var doc = JsonDocument.Parse(instance);
        var results = schema.Evaluate(doc.RootElement, s_options);

        Assert.False(results.IsValid);

        var rawKeywordNode = FindNode(results, n =>
            n.EvaluationPath.ToString().EndsWith("/unevaluatedProperties", StringComparison.Ordinal));

        Assert.NotNull(rawKeywordNode);
        Assert.Equal("/steps/0/taget", rawKeywordNode!.InstanceLocation.ToString());
        Assert.False(rawKeywordNode.IsValid);
    }

    // ── Finding 2, kept alive as a JsonSchema.Net-upgrade regression guard ─────

    /// <summary>
    /// Production code no longer contains the "naive fix" mistake to observe
    /// directly (root-language-schema.json:143 now REPLACES, not adds
    /// alongside), so this guard re-creates it on an in-memory MUTATED COPY
    /// of the real composed schema: adds "additionalProperties": true back
    /// onto $defs/step as a SIBLING of its existing "unevaluatedProperties":
    /// false. Per the JSON Schema 2020-12 spec, this must still be a silent,
    /// total no-op — if a future JsonSchema.Net upgrade changed that
    /// same-object-cancellation rule, this test would start failing and name
    /// exactly why.
    /// </summary>
    [Fact]
    public void NaiveFix_SiblingAdditionalPropertiesOnStepDef_IsStillASilentNoOp()
    {
        var registry = FullRegistry();
        var composedJson = SchemaComposer.ComposeSchemaJson(registry);
        var rootObj = JsonNode.Parse(composedJson)!.AsObject();
        var stepDef = rootObj["$defs"]!["step"]!.AsObject();

        Assert.True(stepDef.ContainsKey("unevaluatedProperties"),
            "Expected $defs/step to already carry 'unevaluatedProperties' before this guard reintroduces the sibling trap.");
        Assert.False(stepDef.ContainsKey("additionalProperties"),
            "Expected $defs/step to NOT already carry 'additionalProperties' — this guard adds it back deliberately.");

        stepDef["additionalProperties"] = JsonValue.Create(true);
        var mutatedSchema = JsonSchema.FromText(rootObj.ToJsonString());

        const string instance = """
            {
              "steps": [
                {
                  "id": "call-api",
                  "type": "http.rest",
                  "target": "orders-api",
                  "method": "GET",
                  "path": "/health",
                  "taget": "oops"
                }
              ]
            }
            """;

        using var doc = JsonDocument.Parse(instance);
        var results = mutatedSchema.Evaluate(doc.RootElement, s_options);

        Assert.True(results.IsValid,
            "Expected the reintroduced sibling additionalProperties:true to still cancel unevaluatedProperties " +
            "(a silent no-op) — if this now fails, JsonSchema.Net's same-object-cancellation behaviour has " +
            "changed and the production schema's REPLACE (not ADD) discipline needs re-verifying.");
    }

    /// <summary>
    /// EVERY provider clause must be free of a top-level
    /// <c>additionalProperties</c>, not merely the one the mutation test below
    /// happens to touch.
    /// </summary>
    /// <remarks>
    /// The sibling test proves that reintroducing the key on ONE fragment
    /// reopens the surface for that provider alone. The corollary is the risk
    /// this test exists to cover: if a single fragment ever kept — or
    /// regained — its own <c>additionalProperties</c>, that provider's steps
    /// would silently accept unknown keys again, and nothing else would
    /// notice. The typo-rejection acceptance tests only exercise
    /// <c>http.rest</c>, and the all-25-types test asserts that valid steps
    /// still VALIDATE, which an over-permissive fragment does not break.
    ///
    /// So the hole would be provider-shaped and invisible: 24 providers
    /// closed, one open, every test green. This asserts the property
    /// structurally over all 25 clauses instead, and names the offender.
    /// </remarks>
    [Fact]
    public void EveryProviderClause_HasNoTopLevelAdditionalProperties()
    {
        var registry = FullRegistry();
        var rootObj = JsonNode.Parse(SchemaComposer.ComposeSchemaJson(registry))!.AsObject();
        var allOf = rootObj["$defs"]!["step"]!.AsObject()["allOf"]!.AsArray();

        var offenders = allOf
            .Select(n => n!.AsObject())
            .Where(clause => clause["then"] is JsonObject then && then.ContainsKey("additionalProperties"))
            .Select(clause => clause["if"]!["properties"]!["type"]!["const"]!.GetValue<string>())
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These provider fragments still declare their own top-level 'additionalProperties', " +
            "which reopens the step surface for those providers alone while every other test " +
            $"stays green:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", offenders)}");

        // Guards the guard: if the composer ever stopped emitting clauses, the
        // assertion above would pass vacuously over an empty collection.
        Assert.Equal(registry.All.Count, allOf.Count);
    }

    /// <summary>
    /// The second half of finding 2: reintroducing just ONE provider
    /// fragment's own top-level additionalProperties: true (here,
    /// http.rest's) reopens the hole for THAT provider ALONE — its own
    /// 'properties' + 'additionalProperties: true' marks every property as
    /// evaluated for its steps specifically — while every OTHER provider's
    /// steps stay correctly closed. This is why all 25 fragments needed the
    /// removal, not only $defs/step's own key.
    /// </summary>
    [Fact]
    public void NaiveFix_OneProvidersOwnAdditionalPropertiesTrue_ReopensOnlyThatProvider()
    {
        var registry = FullRegistry();
        var composedJson = SchemaComposer.ComposeSchemaJson(registry);
        var rootObj = JsonNode.Parse(composedJson)!.AsObject();
        var stepDef = rootObj["$defs"]!["step"]!.AsObject();
        var allOf = stepDef["allOf"]!.AsArray();

        var httpRestClause = allOf
            .Select(n => n!.AsObject())
            .Single(clause => clause["if"]!["properties"]!["type"]!["const"]!.GetValue<string>() == "http.rest");
        var httpRestThen = httpRestClause["then"]!.AsObject();

        Assert.False(httpRestThen.ContainsKey("additionalProperties"),
            "Expected http.rest's 'then' branch to NOT already carry 'additionalProperties' — this guard adds it back deliberately.");
        httpRestThen["additionalProperties"] = JsonValue.Create(true);

        var mutatedSchema = JsonSchema.FromText(rootObj.ToJsonString());

        const string httpRestTypo = """
            {
              "steps": [
                {
                  "id": "call-api",
                  "type": "http.rest",
                  "target": "orders-api",
                  "method": "GET",
                  "path": "/health",
                  "taget": "oops"
                }
              ]
            }
            """;

        const string dbAssertPostgresTypo = """
            {
              "steps": [
                {
                  "id": "check-db",
                  "type": "db-assert.postgres",
                  "target": "orders-db",
                  "query": "SELECT 1",
                  "expect": { "rowCount": 1 },
                  "taget": "oops"
                }
              ]
            }
            """;

        using var httpRestDoc = JsonDocument.Parse(httpRestTypo);
        var httpRestResults = mutatedSchema.Evaluate(httpRestDoc.RootElement, s_options);
        Assert.True(httpRestResults.IsValid,
            "Expected http.rest's own reintroduced additionalProperties:true to reopen ITS typo hole.");

        using var postgresDoc = JsonDocument.Parse(dbAssertPostgresTypo);
        var postgresResults = mutatedSchema.Evaluate(postgresDoc.RootElement, s_options);
        Assert.False(postgresResults.IsValid,
            "Expected db-assert.postgres (untouched by this mutation) to stay correctly closed.");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Depth-first search over the raw <see cref="EvaluationResults"/> tree,
    /// used to find the specific node where a given keyword fired, independent
    /// of <see cref="SchemaErrorCollector"/>'s own filtering/formatting.
    /// </summary>
    private static EvaluationResults? FindNode(EvaluationResults node, Func<EvaluationResults, bool> predicate)
    {
        if (predicate(node))
            return node;

        if (node.Details is { Count: > 0 })
        {
            foreach (var child in node.Details)
            {
                var found = FindNode(child, predicate);
                if (found is not null)
                    return found;
            }
        }

        return null;
    }

    private static string Dump(IReadOnlyList<SchemaValidationError> errors) =>
        errors.Count == 0
            ? "(no errors)"
            : string.Join(Environment.NewLine, errors.Select(e => $"  loc='{e.InstanceLocation}' msg='{e.Message}'"));
}
