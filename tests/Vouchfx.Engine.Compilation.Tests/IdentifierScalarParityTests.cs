// Authoring-only scalar parity for issue #342. No emitted code is executed.
using System.Text.Json;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.CacheAssert.Elasticsearch;
using Vouchfx.Steps.MqExpect.AzureServiceBus;
using Vouchfx.Steps.MqExpect.Kafka;
using Vouchfx.Steps.MqExpect.Rabbitmq;
using Vouchfx.Steps.MqPublish.AzureServiceBus;
using Vouchfx.Steps.MqPublish.Kafka;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class IdentifierScalarParityTests
{
    private static readonly IStepProvider[] s_providers =
    {
        new CacheAssertElasticsearchProvider(), new MqPublishKafkaProvider(),
        new MqExpectKafkaProvider(), new MqPublishAzureServiceBusProvider(),
        new MqExpectAzureServiceBusProvider(), new MqExpectRabbitmqProvider(),
    };
    private static readonly StepKindRegistry s_registry = StepKindRegistry.BuildAndFreeze(s_providers);
    private static readonly BindingContext s_binding = new();
    private static readonly ProjectContext s_project = new("elasticsearch");
    private static readonly CompileContext s_compile = new();

    private static readonly (string Kind, string Field)[] s_identifiers =
    {
        ("cache-assert.elasticsearch", "index"),
        ("mq-publish.kafka", "topic"), ("mq-expect.kafka", "topic"),
        ("mq-publish.azureservicebus", "queue"), ("mq-publish.azureservicebus", "topic"),
        ("mq-expect.azureservicebus", "queue"), ("mq-expect.azureservicebus", "topic"),
        ("mq-expect.rabbitmq", "queue"),
    };

    public static IEnumerable<object[]> AcceptedIdentifiers()
    {
        foreach (var (kind, field) in s_identifiers)
            foreach (var (yaml, text) in new[]
            {
                ("2024", "2024"), ("02024", "02024"), ("1.25", "1.25"),
                ("1e3", "1e3"), ("-42", "-42"), ("0", "0"),
                ("'orders'", "orders"), ("'true'", "true"),
            })
                yield return new object[] { kind, field, yaml, text };
    }

    [Theory]
    [MemberData(nameof(AcceptedIdentifiers))]
    public void NumericOrQuotedIdentifier_SchemaBindValidateAndEmit_Agree(
        string kind, string field, string scalar, string expectedText)
    {
        var yaml = IdentifierDocument(kind, field, scalar);
        AssertValid(yaml);
        var node = StepNode(yaml);
        var registered = s_registry.All.Single(p => p.Kind.Family + "." + p.Kind.Provider == kind);
        switch (registered.Instance)
        {
            case CacheAssertElasticsearchProvider provider:
                AssertPipeline(provider, provider, provider, node, m => m.Index, expectedText);
                break;
            case MqPublishKafkaProvider provider:
                AssertPipeline(provider, provider, provider, node, m => m.Topic, expectedText);
                break;
            case MqExpectKafkaProvider provider:
                AssertPipeline(provider, provider, provider, node, m => m.Topic, expectedText);
                break;
            case MqPublishAzureServiceBusProvider provider:
                AssertPipeline(provider, provider, provider, node,
                    m => field == "queue" ? m.Queue! : m.Topic!, expectedText);
                break;
            case MqExpectAzureServiceBusProvider provider:
                AssertPipeline(provider, provider, provider, node,
                    m => field == "queue" ? m.Queue! : m.Topic!, expectedText);
                break;
            case MqExpectRabbitmqProvider provider:
                AssertPipeline(provider, provider, provider, node, m => m.Queue, expectedText);
                break;
            default:
                throw new InvalidOperationException("Uncovered identifier provider.");
        }
    }

    [Fact]
    public void NumericStepQueue_AgreesWithExistingNumericDependencyQueue()
    {
        const string yaml = """
            environment:
              dependencies:
                broker:
                  type: azureservicebus
                  queues: [2024]
            steps:
              - id: scalar
                type: mq-publish.azureservicebus
                target: broker
                queue: 2024
                payload: message
            """;
        AssertValid(yaml);
        var provider = new MqPublishAzureServiceBusProvider();
        AssertPipeline(provider, provider, provider, StepNode(yaml), m => m.Queue!, "2024");
    }

    public static IEnumerable<object[]> RejectedIdentifiers()
    {
        foreach (var (kind, field) in s_identifiers)
            foreach (var scalar in new[] { "null", "~", "true", "false", "[]", "{}", "''" })
                yield return new object[] { kind, field, scalar };
    }

    [Theory]
    [MemberData(nameof(RejectedIdentifiers))]
    public void NullBooleanCollectionOrEmptyIdentifier_IsRejectedBySchema(
        string kind, string field, string scalar) =>
        AssertRejected(IdentifierDocument(kind, field, scalar), "/steps/0/" + field);

    [Theory]
    [InlineData("'text'", "text")]
    [InlineData("2024", "2024")]
    [InlineData("02024", "02024")]
    [InlineData("1.25", "1.25")]
    [InlineData("1e3", "1e3")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("True", "True")]
    [InlineData("''", "")]
    [InlineData("'{captured}'", "{captured}")]
    public void ElasticsearchFieldValue_ScalarText_IsPreservedThroughEmission(string scalar, string expectedText)
    {
        var yaml = FieldDocument(scalar);
        AssertValid(yaml);
        var provider = new CacheAssertElasticsearchProvider();
        var model = provider.Bind(StepNode(yaml), s_binding);
        Assert.Equal(expectedText, Assert.Single(model.Expect.Fields!).Value);
        Assert.True(provider.Validate(model, s_project).IsValid);
        Assert.Contains(JsonSerializer.Serialize(expectedText), provider.Emit(model, s_compile).StatementBlock);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("~")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void ElasticsearchFieldValue_NullOrCollection_IsRejectedBySchema(string scalar) =>
        AssertRejected(FieldDocument(scalar), "/steps/0/expect/fields/0/value");

    [Theory]
    [InlineData("../audit")]
    [InlineData("orders/../audit")]
    [InlineData("orders\\..\\audit")]
    [InlineData("/audit")]
    [InlineData("orders\\archive")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("%2e%2e/audit")]
    [InlineData("orders index")]
    [InlineData("orders?filter")]
    [InlineData("orders#alias")]
    [InlineData("orders\u0001")]
    [InlineData("orders\u0085")]
    [InlineData("orders\u00a0")]
    [InlineData("orders\u1680")]
    [InlineData("orders\u2007")]
    [InlineData("orders\u2028")]
    [InlineData("orders\u2029")]
    [InlineData("orders\u202f")]
    [InlineData("orders\u205f")]
    [InlineData("orders\u3000")]
    [InlineData("orders\n")]
    [InlineData("orders\r\n")]
    public void ElasticsearchMalformedIndex_SchemaAndTypedValidator_BothReject(string index)
    {
        var yaml = IdentifierDocument("cache-assert.elasticsearch", "index", JsonSerializer.Serialize(index));
        AssertRejected(yaml, "/steps/0/index");
        var provider = new CacheAssertElasticsearchProvider();
        Assert.False(provider.Validate(provider.Bind(StepNode(yaml), s_binding), s_project).IsValid);
    }

    [Theory]
    [InlineData("orders")]
    [InlineData("orders,products")]
    [InlineData("logs-*")]
    [InlineData("orders%2Farchive")]
    [InlineData("orders\ufeff")]
    public void ElasticsearchUnchangedIndexForms_SchemaAndTypedValidator_BothAccept(string index)
    {
        // Percent escapes retain their existing grammar; this does not test URI routing.
        var yaml = IdentifierDocument("cache-assert.elasticsearch", "index", JsonSerializer.Serialize(index));
        AssertValid(yaml);
        var provider = new CacheAssertElasticsearchProvider();
        Assert.True(provider.Validate(provider.Bind(StepNode(yaml), s_binding), s_project).IsValid);
    }

    private static void AssertPipeline<T>(IStepBinder<T> binder, IStepValidator<T> validator,
        IStepCompiler<T> compiler, YamlNode node, Func<T, string> identifier, string expectedText)
        where T : IStepModel
    {
        var model = binder.Bind(node, s_binding);
        Assert.Equal(expectedText, identifier(model));
        var validation = validator.Validate(model, new ProjectContext(((IStepProvider)binder).Kind.Provider));
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        Assert.Contains(JsonSerializer.Serialize(expectedText), compiler.Emit(model, s_compile).StatementBlock);
    }

    private static string IdentifierDocument(string kind, string field, string scalar)
    {
        var extra = kind switch
        {
            "cache-assert.elasticsearch" => "    expect: {count: 1}\n",
            "mq-publish.kafka" or "mq-publish.azureservicebus" => "    payload: message\n",
            "mq-expect.kafka" or "mq-expect.rabbitmq" => "    match: {payloadContains: message}\n",
            "mq-expect.azureservicebus" => "    expectPayloadContains: message\n"
                + (field == "topic" ? "    subscription: subscription\n" : string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return $"steps:\n  - id: scalar\n    type: {kind}\n    target: broker\n    {field}: {scalar}\n" + extra;
    }

    private static string FieldDocument(string scalar) =>
        "steps:\n  - id: scalar\n    type: cache-assert.elasticsearch\n    target: broker\n"
        + "    index: orders\n    expect:\n      fields:\n        - field: value\n"
        + $"          value: {scalar}\n";

    private static YamlMappingNode StepNode(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)((YamlSequenceNode)((YamlMappingNode)stream.Documents[0].RootNode)
            .Children[new YamlScalarNode("steps")]).Children[0];
    }

    private static void AssertValid(string yaml)
    {
        var result = DocumentValidator.Validate(yaml, s_registry);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(e => e.Message)));
    }

    private static void AssertRejected(string yaml, string location)
    {
        var result = DocumentValidator.Validate(yaml, s_registry);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.InstanceLocation == location);
    }

    private sealed class BindingContext : IBindingContext { }
    private sealed class CompileContext : ICompileContext
    {
        public string StepId => "scalar";
        public string SuiteNamespace => "ScalarParity";
        public string SuiteDirectory => AppContext.BaseDirectory;
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } = new Dictionary<string, CaptureExpr>();
        public IReadOnlyDictionary<string, string> Captures { get; } = new Dictionary<string, string>();
    }
    private sealed class ProjectContext : IProjectContext
    {
        public ProjectContext(string dependencyType) => DeclaredDependencies =
            new Dictionary<string, string> { ["broker"] = dependencyType };
        public string SuiteDirectory => AppContext.BaseDirectory;
        public IReadOnlyDictionary<string, string> DeclaredDependencies { get; }
        public IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices { get; } =
            new Dictionary<string, DeclaredServiceInfo>();
    }
}
