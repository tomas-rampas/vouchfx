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
    [InlineData("2026-10-11T00:00:00Z INFO [GroupCoordinator 1] Preparing to rebalance group vouchfx-expect-0123456789abcdef0123456789abcdef in state PreparingRebalance", 1)]
    [InlineData("2026-10-11T00:00:00Z authored vouchfx-expect-0123456789abcdef0123456789abcdef", 0)]
    [InlineData("2026-10-11T00:00:00Z INFO [GroupCoordinator 1] Preparing to rebalance group unrelated-group", 0)]
    public void BrokerLogObserver_RequiresTimestampCoordinatorAndGeneratedGroupGrammar(string log, int count)
        => Assert.Equal(count, ObserveCoordinatorGroups(log).Length);

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
                var completion = CreatorCompletion.Capture(owned);
                File.WriteAllText(Path.Combine(directory, "creator-completion.json"), JsonSerializer.Serialize(completion));
                Assert.False(completion.OriginalProcessStillRunning, "The exact owned DCP creator survived completed topology disposal.");
            }
        }
    }

    private sealed record CreatorCompletion(DateTimeOffset CapturedAt, string Pid, string ExpectedStart,
        bool PidPresent, bool OriginalProcessStillRunning)
    {
        internal static CreatorCompletion Capture(OwnedBroker owned)
        {
            try
            {
                using var process = Process.GetProcessById(int.Parse(owned.CreatorPid, System.Globalization.CultureInfo.InvariantCulture));
                var expected = DateTimeOffset.Parse(owned.CreatorStart, System.Globalization.CultureInfo.InvariantCulture);
                var same = !process.HasExited && process.ProcessName.StartsWith("dcp", StringComparison.OrdinalIgnoreCase)
                    && Math.Abs((expected - new DateTimeOffset(process.StartTime)).TotalSeconds) <= 1;
                return new CreatorCompletion(DateTimeOffset.UtcNow, owned.CreatorPid, owned.CreatorStart, true, same);
            }
            catch (ArgumentException)
            {
                return new CreatorCompletion(DateTimeOffset.UtcNow, owned.CreatorPid, owned.CreatorStart, false, false);
            }
        }
    }

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
                return new ObservedLease(topology, owned, bootstrap, directory, prefix);
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
                    using var groups = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, prefix + "consumer-groups.json")));
                    Assert.True(groups.RootElement.GetProperty("groups").GetArrayLength() > 0);
                    actualBootstraps.Add(groups.RootElement.GetProperty("bootstrap").GetString()!);
                    Assert.Equal("0", await File.ReadAllTextAsync(Path.Combine(directory, prefix + "broker-log-observer.exit")));
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

    private static async Task ProbeAsync(string probe, string mode, string bootstrap, string directory, string profile, string report, CancellationToken cancellationToken, string? foreign = null)
    {
        var arguments = new List<string> { probe, mode, bootstrap, directory, profile, Path.Combine(directory, report) };
        if (foreign is not null) arguments.Add(foreign);
        var result = await CaptureNativeProcessAsync(ProbeStartInfo(arguments),
            TimeSpan.FromSeconds(mode == "negative" ? 35 : 15), cancellationToken);
        File.WriteAllText(Path.Combine(directory, report + ".exit"), result.Exit.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
        private int _disposed;

        internal ObservedLease(IKeptTopology inner, OwnedBroker owned, string bootstrap, string directory, string prefix)
        {
            _inner = inner;
            _owned = owned;
            _bootstrap = bootstrap;
            _directory = directory;
            _prefix = prefix;
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
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                Assert.Equal(_owned, await OwnedBroker.InspectAsync(_owned.Id, _owned.Fixture, budget.Token));
                var result = await DockerCaptureAsync(new[] { "logs", "--timestamps", "--tail", "500", _owned.Id }, budget.Token);
                File.WriteAllText(Path.Combine(_directory, _prefix + "broker-log-observer.exit"), result.Exit.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(0, result.Exit);
                // Raw broker output can contain material paths. Only coordinator-derived IDs and timestamps leave memory.
                Assert.True(result.Stdout.Length + result.Stderr.Length <= 2 * 1024 * 1024, "Bounded broker log tail exceeded the observation budget.");
                var matches = ObserveCoordinatorGroups(result.Stdout + "\n" + result.Stderr);
                File.WriteAllText(Path.Combine(_directory, _prefix + "consumer-groups.json"), JsonSerializer.Serialize(new
                {
                    bootstrap = _bootstrap,
                    containerId = _owned.Id,
                    groups = matches.Select(match => match.Group).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                    observations = matches,
                }));
                Assert.NotEmpty(matches);
            }
            finally { await _inner.DisposeAsync(); }
        }
    }

    private sealed record CoordinatorObservation(string Group, DateTimeOffset Timestamp);

    private static CoordinatorObservation[] ObserveCoordinatorGroups(string log)
    {
        var observations = new List<CoordinatorObservation>();
        foreach (var line in log.Split('\n'))
        {
            if (!line.Contains("GroupCoordinator", StringComparison.Ordinal) && !line.Contains("GroupMetadataManager", StringComparison.Ordinal)) continue;
            var match = Regex.Match(line,
                @"^(?<time>\S+).*?(?:Preparing to rebalance group |Stabilized group )(?<group>vouchfx-expect-[a-f0-9]{32})\b",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (match.Success && DateTimeOffset.TryParse(match.Groups["time"].Value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp))
                observations.Add(new CoordinatorObservation(match.Groups["group"].Value, timestamp));
        }
        return observations.ToArray();
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
            Assert.InRange(Math.Abs((started - new DateTimeOffset(creator.StartTime)).TotalSeconds), 0, 1);
            var uid = Required("uid");
            Assert.True(Guid.TryParse(uid, out _), "Missing owned DCP resource UID.");
            var lifecycle = Required("lifecycle");
            Assert.Matches("^[a-f0-9]{32}$", lifecycle);
            return new OwnedBroker(id, name, fixture, Required("image"), creatorPid, creatorStart, uid, lifecycle);
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
