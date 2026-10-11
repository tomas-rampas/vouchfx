using System.Text.Json;
using Vouchfx.Cli.Selection;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class RuntimeDiagnosticRegressionTests : IDisposable
{
    private const string Canary = "private-driver-or-passphrase-canary";
    private readonly string _root = Directory.CreateTempSubdirectory("vouchfx-runtime-diagnostics-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Run_BindFaultBesidePassingSibling_IsInconclusiveAndArchived(int? parallel)
    {
        WriteDriverScenarios();
        var driver = new DiagnosticDriver();
        var output = new StringWriter();
        Assert.Equal(ExitCodes.Inconclusive, await Run(parallel, output, new EnvironmentDriverRegistry(new[] { driver })));
        Assert.Equal(1, driver.Starts);
        Assert.Equal(1, driver.Disposals);
        foreach (var file in new[] { "events.jsonl", "live.jsonl" })
        {
            var completed = ReadEvents(file, "scenario-completed");
            var refused = Assert.Single(completed, e => e.GetProperty("scenarioId").GetString() == "bad");
            Assert.Equal("INCONCLUSIVE", refused.GetProperty("verdict").GetString());
            Assert.Contains("pre-topology", refused.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(nameof(InvalidOperationException), refused.GetProperty("message").GetString()!, StringComparison.Ordinal);
            Assert.Equal("PASS", Assert.Single(completed, e => e.GetProperty("scenarioId").GetString() == "good")
                .GetProperty("verdict").GetString());
            Assert.Single(ReadEvents(file, "step-completed"));
        }
        foreach (var file in new[] { "events.jsonl", "live.jsonl", "report.html", "junit.xml" })
        {
            var text = File.ReadAllText(Path.Combine(_root, file));
            Assert.DoesNotContain(Canary, text, StringComparison.Ordinal);
            Assert.Contains(nameof(InvalidOperationException), text, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(Canary, output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Run_GenuineAcquisitionFailure_RemainsOptionalEnvironmentError(int? parallel)
    {
        WriteDriverScenarios();
        var driver = new DiagnosticDriver { FailAcquisition = true };
        Assert.Equal(ExitCodes.Success, await Run(parallel, new StringWriter(), new EnvironmentDriverRegistry(new[] { driver })));
        Assert.Equal("ENV_ERROR", Assert.Single(ReadEvents("events.jsonl", "scenario-completed"),
            e => e.GetProperty("scenarioId").GetString() == "bad").GetProperty("verdict").GetString());
        Assert.Equal(2, driver.Starts);
        Assert.Equal(1, driver.Disposals);
    }

    [Fact]
    public async Task Run_SharedEnvironmentDivergence_IsAnInconclusiveAuthoringRefusalInEveryReport()
    {
        File.WriteAllText(Path.Combine(_root, "a.e2e.yaml"), InlineScenario("first", "example/api:1"));
        File.WriteAllText(Path.Combine(_root, "b.e2e.yaml"), InlineScenario("second", "example/api:2"));
        var starts = 0;
        var drivers = new EnvironmentDriverRegistry(Array.Empty<IEnvironmentDriver>(), (_, _) =>
        {
            Interlocked.Increment(ref starts);
            throw new InvalidOperationException("divergence must refuse before acquisition");
        });
        Assert.Equal(ExitCodes.Inconclusive, await Run(null, new StringWriter(), drivers));
        Assert.Equal(0, starts);
        foreach (var file in new[] { "events.jsonl", "live.jsonl" })
        {
            var completed = ReadEvents(file, "scenario-completed");
            Assert.Equal(2, completed.Count);
            Assert.All(completed, e =>
            {
                Assert.Equal("INCONCLUSIVE", e.GetProperty("verdict").GetString());
                Assert.Equal(1, e.GetProperty("counts").GetProperty("inconclusive").GetInt32());
                Assert.Equal(0, e.GetProperty("counts").GetProperty("envError").GetInt32());
                Assert.Contains("different environment block", e.GetProperty("message").GetString()!, StringComparison.Ordinal);
                Assert.DoesNotContain("EnvironmentError", e.GetProperty("message").GetString()!, StringComparison.Ordinal);
            });
        }
        foreach (var file in new[] { "report.html", "junit.xml" })
        {
            var text = File.ReadAllText(Path.Combine(_root, file));
            Assert.Contains("different environment block", text, StringComparison.Ordinal);
            Assert.DoesNotContain("EnvironmentError", text, StringComparison.Ordinal);
        }
        Assert.Empty(ReadEvents("events.jsonl", "step-completed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidateJson_IndependentStepAndSecurityFaults_AreSeparateDiagnostics(bool nestedReference)
    {
        File.WriteAllText(Path.Combine(_root, "client.pem"), "placeholder");
        File.WriteAllText(Path.Combine(_root, "client.key"), "placeholder");
        var password = nestedReference
            ? "${secret:nosuchsource/PASS" + Canary + "${secret:LEAKED}"
            : "${secret:nosuchsource/PASS}";
        var yaml = $$"""
            environment:
              services:
                api:
                  image: example/api:1
                  security:
                    profile: mtls
                    endpoint: 8443
                    clientCert: ./client.pem
                    clientKey: ./client.key
                    clientKeyPassword: "{{password}}"
            steps:
              - id: call
                type: http.rest
                target: api
                method: GET
                path: /health
                headers:
                  Authorization: "Bearer ${secret:nosuchsource/STEP_TOKEN}"
            """;
        var file = Path.Combine(_root, "invalid.e2e.yaml");
        File.WriteAllText(file, yaml);
        var output = new StringWriter();
        Assert.Equal(ExitCodes.Inconclusive, ValidateCommand.Execute(file, json: true, output, new StringWriter()));
        using var document = JsonDocument.Parse(output.ToString());
        var diagnostics = document.RootElement.GetProperty("scenarios")[0].GetProperty("diagnostics");
        Assert.Equal(2, diagnostics.GetArrayLength());
        Assert.Equal("pipeline", diagnostics[0].GetProperty("stage").GetString());
        Assert.Equal("pipeline", diagnostics[1].GetProperty("stage").GetString());
        Assert.Contains("step 'call'", diagnostics[0].GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("environment.services", diagnostics[0].GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Contains("environment.services.api.security.clientKeyPassword", diagnostics[1].GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("step 'call'", diagnostics[1].GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.All(diagnostics.EnumerateArray(), d => Assert.DoesNotContain("\n", d.GetProperty("message").GetString()!, StringComparison.Ordinal));
        Assert.DoesNotContain(Canary, output.ToString(), StringComparison.Ordinal);
    }

    private void WriteDriverScenarios()
    {
        var good = Directory.CreateDirectory(Path.Combine(_root, "a-good")).FullName;
        var bad = Directory.CreateDirectory(Path.Combine(_root, "b-bad")).FullName;
        File.WriteAllText(Path.Combine(good, "good.e2e.yaml"), DriverScenario("good"));
        File.WriteAllText(Path.Combine(bad, "bad.e2e.yaml"), DriverScenario("bad"));
    }

    private Task<int> Run(int? parallel, TextWriter output, EnvironmentDriverRegistry drivers) =>
        RunCommand.ExecuteAsync(_root, SelectionCriteria.None, parallel, watch: false,
            failOnEnvironmentError: false, failOnInconclusive: false,
            htmlReportPath: Path.Combine(_root, "report.html"), junitReportPath: Path.Combine(_root, "junit.xml"),
            eventsReportPath: Path.Combine(_root, "events.jsonl"), eventsStreamPath: Path.Combine(_root, "live.jsonl"),
            decorate: false, output, telemetryHook: null, CancellationToken.None, environmentDrivers: drivers);

    private List<JsonElement> ReadEvents(string file, string type) => File.ReadAllLines(Path.Combine(_root, file))
        .Select(line => JsonDocument.Parse(line))
        .Select(document =>
        {
            using (document) return document.RootElement.Clone();
        })
        .Where(element => element.GetProperty("type").GetString() == type).ToList();

    private static string DriverScenario(string name) => $$"""
        metadata: { name: {{name}} }
        environment:
          source: { driver: diagnostics, config: environment.fixture }
        steps:
          - id: local
            type: script.csharp
            code: 'return;'
        """;

    private static string InlineScenario(string name, string image) => $$"""
        metadata: { name: {{name}} }
        environment:
          services:
            api: { image: {{image}} }
        steps:
          - id: local
            type: script.csharp
            code: 'return;'
        """;

    private sealed class DiagnosticDriver : IEnvironmentDriver
    {
        internal bool FailAcquisition { get; init; }
        internal int Starts;
        internal int Disposals;
        public string Id => "diagnostics";
        public EnvironmentPlan Bind(EnvironmentSourceSpec source, string suiteDirectory)
        {
            var bad = Path.GetFileName(suiteDirectory) == "b-bad";
            if (bad && !FailAcquisition) throw new InvalidOperationException(Canary);
            return new EnvironmentPlan(new EnvironmentDeclarations(new Dictionary<string, DeclaredServiceInfo>(),
                new Dictionary<string, string>()), suiteDirectory, EnvironmentStatePolicy.FreshOwned, (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref Starts);
                if (bad) throw new OrchestrationException(new OrchestrationErrorInfo(
                    OrchestrationErrorKind.Provision, "fixture", null, null, "own synthetic acquisition failure"));
                return Task.FromResult<IKeptTopology>(new DiagnosticLease(this));
            });
        }
    }

    private sealed class DiagnosticLease : IKeptTopology
    {
        private readonly DiagnosticDriver _driver;
        internal DiagnosticLease(DiagnosticDriver driver) => _driver = driver;
        public IReadOnlyList<SecurityConfirmation> SecurityConfirmations => Array.Empty<SecurityConfirmation>();
        public IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices => Array.Empty<EndpointSelectionNotice>();
        public IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices => Array.Empty<EndpointTrustNotice>();
        public IReadOnlyDictionary<string, object> DiscoveredServices => new Dictionary<string, object>();
        public IReadOnlyList<string> DependencyNames => Array.Empty<string>();
        public IReadOnlyDictionary<string, string> DependencyTypes => new Dictionary<string, string>();
        public Task ReseedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _driver.Disposals);
            return ValueTask.CompletedTask;
        }
    }
}
