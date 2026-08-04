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
using System.IO;
using Vouchfx.Engine.Abstractions;
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

    // ── The suite seam: the decision this guards is suite-level, so the guard must be too ─────
    //
    // `ProviderPipeline.Compile` runs ONCE PER SCENARIO, so the check above only ever sees one
    // scenario's steps. The staging decision it protects is suite-level: `RunSuiteAsync` builds ONE
    // shared topology and stages from `SuiteProtocolTargets.KafkaSpeaking(scenarios)` — the union
    // across EVERY scenario. Split the two families across two scenarios and each compilation is
    // individually innocent, while the union that actually drives staging is not.
    //
    // HOW THESE ROWS MEASURE "was the topology build attempted", without Docker: the shared
    // environment declares `env: { FOO: "${conn:typo}" }`, a reference to a dependency that does not
    // exist. `EnvironmentMapper.Map` rejects it EAGERLY inside `SuiteTopology.StartAsync`, long
    // before DCP or any container, and `RunSuiteAsync` catches that as the distinctive line
    // `PreTopologyMarker`. Present → the build was attempted; absent → the run returned before it.
    // (Same technique, and the same marker string, as `RunSuiteAsyncTests`.)
    //
    // MEASURED RED FIRST: with the guard only at the per-scenario seam, the split row reached the
    // topology build (marker present) and printed no diagnostic at all — the exact route the guard
    // was written to close.

    private const string PreTopologyMarker = "RunSuiteAsync: environment configuration error";

    /// <summary>The environment both rows share, byte-identically, as a suite requires.</summary>
    private const string ConflictSuiteEnvironment = """
        environment:
          services:
            broker:
              image: acme/broker:1
              ports: [9093]
              env:
                FOO: "${conn:typo}"
        """;

    private const string HttpStepOnly = ConflictSuiteEnvironment + """

        steps:
          - id: get
            type: http.rest
            target: broker
            method: GET
            path: /
            expect:
              status: 200
        """;

    private const string KafkaStepOnly = ConflictSuiteEnvironment + """

        steps:
          - id: publish
            type: mq-publish.kafka
            target: broker
            topic: orders
            payload: "{}"
        """;

    private const string BothStepsInOneScenario = ConflictSuiteEnvironment + """

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
        """;

    /// <summary>
    /// The two spellings of the SAME authoring error — both families in one scenario, and the two
    /// families split across two scenarios of one suite — must be rejected before the topology is
    /// built, with the same verdict, the same REQ-018 carve-out state, and the same diagnostic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written as ONE fact running both shapes rather than a theory over two rows, because the
    /// load-bearing assertion is an EQUALITY BETWEEN them: the two seams must produce a diagnostic
    /// that is character-for-character identical. A theory could only assert the same fragments
    /// twice, which two texts can satisfy while still differing — and "two spellings of one rule is
    /// how the two drift" is the reason the message was hoisted into
    /// <c>SuiteProtocolTargets.DescribeProtocolConflict</c> in the first place. The diagnostic is
    /// compared by extraction rather than against a literal restated here, since restating it would
    /// re-create the third copy this fix removed.
    /// </para>
    /// </remarks>
    // Hoisted to fields (CA1861): constant-element arrays passed to a repeatedly-called method.
    private static readonly string[] s_singleScenarioYamls = { BothStepsInOneScenario };
    private static readonly string[] s_singleScenarioNames = { "both-in-one" };
    private static readonly string[] s_splitScenarioYamls = { HttpStepOnly, KafkaStepOnly };
    private static readonly string[] s_splitScenarioNames = { "http-half", "kafka-half" };

    [Fact]
    public async Task RunSuiteAsync_BothFamiliesAddressOneTarget_IsRejectedIdenticallyByBothSeams()
    {
        var single = await RunAndCaptureAsync(s_singleScenarioYamls, s_singleScenarioNames);
        var split = await RunAndCaptureAsync(s_splitScenarioYamls, s_splitScenarioNames);

        // The one spelling: identical text from the per-scenario seam and from the suite seam.
        Assert.NotEmpty(single.Diagnostic);
        Assert.Equal(single.Diagnostic, split.Diagnostic);

        // …and it says what it must, naming the target and both families.
        Assert.Contains("'broker'", single.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("http.rest", single.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("mq-publish.kafka", single.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("one endpoint value per target", single.Diagnostic, StringComparison.Ordinal);

        // Nothing was staged on either path: the topology build was never reached.
        Assert.DoesNotContain(PreTopologyMarker, single.Rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(PreTopologyMarker, split.Rendered, StringComparison.Ordinal);

        // Same classification. An authoring error is Inconclusive, and it is NOT a
        // security-confirmation failure — setting that flag would fire REQ-018's unconditional
        // non-zero exit for a non-security reason.
        Assert.Equal(Verdict.Inconclusive, single.Result.Verdict);
        Assert.Equal(Verdict.Inconclusive, split.Result.Verdict);
        Assert.False(single.Result.SecurityConfirmationFailed);
        Assert.False(split.Result.SecurityConfirmationFailed);

        Assert.All(single.Result.ScenarioVerdicts, v => Assert.Equal(Verdict.Inconclusive, v.Verdict));
        Assert.All(split.Result.ScenarioVerdicts, v => Assert.Equal(Verdict.Inconclusive, v.Verdict));
        Assert.Single(single.Result.ScenarioVerdicts);
        Assert.Equal(2, split.Result.ScenarioVerdicts.Count);
    }

    /// <summary>
    /// Runs one suite through <see cref="ScenarioRunner.RunSuiteAsync"/> and returns its result, the
    /// full rendered output, and the conflict diagnostic extracted from it.
    /// </summary>
    /// <remarks>
    /// The two shapes render DIFFERENTLY overall — the single-scenario one returns through the
    /// early-verdict path, which also runs the terminal renderer, while the suite seam returns
    /// before it — so only the diagnostic line itself is comparable, and it is located by its own
    /// opening token rather than by position.
    /// </remarks>
    private static async Task<(SuiteResult Result, string Rendered, string Diagnostic)> RunAndCaptureAsync(
        string[] yamls, string[] names)
    {
        var sw = new StringWriter();

        var result = await ScenarioRunner.RunSuiteAsync(
            scenarios: yamls.Select(Ast).ToArray(),
            scenarioNames: names,
            yamlTexts: yamls,
            providerAssemblies: s_providerAssemblies,
            appHostAssemblyName: "Vouchfx.Engine.Runtime.Tests",
            output: sw);

        var rendered = sw.ToString();
        var diagnostic = rendered
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .FirstOrDefault(line => line.StartsWith("Target(s) ", StringComparison.Ordinal))
            ?? string.Empty;

        return (result, rendered, diagnostic);
    }
}
