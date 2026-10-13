using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Orchestration.Environments;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>Ownership, native snapshot and deadline regressions; no Docker executable is invoked.</summary>
public sealed class ComposeEnvironmentTests
{
    private const string Canary = "compose-canary-$literal-${not-interpolation}";

    [Fact]
    public async Task ChangedManifestAfterValidation_StartupUsesSnapshot_CleanupUsesOnlyVerifiedProjectIds()
    {
        var fixture = new Inputs();
        await using var server = new ReadyHttpServer();
        var process = new FakeProcess(server.Port)
        {
            OnUp = (_, _) =>
            {
                File.WriteAllText(fixture.Manifest, "volumes: { data: { name: unrelated-existing-volume } }");
                return Task.CompletedTask;
            },
        };
        var lease = await ComposeLease.StartAsync(fixture.Load(), process, NullSecretAccessor.Instance,
            CancellationToken.None, fixture.Journals);
        Assert.Equal($"http://127.0.0.1:{server.Port}", lease.DiscoveredServices["api"]);
        var journal = Assert.Single(Directory.GetFiles(fixture.Journals));
        Assert.DoesNotContain(Canary, File.ReadAllText(journal), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Manifest, File.ReadAllText(journal), StringComparison.Ordinal);
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.Empty(Directory.GetFiles(fixture.Journals));
        Assert.False(process.HasOwnedResources);
        Assert.Equal(1, process.RemovedContainers);
        Assert.Equal(1, process.RemovedNetworks);
        Assert.Equal(1, process.RemovedVolumes);
        Assert.DoesNotContain(process.Calls, c => c.Arguments.Contains("down"));
        Assert.DoesNotContain(process.Calls, c => c.Arguments.Contains("unrelated-existing-volume"));
        Assert.All(process.Calls.Where(c => c.Arguments.Contains("up") || c.Arguments.Contains("port")), c =>
        {
            Assert.Equal(process.NativeModel, c.Input);
            Assert.Contains("-", c.Arguments);
            Assert.DoesNotContain(fixture.Manifest, c.Arguments);
        });
    }

