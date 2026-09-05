// Vouchfx.Cli.Tests — the `--watch` pre-topology gate stack and the kept-topology run seam,
// exercised through the real production wiring with a Docker-free topology double (#364, #370,
// #412).
//
// WHAT MAKES THESE POSSIBLE, AND WHY THEY DID NOT EXIST BEFORE. #364 records that `WatchRunner`
// was a second implementation of the run path with NO Docker-free test seam: the kept-topology
// entry point took a concrete `SuiteTopology`, and the whole of `RunAsync`'s wiring was built
// inside a method that stood up an Aspire topology. Three defects were found there in two review
// rounds, every one of them by a human running a container drill. `WatchRunner.CreateSession` now
// takes the topology STARTER as a parameter over the `IKeptTopology` seam, so the real compile /
// build / run / dispose / report wiring runs here against `FakeKeptTopology`.
//
// THIS PROJECT IS DELIBERATELY NOT AN ASPIRE HOST (see its csproj), so a green test in this file
// could not have reached DCP even by accident. That is part of the evidence, not an aside.
//
// THE BUILD COUNT IS THE MEASUREMENT #370 ASKS FOR. Its three consequences all reduce to "a
// pre-topology gate ran after containers were up"; a test that counts how many times the starter
// was invoked measures exactly that, and would have been red on every arm below before this change
// — the starter having been reached first, and the gate only from inside the run seam afterwards.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Cli;
using Vouchfx.Cli.Watch;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Security;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class WatchPreTopologyGateTests : IDisposable
{
    private static readonly StepKindRegistry Registry = ProviderRegistryFactory.BuildCoreRegistry();

    private readonly string _root;

    public WatchPreTopologyGateTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "vouchfx-watch-gate-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the test.
        }
    }

    // ── #370: every pre-topology gate runs BEFORE the topology ────────────────

    /// <summary>
    /// T1 — a schema-invalid save is refused with NO topology built. #370's first consequence:
    /// "a schema-invalid suite starts containers", where the same suite under plain <c>run</c> is
    /// rejected before any Docker work.
    /// </summary>
    [Fact]
    public async Task SchemaInvalidSave_IsRefusedBeforeTheTopologyIsBuilt()
    {
        // A misspelled service key: rejected by the schema, and deliberately a fault the AST
        // builder itself tolerates, so the arm measures the SCHEMA door rather than the parse door
        // that always ran here.
        var harness = await DriveAsync("""
            metadata:
              name: schema-invalid
            environment:
              services:
                api:
                  image: nginx:alpine
                  securty: mtls
            steps:
              - id: s
                type: script.csharp
                code: |
                  Vars.Set("k", "v");
            """);

        Assert.Equal(0, harness.BuildCount);
        Assert.Contains("securty", harness.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Parse / AST error", harness.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// T2 — an unregistered <c>security.profile</c> is refused before the topology, so
    /// <c>SecuredEndpointProbe</c>'s unrecognised-profile refusal stays unreachable by author input
    /// on this path too. #370's third consequence: that refusal is documented unreachable because
    /// the schema and <c>SecurityProfileWiringValidator</c> reject an unregistered profile first —
    /// which was true on every path except <c>--watch</c>, where a typo started containers, passed
    /// the health gate, and then told the author to register a profile in an internal engine
    /// dictionary.
    /// </summary>
    /// <remarks>
    /// Asserted on the OUTCOME rather than on which validator spoke: two doors can legitimately
    /// refuse an unregistered profile (the schema-time registry cross-check and the wiring
    /// validator), and pinning the door would make this test fail on a change that improved the
    /// diagnostic without touching the ordering this test is about.
    /// </remarks>
    [Fact]
    public async Task UnregisteredSecurityProfile_IsRefusedBeforeTheProbeCouldEverSeeIt()
    {
        var harness = await DriveAsync("""
            metadata:
              name: unregistered-profile
            environment:
              services:
                api:
                  image: nginx:alpine
                  ports: [8443]
                  healthCheck: { type: tcp, port: 8443 }
                  security:
                    profile: kerbros
                    endpoint: "8443"
            steps:
              - id: s
                type: script.csharp
                code: |
                  Vars.Set("k", "v");
            """);

        Assert.Equal(0, harness.BuildCount);
        Assert.Contains("kerbros", harness.Output, StringComparison.Ordinal);

        // The probe's own wording must not appear — quoted from SecuredEndpointProbe verbatim, not
        // paraphrased. An earlier form of this assertion searched for "unrecognised security
        // profile", which the probe never emits, so it could not have failed however the ordering
        // regressed: a negative assertion against text that does not exist is green by
        // construction. This is the actual sentence an author saw before #370.
        Assert.DoesNotContain(
            "not a security profile this engine recognises",
            harness.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Engine maintainers:", harness.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// T3 — a REQ-023 both-families conflict is refused before staging. #370's second consequence:
    /// on a secured broker suite the probe attempted a Kafka <c>ApiVersions</c> round trip against a
    /// target the engine had already decided was misconfigured, and reported "the broker did not
    /// answer" — blaming the broker for an authoring conflict detected one layer down.
    /// </summary>
    [Fact]
    public async Task BothFamiliesOnOneTarget_IsRefusedBeforeStaging()
    {
        var harness = await DriveAsync("""
            metadata:
              name: protocol-conflict
            environment:
              services:
                broker:
                  image: nginx:alpine
                  ports: [9093]
                  healthCheck: { type: tcp, port: 9093 }
            steps:
              - id: http-call
                type: http.rest
                target: broker
                method: GET
                path: /
              - id: publish
                type: mq-publish.kafka
                target: broker
                topic: orders
                payload: '{"id":"1"}'
            """);

        Assert.Equal(0, harness.BuildCount);
        Assert.Contains("broker", harness.Output, StringComparison.Ordinal);
    }

    // ── #412: the merged authoring door on the watch seam ─────────────────────

    /// <summary>
    /// T4 — <strong>#412's acceptance.</strong> A document carrying faults in BOTH pre-topology
    /// walks reports both, in the run path's order (the provider-pipeline fault first, the
    /// step-secret fault second).
    /// </summary>
    /// <remarks>
    /// Before this change the watch seam ran the RETIRED order — the secret pass first, returning
    /// at its first fault, so the compile fault was never computed. The two verdict-producing paths
    /// had been unified by #399; this seam kept the old ordering in a third spelling, which is
    /// exactly the residue #412 was filed to remove.
    /// </remarks>
    [Fact]
    public async Task DocumentWithCompileAndSecretFaults_ReportsBothInTheRunPathOrder()
    {
        var harness = await DriveAsync("""
            metadata:
              name: both-faults
            environment:
              services:
                broker:
                  image: nginx:alpine
                  ports: [9093]
                  healthCheck: { type: tcp, port: 9093 }
            steps:
              - id: http-call
                type: http.rest
                target: broker
                method: GET
                path: /
                headers:
                  Authorization: "${secret:nosuchsource/token}"
              - id: publish
                type: mq-publish.kafka
                target: broker
                topic: orders
                payload: '{"id":"1"}'
            """);

        Assert.Equal(0, harness.BuildCount);

        var compileFaultAt = harness.Output.IndexOf(
            "addressed by both an HTTP-family step", StringComparison.Ordinal);
        var secretFaultAt = harness.Output.IndexOf(
            "step 'http-call'", StringComparison.Ordinal);

        Assert.True(
            compileFaultAt >= 0,
            "the provider-pipeline (REQ-023) fault must be reported: "
            + "before #412 this seam returned at the secret fault and never computed it.\n"
            + harness.Output);
        Assert.True(
            secretFaultAt >= 0,
            "the step-secret fault must be reported alongside it.\n" + harness.Output);
        Assert.True(
            compileFaultAt < secretFaultAt,
            "the two faults must appear in the run path's order — pipeline first, step secret "
            + "second — because that ordering is what makes the diagnosis a property of the "
            + "DOCUMENT rather than of which path the author took.\n" + harness.Output);
    }

    // ── #370's recorded residual: the rebuild trigger ─────────────────────────

    /// <summary>
    /// T5 — a save that adds a step TARGETING a previously untargeted service rebuilds the
    /// topology, even though the <c>environment</c> block is byte-identical.
    /// </summary>
    /// <remarks>
    /// This is #370's recorded residual, which <c>WatchRunner</c> carried as a stated RESIDUAL
    /// comment: the rebuild trigger was the environment hash alone, so such a save reused the kept
    /// topology and never re-ran #348's refusal — that session saw a <c>UriFormatException</c>
    /// instead of the located diagnostic, for the rest of the session. The fingerprint-level
    /// measurement of the same residual (the environment hash being EQUAL across the two saves) is
    /// <c>Vouchfx.Engine.Runtime.Tests.TopologyFingerprintTests</c>; this arm proves the wiring
    /// delivers it.
    /// </remarks>
    [Fact]
    public async Task SaveAddingAStepTargetingANewService_RebuildsTheTopology()
    {
        const string Environment = """
            environment:
              services:
                api:
                  image: nginx:alpine
            """;

        var harness = await DriveAsync(
            $$"""
            metadata:
              name: retarget
            {{Environment}}
            steps:
              - id: local
                type: script.csharp
                code: |
                  Vars.Set("k", "v");
            """,
            $$"""
            metadata:
              name: retarget
            {{Environment}}
            steps:
              - id: local
                type: script.csharp
                code: |
                  Vars.Set("k", "v");
              - id: call-api
                type: http.rest
                target: api
                method: GET
                path: /
                timeout: 5s
                continueOnFailure: true
            """);

        Assert.Equal(2, harness.BuildCount);

        // #473, asserted on THIS arm because it is the only one that builds twice. The session's
        // path-disclosure ledger must reach the starter, and it must be the SAME INSTANCE on both
        // builds — reference equality, not a null check, because the ledger's whole value is its
        // lifetime: a resolved `serverArtifacts[].source` or seed SQL path recorded while one
        // topology was built has to stay substitutable from text a later save emits. A starter
        // handed a fresh ledger per build would satisfy every not-null assertion and silently drop
        // everything the previous build recorded.
        Assert.Equal(2, harness.PathLedgers.Count);
        Assert.All(harness.PathLedgers, l => Assert.Same(harness.SessionPathLedger, l));
    }

    /// <summary>
    /// T6 — the reuse guarantee the wider fingerprint must not break: a save that edits only a
    /// step's BODY, changing no targeted resource name, still re-uses the kept topology.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The regression guard on T5's cost. Reuse is the whole point of <c>--watch</c>; a fingerprint
    /// that moved on any steps-level edit would rebuild containers on every save.
    /// </para>
    /// <para>
    /// <strong>The build count alone does not distinguish reuse from refusal</strong> — a second
    /// save that a gate rejected also leaves the count at 1, and <c>SaveCount</c> is the harness's
    /// own loop counter, which increments whatever happened. The topology's reseed count is what
    /// separates them: the reuse arm of the run seam is the only path that reseeds, so
    /// <c>ReseedCount == 1</c> after two saves says the second save really did run against the kept
    /// topology.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SaveEditingOnlyAStepBody_ReusesTheKeptTopology()
    {
        var harness = await DriveAsync(
            ScriptOnly("""Vars.Set("k", "one");"""),
            ScriptOnly("""Vars.Set("k", "two");"""));

        Assert.Equal(1, harness.BuildCount);
        Assert.Equal(2, harness.SaveCount);

        var fake = Assert.Single(harness.Topologies);
        Assert.Equal(1, fake.ReseedCount);
    }

    // ── #364 defect 3: the side effects nothing could assert ──────────────────

    /// <summary>
    /// T8 — the kept topology is RE-SEEDED before a reuse re-run and NOT before the first run
    /// against a freshly built one, and the reseed happens after the build-time advisories are
    /// replayed and before the run.
    /// </summary>
    /// <remarks>
    /// #364's third-defect class: a side effect no signature, guard or analyser catches. The order
    /// is read off the double's own call log rather than from a timestamp, so a re-ordering shows up
    /// as a changed sequence rather than as a flake. The ISOLATION half of the reset is not
    /// asserted here: a double declaring no dependencies gets
    /// <c>NullScenarioIsolation</c> from <c>ScenarioIsolationFactory</c>, whose
    /// <c>EndScenarioAsync</c> is a no-op with nothing to record — pinning it would need a
    /// resettable-dependency double, which is a different seam from this one.
    /// </remarks>
    [Fact]
    public async Task ReuseRun_ReseedsTheKeptTopology_AndTheFirstRunDoesNot()
    {
        var harness = await DriveAsync(
            ScriptOnly("""Vars.Set("k", "one");"""),
            ScriptOnly("""Vars.Set("k", "two");"""),
            configure: fake => fake.SelectionNotices = new[]
            {
                new EndpointSelectionNotice("api", "http", "https"),
            });

        var fake = Assert.Single(harness.Topologies);
        Assert.Equal(1, fake.ReseedCount);

        // The advisory replay precedes the reseed, on the save that reseeds.
        var noticesAt = fake.Calls.ToList().FindLastIndex(
            c => c == nameof(IKeptTopology.EndpointSelectionNotices));
        var reseedAt = fake.Calls.ToList().IndexOf(nameof(IKeptTopology.ReseedAsync));
        Assert.True(reseedAt > 0, "the reuse run must reseed:\n" + string.Join(", ", fake.Calls));
        Assert.True(
            noticesAt < reseedAt,
            "the build-time advisories are replayed ahead of the reset, so a reader sees what the "
            + "topology was built with before the run that follows it: "
            + string.Join(", ", fake.Calls));
    }

    /// <summary>
    /// T9 — <strong>#364's third defect, made assertable.</strong> Security confirmations are
    /// rendered on EVERY re-run, with the qualifier saying they are replayed rather than
    /// re-measured.
    /// </summary>
    /// <remarks>
    /// The probe ran and gated correctly, but a watch user saw a green run with no indication of
    /// WHICH level was confirmed — defeating the entire reason REQ-005 uses named levels rather than
    /// a boolean. Nothing was omitted from a call; a statement was simply absent, which is why no
    /// signature check could have found it.
    /// </remarks>
    [Fact]
    public async Task SecurityConfirmations_AreRenderedOnEveryRun_WithTheReplayQualifier()
    {
        var confirmation = new SecurityConfirmation(
            TargetName: "api",
            TargetKind: "service",
            DeclaredProfile: "mtls",
            DeclaredEndpoint: "8443",
            ObservedAddress: "127.0.0.1:8443",
            ObservedProtocol: "Tls13",
            ClientIdentityResolved: true,
            Level: SecurityConfirmationLevel.AuthenticatedRoundTrip,
            Detail: "confirmed",
            Identity: new SecuredTargetIdentity("api", "DIGEST"));

        var harness = await DriveAsync(
            ScriptOnly("""Vars.Set("k", "one");"""),
            ScriptOnly("""Vars.Set("k", "two");"""),
            configure: fake => fake.Confirmations = new[] { confirmation });

        var qualifiers = Occurrences(
            harness.Output, "security: confirmed once when this topology was built");
        Assert.Equal(2, qualifiers);
        Assert.Equal(2, Occurrences(harness.Output, "AuthenticatedRoundTrip"));
    }

    /// <summary>
    /// T10 — both transport advisories are PRINTED once per re-run, under the staleness qualifier
    /// (#348 / #450 / #453).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Scoped to the printed half, and the name says so.</strong> An earlier form of this
    /// test claimed to assert the EMITTED half as well — that exactly one <c>transport-notice</c>
    /// record is produced per re-run with <c>replayed: true</c> — and it asserted no such thing:
    /// <c>--watch</c> is wired to no report artefact whatever, so the records this path builds reach
    /// <c>TerminalRenderer</c>, which ignores an unrecognised type, and nothing observable. Nothing
    /// in this harness can see them.
    /// </para>
    /// <para>
    /// The emitted half is guarded where it can be: <c>Vouchfx.Engine.Runtime.Tests
    /// .TransportNoticeEventEmissionTests</c> attributes every advisory PRINT site in the runner to
    /// its containing method and fails any that does not also call <c>TransportNoticeEvents.ToLines</c>
    /// — so the print this test counts and the emission that test requires are tied together, across
    /// the two assemblies, without either over-claiming.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TransportNotices_ArePrintedOncePerRun_UnderTheStalenessQualifier()
    {
        var harness = await DriveAsync(
            ScriptOnly("""Vars.Set("k", "one");"""),
            ScriptOnly("""Vars.Set("k", "two");"""),
            configure: fake =>
            {
                fake.SelectionNotices = new[] { new EndpointSelectionNotice("api", "http", "https") };
                fake.TrustNotices = new[] { new EndpointTrustNotice("api", "https") };
            });

        Assert.Equal(
            2, Occurrences(harness.Output, "transport: selected once when this topology was built"));
        Assert.Equal(2, Occurrences(harness.Output, "will use PLAINTEXT"));
        Assert.Equal(2, Occurrences(harness.Output, "configures NO client trust material"));
    }

    // ── #364 defects 1 and 2: the arguments that were dropped ─────────────────

    /// <summary>
    /// T11 — <strong>#364's second defect, made assertable.</strong> The Kafka-speaking target set
    /// reaches the topology request for a broker declared as a SERVICE — the shape REQ-011 exists
    /// for, and the customer's actual shape.
    /// </summary>
    /// <remarks>
    /// Omitting that argument was not an error and not a failure: it silently downgraded
    /// <c>AuthenticatedRoundTrip</c> to <c>TransportConfirmed</c>, and (REQ-023) changed the STAGED
    /// FORM the step reads from a bare <c>host:port</c> authority to a URL.
    /// <c>SuiteTopology.StartAsync</c>'s Step 0 guard is per-ARGUMENT and cannot reach a shape that
    /// degrades rather than fails, which is the argument #364 makes for a test-visible construction
    /// path instead of more guards.
    /// <para>
    /// The starter CAPTURES and THROWS: the assertion is about the request, and letting the run
    /// proceed would connect a Kafka producer to a fabricated authority for no gain.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task KafkaSpeakingTargets_ReachTheTopologyRequest()
    {
        TopologyRequest? captured = null;

        await DriveCapturingAsync(
            """
            metadata:
              name: kafka-service-target
            environment:
              services:
                broker:
                  image: nginx:alpine
                  ports: [9093]
                  healthCheck: { type: tcp, port: 9093 }
            steps:
              - id: publish
                type: mq-publish.kafka
                target: broker
                topic: orders
                payload: '{"id":"1"}'
            """,
            request => captured = request);

        Assert.NotNull(captured);
        Assert.Contains("broker", captured!.KafkaSpeakingTargets);
        Assert.Contains("broker", captured.EndpointConsumingTargets);
    }

    /// <summary>
    /// T12 — <strong>#364's first defect, made assertable.</strong> A suite that DECLARES
    /// <c>security</c> reaches the topology starter with an accessor that RESOLVED its declaration —
    /// not with the null-object singleton, and not with nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The accessor was omitted from this call once, invisibly — an optional parameter left off
    /// compiles and reads correctly — and every secured suite became unrunnable under <c>--watch</c>,
    /// failing closed with messages blaming the author's certificates for a host defect.
    /// </para>
    /// <para>
    /// <strong>The fixture is a real certificate bed, and an unsecured document would have made this
    /// arm vacuous.</strong> Peer review measured that: the starter parameter is non-nullable, and
    /// <c>SecurityConfigurationAccessor.Build</c> returns
    /// <c>NullSecurityConfigurationAccessor.Instance</c> for a document declaring no <c>security</c>
    /// — so a bare <c>Assert.NotNull</c> over an unsecured suite passes even if the <c>Build</c> call
    /// is deleted outright. Both assertions below fail on that deletion: the singleton is rejected by
    /// identity, and <c>For("api")</c> returns the resolved configuration only because the
    /// declaration was really walked.
    /// </para>
    /// <para>
    /// <strong>What is still not covered here.</strong> This shows the accessor RESOLVED the
    /// declaration, not that the certificate material LOADS: the load is lazy, inside
    /// <c>StartAsync</c>, past the Docker line. That half — an encrypted client key opening against
    /// the production resolver set, and a wrong passphrase raising EDGE-001 rather than connecting
    /// anonymously — is <c>Vouchfx.Engine.Runtime.Tests.WatchProbeSecurityWiringTests</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASecuredSuite_ReachesTheTopologyStarterWithAResolvedSecurityAccessor()
    {
        // Real PEMs on disk: the provider-pipeline door checks that every declared path exists and
        // is contained by the suite directory, so the save is refused before the starter otherwise.
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        ISecurityConfigurationAccessor? captured = null;

        await DriveCapturingAsync(
            $$"""
            metadata:
              name: secured
            environment:
              services:
                api:
                  image: nginx:alpine
                  ports: [8443]
                  healthCheck: { type: tcp, port: 8443 }
                  security:
                    profile: mtls
                    endpoint: "8443"
                    caCert: ./{{TestCertificateAuthority.CaFileName}}
                    clientCert: ./{{TestCertificateAuthority.ClientCertFileName}}
                    clientKey: ./{{TestCertificateAuthority.ClientKeyFileName}}
            steps:
              - id: local
                type: script.csharp
                code: |
                  Vars.Set("k", "v");
            """,
            _ => { },
            accessor => captured = accessor,
            suiteDirectory: bed.SuiteDirectory);

        Assert.NotNull(captured);

        // NOT the null object — this is the assertion the unsecured fixture could not make.
        Assert.NotSame(NullSecurityConfigurationAccessor.Instance, captured);

        // …and it really resolved THIS document's declaration.
        Assert.NotNull(captured!.For("api"));
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static string ScriptOnly(string code) => $"""
        metadata:
          name: script-only
        steps:
          - id: local
            type: script.csharp
            code: |
              {code}
        """;

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>
    /// Drives the REAL session wiring over a Docker-free topology double, one
    /// <c>OnChangeAsync</c> per save, and returns what was observed.
    /// </summary>
    private async Task<Harness> DriveAsync(
        string save, Action<FakeKeptTopology>? configure = null)
        => await DriveAsync(new[] { save }, configure);

    private async Task<Harness> DriveAsync(
        string firstSave, string secondSave, Action<FakeKeptTopology>? configure = null)
        => await DriveAsync(new[] { firstSave, secondSave }, configure);

    private async Task<Harness> DriveAsync(
        IReadOnlyList<string> saves, Action<FakeKeptTopology>? configure)
    {
        var filePath = Path.Combine(_root, "watched.e2e.yaml");
        await File.WriteAllTextAsync(filePath, saves[0]);

        var output = new StringWriter();
        var harness = new Harness();

        await using var session = WatchRunner.CreateSession(
            filePath,
            Registry,
            output,
            appHostAssemblyName: "Vouchfx.Cli.Tests",
            new ResolvedSecretLedger(),
            harness.SessionPathLedger,
            (request, accessor, pathLedger, _) =>
            {
                harness.BuildCount++;
                harness.Requests.Add(request);
                harness.Accessors.Add(accessor);
                harness.PathLedgers.Add(pathLedger);
                var fake = new FakeKeptTopology();
                fake.Services["api"] = "http://127.0.0.1:1";
                configure?.Invoke(fake);
                harness.Topologies.Add(fake);
                return Task.FromResult<IKeptTopology>(fake);
            });

        foreach (var content in saves)
        {
            await File.WriteAllTextAsync(filePath, content);
            await session.OnChangeAsync(content, CancellationToken.None);
            harness.SaveCount++;
        }

        harness.Output = output.ToString();
        return harness;
    }

    /// <summary>
    /// Drives ONE save with a starter that captures its arguments and then throws, so the assertion
    /// is made on the request/accessor without a run against a fabricated endpoint.
    /// </summary>
    /// <param name="suiteDirectory">
    /// Where the watched file is written. Defaults to this fixture's own temp root; a secured suite
    /// passes the certificate bed's directory instead, because every declared <c>security</c> path is
    /// resolved against — and contained by — the watched file's own directory.
    /// </param>
    private async Task DriveCapturingAsync(
        string save,
        Action<TopologyRequest> onRequest,
        Action<ISecurityConfigurationAccessor>? onAccessor = null,
        string? suiteDirectory = null)
    {
        var filePath = Path.Combine(suiteDirectory ?? _root, "captured.e2e.yaml");
        await File.WriteAllTextAsync(filePath, save);

        var output = new StringWriter();

        await using var session = WatchRunner.CreateSession(
            filePath,
            Registry,
            output,
            appHostAssemblyName: "Vouchfx.Cli.Tests",
            new ResolvedSecretLedger(),
            new SecurityPathDisclosureLedger(),
            (request, accessor, _, _) =>
            {
                onRequest(request);
                onAccessor?.Invoke(accessor);
                throw new StopAfterCaptureException();
            });

        await Assert.ThrowsAsync<StopAfterCaptureException>(
            () => session.OnChangeAsync(save, CancellationToken.None));

        // Not vacuous: reaching the starter at all means every pre-topology gate passed, so the
        // document under test really did get as far as a topology build.
        Assert.Equal(string.Empty, output.ToString());
    }

    private sealed class Harness
    {
        public int BuildCount { get; set; }

        public int SaveCount { get; set; }

        public string Output { get; set; } = string.Empty;

        public List<TopologyRequest> Requests { get; } = new();

        public List<ISecurityConfigurationAccessor> Accessors { get; } = new();

        /// <summary>
        /// The path-disclosure ledger each build was handed (#473). Captured for the same reason
        /// <see cref="Accessors"/> is: it is the argument whose omission would be invisible — the
        /// session would build a topology whose resolved host paths nothing downstream can
        /// substitute, and every existing assertion would stay green.
        /// </summary>
        public List<SecurityPathDisclosureLedger> PathLedgers { get; } = new();

        /// <summary>
        /// The ledger handed to <c>CreateSession</c>, held so an arm can assert that the SAME
        /// instance reached the starter. A fresh one per build would satisfy a not-null check and
        /// still lose every path the previous build recorded (#473).
        /// </summary>
        public SecurityPathDisclosureLedger SessionPathLedger { get; } = new();

        public List<FakeKeptTopology> Topologies { get; } = new();
    }

    private sealed class StopAfterCaptureException : Exception
    {
    }
}
