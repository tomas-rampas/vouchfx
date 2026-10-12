using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Vouchfx.Cli.Selection;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Cli.Tests;

[Collection("managed-kafka-security")]
public sealed class ManagedKafkaSecurityDockerTests
{
    [Theory]
    [InlineData("group")]
    [InlineData("unread")]
    [InlineData("foreign-bootstrap")]
    public void BrokerStateObserver_RefusesUnexpectedGroupsUnreadLogsAndForeignBootstrap(string fault)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            bootstrap = fault == "foreign-bootstrap" ? "localhost:9999" : "localhost:9093",
            Host = "localhost",
            Port = 9093,
            topic = "owned-orders",
            groups = fault == "group" ? new[] { "vouchfx-expect-0123456789abcdef0123456789abcdef" } : Array.Empty<string>(),
            partitions = new[] { new { partition = 0, low = 0L, high = 1L, readOffset = fault == "unread" ? (long?)null : 0L } },
        }));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertManualConsumerState(document.RootElement, "localhost:9093"));
    }

    [Theory]
    [InlineData("/brokerfixture-abcdef12", true)]
    [InlineData("/brokerfixture-sr-abcdef12", false)]
    [InlineData("/unrelated-abcdef12", false)]
    public void BrokerOwnershipSelection_RequiresExactFixtureAndSingleDcpSuffix(string actualName, bool expected)
        => Assert.Equal(expected, OwnedBroker.HasExactFixtureName(actualName, "brokerfixture"));

    [Fact]
    public void BrokerOwnershipSelection_RefusesTwoMatchingResources()
        => Assert.Throws<Xunit.Sdk.SingleException>(() => OwnedBroker.SelectExactCandidate(
            new[] { "first-id brokerfixture-abcdef12", "second-id brokerfixture-1234abcd" }, "brokerfixture"));

    [Fact]
    public async Task NativeObservation_RefusesOverBudgetOutputWithoutEchoingItsContent()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 1024 * 1024) + "synthetic_material_canary_443");
        using var stream = new MemoryStream(bytes);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadBoundedUtf8Async(stream));
        Assert.DoesNotContain("synthetic_material_canary_443", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeProbe_BuildOnlyOutputIncludesExecutableRuntimeAndNativeAssets()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "KafkaSecurityProbe");
        foreach (var file in new[] { "Vouchfx.Kafka.Security.Probe.dll", "Vouchfx.Kafka.Security.Probe.deps.json", "Vouchfx.Kafka.Security.Probe.runtimeconfig.json" })
            Assert.True(File.Exists(Path.Combine(directory, file)), "The build-only native probe is missing a runtime asset.");
        Assert.True(Directory.Exists(Path.Combine(directory, "runtimes")), "The build-only native probe is missing its platform assets.");
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(directory, "runtimes"), "*librdkafka*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task NativeGroupState_UnavailableBrokerRefusesWithoutCrashingTheObserver()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka443-groups-refusal-").FullName;
        TestCertificateAuthority.WriteKafkaBrokerSuiteDirectory(directory);
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var report = Path.Combine(directory, "groups.json");
        var result = await CaptureNativeProcessAsync(ProbeStartInfo(new[]
        {
            Path.Combine(AppContext.BaseDirectory, "KafkaSecurityProbe", "Vouchfx.Kafka.Security.Probe.dll"),
            "groups", "127.0.0.1:" + port, directory, "tls", report,
        }), TimeSpan.FromSeconds(15), CancellationToken.None);
        File.WriteAllText(report + ".exit", result.Exit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.WriteAllText(report + ".stdout", result.Stdout);
        File.WriteAllText(report + ".stderr", result.Stderr);
        Assert.Equal(1, result.Exit);
        Assert.Empty(result.Stdout);
        Assert.Empty(result.Stderr);
        using var refusal = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        Assert.Equal("KafkaException", refusal.RootElement.GetProperty("errorType").GetString());
    }

    [Fact]
    public async Task NativeProcessTimeout_KillsAndReapsTheActualCapturedChild()
    {
        Process? observed = null;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CaptureNativeProcessAsync(
                ProbeStartInfo(new[] { Path.Combine(AppContext.BaseDirectory, "KafkaSecurityProbe", "Vouchfx.Kafka.Security.Probe.dll"), "wait-for-termination" }),
                TimeSpan.FromMilliseconds(100), CancellationToken.None, process =>
                {
                    observed = Process.GetProcessById(process.Id);
                    Assert.Equal(process.StartTime, observed.StartTime);
                }));
            Assert.NotNull(observed);
            Assert.True(observed.HasExited, "The exact captured child survived the bounded owner.");
        }
        finally { observed?.Dispose(); }
    }

    [Fact]
    public async Task NativeEventsFailedTransfer_KillsAndReapsBeforeReturningCancellation()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka443-transfer-").FullName;
        using var caller = new CancellationTokenSource();
        Process? observed = null;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeEvents.StartAsync(directory, caller.Token,
                ProbeStartInfo(new[] { Path.Combine(AppContext.BaseDirectory, "KafkaSecurityProbe", "Vouchfx.Kafka.Security.Probe.dll"), "wait-for-termination" }),
                process =>
                {
                    observed = Process.GetProcessById(process.Id);
                    Assert.Equal(process.StartTime, observed.StartTime);
                    caller.Cancel();
                }));
            Assert.NotNull(observed);
            Assert.True(observed.HasExited, "The exact collector survived its refused ownership transfer.");
            using var output = File.Open(Path.Combine(directory, "native-events.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var error = File.Open(Path.Combine(directory, "native-events.stderr"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { observed?.Dispose(); }
    }

    [Fact]
    public async Task NativeEventsStartFailure_ReleasesPreparedStreamsWithoutClaimingAChild()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka443-no-child-").FullName;
        var observed = false;
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => NativeEvents.StartAsync(directory,
            CancellationToken.None, new ProcessStartInfo(Path.Combine(directory, "missing-owned-executable")), _ => observed = true));
        Assert.False(observed);
        using var output = File.Open(Path.Combine(directory, "native-events.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var error = File.Open(Path.Combine(directory, "native-events.stderr"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task NativeProcessStartFailure_DoesNotClaimAnAcquiredChild()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka443-start-failure-").FullName;
        var observed = false;
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => CaptureNativeProcessAsync(
            new ProcessStartInfo(Path.Combine(directory, "missing-owned-executable")), TimeSpan.FromSeconds(1),
            CancellationToken.None, _ => observed = true));
        Assert.False(observed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingComposeKillGuard_StopsOnlyTheActualCapturedChild(bool production)
    {
        var helperType = production ? typeof(ComposeProcess) : typeof(ComposeEnvironmentDockerTests)
            .GetNestedType("LiveInputs", System.Reflection.BindingFlags.NonPublic)!;
        var helper = helperType.GetMethod("KillTreeQuietly", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var start = ProbeStartInfo(new[] { Path.Combine(AppContext.BaseDirectory, "KafkaSecurityProbe", "Vouchfx.Kafka.Security.Probe.dll"), "wait-for-termination" });
        using var owned = Process.Start(start)!;
        try
        {
            using var unrelated = Process.Start(start)!;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owned.WaitForExitAsync(timeout.Token));
                helper.Invoke(null, new object[] { owned });
                if (production)
                {
                    var reap = helperType.GetMethod("ReapAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
                    await (Task)reap.Invoke(null, new object[] { owned })!;
                }
                else await ReapAsync(owned);
                Assert.True(owned.HasExited);
                Assert.False(unrelated.HasExited, "The kill guard affected a child it did not own.");
            }
            finally { ChildProcess.KillTreeQuietly(unrelated); await ReapAsync(unrelated); }
        }
        finally { ChildProcess.KillTreeQuietly(owned); await ReapAsync(owned); }
    }

    [Fact]
    public async Task ComposeProcess_PreCancelledInvocationDoesNotLaunchOrReplaceCancellation()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ComposeProcess().RunAsync(
            new[] { "compose", "version" }, Path.GetTempPath(), new Dictionary<string, string>(), caller.Token));
    }

    [Fact]
    public async Task ComposeProcess_ReapDeadlineReportsNonterminalInfrastructureWithoutCallerCancellation()
    {
        var reap = typeof(ComposeProcess).GetMethod("ReapAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        using var owned = Process.Start(ProbeStartInfo(new[]
            { Path.Combine(AppContext.BaseDirectory, "KafkaSecurityProbe", "Vouchfx.Kafka.Security.Probe.dll"), "wait-for-termination" }))!;
        try
        {
            var failure = await Assert.ThrowsAsync<OrchestrationException>(() => (Task)reap.Invoke(null, new object[] { owned })!);
            Assert.Equal(OrchestrationErrorKind.Provision, failure.Info.Kind);
            Assert.Equal("Docker CLI child did not terminate within its independent 10-second reap budget.", failure.Info.Detail);
            Assert.False(owned.HasExited, "This deadline control unexpectedly reached a terminal process state.");
        }
        finally { ChildProcess.KillTreeQuietly(owned); await ReapAsync(owned); }
    }

    [Fact]
    public void GeneratedAvroSutFixture_ValidatesBeforeAnyDockerMutation()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka443-avro-authoring-").FullName;
        TestCertificateAuthority.WriteKafkaBrokerSuiteDirectory(directory);
        var path = Path.Combine(directory, "fixture.e2e.yaml");
        File.WriteAllText(path, AvroSutScenario("broker", "mtls", "9093", "owned-sut:fixture"));
        var output = new StringWriter();
        Assert.True(ValidateCommand.Execute(path, json: true, output, new StringWriter()) == 0, output.ToString());
        using var report = JsonDocument.Parse(output.ToString());
        Assert.True(report.RootElement.GetProperty("valid").GetBoolean());
        var environment = YamlDocumentParser.Parse(File.ReadAllText(path)).Environment;
        var mapped = EnvironmentMapper.Map(environment, directory);
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        { DisableDashboard = true, Args = Array.Empty<string>(), AssemblyName = "Vouchfx.Cli.Tests" });
        mapped.Configure(builder);
        var sut = Assert.Single(builder.Resources.OfType<ContainerResource>(), resource => resource.Name == "sut");
        var endpoint = Assert.Single(sut.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("http", endpoint.Name);
        Assert.Equal("http", endpoint.UriScheme);
        Assert.Equal(8080, endpoint.TargetPort);
    }

    [Theory]
    [Trait("requires", "docker")]
    [InlineData("tls", "tcp")]
    [InlineData("mtls", "internal")]
    [InlineData("tls", "9092")]
    [InlineData("tls", "9093")]
    [InlineData("tls", "29092")]
    [InlineData("tls", "29093")]
    [InlineData("tls", "8082")]
    public Task DefaultPinnedBroker_NormalCliPublishesAndConsumesThroughAllocatedSecureEndpoint(string profile, string selector)
        => RunBrokerAsync(profile, selector);

    [Fact]
    [Trait("requires", "docker")]
    public async Task DefaultPinnedBroker_MutualTlsAvroAndContainerSutUseRelocatedPlaintextListener()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka443-sut-").FullName;
        var fixture = Guid.NewGuid().ToString("N");
        var image = "vouchfx-kafka443-sut:" + fixture;
        File.WriteAllText(Path.Combine(directory, "Dockerfile"), "FROM confluentinc/confluent-local@sha256:11b351dc22765780047a07b02d1f19a820bbdd3fae2cfe7ba4ef0d0a26b52248\nCOPY server.py /owned-sut/server.py\nRUN python3 --version\nENTRYPOINT [\"python3\",\"/owned-sut/server.py\"]\n");
        File.WriteAllText(Path.Combine(directory, "server.py"), PlaintextSutSource);
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        string? imageId = null;
        try
        {
            var build = await DockerCaptureAsync(new[] { "build", "--label", "vouchfx.security.fixture=" + fixture, "-t", image, directory }, budget.Token, timeoutSeconds: 60);
            File.WriteAllText(Path.Combine(directory, "image-build.exit"), build.Exit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(0, build.Exit);
            imageId = Assert.Single(await DockerAsync(new[] { "image", "inspect", image, "--format", "{{.Id}}" }, budget.Token));
            File.WriteAllText(Path.Combine(directory, "owned-image.id"), imageId);
            await RunBrokerAsync("mtls", "9093", image);
        }
        finally
        {
            if (imageId is not null)
            {
                using var cleanupBudget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                Assert.Equal(imageId, Assert.Single(await DockerAsync(new[] { "image", "inspect", image, "--format", "{{.Id}}" }, cleanupBudget.Token)));
                Assert.Equal(fixture, Assert.Single(await DockerAsync(new[] { "image", "inspect", image, "--format", "{{index .Config.Labels \"vouchfx.security.fixture\"}}" }, cleanupBudget.Token)));
                var cleanup = await DockerCaptureAsync(new[] { "image", "rm", image }, cleanupBudget.Token);
                File.WriteAllText(Path.Combine(directory, "owned-image-cleanup.exit"), cleanup.Exit.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(0, cleanup.Exit);
                Assert.Empty(await DockerAsync(new[] { "image", "ls", "-q", image }, cleanupBudget.Token));
            }
        }
    }

    [Fact]
    [Trait("requires", "docker")]
    public Task DefaultPinnedBrokers_ParallelTwoKeepConfirmationBootstrapAndOwnershipIndependent()
        => RunBrokerAsync("mtls", "internal", parallelBrokers: true);

    [Fact]
    public void GeneratedStartupFailureAndCancellationFixtures_ValidateWithoutReadingServerMaterial()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka443-startup-authoring-").FullName;
        TestCertificateAuthority.WriteKafkaBrokerSuiteDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "kafka.keystore.pem"), "synthetic_invalid_material_canary_443");
        foreach (var cancel in new[] { false, true })
        {
            var path = Path.Combine(directory, "fixture.e2e.yaml");
            var scenario = StartupScenario("broker", cancel);
            Assert.Equal(cancel, scenario.Contains("suspend=y", StringComparison.Ordinal));
            File.WriteAllText(path, scenario);
            var output = new StringWriter();
            Assert.True(ValidateCommand.Execute(path, json: true, output, new StringWriter()) == 0, output.ToString());
            Assert.DoesNotContain("synthetic_invalid_material_canary_443", output.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("requires", "docker")]
    public Task DefaultPinnedBroker_InvalidPemReadinessRefusesAndCleansOwnedStartup()
        => RunStartupAsync(cancel: false);

    [Fact]
    [Trait("requires", "docker")]
    public Task DefaultPinnedBroker_CallerCancellationCleansOwnedStartupWithoutFalseConfirmation()
        => RunStartupAsync(cancel: true);

    [Fact]
    [Trait("requires", "docker")]
    public Task DefaultPinnedBroker_CallerCancellationAfterRunningCleansOwnedStartupWithoutFalseConfirmation()
        => RunStartupAsync(cancel: true, requireRunning: true, parallel: 1);

    private static async Task RunStartupAsync(bool cancel, bool requireRunning = false, int? parallel = null)
    {
        const string canary = "synthetic_invalid_material_canary_443";
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka443-startup-").FullName;
        CaptureArtifactIdentity(directory);
        TestCertificateAuthority.WriteKafkaBrokerSuiteDirectory(directory);
        if (!cancel) File.WriteAllText(Path.Combine(directory, "kafka.keystore.pem"), canary);
        var broker = "broker" + Guid.NewGuid().ToString("N")[..10];
        var path = Path.Combine(directory, "broker.e2e.yaml");
        File.WriteAllText(path, StartupScenario(broker, cancel));
        var events = Path.Combine(directory, "events.jsonl");
        var output = new StringWriter();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        await using var nativeEvents = await NativeEvents.StartAsync(directory, budget.Token);
        var baseline = await PrepareBaselineAsync(directory, budget.Token);
        OwnedBroker? owned = null;
        var starts = 0;
        var acquired = 0;
        int? exit = null;
        var cancelled = false;
        var registry = new EnvironmentDriverRegistry(Array.Empty<IEnvironmentDriver>(), async (context, ct) =>
        {
            Interlocked.Increment(ref starts);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var starting = (context.Request with { StartupTimeout = TimeSpan.FromSeconds(25) })
                .StartAsync(context.Security, context.Paths, startup.Token);
            try
            {
                owned = await CaptureStartingBrokerAsync(broker, starting, startup.Token);
                if (owned is not null)
                    File.WriteAllText(Path.Combine(directory, "owned-starting-broker.json"), JsonSerializer.Serialize(owned));
                if (cancel && owned is not null)
                {
                    if (requireRunning)
                    {
                        using var runningDeadline = CancellationTokenSource.CreateLinkedTokenSource(startup.Token);
                        runningDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                        while (true)
                        {
                            var state = await DockerAsync(new[] { "inspect", owned.Id, "--format", "{{.State.Status}}" }, runningDeadline.Token);
                            if (Assert.Single(state) == "running") break;
                            await Task.Delay(100, runningDeadline.Token);
                        }
                        Assert.Equal(owned, await OwnedBroker.InspectAsync(owned.Id, broker, runningDeadline.Token));
                        File.WriteAllText(Path.Combine(directory, "observed-running.utc"), DateTimeOffset.UtcNow.ToString("O"));
                    }
                    Assert.False(starting.IsCompleted, "Cancellation fixture finished acquisition before the owned-container observation.");
                    File.WriteAllText(Path.Combine(directory, "caller-cancel.utc"), DateTimeOffset.UtcNow.ToString("O"));
                    caller.Cancel();
                }
                var topology = await starting;
                Interlocked.Increment(ref acquired);
                return topology;
            }
            finally
            {
                if (!starting.IsCompleted)
                {
                    startup.Cancel();
                    try { await (await starting).DisposeAsync(); }
                    catch (Exception) when (startup.IsCancellationRequested) { }
                }
            }
        });
        try
        {
            File.WriteAllText(Path.Combine(directory, "product-start.utc"), DateTimeOffset.UtcNow.ToString("O"));
            try
            {
                exit = await RunCommand.ExecuteAsync(path, SelectionCriteria.None, parallel, watch: false,
                    failOnEnvironmentError: false, failOnInconclusive: false, htmlReportPath: null, junitReportPath: null,
                    eventsReportPath: events, eventsStreamPath: null, decorate: false, output, telemetryHook: null,
                    caller.Token, environmentDrivers: registry);
            }
            catch (OperationCanceledException) when (cancel && caller.IsCancellationRequested) { cancelled = true; }
            Assert.Equal(1, starts);
            Assert.Equal(0, acquired);
            Assert.NotNull(owned);
            Assert.Equal(cancel, cancelled);
            if (cancel)
                Assert.DoesNotContain(RunCommand.SecurityUnconfirmableNotice, output.ToString(), StringComparison.Ordinal);
            else
            {
                Assert.Equal(3, exit);
                Assert.Contains(RunCommand.SecurityUnconfirmableNotice, output.ToString(), StringComparison.Ordinal);
            }
            var lines = File.Exists(events) ? await File.ReadAllTextAsync(events) : string.Empty;
            Assert.DoesNotContain("\"type\":\"step-completed\"", lines, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, lines + output, StringComparison.Ordinal);
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "product.log"), output.ToString());
            File.WriteAllText(Path.Combine(directory, "cli.exit"), exit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not-completed");
            File.WriteAllText(Path.Combine(directory, "startup-result.json"), JsonSerializer.Serialize(new { cancel, requireRunning, parallel, cancelled, starts, acquired, exit }));
            await VerifyAfterAsync(directory, baseline);
            if (owned is not null)
            {
                var immediate = CreatorCompletion.Capture(owned);
                File.WriteAllText(Path.Combine(directory, "creator-completion-immediate.json"), JsonSerializer.Serialize(immediate));
                var observation = await CreatorCompletion.ObserveTerminalAsync(owned, immediate);
                File.WriteAllText(Path.Combine(directory, "creator-completion.json"), JsonSerializer.Serialize(observation.Final));
                File.WriteAllText(Path.Combine(directory, "creator-observation.json"), JsonSerializer.Serialize(observation));
                Assert.False(observation.Final.OriginalProcessStillRunning, "The exact owned DCP creator survived its independent 10-second completion budget.");
            }
        }
    }

    private sealed record CreatorCompletion(DateTimeOffset CapturedAt, string Pid, string ExpectedStart,
        bool PidPresent, bool OriginalProcessStillRunning, string? LinuxProcessStat)
    {
        internal static async Task<CreatorObservation> ObserveTerminalAsync(OwnedBroker owned, CreatorCompletion immediate)
        {
            // Aspire waits for resource deletion; its detached daemon can finish stopping later.
            // Observe only this PID/name/birth, with the native-child reap budget, never kill by PID.
            var clock = Stopwatch.StartNew();
            var current = immediate;
            var samples = 1;
            var budget = TimeSpan.FromSeconds(10);
            while (current.OriginalProcessStillRunning)
            {
                var remaining = budget - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100));
                current = Capture(owned);
                samples++;
            }
            return new CreatorObservation(immediate, current, samples, clock.Elapsed.TotalMilliseconds);
        }

        internal static CreatorCompletion Capture(OwnedBroker owned)
        {
            try
            {
                using var process = Process.GetProcessById(int.Parse(owned.CreatorPid, System.Globalization.CultureInfo.InvariantCulture));
                var expected = DateTimeOffset.Parse(owned.CreatorStart, System.Globalization.CultureInfo.InvariantCulture);
                var nativeStat = OperatingSystem.IsLinux() ? File.ReadAllText($"/proc/{process.Id}/stat") : null;
                DateTimeOffset identity;
                if (nativeStat is null) identity = CreatorIdentityTime(process);
                else if (!TryParseLinuxCreatorIdentity(nativeStat, process.Id, LinuxClockTicks(), out identity))
                    throw new InvalidOperationException("The owned DCP process identity was malformed.");
                var same = !process.HasExited && process.ProcessName.StartsWith("dcp", StringComparison.OrdinalIgnoreCase)
                    && Math.Abs((expected - identity).TotalSeconds) <= 1;
                return new CreatorCompletion(DateTimeOffset.UtcNow, owned.CreatorPid, owned.CreatorStart, true, same, nativeStat);
            }
            catch (Exception failure) when (failure is ArgumentException or FileNotFoundException or DirectoryNotFoundException)
            {
                return new CreatorCompletion(DateTimeOffset.UtcNow, owned.CreatorPid, owned.CreatorStart, false, false, null);
            }
        }
    }

    [Fact]
    public async Task CreatorCompletion_MatchingLiveCreatorStillFailsAfterIndependentDeadline()
    {
        if (!OperatingSystem.IsLinux()) return;
        await WithCreatorControlAsync(async process =>
        {
            var owned = CreatorControlOwnership(process, CreatorIdentityTime(process));
            var immediate = CreatorCompletion.Capture(owned);
            Assert.True(immediate.OriginalProcessStillRunning);
            var observation = await CreatorCompletion.ObserveTerminalAsync(owned, immediate);
            Assert.True(observation.Final.PidPresent);
            Assert.True(observation.Final.OriginalProcessStillRunning);
            Assert.InRange(observation.ElapsedMilliseconds, 9900, 13000);
            Assert.True(observation.Samples > 1);
            Assert.Throws<Xunit.Sdk.FalseException>(() => Assert.False(observation.Final.OriginalProcessStillRunning));
            Assert.False(process.HasExited);
        });
    }

    [Fact]
    public async Task CreatorCompletion_OtherBirthDoesNotWaitForOrKillEitherLiveCreator()
    {
        if (!OperatingSystem.IsLinux()) return;
        await WithCreatorControlAsync(async first =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            await WithCreatorControlAsync(async second =>
            {
                var firstBirth = CreatorIdentityTime(first);
                var secondBirth = CreatorIdentityTime(second);
                Assert.NotInRange(Math.Abs((firstBirth - secondBirth).TotalSeconds), 0, 1);
                var mismatched = CreatorControlOwnership(first, secondBirth);
                var immediate = CreatorCompletion.Capture(mismatched);
                Assert.True(immediate.PidPresent);
                Assert.False(immediate.OriginalProcessStillRunning);
                var observation = await CreatorCompletion.ObserveTerminalAsync(mismatched, immediate);
                Assert.Equal(1, observation.Samples);
                Assert.InRange(observation.ElapsedMilliseconds, 0, 1000);
                Assert.False(first.HasExited);
                Assert.False(second.HasExited);
            });
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatorControl_CallbackFailureOrCancellation_ReapsOwnedProcess(bool cancel)
    {
        if (!OperatingSystem.IsLinux()) return;
        OwnedBroker? owned = null;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var control = WithCreatorControlAsync(process =>
        {
            owned = CreatorControlOwnership(process, CreatorIdentityTime(process));
            return cancel ? Task.FromCanceled(cancellation.Token) : Task.FromException(new InvalidOperationException("creator-control-failure"));
        });
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => control);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => control);
        Assert.NotNull(owned);
        Assert.False(CreatorCompletion.Capture(owned).OriginalProcessStillRunning);
    }

    private static OwnedBroker CreatorControlOwnership(Process process, DateTimeOffset birth)
        => new("native-control", "/native-control", "native-control", "native-control",
            process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), birth.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            "native-control", "native-control");

    private static async Task WithCreatorControlAsync(Func<Process, Task> observe)
    {
        using var process = Process.Start(ProbeStartInfo(new[]
        {
            Path.Combine(AppContext.BaseDirectory, "KafkaSecurityProbe", "Vouchfx.Kafka.Security.Probe.dll"), "wait-for-creator-control",
        })) ?? throw new InvalidOperationException("The native creator control could not be started.");
        try
        {
            Assert.Equal("ready", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.StartsWith("dcp", process.ProcessName, StringComparison.OrdinalIgnoreCase);
            await observe(process);
        }
        finally
        {
            ChildProcess.KillTreeQuietly(process);
            await ReapAsync(process);
        }
    }

    private sealed record CreatorObservation(CreatorCompletion Immediate, CreatorCompletion Final, int Samples, double ElapsedMilliseconds);

    private static string StartupScenario(string broker, bool cancel)
    {
        var scenario = Scenario(broker, "mtls", "internal");
        if (cancel)
            scenario = scenario.Replace("        KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS: \"0\"", "        KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS: \"0\"\n        KAFKA_OPTS: '-agentlib:jdwp=transport=dt_socket,server=y,suspend=y,address=localhost:5005'", StringComparison.Ordinal);
        return scenario[..scenario.IndexOf("steps:\n", StringComparison.Ordinal)] + "steps:\n  - { id: must-not-execute, type: script.csharp, code: 'throw new InvalidOperationException(\"Startup fixture executed a step.\");' }\n";
    }

    private static async Task<OwnedBroker?> CaptureStartingBrokerAsync(string broker, Task<SuiteTopology> starting, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!starting.IsCompleted)
        {
            var candidates = await DockerAsync(new[] { "ps", "-a", "--no-trunc", "--format", "{{.ID}} {{.Names}}",
                "--filter", "label=com.microsoft.developer.usvc-dev.name", "--filter", "name=^/" + broker + "-" }, deadline.Token);
            if (candidates.Any(candidate => OwnedBroker.HasExactFixtureName("/" + candidate.Split(' ')[1], broker)))
            {
                var selected = OwnedBroker.SelectExactCandidate(candidates, broker);
                return await OwnedBroker.InspectAsync(selected[0], broker, deadline.Token);
            }
            await Task.Delay(100, deadline.Token);
        }
        return null;
    }

    private static async Task RunBrokerAsync(string profile, string selector, string? sutImage = null, bool parallelBrokers = false)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-kafka443-live-").FullName;
        CaptureArtifactIdentity(directory);
        TestCertificateAuthority.WriteKafkaBrokerSuiteDirectory(directory);
        var firstBroker = "broker" + Guid.NewGuid().ToString("N")[..10];
        var brokers = parallelBrokers ? new[] { firstBroker, "broker" + Guid.NewGuid().ToString("N")[..10] } : new[] { firstBroker };
        var path = Path.Combine(directory, "broker.e2e.yaml");
        File.WriteAllText(path, sutImage is null ? Scenario(firstBroker, profile, selector) : AvroSutScenario(firstBroker, profile, selector, sutImage));
        if (parallelBrokers)
            File.WriteAllText(Path.Combine(directory, "other.e2e.yaml"), Scenario(brokers[1], profile, selector)
                .Replace("      type: kafka\n", "      type: kafka\n      image: confluentinc/confluent-local@sha256:11b351dc22765780047a07b02d1f19a820bbdd3fae2cfe7ba4ef0d0a26b52248\n", StringComparison.Ordinal));
        var events = Path.Combine(directory, "events.jsonl");
        var output = new StringWriter();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var nativeEvents = await NativeEvents.StartAsync(directory, budget.Token);
        var beforePreparation = await InventoryAsync(budget.Token);
        File.WriteAllText(Path.Combine(directory, "before-preparation.json"), JsonSerializer.Serialize(beforePreparation));
        File.WriteAllText(Path.Combine(directory, "preparation-start.utc"), DateTimeOffset.UtcNow.ToString("O"));
        var unmatched = await DockerAsync(new[] { "ps", "--no-trunc", "--format", "{{.ID}}",
            "--filter", "label=vouchfx.security.fixture.unmatched=" + Guid.NewGuid().ToString("N") }, budget.Token);
        Assert.Empty(unmatched);
        File.WriteAllText(Path.Combine(directory, "preparation-query.exit"), "0");
        var prepared = await InventoryAsync(budget.Token);
        File.WriteAllText(Path.Combine(directory, "prepared.json"), JsonSerializer.Serialize(prepared));
        var baseline = await InventoryAsync(budget.Token);
        File.WriteAllText(Path.Combine(directory, "baseline.json"), JsonSerializer.Serialize(baseline));
        Assert.Equal(prepared.Containers, baseline.Containers);
        Assert.Equal(prepared.Networks, baseline.Networks);
        Assert.Equal(prepared.Volumes, baseline.Volumes);
        Assert.Equal(prepared.EngineId, baseline.EngineId);
        int? exit = null;
        var registry = new EnvironmentDriverRegistry(Array.Empty<IEnvironmentDriver>(), async (context, ct) =>
        {
            var broker = Assert.Single(context.Request.Environment!.Dependencies!.Keys);
            var prefix = parallelBrokers ? broker + "-" : string.Empty;
            var topology = await context.Request.StartAsync(context.Security, context.Paths, ct);
            try
            {
                var bootstrap = Assert.IsType<string>(topology.DiscoveredServices[broker]);
                var confirmation = Assert.Single(topology.SecurityConfirmations);
                Assert.Equal(bootstrap, confirmation.ObservedAddress);
                Assert.Equal(SecurityConfirmationLevel.AuthenticatedRoundTrip, confirmation.Level);
                var probe = Path.Combine(AppContext.BaseDirectory, "KafkaSecurityProbe", "Vouchfx.Kafka.Security.Probe.dll");
                Assert.True(File.Exists(probe), "Build-only Kafka probe output was not copied into the test output.");
                await ProbeAsync(probe, "metadata", bootstrap, directory, profile, prefix + "metadata.json", ct);
                using (var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, prefix + "metadata.json"), ct)))
                {
                    var authority = new Uri("tcp://" + bootstrap);
                    Assert.Equal(bootstrap, metadata.RootElement.GetProperty("bootstrap").GetString());
                    Assert.Equal(authority.Host, metadata.RootElement.GetProperty("Host").GetString());
                    Assert.Equal(authority.Port, metadata.RootElement.GetProperty("Port").GetInt32());
                }
                if (!parallelBrokers && (selector is "tcp" or "internal"))
                {
                    var foreign = Directory.CreateDirectory(Path.Combine(directory, "foreign")).FullName;
                    TestCertificateAuthority.WriteKafkaBrokerSuiteDirectory(foreign);
                    await ProbeAsync(probe, "negative", bootstrap, directory, profile, "negative-clients.json", ct, foreign);
                    using var negatives = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "negative-clients.json"), ct));
                    foreach (var check in new[] { "wrongCa", "plaintext", "wrongHostname" })
                        Assert.True(negatives.RootElement.GetProperty(check).GetBoolean());
                    if (profile == "mtls")
                    {
                        Assert.True(negatives.RootElement.GetProperty("missingClient").GetBoolean());
                        Assert.True(negatives.RootElement.GetProperty("foreignClient").GetBoolean());
                    }
                    else
                    {
                        Assert.Equal(JsonValueKind.Null, negatives.RootElement.GetProperty("missingClient").ValueKind);
                        Assert.Equal(JsonValueKind.Null, negatives.RootElement.GetProperty("foreignClient").ValueKind);
                    }
                }
                var owned = await OwnedBroker.CaptureAsync(broker, ct);
                File.WriteAllText(Path.Combine(directory, prefix + "owned-broker.json"), JsonSerializer.Serialize(owned));
                if (sutImage is not null)
                {
                    var registryResource = await OwnedBroker.CaptureAsync(broker + "-sr", ct);
                    File.WriteAllText(Path.Combine(directory, "owned-registry.json"), JsonSerializer.Serialize(registryResource));
                    var registryEnvironment = await DockerCaptureAsync(new[] { "inspect", registryResource.Id, "--format", "{{json .Config.Env}}" }, ct);
                    Assert.Equal(0, registryEnvironment.Exit);
                    var values = JsonSerializer.Deserialize<string[]>(registryEnvironment.Stdout)!;
                    var routing = Assert.Single(values, value => value.StartsWith("SCHEMA_REGISTRY_KAFKASTORE_BOOTSTRAP_SERVERS=", StringComparison.Ordinal));
                    var model = topology.Application.Services.GetRequiredService<DistributedApplicationModel>();
                    var brokerResource = Assert.Single(model.Resources.OfType<KafkaServerResource>(), resource => resource.Name == broker);
                    var plaintext = new EndpointReference(brokerResource, "internal", KnownNetworkIdentifiers.DefaultAspireContainerNetwork);
                    var authority = await ReferenceExpression.Create($"{plaintext.Property(EndpointProperty.HostAndPort)}").GetValueAsync(ct);
                    Assert.Equal("SCHEMA_REGISTRY_KAFKASTORE_BOOTSTRAP_SERVERS=PLAINTEXT://" + authority, routing);
                    File.WriteAllText(Path.Combine(directory, "registry-routing.json"), JsonSerializer.Serialize(new { routing }));
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 65536 };
                    using var sut = JsonDocument.Parse(await client.GetStringAsync(Assert.IsType<string>(topology.DiscoveredServices["sut"]), ct));
                    Assert.Equal(authority, sut.RootElement.GetProperty("bootstrap").GetString());
                    Assert.Equal(authority, sut.RootElement.GetProperty("advertisedBootstrap").GetString());
                    Assert.Equal(0, sut.RootElement.GetProperty("apiVersionsError").GetInt32());
                    Assert.Equal(0, sut.RootElement.GetProperty("advertisedError").GetInt32());
                    Assert.Equal(443, sut.RootElement.GetProperty("correlation").GetInt32());
                    File.WriteAllText(Path.Combine(directory, "container-routing.json"), sut.RootElement.GetRawText());
                }
                return new ObservedLease(topology, owned, bootstrap, directory, prefix, profile,
                    !parallelBrokers && profile == "tls" && selector == "tcp");
            }
            catch
            {
                await topology.DisposeAsync();
                throw;
            }
        });
        try
        {
            File.WriteAllText(Path.Combine(directory, "product-start.utc"), DateTimeOffset.UtcNow.ToString("O"));
            exit = await RunCommand.ExecuteAsync(parallelBrokers ? directory : path, SelectionCriteria.None, parallel: parallelBrokers ? 2 : null, watch: false,
                failOnEnvironmentError: true, failOnInconclusive: true, htmlReportPath: null, junitReportPath: null,
                eventsReportPath: events, eventsStreamPath: null, decorate: false, output, telemetryHook: null, budget.Token,
                environmentDrivers: registry);
            Assert.True(exit == 0, $"Actual CLI exit {exit}; retained evidence: {directory}\n{output}");
            var documents = (await File.ReadAllLinesAsync(events)).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var completed = documents.Where(document => document.RootElement.GetProperty("type").GetString() == "step-completed").ToArray();
                Assert.Equal(parallelBrokers ? 4 : sutImage is null ? 2 : 3, completed.Length);
                Assert.All(completed, document => Assert.Equal("PASS", document.RootElement.GetProperty("verdict").GetString()));
                foreach (var consume in completed.Where(document => document.RootElement.GetProperty("stepId").GetString() == "consume"))
                {
                    var observation = consume.RootElement.GetProperty("observation");
                    Assert.True(observation.GetProperty("matched").GetBoolean());
                    Assert.True(observation.GetProperty("scanned").GetInt32() > 0);
                }
                var actualBootstraps = new HashSet<string>(StringComparer.Ordinal);
                foreach (var broker in brokers)
                {
                    var prefix = parallelBrokers ? broker + "-" : string.Empty;
                    using var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, prefix + "consumer-state.json")));
                    var bootstrap = Assert.IsType<string>(state.RootElement.GetProperty("bootstrap").GetString());
                    AssertManualConsumerState(state.RootElement, bootstrap);
                    actualBootstraps.Add(bootstrap);
                    Assert.Equal("0", await File.ReadAllTextAsync(Path.Combine(directory, prefix + "consumer-state.json.exit")));
                }
                Assert.Equal(brokers.Length, actualBootstraps.Count);
                if (parallelBrokers) Assert.Equal(2, completed.Select(document => document.RootElement.GetProperty("runId").GetString()).Distinct().Count());
            }
            finally { foreach (var document in documents) document.Dispose(); }
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "product.log"), output.ToString());
            File.WriteAllText(Path.Combine(directory, "cli.exit"), exit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not-completed");
            using var cleanupProofBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var after = await InventoryAsync(cleanupProofBudget.Token);
            File.WriteAllText(Path.Combine(directory, "after.json"), JsonSerializer.Serialize(after));
            File.WriteAllText(Path.Combine(directory, "product-end.utc"), DateTimeOffset.UtcNow.ToString("O"));
            Assert.Equal(baseline.Containers, after.Containers);
            Assert.Equal(baseline.Networks, after.Networks);
            Assert.Equal(baseline.Volumes, after.Volumes);
            Assert.Equal(baseline.EngineId, after.EngineId);
            Assert.Equal(baseline.EngineVersion, after.EngineVersion);
            Assert.Equal(baseline.EngineEndpoint, after.EngineEndpoint);
        }
    }

    internal static string Scenario(string broker, string profile, string selector) => $$"""
        metadata: { name: managed-kafka-{{profile}} }
        environment:
          dependencies:
            {{broker}}:
              type: kafka
              security:
                profile: {{profile}}
                endpoint: "{{selector}}"
                caCert: ./ca.pem
        {{ClientIdentity(profile)}}
                serverArtifacts:
                  - { source: ./kafka.keystore.pem, target: /etc/kafka/secrets/server.pem }
                  - { source: ./kafka.truststore.pem, target: /etc/kafka/secrets/ca.pem }
              env:
                KAFKA_SSL_KEYSTORE_TYPE: PEM
                KAFKA_SSL_KEYSTORE_LOCATION: /etc/kafka/secrets/server.pem
                KAFKA_SSL_TRUSTSTORE_TYPE: PEM
                KAFKA_SSL_TRUSTSTORE_LOCATION: /etc/kafka/secrets/ca.pem
                KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS: "0"
        steps:
          - id: publish
            type: mq-publish.kafka
            target: {{broker}}
            topic: owned-orders
            payload: '{"id":"owned-order-443"}'
            timeout: 30s
          - id: consume
            type: mq-expect.kafka
            target: {{broker}}
            topic: owned-orders
            match: { payloadContains: owned-order-443 }
            verifyMode: RETRY
            timeout: 30s
        """;

    private static string ClientIdentity(string profile) => profile == "mtls"
        ? "        clientCert: ./client.pem\n        clientKey: ./client-key.pem" : string.Empty;

    private static string AvroSutScenario(string broker, string profile, string selector, string image)
        => Scenario(broker, profile, selector)
            .Replace("      type: kafka\n", "      type: kafka\n      schemaRegistry: true\n", StringComparison.Ordinal)
            .Replace("steps:\n", $$"""
                  services:
                    sut:
                      image: {{image}}
                      httpPort: 8080
                      env: { BOOTSTRAP: '${conn:{{broker}}}' }
                steps:

                """, StringComparison.Ordinal)
            .Replace("    payload: '{\"id\":\"owned-order-443\"}'\n", $$"""
                    payload: unused-in-avro-mode
                    avro:
                      schemaRegistry: {{broker}}
                      subject: owned-orders-value
                      schema: '{"type":"record","name":"Order","fields":[{"name":"id","type":"string"}]}'
                      record: { id: owned-order-443 }

                """, StringComparison.Ordinal)
            .Replace("    match: { payloadContains: owned-order-443 }\n", $$"""
                    avro: { schemaRegistry: {{broker}} }
                    match: { json: { '$.id': owned-order-443 } }

                """, StringComparison.Ordinal)
            + $$"""

                  - id: container-network
                    type: http.rest
                    target: sut
                    method: GET
                    path: /
                    verifyMode: RETRY
                    timeout: 30s
                    expect:
                      status: 200
                      json: { '$.apiVersionsError': '0', '$.advertisedError': '0', '$.correlation': '443' }
                """;

    private const string PlaintextSutSource = """
        import http.server, json, os, socket, struct
        def receive(sock, count):
            result = b''
            while len(result) < count:
                chunk = sock.recv(count - len(result))
                if not chunk: raise EOFError()
                result += chunk
            return result
        def exchange(host, port, api, version, correlation, body=b''):
            client = b'owned-sut'
            request = struct.pack('>hhih', api, version, correlation, len(client)) + client + body
            with socket.create_connection((host, port), 3) as sock:
                sock.sendall(struct.pack('>i', len(request)) + request)
                size = struct.unpack('>i', receive(sock, 4))[0]
                if size < 6 or size > 65536: raise ValueError()
                reply = receive(sock, size)
            if struct.unpack('>i', reply[:4])[0] != correlation: raise ValueError()
            return reply
        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args): pass
            def do_GET(self):
                try:
                    bootstrap = os.environ['BOOTSTRAP']
                    host, port = bootstrap.rsplit(':', 1)
                    reply = exchange(host, int(port), 18, 0, 443)
                    # Metadata v4 is non-flexible: correlation, throttle, brokers, id, host, port.
                    metadata = exchange(host, int(port), 3, 4, 444, struct.pack('>i?', 0, False))
                    if len(metadata) < 22 or struct.unpack('>i', metadata[8:12])[0] != 1: raise ValueError()
                    length = struct.unpack('>h', metadata[16:18])[0]
                    if length < 1 or length > 253 or len(metadata) < 22 + length: raise ValueError()
                    advertisedHost = metadata[18:18+length].decode('utf-8')
                    advertisedPort = struct.unpack('>i', metadata[18+length:22+length])[0]
                    reached = exchange(advertisedHost, advertisedPort, 18, 0, 445)
                    result = {'bootstrap': bootstrap, 'advertisedBootstrap': advertisedHost + ':' + str(advertisedPort), 'correlation': 443, 'apiVersionsError': struct.unpack('>h', reply[4:6])[0], 'advertisedError': struct.unpack('>h', reached[4:6])[0]}
                    status = 200
                except Exception as failure:
                    result = {'errorType': type(failure).__name__}
                    status = 503
                body = json.dumps(result).encode()
                self.send_response(status)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(body)))
                self.end_headers()
                self.wfile.write(body)
        http.server.HTTPServer(('0.0.0.0', 8080), Handler).serve_forever()
        """;

    private static ProcessStartInfo ProbeStartInfo(IEnumerable<string> arguments)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        Assert.True(Path.IsPathFullyQualified(host) && File.Exists(host), "The test runtime's dotnet host could not be resolved.");
        var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task ProbeAsync(string probe, string mode, string bootstrap, string directory, string profile, string report, CancellationToken cancellationToken, params string[] details)
    {
        var arguments = new List<string> { probe, mode, bootstrap, directory, profile, Path.Combine(directory, report) };
        arguments.AddRange(details);
        var result = await CaptureNativeProcessAsync(ProbeStartInfo(arguments),
            TimeSpan.FromSeconds(mode is "negative" or "consumer-control" ? 35 : mode == "consumer-state" ? 25 : 15), cancellationToken);
        File.WriteAllText(Path.Combine(directory, report + ".exit"), result.Exit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.WriteAllText(Path.Combine(directory, report + ".stdout"), result.Stdout);
        File.WriteAllText(Path.Combine(directory, report + ".stderr"), result.Stderr);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.Equal(0, result.Exit);
    }

    private static async Task ReapAsync(Process process)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(deadline.Token);
    }

    private sealed class ObservedLease : IKeptTopology
    {
        private readonly IKeptTopology _inner;
        private readonly OwnedBroker _owned;
        private readonly string _bootstrap;
        private readonly string _directory;
        private readonly string _prefix;
        private readonly string _profile;
        private readonly bool _proveObserver;
        private int _disposed;

        internal ObservedLease(IKeptTopology inner, OwnedBroker owned, string bootstrap, string directory, string prefix,
            string profile, bool proveObserver)
        {
            _inner = inner;
            _owned = owned;
            _bootstrap = bootstrap;
            _directory = directory;
            _prefix = prefix;
            _profile = profile;
            _proveObserver = proveObserver;
        }

        public IReadOnlyList<SecurityConfirmation> SecurityConfirmations => _inner.SecurityConfirmations;
        public IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices => _inner.EndpointSelectionNotices;
        public IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices => _inner.EndpointTrustNotices;
        public IReadOnlyDictionary<string, object> DiscoveredServices => _inner.DiscoveredServices;
        public IReadOnlyList<string> DependencyNames => _inner.DependencyNames;
        public IReadOnlyDictionary<string, string> DependencyTypes => _inner.DependencyTypes;
        public Task ReseedAsync(CancellationToken cancellationToken) => _inner.ReseedAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                using (var identityBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    Assert.Equal(_owned, await OwnedBroker.InspectAsync(_owned.Id, _owned.Fixture, identityBudget.Token));
                // The provider manually assigns retained partitions: its unique GroupId is client
                // configuration, not a subscribed/committed broker group. Rebalance logs cannot
                // establish that contract. Independently read the retained log and broker group state.
                using var before = await StateAsync("consumer-state.json", budget.Token);
                AssertManualConsumerState(before.RootElement, _bootstrap);
                if (_proveObserver)
                {
                    // This is explicit injected state, never attributed to the production step.
                    // A real Subscribe/Commit must be visible and rejected by the SAME observer.
                    var group = "vouchfx-expect-" + Guid.NewGuid().ToString("n");
                    try
                    {
                        await ProbeAsync(Probe, "consumer-control", _bootstrap, _directory, _profile,
                            _prefix + "consumer-control.json", budget.Token, "owned-orders", group);
                        using var control = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, _prefix + "consumer-control.json"), budget.Token));
                        Assert.Equal(group, control.RootElement.GetProperty("group").GetString());
                        Assert.True(control.RootElement.GetProperty("joined").GetBoolean());
                        Assert.False(string.IsNullOrEmpty(control.RootElement.GetProperty("member").GetString()));
                        Assert.Equal(control.RootElement.GetProperty("consumedOffset").GetInt64() + 1,
                            control.RootElement.GetProperty("committedOffset").GetInt64());
                        using var injected = await StateAsync("consumer-injected.json", budget.Token);
                        Assert.Contains(injected.RootElement.GetProperty("groups").EnumerateArray(), item => item.GetString() == group);
                        var refusal = Assert.Throws<Xunit.Sdk.DoesNotContainException>(() => AssertManualConsumerState(injected.RootElement, _bootstrap));
                        Assert.Contains(group, refusal.Message, StringComparison.Ordinal);
                    }
                    finally
                    {
                        // Only the exact unique group this control owns is removed. Topology disposal
                        // below remains unconditional if any observer or control assertion fails.
                        await ProbeAsync(Probe, "delete-control", _bootstrap, _directory, _profile,
                            _prefix + "consumer-control-cleanup.json", budget.Token, group);
                    }
                    using var restored = await StateAsync("consumer-restored.json", budget.Token);
                    AssertManualConsumerState(restored.RootElement, _bootstrap);
                    Assert.Equal(before.RootElement.GetProperty("partitions").GetRawText(),
                        restored.RootElement.GetProperty("partitions").GetRawText());
                }
                using var completionBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                Assert.Equal(_owned, await OwnedBroker.InspectAsync(_owned.Id, _owned.Fixture, completionBudget.Token));
            }
            finally { await _inner.DisposeAsync(); }
        }

        private static string Probe => Path.Combine(AppContext.BaseDirectory, "KafkaSecurityProbe", "Vouchfx.Kafka.Security.Probe.dll");

        private async Task<JsonDocument> StateAsync(string report, CancellationToken cancellationToken)
        {
            await ProbeAsync(Probe, "consumer-state", _bootstrap, _directory, _profile,
                _prefix + report, cancellationToken, "owned-orders");
            return JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, _prefix + report), cancellationToken));
        }
    }

    private static void AssertManualConsumerState(JsonElement state, string bootstrap)
    {
        Assert.Equal(bootstrap, state.GetProperty("bootstrap").GetString());
        var authority = new Uri("tcp://" + bootstrap);
        Assert.Equal(authority.Host, state.GetProperty("Host").GetString());
        Assert.Equal(authority.Port, state.GetProperty("Port").GetInt32());
        Assert.Equal("owned-orders", state.GetProperty("topic").GetString());
        var partitions = state.GetProperty("partitions").EnumerateArray().ToArray();
        Assert.NotEmpty(partitions);
        Assert.All(partitions, partition => Assert.True(partition.GetProperty("high").GetInt64() >= partition.GetProperty("low").GetInt64()));
        Assert.Contains(partitions, partition => partition.GetProperty("readOffset").ValueKind == JsonValueKind.Number
            && partition.GetProperty("readOffset").GetInt64() >= partition.GetProperty("low").GetInt64()
            && partition.GetProperty("readOffset").GetInt64() < partition.GetProperty("high").GetInt64());
        Assert.DoesNotContain(state.GetProperty("groups").EnumerateArray(),
            group => group.GetString()!.StartsWith("vouchfx-expect-", StringComparison.Ordinal));
    }

    private sealed record OwnedBroker(string Id, string Name, string Fixture, string Image, string CreatorPid,
        string CreatorStart, string ResourceUid, string LifecycleKey)
    {
        internal static async Task<OwnedBroker> CaptureAsync(string fixture, CancellationToken cancellationToken)
        {
            var candidates = await DockerAsync(new[] { "ps", "--no-trunc", "--format", "{{.ID}} {{.Names}}",
                "--filter", "label=com.microsoft.developer.usvc-dev.name", "--filter", "name=^/" + fixture + "-" }, cancellationToken);
            var selected = SelectExactCandidate(candidates, fixture);
            var owned = await InspectAsync(selected[0], fixture, cancellationToken);
            Assert.Equal("/" + selected[1], owned.Name);
            return owned;
        }

        internal static bool HasExactFixtureName(string actualName, string fixture)
            => Regex.IsMatch(actualName, "^/" + Regex.Escape(fixture) + "-[a-z0-9]{8}$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

        internal static string[] SelectExactCandidate(IEnumerable<string> candidates, string fixture)
            => Assert.Single(candidates.Select(candidate => candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
                candidate => candidate.Length == 2 && HasExactFixtureName("/" + candidate[1], fixture));

        internal static async Task<OwnedBroker> InspectAsync(string id, string fixture, CancellationToken cancellationToken)
        {
            const string format = "{\"id\":{{json .Id}},\"name\":{{json .Name}},\"image\":{{json .Image}},\"resourceName\":{{json (index .Config.Labels \"com.microsoft.developer.usvc-dev.name\")}},\"creatorPid\":{{json (index .Config.Labels \"com.microsoft.developer.usvc-dev.creatorProcessId\")}},\"creatorStart\":{{json (index .Config.Labels \"com.microsoft.developer.usvc-dev.creatorProcessStartTime\")}},\"uid\":{{json (index .Config.Labels \"com.microsoft.developer.usvc-dev.uid\")}},\"lifecycle\":{{json (index .Config.Labels \"com.microsoft.developer.usvc-dev.lifecycle-key\")}}}";
            var result = await DockerCaptureAsync(new[] { "inspect", id, "--format", format }, cancellationToken);
            Assert.Equal(0, result.Exit);
            using var document = JsonDocument.Parse(result.Stdout);
            var root = document.RootElement;
            string Required(string key) => Assert.IsType<string>(root.GetProperty(key).GetString());
            var name = Required("name");
            Assert.Equal(id, Required("id"));
            Assert.True(HasExactFixtureName(name, fixture), "Actual container name is not the exact owned fixture resource.");
            Assert.Equal(name[1..], Required("resourceName"));
            var creatorPid = Required("creatorPid");
            Assert.True(int.TryParse(creatorPid, out var pid) && pid > 0, "Missing owned DCP creator PID.");
            using var creator = Process.GetProcessById(pid);
            Assert.StartsWith("dcp", creator.ProcessName, StringComparison.OrdinalIgnoreCase);
            var creatorStart = Required("creatorStart");
            Assert.True(DateTimeOffset.TryParse(creatorStart, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var started), "Missing owned DCP creator start time.");
            Assert.InRange(Math.Abs((started - CreatorIdentityTime(creator)).TotalSeconds), 0, 1);
            var uid = Required("uid");
            Assert.True(Guid.TryParse(uid, out _), "Missing owned DCP resource UID.");
            var lifecycle = Required("lifecycle");
            Assert.Matches("^[a-f0-9]{32}$", lifecycle);
            return new OwnedBroker(id, name, fixture, Required("image"), creatorPid, creatorStart, uid, lifecycle);
        }
    }

    // DCP 0.24.3 uses boot-relative process identity on Linux, not wall-clock start time.
    // Keep the same PID/name checks and one-second birth-time tolerance on every platform.
    private static DateTimeOffset CreatorIdentityTime(Process process)
    {
        if (!OperatingSystem.IsLinux()) return new DateTimeOffset(process.StartTime);
        string stat;
        try { stat = File.ReadAllText($"/proc/{process.Id}/stat"); }
        catch (IOException) { throw new InvalidOperationException("The owned DCP process identity could not be read."); }
        catch (UnauthorizedAccessException) { throw new InvalidOperationException("The owned DCP process identity could not be read."); }
        if (!TryParseLinuxCreatorIdentity(stat, process.Id, LinuxClockTicks(), out var identity))
            throw new InvalidOperationException("The owned DCP process identity was malformed.");
        return identity;
    }

    private static bool TryParseLinuxCreatorIdentity(string stat, int expectedPid, long clockTicks, out DateTimeOffset identity)
    {
        identity = default;
        var open = stat.IndexOf('(');
        var close = stat.LastIndexOf(')');
        if (expectedPid <= 0 || clockTicks <= 0 || open <= 0 || close <= open || close + 1 >= stat.Length || stat[close + 1] != ' '
            || !int.TryParse(stat.AsSpan(0, open).Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var pid) || pid != expectedPid)
            return false;
        var fields = stat[(close + 2)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || !ulong.TryParse(fields[19], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var ticks) || ticks == 0)
            return false;
        try
        {
            var frequency = (ulong)clockTicks;
            var milliseconds = checked(ticks / frequency * 1000 + ticks % frequency * 1000 / frequency);
            identity = DateTimeOffset.MinValue.AddTicks(checked((long)milliseconds * TimeSpan.TicksPerMillisecond));
            return true;
        }
        catch (OverflowException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    [DllImport("libc", EntryPoint = "sysconf")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long LinuxClockTicks(int name = 2);

    [Theory]
    [InlineData("dcp")]
    [InlineData("dcp (owned control) worker")]
    public void CreatorIdentity_LinuxStatUsesNativeBirthTicksDespiteComplexProcessNames(string name)
    {
        var fields = Enumerable.Repeat("0", 20).ToArray();
        fields[0] = "S";
        fields[19] = "12345";
        Assert.True(TryParseLinuxCreatorIdentity($"123 ({name}) " + string.Join(' ', fields), 123, 100, out var identity));
        Assert.Equal(DateTimeOffset.MinValue.AddMilliseconds(123450), identity);
    }

    [Fact]
    public void CreatorIdentity_LinuxStatRefusesWrongPidMalformedOrMissingBirth()
    {
        var fields = Enumerable.Repeat("0", 20).ToArray();
        fields[0] = "S";
        fields[19] = "12345";
        var stat = "123 (dcp) " + string.Join(' ', fields);
        Assert.True(TryParseLinuxCreatorIdentity(stat, 123, 100, out var original));
        fields[19] = "12545";
        Assert.True(TryParseLinuxCreatorIdentity("123 (dcp) " + string.Join(' ', fields), 123, 100, out var reusedPid));
        Assert.NotInRange(Math.Abs((original - reusedPid).TotalSeconds), 0, 1);
        Assert.False(TryParseLinuxCreatorIdentity(stat, 456, 100, out _));
        Assert.False(TryParseLinuxCreatorIdentity(stat, 123, 0, out _));
        Assert.False(TryParseLinuxCreatorIdentity("123 (dcp) S", 123, 100, out _));
        Assert.False(TryParseLinuxCreatorIdentity("123 dcp S", 123, 100, out _));
        foreach (var invalid in new[] { "0", "invalid", ulong.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            fields[19] = invalid;
            Assert.False(TryParseLinuxCreatorIdentity("123 (dcp) " + string.Join(' ', fields), 123, 1, out _));
        }
    }

    [Fact]
    public void CreatorIdentity_CurrentProcessPreservesItsPlatformClock()
    {
        using var process = Process.GetCurrentProcess();
        var identity = CreatorIdentityTime(process);
        if (OperatingSystem.IsLinux())
        {
            Assert.True(TryParseLinuxCreatorIdentity(File.ReadAllText($"/proc/{process.Id}/stat"), process.Id, LinuxClockTicks(), out var expected));
            Assert.Equal(expected, identity);
            Assert.NotInRange(Math.Abs((identity - new DateTimeOffset(process.StartTime)).TotalSeconds), 0, 1);
        }
        else Assert.Equal(new DateTimeOffset(process.StartTime), identity);
    }

    [Fact]
    public async Task CreatorIdentity_RejectsAnotherLiveProcessBirthBeyondTheExistingTolerance()
    {
        var start = ProbeStartInfo(new[] { Path.Combine(AppContext.BaseDirectory, "KafkaSecurityProbe", "Vouchfx.Kafka.Security.Probe.dll"), "wait-for-termination" });
        using var first = Process.Start(start)!;
        Process? second = null;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            second = Process.Start(start)!;
            Assert.NotEqual(first.Id, second.Id);
            Assert.False(first.HasExited);
            Assert.False(second.HasExited);
            Assert.NotInRange(Math.Abs((CreatorIdentityTime(first) - CreatorIdentityTime(second)).TotalSeconds), 0, 1);
        }
        finally
        {
            if (second is not null) { ChildProcess.KillTreeQuietly(second); await ReapAsync(second); second.Dispose(); }
            ChildProcess.KillTreeQuietly(first);
            await ReapAsync(first);
        }
    }

    private sealed record DockerCapture(int Exit, string Stdout, string Stderr);

    private static async Task<DockerCapture> DockerCaptureAsync(string[] arguments, CancellationToken cancellationToken, int timeoutSeconds = 10)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return await CaptureNativeProcessAsync(start, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
    }

    private static async Task<DockerCapture> CaptureNativeProcessAsync(ProcessStartInfo start, TimeSpan timeout,
        CancellationToken cancellationToken, Action<Process>? observe = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Native observation child did not start.");
        Task<string> stdout = Task.FromResult(string.Empty);
        Task<string> stderr = Task.FromResult(string.Empty);
        try
        {
            stdout = ReadBoundedUtf8Async(process.StandardOutput.BaseStream);
            stderr = ReadBoundedUtf8Async(process.StandardError.BaseStream);
            observe?.Invoke(process);
            await process.WaitForExitAsync(deadline.Token);
            return new DockerCapture(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            ChildProcess.KillTreeQuietly(process);
            await ReapAsync(process);
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task<string> ReadBoundedUtf8Async(Stream stream)
    {
        const int maximumBytes = 1024 * 1024;
        using var captured = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer);
            if (count == 0) return Encoding.UTF8.GetString(captured.ToArray());
            if (captured.Length + count > maximumBytes)
                throw new InvalidOperationException("Native observation output exceeded its one-MiB stream budget.");
            await captured.WriteAsync(buffer.AsMemory(0, count));
        }
    }

    private sealed class NativeEvents : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly FileStream _stdout;
        private readonly FileStream _stderr;
        private readonly Task _copyOutput;
        private readonly Task _copyError;
        private readonly string _directory;

        private NativeEvents(Process process, string directory, FileStream stdout, FileStream stderr)
        {
            _process = process;
            _directory = directory;
            _stdout = stdout;
            _stderr = stderr;
            _copyOutput = process.StandardOutput.BaseStream.CopyToAsync(_stdout);
            _copyError = process.StandardError.BaseStream.CopyToAsync(_stderr);
        }

        internal static async Task<NativeEvents> StartAsync(string directory, CancellationToken cancellationToken,
            ProcessStartInfo? suppliedStart = null, Action<Process>? observe = null)
        {
            var since = DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O");
            var start = new ProcessStartInfo("docker")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "events", "--since", since, "--format", "{{json .}}" }) start.ArgumentList.Add(argument);
            var transferred = false;
            var stdout = File.Create(Path.Combine(directory, "native-events.jsonl"));
            FileStream? stderr = null;
            try
            {
                stderr = File.Create(Path.Combine(directory, "native-events.stderr"));
                var process = Process.Start(suppliedStart ?? start) ?? throw new InvalidOperationException("Native collector did not start.");
                NativeEvents? collector = null;
                try
                {
                    collector = new NativeEvents(process, directory, stdout, stderr);
                    observe?.Invoke(process);
                    File.WriteAllText(Path.Combine(directory, "native-events-start.utc"), since);
                    await Task.Delay(250, cancellationToken);
                    Assert.False(process.HasExited, "Native event collector exited before the product baseline.");
                    transferred = true;
                    return collector;
                }
                finally
                {
                    if (!transferred)
                    {
                        using (process)
                        {
                            ChildProcess.KillTreeQuietly(process);
                            await ReapAsync(process);
                            if (collector is not null)
                                await Task.WhenAll(collector._copyOutput, collector._copyError).WaitAsync(TimeSpan.FromSeconds(10));
                        }
                    }
                }
            }
            finally
            {
                if (!transferred)
                {
                    await stdout.DisposeAsync();
                    if (stderr is not null) await stderr.DisposeAsync();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                ChildProcess.KillTreeQuietly(_process);
                await ReapAsync(_process);
                await Task.WhenAll(_copyOutput, _copyError).WaitAsync(TimeSpan.FromSeconds(10));
                File.WriteAllText(Path.Combine(_directory, "native-events.exit"), _process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.WriteAllText(Path.Combine(_directory, "native-events.stop"), "owned collector stopped after product inventory");
            }
            finally
            {
                await _stdout.DisposeAsync();
                await _stderr.DisposeAsync();
                _process.Dispose();
            }
        }
    }

    private sealed record Inventory(string[] Containers, string[] Networks, string[] Volumes,
        string EngineId, string EngineVersion, string EngineEndpoint, string[] NetworkMetadata, DateTimeOffset CapturedAt);

    private static void CaptureArtifactIdentity(string directory)
    {
        var assemblies = new[] { typeof(RunCommand).Assembly, typeof(SuiteTopology).Assembly,
            typeof(Vouchfx.Engine.Runtime.ScenarioRunner).Assembly, typeof(ManagedKafkaSecurityDockerTests).Assembly };
        var files = assemblies.SelectMany(assembly => new[] { assembly.Location, Path.ChangeExtension(assembly.Location, ".pdb") })
            .Concat(new[] { Path.Combine(AppContext.BaseDirectory, "KafkaSecurityProbe", "Vouchfx.Kafka.Security.Probe.dll"),
                Path.Combine(AppContext.BaseDirectory, "KafkaSecurityProbe", "Vouchfx.Kafka.Security.Probe.pdb") });
        var manifest = files.Select(path =>
        {
            using var stream = File.OpenRead(path);
            return new { path, length = stream.Length, sha256 = Convert.ToHexString(SHA256.HashData(stream)) };
        }).ToArray();
        File.WriteAllText(Path.Combine(directory, "executing-artifacts.json"), JsonSerializer.Serialize(manifest));
    }

    private static async Task<Inventory> PrepareBaselineAsync(string directory, CancellationToken cancellationToken)
    {
        var before = await InventoryAsync(cancellationToken);
        File.WriteAllText(Path.Combine(directory, "before-preparation.json"), JsonSerializer.Serialize(before));
        File.WriteAllText(Path.Combine(directory, "preparation-start.utc"), DateTimeOffset.UtcNow.ToString("O"));
        Assert.Empty(await DockerAsync(new[] { "ps", "--no-trunc", "--format", "{{.ID}}",
            "--filter", "label=vouchfx.security.fixture.unmatched=" + Guid.NewGuid().ToString("N") }, cancellationToken));
        File.WriteAllText(Path.Combine(directory, "preparation-query.exit"), "0");
        var prepared = await InventoryAsync(cancellationToken);
        File.WriteAllText(Path.Combine(directory, "prepared.json"), JsonSerializer.Serialize(prepared));
        var baseline = await InventoryAsync(cancellationToken);
        File.WriteAllText(Path.Combine(directory, "baseline.json"), JsonSerializer.Serialize(baseline));
        Assert.Equal(prepared.Containers, baseline.Containers);
        Assert.Equal(prepared.Networks, baseline.Networks);
        Assert.Equal(prepared.Volumes, baseline.Volumes);
        Assert.Equal(prepared.EngineId, baseline.EngineId);
        return baseline;
    }

    private static async Task VerifyAfterAsync(string directory, Inventory baseline)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var after = await InventoryAsync(deadline.Token);
        File.WriteAllText(Path.Combine(directory, "after.json"), JsonSerializer.Serialize(after));
        File.WriteAllText(Path.Combine(directory, "product-end.utc"), DateTimeOffset.UtcNow.ToString("O"));
        Assert.Equal(baseline.Containers, after.Containers);
        Assert.Equal(baseline.Networks, after.Networks);
        Assert.Equal(baseline.Volumes, after.Volumes);
        Assert.Equal(baseline.EngineId, after.EngineId);
        Assert.Equal(baseline.EngineVersion, after.EngineVersion);
        Assert.Equal(baseline.EngineEndpoint, after.EngineEndpoint);
    }

    private static async Task<Inventory> InventoryAsync(CancellationToken cancellationToken)
    {
        var networks = await DockerAsync(new[] { "network", "ls", "--no-trunc", "--format", "{{.ID}}" }, cancellationToken);
        var metadata = new List<string>();
        foreach (var network in networks)
            metadata.AddRange(await DockerAsync(new[] { "network", "inspect", network, "--format", "{{json .}}" }, cancellationToken));
        return new Inventory(await DockerAsync(new[] { "ps", "-aq", "--no-trunc" }, cancellationToken), networks,
            await DockerAsync(new[] { "volume", "ls", "--format", "{{.Name}}" }, cancellationToken),
            Assert.Single(await DockerAsync(new[] { "info", "--format", "{{.ID}}" }, cancellationToken)),
            Assert.Single(await DockerAsync(new[] { "info", "--format", "{{.ServerVersion}}" }, cancellationToken)),
            Assert.Single(await DockerAsync(new[] { "context", "inspect", "--format", "{{.Endpoints.docker.Host}}" }, cancellationToken)),
            metadata.ToArray(), DateTimeOffset.UtcNow);
    }

    private static async Task<string[]> DockerAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var result = await DockerCaptureAsync(arguments, cancellationToken);
        Assert.True(result.Exit == 0, $"Docker inventory exit {result.Exit}; native output withheld.");
        return result.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();
    }
}

[CollectionDefinition("managed-kafka-security", DisableParallelization = true)]
public sealed class ManagedKafkaSecurityTestGroup { }
