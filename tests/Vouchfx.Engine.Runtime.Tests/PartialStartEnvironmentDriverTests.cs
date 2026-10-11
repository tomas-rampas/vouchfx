using System.Reflection;
using System.Security.Cryptography;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>A registered non-Compose driver owns a real partial resource before returning a lease.</summary>
public sealed class PartialStartEnvironmentDriverTests
{
    private static readonly Assembly[] s_providers = { typeof(ScriptCsharpProvider).Assembly };
    private static readonly StepKindRegistry s_kinds = StepKindRegistry.BuildAndFreeze(s_providers);
    private const string Yaml = """
        environment:
          source:
            driver: controlled
            config: controlled.config
        steps:
          - id: verify-staging
            type: script.csharp
            code: |
              if ((string)Vars["conn::db"] != "test-connection") throw new Exception("dependency mismatch");
              if ((string)Vars["svc::broker"] != "127.0.0.1:19092") throw new Exception("service mismatch");
        """;

    [Fact]
    public async Task Bind_IsPure_AndSnapshotsDeclarationsBeforeRuntimeSecretResolution()
    {
        using var fixture = new Fixture();
        using var secrets = new CountingSecrets();
        var plan = fixture.Driver.Bind(new EnvironmentSourceSpec("controlled", "controlled.config"), fixture.Directory);
        fixture.Driver.Endpoints[0] = "mutated";
        Assert.Equal("tcp", Assert.Single(plan.Declarations.Services["broker"].EndpointNames));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)plan.InputFiles)[0] = "mutated");
        Assert.Equal(0, secrets.ResolveCount);
        Assert.Equal(0, fixture.Driver.StartCount);
        Assert.Empty(fixture.Driver.Resources);
        fixture.Driver.Ready.SetResult(true);
        var lease = await plan.StartAsync(Request(), null, null, CancellationToken.None, secrets: secrets);
        Assert.Equal(1, secrets.ResolveCount);
        Assert.Equal("127.0.0.1:19092", lease.DiscoveredServices["broker"]);
        await lease.DisposeAsync();
        fixture.AssertNoResources();
    }

    [Fact]
    public async Task Readiness_HoldsPartialResource_AndPreventsAssertionsUntilReleased()
    {
        using var fixture = new Fixture();
        var output = new StringWriter();
        var validation = ScenarioValidator.ValidateScenario(Yaml, "controlled.e2e.yaml", s_kinds,
            seedBaseDirectory: fixture.Directory, environmentDrivers: fixture.Registry);
        Assert.True(validation.IsValid, string.Join("; ", validation.Diagnostics.Select(d => d.Message)));
        Assert.Empty(fixture.Driver.Resources);
        var running = fixture.RunAsync(Yaml, output);
        await fixture.Driver.PartialCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(Assert.Single(fixture.Driver.Resources)));
        Assert.False(running.IsCompleted);
        Assert.DoesNotContain(": PASS", output.ToString(), StringComparison.Ordinal);
        fixture.Driver.Ready.SetResult(true);
        Assert.Equal(Verdict.Pass, await running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, Assert.Single(fixture.Driver.Leases).DisposeCount);
        fixture.AssertNoResources();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialStart_RefusalOrCallerCancellation_RemovesResourceBeforeReturning(bool cancel)
    {
        using var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        var output = new StringWriter();
        var running = fixture.RunAsync(Yaml, output, stop.Token);
        await fixture.Driver.PartialCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(Assert.Single(fixture.Driver.Resources)));
        if (cancel)
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        else
        {
            fixture.Driver.Ready.SetException(new OrchestrationException(new OrchestrationErrorInfo(
                OrchestrationErrorKind.Provision, "controlled", null, null, "test readiness refusal")));
            Assert.Equal(Verdict.EnvironmentError, await running.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        fixture.AssertNoResources();
        Assert.Empty(fixture.Driver.Leases);
        Assert.Equal(1, fixture.Driver.PartialCleanupCount);
        Assert.False(fixture.Driver.CleanupTokenWasCancelled);
        Assert.DoesNotContain(": PASS", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleSourceIdentity_RefusesBeforeCreatingPartialResourceOrResolvingSecrets()
    {
        using var fixture = new Fixture();
        var plan = fixture.Driver.Bind(new EnvironmentSourceSpec("controlled", "controlled.config"), fixture.Directory);
        using var secrets = new CountingSecrets();
        File.WriteAllText(fixture.Config, "changed configuration");
        await Assert.ThrowsAsync<TopologyAuthoringException>(() =>
            plan.StartAsync(Request(), null, null, CancellationToken.None, secrets: secrets));
        Assert.Equal(0, secrets.ResolveCount);
        Assert.Equal(0, fixture.Driver.StartCount);
        Assert.Empty(fixture.Driver.Resources);
    }

    [Fact]
    public async Task NormalRuns_CreateDistinctLeases_AndStageMatchingDeclaredBindings()
    {
        using var fixture = new Fixture();
        fixture.Driver.Ready.SetResult(true);
        for (var run = 0; run < 2; run++)
        {
            Assert.Equal(Verdict.Pass, await fixture.RunAsync(Yaml, new StringWriter()).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        Assert.Equal(2, fixture.Driver.Leases.Count);
        Assert.Equal(2, fixture.Driver.Leases.Select(l => l.Resource).Distinct().Count());
        Assert.All(fixture.Driver.Leases, l => Assert.Equal(1, l.DisposeCount));
        fixture.AssertNoResources();
    }

    [Fact]
    public async Task AcquiredLease_DisposesExactlyOnce_EvenWhenCalledRepeatedly()
    {
        using var fixture = new Fixture();
        fixture.Driver.Ready.SetResult(true);
        var plan = fixture.Driver.Bind(new EnvironmentSourceSpec("controlled", "controlled.config"), fixture.Directory);
        var lease = await plan.StartAsync(Request(), null, null, CancellationToken.None);
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.Equal(1, Assert.Single(fixture.Driver.Leases).DisposeCount);
        fixture.AssertNoResources();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposalFailure_PreservesPrimaryAssertionVerdict(bool failAssertion)
    {
        using var fixture = new Fixture();
        fixture.Driver.FailDispose = true;
        fixture.Driver.Ready.SetResult(true);
        var output = new StringWriter();
        var yaml = failAssertion ? Yaml.Replace("test-connection", "wrong-connection", StringComparison.Ordinal) : Yaml;
        Assert.Equal(failAssertion ? Verdict.Fail : Verdict.Pass,
            await fixture.RunAsync(yaml, output).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("cleanup failed or exceeded", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(fixture.Driver.Leases).DisposeCount);
        fixture.AssertNoResources();
    }

    private static TopologyRequest Request()
        => TopologyRequest.ForScenario(AstBuilder.Build(YamlDocumentParser.Parse(Yaml), s_kinds), "missing-host", null);

    private sealed class Fixture : IDisposable
    {
        internal Fixture()
        {
            Directory = System.IO.Directory.CreateTempSubdirectory("vouchfx-controlled-driver-").FullName;
            Config = Path.Combine(Directory, "controlled.config");
            File.WriteAllText(Config, "configuration reference: ${secret:env/CONTROLLED_FAKE_SECRET}");
            Driver = new ControlledDriver(Directory);
            Registry = new EnvironmentDriverRegistry(new[] { Driver });
        }
        internal string Directory { get; }
        internal string Config { get; }
        internal ControlledDriver Driver { get; }
        internal EnvironmentDriverRegistry Registry { get; }
        internal Task<Verdict> RunAsync(string yaml, TextWriter output, CancellationToken token = default)
            => ScenarioRunner.RunAsync(yaml, "controlled", s_providers, "missing-host", output,
                seedBaseDirectory: Directory, cancellationToken: token, environmentDrivers: Registry);
        internal void AssertNoResources() => Assert.All(Driver.Resources, p => Assert.False(File.Exists(p)));
        public void Dispose()
        {
            foreach (var resource in Driver.Resources) File.Delete(resource);
            File.Delete(Config);
            System.IO.Directory.Delete(Directory);
        }
    }

    private sealed class ControlledDriver : IEnvironmentDriver
    {
        private readonly string _directory;
        internal ControlledDriver(string directory) => _directory = directory;
        public string Id => "controlled";
        internal List<string> Endpoints { get; } = new() { "tcp" };
        internal List<string> Resources { get; } = new();
        internal List<ControlledLease> Leases { get; } = new();
        internal TaskCompletionSource<bool> PartialCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int StartCount { get; private set; }
        internal int PartialCleanupCount { get; private set; }
        internal bool CleanupTokenWasCancelled { get; private set; }
        internal bool FailDispose { get; set; }

        public EnvironmentPlan Bind(EnvironmentSourceSpec source, string suiteDirectory)
        {
            var file = Path.GetFullPath(source.Config, suiteDirectory);
            var identity = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            var declarations = new EnvironmentDeclarations(
                new Dictionary<string, DeclaredServiceInfo> { ["broker"] = new(Endpoints) },
                new Dictionary<string, string> { ["db"] = "postgres" });
            return new EnvironmentPlan(declarations, identity, EnvironmentStatePolicy.FreshOwned,
                async (context, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) != identity)
                        throw new TopologyAuthoringException("Controlled driver inputs changed before acquisition.");
                    StartCount++;
                    // NullSecretAccessor is used by normal test runs; a dedicated acquisition supplies the counting accessor.
                    if (context.Secrets is CountingSecrets) _ = context.Secrets.Resolve("${secret:env/CONTROLLED_FAKE_SECRET}");
                    var resource = Path.Combine(_directory, "partial-" + Guid.NewGuid().ToString("N"));
                    File.WriteAllText(resource, "owned test-only partial resource");
                    Resources.Add(resource);
                    PartialCreated.TrySetResult(true);
                    try
                    {
                        await Ready.Task.WaitAsync(ct);
                        ct.ThrowIfCancellationRequested();
                        var lease = new ControlledLease(resource, FailDispose);
                        Leases.Add(lease);
                        return lease;
                    }
                    catch
                    {
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await Task.Yield();
                        CleanupTokenWasCancelled = cleanup.Token.IsCancellationRequested;
                        cleanup.Token.ThrowIfCancellationRequested();
                        File.Delete(resource);
                        PartialCleanupCount++;
                        throw;
                    }
                }, inputFiles: new[] { file });
        }
    }

    private sealed class ControlledLease : IKeptTopology
    {
        private readonly bool _failDispose;
        internal ControlledLease(string resource, bool failDispose)
        {
            Resource = resource;
            _failDispose = failDispose;
        }
        internal string Resource { get; }
        internal int DisposeCount { get; private set; }
        public IReadOnlyList<SecurityConfirmation> SecurityConfirmations => Array.Empty<SecurityConfirmation>();
        public IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices => Array.Empty<EndpointSelectionNotice>();
        public IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices => Array.Empty<EndpointTrustNotice>();
        public IReadOnlyDictionary<string, object> DiscoveredServices { get; } = new Dictionary<string, object>
        {
            ["broker"] = "127.0.0.1:19092",
            ["db"] = "test-connection",
        };
        public IReadOnlyList<string> DependencyNames { get; } = new[] { "db" };
        public IReadOnlyDictionary<string, string> DependencyTypes { get; } = new Dictionary<string, string> { ["db"] = "postgres" };
        public Task ReseedAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Fresh driver cannot reset.");
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            File.Delete(Resource);
            if (_failDispose) throw new InvalidOperationException("test disposal failure after resource removal");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingSecrets : ISecretAccessor, IDisposable
    {
        private readonly string _variable = "VOUCHFX_CONTROLLED_FAKE_" + Guid.NewGuid().ToString("N");
        internal CountingSecrets() => Environment.SetEnvironmentVariable(_variable, "test-only-value");
        internal int ResolveCount { get; private set; }
        public SecretString Resolve(string reference)
        {
            ResolveCount++;
            return new EnvironmentSecretResolver().Resolve(_variable);
        }
        public void Dispose() => Environment.SetEnvironmentVariable(_variable, null);
    }
}
