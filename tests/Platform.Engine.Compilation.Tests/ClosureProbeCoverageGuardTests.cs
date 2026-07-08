// S10-D-02 — Memory-leak gate provably covers every Core provider's closure.
//
// The permanent M1 memory-leak gate (ClosureMemoryProbeTests + the 5,000-iteration
// harness in CI) exercises the transitive client closure of every Core provider inside
// a collectible AssemblyLoadContext, so a singleton pinner that anchors a reference
// across the collectible boundary is caught.  But that gate's value depends on the
// closure probe ACTUALLY touching each provider's canonical client: a new Core provider
// could be added whose client never gets exercised, and
// the leak gate would stay green while silently NOT covering it.
//
// This guard makes that failure impossible to introduce silently.  It pins, as the
// source of truth, an explicit table mapping each Core provider to the
// canonical client / closure marker its leak coverage depends on, and then asserts:
//
//   1. The enumerated Core-provider table EQUALS the real Core-provider set — built by
//      reflecting the SAME anchor assemblies SchemaFreezeTests /
//      VsCodeShippedSchemaSyncTests / the CLI's ProviderRegistryFactory use, frozen
//      through StepKindRegistry.BuildAndFreeze.  So if a new Core provider is added (or
//      one is renamed/removed) without updating this table, THIS test fails — it can't
//      go stale against the real registry.
//
//   2. ClosureProbeScript.Source actually CONTAINS each provider's closure marker.  So
//      adding a new provider to the table (to satisfy #1) without also extending
//      ClosureProbeScript to exercise its client makes THIS test fail too.
//
// The net effect: adding a Core provider is a deliberate three-place edit — the new
// provider project, ClosureProbeScript.Source, and this table — and any partial edit is
// a red test.  This is the enumeration guard S10-D-02 asks for, in its preferred form
// (an explicit enumerated list cross-checked against the real provider set so it cannot
// drift), as a TEST-ONLY guard that touches no production logic.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Platform.Engine.Compilation.MemoryHarness;
using Platform.Sdk;
using Platform.Steps.CacheAssert.Elasticsearch;
using Platform.Steps.CacheAssert.Redis;
using Platform.Steps.DbAssert.Dynamodb;
using Platform.Steps.DbAssert.Mongodb;
using Platform.Steps.DbAssert.Mysql;
using Platform.Steps.DbAssert.Postgres;
using Platform.Steps.DbAssert.SqlServer;
using Platform.Steps.Http.Soap;
using Platform.Steps.HttpRest;
using Platform.Steps.MailExpect.Smtp;
using Platform.Steps.MetricsAssert.Prometheus;
using Platform.Steps.MqExpect.AzureServiceBus;
using Platform.Steps.MqExpect.Kafka;
using Platform.Steps.MqExpect.Nats;
using Platform.Steps.MqExpect.Rabbitmq;
using Platform.Steps.MqExpect.Redis;
using Platform.Steps.MqPublish.AzureServiceBus;
using Platform.Steps.MqPublish.Kafka;
using Platform.Steps.MqPublish.Nats;
using Platform.Steps.MqPublish.Rabbitmq;
using Platform.Steps.MqPublish.Redis;
using Platform.Steps.Script.Csharp;
using Platform.Steps.StorageAssert.S3;
using Platform.Steps.TraceExpect.Otlp;
using Platform.Steps.WebhookListen.Http;
using Xunit;

namespace Platform.Engine.Compilation.Tests;

/// <summary>
/// S10-D-02: the enumeration guard that ties the memory-leak closure probe to every Core
/// provider, cross-checked against the real frozen registry so it cannot go stale.
/// </summary>
public sealed class ClosureProbeCoverageGuardTests
{
    /// <summary>
    /// Describes one Core provider's leak-gate closure coverage: its
    /// <c>&lt;family&gt;.&lt;provider&gt;</c> step kind, a human-readable name for the
    /// canonical client whose static initialisers/handles its closure must exercise, and
    /// the literal MARKER string that must appear in <see cref="ClosureProbeScript.Source"/>
    /// to prove the probe genuinely touches that client.
    /// </summary>
    /// <param name="StepKind">
    /// The composite step-kind key in the form <c>&lt;family&gt;.&lt;provider&gt;</c>.
    /// </param>
    /// <param name="CanonicalClient">
    /// A human-readable description of the canonical client / closure the leak gate must
    /// touch for this provider (used only in failure messages).
    /// </param>
    /// <param name="ProbeMarker">
    /// A fully-qualified type reference (or accessor) that must be present verbatim in the
    /// closure probe CSX source, proving the probe exercises this provider's closure.
    /// </param>
    private sealed record CoreProviderCoverage(
        string StepKind,
        string CanonicalClient,
        string ProbeMarker);

