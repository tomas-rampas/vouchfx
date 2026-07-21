// Issue #259 — filter spurious "if"-discriminator errors out of schema
// validation error collection, at full 25-provider scale (§8.2, §13.6).
//
// Background: SchemaComposer.BuildIfThenClauses injects one unconditional
// if/then clause per registered provider into $defs/step/allOf. Evaluating
// with OutputFormat.List surfaces EVERY clause's own 'if' sub-evaluation as a
// flat sibling result, including the ones that legitimately do not match the
// step's 'type' — up to 24 spurious "Expected \"<other-provider-type>\""
// entries per invalid step at the full Core catalogue, none of which describe
// an actual document problem (see SchemaErrorCollector's remarks for the
// full mechanism and the exact evaluation-path shape this was verified
// against).
//
// This is the ONLY test fixture in the suite that builds the full 25-Core-
// provider registry to prove the error-collection path scales cleanly —
// SchemaFreezeTests (Golden/composed-schema.v1.json) is the only other place
// that assembles this same registry, for the unrelated purpose of freezing
// the composed schema BYTES. This file asserts nothing about those bytes and
// never touches that gate.
//
// Verified empirically (dotnet test, full 25-provider registry, before the
// fix) via a throwaway diagnostic probe against the RAW EvaluationResults
// tree:
//   Case A (http.rest step missing 'method'/'path'): 25 raw errors — ONE
//     genuine entry (loc=/steps/0, "[required] Required properties
//     [\"method\",\"path\"] are not present") plus 24 discriminator-noise
//     entries (loc=/steps/0/type, "[const] Expected \"<other-type>\"", one
//     per non-matching provider).
//   Case B (unknown type 'no-such.provider' + bad verifyMode): 26 raw errors
//     — ONE genuine entry (loc=/steps/0/verifyMode, "[enum] ...") plus 25
//     discriminator-noise entries (ALL 25 providers fail to match, since none
//     of their type consts equal 'no-such.provider').
//   Case C (fully valid http.rest suite): 0 errors, valid — untouched by the
//     filter either way.
// Confirmed via raw EvaluationPath dump: a noise node's path is always
// shaped '.../allOf/<N>/if/...' (e.g. '.../allOf/7/if/properties/type');
// the genuine 'then'-branch failure is '.../allOf/7/then' — never matched by
// the same check.
using System;
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

/// <summary>
/// Issue #259: error-collection noise filtering at full 25-Core-provider scale.
/// </summary>
public sealed class SchemaErrorCollectionAtScaleTests
{
    /// <summary>
    /// The Core provider assemblies that compose the full v1 schema — a
    /// deliberate, independent duplicate of
    /// <c>SchemaFreezeTests.CoreProviderAssemblies()</c> (itself mirroring
    /// <c>Vouchfx.Cli.ProviderRegistryFactory</c>). Kept as its own copy,
    /// rather than a shared reference, so this file has zero compile-time
    /// coupling to the frozen golden-file gate in SchemaFreezeTests.cs — that
    /// gate's assertions must never be touched by this file.
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

    // ── Full-scale registry sanity ─────────────────────────────────────────────

    /// <summary>
    /// The registry built from <see cref="CoreProviderAssemblies"/> must contain
    /// exactly 25 providers, every one of which contributes a
    /// <see cref="Vouchfx.Sdk.JsonSchemaFragment"/> — i.e. exactly 25 discriminator
    /// clauses land in the composed schema's <c>allOf</c>. This anchors the
    /// "full 25-provider scale" claim for every other test in this fixture.
    /// </summary>
    [Fact]
    public void FullRegistry_Contains25Providers_AllContributingSchemaFragments()
    {
        var registry = FullRegistry();

        Assert.Equal(25, registry.All.Count);
        Assert.Equal(25, registry.All.Count(p => p.SchemaFragment is not null));
    }

    // ── Case A: invalid step, one provider matches ─────────────────────────────

