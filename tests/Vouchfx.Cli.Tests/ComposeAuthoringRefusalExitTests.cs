using System.Text.Json;
using Vouchfx.Cli.Selection;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Xunit;

namespace Vouchfx.Cli.Tests;

[CollectionDefinition("Compose authoring environment", DisableParallelization = true)]
public sealed class ComposeAuthoringEnvironmentGroup { }

/// <summary>Normal CLI, real Compose binding/acquisition, simulated native process; no Docker runs.</summary>
[Collection("Compose authoring environment")]
public sealed class ComposeAuthoringRefusalExitTests
{
    public static TheoryData<int?, string> PolicyCases
    {
        get
        {
            var cases = new TheoryData<int?, string>();
            foreach (var parallel in new int?[] { null, 1, 2 })
                foreach (var fault in new[] { "ownership", "local-daemon", "input-change", "invalid-input-reload" })
                    cases.Add(parallel, fault);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(PolicyCases))]
    public async Task ExplicitPreUpAuthoringRefusal_BesideExecutedPass_NeverExitsSuccess(int? parallel, string fault)
    {
        using var fixture = new Inputs(fault);
        var output = new StringWriter();
        var exit = await fixture.RunAsync(output, parallel);
        Assert.Contains("PASS", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Compose", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, fixture.Driver.StartCount);
        Assert.Equal(1, fixture.Pass.StartCount);
        Assert.Equal(1, Assert.Single(fixture.Pass.Leases).DisposeCount);
        Assert.Equal(0, fixture.Process.UpCount);
        Assert.False(fixture.Process.Owned);
        Assert.False(Directory.Exists(fixture.Journals));
        var events = await File.ReadAllTextAsync(fixture.Events);
        Assert.DoesNotContain(Inputs.Canary, output + events, StringComparison.Ordinal);
        var lines = (await File.ReadAllLinesAsync(fixture.Events)).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Contains(lines, line => line.RootElement.GetProperty("type").GetString() == "step-completed"
                && line.RootElement.GetProperty("stepId").GetString() == "pass-step");
            Assert.DoesNotContain(lines, line => line.RootElement.GetProperty("type").GetString() == "step-started"
                && line.RootElement.GetProperty("stepId").GetString() == "compose-step");
        }
        finally { foreach (var line in lines) line.Dispose(); }
        Assert.Equal(4, exit);
    }

