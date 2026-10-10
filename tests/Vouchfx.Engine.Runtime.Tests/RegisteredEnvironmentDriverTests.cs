using System.Reflection;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Postgres;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>Normal source registration/authoring/compilation/acquisition/execution, without DCP or Docker.</summary>
public sealed class RegisteredEnvironmentDriverTests
{
    private static readonly Assembly[] s_providers = { typeof(ScriptCsharpProvider).Assembly, typeof(DbAssertPostgresProvider).Assembly };
    private static readonly StepKindRegistry s_kinds = StepKindRegistry.BuildAndFreeze(s_providers);
    private static readonly string[] s_scenarioNames = { "first", "second" };
    private static readonly string[] s_httpEndpoints = { "http" };
    private const string Yaml = """
        environment:
          source:
            driver: sandbox
            config: custom.environment.yaml
        steps:
          - id: verify-staging
            type: script.csharp
            code: |
              if ((string)Vars["conn::db"] != "test-connection") throw new Exception("dependency not staged");
              if ((string)Vars["svc::api"] != "test-endpoint") throw new Exception("service not staged");
        """;

    [Fact]
    public void NormalisedDeclarations_DoNotRetainMutableDriverEndpointLists()
    {
        var endpoints = new List<string> { "http" };
        var declarations = new EnvironmentDeclarations(
            new Dictionary<string, DeclaredServiceInfo> { ["api"] = new(endpoints) },
            new Dictionary<string, string>());
        endpoints[0] = "changed-after-binding";
        Assert.Equal("http", Assert.Single(declarations.Services["api"].EndpointNames));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)declarations.Services["api"].EndpointNames)[0] = "mutated-through-plan");
    }

    [Fact]
    public async Task RegisteredSecondDriver_NormalValidationAndRun_CompileRealProviderAndNeverRequireDcp()
    {
        var driver = new SandboxDriver();
        var drivers = new EnvironmentDriverRegistry(new[] { driver });
        var validation = ScenarioValidator.ValidateScenario(Yaml, "normal.e2e.yaml", s_kinds, environmentDrivers: drivers);
        Assert.True(validation.IsValid, string.Join("; ", validation.Diagnostics.Select(d => d.Message)));
        Assert.Empty(driver.Leases);
        var output = new StringWriter();
        var verdict = await ScenarioRunner.RunAsync(Yaml, "normal", s_providers,
            "missing-host-no-dcp-metadata", output, environmentDrivers: drivers);
        Assert.Equal(Verdict.Pass, verdict);
        var lease = Assert.Single(driver.Leases);
        Assert.Equal(1, lease.DisposeCount);
        Assert.Equal(0, lease.ResetCount);
    }

    [Fact]
    public async Task TwoNormalSuiteScenarios_RecreatePolicy_ProducesDistinctFreshLeases()
    {
        var driver = new SandboxDriver();
        var drivers = new EnvironmentDriverRegistry(new[] { driver });
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(Yaml), s_kinds);
        var result = await ScenarioRunner.RunSuiteAsync(new[] { ast, ast }, s_scenarioNames,
            new[] { Yaml, Yaml }, s_providers, "missing-host", new StringWriter(), environmentDrivers: drivers);
        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.Equal(2, result.ScenarioVerdicts.Count);
        Assert.Equal(2, driver.Leases.Count);
        Assert.Equal(2, driver.Leases.Select(l => l.Identity).Distinct().Count());
        Assert.All(driver.Leases, l => { Assert.Equal(1, l.DisposeCount); Assert.Equal(0, l.ResetCount); });
    }

    [Fact]
    public async Task TwoSuiteDirectories_RelativeConfig_RecreateDecisionUsesEachScenarioDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-relative-driver-").FullName;
        var first = Directory.CreateDirectory(Path.Combine(directory, "one")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(directory, "nested", "two")).FullName;
        File.WriteAllText(Path.Combine(first, "custom.environment.yaml"), "first-content");
        File.WriteAllText(Path.Combine(second, "custom.environment.yaml"), "second-content");
        var driver = new SandboxDriver { RequireConfigFile = true };
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(Yaml), s_kinds);
        var result = await ScenarioRunner.RunSuiteAsync(new[] { ast, ast }, s_scenarioNames,
            new[] { Yaml, Yaml }, s_providers, "missing-host", new StringWriter(), seedBaseDirectory: directory,
            scenarioBaseDirectories: new[] { first, second },
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.Equal(2, driver.Leases.Count);
        Assert.Equal(2, driver.Leases.Select(l => l.Identity).Distinct().Count());
        Assert.All(driver.Leases, l => { Assert.Equal(1, l.DisposeCount); Assert.Equal(0, l.ResetCount); });
    }

    [Fact]
    public async Task ExternalDeclaredSecurity_NormalValidationAndRun_RefuseBeforeStartup()
    {
        var driver = new SandboxDriver { DeclareSecurity = true };
        var drivers = new EnvironmentDriverRegistry(new[] { driver });
        var validation = ScenarioValidator.ValidateScenario(Yaml, "security.e2e.yaml", s_kinds, environmentDrivers: drivers);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Diagnostics, d => d.Message.Contains("external-driver security", StringComparison.Ordinal));
        var verdict = await ScenarioRunner.RunAsync(Yaml, "security", s_providers, "missing-host", new StringWriter(), environmentDrivers: drivers);
        Assert.Equal(Verdict.Inconclusive, verdict);
        Assert.Empty(driver.Leases);
    }

    [Fact]
    public void BorrowedExternalResources_NormalValidation_RefuseBeforeStartup()
    {
        var driver = new SandboxDriver { Borrowed = true };
        var validation = ScenarioValidator.ValidateScenario(Yaml, "borrowed.e2e.yaml", s_kinds,
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Diagnostics, d => d.Message.Contains("borrowed infrastructure", StringComparison.Ordinal));
        Assert.Empty(driver.Leases);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("mysql")]
    public void MissingOrWrongDependency_NormalValidationRefusesBeforeStartup(string type)
    {
        var driver = new SandboxDriver { DependencyType = type };
        var yaml = Yaml[..Yaml.IndexOf("steps:", StringComparison.Ordinal)] + """
            steps:
              - id: database-check
                type: db-assert.postgres
                target: db
                query: SELECT 1
                expect:
                  rowCount: 1
            """;
        var validation = ScenarioValidator.ValidateScenario(yaml, "invalid.e2e.yaml", s_kinds,
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        Assert.False(validation.IsValid);
        Assert.Empty(driver.Leases);
        Assert.Contains(validation.Diagnostics, d => d.Message.Contains("db", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownDriver_NormalValidationRefusesLocatedSource()
    {
        var validation = ScenarioValidator.ValidateScenario(Yaml, "unknown.e2e.yaml", s_kinds);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Diagnostics, d => d.Message.Contains("environment.source.driver", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartupFailure_NormalRun_ReturnsEnvironmentErrorWithoutExecutingStep()
    {
        var driver = new SandboxDriver { FailStart = true };
        var output = new StringWriter();
        var verdict = await ScenarioRunner.RunAsync(Yaml, "start-failure", s_providers, "missing-host", output,
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        Assert.Equal(Verdict.EnvironmentError, verdict);
        Assert.Empty(driver.Leases);
        Assert.DoesNotContain("step-completed", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDuringStartup_NormalRegisteredRun_PropagatesWithoutExecutingStep()
    {
        var driver = new SandboxDriver { WaitForCancellation = true };
        var output = new StringWriter();
        using var stop = new CancellationTokenSource();
        var running = ScenarioRunner.RunAsync(Yaml, "start-cancellation", s_providers, "missing-host", output,
            cancellationToken: stop.Token, environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        await driver.StartupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(driver.CancellationObserved);
        Assert.Empty(driver.Leases);
        Assert.DoesNotContain("step-completed", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(": PASS", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanupTimeout_NormalRun_PreservesPrimaryResultAndReportsFailure()
    {
        var driver = new SandboxDriver { SlowCleanup = true };
        var output = new StringWriter();
        var verdict = await ScenarioRunner.RunAsync(Yaml, "cleanup-timeout", s_providers, "missing-host", output,
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        Assert.Equal(Verdict.Pass, verdict);
        Assert.Contains("cleanup failed or exceeded", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(driver.Leases).DisposeCount);
    }

    [Fact]
    public async Task AssertionFailure_NormalRun_StillDisposesOwnedLease()
    {
        var driver = new SandboxDriver();
        var yaml = Yaml.Replace("test-connection", "wrong-connection", StringComparison.Ordinal);
        var verdict = await ScenarioRunner.RunAsync(yaml, "assertion-failure", s_providers, "missing-host", new StringWriter(),
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        Assert.Equal(Verdict.Fail, verdict);
        Assert.Equal(1, Assert.Single(driver.Leases).DisposeCount);
    }

    [Fact]
    public async Task AcquiredLease_RepeatedDispose_IsIdempotentAndRetainsDependencyIdentity()
    {
        var driver = new SandboxDriver();
        var plan = driver.Bind(new EnvironmentSourceSpec("sandbox", "ignored"), ".");
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(Yaml), s_kinds);
        var lease = await TopologyRequest.ForScenario(ast, "missing-host", null).WithPlan(plan)
            .AcquireAsync(null, null, CancellationToken.None);
        Assert.Contains("db", lease.DependencyNames);
        Assert.False(lease.StatePolicy.CanReset);
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        Assert.Equal(1, Assert.Single(driver.Leases).DisposeCount);
    }

    private sealed class SandboxDriver : IEnvironmentDriver
    {
        public string Id => "sandbox";
        internal List<SandboxLease> Leases { get; } = new();
        internal string DependencyType { get; init; } = "postgres";
        internal bool FailStart { get; init; }
        internal bool WaitForCancellation { get; init; }
        internal TaskCompletionSource<bool> StartupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool CancellationObserved { get; private set; }
        internal bool SlowCleanup { get; init; }
        internal bool RequireConfigFile { get; init; }
        internal bool DeclareSecurity { get; init; }
        internal bool Borrowed { get; init; }
        public EnvironmentPlan Bind(EnvironmentSourceSpec source, string suiteDirectory)
        {
            var identity = "sandbox-v1";
            if (RequireConfigFile)
            {
                var file = Path.GetFullPath(source.Config, suiteDirectory);
                if (!File.Exists(file)) throw new ArgumentException("sandbox config is missing relative to the suite");
                identity = File.ReadAllText(file);
            }
            var dependencies = new Dictionary<string, string>();
            if (DependencyType != "missing") dependencies["db"] = DependencyType;
            return new EnvironmentPlan(new EnvironmentDeclarations(
                new Dictionary<string, DeclaredServiceInfo> { ["api"] = new(s_httpEndpoints) }, dependencies),
                identity, Borrowed ? new EnvironmentStatePolicy(EnvironmentOwnership.Borrowed, EnvironmentReusePolicy.Recreate, false) : EnvironmentStatePolicy.FreshOwned,
                async (_, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (FailStart) throw new OrchestrationException(new OrchestrationErrorInfo(
                        OrchestrationErrorKind.Provision, "sandbox", null, null, "test startup failure"));
                    if (WaitForCancellation)
                    {
                        StartupEntered.TrySetResult(true);
                        try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                        catch (OperationCanceledException) { CancellationObserved = true; throw; }
                    }
                    var lease = new SandboxLease(SlowCleanup);
                    Leases.Add(lease);
                    return lease;
                }, cleanupTimeout: TimeSpan.FromMilliseconds(50),
                declaredSecurity: DeclareSecurity ? new[] { new SecuredTarget("api", "service", new SecuritySpec("tls", "http", null, null, null, null)) } : null);
        }
    }

    private sealed class SandboxLease : IKeptTopology
    {
        private readonly bool _slowCleanup;
        internal SandboxLease(bool slowCleanup) => _slowCleanup = slowCleanup;
        internal Guid Identity { get; } = Guid.NewGuid();
        internal int DisposeCount { get; private set; }
        internal int ResetCount { get; private set; }
        public IReadOnlyList<SecurityConfirmation> SecurityConfirmations => Array.Empty<SecurityConfirmation>();
        public IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices => Array.Empty<EndpointSelectionNotice>();
        public IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices => Array.Empty<EndpointTrustNotice>();
        public IReadOnlyDictionary<string, object> DiscoveredServices { get; } = new Dictionary<string, object>
        {
            ["api"] = "test-endpoint",
            ["db"] = "test-connection",
        };
        public IReadOnlyList<string> DependencyNames { get; } = new[] { "db" };
        public IReadOnlyDictionary<string, string> DependencyTypes { get; } = new Dictionary<string, string> { ["db"] = "postgres" };
        public Task ReseedAsync(CancellationToken cancellationToken) { ResetCount++; return Task.CompletedTask; }
        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (_slowCleanup) await Task.Delay(500);
        }
    }
}
