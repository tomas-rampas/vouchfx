// Shared host:port rendering for orchestration diagnostics (m1 fix round five / m3 fix round six).
//
// WHY THIS EXISTS AS ITS OWN TYPE. Several places in this assembly hold a bracket-free host and a
// port and render them back into an authority: SecuredEndpointProbe's declared-versus-observed
// messages, EnvironmentMapper's TCP health-check diagnostics, and — since fix round eight —
// EnvironmentMapper.StageServiceEndpoint's Kafka bootstrap value. Fix round five fixed the first by
// adding a private FormatAuthority to SecuredEndpointProbe and left the second interpolating
// "{host}:{port}" raw, which was the same defect one file over; round eight found the third, which
// had been missed because it stages a value rather than printing one. Every caller lives in THIS
// assembly and THIS namespace (measured: src/Engine/Vouchfx.Engine.Orchestration/
// SecuredEndpointProbe.cs and .../EnvironmentMapper.cs), so hoisting the rule costs no project
// reference and no public surface — the alternative the brief allowed, duplicating the three-line
// bracket rule with a comment naming its origin, would have shipped the branch with copies of a
// rule whose whole point is that there be one.
//
// The caller list is not counted here on purpose. It has grown twice.

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
    /// <strong>Idempotent on an already-bracketed host</strong> (m5, gatekeeper, fix round seven).
    /// The colon test alone rendered <c>[::1]</c> as <c>[[::1]]:9093</c>, because a bracketed IPv6
    /// literal still contains a colon. Both callers are believed to hold the bracket-free form —
    /// the probe's is PROVEN so by test, but <c>EnvironmentMapper</c>'s comes from Aspire's
    /// <c>EndpointReference.Host</c> and is only INFERRED — so the one unmeasured caller was the
    /// one that could produce the double. Guarding here costs a length check and removes the
    /// question; it is not a fix for a defect anyone has observed.
    /// </para>
    /// <para>
    /// <strong>No longer display-only</strong> (m1, peer-review critic, fix round eight). Two of
    /// the three callers render a diagnostic, but <c>EnvironmentMapper.StageServiceEndpoint</c>
    /// stages the Kafka bootstrap authority a step's own client then CONNECTS to. That does not
    /// change the rule — the bracketed form is what a bootstrap parser expects, and it is the
    /// unbracketed <c>::1:9093</c> that would be ambiguous to a client as well as to a reader —
    /// but it does mean a change here is a change to behaviour, not merely to a message.
    /// </para>
    /// </remarks>
    /// <param name="host">
    /// The host (a registered name, IPv4 literal, or IPv6 literal). Normally bracket-free; an
    /// already-bracketed IPv6 literal is accepted and passed through unchanged rather than doubled.
    /// </param>
    /// <param name="port">The port.</param>
    /// <returns>
    /// <c>host:port</c>, or <c>[host]:port</c> when <paramref name="host"/> is an IPv6 literal that
    /// is not already bracketed.
    /// </returns>
    public static string Format(string host, int port) =>
        NeedsBrackets(host)
            ? FormattableString.Invariant($"[{host}]:{port}")
            : FormattableString.Invariant($"{host}:{port}");

    /// <summary>
    /// True when <paramref name="host"/> is an IPv6 literal that is not already bracketed.
    /// </summary>
    private static bool NeedsBrackets(string host) =>
        host.Contains(':', StringComparison.Ordinal)
        && !(host.Length >= 2 && host[0] == '[' && host[^1] == ']');
}
