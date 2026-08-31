// The deployment's three stated requirements, in ONE suite against ONE topology
// (authenticated-infrastructure-mtls, slice F acceptance drills).
//
// Everything else in this slice proves a failure mode. This file proves the thing the feature was
// commissioned for, end to end and in the customer's own words:
//
//   (i)   the container is configured through `env:` — carrying a value out of the ENGINE HOST's
//         own environment via `${env:NAME}`, which is the shape a CI system supplies
//   (ii)  a REST call presents a CLIENT CERTIFICATE to a service whose own certificate chains to a
//         PRIVATE CA
//   (iii) a Kafka CONSUMER reads over mutual TLS
//
// One suite, one topology, two secured services, every step green and a flagless exit code of 0.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// WHY THIS COULD NOT BE WRITTEN BEFORE, in two parts, because both were real blockers.
//
// 1. THE LAST HOP. A service-form broker advertises the address clients must use for the partition
//    leader, and until a host port could be PINNED there was no value it could advertise that was
//    true outside its container — so a Kafka step reached the broker, was redirected, and failed.
//    That hop blocks a PRODUCE exactly as it blocks a consume: this repository's own
//    `KafkaServiceTargetDockerTests` records a produce against this shape spending its whole budget
//    on `Connect to ipv4#127.0.0.1:9092 failed` — the advertised address. Requirement (iii) is a
//    CONSUMER, so it needs that hop; it does not need it MORE than the `publish` step beside it,
//    which would fail identically without the pin. The pinned port below is what makes both work.
//
// 2. NO TLS-TERMINATING HTTP CONTAINER EXISTED. The earlier REST proof is an in-process
//    TcpListener/SslStream that injects its own URL into Vars; it never touches EnvironmentMapper
//    or YAML, so it cannot join a topology or be probed. Requirement (ii) needs a real container
//    with a real certificate, and that is the nginx fixture below.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// ONE CERTIFICATE AUTHORITY FOR BOTH SERVICES, and the reason is the deployment rather than
// convenience. A bank issuing certificates to its own broker and its own API issues them from ONE
// internal CA; that is what makes a single declared `caCert` per service — the same file — the
// realistic shape. Two authorities would test the fixture's ability to hold two files, which is
// not a property of this engine, and would obscure the property that IS under test: that one
// declared anchor is applied independently to two different secured targets in one run. The two
// services still get their own `security` blocks, their own probes and their own confirmations.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// EVIDENCE. A green verdict is not evidence that authentication happened — a fixture with
// verification switched off is also green. Both legs therefore assert the SERVER's own record of
// the client it authenticated:
//   Kafka  → `peerPrincipal 'CN=…'` from the broker's transport layer
//   nginx  → `client_dn="CN=…" verify=SUCCESS` from an access-log format written for this purpose
// Neither can be produced by anything on the engine's side of the connection.
//
// Run with: dotnet test --filter "requires=docker" --blame-crash --blame-crash-dump-type full
//
// The blame flags are not optional decoration: this lane launches the CLI as a child process and
// has crashed a test host once, with no dump. See the canonical account in DrillHostHygiene.cs
// ("WHY THE DRILL LANE IS ALWAYS RUN WITH --blame-crash") — the frequency there is a property of
// the LANE, not of this class.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.MqExpect.Kafka;
using Vouchfx.Steps.MqPublish.Kafka;
using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// The three requirements, together, against live containers.
/// </summary>
[Collection(DrillHostSweepCollectionDefinition.Name)]
public sealed class ThreeRequirementsSuiteDockerTests
{
    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";
    private const string BrokerName = "deployment-broker";
    private const string ApiName = "deployment-api";
    private const string SuiteDirectory = "vouchfx-three-requirements";

    /// <summary>
    /// The value carried into the API container through <c>env:</c> and returned in its response —
    /// requirement (i), made observable rather than incidental.
    /// </summary>
    private const string EnvGreeting = "configured-through-env";

    /// <summary>
    /// The name of the variable in the ENGINE HOST's own environment that
    /// <see cref="EnvGreeting"/> is delivered through — requirement (i)'s
    /// <c>${env:NAME}</c> passthrough, which is the shape a bank actually uses: the CI system
    /// holds the value and the suite only names it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not a name any real deployment would carry. The engine reads it from whatever
    /// process runs the suite, so a name in common use could be answered by an ambient value on a
    /// developer's machine or a build agent; each row sets this one explicitly on entry — and nulls
    /// it again in a <c>finally</c> — so what the container receives is chosen by this test on
    /// every host, and no later test in this assembly inherits the assignment.
    /// </para>
    /// </remarks>
    private const string EnvGreetingVariable = "VOUCHFX_TEST_THREE_REQUIREMENTS_GREETING";

    private readonly ITestOutputHelper _output;

    public ThreeRequirementsSuiteDockerTests(ITestOutputHelper output) => _output = output;

