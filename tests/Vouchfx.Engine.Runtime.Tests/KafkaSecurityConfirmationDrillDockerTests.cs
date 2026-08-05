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

    /// <summary>The suite's single step id, shared by the YAML and the CLI drills' output checks.</summary>
    private const string StepId = "publish";

    /// <summary>The in-container path the fixture's conditional entrypoint checks for the keystore.</summary>
    private const string CheckedKeystorePath = "/etc/kafka/secrets/kafka.keystore.pem";

    /// <summary>
    /// The plausible typo EDGE-005 turns on: <c>secret</c> for <c>secrets</c>. The host file
    /// exists and REQ-004's preflight passes; only the in-container destination is wrong.
    /// </summary>
    private const string TypoKeystorePath = "/etc/kafka/secret/kafka.keystore.pem";

    /// <summary>The prefix every row's suite directory shares, under the system temp directory.</summary>
    private const string SuiteDirectoryPrefix = "vouchfx-edge-drill-";

    private readonly ITestOutputHelper _output;

    public KafkaSecurityConfirmationDrillDockerTests(ITestOutputHelper output)
    {
        _output = output;
        SweepStaleSuiteDirectories();
    }

    /// <summary>
    /// Removes suite directories left by earlier runs, best-effort.
    /// </summary>
    /// <remarks>
    /// These directories are deliberately not deleted at the end of a row (see
    /// <see cref="MaterialiseSuiteDirectory"/>), which means the private keys inside them outlive
    /// the run that made them. Sweeping at class init bounds that to the interval between two
    /// runs instead of forever, without breaking the property the retention exists for — a CLI
    /// drill naming the suite after the test that built it, within the same run.
    /// <para>
    /// The material is short-lived by construction (the generated CA and leaves are valid for two
    /// days and one day respectively), and the key files are written 0600 where the platform has
    /// POSIX permissions — so this is defence in depth on a test fixture, not the only control.
    /// </para>
    /// <para>
    /// Every failure is swallowed: a directory another process still holds open is not a reason to
    /// fail a drill, and each row recreates its own directory regardless.
    /// </para>
    /// </remarks>
    private static void SweepStaleSuiteDirectories()
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
            drill.Result.SecurityConfirmationFailed,
            "a plaintext endpoint is a security-confirmation failure (REQ-018), not an ordinary "
            + "environment error — without this flag `vouchfx run` would exit 0.");

        // ── The abort names both halves an author needs to act on ─────────────────────────────
        Assert.Contains("on endpoint '9092'", drill.AbortDetail, StringComparison.Ordinal);
        Assert.Contains(BrokerName, drill.AbortDetail, StringComparison.Ordinal);

        // ── Zero steps executed ───────────────────────────────────────────────────────────────
        AssertNoStepRan(drill);

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
    private static void AssertNoStepRan(DrillOutcome drill)
    {
        // The abort came from REQ-005's PROBE, not from the pre-topology security preflight. Worth
        // pinning separately, because `SecurityConfirmationFailed` is set by BOTH — a preflight
        // rejection (a path that escapes the suite directory, a file that is not there) sets the
        // same flag — and the two edges are specifically about failures no pre-topology check can
        // see. `SecurityConfirmation` is OrchestrationErrorKind's own name for the probe's arm.
        Assert.Equal("SecurityConfirmation", drill.AbortKind);

        // …and from a network arm of it, so TLS was actually attempted. See
        // AssertAbortedAtANetworkArm for the five pre-network arms this rules out.
        AssertAbortedAtANetworkArm(drill.AbortDetail);

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
            drill.Result.SecurityConfirmationFailed,
            "the run must have reached and failed REQ-005's probe — a health-gate failure would "
            + "leave this flag false, and would mean this drill measured a broken container "
            + "rather than a healthy unsecured one.");
        Assert.Equal(Verdict.EnvironmentError, drill.Result.Verdict);

        Assert.Contains(BrokerName, drill.AbortDetail, StringComparison.Ordinal);
        Assert.Contains("on endpoint '9093'", drill.AbortDetail, StringComparison.Ordinal);

        // ── Zero steps executed ───────────────────────────────────────────────────────────────
        AssertNoStepRan(drill);

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
            "positive-control", securedEndpoint: "9093", keystoreTarget: CheckedKeystorePath);

        // ── The probe confirmed ───────────────────────────────────────────────────────────────
        Assert.False(
            drill.Result.SecurityConfirmationFailed,
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
        // MATCHED ON THE SENTENCE, and not on SecurityConfirmationLevel.AuthenticatedRoundTrip,
        // because the symbol is not reachable from here — measured, twice over. The enum is public,
        // but nothing on this path exposes a value of it: ScenarioCoreResult carries a verdict, a
        // buffer and the security flag and no confirmations at all, and SecurityConfirmation.
        // ToString() — the only rendering that reaches this test — omits the Level entirely,
        // printing the profile, endpoint, observed address, protocol, client-identity state and
        // Detail. So the Detail sentence IS the signal available, and it is the specific one: this
        // text is produced by exactly one branch, the mtls arm that ran the anonymous differential.
        // Reaching the symbol would need the test to own the topology (SuiteTopology.
        // SecurityConfirmations), which would mean not running the scenario this control is about.
        Assert.Contains(
            "REFUSED the same request on a second connection presenting no client certificate",
            drill.Diagnostics,
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
        var (exitCode, output) = await RunCliAsync(cli, suite, cts.Token);

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
        var (exitCode, output) = await RunCliAsync(cli, suite, cts.Token);

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
        AssertAbortedAtANetworkArm(output);

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
        string cliAssembly, string suitePath, CancellationToken cancellationToken)
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
        // whole of what REQ-018 and both edges specify.
        startInfo.ArgumentList.Add(cliAssembly);
        startInfo.ArgumentList.Add("run");
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
    private sealed record DrillOutcome(ScenarioCoreResult Result, string Diagnostics, string Evidence)
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
    /// <strong>Why a disjunction and not one arm.</strong> Which of the two fires is a property of
    /// the port-publishing layer, not of the edge: measured against this fixture, a container
    /// runtime's forwarding proxy ACCEPTS on the host whether or not anything listens behind it,
    /// so an unsecured broker fails at the handshake; a target reached with no proxy in front of it
    /// would fail at the connect. Pinning either one alone would make the drill a test of the
    /// runtime's port publishing.
    /// </para>
    /// <para>
    /// Both texts are verified against the probe's own two throw sites, whose runtime messages read
    /// <c>…, but the TLS handshake against &lt;address&gt; failed: …</c> and
    /// <c>…, but the engine could not connect to &lt;address&gt;: …</c>.
    /// </para>
    /// </remarks>
    private static void AssertAbortedAtANetworkArm(string detail)
    {
        Assert.True(
            detail.Contains("but the TLS handshake against", StringComparison.Ordinal)
            || detail.Contains("but the engine could not connect to", StringComparison.Ordinal),
            "the abort did not come from either of the probe's network arms, so no TLS was "
            + $"attempted and this row measured nothing about the endpoint. Detail: {detail}");
    }

    /// <summary>
    /// Materialises the suite, starts the container-evidence watcher, and runs the scenario.
    /// </summary>
    /// <param name="row">
    /// The row's name — used for the scenario id and the suite directory, so a failing run names
    /// which of the four rows produced it.
    /// </param>
    /// <param name="securedEndpoint">EDGE-004's variable: the port <c>security.endpoint</c> names.</param>
    /// <param name="keystoreTarget">EDGE-005's variable: where the keystore artefact is delivered.</param>
    private async Task<DrillOutcome> RunDrillAsync(
        string row, string securedEndpoint, string keystoreTarget)
    {
        var suiteDirectory = MaterialiseSuiteDirectory(row, securedEndpoint, keystoreTarget);
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

        _output.WriteLine($"verdict={result.Verdict} securityConfirmationFailed={result.SecurityConfirmationFailed}");
        _output.WriteLine("── diagnostics ──\n" + diagnostics);
        _output.WriteLine("── event buffer ──\n" + string.Join("\n", result.Buffer));
        _output.WriteLine("── container evidence ──\n" + evidence);

        Assert.False(
            string.IsNullOrWhiteSpace(evidence),
            "the container-evidence watcher captured nothing, so this row's listener and artefact "
            + "assertions would be vacuous.");

        return new DrillOutcome(result, diagnostics.ToString(), evidence);
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
    private static string MaterialiseSuiteDirectory(string row, string securedEndpoint, string keystoreTarget)
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
        File.WriteAllText(Path.Combine(suiteDirectory, "drill.e2e.yaml"), SuiteYaml(securedEndpoint, keystoreTarget));

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
        + "  export KAFKA_ADVERTISED_LISTENERS=\"PLAINTEXT://localhost:9092,SECURE://localhost:9093\"\n"
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
    /// The one suite the four rows share, with the two variables spliced in. Everything else —
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
    private static string SuiteYaml(string securedEndpoint, string keystoreTarget) =>
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
              ports: [9092, 9093]
              healthCheck: { type: tcp, port: 9092 }
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
        steps:
          - id: {{StepId}}
            type: mq-publish.kafka
            target: {{BrokerName}}
            topic: orders
            payload: '{"id":"edge-drill"}'
            timeout: 10s
        """;

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
    /// added complexity is worse than naming the window. Measured flake rate so far: 0 in 10
    /// negative-row captures across five runs of the set.
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

        var log = string.Empty;
        var filesystem = string.Empty;

        while (!cancellationToken.IsCancellationRequested)
        {
            // --tail for the READINESS poll, and the full log only once. The readiness marker is
            // the broker's last line at the moment it appears, so a short tail always carries it,
            // while a full `docker logs` costs tens of kilobytes per poll — and this loop polls
            // fast on purpose (see the window note above), so the per-poll cost is what has to be
            // small, not the cadence. The full fetch below is what the evidence is read from.
            var ready = await DockerAsync($"logs --tail 30 {container}", cancellationToken)
                .ConfigureAwait(false);

            if (ready.Contains("Kafka Server started", StringComparison.Ordinal))
            {
                log = await DockerAsync($"logs {container}", cancellationToken).ConfigureAwait(false);
            }

            if (log.Contains("Kafka Server started", StringComparison.Ordinal))
            {
                if (filesystem.Length == 0)
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

                if (filesystem.Length > 0)
                {
                    break;
                }
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

        return $"container: {container}\n"
            + $"listening ports: {string.Join(", ", bound)}\n"
            + $"entrypoint decision: {decisions}\n"
            + $"filesystem (declared target {keystoreTarget}):\n{filesystem}";
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

        return listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
