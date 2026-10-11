using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using Confluent.Kafka;

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
        if (args.Length is < 5 or > 6 || args[3] is not ("tls" or "mtls")) return 2;
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
