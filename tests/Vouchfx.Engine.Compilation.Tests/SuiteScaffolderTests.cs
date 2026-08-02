// Spec B — SuiteScaffolder public library (mcp-generator-scaffold-and-run).
// Docker-free unit tests covering REQ-001..006 and EDGE-001/002/004/005 for the library.

using System.Reflection;
using Vouchfx.Engine.Compilation.Scaffold;
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

public sealed class SuiteScaffolderTests
{
    private static StepKindRegistry BuildHttpAndPostgresRegistry() =>
        StepKindRegistry.BuildAndFreeze(new IStepProvider[]
        {
            new HttpRestProvider(),
            new DbAssertPostgresProvider(),
        });

    // ── B1 crown deliverable (gatekeeper): all-25-registered-types scaffold round-trip ──
    //
    // The reason B1 (prose synthesised into RequiredFields breaking SuiteScaffolder's
    // YAML emission) could never recur: for EVERY type in the full Core registry,
    // scaffold a minimal document from nothing but the catalogue-driven intent, then
    // run it through DocumentValidator against the SAME composed schema — the actual
    // production path an author's suite hits (mirrors SchemaStepSurfaceClosureTests'
    // own registry-parity discipline). A provider whose catalogue entry under- or
    // over-specifies what SuiteScaffolder needs to emit a valid skeleton fails here
    // immediately, by type, rather than surfacing later as a broken MCP scaffold call.
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

    private static StepKindRegistry FullCoreRegistry() =>
        StepKindRegistry.BuildAndFreeze(CoreProviderAssemblies());

    public static IEnumerable<object[]> AllCoreProviderTypes() =>
        FullCoreRegistry().All.Select(p => new object[] { $"{p.Kind.Family}.{p.Kind.Provider}" });

    /// <summary>
    /// Guards the exhaustiveness of the theory itself (mirrors
    /// <c>SchemaStepSurfaceClosureTests.MinimalValidStepDocuments_CoversExactlyTheRegisteredCoreProviders</c>):
    /// without this, a provider silently absent from discovery would just never
    /// run its case, rather than failing loudly.
    /// </summary>
    [Fact]
    public void AllCoreProviderTypes_DiscoversExactlyTwentyFive()
    {
        Assert.Equal(25, AllCoreProviderTypes().Count());
    }

