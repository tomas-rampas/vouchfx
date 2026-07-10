// Vouchfx.Cli — ProviderRegistryFactory (S07-C-01).
//
// The ONE place in the CLI that names the Core provider assemblies. The runner is
// deliberately provider-agnostic (it takes the assemblies to scan as a parameter), so
// this factory is the single seam where the CLI declares which providers it ships with.
// Adding a provider to the CLI is a one-line change here (plus the matching
// ProjectReference in Vouchfx.Cli.csproj).

using System.Reflection;
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

namespace Vouchfx.Cli;

/// <summary>
/// Builds the set of Core provider assemblies the CLI ships with, and freezes them
/// into a <see cref="StepKindRegistry"/>.
/// </summary>
/// <remarks>
/// This is the single point of truth for which providers the <c>vouchfx</c> executable
/// bundles.  It is referenced anchor-by-anchor (one concrete provider type per assembly)
/// rather than by string name so a renamed/removed provider is a compile error here, not
/// a silent runtime gap.
/// </remarks>
internal static class ProviderRegistryFactory
{
    /// <summary>
    /// Returns the assemblies the CLI scans for <c>[StepProvider]</c>-decorated providers.
    /// </summary>
    /// <remarks>
    /// One anchor type per Core provider assembly.  The Core providers wired into the
    /// CLI are <c>http.rest</c>, <c>http.soap</c>, <c>db-assert.postgres</c>,
    /// <c>db-assert.sqlserver</c>, <c>db-assert.mongodb</c>, <c>db-assert.mysql</c>,
    /// <c>script.csharp</c>, <c>mq-publish.kafka</c>, <c>mq-expect.kafka</c>,
    /// <c>mq-publish.rabbitmq</c>, <c>mq-expect.rabbitmq</c>, <c>mq-publish.nats</c>,
    /// <c>mq-expect.nats</c>, <c>mq-publish.azureservicebus</c>,
    /// <c>mq-expect.azureservicebus</c>, <c>mq-publish.redis</c>, <c>mq-expect.redis</c>,
    /// <c>webhook-listen.http</c>, <c>mail-expect.smtp</c>,
    /// <c>cache-assert.redis</c>, <c>cache-assert.elasticsearch</c>,
    /// <c>metrics-assert.prometheus</c>, <c>db-assert.dynamodb</c>,
    /// <c>storage-assert.s3</c> and <c>trace-expect.otlp</c>.
    /// </remarks>
    public static Assembly[] CoreProviderAssemblies() => new[]
    {
        typeof(HttpRestProvider).Assembly,            // http.rest
        typeof(HttpSoapProvider).Assembly,            // http.soap
        typeof(DbAssertPostgresProvider).Assembly,    // db-assert.postgres
        typeof(DbAssertSqlServerProvider).Assembly,   // db-assert.sqlserver
        typeof(DbAssertMongodbProvider).Assembly,     // db-assert.mongodb
        typeof(DbAssertMysqlProvider).Assembly,       // db-assert.mysql
        typeof(ScriptCsharpProvider).Assembly,        // script.csharp
        typeof(MqPublishKafkaProvider).Assembly,      // mq-publish.kafka
        typeof(MqExpectKafkaProvider).Assembly,       // mq-expect.kafka
        typeof(MqPublishRabbitmqProvider).Assembly,   // mq-publish.rabbitmq
        typeof(MqExpectRabbitmqProvider).Assembly,    // mq-expect.rabbitmq
        typeof(MqPublishNatsProvider).Assembly,       // mq-publish.nats
        typeof(MqExpectNatsProvider).Assembly,        // mq-expect.nats
        typeof(MqPublishAzureServiceBusProvider).Assembly, // mq-publish.azureservicebus
        typeof(MqExpectAzureServiceBusProvider).Assembly,  // mq-expect.azureservicebus
        typeof(MqPublishRedisProvider).Assembly,      // mq-publish.redis
        typeof(MqExpectRedisProvider).Assembly,       // mq-expect.redis
        typeof(WebhookListenHttpProvider).Assembly,   // webhook-listen.http
        typeof(MailExpectSmtpProvider).Assembly,      // mail-expect.smtp
        typeof(CacheAssertRedisProvider).Assembly,    // cache-assert.redis
        typeof(CacheAssertElasticsearchProvider).Assembly, // cache-assert.elasticsearch
        typeof(MetricsAssertPrometheusProvider).Assembly,  // metrics-assert.prometheus
        typeof(DbAssertDynamodbProvider).Assembly,    // db-assert.dynamodb
        typeof(StorageAssertS3Provider).Assembly,     // storage-assert.s3
        typeof(TraceExpectOtlpProvider).Assembly,     // trace-expect.otlp
    };

    /// <summary>
    /// Builds and freezes the <see cref="StepKindRegistry"/> from the CLI's Core provider
    /// assemblies.
    /// </summary>
    /// <returns>A frozen registry containing every Core provider this CLI ships with.</returns>
    public static StepKindRegistry BuildCoreRegistry() =>
        StepKindRegistry.BuildAndFreeze(CoreProviderAssemblies());
}
