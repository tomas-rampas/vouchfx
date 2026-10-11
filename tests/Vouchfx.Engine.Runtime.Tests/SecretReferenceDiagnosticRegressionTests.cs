using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class SecretReferenceDiagnosticRegressionTests : IDisposable
{
    private const string Canary = "private-passphrase-canary";
    private readonly string _root = Directory.CreateTempSubdirectory("vouchfx-secret-diagnostics-").FullName;
    private static readonly StepKindRegistry s_registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(HttpRestProvider).Assembly });
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentInvalidFields_RemainSeparateAndLocatedEvenWithAPipelineRefusal(bool missingCertificate)
    {
        File.WriteAllText(Path.Combine(_root, "client.pem"), "placeholder");
        File.WriteAllText(Path.Combine(_root, "client.key"), "placeholder");
        var yaml = $$"""
            environment:
              services:
                first:
                  image: example/api:1
                  security:
                    profile: mtls
                    endpoint: 8443
                    clientCert: {{(missingCertificate ? "./missing.pem" : "./client.pem")}}
                    clientKey: ./client.key
                    clientKeyPassword: "${secret:nosuchsource/FIRST}"
                second:
                  image: example/api:1
                  security:
                    profile: mtls
                    endpoint: 8443
                    clientCert: ./client.pem
                    clientKey: ./client.key
                    clientKeyPassword: "${secret:nosuchsource/SECOND{{Canary}}${secret:LEAKED}"
            steps:
              - id: first-call
                type: http.rest
                target: first
                method: GET
                path: /health
                headers:
                  Authorization: "Bearer ${secret:nosuchsource/FIRST_TOKEN}"
              - id: second-call
                type: http.rest
                target: second
                method: GET
                path: /health
                headers:
                  Authorization: "Bearer ${secret:nosuchsource/SECOND_TOKEN}"
            """;
        var result = ScenarioValidator.ValidateScenario(yaml, "multiple.e2e.yaml", s_registry, _root);
        Assert.False(result.IsValid);
        Assert.Equal(missingCertificate ? 5 : 4, result.Diagnostics.Count);
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.Equal(ValidationStage.Pipeline, diagnostic.Stage);
            Assert.DoesNotContain("\n", diagnostic.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, diagnostic.Message, StringComparison.Ordinal);
        });
        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Message.StartsWith("step 'first-call'", StringComparison.Ordinal));
        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Message.StartsWith("step 'second-call'", StringComparison.Ordinal));
        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Message.StartsWith(
            "environment.services.first.security.clientKeyPassword", StringComparison.Ordinal));
        Assert.Single(result.Diagnostics, diagnostic => diagnostic.Message.StartsWith(
            "environment.services.second.security.clientKeyPassword", StringComparison.Ordinal));
        Assert.All(result.Diagnostics.Where(diagnostic => diagnostic.Message.StartsWith("step '", StringComparison.Ordinal)),
            diagnostic => Assert.Contains("field '", diagnostic.Message, StringComparison.Ordinal));
        if (missingCertificate)
            Assert.Single(result.Diagnostics, diagnostic => diagnostic.Message.Contains("missing.pem", StringComparison.Ordinal));
    }
}
