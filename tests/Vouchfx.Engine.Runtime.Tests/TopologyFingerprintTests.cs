// Vouchfx.Engine.Runtime.Tests — the `--watch` rebuild trigger (#370's recorded residual). No Docker.
//
// EACH ARM MEASURES THE RESIDUAL BEFORE IT ASSERTS THE FIX, and that ordering is the whole value of
// these tests. `ScenarioRunner.ComputeEnvironmentHash` was the SOLE input to the reuse-vs-rebuild
// decision, so every arm below first asserts that the two documents' environment hashes are EQUAL —
// which is the defect, stated as a measurement rather than as a claim — and only then that their
// topology fingerprints DIFFER. Delete the fix and the first assertion still passes while the second
// fails, so the failure names the residual rather than a broken expectation.
//
// The two residuals are different consequences of the same trigger:
//
//   1. #348's refusal (RECORDED in WatchRunner as a RESIDUAL comment). `endpointConsumingTargets`
//      decides in EnvironmentMapper.Map whether an endpoint-less `project:`-form service is a
//      refused authoring fault or a legitimate untargeted worker. A save that added a step
//      targeting such a worker left the environment hash unchanged, reused the kept topology, and
//      never re-ran the refusal — that session saw a UriFormatException instead of the located
//      diagnostic, for the rest of the session.
//
//   2. NOT PREVIOUSLY REPORTED. `kafkaSpeakingTargets` decides the STAGED FORM, not merely the
//      confirmation level (REQ-023): a Kafka-speaking service is staged as a bare `host:port`
//      bootstrap authority, anything else as a scheme-carrying URL. A save that makes a service
//      Kafka-speaking moves that set WITHOUT tripping the protocol-conflict guard — only one family
//      addresses the target at a time — so the kept topology went on staging a URL for a step that
//      consumes an authority.
//
// EACH ARM ISOLATES THE INPUT IT IS ABOUT. The two target sets overlap — the endpoint-consuming set
// is a SUPERSET of the Kafka-speaking one — so a document change that grows both proves neither. The
// first arm below moves only the superset (an http.rest step against a new target); the second moves
// only the Kafka set (an existing target's step swapped between families) and ASSERTS the superset
// is unchanged before concluding anything. That is not fastidiousness: the first draft of the second
// arm ADDED a Kafka step, and a mutation drill that deleted the Kafka input from the fingerprint
// entirely left it green.