    private static readonly System.Reflection.Assembly[] s_providerAssemblies =
    {
        typeof(HttpRestProvider).Assembly,
        typeof(MqPublishKafkaProvider).Assembly,
        typeof(MqExpectKafkaProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    [Fact]
    [Trait("requires", "docker")]
    public async Task AllThreeRequirements_InOneSuite_PassAgainstLiveContainers()
    {
        // Requirement (i) starts HERE, in the ENGINE HOST's environment rather than in the YAML —
        // standing in for the CI system that would hold the value in a bank's own pipeline. Set in
        // the ROW rather than inside Materialise so that the assignment and its restore are the
        // same method's business, which is how every env-mutating test in this assembly is written
        // (M2EndToEndTests, SecretRedactionTests, ReproducibilityEnvelopeRunnerTests and
        // Sprint11ReferenceCapstoneTests all set on entry and null it in a `finally`).
        //
        // The restore is not correcting a live fault. MEASURED: no other test file in this repository
        // so much as mentions this name, and the only readers of the VALUE are the engine's
        // EnvironmentMapper, during the topology build both rows drive, and this row's own
        // precondition assertion. So the restore buys one thing, and it is worth one line: a future
        // row proving the UNSET branch of `${env:}` (EDGE-008) would otherwise become
        // order-dependent on this one.
        Environment.SetEnvironmentVariable(EnvGreetingVariable, EnvGreeting);
        try
        {
            var suiteDirectory = Materialise(out var brokerHostPort);
            var yaml = File.ReadAllText(Path.Combine(suiteDirectory, "deployment.e2e.yaml"));
            _output.WriteLine($"suite: {suiteDirectory} (broker host port {brokerHostPort})");

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));

            // Both taken BEFORE anything starts, and both defend the same thing by different means: a
            // leaked container from an earlier run of this same test carries every line asserted below
            // — see CaptureAsync and DockerLogWindow.
            var since = DockerLogWindow.Start();
            var preExistingApi = await ListContainersAsync(ApiName, cts.Token);
            var preExistingBroker = await ListContainersAsync(BrokerName, cts.Token);
            if (preExistingApi.Count > 0 || preExistingBroker.Count > 0)
            {
                _output.WriteLine(
                    "pre-existing containers: "
                    + string.Join(", ", preExistingApi.Concat(preExistingBroker)));
            }

            using var watcherStop = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var brokerEvidence = CaptureAsync(BrokerName, preExistingBroker, since, watcherStop.Token);
            var apiEvidence = CaptureAsync(ApiName, preExistingApi, since, watcherStop.Token);

            var diagnostics = new StringWriter();
            ScenarioCoreResult result;
            try
            {
                result = await ScenarioRunner.RunScenarioOwningTopologyAsync(
                    s_registry, yaml, "three-requirements",
                    // Issue #409: supplied, not derived — the same walk the aggregator passes in.
                    ScenarioRunner.DeclaredTargetsOf(yaml, s_registry),
                    AppHostAssemblyName, diagnostics,
                    seedBaseDirectory: suiteDirectory, livePump: null, cancellationToken: cts.Token);
            }
            finally
            {
                watcherStop.Cancel();
            }

            var broker = await brokerEvidence;
            var api = await apiEvidence;

            _output.WriteLine($"verdict={result.Verdict}");
            _output.WriteLine("── diagnostics ──\n" + diagnostics);
            _output.WriteLine("── buffer ──\n" + string.Join("\n", result.Buffer));
            _output.WriteLine("── broker evidence ──\n" + broker);
            _output.WriteLine("── api evidence ──\n" + api);

            // ── The whole suite passed ────────────────────────────────────────────────────────────
            Assert.False(result.Assurance.Unconfirmed, "diagnostics: " + diagnostics);
            Assert.Equal(Verdict.Pass, result.Verdict);

            // Three steps, three completions — none skipped, none merely started.
            Assert.Equal(3, CountEvents(result.Buffer, EventTypes.StepCompleted));

            // ── BOTH secured targets were confirmed, AT THE LEVEL EACH IS SUPPOSED TO REACH ───────
            //
            // Each target is pinned to its own Detail rather than to the mere appearance of
            // `service '<name>'`. What that buys needs stating accurately, because an earlier version
            // of this comment justified it with a hazard that measurement says is unreachable.
            //
            // WHAT IT DOES NOT BUY: protection against a silently downgraded confirmation. For THIS
            // suite the probe fails CLOSED rather than downgrading. Both steps are `mq-*`, so
            // `deployment-broker` is in the Kafka-speaking target set, `SpeaksKafka` is true, and the
            // mtls branch runs the anonymous-refusal arm; if a broker stopped REQUIRING a client
            // certificate the anonymous exchange would succeed and the probe would THROW
            // (`SecuredEndpointProbe`'s unconditional failure on that path) → SecurityConfirmation →
            // EnvironmentError. `TransportConfirmed` is reachable only on the non-Kafka branch, which
            // this broker never takes. So neither dropping `ssl.client.auth=required` nor failing to
            // deliver the key store could produce a green run here: the first throws at the probe, and
            // the second leaves the fixture's `if [ -f … ]` guard with no SECURE listener at all, which
            // fails the handshake arm. This suite would abort, not pass quietly.
            //
            // WHAT IT DOES BUY, and why it stays: the assertions are four string comparisons on a report
            // the test already holds, and they pin the CONTRACT — this broker reaches the
            // application-layer level, this API reaches the transport level — so a future change to
            // either probe's behaviour is caught HERE, in a suite whose whole purpose is the customer's
            // proof, rather than being absorbed by a Pass that no longer means what it did. Defence in
            // depth against a probe change, not against a fixture change. (The name alone would in any
            // case not be unique to a confirmation: the pinned-port collision path prints the same
            // shape.)
            //
            // Each pair is matched ON ONE LINE, which is the same discipline the REST-evidence assertion
            // below argues for, applied here where the report carries TWO confirmations. A confirmation
            // IS one line: `SecurityConfirmation.ToString()` renders name, declared profile, declared
            // endpoint, observed protocol, observed address, client-identity state, LEVEL and Detail
            // into a single string, and ScenarioRunner writes each one with a single WriteLineAsync
            // (its `SecurityConfirmations` loop). The level joined that list in slice F's peer-review
            // fix and is now what each pair below is bound on — see the note at the assertions.
            //
            // The SANITISER on that write is NOT what keeps a confirmation on one line, and an earlier
            // version of this comment said it was. `DisplaySanitiser` deliberately PRESERVES `\n` — it
            // strips `\r` and the other C0/C1 controls, and its own class remarks say so — so a newline
            // already in the text would pass through it verbatim. What keeps the confirmation on one
            // line is the composed string: every member rendered BETWEEN the name and the Detail is
            // either declared by this fixture (profile `mtls`, endpoint `9093`/`8443`) or derived by
            // the engine (an SslProtocols enum name, a host:port), and the two Detail values pinned
            // below are compile-time literals in SecuredEndpointProbe containing no newline. Name and
            // Detail therefore share a line.
            //
            // Left unbound — four independent `Contains` over the whole report, as this block was first
            // written — the same four comparisons would also be satisfied by a report in which the API
            // earned the round-trip level and the broker the transport one: the exact inversion of what
            // is claimed. That the engine cannot currently produce that report is a property of TODAY's
            // probe (only a Kafka-addressed target enters the round-trip branch), not of the assertion;
            // binding each pair costs one Split and stops the claim depending on it.
            var reportLines = diagnostics.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            //   THE LEVEL TOKEN IS NOW HALF OF EACH PAIR, and it is the half a customer can actually
            //   gate on. Before slice F's peer-review fix the rendered line omitted the level entirely,
            //   so the only thing distinguishing these two confirmations was the trailing Detail prose
            //   — an operator wanting "every secured target reached the strong level" had a sentence to
            //   grep and nothing else. Each assertion below binds the target name, the level token and
            //   the Detail sentence on ONE line: the token says WHICH level, the sentence says which
            //   branch produced it, and neither can carry the claim alone.
            //
            //   the broker earns the application-layer level, and the half that cannot be faked is the
            //   REFUSAL of an anonymous second connection — that is what says the peer REQUIRES an
            //   identity rather than merely tolerating one.
            Assert.Contains(
                reportLines,
                line =>
                    line.Contains($"service '{BrokerName}'", StringComparison.Ordinal)
                    && line.Contains("level AuthenticatedRoundTrip", StringComparison.Ordinal)
                    && line.Contains(
                        "REFUSED the same request on a second connection presenting no client certificate",
                        StringComparison.Ordinal));

            //   the API is confirmed at the TRANSPORT level, which is the honest ceiling for a target
            //   whose application protocol the suite's steps do not reveal to the probe. Pinned so a
            //   silent change in either direction is visible.
            Assert.Contains(
                reportLines,
                line =>
                    line.Contains($"service '{ApiName}'", StringComparison.Ordinal)
                    && line.Contains("level TransportConfirmed", StringComparison.Ordinal)
                    && line.Contains(
                        "the declared client certificate was configured on the handshake with no rejection raised",
                        StringComparison.Ordinal));

            // ── (i) env: carried the HOST's value into the container, and the SUITE enforces it ───
            // The value itself is not asserted from this test, and that is deliberate — measured, the
            // event stream records a capture's STATUS and not its VALUE (§17: a captured value may be a
            // secret), so reading the greeting back out of the buffer is not available and a test that
            // tried was asserting on something the stream does not carry.
            //
            // Instead the value is load-bearing INSIDE the suite, along a chain that starts one hop
            // earlier than a literal declaration would: this PROCESS's environment holds it,
            // `${env:...}` carries it into the container's environment at topology-build time, the API
            // renders it through envsubst, the REST step captures it, the publish step carries it into
            // the payload, and the consumer matches it against the constant. A Verdict.Pass is
            // therefore only reachable if the host's variable reached the container and came back out
            // again — a stronger statement than any assertion this test could make about a log line,
            // and why the Pass above is asserted before anything else.
            //
            // Both ends of that chain are pinned below — the DECLARATION that starts it and the
            // consumer's MATCH that ends it — because they are what makes the Pass mean it. Those two
            // are the tripwires.
            //
            // The third assertion, between them, is not a tripwire and is not offered as one. It
            // states the fixture's own precondition — this process's variable holds the greeting —
            // and once `Verdict.Pass` has been asserted above there is NO reachable state in which it
            // fails: an unset variable throws at topology build (EDGE-008, EnvironmentMapper), a wrong
            // value fails the consumer's match, and nothing between the two mutates it. It documents
            // the chain rather than measuring it, and is kept for that.
            //
            // The declaration is read from the MATERIALISED YAML. That buys no independence from the
            // emitter, and an earlier version of this comment implied it did: Materialise writes
            // SuiteYaml's return verbatim, so reading the file back is equivalent to asserting on the
            // emitter's own output. What the assertion IS worth is untouched by that — it is the only
            // tripwire on the declaration FORM, so an edit that quietly reverts `${env:...}` to a
            // literal, which would still satisfy every other assertion in this row and would silently
            // take requirement (i) back to proving only that the engine copies YAML text into a
            // container, fails here.
            var suiteYaml = File.ReadAllText(Path.Combine(suiteDirectory, "deployment.e2e.yaml"));
            Assert.Contains(
                $"VOUCHFX_GREETING: \"${{env:{EnvGreetingVariable}}}\"",
                suiteYaml,
                StringComparison.Ordinal);

            // The fixture's precondition, stated in code — see above for why it cannot fail here.
            Assert.Equal(EnvGreeting, Environment.GetEnvironmentVariable(EnvGreetingVariable));

            Assert.Contains(
                $"\"$.greeting\": \"{EnvGreeting}\"", suiteYaml, StringComparison.Ordinal);

            // ── (ii) EVIDENCE for the REST leg: the API's own record of who it authenticated ───────
            // Matched on ONE LINE rather than on the joined blob.
            //
            // Not because the capture is expected to hold several lines — measured against the code, it
            // holds exactly one. The probe's API arm completes the TLS handshake and then only READS
            // for a one-second grace before disposing; it sends no request bytes, and nginx writes an
            // access-log entry per completed REQUEST, so the probe contributes no `client_dn=` line at
            // all. The health gate is `type: tcp` and never speaks TLS. The single GET /orders is the
            // only entry, and the SortedSet would collapse byte-identical repeats anyway.
            //
            // It is written this way because the assertion is about ONE REQUEST: identity, outcome and
            // path are properties of the same access-log entry, and splitting them across three
            // `Contains` calls over a concatenation would assert something weaker than intended the
            // moment a second line ever appears — a fixture change, a retry, a probe that starts
            // sending a request. Binding them costs nothing and cannot drift.
            Assert.Contains(
                api.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                line =>
                    line.Contains(
                        $"client_dn=\"CN={TestCertificateAuthority.ClientSubjectCommonName}\"",
                        StringComparison.Ordinal)
                    && line.Contains("verify=SUCCESS", StringComparison.Ordinal)
                    && line.Contains("uri=/orders", StringComparison.Ordinal));

            // ── (iii) EVIDENCE for the Kafka leg: the broker's own record ─────────────────────────
            Assert.Contains(
                $"peerPrincipal 'CN={TestCertificateAuthority.ClientSubjectCommonName}'",
                broker,
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvGreetingVariable, null);
        }
    }

