using System.Reflection;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Engine.Runtime.Secrets;
using Vouchfx.Sdk;
using Vouchfx.TestSupport;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class ScenarioSecretLedgerTests
{
    private static readonly string[] s_scenarioNames = { "first", "second" };
    private static readonly string[] s_manualScenarioNames = { "manual-record" };
    private static readonly Assembly[] s_providers = { typeof(ScriptCsharpProvider).Assembly };
    private static readonly StepKindRegistry s_registry = StepKindRegistry.BuildAndFreeze(s_providers);

    [Fact]
    public void ScenarioAccessors_ExposeOnlyOwnResolutions_WhileReportingRetainsAllScopes()
    {
        using var probeValue = new EnvironmentValue("probe-ledger-value");
        using var firstValue = new EnvironmentValue("first-ledger-value");
        using var secondValue = new EnvironmentValue("second-ledger-value");
        var aggregate = new ResolvedSecretLedger();
        using var probe = new SecretAccessorScope(new ISecretResolver[] { new EnvironmentSecretResolver() }, aggregate);
        using var first = new SecretAccessorScope(new ISecretResolver[] { new EnvironmentSecretResolver() }, aggregate);
        using var second = new SecretAccessorScope(new ISecretResolver[] { new EnvironmentSecretResolver() }, aggregate);
        _ = probe.Accessor.Resolve(probeValue.Reference);
        var firstScript = first.CreateScenarioAccessor();
        var secondScript = second.CreateScenarioAccessor();
        Assert.Equal(0, firstScript.ResolvedSecrets.Count);
        Assert.Equal(0, secondScript.ResolvedSecrets.Count);
        _ = firstScript.Resolve(firstValue.Reference);
        Assert.Equal(1, firstScript.ResolvedSecrets.Count);
        Assert.Equal(SecretString.RedactedMarker, firstScript.ResolvedSecrets.Scrub(firstValue.Value));
        Assert.Equal(probeValue.Value, firstScript.ResolvedSecrets.Scrub(probeValue.Value));
        Assert.Equal(firstValue.Value, secondScript.ResolvedSecrets.Scrub(firstValue.Value));
        _ = secondScript.Resolve(secondValue.Reference);
        Assert.Equal(1, secondScript.ResolvedSecrets.Count);
        Assert.Equal(secondValue.Value, firstScript.ResolvedSecrets.Scrub(secondValue.Value));
        Assert.Equal(3, aggregate.Count);
        foreach (var value in new[] { probeValue.Value, firstValue.Value, secondValue.Value })
            Assert.Equal(SecretString.RedactedMarker, aggregate.Scrub(value));
    }

    [Fact]
    public void ScenarioResolve_InvokesUnderlyingResolverOnce_AndFailuresRecordNothing()
    {
        using var value = new EnvironmentValue("single-resolution-value");
        var resolver = new CountingResolver();
        var aggregate = new ResolvedSecretLedger();
        using (var scope = new SecretAccessorScope(new ISecretResolver[] { resolver }, aggregate))
        {
            var accessor = scope.CreateScenarioAccessor();
            Assert.Equal(value.Value, accessor.Resolve(value.Reference).Reveal());
            Assert.Equal(1, resolver.Calls);
            Assert.Equal(1, accessor.ResolvedSecrets.Count);
            Assert.Equal(1, aggregate.Count);
            Assert.Throws<SecretResolutionException>(() => accessor.Resolve("env/" + Guid.NewGuid().ToString("N")));
            Assert.Equal(2, resolver.Calls);
            Assert.Equal(1, accessor.ResolvedSecrets.Count);
            Assert.Equal(1, aggregate.Count);
            Assert.Equal(0, resolver.Disposals);
        }
        Assert.Equal(1, resolver.Disposals);
    }

    [Fact]
    public void ScenarioAccessor_WithoutSuppliedAggregate_StillRecordsForReporting()
    {
        using var value = new EnvironmentValue("private-scope-value");
        using var scope = new SecretAccessorScope(new ISecretResolver[] { new EnvironmentSecretResolver() });
        var accessor = scope.CreateScenarioAccessor();
        _ = accessor.Resolve(value.Reference);
        Assert.NotSame(accessor.ResolvedSecrets, scope.Accessor.ResolvedSecrets);
        Assert.Equal(SecretString.RedactedMarker, scope.Accessor.ResolvedSecrets.Scrub(value.Value));
    }

    [Theory]
    [InlineData("probe\u001bvalue")]
    [InlineData("probe\rvalue")]
    [InlineData("probe\u009bvalue")]
    [InlineData("probe\u007fvalue")]
    public async Task RunSuite_ScriptLedgersStayLocal_AndLiveBatchReportsRedactAllResolvedScopes(string probeSecret)
    {
        using var probeValue = new EnvironmentValue(probeSecret);
        using var firstValue = new EnvironmentValue("sibling-canary-" + Guid.NewGuid().ToString("N"));
        using var secondValue = new EnvironmentValue("own-canary-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(Path.GetTempPath(), "vouchfx-ledger395-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var first = Yaml(firstValue, probeValue, secondValue, report: false);
            var second = Yaml(secondValue, probeValue, firstValue, report: true);
            var driver = new ProbeDriver(probeValue.Reference);
            var output = new StringWriter();
            var events = Path.Combine(directory, "events.jsonl");
            var live = Path.Combine(directory, "live.jsonl");
            var html = Path.Combine(directory, "report.html");
            var junit = Path.Combine(directory, "report.xml");
            var result = await ScenarioRunner.RunSuiteAsync(
                new[] { BuildAst(first), BuildAst(second) }, s_scenarioNames, new[] { first, second },
                s_providers, "unused-host", output, seedBaseDirectory: directory,
                eventsReportPath: events, eventsStreamPath: live, htmlReportPath: html, junitReportPath: junit,
                environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
            Assert.Equal(1, driver.Starts);
            Assert.True(result.ExecutedAnyScenario);
            Assert.True(result.Verdict == Verdict.Fail, output.ToString());
            var lines = await File.ReadAllLinesAsync(events);
            var checks = lines.Where(line => EventStreamJson.FromLine(line).Type == EventTypes.StepCompleted)
                .Select(EventStreamJson.FromLine<StepCompletedEvent>)
                .Where(e => e.StepId == "check").ToArray();
            Assert.Equal(2, checks.Length);
            Assert.All(checks, e => Assert.Equal(Verdict.Pass, e.Verdict));
            foreach (var stream in new[] { events, live })
                Assert.Contains("REDACTED", await File.ReadAllTextAsync(stream), StringComparison.Ordinal);
            foreach (var text in new[] { output.ToString(), await File.ReadAllTextAsync(events),
                await File.ReadAllTextAsync(live), await File.ReadAllTextAsync(html), await File.ReadAllTextAsync(junit) })
            {
                Assert.DoesNotContain(firstValue.Value, text, StringComparison.Ordinal);
                Assert.DoesNotContain(probeValue.Value, text, StringComparison.Ordinal);
                Assert.DoesNotContain(DisplaySanitiser.SanitiseForDisplay(probeValue.Value)!, text, StringComparison.Ordinal);

            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ScenarioManualRecord_ForwardsOnlyMeaningfulDistinctValues_WithoutExposingOtherScopes()
    {
        var aggregate = new ResolvedSecretLedger();
        using var scope = new SecretAccessorScope(Array.Empty<ISecretResolver>(), aggregate);
        var first = scope.CreateScenarioAccessor().ResolvedSecrets;
        var second = scope.CreateScenarioAccessor().ResolvedSecrets;
        aggregate.Record("probe-only-value");
        foreach (var ignored in new string?[] { null, "", " ", "\t\r\n" })
            first.Record(ignored);
        Assert.Equal(0, first.Count);
        Assert.Equal(1, aggregate.Count);
        first.Record("manual-record-canary");
        first.Record("manual-record-canary");
        Assert.Equal(1, first.Count);
        Assert.Equal(2, aggregate.Count);
        Assert.Equal(SecretString.RedactedMarker, first.Scrub("manual-record-canary"));
        Assert.Equal(SecretString.RedactedMarker, aggregate.Scrub("manual-record-canary"));
        Assert.Equal("probe-only-value", first.Scrub("probe-only-value"));
        Assert.Equal(0, second.Count);
        Assert.Equal("manual-record-canary", second.Scrub("manual-record-canary"));
    }

    [Theory]
    [InlineData("manually-recorded-provider-secret")]
    [InlineData("manual\u001bpassphrase")]
    [InlineData("manual\rpassphrase")]
    public async Task RunSuite_ManualRecord_RedactsStructuredObservationInLiveAndArchivedReports(string canary)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vouchfx-record395-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var probeValue = new EnvironmentValue("manual-registration-probe-value");
            var literal = JsonSerializer.Serialize(canary);
            var yaml = $$"""
                environment:
                  source: { driver: ledger-probe, config: unused.fixture }
                steps:
                  - id: record
                    type: script.csharp
                    code: |
                      var canary = {{literal}};
                      var ledger = ((Vouchfx.Engine.Abstractions.Secrets.SecretAccessor)Secrets).ResolvedSecrets;
                      ledger.Record(canary);
                      if (ledger.Count != 1 || ledger.Scrub(canary) == canary)
                          throw new System.Exception("local manual registration missing");
                      throw new System.Exception(System.Text.Json.JsonSerializer.Serialize(canary));
                """;
            var events = Path.Combine(directory, "events.jsonl");
            var live = Path.Combine(directory, "live.jsonl");
            var html = Path.Combine(directory, "report.html");
            var junit = Path.Combine(directory, "report.xml");
            var output = new StringWriter();
            var result = await ScenarioRunner.RunSuiteAsync(
                new[] { BuildAst(yaml) }, s_manualScenarioNames, new[] { yaml },
                s_providers, "unused-host", output, seedBaseDirectory: directory,
                eventsReportPath: events, eventsStreamPath: live, htmlReportPath: html, junitReportPath: junit,
                environmentDrivers: new EnvironmentDriverRegistry(new[] { new ProbeDriver(probeValue.Reference) }));
            Assert.True(result.Verdict == Verdict.Fail, output.ToString());
            foreach (var stream in new[] { events, live })
            {
                var completed = (await File.ReadAllLinesAsync(stream))
                    .Where(line => EventStreamJson.FromLine(line).Type == EventTypes.StepCompleted)
                    .Select(EventStreamJson.FromLine<StepCompletedEvent>).Single();
                Assert.Equal(Verdict.Fail, completed.Verdict);
                Assert.Equal(SecretString.RedactedMarker, completed.Observation?.GetString());
            }
            foreach (var text in new[] { output.ToString(), await File.ReadAllTextAsync(events),
                await File.ReadAllTextAsync(live), await File.ReadAllTextAsync(html), await File.ReadAllTextAsync(junit) })
            {
                Assert.DoesNotContain(canary, text, StringComparison.Ordinal);
                Assert.DoesNotContain(DisplaySanitiser.SanitiseForDisplay(canary)!, text, StringComparison.Ordinal);
                Assert.DoesNotContain(literal.Trim('"'), text, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task KeptTopology_SecurityAndScriptUseOneLocalLedger_WhileSessionRetainsProbeValues()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        using var password = new EnvironmentValue("scenario-key-passphrase");
        using var probe = new EnvironmentValue("earlier-probe-only-value");
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, password.Value);
        var aggregate = new ResolvedSecretLedger();
        using var probeScope = ScenarioRunner.CreateSecretAccessorScope(aggregate);
        _ = probeScope.Accessor.Resolve(probe.Reference);
        var yaml = $$"""
            environment:
              services:
                api:
                  image: acme/payments:1.2
                  ports: ["8443"]
                  security:
                    profile: mtls
                    endpoint: "8443"
                    caCert: {{TestCertificateAuthority.CaFileName}}
                    clientCert: {{TestCertificateAuthority.ClientCertFileName}}
                    clientKey: {{TestCertificateAuthority.ClientKeyFileName}}
                    clientKeyPassword: "${secret:{{password.Reference}}}"
            steps:
              - id: identity
                type: script.csharp
                code: |
                  var local = ((Vouchfx.Engine.Abstractions.Secrets.SecretAccessor)Secrets).ResolvedSecrets;
                  if (local.Count != 0) throw new System.Exception("probe value appeared in local ledger");
                  var material = Security.For("api").Certificates;
                  var password = material.ClientKeyPassword.Reveal();
                  if (local.Count != 1 || local.Scrub(password) == password)
                      throw new System.Exception("security accessor used a different ledger");
                  var probe = System.Environment.GetEnvironmentVariable("{{probe.Name}}");
                  if (local.Scrub(probe) != probe) throw new System.Exception("probe value appeared after identity load");
            """;
        var plan = WatchIterationPlan.Create(BuildAst(yaml), yaml, "identity", s_registry,
            "unused-host", bed.SuiteDirectory);
        Assert.False(plan.IsRefused, plan.Diagnostic);
        var output = new StringWriter();
        var verdict = await ScenarioRunner.RunPlannedScenarioAgainstKeptTopologyAsync(
            new EmptyTopology(), new NullScenarioIsolation(), plan, s_registry, output,
            resetAndReseed: false, sharedLedger: aggregate);
        Assert.True(verdict == Verdict.Pass, output.ToString());
        Assert.Equal(2, aggregate.Count);
        Assert.Equal(SecretString.RedactedMarker, aggregate.Scrub(password.Value));
    }

    private static ScenarioAst BuildAst(string yaml) => AstBuilder.Build(YamlDocumentParser.Parse(yaml), s_registry);

    private static string Yaml(EnvironmentValue own, EnvironmentValue probe, EnvironmentValue other, bool report)
    {
        var code = $$"""
            var ledger = ((Vouchfx.Engine.Abstractions.Secrets.SecretAccessor)Secrets).ResolvedSecrets;
            var probe = System.Environment.GetEnvironmentVariable("{{probe.Name}}");
            var other = System.Environment.GetEnvironmentVariable("{{other.Name}}");
            if (ledger.Count != 0 || ledger.Scrub(probe) != probe || ledger.Scrub(other) != other)
                throw new System.Exception("another scope appeared in the public ledger");
            var own = Secrets.Resolve("{{own.Reference}}").Reveal();
            if (ledger.Count != 1 || ledger.Scrub(own) == own)
                throw new System.Exception("own resolution missing from local ledger");
            """;
        var yaml = "environment:\n  source: { driver: ledger-probe, config: unused.fixture }\nsteps:\n"
            + "  - id: check\n    type: script.csharp\n    code: |\n"
            + string.Join("\n", code.Split('\n').Select(line => "      " + line)) + "\n";
        if (report)
        {
            yaml += $$"""
                  - id: report
                    type: script.csharp
                    code: |
                      throw new System.Exception(System.Text.Json.JsonSerializer.Serialize(
                          System.Environment.GetEnvironmentVariable("{{probe.Name}}") + " / " +
                          System.Environment.GetEnvironmentVariable("{{other.Name}}")));
                """ + "\n";
        }
        return yaml;
    }

    private sealed class CountingResolver : ISecretResolver, IDisposable
    {
        private readonly EnvironmentSecretResolver _inner = new();
        internal int Calls { get; private set; }
        internal int Disposals { get; private set; }
        public string Source => _inner.Source;
        public SecretString Resolve(string path) { Calls++; return _inner.Resolve(path); }
        public void Dispose() => Disposals++;
    }

    private sealed class EnvironmentValue : IDisposable
    {
        internal EnvironmentValue(string value)
        {
            Name = "VOUCHFX_LEDGER395_" + Guid.NewGuid().ToString("N");
            Value = value;
            Environment.SetEnvironmentVariable(Name, value);
        }
        internal string Name { get; }
        internal string Value { get; }
        internal string Reference => "env/" + Name;
        public void Dispose() => Environment.SetEnvironmentVariable(Name, null);
    }

    // Resolves one topology-only secret in the same aggregate accessor the real probe uses.
    // No broker or certificate-confirmation claim is made by this Docker-free driver.
    private sealed class ProbeDriver : IEnvironmentDriver
    {
        private readonly string _reference;
        internal ProbeDriver(string reference) => _reference = reference;
        internal int Starts { get; private set; }
        public string Id => "ledger-probe";
        public EnvironmentPlan Bind(EnvironmentSourceSpec source, string suiteDirectory) => new(
            new EnvironmentDeclarations(new Dictionary<string, DeclaredServiceInfo>(), new Dictionary<string, string>()),
            "ledger395", EnvironmentStatePolicy.Inline, (context, token) =>
            {
                token.ThrowIfCancellationRequested();
                Starts++;
                _ = context.Secrets.Resolve(_reference);
                return Task.FromResult<IKeptTopology>(new EmptyTopology());
            });
    }

    private sealed class EmptyTopology : IKeptTopology
    {
        public IReadOnlyList<SecurityConfirmation> SecurityConfirmations => Array.Empty<SecurityConfirmation>();
        public IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices => Array.Empty<EndpointSelectionNotice>();
        public IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices => Array.Empty<EndpointTrustNotice>();
        public IReadOnlyDictionary<string, object> DiscoveredServices { get; } = new Dictionary<string, object>();
        public IReadOnlyList<string> DependencyNames => Array.Empty<string>();
        public IReadOnlyDictionary<string, string> DependencyTypes { get; } = new Dictionary<string, string>();
        public Task ReseedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
