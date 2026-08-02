// Pre-GA schema completeness (T3d remainder, feat/fragment-completeness) — Part 3.
//
// Pins every Validate()-only constraint lifted into a provider's JsonSchemaFragment:
// each was previously enforced ONLY at runtime by IStepValidator.Validate (an "S*"
// gap — schema too weak), and is now also rejected at schema/authoring time. Uses
// DocumentValidator directly (inline YAML), the same production path
// SchemaStepSurfaceClosureTests and the Corpus/Accepted|Rejected gates exercise,
// rather than a separate corpus file per case — more compact for a suite this wide
// while still pinning exact InstanceLocation + keyword per the brief's requirement.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

public sealed class SchemaValidateConstraintsTests
{
    private static Assembly[] CoreProviderAssemblies() => new[]
    {
        typeof(HttpRestProvider).Assembly,
        typeof(DbAssertPostgresProvider).Assembly,
        typeof(DbAssertSqlServerProvider).Assembly,
        typeof(DbAssertMongodbProvider).Assembly,
        typeof(DbAssertMysqlProvider).Assembly,
        typeof(ScriptCsharpProvider).Assembly,
        typeof(MqPublishKafkaProvider).Assembly,
        typeof(MqExpectKafkaProvider).Assembly,
        typeof(WebhookListenHttpProvider).Assembly,
        typeof(MailExpectSmtpProvider).Assembly,
        typeof(CacheAssertRedisProvider).Assembly,
        typeof(MqPublishRabbitmqProvider).Assembly,
        typeof(MqExpectRabbitmqProvider).Assembly,
        typeof(MqPublishNatsProvider).Assembly,
        typeof(MqExpectNatsProvider).Assembly,
        typeof(CacheAssertElasticsearchProvider).Assembly,
        typeof(MqPublishAzureServiceBusProvider).Assembly,
        typeof(MqExpectAzureServiceBusProvider).Assembly,
        typeof(MqPublishRedisProvider).Assembly,
        typeof(MqExpectRedisProvider).Assembly,
        typeof(MetricsAssertPrometheusProvider).Assembly,
        typeof(DbAssertDynamodbProvider).Assembly,
        typeof(StorageAssertS3Provider).Assembly,
        typeof(TraceExpectOtlpProvider).Assembly,
        typeof(HttpSoapProvider).Assembly,
    };

    private static readonly StepKindRegistry Registry = StepKindRegistry.BuildAndFreeze(CoreProviderAssemblies());

    private static string Dump(IReadOnlyList<SchemaValidationError> errors) =>
        errors.Count == 0
            ? "(no errors)"
            : string.Join(Environment.NewLine, errors.Select(e => $"  loc='{e.InstanceLocation}' msg='{e.Message}'"));

    private static void AssertRejectedAt(string yaml, string expectedLocation, string expectedKeyword)
    {
        var result = DocumentValidator.Validate(yaml, Registry);
        Assert.False(result.IsValid, $"Expected rejection but the document validated. YAML:{Environment.NewLine}{yaml}");
        var tag = $"[{expectedKeyword}]";
        var matched = result.Errors.Any(e =>
            string.Equals(e.InstanceLocation, expectedLocation, StringComparison.Ordinal) &&
            e.Message.Contains(tag, StringComparison.Ordinal));
        Assert.True(matched,
            $"Expected a rejection at '{expectedLocation}' carrying '{tag}'; got:{Environment.NewLine}{Dump(result.Errors)}");
    }

    private static void AssertAccepted(string yaml)
    {
        var result = DocumentValidator.Validate(yaml, Registry);
        Assert.True(result.IsValid, $"Expected valid; got:{Environment.NewLine}{Dump(result.Errors)}");
    }

    // ── SSRF path pattern regex verification (http.rest / http.soap / metrics-assert.prometheus) ──

    [Fact]
    public void HttpRest_Path_ProtocolRelative_IsRejectedByPattern()
    {
        const string yaml = """
            steps:
              - id: s1
                type: http.rest
                target: api
                method: GET
                path: "//evil.example.com/steal"
            """;
        AssertRejectedAt(yaml, "/steps/0/path", "pattern");
    }

