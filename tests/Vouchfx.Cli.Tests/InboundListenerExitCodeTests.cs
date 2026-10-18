using Vouchfx.Cli;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.WebhookListen.Http;
using Xunit;

namespace Vouchfx.Cli.Tests;

#pragma warning disable CA1861 // Each row supplies a small declaration-ordered scenario table.

public sealed class InboundListenerExitCodeTests
{
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
