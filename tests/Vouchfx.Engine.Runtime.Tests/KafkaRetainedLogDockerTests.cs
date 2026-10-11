using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Steps.MqExpect.Kafka;
using Vouchfx.Steps.Script.Csharp;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>Real retained records must be visible without waiting for a fresh group's rebalance.</summary>
public sealed class KafkaRetainedLogDockerTests
{
    private readonly ITestOutputHelper _output;

    public KafkaRetainedLogDockerTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(3000, 1, 0)]
    [InlineData(0, 3, 2)]
    [Trait("requires", "docker")]
    public async Task ImmediateDrain_ReadsAcknowledgedRecord_WithDelayedRebalanceOrEmptyPartitions(
        int rebalanceDelayMs, int partitions, int populatedPartition)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka-retained-").FullName;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var topic = "retained-" + Guid.NewGuid().ToString("N");
        try
        {
            var compose = $$"""
                services:
                  broker:
                    image: confluentinc/cp-kafka:7.6.1@sha256:620734d9fc0bb1f9886932e5baf33806074469f40e3fe246a3fdbb59309535fa
                    ports:
                      - target: 9092
                        published: "{{port.ToString(CultureInfo.InvariantCulture)}}"
                        host_ip: 127.0.0.1
                        protocol: tcp
                    environment:
                      KAFKA_NODE_ID: "1"
                      KAFKA_PROCESS_ROLES: broker,controller
                      KAFKA_CONTROLLER_QUORUM_VOTERS: 1@broker:29093
                      KAFKA_CONTROLLER_LISTENER_NAMES: CONTROLLER
                      KAFKA_LISTENERS: INTERNAL://0.0.0.0:29092,EXTERNAL://0.0.0.0:9092,CONTROLLER://0.0.0.0:29093
                      KAFKA_ADVERTISED_LISTENERS: INTERNAL://broker:29092,EXTERNAL://127.0.0.1:{{port.ToString(CultureInfo.InvariantCulture)}}
                      KAFKA_LISTENER_SECURITY_PROTOCOL_MAP: INTERNAL:PLAINTEXT,EXTERNAL:PLAINTEXT,CONTROLLER:PLAINTEXT
                      KAFKA_INTER_BROKER_LISTENER_NAME: INTERNAL
                      KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR: "1"
                      KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR: "1"
                      KAFKA_TRANSACTION_STATE_LOG_MIN_ISR: "1"
                      KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS: "{{rebalanceDelayMs.ToString(CultureInfo.InvariantCulture)}}"
                      CLUSTER_ID: MkU3OEVBNTcwNTJENDM2Qk
                    healthcheck:
                      test: [CMD, cub, kafka-ready, "1", "10", -b, "broker:29092"]
                      interval: 2s
                      timeout: 15s
                      retries: 30
                      start_period: 20s
                """;
            File.WriteAllText(Path.Combine(directory, "compose.yaml"), compose);
            File.WriteAllText(Path.Combine(directory, "environment.yaml"), """
                version: 1
                files: [./compose.yaml]
                startupTimeoutSeconds: 180
                cleanupTimeoutSeconds: 30
                services:
                  broker:
                    type: tcp
                    service: broker
                    port: 9092
                    readiness: { type: healthcheck }
                """);
            var yaml = $$"""
                metadata:
                  name: retained-kafka-drain
                environment:
                  source:
                    driver: compose
                    config: environment.yaml
                steps:
                  - id: publish-acknowledged-record
                    type: script.csharp
                    timeout: 10s
                    code: |
                      var bootstrap = (string)Vars["svc::broker"];
                      var admin = new Confluent.Kafka.AdminClientBuilder(new Confluent.Kafka.AdminClientConfig { BootstrapServers = bootstrap }).Build();
                      try
                      {
                          await admin.CreateTopicsAsync(new[] { new Confluent.Kafka.Admin.TopicSpecification { Name = "{{topic}}", NumPartitions = {{partitions}}, ReplicationFactor = 1 } });
                      }
                      finally { admin.Dispose(); }
                      var producer = new Confluent.Kafka.ProducerBuilder<string, string>(new Confluent.Kafka.ProducerConfig { BootstrapServers = bootstrap, MessageTimeoutMs = 5000 }).Build();
                      try
                      {
                          var delivery = await producer.ProduceAsync(new Confluent.Kafka.TopicPartition("{{topic}}", {{populatedPartition}}), new Confluent.Kafka.Message<string, string> { Key = "expected", Value = "retained-record" }).WaitAsync(TimeSpan.FromSeconds(5));
                          if (delivery.Status != Confluent.Kafka.PersistenceStatus.Persisted) throw new Exception("Record was not acknowledged");
                      }
                      finally { producer.Dispose(); }
                  - id: drain-retained-record
                    type: mq-expect.kafka
                    target: broker
                    topic: {{topic}}
                    verifyMode: IMMEDIATE
                    timeout: 5s
                    match:
                      key: expected
                      payloadContains: retained-record
                """;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var output = new StringWriter();
            var verdict = await ScenarioRunner.RunAsync(yaml, "retained-kafka-drain",
                new[] { typeof(ScriptCsharpProvider).Assembly, typeof(MqExpectKafkaProvider).Assembly },
                "unused-no-dcp", output, seedBaseDirectory: directory, cancellationToken: cancellation.Token);
            _output.WriteLine($"rebalanceDelayMs={rebalanceDelayMs}; partitions={partitions}; populatedPartition={populatedPartition}\n{output}");
            Assert.Equal(Verdict.Pass, verdict);
            Assert.Contains("publish-acknowledged-record", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("drain-retained-record", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            // The normal runner disposes its owned Compose lease, including on assertion failure.
            File.Delete(Path.Combine(directory, "compose.yaml"));
            File.Delete(Path.Combine(directory, "environment.yaml"));
            Directory.Delete(directory);
        }
    }
}
