namespace Vouchfx.Engine.Authoring.Model;

/// <summary>A server-side policy for one step-contributed host resource.</summary>
public sealed record InboundListenerSpec(string Kind, InboundListenerSecuritySpec Security);

/// <summary>Declared PEM material for an engine-owned TLS listener.</summary>
public sealed record InboundListenerSecuritySpec(
    string Profile,
    string ServerCert,
    string ServerKey,
    string? ServerKeyPassword,
    string? ClientCaCert)
{
    /// <summary>Redacts the password reference from diagnostic rendering.</summary>
    public override string ToString() => $"InboundListenerSecuritySpec {{ Profile = {Profile}, ServerKeyPassword = [REDACTED] }}";
}