    [Fact]
    public async Task NonzeroStartupExit_PreservesProvisionDiagnostic_AfterOwnedCleanup()
    {
        var fixture = new Inputs();
        var process = new FakeProcess(1) { FailUp = true, UpExitCode = 37 };
        var error = await Assert.ThrowsAsync<OrchestrationException>(() => ComposeLease.StartAsync(fixture.Load(), process,
            NullSecretAccessor.Instance, CancellationToken.None, fixture.Journals));
        Assert.Equal(OrchestrationErrorKind.Provision, error.Info.Kind);
        Assert.Contains("operation failed (exit 37)", error.Info.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Manifest, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.False(process.HasOwnedResources);
        Assert.Empty(Directory.GetFiles(fixture.Journals));
        Assert.Equal(1, process.RemovedContainers);
        Assert.Equal(1, process.RemovedNetworks);
        Assert.Equal(1, process.RemovedVolumes);
        Assert.DoesNotContain(process.Calls, c => c.Arguments.Contains("port"));
    }

    [Fact]
    public async Task MalformedDiscoveredPort_PreservesDiscoveryDiagnostic_AfterOwnedCleanup()
    {
        var fixture = new Inputs();
        var process = new FakeProcess(1) { PortOutput = Canary };
        var error = await Assert.ThrowsAsync<OrchestrationException>(() => ComposeLease.StartAsync(fixture.Load(), process,
            NullSecretAccessor.Instance, CancellationToken.None, fixture.Journals));
        Assert.Equal(OrchestrationErrorKind.Discovery, error.Info.Kind);
        Assert.Contains("Selected port is absent", error.Info.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.False(process.HasOwnedResources);
        Assert.Empty(Directory.GetFiles(fixture.Journals));
        Assert.Equal(1, process.RemovedContainers);
        Assert.Equal(1, process.RemovedNetworks);
        Assert.Equal(1, process.RemovedVolumes);
        Assert.Contains(process.Calls, c => c.Arguments.Contains("port"));
    }

    [Fact]
    public async Task PartialStartupCancellation_CleanupHasIndependentTokenAndRemovesOwnedResources()
    {
        var fixture = new Inputs();
        using var cancellation = new CancellationTokenSource();
        var process = new FakeProcess(1)
        {
            OnUp = (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ComposeLease.StartAsync(fixture.Load(), process,
            NullSecretAccessor.Instance, cancellation.Token, fixture.Journals));
        Assert.False(process.HasOwnedResources);
        Assert.Empty(Directory.GetFiles(fixture.Journals));
        Assert.All(process.Calls.Where(c => c.Arguments[0] != "compose" && c.Arguments[0] != "context"), c => Assert.False(c.Cancelled));
    }

    [Fact]
    public async Task RunningButNotReady_StartupDeadlineCannotReturnLease_CleanupCompletes()
    {
        var fixture = new Inputs();
        await using var server = new ReadyHttpServer(status: 503);
        var process = new FakeProcess(server.Port);
        var error = await Assert.ThrowsAsync<OrchestrationException>(() => ComposeLease.StartAsync(fixture.Load(), process,
            NullSecretAccessor.Instance, CancellationToken.None, fixture.Journals));
        Assert.Contains("readiness failed", error.Message, StringComparison.Ordinal);
        Assert.False(process.HasOwnedResources);
        Assert.Empty(Directory.GetFiles(fixture.Journals));
    }

    [Fact]
    public async Task RecoveryUsesJournalOnly_OriginalInputsAndCredentialsAreNotNeeded()
    {
        var fixture = new Inputs();
        var process = new FakeProcess(1) { HasOwnedResources = true };
        var record = new ComposeOwnershipRecord(1, process.Project, "unix:///var/run/docker.sock", 2);
        var path = ComposeOwnershipJournal.Write(record, fixture.Journals);
        // The record contains no configuration path, environment or rendered model.
        var recordText = File.ReadAllText(path);
        Assert.DoesNotContain("ConfigPath", recordText, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, recordText, StringComparison.Ordinal);
        await ComposeLease.RecoverAsync(path, CancellationToken.None, process);
        Assert.False(process.HasOwnedResources);
        Assert.False(File.Exists(path));
        Assert.All(process.Calls, c => Assert.Null(c.Input));
    }

    [Fact]
    public async Task InventoryResourceWithWrongOwnershipLabel_IsNeverRemoved_JournalRemains()
    {
        var fixture = new Inputs();
        var process = new FakeProcess(1) { HasOwnedResources = true, WrongOwner = true };
        var path = ComposeOwnershipJournal.Write(new ComposeOwnershipRecord(1, process.Project, "unix:///var/run/docker.sock", 2), fixture.Journals);
        var error = await Assert.ThrowsAsync<OrchestrationException>(() => ComposeLease.RecoverAsync(path, CancellationToken.None, process));
        Assert.Contains("ownership label", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, process.RemovedContainers);
        Assert.Equal(0, process.RemovedNetworks);
        Assert.Equal(0, process.RemovedVolumes);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task CleanupDeadline_RetainsRecoverableJournal_AndDoesNotLeakNativeOutput()
    {
        var fixture = new Inputs();
        var process = new FakeProcess(1)
        {
            FailUp = true,
            StallCleanup = true,
        };
        var error = await Assert.ThrowsAsync<OrchestrationException>(() => ComposeLease.StartAsync(fixture.Load(), process,
            NullSecretAccessor.Instance, CancellationToken.None, fixture.Journals));
        Assert.Contains("environment cleanup --journal", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, error.Message, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(fixture.Journals));
    }

    [Theory]
    [InlineData("provider", "{\"type\":\"external-plugin\"}", "provider plugins")]
    [InlineData("network_mode", "\"host\"", "shared container/network")]
    [InlineData("privileged", "true", "host namespaces")]
    [InlineData("uts", "\"host\"", "host namespaces")]
    [InlineData("userns_mode", "\"host\"", "host namespaces")]
    [InlineData("cgroup", "\"host\"", "host namespaces")]
    [InlineData("container_name", "\"shared\"", "fixed container names")]
    public async Task UnsupportedNativeConfiguration_RefusesBeforeUpAndBeforeJournal(string property, string value, string diagnostic)
    {
        var fixture = new Inputs();
        var process = new FakeProcess(1) { ExtraServiceProperty = $"\"{property}\":{value}," };
        var error = await Assert.ThrowsAsync<TopologyAuthoringException>(() => ComposeLease.StartAsync(fixture.Load(), process,
            NullSecretAccessor.Instance, CancellationToken.None, fixture.Journals));
        Assert.Contains(diagnostic, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(process.Calls, c => c.Arguments.Contains("up"));
        Assert.False(Directory.Exists(fixture.Journals));
    }

    public static IEnumerable<object[]> InvalidDaemonHosts()
    {
        foreach (var host in new[]
        {
            "npipe:////remote-server/pipe/docker_engine", "npipe:////./pipe/", "npipe:////./pipe/docker_engine/extra",
            "npipe:////./pipe/docker_engine?query", "npipe:////./pipe/docker_engine\n", "unix:///", "unix:////server/docker.sock",
            "unix:///tmp/../docker.sock", "unix:///tmp/docker.sock?query", "unix:///tmp/docker.sock#fragment",
            "unix:///tmp/docker.sock%00", "unix:///tmp/docker.sock\n", "unix://remote-server/tmp/docker.sock",
        }) yield return new object[] { host };
    }

    [Theory]
    [MemberData(nameof(InvalidDaemonHosts))]
    public async Task InvalidDaemon_ExplicitHostOrContext_RefusesBeforeComposeAndJournal(string host)
    {
        var original = Environment.GetEnvironmentVariable("DOCKER_HOST");
        try
        {
            foreach (var fromContext in new[] { false, true })
            {
                Environment.SetEnvironmentVariable("DOCKER_HOST", fromContext ? null : host);
                var fixture = new Inputs();
                var process = new FakeProcess(1) { DockerHost = host };
                var error = await Assert.ThrowsAsync<TopologyAuthoringException>(() => ComposeLease.StartAsync(fixture.Load(), process,
                    NullSecretAccessor.Instance, CancellationToken.None, fixture.Journals));
                Assert.Contains("local", error.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(process.Calls, c => c.Arguments[0] == "compose");
                if (fromContext) Assert.Equal("context", Assert.Single(process.Calls).Arguments[0]);
                else Assert.Empty(process.Calls);
                Assert.False(Directory.Exists(fixture.Journals));
            }
        }
        finally { Environment.SetEnvironmentVariable("DOCKER_HOST", original); }
    }

    [Theory]
    [MemberData(nameof(InvalidDaemonHosts))]
    public async Task InvalidRecoveryDaemon_RefusesWithoutProcessInvocation(string host)
    {
        var fixture = new Inputs();
        var process = new FakeProcess(1) { HasOwnedResources = true };
        var journal = ComposeOwnershipJournal.Write(new ComposeOwnershipRecord(1, process.Project, host, 2), fixture.Journals);
        await Assert.ThrowsAsync<ArgumentException>(() => ComposeLease.RecoverAsync(journal, CancellationToken.None, process));
        Assert.Empty(process.Calls);
        Assert.True(process.HasOwnedResources);
        Assert.True(File.Exists(journal));
    }

    [Theory]
    [InlineData("npipe:////./pipe/docker_engine")]
    [InlineData("npipe:////./pipe/dockerDesktopLinuxEngine")]
    [InlineData("unix:///var/run/docker.sock")]
    [InlineData("unix:///run/user/1000/docker.sock")]
    public async Task LocalDaemonContext_UsesPinnedHostForStartupAndCleanup(string host)
    {
        var original = Environment.GetEnvironmentVariable("DOCKER_HOST");
        try
        {
            Environment.SetEnvironmentVariable("DOCKER_HOST", null);
            var fixture = new Inputs();
            await using var server = new ReadyHttpServer();
            var process = new FakeProcess(server.Port) { DockerHost = host };
            var lease = await ComposeLease.StartAsync(fixture.Load(), process, NullSecretAccessor.Instance,
                CancellationToken.None, fixture.Journals);
            await lease.DisposeAsync();
            Assert.False(process.HasOwnedResources);
            Assert.All(process.Calls.Where(c => c.Arguments[0] != "context"), c => Assert.Equal(host, c.DockerHost));
        }
        finally { Environment.SetEnvironmentVariable("DOCKER_HOST", original); }
    }

    [Theory]
    [InlineData("environment", "include")]
    [InlineData("environment", "extends")]
    [InlineData("environment", "env_file")]
    [InlineData("labels", "include")]
    [InlineData("labels", "extends")]
    [InlineData("labels", "env_file")]
    [InlineData("build.args", "include")]
    [InlineData("build.args", "extends")]
    [InlineData("build.args", "env_file")]
    public void DirectiveNamesInOpaqueServiceData_DoNotReadReferencedFiles(string context, string key)
    {
        var fixture = new Inputs();
        var value = key + ": ./deliberately-missing-not-an-input";
        var data = context == "build.args" ? "build: { context: ., args: { " + value + " } }" : context + ": { " + value + " }";
        File.WriteAllText(fixture.Manifest, "services: { api: { image: app, " + data + " } }");
        var configuration = fixture.Load();
        Assert.Equal(3, configuration.InputFiles.Count); // config, manifest and absent default .env
        Assert.DoesNotContain(configuration.InputFiles, p => p.EndsWith("deliberately-missing-not-an-input", StringComparison.Ordinal));
    }

    [Fact]
    public void DirectiveNamesAsServiceNamesAndUnusedExtensions_AreOpaque()
    {
        var fixture = new Inputs();
        File.WriteAllText(fixture.Manifest, """
            x-unused: { include: ./missing.yaml, extends: ./missing.yaml, env_file: ./missing.env }
            services:
              include: { image: app }
              extends: { image: app }
              env_file: { image: app }
              api: { image: app }
            """);
        Assert.Equal(3, fixture.Load().InputFiles.Count);
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("merge")]
    [InlineData("root-merge")]
    [InlineData("services-merge")]
    public void ServiceEnvFileThroughYamlAliasesAndMerges_IsTracked(string shape)
    {
        var fixture = new Inputs();
        var env = Path.Combine(fixture.Directory, "app.env");
        File.WriteAllText(env, "PASSWORD=initial");
        var manifest = shape switch
        {
            "alias" => "x-base: &base { image: app, env_file: ./app.env }\nservices: { api: *base }",
            "merge" => "x-base: &base { env_file: ./app.env }\nservices: { api: { <<: *base, image: app } }",
            "services-merge" => "x-base: &base { api: { image: app, env_file: ./app.env } }\nservices: { <<: *base }",
            _ => "x-base: &base { services: { api: { image: app, env_file: ./app.env } } }\n<<: *base",
        };
        File.WriteAllText(fixture.Manifest, manifest);
        var first = fixture.Load();
        Assert.Contains(env, first.InputFiles);
        File.WriteAllText(env, "PASSWORD=" + Canary);
        Assert.NotEqual(first.Fingerprint, fixture.Load().Fingerprint);
    }

    [Theory]
    [InlineData("x-base: &base { extends: { file: ./missing.yaml, service: api } }\nservices: { api: *base }")]
    [InlineData("x-base: &base { extends: { file: ./missing.yaml, service: api } }\nservices: { api: { <<: *base, image: app } }")]
    [InlineData("x-base: &base { include: ./missing.yaml }\n<<: *base\nservices: { api: { image: app } }")]
    public void ActiveIncludeOrServiceExtendsThroughYamlInheritance_RefuseDuringBinding(string manifest)
    {
        var fixture = new Inputs();
        File.WriteAllText(fixture.Manifest, manifest);
        Assert.Contains("include/extends", Assert.Throws<ArgumentException>(() => fixture.Load()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceYamlMergePrecedence_TracksOnlyTheEffectiveEnvFile()
    {
        var fixture = new Inputs();
        var actual = Path.Combine(fixture.Directory, "actual.env");
        File.WriteAllText(actual, "VALUE=tracked");
        File.WriteAllText(fixture.Manifest, """
            x-first: &first { image: app, env_file: ./actual.env }
            x-second: &second { env_file: ./missing-shadowed.env }
            services:
              api: { <<: [*first, *second] }
              other: { <<: *second, image: app, env_file: ./actual.env }
            """);
        var configuration = fixture.Load();
        Assert.Contains(actual, configuration.InputFiles);
        Assert.DoesNotContain(configuration.InputFiles, p => p.EndsWith("missing-shadowed.env", StringComparison.Ordinal));
    }

    [Fact]
    public void CyclicServiceYamlMerge_RefusesWithBoundedDiagnostic()
    {
        var fixture = new Inputs();
        File.WriteAllText(fixture.Manifest, "services: { api: &loop { <<: *loop, image: app } }");
        Assert.Contains("cyclic", Assert.Throws<ArgumentException>(() => fixture.Load()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepServiceYamlMerge_RefusesBeforeUnboundedAliasTraversal()
    {
        var fixture = new Inputs();
        var manifest = new StringBuilder("x-0: &node0 { image: app }\n");
        for (var index = 1; index <= 70; index++)
            manifest.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"x-{index}: &node{index} {{ <<: *node{index - 1} }}");
        manifest.AppendLine("services: { api: { <<: *node70 } }");
        File.WriteAllText(fixture.Manifest, manifest.ToString());
        var error = Assert.Throws<ArgumentException>(() => fixture.Load());
        Assert.Contains("bounded input-tracking limit", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("include: ./other.yaml")]
    [InlineData("services: { api: { extends: { file: ./other.yaml, service: api } } }")]
    public void ReferencedManifestFormatsWithoutInputTracking_RefuseDuringPureBinding(string manifest)
    {
        var fixture = new Inputs();
        File.WriteAllText(fixture.Manifest, manifest);
        var error = Assert.Throws<ArgumentException>(() => fixture.Load());
        Assert.Contains("include/extends", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeSnapshotReloadChangesConfiguration_RefusesBeforeMutation()
    {
        var fixture = new Inputs();
        var process = new FakeProcess(1) { ChangeSnapshotReload = true };
        var error = await Assert.ThrowsAsync<OrchestrationException>(() => ComposeLease.StartAsync(fixture.Load(), process,
            NullSecretAccessor.Instance, CancellationToken.None, fixture.Journals));
        Assert.Contains("reload", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(process.Calls, c => c.Arguments.Contains("up"));
        Assert.False(Directory.Exists(fixture.Journals));
    }

    [Theory]
    [InlineData("{\"services\":")]
    [InlineData("{\"services\":null}")]
    public async Task MalformedOrTruncatedNativeModel_RefusesWithoutStarting(string json)
    {
        var fixture = new Inputs();
        var process = new FakeProcess(1) { RawModel = json };
        await Assert.ThrowsAsync<OrchestrationException>(() => ComposeLease.StartAsync(fixture.Load(), process,
            NullSecretAccessor.Instance, CancellationToken.None, fixture.Journals));
        Assert.DoesNotContain(process.Calls, c => c.Arguments.Contains("up"));
        Assert.False(Directory.Exists(fixture.Journals));
    }

    [Fact]
    public void ServiceEnvFileEdit_ChangesPureSourceIdentity_WithoutResolvingCredentials()
    {
        var fixture = new Inputs();
        var env = Path.Combine(fixture.Directory, "app.env");
        File.WriteAllText(env, "PASSWORD=initial");
        File.WriteAllText(fixture.Manifest, "services: { api: { image: app, env_file: ./app.env } }");
        var first = fixture.Load().Fingerprint;
        File.WriteAllText(env, "PASSWORD=" + Canary);
        var second = fixture.Load().Fingerprint;
        Assert.NotEqual(first, second);
        Assert.DoesNotContain(Canary, second, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[::]:8080")]
    [InlineData("127.0.0.1:123\n127.0.0.1:456")]
    [InlineData("")]
    public void AmbiguousOrUnsupportedPort_DiscoveryRefuses(string output)
        => Assert.Throws<OrchestrationException>(() => ComposeLease.ParsePort(output));

    private sealed class Inputs
    {
        internal Inputs()
        {
            Directory = System.IO.Directory.CreateTempSubdirectory("vouchfx-compose-fake-").FullName;
            Manifest = Path.Combine(Directory, "compose.yaml");
            Config = Path.Combine(Directory, "environment.compose.yaml");
            Journals = Path.Combine(Directory, "journals");
            File.WriteAllText(Manifest, "services: { api: { image: app } }");
            File.WriteAllText(Config, """
                version: 1
                files: [./compose.yaml]
                startupTimeoutSeconds: 1
                cleanupTimeoutSeconds: 1
                services:
                  api:
                    service: api
                    port: 8080
                    readiness: { path: /health, status: 200 }
                """);
        }
        internal string Directory { get; }
        internal string Manifest { get; }
        internal string Config { get; }
        internal string Journals { get; }
        internal ComposeConfiguration Load() => ComposeConfiguration.Load(Config);
    }

    private sealed record Call(IReadOnlyList<string> Arguments, string? Input, bool Cancelled, string? DockerHost);
    private sealed class FakeProcess : IComposeProcess
    {
        private readonly int _port;
        private bool _container = true;
        private bool _network = true;
        private bool _volume = true;
        internal FakeProcess(int port) => _port = port;
        internal string Project { get; private set; } = "vouchfx-" + Guid.NewGuid().ToString("N");
        internal string? NativeModel { get; private set; }
        internal List<Call> Calls { get; } = new();
        internal bool HasOwnedResources { get; set; }
        internal bool WrongOwner { get; init; }
        internal bool FailUp { get; init; }
        internal int UpExitCode { get; init; } = 1;
        internal string? PortOutput { get; init; }
        internal bool StallCleanup { get; init; }
        internal string ExtraServiceProperty { get; init; } = "";
        internal string? RawModel { get; init; }
        internal bool ChangeSnapshotReload { get; init; }
        internal string DockerHost { get; init; } = "unix:///var/run/docker.sock";
        internal Func<string?, CancellationToken, Task>? OnUp { get; init; }
        internal int RemovedContainers { get; private set; }
        internal int RemovedNetworks { get; private set; }
        internal int RemovedVolumes { get; private set; }
        public async Task<ComposeProcessResult> RunAsync(IReadOnlyList<string> arguments, string directory,
            IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken, string? standardInput = null)
        {
            Calls.Add(new Call(arguments, standardInput, cancellationToken.IsCancellationRequested, environment.GetValueOrDefault("DOCKER_HOST")));
            cancellationToken.ThrowIfCancellationRequested();
            string output = "";
            if (arguments[0] == "context") output = JsonSerializer.Serialize(DockerHost);
            else if (arguments[0] == "compose")
            {
                if (arguments.Contains("config"))
                {
                    Project = arguments[arguments.ToList().IndexOf("--project-name") + 1];
                    NativeModel = "{\"services\":{\"api\":{" + ExtraServiceProperty + "\"image\":\"app\",\"environment\":{\"CANARY\":"
                        + JsonSerializer.Serialize(Canary) + "},\"ports\":[{\"target\":8080,\"protocol\":\"tcp\"}]}},\"networks\":{\"default\":{\"name\":\""
                        + Project + "_default\"}},\"volumes\":{\"data\":{\"name\":\"" + Project + "_data\"}}}";
                    output = RawModel ?? NativeModel;
                    if (standardInput is not null && ChangeSnapshotReload) output = "{}";
                }
                else if (arguments.Contains("up"))
                {
                    HasOwnedResources = true;
                    if (OnUp is not null) await OnUp(standardInput, cancellationToken);
                    if (FailUp) return new ComposeProcessResult(UpExitCode, Canary);
                }
                else if (arguments.Contains("port")) output = PortOutput ?? "127.0.0.1:" + _port;
            }
            else
            {
                if (StallCleanup) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                var kind = arguments[0];
                if (arguments[1] == "ls")
                    output = !HasOwnedResources ? "" : kind switch
                    {
                        "container" when _container => new string('a', 64),
                        "network" when _network => new string('b', 64),
                        "volume" when _volume => Project + "_data",
                        _ => "",
                    };
                else if (arguments[1] == "inspect") output = WrongOwner ? "unrelated-project" : Project;
                else if (arguments[1] == "rm")
                {
                    if (kind == "container") { _container = false; RemovedContainers++; }
                    if (kind == "network") { _network = false; RemovedNetworks++; }
                    if (kind == "volume") { _volume = false; RemovedVolumes++; }
                    HasOwnedResources = _container || _network || _volume;
                }
            }
            return new ComposeProcessResult(0, output);
        }
    }

    private sealed class ReadyHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        internal ReadyHttpServer(int status = 200)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _worker = ServeAsync(status);
        }
        internal int Port { get; }
        private async Task ServeAsync(int status)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    var buffer = new byte[4096];
                    _ = await stream.ReadAsync(buffer, _stop.Token);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        }
        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _worker;
            _stop.Dispose();
        }
    }
}
