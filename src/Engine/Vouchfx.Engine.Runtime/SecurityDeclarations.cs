using System.Security.Cryptography;
using System.Text;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;

namespace Vouchfx.Engine.Runtime;

/// <summary>Direction of a security assertion made by a suite.</summary>
public enum SecurityDirection { OutboundTarget, InboundListener }

/// <summary>Internal assurance identity; a name alone cannot establish confirmation.</summary>
public readonly record struct SecurityDeclarationIdentity(SecurityDirection Direction, string Name, string Digest);

/// <summary>Evidence for exactly one declaration instance.</summary>
public readonly record struct SecurityConfirmationEvidence(SecurityDeclarationIdentity Identity);

internal static class SecurityDeclarations
{
    internal static SecurityDeclarationIdentity Outbound(SecuredTargetIdentity identity) =>
        new(SecurityDirection.OutboundTarget, identity.Name, identity.Digest);

    internal static SecurityDeclarationIdentity Inbound(
        string name, InboundListenerSpec listener, string? suiteDirectory)
    {
        var security = listener.Security;
        var passwordFrame = security.ServerKeyPassword is { } password
            && SecretReference.TryParse(password, out _)
                ? password : security.ServerKeyPassword is null ? "absent" : "present-invalid";
        var frame = string.Join("\0", "inbound-listener", name, listener.Kind, security.Profile,
            security.ServerCert, security.ServerKey, security.ClientCaCert ?? string.Empty,
            passwordFrame, suiteDirectory is null ? string.Empty : Path.GetFullPath(suiteDirectory));
        return new(SecurityDirection.InboundListener, name,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(frame))));
    }

    internal static SecurityDeclarationIdentity[] Enumerate(ScenarioAst ast, string? suiteDirectory)
    {
        var identities = SecuredTargets.Enumerate(ast.Environment)
            .Select(target => Outbound(SecuredTargets.IdentityOf(target))).ToList();
        if (ast.Listeners is not null)
        {
            identities.AddRange(ast.Listeners.Select(entry => Inbound(entry.Key, entry.Value, suiteDirectory)));
        }

        return identities.Distinct().ToArray();
    }

    internal static SecurityDeclarationIdentity[] Enumerate(E2eDocument document, string? suiteDirectory)
    {
        var identities = SecuredTargets.Enumerate(document.Environment)
            .Select(target => Outbound(SecuredTargets.IdentityOf(target))).ToList();
        if (document.Listeners is not null)
        {
            identities.AddRange(document.Listeners.Select(entry => Inbound(entry.Key, entry.Value, suiteDirectory)));
        }

        return identities.Distinct().ToArray();
    }
}