    [Fact]
    public void HttpRest_Path_AbsoluteUrl_IsRejectedByPattern()
    {
        const string yaml = """
            steps:
              - id: s1
                type: http.rest
                target: api
                method: GET
                path: "http://evil.example.com/steal"
            """;
        AssertRejectedAt(yaml, "/steps/0/path", "pattern");
    }

    [Fact]
    public void HttpRest_Path_Backslash_IsRejectedByPattern()
    {
        const string yaml = """
            steps:
              - id: s1
                type: http.rest
                target: api
                method: GET
                path: "/a\\b"
            """;
        AssertRejectedAt(yaml, "/steps/0/path", "pattern");
    }

    [Fact]
    public void HttpRest_Path_RootedRelative_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: s1
                type: http.rest
                target: api
                method: GET
                path: /orders/123/items
            """;
        AssertAccepted(yaml);
    }

    [Fact]
    public void HttpRest_Path_SingleSlash_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: s1
                type: http.rest
                target: api
                method: GET
                path: /
            """;
        AssertAccepted(yaml);
    }

    [Fact]
    public void HttpSoap_Path_ProtocolRelative_IsRejectedByPattern()
    {
        const string yaml = """
            steps:
              - id: s1
                type: http.soap
                target: api
                path: "//evil.example.com/steal"
                envelope: "<a/>"
            """;
        AssertRejectedAt(yaml, "/steps/0/path", "pattern");
    }

    [Fact]
    public void MetricsAssertPrometheus_Path_ProtocolRelative_IsRejectedByPattern()
    {
        const string yaml = """
            steps:
              - id: s1
                type: metrics-assert.prometheus
                target: api
                path: "//evil.example.com/steal"
                metric: orders_total
                expect:
                  min: "0"
            """;
        AssertRejectedAt(yaml, "/steps/0/path", "pattern");
    }

    [Fact]
    public void MetricsAssertPrometheus_Path_Omitted_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: s1
                type: metrics-assert.prometheus
                target: api
                metric: orders_total
                expect:
                  min: "0"
            """;
        AssertAccepted(yaml);
    }

    // ── cache-assert.elasticsearch 'index' pattern regex verification ──────────

    [Fact]
    public void CacheAssertElasticsearch_Index_ContainingSpace_IsRejectedByPattern()
    {
        const string yaml = """
            steps:
              - id: s1
                type: cache-assert.elasticsearch
                target: search
                index: "bad index"
                expect:
                  min-count: 1
            """;
        AssertRejectedAt(yaml, "/steps/0/index", "pattern");
    }

    [Fact]
    public void CacheAssertElasticsearch_Index_ContainingQuestionMark_IsRejectedByPattern()
    {
        const string yaml = """
            steps:
              - id: s1
                type: cache-assert.elasticsearch
                target: search
                index: "bad?index"
                expect:
                  min-count: 1
            """;
        AssertRejectedAt(yaml, "/steps/0/index", "pattern");
    }

    [Fact]
    public void CacheAssertElasticsearch_Index_ContainingHash_IsRejectedByPattern()
    {
        const string yaml = """
            steps:
              - id: s1
                type: cache-assert.elasticsearch
                target: search
                index: "bad#index"
                expect:
                  min-count: 1
            """;
        AssertRejectedAt(yaml, "/steps/0/index", "pattern");
    }

    [Fact]
    public void CacheAssertElasticsearch_Index_WithCommaAndWildcard_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: s1
                type: cache-assert.elasticsearch
                target: search
                index: "orders-*,shipments"
                expect:
                  min-count: 1
            """;
        AssertAccepted(yaml);
    }

    [Fact]
    public void CacheAssertElasticsearch_FieldsItem_DottedFieldName_IsRejectedByPattern()
    {
        const string yaml = """
            steps:
              - id: s1
                type: cache-assert.elasticsearch
                target: search
                index: orders
                expect:
                  min-count: 1
                  fields:
                    - field: "a.b"
                      value: "1"
            """;
        AssertRejectedAt(yaml, "/steps/0/expect/fields/0/field", "pattern");
    }

    // ── mq-publish.azureservicebus: exactly one of queue/topic (oneOf) ─────────

    [Fact]
    public void MqPublishAzureServiceBus_NeitherQueueNorTopic_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: mq-publish.azureservicebus
                target: orders-asb
                payload: hello
            """;
        var result = DocumentValidator.Validate(yaml, Registry);
        Assert.False(result.IsValid, "Expected rejection when neither 'queue' nor 'topic' is set.");
        // Both oneOf branches genuinely fail their own 'required' check — each is a
        // real, independently-reportable defect (not noise: the composite as a
        // whole is unsatisfied, so SuppressSatisfiedCompositeBranchNoise leaves both).
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/steps/0" && e.Message.Contains("[required]", StringComparison.Ordinal));
    }

    [Fact]
    public void MqPublishAzureServiceBus_BothQueueAndTopic_IsRejected_WithNamedOneOfMessage()
    {
        const string yaml = """
            steps:
              - id: s1
                type: mq-publish.azureservicebus
                target: orders-asb
                queue: orders
                topic: orders-topic
                payload: hello
            """;
        var result = DocumentValidator.Validate(yaml, Registry);
        Assert.False(result.IsValid, "Expected rejection when both 'queue' and 'topic' are set.");
        // The too-many-oneOf-matches synthesis (SchemaErrorCollector) names both fields.
        var oneOfError = Assert.Single(result.Errors, e => e.Message.Contains("[oneOf]", StringComparison.Ordinal));
        Assert.Contains("'queue'", oneOfError.Message, StringComparison.Ordinal);
        Assert.Contains("'topic'", oneOfError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MqPublishAzureServiceBus_QueueOnly_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: s1
                type: mq-publish.azureservicebus
                target: orders-asb
                queue: orders
                payload: hello
            """;
        AssertAccepted(yaml);
    }

    // ── mq-expect.azureservicebus: queue XOR (topic+subscription); dependentRequired; ≥1 of expectPayloadContains/expectProperties ──

    [Fact]
    public void MqExpectAzureServiceBus_QueueOnly_WithPayloadContains_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: s1
                type: mq-expect.azureservicebus
                target: orders-asb
                queue: orders
                expectPayloadContains: order
            """;
        AssertAccepted(yaml);
    }

    [Fact]
    public void MqExpectAzureServiceBus_TopicAndSubscription_WithExpectProperties_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: s1
                type: mq-expect.azureservicebus
                target: orders-asb
                topic: orders-topic
                subscription: orders-sub
                expectProperties:
                  orderId: "1"
            """;
        AssertAccepted(yaml);
    }

    [Fact]
    public void MqExpectAzureServiceBus_NeitherExpectCriterion_IsRejected_WithRequiredMessage()
    {
        const string yaml = """
            steps:
              - id: s1
                type: mq-expect.azureservicebus
                target: orders-asb
                queue: orders
            """;
        var result = DocumentValidator.Validate(yaml, Registry);
        Assert.False(result.IsValid, "Expected rejection when neither expectPayloadContains nor expectProperties is set.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/steps/0" && e.Message.Contains("[required]", StringComparison.Ordinal));
    }

    [Fact]
    public void MqExpectAzureServiceBus_TopicWithoutSubscription_IsRejected_WithActionableMessage()
    {
        const string yaml = """
            steps:
              - id: s1
                type: mq-expect.azureservicebus
                target: orders-asb
                topic: orders-topic
                expectPayloadContains: order
            """;
        var result = DocumentValidator.Validate(yaml, Registry);
        Assert.False(result.IsValid, "Expected rejection when 'topic' is set without 'subscription'.");
        // dependentRequired fires a specific, correctly-located message naming the pair.
        var located = result.Errors.Where(e => e.InstanceLocation == "/steps/0").ToList();
        Assert.True(located.Count > 0, $"Expected at least one error at '/steps/0'; got:{Environment.NewLine}{Dump(result.Errors)}");
        Assert.Contains(located, e => e.Message.Contains("[dependentRequired]", StringComparison.Ordinal));
    }

    [Fact]
    public void MqExpectAzureServiceBus_QueueAndTopicBoth_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: mq-expect.azureservicebus
                target: orders-asb
                queue: orders
                topic: orders-topic
                subscription: orders-sub
                expectPayloadContains: order
            """;
        var result = DocumentValidator.Validate(yaml, Registry);
        Assert.False(result.IsValid, "Expected rejection when both 'queue' and the topic+subscription pair are set.");
    }

    // ── cache-assert.redis: field required iff hget; expectation matches operation ──

    [Fact]
    public void CacheAssertRedis_Hget_WithoutField_IsRejected_WithRequiredMessage()
    {
        const string yaml = """
            steps:
              - id: s1
                type: cache-assert.redis
                target: cache
                key: orders:1
                operation: hget
                expect:
                  value: "1"
            """;
        AssertRejectedAt(yaml, "/steps/0", "required");
    }

    [Fact]
    public void CacheAssertRedis_Hget_WithFieldAndValue_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: s1
                type: cache-assert.redis
                target: cache
                key: orders:1
                operation: hget
                field: total
                expect:
                  value: "1"
            """;
        AssertAccepted(yaml);
    }

    [Fact]
    public void CacheAssertRedis_Hget_WithEmptyField_IsRejectedByMinLength()
    {
        const string yaml = """
            steps:
              - id: s1
                type: cache-assert.redis
                target: cache
                key: orders:1
                operation: hget
                field: ""
                expect:
                  value: "1"
            """;
        AssertRejectedAt(yaml, "/steps/0/field", "minLength");
    }

    /// <summary>
    /// M3 (gatekeeper): EXACT PARITY with CacheAssertRedisProvider.Validate, not
    /// merely documentation of intent. Validate's own emptiness check
    /// (<c>model.Operation == RedisOp.HGet &amp;&amp; string.IsNullOrWhiteSpace(model.Field)</c>)
    /// fires ONLY for hget — an empty 'field' on a NON-hget operation (here,
    /// 'get', which never reads 'field' at all) always ran successfully before
    /// this schema work landed. An unconditional 'minLength: 1' on 'field'
    /// would reject this and silently narrow behaviour beyond what Validate
    /// ever enforced — the minLength must live inside the hget if/then branch
    /// only, exactly as this test pins.
    /// </summary>
    [Fact]
    public void CacheAssertRedis_Get_WithEmptyField_IsAccepted_ParityWithValidate()
    {
        const string yaml = """
            steps:
              - id: s1
                type: cache-assert.redis
                target: cache
                key: orders:1
                operation: get
                field: ""
                expect:
                  value: "1"
            """;
        AssertAccepted(yaml);
    }

    [Theory]
    [InlineData("get", "value")]
    [InlineData("hget", "value")]
    [InlineData("exists", "exists")]
    [InlineData("ttl", "exists")]
    [InlineData("hlen", "length")]
    [InlineData("llen", "length")]
    [InlineData("scard", "length")]
    public void CacheAssertRedis_ExpectationMemberMismatchingOperation_IsRejected(
        string operation, string wrongMember)
    {
        // Deliberately supplies a DIFFERENT expect member than the one 'operation'
        // requires (e.g. operation: exists with expect.value set, never expect.exists).
        var offMember = wrongMember == "value" ? "exists" : "value";
        var offValue = offMember == "exists" ? "true" : "\"x\"";
        var field = operation == "hget" ? "\n    field: total" : string.Empty;
        var yaml = $$"""
            steps:
              - id: s1
                type: cache-assert.redis
                target: cache
                key: orders:1
                operation: {{operation}}{{field}}
                expect:
                  {{offMember}}: {{offValue}}
            """;
        var result = DocumentValidator.Validate(yaml, Registry);
        Assert.False(result.IsValid,
            $"{operation}: expected rejection when expect.{offMember} is set instead of expect.{wrongMember}.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/steps/0/expect" && e.Message.Contains("[required]", StringComparison.Ordinal));
    }

    // ── db-assert.{postgres,sqlserver,mysql}: expect needs rowCount and/or row ──

    [Theory]
    [InlineData("db-assert.postgres")]
    [InlineData("db-assert.sqlserver")]
    [InlineData("db-assert.mysql")]
    public void DbAssertSql_ExpectWithNeitherRowCountNorRow_IsRejected(string typeKey)
    {
        var yaml = $$"""
            steps:
              - id: s1
                type: {{typeKey}}
                target: db
                query: SELECT 1
                expect: {}
            """;
        var result = DocumentValidator.Validate(yaml, Registry);
        Assert.False(result.IsValid, $"{typeKey}: expected rejection when expect declares neither rowCount nor row.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/steps/0/expect" && e.Message.Contains("[required]", StringComparison.Ordinal));
    }

    [Fact]
    public void DbAssertMongodb_ExpectWithNeitherCountNorDocument_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: db-assert.mongodb
                target: db
                collection: orders
                filter: "{}"
                expect: {}
            """;
        var result = DocumentValidator.Validate(yaml, Registry);
        Assert.False(result.IsValid, "Expected rejection when expect declares neither count nor document.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/steps/0/expect" && e.Message.Contains("[required]", StringComparison.Ordinal));
    }

    // ── B2 (gatekeeper): the ["integer","string"] widening opened a silent-pass path ──
    //
    // expect.status: "abc" -> int.TryParse fails -> null -> the provider asserts
    // NOTHING -> a green run that proves nothing (proven against the built CLI by the
    // gatekeeper). Fixed by adding "pattern": "^[0-9]+$" alongside the existing
    // ["integer","string"] union on all ten widened integer fields (a no-op on the
    // integer branch — JSON Schema's 'pattern' keyword only ever applies to string
    // instances — the same no-op-keyword trick httpPort already established in this
    // schema). This preserves the intended widening ("200") while rejecting "abc",
    // "2xx", and an unresolved "{placeholder}" token exactly as before the widening
    // (verified separately: these fields carry no {placeholder} substitution in any
    // provider's Bind, so a literal, un-substituted "{placeholder}" was ALWAYS destined
    // to fail int.TryParse and silently no-op — the pattern restores that original
    // REJECTION at the schema layer instead of leaving it as a silent runtime no-op).
    //
    // Verified count (gatekeeper said eleven; the actual, grepped count of
    // '"type": ["integer", "string"]' occurrences across every provider fragment is
    // TEN — reported as a discrepancy, not silently reconciled): http.rest and
    // http.soap's expect.status; db-assert.postgres/sqlserver/mysql's expect.rowCount;
    // db-assert.mongodb's expect.count; cache-assert.elasticsearch's expect.count AND
    // expect.min-count (two distinct fields on the same provider); mail-expect.smtp's
    // expect.count; cache-assert.redis's expect.length.
    [Theory]
    [MemberData(nameof(WidenedIntegerFieldCases))]
    public void WidenedIntegerField_NonDigitString_IsRejectedByPattern(string yaml, string expectedLocation)
    {
        AssertRejectedAt(yaml, expectedLocation, "pattern");
    }

    [Theory]
    [MemberData(nameof(WidenedIntegerFieldCases))]
    public void WidenedIntegerField_QuotedNumericString_IsStillAccepted(string yamlWithNonDigit, string _)
    {
        // Same ten locations, but with a QUOTED NUMERIC string in place of "abc" —
        // proves the pattern preserves the intended T2a widening rather than silently
        // reverting to integer-only. Reuses the same case list, textually substituting
        // the sentinel "abc" for "200" (every case below writes literally "abc").
        var yamlWithDigits = yamlWithNonDigit.Replace("\"abc\"", "\"200\"", StringComparison.Ordinal);
        AssertAccepted(yamlWithDigits);
    }

    public static IEnumerable<object[]> WidenedIntegerFieldCases()
    {
        yield return new object[]
        {
            """
            steps:
              - id: s1
                type: http.rest
                target: api
                method: GET
                path: /health
                expect:
                  status: "abc"
            """,
            "/steps/0/expect/status",
        };
        yield return new object[]
        {
            """
            steps:
              - id: s1
                type: http.soap
                target: api
                path: /soap
                envelope: "<a/>"
                expect:
                  status: "abc"
            """,
            "/steps/0/expect/status",
        };
        yield return new object[]
        {
            """
            steps:
              - id: s1
                type: db-assert.postgres
                target: db
                query: SELECT 1
                expect:
                  rowCount: "abc"
            """,
            "/steps/0/expect/rowCount",
        };
        yield return new object[]
        {
            """
            steps:
              - id: s1
                type: db-assert.sqlserver
                target: db
                query: SELECT 1
                expect:
                  rowCount: "abc"
            """,
            "/steps/0/expect/rowCount",
        };
        yield return new object[]
        {
            """
            steps:
              - id: s1
                type: db-assert.mysql
                target: db
                query: SELECT 1
                expect:
                  rowCount: "abc"
            """,
            "/steps/0/expect/rowCount",
        };
        yield return new object[]
        {
            """
            steps:
              - id: s1
                type: db-assert.mongodb
                target: db
                collection: orders
                filter: "{}"
                expect:
                  count: "abc"
            """,
            "/steps/0/expect/count",
        };
        yield return new object[]
        {
            """
            steps:
              - id: s1
                type: cache-assert.elasticsearch
                target: search
                index: orders
                expect:
                  count: "abc"
            """,
            "/steps/0/expect/count",
        };
        yield return new object[]
        {
            """
            steps:
              - id: s1
                type: cache-assert.elasticsearch
                target: search
                index: orders
                expect:
                  min-count: "abc"
            """,
            "/steps/0/expect/min-count",
        };
        yield return new object[]
        {
            """
            steps:
              - id: s1
                type: mail-expect.smtp
                target: mailpit
                expect:
                  count: "abc"
                  match:
                    to: a@b.com
            """,
            "/steps/0/expect/count",
        };
        yield return new object[]
        {
            """
            steps:
              - id: s1
                type: cache-assert.redis
                target: cache
                key: orders:1
                operation: hlen
                expect:
                  length: "abc"
            """,
            "/steps/0/expect/length",
        };
    }

    /// <summary>
    /// A ${secret:...}-free, unresolved {placeholder} token in a widened integer
    /// field must ALSO be rejected by the pattern — parity with, not a regression
    /// from, pre-widening behaviour (measured: none of these fields' Bind methods
    /// apply {placeholder} substitution before int/long.TryParse, so a literal
    /// unresolved token was always destined to fail the parse and silently no-op;
    /// the schema now catches it at authoring time instead).
    /// </summary>
    [Fact]
    public void HttpRest_ExpectStatus_UnresolvedPlaceholder_IsRejectedByPattern()
    {
        const string yaml = """
            steps:
              - id: s1
                type: http.rest
                target: api
                method: GET
                path: /health
                expect:
                  status: "{previousStatus}"
            """;
        AssertRejectedAt(yaml, "/steps/0/expect/status", "pattern");
    }

    // ── db-assert.dynamodb: exists:false forbids item ───────────────────────────

    [Fact]
    public void DbAssertDynamodb_ExistsFalse_WithItem_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: db-assert.dynamodb
                target: table
                table: orders
                key: "{\"orderId\":\"1\"}"
                expect:
                  exists: false
                  item:
                    status: shipped
            """;
        AssertRejectedAt(yaml, "/steps/0/expect/item", "properties");

        // M5 (gatekeeper): the message must name WHY, not just WHAT — derived from
        // the live schema's own if.properties.exists.const, never hardcoded.
        var result = DocumentValidator.Validate(yaml, Registry);
        var error = Assert.Single(result.Errors);
        Assert.EndsWith(
            "[properties] Property 'item' is not valid when 'exists' is false",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DbAssertDynamodb_ExistsFalse_WithoutItem_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: s1
                type: db-assert.dynamodb
                target: table
                table: orders
                key: "{\"orderId\":\"1\"}"
                expect:
                  exists: false
            """;
        AssertAccepted(yaml);
    }

    // ── storage-assert.s3: size XOR minSize; exists:false forbids six content expectations ──

    [Fact]
    public void StorageAssertS3_SizeAndMinSizeBoth_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: storage-assert.s3
                target: bucket
                bucket: orders
                key: receipts/1.json
                expect:
                  size: 100
                  minSize: 10
            """;
        AssertRejectedAt(yaml, "/steps/0/expect/minSize", "properties");

        // M5 (gatekeeper): named via the live schema's own sibling if.required,
        // never hardcoded field names.
        var result = DocumentValidator.Validate(yaml, Registry);
        var error = Assert.Single(result.Errors);
        Assert.EndsWith(
            "[properties] Property 'minSize' cannot be combined with 'size' — exactly one of the two may be set",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StorageAssertS3_ExistsFalse_WithSize_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: storage-assert.s3
                target: bucket
                bucket: orders
                key: receipts/1.json
                expect:
                  exists: false
                  size: 100
            """;
        AssertRejectedAt(yaml, "/steps/0/expect/size", "properties");

        var result = DocumentValidator.Validate(yaml, Registry);
        var error = Assert.Single(result.Errors);
        Assert.EndsWith(
            "[properties] Property 'size' is not valid when 'exists' is false",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StorageAssertS3_ExistsFalse_Alone_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: s1
                type: storage-assert.s3
                target: bucket
                bucket: orders
                key: receipts/1.json
                expect:
                  exists: false
            """;
        AssertAccepted(yaml);
    }

    // ── minProperties:1 additions ────────────────────────────────────────────────

    [Fact]
    public void MetricsAssertPrometheus_EmptyExpect_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: metrics-assert.prometheus
                target: api
                metric: orders_total
                expect: {}
            """;
        AssertRejectedAt(yaml, "/steps/0/expect", "minProperties");
    }

    [Fact]
    public void MailExpectSmtp_EmptyMatch_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: mail-expect.smtp
                target: mailpit
                expect:
                  match: {}
            """;
        AssertRejectedAt(yaml, "/steps/0/expect/match", "minProperties");
    }

    [Theory]
    [InlineData("mq-expect.kafka", "topic: orders")]
    [InlineData("mq-expect.rabbitmq", "queue: orders")]
    [InlineData("mq-expect.nats", "subject: orders")]
    [InlineData("mq-expect.redis", "stream: orders")]
    public void MqExpect_EmptyMatch_IsRejected(string typeKey, string ownField)
    {
        var yaml = $$"""
            steps:
              - id: s1
                type: {{typeKey}}
                target: dep
                {{ownField}}
                match: {}
            """;
        AssertRejectedAt(yaml, "/steps/0/match", "minProperties");
    }

    [Fact]
    public void WebhookListenHttp_EmptyMatch_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: webhook-listen.http
                listener: hook
                match: {}
            """;
        AssertRejectedAt(yaml, "/steps/0/match", "minProperties");
    }

    [Fact]
    public void MqPublishKafka_AvroRecordEmpty_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: mq-publish.kafka
                target: events
                topic: orders
                payload: "{}"
                avro:
                  schemaRegistry: events
                  subject: orders-value
                  schema: "{\"type\":\"record\",\"name\":\"Order\",\"fields\":[]}"
                  record: {}
            """;
        AssertRejectedAt(yaml, "/steps/0/avro/record", "minProperties");
    }

    // ── script.csharp: maxLength:65536 on code ──────────────────────────────────

    [Fact]
    public void ScriptCsharp_CodeExceeding64KiB_IsRejected()
    {
        var oversized = new string('a', 65537);
        var yaml = $$"""
            steps:
              - id: s1
                type: script.csharp
                code: "{{oversized}}"
            """;
        AssertRejectedAt(yaml, "/steps/0/code", "maxLength");
    }

    [Fact]
    public void ScriptCsharp_EmptyCode_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: script.csharp
                code: ""
            """;
        AssertRejectedAt(yaml, "/steps/0/code", "minLength");
    }

    // ── minLength:1 representative samples across common/nested positions ──────

    [Fact]
    public void HttpRest_EmptyTarget_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: http.rest
                target: ""
                method: GET
                path: /health
            """;
        AssertRejectedAt(yaml, "/steps/0/target", "minLength");
    }

    [Fact]
    public void MqPublishKafka_EmptyAvroSchemaRegistry_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: mq-publish.kafka
                target: events
                topic: orders
                payload: "{}"
                avro:
                  schemaRegistry: ""
                  subject: orders-value
                  schema: "{}"
                  record:
                    id: "1"
            """;
        AssertRejectedAt(yaml, "/steps/0/avro/schemaRegistry", "minLength");
    }

    [Fact]
    public void HttpSoap_XpathItem_EmptyPath_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: http.soap
                target: api
                path: /soap
                envelope: "<a/>"
                expect:
                  xpath:
                    - path: ""
                      value: "1"
            """;
        AssertRejectedAt(yaml, "/steps/0/expect/xpath/0/path", "minLength");
    }
}
