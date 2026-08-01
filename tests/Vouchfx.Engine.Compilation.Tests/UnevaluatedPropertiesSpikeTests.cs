// CHARACTERISATION TEST — records how JsonSchema.Net behaves on the step surface,
// and why the schema-closing change that follows is shaped the way it is. These
// tests run in CI deliberately: the change they justify rests on an assumption
// about a third-party library, and a version bump could invalidate it silently.
//
// The findings below were gathered before the change was made, by mutating the
// composed schema IN MEMORY. That is still how they are asserted here, so this
// file stands on its own against the schema as it exists at this commit.
//
// THE QUESTION being answered here (nothing else): today, all 25 provider
// JsonSchemaFragments end with an explicit "additionalProperties": true, and
// $defs/step in root-language-schema.json ALSO carries its own top-level
// "additionalProperties": true. An unknown top-level key on ANY step (e.g. a
// 'target' typo'd as 'taget' on http.rest) therefore validates cleanly and is
// silently dropped by the binder — the single largest authoring-error class in
// the language.
//
// The proposed fix is to remove "additionalProperties": true from all 25
// fragments and set "unevaluatedProperties": false on $defs/step, as one atomic
// 26-file change. Before touching those 26 files, this spike proves — against the
// REAL composed schema (SchemaComposer.ComposeSchemaJson over the full 25-provider
// Core registry, exactly as SchemaFreezeTests.cs builds it), mutated ONLY in
// memory — whether JsonSchema.Net 9.2.1 actually closes the step surface that way.
//
// unevaluatedProperties works off ANNOTATIONS left by sibling/adjacent keywords
// (JSON Schema 2020-12 §11.3). Two distinct risks were identified before writing
// a single line of the real change:
//
//   (1) Does an evaluated-properties annotation from a 'properties' keyword
//       declared INSIDE an allOf -> if/then -> <fragment> branch actually reach
//       the unevaluatedProperties keyword sitting on the PARENT ($defs/step)
//       schema? This is the advertised, headline risk — a library-correctness
//       question about how JsonSchema.Net threads annotations through if/then.
//
//   (2) A second, easy-to-miss risk that follows directly from reading the
//       JSON Schema 2020-12 spec text for unevaluatedProperties: "If
//       'additionalProperties' is present in the SAME schema object,
//       'unevaluatedProperties' has no effect there, because 'additionalProperties'
//       has already claimed every property not named by 'properties'." $defs/step
//       carries its OWN "additionalProperties": true (root-language-schema.json:143)
//       as a SIBLING of where "unevaluatedProperties": false would be added. If the
//       26-file change ADDS unevaluatedProperties as a sibling without also
//       REMOVING $defs/step's own additionalProperties: true, the fix is a
//       self-cancelling no-op by spec, independent of any allOf/if/then question.
//       This spike tests both readings explicitly (see the "NaiveFix_" vs
//       "CorrectFix_" test groups) rather than assuming the more careful reading
//       is what a 26-file diff would actually do.
//
// Constraints observed (do not violate when reading/extending this file):
//   • root-language-schema.json, every provider *Provider.cs, and both
//     composed-schema.v1.json goldens are NEVER modified. All mutation happens on
//     an in-memory System.Text.Json.Nodes.JsonObject built from the real composer's
//     output, inside this test class only.
//   • Registry construction mirrors SchemaFreezeTests.CoreProviderAssemblies() —
//     duplicated here (not shared) for the same reason
//     SchemaErrorCollectionAtScaleTests duplicates it: zero compile-time coupling
//     to the frozen golden-file gate.
using System;
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
/// Characterises whether <c>unevaluatedProperties: false</c> on <c>$defs/step</c>
/// closes the step surface through the composer's <c>allOf</c>/<c>if</c>/<c>then</c>
/// discriminator chain in JsonSchema.Net 9.2.1 — the assumption the step-surface
/// closure depends on. Kept in CI so a library upgrade that changes annotation
/// propagation fails here, loudly, rather than reopening the surface in silence.
/// See the file header for the full findings.
/// </summary>
public sealed class UnevaluatedPropertiesSpikeTests
{
    // Same OutputFormat the real composer/validator use (SchemaComposer._options),
    // so the flat-Details walk SchemaErrorCollector expects is present.
    private static readonly EvaluationOptions s_options = new()
    {
        OutputFormat = OutputFormat.List,
    };

