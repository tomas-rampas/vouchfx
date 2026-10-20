using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>TCP and native-health readiness with a fake process and owned loopback listeners, not Docker.</summary>
public sealed class ComposeTcpEnvironmentTests
{
    private const string Canary = "native-health-command-output-canary";

    [Fact]
    public async Task NormalRunner_StagesTcpServiceAndExecutesOnlyAfterNativeHealthyReadiness()
    {
        using var inputs = new Inputs("healthcheck");
        await using var listener = new ReadyTcpServer();
        var marker = Path.Combine(inputs.DirectoryPath, "step-executed");
        var process = new FakeProcess(listener.Port)
        {
            StartingInspections = 1,
            OnHealthInspection = _ => Assert.False(File.Exists(marker)),
        };
        var yaml = $$"""
            environment:
              source: { driver: compose, config: {{JsonSerializer.Serialize(inputs.Config)}} }
            steps:
              - id: verify-service-staging
                type: script.csharp
                code: |
                  if ((string)Vars["svc::broker"] != "127.0.0.1:{{listener.Port}}") throw new Exception("wrong service endpoint");
                  if (Vars.ContainsKey("conn::broker")) throw new Exception("service incorrectly staged as dependency");
                  System.IO.File.WriteAllText({{JsonSerializer.Serialize(marker)}}, "executed");
            """;
        var output = new StringWriter();
        var verdict = await ScenarioRunner.RunAsync(yaml, "compose-tcp-staging",
            new[] { typeof(ScriptCsharpProvider).Assembly }, "missing-host-no-dcp", output,
            environmentDrivers: new EnvironmentDriverRegistry(new[] { new TestComposeDriver(inputs, process) }));
        Assert.Equal(Verdict.Pass, verdict);
        Assert.Equal("executed", File.ReadAllText(marker));
        Assert.True(process.HealthInspections >= 2);
        AssertClean(inputs, process);
    }

    [Fact]
    public async Task TcpReachability_StagesBareServiceEndpointWithoutNativeHealthcheck()
    {
        using var inputs = new Inputs("tcp");
        await using var listener = new ReadyTcpServer();
        var process = new FakeProcess(listener.Port) { Healthcheck = null };
        var lease = await Start(inputs, process);
        Assert.Equal($"127.0.0.1:{listener.Port}", lease.DiscoveredServices["broker"]);
        Assert.Empty(lease.DependencyNames);
        Assert.Empty(lease.DependencyTypes);
        Assert.Equal(EnvironmentStatePolicy.FreshOwned, lease.StatePolicy);
        Assert.Equal(0, process.HealthInspections);
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        AssertClean(inputs, process);
        Assert.Equal(1, process.RemovedContainers);
        Assert.DoesNotContain(process.Calls, c => c.Arguments.Contains("down"));
    }

    [Fact]
    public async Task NativeHealthyAndReachable_OnlyReturnsLeaseAfterHealthyTransition()
    {
        using var inputs = new Inputs("healthcheck");
        await using var listener = new ReadyTcpServer();
        var process = new FakeProcess(listener.Port) { StartingInspections = 1 };
        var lease = await Start(inputs, process);
        Assert.True(process.HealthInspections >= 2);
        Assert.Equal($"127.0.0.1:{listener.Port}", lease.DiscoveredServices["broker"]);
        Assert.All(process.Calls.Where(c => c.Arguments[0] == "compose" && !c.Arguments.Contains("config")),
            c => Assert.Equal(process.NativeModel, c.Input));
        var journal = Assert.Single(Directory.GetFiles(inputs.Journals));
        Assert.DoesNotContain(Canary, File.ReadAllText(journal), StringComparison.Ordinal);
        Assert.All(process.Calls.Where(c => c.IsHealthInspection), c =>
        {
            Assert.Contains(".State.Health.Status", c.Arguments[3], StringComparison.Ordinal);
            Assert.DoesNotContain(".State.Health.Log", c.Arguments[3], StringComparison.Ordinal);
            Assert.DoesNotContain(".Config.Healthcheck", c.Arguments[3], StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, string.Join(" ", c.Arguments), StringComparison.Ordinal);
        });
        await lease.DisposeAsync();
        AssertClean(inputs, process);
    }

