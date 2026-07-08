// Vouchfx.Cli — ProviderRegistryFactory (S07-C-01).
//
// The ONE place in the CLI that names the Core provider assemblies. The runner is
// deliberately provider-agnostic (it takes the assemblies to scan as a parameter), so
// this factory is the single seam where the CLI declares which providers it ships with.
// Adding a provider to the CLI is a one-line change here (plus the matching
// ProjectReference in Vouchfx.Cli.csproj).

using System.Reflection;
using Platform.Sdk;
using Platform.Steps.CacheAssert.Elasticsearch;
using Platform.Steps.CacheAssert.Redis;
using Platform.Steps.DbAssert.Dynamodb;
using Platform.Steps.DbAssert.Mongodb;
using Platform.Steps.DbAssert.Mysql;
using Platform.Steps.DbAssert.Postgres;
using Platform.Steps.DbAssert.SqlServer;
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
using Platform.Steps.WebhookListen.Http;

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
    /// CLI are <c>http.rest</c>, <c>db-assert.postgres</c>, <c>db-assert.sqlserver</c>,
    /// <c>db-assert.mongodb</c>, <c>db-assert.mysql</c>, <c>script.csharp</c>,
    /// <c>mq-publish.kafka</c>, <c>mq-expect.kafka</c>, <c>mq-publish.rabbitmq</c>,
    /// <c>mq-expect.rabbitmq</c>, <c>mq-publish.nats</c>, <c>mq-expect.nats</c>,
    /// <c>mq-publish.azureservicebus</c>, <c>mq-expect.azureservicebus</c>,
    /// <c>mq-publish.redis</c>, <c>mq-expect.redis</c>,
    /// <c>webhook-listen.http</c>, <c>mail-expect.smtp</c>,
    /// <c>cache-assert.redis</c>, <c>cache-assert.elasticsearch</c>,
    /// <c>metrics-assert.prometheus</c>, <c>db-assert.dynamodb</c> and
    /// <c>storage-assert.s3</c>.
    /// </remarks>
    public static Assembly[] CoreProviderAssemblies() => new[]
    {
        typeof(HttpRestProvider).Assembly,            // http.rest
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
    };

    /// <summary>
    /// Builds and freezes the <see cref="StepKindRegistry"/> from the CLI's Core provider
    /// assemblies.
    /// </summary>
    /// <returns>A frozen registry containing every Core provider this CLI ships with.</returns>
    public static StepKindRegistry BuildCoreRegistry() =>
        StepKindRegistry.BuildAndFreeze(CoreProviderAssemblies());
}
