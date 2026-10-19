using Vouchfx.Cli;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Vouchfx.Steps.WebhookListen.Http;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Cli.Tests;

#pragma warning disable CA1861 // Each row supplies a small declaration-ordered scenario table.

public sealed class InboundListenerExitCodeTests
{
    [Theory]
    [InlineData("compilation", false)]
    [InlineData("compilation", true)]
    [InlineData("topology", false)]
    [InlineData("topology", true)]
    public async Task SharedSuiteCompilationAndTypedTopologyDoors_RetainCensusAndExitNonzero(string door, bool reverse)
    {
        using var bed = HostedHttpTlsCertificateBed.CreateClientChain("supplied");
        var providers = new[] { typeof(WebhookListenHttpProvider).Assembly, typeof(ScriptCsharpProvider).Assembly };
        var registry = StepKindRegistry.BuildAndFreeze(providers);
        File.WriteAllText(Path.Combine(bed.SuiteDirectory, "oversized.csx"), new string(' ', 65537));
        const string yaml = """
            environment:
              source: { driver: early-door, config: unused.yaml }
            listeners:
              callbacks:
                kind: webhook-listener
                security:
                  profile: mtls
                  serverCert: server.pem
                  serverKey: server-key.pem
                  clientCaCert: ca.pem
            steps:
              - id: callback
                type: webhook-listen.http
                listener: callbacks
                match: { path: /event }
            """;
        var tested = door == "compilation" ? yaml + "\n  - id: oversized\n    type: script.csharp\n    file: oversized.csx\n" : yaml;
        Assert.Empty(DocumentValidator.Validate(tested, registry).Errors); // neither case is a schema refusal.
        var secured = AstBuilder.Build(YamlDocumentParser.Parse(tested), registry);
        var unsecuredYaml = "steps:\n  - id: plain\n    type: script.csharp\n    code: return;\nunknown: true\n";
        var unsecured = AstBuilder.Build(YamlDocumentParser.Parse(unsecuredYaml), registry);
        var driver = new TypedTopologyFailureDriver();
        var drivers = new EnvironmentDriverRegistry(new[] { driver });
        // The schema door gives the same declaration's complete canonical identity before
        // acquisition. Compare every identity field without duplicating its digest algorithm.
        var baselineYaml = yaml + "\nunknown: true\n";
        var baseline = await ScenarioRunner.RunSuiteAsync(new[] { AstBuilder.Build(YamlDocumentParser.Parse(baselineYaml), registry) },
            new[] { "baseline" }, new[] { baselineYaml }, providers, null, new StringWriter(),
            seedBaseDirectory: bed.SuiteDirectory, environmentDrivers: drivers);
        Assert.Equal(0, driver.Starts);
        var output = new StringWriter();
        var result = await ScenarioRunner.RunSuiteAsync(reverse ? new[] { unsecured, secured } : new[] { secured, unsecured },
            new[] { "first", "second" }, reverse ? new[] { unsecuredYaml, tested } : new[] { tested, unsecuredYaml },
            providers, null, output, seedBaseDirectory: bed.SuiteDirectory, environmentDrivers: drivers);
        Assert.Equal(door == "topology" ? 1 : 0, driver.Starts);
        Assert.Equal(baseline.Assurance.Declared, result.Assurance.Declared);
        Assert.Single(result.Assurance.Declared);
        Assert.Empty(result.Assurance.Confirmed);
        Assert.True(result.Assurance.Unconfirmed);
        Assert.NotEqual(ExitCodes.Success, ExitCodes.FromVerdict(result.Verdict, false, false, result.Assurance));
        Assert.DoesNotContain("step-started", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(door == "compilation" ? "65536" : "typed-topology-canary", output.ToString(), StringComparison.Ordinal);
    }

    private sealed class TypedTopologyFailureDriver : IEnvironmentDriver
    {
        public string Id => "early-door";
        public int Starts { get; private set; }
        public EnvironmentPlan Bind(EnvironmentSourceSpec source, string suiteDirectory) => new(
            new EnvironmentDeclarations(new Dictionary<string, DeclaredServiceInfo>(), new Dictionary<string, string>()),
            "early-door", EnvironmentStatePolicy.Inline, (_, _) =>
            {
                Starts++;
                throw new OrchestrationException(new OrchestrationErrorInfo(
                    OrchestrationErrorKind.Provision, "source", null, null, "typed-topology-canary"));
            });
    }

    [Theory]
    [InlineData("schema", false)]
    [InlineData("schema", true)]
    [InlineData("material", false)]
    [InlineData("material", true)]
    [InlineData("unbuilt", false)]
    [InlineData("unbuilt", true)]
    [InlineData("malformed", false)]
    [InlineData("malformed", true)]
    public async Task RunnerEarlyDoor_UnconfirmedInboundNeverExitsZero(string door, bool reverse)
    {
        var providers = new[] { typeof(WebhookListenHttpProvider).Assembly };
        var registry = StepKindRegistry.BuildAndFreeze(providers);
        const string listener = """
            listeners:
              callbacks:
                kind: webhook-listener
                security:
                  profile: mtls
                  serverCert: missing-cert.pem
                  serverKey: missing-key.pem
                  clientCaCert: missing-ca.pem
            """;
        const string steps = """
            steps:
              - id: callback
                type: webhook-listen.http
                listener: callbacks
                match: { path: /event }
            """;
        var yaml = listener + "\n" + steps;
        if (door == "schema") yaml += "\nunknown: true\n";
        if (door == "malformed") yaml = "listeners:\n  callbacks:\n    kind: webhook-listener\n    security: mtls\n" + steps;
        var document = YamlDocumentParser.Parse(yaml);
        SuiteResult result;
        if (door is "unbuilt" or "malformed")
        {
            result = await ScenarioRunner.RunSuiteAsync(Array.Empty<ScenarioAst>(), Array.Empty<string>(), Array.Empty<string>(),
                providers, null, new StringWriter(), unbuiltDocuments: new[] { new UnbuiltDocument(yaml, document) });
        }
        else
        {
            var secured = AstBuilder.Build(document, registry);
            var unsecuredYaml = steps + "\nunknown: true\n";
            var unsecured = AstBuilder.Build(YamlDocumentParser.Parse(unsecuredYaml), registry);
            result = await ScenarioRunner.RunSuiteAsync(reverse ? new[] { unsecured, secured } : new[] { secured, unsecured },
                new[] { "first", "second" }, reverse ? new[] { unsecuredYaml, yaml } : new[] { yaml, unsecuredYaml },
                providers, null, new StringWriter());
        }
        Assert.True(result.Assurance.Unconfirmed);
        Assert.Empty(result.Assurance.Confirmed);
        Assert.NotEqual(ExitCodes.Success, ExitCodes.FromVerdict(result.Verdict, false, false, result.Assurance));
        if (door != "malformed")
        {
            var declaration = Assert.Single(result.Assurance.Declared);
            Assert.Equal(SecurityDirection.InboundListener, declaration.Direction);
            Assert.Equal("callbacks", declaration.Name);
        }
    }
}
