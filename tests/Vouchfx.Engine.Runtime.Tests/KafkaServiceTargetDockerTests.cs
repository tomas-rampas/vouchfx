// REQ-011 + REQ-023 (as amended 2026-08-04) — a Kafka broker authored as a SERVICE, proven by
// execution against a real broker (authenticated-infrastructure-mtls, slice E; peer review fix
// round three, MAJOR-2).
//
// WHAT WAS BROKEN, and why inspection could not have settled it. `mq-publish.kafka` and
// `mq-expect.kafka` have accepted a `target` naming a declared SERVICE since REQ-011 — the shape
// the target deployment uses, because the customer's broker runs its own entrypoint and
// configuration — while the engine staged a service's endpoint at `svc::<name>` and both providers
// read `conn::<name>`. A service-targeted suite therefore validated, confirmed green at REQ-005's
// probe, and then failed on its first step with `kafka bootstrap not found for key 'conn::<name>'`.
// Two unit-testable halves fix it (the staged FORM and the emitted KEY), and a third thing — that
// the value the engine stages is one a Kafka client can actually use — is only observable against
// a real broker. That is what this file measures.
//
// MEASURED HERE, in order:
//   1. the value staged at svc::<broker> is a bare `host:port` authority carrying NO scheme;
//   2. a Kafka client can complete an ApiVersions round trip against exactly that value, with no
//      transformation of any kind — REQ-023's own rule, that a provider must never have to rewrite
//      what the engine staged;
//   3. a compiled `mq-publish.kafka` step targeting that service runs and does NOT fail with
//      `kafka bootstrap not found` — the exact pre-fix failure.
//
// A LIMIT THIS FILE DOES NOT PAPER OVER, because it is real and it is separate. Step 3 does not
// reach a PASS, and the reason has nothing to do with the staging fix: a Kafka client resolves the
// partition leader from the broker's own `advertised.listeners`, and a container-run broker cannot
// know the host-side port the orchestrator will allocate for it, so it must advertise an address
// the host cannot reach. MEASURED: a produce against this exact topology spends its whole budget
// on `Connect to ipv4#127.0.0.1:9092 failed` — the ADVERTISED address, which appears nowhere in
// the staged bootstrap, so even that failure is positive evidence the broker answered the client's
// metadata request. The engine's own probe is immune to this (it speaks to the connected broker
// and nothing else, by design — see SecuredEndpointProbe's header, which records rejecting
// AdminClient.GetMetadata for exactly this reason). Closing it for a step needs a way to publish a
// service endpoint on a predictable host port, which this release's language has no field for.
//
// Run with: dotnet test --filter "requires=docker". Excluded from the unit-CI job.
using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.Kafka;
using Vouchfx.Steps.MqPublish.Kafka;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// The customer's shape, executed: a Kafka broker declared under <c>environment.services</c>, and
/// a Kafka step that reaches it.
/// </summary>
public sealed class KafkaServiceTargetDockerTests
{
    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";

    private const string BrokerName = "kafka-service-broker";

    private readonly ITestOutputHelper _output;

    public KafkaServiceTargetDockerTests(ITestOutputHelper output) => _output = output;

    private static readonly System.Reflection.Assembly[] s_providerAssemblies =
    {
        typeof(MqPublishKafkaProvider).Assembly,
        typeof(MqExpectKafkaProvider).Assembly,
    };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    /// <summary>
    /// A single-node KRaft broker declared as a SERVICE — never as the engine-provisioned
    /// <c>kafka</c> dependency type, which is the whole point.
    /// </summary>
    private const string Yaml = """
        metadata:
          name: kafka-service-target
        environment:
          services:
            kafka-service-broker:
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
            target: kafka-service-broker
            topic: orders
            payload: '{"id":"service-target-1"}'
            timeout: 20s
        """;

    [Fact]
    [Trait("requires", "docker")]
    public async Task KafkaStepTargetingADeclaredService_ResolvesAWorkingBootstrapAndReachesTheBroker()
    {
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(Yaml), s_registry);
        var kafkaTargets = SuiteProtocolTargets.KafkaSpeaking(ast);
        Assert.Contains(BrokerName, kafkaTargets);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        await using var topology = await SuiteTopology.StartAsync(
            ast.Environment,
            AppHostAssemblyName,
            startupTimeout: TimeSpan.FromMinutes(3),
            seedBaseDirectory: Directory.GetCurrentDirectory(),
            securityConfiguration: null,
            kafkaSpeakingTargets: kafkaTargets,
            cancellationToken: cts.Token);

        // ── 1. The staged FORM (REQ-023 as amended) ───────────────────────────────────────
        var staged = Assert.IsType<string>(topology.DiscoveredServices[BrokerName]);
        _output.WriteLine($"staged svc::{BrokerName} = '{staged}'");

