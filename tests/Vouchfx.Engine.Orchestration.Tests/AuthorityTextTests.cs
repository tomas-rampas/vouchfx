// Non-docker unit tests for AuthorityText.Format (authenticated-infrastructure-mtls, slice E).
//
// AuthorityText is the ONE spelling of the host:port rendering rule shared by
// SecuredEndpointProbe's declared-versus-observed messages and EnvironmentMapper's TCP
// health-check diagnostics. Until fix round seven it had no direct test at all: its behaviour was
// observed only through SecuredEndpointProbeTests' end-to-end message assertions, which exercise
// the probe caller alone. These rows pin the rule itself, including the m5 idempotency case that
// no caller is currently proven to reach.
//
// Pure string formatting — no Docker, no Aspire — so it runs in the standard unit-CI gate
// (dotnet test --filter "requires!=docker").

using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Unit tests for <see cref="AuthorityText.Format"/>.
/// </summary>
public sealed class AuthorityTextTests
{
    /// <summary>
    /// A host with no colon is never bracketed — a registered name or an IPv4 literal.
    /// </summary>
    /// <remarks>
    /// The colon is RFC 3986's own discriminator: it cannot appear in a registered name or an
    /// IPv4 literal, so its absence is sufficient to rule out an IPv6 literal.
    /// </remarks>
    [Theory]
    [InlineData("localhost", 8080, "localhost:8080")]
    [InlineData("api", 443, "api:443")]
    [InlineData("127.0.0.1", 5432, "127.0.0.1:5432")]
    [InlineData("host.docker.internal", 9093, "host.docker.internal:9093")]
    public void Format_HostWithoutColon_IsNotBracketed(string host, int port, string expected) =>
        Assert.Equal(expected, AuthorityText.Format(host, port));

    /// <summary>
    /// A bracket-free IPv6 literal is bracketed, so the rendering is a parseable authority rather
    /// than the ambiguous <c>::1:9093</c>.
    /// </summary>
    [Theory]
    [InlineData("::1", 9093, "[::1]:9093")]
    [InlineData("2001:db8::1", 443, "[2001:db8::1]:443")]
    [InlineData("fe80::1ff:fe23:4567:890a", 8443, "[fe80::1ff:fe23:4567:890a]:8443")]
    public void Format_BracketFreeIpv6_IsBracketed(string host, int port, string expected) =>
        Assert.Equal(expected, AuthorityText.Format(host, port));

    /// <summary>
    /// m5 (gatekeeper, fix round seven): an ALREADY-bracketed IPv6 literal is passed through
    /// unchanged rather than bracketed a second time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED BEFORE THE FIX: <c>Format("[::1]", 9093)</c> returned <c>[[::1]]:9093</c> — a
    /// bracketed literal still contains a colon, so the colon test alone fired on it.
    /// </para>
    /// <para>
    /// <strong>Why this row exists when no caller is proven to reach it.</strong> The probe caller
    /// is proven bracket-free by <c>SecuredEndpointProbeTests</c>. The
    /// <c>EnvironmentMapper</c> caller is not: its host is Aspire's
    /// <c>EndpointReference.Host</c>, whose bracket-freeness is inferred from the framework rather
    /// than measured here, and it is the unmeasured caller that could produce the double. The
    /// guard is display-only and costs a length check.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("[::1]", 9093, "[::1]:9093")]
    [InlineData("[2001:db8::1]", 443, "[2001:db8::1]:443")]
    public void Format_AlreadyBracketedIpv6_IsNotDoubleBracketed(string host, int port, string expected) =>
        Assert.Equal(expected, AuthorityText.Format(host, port));

    /// <summary>
    /// Formatting is idempotent over its own IPv6 output: re-formatting a rendered host cannot
    /// accumulate brackets.
    /// </summary>
    /// <remarks>
    /// The property the m5 guard actually buys, stated once rather than per-row. It holds for the
    /// bracket-free and already-bracketed spellings alike.
    /// </remarks>
    [Theory]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("2001:db8::1")]
    [InlineData("localhost")]
    public void Format_IsIdempotentOverItsOwnHostRendering(string host)
    {
        var once = AuthorityText.Format(host, 9093);

        // Strip the ":9093" suffix to recover the rendered host, then render it again.
        var renderedHost = once[..^":9093".Length];

        Assert.Equal(once, AuthorityText.Format(renderedHost, 9093));
    }
}
