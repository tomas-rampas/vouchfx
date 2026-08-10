// EDGE-011 — authorisation is enforced, and the engine reports it as the ORDINARY environment
// fault it is (authenticated-infrastructure-mtls, slice F acceptance drills).
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// THE INVERSION, WHICH IS THE WHOLE POINT OF THIS FILE.
//
// Every other negative control in this slice ends the same way: the run aborts before any step,
// and a flagless `vouchfx run` exits NON-ZERO. (They do not all get there by the same route — the
// absent-client-certificate pair never reaches the probe at all, being refused by the security
// PREFLIGHT as `Inconclusive` and exiting 4, while the transport negatives fail at the probe and
// exit 3.) This one inverts both. A certificate that is perfectly valid for the mutual-TLS
// handshake, and simply not granted anything by the broker's own authorisation rules, produces:
//
//   • a probe that PASSES — legitimately, by design and by mandate. It confirms transport and
//     authentication; topic authorisation is per-request, per-topic and step-scoped, and the probe
//     has neither the means nor the remit to check it.
//   • a failure that arrives at the STEP, mapped by the provider's existing catches to an
//     ORDINARY Verdict.EnvironmentError — not the security-confirmation carve-out.
//   • therefore a flagless `vouchfx run` that exits ZERO, and exits 3 only with
//     --fail-on-env-error.
//
// That difference is the demonstration. A broker that authenticates everybody and authorises
// everybody would pass every other row in this slice; only this pair separates "TLS is working"
// from "authorisation is actually enforced".
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// FIXTURE OWNERSHIP, STATED PLAINLY BECAUSE IT IS EASY TO MISREAD THIS FILE AS AN ENGINE FEATURE.
//
// The broker-side authorisation configuration below — the authoriser, the principal mapping, the
// super-user entry, the ACL grants — is the CUSTOMER'S responsibility in the deployment and the
// FIXTURE'S here. The engine neither provisions ACLs nor inspects them, and nothing in this file
// asks it to. What the engine owns is the half these rows actually measure: that a broker's
// refusal surfaces as a legible step-level environment error rather than a pass, a crash, or a
// security-confirmation failure. This is fixture realism in service of an engine assertion.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// THE BROKER CONFIGURATION, AND WHY EACH PIECE IS THERE (measured against cp-kafka:7.6.1).
//
//   authorizer.class.name = StandardAuthorizer   the KRaft authoriser; the ZooKeeper-era
//                                                AclAuthorizer is not usable in this mode
//   ssl.principal.mapping.rules                  maps the certificate DN to a short principal, so
//                                                an ACL can name `User:cert-vouchfx-test-client`
//                                                rather than a full distinguished name
//   super.users = User:ANONYMOUS                 not laxity. The inter-broker and KRaft controller
//                                                listeners are PLAINTEXT, and a plaintext transport
//                                                layer's peer principal IS ANONYMOUS (measured in
//                                                the image), so the broker's own internal traffic
//                                                arrives under that name and must be allowed to
//                                                use the cluster. The SECURED listener, which is
//                                                what these rows exercise, is unaffected.
//   allow.everyone.if.no.acl.found = false       explicit, but NOT load-bearing — see below
//
// TWO CORRECTIONS TO THE OBVIOUS READING OF THE ABOVE, both measured against the image itself.
//
// (a) `allow.everyone.if.no.acl.found=false` is not what makes the unauthorised row fail.
//     Disassembled, StandardAuthorizer.getDefaultResult() returns DENIED when the property is
//     ABSENT, and the image adds no default of its own — so the deny posture is Kafka's own.
//     Stronger still: the rule builder returns a static DENY result whenever a resource HAS ACLs
//     but none match the principal, which is exactly this row, so the configured default is never
//     consulted on this path. The setting stays because a security-relevant default that a reader
//     would otherwise have to look up belongs in the file; the claim that it is what makes the row
//     work does not.
//
// (b) A static `super.users` entry for the authorised client was considered and REJECTED — but not
//     because it "bypasses the authoriser". `super.users` IS the authoriser's own configuration,
//     and the allow is issued BY the authoriser, through a super-user rule that would name itself
//     in the audit line as `based on rule SuperUser`. What it bypasses is ACL EVALUATION. The
//     design conclusion is unchanged and is the reason it was rejected: the row would prove the
//     broker can be configured to skip the ACL check, not that the check exists.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// THE `cert-` PREFIX IN THE MAPPING RULE IS A CONTROL, NOT A NAMING PREFERENCE — DO NOT SIMPLIFY.
//
// The obvious rule is `RULE:^CN=([^,]*).*$/$1/`, which maps `CN=x` to `User:x`. That puts every
// certificate-derived principal in the SAME namespace as the internal principals the broker names
// itself, and `super.users` above names one of those: `User:ANONYMOUS`. With the bare rule, a
// certificate issued by the trusted CA with `CN=ANONYMOUS` maps to `User:ANONYMOUS` and arrives on
// the SSL listener holding full cluster administration — the authoriser is not bypassed so much as
// satisfied, and nothing in the configuration looks wrong. Prefixing puts certificate identities in
// a namespace of their own (`User:cert-…`) that no internal principal can be spelled into, so the
// super-user entry can only ever be reached by the listeners that genuinely produce ANONYMOUS.
//
// The trailing `,DEFAULT` keeps the full-DN fallback for a certificate the rule does not match.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// THE ORDERING HAZARD, AND THE READINESS GATE THAT CLOSES IT.
//
// ACLs cannot be applied before the broker runs — they are metadata records, written through the
// broker's own admin API — so the fixture applies them from a background task in the entrypoint.
// That creates a race the engine cannot see. A TCP health check against the broker's own port
// would pass within about a second of that listener opening, which is well before any grant
// exists, so the first step could run against a broker that would deny the authorised identity
// too. The row would fail for the right-looking reason and prove nothing.
//
// (Precisely: the engine's TCP gate is not satisfied by mere ACCEPTANCE — `ProbeAsync` connects
// and then performs a bounded one-byte read, treating zero bytes as Unhealthy, which is why a
// proxy in front of a dead backend does not pass it. Behind a live Kafka listener that
// distinction costs about a second and changes nothing here; it is stated because the mechanism
// as originally written in this comment was wrong even though the conclusion held.)
//
// Closed by making READINESS DEPEND ON THE GRANT: the applier creates the topic, adds the ACLs,
// then POLLS `kafka-acls --list` until the grant is readable back, and only then opens a dedicated
// TCP port. The suite health-checks THAT port. Reading the grant back is the part that matters —
// an `--add` that returned says the request was accepted, not that the authoriser has the record.
//
// The ordering this depends on, and how much of it is actually established:
//     Awaiting socket connections on 0.0.0.0:9092      ← a gate here would pass too early
//     ===> vouchfx fixture: ACL grant CONFIRMED for User:cert-vouchfx-test-client
//     ===> vouchfx fixture: opening readiness port 9099 ← the gate this suite waits for
//
// The two orderings this fixture RELIES on are structural, not observed luck: the applier cannot
// confirm a grant before 9092 accepts its admin call, and the CONFIRMED and readiness lines are
// consecutive echoes in the same branch. The relative order of the two listeners' own
// "Awaiting socket connections" lines is neither relied upon nor established — this suite's
// harvester keeps only principal, denial and fixture lines, so it has never captured them.
//
// Run with: dotnet test --filter "requires=docker".
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.Kafka;
using Vouchfx.Steps.MqPublish.Kafka;
using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// EDGE-011 against a live broker whose authoriser is switched on: authentication succeeds for
/// both identities, and only one of them is allowed to use the topic.
/// </summary>
public sealed class KafkaAuthorisationDrillDockerTests
{
    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";
    private const string BrokerName = "acl-broker";
    private const string StepId = "publish";
    private const string SuiteDirectoryPrefix = "vouchfx-acl-drill-";

