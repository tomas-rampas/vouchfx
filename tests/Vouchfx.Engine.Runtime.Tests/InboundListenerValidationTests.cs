using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.WebhookListen.Http;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class InboundListenerValidationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vouchfx-inbound-validation-" + Guid.NewGuid().ToString("n"));

    public InboundListenerValidationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Parser_BindsTopLevelListenerBesideSourceEnvironment()
    {
        var document = YamlDocumentParser.Parse("""
            environment:
              source:
                driver: existing
                config: source.yaml
            listeners:
              callbacks:
                kind: webhook-listener
                security:
                  profile: mtls
                  serverCert: certs/server.pem
                  serverKey: certs/key.pem
                  clientCaCert: certs/client-ca.pem
            steps:
              - id: callback
                type: webhook-listen.http
                listener: callbacks
                match: { path: /event }
            """);

        Assert.Equal("existing", document.Environment?.Source?.Driver);
        Assert.Equal("mtls", document.Listeners!["callbacks"].Security.Profile);
        Assert.Equal("certs/client-ca.pem", document.Listeners["callbacks"].Security.ClientCaCert);
    }

    [Theory]
    [InlineData("kind: unknown\n    security: { profile: tls, serverCert: a, serverKey: b }")]
    [InlineData("kind: webhook-listener\n    security: no")]
    [InlineData("kind: webhook-listener\n    security: { profile: tls, serverCert: a, serverKey: b, clientCaCert: c }")]
    [InlineData("kind: webhook-listener\n    security: { profile: mtls, serverCert: a, serverKey: b }")]
    [InlineData("kind: webhook-listener\n    security: { profile: tls, serverCert: a, serverKey: b, unknown: c }")]
    public void Schema_RejectsInvalidListenerPolicy(string entry)
    {
        var yaml = "listeners:\n  callbacks:\n    " + entry + "\nsteps:\n  - id: callback\n    type: webhook-listen.http\n    listener: callbacks\n    match: { path: /event }\n";
        var registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(WebhookListenHttpProvider).Assembly });

        Assert.NotEmpty(DocumentValidator.Validate(yaml, registry).Errors);
    }

    [Fact]
    public void MaterialPreflight_AcceptsBothRequiredNamesAndRejectsMissingFileOrTraversal()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=listener", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("host.docker.internal");
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(Path.Combine(_directory, "server.pem"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(_directory, "key.pem"), rsa.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(_directory, "ca.pem"), certificate.ExportCertificatePem());

        Assert.Null(InboundListenerValidator.ValidateMaterial(Ast("server.pem", "key.pem", "ca.pem"), _directory));
        Assert.Contains("listeners.callbacks.security.serverKey", InboundListenerValidator.ValidateMaterial(
            Ast("server.pem", "missing.pem", "ca.pem"), _directory)!.Message);
        Assert.Contains("listeners.callbacks.security.serverCert", InboundListenerValidator.ValidateMaterial(
            Ast("../escape.pem", "key.pem", "ca.pem"), _directory)!.Message);
    }

    [Fact]
    public void Binding_RequiresMatchingContributedNameAndKind()
    {
        var ast = Ast("server.pem", "key.pem", "ca.pem");
        Assert.Contains("listeners.callbacks.kind", InboundListenerValidator.ValidateBindings(
            ast, Array.Empty<HostResourcePlanEntry>())!.Message);
        Assert.Null(InboundListenerValidator.ValidateBindings(ast,
            new[] { new HostResourcePlanEntry("callback", new HostResourceRequirement("webhook-listener", "callbacks")) }));
    }

    [Fact]
    public void Pipeline_RefusesMissingMaterialBeforeTopologyWithSecurityProvenance()
    {
        var yaml = """
            listeners:
              callbacks:
                kind: webhook-listener
                security:
                  profile: mtls
                  serverCert: certs/missing.pem
                  serverKey: certs/missing-key.pem
                  clientCaCert: certs/missing-ca.pem
            steps:
              - id: callback
                type: webhook-listen.http
                listener: callbacks
                match: { path: /event }
            """;
        var registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(WebhookListenHttpProvider).Assembly });
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry);

        var result = ProviderPipeline.Compile(ast, registry, "InboundValidation", _directory);

        Assert.NotNull(result.Failure);
        Assert.True(result.Failure.IsSecurityPreflight);
        Assert.Contains("listeners.callbacks.security.serverCert", result.Failure.Message);
        Assert.DoesNotContain(_directory, result.Failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Binding_FingerprintCriterionRequiresMtls()
    {
        var yaml = """
            listeners:
              callbacks:
                kind: webhook-listener
                security:
                  profile: tls
                  serverCert: server.pem
                  serverKey: key.pem
            steps:
              - id: callback
                type: webhook-listen.http
                listener: callbacks
                match:
                  peerCertificateSha256: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
            """;
        var registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(WebhookListenHttpProvider).Assembly });
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry);

        var result = InboundListenerValidator.ValidateBindings(ast,
            new[] { new HostResourcePlanEntry("callback", new HostResourceRequirement("webhook-listener", "callbacks")) });

        Assert.Contains("steps.callback.match.peerCertificateSha256", result!.Message);
    }

    private static ScenarioAst Ast(string certificate, string key, string ca) =>
        new(null, null, new Dictionary<string, string>(), Array.Empty<StepNode>())
        {
            Listeners = new Dictionary<string, InboundListenerSpec>
            {
                ["callbacks"] = new("webhook-listener", new("mtls", certificate, key, null, ca)),
            },
        };
}
