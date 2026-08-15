// EDGE-004 + EDGE-005 — the two false-assurance traps REQ-005's probe exists to close, executed
// against a live broker (authenticated-infrastructure-mtls, slice F acceptance drills).
//
// WHAT THESE TWO EDGES HAVE IN COMMON, and why neither is checkable by inspection. Both describe a
// suite that VALIDATES, a topology that comes up HEALTHY, and a broker that would answer every
// step — while the connection the steps make is unauthenticated. Nothing in the declaration is
// wrong-looking; the fault is entirely in the runtime behaviour of the endpoint the suite resolved
// to. A unit test can assert that the probe raises on a stubbed unsecured peer, and slice E's
// SecuredEndpointProbeTests do exactly that; what it cannot do is establish that a REAL broker,
// configured the way the deployment configures one, presents the shape the stub imitates. That is
// what this file measures.
//
//   EDGE-004 — the false-pass trap. The deployment keeps an inter-broker PLAINTEXT listener open
//   beside the SSL one, so `security.endpoint: 9092` names a port that answers every request and
//   authenticates nothing. REQ-002 makes the author name the endpoint; REQ-005 makes the engine
//   confirm it.
//
//   EDGE-005 — the healthy-but-unsecured broker. The keystore lands at a path the broker's own
//   startup logic does not check, so the broker comes up with no SSL listener at all. REQ-004's
//   host-side existence check cannot see this: the host file exists, and only its in-container
//   destination is wrong.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// THE FIXTURE'S CONDITIONAL ENTRYPOINT, which is EDGE-005's whole difficulty and is therefore
// recorded with the measurements that settled it.
//
// EDGE-005 needs a broker that is HEALTHY and UNSECURED. The stock Confluent image cannot produce
// that shape — MEASURED by running it, not merely by reading it. confluentinc/cp-kafka:7.6.1 with
// an `SSL://` listener declared and no keystore delivered prints
//
//     ===> Configuring ...
//     SSL is enabled.
//     Command [/usr/local/bin/dub path /etc/kafka/secrets/kafka.keystore.jks exists] FAILED !
//
// and the container exits 1. So on the stock image a missing keystore is a container that never
// starts — an ordinary environment error, and the opposite of the silent downgrade EDGE-005
// describes. The spec attributes that downgrade to the customer's own entrypoint, which appends
// SSL configuration only if the keystore is present, and that is what this fixture replicates.
//
// HOW IT IS REPLICATED, through the authoring surface and nothing else. `environment.services`
// exposes `image`, `env`, `ports`, `healthCheck` and `security` — MEASURED against
// `ServiceSpec`: there is no `command` or `entrypoint` field, so the container's entrypoint
// cannot be overridden directly and the conditional has to be delivered as a FILE the image
// already runs. `security.serverArtifacts` delivers it, which makes the mechanism itself part of
// what these drills exercise rather than a fixture trick beside them.
//
// The file chosen is `/etc/confluent/docker/bash-config`, and the choice is measured rather than
// arbitrary:
//
//   • `run` (the image's entrypoint) SOURCES it — `. /etc/confluent/docker/bash-config` — as its
//     first statement, before `configure` translates KAFKA_* environment variables into
//     `/etc/kafka/kafka.properties`. So a replacement runs early enough to decide what that
//     translation sees.
//   • It is mode 644 in the image (`-rw-r--r--`, measured via `ls -la`), where `configure`,
//     `ensure` and `launch` are 755. A SOURCED file needs no execute bit, and an injected one does
//     not get one: `ServerArtifactInjection` sets `ContainerFile.Name`/`SourcePath` and no mode,
//     and the artefacts this fixture delivers arrive as `-rw-r--r--` (measured, in the capture
//     every row below records). Replacing any of the other three would therefore hand the image an
//     unexecutable entrypoint and break it for a reason that has nothing to do with either edge.
//
// The replacement keeps the image's own `set -o nounset -o errexit` and TRACE handling and adds
// one conditional: when the keystore exists at the path it checks, it exports the SSL listener
// configuration; when it does not, it exports nothing and the broker starts from the plaintext
// configuration the suite's `env:` block declared. That is the customer's shape, in one `if`.
//
// What is retained is FUNCTIONALLY IDENTICAL to the image's, not textually so, and the four
// differences are named here because a reader diffing this fixture against the image will meet
// them: (1) the image spells `set -o nounset \` across two continuation lines and this collapses
// it to one; (2) it likewise spells `set -o verbose \` across two, collapsed here into the
// single-line TRACE guard; (3) it writes `==` in that guard where this writes `=`; (4) it carries
// an Apache licence header and a comment about TRACE exposing credentials, both dropped. The
// auditability argument rests on functional fidelity — nothing about what the shell does with
// those lines differs — not on a byte match.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// IF EVERY ROW IN THIS FILE FAILS AT ONCE, SUSPECT THE SANDBOX BEFORE THE FIXTURE. A runner that
// cannot bind host ports fails every row during topology startup with `errorKind: Provision`,
// long before any assertion in this file is reached — which looks nothing like a drill failure
// and is not one. The discriminator is the error kind: these drills fail with
// `SecurityConfirmation`, and anything else means the topology never got far enough to be
// measured.
// ─────────────────────────────────────────────────────────────────────────────────────────────
//
// ONE MORE DETAIL, without which `configure` would defeat the whole arrangement. Its SSL branch
// triggers on the LITERAL substring `SSL://` in KAFKA_ADVERTISED_LISTENERS, and once triggered it
// insists on three files this fixture has no use for — the key store under a name it composes
// itself, plus a `key_creds` and a `keystore_creds` file each holding a PASSWORD, which unencrypted
// PEM key material does not have — and on two more (a trust store and a `truststore_creds`) as soon
// as `KAFKA_SSL_CLIENT_AUTH` is `required`, which this fixture sets. The secured listener is
// therefore NAMED `SECURE` and mapped to the SSL protocol through
// `listener.security.protocol.map` — the ordinary Kafka idiom for a named listener, and it contains
// no `SSL://` substring, so `configure` leaves the ssl.* properties this fixture exports through
// untouched.
//
// MEASURED against the real image before any of this was written into a test, both arms:
//   keystore at the checked path  → `listeners=PLAINTEXT://0.0.0.0:9092,SECURE://0.0.0.0:9093,
//                                    CONTROLLER://0.0.0.0:9094` in the generated kafka.properties,
//                                    and the broker's own log reports `Awaiting socket connections
//                                    on 0.0.0.0:9093`
//   keystore at a sibling path    → the container up, and only 9092 and 9094 listening
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// WHAT THE POSITIVE CONTROL CAN AND CANNOT CLAIM, stated here because it bounds every assertion
// below. The control proves the fixture's SSL listener genuinely works — the probe completes a
// TLS handshake against it under the declared anchor, exchanges Kafka ApiVersions over it, and
// (for `mtls`) confirms the broker REFUSES the same exchange from a connection presenting no
// client certificate. It does NOT reach a passing `mq-publish.kafka` step, and the reason is the
// limit KafkaServiceTargetDockerTests already records in full: a Kafka client resolves the
// partition leader from the broker's own `advertised.listeners`, a container-run broker cannot
// know the host-side port the orchestrator will allocate for it, and this release's language has
// no field for publishing a service endpoint on a predictable host port. MEASURED here, and it is
// positive evidence rather than an excuse: the step's own client spends its budget on
// `ssl://localhost:9093/1: Connect to ipv4#127.0.0.1:9093 failed` — the ADVERTISED address, which
// is not the staged one, so the broker answered the metadata request over the secured connection
// before the client went looking for a leader it cannot reach. So the control asserts that the run
// REACHED its step — a step-started event exists — which is precisely the differential the
// negatives are about, and asserts nothing about the step's own outcome.
//
// Run with: dotnet test --filter "requires=docker". Excluded from the unit-CI job.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.Kafka;
using Vouchfx.Steps.MqPublish.Kafka;
using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// EDGE-004 and EDGE-005 against a live <c>confluentinc/cp-kafka:7.6.1</c> broker, each paired
/// with the positive control that makes its negative mean something.
/// </summary>
public sealed class KafkaSecurityConfirmationDrillDockerTests
{
    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";

    /// <summary>The declared service name, and therefore the container-name prefix DCP allocates from.</summary>
    private const string BrokerName = "mtls-broker";

    /// <summary>The suite's publish step id, shared by the YAML and the CLI drills' output checks.</summary>
    private const string StepId = "publish";

    /// <summary>The consume step id, present only on REQ-025's produce-and-consume row.</summary>
    private const string ConsumeStepId = "consume";

    /// <summary>The in-container path the fixture's conditional entrypoint checks for the keystore.</summary>
    private const string CheckedKeystorePath = "/etc/kafka/secrets/kafka.keystore.pem";

    /// <summary>
    /// The plausible typo EDGE-005 turns on: <c>secret</c> for <c>secrets</c>. The host file
    /// exists and REQ-004's preflight passes; only the in-container destination is wrong.
    /// </summary>
    private const string TypoKeystorePath = "/etc/kafka/secret/kafka.keystore.pem";

    /// <summary>The prefix every row's suite directory shares, under the system temp directory.</summary>
    private const string SuiteDirectoryPrefix = "vouchfx-edge-drill-";

    /// <summary>
    /// Runs the stale-directory sweep EXACTLY ONCE for the whole class, however many rows execute.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A static initialiser, not the instance constructor, and the difference is not
    /// stylistic.</strong> xUnit constructs a NEW INSTANCE OF THE TEST CLASS PER TEST METHOD —
    /// measured, three methods yielding three constructor calls — so a sweep in the constructor
    /// runs before every row and deletes every <c>vouchfx-edge-drill-*</c> directory each time,
    /// including ones other rows own. A static field initialiser runs once, on first access,
    /// before any row.
    /// </para>
    /// <para>
    /// <strong>What was measured, and what was not.</strong> The per-method construction is
    /// measured. The specific failure it was proposed to explain — a row losing its own
    /// <c>client.pem</c> mid-run, surfacing as a probe abort at the client-identity-load arm — is
    /// NOT explained by it: the same measurement shows construction is strictly SEQUENTIAL (the
    /// next constructor ran 4ms after the previous method returned, never during it), and every row
    /// here materialises its own directory inside its own method. So the mechanism is refuted as
    /// stated, and the root cause of that intermittency is not established.
    /// </para>
    /// <para>
    /// This change is made anyway, and honestly labelled: the sweep is the only code in this
    /// repository that deletes these directories, an unexplained intermittent failure is exactly
    /// the shape a cross-row filesystem interaction takes, and a fixture whose rows are coupled
    /// through the filesystem is wrong on its own terms in a class destined for a proof pack.
    /// Running it once removes the coupling by construction rather than by argument.
    /// </para>
    /// </remarks>
    private static readonly bool s_swept = SweepStaleSuiteDirectoriesOnce();

    private readonly ITestOutputHelper _output;

    public KafkaSecurityConfirmationDrillDockerTests(ITestOutputHelper output)
    {
        _output = output;
        _ = s_swept;
    }

