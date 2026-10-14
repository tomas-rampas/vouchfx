using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Vouchfx.Cli;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Postgres;
using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Cli.Tests;

/// <summary>Real owned topology cycles; these cases execute only in the Docker lane.</summary>
[Collection("Compose environment Docker acceptance")]
public sealed class EnvironmentTopologyDurabilityDockerTests
{
    private const string WhoamiImage = "traefik/whoami@sha256:c4717a8d1f0134a7444e24f881160e033991f23027c6c5a9a3f8fd22e70d1d44";
    private readonly ITestOutputHelper _output;

    public EnvironmentTopologyDurabilityDockerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("requires", "docker")]
    public async Task Compose_RequestedProfileAndOrderedInterpolationFilesAndServiceEnvFile_AffectRealContainer()
    {
        var fixture = await OwnedFixture.CreateAsync(nameof(Compose_RequestedProfileAndOrderedInterpolationFilesAndServiceEnvFile_AffectRealContainer), _output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var evidence = new AcceptanceEvidence("compose-capabilities", 1, fixture);
        try
        {
            var inputs = Path.Combine(fixture.Directory, "capabilities");
            Directory.CreateDirectory(inputs);
            var hostnameVariable = fixture.PasswordVariable + "_HOSTNAME";
            Assert.Null(Environment.GetEnvironmentVariable(hostnameVariable));
            var expectedHostname = "vouchfx-" + Guid.NewGuid().ToString("N")[..12];
            await File.WriteAllTextAsync(Path.Combine(inputs, "first.env"), hostnameVariable + "=wrong-first-value\n", deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(inputs, "second.env"), hostnameVariable + "=" + expectedHostname + "\n", deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(inputs, "service.env"), "CAPABILITY_VALUE=from-service-env-file\n", deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(inputs, "compose.yaml"), $$"""
                services:
                  web:
                    image: {{WhoamiImage}}
                    profiles: [acceptance]
                    hostname: INTERPOLATED_HOSTNAME
                    env_file: ./service.env
                    ports:
                      - target: 80
                        host_ip: 127.0.0.1
                  excluded:
                    image: {{WhoamiImage}}
                    profiles: [not-selected]
                """.Replace("INTERPOLATED_HOSTNAME", "$" + "{" + hostnameVariable + "}", StringComparison.Ordinal), deadline.Token);
            await File.WriteAllTextAsync(Path.Combine(inputs, "environment.compose.yaml"), """
                version: 1
                files: [./compose.yaml]
                profiles: [acceptance]
                envFiles: [./first.env, ./second.env]
                startupTimeoutSeconds: 120
                cleanupTimeoutSeconds: 30
                services:
                  api: {service: web, port: 80, readiness: {path: /api, status: 200}}
                """, deadline.Token);
            var environment = SourceEnvironment(Path.Combine(inputs, "environment.compose.yaml"));
            var request = Request(environment, inputs);
            var plan = EnvironmentDriverRegistry.BuiltIn.Bind(environment, inputs);
            Assert.All(new[] { "first.env", "second.env", "service.env" }, name => Assert.Contains(Path.Combine(inputs, name), plan.InputFiles));
            evidence.AttemptedCycles = 1;
            evidence.CurrentCycle = 1;
            var before = await fixture.CaptureAsync();
            evidence.LastBefore = before;
            ResourceInventory active;
            string project;
            string actualHostname;
            string actualServiceEnvironment;
            string[] actualSelectedServices;
            await using (var topology = await request.WithPlan(plan).AcquireAsync(null, null, deadline.Token))
            {
                active = await fixture.CaptureAsync();
                evidence.LastActive = active;
                var container = Assert.Single(active.Containers.Except(before.Containers));
                project = await OwnedFixture.DockerAsync("container", "inspect", "--format", "{{index .Config.Labels \"com.docker.compose.project\"}}", container);
                Assert.StartsWith("vouchfx-", project, StringComparison.Ordinal);
                var services = await OwnedFixture.DockerAsync("container", "ls", "--all", "--filter", "label=com.docker.compose.project=" + project,
                    "--format", "{{.Label \"com.docker.compose.service\"}}");
                actualSelectedServices = services.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                Assert.Equal(new[] { "web" }, actualSelectedServices);
                var environmentJson = await OwnedFixture.DockerAsync("container", "inspect", "--format", "{{json .Config.Env}}", container);
                actualServiceEnvironment = Assert.Single(JsonSerializer.Deserialize<string[]>(environmentJson)!, value => value.StartsWith("CAPABILITY_VALUE=", StringComparison.Ordinal));
                Assert.Equal("CAPABILITY_VALUE=from-service-env-file", actualServiceEnvironment);
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var response = JsonDocument.Parse(await client.GetStringAsync((string)topology.DiscoveredServices["api"] + "/api", deadline.Token));
                actualHostname = response.RootElement.GetProperty("hostname").GetString()!;
                Assert.Equal(expectedHostname, actualHostname);
            }
            var after = await fixture.AssertReleasedAsync(before, active, deadline.Token);
            evidence.LastAfter = after;
            evidence.Cycles.Add(new
            {
                index = 1,
                completed = true,
                project,
                selectedServices = new[] { "web" },
                excludedServices = new[] { "excluded" },
                expectedHostname,
                actualHostname,
                actualServiceEnvironment,
                actualSelectedServices,
                interpolationFiles = new[] { "first.env", "second.env" },
                serviceEnvFile = "service.env",
                before,
                active,
                after
            });
            evidence.Completed = true;
        }
        catch (Exception error) { evidence.Failure = fixture.Scrub(error.ToString()); throw; }
        finally { await evidence.FinishAsync(_output); }
    }

    [Fact]
    [Trait("requires", "docker")]
    public async Task TwentyWarmAcquisitions_TenInlineAndTenCompose_ReuseOneCompiledImageAndReleaseFreshState()
    {
        var fixture = await OwnedFixture.CreateAsync(nameof(TwentyWarmAcquisitions_TenInlineAndTenCompose_ReuseOneCompiledImageAndReleaseFreshState), _output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var evidence = new AcceptanceEvidence("warm-host-compiled-image", 20, fixture);
        try
        {
            var composeConfig = await fixture.WritePostgresInputsAsync(Path.Combine(fixture.Directory, "warm-compose"), deadline.Token);
            var environments = new[] { InlineEnvironment(), SourceEnvironment(composeConfig) };
            var requests = environments.Select(environment => Request(environment, fixture.Directory)).ToArray();
            var plans = environments.Select(environment => EnvironmentDriverRegistry.BuiltIn.Bind(environment, fixture.Directory)).ToArray();
            var provider = new DbAssertPostgresProvider();
            var fragment = provider.Emit(new DbAssertPostgresModel("db", "SELECT marker FROM topology_durability", null,
                new PostgresExpectation(1, new Dictionary<string, string> { ["marker"] = "written" })), new CompileContext());
            var assembled = CsxAssembler.Assemble(new[] { ("persisted-state", fragment) });
            // One emitted image survives all twenty real acquisitions. Each execution still uses a fresh collectible ALC.
            var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource, additionalReferencePaths: new[]
            {
                typeof(NpgsqlConnection).Assembly.Location, typeof(JsonSerializer).Assembly.Location, typeof(CultureInfo).Assembly.Location,
            });
            var imageSha256 = Convert.ToHexString(SHA256.HashData(compiled.Image));
            using var owner = Process.GetCurrentProcess();
            var ownerBirthUtc = owner.StartTime.ToUniversalTime();
            var previousContainers = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < 20; index++)
            {
                var path = index % 2;
                evidence.AttemptedCycles++;
                evidence.CurrentCycle = index + 1;
                var before = await fixture.CaptureAsync();
                evidence.LastBefore = before;
                ResourceInventory active;
                await using (var topology = await requests[path].WithPlan(plans[path]).AcquireAsync(null, null, deadline.Token, secrets: fixture.Secrets))
                {
                    active = await fixture.CaptureAsync();
                    var created = active.Containers.Except(before.Containers).ToArray();
                    Assert.Single(created);
                    Assert.All(created, id => Assert.True(previousContainers.Add(id), "Each acquisition must create a previously unseen container."));
                    var connectionString = (string)topology.DiscoveredServices["db"];
                    await using var connection = new NpgsqlConnection(connectionString);
                    await connection.OpenAsync(deadline.Token);
                    await using (var fresh = new NpgsqlCommand("SELECT to_regclass('public.topology_durability') IS NULL", connection))
                        Assert.Equal(true, await fresh.ExecuteScalarAsync(deadline.Token));
                    await using (var mutate = new NpgsqlCommand("CREATE TABLE topology_durability(marker text NOT NULL); INSERT INTO topology_durability VALUES ('written')", connection))
                        await mutate.ExecuteNonQueryAsync(deadline.Token);
                    var vars = new Dictionary<string, object?> { [VarKeys.Connection("db")] = connectionString };
                    await RoslynScriptCompiler.RunIsolatedAsync(compiled, new ScriptGlobalVariables(vars), cancellationToken: deadline.Token);
                    Assert.Equal(Verdict.Pass, Assert.IsType<StepOutcome>(vars[VarKeys.Outcome("persisted_state")]).Verdict);
                    Assert.Equal(imageSha256, Convert.ToHexString(SHA256.HashData(compiled.Image)));
                    Assert.Equal(ownerBirthUtc, owner.StartTime.ToUniversalTime());
                }
                var after = await fixture.AssertReleasedAsync(before, active, deadline.Token);
                evidence.Cycles.Add(new
                {
                    index = index + 1,
                    completed = true,
                    driver = path == 0 ? "inline" : "compose",
                    ownerPid = owner.Id,
                    ownerBirthUtc,
                    compiledImageSha256 = imageSha256,
                    freshTableAbsent = true,
                    mutationObservedByCompiledProvider = true,
                    before,
                    active,
                    after
                });
                await evidence.SaveAsync(_output);
            }
            Assert.Equal(20, evidence.Cycles.Count);
            Assert.Equal(20, previousContainers.Count);
            evidence.Completed = true;
        }
        catch (Exception error) { evidence.Failure = fixture.Scrub(error.ToString()); throw; }
        finally { await evidence.FinishAsync(_output); }
    }

    [Fact]
    [Trait("requires", "docker")]
    public async Task FiveColdCliProcesses_ThreeInlineAndTwoCompose_FreshWritableProfilesAndOwnedClosure()
    {
        Assert.True(OperatingSystem.IsLinux(), "The cold writable-profile acceptance case runs in the Linux Docker lane.");
        var fixture = await OwnedFixture.CreateAsync(nameof(FiveColdCliProcesses_ThreeInlineAndTwoCompose_FreshWritableProfilesAndOwnedClosure), _output);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var evidence = new AcceptanceEvidence("cold-host-compiler-profile", 5, fixture);
        try
        {
            var cli = typeof(RunCommand).Assembly.Location;
            var cliDirectory = Path.GetDirectoryName(cli)!;
            // A solution build produces the executable's runtime configuration in its own output.
            if (!File.Exists(Path.Combine(cliDirectory, "vouchfx.runtimeconfig.json")))
            {
                var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
                cli = Path.Combine(RepoRoot.Resolve(), "src", "Cli", "Vouchfx.Cli", "bin", configuration, "net8.0", "vouchfx.dll");
            }
            Assert.True(File.Exists(cli));
            Assert.True(File.Exists(Path.ChangeExtension(cli, ".runtimeconfig.json")));
            evidence.CliSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(cli, deadline.Token)));
            var births = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < 5; index++)
            {
                evidence.AttemptedCycles++;
                evidence.CurrentCycle = index + 1;
                var profile = Path.Combine(fixture.Directory, "cold-" + (index + 1).ToString(CultureInfo.InvariantCulture));
                Assert.False(Directory.Exists(profile));
                Directory.CreateDirectory(profile);
                var compose = index % 2 == 1;
                var config = compose ? await fixture.WritePostgresInputsAsync(Path.Combine(profile, "inputs"), deadline.Token) : null;
                var suite = Path.Combine(profile, "durability.e2e.yaml");
                var environment = compose ? "  source:\n    driver: compose\n    config: " + config!.Replace('\\', '/') + "\n"
                    : "  dependencies:\n    db: {type: postgres}\n";
                await File.WriteAllTextAsync(suite, "metadata: {name: cold-topology}\nenvironment:\n" + environment + """
                    steps:
                      - id: fresh-database
                        type: db-assert.postgres
                        target: db
                        query: SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename = 'topology_durability'
                        expect: {rowCount: 0}
                      - id: ready-database
                        type: db-assert.postgres
                        target: db
                        query: SELECT 1 AS ready
                        expect: {rowCount: 1, row: {ready: '1'}}
                    """, deadline.Token);
                var before = await fixture.CaptureAsync();
                evidence.LastBefore = before;
                var result = await fixture.RunColdCliAsync(cli, suite, profile, before, deadline.Token);
                Assert.True(births.Add(result.Pid.ToString(CultureInfo.InvariantCulture) + ":" + result.BirthUtc.Ticks.ToString(CultureInfo.InvariantCulture)));
                Assert.Equal(0, result.ExitCode);
                Assert.True(result.ObservedContainers.Length > 0, "The observer must see an actual container; exit zero alone is insufficient.");
                if (compose)
                {
                    Assert.Empty(result.ObservedDcpProcesses);
                    Assert.NotEmpty(result.ObservedJournals);
                    Assert.All(result.ObservedJournals, journal => Assert.StartsWith(profile + Path.DirectorySeparatorChar, journal, StringComparison.Ordinal));
                }
                else Assert.NotEmpty(result.ObservedDcpProcesses);
                var completed = File.ReadLines(Path.Combine(profile, "events.jsonl")).Select(line => JsonDocument.Parse(line)).ToArray();
                try
                {
                    var steps = completed.Select(document => document.RootElement).Where(element => element.GetProperty("type").GetString() == "step-completed").ToArray();
                    Assert.Equal(2, steps.Length);
                    Assert.All(steps, step => Assert.Equal("PASS", step.GetProperty("verdict").GetString()));
                    var scenario = Assert.Single(completed.Select(document => document.RootElement), element => element.GetProperty("type").GetString() == "scenario-completed");
                    Assert.Equal("PASS", scenario.GetProperty("verdict").GetString());
                }
                finally { foreach (var document in completed) document.Dispose(); }
                evidence.LastActive = result.Active;
                var after = await fixture.AssertReleasedAsync(before, result.Active, deadline.Token);
                evidence.LastAfter = after;
                Assert.Empty(Directory.GetFiles(profile, "vouchfx-*.json", SearchOption.AllDirectories));
                evidence.Cycles.Add(new
                {
                    index = index + 1,
                    completed = true,
                    driver = compose ? "compose" : "inline",
                    result.Pid,
                    result.BirthUtc,
                    result.ExitCode,
                    profile,
                    compilerLifetime = "new CLI process",
                    result.ObservedContainers,
                    result.ObservedDcpProcesses,
                    result.ObservedJournals,
                    result.Stdout,
                    result.Stderr,
                    before,
                    active = result.Active,
                    after
                });
                await evidence.SaveAsync(_output);
            }
            Assert.Equal(5, evidence.Cycles.Count);
            Assert.Equal(5, births.Count);
            evidence.Completed = true;
        }
        catch (Exception error) { evidence.Failure = fixture.Scrub(error.ToString()); throw; }
        finally { await evidence.FinishAsync(_output); }
    }

    private static EnvironmentSpec InlineEnvironment() => new(null,
        new Dictionary<string, DependencySpec> { ["db"] = new("postgres", null, null) }, null, null, null);

    private static EnvironmentSpec SourceEnvironment(string config) => new(null, null, null, null, null)
    { Source = new EnvironmentSourceSpec("compose", config) };

    private static TopologyRequest Request(EnvironmentSpec environment, string directory) => new(environment,
        typeof(RunCommand).Assembly.GetName().Name, TimeSpan.FromSeconds(120), directory,
        new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

    [Fact]
    public async Task RawCapture_OneByteReads_PreserveBinaryAndUtf8Bytes()
    {
        var bytes = Encoding.UTF8.GetBytes("split-\u00e9").Concat(new byte[] { 0, 255, 128 }).ToArray();
        using var input = new ChunkedReadStream(bytes, 1);
        using var output = new MemoryStream();
        var status = await CaptureRawAsync(input, output, 4096, CancellationToken.None);
        Assert.True(status.Completed);
        Assert.Null(status.Error);
        Assert.Equal(bytes.Length, status.ObservedBytes);
        Assert.Equal(bytes, output.ToArray());
    }

    [Fact]
    public async Task RawCapture_ReaderFailure_RetainsAlreadyWrittenPrefix()
    {
        using var input = new ChunkedReadStream(new byte[] { 1, 2, 3, 4, 5 }, 3, failAfterReads: 1);
        using var output = new MemoryStream();
        var status = await CaptureRawAsync(input, output, 4096, CancellationToken.None);
        Assert.False(status.Completed);
        Assert.Equal(nameof(IOException), status.Error);
        Assert.Equal(3, status.ObservedBytes);
        Assert.Equal(new byte[] { 1, 2, 3 }, output.ToArray());
    }

    [Fact]
    public async Task RawCapture_CancellationAfterRead_RetainsItsChunkUnderIndependentToken()
    {
        using var cancellation = new CancellationTokenSource();
        using var input = new ChunkedReadStream(new byte[] { 1, 2, 3, 4 }, 2, afterFirstRead: cancellation.Cancel);
        using var output = new MemoryStream();
        var status = await CaptureRawAsync(input, output, 4096, cancellation.Token);
        Assert.False(status.Completed);
        Assert.Equal(nameof(OperationCanceledException), status.Error);
        Assert.Equal(2, status.ObservedBytes);
        Assert.Equal(new byte[] { 1, 2 }, output.ToArray());
    }

    [Fact]
    public async Task RawCapture_CapBreach_RetainsExactlyItsBoundedPrefix()
    {
        var bytes = Enumerable.Range(0, 4100).Select(index => (byte)(index % 256)).ToArray();
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        var status = await CaptureRawAsync(input, output, 4096, CancellationToken.None);
        Assert.False(status.Completed);
        Assert.Equal("OutputLimitExceeded", status.Error);
        Assert.Equal(4100, status.ObservedBytes);
        Assert.Equal(bytes[..4096], output.ToArray());
    }

    [Fact]
    public async Task RawCapture_ExactLimitAndEmptyEof_AreComplete()
    {
        foreach (var bytes in new[] { Array.Empty<byte>(), new byte[4096] })
        {
            using var input = new MemoryStream(bytes);
            using var output = new MemoryStream();
            var status = await CaptureRawAsync(input, output, 4096, CancellationToken.None);
            Assert.True(status.Completed);
            Assert.Null(status.Error);
            Assert.Equal(bytes.Length, status.ObservedBytes);
            Assert.Equal(bytes, output.ToArray());
        }
    }

    [Fact]
    public async Task RawCapture_DestinationFailure_RetainsBytesWrittenBeforeTheFault()
    {
        using var input = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        using var output = new PartialWriteFailureStream();
        var status = await CaptureRawAsync(input, output, 4096, CancellationToken.None);
        Assert.False(status.Completed);
        Assert.Equal(nameof(IOException), status.Error);
        Assert.Equal(4, status.ObservedBytes);
        Assert.Equal(new byte[] { 1, 2 }, output.ToArray());
    }

    private sealed class ChunkedReadStream : MemoryStream
    {
        private readonly int _chunkSize;
        private readonly int? _failAfterReads;
        private readonly Action? _afterFirstRead;
        private int _reads;
        internal ChunkedReadStream(byte[] bytes, int chunkSize, int? failAfterReads = null, Action? afterFirstRead = null) : base(bytes)
        { _chunkSize = chunkSize; _failAfterReads = failAfterReads; _afterFirstRead = afterFirstRead; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_failAfterReads is { } count && _reads >= count) throw new IOException("Synthetic reader fault.");
            var read = await base.ReadAsync(buffer[..Math.Min(buffer.Length, _chunkSize)], cancellationToken);
            _reads++;
            if (_reads == 1) _afterFirstRead?.Invoke();
            return read;
        }
    }

    private sealed class PartialWriteFailureStream : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer[..Math.Min(2, buffer.Length)], cancellationToken);
            throw new IOException("Synthetic destination fault after a partial write.");
        }
    }

    private const int RawOutputLimitBytes = 1024 * 1024;
    private sealed record RawCaptureStatus(bool Completed, string? Error, long ObservedBytes, int LimitBytes);
    private sealed record RawStreamBinding(string FileName, long? Bytes, string? Sha256, bool Completed, string? Error, long ObservedBytes, int LimitBytes);

    private static async Task<RawCaptureStatus> CaptureRawAsync(Stream input, Stream output, int limitBytes, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitBytes);
        var buffer = new byte[4096];
        long observed = 0;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), ct);
                if (read == 0) return new RawCaptureStatus(true, null, observed, limitBytes);
                observed += read;
                var remaining = limitBytes - checked((int)output.Length);
                var retain = Math.Min(read, remaining);
                if (retain > 0)
                {
                    // Once a chunk was read, retain it under an independent token before another read.
                    await output.WriteAsync(buffer.AsMemory(0, retain), CancellationToken.None);
                    await output.FlushAsync(CancellationToken.None);
                }
                if (read > remaining) return new RawCaptureStatus(false, "OutputLimitExceeded", observed, limitBytes);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new RawCaptureStatus(false, error.GetType().Name, observed, limitBytes);
        }
    }

    private static async Task<RawStreamBinding> CaptureRawFileAsync(Stream input, string file, CancellationToken ct)
    {
        RawCaptureStatus? status = null;
        try
        {
            await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, FileOptions.Asynchronous);
            status = await CaptureRawAsync(input, output, RawOutputLimitBytes, ct);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            status = new RawCaptureStatus(false, error.GetType().Name, status?.ObservedBytes ?? 0, RawOutputLimitBytes);
        }
        try
        {
            var bytes = await File.ReadAllBytesAsync(file, CancellationToken.None);
            return new RawStreamBinding(Path.GetFileName(file), bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)),
                status.Completed, status.Error, status.ObservedBytes, status.LimitBytes);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Unreadable/missing raw data stays an explicit failed binding; never invent an empty raw stream.
            return new RawStreamBinding(Path.GetFileName(file), null, null, false,
                (status.Error is null ? "" : status.Error + ";") + "Snapshot:" + error.GetType().Name, status.ObservedBytes, status.LimitBytes);
        }
    }

    private static void RefuseFailedCapture(Task<RawStreamBinding> capture)
    {
        if (capture.IsFaulted || capture.IsCanceled || (capture.IsCompletedSuccessfully && !capture.Result.Completed))
            throw new IOException("CLI raw output capture failed; retained bytes and per-stream metadata cannot supply a passing result.");
    }

    private sealed class CompileContext : ICompileContext
    {
        public string StepId => "persisted-state";
        public string SuiteDirectory => Directory.GetCurrentDirectory();
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } = new Dictionary<string, CaptureExpr>();
    }

    private sealed class AcceptanceEvidence
    {
        internal AcceptanceEvidence(string kind, int expectedCycles, OwnedFixture fixture)
        { Kind = kind; ExpectedCycles = expectedCycles; Fixture = fixture; }
        private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
        private OwnedFixture Fixture { get; }
        public int SchemaVersion { get; } = 1;
        public string Kind { get; }
        public int ExpectedCycles { get; }
        public bool Completed { get; set; }
        public int CompletedCycles => Cycles.Count;
        public string? Failure { get; set; }
        public string? CleanupFailure { get; set; }
        public bool FixtureCleanupCompleted { get; set; }
        public string? CheckoutCommit { get; } = Environment.GetEnvironmentVariable("GITHUB_SHA");
        public string? PullRequestHeadCommit { get; } = Environment.GetEnvironmentVariable("VOUCHFX_TOPOLOGY_PR_HEAD_SHA");
        public string? BuiltCliInformationalVersion { get; } = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(RunCommand).Assembly)?.InformationalVersion;
        public string BuiltCliSha256 { get; } = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RunCommand).Assembly.Location)));
        public string? CliSha256 { get; set; }
        public int AttemptedCycles { get; set; }
        public int? CurrentCycle { get; set; }
        public ResourceInventory? LastBefore { get; set; }
        public ResourceInventory? LastActive { get; set; }
        public ResourceInventory? LastAfter { get; set; }
        public string DockerCachePolicy { get; } = "existing image layers retained; no image prune or uncached-pull claim";
        public List<object> Cycles { get; } = new();
        internal async Task FinishAsync(ITestOutputHelper output)
        {
            Exception? cleanupError = null;
            try { await Fixture.DisposeAsync(); FixtureCleanupCompleted = true; }
            catch (Exception error) { cleanupError = error; CleanupFailure = Fixture.Scrub(error.ToString()); }
            await SaveAsync(output);
            if (cleanupError is not null && Failure is null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupError).Throw();
        }

        internal async Task SaveAsync(ITestOutputHelper output)
        {
            var directory = Environment.GetEnvironmentVariable("VOUCHFX_TOPOLOGY_ACCEPTANCE_RESULTS") ?? Path.Combine(Fixture.Directory, "evidence");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, Kind + ".json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(this, s_jsonOptions));
            output.WriteLine("Topology acceptance {0}: {1}/{2}; completed={3}; evidence={4}", Kind, CompletedCycles, ExpectedCycles, Completed, file);
        }
    }

    private sealed record ResourceInventory(string[] Containers, string[] Networks, string[] Volumes,
        IReadOnlyDictionary<string, string> Created, string[] DcpProcesses, string[] Journals);
    private sealed record ColdResult(int Pid, DateTime BirthUtc, int ExitCode, string[] ObservedContainers, string[] ObservedDcpProcesses, string[] ObservedJournals, RawStreamBinding Stdout, RawStreamBinding Stderr, ResourceInventory Active);

    private sealed class OwnedFixture : IAsyncDisposable
    {
        private readonly string _sentinel = "vouchfx-durability-sentinel-" + Guid.NewGuid().ToString("N");
        private readonly string _journalDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vouchfx", "environments");
        private string? _container;
        private string? _network;
        private string? _volume;
        private ResourceInventory? _initial;
        private string Password { get; } = "fixture-" + Guid.NewGuid().ToString("N");
        internal string PasswordVariable { get; } = "VOUCHFX_TOPOLOGY_PASSWORD_" + Guid.NewGuid().ToString("N");
        internal string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("vouchfx-topology-acceptance-").FullName;
        internal ISecretAccessor Secrets { get; } = new SecretAccessor(new SecretSourceCatalog(new[] { new EnvironmentSecretResolver() }));

        internal static async Task<OwnedFixture> CreateAsync(string name, ITestOutputHelper output)
        {
            var fixture = new OwnedFixture();
            try
            {
                Assert.Null(Environment.GetEnvironmentVariable(fixture.PasswordVariable));
                Environment.SetEnvironmentVariable(fixture.PasswordVariable, fixture.Password);
                fixture._initial = await fixture.CaptureAsync();
                fixture._network = await DockerAsync("network", "create", fixture._sentinel);
                fixture._volume = await DockerAsync("volume", "create", fixture._sentinel);
                fixture._container = await DockerAsync("run", "--detach", "--name", fixture._sentinel, "--network", fixture._network,
                    "--mount", "type=volume,source=" + fixture._volume + ",target=/sentinel", WhoamiImage);
                output.WriteLine("Topology fixture {0}: {1}; source CLI SHA256={2}", name, fixture.Directory,
                    Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(RunCommand).Assembly.Location))));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        internal string Scrub(string text) => text.Replace(Password, "***REDACTED***", StringComparison.Ordinal);

        internal async Task<string> WritePostgresInputsAsync(string directory, CancellationToken ct)
        {
            System.IO.Directory.CreateDirectory(directory);
            var secret = "$" + "{secret:env/" + PasswordVariable + "}";
            await File.WriteAllTextAsync(Path.Combine(directory, "compose.yaml"), """
                services:
                  postgres:
                    image: postgres:18.3
                    environment:
                      POSTGRES_DB: app
                      POSTGRES_USER: app
                      POSTGRES_PASSWORD: PASSWORD_INTERPOLATION
                    ports:
                      - target: 5432
                        host_ip: 127.0.0.1
                    volumes: [data:/var/lib/postgresql]
                volumes:
                  data: {}
                """.Replace("PASSWORD_INTERPOLATION", "$" + "{TEST_PASSWORD}", StringComparison.Ordinal), ct);
            var config = Path.Combine(directory, "environment.compose.yaml");
            await File.WriteAllTextAsync(config, $$"""
                version: 1
                files: [./compose.yaml]
                environment:
                  TEST_PASSWORD: '{{secret}}'
                startupTimeoutSeconds: 120
                cleanupTimeoutSeconds: 30
                dependencies:
                  db:
                    type: postgres
                    service: postgres
                    port: 5432
                    database: app
                    username: app
                    password: '{{secret}}'
                """, ct);
            return config;
        }

        internal async Task<ResourceInventory> CaptureAsync(string? profile = null, bool includeCreated = true)
        {
            var containers = await ListAsync("container");
            var networks = await ListAsync("network");
            var volumes = await ListAsync("volume");
            var created = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (kind, ids) in includeCreated ? new[] { ("container", containers), ("network", networks), ("volume", volumes) } : Array.Empty<(string, string[])>())
                foreach (var id in ids) created[kind + ":" + id] = await DockerAsync(kind, "inspect", "--format",
                    kind == "volume" ? "{{.CreatedAt}}" : "{{.Created}}", id);
            var journals = System.IO.Directory.Exists(_journalDirectory) ? System.IO.Directory.GetFiles(_journalDirectory) : Array.Empty<string>();
            if (profile is not null) journals = journals.Concat(System.IO.Directory.GetFiles(profile, "vouchfx-*.json", SearchOption.AllDirectories)).ToArray();
            return new ResourceInventory(containers, networks, volumes, created, DcpIdentities(), journals.Order(StringComparer.Ordinal).ToArray());
        }

        internal async Task<ResourceInventory> AssertReleasedAsync(ResourceInventory before, ResourceInventory active, CancellationToken ct)
        {
            using var join = CancellationTokenSource.CreateLinkedTokenSource(ct);
            join.CancelAfter(TimeSpan.FromSeconds(30));
            ResourceInventory after;
            while (true)
            {
                after = await CaptureAsync();
                if (after.Containers.SequenceEqual(before.Containers) && after.Networks.SequenceEqual(before.Networks)
                    && after.Volumes.SequenceEqual(before.Volumes) && after.DcpProcesses.SequenceEqual(before.DcpProcesses)
                    && after.Journals.SequenceEqual(before.Journals)) break;
                await Task.Delay(100, join.Token);
            }
            Assert.Equal(before.Created.OrderBy(pair => pair.Key, StringComparer.Ordinal), after.Created.OrderBy(pair => pair.Key, StringComparer.Ordinal));
            Assert.All(active.Containers.Except(before.Containers), id => Assert.DoesNotContain(id, after.Containers));
            Assert.All(active.Networks.Except(before.Networks), id => Assert.DoesNotContain(id, after.Networks));
            Assert.All(active.Volumes.Except(before.Volumes), id => Assert.DoesNotContain(id, after.Volumes));
            Assert.Equal("true", await DockerAsync("container", "inspect", "--format", "{{.State.Running}}", _container!));
            return after;
        }

        internal async Task<ColdResult> RunColdCliAsync(string cli, string suite, string profile, ResourceInventory before, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = profile,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in new[] { cli, "run", suite, "--fail-on-env-error", "--fail-on-inconclusive", "--events", Path.Combine(profile, "events.jsonl") })
                start.ArgumentList.Add(arg);
            foreach (var key in new[] { "LOCALAPPDATA", "XDG_DATA_HOME", "DOTNET_CLI_HOME", "NUGET_PACKAGES", "TMP", "TEMP", "TMPDIR" })
                start.Environment[key] = System.IO.Directory.CreateDirectory(Path.Combine(profile, key.ToLowerInvariant())).FullName;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The CLI could not start.");
            var pid = process.Id;
            var birthUtc = process.StartTime.ToUniversalTime();
            var evidenceRoot = Environment.GetEnvironmentVariable("VOUCHFX_TOPOLOGY_ACCEPTANCE_RESULTS");
            var retained = evidenceRoot is null ? profile : Path.Combine(evidenceRoot, "cold-cli", Path.GetFileName(profile));
            System.IO.Directory.CreateDirectory(retained);
            var stdout = CaptureRawFileAsync(process.StandardOutput.BaseStream, Path.Combine(retained, "stdout.txt"), deadline.Token);
            var stderr = CaptureRawFileAsync(process.StandardError.BaseStream, Path.Combine(retained, "stderr.txt"), deadline.Token);
            var observed = new HashSet<string>(StringComparer.Ordinal);
            var observedDcp = new HashSet<string>(StringComparer.Ordinal);
            var observedJournals = new HashSet<string>(StringComparer.Ordinal);
            ResourceInventory active = before;
            try
            {
                while (!process.HasExited)
                {
                    RefuseFailedCapture(stdout);
                    RefuseFailedCapture(stderr);
                    var sample = await CaptureAsync(profile, includeCreated: false);
                    foreach (var id in sample.Containers.Except(before.Containers)) observed.Add(id);
                    foreach (var id in sample.DcpProcesses.Except(before.DcpProcesses)) observedDcp.Add(id);
                    foreach (var journal in sample.Journals.Except(before.Journals)) observedJournals.Add(journal);
                    if (sample.Containers.Length > active.Containers.Length) active = sample;
                    await Task.Delay(100, deadline.Token);
                }
                await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
                RefuseFailedCapture(stdout);
                RefuseFailedCapture(stderr);
                return new ColdResult(pid, birthUtc, process.ExitCode, observed.Order(StringComparer.Ordinal).ToArray(),
                    observedDcp.Order(StringComparer.Ordinal).ToArray(), observedJournals.Order(StringComparer.Ordinal).ToArray(), stdout.Result, stderr.Result, active);
            }
            finally
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(reap.Token);
                }
                finally
                {
                    // A failed/cancelled/reap-failed attempt retains its first bytes too; it never becomes complete evidence.
                    deadline.Cancel();
                    RawStreamBinding? stdoutBinding = null;
                    RawStreamBinding? stderrBinding = null;
                    string? captureError = null;
                    try { stdoutBinding = await stdout; }
                    catch (Exception error) { captureError = "stdout:" + error.GetType().Name; }
                    try { stderrBinding = await stderr; }
                    catch (Exception error) { captureError = (captureError is null ? "" : captureError + ";") + "stderr:" + error.GetType().Name; }
                    await File.WriteAllTextAsync(Path.Combine(retained, "captures.json"), JsonSerializer.Serialize(new
                    { SchemaVersion = 1, Stdout = stdoutBinding, Stderr = stderrBinding, CaptureError = captureError }), CancellationToken.None);
                    if (process.HasExited)
                        await File.WriteAllTextAsync(Path.Combine(retained, "exit.txt"), process.ExitCode.ToString(CultureInfo.InvariantCulture), CancellationToken.None);
                    if (evidenceRoot is not null)
                    {
                        foreach (var name in new[] { "events.jsonl", "durability.e2e.yaml" })
                        {
                            var input = Path.Combine(profile, name);
                            if (File.Exists(input)) File.Copy(input, Path.Combine(retained, name), overwrite: false);
                        }
                    }
                }
            }
        }

        private static string[] DcpIdentities()
        {
            var identities = new List<string>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.ProcessName is "dcp" or "dcpctrl") identities.Add(process.Id.ToString(CultureInfo.InvariantCulture) + ":" +
                            process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
                    }
                    catch (InvalidOperationException) { }
                }
            }
            return identities.Order(StringComparer.Ordinal).ToArray();
        }

        private static async Task<string[]> ListAsync(string kind)
        {
            var args = new List<string> { kind, "ls", "--quiet" };
            if (kind != "volume") args.Add("--no-trunc");
            if (kind == "container") args.Add("--all");
            return (await DockerAsync(args.ToArray())).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal).ToArray();
        }

        internal static async Task<string> DockerAsync(params string[] arguments)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker could not start.");
            var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
            try
            {
                await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
                Assert.True(process.ExitCode == 0, "Owned topology fixture Docker operation failed (exit " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + "); output withheld.");
                return (await stdout).Trim();
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(reap.Token);
            }
        }

        private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
        {
            var text = new StringBuilder();
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
            {
                if (text.Length + read > 1024 * 1024) throw new IOException("Owned fixture child output exceeded 1 MiB.");
                text.Append(buffer, 0, read);
            }
            return text.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            Environment.SetEnvironmentVariable(PasswordVariable, null);
            if (_container is not null) { await DockerAsync("container", "rm", "--force", _container); _container = null; }
            if (_network is not null) { await DockerAsync("network", "rm", _network); _network = null; }
            if (_volume is not null) { await DockerAsync("volume", "rm", _volume); _volume = null; }
            if (_initial is not null)
            {
                var after = await CaptureAsync();
                Assert.Equal(_initial.Containers, after.Containers);
                Assert.Equal(_initial.Networks, after.Networks);
                Assert.Equal(_initial.Volumes, after.Volumes);
                Assert.Equal(_initial.Created.OrderBy(pair => pair.Key, StringComparer.Ordinal), after.Created.OrderBy(pair => pair.Key, StringComparer.Ordinal));
                Assert.Equal(_initial.DcpProcesses, after.DcpProcesses);
                Assert.Equal(_initial.Journals, after.Journals);
            }
            // Test inputs, first-attempt output and failure journals remain available; no global cleanup.
        }
    }
}
