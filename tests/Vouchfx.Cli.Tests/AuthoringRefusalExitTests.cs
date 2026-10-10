using System.Collections.Concurrent;
using System.Text.Json;
using Vouchfx.Cli.Selection;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class AuthoringRefusalExitTests
{
    public static TheoryData<string, string, string> RefusalCases
    {
        get
        {
            var cases = new TheoryData<string, string, string>();
            foreach (var mode in new[] { "shared", "fresh", "parallel-1", "parallel-2" })
                foreach (var fault in new[] { "schema", "missing-script", "source-binding", "ast-discovery" })
                    foreach (var sibling in new[] { "alone", "pass", "fail" }) cases.Add(mode, fault, sibling);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(RefusalCases))]
    public async Task AuthoringRefusal_AloneOrBesidePassOrFail_NormalCliPreservesExitAndAcquisition(
        string mode, string fault, string sibling)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-refusal-matrix-").FullName;
        var bad = Directory.CreateDirectory(Path.Combine(directory, "bad")).FullName;
        var good = Directory.CreateDirectory(Path.Combine(directory, "good")).FullName;
        await File.WriteAllTextAsync(Path.Combine(good, "environment.yaml"), "owned-test-config");
        if (fault != "source-binding")
            await File.WriteAllTextAsync(Path.Combine(bad, "environment.yaml"), "owned-test-config");
        var yaml = fault switch
        {
            "schema" => Script("return;") + "    unknownAuthoredField: true\n",
            "missing-script" => Source + "steps:\n  - id: refused-step\n    type: script.csharp\n    file: ./missing.csx\n",
            "ast-discovery" => Script("return;") + "  - id: executed-step\n    type: script.csharp\n    code: 'return;'\n",
            _ => Script("return;"),
        };
        await File.WriteAllTextAsync(Path.Combine(bad, "refused.e2e.yaml"), yaml);
        if (sibling != "alone")
            await File.WriteAllTextAsync(Path.Combine(good, "sibling.e2e.yaml"),
                Script(sibling == "fail" ? "throw new Exception(\"expected assertion failure\");" : "return;"));
        var driver = Driver(mode);
        var output = new StringWriter();
        var events = Path.Combine(directory, "events.jsonl");
        var exit = await ExecuteAsync(directory, driver, output, events, Parallel(mode));
        Assert.Equal(sibling == "fail" ? 1 : 4, exit);
        Assert.Equal(sibling == "alone" ? 0 : 1, driver.StartCount);
        Assert.All(driver.StartedConfigurations, config => Assert.StartsWith(good, config, StringComparison.Ordinal));
        Assert.All(driver.Leases, lease => { Assert.Equal(1, lease.DisposeCount); Assert.Equal(0, lease.ResetCount); });
        if (sibling != "alone") Assert.Contains(sibling == "fail" ? "FAIL" : "PASS", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(RunCommand.ProviderOrEngineFaultNotice, output.ToString(), StringComparison.Ordinal);
        if (File.Exists(events))
        {
            using var lines = JsonDocument.Parse("[" + string.Join(",", await File.ReadAllLinesAsync(events)) + "]");
            Assert.DoesNotContain(lines.RootElement.EnumerateArray(), line => line.GetProperty("type").GetString() == "step-started"
                && line.GetProperty("stepId").GetString() == "refused-step");
        }
    }

    [Theory]
    [InlineData("shared", 1)]
    [InlineData("fresh", 2)]
    [InlineData("parallel-1", 2)]
    [InlineData("parallel-2", 2)]
    public async Task TwoPassingScenarios_ControlProvesSharedVersusFreshAcquisition(string mode, int starts)
    {
        var directory = await DirectoryWithConfigAsync();
        await File.WriteAllTextAsync(Path.Combine(directory, "first.e2e.yaml"), Script("return;"));
        await File.WriteAllTextAsync(Path.Combine(directory, "second.e2e.yaml"), Script("return;"));
        var driver = Driver(mode);
        Assert.Equal(0, await ExecuteAsync(directory, driver, new StringWriter(), Path.Combine(directory, "events.jsonl"), Parallel(mode)));
        Assert.Equal(starts, driver.StartCount);
        Assert.Equal(starts, driver.Leases.Count);
        Assert.All(driver.Leases, lease => { Assert.Equal(1, lease.DisposeCount); Assert.Equal(0, lease.ResetCount); });
    }

    [Theory]
    [InlineData("shared", "alone")]
    [InlineData("shared", "pass")]
    [InlineData("shared", "fail")]
    [InlineData("fresh", "alone")]
    [InlineData("fresh", "pass")]
    [InlineData("fresh", "fail")]
    [InlineData("parallel-1", "alone")]
    [InlineData("parallel-1", "pass")]
    [InlineData("parallel-1", "fail")]
    [InlineData("parallel-2", "alone")]
    [InlineData("parallel-2", "pass")]
    [InlineData("parallel-2", "fail")]
    public async Task LateInvalidCsx_AloneOrBesidePassOrFail_PreservesCompletionAndCleanup(string mode, string sibling)
    {
        var directory = await DirectoryWithConfigAsync();
        await File.WriteAllTextAsync(Path.Combine(directory, "invalid.e2e.yaml"), Script("this is not valid C# !!!"));
        if (sibling != "alone")
            await File.WriteAllTextAsync(Path.Combine(directory, "sibling.e2e.yaml"),
                Script(sibling == "fail" ? "throw new Exception(\"expected assertion failure\");" : "return;"));
        var driver = Driver(mode);
        var output = new StringWriter();
        var events = Path.Combine(directory, "events.jsonl");
        Assert.Equal(sibling == "fail" ? 1 : 4, await ExecuteAsync(directory, driver, output, events, Parallel(mode)));
        Assert.Contains("CSX compilation failed", output.ToString(), StringComparison.Ordinal);
        if (sibling != "alone") Assert.Contains(sibling == "fail" ? "FAIL" : "PASS", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(mode == "shared" || sibling == "alone" ? 1 : 2, driver.StartCount);
        Assert.All(driver.Leases, lease => { Assert.Equal(1, lease.DisposeCount); Assert.Equal(0, lease.ResetCount); });
        Assert.Contains("scenario-completed", await File.ReadAllTextAsync(events), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("shared", false)]
    [InlineData("shared", true)]
    [InlineData("fresh", false)]
    [InlineData("fresh", true)]
    [InlineData("parallel-1", false)]
    [InlineData("parallel-1", true)]
    [InlineData("parallel-2", false)]
    [InlineData("parallel-2", true)]
    public async Task RuntimeTimeoutBesidePass_RemainsOptIn(string mode, bool strict)
    {
        var directory = await DirectoryWithConfigAsync();
        await File.WriteAllTextAsync(Path.Combine(directory, "timeout.e2e.yaml"),
            Script("await Task.Delay(System.Threading.Timeout.Infinite, __stepCt_executed_step);") + "    timeout: 20ms\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "pass.e2e.yaml"), Script("return;"));
        var driver = Driver(mode);
        var output = new StringWriter();
        var exit = await ExecuteAsync(directory, driver, output,
            Path.Combine(directory, "events.jsonl"), Parallel(mode), failOnInconclusive: strict);
        Assert.True(exit == (strict ? 4 : 0), $"Unexpected exit {exit}: {output}");
        Assert.Contains("INCONCLUSIVE", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("PASS", output.ToString(), StringComparison.Ordinal);
        Assert.All(driver.Leases, lease => Assert.Equal(1, lease.DisposeCount));
    }

    [Theory]
    [InlineData("shared", false, false, 0)]
    [InlineData("shared", true, false, 3)]
    [InlineData("shared", false, true, 4)]
    [InlineData("shared", true, true, 3)]
    [InlineData("parallel-2", false, false, 0)]
    [InlineData("parallel-2", true, false, 3)]
    [InlineData("parallel-2", false, true, 4)]
    [InlineData("parallel-2", true, true, 3)]
    public async Task InfrastructureDominance_PreservesKnownAuthoringEvidenceAndExistingGate(
        string mode, bool strictEnvironment, bool schemaRefusedSibling, int expectedExit)
    {
        var directory = await DirectoryWithConfigAsync();
        await File.WriteAllTextAsync(Path.Combine(directory, "valid.e2e.yaml"), Script("return;"));
        if (schemaRefusedSibling)
            await File.WriteAllTextAsync(Path.Combine(directory, "invalid.e2e.yaml"), Script("return;") + "    unknownAuthoredField: true\n");
        var driver = new TestDriver { FailStart = true };
        var output = new StringWriter();
        Assert.Equal(expectedExit, await ExecuteAsync(directory, driver, output,
            Path.Combine(directory, "events.jsonl"), Parallel(mode), failOnEnvironmentError: strictEnvironment));
        Assert.Equal(1, driver.StartCount);
        Assert.Empty(driver.Leases);
        Assert.Contains("ENV_ERROR", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeScriptCompilationException_IdenticalTypeIsNotAnAuthoringRefusal()
    {
        var directory = await DirectoryWithConfigAsync();
        // Deliberately close the author local function: this valid submission throws from the
        // execution call, outside the provider's assertion catch, not from CompileOnce.
        var code = """
            }
            var diagnosticType = Type.GetType("Microsoft.CodeAnalysis.Diagnostic, Microsoft.CodeAnalysis", throwOnError: true)!;
            var exceptionType = Type.GetType("Vouchfx.Engine.Compilation.ScriptCompilationException, Vouchfx.Engine.Compilation", throwOnError: true)!;
            throw (Exception)Activator.CreateInstance(exceptionType, new object[] { Array.CreateInstance(diagnosticType, 0) })!;
            async Task __remaining_author_body() {
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, "runtime.e2e.yaml"), Script(code));
        await File.WriteAllTextAsync(Path.Combine(directory, "pass.e2e.yaml"), Script("return;"));
        var driver = new TestDriver();
        var output = new StringWriter();
        Assert.Equal(0, await ExecuteAsync(directory, driver, output, Path.Combine(directory, "events.jsonl")));
        Assert.Contains("CSX compilation failed: Script compilation failed:", output.ToString(), StringComparison.Ordinal);
        Assert.All(driver.Leases, lease => Assert.Equal(1, lease.DisposeCount));
    }

    [Fact]
    public async Task LateCompileRefusal_ResultCarriesProvenanceWithoutPreacquisitionSecurityAbort()
    {
        var directory = await DirectoryWithConfigAsync();
        var yaml = Script("this is not valid C# !!!");
        var driver = new TestDriver();
        var registry = ProviderRegistryFactory.BuildCoreRegistry();
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry);
        var result = await ScenarioRunner.RunSuiteAsync(new[] { ast }, new[] { "late-compile" }, new[] { yaml },
            ProviderRegistryFactory.CoreProviderAssemblies(), "missing-no-dcp-host", new StringWriter(), seedBaseDirectory: directory,
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        Assert.Equal(Verdict.Inconclusive, result.Verdict);
        Assert.True(result.AuthoringRefusalObserved);
        Assert.Null(result.Assurance.Refusal);
        Assert.Equal(1, driver.StartCount);
        Assert.Equal(1, Assert.Single(driver.Leases).DisposeCount);
    }

    [Fact]
    public async Task GenuineCallerCancellation_SharedPipelineStillPropagatesWithoutAuthoringClassification()
    {
        var directory = await DirectoryWithConfigAsync();
        await File.WriteAllTextAsync(Path.Combine(directory, "cancelled.e2e.yaml"), Script("return;"));
        using var cancellation = new CancellationTokenSource();
        var driver = new TestDriver { BeforeStart = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(directory, driver, new StringWriter(),
            Path.Combine(directory, "events.jsonl"), cancellationToken: cancellation.Token));
        Assert.Equal(1, driver.StartCount);
        Assert.Empty(driver.Leases);
    }

    [Theory]
    [InlineData("fresh", false)]
    [InlineData("fresh", true)]
    [InlineData("parallel-1", false)]
    [InlineData("parallel-1", true)]
    [InlineData("parallel-2", false)]
    [InlineData("parallel-2", true)]
    public async Task GenericAcquisitionArgumentException_BesidePassRemainsOptIn(string mode, bool strict)
    {
        var directory = await DirectoryWithConfigAsync();
        var bad = Directory.CreateDirectory(Path.Combine(directory, "bad")).FullName;
        await File.WriteAllTextAsync(Path.Combine(bad, "environment.yaml"), "owned-test-config");
        await File.WriteAllTextAsync(Path.Combine(bad, "fault.e2e.yaml"), Script("return;"));
        await File.WriteAllTextAsync(Path.Combine(directory, "pass.e2e.yaml"), Script("return;"));
        var driver = new TestDriver
        {
            Policy = Driver(mode).Policy,
            AcquisitionFailure = config => config.StartsWith(bad, StringComparison.Ordinal)
                ? new ArgumentException("synthetic acquisition failure") : null,
        };
        var output = new StringWriter();
        Assert.Equal(strict ? 4 : 0, await ExecuteAsync(directory, driver, output,
            Path.Combine(directory, "events.jsonl"), Parallel(mode), failOnInconclusive: strict));
        Assert.Equal(2, driver.StartCount);
        Assert.Equal(1, Assert.Single(driver.Leases).DisposeCount);
        Assert.Contains("synthetic acquisition failure", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("PASS", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericAcquisitionArgumentException_DoesNotClaimPreacquisitionAuthoringRefusal()
    {
        var directory = await DirectoryWithConfigAsync();
        var yaml = Script("return;");
        var registry = ProviderRegistryFactory.BuildCoreRegistry();
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry);
        var driver = new TestDriver { AcquisitionFailure = _ => new ArgumentException("synthetic acquisition failure") };
        var result = await ScenarioRunner.RunSuiteAsync(new[] { ast }, new[] { "acquisition" }, new[] { yaml },
            ProviderRegistryFactory.CoreProviderAssemblies(), "missing-no-dcp-host", new StringWriter(), seedBaseDirectory: directory,
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        Assert.Equal(Verdict.Inconclusive, result.Verdict);
        Assert.False(result.AuthoringRefusalObserved);
        Assert.Null(result.Assurance.Refusal);
        Assert.Equal(1, driver.StartCount);
        Assert.Empty(driver.Leases);
    }

    [Fact]
    public async Task EagerMapperAuthoringFault_IsTypedBeforeHostAcquisition()
    {
        var environment = new EnvironmentSpec(new Dictionary<string, ServiceSpec>
        {
            ["invalid"] = new("image", "project.csproj", null, null, null),
        }, null, null, null, null);
        var refusal = await Assert.ThrowsAsync<TopologyAuthoringException>(
            () => SuiteTopology.StartAsync(environment, "missing-no-dcp-host"));
        Assert.IsType<ArgumentException>(refusal.InnerException);
        Assert.Contains("both 'image' and 'project'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeDiagnostic_ResolvedSyntheticSecret_RemainsScrubbedInTerminalAndEvents()
    {
        var directory = await DirectoryWithConfigAsync();
        var key = "VOUCHFX_REFUSAL_TEST_" + Guid.NewGuid().ToString("N");
        var canary = "synthetic-canary-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(key, canary);
        try
        {
            var code = "}\nthrow new Exception(\"runtime-control \" + Secrets.Resolve(\"env/" + key + "\").Reveal());\nasync Task __remaining_author_body() {";
            await File.WriteAllTextAsync(Path.Combine(directory, "runtime.e2e.yaml"), Script(code));
            var driver = new TestDriver();
            var output = new StringWriter();
            var events = Path.Combine(directory, "events.jsonl");
            Assert.Equal(0, await ExecuteAsync(directory, driver, output, events));
            Assert.Contains("runtime-control", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(canary, output.ToString() + await File.ReadAllTextAsync(events), StringComparison.Ordinal);
            Assert.Equal(1, Assert.Single(driver.Leases).DisposeCount);
        }
        finally { Environment.SetEnvironmentVariable(key, null); }
    }

    private static TestDriver Driver(string mode) => new() { Policy = mode == "fresh" ? EnvironmentStatePolicy.FreshOwned : EnvironmentStatePolicy.Inline };
    private static int? Parallel(string mode) => mode switch { "parallel-1" => 1, "parallel-2" => 2, _ => null };
    private static async Task<string> DirectoryWithConfigAsync()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-authoring-control-").FullName;
        await File.WriteAllTextAsync(Path.Combine(directory, "environment.yaml"), "owned-test-config");
        return directory;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MissingScriptBesidePass_NormalCliPipeline_NeverExitsSuccess(int? parallel)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-authoring-refusal-").FullName;
        await File.WriteAllTextAsync(Path.Combine(directory, "environment.yaml"), "owned-test-config");
        await File.WriteAllTextAsync(Path.Combine(directory, "refused.e2e.yaml"), Source + "steps:\n  - id: refused-step\n    type: script.csharp\n    file: ./missing.csx\n");
        await File.WriteAllTextAsync(Path.Combine(directory, "pass.e2e.yaml"), Script("return;"));
        var driver = new TestDriver();
        var output = new StringWriter();
        var events = Path.Combine(directory, "events.jsonl");
        var exit = await ExecuteAsync(directory, driver, output, events, parallel);
        Assert.Equal(1, driver.StartCount);
        var lease = Assert.Single(driver.Leases);
        Assert.Equal(1, lease.DisposeCount);
        Assert.Equal(0, lease.ResetCount);
        var lines = (await File.ReadAllLinesAsync(events)).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Contains(lines, line => line.RootElement.GetProperty("type").GetString() == "step-completed"
                && line.RootElement.GetProperty("stepId").GetString() == "executed-step");
            Assert.DoesNotContain(lines, line => line.RootElement.GetProperty("type").GetString() == "step-started"
                && line.RootElement.GetProperty("stepId").GetString() == "refused-step");
        }
        finally { foreach (var line in lines) line.Dispose(); }
        Assert.Equal(4, exit);
    }

    private const string Source = "environment:\n  source: {driver: test-owned, config: ./environment.yaml}\n";

    private static string Script(string code)
        => Source + "steps:\n  - id: executed-step\n    type: script.csharp\n    code: |\n"
            + string.Join("\n", code.Split('\n').Select(line => "      " + line)) + "\n";

    private static Task<int> ExecuteAsync(string path, TestDriver driver, TextWriter output, string events,
        int? parallel = null, bool failOnInconclusive = false, bool failOnEnvironmentError = false,
        CancellationToken cancellationToken = default)
        => RunCommand.ExecuteAsync(path, SelectionCriteria.None, parallel, watch: false,
            failOnEnvironmentError, failOnInconclusive, htmlReportPath: null, junitReportPath: null,
            eventsReportPath: events, eventsStreamPath: null, decorate: false, output, telemetryHook: null,
            cancellationToken, environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));

    internal sealed class TestDriver : IEnvironmentDriver
    {
        private int _startCount;
        public string Id => "test-owned";
        internal EnvironmentStatePolicy Policy { get; init; } = EnvironmentStatePolicy.Inline;
        internal int StartCount => _startCount;
        internal ConcurrentBag<TestLease> Leases { get; } = new();
        internal ConcurrentBag<string> StartedConfigurations { get; } = new();
        internal bool FailStart { get; init; }
        internal Action? BeforeStart { get; init; }
        internal Func<string, Exception?>? AcquisitionFailure { get; init; }

        public EnvironmentPlan Bind(EnvironmentSourceSpec source, string suiteDirectory)
        {
            var config = Path.GetFullPath(source.Config, suiteDirectory);
            if (!File.Exists(config)) throw new EnvironmentBindingException("test-owned configuration is missing", new[] { config });
            return new EnvironmentPlan(new EnvironmentDeclarations(new Dictionary<string, DeclaredServiceInfo>(),
                new Dictionary<string, string>()), "stable-owned-test-environment", Policy,
                (_, cancellationToken) =>
                {
                    Interlocked.Increment(ref _startCount);
                    StartedConfigurations.Add(config);
                    BeforeStart?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (AcquisitionFailure?.Invoke(config) is { } acquisitionFailure) throw acquisitionFailure;
                    if (FailStart) throw new OrchestrationException(new OrchestrationErrorInfo(
                        OrchestrationErrorKind.HealthGate, "test-owned", null, null, "synthetic readiness failure"));
                    var lease = new TestLease();
                    Leases.Add(lease);
                    return Task.FromResult<IKeptTopology>(lease);
                }, inputFiles: new[] { config });
        }
    }

    internal sealed class TestLease : IKeptTopology
    {
        internal int DisposeCount { get; private set; }
        internal int ResetCount { get; private set; }
        public IReadOnlyList<SecurityConfirmation> SecurityConfirmations => Array.Empty<SecurityConfirmation>();
        public IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices => Array.Empty<EndpointSelectionNotice>();
        public IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices => Array.Empty<EndpointTrustNotice>();
        public IReadOnlyDictionary<string, object> DiscoveredServices { get; } = new Dictionary<string, object>();
        public IReadOnlyList<string> DependencyNames => Array.Empty<string>();
        public IReadOnlyDictionary<string, string> DependencyTypes { get; } = new Dictionary<string, string>();
        public Task ReseedAsync(CancellationToken cancellationToken) { ResetCount++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }
}
