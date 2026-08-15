// Tests for the client-key-password spec, REQ-004: ISecurityCertificateMaterial gains
// 'SecretString? ClientKeyPassword => null' as a DEFAULT-IMPLEMENTED member.
//
// Two properties are pinned, and each exists because losing it breaks something concrete:
//   • the default HOLDS — an implementor that does not override the member returns null, so
//     every existing implementor (the production material plus four test doubles across the
//     Orchestration and Kafka suites) keeps compiling and behaving unchanged; and
//   • the member is genuinely DEFAULT-implemented rather than abstract, asserted by
//     reflection, because retyping it as abstract compiles fine in this assembly and breaks
//     all five implementors elsewhere — a failure this suite should name, not a build error
//     someone bisects.

using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Security;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests.Security;

/// <summary>
/// Verifies the default implementation of
/// <see cref="ISecurityCertificateMaterial.ClientKeyPassword"/> (client-key-password spec,
/// REQ-004).
/// </summary>
public sealed class SecurityCertificateMaterialDefaultsTests
{
    [Fact]
    public void ClientKeyPassword_OnAnImplementorThatDoesNotOverrideIt_IsNull()
    {
        // Deliberately reached through the INTERFACE, not the concrete type: a default
        // interface member is dispatched only through the interface, so a test written
        // against the concrete type would not compile — and would not be testing the
        // default at all.
        ISecurityCertificateMaterial material = new MinimalMaterial();

        Assert.Null(material.ClientKeyPassword);
    }

    [Fact]
    public void ClientKeyPassword_IsDefaultImplemented_NotAbstract()
    {
        var property = typeof(ISecurityCertificateMaterial)
            .GetProperty(nameof(ISecurityCertificateMaterial.ClientKeyPassword));

        Assert.NotNull(property);
        Assert.Equal(typeof(SecretString), property!.PropertyType);

        var getter = property.GetGetMethod();
        Assert.NotNull(getter);
        Assert.False(
            getter!.IsAbstract,
            "ISecurityCertificateMaterial.ClientKeyPassword must stay DEFAULT-implemented "
            + "('=> null'). Making it abstract breaks every existing implementor — the "
            + "production SecurityConfigurationAccessor.SecurityCertificateMaterial and the "
            + "FakeMaterial / MalformedCaMaterial / ChainFaultMaterial / StubMaterial test "
            + "doubles — which is exactly what the default exists to prevent (CLAUDE.md's "
            + "additive-evolution rule for an engine-supplied interface others implement).");
    }

    /// <summary>
    /// The smallest possible implementor: it implements the six members that ARE abstract and
    /// says nothing at all about <see cref="ISecurityCertificateMaterial.ClientKeyPassword"/>
    /// — which is the whole point. It stands in for the four in-tree test doubles that were
    /// deliberately left untouched by REQ-004.
    /// </summary>
    private sealed class MinimalMaterial : ISecurityCertificateMaterial
    {
        public string? CaCertificatePath => null;

        public string? ClientCertificatePath => null;

        public string? ClientKeyPath => null;

        public X509Certificate2? CaCertificate => null;

        public X509Certificate2? ClientCertificate => null;

        public bool TrustsRemoteCertificate(
            X509Certificate2? remoteCertificate,
            X509Chain? platformBuiltChain,
            SslPolicyErrors sslPolicyErrors)
            => sslPolicyErrors == SslPolicyErrors.None;
    }
}
