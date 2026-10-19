using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.TraceExpect.Otlp;
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

        Assert.Contains(DocumentValidator.Validate(yaml, registry).Errors, error =>
            error.InstanceLocation.StartsWith("/listeners/callbacks", StringComparison.Ordinal));
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

    [Fact]
    public void SourceEnvironmentAndListeners_ValidateAndCompileTogether()
    {
        WriteMaterial();
        File.WriteAllText(Path.Combine(_directory, "compose.yaml"), "version: 1\nfiles: [native.yaml]\nservices:\n  api: { type: tcp, service: api, port: 9000, readiness: { type: tcp } }\n");
        File.WriteAllText(Path.Combine(_directory, "native.yaml"), "services:\n  api: { image: example/api:1 }\n");
        var yaml = "environment:\n  source: { driver: compose, config: compose.yaml }\n" + ListenerYaml();
        var registry = Registry();
        Assert.True(DocumentValidator.Validate(yaml, registry).IsValid);
        var result = ProviderPipeline.Compile(AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry),
            registry, "SourceAndListeners", _directory);
        Assert.Null(result.Failure);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("TLS")]
    public void Schema_UnknownProfileHasLocatedDiagnostic(string profile)
    {
        var result = DocumentValidator.Validate(ListenerYaml().Replace("profile: mtls", "profile: " + profile), Registry());
        Assert.Contains(result.Errors, error => error.InstanceLocation == "/listeners/callbacks/security/profile");
    }

    [Fact]
    public void DuplicateListenerName_IsRejectedAtItsSourceLine()
    {
        var yaml = ListenerYaml().Replace("steps:", "  callbacks: { kind: otlp-receiver, security: {} }\nsteps:");
        var failure = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("callbacks", failure.Message);
        Assert.True(failure.Line > 0);
    }

    [Theory]
    [InlineData(false, false, "127.0.0.1")]
    [InlineData(true, false, "host.docker.internal")]
    [InlineData(false, true, "127.0.0.1")]
    public void Pipeline_MissingRequiredSanIsLocated(bool ip, bool dns, string missingName)
    {
        WriteMaterial(ip, dns);
        var failure = CompileFailure(ListenerYaml());
        Assert.Contains("listeners.callbacks.security.serverCert", failure.Message);
        Assert.Contains(missingName, failure.Message);
        Assert.True(failure.IsSecurityPreflight);
    }

    [Theory]
    [InlineData("serverCert", "../outside.pem")]
    [InlineData("serverKey", "../outside.pem")]
    [InlineData("clientCaCert", "../outside.pem")]
    [InlineData("serverCert", "${secret:env/CERT}")]
    [InlineData("serverKey", "${secret:env/KEY}")]
    [InlineData("clientCaCert", "${secret:env/CA}")]
    [InlineData("serverCert", "/absolute.pem")]
    [InlineData("serverKey", "/absolute.pem")]
    [InlineData("clientCaCert", "/absolute.pem")]
    public void Pipeline_InvalidMaterialPathIsLocatedAndSanitised(string field, string path)
    {
        WriteMaterial();
        var original = field == "serverCert" ? "server.pem" : field == "serverKey" ? "key.pem" : "ca.pem";
        var failure = CompileFailure(ListenerYaml().Replace(field + ": " + original, field + ": '" + path + "'"));
        Assert.Contains("listeners.callbacks.security." + field, failure.Message);
        Assert.DoesNotContain(_directory, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("key.pem", "serverKey")]
    [InlineData("ca.pem", "clientCaCert")]
    public void Pipeline_ExclusivelyLockedMaterialIsRejectedBeforeTopology(string file, string field)
    {
        WriteMaterial();
        using var locked = new FileStream(Path.Combine(_directory, file), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var failure = CompileFailure(ListenerYaml());
        Assert.Contains("listeners.callbacks.security." + field, failure.Message);
        Assert.Contains("missing or unreadable", failure.Message);
        Assert.DoesNotContain(_directory, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(failure.IsSecurityPreflight);
    }

    [Theory]
    [InlineData("literal-password")]
    [InlineData("prefix-${secret:env/PASSWORD}")]
    [InlineData("${secret:bad.source/PASSWORD}")]
    public void Pipeline_PasswordMustBeOneWholeSecretReference(string reference)
    {
        WriteMaterial();
        var yaml = ListenerYaml().Replace("      serverKey: key.pem",
            "      serverKey: key.pem\n      serverKeyPassword: '" + reference + "'");
        var failure = CompileFailure(yaml);
        Assert.Contains("listeners.callbacks.security.serverKeyPassword", failure.Message);
        Assert.DoesNotContain(reference, failure.Message, StringComparison.Ordinal);
        Assert.True(failure.IsSecurityPreflight);
    }

    [Fact]
    public void MaterialPreflight_ReadableInTreeLinkMayTargetOutsideSuite()
    {
        WriteMaterial();
        var outside = Path.Combine(Path.GetTempPath(), "vouchfx-inbound-outside-" + Guid.NewGuid().ToString("n") + ".pem");
        var link = Path.Combine(_directory, "linked-key.pem");
        try
        {
            File.Copy(Path.Combine(_directory, "key.pem"), outside);
            try
            {
                File.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException
                || ex is IOException && OperatingSystem.IsWindows())
            {
                // Windows may lack Developer Mode or SeCreateSymbolicLinkPrivilege.
                return;
            }

            Assert.NotNull(new FileInfo(link).LinkTarget);
            Assert.Null(InboundListenerValidator.ValidateMaterial(Ast("server.pem", "linked-key.pem", "ca.pem"), _directory));
            File.Delete(outside);
            var failure = InboundListenerValidator.ValidateMaterial(Ast("server.pem", "linked-key.pem", "ca.pem"), _directory);
            Assert.Contains("listeners.callbacks.security.serverKey", failure!.Message);
            Assert.DoesNotContain(outside, failure.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(link);
            File.Delete(outside);
        }
    }

    [Theory]
    [InlineData("webhook-listen.http", "listener")]
    [InlineData("trace-expect.otlp", "receiver")]
    public void Binding_PlaintextFingerprintCriterionIsLocated(string type, string nameField)
    {
        var yaml = $"steps:\n  - id: incoming\n    type: {type}\n    {nameField}: callbacks\n    match: {{ peerCertificateSha256: '{new string('a', 64)}' }}\n";
        if (type == "trace-expect.otlp")
        {
            yaml = yaml.Replace("match: {", "match: { traceId: abc,");
        }
        var failure = CompileFailure(yaml);
        Assert.Contains("steps.incoming.match.peerCertificateSha256", failure.Message);
        Assert.True(failure.IsSecurityPreflight);
    }

    [Theory]
    [InlineData("cross-kind", "more than one kind")]
    [InlineData("service", "collides with")]
    [InlineData("unused", "listeners.callbacks.kind")]
    [InlineData("kind", "listeners.callbacks.kind")]
    public void Pipeline_SecuredResourceCollisionOrMismatchIsRejected(string defect, string diagnostic)
    {
        WriteMaterial();
        var yaml = ListenerYaml();
        if (defect == "cross-kind")
        {
            yaml += "  - id: traces\n    type: trace-expect.otlp\n    receiver: callbacks\n    match: { traceId: abc }\n";
        }
        else if (defect == "service")
        {
            yaml = "environment:\n  services:\n    callbacks: { image: example/callbacks:1 }\n" + yaml;
        }
        else if (defect == "unused")
        {
            yaml = yaml.Replace("listener: callbacks", "listener: other");
        }
        else
        {
            yaml = yaml.Replace("kind: webhook-listener", "kind: otlp-receiver");
        }
        var failure = CompileFailure(yaml);
        Assert.Contains(diagnostic, failure.Message);
        Assert.Contains("callbacks", failure.Message);
        Assert.DoesNotContain(_directory, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private ValidationFailure CompileFailure(string yaml)
    {
        var registry = Registry();
        var result = ProviderPipeline.Compile(AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry),
            registry, "InboundNegative", _directory);
        Assert.NotNull(result.Failure);
        return result.Failure;
    }

    private static StepKindRegistry Registry() => StepKindRegistry.BuildAndFreeze(new[]
    {
        typeof(WebhookListenHttpProvider).Assembly, typeof(TraceExpectOtlpProvider).Assembly,
    });

    private static string ListenerYaml() => """
        listeners:
          callbacks:
            kind: webhook-listener
            security:
              profile: mtls
              serverCert: server.pem
              serverKey: key.pem
              clientCaCert: ca.pem
        steps:
          - id: callback
            type: webhook-listen.http
            listener: callbacks
            match: { path: /event }
        """ + "\n";

    private void WriteMaterial(bool ip = true, bool dns = true)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=host.docker.internal", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (ip || dns)
        {
            var san = new SubjectAlternativeNameBuilder();
            if (ip)
            {
                san.AddIpAddress(IPAddress.Loopback);
            }
            if (dns)
            {
                san.AddDnsName("host.docker.internal");
            }
            request.CertificateExtensions.Add(san.Build());
        }
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(Path.Combine(_directory, "server.pem"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(_directory, "key.pem"), key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(_directory, "ca.pem"), certificate.ExportCertificatePem());
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