    /// <summary>
    /// THE SOURCE OF TRUTH for which Core providers the memory-leak gate must cover.
    ///
    /// A new Core provider MUST be added here AND its canonical client exercised in
    /// <see cref="ClosureProbeScript.Source"/> (the <see cref="CoreProviderCoverage.ProbeMarker"/>
    /// must appear in that CSX body).  Both halves of this guard then enforce the pairing:
    /// <see cref="EnumeratedCoverage_EqualsRealCoreProviderSet"/> proves this list equals the
    /// real registry, and <see cref="ClosureProbe_Exercises_EachCoreProviderClient"/> proves
    /// each marker is present in the probe.
    /// </summary>
    /// <remarks>
    /// Marker choices, per provider:
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>http.rest</c> → <c>System.Net.Http.HttpClient</c>: the provider issues REST
    ///     calls through <c>HttpClient</c>/<c>SocketsHttpHandler</c>; the probe creates one
    ///     to trigger the handler pool.
    ///   </description></item>
    ///   <item><description>
    ///     <c>db-assert.postgres</c> → <c>Npgsql</c>: the provider asserts against Postgres
    ///     via Npgsql; the probe builds an <c>NpgsqlConnectionStringBuilder</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <c>script.csharp</c> → the Polly-backed <c>RetryRunner</c> + <c>Vars</c> boundary:
    ///     script.csharp runs author CSX through the SAME compile-once/collectible-ALC path
    ///     the probe itself uses, and the probe drives <c>RetryRunner.PollAsync</c> across the
    ///     ALC boundary — the closure this provider's leak behaviour depends on.
    ///   </description></item>
    ///   <item><description>
    ///     <c>mq-publish.kafka</c> → <c>Confluent.Kafka.ProducerBuilder</c>: the publish
    ///     provider builds a producer (native librdkafka handle); the probe builds + disposes
    ///     a real producer.
    ///   </description></item>
    ///   <item><description>
    ///     <c>mq-expect.kafka</c> → <c>Confluent.Kafka.ConsumerBuilder</c>: the expect
    ///     provider builds a consumer (native librdkafka handle); the probe builds + disposes
    ///     a real consumer.
    ///   </description></item>
    ///   <item><description>
    ///     <c>webhook-listen.http</c> → <c>Webhooks.GetCaptured</c>: the listen provider
    ///     reads captured requests through <c>ScriptGlobalVariables.Webhooks</c>; the probe
    ///     walks that exact read path + record graph across the collectible ALC.
    ///   </description></item>
    ///   <item><description>
    ///     <c>db-assert.sqlserver</c> → <c>Microsoft.Data.SqlClient.SqlConnection</c>: the
    ///     provider asserts against SQL Server via <c>SqlCommand</c>/<c>SqlDataReader</c>;
    ///     the probe builds a real <c>SqlConnection</c> (exercises SNI init + connection-pool
    ///     static state) and disposes it in <c>finally</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <c>db-assert.mongodb</c> → <c>MongoDB.Driver.MongoClient</c>: the provider asserts
    ///     against MongoDB; the probe builds a real <c>MongoClient</c> (exercises the
    ///     connection-pool and SDAM background-thread static state) and disposes it in
    ///     <c>finally</c>.  MongoDB.Driver 3.x: <c>IMongoClient</c> extends <c>IDisposable</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <c>mail-expect.smtp</c> → <c>System.Net.Http.HttpClient</c> (BCL): the provider
    ///     queries the Mailpit HTTP API via <c>HttpClient</c>.  Its closure is subsumed by
    ///     the <c>http.rest</c> probe — both use only the BCL <c>HttpClient</c>/
    ///     <c>SocketsHttpHandler</c> pool, which the probe already exercises.  The shared
    ///     probe marker (<c>new System.Net.Http.HttpClient()</c>) satisfies both rows.
    ///   </description></item>
    ///   <item><description>
    ///     <c>mq-publish.redis</c> / <c>mq-expect.redis</c> →
    ///     <c>StackExchange.Redis.ConnectionMultiplexer</c>: both providers open a
    ///     multiplexer via <c>ConnectionMultiplexer.ConnectAsync</c> exactly like
    ///     <c>cache-assert.redis</c>'s emitted helper.  Their closure is subsumed by the
    ///     existing <c>cache-assert.redis</c> probe marker — no new probe block needed.
    ///   </description></item>
    ///   <item><description>
    ///     <c>metrics-assert.prometheus</c> → <c>System.Net.Http.HttpClient</c> (BCL — closure
    ///     subsumed by the http.rest probe; metrics-assert.prometheus scrapes the SUT's
    ///     Prometheus exposition endpoint via HttpClient exactly like http.rest / mail-expect.smtp
    ///     / cache-assert.elasticsearch).
    ///   </description></item>
    ///   <item><description>
    ///     <c>db-assert.dynamodb</c> → <c>Amazon.DynamoDBv2.AmazonDynamoDBClient</c>: the
    ///     provider asserts against DynamoDB Local via the AWS SDK; the probe builds a real
    ///     client and disposes it in <c>finally</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <c>storage-assert.s3</c> → <c>Amazon.S3.AmazonS3Client</c>: the provider HEADs/GETs
    ///     an S3-compatible (MinIO) object via the AWS SDK; the probe builds a real client and
    ///     disposes it in <c>finally</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <c>trace-expect.otlp</c> → <c>ScriptGlobalVariables.Traces</c> captured-span read
    ///     path: the provider reads spans captured by the host-owned OTLP/HTTP receiver
    ///     through the NEW <c>Traces</c> accessor; the probe walks that exact read path +
    ///     record graph across the collectible ALC (Phase C, mirrors the
    ///     <c>webhook-listen.http</c> row exactly).
    ///   </description></item>
    ///   <item><description>
    ///     <c>http.soap</c> → <c>System.Net.Http.HttpClient</c> (BCL — closure subsumed by the
    ///     http.rest probe; http.soap issues its SOAP request through the same
    ///     <c>HttpClient</c>/<c>SocketsHttpHandler</c> pool).
    ///   </description></item>
    /// </list>
    /// </remarks>
    private static readonly IReadOnlyList<CoreProviderCoverage> EnumeratedCoverage = new[]
    {
        new CoreProviderCoverage(
            StepKind: "http.rest",
            CanonicalClient: "System.Net.Http.HttpClient (REST client / SocketsHttpHandler pool)",
            ProbeMarker: "new System.Net.Http.HttpClient()"),
        new CoreProviderCoverage(
            StepKind: "db-assert.postgres",
            CanonicalClient: "Npgsql (Postgres client / connection pool)",
            ProbeMarker: "Npgsql.NpgsqlConnectionStringBuilder"),
        new CoreProviderCoverage(
            StepKind: "script.csharp",
            CanonicalClient: "Polly-backed RetryRunner across the collectible-ALC Vars boundary",
            ProbeMarker: "Platform.Engine.Abstractions.Retry.RetryRunner.PollAsync"),
        new CoreProviderCoverage(
            StepKind: "mq-publish.kafka",
            CanonicalClient: "Confluent.Kafka producer (native librdkafka handle)",
            ProbeMarker: "Confluent.Kafka.ProducerBuilder<string, string>"),
        new CoreProviderCoverage(
            StepKind: "mq-expect.kafka",
            CanonicalClient: "Confluent.Kafka consumer (native librdkafka handle)",
            ProbeMarker: "Confluent.Kafka.ConsumerBuilder<string, string>"),
        new CoreProviderCoverage(
            StepKind: "webhook-listen.http",
            CanonicalClient: "ScriptGlobalVariables.Webhooks captured-request read path",
            ProbeMarker: "Webhooks.GetCaptured("),
        new CoreProviderCoverage(
            StepKind: "db-assert.sqlserver",
            CanonicalClient: "Microsoft.Data.SqlClient.SqlConnection (SQL Server client / SNI handle)",
            ProbeMarker: "Microsoft.Data.SqlClient.SqlConnection"),
        new CoreProviderCoverage(
            StepKind: "db-assert.mongodb",
            CanonicalClient: "MongoDB.Driver.MongoClient (MongoDB client / connection pool)",
            ProbeMarker: "MongoDB.Driver.MongoClient(\"mongodb://localhost:27017\")"),
        new CoreProviderCoverage(
            StepKind: "mail-expect.smtp",
            CanonicalClient: "System.Net.Http.HttpClient (BCL — closure subsumed by http.rest probe; mail-expect.smtp queries the Mailpit HTTP API via HttpClient)",
            ProbeMarker: "new System.Net.Http.HttpClient()"),
        new CoreProviderCoverage(
            StepKind: "db-assert.mysql",
            CanonicalClient: "MySqlConnector.MySqlConnection (MySQL client / connection pool)",
            ProbeMarker: "MySqlConnector.MySqlConnection"),
        new CoreProviderCoverage(
            StepKind: "cache-assert.redis",
            CanonicalClient: "StackExchange.Redis.ConnectionMultiplexer (Redis client / heartbeat timer + socket + reconnect thread)",
            ProbeMarker: "StackExchange.Redis.ConnectionMultiplexer.Connect"),
        new CoreProviderCoverage(
            StepKind: "mq-publish.rabbitmq",
            CanonicalClient: "RabbitMQ.Client.ConnectionFactory (RabbitMQ connection/channel)",
            ProbeMarker: "RabbitMQ.Client.ConnectionFactory"),
        new CoreProviderCoverage(
            StepKind: "mq-expect.rabbitmq",
            CanonicalClient: "RabbitMQ.Client.ConnectionFactory (RabbitMQ connection/channel)",
            ProbeMarker: "RabbitMQ.Client.ConnectionFactory"),
        new CoreProviderCoverage(
            StepKind: "cache-assert.elasticsearch",
            CanonicalClient: "System.Net.Http.HttpClient (BCL — closure subsumed by http.rest probe; cache-assert.elasticsearch queries the Elasticsearch HTTP API via HttpClient)",
            ProbeMarker: "new System.Net.Http.HttpClient()"),
        new CoreProviderCoverage(
            StepKind: "mq-publish.nats",
            CanonicalClient: "NATS.Client.Core.NatsConnection (NATS JetStream connection/publish via NATS.Net 2.x)",
            ProbeMarker: "NATS.Client.Core.NatsConnection"),
        new CoreProviderCoverage(
            StepKind: "mq-expect.nats",
            CanonicalClient: "NATS.Client.Core.NatsConnection (NATS JetStream connection/consume via NATS.Net 2.x)",
            ProbeMarker: "NATS.Client.Core.NatsConnection"),
        new CoreProviderCoverage(
            StepKind: "mq-publish.azureservicebus",
            CanonicalClient: "Azure.Messaging.ServiceBus.ServiceBusClient (Azure Service Bus AMQP connection + transport lifecycle)",
            ProbeMarker: "Azure.Messaging.ServiceBus.ServiceBusClient"),
        new CoreProviderCoverage(
            StepKind: "mq-expect.azureservicebus",
            CanonicalClient: "Azure.Messaging.ServiceBus.ServiceBusClient (Azure Service Bus AMQP connection + transport lifecycle; non-destructive PeekMessagesAsync path)",
            ProbeMarker: "Azure.Messaging.ServiceBus.ServiceBusClient"),
        new CoreProviderCoverage(
            StepKind: "mq-publish.redis",
            CanonicalClient: "StackExchange.Redis.ConnectionMultiplexer (Redis Streams XADD via ConnectAsync — closure subsumed by the cache-assert.redis probe marker)",
            ProbeMarker: "StackExchange.Redis.ConnectionMultiplexer.Connect"),
        new CoreProviderCoverage(
            StepKind: "mq-expect.redis",
            CanonicalClient: "StackExchange.Redis.ConnectionMultiplexer (Redis Streams XRANGE via ConnectAsync — closure subsumed by the cache-assert.redis probe marker)",
            ProbeMarker: "StackExchange.Redis.ConnectionMultiplexer.Connect"),
        new CoreProviderCoverage(
            StepKind: "metrics-assert.prometheus",
            CanonicalClient: "System.Net.Http.HttpClient (BCL — closure subsumed by the http.rest probe; metrics-assert.prometheus scrapes the SUT's /metrics endpoint via HttpClient)",
            ProbeMarker: "new System.Net.Http.HttpClient()"),
        new CoreProviderCoverage(
            StepKind: "db-assert.dynamodb",
            CanonicalClient: "Amazon.DynamoDBv2.AmazonDynamoDBClient (DynamoDB Local client)",
            ProbeMarker: "Amazon.DynamoDBv2.AmazonDynamoDBClient"),
        new CoreProviderCoverage(
            StepKind: "storage-assert.s3",
            CanonicalClient: "Amazon.S3.AmazonS3Client (S3-compatible / MinIO client)",
            ProbeMarker: "Amazon.S3.AmazonS3Client"),
        new CoreProviderCoverage(
            StepKind: "trace-expect.otlp",
            CanonicalClient: "ScriptGlobalVariables.Traces captured-span read path (host-owned OTLP/HTTP receiver)",
            ProbeMarker: "Traces.GetCaptured("),
        new CoreProviderCoverage(
            StepKind: "http.soap",
            CanonicalClient: "System.Net.Http.HttpClient (BCL — closure subsumed by the http.rest probe; http.soap issues its SOAP request via HttpClient)",
            ProbeMarker: "new System.Net.Http.HttpClient()"),
    };