    /// <summary>
    /// Removes suite directories left by earlier RUNS, best-effort, once per process.
    /// </summary>
    /// <remarks>
    /// These directories are deliberately not deleted at the end of a row (see
    /// <see cref="MaterialiseSuiteDirectory"/>), which means the private keys inside them outlive
    /// the run that made them. Sweeping once at class init bounds that to the interval between two
    /// runs instead of forever, without breaking the property the retention exists for — a CLI
    /// drill naming the suite after the test that built it, within the same run.
    /// <para>
    /// The material is short-lived by construction (the generated CA and leaves are valid for two
    /// days and one day respectively), and the key files are written 0600 where the platform has
    /// POSIX permissions — so this is defence in depth on a test fixture, not the only control.
    /// </para>
    /// <para>
    /// Note the reach this acquired when the absent-material rows landed: those two are NOT
    /// docker-gated, so an ordinary unit run — the one every contributor and every CI job makes —
    /// now writes private keys into the system temp directory where before only a docker-gated run
    /// did. It grew again with the authorisation drills: <c>WriteKafkaBrokerSuiteDirectory</c>
    /// mints its second client identity UNCONDITIONALLY, so the same ordinary unit run now leaves
    /// FOUR private keys per suite directory — authority, server, client, unauthorised client —
    /// where the paragraph above was written for two. Judged acceptable on the same controls
    /// (throwaway per-run material, 0600 where the platform allows it, swept at the next class
    /// init), and both increments are recorded because each is a change in blast radius rather
    /// than a change in kind.
    /// </para>
    /// <para>
    /// Every failure is swallowed: a directory another process still holds open is not a reason to
    /// fail a drill, and each row recreates its own directory regardless.
    /// </para>
    /// </remarks>
    private static bool SweepStaleSuiteDirectoriesOnce()
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(
                         Path.GetTempPath(), SuiteDirectoryPrefix + "*"))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use, or not ours to remove.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The temp directory itself could not be enumerated.
        }

        // The value is meaningless; the static field exists only to make this run exactly once.
        return true;
    }

    private static readonly System.Reflection.Assembly[] s_providerAssemblies =
    {
        typeof(MqPublishKafkaProvider).Assembly,
        typeof(MqExpectKafkaProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The drills
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// EDGE-004: the suite names the PLAINTEXT port on a broker that is genuinely serving TLS on
    /// the other one. The run must abort at the probe, naming the port and the broker, with no
    /// step executed.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Edge004_EndpointNamesThePlaintextPortBesideAWorkingSslListener_AbortsBeforeAnyStep()
    {
        var drill = await RunDrillAsync(
            "edge-004-wrong-port", securedEndpoint: "9092", keystoreTarget: CheckedKeystorePath);

        // ── The verdict, and that it is REQ-018's carve-out rather than an ordinary env error ──
        Assert.Equal(Verdict.EnvironmentError, drill.Result.Verdict);
        Assert.True(
            drill.Result.Assurance.Unconfirmed,
            "a plaintext endpoint is a security-confirmation failure (REQ-018), not an ordinary "
            + "environment error — without this flag `vouchfx run` would exit 0.");

        // ── The abort names both halves an author needs to act on ─────────────────────────────
        Assert.Contains("on endpoint '9092'", drill.AbortDetail, StringComparison.Ordinal);
        Assert.Contains(BrokerName, drill.AbortDetail, StringComparison.Ordinal);

        // ── Zero steps executed ───────────────────────────────────────────────────────────────
        AssertNoStepRan(drill, NetworkArm.Connect | NetworkArm.Handshake);

        // ── The trap was real: the broker WAS serving TLS on the port the suite did not name ───
        // Without this the negative proves nothing — a broker with no SSL listener at all would
        // produce the same verdict for an entirely different reason (that is EDGE-005).
        AssertListening(drill.Evidence, 9092);
        AssertListening(drill.Evidence, 9093);
    }

    /// <summary>
    /// Asserts that a drill row aborted before executing anything — measured on the event buffer,
    /// which exists on this path: the probe's <c>OrchestrationException</c> is caught by
    /// <c>RunScenarioOwningTopologyAsync</c>, which buffers scenario-started, the environment-error
    /// line and scenario-completed. So "no step ran" is measured against a buffer that demonstrably
    /// contains events, never against an empty or absent one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The measurement that fixed this assertion, because it is a trap.</strong> The
    /// acceptance criteria for both edges are worded as "zero <c>step-attempt</c> events", and
    /// asserting only that would be VACUOUS for this suite: MEASURED, a
    /// <c>verifyMode: IMMEDIATE</c> step — the default, and what this suite's step is — emits NO
    /// <c>step-attempt</c> event even when it executes in full. That event type is built by
    /// <c>ScenarioRunner.BuildAttemptEventLines</c> from the <c>AttemptRecord</c> list the RETRY
    /// runner writes into <c>Vars</c>, so an IMMEDIATE step contributes none and the count is zero
    /// on BOTH sides of the differential. The positive control confirms this directly: it clears
    /// the probe, runs its step, and still reports zero <c>step-attempt</c> events.
    /// </para>
    /// <para>
    /// This is not an engine defect — §14 introduces the event as the RETRY polling timeline, and
    /// an IMMEDIATE step has no attempts to record — but it does mean the discriminating evidence
    /// is <c>step-started</c>/<c>step-completed</c>, which an executing step always emits. All
    /// three are asserted here so the criterion is met as written AND met by something that can
    /// actually fail.
    /// </para>
    /// </remarks>
    private static void AssertNoStepRan(DrillOutcome drill, NetworkArm expectedArm)
    {
        // The abort came from REQ-005's PROBE, not from the pre-topology security preflight. Worth
        // pinning separately, because the assurance reads UNCONFIRMED for BOTH — a preflight
        // rejection (a path that escapes the suite directory, a file that is not there) sets the
        // same flag — and the two edges are specifically about failures no pre-topology check can
        // see. `SecurityConfirmation` is OrchestrationErrorKind's own name for the probe's arm.
        Assert.Equal("SecurityConfirmation", drill.AbortKind);

        // …and from the network arm THIS row's edge belongs to, so TLS was actually attempted and
        // the row has not drifted into a different edge's shape. See AssertAbortedAtANetworkArm
        // for the five pre-network arms this rules out and why the arm set is per-row.
        AssertAbortedAtANetworkArm(drill.AbortDetail, expectedArm);

        Assert.NotEmpty(drill.Result.Buffer);
        Assert.Equal(0, CountEvents(drill.Result.Buffer, EventTypes.StepStarted));
        Assert.Equal(0, CountEvents(drill.Result.Buffer, EventTypes.StepCompleted));
        Assert.Equal(0, CountEvents(drill.Result.Buffer, EventTypes.StepAttempt));
    }

    /// <summary>
    /// EDGE-005: the keystore is delivered to a path the broker's startup logic does not check.
    /// The container must come up HEALTHY with no SSL listener, and the probe must still abort.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Edge005_KeystoreDeliveredToAnUncheckedPath_LeavesAHealthyBrokerAndStillAborts()
    {
        var drill = await RunDrillAsync(
            "edge-005-wrong-target", securedEndpoint: "9093", keystoreTarget: TypoKeystorePath);

        // ── The topology was HEALTHY — which is what makes this edge distinct ─────────────────
        // Reaching the probe at all is the proof: SuiteTopology runs it only after every declared
        // resource has passed WaitForResourceHealthyAsync. A container that failed to start, or a
        // health gate that timed out, produces an OrchestrationException of a different Kind and
        // never sets the security flag asserted below.
        Assert.True(
            drill.Result.Assurance.Unconfirmed,
            "the run must have reached and failed REQ-005's probe — a health-gate failure would "
            + "leave this flag false, and would mean this drill measured a broken container "
            + "rather than a healthy unsecured one.");
        Assert.Equal(Verdict.EnvironmentError, drill.Result.Verdict);

        Assert.Contains(BrokerName, drill.AbortDetail, StringComparison.Ordinal);
        Assert.Contains("on endpoint '9093'", drill.AbortDetail, StringComparison.Ordinal);

        // ── Zero steps executed ───────────────────────────────────────────────────────────────
        AssertNoStepRan(drill, NetworkArm.Connect | NetworkArm.Handshake);

        // ── The artefact landed where it was DECLARED, and not where the broker looks ─────────
        // Both halves matter: the first shows REQ-016 did its job faithfully (this is not a failed
        // copy), the second shows why REQ-004's host-side existence check cannot catch this.
        Assert.Contains("DECLARED-TARGET-PRESENT", drill.Evidence, StringComparison.Ordinal);
        Assert.Contains("CHECKED-PATH-ABSENT", drill.Evidence, StringComparison.Ordinal);

        // ── And the broker really came up with no SSL listener ────────────────────────────────
        AssertListening(drill.Evidence, 9092);
        AssertNotListening(drill.Evidence, 9093);
    }

    /// <summary>
    /// The declared client identity EXISTS but was issued by an unrelated authority. The existence
    /// check passes, the topology comes up, and the LIVE broker refuses the presented certificate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Only a real broker can produce this row, and only a broker that DEMANDS an
    /// identity.</strong> A peer that never asks for a certificate refuses nothing: against a
    /// broker with client auth off, this suite's foreign certificate would be IGNORED and the
    /// round trip would complete. The run would still not pass — the probe's anonymous-client
    /// differential would then also complete and it fails CLOSED, aborting with "TLS wearing the
    /// word 'mutual'" — but it would abort for the broker's misconfiguration, not for the
    /// identity, and this row would be measuring something other than what it claims. That is why
    /// the fixture's positive control asserts, on the same bed and the same trust store, that the
    /// broker refuses an anonymous client: it is what makes THIS row's abort attributable to the
    /// presented identity rather than to a broker that demands nothing.
    /// </para>
    /// <para>
    /// The suite is byte-identical to the positive control's. The only difference on disk is the
    /// CONTENT of the two client files.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task ClientIdentityFromAForeignAuthority_IsRefusedByTheBrokerBeforeAnyStep()
    {
        var drill = await RunDrillAsync(
            "foreign-client-identity",
            securedEndpoint: "9093",
            keystoreTarget: CheckedKeystorePath,
            afterMaterialise: TestCertificateAuthority.OverwriteWithForeignClientIdentity,

            // Gate on the SECURED port: this row's broker DOES serve 9093 (its keystore is at the
            // checked path) and only the client identity is wrong, so the same early-gate race
            // that flaked the positive control applies here.
            healthCheckPort: 9093);

        Assert.Equal(Verdict.EnvironmentError, drill.Result.Verdict);
        Assert.True(drill.Result.Assurance.Unconfirmed);
        Assert.Contains(BrokerName, drill.AbortDetail, StringComparison.Ordinal);
        Assert.Contains("on endpoint '9093'", drill.AbortDetail, StringComparison.Ordinal);

        AssertNoStepRan(drill, NetworkArm.RoundTrip);

        // ── THE DISCRIMINATOR FOR THIS EDGE, and it is what separates it from the other two ───
        // The other two negatives fail because TLS was ABSENT — a plaintext listener, or no
        // listener. This one must fail because TLS was PRESENT and the peer rejected who we said
        // we were. The probe reports exactly that shape: the handshake COMPLETED, and the broker
        // then declined to answer over it.
        //
        // The refusal half carries the discrimination on its own: "did not answer a Kafka
        // ApiVersions request" has exactly ONE throw site in the probe, and it sits after
        // AuthenticateAsClientAsync returned — so it is unreachable unless TLS was served. A
        // fixture whose SSL listener had disappeared produces the handshake-arm message instead,
        // which this can never match, and AssertListening(9093) below fails independently.
        //
        // The completion half is therefore BELT-AND-BRACES, not load-bearing: it adds no
        // discriminating power the line below lacks, and it is kept only because it states the
        // edge's shape in the assertion rather than only in this comment.
        Assert.Contains("completed", drill.AbortDetail, StringComparison.Ordinal);
        Assert.Contains(
            "did not answer a Kafka ApiVersions request",
            drill.AbortDetail,
            StringComparison.Ordinal);

        // The broker really was serving TLS on the port the suite named — the same listener the
        // positive control authenticates against.
        AssertListening(drill.Evidence, 9093);

        // ── AND THE BROKER'S OWN ACCOUNT OF IT ────────────────────────────────────────────────
        // Everything above is the engine reporting on itself. These two read the peer's record:
        // the broker refused a TLS peer at the certificate layer during this run, and it never
        // authenticated the foreign principal. Together they prove the decision was the BROKER's
        // and that it was taken AT PROBE TIME — a start-up capture could show neither — and they
        // hold whichever TLS version negotiated, where the engine-side arm above does not.
        AssertBrokerRefusedAPeer(drill.Evidence);
        AssertBrokerAuthenticated(
            drill.Evidence, TestCertificateAuthority.ForeignClientSubjectCommonName, expected: false);
    }

    /// <summary>
    /// The foreign-identity suite as a plain <c>vouchfx run</c> with no gating flags.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task ForeignClientIdentity_PlainVouchfxRunWithNoGatingFlags_ExitsWithTheSecurityConfirmationCode()
    {
        var cli = ResolveCliAssembly();
        var suiteDirectory = MaterialiseSuiteDirectory(
            "foreign-client-identity-cli",
            securedEndpoint: "9093",
            keystoreTarget: CheckedKeystorePath);
        TestCertificateAuthority.OverwriteWithForeignClientIdentity(suiteDirectory);

        var suite = Path.Combine(suiteDirectory, "drill.e2e.yaml");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var (exitCode, output) = await RunCliAsync(cli, "run", suite, cts.Token);

        _output.WriteLine($"exit code: {exitCode}");
        _output.WriteLine("── CLI output ──\n" + output);

        // 3, not the 4 the ABSENT variant exits with: this one reached a running topology and
        // aborted at the probe with EnvironmentError, where that one never built a topology and
        // stayed Inconclusive. Same carve-out, same flaglessness, different verdicts — so
        // different codes, each its own verdict's.
        Assert.Equal(3, exitCode);
        Assert.Contains("SecurityConfirmation", output, StringComparison.Ordinal);
        Assert.Contains(BrokerName, output, StringComparison.Ordinal);
        Assert.Contains("on endpoint '9093'", output, StringComparison.Ordinal);
        AssertAbortedAtANetworkArm(output, NetworkArm.RoundTrip);
        Assert.DoesNotContain($"step '{StepId}'", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// REQ-025's payoff: with the secured port's HOST side pinned and advertised, a Kafka producer
    /// AND consumer complete over mutual TLS from the engine host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the row every other Kafka row in this file could not be.</strong> Each of
    /// them asserts that a step was REACHED and says plainly that its outcome is not asserted,
    /// because it could not pass: the broker advertised <c>localhost:9093</c>, the engine host had
    /// nothing there, and the client followed the advertisement into a dead end. Pinning the host
    /// port is the one thing that changes, and the fixture is otherwise the positive control's.
    /// </para>
    /// <para>
    /// So the assertion is on the VERDICT, not on execution — a Pass for both steps, from a
    /// producer and a consumer that each negotiated mutual TLS, and the broker's own record naming
    /// the client it authenticated.
    /// </para>
    /// <para>
    /// The port is chosen at run time from a free-port probe rather than hard-coded: a hard-coded
    /// number that some other process on a developer's machine happens to hold would fail this row
    /// with EDGE-012's collision error, which is correct behaviour reported as a REQ-025 failure.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task PinnedAndAdvertisedHostPort_CompletesAProduceAndAConsumeOverMutualTls()
    {
        var pinned = ReserveAFreePort();
        _output.WriteLine($"pinning and advertising host port {pinned} for the secured listener");

        var drill = await RunDrillAsync(
            "pinned-produce-consume",
            securedEndpoint: "9093",
            keystoreTarget: CheckedKeystorePath,
            pinnedHostPort: pinned,
            consumeStep: true,

            // Gate on the SECURED port, not the plaintext one: this row's steps transact over
            // 9093, and a gate on 9092 can clear before 9093 is behind its proxy.
            healthCheckPort: 9093);

        // The probe confirmed, as on every correctly-configured row.
        Assert.False(
            drill.Result.Assurance.Unconfirmed,
            "the probe did not confirm; diagnostics: " + drill.Diagnostics);

        // THE PAYOFF: both steps passed. Not "were reached" — passed.
        Assert.Equal(Verdict.Pass, drill.Result.Verdict);
        Assert.Equal(2, CountEvents(drill.Result.Buffer, EventTypes.StepCompleted));
        Assert.DoesNotContain(
            "\"verdict\":\"FAIL\"", string.Join("\n", drill.Result.Buffer), StringComparison.Ordinal);

        // And the broker authenticated the declared identity by name for that traffic — the same
        // evidence the transport rows use, now covering a run that actually moved a message.
        AssertBrokerAuthenticated(
            drill.Evidence, TestCertificateAuthority.ClientSubjectCommonName, expected: true);
    }

    /// <summary>
    /// Finds a host port nothing is currently listening on, by binding port 0 and releasing it.
    /// </summary>
    /// <remarks>
    /// Binds the ANY address, not loopback. The engine's own pinned-port pre-flight — which THIS
    /// row runs, unlike its sibling in the orchestration tests — probes the any-address on every
    /// platform, and the two are independent on Windows: a port free on <c>127.0.0.1</c> can be
    /// held on <c>0.0.0.0</c>, so a loopback-derived port can be one the pre-flight then refuses.
    /// That would surface as a collision failure inside the row that exists to prove pinning
    /// WORKS. Measured across three full class runs (33 row-executions) without a recurrence, but
    /// the sample bounds the risk rather than removing it, which is why the picker changed.
    /// </remarks>
    private static int ReserveAFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// The positive control for BOTH edges: the same fixture with the endpoint naming the secured
    /// port AND the keystore landing at the checked path. The probe must confirm, and the run must
    /// reach its step.
    /// </summary>
    /// <remarks>
    /// One control serves both drills because each edge's "correct" pole is the same point: the
    /// two variables are independent, and everything-correct is where both negatives' single
    /// changed variable returns to. Running it twice would start a second identical topology and
    /// measure nothing the first did not.
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task PositiveControl_SecuredEndpointAndKeystoreAtTheCheckedPath_ConfirmsAndReachesItsStep()
    {
        var drill = await RunDrillAsync(
            "positive-control",
            securedEndpoint: "9093",
            keystoreTarget: CheckedKeystorePath,

            // Gate on the SECURED port. This row's whole premise is that 9093 is serving, and a
            // gate on 9092 clears before that is true — the measured cause of the intermittent
            // "no backend is listening behind the host-published proxy" failures.
            healthCheckPort: 9093);

        // ── The probe confirmed ───────────────────────────────────────────────────────────────
        Assert.False(
            drill.Result.Assurance.Unconfirmed,
            "the control must clear REQ-005's probe — if it does not, the negatives above are "
            + "measuring a broken fixture rather than the edges they name. Diagnostics: "
            + drill.Diagnostics);

        // ── …and confirmed at the STRONGEST level the feature defines ─────────────────────────
        // Not merely "no failure": the probe completed a Kafka ApiVersions exchange over the TLS
        // session AND was refused when it repeated that exchange presenting no client certificate.
        // The second half is what makes this a control for the fixture rather than for the probe —
        // a broker whose keystore landed correctly but whose KAFKA_SSL_CLIENT_AUTH was unset would
        // serve TLS, answer the round trip and refuse nothing, and this assertion would fail.
        //
        // MATCHED ON THE LEVEL TOKEN *AND* ON THE SENTENCE, which is a change from the drill as
        // first written and worth recording, because the reason it matched the sentence ALONE has
        // been removed. That reason was: the enum value is not reachable from here (ScenarioCoreResult
        // carries a verdict, a buffer and the security flag and no confirmations at all, and reaching
        // SuiteTopology.SecurityConfirmations would mean the test owning the topology instead of
        // running the scenario this control is about) AND SecurityConfirmation.ToString() omitted the
        // Level entirely. The second half stopped being true in slice F's peer-review fix: the
        // rendered line now carries `level AuthenticatedRoundTrip` immediately after the
        // client-identity clause. The symbol is still unreachable; its NAME now is.
        //
        // Both are asserted, and they are not redundant. The token pins the LEVEL — the thing REQ-005
        // introduced named levels to make matchable, and the thing a CI gate would key on. The
        // sentence pins WHICH BRANCH produced it: only the mtls arm that actually ran the anonymous
        // differential emits that text, so it is what separates "the strong level" from "the strong
        // level for the right reason". A rewording of the Detail would leave the token standing, and
        // a level dropped from the rendering would leave the sentence standing; this drill wants
        // neither to pass alone.
        Assert.Contains("level AuthenticatedRoundTrip", drill.Diagnostics, StringComparison.Ordinal);
        Assert.Contains(
            "REFUSED the same request on a second connection presenting no client certificate",
            drill.Diagnostics,
            StringComparison.Ordinal);

        // ── The handshake itself: a TLS session was negotiated and the declared identity was
        //    CONFIGURED ON it. The sentence above is about the ROUND TRIP; these two fragments are
        //    about the HANDSHAKE, and they are separate facts. A confirmation reporting
        //    `observed None` or `client identity none declared` would still be a confirmation —
        //    just not this one.
        //
        //    BELT-AND-BRACES: both are entailed by the round-trip sentence above, which only the
        //    mtls AuthenticatedRoundTrip branch emits and which is unreachable without a completed
        //    handshake and a resolved identity. Kept because they name the two facts separately,
        //    so a future rewording of that one sentence cannot quietly drop either.
        //
        //    And note what `client identity resolved` does NOT say: it reports that a certificate
        //    was RESOLVED and configured on the handshake, never that the platform put it on the
        //    wire — measured on the foreign-identity row, where SChannel presented nothing and this
        //    same fragment would still have been printed. The broker-side assertion at the end of
        //    this test is what closes that gap.
        Assert.Contains("; observed Tls", drill.Diagnostics, StringComparison.Ordinal);
        Assert.Contains(", client identity resolved", drill.Diagnostics, StringComparison.Ordinal);

        // ── …and it was validated against the anchor the SUITE declared, not the platform's.
        //    Asserted on the suite text because the declaration is what carries it and no run
        //    output names it.
        //
        //    The anchor is load-bearing, and the mechanism is what makes it so: with no `caCert`
        //    declared the probe installs NO validation callback and the platform's own verdict
        //    stands — and this bed's server certificate is issued by a per-run private CA no
        //    platform trust store has ever heard of, so that verdict is a rejection. Drop the
        //    field and AuthenticateAsClientAsync throws: no handshake, no confirmation line, no
        //    step, and the security flag set. Nearly every other assertion in this test breaks
        //    with it. The negative of this control is not a different message — it is no
        //    handshake, which is also why a handshake that completes AT ALL is one the declared
        //    file validated.
        //
        //    So this assertion is a CHANGE DETECTOR on the entailment's premise, not independent
        //    evidence: it does not prove the chain was checked — the completed handshake does that
        //    — it fails loudly if someone removes the field the entailment rests on.
        Assert.Contains(
            $"caCert: {TestCertificateAuthority.CaFileName}",
            File.ReadAllText(Path.Combine(drill.SuiteDirectory, "drill.e2e.yaml")),
            StringComparison.Ordinal);

        // ── And the run reached its step, which is exactly what the negatives did not ─────────
        // The step's own outcome is NOT asserted: see this file's header for the advertised-listener
        // limit that bounds it. What is asserted is the differential the drills are about — and it
        // is asserted on step-started rather than step-attempt, because an IMMEDIATE step emits no
        // step-attempt event at all (see AssertNoStepRan's remarks for that measurement).
        Assert.True(
            CountEvents(drill.Result.Buffer, EventTypes.StepStarted) >= 1,
            "expected at least one step-started event once the probe confirmed; buffer:\n"
            + string.Join("\n", drill.Result.Buffer));

        // The step ran IN FULL and still emitted no step-attempt event — which is what makes
        // AssertNoStepRan's step-attempt assertion honest about being vacuous on its own, and is
        // asserted rather than merely asserted-in-prose. An IMMEDIATE step has no attempt records
        // (see that method's remarks); if this ever becomes non-zero, the negatives' step-attempt
        // count has become load-bearing and their comment needs revisiting.
        Assert.Equal(0, CountEvents(drill.Result.Buffer, EventTypes.StepAttempt));

        // ── Both listeners were up, so the fixture is the EDGE-004 shape with the right port ──
        AssertListening(drill.Evidence, 9092);
        AssertListening(drill.Evidence, 9093);

        // ── The BROKER's own account: it authenticated the declared client identity by name. ──
        // This is the assertion that makes "the handshake completed presenting the declared
        // identity" a fact about the PEER rather than a fact about what this side configured — and
        // measured, it is the one thing SslStream cannot tell you: on the identity row above the
        // platform silently presented nothing, and reported the same `client identity resolved`
        // this row does. Only the broker knows who it authenticated.
        AssertBrokerAuthenticated(
            drill.Evidence, TestCertificateAuthority.ClientSubjectCommonName, expected: true);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The two transport negative controls: the declared client identity ABSENT, and PRESENT BUT
    // ISSUED BY SOMEONE ELSE. Neither may let the passing suite pass, and each is caught at a
    // different stage — which is the whole point of running both.
    //
    //   ABSENT           → the host-side preflight existence check, at VALIDATION time. No
    //                      container is ever created, so this variant needs no Docker at all.
    //   PRESENT-INVALID  → the existence check passes; the LIVE broker refuses the identity during
    //                      the probe's own exchange. Only a running broker can produce this.
    //
    // Together they say: the engine refuses to proceed at all when it cannot authenticate at the
    // transport layer, whichever way the material is wrong.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The declared <c>clientCert</c> file is deleted. The suite must be refused before any
    /// topology work, and no container may be created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Driven through <c>RunSuiteAsync</c>, not the per-scenario core the docker rows
    /// use</strong> — because that is the seam the CLI's own <c>run</c> takes, and this variant is
    /// entirely about what happens BEFORE a topology exists. It is also the seam carrying the
    /// all-early-verdict guard: when every scenario has a pre-topology verdict, the suite completes
    /// without the topology being built at all.
    /// </para>
    /// <para>
    /// <strong>NOT docker-gated, and that is the claim.</strong> This row is in the unit suite
    /// because a correct engine never reaches Docker here. A regression that started a topology is
    /// caught by the VERDICT assertion, not by the container snapshot: THIS suite is a single
    /// scenario whose only verdict is a pre-topology one, so it takes <c>RunSuiteAsync</c>'s
    /// all-early-verdict guard into <c>CompleteWithoutTopologyAsync</c>, and an engine that built a
    /// topology instead would have to reach the probe, whose failures are <c>EnvironmentError</c>
    /// rather than the <c>Inconclusive</c> asserted below. The snapshot cannot catch it — teardown
    /// removes the container before the run returns (measured, and recorded at the container
    /// watcher), so a regressed engine that built, probed, failed and tore down leaves before and
    /// after equal.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AbsentClientCertificate_IsRefusedByThePreflightWithNoTopologyWork()
    {
        var suiteDirectory = MaterialiseSuiteDirectory(
            "absent-client-cert", securedEndpoint: "9093", keystoreTarget: CheckedKeystorePath);

        // The ONE difference from the passing suite: its declared client certificate is gone.
        File.Delete(Path.Combine(suiteDirectory, TestCertificateAuthority.ClientCertFileName));

        var yaml = File.ReadAllText(Path.Combine(suiteDirectory, "drill.e2e.yaml"));
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), s_registry);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var containersBefore = await ListBrokerContainersAsync(cts.Token);

        var scenarioNames = new List<string> { "absent-client-cert" };
        var diagnostics = new StringWriter();
        var result = await ScenarioRunner.RunSuiteAsync(
            new[] { ast },
            scenarioNames,
            new[] { yaml },
            s_providerAssemblies,
            AppHostAssemblyName,
            diagnostics,
            seedBaseDirectory: suiteDirectory,
            scenarioBaseDirectories: new string?[] { suiteDirectory },
            cancellationToken: cts.Token);

        var containersAfter = await ListBrokerContainersAsync(cts.Token);
        var output = diagnostics.ToString();
        _output.WriteLine($"verdict={result.Verdict} securityUnconfirmed={result.Assurance.Unconfirmed} refusal={result.Assurance.Refusal}");
        _output.WriteLine("── diagnostics ──\n" + output);

        // ── The classification: a pre-topology security rejection ─────────────────────────────
        // Inconclusive, NOT EnvironmentError: the scenario never ran, and an authoring error is not
        // an infrastructure fault. The flag is what lifts it off exit 0 all the same.
        Assert.Equal(Verdict.Inconclusive, result.Verdict);
        Assert.True(
            result.Assurance.Unconfirmed,
            "a declared-but-missing client certificate is a security rejection, so the flagless "
            + "run must not exit 0 — see this test's exit-code derivation below.");

        // ── NO TOPOLOGY WORK, argued structurally and then corroborated ───────────────────────
        // The structural half is the stronger one, and it is scoped to THIS suite's shape rather
        // than asserted as an engine invariant. "Every Inconclusive-with-the-flag result is
        // pre-topology" is FALSE in general: a multi-scenario suite that builds a topology and
        // completes normally still carries the flag forward from a scenario that failed preflight,
        // and the parallel runner aggregates it the same way. What holds here is narrower and
        // sufficient: this is a SINGLE scenario whose one verdict is pre-topology, so RunSuiteAsync
        // takes its all-early-verdict guard and returns through CompleteWithoutTopologyAsync
        // without building anything — and the only door that reaches a running topology, the
        // confirmation probe, yields EnvironmentError rather than the Inconclusive asserted above.
        // The message below says which pre-topology door it came through.
        //
        // The general form of that claim has now been wrong three times on this branch, in this
        // file and in the engine's own comments; it is written scoped here for that reason.
        Assert.Contains("clientCert", output, StringComparison.Ordinal);
        Assert.Contains(
            $"file '{TestCertificateAuthority.ClientCertFileName}' not found",
            output,
            StringComparison.Ordinal);

        // The corroborating half, and it is WEAKER than it looks in three ways worth naming: on a
        // host with no Docker at all both snapshots are empty and this compares nothing; even with
        // Docker it cannot see a topology built and torn down inside the call (see this test's own
        // remarks); and it reads GLOBAL docker state, which this row does not own.
        //
        // So it asserts only that no broker container APPEARED — never that the two lists are
        // equal. MEASURED: equality fails when a PREVIOUS row's teardown completes during this
        // one, which removes a container between the two snapshots and is not this row's doing at
        // all. A row that fails because a sibling finished tidying up is order-coupled by
        // construction, and a subset check is the assertion that actually matches the claim.
        AssertNoBrokerContainerAppeared(containersBefore, containersAfter);
    }

    /// <summary>
    /// The same absent-material suite as a plain <c>vouchfx run</c> with no gating flags, and as
    /// <c>vouchfx validate</c> — the two invocations an author actually makes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The expected code is DERIVED, not assumed, and the derivation is a committed
    /// measurement rather than an argument made here.</strong> This door produces
    /// <c>Verdict.Inconclusive</c> with the security signal set (asserted by the row above), and
    /// <c>Vouchfx.Cli.Tests.SecurityConfirmationExitCodeTests.
    /// FromVerdict_SecurityPreflightRejection_ExitsInconclusiveWithoutTheFlag</c> pins
    /// <c>ExitCodes.FromVerdict(Verdict.Inconclusive, failOnEnvironmentError: false,
    /// failOnInconclusive: false, an UNCONFIRMED SecurityAssurance) == ExitCodes.Inconclusive</c>.
    /// So the code is <b>4</b>, and it is NOT the 3 the two probe-failure drills in this file
    /// expect: those abort with <c>EnvironmentError</c> from a running topology, this one never
    /// builds a topology at all. Two security rejections, two different codes, each keeping the
    /// code its own verdict names — which is exactly the property the carve-out was written to
    /// preserve, and the reason assuming 3 here would have been wrong.
    /// </para>
    /// <para>
    /// Not docker-gated, for the same reason as the row above.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AbsentClientCertificate_PlainVouchfxRunAndValidate_BothRefuseWithTheInconclusiveCode()
    {
        var cli = ResolveCliAssembly();
        var suiteDirectory = MaterialiseSuiteDirectory(
            "absent-client-cert-cli", securedEndpoint: "9093", keystoreTarget: CheckedKeystorePath);
        File.Delete(Path.Combine(suiteDirectory, TestCertificateAuthority.ClientCertFileName));

        var suite = Path.Combine(suiteDirectory, "drill.e2e.yaml");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var containersBefore = await ListBrokerContainersAsync(cts.Token);

        var (runExit, runOutput) = await RunCliAsync(cli, "run", suite, cts.Token);
        _output.WriteLine($"`run` exit code: {runExit}");
        _output.WriteLine("── run output ──\n" + runOutput);

        Assert.Equal(4, runExit);
        Assert.Contains("clientCert", runOutput, StringComparison.Ordinal);
        Assert.Contains(
            $"file '{TestCertificateAuthority.ClientCertFileName}' not found",
            runOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain($"step '{StepId}'", runOutput, StringComparison.Ordinal);

        // The author-facing half: the same fault is reachable without running anything, which is
        // what makes it a VALIDATION-time check rather than a run-time one.
        var (validateExit, validateOutput) = await RunCliAsync(cli, "validate", suite, cts.Token);
        _output.WriteLine($"`validate` exit code: {validateExit}");
        _output.WriteLine("── validate output ──\n" + validateOutput);

        // 4, asserted exactly: `validate`'s code for an invalid document is deterministic, and this
        // test's own name promises a number. It is NOT the carve-out's doing — `validate` never
        // reaches a verdict to carve out of; the two invocations agreeing on 4 here is the
        // taxonomy's Inconclusive code being reached by two different routes.
        Assert.Equal(4, validateExit);
        Assert.Contains(
            $"file '{TestCertificateAuthority.ClientCertFileName}' not found",
            validateOutput,
            StringComparison.Ordinal);

        var containersAfter = await ListBrokerContainersAsync(cts.Token);
        AssertNoBrokerContainerAppeared(containersBefore, containersAfter);
    }

    /// <summary>
    /// Asserts no broker container APPEARED between two snapshots — deliberately a subset check
    /// rather than equality.
    /// </summary>
    /// <remarks>
    /// These rows read global docker state they do not own. A container VANISHING between the two
    /// snapshots is a previous row's teardown completing, which says nothing about this row;
    /// measured, that is exactly what failed an equality assertion here, repeatedly, in a class
    /// run. Only an ARRIVAL is evidence about the row under test, so only an arrival fails.
    /// </remarks>
    private static void AssertNoBrokerContainerAppeared(
        IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var appeared = after.Except(before, StringComparer.Ordinal).ToArray();

        Assert.True(
            appeared.Length == 0,
            "a broker container appeared during a row that must never start one: "
            + string.Join(", ", appeared));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The CLI drills — REQ-018's acceptance is about a PROCESS EXIT CODE, so it is measured on one
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// EDGE-004 as its acceptance criterion is actually worded: a plain <c>vouchfx run</c> with no
    /// gating flags, exiting non-zero.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public Task Edge004_PlainVouchfxRunWithNoGatingFlags_ExitsWithTheSecurityConfirmationCode() =>
        AssertFlaglessCliRunAbortsWithExitCodeThreeAsync(
            "edge-004-wrong-port-cli", securedEndpoint: "9092", keystoreTarget: CheckedKeystorePath);

    /// <summary>
    /// EDGE-005 as its acceptance criterion is actually worded: the same flagless invocation
    /// against a broker that came up healthy and unsecured.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public Task Edge005_PlainVouchfxRunWithNoGatingFlags_ExitsWithTheSecurityConfirmationCode() =>
        AssertFlaglessCliRunAbortsWithExitCodeThreeAsync(
            "edge-005-wrong-target-cli", securedEndpoint: "9093", keystoreTarget: TypoKeystorePath);

    /// <summary>
    /// The CLI positive control, and the row that makes the two above mean something: the same
    /// flagless invocation against the same fixture with both variables correct exits <b>0</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this row, "a security-confirmation failure exits 3" is unfalsifiable from the CLI's
    /// side — every run of this suite could exit 3 for any reason and both drills above would still
    /// pass. This row is what makes the carve-out NARROW rather than universal, and it makes the
    /// point more sharply than a passing run could: this scenario does not pass either. Its step
    /// runs out of its declared budget (the advertised-listener limit in this file's header), so
    /// the suite verdict is <c>Inconclusive</c> — and a flagless run still exits 0, because an
    /// Inconclusive that is not a security-confirmation failure does not gate CI. Two non-passing
    /// runs, one exiting 3 and one exiting 0, separated by exactly the thing REQ-018 keys on.
    /// </para>
    /// <para>
    /// It is also the most expensive drill in this file (~50 s, against ~20 s for every other row,
    /// because it is the only one that reaches a step at all). If the docker set ever has to be
    /// trimmed for time, this is the row to drop FIRST — its claim degrades to
    /// <c>ExitCodesTests.FromVerdict_MapsPerTaxonomy</c>'s existing
    /// <c>(EnvironmentError, false, false) → Success</c> case, which pins the same narrowness one
    /// layer down and costs no container at all.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task PositiveControl_PlainVouchfxRunWithNoGatingFlags_ExitsZero()
    {
        var cli = ResolveCliAssembly();
        var suiteDirectory = MaterialiseSuiteDirectory(
            "positive-control-cli", securedEndpoint: "9093", keystoreTarget: CheckedKeystorePath);
        var suite = Path.Combine(suiteDirectory, "drill.e2e.yaml");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var (exitCode, output) = await RunCliAsync(cli, "run", suite, cts.Token);

        _output.WriteLine($"exit code: {exitCode}");
        _output.WriteLine("── CLI output ──\n" + output);

        Assert.Equal(0, exitCode);

        // And it got there the intended way: the probe confirmed, and the step ran. Asserted so a
        // future change that made this exit 0 by never reaching the probe — the failure mode this
        // whole file exists to catch — cannot pass as a green control.
        Assert.Contains("security: service '" + BrokerName + "'", output, StringComparison.Ordinal);
        Assert.Contains($"step '{StepId}'", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs the built CLI as a subprocess against one drill row's suite, with NO
    /// <c>--fail-on-env-error</c> and no <c>--fail-on-inconclusive</c>, and asserts the integer
    /// exit code and the abort line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why a subprocess at all, when the in-process drills already assert the verdict and
    /// the security flag.</strong> Those two facts plus <c>ExitCodes.FromVerdict</c>'s own
    /// parameterised unit test compose to exit 3, but composition is not the criterion:
    /// REQ-018's and both edges' acceptance name a flagless <c>vouchfx run &lt;suite&gt;</c> and a
    /// PROCESS exit code, and nothing in an in-process run ever calls the CLI's exit-code
    /// decision. This is the only place the criterion is measured as written.
    /// </para>
    /// <para>
    /// <strong>Which invocation, decided by measurement rather than by preference.</strong> Two
    /// candidates, run back to back against the same EDGE-004 suite on this host:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>dotnet &lt;built&gt;/vouchfx.dll run &lt;suite&gt;</c> — exit 3 in <strong>28.0 s</strong>.
    ///   </description></item>
    ///   <item><description>
    ///     <c>dotnet run --project src/Cli/Vouchfx.Cli -c Release -- run &lt;suite&gt;</c> — exit 3
    ///     in <strong>46.5 s</strong>.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The built assembly is taken: it is 18.5 s cheaper per row, and it runs no MSBuild inside a
    /// test — so there is no build to contend with the test host, no <c>-c</c> to keep in step with
    /// the configuration the test itself was built in, and no reason to reach for
    /// <c>MSBUILDDISABLENODEREUSE</c>. The cost is that the artefact must already exist, which
    /// <see cref="ResolveCliAssembly"/> turns into a named failure rather than a skip.
    /// </para>
    /// </remarks>
    private async Task AssertFlaglessCliRunAbortsWithExitCodeThreeAsync(
        string row, string securedEndpoint, string keystoreTarget)
    {
        var cli = ResolveCliAssembly();
        var suiteDirectory = MaterialiseSuiteDirectory(row, securedEndpoint, keystoreTarget);
        var suite = Path.Combine(suiteDirectory, "drill.e2e.yaml");
        _output.WriteLine($"row '{row}': {cli} run {suite}");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var (exitCode, output) = await RunCliAsync(cli, "run", suite, cts.Token);

        _output.WriteLine($"exit code: {exitCode}");
        _output.WriteLine("── CLI output ──\n" + output);

        // The exit code REQ-018 specifies. Spelled as a literal because `ExitCodes` is internal to
        // the CLI assembly, which this one does not reference; 3 is `ExitCodes.EnvironmentError`,
        // the taxonomy's own code for the verdict — the carve-out changes WHETHER that code is
        // returned without a gating flag, never WHICH code it is.
        Assert.Equal(3, exitCode);

        // The abort reached the operator, naming what to fix. These are the POSITIVE anchors that
        // stop the absence assertion below from passing for the wrong reason: a CLI that fell over
        // before it reached the probe could not print any of them.
        Assert.Contains("SecurityConfirmation", output, StringComparison.Ordinal);
        Assert.Contains(BrokerName, output, StringComparison.Ordinal);
        Assert.Contains($"on endpoint '{securedEndpoint}'", output, StringComparison.Ordinal);

        // And the abort came from a network arm, so TLS was actually attempted rather than the run
        // failing at one of the probe's five pre-socket arms with the same classified kind — see
        // AssertAbortedAtANetworkArm. Applied to the whole output here, where the in-process rows
        // apply it to the decoded event detail: this path renders the message to the terminal
        // unescaped, so the phrase appears literally.
        AssertAbortedAtANetworkArm(output, NetworkArm.Connect | NetworkArm.Handshake);

        // And no step ran. Matched on the terminal renderer's own step line rather than on the bare
        // step id, because the id `publish` also occurs inside the phrase "host-published proxy" in
        // an unrelated health-check diagnostic — measured, three occurrences per run, and a naive
        // substring search reads them as steps.
        Assert.DoesNotContain($"step '{StepId}'", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Locates the built CLI assembly beside this test assembly's own configuration.
    /// </summary>
    /// <remarks>
    /// The configuration is read from this assembly's own output path rather than assumed, so a
    /// Debug test run drives the Debug CLI. A missing artefact FAILS with the command that
    /// produces it — never skips: a silently-skipped drill is indistinguishable from a passing one,
    /// and this is the only test that measures REQ-018's stated criterion. CI cannot reach that
    /// failure (the integration job runs `dotnet build vouchfx.sln -c Release` before any
    /// docker-gated test), so it is a local-run guard.
    /// </remarks>
    private static string ResolveCliAssembly()
    {
        var assemblyDirectory = Path.GetDirectoryName(
            typeof(KafkaSecurityConfirmationDrillDockerTests).Assembly.Location)!;

        // …/tests/<project>/bin/<configuration>/net8.0 — the configuration is the grandparent's name.
        var configuration = Path.GetFileName(Path.GetDirectoryName(assemblyDirectory))!;

        // Walk up: net8.0 → <configuration> → bin → <project> → tests → repo root. The same shape
        // ExamplesCompileTests.ResolveRepoRoot and Sprint11ReferenceCompileTests use.
        var repoRoot = Path.GetFullPath(
            Path.Combine(assemblyDirectory, "..", "..", "..", "..", ".."));

        var cli = Path.Combine(
            repoRoot, "src", "Cli", "Vouchfx.Cli", "bin", configuration, "net8.0", "vouchfx.dll");

        Assert.True(
            File.Exists(cli),
            $"The built CLI was not found at '{cli}'. This drill runs the CLI as a subprocess "
            + "because REQ-018's acceptance is about a process exit code. Build the solution first: "
            + $"dotnet build vouchfx.sln -c {configuration}");

        return cli;
    }

    /// <summary>
    /// Runs <c>dotnet &lt;cli&gt; run &lt;suite&gt;</c> to completion and returns its integer exit
    /// code with stdout and stderr interleaved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both streams are drained CONCURRENTLY and awaited before <c>WaitForExit</c>. Sequential
    /// draining deadlocks a chatty child: this one writes Aspire, DCP and librdkafka diagnostics to
    /// stderr while stdout is still being read, and a full pipe buffer would block it forever.
    /// </para>
    /// <para>
    /// <strong>The child is killed on any exit path, tree and all.</strong> <c>using</c> disposes
    /// the <see cref="Process"/> OBJECT and never the process, so a cancelled or faulted await
    /// would leave a CLI running — and this particular child owns a live Aspire topology, so an
    /// orphan holds containers and an <c>aspire-session-network-*</c> open with nothing left to
    /// tear them down. That is the §4.5 teardown failure arriving through the test harness instead
    /// of through the engine. <c>entireProcessTree: true</c> because the CLI is itself a parent:
    /// DCP runs as its child.
    /// </para>
    /// </remarks>
    private static async Task<(int ExitCode, string Output)> RunCliAsync(
        string cliAssembly, string verb, string suitePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,

            // Set explicitly rather than inherited from the test host, so the run does not depend
            // on where the runner happened to be started from.
            WorkingDirectory = Path.GetDirectoryName(cliAssembly)!,
        };

        // NO --fail-on-env-error and NO --fail-on-inconclusive: the flagless invocation is the
        // whole of what the security-confirmation carve-out and both edges specify.
        startInfo.ArgumentList.Add(cliAssembly);
        startInfo.ArgumentList.Add(verb);
        startInfo.ArgumentList.Add(suitePath);

        using var process = Process.Start(startInfo)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return (process.ExitCode, await stdout + "\n" + await stderr);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                           or System.ComponentModel.Win32Exception
                                           or NotSupportedException)
            {
                // The process ended between HasExited and Kill, or the platform refused the kill.
                // Nothing further this fixture can do, and throwing here would replace the real
                // failure with a teardown one.
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // The one fixture every drill shares
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What one drill row produced.</summary>
    /// <param name="Result">The verdict, the event buffer and REQ-018's security signal.</param>
    /// <param name="Diagnostics">
    /// Everything the run wrote to its own <see cref="TextWriter"/> — which on a CONFIRMED run is
    /// where REQ-005's declared-versus-observed report lands, and on an aborted one is empty
    /// (measured: the abort travels as an <c>environment-error</c> EVENT, not as writer output).
    /// </param>
    /// <param name="Evidence">The container capture.</param>
    /// <param name="SuiteDirectory">The materialised suite this row ran, retained so a test can
    /// assert on the declaration itself and not only on the run's output.</param>
    private sealed record DrillOutcome(
        ScenarioCoreResult Result, string Diagnostics, string Evidence, string SuiteDirectory)
    {
        /// <summary>
        /// The <c>environment-error</c> event line, which is where the probe's abort message
        /// actually travels — empty when the run raised none.
        /// </summary>
        /// <remarks>
        /// Measured, and it corrected a first draft that asserted the abort text against
        /// <see cref="Diagnostics"/>: on the probe-failure path that writer receives NOTHING.
        /// <c>RunScenarioOwningTopologyAsync</c> catches the <c>OrchestrationException</c> and
        /// buffers the message as this event's <c>detail</c> field, which is also the form every
        /// §14 renderer and the Healer agent read — so asserting here checks the text an operator
        /// is actually shown.
        /// </remarks>
        public string AbortEvent =>
            Result.Buffer.FirstOrDefault(
                line => line.Contains(
                    $"\"type\":\"{EventTypes.EnvironmentError}\"", StringComparison.Ordinal))
            ?? string.Empty;

        /// <summary>The abort event's classified <c>errorKind</c>, or empty when there is none.</summary>
        public string AbortKind => ReadAbortProperty("errorKind");

        /// <summary>
        /// The abort event's <c>detail</c> — the probe's own message, JSON-DECODED.
        /// </summary>
        /// <remarks>
        /// Read through <see cref="JsonDocument"/> rather than matched as a substring of the raw
        /// line, and the reason is measured rather than stylistic: the serialiser escapes the
        /// message's apostrophes, so the line literally reads
        /// <c>on endpoint '9093'</c>. A substring assertion for <c>on endpoint '9093'</c>
        /// can never match it, and an assertion for the bare <c>9093</c> — which is what an earlier
        /// draft settled for — also matches a port that merely happens to appear in the observed
        /// address. Decoding first is what lets the tests assert the phrase an operator reads.
        /// </remarks>
        public string AbortDetail => ReadAbortProperty("detail");

        private string ReadAbortProperty(string name)
        {
            if (AbortEvent.Length == 0)
            {
                return string.Empty;
            }

            using var document = JsonDocument.Parse(AbortEvent);
            return document.RootElement.TryGetProperty(name, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
    }

    /// <summary>
    /// Asserts that an abort message came from one of the probe's two NETWORK arms — it either
    /// failed to connect, or failed the TLS handshake.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this is not covered by <c>errorKind</c> plus the target name.</strong> Five
    /// arms of <c>SecuredEndpointProbe.ConfirmOneAsync</c> raise the identical
    /// <c>SecurityConfirmation</c> kind, naming the same target and endpoint, BEFORE a socket is
    /// ever opened: an unrecognised profile, a target the topology staged no address for, a client
    /// identity that would not load, a declared <c>mtls</c> with no resolved <c>clientCert</c>/
    /// <c>clientKey</c> pair, and a trust anchor that would not load. A regression in endpoint
    /// staging would therefore turn EDGE-005 green with ZERO TLS measured — and the container
    /// evidence would not catch it either, because that evidence is about the broker and says
    /// nothing about what the probe did.
    /// </para>
    /// <para>
    /// <strong>Why a disjunction and not one arm.</strong> Which arm fires is a property of the
    /// deployment, not of the edge. Measured against this fixture: a container runtime's forwarding
    /// proxy ACCEPTS on the host whether or not anything listens behind it, so an unsecured broker
    /// fails at the HANDSHAKE where a directly-reached target would fail at the CONNECT; and a
    /// broker that serves TLS but refuses the presented identity gets past both and fails at the
    /// ROUND TRIP. Pinning any one alone would make the drill a test of the deployment shape.
    /// </para>
    /// <para>
    /// <strong>The third arm was added by measurement, and its absence was a live defect in this
    /// helper.</strong> The foreign-identity row failed here on its first run — the engine
    /// behaving exactly as specified — because the round-trip arm's message is framed differently
    /// from the other two: it reports what SUCCEEDED before saying what did not
    /// (<c>…; the TLS handshake against &lt;address&gt; completed, but the broker did not
    /// answer …</c>), so neither of the original two substrings appears in it. Two arms was a
    /// statement about the two edges that existed when it was written, not about the probe.
    /// </para>
    /// <para>
    /// Admitting the round-trip arm does not weaken the transport-absent rows that also call this.
    /// Reaching it means a TLS session was ESTABLISHED, which a plaintext listener and an absent
    /// listener cannot do — and that alone carries the conclusion. It does NOT additionally mean
    /// the endpoint answered as a broker: the arm fires precisely when the endpoint did not, and
    /// the probe's own header records a measured non-broker (nginx over TLS) reaching it. Both
    /// transport-absent rows also pin their own endpoint, and the unsecured one the container's
    /// listener table besides.
    /// </para>
    /// <para>
    /// All three texts are verified against the probe's own throw sites, whose runtime messages
    /// read <c>…, but the engine could not connect to &lt;address&gt;: …</c>,
    /// <c>…, but the TLS handshake against &lt;address&gt; failed: …</c>, and
    /// <c>…; the TLS handshake against &lt;address&gt; completed, but the broker did not answer a
    /// Kafka ApiVersions request over it: …</c>. Only the probe's OWN framing is matched, never the
    /// platform exception text it quotes: that varies run to run for one and the same cause —
    /// measured here as "An established connection was aborted by the software in your host
    /// machine", where the probe's own header records "The decryption operation failed" for the
    /// same refusal.
    /// </para>
    /// </remarks>
    private static void AssertAbortedAtANetworkArm(string detail, NetworkArm expected)
    {
        var matched = NetworkArm.None;

        if (detail.Contains("but the engine could not connect to", StringComparison.Ordinal))
        {
            matched |= NetworkArm.Connect;
        }

        if (detail.Contains("but the TLS handshake against", StringComparison.Ordinal))
        {
            matched |= NetworkArm.Handshake;
        }

        if (detail.Contains("did not answer a Kafka ApiVersions request", StringComparison.Ordinal))
        {
            matched |= NetworkArm.RoundTrip;
        }

        Assert.True(
            matched != NetworkArm.None && (matched & ~expected) == NetworkArm.None,
            $"expected this row to abort at {expected} but it aborted at {matched}. A row that "
            + "moves to a different arm is not a row that still measures its own edge. Detail: "
            + detail);
    }

    /// <summary>
    /// Which arm of the probe a row's abort is allowed to come from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Per-row rather than shared, and the reason is a live hole the shared version had.
    /// </strong> A single three-arm disjunction admits any arm for any row, so a one-token drift in
    /// this fixture — mapping 9092 to SSL in the protocol map — would move the plaintext row from
    /// the handshake arm to the round-trip arm, leave every one of its assertions green, and leave
    /// it measuring nothing whatever about a plaintext listener open beside a secured one. The
    /// transport-absent rows and the identity row assert DISJOINT arms, so neither can drift into
    /// the other's shape unnoticed.
    /// </para>
    /// <para>
    /// The transport-absent rows still take TWO arms between them, and that pair is not laxity:
    /// which of connect and handshake fires is decided by the deployment, not the edge — measured,
    /// a container runtime's forwarding proxy accepts on the host whether or not anything listens
    /// behind it, so an unsecured broker fails at the handshake where a directly-reached one would
    /// fail at the connect.
    /// </para>
    /// </remarks>
    [Flags]
    private enum NetworkArm
    {
        /// <summary>No arm matched — the abort happened before any socket work.</summary>
        None = 0,

        /// <summary>The TCP connect failed: nothing listening, or nothing reachable.</summary>
        Connect = 1,

        /// <summary>The TLS handshake failed: the endpoint is not serving TLS the engine can use.</summary>
        Handshake = 2,

        /// <summary>
        /// TLS was established and the application-layer exchange over it did not complete.
        /// </summary>
        RoundTrip = 4,
    }

    /// <summary>
    /// Asserts the BROKER's own record shows it refused a TLS peer during this run, and names the
    /// cause it recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why the broker's record and not only the engine's.</strong> Everything else a
    /// negative row asserts is the engine reporting on itself. This is the peer's independent
    /// account of the same event, and it closes two gaps at once: it proves the broker was alive
    /// and making decisions AT PROBE TIME rather than merely at start-up, and it is indifferent to
    /// which TLS version negotiated — a stack that refused mid-handshake instead of after it would
    /// move the engine-side arm, and this line would read the same either way.
    /// </para>
    /// <para>
    /// <strong>The cause is a DISJUNCTION, and the measurement behind it is uncomfortable enough
    /// to state plainly.</strong> Measured on this host (.NET 8 / SChannel against
    /// cp-kafka:7.6.1 with <c>ssl.client.auth=required</c>): when the declared client certificate
    /// is issued by an authority the broker does not trust, SChannel filters it against the
    /// <c>certificate_authorities</c> list in the server's CertificateRequest, finds no match, and
    /// presents NOTHING — <c>SslStream.LocalCertificate</c> is null and
    /// <c>IsMutuallyAuthenticated</c> is false. The broker therefore records
    /// <c>Empty client certificate chain</c>, byte-identically to a connection that declared no
    /// certificate at all. A platform that put the foreign chain on the wire would instead record a
    /// path-building failure. Both are accepted here because both are the broker refusing the
    /// identity this suite declared; asserting only the first would pin a platform quirk as the
    /// contract.
    /// </para>
    /// </remarks>
    private static void AssertBrokerRefusedAPeer(string evidence)
    {
        Assert.Contains("Failed authentication", evidence, StringComparison.Ordinal);
        Assert.Contains("(SSL handshake failed)", evidence, StringComparison.Ordinal);

        Assert.True(
            evidence.Contains("Empty client certificate chain", StringComparison.Ordinal)
            || evidence.Contains("PKIX path building failed", StringComparison.Ordinal)
            || evidence.Contains("unable to find valid certification path", StringComparison.Ordinal),
            "the broker refused a peer but recorded none of the certificate-layer causes this row "
            + $"expects, so what it refused is unknown. Records:\n{evidence}");
    }

    /// <summary>
    /// Asserts the broker's own record names <paramref name="commonName"/> as a principal it
    /// authenticated — or, when <paramref name="expected"/> is <see langword="false"/>, that it
    /// never did.
    /// </summary>
    /// <remarks>
    /// <c>peerPrincipal</c> appears only on an SSL handshake the broker completed, so this reads
    /// exclusively the secured listener and cannot be confused by traffic on the plaintext one that
    /// the health gate generates.
    /// </remarks>
    private static void AssertBrokerAuthenticated(string evidence, string commonName, bool expected)
    {
        var fragment = $"peerPrincipal 'CN={commonName}'";

        if (expected)
        {
            Assert.Contains(fragment, evidence, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain(fragment, evidence, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Materialises the suite, starts the container-evidence watcher, and runs the scenario.
    /// </summary>
    /// <param name="row">
    /// The row's name — used for the scenario id and the suite directory, so a failing run names
    /// which row produced it.
    /// </param>
    /// <param name="securedEndpoint">EDGE-004's variable: the port <c>security.endpoint</c> names.</param>
    /// <param name="keystoreTarget">EDGE-005's variable: where the keystore artefact is delivered.</param>
    private async Task<DrillOutcome> RunDrillAsync(
        string row,
        string securedEndpoint,
        string keystoreTarget,
        Action<string>? afterMaterialise = null,
        int? pinnedHostPort = null,
        bool consumeStep = false,
        int healthCheckPort = 9092)
    {
        var suiteDirectory = MaterialiseSuiteDirectory(
            row, securedEndpoint, keystoreTarget, pinnedHostPort, consumeStep, healthCheckPort);
        afterMaterialise?.Invoke(suiteDirectory);
        _output.WriteLine($"row '{row}': suite directory {suiteDirectory}");

        var yaml = File.ReadAllText(Path.Combine(suiteDirectory, "drill.e2e.yaml"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // The container lives only for the duration of the run — SuiteTopology disposes the
        // topology on a probe failure, so a docker exec issued after the run returns finds nothing
        // to interrogate. The watcher therefore runs CONCURRENTLY, and only records a capture once
        // the broker's own log says it has started, so an early capture cannot report a listener
        // "absent" that had merely not been opened yet.
        // Snapshotted BEFORE anything is started, so the watcher can tell this row's container from
        // a sibling row's that has not finished being removed. See WaitForContainerAsync.
        var preExisting = (await ListBrokerContainersAsync(cts.Token)).ToHashSet(StringComparer.Ordinal);
        if (preExisting.Count > 0)
        {
            _output.WriteLine("pre-existing broker containers: " + string.Join(", ", preExisting));
        }

        using var watcherStop = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var watcher = CaptureContainerEvidenceAsync(keystoreTarget, preExisting, watcherStop.Token);

        var diagnostics = new StringWriter();
        ScenarioCoreResult result;
        try
        {
            result = await ScenarioRunner.RunScenarioOwningTopologyAsync(
                s_registry,
                yaml,
                row,
                AppHostAssemblyName,
                diagnostics,
                seedBaseDirectory: suiteDirectory,
                livePump: null,
                cancellationToken: cts.Token);
        }
        finally
        {
            watcherStop.Cancel();
        }

        var evidence = await watcher;

        _output.WriteLine($"verdict={result.Verdict} securityUnconfirmed={result.Assurance.Unconfirmed} refusal={result.Assurance.Refusal}");
        _output.WriteLine("── diagnostics ──\n" + diagnostics);
        _output.WriteLine("── event buffer ──\n" + string.Join("\n", result.Buffer));
        _output.WriteLine("── container evidence ──\n" + evidence);

        Assert.False(
            string.IsNullOrWhiteSpace(evidence),
            "the container-evidence watcher captured nothing, so this row's listener and artefact "
            + "assertions would be vacuous.");

        return new DrillOutcome(result, diagnostics.ToString(), evidence, suiteDirectory);
    }

    /// <summary>
    /// Writes one row's complete suite directory: the PKI, the conditional entrypoint, and the
    /// <c>.e2e.yaml</c> that declares them.
    /// </summary>
    /// <remarks>
    /// The directory name is STABLE per row and is deliberately not deleted. A CLI drill —
    /// <c>vouchfx run &lt;suite&gt;</c>, which is where REQ-018's process exit code is observable at
    /// all — has to be able to name the suite after the test that built it has finished, and a
    /// randomly-named directory that deletes itself cannot be named. This assembly disables
    /// intra-assembly test parallelism (see AssemblyInfo.cs), so two rows cannot race for one
    /// directory.
    /// </remarks>
    private static string MaterialiseSuiteDirectory(
        string row,
        string securedEndpoint,
        string keystoreTarget,
        int? pinnedHostPort = null,
        bool consumeStep = false,
        int healthCheckPort = 9092)
    {
        var suiteDirectory = Path.Combine(Path.GetTempPath(), SuiteDirectoryPrefix + row);
        if (Directory.Exists(suiteDirectory))
        {
            Directory.Delete(suiteDirectory, recursive: true);
        }

        TestCertificateAuthority.WriteKafkaBrokerSuiteDirectory(suiteDirectory);

        // LF, not Environment.NewLine: this is a bash script read inside a Linux container, and a
        // CRLF `if [ -f … ]; then` is a syntax error the broker would report as nothing at all.
        File.WriteAllText(Path.Combine(suiteDirectory, "bash-config"), ConditionalEntrypoint);
        File.WriteAllText(
            Path.Combine(suiteDirectory, "drill.e2e.yaml"),
            SuiteYaml(securedEndpoint, keystoreTarget, pinnedHostPort, consumeStep, healthCheckPort));

        return suiteDirectory;
    }

    /// <summary>
    /// The replacement for the image's own <c>/etc/confluent/docker/bash-config</c>: the
    /// customer's conditional, which appends SSL configuration only when the keystore is present
    /// at the path this script checks.
    /// </summary>
    /// <remarks>
    /// The first two lines reproduce the BEHAVIOUR of the image's own file, not its text (measured
    /// by reading it out of confluentinc/cp-kafka:7.6.1) — see this file's header for the four
    /// textual differences, none of which changes what the shell does. So replacing the file
    /// changes exactly one thing that matters: the conditional.
    /// </remarks>
    private const string ConditionalEntrypoint =
        "set -o nounset -o errexit\n"
        + "if [ \"${TRACE:-}\" = \"true\" ]; then set -o verbose -o xtrace; fi\n"
        + "if [ -f " + CheckedKeystorePath + " ]; then\n"
        + "  echo \"===> vouchfx fixture: keystore present, appending SSL configuration\"\n"
        + "  export KAFKA_LISTENERS=\"PLAINTEXT://0.0.0.0:9092,SECURE://0.0.0.0:9093,CONTROLLER://0.0.0.0:9094\"\n"
        // The secured listener's ADVERTISED address is env-driven, defaulting to the container
        // port. REQ-025's payoff row overrides it with the pinned HOST port, which is the whole
        // point: a broker announces the address clients must use for the partition leader, and
        // until a host port could be pinned there was no value it could announce that was true
        // from outside the container. `${VAR:-default}` is safe under `set -o nounset`.
        + "  export KAFKA_ADVERTISED_LISTENERS=\"PLAINTEXT://localhost:9092,SECURE://${VOUCHFX_SECURE_ADVERTISED:-localhost:9093}\"\n"
        + "  export KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=\"CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT,SECURE:SSL\"\n"
        + "  export KAFKA_SSL_KEYSTORE_TYPE=\"PEM\"\n"
        + "  export KAFKA_SSL_KEYSTORE_LOCATION=\"" + CheckedKeystorePath + "\"\n"
        + "  export KAFKA_SSL_TRUSTSTORE_TYPE=\"PEM\"\n"
        + "  export KAFKA_SSL_TRUSTSTORE_LOCATION=\"/etc/kafka/secrets/kafka.truststore.pem\"\n"
        + "  export KAFKA_SSL_CLIENT_AUTH=\"required\"\n"
        + "else\n"
        + "  echo \"===> vouchfx fixture: no keystore at " + CheckedKeystorePath + ", staying plaintext\"\n"
        + "fi\n";

    /// <summary>
    /// The one suite every row shares, with the two variables spliced in. Everything else —
    /// image, ports, health check, profile, client material, steps — is identical across every
    /// row, which is what makes each pair a measurement of its own single variable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>env:</c> block declares the PLAINTEXT-ONLY configuration. The SSL listener is added
    /// only by the conditional entrypoint above, which is the point: with the keystore at an
    /// unchecked path the broker is left exactly as this block describes it.
    /// </para>
    /// <para>
    /// <c>healthCheck</c> is declared EXPLICITLY against 9092 rather than left to default, and the
    /// reason is EDGE-005 itself. A service that declares <c>security</c> and no health check
    /// defaults to a TCP probe on its SECURED endpoint (REQ-023, EnvironmentMapper.ApplyHealthCheck)
    /// — so under EDGE-005, where nothing ever listens there, the run would fail at the HEALTH GATE
    /// and the drill would measure a container that never became healthy instead of the healthy
    /// unsecured broker the edge is about. Health-checking a plaintext or dedicated health port is
    /// also what a real secured deployment does, for the reason recorded at that default: no
    /// container health check can present a client certificate.
    /// </para>
    /// </remarks>
    /// <param name="pinnedHostPort">
    /// REQ-025: when set, the secured container port is declared as
    /// <c>"&lt;pinnedHostPort&gt;:9093"</c> and the broker is told to advertise that host port. When
    /// null every row keeps the bare-integer form and the orchestrator-allocated host port every
    /// other drill in this file uses.
    /// </param>
    /// <param name="consumeStep">
    /// Appends an <c>mq-expect.kafka</c> step after the publish, so one run exercises a Kafka
    /// producer AND a consumer over mutual TLS — the customer's own stated requirement, and the
    /// pair REQ-025 exists to make reachable.
    /// </param>
    /// <param name="healthCheckPort">
    /// Which container port the health gate probes, and it is PER-ROW rather than fixed at 9092
    /// for a measured reason. A conformance run recorded 3 failures in 22 row-executions, every
    /// one of them the orchestrator's "TCP connect … succeeded, but the connection was closed
    /// immediately (zero bytes read) — no backend is listening behind the host-published proxy":
    /// gating on 9092 while the probe targets 9093 lets the gate clear before the SECURED listener
    /// is behind its proxy. Rows whose secured listener is meant to exist therefore gate on 9093 —
    /// which is also the engine's own default for a security-declaring service, and needs no
    /// client certificate to pass. The two rows whose premise is that NO secured listener exists
    /// must keep 9092: gating on 9093 there would hold the topology unhealthy and the run would
    /// never reach the probe those edges are about.
    /// </param>
    private static string SuiteYaml(
        string securedEndpoint,
        string keystoreTarget,
        int? pinnedHostPort = null,
        bool consumeStep = false,
        int healthCheckPort = 9092) =>
        // Double-dollar raw string: a single '{'/'}' is a literal brace (this YAML carries a flow
        // mapping and a JSON payload) and '{{expr}}' is the interpolation hole — the same form
        // CsxFragment bodies use, and for the same reason. The single-dollar spelling inverts both
        // and does not compile here.
        $$"""
        metadata:
          name: kafka-security-confirmation-drill
        environment:
          services:
            {{BrokerName}}:
              image: confluentinc/cp-kafka:7.6.1
              ports: [9092, {{(pinnedHostPort is { } pin ? $"\"{pin}:9093\"" : "9093")}}]
              healthCheck: { type: tcp, port: {{healthCheckPort}} }
              security:
                profile: mtls
                endpoint: "{{securedEndpoint}}"
                caCert: {{TestCertificateAuthority.CaFileName}}
                clientCert: {{TestCertificateAuthority.ClientCertFileName}}
                clientKey: {{TestCertificateAuthority.ClientKeyFileName}}
                serverArtifacts:
                  - source: bash-config
                    target: /etc/confluent/docker/bash-config
                  - source: {{TestCertificateAuthority.BrokerKeystoreFileName}}
                    target: {{keystoreTarget}}
                  - source: {{TestCertificateAuthority.BrokerTruststoreFileName}}
                    target: /etc/kafka/secrets/kafka.truststore.pem
              env:
                KAFKA_NODE_ID: "1"
                KAFKA_PROCESS_ROLES: "broker,controller"
                KAFKA_LISTENERS: "PLAINTEXT://0.0.0.0:9092,CONTROLLER://0.0.0.0:9094"
                KAFKA_ADVERTISED_LISTENERS: "PLAINTEXT://localhost:9092"
                KAFKA_CONTROLLER_LISTENER_NAMES: "CONTROLLER"
                KAFKA_CONTROLLER_QUORUM_VOTERS: "1@localhost:9094"
                KAFKA_LISTENER_SECURITY_PROTOCOL_MAP: "CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT"
                KAFKA_INTER_BROKER_LISTENER_NAME: "PLAINTEXT"
                KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR: "1"
                KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR: "1"
                KAFKA_TRANSACTION_STATE_LOG_MIN_ISR: "1"
                KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS: "0"
                CLUSTER_ID: "MkU3OEVBNTcwNTJENDM2Qk"

                # Raises the broker's own authenticator logging so every row can read the BROKER's
                # verdict on each TLS connection, not only the engine's. Measured against this
                # image, with these two loggers at DEBUG:
                #
                #   success → SslTransportLayer: "SSL handshake completed successfully with
                #             peerHost … peerPrincipal 'CN=<client>' protocol 'TLSv1.3' …"
                #             Selector:           "Successfully authenticated with /<ip>"
                #   failure → SslTransportLayer: "SSL Handshake failed" + the SSLHandshakeException
                #             Selector:           "Failed authentication with /<ip> (SSL handshake
                #                                 failed)"
                #
                # The Selector FAILURE line is emitted at INFO by the image's default level, so
                # raising it is not what makes failures visible. What DEBUG adds is the two facts
                # the assertions actually need: the peerPrincipal on success (which names WHO the
                # broker authenticated) and the exception cause on failure (which names WHY it
                # refused).
                KAFKA_LOG4J_LOGGERS: "org.apache.kafka.common.network.SslTransportLayer=DEBUG,org.apache.kafka.common.network.Selector=DEBUG"
                {{AdvertisedOverride(pinnedHostPort)}}
        steps:
          - id: {{StepId}}
            type: mq-publish.kafka
            target: {{BrokerName}}
            topic: orders
            payload: '{"id":"edge-drill"}'

            # The publish budget, and which rows DEPEND on it versus merely tolerate it:
            #
            #   • The pinned produce-and-consume row DEPENDS on it. It asserts Verdict.Pass, so
            #     this step must complete a TLS handshake, fetch metadata, auto-create the topic
            #     and be acknowledged inside the budget. It gets 30s for that reason — the 5s the
            #     other rows use was inherited by it unexamined, and a budget chosen because a
            #     step could not pass is the wrong budget for the one row that requires it to.
            #   • The two abort rows never reach the step at all, so any value does.
            #   • The remaining rows reach it and cannot pass (see this file's header on advertised
            #     listeners). They tolerate the value — but not any value: the CLI control asserts
            #     exit 0, and that couples to the step's CLASSIFICATION, since a budget expiry
            #     resolves as Inconclusive (flaglessly exit 0) where a Fail would exit 1.
            #
            # Measured at 10s the unpinned step spent ~20s of wall clock; a value below 5s is
            # schema-legal and untried, so nothing here claims 5s is a floor.
            timeout: {{(consumeStep ? "30s" : "5s")}}
        """
        + (consumeStep ? ConsumeStepYaml : string.Empty);

    /// <summary>
    /// A YAML comment when nothing is pinned, and the env entry that overrides the broker's
    /// advertised secured address when something is.
    /// </summary>
    /// <remarks>
    /// A comment rather than an empty string because this sits mid-document on its own line: an
    /// empty substitution would leave a blank line carrying the surrounding indentation, which is
    /// legal YAML but reads as an accident.
    /// </remarks>
    private static string AdvertisedOverride(int? pinnedHostPort) =>
        pinnedHostPort is { } port
            ? $"VOUCHFX_SECURE_ADVERTISED: \"localhost:{port}\""
            : "# no pinned host port: the entrypoint advertises the container port";

    /// <summary>
    /// The consumer half of REQ-025's produce-and-consume row, appended outside the raw string
    /// literal because a raw string's closing delimiter must stand on its own line.
    /// </summary>
    private const string ConsumeStepYaml =
        "\n"
        + "  - id: " + ConsumeStepId + "\n"
        + "    type: mq-expect.kafka\n"
        + "    target: " + BrokerName + "\n"
        + "    topic: orders\n"
        + "    verifyMode: RETRY\n"
        + "    timeout: 30s\n"
        + "    match:\n"
        + "      json:\n"
        + "        \"$.id\": \"edge-drill\"\n";

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Container evidence
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Watches for the broker's container and, once its own log says the server has started,
    /// records which ports it is listening on and where the keystore actually landed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why the capture waits for the log line.</strong> The health gate passes as soon as
    /// something accepts a connection on 9092, which the broker does before every listener is
    /// open. A capture taken then could report 9093 absent on a run where it was merely late, and
    /// EDGE-005's central assertion is exactly that absence — so nothing is recorded until the
    /// broker reports <c>Kafka Server started</c>, and every caller additionally asserts 9092
    /// PRESENT in the same reading, which no started broker in this fixture can be missing. That
    /// pairing is what stops an absence-only assertion from passing for the wrong reason.
    /// </para>
    /// <para>
    /// <strong>Two independent sources, because the capture races a teardown.</strong>
    /// <c>SuiteTopology</c> disposes the topology the moment the probe fails, so the window in
    /// which <c>docker exec</c> works is short and not under this test's control.
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>docker logs</c> — polled from the moment the container appears and retained. The
    ///     broker's own <c>Awaiting socket connections on 0.0.0.0:&lt;port&gt;</c> lines are its
    ///     record of which listeners it opened, which is the evidence EDGE-004's and EDGE-005's
    ///     acceptance criteria ask for, and it stays readable while the container exists at all —
    ///     including after it has been stopped, when <c>exec</c> no longer works.
    ///   </description></item>
    ///   <item><description>
    ///     one combined <c>docker exec</c> — the filesystem facts (where the keystore actually
    ///     landed), which no log can carry. A SINGLE round trip rather than five, so the whole
    ///     filesystem capture either lands or does not, and a half-finished one cannot be read as
    ///     a fact about the container.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <strong>Why <c>/proc/net/tcp6</c> is not used.</strong> It was, in a first draft, and was
    /// dropped by measurement: neither <c>ss</c> nor <c>netstat</c> exists in
    /// confluentinc/cp-kafka:7.6.1 and <c>/proc/net/tcp</c> is EMPTY (the JVM binds dual-stack, so
    /// every listener appears in <c>tcp6</c> only) — so it works, but it is available only through
    /// the <c>exec</c> that races teardown, whereas the broker's own log says the same thing and
    /// outlives the race.
    /// </para>
    /// <para>
    /// <strong>THE WINDOW THIS CANNOT CLOSE, stated rather than implied.</strong> Nothing is
    /// recorded until the broker logs <c>Kafka Server started</c>. On a negative row the probe can
    /// fail within milliseconds of the health gate passing, and the health gate can pass on the
    /// same 9092 listener the broker opens 8 ms before that line — so a run CAN in principle tear
    /// the topology down before the first capture lands, and the caller's non-empty guard then
    /// fails the row. That failure is a lost race, not a defect in either edge, and it will read
    /// as "the container-evidence watcher captured nothing".
    /// </para>
    /// <para>
    /// <strong>A post-failure grace was considered and rejected on measurement.</strong> Teardown
    /// REMOVES the container rather than stopping it — measured, after every run in this file:
    /// <c>docker ps -a</c> lists no <c>mtls-broker-*</c> at all — and a removed container's log is
    /// gone with it, so a grace period could only help inside the stop-to-remove interval, whose
    /// length this fixture neither controls nor can observe. Buying an unquantifiable margin with
    /// added complexity is worse than naming the window. No lost capture has been observed in any
    /// run of this set to date — deliberately not a count, because the number of rows and of runs
    /// has changed with every round and a stale tally reads as a measurement.
    /// </para>
    /// </remarks>
    private static async Task<string> CaptureContainerEvidenceAsync(
        string keystoreTarget, IReadOnlySet<string> preExisting, CancellationToken cancellationToken)
    {
        var container = await WaitForContainerAsync(preExisting, cancellationToken).ConfigureAwait(false);
        if (container is null)
        {
            return string.Empty;
        }

        // `log` is the whole log as it stood when the broker finished starting — the start-up facts
        // (which listeners opened, which branch the entrypoint took) which never change afterwards.
        // `recent` is the moving tail, refreshed to the end of the run, which is where everything
        // about authentication appears.
        var log = string.Empty;
        var recent = string.Empty;
        var filesystem = string.Empty;

        // ACCUMULATED across every poll, not read off the final snapshot — and that distinction is
        // a defect this fixture actually shipped. The authentication records are written EARLY (the
        // probe runs immediately after the health gate), while the tail this watcher re-reads is a
        // fixed 200 lines. On a short row that window still contains them at the end; on the
        // produce-and-consume row it does not, because a consumer plus DEBUG-level transport
        // logging pushes hundreds of lines out behind it. MEASURED: that row passed in isolation
        // and failed inside the class, asserting the broker never authenticated a client it
        // demonstrably had. A record seen once is kept.
        var authenticationRecords = new SortedSet<string>(StringComparer.Ordinal);

        while (!cancellationToken.IsCancellationRequested)
        {
            // --tail for the READINESS poll, and the full log only once ready. The readiness marker
            // is the broker's last line at the moment it appears, so a short tail always carries
            // it, while a full `docker logs` costs tens of kilobytes per poll — and this loop polls
            // fast until it is ready (see the window note above), so the per-poll cost is what has
            // to be small, not the cadence. The full fetch below is what the evidence is read from.
            var ready = await DockerAsync($"logs --tail 30 {container}", cancellationToken)
                .ConfigureAwait(false);

            if (ready.Contains("Kafka Server started", StringComparison.Ordinal))
            {
                var full = await DockerAsync($"logs {container}", cancellationToken).ConfigureAwait(false);
                if (full.Length > 0)
                {
                    log = full;
                    CollectAuthenticationRecords(full, authenticationRecords);
                }
            }

            if (log.Contains("Kafka Server started", StringComparison.Ordinal) && filesystem.Length == 0)
            {
                filesystem = await DockerExecAsync(
                        container,
                        $"(test -f {keystoreTarget} && echo DECLARED-TARGET-PRESENT || echo DECLARED-TARGET-ABSENT); "
                        + $"(test -f {CheckedKeystorePath} && echo CHECKED-PATH-PRESENT || echo CHECKED-PATH-ABSENT); "
                        + "echo '--- ls /etc/kafka/secrets ---'; ls -l /etc/kafka/secrets 2>&1; "
                        + "echo '--- ls /etc/kafka/secret ---'; ls -l /etc/kafka/secret 2>&1; true",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            // DELIBERATELY NO `break` ONCE THE FILESYSTEM CAPTURE LANDS — this loop used to stop
            // there, and stopping was a defect rather than an economy. Everything the broker
            // records about AUTHENTICATION happens later than that: the probe runs after the
            // health gate, so a capture frozen at start-up predates every TLS connection the run
            // makes and can prove nothing about what the broker did with them.
            //
            // The refresh is a TAIL, and that shape was forced by measurement rather than chosen.
            // A first attempt refreshed the WHOLE log at a 1s cadence and lost the negative rows'
            // authentication records outright: a probe failure and its teardown occupy little more
            // than a second, so the last in-loop fetch predated the record and the post-cancellation
            // fetch found the container already removed. A 200-line tail costs little enough to
            // re-read every 200ms, which is the cadence that wins that race — and the full log is
            // fetched exactly once, for the start-up lines that never change.
            if (await DockerAsync($"logs --tail 200 {container}", cancellationToken)
                    .ConfigureAwait(false) is { Length: > 0 } refreshed)
            {
                recent = refreshed;
                CollectAuthenticationRecords(refreshed, authenticationRecords);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // One last attempt on the way out, on its own budget. Best-effort by construction — by the
        // time a row's abort cancels this watcher the topology has already been disposed, so this
        // usually finds the container gone and the retained tail is what stands (see the
        // container-removal note above). It is kept for the rows that end without a teardown race.
        using (var lastCall = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var final = await DockerAsync($"logs --tail 200 {container}", lastCall.Token)
                .ConfigureAwait(false);
            if (final.Length > 0)
            {
                recent = final;
                CollectAuthenticationRecords(final, authenticationRecords);
            }
        }

        if (!log.Contains("Kafka Server started", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var bound = BoundListenerPorts(log);
        var decisions = string.Join(
            " | ",
            log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Contains("vouchfx fixture", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal));

        // The broker's own authentication record. Every line it wrote about a TLS peer, kept
        // verbatim so a row can assert on the verdict the BROKER reached rather than only on the
        // one the engine reported.
        var authentication = string.Join("\n", authenticationRecords);

        return $"container: {container}\n"
            + $"listening ports: {string.Join(", ", bound)}\n"
            + $"entrypoint decision: {decisions}\n"
            + $"broker authentication records:\n{authentication}\n"
            + $"filesystem (declared target {keystoreTarget}):\n{filesystem}";
    }

    /// <summary>
    /// Adds every line of <paramref name="text"/> that is the broker's own record of an
    /// authentication outcome to <paramref name="into"/>, which de-duplicates them.
    /// </summary>
    /// <remarks>
    /// The four markers are the two Selector verdicts (<c>Successfully authenticated</c> /
    /// <c>Failed authentication</c>), the SslTransportLayer line naming the authenticated
    /// <c>peerPrincipal</c>, and the <c>SSLHandshakeException</c> that names a refusal's cause.
    /// </remarks>
    private static void CollectAuthenticationRecords(string text, SortedSet<string> into)
    {
        foreach (var line in text.Split(
                     '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Contains("Failed authentication", StringComparison.Ordinal)
                || line.Contains("Successfully authenticated", StringComparison.Ordinal)
                || line.Contains("peerPrincipal", StringComparison.Ordinal)
                || line.Contains("SSLHandshakeException", StringComparison.Ordinal))
            {
                into.Add(line);
            }
        }
    }

    /// <summary>
    /// The ports the broker itself reports opening — one
    /// <c>Awaiting socket connections on 0.0.0.0:&lt;port&gt;</c> line per data-plane listener.
    /// </summary>
    private static int[] BoundListenerPorts(string log)
    {
        const string marker = "Awaiting socket connections on 0.0.0.0:";
        var ports = new SortedSet<int>();

        foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var digits = line[(at + marker.Length)..];
            var end = 0;
            while (end < digits.Length && char.IsAsciiDigit(digits[end]))
            {
                end++;
            }

            if (end > 0 && int.TryParse(
                    digits[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                ports.Add(port);
            }
        }

        return ports.ToArray();
    }

    /// <summary>
    /// Lists the RUNNING containers DCP has started for <see cref="BrokerName"/>, whose names it
    /// allocates as <c>&lt;resource&gt;-&lt;eight lower-case letters&gt;</c>.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ListBrokerContainersAsync(
        CancellationToken cancellationToken)
    {
        // status=running, because a name-prefix filter alone also matches a container that has
        // EXITED — including this fixture's own previous row, in the window before DCP removes it.
        var listed = await DockerAsync(
                $"ps --filter \"name=^{BrokerName}-\" --filter status=running --format \"{{{{.Names}}}}\"",
                cancellationToken)
            .ConfigureAwait(false);

        // Sorted, because two call sites compare two of these lists for equality and `docker ps`
        // guarantees no ordering: an unsorted comparison would report a difference for a pair of
        // identical sets that came back in a different order.
        var names = listed.Split(
            '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }

    /// <summary>
    /// Waits for the container this row started, excluding any that were already running when the
    /// row began.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two guards, because the shared name prefix makes one insufficient.</strong> Every
    /// row in this file declares the same service name, so every row's container matches
    /// <c>^mtls-broker-</c>. Taking <c>names[0]</c> off that filter latches whatever Docker lists
    /// first, which on an unlucky overlap is the PREVIOUS row's container — and the capture would
    /// then report another row's listeners and another row's keystore placement as this row's, in
    /// the direction that makes a negative pass. Two combinations produce a wrong-but-plausible
    /// answer that way (either negative reading the positive control's container, and the reverse).
    /// </para>
    /// <para>
    /// So: <paramref name="preExisting"/> is snapshotted BEFORE the topology is asked to start and
    /// excluded here, and the wait additionally refuses to proceed while more than one unexpected
    /// container is present rather than picking one. Belt (exclude what was already there) and
    /// brace (never choose between candidates). The assembly disables intra-assembly parallelism,
    /// so the overlap this guards against comes from a teardown that has not finished, not from a
    /// concurrent row.
    /// </para>
    /// </remarks>
    private static async Task<string?> WaitForContainerAsync(
        IReadOnlySet<string> preExisting, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var candidates = (await ListBrokerContainersAsync(cancellationToken).ConfigureAwait(false))
                .Where(name => !preExisting.Contains(name))
                .ToArray();

            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            // candidates.Length > 1 falls through deliberately: a leftover from an unfinished
            // teardown is transient, and waiting for it to go is safer than guessing which is ours.
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    private static void AssertListening(string evidence, int port) =>
        Assert.True(
            ListeningPortsOf(evidence).Contains(port),
            $"expected the broker to be listening on {port}; evidence:\n{evidence}");

    private static void AssertNotListening(string evidence, int port) =>
        Assert.False(
            ListeningPortsOf(evidence).Contains(port),
            $"expected the broker NOT to be listening on {port}; evidence:\n{evidence}");

    /// <summary>Reads the decoded port list back out of a capture.</summary>
    private static int[] ListeningPortsOf(string evidence)
    {
        const string marker = "listening ports: ";
        var start = evidence.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"capture carries no listening-port line:\n{evidence}");

        var end = evidence.IndexOf('\n', start);
        var line = end < 0 ? evidence[(start + marker.Length)..] : evidence[(start + marker.Length)..end];

        return line
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.Parse(p, CultureInfo.InvariantCulture))
            .ToArray();
    }

    /// <summary>Counts event-stream lines of one <c>type</c> in a scenario's buffer.</summary>
    private static int CountEvents(IEnumerable<string> buffer, string eventType) =>
        buffer.Count(line => line.Contains($"\"type\":\"{eventType}\"", StringComparison.Ordinal));

    private static Task<string> DockerExecAsync(
        string container, string command, CancellationToken cancellationToken) =>
        DockerAsync($"exec {container} sh -c \"{command}\"", cancellationToken);

    /// <summary>
    /// Runs one <c>docker</c> command and returns its standard output, or an empty string on any
    /// failure or cancellation.
    /// </summary>
    /// <remarks>
    /// Swallowing the failure is deliberate here, and is the opposite of what
    /// <c>ServerArtifactInjectionDockerTests</c>'s own runner does. That one owns its container
    /// and asserts on a non-zero exit, because a failing command there can only be a mistyped
    /// one. This runner RACES a teardown it does not control: a command failing because the
    /// container has already gone is an ordinary outcome, not a fault. What replaces the exit-code
    /// assertion is the caller's refusal of an empty capture, which turns a lost race into a
    /// legible test failure rather than a vacuous pass.
    /// </remarks>
    private static async Task<string> DockerAsync(string arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;

            // CONCURRENTLY, for the reason RunCliAsync spells out: `docker logs` on a broker that
            // has been running for half a minute returns tens of kilobytes, and draining stdout to
            // the end before touching stderr lets a full stderr buffer block the child forever.
            // This helper argues that case for the CLI and used to violate it here.
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return process.ExitCode == 0 ? await stdout : string.Empty;
        }
        catch (Exception ex) when (ex is OperationCanceledException
                                       or System.ComponentModel.Win32Exception
                                       or InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
