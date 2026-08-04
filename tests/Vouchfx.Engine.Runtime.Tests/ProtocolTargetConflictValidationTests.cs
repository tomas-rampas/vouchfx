// REQ-023 (as amended 2026-08-04) — one target, one staged form
// (authenticated-infrastructure-mtls, slice E; peer review fix round three, MAJOR-2).
//
// The amended requirement stages a service's endpoint in the form its own consumer uses: an
// `https://host:port` URL for a target the HTTP family addresses, a bare `host:port` bootstrap
// authority for one the Kafka families address. One endpoint stages ONE value, so a target
// addressed by both families cannot be staged correctly for either without the other having to
// transform it — which is exactly what that requirement forbids.
//
// Rejected at the pre-topology validation stage, alongside the security-artefact preflight and the
// profile-wiring invariant, so it surfaces at `vouchfx validate` rather than as a run-time
// environment error inside a step. It narrows nothing that ever worked: before the amendment, the
// Kafka half of such a suite read `conn::<name>` — never staged for a service — and failed every
// time with "kafka bootstrap not found".
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqPublish.Kafka;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Pins the both-families rejection, and pins that it does not fire on the ordinary shape.
/// </summary>
public sealed class ProtocolTargetConflictValidationTests
{
    private static readonly System.Reflection.Assembly[] s_providerAssemblies =
        new[]
        {
            typeof(MqPublishKafkaProvider).Assembly,
            typeof(Vouchfx.Steps.MqExpect.Kafka.MqExpectKafkaProvider).Assembly,
            typeof(Vouchfx.Steps.HttpRest.HttpRestProvider).Assembly,
        };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    private static ScenarioAst Ast(string yaml) =>
        AstBuilder.Build(YamlDocumentParser.Parse(yaml), s_registry);

    /// <summary>
    /// A single service addressed by an <c>http.rest</c> step and a <c>mq-publish.kafka</c> step
    /// fails validation, naming the target and both families.
    /// </summary>
    [Fact]
    public void Compile_ServiceAddressedByBothFamilies_FailsValidationNamingTheTarget()
    {
        var result = ProviderPipeline.Compile(
            Ast("""
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
                """),
            s_registry,
            "TestSuite");

        Assert.NotNull(result.Failure);
        Assert.Contains("'broker'", result.Failure!.Message, StringComparison.Ordinal);
        Assert.Contains("http.rest", result.Failure.Message, StringComparison.Ordinal);
        Assert.Contains("mq-publish.kafka", result.Failure.Message, StringComparison.Ordinal);
        Assert.Contains(
            "one endpoint value per target", result.Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ordinary shape — an HTTP API and a broker declared as two services, one family each —
    /// compiles unchanged. The check is per target, not per suite.
    /// </summary>
    [Fact]
    public void Compile_SeparateServicesPerFamily_Compiles()
    {
        var result = ProviderPipeline.Compile(
            Ast("""
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
                """),
            s_registry,
            "TestSuite");

        Assert.Null(result.Failure);
    }

    /// <summary>
    /// A Kafka step targeting a declared SERVICE — REQ-011's shape, and the one MAJOR-2 fixes —
    /// compiles, and the emitted CSX reads the <c>svc::</c> key the engine actually stages for a
    /// service rather than the <c>conn::</c> key it stages only for dependencies.
    /// </summary>
    /// <remarks>
    /// This is the compile-level half of the proof; the execution half runs a real broker declared
    /// as a service and asserts the step reaches it (see
    /// <c>KafkaServiceTargetDockerTests</c>).
    /// </remarks>
    [Fact]
    public void Compile_KafkaStepTargetingAService_EmitsTheServiceKey()
    {
        var result = ProviderPipeline.Compile(
            Ast("""
                environment:
                  services:
                    broker:
                      image: acme/broker:1
                      ports: [9093]
                steps:
                  - id: publish
                    type: mq-publish.kafka
                    target: broker
                    topic: orders
                    payload: "{}"
                """),
            s_registry,
            "TestSuite");

        Assert.Null(result.Failure);
        Assert.NotNull(result.Assembled);
        Assert.Contains("\"svc::broker\"", result.Assembled!.CsxSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"conn::broker\"", result.Assembled.CsxSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// The dependency shape is unchanged: a Kafka step targeting a declared <c>kafka</c> dependency
    /// still reads <c>conn::</c>, so nothing that worked before now reads a key the engine does not
    /// stage.
    /// </summary>
    [Fact]
    public void Compile_KafkaStepTargetingADependency_EmitsTheConnectionKey()
    {
        var result = ProviderPipeline.Compile(
            Ast("""
                environment:
                  dependencies:
                    events:
                      type: kafka
                steps:
                  - id: publish
                    type: mq-publish.kafka
                    target: events
                    topic: orders
                    payload: "{}"
                """),
            s_registry,
            "TestSuite");

        Assert.Null(result.Failure);
        Assert.NotNull(result.Assembled);
        Assert.Contains("\"conn::events\"", result.Assembled!.CsxSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"svc::events\"", result.Assembled.CsxSource, StringComparison.Ordinal);
    }
}