    /// <summary>
    /// All Core provider assemblies, anchored by one concrete provider type each —
    /// mirrors <see cref="SchemaFreezeTests"/>, <see cref="VsCodeShippedSchemaSyncTests"/>,
    /// and <c>Vouchfx.Cli.ProviderRegistryFactory.CoreProviderAssemblies</c>.  Listing them
    /// by anchor type makes a renamed/removed provider a COMPILE error here, and building the
    /// registry from them makes an ADDED Core provider a runtime failure in
    /// <see cref="EnumeratedCoverage_EqualsRealCoreProviderSet"/>.
    /// </summary>
    private static Assembly[] CoreProviderAssemblies() => new[]
    {
        typeof(HttpRestProvider).Assembly,            // http.rest
        typeof(DbAssertPostgresProvider).Assembly,    // db-assert.postgres
        typeof(ScriptCsharpProvider).Assembly,        // script.csharp
        typeof(MqPublishKafkaProvider).Assembly,      // mq-publish.kafka
        typeof(MqExpectKafkaProvider).Assembly,       // mq-expect.kafka
        typeof(WebhookListenHttpProvider).Assembly,   // webhook-listen.http
        typeof(DbAssertSqlServerProvider).Assembly,   // db-assert.sqlserver
        typeof(DbAssertMongodbProvider).Assembly,     // db-assert.mongodb
        typeof(MailExpectSmtpProvider).Assembly,      // mail-expect.smtp
        typeof(DbAssertMysqlProvider).Assembly,       // db-assert.mysql
        typeof(CacheAssertRedisProvider).Assembly,    // cache-assert.redis
        typeof(MqPublishRabbitmqProvider).Assembly,   // mq-publish.rabbitmq
        typeof(MqExpectRabbitmqProvider).Assembly,    // mq-expect.rabbitmq
        typeof(CacheAssertElasticsearchProvider).Assembly, // cache-assert.elasticsearch
        typeof(MqPublishNatsProvider).Assembly,       // mq-publish.nats
        typeof(MqExpectNatsProvider).Assembly,        // mq-expect.nats
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

    // -------------------------------------------------------------------------
    // Guard #1 — the enumerated coverage table EQUALS the real Core-provider set.
    // Adding/renaming/removing a Core provider without updating the table fails here.
    // -------------------------------------------------------------------------

    /// <summary>
    /// The set of step kinds in <see cref="EnumeratedCoverage"/> must be exactly the set
    /// of <c>&lt;family&gt;.&lt;provider&gt;</c> keys the real frozen registry reports for
    /// all Core provider assemblies.  This cross-check is what stops the table going
    /// stale: a new Core provider (or a rename) changes the registry's key set and this
    /// equality breaks until the table is deliberately updated.
    /// </summary>
    [Fact]
    public void EnumeratedCoverage_EqualsRealCoreProviderSet()
    {
        var registry = StepKindRegistry.BuildAndFreeze(CoreProviderAssemblies());

        var actualKinds = registry.All
            .Select(p => $"{p.Kind.Family}.{p.Kind.Provider}")
            .ToHashSet(StringComparer.Ordinal);

        var enumeratedKinds = EnumeratedCoverage
            .Select(c => c.StepKind)
            .ToHashSet(StringComparer.Ordinal);

        // Twenty-five Core providers for the v1.x engine (6 original + db-assert.sqlserver + db-assert.mongodb + mail-expect.smtp + db-assert.mysql + cache-assert.redis + mq-publish.rabbitmq + mq-expect.rabbitmq + cache-assert.elasticsearch + mq-publish.nats + mq-expect.nats + mq-publish.azureservicebus + mq-expect.azureservicebus + mq-publish.redis + mq-expect.redis + metrics-assert.prometheus + db-assert.dynamodb + storage-assert.s3 + trace-expect.otlp + http.soap).
        Assert.Equal(25, actualKinds.Count);

        var missingFromTable = actualKinds.Except(enumeratedKinds, StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        var staleInTable = enumeratedKinds.Except(actualKinds, StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missingFromTable.Count == 0,
            "A Core provider exists in the registry but is NOT in the closure-coverage "
            + "table. A new Core provider MUST be added to "
            + $"{nameof(ClosureProbeCoverageGuardTests)}.{nameof(EnumeratedCoverage)} AND its "
            + "canonical client exercised in ClosureProbeScript.Source. Missing: "
            + string.Join(", ", missingFromTable));

        Assert.True(
            staleInTable.Count == 0,
            "The closure-coverage table lists a step kind that the real Core-provider "
            + "registry no longer reports (renamed/removed provider). Update "
            + $"{nameof(ClosureProbeCoverageGuardTests)}.{nameof(EnumeratedCoverage)}. Stale: "
            + string.Join(", ", staleInTable));

        // Belt-and-braces: the two sets are equal (catches any case the diffs above miss).
        Assert.Equal(actualKinds, enumeratedKinds);

        // The table must have no duplicate step kinds (twenty-five distinct rows).
        Assert.Equal(25, enumeratedKinds.Count);
    }

    // -------------------------------------------------------------------------
    // Guard #2 — the closure probe ACTUALLY exercises each provider's client.
    // Adding a row to the table without extending ClosureProbeScript fails here.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Every Core provider in the coverage table must have its canonical-client marker
    /// present verbatim in <see cref="ClosureProbeScript.Source"/>.  If a row is added to
    /// the table (to satisfy guard #1 when a provider is added) without extending the probe
    /// CSX to touch that client, this fails — closing the loop so the leak gate genuinely
    /// covers every Core provider's closure.
    /// </summary>
    [Theory]
    [MemberData(nameof(CoverageRows))]
    public void ClosureProbe_Exercises_EachCoreProviderClient(
        string stepKind,
        string canonicalClient,
        string probeMarker)
    {
        Assert.True(
            ClosureProbeScript.Source.Contains(probeMarker, StringComparison.Ordinal),
            $"The memory-leak closure probe (ClosureProbeScript.Source) does NOT exercise "
            + $"the canonical client for Core provider '{stepKind}' ({canonicalClient}). "
            + $"Expected to find the marker '{probeMarker}' in the probe CSX. A new Core "
            + "provider MUST be added to ClosureProbeScript.Source AND to "
            + $"{nameof(ClosureProbeCoverageGuardTests)}.{nameof(EnumeratedCoverage)}.");
    }

    /// <summary>
    /// xUnit member-data feed for <see cref="ClosureProbe_Exercises_EachCoreProviderClient"/>:
    /// one row per Core provider in <see cref="EnumeratedCoverage"/>.
    /// </summary>
    public static IEnumerable<object[]> CoverageRows() =>
        EnumeratedCoverage.Select(c => new object[]
        {
            c.StepKind,
            c.CanonicalClient,
            c.ProbeMarker,
        });
}