    /// <summary>The port the fixture opens only once the ACL grant is confirmed readable.</summary>
    private const int ReadinessPort = 9099;

    /// <summary>
    /// The namespace <c>ssl.principal.mapping.rules</c> puts CERTIFICATE identities in, keeping
    /// them unspellable as the internal principals <c>super.users</c> names. See this file's
    /// header — removing it makes a <c>CN=ANONYMOUS</c> certificate a cluster administrator.
    /// </summary>
    private const string MappedPrincipalPrefix = "cert-";

    /// <summary>The principal the fixture grants, as the broker's authoriser sees it.</summary>
    private const string AuthorisedPrincipal =
        MappedPrincipalPrefix + TestCertificateAuthority.ClientSubjectCommonName;

    /// <summary>The principal the unauthorised row authenticates as, and which is granted nothing.</summary>
    private const string UnauthorisedPrincipal =
        MappedPrincipalPrefix + TestCertificateAuthority.UnauthorisedClientSubjectCommonName;

    private static readonly bool s_swept = SweepStaleSuiteDirectoriesOnce();

    private readonly ITestOutputHelper _output;

    public KafkaAuthorisationDrillDockerTests(ITestOutputHelper output)
    {
        _output = output;
        _ = s_swept;
    }

    /// <summary>
    /// Removes suite directories left by earlier RUNS, best-effort, once per process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each row here suffixes its own directory, so a row's <c>MaterialiseSuite</c> deletes only
    /// the one it is about to rewrite — the other two rows' directories, and the private keys in
    /// them, outlive the run that made them indefinitely. This bounds that to the interval between
    /// two runs. It cannot disturb a run in progress: the field is a STATIC initialiser, so it
    /// executes once, at this class's first construction.
    /// </para>
    /// <para>
    /// Defence in depth on a test fixture rather than the only control — the material is generated
    /// per run and short-lived, and the key files are written 0600 where the platform has POSIX
    /// permissions. Every failure is swallowed: a directory another process still holds open is not
    /// a reason to fail a drill, and each row recreates its own regardless.
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
    // The rows
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The AUTHORISED identity: the probe confirms, the step publishes, the suite passes.
    /// </summary>
    /// <remarks>
    /// This is also the positive control for the row below, and it is what makes that row's
    /// failure attributable to authorisation. Without it, "the step failed" is equally consistent
    /// with a broken fixture — a broker that grants nothing to anyone would fail the unauthorised
    /// row for a reason that has nothing to do with the edge.
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task AuthorisedIdentity_ProbeConfirmsAndTheStepPublishes()
    {
        var drill = await RunDrillAsync("acl-authorised", unauthorised: false);

        Assert.False(
            drill.Result.SecurityConfirmationFailed,
            "the probe must confirm; diagnostics: " + drill.Diagnostics);
        Assert.Equal(Verdict.Pass, drill.Result.Verdict);

        // The broker names the identity it authenticated — the same evidence the transport drills
        // use, here on a run that actually moved a message through an authorising broker.
        Assert.Contains(
            $"peerPrincipal 'CN={TestCertificateAuthority.ClientSubjectCommonName}'",
            drill.Evidence,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The UNAUTHORISED identity: the probe still confirms — and then the step fails as an
    /// ORDINARY environment error.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task UnauthorisedIdentity_ProbeConfirmsThenTheStepFailsAsAnOrdinaryEnvironmentError()
    {
        var drill = await RunDrillAsync("acl-unauthorised", unauthorised: true);

        // ── 1. THE PREMISE: authentication SUCCEEDED. ────────────────────────────────────────
        // If this flags, the row has become a transport control wearing an authorisation label,
        // and everything below it would be measuring the wrong thing.
        Assert.False(
            drill.Result.SecurityConfirmationFailed,
            "the probe must CONFIRM for this edge — an unauthorised identity is a fully "
            + "authenticated one. Diagnostics: " + drill.Diagnostics);
        Assert.Contains(
            "REFUSED the same request on a second connection presenting no client certificate",
            drill.Diagnostics,
            StringComparison.Ordinal);

        // ── 2. And the broker authenticated THIS principal by name. ──────────────────────────
        Assert.Contains(
            $"peerPrincipal 'CN={TestCertificateAuthority.UnauthorisedClientSubjectCommonName}'",
            drill.Evidence,
            StringComparison.Ordinal);

        // ── 3. THE STEP RAN, and RAN TO COMPLETION. ──────────────────────────────────────────
        // Not aborted before — RUN. That is the second half of the inversion: every other negative
        // in this slice executes zero steps.
        //
        // Both counts are asserted, and the honest description of what they buy is narrower than
        // it looks. The buffer is RECONSTRUCTED after the delegate returns, in one loop over the
        // declared steps that emits the started and completed lines unconditionally in the same
        // iteration — so no reachable state yields one without the other, and the completed line
        // appears even where the step reported no outcome of its own. Neither count therefore
        // distinguishes "ran to completion" from "began and vanished": a step that vanishes takes
        // the surrounding catch and emits NEITHER line.
        //
        // What the pair does pin is the SHAPE OF THE RUN — exactly one declared step, which
        // reached the executing path at all. That is the half this row needs: every other negative
        // in the slice emits zero step events because it never gets there. It does not pin the
        // number of ATTEMPTS (retries are separate `step-attempt` events, and a RETRY step polling
        // five times would still leave this at 1) — the YAML's IMMEDIATE default is what makes
        // this a single attempt.
        Assert.Equal(1, CountEvents(drill.Result.Buffer, EventTypes.StepStarted));
        Assert.Equal(1, CountEvents(drill.Result.Buffer, EventTypes.StepCompleted));

        // ── 4. …as an ORDINARY environment error, NOT the security carve-out. ────────────────
        Assert.Equal(Verdict.EnvironmentError, drill.Result.Verdict);
        Assert.False(
            drill.Result.SecurityConfirmationFailed,
            "an authorisation refusal is an ordinary environment error. Were this flag set, a "
            + "flagless run would exit non-zero and the taxonomy would no longer distinguish an "
            + "unconfirmable security assertion from a broker that refused one request.");

        // ── 5. THE GRANT EXISTED, and this identity was still refused. ───────────────────────
        // The reason for this assertion is NOT that the row would otherwise go green with no ACL
        // anywhere — the readiness gate above already forecloses that world. With no grant, 9099
        // never listens, the health gate never passes, and the run dies before any step, failing
        // assertions 2, 3 and 6 rather than passing them.
        //
        // What it buys is that the row STATES ITS OWN PREMISE instead of borrowing it. "This
        // identity was denied" is only interesting beside "another identity was granted", and
        // without this line that second half lives entirely in the authorised sibling row — a
        // coupling that is convention, not mechanism, and that a `--filter` run of this row alone
        // dissolves. Reading the grant out of the broker's own log makes the row self-contained,
        // and cheaply: the line is already in the captured evidence.
        Assert.Contains(
            $"ACL grant CONFIRMED for User:{AuthorisedPrincipal}",
            drill.Evidence,
            StringComparison.Ordinal);

        // ── 6. The broker's own account of the refusal, ON ONE LINE. ─────────────────────────
        // The step's observation says the client was refused; this says the AUTHORISER refused it,
        // names the principal, the resource and the RULE it applied, and is written by the peer
        // rather than by anything on this side.
        //
        // Composed rather than asserted piecewise, because the three fragments say different
        // things about different requests when they are allowed to come from different lines. The
        // rule fragment is the one that carries the edge: `DefaultDeny` means NO ACL MATCHED, so
        // the refusal came from the default-deny posture rather than from an explicit DENY entry,
        // which would name a matching-ACL rule instead. Composing them is what pins that to the
        // same decision that names this principal and this topic.
        //
        // Whether a PIECEWISE form would actually be satisfied under an explicit DENY has not been
        // measured — it would need a run with a `--deny-principal` ACL to see whether some other
        // request in the window still logs a DefaultDeny line. Plausible, since metadata and
        // describe decisions do log them, but unmeasured, so it is not offered as the reason.
        Assert.Contains(
            drill.Evidence.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            line =>
                line.Contains($"Principal = User:{UnauthorisedPrincipal} is Denied", StringComparison.Ordinal)
                && line.Contains("Topic:LITERAL:orders", StringComparison.Ordinal)
                && line.Contains("based on rule DefaultDeny", StringComparison.Ordinal));

        // ── 7. THE ENGINE's own account of the same refusal. ─────────────────────────────────
        // Section 6 is the far end of the connection. This is THIS end, and the pair is what makes
        // the row's central claim — the step failed BECAUSE OF AUTHORISATION rather than for some
        // other reason — true from both ends instead of one.
        //
        // What it adds over section 6, stated narrowly. Section 6 says the authoriser refused this
        // principal on this topic during this run. It does not say that refusal is what the STEP
        // recorded as its failure: those are two facts about the same run, joined here by nothing
        // but the fact that both are true. INFERRED, not measured: a step that failed for an
        // unrelated reason — a connect timeout, a metadata error — beside a denial the broker
        // logged for some other request in the window would leave sections 3, 4 and 6 intact. This
        // closes that join from the engine's side, and the evidence was already in the buffer.
        //
        // Parsed and read off ONE event, for the reason section 6 composes: `stepId` and
        // `observation.error` are properties of the same step-completed record, and two `Contains`
        // over the joined buffer would not say they came from the same one. Section 3 has already
        // pinned the count at exactly one, so `Single` inside the helper is a second guard on it.
        //
        // The string is pinned WHOLE, and both halves earn it. It is librdkafka's rendering of the
        // broker's own error code, and the `Broker: ` prefix is its marker for a code that arrived
        // in a broker RESPONSE — as against `Local: ` for one the client generated. That prefix is
        // therefore the half carrying "the peer said so"; the remainder is the code's description,
        // which is the half carrying "and what it said was an authorisation refusal". Drop either
        // and the section stops making the claim it exists to make.
        //
        // Exact equality rather than a substring, because the string carries NO run-varying part —
        // no address, no timestamp, no topic name — which is measured, not assumed: it came back
        // byte-identical on every repeat of this row. So pinning it whole costs nothing in
        // flakiness, and it buys the one thing a looser match cannot: a client-library upgrade that
        // rewords or reclassifies this error fails HERE, with the new text in the assertion
        // message, instead of being absorbed by a match that no longer means what it did.
        //
        // The observation is reached with TryGetProperty rather than GetProperty because its
        // ABSENCE is a real outcome rather than an impossible one: `StepCompletedEvent.Observation`
        // is a nullable JsonElement and the event stream is serialised WhenWritingNull, so a step
        // that recorded no observation omits the property from the wire entirely — and GetProperty
        // would surface that as a bare KeyNotFoundException naming nothing.
        var completed = SingleEvent(drill.Result.Buffer, EventTypes.StepCompleted);
        Assert.Equal(StepId, completed.GetProperty("stepId").GetString());

        Assert.True(
            completed.TryGetProperty("observation", out var observation),
            $"the '{StepId}' step-completed event carries no `observation`: the step recorded none, "
            + $"so it is omitted from the wire. Event: {completed}");
        Assert.True(
            observation.TryGetProperty("error", out var error),
            $"the '{StepId}' observation carries no `error`. Observation: {observation}");
        Assert.Equal("Broker: Topic authorization failed", error.GetString());
    }

    /// <summary>
    /// The taxonomy demonstration, on real processes: the SAME unauthorised suite exits <b>0</b>
    /// flagless and <b>3</b> with <c>--fail-on-env-error</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both invocations are asserted because neither alone is the claim. Exit 0 on its own is
    /// indistinguishable from a suite that did nothing; exit 3 on its own is what the transport
    /// negatives in this slice also produce. (Not <em>every</em> other negative: the
    /// absent-client-certificate pair exits 4, its verdict being <c>Inconclusive</c> rather than
    /// an environment error.) The PAIR is what says "this is an ordinary environment error, gated
    /// by the flag that exists for exactly that" — and it is the only NEGATIVE in the slice whose
    /// flagless code is 0. Other rows assert a flagless 0, but they are controls rather than
    /// negatives: the positive control's suite is Inconclusive, and the three-requirements suite
    /// passes outright.
    /// </para>
    /// <para>
    /// Two topologies, deliberately: a flag cannot be applied retrospectively to a finished run,
    /// and running the same suite twice is what proves the difference is the flag rather than the
    /// fixture.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task UnauthorisedIdentity_ExitsZeroFlaglessAndThreeWhenEnvironmentErrorsGateCi()
    {
        var cli = ResolveCliAssembly();
        var suite = MaterialiseSuite("acl-unauthorised-cli", unauthorised: true);

        // A BUDGET EACH, not one shared across both. A single CTS spanning the pair makes the
        // second run's deadline whatever the first one left behind: a slow first topology — an
        // image pull, a loaded host — cancels the second before it starts, and the failure arrives
        // as `Assert.Equal(3, gated)` seeing a cancellation code. That reads as a taxonomy defect
        // and is a stopwatch.
        using var flaglessBudget = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var (flagless, flaglessOutput) = await RunCliAsync(cli, suite, null, flaglessBudget.Token);
        _output.WriteLine($"flagless exit={flagless}");

        using var gatedBudget = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var (gated, gatedOutput) = await RunCliAsync(
            cli, suite, "--fail-on-env-error", gatedBudget.Token);
        _output.WriteLine($"--fail-on-env-error exit={gated}");

        Assert.Equal(0, flagless);
        Assert.Equal(3, gated);

        // Both runs reached and failed the STEP — so the codes differ because of the flag, not
        // because the two runs took different paths.
        foreach (var output in new[] { flaglessOutput, gatedOutput })
        {
            Assert.Contains($"step '{StepId}'", output, StringComparison.Ordinal);
            Assert.DoesNotContain("SecurityConfirmation", output, StringComparison.Ordinal);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixture
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed record DrillOutcome(ScenarioCoreResult Result, string Diagnostics, string Evidence);

    private async Task<DrillOutcome> RunDrillAsync(string row, bool unauthorised)
    {
        var suite = MaterialiseSuite(row, unauthorised);
        var suiteDirectory = Path.GetDirectoryName(suite)!;
        var yaml = File.ReadAllText(suite);
        _output.WriteLine($"row '{row}': {suiteDirectory}");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));

        // Both taken before the topology exists, and independent of each other: the snapshot says
        // WHICH container is this run's, the window says WHICH LINES are — see DockerLogWindow.
        var since = DockerLogWindow.Start();
        var preExisting = await ListBrokerContainersAsync(cts.Token);
        if (preExisting.Count > 0)
        {
            _output.WriteLine("pre-existing broker containers: " + string.Join(", ", preExisting));
        }

        using var watcherStop = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var watcher = CaptureBrokerEvidenceAsync(preExisting, since, watcherStop.Token);

        var diagnostics = new StringWriter();
        ScenarioCoreResult result;
        try
        {
            result = await ScenarioRunner.RunScenarioOwningTopologyAsync(
                s_registry, yaml, row, AppHostAssemblyName, diagnostics,
                seedBaseDirectory: suiteDirectory, livePump: null, cancellationToken: cts.Token);
        }
        finally
        {
            watcherStop.Cancel();
        }

        var evidence = await watcher;
        _output.WriteLine($"verdict={result.Verdict} securityConfirmationFailed={result.SecurityConfirmationFailed}");
        _output.WriteLine("── diagnostics ──\n" + diagnostics);
        _output.WriteLine("── buffer ──\n" + string.Join("\n", result.Buffer));
        _output.WriteLine("── broker evidence ──\n" + evidence);

        Assert.False(
            string.IsNullOrWhiteSpace(evidence),
            "no broker evidence was captured, so this row's authentication and authorisation "
            + "assertions would be vacuous.");

        return new DrillOutcome(result, diagnostics.ToString(), evidence);
    }

    /// <summary>
    /// Writes one row's suite directory and returns the path of its <c>.e2e.yaml</c>.
    /// </summary>
    private static string MaterialiseSuite(string row, bool unauthorised)
    {
        var suiteDirectory = Path.Combine(Path.GetTempPath(), SuiteDirectoryPrefix + row);
        if (Directory.Exists(suiteDirectory))
        {
            Directory.Delete(suiteDirectory, recursive: true);
        }

        TestCertificateAuthority.WriteKafkaBrokerSuiteDirectory(suiteDirectory);

        // THE ONLY VARIABLE THAT MATTERS. The two rows' YAML is not byte-identical — each reserves
        // its own host port, which changes both `ports:` and the advertised address — but every
        // DECLARED SECURITY INPUT is: the same `caCert`, the same `clientCert`/`clientKey`
        // filenames, the same broker key store, the same authoriser configuration and the same
        // grant. Only the IDENTITY INSIDE client.pem/client-key.pem changes, and it remains one the
        // broker authenticates without complaint. That is the claim the inversion rests on, and it
        // is the one that is exactly true.
        if (unauthorised)
        {
            TestCertificateAuthority.SwitchToUnauthorisedClientIdentity(suiteDirectory);
        }

        File.WriteAllText(Path.Combine(suiteDirectory, "bash-config"), AuthorisingEntrypoint);

        var hostPort = ReserveAFreePort();
        var suite = Path.Combine(suiteDirectory, "acl.e2e.yaml");
        File.WriteAllText(suite, SuiteYaml(hostPort));
        return suite;
    }

    /// <summary>
    /// The fixture's replacement for the image's <c>bash-config</c>: SSL, the authoriser, the ACL
    /// applier, and the readiness gate that makes the grant a precondition of health.
    /// </summary>
    private const string AuthorisingEntrypoint =
        "set -o nounset -o errexit\n"
        + "if [ \"${TRACE:-}\" = \"true\" ]; then set -o verbose -o xtrace; fi\n"
        + "if [ -f /etc/kafka/secrets/kafka.keystore.pem ]; then\n"
        + "  export KAFKA_LISTENERS=\"PLAINTEXT://0.0.0.0:9092,SECURE://0.0.0.0:9093,CONTROLLER://0.0.0.0:9094\"\n"
        + "  export KAFKA_ADVERTISED_LISTENERS=\"PLAINTEXT://localhost:9092,SECURE://${VOUCHFX_SECURE_ADVERTISED:-localhost:9093}\"\n"
        + "  export KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=\"CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT,SECURE:SSL\"\n"
        + "  export KAFKA_SSL_KEYSTORE_TYPE=\"PEM\"\n"
        + "  export KAFKA_SSL_KEYSTORE_LOCATION=\"/etc/kafka/secrets/kafka.keystore.pem\"\n"
        + "  export KAFKA_SSL_TRUSTSTORE_TYPE=\"PEM\"\n"
        + "  export KAFKA_SSL_TRUSTSTORE_LOCATION=\"/etc/kafka/secrets/kafka.truststore.pem\"\n"
        + "  export KAFKA_SSL_CLIENT_AUTH=\"required\"\n"
        + "  export KAFKA_AUTHORIZER_CLASS_NAME=\"org.apache.kafka.metadata.authorizer.StandardAuthorizer\"\n"
        // The `cert-` prefix is load-bearing — see this file's header before changing it.
        + "  export KAFKA_SSL_PRINCIPAL_MAPPING_RULES='RULE:^CN=([^,]*).*$/cert-$1/,DEFAULT'\n"
        + "  export KAFKA_SUPER_USERS=\"User:ANONYMOUS\"\n"
        + "  export KAFKA_ALLOW_EVERYONE_IF_NO_ACL_FOUND=\"false\"\n"

        // The image sources this file more than once, so the applier is guarded by an atomic
        // mkdir: without it three background copies race, and two lose the readiness-port bind.
        + "  if mkdir /tmp/vouchfx-acl-applier 2>/dev/null; then\n"
        + "  (\n"
        + "    GRANTEE=\"" + AuthorisedPrincipal + "\"\n"
        + "    for _ in $(seq 1 120); do\n"
        + "      if kafka-topics --bootstrap-server localhost:9092 --create --if-not-exists \\\n"
        + "           --topic orders --partitions 1 --replication-factor 1 >/dev/null 2>&1; then break; fi\n"
        + "      sleep 1\n"
        + "    done\n"
        + "    kafka-acls --bootstrap-server localhost:9092 --add \\\n"
        + "      --allow-principal \"User:${GRANTEE}\" --operation All --topic orders >/dev/null 2>&1 || true\n"
        + "    kafka-acls --bootstrap-server localhost:9092 --add \\\n"
        + "      --allow-principal \"User:${GRANTEE}\" --operation All --group '*' >/dev/null 2>&1 || true\n"

        // Read the grant BACK before declaring readiness. An --add that returned says the request
        // was accepted, not that the authoriser holds the record.
        // The readiness port is opened INSIDE the confirmation branch. Outside it, readiness would
        // depend on "the grant was confirmed OR the polling loop gave up", which is not the
        // invariant this fixture claims — and a suite whose grant never landed would come up
        // healthy and then fail its step for a reason nobody could see. Inside it, a grant that
        // never confirms simply never becomes healthy, and the run ends as a legible health-gate
        // environment error naming the resource.
        + "    for _ in $(seq 1 120); do\n"
        + "      if kafka-acls --bootstrap-server localhost:9092 --list --topic orders 2>/dev/null \\\n"
        + "           | grep -q \"principal=User:${GRANTEE}\"; then\n"
        + "        echo \"===> vouchfx fixture: ACL grant CONFIRMED for User:${GRANTEE}\"\n"
        + "        echo \"===> vouchfx fixture: opening readiness port 9099\"\n"
        + "        exec ncat -lk 9099 >/dev/null 2>&1\n"
        + "      fi\n"
        + "      sleep 1\n"
        + "    done\n"
        + "  ) &\n"
        + "  fi\n"
        + "fi\n";

    /// <summary>
    /// The one suite both rows share. The health check targets the READINESS port, not the
    /// broker's own, so the gate cannot pass before the grant exists.
    /// </summary>
    private static string SuiteYaml(int pinnedHostPort) =>
        $$"""
        metadata:
          name: kafka-authorisation-drill
        environment:
          services:
            {{BrokerName}}:
              image: confluentinc/cp-kafka:7.6.1

              # 9092 IS DELIBERATELY NOT PUBLISHED, and on this broker that is not tidiness.
              # It is the PLAINTEXT listener, and `super.users=User:ANONYMOUS` below makes every
              # principal arriving on it a cluster administrator — including for `kafka-acls
              # --add/--remove`, the very grants this drill's verdict depends on. Publishing it
              # binds that to all host interfaces (Docker's default publish address is 0.0.0.0 and
              # [::]) for the life of the run, reachable unauthenticated by anything that can reach
              # this machine, and offers a route to make the unauthorised row pass from outside it.
              #
              # Nothing needs it published: the ACL applier reaches `localhost:9092` from INSIDE
              # the container, the health gate probes {{ReadinessPort}}, and the step targets 9093.
              # The listener itself is untouched — not publishing a port does not unbind it. Its
              # in-container user here is the fixture's own applier; the KRaft controller uses a
              # separate listener on 9094, and this single-node cluster has no peer broker for
              # inter-broker traffic to reach.
              ports: ["{{pinnedHostPort}}:9093", {{ReadinessPort}}]
              healthCheck: { type: tcp, port: {{ReadinessPort}} }
              security:
                profile: mtls
                endpoint: "9093"
                caCert: {{TestCertificateAuthority.CaFileName}}
                clientCert: {{TestCertificateAuthority.ClientCertFileName}}
                clientKey: {{TestCertificateAuthority.ClientKeyFileName}}
                serverArtifacts:
                  - source: bash-config
                    target: /etc/confluent/docker/bash-config
                  - source: {{TestCertificateAuthority.BrokerKeystoreFileName}}
                    target: /etc/kafka/secrets/kafka.keystore.pem
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
                KAFKA_LOG4J_LOGGERS: "kafka.authorizer.logger=DEBUG,org.apache.kafka.common.network.SslTransportLayer=DEBUG,org.apache.kafka.common.network.Selector=DEBUG"

                # REQ-025 at work: the broker advertises the PINNED host port, so a client on the
                # engine host follows the metadata to an address that exists. Without pinning this
                # value could not be written at all — the port is not known until the run starts.
                VOUCHFX_SECURE_ADVERTISED: "localhost:{{pinnedHostPort}}"
        steps:
          - id: {{StepId}}
            type: mq-publish.kafka
            target: {{BrokerName}}
            topic: orders
            payload: '{"id":"acl-drill"}'
            timeout: 30s
        """;

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Evidence + process helpers
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Accumulates the broker's own authentication and authorisation records for the run.
    /// </summary>
    private static async Task<string> CaptureBrokerEvidenceAsync(
        IReadOnlySet<string> preExisting, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var container = await WaitForContainerAsync(preExisting, cancellationToken).ConfigureAwait(false);
        if (container is null)
        {
            return string.Empty;
        }

        // Accumulated across polls rather than read off a final snapshot — and the reason is
        // container LIFETIME, not log windowing. `docker logs --since` returns the whole window,
        // not a tail (measured: a fetch issued after a late line returns the early one too), so
        // any single fetch taken after the denial would hold the handshake records as well. What
        // it cannot survive is the container being removed during teardown, which is exactly when
        // the last fetch tends to land. Accumulating means an early poll's records stand even when
        // the final one comes back empty.
        var records = new SortedSet<string>(StringComparer.Ordinal);
        var started = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            started |= await HarvestAsync(container, records, since, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // ONE LAST HARVEST AFTER CANCELLATION, on its own budget — and it is not belt-and-braces,
        // it is the fix for a measured flake. The authoriser's denial is written when the STEP
        // runs, and this row's step fails in about 170ms; the watcher is cancelled the moment the
        // run returns, so the last in-loop poll can predate the very record the row asserts.
        // Observed once in a class run and never in isolation, which is the signature.
        //
        // Best-effort by construction: the topology is already disposing, so this often finds the
        // container gone, and then what was accumulated stands.
        using (var lastCall = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            started |= await HarvestAsync(container, records, since, lastCall.Token)
                .ConfigureAwait(false);
        }

        return started ? string.Join("\n", records) : string.Empty;
    }

    /// <summary>
    /// Adds every authentication, authorisation and fixture-milestone line currently in the
    /// container's log to <paramref name="records"/>, and reports whether the broker had started.
    /// </summary>
    private static async Task<bool> HarvestAsync(
        string container,
        SortedSet<string> records,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var log = await DockerAsync(
                $"logs --since {DockerLogWindow.Since(since)} {container}", cancellationToken)
            .ConfigureAwait(false);
        if (log.Length == 0)
        {
            return false;
        }

        foreach (var line in log.Split(
                     '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Contains("peerPrincipal", StringComparison.Ordinal)
                || line.Contains("is Denied operation", StringComparison.Ordinal)
                || line.Contains("vouchfx fixture", StringComparison.Ordinal))
            {
                records.Add(line);
            }
        }

        return log.Contains("Kafka Server started", StringComparison.Ordinal);
    }

    /// <summary>
    /// Waits for the container THIS row started, excluding any already running when it began.
    /// </summary>
    /// <remarks>
    /// Both rows in this class share the <c>acl-broker</c> prefix, so the exactly-one guard alone
    /// leaves a window in which a sibling's container is still being removed. The two identities
    /// carry different common names, so a mix-up fails closed rather than passing — but a
    /// confusing flake is still a flake, and the sibling drill class already carries both guards.
    /// </remarks>
    private static async Task<string?> WaitForContainerAsync(
        IReadOnlySet<string> preExisting, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var names = (await ListBrokerContainersAsync(cancellationToken).ConfigureAwait(false))
                .Where(name => !preExisting.Contains(name))
                .ToArray();

            if (names.Length == 1)
            {
                return names[0];
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The running containers currently carrying this suite's broker name prefix.</summary>
    private static async Task<IReadOnlySet<string>> ListBrokerContainersAsync(
        CancellationToken cancellationToken) =>
        (await DockerAsync(
                $"ps --filter \"name=^{BrokerName}-\" --filter status=running --format \"{{{{.Names}}}}\"",
                cancellationToken)
            .ConfigureAwait(false))
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.Ordinal);

    private static int CountEvents(IEnumerable<string> buffer, string eventType) =>
        buffer.Count(line => line.Contains($"\"type\":\"{eventType}\"", StringComparison.Ordinal));

    /// <summary>
    /// The single event of the given type in a run's JSON Lines buffer, parsed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deserialising into a <see cref="JsonElement"/> — rather than
    /// <see cref="JsonDocument"/>.<c>Parse</c> — returns a CLONED element that owns no pooled
    /// document, so there is nothing for the caller to dispose and no analyser to placate.
    /// </para>
    /// <para>
    /// Exactly one, rather than the first: the callers here have already asserted the count they
    /// expect, and a second matching event would mean the run took a shape those counts were not
    /// written for. <c>TryGetProperty</c> on the discriminator keeps a line that is valid JSON but
    /// not an envelope from throwing before the filter can reject it.
    /// </para>
    /// <para>
    /// The count is asserted with a MESSAGE rather than left to <c>Single</c>, whose own failures
    /// (<c>Sequence contains no matching element</c> / <c>…more than one matching element</c>) name
    /// neither the type sought nor how much was searched — so a fixture that stopped emitting the
    /// event and a buffer that arrived empty read identically, in a docker row where re-running to
    /// find out costs minutes.
    /// </para>
    /// </remarks>
    private static JsonElement SingleEvent(IEnumerable<string> buffer, string eventType)
    {
        var lines = buffer as IReadOnlyCollection<string> ?? buffer.ToArray();
        var matches = lines
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(e => e.TryGetProperty("type", out var type) && type.GetString() == eventType)
            .ToArray();

        Assert.True(
            matches.Length == 1,
            $"expected exactly one '{eventType}' event, found {matches.Length} "
            + $"in a buffer of {lines.Count} line(s).");

        return matches[0];
    }

    /// <summary>Finds a free host port on the any-address (see the pinned-port fixtures' notes).</summary>
    private static int ReserveAFreePort()
    {
        var probe = new TcpListener(IPAddress.Any, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string ResolveCliAssembly()
    {
        var assemblyDirectory = Path.GetDirectoryName(
            typeof(KafkaAuthorisationDrillDockerTests).Assembly.Location)!;
        var configuration = Path.GetFileName(Path.GetDirectoryName(assemblyDirectory))!;
        var repoRoot = Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "..", "..", "..", ".."));
        var cli = Path.Combine(
            repoRoot, "src", "Cli", "Vouchfx.Cli", "bin", configuration, "net8.0", "vouchfx.dll");

        Assert.True(
            File.Exists(cli),
            $"The built CLI was not found at '{cli}'. Build the solution first: "
            + $"dotnet build vouchfx.sln -c {configuration}");

        return cli;
    }

    private static async Task<(int ExitCode, string Output)> RunCliAsync(
        string cliAssembly, string suitePath, string? flag, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(cliAssembly)!,
        };

        startInfo.ArgumentList.Add(cliAssembly);
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add(suitePath);
        if (flag is not null)
        {
            startInfo.ArgumentList.Add(flag);
        }

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
                // Ended between the check and the kill, or the platform refused it.
            }
        }
    }

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