        Assert.DoesNotContain("://", staged, StringComparison.Ordinal);
        var separator = staged.LastIndexOf(':');
        Assert.True(separator > 0, $"staged value '{staged}' is not a host:port authority");
        var host = staged[..separator];
        var port = int.Parse(staged[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        // ── 2. That value IS a working Kafka bootstrap, used verbatim ─────────────────────
        // The whole of REQ-023's amended rule in one assertion: a Kafka client takes what the
        // engine staged and connects, with nothing stripped, prefixed or rewritten. Hand-framed
        // rather than driven through Confluent.Kafka for the same reason SecuredEndpointProbe
        // hand-frames its own: an AdminClient follows advertised.listeners and would measure the
        // broker's deployment configuration instead of the engine's staging.
        //
        // Splitting the authority into a host and a port is not one of those rewrites — it is what
        // any client does before it opens a socket — but the split above is ad hoc where the
        // engine's is not, and it stops one step short. SecuredEndpointProbe.TryParseBareAuthority
        // hands the value to Uri and reads DnsSafeHost, which yields '::1' for '[::1]:9093';
        // LastIndexOf(':') keeps the brackets.
        //
        // The brackets would NOT break this connect, and claiming they would is a false consequence
        // this branch has carried before. MEASURED on net8.0: IPAddress.TryParse("[::1]") returns
        // true yielding ::1, and TcpClient.ConnectAsync("[::1]", port) against an IPv6 loopback
        // listener connected in 4 ms. The reasons to normalise anyway are display correctness,
        // parity with the form the engine's own parser produces, and that a staged value is
        // ultimately consumed by clients with no obligation to match .NET's leniency — librdkafka
        // takes the bootstrap string verbatim.
        //
        // Nothing in the runs observed here stages an IPv6 literal (the recorded live measurement is
        // 'tcp://localhost:60081'), so this branch is not exercised today; a host whose orchestrator
        // published an IPv6 loopback endpoint would exercise it. The STAGED value itself is
        // untouched: it is what REQ-023 pins, and the assertions above read it in exactly the form
        // the engine produced.
        var connectHost = host.Length >= 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
        var errorCode = await KafkaApiVersionsAsync(connectHost, port, cts.Token);
        _output.WriteLine($"ApiVersions against the staged value: error_code={errorCode}");
        Assert.Equal(0, errorCode);

        // ── 3. The compiled step reads that key, and never reports it missing ─────────────
        var sw = new StringWriter();
        var verdict = await ScenarioRunner.RunScenarioAgainstKeptTopologyAsync(
            topology,
            new NullScenarioIsolation(),
            s_registry,
            ast,
            Yaml,
            "kafka-service-target",
            sw,
            resetAndReseed: false,
            seedBaseDirectory: Directory.GetCurrentDirectory(),
            cancellationToken: cts.Token);

        var rendered = sw.ToString();
        _output.WriteLine($"verdict={verdict}\n{rendered}");

        // The pre-fix failure, named exactly. Before this change the step never contacted the
        // broker at all: it looked up conn::<name>, found nothing, and reported this.
        Assert.DoesNotContain("bootstrap not found", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("conn::" + BrokerName, rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sends one Kafka <c>ApiVersions</c> v0 request over a plain TCP connection and returns the
    /// reply's <c>error_code</c>.
    /// </summary>
    private static async Task<short> KafkaApiVersionsAsync(
        string host, int port, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, cancellationToken);
        var stream = tcp.GetStream();

        await stream.WriteAsync(BuildApiVersionsRequest(1), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var sizeBuffer = new byte[4];
        await stream.ReadExactlyAsync(sizeBuffer, cancellationToken);
        var size = BinaryPrimitives.ReadInt32BigEndian(sizeBuffer);
        Assert.InRange(size, 6, 1 << 20);

        var body = new byte[size];
        await stream.ReadExactlyAsync(body, cancellationToken);

        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(0, 4)));
        return BinaryPrimitives.ReadInt16BigEndian(body.AsSpan(4, 2));
    }

    /// <summary>
    /// Frames one <c>ApiVersions</c> v0 request: length prefix, then request header v1.
    /// </summary>
    private static byte[] BuildApiVersionsRequest(int correlationId)
    {
        var clientId = Encoding.UTF8.GetBytes("vouchfx-service-target-test");
        var payloadLength = 2 + 2 + 4 + 2 + clientId.Length;
        var request = new byte[4 + payloadLength];
        var span = request.AsSpan();
        BinaryPrimitives.WriteInt32BigEndian(span[..4], payloadLength);
        BinaryPrimitives.WriteInt16BigEndian(span.Slice(4, 2), 18);
        BinaryPrimitives.WriteInt16BigEndian(span.Slice(6, 2), 0);
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(8, 4), correlationId);
        BinaryPrimitives.WriteInt16BigEndian(span.Slice(12, 2), (short)clientId.Length);
        clientId.CopyTo(span[14..]);
        return request;
    }
}