    [Theory]
    [MemberData(nameof(AllCoreProviderTypes))]
    public void Generate_ForEveryRegisteredCoreType_ProducesSchemaValidDocument(string typeKey)
    {
        var registry = FullCoreRegistry();
        var stepId = "s_" + typeKey.Replace('.', '_').Replace('-', '_');

        var yaml = SuiteScaffolder.Generate(
            registry,
            new ScaffoldIntent(Steps: new[] { new ScaffoldStepIntent(stepId, typeKey) }),
            engineVersion: "round-trip-test");

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.True(
            result.IsValid,
            $"{typeKey}: scaffolded document failed schema validation. Errors: "
            + string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))
            + $"{Environment.NewLine}--- YAML ---{Environment.NewLine}{yaml}");
    }

    private static ScaffoldIntent MultiTypeIntent() => new(
        Steps: new[]
        {
            new ScaffoldStepIntent("get-api", "http.rest", Label: "GET api"),
            new ScaffoldStepIntent("check-db", "db-assert.postgres"),
        },
        Services: new[] { new ScaffoldServiceIntent("api", "traefik/whoami") },
        Dependencies: new[] { new ScaffoldDependencyIntent("db", "postgres") });

    [Fact]
    public void Generate_MultiType_EmitsTypesIdsEnvAndOrder()
    {
        // REQ-001 / REQ-004
        var yaml = SuiteScaffolder.Generate(
            BuildHttpAndPostgresRegistry(),
            MultiTypeIntent(),
            engineVersion: "test-1.0");

        Assert.Contains("type: http.rest", yaml, StringComparison.Ordinal);
        Assert.Contains("type: db-assert.postgres", yaml, StringComparison.Ordinal);
        Assert.Contains("id: get-api", yaml, StringComparison.Ordinal);
        Assert.Contains("id: check-db", yaml, StringComparison.Ordinal);
        Assert.Contains("api:", yaml, StringComparison.Ordinal);
        Assert.Contains("db:", yaml, StringComparison.Ordinal);
        Assert.Contains("type: postgres", yaml, StringComparison.Ordinal);
        Assert.Contains("# label: GET api", yaml, StringComparison.Ordinal);

        var getIdx = yaml.IndexOf("id: get-api", StringComparison.Ordinal);
        var checkIdx = yaml.IndexOf("id: check-db", StringComparison.Ordinal);
        Assert.True(getIdx >= 0 && checkIdx > getIdx, "Step order must match intent order.");
    }

    [Fact]
    public void Generate_UnknownType_ThrowsNamingType()
    {
        // REQ-002
        var ex = Assert.Throws<ScaffoldException>(() =>
            SuiteScaffolder.Generate(
                BuildHttpAndPostgresRegistry(),
                new ScaffoldIntent(
                    Steps: new[] { new ScaffoldStepIntent("x", "nope.fake") })));

        Assert.Contains("nope.fake", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_MultiType_PassesDocumentValidator()
    {
        // REQ-003
        var registry = BuildHttpAndPostgresRegistry();
        var yaml = SuiteScaffolder.Generate(registry, MultiTypeIntent(), engineVersion: "test");

        var result = DocumentValidator.Validate(yaml, registry);
        Assert.True(
            result.IsValid,
            "Scaffold output must be schema-valid. Errors: "
            + string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void Generate_ProvenanceComments_MarkMachineDraftedAndReview()
    {
        // REQ-005
        var yaml = SuiteScaffolder.Generate(
            BuildHttpAndPostgresRegistry(),
            MultiTypeIntent(),
            engineVersion: "1.2.3");

        var lines = yaml.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .Take(5)
            .ToList();

        Assert.All(lines.Take(3), line => Assert.StartsWith("#", line, StringComparison.Ordinal));
        var header = string.Join('\n', lines);
        Assert.Contains("Machine-drafted", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vouchfx scaffold", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.2.3", header, StringComparison.Ordinal);
        Assert.Contains("review", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_NeverEmitsPlantedSecretLiteral_UsesSecretReferenceForSecretLikeFields()
    {
        // REQ-006
        const string planted = "super-secret-token-VALUE-9f3a";
        Environment.SetEnvironmentVariable("VOUCHFX_SCAFFOLD_TEST_TOKEN", planted);
        try
        {
            var registry = StepKindRegistry.BuildAndFreeze(new IStepProvider[]
            {
                new HttpRestProvider(),
                new SampleSecretFieldProvider(),
            });

            var yaml = SuiteScaffolder.Generate(
                registry,
                new ScaffoldIntent(
                    Steps: new[] { new ScaffoldStepIntent("s", "sample.secretstep") }));

            Assert.DoesNotContain(planted, yaml, StringComparison.Ordinal);
            Assert.Contains("${secret:env/SCAFFOLD_PLACEHOLDER}", yaml, StringComparison.Ordinal);
            Assert.Contains("apiToken:", yaml, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VOUCHFX_SCAFFOLD_TEST_TOKEN", null);
        }
    }

    [Fact]
    public void Generate_DuplicateIds_ThrowsNamingId()
    {
        // EDGE-001
        var ex = Assert.Throws<ScaffoldException>(() =>
            SuiteScaffolder.Generate(
                BuildHttpAndPostgresRegistry(),
                new ScaffoldIntent(
                    Steps: new[]
                    {
                        new ScaffoldStepIntent("same", "http.rest"),
                        new ScaffoldStepIntent("same", "db-assert.postgres"),
                    })));

        Assert.Contains("same", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("bad id")]
    [InlineData("1starts-digit")]
    [InlineData("has.dot")]
    [InlineData("has/slash")]
    public void Generate_InvalidStepIdFormat_Throws(string badId)
    {
        // Schema pattern ^[A-Za-z_][A-Za-z0-9_-]*$ — fail closed before emit.
        var ex = Assert.Throws<ScaffoldException>(() =>
            SuiteScaffolder.Generate(
                BuildHttpAndPostgresRegistry(),
                new ScaffoldIntent(
                    Steps: new[] { new ScaffoldStepIntent(badId, "http.rest") })));

        Assert.Contains(badId, ex.Message, StringComparison.Ordinal);
        Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_LabelWithNewlines_StaysSingleLineComment()
    {
        // Labels must not inject multi-line content into the YAML comment header.
        var yaml = SuiteScaffolder.Generate(
            BuildHttpAndPostgresRegistry(),
            new ScaffoldIntent(
                Steps: new[]
                {
                    new ScaffoldStepIntent(
                        "get-api",
                        "http.rest",
                        Label: "safe\nid: injected\ntype: evil"),
                }));

        Assert.DoesNotContain("\n  id: injected", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("\ntype: evil", yaml, StringComparison.Ordinal);
        Assert.Contains("# label: safe id: injected type: evil", yaml, StringComparison.Ordinal);
        Assert.Contains("id: get-api", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_EmptySteps_Throws()
    {
        // EDGE-002
        var ex = Assert.Throws<ScaffoldException>(() =>
            SuiteScaffolder.Generate(
                BuildHttpAndPostgresRegistry(),
                new ScaffoldIntent(Steps: Array.Empty<ScaffoldStepIntent>())));

        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generate_UnknownDependencyKind_ThrowsNamingKind()
    {
        // EDGE-004
        var ex = Assert.Throws<ScaffoldException>(() =>
            SuiteScaffolder.Generate(
                BuildHttpAndPostgresRegistry(),
                new ScaffoldIntent(
                    Steps: new[] { new ScaffoldStepIntent("a", "http.rest") },
                    Dependencies: new[] { new ScaffoldDependencyIntent("x", "not-a-real-dep") })));

        Assert.Contains("not-a-real-dep", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_TwoRunsIdentical_NoTimestamps()
    {
        // EDGE-005
        var registry = BuildHttpAndPostgresRegistry();
        var intent = MultiTypeIntent();

        var a = SuiteScaffolder.Generate(registry, intent, engineVersion: "stable");
        var b = SuiteScaffolder.Generate(registry, intent, engineVersion: "stable");

        Assert.Equal(a, b);
        Assert.DoesNotContain("2026-", a, StringComparison.Ordinal);
        Assert.DoesNotContain("T00:", a, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownDependencyKinds_ContainsCoreMapperKinds()
    {
        Assert.True(KnownDependencyKinds.Contains("postgres"));
        Assert.True(KnownDependencyKinds.Contains("minio"));
        Assert.True(KnownDependencyKinds.Contains("mailpit"));
        Assert.False(KnownDependencyKinds.Contains("not-a-real-dep"));
    }

    /// <summary>
    /// Lookup is case-sensitive (pre-GA decision, feat/case-sensitive-kinds): only the exact
    /// canonical (lower-case) spelling is recognised — mirrors EnvironmentMapper's own
    /// s_dependencyRegistry so the scaffolder and the engine agree on exactly one spelling.
    /// </summary>
    [Theory]
    [InlineData("Postgres")]
    [InlineData("POSTGRES")]
    [InlineData("Minio")]
    [InlineData("MailPit")]
    public void KnownDependencyKinds_Contains_IsCaseSensitive(string wrongCaseKind)
    {
        Assert.False(KnownDependencyKinds.Contains(wrongCaseKind));
    }

    /// <summary>
    /// A dependency type spelled with the wrong case is rejected exactly like an unrecognised
    /// type, with a message naming the correct canonical spelling — the scaffolder must teach
    /// the fix, not merely say "unsupported".
    /// </summary>
    [Fact]
    public void Generate_DependencyTypeWrongCase_ThrowsNamingCorrectSpelling()
    {
        var ex = Assert.Throws<ScaffoldException>(() =>
            SuiteScaffolder.Generate(
                BuildHttpAndPostgresRegistry(),
                new ScaffoldIntent(
                    Steps: new[] { new ScaffoldStepIntent("a", "http.rest") },
                    Dependencies: new[] { new ScaffoldDependencyIntent("db", "Postgres") })));

        Assert.Contains("Postgres", ex.Message, StringComparison.Ordinal);
        Assert.Contains("postgres", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Minimal provider whose required field name is secret-shaped (for REQ-006).
    /// </summary>
    private sealed class SampleSecretFieldProvider : IStepProvider, IStepBinder<SampleSecretModel>
    {
        public StepKindId Kind { get; } = new("sample", "secretstep");

        public ProviderMetadata Metadata { get; } = new(
            Version: "0.0.0-test",
            MinEngineVersion: "1.0.0",
            License: "Apache-2.0",
            Authors: new[] { "test-only" });

        public JsonSchemaFragment SchemaFragment { get; } = new(
            """
            {
              "type": "object",
              "required": ["apiToken"],
              "properties": {
                "apiToken": { "type": "string" }
              }
            }
            """);

        public SampleSecretModel Bind(
            YamlDotNet.RepresentationModel.YamlNode node,
            IBindingContext ctx) => new();
    }

    private sealed record SampleSecretModel : IStepModel;
}