    /// <summary>
    /// The Core provider assemblies that compose the full v1 schema — a deliberate,
    /// independent duplicate of <c>SchemaFreezeTests.CoreProviderAssemblies()</c>
    /// (itself mirroring <c>Vouchfx.Cli.ProviderRegistryFactory</c>), kept as its own
    /// copy so this file has zero compile-time coupling to the frozen golden-file
    /// gate — changing or removing it later cannot perturb that gate.
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

    // ── Schema mutation (in-memory only — never touches disk) ──────────────────

    /// <summary>
    /// Composes the REAL v1 schema via <see cref="SchemaComposer.ComposeSchemaJson"/>
    /// (the exact text SchemaFreezeTests snapshots), then applies the proposed fix to
    /// the in-memory <see cref="JsonNode"/> tree ONLY:
    /// <list type="bullet">
    ///   <item>removes each of the 25 fragments' own top-level
    ///   <c>"additionalProperties": true</c> (spliced into
    ///   <c>$defs/step/allOf/&lt;n&gt;/then</c> by the composer);</item>
    ///   <item>when <paramref name="removeStepOwnAdditionalProperties"/> is
    ///   <see langword="true"/> (the CORRECT reading of the proposed fix), also
    ///   removes <c>$defs/step</c>'s OWN top-level <c>"additionalProperties": true</c>
    ///   (root-language-schema.json:143) — otherwise it stays, deliberately, so the
    ///   "NaiveFix_" tests can prove what happens if a 26-file diff ADDS
    ///   unevaluatedProperties as a sibling instead of REPLACING that key;</item>
    ///   <item>sets <c>$defs/step["unevaluatedProperties"] = false</c>.</item>
    /// </list>
    /// Nested blocks (<c>expect</c>/<c>match</c>/<c>avro</c>) are deliberately left
    /// exactly as authored — that is Question 4, answered by a separate test group
    /// below, not baked into this helper.
    /// </summary>
    private static JsonObject BuildMutatedSchemaObject(
        StepKindRegistry registry, bool removeStepOwnAdditionalProperties)
    {
        var composedJson = SchemaComposer.ComposeSchemaJson(registry);
        var rootNode = JsonNode.Parse(composedJson)
            ?? throw new InvalidOperationException("Composed schema JSON parsed to null.");
        var rootObj = rootNode.AsObject();

        var stepDef = rootObj["$defs"]!["step"]!.AsObject();

        var allOf = stepDef["allOf"]!.AsArray();
        Assert.Equal(25, allOf.Count); // sanity: one if/then clause per Core provider.

        foreach (var clauseNode in allOf)
        {
            var clause = clauseNode!.AsObject();
            var thenFragment = clause["then"]!.AsObject();
            var removed = thenFragment.Remove("additionalProperties");
            Assert.True(removed, "Expected every fragment's 'then' branch to carry its own top-level 'additionalProperties' before this spike removes it.");
        }

        if (removeStepOwnAdditionalProperties)
        {
            var removedStepOwn = stepDef.Remove("additionalProperties");
            Assert.True(removedStepOwn, "Expected $defs/step to carry its own top-level 'additionalProperties' before this spike removes it.");
        }

        stepDef["unevaluatedProperties"] = JsonValue.Create(false);

        return rootObj;
    }

    private static JsonSchema BuildCorrectlyFixedSchema(StepKindRegistry registry) =>
        JsonSchema.FromText(BuildMutatedSchemaObject(registry, removeStepOwnAdditionalProperties: true).ToJsonString());

    private static JsonSchema BuildNaivelyFixedSchema(StepKindRegistry registry) =>
        JsonSchema.FromText(BuildMutatedSchemaObject(registry, removeStepOwnAdditionalProperties: false).ToJsonString());

    private static EvaluationResults Evaluate(JsonSchema schema, string instanceJson)
    {
        using var doc = JsonDocument.Parse(instanceJson);
        return schema.Evaluate(doc.RootElement, s_options);
    }

    // ── Baseline: reproduce today's defect against the UNMODIFIED composed schema ──

    /// <summary>
    /// Control case. Proves the defect described in the objective actually exists
    /// against today's real, unmodified composed schema (not a hand-rolled miniature)
    /// before any fix is evaluated: an unknown top-level key on a step with otherwise
    /// fully valid required fields validates cleanly.
    /// </summary>
    [Fact]
    public void Baseline_TodaysUnmodifiedComposedSchema_SilentlyAcceptsUnknownTopLevelKey()
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

