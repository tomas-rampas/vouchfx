using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace Vouchfx.Kafka.Security.Probe;

// Native clients deliberately live in a child, separate from collectible provider execution.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["wait-for-termination"])
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        if (args.Length is < 5 or > 7 || args[3] is not ("tls" or "mtls")) return 2;
        var mode = args[0];
        var bootstrap = args[1];
        var directory = Path.GetFullPath(args[2]);
        var profile = args[3];
        var report = Path.GetFullPath(args[4]);
        if (Path.GetDirectoryName(report) != directory) return 2;
        var configuration = new AdminClientConfig
        {
            BootstrapServers = bootstrap,
            SecurityProtocol = SecurityProtocol.Ssl,
            SslCaLocation = Path.Combine(directory, "ca.pem"),
            SslCertificateLocation = profile == "mtls" ? Path.Combine(directory, "client.pem") : null,
            SslKeyLocation = profile == "mtls" ? Path.Combine(directory, "client-key.pem") : null,
            SocketTimeoutMs = 3000,
            SocketConnectionSetupTimeoutMs = 3000,
        };
        try
        {
            switch (mode)
            {
                case "metadata":
                    using (var client = Client(configuration))
                    {
                        var metadata = client.GetMetadata(TimeSpan.FromSeconds(10));
                        var actual = metadata.Brokers.Single();
                        Write(report, new { bootstrap, actual.Host, actual.Port });
                    }
                    break;
                case "negative" when args.Length == 6:
                    var foreign = Path.GetFullPath(args[5]);
                    var wrongCa = Rejected(new AdminClientConfig(configuration) { SslCaLocation = Path.Combine(foreign, "ca.pem") });
                    var plaintext = Rejected(new AdminClientConfig(configuration) { SecurityProtocol = SecurityProtocol.Plaintext });
                    bool? missingClient = profile == "mtls" ? Rejected(new AdminClientConfig(configuration)
                    { SslCertificateLocation = null, SslKeyLocation = null }) : null;
                    bool? foreignClient = profile == "mtls" ? Rejected(new AdminClientConfig(configuration)
                    {
                        SslCertificateLocation = Path.Combine(foreign, "client.pem"),
                        SslKeyLocation = Path.Combine(foreign, "client-key.pem"),
                    }) : null;
                    var authority = new Uri("tcp://" + bootstrap);
                    using (var socket = new TcpClient())
                    using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    {
                        await socket.ConnectAsync(authority.Host, authority.Port, budget.Token);
                        var nameMismatchObserved = false;
                        using var tls = new SslStream(socket.GetStream(), false, (_, _, _, errors) =>
                        {
                            nameMismatchObserved = (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0;
                            return false;
                        });
                        var wrongHostname = false;
                        try
                        {
                            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "wrong-host.invalid" }, budget.Token);
                        }
                        catch (System.Security.Authentication.AuthenticationException) { wrongHostname = nameMismatchObserved; }
                        Write(report, new { wrongCa, plaintext, wrongHostname, missingClient, foreignClient });
                        if (!wrongCa || !plaintext || !wrongHostname || profile == "mtls" && (missingClient != true || foreignClient != true)) return 2;
                    }
                    break;
                case "consumer-state" when args.Length == 6:
                    using (var admin = Client(configuration))
                    using (var consumer = ObserverConsumer(configuration, "vouchfx-observer-" + Guid.NewGuid().ToString("n")))
                    {
                        var metadata = admin.GetMetadata(args[5], TimeSpan.FromSeconds(5));
                        var topic = metadata.Topics.Single();
                        if (topic.Error.IsError) throw new KafkaException(topic.Error);
                        var broker = metadata.Brokers.Single();
                        var groups = await admin.ListConsumerGroupsAsync(new ListConsumerGroupsOptions { RequestTimeout = TimeSpan.FromSeconds(5) });
                        var partitions = new List<object>();
                        foreach (var partition in topic.Partitions)
                        {
                            if (partition.Error.IsError) throw new KafkaException(partition.Error);
                            var target = new TopicPartition(topic.Topic, partition.PartitionId);
                            var bounds = consumer.QueryWatermarkOffsets(target, TimeSpan.FromSeconds(5));
                            long? readOffset = null;
                            if (bounds.High > bounds.Low)
                            {
                                consumer.Assign(new TopicPartitionOffset(target, bounds.Low));
                                var record = consumer.Consume(TimeSpan.FromSeconds(5)) ?? throw new TimeoutException();
                                if (record.TopicPartition != target || record.Offset < bounds.Low || record.Offset >= bounds.High)
                                    throw new InvalidOperationException();
                                readOffset = record.Offset.Value;
                                consumer.Unassign();
                            }
                            partitions.Add(new { partition = partition.PartitionId, low = bounds.Low.Value, high = bounds.High.Value, readOffset });
                        }
                        Write(report, new
                        {
                            bootstrap,
                            broker.Host,
                            broker.Port,
                            topic = topic.Topic,
                            groups = groups.Valid.Select(group => group.GroupId).Order(StringComparer.Ordinal).ToArray(),
                            partitions,
                        });
                    }
                    break;
                case "consumer-control" when args.Length == 7 && IsFixtureGroup(args[6]):
                    using (var admin = Client(configuration))
                    using (var consumer = ObserverConsumer(configuration, args[6]))
                    {
                        try
                        {
                            consumer.Subscribe(args[5]);
                            var record = consumer.Consume(TimeSpan.FromSeconds(15)) ?? throw new TimeoutException();
                            consumer.Commit(record);
                            var described = await admin.DescribeConsumerGroupsAsync(new[] { args[6] },
                                new DescribeConsumerGroupsOptions { RequestTimeout = TimeSpan.FromSeconds(5) });
                            var group = described.ConsumerGroupDescriptions.Single();
                            if (group.Error.IsError) throw new KafkaException(group.Error);
                            var joined = group.Members.Any(member => member.ConsumerId == consumer.MemberId);
                            if (!joined || !consumer.Subscription.SequenceEqual(new[] { args[5] })) throw new InvalidOperationException();
                            var offsets = await admin.ListConsumerGroupOffsetsAsync(new[]
                            {
                                new ConsumerGroupTopicPartitions(args[6], new List<TopicPartition> { record.TopicPartition }),
                            }, new ListConsumerGroupOffsetsOptions { RequestTimeout = TimeSpan.FromSeconds(5) });
                            var committed = offsets.Single().Partitions.Single();
                            if (committed.Error.IsError) throw new KafkaException(committed.Error);
                            if (committed.Offset != record.Offset + 1) throw new InvalidOperationException();
                            Write(report, new
                            {
                                bootstrap,
                                group = group.GroupId,
                                member = consumer.MemberId,
                                joined,
                                topic = record.Topic,
                                partition = record.Partition.Value,
                                consumedOffset = record.Offset.Value,
                                committedOffset = committed.Offset.Value,
                            });
                        }
                        finally { consumer.Close(); }
                    }
                    break;
                case "delete-control" when args.Length == 6 && IsFixtureGroup(args[5]):
                    using (var admin = Client(configuration))
                    {
                        await admin.DeleteGroupsAsync(new[] { args[5] }, new DeleteGroupsOptions { RequestTimeout = TimeSpan.FromSeconds(5) });
                        Write(report, new { bootstrap, deletedGroup = args[5] });
                    }
                    break;
                default: return 2;
            }
            return 0;
        }
        catch (Exception failure)
        {
            // Native errors can quote paths or material. Only the error type crosses the process boundary.
            Write(report, new { errorType = failure.GetType().Name });
            return 1;
        }
    }

    private static bool IsFixtureGroup(string group)
        => group.StartsWith("vouchfx-expect-", StringComparison.Ordinal)
            && Guid.TryParseExact(group["vouchfx-expect-".Length..], "n", out _);

    private static IConsumer<byte[], byte[]> ObserverConsumer(AdminClientConfig configuration, string group)
        => new ConsumerBuilder<byte[], byte[]>(new ConsumerConfig(configuration)
        {
            GroupId = group,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).SetLogHandler((_, _) => { }).SetErrorHandler((_, _) => { }).Build();

    private static IAdminClient Client(AdminClientConfig configuration)
        => new AdminClientBuilder(configuration).SetLogHandler((_, _) => { }).SetErrorHandler((_, _) => { }).Build();

    private static bool Rejected(AdminClientConfig configuration)
    {
        using var client = Client(configuration);
        try { client.GetMetadata(TimeSpan.FromSeconds(4)); return false; }
        catch (KafkaException) { return true; }
    }

    private static void Write(string report, object result) => File.WriteAllText(report, JsonSerializer.Serialize(result));
}
