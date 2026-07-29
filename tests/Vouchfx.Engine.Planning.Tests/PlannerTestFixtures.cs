// Vouchfx.Engine.Planning.Tests — PlannerTestFixtures (M3 Planner).
//
// Read-only shared helpers for every Planner test file. T2/T3/T4 CALL these; none of them
// edits this file — that is what keeps their fixture/test additions disjoint.

using Vouchfx.Engine.Planning.Report;
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

namespace Vouchfx.Engine.Planning.Tests;

/// <summary>Read-only shared test helpers for the Planner test suite.</summary>
internal static class PlannerTestFixtures
{
    /// <summary>
    /// Lazily builds the full 25-provider Core registry exactly once per test run (fix-round-2
    /// NEW-5): registry discovery scans 25 assemblies via reflection, and
    /// <see cref="PlanExport.BuildPlan"/> additionally runs <c>EngineExport.BuildCatalogue</c>
    /// (parsing every provider's schema fragment) on every call — with T2/T3/T4 about to add
    /// roughly 60 more tests calling <see cref="Plan"/>, rebuilding the registry per call would
    /// multiply that cost needlessly. <see cref="Lazy{T}"/>'s default thread-safety mode
    /// (<c>ExecutionAndPublication</c>) makes this safe under xUnit's parallel test execution;
    /// <see cref="StepKindRegistry"/> is immutable once frozen, so sharing the single instance
    /// across every test is safe.
    /// </summary>
    private static readonly Lazy<StepKindRegistry> CoreRegistry = new(BuildCoreRegistryCore);

    /// <summary>
    /// Returns the cached full 25-provider Core registry (see <see cref="CoreRegistry"/>).
    /// </summary>
    public static StepKindRegistry BuildCoreRegistry() => CoreRegistry.Value;

    /// <summary>
    /// Builds the full 25-provider Core registry — anchor-by-anchor, mirroring
    /// <c>Vouchfx.Cli.ProviderRegistryFactory.CoreProviderAssemblies</c> exactly so a
    /// dropped provider is a compile error here too, not a silent registry gap.
    /// </summary>
    private static StepKindRegistry BuildCoreRegistryCore() => StepKindRegistry.BuildAndFreeze(new[]
    {
        typeof(HttpRestProvider).Assembly,
        typeof(HttpSoapProvider).Assembly,
        typeof(DbAssertPostgresProvider).Assembly,
        typeof(DbAssertSqlServerProvider).Assembly,
        typeof(DbAssertMongodbProvider).Assembly,
        typeof(DbAssertMysqlProvider).Assembly,
        typeof(ScriptCsharpProvider).Assembly,
        typeof(MqPublishKafkaProvider).Assembly,
        typeof(MqExpectKafkaProvider).Assembly,
        typeof(MqPublishRabbitmqProvider).Assembly,
        typeof(MqExpectRabbitmqProvider).Assembly,
        typeof(MqPublishNatsProvider).Assembly,
        typeof(MqExpectNatsProvider).Assembly,
        typeof(MqPublishAzureServiceBusProvider).Assembly,
        typeof(MqExpectAzureServiceBusProvider).Assembly,
        typeof(MqPublishRedisProvider).Assembly,
        typeof(MqExpectRedisProvider).Assembly,
        typeof(WebhookListenHttpProvider).Assembly,
        typeof(MailExpectSmtpProvider).Assembly,
        typeof(CacheAssertRedisProvider).Assembly,
        typeof(CacheAssertElasticsearchProvider).Assembly,
        typeof(MetricsAssertPrometheusProvider).Assembly,
        typeof(DbAssertDynamodbProvider).Assembly,
        typeof(StorageAssertS3Provider).Assembly,
        typeof(TraceExpectOtlpProvider).Assembly,
    });

    /// <summary>
    /// Resolves a path under this test project's <c>Fixtures/</c> directory (copied next to
    /// the test DLL via the recursive <c>Content</c> glob).
    /// </summary>
    /// <param name="relativePath">A path relative to <c>Fixtures/</c>, e.g. <c>"ingest/two-suites"</c>.</param>
    public static string FixtureRoot(string relativePath) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath);

    /// <summary>
    /// Runs a Planner analysis over <paramref name="suitePath"/> (and optional
    /// <paramref name="eventsPath"/>) against the full Core registry.
    /// </summary>
    /// <param name="suitePath">A suite directory or single <c>.e2e.yaml</c> file.</param>
    /// <param name="eventsPath">An optional event-history file or directory.</param>
    /// <param name="thresholds">Optional REQ-006 threshold overrides.</param>
    /// <param name="engineVersion">Optional engine version stamp.</param>
    public static PlanReportDocument Plan(
        string suitePath,
        string? eventsPath = null,
        PlanThresholds? thresholds = null,
        string? engineVersion = null)
    {
        var registry = BuildCoreRegistry();
        var request = new PlanRequest(suitePath, eventsPath, thresholds);
        return PlanExport.BuildPlan(request, registry, engineVersion);
    }
}
