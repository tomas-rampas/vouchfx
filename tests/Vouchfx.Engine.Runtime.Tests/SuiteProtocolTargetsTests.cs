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

    // ── REQ-023 (amended): the HTTP half of the same inference, and the conflict ──────────

    /// <summary>
    /// The HTTP-family half: a target named by <c>http.rest</c> is one the engine must stage as a
    /// scheme-carrying URL, and it is reported independently of the Kafka set.
    /// </summary>
    [Fact]
    public void HttpSpeaking_TargetOfAnHttpStep_IsIncludedAndIsNotKafkaSpeaking()
    {
        var ast = Ast("""
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
            """);

        Assert.Equal(OnlyApi, SuiteProtocolTargets.HttpSpeaking(new[] { (ScenarioAst?)ast }));
        Assert.Empty(SuiteProtocolTargets.KafkaSpeaking(ast));
        Assert.Empty(SuiteProtocolTargets.BothHttpAndKafkaSpeaking(new[] { (ScenarioAst?)ast }));
    }

    /// <summary>
    /// One service addressed by BOTH families is the conflict REQ-023's amendment creates and the
    /// validator rejects: the engine stages one value per target and the two families consume
    /// different shapes of it.
    /// </summary>
    [Fact]
    public void BothHttpAndKafkaSpeaking_ServiceAddressedByBothFamilies_IsReported()
    {
        var ast = Ast("""
            environment:
              services:
                broker:
                  image: acme/broker:1
                  ports: [9093]
            steps:
              - id: get
                type: http.rest
                target: broker
                method: GET
                path: /
                expect:
                  status: 200
              - id: publish
                type: mq-publish.kafka
                target: broker
                topic: orders
                payload: "{}"
            """);

        Assert.Equal(OnlyBroker, SuiteProtocolTargets.BothHttpAndKafkaSpeaking(new[] { (ScenarioAst?)ast }));
    }

    /// <summary>
    /// The conflict is per TARGET, not per suite: two different services, one addressed by each
    /// family, is the ordinary shape and must not be rejected.
    /// </summary>
    [Fact]
    public void BothHttpAndKafkaSpeaking_SeparateServicesPerFamily_IsNotAConflict()
    {
        var ast = Ast("""
            environment:
              services:
                api:
                  image: acme/api:1
                broker:
                  image: acme/broker:1
                  ports: [9093]
            steps:
              - id: get
                type: http.rest
                target: api
                method: GET
                path: /
                expect:
                  status: 200
              - id: publish
                type: mq-publish.kafka
                target: broker
                topic: orders
                payload: "{}"
            """);

        Assert.Empty(SuiteProtocolTargets.BothHttpAndKafkaSpeaking(new[] { (ScenarioAst?)ast }));
    }

    /// <summary>
    /// The drift guard behind <c>SuiteProtocolTargets</c>'s hand-written family lists: exactly the
    /// five step types those lists name may read <c>VarKeys.Service(model.Target)</c> in their
    /// emitted CSX. A sixth provider adopting that pattern without being classified would be
    /// staged in whatever form the suite's other steps happened to imply, and would silently stop
    /// conflicting with a step of the other family on the same target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scans the provider SOURCES rather than restating the list, for the same reason
    /// <c>SecurityProfileRegistryTests</c> reads the emitted helpers' own switch arms: a second
    /// hand-maintained list here would be the very drift this test exists to catch.
    /// </para>
    /// <para>
    /// The five split into two groups and the split is the point. The three HTTP-family providers
    /// read that key UNCONDITIONALLY — a dependency target is rejected outright at validation
    /// (REQ-012 as narrowed) — so their targets are always staged as scheme-carrying URLs. The two
    /// Kafka providers read it CONDITIONALLY, only when the target names a declared service, and
    /// read <c>VarKeys.Connection</c> otherwise; their service targets are staged as bootstrap
    /// authorities. Only <c>VarKeys.Service(model.Target)</c> counts: a provider staging a HOST
    /// RESOURCE's own key (a <c>webhook-listen.http</c> listener, a <c>trace-expect.otlp</c>
    /// receiver) or a dependency's sidecar
    /// (<c>VarKeys.Service(avro.SchemaRegistryTarget + "-sr")</c>) is reading a name the engine
    /// itself minted, not the step's <c>target</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void ProtocolFamilyLists_CoverEverySvcKeyConsumingStepType()
    {
        var providersDirectory = Path.Combine(ResolveRepoRoot(), "src", "Providers");
        Assert.True(
            Directory.Exists(providersDirectory),
            $"Provider sources not found at '{providersDirectory}'; this guard cannot run.");

        var consuming = Directory
            .EnumerateFiles(providersDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                       StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                       StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path)
                .Contains("VarKeys.Service(model.Target)", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(SvcKeyConsumingProviderTypes, consuming);
    }

    /// <summary>
    /// The provider TYPE names behind the five step types <c>SuiteProtocolTargets</c> classifies —
    /// its three HTTP-family entries plus the two Kafka ones — spelled the way the source scan
    /// above finds them, and ordinally sorted to match its ordering.
    /// </summary>
    private static readonly string[] SvcKeyConsumingProviderTypes =
    {
        "HttpRestProvider",
        "HttpSoapProvider",
        "MetricsAssertPrometheusProvider",
        "MqExpectKafkaProvider",
        "MqPublishKafkaProvider",
    };

    private static readonly string[] OnlyApi = { "api" };

    private static readonly string[] OnlyBroker = { "broker" };

    /// <summary>
    /// Walks up from the test assembly's output directory to the repository root — the same
    /// derivation <c>ExamplesCompileTests.ResolveRepoRoot</c> uses, and for the same reason.
    /// </summary>
    private static string ResolveRepoRoot()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(SuiteProtocolTargetsTests).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."));
    }
}
