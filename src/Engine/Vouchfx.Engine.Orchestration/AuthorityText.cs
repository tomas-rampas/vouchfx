// Shared host:port rendering for orchestration diagnostics (m1 fix round five / m3 fix round six).
//
// WHY THIS EXISTS AS ITS OWN TYPE. Two places in this assembly hold a bracket-free host and a port
// and render them back into a readable authority for a human: SecuredEndpointProbe's declared-
// versus-observed messages, and EnvironmentMapper's TCP health-check diagnostics. Fix round five
// fixed the first by adding a private FormatAuthority to SecuredEndpointProbe and left the second
// interpolating "{host}:{port}" raw, which is the same defect one file over. Both callers live in
// THIS assembly and THIS namespace (measured: src/Engine/Vouchfx.Engine.Orchestration/
// SecuredEndpointProbe.cs and .../EnvironmentMapper.cs), so hoisting the rule costs no project
// reference and no public surface — the alternative the brief allowed, duplicating the three-line
// bracket rule with a comment naming its origin, would have shipped the branch with two copies of a
// rule whose whole point is that there be one.

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// Renders a resolved host and port back into a readable authority for diagnostics.
/// </summary>
internal static class AuthorityText
{
    /// <summary>
    /// Renders <paramref name="host"/> and <paramref name="port"/> as an authority, bracketing an
    /// IPv6 literal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The callers deliberately hold the BRACKET-FREE host, because that is the form
    /// <c>TcpClient.ConnectAsync</c> can use — but interpolating it as <c>{host}:{port}</c> renders
    /// <c>[::1]:9093</c> as <c>::1:9093</c>: not a parseable authority, and ambiguous about where
    /// the address ends and the port begins.
    /// </para>
    /// <para>
    /// The discriminator is a colon in the host, which is exactly RFC 3986's own rule: a colon
    /// cannot appear in a registered name or an IPv4 literal, so it identifies an IPv6 literal and
    /// nothing else. Brackets are not re-added by <c>Uri</c> here because the host has typically
    /// already left the parser as <c>DnsSafeHost</c>.
    /// </para>
    /// <para>
    /// Display only. Nothing connects to this string; it is the human-readable rendering of an
    /// address the caller has already resolved.
    /// </para>
    /// </remarks>
    /// <param name="host">The bracket-free host (a registered name, IPv4 literal, or IPv6 literal).</param>
    /// <param name="port">The port.</param>
    /// <returns><c>host:port</c>, or <c>[host]:port</c> when <paramref name="host"/> is an IPv6 literal.</returns>
    public static string Format(string host, int port) =>
        host.Contains(':', StringComparison.Ordinal)
            ? FormattableString.Invariant($"[{host}]:{port}")
            : FormattableString.Invariant($"{host}:{port}");
}