    [Fact]
    public async Task TcpListenerWithoutHealthyNativeCheck_CannotSatisfyHealthcheckReadiness()
    {
        using var inputs = new Inputs("healthcheck", startupSeconds: 1);
        await using var listener = new ReadyTcpServer();
        var process = new FakeProcess(listener.Port) { HealthStatus = "unhealthy" };
        var error = await Assert.ThrowsAsync<OrchestrationException>(() => Start(inputs, process));
        Assert.Contains("readiness failed", error.Message, StringComparison.Ordinal);
        Assert.True(process.HealthInspections > 0);
        AssertClean(inputs, process);
    }

    [Fact]
    public async Task HealthyNativeCheckWithoutReachableHostPort_CannotReturnLease()
    {
        using var inputs = new Inputs("healthcheck", startupSeconds: 1);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        var process = new FakeProcess(port);
        var error = await Assert.ThrowsAsync<OrchestrationException>(() => Start(inputs, process));
        Assert.Contains("readiness failed", error.Message, StringComparison.Ordinal);
        Assert.True(process.HealthInspections > 0);
        AssertClean(inputs, process);
    }

    [Theory]
    [InlineData("{\"disable\":false,\"test\":[\"CMD\",\"probe\",\"\"]}")]
    [InlineData("{\"test\":[\"CMD-SHELL\",\"probe\"]}")]
    public async Task ExplicitNativeHealthcheckForms_WithTrailingInspectionNewline_AreAccepted(string healthcheck)
    {
        using var inputs = new Inputs("healthcheck");
        await using var listener = new ReadyTcpServer();
        var process = new FakeProcess(listener.Port) { Healthcheck = healthcheck, InspectionShape = "trailing-newline" };
        var lease = await Start(inputs, process);
        Assert.Equal($"127.0.0.1:{listener.Port}", lease.DiscoveredServices["broker"]);
        await lease.DisposeAsync();
        AssertClean(inputs, process);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"disable\":true,\"test\":[\"CMD\",\"probe\"]}")]
    [InlineData("{\"test\":[\"NONE\"]}")]
    [InlineData("{\"test\":[]}")]
    [InlineData("{\"test\":[\"CMD\"]}")]
    [InlineData("{\"test\":[\"CMD-SHELL\",\"\"]}")]
    [InlineData("{\"test\":[\"UNKNOWN\",\"probe\"]}")]
    [InlineData("{\"test\":\"native-health-command-output-canary\"}")]
    [InlineData("{\"test\":[\"CMD\",5]}")]
    public async Task MissingOrDisabledOrInvalidNativeHealthcheck_RefusesBeforeUpAndJournal(string? healthcheck)
    {
        using var inputs = new Inputs("healthcheck");
        var process = new FakeProcess(1) { Healthcheck = healthcheck };
        var error = await Assert.ThrowsAsync<TopologyAuthoringException>(() => Start(inputs, process));
        Assert.Contains("healthcheck", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(process.Calls, c => c.Arguments.Contains("up"));
        Assert.False(Directory.Exists(inputs.Journals));
        Assert.False(process.HasOwnedResources);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"target\":9092,\"protocol\":\"udp\"}]")]
    [InlineData("[{\"target\":9093,\"protocol\":\"tcp\"}]")]
    [InlineData("[{\"target\":9092},{\"target\":9092}]")]
    [InlineData("[{\"target\":9092,\"host_ip\":\"::1\"}]")]
    public async Task MissingAmbiguousOrUnsupportedTcpPort_RefusesBeforeUp(string ports)
    {
        using var inputs = new Inputs("tcp");
        var process = new FakeProcess(1) { Ports = ports };
        await Assert.ThrowsAsync<TopologyAuthoringException>(() => Start(inputs, process));
        Assert.DoesNotContain(process.Calls, c => c.Arguments.Contains("up"));
        Assert.False(Directory.Exists(inputs.Journals));
    }

    [Theory]
    [InlineData("\"scale\":2,")]
    [InlineData("\"scale\":0,")]
    [InlineData("\"deploy\":{\"replicas\":2},")]
    [InlineData("\"deploy\":{\"replicas\":0},")]
    public async Task MultipleOrZeroNativeReplicas_RefusesBeforeUp(string properties)
    {
        using var inputs = new Inputs("tcp");
        var process = new FakeProcess(1) { ServiceProperties = properties };
        var error = await Assert.ThrowsAsync<TopologyAuthoringException>(() => Start(inputs, process));
        Assert.Contains("replicated services", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(process.Calls, c => c.Arguments.Contains("up"));
        Assert.False(Directory.Exists(inputs.Journals));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\nbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task MissingOrAmbiguousHealthContainer_RefusesAndCleansPartialStartup(string inventory)
    {
        using var inputs = new Inputs("healthcheck");
        var process = new FakeProcess(1) { HealthInventory = inventory };
        await Assert.ThrowsAsync<OrchestrationException>(() => Start(inputs, process));
        Assert.Contains(process.Calls, c => c.Arguments.Contains("up"));
        AssertClean(inputs, process);
    }

    [Theory]
    [InlineData("wrong-project")]
    [InlineData("wrong-service")]
    [InlineData("exited")]
    [InlineData("paused")]
    [InlineData("dead")]
    [InlineData("missing-health")]
    [InlineData("invalid-health")]
    [InlineData("malformed")]
    [InlineData("truncated")]
    public async Task UntrustedOrNotRunningHealthInspection_RefusesWithoutLeakingValues(string shape)
    {
        using var inputs = new Inputs("healthcheck");
        var process = new FakeProcess(1) { InspectionShape = shape };
        var error = await Assert.ThrowsAsync<OrchestrationException>(() => Start(inputs, process));
        Assert.DoesNotContain(Canary, error.Message, StringComparison.Ordinal);
        Assert.True(process.HealthInspections > 0);
        AssertClean(inputs, process);
    }

    [Fact]
    public async Task CallerCancellationDuringHealthGate_CleanupUsesIndependentToken()
    {
        using var inputs = new Inputs("healthcheck");
        using var cancellation = new CancellationTokenSource();
        var process = new FakeProcess(1)
        {
            OnHealthInspection = token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Start(inputs, process, cancellation.Token));
        AssertClean(inputs, process);
        Assert.All(process.Calls.Where(c => c.IsCleanup), c => Assert.False(c.Cancelled));
    }

    [Fact]
    public async Task CallerCancellationAfterUpCreatesResource_CleansOwnedResourcesAndJournal()
    {
        using var inputs = new Inputs("tcp");
        using var cancellation = new CancellationTokenSource();
        var process = new FakeProcess(1)
        {
            OnUp = token => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Start(inputs, process, cancellation.Token));
        AssertClean(inputs, process);
        Assert.All(process.Calls.Where(c => c.IsCleanup), c => Assert.False(c.Cancelled));
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1:123\n127.0.0.1:456")]
    [InlineData("[::1]:9092")]
    public async Task AmbiguousDiscoveredEndpoint_CleansOwnedResourcesBeforeReturningAnyLease(string endpoint)
    {
        using var inputs = new Inputs("tcp");
        var process = new FakeProcess(1) { PublishedEndpoint = endpoint };
        await Assert.ThrowsAsync<OrchestrationException>(() => Start(inputs, process));
        AssertClean(inputs, process);
    }

    [Fact]
    public async Task AnyIpv4PublishedAddress_UsesHostLoopback()
    {
        using var inputs = new Inputs("tcp");
        await using var listener = new ReadyTcpServer();
        var process = new FakeProcess(listener.Port) { PublishedEndpoint = $"0.0.0.0:{listener.Port}", ServiceProperties = "\"scale\":1," };
        var lease = await Start(inputs, process);
        Assert.Equal($"127.0.0.1:{listener.Port}", lease.DiscoveredServices["broker"]);
        await lease.DisposeAsync();
        AssertClean(inputs, process);
    }

    private static Task<IKeptTopology> Start(Inputs inputs, FakeProcess process, CancellationToken ct = default)
        => ComposeLease.StartAsync(ComposeConfiguration.Load(inputs.Config), process, NullSecretAccessor.Instance, ct, inputs.Journals);

    private static void AssertClean(Inputs inputs, FakeProcess process)
    {
        Assert.False(process.HasOwnedResources);
        Assert.Empty(Directory.GetFiles(inputs.Journals));
    }

    private sealed class TestComposeDriver : IEnvironmentDriver
    {
        private readonly Inputs _inputs;
        private readonly FakeProcess _process;
        internal TestComposeDriver(Inputs inputs, FakeProcess process) { _inputs = inputs; _process = process; }
        public string Id => "compose";
        public EnvironmentPlan Bind(EnvironmentSourceSpec source, string suiteDirectory)
        {
            var configuration = ComposeConfiguration.Load(Path.GetFullPath(source.Config, suiteDirectory));
            return new EnvironmentPlan(configuration.Declarations(), "compose:" + configuration.Fingerprint,
                EnvironmentStatePolicy.FreshOwned,
                (context, ct) => ComposeLease.StartAsync(configuration, _process, context.Secrets, ct, _inputs.Journals),
                inputFiles: configuration.InputFiles);
        }
    }

    private sealed class Inputs : IDisposable
    {
        internal Inputs(string readiness, int startupSeconds = 30)
        {
            DirectoryPath = Directory.CreateTempSubdirectory("vouchfx-compose-tcp-ready-").FullName;
            Config = Path.Combine(DirectoryPath, "environment.compose.yaml");
            Journals = Path.Combine(DirectoryPath, "journals");
            File.WriteAllText(Path.Combine(DirectoryPath, "compose.yaml"), "services: { kafka: { image: broker } }");
            File.WriteAllText(Config, $$"""
                version: 1
                files: [./compose.yaml]
                startupTimeoutSeconds: {{startupSeconds}}
                cleanupTimeoutSeconds: 1
                services:
                  broker:
                    type: tcp
                    service: kafka
                    port: 9092
                    readiness: { type: {{readiness}} }
                """);
        }
        internal string DirectoryPath { get; }
        internal string Config { get; }
        internal string Journals { get; }
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    private sealed record Call(IReadOnlyList<string> Arguments, string? Input, bool Cancelled)
    {
        internal bool IsHealthInspection => Arguments[0] == "container" && Arguments[1] == "inspect" && Arguments[3].Contains(".State.Health.Status", StringComparison.Ordinal);
        internal bool IsCleanup => Arguments[0] is "container" or "network" or "volume" && !IsHealthInspection;
    }

    private sealed class FakeProcess : IComposeProcess
    {
        private const string ContainerId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private readonly int _port;
        private bool _container;
        private bool _network;
        private bool _volume;
        internal FakeProcess(int port) => _port = port;
        internal string Project { get; private set; } = "";
        internal string NativeModel { get; private set; } = "";
        internal List<Call> Calls { get; } = new();
        internal string? Healthcheck { get; init; } = "{\"test\":[\"CMD\",\"native-health-command-output-canary\"]}";
        internal string Ports { get; init; } = "[{\"target\":9092,\"protocol\":\"tcp\"}]";
        internal string ServiceProperties { get; init; } = "";
        internal string HealthInventory { get; init; } = ContainerId;
        internal string HealthStatus { get; init; } = "healthy";
        internal int StartingInspections { get; init; }
        internal string? InspectionShape { get; init; }
        internal string? PublishedEndpoint { get; init; }
        internal Action<CancellationToken>? OnUp { get; init; }
        internal Action<CancellationToken>? OnHealthInspection { get; init; }
        internal int HealthInspections { get; private set; }
        internal int RemovedContainers { get; private set; }
        internal bool HasOwnedResources => _container || _network || _volume;

        public Task<ComposeProcessResult> RunAsync(IReadOnlyList<string> arguments, string directory,
            IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken, string? standardInput = null)
        {
            var call = new Call(arguments, standardInput, cancellationToken.IsCancellationRequested);
            Calls.Add(call);
            cancellationToken.ThrowIfCancellationRequested();
            var output = "";
            if (arguments[0] == "context") output = "\"unix:///var/run/docker.sock\"";
            else if (arguments[0] == "compose")
            {
                if (arguments.Contains("config"))
                {
                    Project = arguments[arguments.ToList().IndexOf("--project-name") + 1];
                    NativeModel = "{\"services\":{\"kafka\":{" + ServiceProperties + "\"image\":\"broker\",\"ports\":" + Ports
                        + (Healthcheck is null ? "" : ",\"healthcheck\":" + Healthcheck) + "}},\"networks\":{\"default\":{\"name\":\"" + Project
                        + "_default\"}},\"volumes\":{\"data\":{\"name\":\"" + Project + "_data\"}}}";
                    output = NativeModel;
                }
                else if (arguments.Contains("up"))
                {
                    _container = _network = _volume = true;
                    OnUp?.Invoke(cancellationToken);
                }
                else if (arguments.Contains("port")) output = PublishedEndpoint ?? $"127.0.0.1:{_port}";
                else if (arguments.Contains("ps")) output = HealthInventory;
            }
            else if (call.IsHealthInspection)
            {
                HealthInspections++;
                OnHealthInspection?.Invoke(cancellationToken);
                output = HealthProjection();
            }
            else if (arguments[1] == "ls")
                output = arguments[0] switch
                {
                    "container" when _container => ContainerId,
                    "network" when _network => new string('b', 64),
                    "volume" when _volume => Project + "_data",
                    _ => "",
                };
            else if (arguments[1] == "inspect") output = Project;
            else if (arguments[1] == "rm")
            {
                if (arguments[0] == "container") { _container = false; RemovedContainers++; }
                if (arguments[0] == "network") _network = false;
                if (arguments[0] == "volume") _volume = false;
            }
            return Task.FromResult(new ComposeProcessResult(0, output));
        }

        private string HealthProjection()
        {
            if (InspectionShape == "malformed") return Canary;
            if (InspectionShape == "truncated") return JsonSerializer.Serialize(Project);
            var health = InspectionShape == "missing-health" ? null
                : InspectionShape == "invalid-health" ? Canary
                : HealthInspections <= StartingInspections ? "starting" : HealthStatus;
            return string.Join('\n', JsonSerializer.Serialize(InspectionShape == "wrong-project" ? Canary : Project),
                JsonSerializer.Serialize(InspectionShape == "wrong-service" ? Canary : "kafka"),
                JsonSerializer.Serialize(InspectionShape is "exited" or "paused" or "dead" ? InspectionShape : "running"), JsonSerializer.Serialize(health))
                + (InspectionShape == "trailing-newline" ? "\r\n" : "");
        }
    }

    private sealed class ReadyTcpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        internal ReadyTcpServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _worker = AcceptAsync();
        }
        internal int Port { get; }
        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                await _stop.CancelAsync();
                // Cancellation stops the pending accept. Join it before Stop disposes the socket;
                // otherwise Windows may surface ObjectDisposedException instead of cancellation.
                await _worker;
            }
            finally
            {
                _listener.Stop();
                _stop.Dispose();
            }
        }
    }
}
