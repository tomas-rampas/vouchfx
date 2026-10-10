using Vouchfx.Cli;
using Vouchfx.Cli.Watch;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ExternalEnvironmentWatchTests
{
    private static readonly StepKindRegistry s_kinds = StepKindRegistry.BuildAndFreeze(new[] { typeof(ScriptCsharpProvider).Assembly });
    private static readonly string[] s_endpoints = { "http" };
    private const string Yaml = """
        environment:
          source:
            driver: sandbox
            config: ./environment.yaml
        steps:
          - id: actual-script
            type: script.csharp
            code: |
              if ((string)Vars["svc::api"] != "sandbox-endpoint") throw new Exception("endpoint missing");
              if ((string)Vars["conn::db"] != "sandbox-connection") throw new Exception("connection missing");
        """;

    [Fact]
    public async Task NormalRegisteredDriver_TwoWatchRuns_ExecuteRealScriptWithFreshLeasesAndNoReset()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-driver-watch-").FullName;
        var config = Path.Combine(directory, "environment.yaml");
        File.WriteAllText(config, "first-content");
        var path = Path.Combine(directory, "watch.e2e.yaml");
        var driver = new SandboxDriver(config);
        var drivers = new EnvironmentDriverRegistry(new[] { driver });
        var output = new StringWriter();
        await using var session = WatchRunner.CreateSession(path, s_kinds, output, "missing-no-dcp-host",
            new ResolvedSecretLedger(), new SecurityPathDisclosureLedger(), environmentDrivers: drivers);
        await session.OnChangeAsync(Yaml, CancellationToken.None);
        File.WriteAllText(config, "second-content");
        await session.OnChangeAsync(Yaml, CancellationToken.None);
        Assert.Equal(2, driver.Leases.Count);
        Assert.Equal(2, driver.Leases.Select(l => l.Identity).Distinct().Count());
        Assert.All(driver.Leases, l => { Assert.Equal(1, l.DisposeCount); Assert.Equal(0, l.ResetCount); });
        Assert.Contains(config, session.InputFiles);
        Assert.DoesNotContain("FAIL", output.ToString(), StringComparison.Ordinal);
        // Each run renders its executed step and its scenario summary as PASS.
        Assert.Equal(4, output.ToString().Split("PASS", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void BoundInputContentAndStatePolicy_ChangeGenericWatchFingerprint()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-driver-identity-").FullName;
        var config = Path.Combine(directory, "environment.yaml");
        File.WriteAllText(config, "first-content");
        var driver = new SandboxDriver(config);
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(Yaml), s_kinds);
        var drivers = new EnvironmentDriverRegistry(new[] { driver });
        var first = WatchIterationPlan.Create(ast, Yaml, "watch", s_kinds, "missing-host", directory, drivers);
        File.WriteAllText(config, "second-content");
        var second = WatchIterationPlan.Create(ast, Yaml, "watch", s_kinds, "missing-host", directory, drivers);
        Assert.False(first.IsRefused);
        Assert.False(second.IsRefused);
        Assert.NotEqual(first.TopologyFingerprint, second.TopologyFingerprint);
        var reusable = new SandboxDriver(config) { Policy = EnvironmentStatePolicy.Inline };
        var third = WatchIterationPlan.Create(ast, Yaml, "watch", s_kinds, "missing-host", directory,
            new EnvironmentDriverRegistry(new[] { reusable }));
        Assert.False(third.IsRefused);
        Assert.NotEqual(second.TopologyFingerprint, third.TopologyFingerprint);
        Assert.Empty(driver.Leases);
    }

    [Fact]
    public async Task FileObservation_OutsideSuiteDirectory_EditSignalsWithoutScenarioEdit()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-driver-observation-").FullName;
        var config = Path.Combine(directory, "manifest.yaml");
        File.WriteAllText(config, "initial");
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new WatchInputFiles(() => changed.TrySetResult());
        watcher.Refresh(new[] { config });
        File.WriteAllText(config, "modified-content");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RefusedSourceConfig_IsStillObserved_NoAcquisitionOccurs()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-driver-refusal-watch-").FullName;
        var config = Path.Combine(directory, "environment.yaml");
        var driver = new SandboxDriver(config);
        await using var session = WatchRunner.CreateSession(Path.Combine(directory, "watch.e2e.yaml"), s_kinds,
            new StringWriter(), "missing-host", new ResolvedSecretLedger(), new SecurityPathDisclosureLedger(),
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        await session.OnChangeAsync(Yaml, CancellationToken.None);
        Assert.Empty(driver.Leases);
        Assert.Contains(config, session.InputFiles);
    }

    [Fact]
    public void ComposeSchemaCommand_ExportsAdapterSchema_UnknownDriverRefuses()
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        Assert.Equal(0, SchemaCommand.Execute(null, output, errors, "compose"));
        Assert.Equal(ComposeEnvironmentDriver.ExportConfigurationSchema().Trim(), output.ToString().Trim());
        Assert.Equal(string.Empty, errors.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(2, SchemaCommand.Execute(null, output, errors, "unknown"));
        Assert.Equal(string.Empty, output.ToString());
    }

    private sealed class SandboxDriver : IEnvironmentDriver
    {
        private readonly string _config;
        internal SandboxDriver(string config) => _config = config;
        public string Id => "sandbox";
        internal EnvironmentStatePolicy Policy { get; init; } = EnvironmentStatePolicy.FreshOwned;
        internal List<Lease> Leases { get; } = new();
        public EnvironmentPlan Bind(EnvironmentSourceSpec source, string suiteDirectory)
        {
            if (!File.Exists(_config)) throw new ArgumentException("sandbox configuration is missing");
            return new EnvironmentPlan(new EnvironmentDeclarations(
                new Dictionary<string, DeclaredServiceInfo> { ["api"] = new(s_endpoints) },
                new Dictionary<string, string> { ["db"] = "postgres" }), File.ReadAllText(_config), Policy,
                (_, _) =>
                {
                    var lease = new Lease();
                    Leases.Add(lease);
                    return Task.FromResult<IKeptTopology>(lease);
                }, inputFiles: new[] { _config });
        }
    }

    private sealed class Lease : IKeptTopology
    {
        internal Guid Identity { get; } = Guid.NewGuid();
        internal int DisposeCount { get; private set; }
        internal int ResetCount { get; private set; }
        public IReadOnlyList<SecurityConfirmation> SecurityConfirmations => Array.Empty<SecurityConfirmation>();
        public IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices => Array.Empty<EndpointSelectionNotice>();
        public IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices => Array.Empty<EndpointTrustNotice>();
        public IReadOnlyDictionary<string, object> DiscoveredServices { get; } = new Dictionary<string, object>
        {
            ["api"] = "sandbox-endpoint",
            ["db"] = "sandbox-connection",
        };
        public IReadOnlyList<string> DependencyNames { get; } = new[] { "db" };
        public IReadOnlyDictionary<string, string> DependencyTypes { get; } = new Dictionary<string, string> { ["db"] = "postgres" };
        public Task ReseedAsync(CancellationToken cancellationToken) { ResetCount++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }
}
