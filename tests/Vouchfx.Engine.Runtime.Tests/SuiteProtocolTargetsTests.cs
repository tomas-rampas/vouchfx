// REQ-005 / REQ-011 — SuiteProtocolTargets, and SuiteTopology.StartAsync's no-accessor guard
// (authenticated-infrastructure-mtls, slice E).
//
// Non-Docker throughout. SuiteProtocolTargets is a pure function over an AST, and the guard it
// feeds fires at StartAsync's Step 0 — before EnvironmentMapper.Map and long before DCP — so both
// are provable without a container.
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqPublish.Kafka;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// DECISION-1's inference: the confirmation level follows the protocol the suite's own STEPS will
/// speak, not the kind the target happens to be declared as.
/// </summary>
public sealed class SuiteProtocolTargetsTests
{
    private static readonly System.Reflection.Assembly[] ProviderAssemblies =
        new[]
        {
            typeof(MqPublishKafkaProvider).Assembly,
            typeof(Vouchfx.Steps.MqExpect.Kafka.MqExpectKafkaProvider).Assembly,
            typeof(Vouchfx.Steps.HttpRest.HttpRestProvider).Assembly,
        };

    private static readonly StepKindRegistry Registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);

    // Expected-value arrays hoisted to fields (CA1861).
    private static readonly string[] OnlyKafkaBroker = new[] { "kafka-broker" };
    private static readonly string[] BothBrokers = new[] { "broker-a", "broker-b" };

    private static ScenarioAst Ast(string yaml) =>
        AstBuilder.Build(YamlDocumentParser.Parse(yaml), Registry);

    /// <summary>
    /// The shape REQ-011 exists for and the whole reason this inference was built: the customer's
    /// broker is a declared SERVICE, and a <c>mq-publish.kafka</c> step naming it is what says so.
    /// </summary>
    [Fact]
    public void KafkaSpeaking_ServiceTargetedByAPublishStep_IsIncluded()
    {
        var targets = SuiteProtocolTargets.KafkaSpeaking(Ast("""
            environment:
              services:
                kafka-broker:
                  image: acme/broker:1
                  ports: [9093]
            steps:
              - id: publish
                type: mq-publish.kafka
                target: kafka-broker
                topic: orders
                payload: "{}"
            """));

        Assert.Equal(OnlyKafkaBroker, targets.OrderBy(t => t, StringComparer.Ordinal));
    }

    /// <summary>
    /// <c>mq-expect.kafka</c> counts too — a consumer authenticates exactly as a producer does.
    /// </summary>
    [Fact]
    public void KafkaSpeaking_ServiceTargetedByAnExpectStep_IsIncluded()
    {
        var targets = SuiteProtocolTargets.KafkaSpeaking(Ast("""
            environment:
              services:
                kafka-broker:
                  image: acme/broker:1
                  ports: [9093]
            steps:
              - id: consume
                type: mq-expect.kafka
                target: kafka-broker
                topic: orders
                expect:
                  count: 1
            """));

        Assert.Contains("kafka-broker", targets);
    }

    /// <summary>
    /// Nothing is guessed: a target no Kafka step names contributes nothing, which is what keeps
    /// the probe from writing Kafka framing into a connection that might be HTTP.
    /// </summary>
    [Fact]
    public void KafkaSpeaking_TargetOfANonKafkaStep_IsNotIncluded()
    {
        var targets = SuiteProtocolTargets.KafkaSpeaking(Ast("""
            environment:
              services:
                api:
                  image: acme/api:1
            steps:
              - id: get
                type: http.rest
                target: api
                method: GET
                path: /
                expect:
                  status: 200
            """));

        Assert.Empty(targets);
    }

    /// <summary>
    /// A suite with no scenarios, or a scenario with no steps, yields the empty set rather than
    /// throwing — the probe's common path.
    /// </summary>
    [Fact]
    public void KafkaSpeaking_NullInputs_YieldTheEmptySet()
    {
        Assert.Empty(SuiteProtocolTargets.KafkaSpeaking((ScenarioAst?)null));
        Assert.Empty(SuiteProtocolTargets.KafkaSpeaking((IEnumerable<ScenarioAst?>?)null));
        Assert.Empty(SuiteProtocolTargets.KafkaSpeaking(new ScenarioAst?[] { null, null }));
    }

    /// <summary>
    /// A multi-scenario suite shares ONE topology, so the set is the UNION: a target any scenario
    /// speaks Kafka to is one the single shared probe must confirm as a broker.
    /// </summary>
    [Fact]
    public void KafkaSpeaking_ManyScenarios_UnionsTheirTargets()
    {
        const string environmentBlock = """
            environment:
              services:
                broker-a:
                  image: acme/broker:1
                  ports: [9093]
                broker-b:
                  image: acme/broker:1
                  ports: [9094]
            """;

        var first = Ast(environmentBlock + """

            steps:
              - id: publish
                type: mq-publish.kafka
                target: broker-a
                topic: orders
                payload: "{}"
            """);

        var second = Ast(environmentBlock + """

            steps:
              - id: publish
                type: mq-publish.kafka
                target: broker-b
                topic: orders
                payload: "{}"
            """);

        var targets = SuiteProtocolTargets.KafkaSpeaking(new ScenarioAst?[] { first, second });

        Assert.Equal(BothBrokers, targets.OrderBy(t => t, StringComparer.Ordinal));
    }
}
