// Issue #367 — a declared `timeout` is an UPPER BOUND for `mq-publish.kafka`, measured.
//
// WHAT WAS BROKEN. Two filed measurements bracketed the defect: a step declaring `timeout: 20s`
// concluded at 30027 ms, and one declaring `timeout: 10s` concluded at 20099 ms. Both overran by
// exactly ten seconds, and that constant — not a proportion, not librdkafka's 30 s anything — is
// the whole diagnosis. The emitted helper's `finally` ran an unconditional
// `producer.Flush(TimeSpan.FromSeconds(10))`. The filing's hypothesis, that librdkafka ignores the
// step's cancellation token, was WRONG: `ProduceAsync(topic, msg, ct)` observes the token and
// returns at the budget. It is the teardown that then sat for ten more seconds waiting to flush a
// message the broker was never going to accept, and the engine's own stopwatch is still running
// while it does — the step's block has not returned yet.
//
// WHY THIS NEEDS A CONTAINER. The overrun only appears when a message is genuinely STUCK: queued,
// unacknowledged, and undeliverable. A produce that succeeds leaves nothing outstanding and flushes
// in microseconds; a produce that fails fast leaves nothing outstanding either. The reliable shape
// is a broker reachable for bootstrap but not for the partition leader — a container-run broker
// advertising `localhost:9092` while the orchestrator publishes it on some other host port. The
// client completes metadata (positive evidence the broker answered) and then cannot reach the
// leader, so the message sits in the queue for the whole budget and beyond. That is the same
// topology KafkaServiceTargetDockerTests documents; this file reuses its shape and measures the
// one thing that file deliberately left open.
//
// Run with: dotnet test --filter "requires=docker". Excluded from the unit-CI job.
using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.Kafka;
using Vouchfx.Steps.MqPublish.Kafka;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Measures that an <c>mq-publish.kafka</c> step against an unreachable partition leader concludes
/// within its declared <c>timeout</c> plus a small engine grace (#367).
/// </summary>
public sealed class KafkaStepTimeoutBoundDockerTests
{
    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";

    private const string BrokerName = "kafka-timeout-bound-broker";

    private const string StepId = "publish";

    /// <summary>The step's declared budget, in milliseconds — <c>timeout: 10s</c> below.</summary>
    private const long BudgetMs = 10_000;

    /// <summary>
    /// The engine grace allowed on top of the declared budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 2000 ms, and the number is chosen to sit in the wide gap between the fixed tail the fix
    /// leaves and the defect it replaces — not tuned to a passing run.
    /// </para>
    /// <para>
    /// What is left after the budget is bounded work, MEASURED in isolation against an unreachable
    /// broker on Confluent.Kafka 2.14.2 with a 5000 ms token: <c>ProduceAsync</c> returned at
    /// 5025 ms (+25 ms), the token-linked flush was cut at 115 ms (librdkafka polls its queue in
    /// 100 ms slices, so one slice is the worst case), and <c>Dispose()</c> cost 94 ms — 5229 ms
    /// total, a 229 ms tail. On top of that sits CSX bookkeeping and the runner's own event write.
    /// 2000 ms is roughly 8x the measured tail, which absorbs a loaded CI host, and is still five
    /// times smaller than the 10 000 ms overrun this test exists to catch — so the assertion cannot
    /// pass while the defect is present, whatever the host is doing.
    /// </para>
    /// <para>
    /// SHARED MEASUREMENT: the ~250 ms tail above is the same figure that sizes the Docker-free
    /// sibling pin (<c>MqPublishKafkaEmitTests</c>'s
    /// <c>Emit_CompileAndRun_GovernedStepAgainstARefusedPeer_ConcludesAtItsBudget</c>). The two
    /// constants are deliberately duplicated rather than hoisted into a shared TestSupport
    /// constant: they live in different assemblies and each is sized relative to its own budget
    /// (10 s here, 2 s there), so a single shared number would fit neither.
    /// </para>
    /// </remarks>
    private const long GraceMs = 2_000;

    private readonly ITestOutputHelper _output;

    public KafkaStepTimeoutBoundDockerTests(ITestOutputHelper output) => _output = output;