    public static TheoryData<int?, string, bool> RuntimeCases
    {
        get
        {
            var cases = new TheoryData<int?, string, bool>();
            foreach (var parallel in new int?[] { null, 2 })
                foreach (var fault in new[] { "native-process", "native-malformed", "native-shape", "reload-fidelity",
                    "reload-malformed", "post-up-discovery", "post-up-process", "post-up-argument", "post-up-deadline" })
                    foreach (var strict in new[] { false, true }) cases.Add(parallel, fault, strict);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(RuntimeCases))]
    public async Task OrdinaryNativeOrPostUpFault_BesidePass_RetainsOptIn(int? parallel, string fault, bool strict)
    {
        using var fixture = new Inputs(fault);
        var output = new StringWriter();
        var exit = await fixture.RunAsync(output, parallel, strict);
        Assert.Equal(strict ? fault == "post-up-argument" ? 4 : 3 : 0, exit);
        Assert.Contains("PASS", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(fault == "post-up-argument" ? "INCONCLUSIVE" : "ENV_ERROR", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, fixture.Driver.StartCount);
        Assert.Equal(1, Assert.Single(fixture.Pass.Leases).DisposeCount);
        Assert.Equal(fault.StartsWith("post-up-", StringComparison.Ordinal) ? 1 : 0, fixture.Process.UpCount);
        Assert.False(fixture.Process.Owned);
        Assert.Equal(fixture.Process.UpCount, fixture.Process.RemovedContainers);
        Assert.Empty(Directory.Exists(fixture.Journals) ? Directory.GetFiles(fixture.Journals) : Array.Empty<string>());
        Assert.DoesNotContain(Inputs.Canary, output + await File.ReadAllTextAsync(fixture.Events), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationAfterUp_PreservesParallelPolicyAndIndependentCleanup()
    {
        using var fixture = new Inputs("caller-cancel");
        var output = new StringWriter();
        // Nominal sequential uses the generic FreshOwned recreation policy: pass completes first.
        Assert.Equal(0, await fixture.RunAsync(output, parallel: null));
        Assert.True(fixture.Cancellation.IsCancellationRequested);
        Assert.Contains("PASS", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, fixture.Process.UpCount);
        Assert.Equal(1, fixture.Process.RemovedContainers);
        Assert.False(fixture.Process.CleanupTokenWasCancelled);
        Assert.False(fixture.Process.Owned);
        Assert.Empty(Directory.GetFiles(fixture.Journals));
    }

    private sealed class Inputs : IDisposable
    {
        internal const string Canary = "synthetic-compose-refusal-canary";
        private readonly string? _originalDockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
        internal Inputs(string fault)
        {
            Environment.SetEnvironmentVariable("DOCKER_HOST", null);
            Directory = System.IO.Directory.CreateTempSubdirectory("vouchfx-compose-refusal-cli-").FullName;
            Journals = Path.Combine(Directory, "journals");
            Events = Path.Combine(Directory, "events.jsonl");
            File.WriteAllText(Path.Combine(Directory, "environment.yaml"), "test-owned");
            File.WriteAllText(Path.Combine(Directory, "a-pass.e2e.yaml"), """
                environment:
                  source: { driver: test-owned, config: ./environment.yaml }
                steps:
                  - id: pass-step
                    type: script.csharp
                    code: 'return;'
                """);
            File.WriteAllText(Path.Combine(Directory, "z-compose.e2e.yaml"), """
                environment:
                  source: { driver: compose, config: ./environment.compose.yaml }
                steps:
                  - id: compose-step
                    type: script.csharp
                    code: 'return;'
                """);
            File.WriteAllText(Path.Combine(Directory, "environment.compose.yaml"), """
                version: 1
                files: [./compose.yaml]
                startupTimeoutSeconds: 1
                cleanupTimeoutSeconds: 1
                services:
                  api: { service: api, port: 8080, readiness: { path: /health, status: 200 } }
                """);
            var manifest = Path.Combine(Directory, "compose.yaml");
            File.WriteAllText(manifest, "services: { api: { image: app, ports: ['8080'] } }");
            Process = new FakeProcess(fault, manifest, Cancellation);
            Driver = new TestComposeDriver(Process, Journals);
        }

        internal string Directory { get; }
        internal string Journals { get; }
        internal string Events { get; }
        internal FakeProcess Process { get; }
        internal TestComposeDriver Driver { get; }
        internal AuthoringRefusalExitTests.TestDriver Pass { get; } = new();
        internal CancellationTokenSource Cancellation { get; } = new();

        internal Task<int> RunAsync(TextWriter output, int? parallel, bool strict = false)
            => RunCommand.ExecuteAsync(Directory, SelectionCriteria.None, parallel, watch: false,
                failOnEnvironmentError: strict, failOnInconclusive: strict, htmlReportPath: null,
                junitReportPath: null, eventsReportPath: Events, eventsStreamPath: null, decorate: false,
                output, telemetryHook: null, Cancellation.Token,
                environmentDrivers: new EnvironmentDriverRegistry(new IEnvironmentDriver[] { Driver, Pass }));

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DOCKER_HOST", _originalDockerHost);
            Cancellation.Dispose();
        }
    }

    // Preserve the production driver's pure declarations, policy, source/input identity and cleanup
    // budget; substitute only the existing native process/journal seam in its real acquisition.
    private sealed class TestComposeDriver : IEnvironmentDriver
    {
        private readonly IComposeProcess _process;
        private readonly string _journals;
        internal TestComposeDriver(IComposeProcess process, string journals)
        {
            _process = process;
            _journals = journals;
        }
        internal int StartCount { get; private set; }
        public string Id => "compose";
        public EnvironmentPlan Bind(EnvironmentSourceSpec source, string suiteDirectory)
        {
            var plan = new ComposeEnvironmentDriver().Bind(source, suiteDirectory);
            var config = ComposeConfiguration.Load(Path.GetFullPath(source.Config, suiteDirectory));
            return new EnvironmentPlan(plan.Declarations, plan.SourceIdentity, plan.StatePolicy,
                (context, ct) =>
                {
                    StartCount++;
                    return ComposeLease.StartAsync(config, _process, context.Secrets, ct, _journals);
                }, plan.CleanupTimeout, plan.DeclaredSecurity, plan.InputFiles);
        }
    }

    private sealed class FakeProcess : IComposeProcess
    {
        private readonly string _fault;
        private readonly string _manifest;
        private readonly CancellationTokenSource _cancellation;
        internal FakeProcess(string fault, string manifest, CancellationTokenSource cancellation)
        {
            _fault = fault;
            _manifest = manifest;
            _cancellation = cancellation;
        }
        private string _project = string.Empty;
        internal int UpCount { get; private set; }
        internal bool Owned { get; private set; }
        internal int RemovedContainers { get; private set; }
        internal bool CleanupTokenWasCancelled { get; private set; }

        public async Task<ComposeProcessResult> RunAsync(IReadOnlyList<string> arguments, string directory,
            IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken, string? standardInput = null)
        {
            var fault = _fault;
            var manifest = _manifest;
            var cancellation = _cancellation;
            cancellationToken.ThrowIfCancellationRequested();
            if (arguments[0] == "context") return Result(JsonSerializer.Serialize(
                fault == "local-daemon" ? "npipe:////remote-host/pipe/docker_engine" : "unix:///var/run/docker.sock"));
            if (arguments[0] == "compose")
            {
                if (arguments.Contains("config"))
                {
                    _project = arguments[arguments.ToList().IndexOf("--project-name") + 1];
                    if (standardInput is null)
                    {
                        if (fault == "input-change") File.AppendAllText(manifest, "\n# changed authoring input\n");
                        if (fault == "invalid-input-reload") File.WriteAllText(manifest, "services: [" + Inputs.Canary);
                        if (fault == "native-process") return new ComposeProcessResult(7, Inputs.Canary);
                        if (fault == "native-malformed") return Result("{" + Inputs.Canary);
                        if (fault == "native-shape") return Result("{\"services\":17}");
                    }
                    else
                    {
                        if (fault == "reload-fidelity") return Result("{}");
                        if (fault == "reload-malformed") return Result("{");
                    }
                    return Result("{\"services\":{\"api\":{"
                        + (fault == "ownership" ? "\"privileged\":true," : string.Empty)
                        + "\"image\":\"app\",\"environment\":{\"CANARY\":\"" + Inputs.Canary
                        + "\"},\"ports\":[{\"target\":8080,\"protocol\":\"tcp\"}]}},\"networks\":{\"default\":{\"name\":\""
                        + _project + "_default\"}}}");
                }
                if (arguments.Contains("up"))
                {
                    UpCount++;
                    Owned = true;
                    if (fault == "caller-cancel") { cancellation.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
                    if (fault == "post-up-deadline") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    if (fault == "post-up-process") return new ComposeProcessResult(7, Inputs.Canary);
                }
                if (arguments.Contains("port"))
                {
                    if (fault == "post-up-argument") throw new ArgumentException("synthetic post-up discovery argument failure");
                    return Result("invalid published endpoint " + Inputs.Canary);
                }
                return Result("");
            }
            CleanupTokenWasCancelled |= cancellationToken.IsCancellationRequested;
            if (arguments[1] == "ls") return Result(Owned && arguments[0] == "container" ? new string('a', 64) : "");
            if (arguments[1] == "inspect") return Result(_project);
            if (arguments[1] == "rm") { Owned = false; RemovedContainers++; }
            return Result("");
        }

        private static ComposeProcessResult Result(string output) => new(0, output);
    }
}