    /// <summary>
    /// The same suite as a plain <c>vouchfx run</c>: exit <b>0</b>, measured on a real process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A separate row because the claim is about a PROCESS EXIT CODE and the row above never
    /// starts one. Inferring exit 0 from <c>Verdict.Pass</c> is a composition, not a measurement,
    /// and this branch has already had to retract one claim that was never measured — so the
    /// published sentence "every step green, flagless exit 0" gets its own process or it does not
    /// get said.
    /// </para>
    /// <para>
    /// It costs a second topology. That is the price of the word "measured".
    /// </para>
    /// <para>
    /// The two rows exercise the SAME declaration — requirement (i)'s <c>${env:NAME}</c> — but a
    /// different process resolves it. Above, the test host itself builds the topology and reads its
    /// own environment; here a child <c>dotnet vouchfx run</c> does, having INHERITED the variable
    /// this row sets on entry. Exit 0 is what measures that inheritance end to end: the
    /// value is load-bearing inside the suite (see requirement (i)'s note above), and a child that
    /// did not receive the variable fails at EDGE-008 rather than passing quietly.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task AllThreeRequirements_AsAPlainVouchfxRun_ExitsZero()
    {
        // Requirement (i)'s value, published the same way row 1 publishes it — see that row's note.
        //
        // MEASURED on net8.0 that the CLI child below sees it: a `dotnet <dll>` child started
        // through ProcessStartInfo with UseShellExecute = false and an untouched Environment
        // dictionary — exactly the shape RunCliAsync uses — inherits a variable the parent set with
        // Environment.SetEnvironmentVariable AFTER its own start.
        //
        // The restore cannot race that child, and the FIRST of these two reasons is the one that
        // carries it: Windows copies a process's environment block at CreateProcess time, so the
        // child's view is fixed before Process.Start returns and no later mutation by this process
        // can reach it. Secondarily — on the non-cancelled path only — the `finally` below runs
        // after RunCliAsync has awaited WaitForExitAsync, so the child has already exited.
        Environment.SetEnvironmentVariable(EnvGreetingVariable, EnvGreeting);
        try
        {
            var suiteDirectory = Materialise(out _);
            var suite = Path.Combine(suiteDirectory, "deployment.e2e.yaml");
            var cli = ResolveCliAssembly();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            var (exitCode, output) = await RunCliAsync(cli, suite, cts.Token);

            _output.WriteLine($"exit code: {exitCode}");
            _output.WriteLine("── CLI output ──\n" + output);

            Assert.Equal(0, exitCode);

            // …and it got there by running the suite, not by skipping it: all three steps appear, and
            // no security-confirmation failure was reported for either secured target.
            foreach (var step in new[] { "call-api", "publish", "consume" })
            {
                Assert.Contains($"step '{step}'", output, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("SecurityConfirmation", output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvGreetingVariable, null);
        }
    }

    private static string ResolveCliAssembly()
    {
        var assemblyDirectory = Path.GetDirectoryName(
            typeof(ThreeRequirementsSuiteDockerTests).Assembly.Location)!;
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
        string cliAssembly, string suitePath, CancellationToken cancellationToken)
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
            // Unconditional, on every exit path — a cancelled or faulted await would otherwise
            // leave a CLI (and the DCP beneath it) running. See ChildProcess.KillTreeQuietly.
            ChildProcess.KillTreeQuietly(process);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixture
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the suite directory — certificates, both server-side artefacts, the YAML — and
    /// returns the path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No class-level stale-directory sweep here, and the omission is considered rather than
    /// missed. The sibling drill classes suffix their directory per ROW, so a row deletes only its
    /// own and material from other rows outlives the run that made it; they need a sweep. Both rows
    /// here share ONE fixed directory and each deletes it on entry, so a sweep at class init would
    /// delete exactly what the next line deletes anyway. The retention window is identical to the
    /// swept classes': the last row's keys survive until the next run of this class, and no longer.
    /// </para>
    /// <para>
    /// <strong>Requirement (i)'s environment variable is NOT set here</strong>, though it is as much
    /// part of the fixture as the files this method writes. It is set by each ROW, on entry, and
    /// restored in that row's <c>finally</c> — because a mutation of process-wide state and its
    /// undo belong to one method, which is how every env-mutating test in this assembly is written.
    /// Setting it PROCESS-wide is safe here: <c>AssemblyInfo.cs</c> carries
    /// <c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c>, so no other test
    /// observes the mutation concurrently, both rows set the same value, and the name is this
    /// class's alone.
    /// </para>
    /// </remarks>
    private static string Materialise(out int brokerHostPort)
    {
        var suiteDirectory = Path.Combine(Path.GetTempPath(), SuiteDirectory);
        if (Directory.Exists(suiteDirectory))
        {
            Directory.Delete(suiteDirectory, recursive: true);
        }

        TestCertificateAuthority.WriteKafkaBrokerSuiteDirectory(suiteDirectory);
        File.WriteAllText(Path.Combine(suiteDirectory, "bash-config"), BrokerEntrypoint);
        File.WriteAllText(Path.Combine(suiteDirectory, "default.conf.template"), ApiTemplate);

        brokerHostPort = ReserveAFreePort();
        File.WriteAllText(
            Path.Combine(suiteDirectory, "deployment.e2e.yaml"), SuiteYaml(brokerHostPort));

        return suiteDirectory;
    }

    /// <summary>
    /// The broker's conditional entrypoint: SSL appended only when the key store is where this
    /// script looks for it — the customer's own shape, and the fixture pattern this slice uses
    /// throughout.
    /// </summary>
    private const string BrokerEntrypoint =
        "set -o nounset -o errexit\n"
        + "if [ -f /etc/kafka/secrets/kafka.keystore.pem ]; then\n"
        + "  export KAFKA_LISTENERS=\"PLAINTEXT://0.0.0.0:9092,SECURE://0.0.0.0:9093,CONTROLLER://0.0.0.0:9094\"\n"
        + "  export KAFKA_ADVERTISED_LISTENERS=\"PLAINTEXT://localhost:9092,SECURE://${VOUCHFX_SECURE_ADVERTISED}\"\n"
        + "  export KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=\"CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT,SECURE:SSL\"\n"
        + "  export KAFKA_SSL_KEYSTORE_TYPE=\"PEM\"\n"
        + "  export KAFKA_SSL_KEYSTORE_LOCATION=\"/etc/kafka/secrets/kafka.keystore.pem\"\n"
        + "  export KAFKA_SSL_TRUSTSTORE_TYPE=\"PEM\"\n"
        + "  export KAFKA_SSL_TRUSTSTORE_LOCATION=\"/etc/kafka/secrets/kafka.truststore.pem\"\n"
        + "  export KAFKA_SSL_CLIENT_AUTH=\"required\"\n"
        + "fi\n";

    /// <summary>
    /// The API's nginx configuration, delivered as a TEMPLATE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The image's own entrypoint renders <c>/etc/nginx/templates/*.template</c> through
    /// <c>envsubst</c> at start-up, which is what carries requirement (i)'s environment variable
    /// into the served response. MEASURED: <c>envsubst</c> there substitutes only variables that
    /// are actually DEFINED in the environment, so nginx's own <c>$ssl_client_s_dn</c> and
    /// <c>$ssl_client_verify</c> survive the pass untouched and are still evaluated by nginx per
    /// request.
    /// </para>
    /// <para>
    /// <c>ssl_verify_client on</c> is the point of the fixture: without it the API would accept an
    /// anonymous caller and requirement (ii) would be satisfied by a suite that presented nothing.
    /// Measured against this configuration, a request with no client certificate is answered
    /// <c>400 No required SSL certificate was sent</c>.
    /// </para>
    /// <para>
    /// The access-log format exists solely to make the authentication auditable from the SERVER's
    /// side — see this file's header.
    /// </para>
    /// </remarks>
    private const string ApiTemplate =
        "log_format mtls '$remote_addr client_dn=\"$ssl_client_s_dn\" verify=$ssl_client_verify uri=$uri';\n"
        + "server {\n"
        + "    listen 8443 ssl;\n"
        + "    server_name localhost;\n"
        + "    ssl_certificate        /etc/nginx/certs/" + TestCertificateAuthority.ServerCertFileName + ";\n"
        + "    ssl_certificate_key    /etc/nginx/certs/" + TestCertificateAuthority.ServerKeyFileName + ";\n"
        + "    ssl_client_certificate /etc/nginx/certs/" + TestCertificateAuthority.CaFileName + ";\n"

        // The TLS floor, stated by the FIXTURE instead of inherited from the image. Measured, the
        // pinned tag ships nginx/1.31.3, on which this line is very likely a no-op — and that is
        // the point rather than an objection: without it the floor is a property of whichever
        // nginx build a reader copies this configuration onto, and older builds default to
        // accepting TLS 1.0. Costs one line; removes the dependency.
        + "    ssl_protocols TLSv1.2 TLSv1.3;\n"
        + "    ssl_verify_client on;\n"
        + "    access_log /dev/stdout mtls;\n"
        + "    location / {\n"
        + "        default_type application/json;\n"
        + "        return 200 '{\"greeting\":\"${VOUCHFX_GREETING}\",\"clientDn\":\"$ssl_client_s_dn\"}';\n"
        + "    }\n"
        + "}\n";

    private static string SuiteYaml(int brokerHostPort) =>
        $$"""
        metadata:
          name: three-requirements
        environment:
          services:
            {{ApiName}}:
              image: nginx:alpine
              ports: [8443]
              healthCheck: { type: tcp, port: 8443 }
              security:
                profile: mtls
                endpoint: "8443"
                caCert: {{TestCertificateAuthority.CaFileName}}
                clientCert: {{TestCertificateAuthority.ClientCertFileName}}
                clientKey: {{TestCertificateAuthority.ClientKeyFileName}}
                serverArtifacts:
                  - source: default.conf.template
                    target: /etc/nginx/templates/default.conf.template
                  - source: {{TestCertificateAuthority.ServerCertFileName}}
                    target: /etc/nginx/certs/{{TestCertificateAuthority.ServerCertFileName}}
                  - source: {{TestCertificateAuthority.ServerKeyFileName}}
                    target: /etc/nginx/certs/{{TestCertificateAuthority.ServerKeyFileName}}
                  - source: {{TestCertificateAuthority.CaFileName}}
                    target: /etc/nginx/certs/{{TestCertificateAuthority.CaFileName}}
              env:
                # REQUIREMENT (i): container configuration through env:, in the shape a bank
                # actually uses — the value lives in the ENGINE HOST's environment (a CI variable)
                # and the suite only NAMES it. `${env:NAME}` is the engine's passthrough for that
                # (REQ-006), resolved from the engine process's own environment at topology-build
                # time; each row sets the variable on entry, before it materialises this file.
                #
                # Declaring it this way rather than as a literal is what makes requirement (i)
                # exercise the capability it is credited with. A literal would prove only that the
                # engine copies YAML text into a container. This proves the whole hop: host
                # environment → engine → container environment → envsubst → nginx → REST capture →
                # publish payload → consumer match.
                #
                # It cannot pass without resolving. An UNSET variable is EDGE-008 — the mapper
                # throws, naming the variable, rather than substituting an empty string — and an
                # unresolved reference passed through verbatim would render the literal
                # `${env:...}` into the response, which the consumer's `match` below (pinned to the
                # constant) would then fail. There is no silent middle.
                #
                # The VALUE must stay one this test chooses, and it does: each row assigns the
                # same compile-time constant the consumer matches, overriding any ambient value. The
                # reason is unchanged from when this was a literal — envsubst renders the template
                # before nginx parses it, so the value is spliced into an nginx CONFIGURATION rather
                # than into a served body, and a value carrying a quote and a brace could close the
                # `return` directive and open a `location` block of its own. A suite interpolating
                # anything genuinely caller-supplied into a config template would have to establish
                # that separately; this one does not, because the caller is this file.
                VOUCHFX_GREETING: "${env:{{EnvGreetingVariable}}}"

                # Restricts envsubst to VOUCHFX_-prefixed NAMES, and it closes a real hole rather
                # than tidying one. The `log_format` line that carries this file's REST evidence
                # sits inside the same rendered template, so without a filter its independence
                # rests on nobody having defined an environment variable called `ssl_client_s_dn`.
                #
                # MEASURED on nginx:alpine, both arms:
                #   no filter   → `-e ssl_client_s_dn=CN=forged -e ssl_client_verify=SUCCESS`
                #                 renders `client_dn="CN=forged" verify=SUCCESS` as LITERALS in the
                #                 log format — the server's evidence line becomes a constant chosen
                #                 by the environment rather than a per-request fact.
                #   ^VOUCHFX_   → the same two variables render as `$ssl_client_s_dn` and
                #                 `$ssl_client_verify`, evaluated by nginx per request, while
                #                 VOUCHFX_GREETING still substitutes.
                #
                # To be exact about what the filter removes: it is the CLASS of environment-driven
                # log-format rewriting, not one specific defeat. The demonstration value above,
                # `CN=forged`, would NOT satisfy this file's assertion, which requires the declared
                # `CN=vouchfx-test-client` — a working forgery would have to inject that exact
                # string. The filter is worth having because it makes the whole class unreachable
                # for one line of configuration, not because a defeat was ever demonstrated
                # end to end.
                NGINX_ENVSUBST_FILTER: "^VOUCHFX_"

            {{BrokerName}}:
              image: confluentinc/cp-kafka:7.6.1

              # ONLY the secured port is published, and the omission is deliberate. The broker
              # still LISTENS on 9092 inside its container — its own inter-broker traffic needs
              # that — but publishing it would put an unauthenticated, unencrypted Kafka admin
              # endpoint on every host interface for the duration of the run (Docker's default
              # publish binding here is 0.0.0.0 and [::]), reachable by anything that can reach
              # this machine. Nothing on the host side needs it: both steps below target the
              # secured endpoint, and the health gate probes 9093.
              ports: ["{{brokerHostPort}}:9093"]
              healthCheck: { type: tcp, port: 9093 }
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
                KAFKA_AUTO_CREATE_TOPICS_ENABLE: "true"
                CLUSTER_ID: "MkU3OEVBNTcwNTJENDM2Qk"
                KAFKA_LOG4J_LOGGERS: "org.apache.kafka.common.network.SslTransportLayer=DEBUG"

                # The broker advertises the PINNED host port, so the consumer below follows the
                # metadata to an address that exists on this machine.
                VOUCHFX_SECURE_ADVERTISED: "localhost:{{brokerHostPort}}"
        steps:
          # REQUIREMENT (ii): a REST call presenting a client certificate to a service whose own
          # certificate chains to the suite's declared private CA.
          - id: call-api
            type: http.rest
            target: {{ApiName}}
            method: GET
            path: /orders
            expect: { status: 200 }
            capture:
              greeting: "$.greeting"
            timeout: 30s

          # The captured value is CARRIED FORWARD, which is what makes requirement (i) load-bearing
          # rather than decorative: this payload contains whatever the API said, and the consumer
          # below matches it against the literal the suite put in `env:`. If the environment
          # variable never reached the container, the consumer finds no match and the run fails.
          - id: publish
            type: mq-publish.kafka
            target: {{BrokerName}}
            topic: orders
            payload: '{"id":"three-requirements","greeting":"{greeting}"}'
            timeout: 30s

          # REQUIREMENT (iii): the CONSUMER — the deployment's requirement is verbatim a consumer,
          # not a producer, and it is the half that needs the advertised address to be reachable.
          - id: consume
            type: mq-expect.kafka
            target: {{BrokerName}}
            topic: orders
            verifyMode: RETRY
            timeout: 60s
            match:
              json:
                "$.id": "three-requirements"
                "$.greeting": "{{EnvGreeting}}"
        """;

    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Accumulates one service's authentication records, from the container THIS run started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The pre-existing snapshot is the whole point of the signature, and its absence was
    /// a false-pass path.</strong> Taking the first name off a
    /// <c>docker ps --filter name=^&lt;resource&gt;-</c> latches whatever Docker lists first, and a
    /// leaked container from an EARLIER run of this same test carries
    /// <c>client_dn="CN=vouchfx-test-client" verify=SUCCESS</c> and
    /// <c>peerPrincipal 'CN=vouchfx-test-client'</c> verbatim — so every evidence assertion in this
    /// file could be satisfied by a container the run never touched. Composed with a probe that
    /// silently downgrades its level, that is a green run proving no authentication at all.
    /// </para>
    /// <para>
    /// Three guards, and they are independent. Names present BEFORE the topology started are
    /// excluded; more than one remaining candidate is waited out rather than chosen between; and
    /// the log itself is read from <paramref name="since"/> forward, so even a container latched in
    /// error can only contribute records written during THIS run. The third matters because this
    /// file creates the leak the first two defend against: the CLI row hard-kills its child process
    /// tree on cancellation, which bypasses the topology's disposal chokepoint and can orphan a
    /// container carrying exactly the lines asserted here.
    /// </para>
    /// </remarks>
    private static async Task<string> CaptureAsync(
        string resource,
        IReadOnlySet<string> preExisting,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        string? container = null;
        var records = new SortedSet<string>(StringComparer.Ordinal);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (container is null)
            {
                var candidates = (await DockerAsync(
                        $"ps --filter \"name=^{resource}-\" --filter status=running --format \"{{{{.Names}}}}\"",
                        cancellationToken))
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(name => !preExisting.Contains(name))
                    .ToArray();

                if (candidates.Length == 1)
                {
                    container = candidates[0];
                }
            }

            if (container is not null)
            {
                await HarvestAsync(container, records, since, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // One last harvest after cancellation, on its own budget. It is needed for the BROKER: the
        // consume step's handshake lands in the run's final moments, `watcherStop.Cancel()` fires
        // in the `finally` the instant the run returns, and the poll interval is 250 ms — so the
        // last in-loop poll can genuinely predate the record.
        //
        // It is NOT needed for the API, and saying otherwise would be wrong: `call-api` is step 1
        // of 3, followed by a publish and a RETRY consume with a 60 s budget, and nginx writes its
        // access log unbuffered — so that line exists seconds to a minute before the run returns,
        // with dozens of polls after it. The call is made for both because it is the same helper
        // and costs one `docker logs` per service; only one of them earns it.
        //
        // Best-effort either way: the topology is already disposing, so this often finds the
        // container gone, and then what was accumulated stands.
        if (container is not null)
        {
            using var lastCall = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await HarvestAsync(container, records, since, lastCall.Token).ConfigureAwait(false);
        }

        return string.Join("\n", records);
    }

    private static async Task HarvestAsync(
        string container,
        SortedSet<string> records,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var log = await DockerAsync(
                $"logs --since {DockerLogWindow.Since(since)} {container}", cancellationToken)
            .ConfigureAwait(false);

        foreach (var line in log.Split(
                     '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Contains("peerPrincipal", StringComparison.Ordinal)
                || line.Contains("client_dn=", StringComparison.Ordinal))
            {
                records.Add(line);
            }
        }
    }

    /// <summary>The running containers currently carrying one service's DCP name prefix.</summary>
    private static async Task<IReadOnlySet<string>> ListContainersAsync(
        string resource, CancellationToken cancellationToken) =>
        (await DockerAsync(
                $"ps --filter \"name=^{resource}-\" --filter status=running --format \"{{{{.Names}}}}\"",
                cancellationToken))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

    private static int CountEvents(IEnumerable<string> buffer, string eventType) =>
        buffer.Count(line => line.Contains($"\"type\":\"{eventType}\"", StringComparison.Ordinal));

    private static int ReserveAFreePort()
    {
        var probe = new TcpListener(IPAddress.Any, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
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

            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return process.ExitCode == 0 ? await stdout : string.Empty;
            }
            finally
            {
                // Issue #378. The catch below returns on cancellation while the child is still
                // running, and `using` disposes the Process OBJECT rather than the process — so
                // before this block a cancelled docker command left one behind. Cheap for the
                // short reads here, and load-bearing for the arbitrary-duration `docker exec`
                // that KafkaSecurityConfirmationDrillDockerTests runs through the same shape (its
                // DockerExecAsync; no other class in this assembly has one).
                ChildProcess.KillTreeQuietly(process);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException
                                       or System.ComponentModel.Win32Exception
                                       or InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