    private static readonly System.Reflection.Assembly[] s_providerAssemblies =
    {
        typeof(MqPublishKafkaProvider).Assembly,
        typeof(MqExpectKafkaProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    /// <summary>
    /// A single-node KRaft broker advertising <c>localhost:9092</c>, which is NOT the host-side
    /// port the orchestrator publishes: bootstrap succeeds, the partition leader is unreachable,
    /// and the produce stays queued for the whole budget.
    /// </summary>
    private const string Yaml = """
        metadata:
          name: kafka-step-timeout-bound
        environment:
          services:
            kafka-timeout-bound-broker:
              image: confluentinc/cp-kafka:7.6.1
              ports: [9092]
              healthCheck: { type: tcp, port: 9092 }
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
        steps:
          - id: publish
            type: mq-publish.kafka
            target: kafka-timeout-bound-broker
            topic: orders
            payload: '{"id":"timeout-bound-1"}'
            timeout: 10s
        """;

    [Fact]
    [Trait("requires", "docker")]
    public async Task PublishAgainstAnUnreachableLeader_ConcludesWithinItsDeclaredTimeout()
    {
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(Yaml), s_registry);
        var kafkaTargets = SuiteProtocolTargets.KafkaSpeaking(ast);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        await using var topology = await SuiteTopology.StartAsync(
            ast.Environment,
            AppHostAssemblyName,
            startupTimeout: TimeSpan.FromMinutes(3),
            seedBaseDirectory: Directory.GetCurrentDirectory(),
            securityConfiguration: null,
            kafkaSpeakingTargets: kafkaTargets,
            cancellationToken: cts.Token);

        _output.WriteLine($"staged svc::{BrokerName} = '{topology.DiscoveredServices[BrokerName]}'");

        var sw = new StringWriter();
        var verdict = await ScenarioRunner.RunScenarioAgainstKeptTopologyAsync(
            topology,
            new NullScenarioIsolation(),
            s_registry,
            ast,
            Yaml,
            "kafka-step-timeout-bound",
            sw,
            resetAndReseed: false,
            seedBaseDirectory: Directory.GetCurrentDirectory(),
            cancellationToken: cts.Token);

        var rendered = sw.ToString();
        _output.WriteLine($"verdict={verdict}\n{rendered}");

        // The step must have reached the broker at all: the staging defect KafkaServiceTargetDockerTests
        // pins would short-circuit this test into a fast EnvironmentError that trivially satisfies the
        // timing assertion below, so rule it out before reading the clock.
        Assert.DoesNotContain("bootstrap not found", rendered, StringComparison.Ordinal);

        var (stepVerdict, durationMs) = ReadStepLine(rendered, StepId);
        _output.WriteLine(
            $"MEASURED: step '{StepId}' concluded {stepVerdict} in {durationMs} ms " +
            $"(budget {BudgetMs} ms, grace {GraceMs} ms, overrun {durationMs - BudgetMs} ms)");

        // FIRST, the floor — because it is the assertion that explains itself. A conclusion far
        // BELOW the budget means the produce failed fast rather than staying queued, i.e. the
        // topology stopped reproducing the stuck-message shape and nothing below measures the
        // defect any more. Asserted ahead of the verdict so that a non-reproducing broker fails
        // with this diagnosis rather than with a bare verdict mismatch several lines down, which
        // would read as a product defect. Half the budget is a generous floor for "the step really
        // did spend its budget waiting".
        Assert.True(
            durationMs >= BudgetMs / 2,
            $"step '{StepId}' concluded in {durationMs} ms, far below its {BudgetMs} ms budget: the " +
            "broker is no longer producing the unreachable-leader shape this test needs, so the " +
            "upper-bound assertion is not measuring the defect.");

        // §12.1: a timeout is Inconclusive, never Fail. The filing confirms this half was already
        // correct; it is asserted so a timing fix cannot silently trade the verdict away.
        //
        // The literal is the frozen taxonomy TOKEN, not the CLR enum name — `VerdictJsonConverter`
        // writes PASS / FAIL / ENV_ERROR / INCONCLUSIVE, and `nameof(Verdict.EnvironmentError)`
        // would not match its token at all. Spelled the same way the renderer tests spell it.
        Assert.Equal("INCONCLUSIVE", stepVerdict);

        // The contract #232 delivered and #367 broke: the declared timeout is an upper bound, so a
        // suite's runtime is bounded by the sum of its declared timeouts.
        Assert.True(
            durationMs <= BudgetMs + GraceMs,
            $"step '{StepId}' declared a {BudgetMs} ms timeout but concluded in {durationMs} ms — " +
            $"an overrun of {durationMs - BudgetMs} ms against an allowed grace of {GraceMs} ms (#367).");
    }

    /// <summary>
    /// Reads the terminal renderer's per-step line — <c>step 'id': VERDICT (N ms)</c> — and returns
    /// the verdict token and duration. This is deliberately the same rendered line the filing
    /// quotes (<c>step 'publish': INCONCLUSIVE (30027 ms)</c>), so the measurement here is
    /// comparable with the numbers in the issue rather than merely adjacent to them.
    /// </summary>
    private static (string Verdict, long DurationMs) ReadStepLine(string rendered, string stepId)
    {
        // The verdict token is optionally wrapped in ANSI colour when the renderer decorates, so
        // the escapes are matched and discarded rather than assumed absent — the assertion is
        // about the timing, and it must not turn red over a rendering mode.
        const string Ansi = @"(?:\x1b\[[0-9;]*m)?";

        var match = Regex.Match(
            rendered,
            @"step '" + Regex.Escape(stepId) + @"': " + Ansi + @"(?<verdict>[A-Z_]+)" + Ansi +
            @" \((?<ms>\d+) ms\)",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(
            match.Success,
            $"no rendered step line for '{stepId}' with a duration was found in:\n{rendered}");

        return (
            match.Groups["verdict"].Value,
            long.Parse(match.Groups["ms"].Value, CultureInfo.InvariantCulture));
    }
}
