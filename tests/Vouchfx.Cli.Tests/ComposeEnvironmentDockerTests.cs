using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Vouchfx.Cli;
using Vouchfx.Cli.Selection;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Cli.Tests;

/// <summary>Normal CLI watch/run ownership acceptance. Missing Docker prerequisites fail this explicit lane.</summary>
[Collection("Compose environment Docker acceptance")]
public sealed class ComposeEnvironmentDockerTests
{
    [Fact]
    [Trait("requires", "docker")]
    public async Task NormalWatch_ConfigEditOutsideSuite_TwoFreshPassingRuns_CleansOwnedResources()
    {
        await using var fixture = await LiveInputs.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var output = new ObservedOutput();
        var watching = ExecuteAsync(fixture.Suite, watch: true, output, stop.Token);
        await output.WaitForPassesAsync(1, deadline.Token);
        await fixture.WaitForCleanupAsync(deadline.Token);
        // The initial-run cleanup finishes before the file watchers are installed.
        await Task.Delay(500, deadline.Token);
        await File.AppendAllTextAsync(fixture.Config, "\n# trigger the generic external-input watcher\n", deadline.Token);
        await output.WaitForPassesAsync(2, deadline.Token);
        await fixture.WaitForCleanupAsync(deadline.Token);
        stop.Cancel();
        Assert.Equal(0, await watching);
        Assert.Equal(2, fixture.Projects.Count);
        Assert.Equal(2, output.Passes);
        Assert.Equal(6, output.Text.Split(": PASS (", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("FAIL", output.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Canary, output.Text, StringComparison.Ordinal);
        await fixture.AssertBaselineAndSentinelsAsync();
    }

    [Fact]
    [Trait("requires", "docker")]
    public async Task TwoNormalScenarios_AreFresh_FixedPortConflictAndReadinessDeadlineCannotPass()
    {
        await using var fixture = await LiveInputs.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var yaml = await File.ReadAllTextAsync(fixture.Suite, deadline.Token);
        var duplicate = Path.Combine(Path.GetDirectoryName(fixture.Suite)!, "second.e2e.yaml");
        await File.WriteAllTextAsync(duplicate, yaml, deadline.Token);
        var passing = new ObservedOutput();
        Assert.Equal(0, await ExecuteAsync(Path.GetDirectoryName(fixture.Suite)!, watch: false, passing, deadline.Token));
        Assert.Equal(2, passing.Passes);
        Assert.Equal(6, passing.Text.Split(": PASS (", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, fixture.Projects.Count);
        await fixture.AssertBaselineAndSentinelsAsync();

        var config = await File.ReadAllTextAsync(fixture.Config, deadline.Token);
        await File.WriteAllTextAsync(fixture.Config, config.Replace("startupTimeoutSeconds: 180", "startupTimeoutSeconds: 5", StringComparison.Ordinal)
            .Replace("status: 200", "status: 299", StringComparison.Ordinal), deadline.Token);
        var timeout = new ObservedOutput();
        Assert.Equal(3, await ExecuteAsync(fixture.Suite, watch: false, timeout, deadline.Token));
        Assert.DoesNotContain(": PASS", timeout.Text, StringComparison.Ordinal);
        Assert.Contains("ENV_ERROR", timeout.Text, StringComparison.Ordinal);
        await fixture.AssertBaselineAndSentinelsAsync();

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var overrides = Path.Combine(Path.GetDirectoryName(fixture.Config)!, "occupied-port.yaml");
            await File.WriteAllTextAsync(overrides, $$"""
                services:
                  web:
                    ports: !override
                      - target: 8080
                        published: {{port}}
                        host_ip: 127.0.0.1
                """, deadline.Token);
            config = config.Replace("]", ", ./occupied-port.yaml]", StringComparison.Ordinal)
                .Replace("startupTimeoutSeconds: 180", "startupTimeoutSeconds: 5", StringComparison.Ordinal);
            await File.WriteAllTextAsync(fixture.Config, config, deadline.Token);
            var conflict = new ObservedOutput();
            Assert.Equal(3, await ExecuteAsync(fixture.Suite, watch: false, conflict, deadline.Token));
            Assert.DoesNotContain(": PASS", conflict.Text, StringComparison.Ordinal);
            Assert.Contains("ENV_ERROR", conflict.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Canary, passing.Text + timeout.Text + conflict.Text, StringComparison.Ordinal);
            await fixture.AssertBaselineAndSentinelsAsync();
        }
        finally { listener.Stop(); }
    }

    [Fact]
    [Trait("requires", "docker")]
    public async Task NormalRun_AssertionFailure_AndCancellationDuringReadiness_CleanOnlyOwnedResources()
    {
        await using var fixture = await LiveInputs.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var yaml = await File.ReadAllTextAsync(fixture.Suite, deadline.Token);
        await File.WriteAllTextAsync(fixture.Suite, yaml.Replace("status: 201", "status: 418", StringComparison.Ordinal), deadline.Token);
        var failure = new ObservedOutput();
        Assert.Equal(1, await ExecuteAsync(fixture.Suite, watch: false, failure, deadline.Token));
        Assert.Contains("step 'create-order': FAIL", failure.Text, StringComparison.Ordinal);
        await fixture.WaitForCleanupAsync(deadline.Token);
        await fixture.AssertBaselineAndSentinelsAsync();

        await File.WriteAllTextAsync(fixture.Suite, yaml, deadline.Token);
        var config = await File.ReadAllTextAsync(fixture.Config, deadline.Token);
        await File.WriteAllTextAsync(fixture.Config, config.Replace("status: 200", "status: 299", StringComparison.Ordinal), deadline.Token);
        var cancelled = new ObservedOutput();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var running = ExecuteAsync(fixture.Suite, watch: false, cancelled, stop.Token);
        await fixture.WaitForOwnedContainersAsync(deadline.Token);
        await Task.Delay(200, deadline.Token);
        stop.Cancel();
        // Normal runs rethrow caller cancellation after joining owned cleanup.
        // The suite slot still records Inconclusive without executing any step.
        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(stop.Token, cancellation.CancellationToken);
        await fixture.WaitForCleanupAsync(deadline.Token);
        Assert.DoesNotContain(": PASS", cancelled.Text, StringComparison.Ordinal);
        Assert.Contains("INCONCLUSIVE", cancelled.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Canary, failure.Text + cancelled.Text, StringComparison.Ordinal);
        await fixture.AssertBaselineAndSentinelsAsync();
    }

    private static Task<int> ExecuteAsync(string path, bool watch, TextWriter output, CancellationToken ct)
        => RunCommand.ExecuteAsync(path, SelectionCriteria.None, parallel: null, watch,
            failOnEnvironmentError: true, failOnInconclusive: true, htmlReportPath: null,
            junitReportPath: null, eventsReportPath: null, eventsStreamPath: null, decorate: false,
            output, telemetryHook: null, cancellationToken: ct);

    private sealed class ObservedOutput : TextWriter
    {
        private readonly StringBuilder _text = new();
        private readonly object _gate = new();
        public override Encoding Encoding => Encoding.UTF8;
        internal int Passes { get; private set; }
        internal string Text { get { lock (_gate) return _text.ToString(); } }
        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                _text.AppendLine(value);
                if (value?.Contains("Scenario 'compose-orders': PASS", StringComparison.Ordinal) == true) Passes++;
            }
        }
        public override void Write(string? value) { lock (_gate) _text.Append(value); }
        internal async Task WaitForPassesAsync(int count, CancellationToken ct)
        {
            while (true)
            {
                lock (_gate) if (Passes >= count) return;
                await Task.Delay(50, ct);
            }
        }
    }