using System;
using System.IO;
using System.Linq;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.MqPublish.Kafka;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class TopologyFingerprintTests
{
    private static readonly StepKindRegistry Registry =
        StepKindRegistry.BuildAndFreeze(new[]
        {
            typeof(HttpRestProvider).Assembly,
            typeof(MqPublishKafkaProvider).Assembly,
        });

    /// <summary>
    /// A save that adds a step TARGETING a previously untargeted service changes the fingerprint,
    /// although the <c>environment</c> block — and therefore the old reuse key — is unchanged.
    /// </summary>
    [Fact]
    public void SaveAddingAStepTargetingANewService_ChangesTheFingerprint_ThoughTheEnvironmentHashDoesNot()
    {
        const string Environment = """
            environment:
              services:
                api:
                  image: nginx:alpine
                worker:
                  image: nginx:alpine
            """;

        var before = Fingerprint($$"""
            {{Environment}}
            steps:
              - id: call-api
                type: http.rest
                target: api
                method: GET
                path: /
            """);

        var after = Fingerprint($$"""
            {{Environment}}
            steps:
              - id: call-api
                type: http.rest
                target: api
                method: GET
                path: /
              - id: call-worker
                type: http.rest
                target: worker
                method: GET
                path: /
            """);

        // THE RESIDUAL, measured: the retired reuse key cannot tell these two documents apart.
        Assert.Equal(before.EnvironmentHash, after.EnvironmentHash);

        // THE FIX.
        Assert.NotEqual(before.TopologyFingerprint, after.TopologyFingerprint);
        Assert.DoesNotContain("worker", before.Request.EndpointConsumingTargets);
        Assert.Contains("worker", after.Request.EndpointConsumingTargets);
    }

    /// <summary>
    /// A save that makes a service Kafka-speaking changes the fingerprint <strong>on the
    /// Kafka-speaking set alone</strong>. The protocol-conflict guard cannot see this — only one
    /// family addresses the target at a time — so before this widening the kept topology went on
    /// staging a URL where the step expects a bare <c>host:port</c> bootstrap authority.
    /// </summary>
    /// <remarks>
    /// <strong>The step is SWAPPED, not added, and that is the whole design of this arm.</strong>
    /// The obvious spelling — add an <c>mq-publish.kafka</c> step to a document that had none —
    /// grows the endpoint-consuming SUPERSET at the same time, so the fingerprint would still move
    /// with the Kafka set removed from it entirely: MEASURED, by a mutation drill that dropped that
    /// input and left the arm green. Replacing the <c>http.rest</c> step with a Kafka one against
    /// the SAME target holds the superset fixed (asserted below, so the isolation is a measurement
    /// rather than a claim) and leaves the Kafka set as the only input that moved.
    /// </remarks>
    [Fact]
    public void SaveMakingAServiceKafkaSpeaking_ChangesTheFingerprint_OnThatInputAlone()
    {
        const string Environment = """
            environment:
              services:
                broker:
                  image: nginx:alpine
                  ports: [9093]
                  healthCheck: { type: tcp, port: 9093 }
            """;

        var before = Fingerprint($$"""
            {{Environment}}
            steps:
              - id: talk-to-broker
                type: http.rest
                target: broker
                method: GET
                path: /
            """);

        var after = Fingerprint($$"""
            {{Environment}}
            steps:
              - id: talk-to-broker
                type: mq-publish.kafka
                target: broker
                topic: orders
                payload: '{"id":"1"}'
            """);

        Assert.Equal(before.EnvironmentHash, after.EnvironmentHash);

        // The ISOLATION: the superset is identical across the two saves, so nothing but the
        // Kafka-speaking set can be moving the fingerprint below.
        Assert.Equal(
            before.Request.EndpointConsumingTargets.OrderBy(t => t, StringComparer.Ordinal),
            after.Request.EndpointConsumingTargets.OrderBy(t => t, StringComparer.Ordinal));
        Assert.Empty(before.Request.KafkaSpeakingTargets);
        Assert.Contains("broker", after.Request.KafkaSpeakingTargets);

        Assert.NotEqual(before.TopologyFingerprint, after.TopologyFingerprint);
    }

    /// <summary>
    /// The reuse guarantee the widening must not break: editing a step's BODY, changing no targeted
    /// resource name, leaves the fingerprint alone.
    /// </summary>
    /// <remarks>
    /// This is the bound on the cost. The fingerprint moves when the SET OF TARGETED RESOURCE NAMES
    /// changes — not when a body, header, assertion or URL path changes — so the common local
    /// iteration loop still re-uses the kept topology.
    /// </remarks>
    [Fact]
    public void SaveEditingOnlyAStepBody_LeavesTheFingerprintUnchanged()
    {
        const string Environment = """
            environment:
              services:
                api:
                  image: nginx:alpine
            """;

        var before = Fingerprint($$"""
            {{Environment}}
            steps:
              - id: call-api
                type: http.rest
                target: api
                method: GET
                path: /one
            """);

        var after = Fingerprint($$"""
            {{Environment}}
            steps:
              - id: call-api
                type: http.rest
                target: api
                method: POST
                path: /two
                body: '{"changed":true}'
            """);

        Assert.Equal(before.EnvironmentHash, after.EnvironmentHash);
        Assert.Equal(before.TopologyFingerprint, after.TopologyFingerprint);
    }

    /// <summary>
    /// TWO DOCUMENTS WHOSE TARGET SETS DIFFER ONLY IN WHERE THE ELEMENT BOUNDARY FALLS produce
    /// DIFFERENT fingerprints — the intra-set join collision, closed by length framing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This arm exists because the first form of the digest was forgeable from ordinary
    /// YAML.</strong> Target names are unconstrained author text: the schema puts no
    /// <c>propertyNames</c> on <c>environment.services</c>, and <c>SuiteProtocolTargets</c> reads the
    /// step's <c>target</c> scalar verbatim. Joining a set's elements with a comma therefore made
    /// <c>{"svc,zzz"}</c> and <c>{"svc", "zzz"}</c> serialise identically — so a save that split one
    /// oddly-named target into two real ones kept its fingerprint, reused the topology, and never
    /// re-ran #348's refusal for <c>svc</c>. That is precisely the residual the fingerprint exists
    /// to close, returning through its own encoding.
    /// </para>
    /// <para>
    /// The environment blocks are deliberately IDENTICAL (both declare all three names), so the only
    /// thing that moves between the two saves is which names the steps target — asserted below
    /// before the fingerprints are compared, so a change that made the environments differ would
    /// fail loudly instead of making this arm pass for the wrong reason.
    /// </para>
    /// </remarks>
    [Fact]
    public void TargetSetsDifferingOnlyInElementBoundary_ProduceDifferentFingerprints()
    {
        // All three names declared in BOTH documents, so `environment` is byte-identical.
        const string Environment = """
            environment:
              services:
                "svc,zzz":
                  image: nginx:alpine
                svc:
                  image: nginx:alpine
                zzz:
                  image: nginx:alpine
            """;

        var oneOddlyNamedTarget = Fingerprint($$"""
            {{Environment}}
            steps:
              - id: call-one
                type: http.rest
                target: "svc,zzz"
                method: GET
                path: /
            """);

        var twoOrdinaryTargets = Fingerprint($$"""
            {{Environment}}
            steps:
              - id: call-svc
                type: http.rest
                target: svc
                method: GET
                path: /
              - id: call-zzz
                type: http.rest
                target: zzz
                method: GET
                path: /
            """);

        // The premise: same environment, different target sets. Both halves asserted so neither the
        // collision nor its absence can be concluded from an accident of the fixture.
        Assert.Equal(oneOddlyNamedTarget.EnvironmentHash, twoOrdinaryTargets.EnvironmentHash);
        Assert.Equal(
            "svc,zzz",
            string.Join(
                '|',
                oneOddlyNamedTarget.Request.EndpointConsumingTargets
                    .OrderBy(t => t, StringComparer.Ordinal)));
        Assert.Equal(
            "svc|zzz",
            string.Join(
                '|',
                twoOrdinaryTargets.Request.EndpointConsumingTargets
                    .OrderBy(t => t, StringComparer.Ordinal)));

        Assert.NotEqual(
            oneOddlyNamedTarget.TopologyFingerprint, twoOrdinaryTargets.TopologyFingerprint);
    }

    /// <summary>
    /// A change confined to a <c>security:</c> block moves the fingerprint, so a kept topology is
    /// never re-used across a change to what the run asserts about its transport.
    /// </summary>
    /// <remarks>
    /// It travels via <c>ComputeEnvironmentHash</c> rather than via either target set — the
    /// <c>security</c> block is part of the serialised <c>environment</c> — so this arm is a
    /// regression guard on the environment hash remaining an input, not evidence about the target
    /// sets. Worth pinning because the consequence is the sharpest one in the taxonomy: reusing a
    /// topology probed under <c>tls</c> for a save that now declares <c>mtls</c> would report a
    /// confirmation the run never obtained.
    /// </remarks>
    [Fact]
    public void SaveChangingASecurityProfile_ChangesTheFingerprint()
    {
        // Written out twice rather than interpolated: `mtls` requires the client pair and `tls`
        // forbids it, so the two blocks are not one block with a substituted word.
        var tls = Fingerprint("""
            environment:
              services:
                api:
                  image: nginx:alpine
                  ports: [8443]
                  healthCheck: { type: tcp, port: 8443 }
                  security:
                    profile: tls
                    endpoint: "8443"
                    caCert: ./ca.pem
            steps:
              - id: call-api
                type: http.rest
                target: api
                method: GET
                path: /
            """);

        var mtls = Fingerprint("""
            environment:
              services:
                api:
                  image: nginx:alpine
                  ports: [8443]
                  healthCheck: { type: tcp, port: 8443 }
                  security:
                    profile: mtls
                    endpoint: "8443"
                    caCert: ./ca.pem
                    clientCert: ./client.pem
                    clientKey: ./client-key.pem
            steps:
              - id: call-api
                type: http.rest
                target: api
                method: GET
                path: /
            """);

        Assert.NotEqual(tls.EnvironmentHash, mtls.EnvironmentHash);
        Assert.NotEqual(tls.TopologyFingerprint, mtls.TopologyFingerprint);

        // The target sets are identical across the two, so the move came from the environment.
        Assert.Equal(
            tls.Request.EndpointConsumingTargets.OrderBy(t => t, StringComparer.Ordinal),
            mtls.Request.EndpointConsumingTargets.OrderBy(t => t, StringComparer.Ordinal));
    }

    /// <summary>
    /// Every non-target input the request carries is hashed too — the completeness the census in
    /// <c>Vouchfx.Engine.Orchestration.Tests</c> pins structurally, measured here behaviourally.
    /// </summary>
    [Fact]
    public void EveryRequestInput_MovesTheFingerprint()
    {
        var ast = AstBuilder.Build(
            YamlDocumentParser.Parse("""
                environment:
                  services:
                    api:
                      image: nginx:alpine
                steps:
                  - id: call-api
                    type: http.rest
                    target: api
                    method: GET
                    path: /
                """),
            Registry);

        var baseline = TopologyRequest.ForScenario(ast, "host-a", "/dir-a");
        var baselinePrint = ScenarioRunner.ComputeTopologyFingerprint(baseline);

        Assert.NotEqual(
            baselinePrint,
            ScenarioRunner.ComputeTopologyFingerprint(baseline with { AppHostAssemblyName = "host-b" }));
        Assert.NotEqual(
            baselinePrint,
            ScenarioRunner.ComputeTopologyFingerprint(baseline with { SeedBaseDirectory = "/dir-b" }));
        Assert.NotEqual(
            baselinePrint,
            ScenarioRunner.ComputeTopologyFingerprint(
                baseline with { StartupTimeout = TimeSpan.FromSeconds(1) }));
    }

    private static (string EnvironmentHash, string TopologyFingerprint, TopologyRequest Request)
        Fingerprint(string yaml)
    {
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), Registry);
        var request = TopologyRequest.ForScenario(
            ast, "Vouchfx.Engine.Runtime.Tests", Directory.GetCurrentDirectory());

        return (
            ScenarioRunner.ComputeEnvironmentHash(ast.Environment),
            ScenarioRunner.ComputeTopologyFingerprint(request),
            request);
    }
}
