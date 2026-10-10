using System.Text.Json;
using Vouchfx.Cli.Selection;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class PrecompileSecretCensusCliTests
{
    [Theory]
    [InlineData("${secret:unknownsource/IGNORED}")]
    [InlineData("${secret:env/}")]
    [InlineData("${secret:env/IGNORED}")]
    public async Task AvroIgnoredPayload_NormalValidateAndRun_PreservesRuntimePolicy(string payload)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-secret563-avro-").FullName;
        var path = Path.Combine(directory, "ignored.e2e.yaml");
        File.WriteAllText(path, AvroScenario(payload));
        var validation = new StringWriter();
        Assert.Equal(0, ValidateCommand.Execute(path, json: true, validation, new StringWriter()));
        using var report = JsonDocument.Parse(validation.ToString());
        Assert.True(report.RootElement.GetProperty("valid").GetBoolean());
        foreach (var parallel in new int?[] { null, 1, 2 })
            foreach (var sibling in new[] { false, true })
                foreach (var strict in new[] { false, true })
                {
                    File.WriteAllText(path, "environment:\n  source: {driver: secret-census-test, config: ./environment.yaml}\n"
                        + AvroScenario(payload)[AvroScenario(payload).IndexOf("steps:", StringComparison.Ordinal)..]);
                    var passing = Path.Combine(directory, "pass.e2e.yaml");
                    if (sibling) File.WriteAllText(passing,
                        "environment:\n  source: {driver: secret-census-test, config: ./environment.yaml}\nsteps:\n  - id: passing-sibling\n    type: script.csharp\n    code: 'return;'\n");
                    else if (File.Exists(passing)) File.Delete(passing);
                    var driver = new CensusDriver();
                    var output = new StringWriter();
                    var exit = await RunCommand.ExecuteAsync(directory, SelectionCriteria.None, parallel, watch: false,
                        failOnEnvironmentError: strict, failOnInconclusive: false, htmlReportPath: null,
                        junitReportPath: null, eventsReportPath: null, eventsStreamPath: null,
                        decorate: false, output, telemetryHook: null, CancellationToken.None,
                        environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
                    // Empty discovery yields a genuine missing-bootstrap EnvironmentError;
                    // it returns before any Kafka client construction or network access.
                    Assert.Equal(strict ? 3 : 0, exit);
                    Assert.Equal(sibling ? 2 : 1, driver.StartCount);
                    Assert.Equal(sibling ? 2 : 1, driver.DisposeCount);
                    Assert.DoesNotContain("names an unknown source", output.ToString(), StringComparison.Ordinal);
                    Assert.DoesNotContain("malformed secret reference", output.ToString(), StringComparison.Ordinal);
                    if (sibling) Assert.Contains("PASS", output.ToString(), StringComparison.Ordinal);
                }
    }

    internal static string AvroScenario(string payload) => $$"""
        environment:
          dependencies:
            broker: {type: kafka}
        steps:
          - id: publish
            type: mq-publish.kafka
            target: broker
            topic: test
            payload: '{{payload}}'
            avro:
              schemaRegistry: broker
              subject: test-value
              schema: '{"type":"record","name":"Fixture","fields":[{"name":"f","type":"string"}]}'
              record: {f: value}
        """;

    public static IEnumerable<object[]> RunCases() => new[] { "soap", "kafka", "s3", "http" }
        .SelectMany(kind => new[] { false, true }.SelectMany(sibling => new int?[] { null, 1, 2 }
            .Select(parallel => new object[] { kind, sibling, parallel! })));

    [Theory]
    [MemberData(nameof(RunCases))]
    public async Task Run_NormalRegisteredDriver_RefusalCannotBeMaskedByExecutedPass(
        string kind, bool sibling, int? parallel)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-secret563-run-").FullName;
        File.WriteAllText(Path.Combine(directory, "environment.yaml"), "test-owned");
        const string source = "environment:\n  source: {driver: secret-census-test, config: ./environment.yaml}\n";
        var scenario = Scenario(kind);
        File.WriteAllText(Path.Combine(directory, "refused.e2e.yaml"), source + scenario[scenario.IndexOf("steps:", StringComparison.Ordinal)..]);
        if (sibling) File.WriteAllText(Path.Combine(directory, "pass.e2e.yaml"),
            source + "steps:\n  - id: passing-sibling\n    type: script.csharp\n    code: 'return;'\n");
        var driver = new CensusDriver();
        var output = new StringWriter();
        var events = Path.Combine(directory, "events.jsonl");
        var exit = await RunCommand.ExecuteAsync(directory, SelectionCriteria.None, parallel, watch: false,
            failOnEnvironmentError: false, failOnInconclusive: false, htmlReportPath: null,
            junitReportPath: null, eventsReportPath: events, eventsStreamPath: null,
            decorate: false, output, telemetryHook: null, CancellationToken.None,
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        Assert.Equal(4, exit);
        Assert.Contains("names an unknown source", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(sibling ? 1 : 0, driver.StartCount);
        Assert.Equal(sibling ? 1 : 0, driver.DisposeCount);
        if (sibling) Assert.Contains("PASS", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(RunCommand.ProviderOrEngineFaultNotice, output.ToString(), StringComparison.Ordinal);
        var lines = File.Exists(events) ? File.ReadAllLines(events) : Array.Empty<string>();
        Assert.DoesNotContain(lines, line => line.Contains("step-started", StringComparison.Ordinal)
            && !line.Contains("passing-sibling", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("soap", 4)]
    [InlineData("kafka", 4)]
    [InlineData("s3", 4)]
    [InlineData("http", 4)]
    [InlineData("script", 0)]
    public void Validate_NormalCli_UnknownReferenceUsesActualFieldMode(string kind, int expected)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-secret563-cli-").FullName;
        var path = Path.Combine(directory, "probe.e2e.yaml");
        File.WriteAllText(path, Scenario(kind));
        var output = new StringWriter();
        Assert.Equal(expected, ValidateCommand.Execute(path, json: true, output, new StringWriter()));
        using var report = JsonDocument.Parse(output.ToString());
        Assert.Equal(expected == 0, report.RootElement.GetProperty("valid").GetBoolean());
        if (expected != 0) Assert.Contains("names an unknown source", output.ToString(), StringComparison.Ordinal);
    }

    internal static string Scenario(string kind) => kind switch
    {
        "soap" => """
            environment:
              services:
                api: { image: app }
            steps:
              - id: request
                type: http.soap
                target: api
                path: /soap
                envelope: '<Envelope>${secret:nosuchsource/TOKEN}</Envelope>'
            """,
        "kafka" => """
            environment:
              dependencies:
                broker: { type: kafka }
            steps:
              - id: publish
                type: mq-publish.kafka
                target: broker
                topic: synthetic-probe
                payload: '${secret:nosuchsource/TOKEN}'
            """,
        "s3" => """
            environment:
              dependencies:
                objects: { type: s3 }
            steps:
              - id: assert-object
                type: storage-assert.s3
                target: objects
                bucket: synthetic-bucket
                key: '${secret:nosuchsource/TOKEN}'
                expect: { exists: false }
            """,
        "http" => """
            environment:
              services:
                api: { image: app }
            steps:
              - id: request
                type: http.rest
                target: api
                method: GET
                path: '/${secret:nosuchsource/TOKEN}'
            """,
        "script" => """
            steps:
              - id: literal
                type: script.csharp
                code: 'var literal = "${secret:nosuchsource/TOKEN}";'
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private sealed class CensusDriver : IEnvironmentDriver
    {
        public string Id => "secret-census-test";
        internal int StartCount;
        internal int DisposeCount;
        public EnvironmentPlan Bind(EnvironmentSourceSpec source, string suiteDirectory) => new(
            new EnvironmentDeclarations(
                new Dictionary<string, DeclaredServiceInfo> { ["api"] = new(Array.Empty<string>()) },
                new Dictionary<string, string> { ["broker"] = "kafka", ["objects"] = "s3" }),
            "secret-census-fixture", EnvironmentStatePolicy.FreshOwned,
            (_, _) =>
            {
                Interlocked.Increment(ref StartCount);
                return Task.FromResult<IKeptTopology>(new CensusLease(this));
            });
    }

    private sealed class CensusLease : IKeptTopology
    {
        private readonly CensusDriver _driver;
        internal CensusLease(CensusDriver driver) => _driver = driver;
        public IReadOnlyList<SecurityConfirmation> SecurityConfirmations => Array.Empty<SecurityConfirmation>();
        public IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices => Array.Empty<EndpointSelectionNotice>();
        public IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices => Array.Empty<EndpointTrustNotice>();
        public IReadOnlyDictionary<string, object> DiscoveredServices => new Dictionary<string, object>();
        public IReadOnlyList<string> DependencyNames => Array.Empty<string>();
        public IReadOnlyDictionary<string, string> DependencyTypes => new Dictionary<string, string>();
        public Task ReseedAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Fresh-owned fixture cannot be reset.");
        public ValueTask DisposeAsync() { Interlocked.Increment(ref _driver.DisposeCount); return ValueTask.CompletedTask; }
    }
}