    /// <summary>
    /// An <c>http.rest</c> step missing its required <c>method</c>/<c>path</c>
    /// fields must, at full 25-provider scale, yield EXACTLY the one genuine
    /// "required" violation from the <c>http.rest</c> fragment's own <c>then</c>
    /// branch — none of the 24 other providers' non-matching discriminator
    /// clauses may leak an "Expected &lt;other type&gt;" entry into the result.
    /// </summary>
    /// <remarks>
    /// Before the fix, this yields 25 errors (1 genuine + 24 "if"-discriminator
    /// noise entries) — verified empirically against the raw
    /// <c>EvaluationResults</c> tree (see this file's header remarks).
    /// </remarks>
    [Fact]
    public void InvalidHttpRestStep_AtFullProviderScale_YieldsOnlyTheGenuineRequiredError()
    {
        var registry = FullRegistry();

        const string yaml = """
            steps:
              - id: bad-step
                type: http.rest
                target: orders-api
            """;

        var result = SchemaComposer.Validate(registry, yaml);

        Assert.False(result.IsValid, "Expected invalid: 'method' and 'path' are required by http.rest.");

        AssertNoDiscriminatorNoise(result, registry, exceptTypeKey: "http.rest");

        // Exactly one genuine error survives: the missing-required-properties
        // violation from http.rest's own 'then' fragment — not "at least one",
        // not "not empty": the 24 spurious discriminator entries must be gone.
        Assert.True(result.Errors.Count == 1,
            $"Expected exactly 1 genuine error but got {result.Errors.Count}:{Environment.NewLine}{FormatErrors(result)}");

        var genuine = result.Errors[0];
        Assert.Equal("/steps/0", genuine.InstanceLocation);
        Assert.Contains("required", genuine.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("method", genuine.Message, StringComparison.Ordinal);
        Assert.Contains("path", genuine.Message, StringComparison.Ordinal);
    }

    // ── Case B: unknown step type, NO provider matches ─────────────────────────

    /// <summary>
    /// A step whose <c>type</c> matches none of the 25 registered providers —
    /// combined with a genuine root-schema violation (<c>verifyMode</c> outside
    /// its <c>IMMEDIATE</c>/<c>RETRY</c> enum) so a real error exists to observe
    /// — must, at full 25-provider scale, yield EXACTLY that one genuine enum
    /// violation. This is the worst case for the noise filter: since no
    /// provider's <c>type</c> const matches, ALL 25 discriminator clauses fail
    /// their own <c>if</c> check and would each leak noise if unfiltered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A step with an unknown type and no OTHER violation is (both before and
    /// after this fix — this is an existing, out-of-scope gap, not something
    /// this fix changes) accepted as vacuously valid by the composed schema
    /// alone: an <c>if</c>/<c>then</c> clause with no <c>else</c> is satisfied
    /// whenever its <c>if</c> does not match, so when NONE of the 25 clauses
    /// match, the schema reports no error at all. Verified empirically: a
    /// minimal <c>{id, type: no-such.provider}</c> step returns
    /// <c>IsValid: true, Errors.Count: 0</c>. Detecting unknown step types is a
    /// separate concern (see e.g. the vouchfx-mcp <c>SuiteValidator</c>'s
    /// dedicated <c>StepTypeCatalogue</c> cross-check) and out of this issue's
    /// scope — this test instead pairs the unknown type with an independent
    /// root-schema violation so there IS a genuine error to prove survives
    /// the noise filter.
    /// </para>
    /// <para>
    /// Before the fix, this yields 26 errors (1 genuine + 25 "if"-discriminator
    /// noise entries, one per provider) — verified empirically.
    /// </para>
    /// </remarks>
    [Fact]
    public void UnknownStepTypeWithGenuineViolation_AtFullProviderScale_YieldsOnlyTheGenuineError()
    {
        var registry = FullRegistry();

        const string yaml = """
            steps:
              - id: bad-step
                type: no-such.provider
                verifyMode: SOMETIMES
            """;

        var result = SchemaComposer.Validate(registry, yaml);

        Assert.False(result.IsValid, "Expected invalid: verifyMode 'SOMETIMES' is not in the IMMEDIATE/RETRY enum.");

        // No provider's type const matches 'no-such.provider' — ALL 25 clauses
        // are candidates for noise here (there is no "exceptTypeKey" to spare).
        AssertNoDiscriminatorNoise(result, registry, exceptTypeKey: null);

        Assert.True(result.Errors.Count == 1,
            $"Expected exactly 1 genuine error but got {result.Errors.Count}:{Environment.NewLine}{FormatErrors(result)}");

        var genuine = result.Errors[0];
        Assert.Equal("/steps/0/verifyMode", genuine.InstanceLocation);
        Assert.Contains("enum", genuine.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Case C: valid suite stays valid — no false positives ───────────────────

    /// <summary>
    /// A fully valid <c>http.rest</c> suite must remain valid with zero errors
    /// at full 25-provider scale: the noise filter must never suppress a
    /// genuine error NOR fabricate one, and it must never flip a valid document
    /// to invalid.
    /// </summary>
    [Fact]
    public void ValidFullSuite_AtFullProviderScale_StaysValid()
    {
        var registry = FullRegistry();

        const string yaml = """
            steps:
              - id: call-api
                type: http.rest
                target: orders-api
                method: GET
                path: /health
            """;

        var result = SchemaComposer.Validate(registry, yaml);

        Assert.True(result.IsValid, $"Expected valid but got: {FormatErrors(result)}");
        Assert.Empty(result.Errors);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts that none of <paramref name="result"/>'s collected error messages
    /// reference any OTHER registered provider's <c>&lt;family&gt;.&lt;provider&gt;</c>
    /// discriminator key. A genuine "if"-discriminator noise entry always embeds
    /// the mismatched clause's own type key as its <c>const</c> keyword's expected
    /// value (e.g. <c>"Expected \"db-assert.postgres\""</c>), so this is a robust,
    /// black-box way to prove the noise is gone without coupling the test to the
    /// exact keyword-tag/quoting format <see cref="SchemaErrorCollector"/> happens
    /// to emit.
    /// </summary>
    /// <param name="exceptTypeKey">
    /// The step's own (matching) type key, excluded from the noise scan since it
    /// legitimately appears in a genuine <c>then</c>-branch error's context; pass
    /// <see langword="null"/> when no provider matches at all (every key is a
    /// noise candidate).
    /// </param>
    private static void AssertNoDiscriminatorNoise(
        SchemaValidationResult result, StepKindRegistry registry, string? exceptTypeKey)
    {
        var otherTypeKeys = registry.All
            .Select(p => $"{p.Kind.Family}.{p.Kind.Provider}")
            .Where(key => key != exceptTypeKey)
            .ToList();

        foreach (var error in result.Errors)
        {
            foreach (var key in otherTypeKeys)
            {
                Assert.True(
                    !error.Message.Contains(key, StringComparison.Ordinal),
                    $"Found discriminator-noise: error message references OTHER provider type " +
                    $"'{key}': loc='{error.InstanceLocation}' msg='{error.Message}'");
            }
        }
    }

    private static string FormatErrors(SchemaValidationResult result) =>
        string.Join(Environment.NewLine, result.Errors.Select(e => $"  loc='{e.InstanceLocation}' msg='{e.Message}'"));
}