    private sealed class LiveInputs : IAsyncDisposable
    {
        private readonly string _variable = "VOUCHFX_COMPOSE_TEST_" + Guid.NewGuid().ToString("N");
        private readonly string _sentinel = "vouchfx-sentinel-" + Guid.NewGuid().ToString("N");
        private readonly string _ownedImageTag = "vouchfx-compose-fixture-" + Guid.NewGuid().ToString("N") + ":local";
        private readonly string _journalDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vouchfx", "environments");
        private readonly HashSet<string> _initialJournals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> _baseline = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _observeStop = new();
        private Task? _observer;
        private string? _container;
        private string? _network;
        private string? _volume;
        internal string Suite { get; private set; } = string.Empty;
        internal string Config { get; private set; } = string.Empty;
        internal string Canary { get; } = "compose-test-$literal-${not_interpolation}-" + Guid.NewGuid().ToString("N");
        internal HashSet<string> Projects { get; } = new(StringComparer.Ordinal);

        internal static async Task<LiveInputs> CreateAsync([CallerMemberName] string testName = "")
        {
            var fixture = new LiveInputs();
            try
            {
                var initialCaptureUtc = DateTimeOffset.UtcNow;
                var initialInventory = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var kind in new[] { "container", "network", "volume" }) initialInventory[kind] = await ListAsync(kind);
                var initialBridgeCreated = await DockerAsync("network", "inspect", "--format", "{{.Created}}", "bridge");
                var initialCaptureCompletedUtc = DateTimeOffset.UtcNow;
                fixture._network = await DockerAsync("network", "create", fixture._sentinel);
                fixture._volume = await DockerAsync("volume", "create", fixture._sentinel);
                fixture._container = await DockerAsync("run", "--detach", "--name", fixture._sentinel,
                    "--network", fixture._sentinel, "--mount", "type=volume,source=" + fixture._sentinel + ",target=/sentinel",
                    "alpine:3.20", "sleep", "3600");
                var directory = Directory.CreateTempSubdirectory("vouchfx-compose-acceptance-").FullName;
                var suites = Directory.CreateDirectory(Path.Combine(directory, "suites")).FullName;
                var inputs = Directory.CreateDirectory(Path.Combine(directory, "external")).FullName;
                var example = Path.Combine(RepoRoot.Resolve(), "examples", "compose-orders");
                fixture.Suite = Path.Combine(suites, "orders.e2e.yaml");
                fixture.Config = Path.Combine(inputs, "environment.compose.yaml");
                var yaml = await File.ReadAllTextAsync(Path.Combine(example, "compose.e2e.yaml"));
                await File.WriteAllTextAsync(fixture.Suite, yaml.Replace("./environment.compose.yaml", "../external/environment.compose.yaml", StringComparison.Ordinal));
                var config = await File.ReadAllTextAsync(Path.Combine(example, "environment.compose.yaml"));
                Assert.True(config.Contains("files: [./compose.yaml]", StringComparison.Ordinal),
                    "The example files list changed; refuse to build under its shared image tag.");
                // Keep the example's build context, but override its shared image tag only in
                // this fixture's external input. Compose resolves builds relative to the first file.
                await File.WriteAllTextAsync(Path.Combine(inputs, "test-image.compose.yaml"), $$"""
                    services:
                      web:
                        image: {{fixture._ownedImageTag}}
                    """);
                config = config.Replace("files: [./compose.yaml]",
                        "files: [" + Path.Combine(example, "compose.yaml").Replace('\\', '/') + ", ./test-image.compose.yaml]", StringComparison.Ordinal)
                    .Replace("${secret:env/VOUCHFX_COMPOSE_PASSWORD}", "${secret:env/" + fixture._variable + "}", StringComparison.Ordinal);
                await File.WriteAllTextAsync(fixture.Config, config);
                Environment.SetEnvironmentVariable(fixture._variable, fixture.Canary);
                if (Directory.Exists(fixture._journalDirectory))
                    fixture._initialJournals.UnionWith(Directory.GetFiles(fixture._journalDirectory));
                fixture._observer = fixture.ObserveAsync();

                // Preparation can coincide with a host inventory change. Preserve that diagnostic
                // interval separately, then require exact IDs throughout every product operation.
                var sentinelCreated = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kind in new[] { "container", "network", "volume" })
                {
                    var sentinel = kind == "container" ? fixture._container : kind == "network" ? fixture._network : fixture._volume;
                    sentinelCreated[kind] = await DockerAsync(kind, "inspect", "--format",
                        kind == "volume" ? "{{.CreatedAt}}" : "{{.Created}}", sentinel!);
                }
                Assert.Equal("true", await DockerAsync("container", "inspect", "--format", "{{.State.Running}}", fixture._container!));
                var productCaptureUtc = DateTimeOffset.UtcNow;
                var productInventory = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var kind in new[] { "container", "network", "volume" })
                {
                    var sentinel = kind == "container" ? fixture._container : kind == "network" ? fixture._network : fixture._volume;
                    var ids = await ListAsync(kind);
                    Assert.Contains(sentinel!, ids);
                    productInventory[kind] = ids;
                    fixture._baseline[kind] = ids.Where(id => !StringComparer.Ordinal.Equals(id, sentinel)).ToArray();
                }
                var productCaptureCompletedUtc = DateTimeOffset.UtcNow;
                var evidence = new
                {
                    testName,
                    initialCaptureUtc,
                    initialCaptureCompletedUtc,
                    productCaptureUtc,
                    productCaptureCompletedUtc,
                    initialInventory = BoundedInventory(initialInventory),
                    productInventory = BoundedInventory(productInventory),
                    sentinels = new { container = fixture._container, network = fixture._network, volume = fixture._volume },
                    sentinelCreated,
                    initialBridgeCreated,
                    bridgeCreated = await DockerAsync("network", "inspect", "--format", "{{.Created}}", "bridge"),
                };
                await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), fixture._sentinel + "-inventories.json"), JsonSerializer.Serialize(evidence));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private static Dictionary<string, object> BoundedInventory(Dictionary<string, string[]> inventory)
            => inventory.ToDictionary(pair => pair.Key,
                pair => (object)new { count = pair.Value.Length, ids = pair.Value.Take(1024).ToArray(), truncated = pair.Value.Length > 1024 },
                StringComparer.Ordinal);

        private async Task ObserveAsync()
        {
            try
            {
                while (true)
                {
                    foreach (var path in Journals()) lock (Projects) Projects.Add(Path.GetFileNameWithoutExtension(path));
                    await Task.Delay(25, _observeStop.Token);
                }
            }
            catch (OperationCanceledException) when (_observeStop.IsCancellationRequested) { }
        }
        private string[] Journals() => Directory.Exists(_journalDirectory)
            ? Directory.GetFiles(_journalDirectory).Except(_initialJournals, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
        internal async Task WaitForCleanupAsync(CancellationToken ct)
        {
            while (Journals().Length != 0) await Task.Delay(50, ct);
        }
        internal async Task WaitForOwnedContainersAsync(CancellationToken ct)
        {
            while (true)
            {
                string[] projects;
                lock (Projects) projects = Projects.ToArray();
                foreach (var project in projects)
                    if ((await DockerAsync("container", "ls", "--quiet", "--filter", "label=com.docker.compose.project=" + project))
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length >= 2) return;
                await Task.Delay(50, ct);
            }
        }
        internal async Task AssertBaselineAndSentinelsAsync()
        {
            foreach (var kind in new[] { "container", "network", "volume" })
            {
                var sentinel = kind == "container" ? _container : kind == "network" ? _network : _volume;
                Assert.Equal(_baseline[kind].Append(sentinel!).Order(StringComparer.Ordinal),
                    (await ListAsync(kind)).Order(StringComparer.Ordinal));
            }
            Assert.Contains("true", await DockerAsync("container", "inspect", "--format", "{{.State.Running}}", _container!), StringComparison.Ordinal);
            Assert.Empty(Journals());
        }
        public async ValueTask DisposeAsync()
        {
            _observeStop.Cancel();
            if (_observer is not null) await _observer;
            _observeStop.Dispose();
            Environment.SetEnvironmentVariable(_variable, null);
            if (_container is not null) await DockerAsync("container", "rm", "--force", _container);
            if (_network is not null) await DockerAsync("network", "rm", _network);
            if (_volume is not null) await DockerAsync("volume", "rm", _volume);
            // Remove only this fixture's tag. Never rebuild or retag the user example image.
            if (!string.IsNullOrEmpty(await DockerAsync("image", "ls", "--quiet", _ownedImageTag)))
                await DockerAsync("image", "rm", _ownedImageTag);
        }
        private static async Task<string[]> ListAsync(string kind)
        {
            var arguments = new List<string> { kind, "ls", "--quiet" };
            if (kind != "volume") arguments.Add("--no-trunc");
            if (kind == "container") arguments.Add("--all");
            return (await DockerAsync(arguments.ToArray())).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        private static async Task<string> DockerAsync(params string[] arguments)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker did not start.");
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            try { await process.WaitForExitAsync(deadline.Token); }
            finally
            {
                KillTreeQuietly(process);
                using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(reap.Token);
            }
            Assert.True(process.ExitCode == 0, "Docker acceptance prerequisite/operation failed (exit " + process.ExitCode + "); native output withheld.");
            await error;
            return (await output).Trim();
        }

        private static void KillTreeQuietly(Process process)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}

[CollectionDefinition("Compose environment Docker acceptance", DisableParallelization = true)]
public sealed class ComposeEnvironmentDockerCollectionDefinition { }