        var result = SchemaComposer.Validate(registry, yaml);

        Assert.True(result.IsValid, "Baseline defect not reproduced: expected today's schema to accept the 'taget' typo, but it was rejected.");
    }

    // ── Q1 + Q3: valid steps of several providers, including nested blocks, still validate ──

    [Fact]
    public void CorrectFix_ValidHttpRestStep_WithExpectBlockAndAllCommonFields_StillValidates()
    {
        var registry = FullRegistry();
        var schema = BuildCorrectlyFixedSchema(registry);

        // Exercises every common field declared on $defs/step itself (id, type,
        // capture, verifyMode, timeout, continueOnFailure) PLUS http.rest's own
        // nested 'expect' block — Question 3 and half of Question 1 in one instance.
        const string instance = """
            {
              "steps": [
                {
                  "id": "call-api",
                  "type": "http.rest",
                  "description": "smoke check",
                  "target": "orders-api",
                  "method": "GET",
                  "path": "/health",
                  "expect": { "status": 200 },
                  "capture": { "traceId": "$.headers.trace" },
                  "verifyMode": "IMMEDIATE",
                  "timeout": "30s",
                  "continueOnFailure": false
                }
              ]
            }
            """;

        var results = Evaluate(schema, instance);
        var errors = SchemaErrorCollector.CollectErrors(results);

        Assert.True(results.IsValid, $"Expected valid; got: {Dump(errors)}");
    }

    [Fact]
    public void CorrectFix_ValidMqPublishKafkaStep_WithAvroBlock_StillValidates()
    {
        var registry = FullRegistry();
        var schema = BuildCorrectlyFixedSchema(registry);

        const string instance = """
            {
              "steps": [
                {
                  "id": "publish-order",
                  "type": "mq-publish.kafka",
                  "target": "orders-kafka",
                  "topic": "orders",
                  "payload": "{\"id\":1}",
                  "avro": {
                    "schemaRegistry": "orders-kafka",
                    "subject": "orders-value",
                    "schema": "{\"type\":\"record\",\"name\":\"Order\",\"fields\":[]}",
                    "record": { "id": "1" }
                  }
                }
              ]
            }
            """;

        var results = Evaluate(schema, instance);
        var errors = SchemaErrorCollector.CollectErrors(results);

        Assert.True(results.IsValid, $"Expected valid; got: {Dump(errors)}");
    }

    [Fact]
    public void CorrectFix_ValidMqExpectKafkaStep_WithMatchBlock_StillValidates()
    {
        var registry = FullRegistry();
        var schema = BuildCorrectlyFixedSchema(registry);

        const string instance = """
            {
              "steps": [
                {
                  "id": "expect-order",
                  "type": "mq-expect.kafka",
                  "target": "orders-kafka",
                  "topic": "orders",
                  "match": {
                    "key": "order-1",
                    "payloadContains": "id",
                    "headers": { "trace-id": "abc" },
                    "json": { "$.id": "1" }
                  },
                  "verifyMode": "RETRY"
                }
              ]
            }
            """;

        var results = Evaluate(schema, instance);
        var errors = SchemaErrorCollector.CollectErrors(results);

        Assert.True(results.IsValid, $"Expected valid; got: {Dump(errors)}");
    }

    [Fact]
    public void CorrectFix_ValidDbAssertPostgresStep_WithExpectBlock_StillValidates()
    {
        var registry = FullRegistry();
        var schema = BuildCorrectlyFixedSchema(registry);

        // db-assert.postgres is one of the 9 providers whose nested 'expect' ALREADY
        // declares additionalProperties: false — a useful fourth, distinct provider
        // to widen Question 1's coverage beyond the three named in the brief.
        const string instance = """
            {
              "steps": [
                {
                  "id": "check-db",
                  "type": "db-assert.postgres",
                  "target": "orders-db",
                  "query": "SELECT 1",
                  "expect": { "rowCount": 1 }
                }
              ]
            }
            """;

        var results = Evaluate(schema, instance);
        var errors = SchemaErrorCollector.CollectErrors(results);

        Assert.True(results.IsValid, $"Expected valid; got: {Dump(errors)}");
    }

    // ── Q2: unknown top-level key is now rejected, with a useful diagnostic ────────

    /// <summary>
    /// The make-or-break assertion: with the fix CORRECTLY applied (both halves —
    /// see <see cref="BuildCorrectlyFixedSchema"/>), the 'taget' typo from the
    /// objective's own example must be rejected, AND the rejection must be
    /// diagnosable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EMPIRICAL FINDING (captured via a throwaway raw-tree dump against JsonSchema.Net
    /// 9.2.1 before this assertion was written — see the git history of this file for
    /// the diagnostic): annotation propagation through allOf/if/then WORKS CORRECTLY.
    /// The evaluation tree contains a node whose <c>EvaluationPath</c> is
    /// <c>…/unevaluatedProperties</c> (i.e. the keyword genuinely fired) and whose
    /// <c>InstanceLocation</c> is EXACTLY <c>/steps/0/taget</c> — the offending
    /// property, pinpointed, not just "the step object is invalid".
    /// </para>
    /// <para>
    /// BUT that node's own <c>.Errors</c> dictionary carries a single entry keyed by
    /// the EMPTY STRING (message "All values fail against the false schema") — NOT
    /// keyed "unevaluatedProperties". JsonSchema.Net evaluates
    /// <c>unevaluatedProperties: false</c> per-offending-property against the boolean
    /// schema <c>false</c> itself (mirroring how <c>additionalProperties</c> works),
    /// and a bare boolean-false subschema failure carries no keyword name of its own —
    /// only the parent node's EvaluationPath records that "unevaluatedProperties" was
    /// the keyword that produced this branch. <see cref="SchemaErrorCollector"/>'s
    /// current message format (<c>$"[{keyword}] {message}"</c>, using the PER-NODE
    /// keyword from <c>.Errors</c>, not the EvaluationPath) therefore renders this
    /// today as <c>"[] All values fail against the false schema"</c> — functionally
    /// locatable (via InstanceLocation) but with an uninformative bracket tag. This is
    /// a real, if minor, diagnostic-quality gap worth a follow-up in
    /// <see cref="SchemaErrorCollector"/> (e.g. falling back to the EvaluationPath's
    /// final segment when the per-node keyword is empty) — separate from, and not a
    /// reason to abandon, the schema change itself.
    /// </para>
    /// <para>
    /// This test asserts BOTH halves precisely: the RAW evaluation tree names the
    /// keyword correctly (proving the schema-level mechanism works), and
    /// <see cref="SchemaErrorCollector.CollectErrors"/> — what an author actually sees
    /// today — locates the property correctly but does not literally say
    /// "unevaluatedProperties" in the message.
    /// </para>
    /// </remarks>
    [Fact]
    public void CorrectFix_UnknownTopLevelKeyOnHttpRestStep_IsRejected_AndPreciselyLocated()
    {
        var registry = FullRegistry();
        var schema = BuildCorrectlyFixedSchema(registry);

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

        var results = Evaluate(schema, instance);

        Assert.False(results.IsValid, "Expected the 'taget' typo to be rejected once the fix is correctly applied.");

        // (a) The RAW evaluation tree: somewhere in it, the 'unevaluatedProperties'
        // keyword genuinely fired (its EvaluationPath ends with that keyword) and its
        // InstanceLocation is EXACTLY the offending property — proving JsonSchema.Net
        // correctly threads the 25 branches' 'properties' annotations up through
        // allOf/if/then to the parent's unevaluatedProperties (the headline risk this
        // whole spike exists to answer).
        var rawKeywordNode = FindNode(results, n =>
            n.EvaluationPath.ToString().EndsWith("/unevaluatedProperties", StringComparison.Ordinal));

        Assert.NotNull(rawKeywordNode);
        Assert.Equal("/steps/0/taget", rawKeywordNode!.InstanceLocation.ToString());
        Assert.False(rawKeywordNode.IsValid);

        // (b) What SchemaErrorCollector.CollectErrors — the shared path used by both
        // SchemaComposer.Validate and YamlSchemaValidator — actually hands an author
        // TODAY. The location is correct; the message text does NOT contain the word
        // "unevaluatedProperties" (see remarks) — asserted explicitly, both ways, so
        // this negative result is pinned rather than silently assumed away.
        var errors = SchemaErrorCollector.CollectErrors(results);
        var locatedAtProperty = errors.Where(e => e.InstanceLocation == "/steps/0/taget").ToList();

        Assert.True(locatedAtProperty.Count > 0,
            $"Expected at least one collected error located exactly at '/steps/0/taget'; got: {Dump(errors)}");
        Assert.All(locatedAtProperty, e => Assert.Contains("false schema", e.Message, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(locatedAtProperty, e => e.Message.Contains("unevaluatedProperties", StringComparison.OrdinalIgnoreCase));
    }

    // ── Q5: error volume on a single typo stays sane after discriminator-noise filtering ──

    /// <summary>
    /// The composer's if/then chain produces one branch per provider (25 at full
    /// Core scale). A single genuine typo must not resurface as ~25 errors once
    /// <see cref="SchemaErrorCollector.IsIfDiscriminatorNoise"/> has done its job —
    /// exactly the #259 concern this collector already guards against for
    /// 'required'/'enum' violations, now re-proved for 'unevaluatedProperties'.
    /// </summary>
    [Fact]
    public void CorrectFix_SingleTypo_YieldsBoundedErrorCount_NotOnePerProvider()
    {
        var registry = FullRegistry();
        var schema = BuildCorrectlyFixedSchema(registry);

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

        var results = Evaluate(schema, instance);
        var errors = SchemaErrorCollector.CollectErrors(results);

        // Bound generously (5, not 1) so the assertion is about "sane", not about
        // pinning JsonSchema.Net's exact internal shape for a single
        // unevaluatedProperties violation — but 25 (one per provider) must never
        // recur, which is the specific #259-style regression being guarded against.
        Assert.True(errors.Count is > 0 and <= 5,
            $"Expected a small, bounded error count (not one per provider); got {errors.Count}: {Dump(errors)}");

        // None of the surviving errors may reference another provider's own
        // <family>.<provider> discriminator key — the black-box noise check used by
        // SchemaErrorCollectionAtScaleTests, re-run here for the new keyword.
        var otherTypeKeys = registry.All
            .Select(p => $"{p.Kind.Family}.{p.Kind.Provider}")
            .Where(key => key != "http.rest")
            .ToList();

        foreach (var error in errors)
        {
            foreach (var key in otherTypeKeys)
            {
                Assert.False(error.Message.Contains(key, StringComparison.Ordinal),
                    $"Found discriminator-noise referencing '{key}': loc='{error.InstanceLocation}' msg='{error.Message}'");
            }
        }
    }

    // ── Q4: does unevaluatedProperties:false at the step root reach INTO nested blocks? ──

    /// <summary>
    /// mq-publish.kafka's nested 'avro' block explicitly declares its own
    /// <c>"additionalProperties": true</c> (left untouched by this spike's mutation —
    /// only $defs/step and the fragments' TOP-LEVEL additionalProperties are
    /// touched). If <c>unevaluatedProperties: false</c> on $defs/step reached INTO
    /// nested objects, an unknown key here would still be rejected on some other
    /// basis; per the JSON Schema 2020-12 spec, unevaluatedProperties only ever
    /// concerns the immediate object it is declared on, so this must stay valid —
    /// confirming closing 'avro' remains a SEPARATE, still-necessary change.
    /// </summary>
    [Fact]
    public void CorrectFix_UnknownKeyInsideOpenNestedAvroBlock_MqPublishKafka_IsNotClosed()
    {
        var registry = FullRegistry();
        var schema = BuildCorrectlyFixedSchema(registry);

        const string instance = """
            {
              "steps": [
                {
                  "id": "publish-order",
                  "type": "mq-publish.kafka",
                  "target": "orders-kafka",
                  "topic": "orders",
                  "payload": "{\"id\":1}",
                  "avro": {
                    "schemaRegistry": "orders-kafka",
                    "subject": "orders-value",
                    "schema": "{\"type\":\"record\",\"name\":\"Order\",\"fields\":[]}",
                    "record": { "id": "1" },
                    "unknownAvroKey": "typo"
                  }
                }
              ]
            }
            """;

        var results = Evaluate(schema, instance);
        var errors = SchemaErrorCollector.CollectErrors(results);

        Assert.True(results.IsValid,
            $"Expected the step-root unevaluatedProperties:false to have NO effect inside 'avro' (a nested block with its own open additionalProperties:true); got: {Dump(errors)}");
    }

    /// <summary>
    /// Same probe as the avro test, against mq-expect.kafka's nested 'match' block
    /// (also explicit <c>additionalProperties: true</c>, untouched).
    /// </summary>
    [Fact]
    public void CorrectFix_UnknownKeyInsideOpenNestedMatchBlock_MqExpectKafka_IsNotClosed()
    {
        var registry = FullRegistry();
        var schema = BuildCorrectlyFixedSchema(registry);

        const string instance = """
            {
              "steps": [
                {
                  "id": "expect-order",
                  "type": "mq-expect.kafka",
                  "target": "orders-kafka",
                  "topic": "orders",
                  "match": {
                    "key": "order-1",
                    "unknownMatchKey": "typo"
                  }
                }
              ]
            }
            """;

        var results = Evaluate(schema, instance);
        var errors = SchemaErrorCollector.CollectErrors(results);

        Assert.True(results.IsValid,
            $"Expected the step-root unevaluatedProperties:false to have NO effect inside 'match' (a nested block with its own open additionalProperties:true); got: {Dump(errors)}");
    }

    /// <summary>
    /// http.rest's nested 'expect' block is a third, DIFFERENT shape of "open": it
    /// declares NO additionalProperties keyword at all (neither true nor false) —
    /// the JSON Schema default is unconstrained either way, so this probes the same
    /// question via the "absent" form rather than the "explicit true" form covered
    /// above.
    /// </summary>
    [Fact]
    public void CorrectFix_UnknownKeyInsideOpenNestedExpectBlock_HttpRest_IsNotClosed()
    {
        var registry = FullRegistry();
        var schema = BuildCorrectlyFixedSchema(registry);

        const string instance = """
            {
              "steps": [
                {
                  "id": "call-api",
                  "type": "http.rest",
                  "target": "orders-api",
                  "method": "GET",
                  "path": "/health",
                  "expect": { "status": 200, "unknownExpectKey": "typo" }
                }
              ]
            }
            """;

        var results = Evaluate(schema, instance);
        var errors = SchemaErrorCollector.CollectErrors(results);

        Assert.True(results.IsValid,
            $"Expected the step-root unevaluatedProperties:false to have NO effect inside 'expect' (a nested block with no additionalProperties keyword at all); got: {Dump(errors)}");
    }

    // ── The self-cancelling-no-op trap: ADDING unevaluatedProperties without REMOVING $defs/step's own additionalProperties:true ──

    /// <summary>
    /// Per the JSON Schema 2020-12 spec text for <c>unevaluatedProperties</c>: when
    /// <c>additionalProperties</c> is present in the SAME schema object,
    /// <c>unevaluatedProperties</c> has nothing left to evaluate there, because
    /// <c>additionalProperties</c> has already claimed every property not named by
    /// <c>properties</c>. $defs/step carries its own top-level
    /// <c>"additionalProperties": true</c> (root-language-schema.json:143) — a
    /// SIBLING of where <c>unevaluatedProperties: false</c> would land. This test
    /// proves, empirically, that ADDING unevaluatedProperties as a sibling WITHOUT
    /// also removing/replacing that pre-existing key is a total, silent no-op: the
    /// exact 'taget' typo from the objective's own example still validates cleanly.
    /// This is why the 26-file diff must REPLACE $defs/step's own
    /// "additionalProperties": true with "unevaluatedProperties": false, not merely
    /// ADD the latter alongside the former.
    /// </summary>
    [Fact]
    public void NaiveFix_AddingUnevaluatedPropertiesWithoutRemovingStepsOwnAdditionalPropertiesTrue_IsSilentNoOp()
    {
        var registry = FullRegistry();
        var schema = BuildNaivelyFixedSchema(registry);

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

        var results = Evaluate(schema, instance);
        var errors = SchemaErrorCollector.CollectErrors(results);

        Assert.True(results.IsValid,
            $"Expected the NAIVE application (unevaluatedProperties added as a sibling of $defs/step's own additionalProperties:true, which stays) to be a silent no-op — 'taget' should still validate; got: {Dump(errors)}");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Depth-first search over the raw <see cref="EvaluationResults"/> tree (used by
    /// the Q2 test to find the specific node where the 'unevaluatedProperties' keyword
    /// fired, independent of <see cref="SchemaErrorCollector"/>'s own filtering/
    /// formatting — so the schema-level mechanism can be checked in isolation from the
    /// collector's message text).
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

    private static string Dump(System.Collections.Generic.IReadOnlyList<SchemaValidationError> errors) =>
        errors.Count == 0
            ? "(no errors)"
            : string.Join(Environment.NewLine, errors.Select(e => $"  loc='{e.InstanceLocation}' msg='{e.Message}'"));
}
