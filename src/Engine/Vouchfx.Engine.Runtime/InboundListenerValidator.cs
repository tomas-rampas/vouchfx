using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Runtime;

/// <summary>Preflights declared server material without opening private keys.</summary>
internal static class InboundListenerValidator
{
    internal static ValidationFailure? ValidateMaterial(ScenarioAst ast, string suiteDirectory)
    {
        if (ast.Listeners is null)
        {
            return null;
        }

        var baseDirectory = Path.GetFullPath(suiteDirectory);
        foreach (var (name, listener) in ast.Listeners)
        {
            var security = listener.Security;
            var prefix = $"listeners.{name}.security";
            foreach (var (field, value) in new[]
            {
                ("serverCert", security.ServerCert),
                ("serverKey", security.ServerKey),
                ("clientCaCert", security.ClientCaCert),
            })
            {
                if (value is null)
                {
                    continue;
                }

                if (value.Contains(SecretReference.Sigil, StringComparison.Ordinal))
                {
                    return Failure($"{prefix}.{field}: a certificate path must name a file, not a secret reference.");
                }

                var error = SecurityArtifactPath.TryResolveContained(value, baseDirectory, out var resolved);
                if (error is not null)
                {
                    return Failure($"{prefix}.{field}: {error}");
                }

                if (!File.Exists(resolved))
                {
                    return Failure($"{prefix}.{field}: declared file '{value}' is missing or unreadable.");
                }
            }

            if (security.ServerKeyPassword is { } password
                && (!SecretReference.TryParse(password, out _) || password.Length == 0))
            {
                return Failure($"{prefix}.serverKeyPassword: expected one whole secret reference.");
            }

            try
            {
                SecurityArtifactPath.TryResolveContained(security.ServerCert, baseDirectory, out var certPath);
                using var cert = X509Certificate2.CreateFromPem(File.ReadAllText(certPath!));
                if (DateTime.UtcNow < cert.NotBefore.ToUniversalTime()
                    || DateTime.UtcNow > cert.NotAfter.ToUniversalTime())
                {
                    return Failure($"{prefix}.serverCert: certificate is not currently valid.");
                }

                var san = cert.Extensions["2.5.29.17"];
                if (san is null)
                {
                    return Failure($"{prefix}.serverCert: certificate is missing SAN for 127.0.0.1 and host.docker.internal.");
                }

                var names = new AsnReader(san.RawData, AsnEncodingRules.DER).ReadSequence();
                var hasIp = false;
                var hasDns = false;
                while (names.HasData)
                {
                    var tag = names.PeekTag();
                    if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 7)))
                    {
                        hasIp |= names.ReadOctetString(tag).AsSpan().SequenceEqual(IPAddress.Loopback.GetAddressBytes());
                    }
                    else if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 2)))
                    {
                        hasDns |= string.Equals(names.ReadCharacterString(UniversalTagNumber.IA5String, tag),
                            "host.docker.internal", StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        names.ReadEncodedValue();
                    }
                }

                if (!hasIp || !hasDns)
                {
                    return Failure($"{prefix}.serverCert: certificate is missing SAN for {(hasIp ? "host.docker.internal" : "127.0.0.1")}.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or AsnContentException or ArgumentException)
            {
                return Failure($"{prefix}.serverCert: declared certificate could not be read as PEM with valid SANs.");
            }
        }

        return null;
    }

    internal static ValidationFailure? ValidateBindings(
        ScenarioAst ast, IReadOnlyList<HostResourcePlanEntry> hostResources)
    {
        foreach (var step in ast.Steps)
        {
            if (step.CanonicalType is not ("webhook-listen.http" or "trace-expect.otlp")
                || !step.RawNode.Children.TryGetValue(new YamlScalarNode("match"), out var matchNode)
                || matchNode is not YamlMappingNode match
                || !match.Children.ContainsKey(new YamlScalarNode("peerCertificateSha256")))
            {
                continue;
            }

            var nameField = step.CanonicalType == "webhook-listen.http" ? "listener" : "receiver";
            var name = step.RawNode.Children.TryGetValue(new YamlScalarNode(nameField), out var nameNode)
                ? (nameNode as YamlScalarNode)?.Value : null;
            if (name is null || ast.Listeners is null
                || !ast.Listeners.TryGetValue(name, out var secured)
                || secured.Security.Profile != "mtls")
            {
                return Failure($"steps.{step.Id}.match.peerCertificateSha256: requires a named listener with profile: mtls.");
            }
        }

        if (ast.Listeners is null)
        {
            return null;
        }

        foreach (var (name, listener) in ast.Listeners)
        {
            if (!hostResources.Any(entry => string.Equals(entry.Requirement.VarName, name, StringComparison.Ordinal)
                && string.Equals(entry.Requirement.Kind, listener.Kind, StringComparison.Ordinal)))
            {
                return Failure($"listeners.{name}.kind: no matching step-contributed {listener.Kind} host resource.");
            }
        }

        return null;
    }

    private static ValidationFailure Failure(string message) => new(message) { IsSecurityPreflight = true };
}
