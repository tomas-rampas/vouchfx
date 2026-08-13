// REQ-014 — the per-target client-configuration accessor
// (authenticated-infrastructure-mtls, slice D).
//
// Non-Docker. Every certificate here is generated in-process (CertificateRequest) and written
// to a temp suite directory, so these tests exercise the REAL load path — including the
// PKCS#12 round trip the accessor performs — against real PEM files, not a stub.
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Secrets.Vault;
using Vouchfx.Engine.Abstractions.Security;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Runtime;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// REQ-014 tests for <c>SecurityConfigurationAccessor</c>: both certificate views usable, an
/// absent <c>caCert</c> staying absent, and the material never reaching <c>Vars</c>.
/// </summary>
public sealed class SecurityConfigurationAccessorTests
{
    /// <summary>
    /// The <c>mtls</c> block a suite would declare against the generated bed: paths RELATIVE to
    /// the suite directory, exactly as REQ-003 requires an author to write them.
    /// </summary>
    private static SecuritySpec MtlsSecurity(string endpoint) =>
        new(
            Profile: "mtls",
            Endpoint: endpoint,
            CaCert: TestCertificateAuthority.CaFileName,
            ClientCert: TestCertificateAuthority.ClientCertFileName,
            ClientKey: TestCertificateAuthority.ClientKeyFileName,
            ServerArtifacts: null);

    private static ScenarioAst AstWithSecuredService(string serviceName, SecuritySpec security) =>
        new(
            Metadata: null,
            Environment: new EnvironmentSpec(
                Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
                {
                    [serviceName] = new ServiceSpec(
                        Image: "acme/payments:1.2",
                        Project: null,
                        ImagePullPolicy: null,
                        HttpPort: null,
                        Env: null)
                    {
                        Security = security,
                    },
                },
                Dependencies: null,
                Seed: null,
                ImageRegistry: null,
                ImagePullPolicy: null),
            Variables: new Dictionary<string, string>(StringComparer.Ordinal),
            Steps: Array.Empty<StepNode>());

    /// <summary>
    /// <c>SecurityConfigurationAccessor.Build</c> for the tests declaring NO
    /// <c>clientKeyPassword</c> — every test in this class bar the passphrase section below —
    /// stating the <see langword="null"/> secret accessor once rather than at each call site.
    /// </summary>
    /// <remarks>
    /// Named for what it passes rather than for what it is. The third parameter is deliberately
    /// required (see <c>Build</c>'s own remarks on the optional-parameter omission this branch has
    /// already paid for once), and a helper that hid it behind a default would hand these call
    /// sites back exactly the silence that parameter exists to prevent.
    /// </remarks>
    private static ISecurityConfigurationAccessor BuildWithNoSecretAccessor(
        ScenarioAst ast, string? suiteDirectory) =>
        SecurityConfigurationAccessor.Build(ast, suiteDirectory, secrets: null);

    /// <summary>
    /// REQ-014's first acceptance: for a suite declaring a client certificate on a service,
    /// BOTH views resolve and are usable — the host-path view (which librdkafka accepts and
    /// certificate objects do not) and the <see cref="X509Certificate2"/> view (which
    /// <c>HttpClientHandler</c> accepts and paths do not).
    /// </summary>
    [Fact]
    public void For_ServiceDeclaringAClientCertificate_ReturnsBothViewsUsable()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurity("8443")),
            bed.SuiteDirectory);
        try
        {
            var configuration = accessor.For("payments");
            Assert.NotNull(configuration);
            Assert.Equal("mtls", configuration!.Profile);

            var certificates = configuration.Certificates;
            Assert.NotNull(certificates);

            // View 1 — host paths. Absolute, resolved against the suite directory, and
            // pointing at the files the author declared.
            Assert.True(Path.IsPathRooted(certificates!.ClientCertificatePath));
            Assert.True(File.Exists(certificates.ClientCertificatePath));
            Assert.True(File.Exists(certificates.ClientKeyPath));
            Assert.True(File.Exists(certificates.CaCertificatePath));

            // View 2 — certificate objects. USABLE means more than non-null: the client
            // certificate must carry a private key, or it cannot complete a mutual-TLS
            // handshake at all (the failure mode the accessor's PKCS#12 round trip exists
            // to prevent — a PEM-loaded pair reports HasPrivateKey=true and then fails the
            // handshake on Windows with an ephemeral key SChannel will not use).
            Assert.NotNull(certificates.ClientCertificate);
            Assert.True(certificates.ClientCertificate!.HasPrivateKey);
            Assert.Equal(TestCertificateAuthority.ClientSubjectCommonName, certificates.ClientCertificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));

            Assert.NotNull(certificates.CaCertificate);
            Assert.Equal(
                TestCertificateAuthority.CaSubjectCommonName,
                certificates.CaCertificate!.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Repeated resolution hands back the SAME instances, which is what makes the borrowing
    /// contract safe: a second step resolving the same target must not pay for a second load,
    /// and must not receive an object the first step's handler disposal could have invalidated.
    /// </summary>
    [Fact]
    public void For_ResolvedTwice_ReturnsTheSameCertificateInstances()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurity("8443")),
            bed.SuiteDirectory);
        try
        {
            var first = accessor.For("payments")!.Certificates!;
            var second = accessor.For("payments")!.Certificates!;

            Assert.Same(first.ClientCertificate, second.ClientCertificate);
            Assert.Same(first.CaCertificate, second.CaCertificate);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// REQ-001/REQ-024: an undeclared <c>caCert</c> is ABSENT, never defaulted. Both the path
    /// view and the object view stay <see langword="null"/> — the engine synthesises nothing
    /// and the platform's own trust store applies.
    /// </summary>
    [Fact]
    public void For_ProfileWithNoCaCert_LeavesBothCaViewsNull()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var security = MtlsSecurity("8443") with { CaCert = null };
        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", security), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;

            Assert.Null(certificates.CaCertificatePath);
            Assert.Null(certificates.CaCertificate);

            // The client half is unaffected — an absent CA is not an absent identity.
            Assert.NotNull(certificates.ClientCertificate);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// A <c>tls</c> profile declaring only a <c>caCert</c> presents no client identity: the
    /// client views stay null, and the trust anchor still loads.
    /// </summary>
    [Fact]
    public void For_TlsProfileWithOnlyACaCert_LoadsTheAnchorAndPresentsNoClientIdentity()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var security = new SecuritySpec(
            Profile: "tls",
            Endpoint: "8443",
            CaCert: TestCertificateAuthority.CaFileName,
            ClientCert: null,
            ClientKey: null,
            ServerArtifacts: null);

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", security), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;

            Assert.NotNull(certificates.CaCertificate);
            Assert.Null(certificates.ClientCertificate);
            Assert.Null(certificates.ClientCertificatePath);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// A target with no declared <c>security</c> block resolves to <see langword="null"/>, and
    /// a scenario declaring none at all gets the shared Null accessor — the common path, which
    /// allocates nothing and has nothing to dispose.
    /// </summary>
    [Fact]
    public void Build_ScenarioWithNoSecurityBlock_ReturnsTheNullAccessor()
    {
        var ast = new ScenarioAst(
            Metadata: null,
            Environment: new EnvironmentSpec(
                Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
                {
                    ["plain"] = new ServiceSpec("acme/api:1", null, null, 8080, null),
                },
                Dependencies: null,
                Seed: null,
                ImageRegistry: null,
                ImagePullPolicy: null),
            Variables: new Dictionary<string, string>(StringComparer.Ordinal),
            Steps: Array.Empty<StepNode>());

        var accessor = BuildWithNoSecretAccessor(ast, Directory.GetCurrentDirectory());

        Assert.Same(NullSecurityConfigurationAccessor.Instance, accessor);
        Assert.Null(accessor.For("plain"));
    }

    /// <summary>
    /// The trust decision itself, exercised directly rather than only through a live handshake:
    /// a chain error is forgiven ONLY by rebuilding against the declared anchor, and a name
    /// mismatch is never forgiven. Widening this would turn "trust this CA" into "trust
    /// anything it ever signed, presented by anyone".
    /// </summary>
    [Fact]
    public void TrustsRemoteCertificate_ForgivesChainErrorsUnderTheDeclaredAnchorOnly()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurity("8443")), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            using var serverCertificate = new X509Certificate2(bed.ServerCertificate.Export(X509ContentType.Cert));

            // A chain error against the platform store, but the certificate chains to the
            // declared anchor → trusted.
            Assert.True(certificates.TrustsRemoteCertificate(
                serverCertificate, null, SslPolicyErrors.RemoteCertificateChainErrors));

            // A name mismatch is NOT a chain problem and is never forgiven, even alongside a
            // chain error the anchor would otherwise resolve.
            Assert.False(certificates.TrustsRemoteCertificate(
                serverCertificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
            Assert.False(certificates.TrustsRemoteCertificate(
                serverCertificate,
                null,
                SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));

            // An absent certificate is never forgiven.
            Assert.False(certificates.TrustsRemoteCertificate(
                null, null, SslPolicyErrors.RemoteCertificateNotAvailable));

            // A certificate that does NOT chain to the declared anchor is rejected.
            using var foreign = TestCertificateAuthority.CreateUnrelatedLeaf();
            Assert.False(certificates.TrustsRemoteCertificate(
                foreign, null, SslPolicyErrors.RemoteCertificateChainErrors));
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// A declared <c>caCert</c> is a PIN, not additive trust: it is consulted on EVERY path,
    /// including <see cref="SslPolicyErrors.None"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="SslPolicyErrors.None"/> is exactly what the platform reports for a peer the
    /// MACHINE trust store already accepts, so the first two assertions are that case in the
    /// shape the callback actually sees it. Measured before this behaviour existed, both
    /// returned <see langword="true"/> — a certificate the host happened to trust was accepted
    /// without the declared anchor ever being consulted, so an author who wrote "only this
    /// private CA" got "this private CA, or any public one the host trusts". The third
    /// assertion is the control that matters: pinning must not break the ordinary case where
    /// the peer really does chain to the declared anchor and the platform is content.
    /// </remarks>
    [Fact]
    public void TrustsRemoteCertificate_WithADeclaredAnchor_PinsEvenWhenThePlatformIsSatisfied()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurity("8443")), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;

            // A leaf under an unrelated root, presented with the platform reporting no errors
            // at all — the machine-store case. REJECTED: it does not chain to the declared
            // anchor.
            using var foreign = TestCertificateAuthority.CreateUnrelatedLeaf();
            Assert.False(certificates.TrustsRemoteCertificate(foreign, null, SslPolicyErrors.None));

            // Same for a self-signed certificate the platform somehow accepted.
            var (imposterRoot, imposterLeaf) = TestCertificateAuthority.CreateImposterAuthority();
            using (imposterRoot)
            using (imposterLeaf)
            {
                Assert.False(certificates.TrustsRemoteCertificate(imposterRoot, null, SslPolicyErrors.None));
                Assert.False(certificates.TrustsRemoteCertificate(imposterLeaf, null, SslPolicyErrors.None));
            }

            // NEGATIVE CONTROL for the pin: the declared anchor's own leaf, with the platform
            // reporting no errors, is still ACCEPTED. Pinning narrows, it does not break.
            using var serverCertificate = new X509Certificate2(bed.ServerCertificate.Export(X509ContentType.Cert));
            Assert.True(certificates.TrustsRemoteCertificate(serverCertificate, null, SslPolicyErrors.None));
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// With NO <c>caCert</c> declared the platform's own verdict is the only one — the engine
    /// narrows nothing and relaxes nothing. This is the half of the pin decision that must NOT
    /// change: an unsecured or anchor-less target behaves exactly as before.
    /// </summary>
    [Fact]
    public void TrustsRemoteCertificate_WithNoDeclaredAnchor_DefersEntirelyToThePlatform()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurity("8443") with { CaCert = null }), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            using var foreign = TestCertificateAuthority.CreateUnrelatedLeaf();

            // The platform is satisfied → accepted, whatever the certificate chains to.
            Assert.True(certificates.TrustsRemoteCertificate(foreign, null, SslPolicyErrors.None));

            // The platform is not → rejected. There is no declared anchor to forgive with.
            Assert.False(certificates.TrustsRemoteCertificate(
                foreign, null, SslPolicyErrors.RemoteCertificateChainErrors));
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The rebuilt chain requires the <c>serverAuth</c> extended key usage, because a custom
    /// callback replaces the platform's verdict wholesale and the platform enforces it.
    /// </summary>
    /// <remarks>
    /// The sharpest arm is the first. In a mutual-TLS deployment the CA that signs the server
    /// signs every client, so a holder of any client certificate from the declared CA whose SAN
    /// matches the target host can present it AS the server. Measured before the application
    /// policy was set, that certificate was trusted, and an end-to-end HTTPS request against a
    /// listener using it connected with status 200. The final arm is what proves the constraint
    /// breaks nothing legitimate: an absent extended-key-usage extension means UNCONSTRAINED,
    /// and such a leaf stays trusted.
    /// </remarks>
    [Fact]
    public void TrustsRemoteCertificate_RequiresServerAuthExtendedKeyUsage()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurity("8443")), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;

            // A leaf from the DECLARED anchor, for the right host, carrying clientAuth only.
            Assert.False(certificates.TrustsRemoteCertificate(
                bed.ClientAuthOnlyServerCertificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(certificates.TrustsRemoteCertificate(
                bed.ClientAuthOnlyServerCertificate, null, SslPolicyErrors.None));

            // The ordinary server certificate (serverAuth present) is unaffected.
            using var serverCertificate = new X509Certificate2(bed.ServerCertificate.Export(X509ContentType.Cert));
            Assert.True(certificates.TrustsRemoteCertificate(
                serverCertificate, null, SslPolicyErrors.RemoteCertificateChainErrors));

            // NEGATIVE CONTROL: no EKU extension at all means unconstrained, so still trusted.
            Assert.True(certificates.TrustsRemoteCertificate(
                bed.NoEkuServerCertificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// A two-tier CA — an offline root declared as <c>caCert</c>, an issuing intermediate the
    /// server sends alongside its own leaf — validates. This is the NORMAL shape of an
    /// enterprise CA, and without the peer-supplied intermediate reaching the rebuilt chain's
    /// <c>ExtraStore</c> it cannot validate at all.
    /// </summary>
    [Fact]
    public void TrustsRemoteCertificate_WithATwoTierCa_ValidatesUsingThePeerSuppliedIntermediate()
    {
        using var bed = TestCertificateAuthority.CreateTwoTierSuiteDirectory();

        var security = new SecuritySpec(
            Profile: "tls",
            Endpoint: "8443",
            CaCert: TestCertificateAuthority.CaFileName,
            ClientCert: null,
            ClientKey: null,
            ServerArtifacts: null);

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", security), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;

            // Without the intermediate there is no path from the leaf to the declared root.
            Assert.False(certificates.TrustsRemoteCertificate(
                bed.ServerLeaf, null, SslPolicyErrors.RemoteCertificateChainErrors));

            // With the chain the peer actually sent — leaf plus intermediate — it validates.
            using var peerChain = PeerChain(bed.ServerLeaf, bed.IntermediateCertificate);
            Assert.True(certificates.TrustsRemoteCertificate(
                bed.ServerLeaf, peerChain, SslPolicyErrors.RemoteCertificateChainErrors));
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Peer-supplied certificates are candidate path LINKS, never trust anchors: a self-signed
    /// root handed over as an "intermediate" does not become trusted by sitting in
    /// <c>ExtraStore</c>.
    /// </summary>
    /// <remarks>
    /// This is the control that makes the two-tier support safe. The certificates in
    /// <c>ExtraStore</c> arrive from the peer and are therefore attacker-chosen in the threat
    /// model that matters; the rebuilt chain uses <c>CustomRootTrust</c>, so it terminates only
    /// at the DECLARED anchor, and an imposter root can shorten no path to it.
    /// </remarks>
    [Fact]
    public void TrustsRemoteCertificate_PeerSuppliedSelfSignedRoot_DoesNotBecomeATrustAnchor()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        var (imposterRoot, imposterLeaf) = TestCertificateAuthority.CreateImposterAuthority();

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurity("8443")), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;

            // The peer sends its leaf AND the self-signed root that issued it, hoping the root
            // is taken as an anchor rather than as a link.
            using var peerChain = PeerChain(imposterLeaf, imposterRoot);

            Assert.False(certificates.TrustsRemoteCertificate(
                imposterLeaf, peerChain, SslPolicyErrors.RemoteCertificateChainErrors));

            // Nor does presenting the imposter root itself as the leaf help.
            Assert.False(certificates.TrustsRemoteCertificate(
                imposterRoot, peerChain, SslPolicyErrors.RemoteCertificateChainErrors));

            // And the legitimate server certificate still validates with that same hostile
            // ExtraStore attached — the guard rejects the imposter, not everything.
            using var serverCertificate = new X509Certificate2(bed.ServerCertificate.Export(X509ContentType.Cert));
            Assert.True(certificates.TrustsRemoteCertificate(
                serverCertificate, peerChain, SslPolicyErrors.RemoteCertificateChainErrors));
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
            imposterRoot.Dispose();
            imposterLeaf.Dispose();
        }
    }

    /// <summary>
    /// Building the chain makes NO outbound request, even when the peer's certificate names a
    /// URL to fetch its issuer from.
    /// </summary>
    /// <remarks>
    /// <c>RevocationMode.NoCheck</c> suppresses CRL and OCSP but NOT Authority Information
    /// Access <c>caIssuers</c> fetching, which is governed separately. Measured before
    /// <c>DisableCertificateDownloads</c> was set: one outbound GET to the URL named in the
    /// certificate, issued inside the handshake and on the REJECTION path — so a peer need not
    /// be trusted to make the test host fetch a URL it chose, with Windows' 15-second-per-URL
    /// retrieval timeout as the stall bound. A raw <see cref="TcpListener"/> is used rather than
    /// an HTTP server because a completed TCP accept is already proof of the outbound request,
    /// and it needs no URL reservation on any platform.
    /// </remarks>
    [Fact]
    public async Task TrustsRemoteCertificate_WithAnAuthorityInfoAccessUrl_MakesNoOutboundRequest()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var accepted = 0;
        var pump = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref accepted);
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // Listener stopped — the only way out of this loop.
            }
        });

        using var bed = TestCertificateAuthority.CreateAuthorityInfoAccessBed(
            $"http://127.0.0.1:{port}/probe-intermediate.cer");

        var security = new SecuritySpec(
            Profile: "tls",
            Endpoint: "8443",
            CaCert: TestCertificateAuthority.CaFileName,
            ClientCert: null,
            ClientKey: null,
            ServerArtifacts: null);

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", security), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;

            // The issuer is deliberately absent, so the verdict is a rejection either way —
            // what this pins is that reaching that verdict costs no outbound request.
            Assert.False(certificates.TrustsRemoteCertificate(
                bed.LeafWithAuthorityInfoAccess, null, SslPolicyErrors.RemoteCertificateChainErrors));

            Assert.Equal(0, Volatile.Read(ref accepted));
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
            listener.Stop();
            await pump;
        }
    }

    /// <summary>
    /// Builds the <see cref="X509Chain"/> a validation callback would receive for a peer that
    /// sent <paramref name="leaf"/> together with <paramref name="sent"/>.
    /// </summary>
    /// <remarks>
    /// Built under <see cref="X509ChainTrustMode.CustomRootTrust"/> against the peer's OWN
    /// certificates purely so the chain populates its <c>ChainElements</c> — this fixture is
    /// modelling what the peer transmitted, not making a trust decision. Measured against a
    /// real <c>SslStream</c> handshake with an untrusted root: the callback's chain carries
    /// exactly <c>[leaf, intermediate]</c>, which is what this reproduces.
    /// </remarks>
    private static X509Chain PeerChain(X509Certificate2 leaf, X509Certificate2 sent)
    {
        var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        chain.ChainPolicy.ExtraStore.Add(sent);
        chain.ChainPolicy.CustomTrustStore.Add(sent);
        chain.Build(leaf);
        return chain;
    }

    /// <summary>
    /// A declared file that exists but is not loadable fails as a named
    /// <see cref="SecurityMaterialException"/>, not as an opaque handshake failure three layers
    /// down. Existence is already the validator's job (REQ-004); CONTENT is this class's.
    /// </summary>
    /// <remarks>
    /// The message names the DECLARED path and NOT the resolved absolute one, which is a
    /// disclosure boundary rather than a formatting preference: a provider's general catch
    /// writes an exception message into <c>Vars[outcomeKey]</c>, from where it reaches the §14
    /// event stream, the JSONL archive, the HTML report and the terminal.
    /// <c>ScenarioRunner.ScrubDiagnostic</c> is <c>ResolvedSecrets.Scrub</c> — a targeted net
    /// over values the run's <c>SecretAccessor</c> actually revealed — so a filesystem path
    /// there is never redacted and cannot be. The declared form is also the more useful one: it
    /// is what the author wrote and what they must edit.
    /// </remarks>
    [Fact]
    public void For_MalformedClientCertificate_ThrowsSecurityMaterialExceptionNamingTheField()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        File.WriteAllText(Path.Combine(bed.SuiteDirectory, TestCertificateAuthority.ClientCertFileName), "not a pem");

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurity("8443")), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);

            Assert.Contains("environment.services.payments.security.clientCert", ex.Message, StringComparison.Ordinal);

            // The author's own text, so the message is actionable...
            Assert.Contains(TestCertificateAuthority.ClientCertFileName, ex.Message, StringComparison.Ordinal);

            // ...and NOT the host path it resolved to.
            Assert.DoesNotContain(bed.SuiteDirectory, ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The same disclosure rule for the trust anchor: a malformed <c>caCert</c> names the
    /// declared path, never the resolved one.
    /// </summary>
    [Fact]
    public void For_MalformedCaCertificate_NamesTheDeclaredPathAndNotTheHostPath()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        File.WriteAllText(Path.Combine(bed.SuiteDirectory, TestCertificateAuthority.CaFileName), "not a pem");

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurity("8443")), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.CaCertificate);

            Assert.Contains("environment.services.payments.security.caCert", ex.Message, StringComparison.Ordinal);
            Assert.Contains(TestCertificateAuthority.CaFileName, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(bed.SuiteDirectory, ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    // ── The ENCRYPTED private key, which is what an enterprise PKI hands out by default ───────
    //
    // MEASURED on .NET 8.0.29 / Windows 10.0.26200 against X509Certificate2.CreateFromPemFile —
    // the overload the accessor takes when NO `clientKeyPassword` is declared, which is the path
    // every test in this section exercises. (With one declared the accessor takes
    // CreateFromEncryptedPemFile instead; that branch is covered by the passphrase section below.)
    //
    //   key shape                                          result
    //   ────────────────────────────────────────────────── ─────────────────────────────────────
    //   unencrypted PKCS#8  (BEGIN PRIVATE KEY)            OK, HasPrivateKey=True
    //   unencrypted PKCS#1  (BEGIN RSA PRIVATE KEY)        OK, HasPrivateKey=True
    //   ENCRYPTED PKCS#8    (BEGIN ENCRYPTED PRIVATE KEY)  CryptographicException
    //   openssl legacy enc  (Proc-Type: 4,ENCRYPTED)       CryptographicException
    //   outright garbage                                   CryptographicException
    //
    // All three failures raise the SAME type with the SAME text — "The key contents do not contain
    // a PEM, the content is malformed, or the key does not match the certificate." — so the
    // exception carries no signal at all about encryption being the cause, and reporting it
    // verbatim tells a bank's pilot their perfectly well-formed key is malformed. The FILE is where
    // the distinction survives; these tests pin that the accessor reads it and says so.
    //
    // Each arm rewrites the bed's OWN client key rather than generating a new one, so encryption is
    // the only variable: the same key, the same certificate, the same declaration.

    /// <summary>
    /// An ENCRYPTED PKCS#8 client key is reported AS ENCRYPTED — naming the field, the declared
    /// path and what to do about it — rather than as a malformed one.
    /// </summary>
    [Fact]
    public void For_EncryptedClientKey_NamesEncryptionAsTheCauseRatherThanMalformation()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        var keyPath = Path.Combine(bed.SuiteDirectory, TestCertificateAuthority.ClientKeyFileName);

        // The SAME key the bed issued, re-exported under a password. Nothing else about the bed
        // changes, so a failure here cannot be a malformed file wearing an encryption label.
        using (var key = RSA.Create())
        {
            key.ImportFromPem(File.ReadAllText(keyPath));
            File.WriteAllText(
                keyPath,
                key.ExportEncryptedPkcs8PrivateKeyPem(
                    "pilot-passphrase".AsSpan(),
                    new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000)));
        }

        // The label is .NET's own, not this test's guess at one — asserted so a runtime that
        // started writing a different label would fail HERE, where the classifier's premise lives,
        // rather than silently downgrading the diagnostic in the field.
        Assert.StartsWith(
            "-----BEGIN ENCRYPTED PRIVATE KEY-----",
            File.ReadAllText(keyPath),
            StringComparison.Ordinal);

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurity("8443")), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);

            // The FIELD at fault — the key, not the certificate beside it.
            Assert.Contains(
                "environment.services.payments.security.clientKey", ex.Message, StringComparison.Ordinal);

            // The CAUSE, named…
            Assert.Contains("ENCRYPTED private key", ex.Message, StringComparison.Ordinal);
            Assert.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", ex.Message, StringComparison.Ordinal);

            // …and REQ-007's remedy: the FIELD THAT NOW EXISTS, shown in the `${secret:}` form the
            // schema accepts, with the literal form refused in the same breath.
            Assert.Contains("clientKeyPassword", ex.Message, StringComparison.Ordinal);
            Assert.Contains(
                "clientKeyPassword: ${secret:env/CLIENT_KEY_PASS}", ex.Message, StringComparison.Ordinal);
            Assert.Contains("literal passphrase is refused", ex.Message, StringComparison.Ordinal);

            // Decryption survives as a SECONDARY option, and "secondary" is asserted as an
            // ordering rather than trusted to prose: the field is named before the openssl
            // incantation, so an author reading top-down meets the supported route first. This is
            // the whole of REQ-007 — the previous message told the author "there is no field for a
            // key password", which this change makes false.
            var fieldAt = ex.Message.IndexOf("clientKeyPassword", StringComparison.Ordinal);
            var decryptAt = ex.Message.IndexOf(
                "openssl pkcs8 -topk8 -nocrypt", StringComparison.Ordinal);
            Assert.True(decryptAt > fieldAt, "the decrypt instruction must follow the field name");
            Assert.DoesNotContain(
                "there is no field for a key password", ex.Message, StringComparison.Ordinal);

            // And NOT the platform's own text, which says "malformed" about a file that is not.
            Assert.DoesNotContain("could not be loaded as a certificate", ex.Message, StringComparison.Ordinal);

            // The declared-path disclosure rule holds here as everywhere in this class.
            Assert.Contains(TestCertificateAuthority.ClientKeyFileName, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(bed.SuiteDirectory, ex.Message, StringComparison.OrdinalIgnoreCase);

            // The platform's fault is preserved as the inner exception, so nothing is lost —
            // this is a rewording of the message, never a swallowing of the cause.
            Assert.IsAssignableFrom<CryptographicException>(ex.InnerException);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The SECOND encrypted shape, which the PEM label alone does not identify: openssl's legacy
    /// form keeps the ordinary <c>-----BEGIN RSA PRIVATE KEY-----</c> label and marks the
    /// encryption with a <c>Proc-Type: 4,ENCRYPTED</c> header.
    /// </summary>
    /// <remarks>
    /// Written by hand rather than by shelling out to openssl: the classifier under test reads the
    /// FILE, so the file is the fixture, and a test that needed openssl on PATH would be skipped on
    /// exactly the CI host this must not regress on. Measured, the real
    /// <c>openssl rsa -aes256 -traditional</c> output on this host carries precisely these two
    /// lines, and <c>CreateFromPemFile</c> rejects it with the same undifferentiated
    /// <c>CryptographicException</c> as every other failure.
    /// </remarks>
    [Fact]
    public void For_LegacyEncryptedClientKey_NamesTheProcTypeHeaderAsTheCause()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        var keyPath = Path.Combine(bed.SuiteDirectory, TestCertificateAuthority.ClientKeyFileName);

        File.WriteAllText(
            keyPath,
            "-----BEGIN RSA PRIVATE KEY-----\n"
            + "Proc-Type: 4,ENCRYPTED\n"
            + "DEK-Info: AES-256-CBC,0123456789ABCDEF0123456789ABCDEF\n"
            + "\n"
            + Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)) + "\n"
            + "-----END RSA PRIVATE KEY-----\n");

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurity("8443")), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);

            Assert.Contains(
                "environment.services.payments.security.clientKey", ex.Message, StringComparison.Ordinal);
            Assert.Contains("ENCRYPTED private key", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Proc-Type: 4,ENCRYPTED", ex.Message, StringComparison.Ordinal);
            Assert.Contains("clientKeyPassword", ex.Message, StringComparison.Ordinal);

            // The legacy shape is the one `clientKeyPassword` CANNOT open — .NET's PEM reader takes
            // only the PKCS#8 `-----BEGIN ENCRYPTED PRIVATE KEY-----` form — so this message has to
            // say the conversion out loud rather than point at the field and stop.
            Assert.Contains("openssl pkcs8 -topk8", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(bed.SuiteDirectory, ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// <strong>The false-positive control.</strong> A key that is genuinely malformed — carrying
    /// neither encryption marker — keeps the ORIGINAL generic message, so the new diagnostic cannot
    /// tell an author their broken file is merely encrypted.
    /// </summary>
    /// <remarks>
    /// This is the arm that makes the two above worth anything. All three shapes raise the same
    /// <c>CryptographicException</c> with the same text (see this section's measured table), so a
    /// classifier that answered "encrypted" too eagerly would pass both tests above and be wrong in
    /// the field on the commonest fault of all: right filename, wrong bytes.
    /// </remarks>
    [Fact]
    public void For_MalformedClientKey_KeepsTheGenericMessageAndClaimsNoEncryption()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        File.WriteAllText(
            Path.Combine(bed.SuiteDirectory, TestCertificateAuthority.ClientKeyFileName), "not a pem");

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurity("8443")), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);

            Assert.Contains(
                "could not be loaded as a certificate and matching private key",
                ex.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain("ENCRYPTED", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(bed.SuiteDirectory, ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    // ── clientKeyPassword: the encrypted key becomes loadable (REQ-005 / REQ-006 / REQ-009) ───
    //
    // Every arm here rewrites the bed's OWN client key, so the passphrase is the only variable —
    // the same key, the same certificate, the same declaration as the section above.
    //
    // No SecretString is ever minted by a test: its constructor is internal to
    // Vouchfx.Engine.Abstractions and this project has no InternalsVisibleTo grant. So a resolved
    // passphrase reaches the accessor the way a production one does — through a real
    // SecretAccessor over a real resolver. `env` for the ordinary arms (with a per-test unique
    // variable name, because environment variables are process-global and xUnit runs classes in
    // parallel), and a stubbed Vault client for the one value `env` CANNOT express: on Windows,
    // Environment.SetEnvironmentVariable(name, "") DELETES the variable rather than setting it
    // empty, so the empty-resolution arm would silently become the not-set arm.

    /// <summary>
    /// REQ-005: an encrypted client key plus the correct passphrase loads, and yields a client
    /// certificate that is USABLE — carrying a private key that survived the PKCS#12 round trip.
    /// </summary>
    [Fact]
    public void For_EncryptedClientKeyAndCorrectPassphrase_LoadsAUsableClientCertificate()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "pilot-passphrase");

        var variable = TestCertificateAuthority.UniqueClientKeyPassphraseVariableName();
        Environment.SetEnvironmentVariable(variable, "pilot-passphrase");
        try
        {
            var accessor = SecurityConfigurationAccessor.Build(
                AstWithSecuredService("payments", MtlsSecurityWithPassphrase(EnvReference(variable))),
                bed.SuiteDirectory,
                EnvironmentSecretAccessor());
            try
            {
                var certificates = accessor.For("payments")!.Certificates!;

                Assert.NotNull(certificates.ClientCertificate);

                // USABLE means more than non-null, exactly as the unencrypted arm asserts: the
                // PKCS#12 round trip applies to this branch too, and a certificate whose key did
                // not survive it fails the handshake on Windows while reporting HasPrivateKey.
                Assert.True(certificates.ClientCertificate!.HasPrivateKey);
                Assert.Equal(
                    TestCertificateAuthority.ClientSubjectCommonName,
                    certificates.ClientCertificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));

                // The trust anchor beside it is unaffected by any of this.
                Assert.NotNull(certificates.CaCertificate);
            }
            finally
            {
                (accessor as IDisposable)?.Dispose();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// The resolved passphrase is surfaced on the material as a <c>SecretString</c> — the form the
    /// Kafka wiring will read — and it is redacted by the type, not by a caller remembering to.
    /// </summary>
    [Fact]
    public void For_EncryptedClientKeyAndCorrectPassphrase_SurfacesTheValueRedacted()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "pilot-passphrase");

        var variable = TestCertificateAuthority.UniqueClientKeyPassphraseVariableName();
        Environment.SetEnvironmentVariable(variable, "pilot-passphrase");
        try
        {
            var accessor = SecurityConfigurationAccessor.Build(
                AstWithSecuredService("payments", MtlsSecurityWithPassphrase(EnvReference(variable))),
                bed.SuiteDirectory,
                EnvironmentSecretAccessor());
            try
            {
                var certificates = accessor.For("payments")!.Certificates!;

                Assert.NotNull(certificates.ClientKeyPassword);
                Assert.Equal("pilot-passphrase", certificates.ClientKeyPassword!.Reveal());
                Assert.Equal(SecretString.RedactedMarker, certificates.ClientKeyPassword.ToString());
            }
            finally
            {
                (accessor as IDisposable)?.Dispose();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// REQ-006: a passphrase declared against a key that is NOT encrypted is refused, naming
    /// <c>clientKeyPassword</c> — in BOTH unencrypted PEM shapes, because "unencrypted" is a
    /// property of the file rather than of one label.
    /// </summary>
    /// <remarks>
    /// Silently falling through to the unencrypted load would let a suite pass while the
    /// passphrase it declares does nothing at all, which is how a key rotated back to plaintext
    /// goes unnoticed — the same fail-closed argument the half-a-pair guard rests on.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void For_PassphraseAgainstAnUnencryptedKey_IsRefusedNamingTheField(bool pkcs8)
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        var keyPath = Path.Combine(bed.SuiteDirectory, TestCertificateAuthority.ClientKeyFileName);

        // The bed writes PKCS#8; re-export the SAME key as PKCS#1 for the second arm, so the only
        // difference between the two rows is the PEM label.
        using (var key = RSA.Create())
        {
            key.ImportFromPem(File.ReadAllText(keyPath));
            File.WriteAllText(
                keyPath, pkcs8 ? key.ExportPkcs8PrivateKeyPem() : key.ExportRSAPrivateKeyPem());
        }

        Assert.StartsWith(
            pkcs8 ? "-----BEGIN PRIVATE KEY-----" : "-----BEGIN RSA PRIVATE KEY-----",
            File.ReadAllText(keyPath),
            StringComparison.Ordinal);

        var variable = TestCertificateAuthority.UniqueClientKeyPassphraseVariableName();
        Environment.SetEnvironmentVariable(variable, "pilot-passphrase");
        try
        {
            var accessor = SecurityConfigurationAccessor.Build(
                AstWithSecuredService("payments", MtlsSecurityWithPassphrase(EnvReference(variable))),
                bed.SuiteDirectory,
                EnvironmentSecretAccessor());
            try
            {
                var certificates = accessor.For("payments")!.Certificates!;
                var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);

                Assert.Contains(
                    "environment.services.payments.security.clientKeyPassword",
                    ex.Message,
                    StringComparison.Ordinal);
                Assert.Contains("NOT an encrypted private key", ex.Message, StringComparison.Ordinal);
                Assert.Contains(TestCertificateAuthority.ClientKeyFileName, ex.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(bed.SuiteDirectory, ex.Message, StringComparison.OrdinalIgnoreCase);

                // The refusal happens BEFORE any load is attempted, so there is no platform fault
                // to preserve — which is also what proves the guard ran rather than the load
                // failing for some unrelated reason.
                Assert.Null(ex.InnerException);
            }
            finally
            {
                (accessor as IDisposable)?.Dispose();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// REQ-006 is a property of the MATERIAL, not of the certificate load: a consumer that reads
    /// only <c>ClientKeyPassword</c> and the path views — librdkafka's shape, which never touches
    /// <c>ClientCertificate</c> at all — is refused by the same guard.
    /// </summary>
    /// <remarks>
    /// Written as its own arm rather than folded into the theory above because the two exercise
    /// DIFFERENT entry points into the same guard, and a guard living in the certificate load would
    /// pass that theory while being absent from exactly the consumer that never loads a certificate.
    /// </remarks>
    [Fact]
    public void For_PassphraseAgainstAnUnencryptedKey_IsRefusedOnThePassphraseReadToo()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        // The bed's key is already unencrypted PKCS#8; nothing is rewritten here.
        var variable = TestCertificateAuthority.UniqueClientKeyPassphraseVariableName();
        Environment.SetEnvironmentVariable(variable, "pilot-passphrase");
        try
        {
            var accessor = SecurityConfigurationAccessor.Build(
                AstWithSecuredService("payments", MtlsSecurityWithPassphrase(EnvReference(variable))),
                bed.SuiteDirectory,
                EnvironmentSecretAccessor());
            try
            {
                var certificates = accessor.For("payments")!.Certificates!;

                // The path views still answer — they say nothing about encryption…
                Assert.NotNull(certificates.ClientKeyPath);

                // …and the passphrase read is where the contradiction surfaces.
                var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientKeyPassword);
                Assert.Contains(
                    "environment.services.payments.security.clientKeyPassword",
                    ex.Message,
                    StringComparison.Ordinal);
                Assert.Contains("NOT an encrypted private key", ex.Message, StringComparison.Ordinal);
            }
            finally
            {
                (accessor as IDisposable)?.Dispose();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// HAZARD H2: a declared passphrase with NO secret accessor fails closed. It must never load
    /// the key as though it were unencrypted, and it must never return <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <c>ClientCertificate == null</c> is this engine's wire signal for "profile: tls, present
    /// nothing", so a null here would reproduce exactly the measured bypass the half-a-pair guard
    /// exists to close: against a listener that requests but does not enforce a client
    /// certificate, the suite PASSES while presenting no identity.
    /// </remarks>
    [Fact]
    public void For_DeclaredPassphraseWithNoSecretAccessor_FailsClosedRatherThanLoadingUnencrypted()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "pilot-passphrase");

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService(
                "payments", MtlsSecurityWithPassphrase("${secret:env/CLIENT_KEY_PASS}")),
            bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);

            Assert.Contains(
                "environment.services.payments.security.clientKeyPassword",
                ex.Message,
                StringComparison.Ordinal);
            Assert.Contains("no secret accessor", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// REQ-009: <c>Build</c> resolves NOTHING. A throwing accessor survives construction untouched,
    /// and the first read of <c>ClientCertificate</c> is what reaches for the secret.
    /// </summary>
    /// <remarks>
    /// This is §17 stated as a test rather than as a comment: resolution at first use of the
    /// material, never at build time and never at compile time, is what keeps a passphrase out of
    /// the emitted IL and out of the reproducibility envelope.
    /// </remarks>
    [Fact]
    public void Build_WithADeclaredPassphrase_ResolvesNothing_UntilClientCertificateIsRead()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "pilot-passphrase");

        var secrets = new ThrowingSecretAccessor();

        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService(
                "payments", MtlsSecurityWithPassphrase("${secret:env/CLIENT_KEY_PASS}")),
            bed.SuiteDirectory,
            secrets);
        try
        {
            // Build alone, and For() alone, resolve nothing — a throwing resolver would have made
            // either of them fail.
            var certificates = accessor.For("payments")!.Certificates!;
            Assert.Equal(0, secrets.Calls);

            // The FIRST read of the certificate is the resolution point.
            Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);
            Assert.Equal(1, secrets.Calls);

            // And the answer is cached, failure included: a second read does not re-resolve.
            Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);
            Assert.Equal(1, secrets.Calls);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// EDGE-001: a WRONG passphrase names <c>clientKeyPassword</c> as the likely cause and echoes
    /// neither the value nor its length (HAZARD H3 — this message becomes a §14 event).
    /// </summary>
    [Fact]
    public void For_WrongPassphrase_NamesTheFieldAndEchoesNeitherValueNorLength()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "the-right-passphrase");

        var variable = TestCertificateAuthority.UniqueClientKeyPassphraseVariableName();
        Environment.SetEnvironmentVariable(variable, "the-wrong-passphrase");
        try
        {
            var accessor = SecurityConfigurationAccessor.Build(
                AstWithSecuredService("payments", MtlsSecurityWithPassphrase(EnvReference(variable))),
                bed.SuiteDirectory,
                EnvironmentSecretAccessor());
            try
            {
                var certificates = accessor.For("payments")!.Certificates!;
                var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);

                Assert.Contains("clientKeyPassword", ex.Message, StringComparison.Ordinal);
                Assert.Contains("the passphrase is wrong", ex.Message, StringComparison.Ordinal);

                // NEITHER value: not the one that was set, nor the one the key actually takes.
                Assert.DoesNotContain("the-wrong-passphrase", ex.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("the-right-passphrase", ex.Message, StringComparison.Ordinal);

                // …and not its LENGTH either, which is an oracle in its own right. The message
                // carries no decimal number at all except inside the PKCS#8 label it quotes.
                Assert.DoesNotContain("20 character", ex.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("length", ex.Message, StringComparison.OrdinalIgnoreCase);

                // It stays a message about a DECLARED path, not a host one.
                Assert.DoesNotContain(bed.SuiteDirectory, ex.Message, StringComparison.OrdinalIgnoreCase);

                // The platform's fault is preserved rather than swallowed.
                Assert.IsAssignableFrom<CryptographicException>(ex.InnerException);
            }
            finally
            {
                (accessor as IDisposable)?.Dispose();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// EDGE-006 / HAZARD H2: a reference resolving to an EMPTY value is refused by name, rather
    /// than attempted and surfaced as the platform's undifferentiated <c>CryptographicException</c>.
    /// </summary>
    [Fact]
    public void For_PassphraseResolvingToAnEmptyValue_IsRefusedNamingTheField()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "pilot-passphrase");

        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService(
                "payments", MtlsSecurityWithPassphrase("${secret:vault/kv/client#pass}")),
            bed.SuiteDirectory,
            VaultSecretAccessorReturning(string.Empty));
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);

            Assert.Contains(
                "environment.services.payments.security.clientKeyPassword",
                ex.Message,
                StringComparison.Ordinal);
            Assert.Contains("EMPTY value", ex.Message, StringComparison.Ordinal);

            // The reference is named — that is what an author needs to fix it — and nothing else.
            Assert.Contains("${secret:vault/kv/client#pass}", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// HAZARD H3: a reference carrying control characters is ESCAPED where a diagnostic quotes it,
    /// never concatenated raw.
    /// </summary>
    /// <remarks>
    /// The reference grammar's path class is <c>[^}]+</c> — in the schema pattern and in
    /// <c>SecretReference</c>'s own grammar alike — so a newline, a C0 control character or an ANSI
    /// escape sequence is admissible inside a reference. These messages reach the §14 JSON Lines
    /// stream through <c>SecuredEndpointProbe</c> and the terminal renderer, where a raw newline
    /// forges a line of engine output.
    /// </remarks>
    [Fact]
    public void For_PassphraseReferenceCarryingControlCharacters_EscapesThemInTheDiagnostic()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "pilot-passphrase");

        // Constructed from escapes, never typed as raw bytes into this file.
        const char esc = '\u001b';
        var hostile = "${secret:env/A\nB" + esc + "[31mC}";

        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService("payments", MtlsSecurityWithPassphrase(hostile)),
            bed.SuiteDirectory,
            EnvironmentSecretAccessor());
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);

            Assert.DoesNotContain("\n", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(esc.ToString(), ex.Message, StringComparison.Ordinal);
            Assert.Contains("\\u000a", ex.Message, StringComparison.Ordinal);
            Assert.Contains("\\u001b", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// HAZARD H3's SECOND property, which escaping alone does not give: a quoted reference is
    /// CAPPED. The grammar's path class is unbounded, and a diagnostic that reproduced an
    /// arbitrarily long one would carry every character of it into the §14 event stream, the HTML
    /// report and the terminal.
    /// </summary>
    /// <remarks>
    /// The null-accessor refusal is the vehicle because it quotes the reference without needing it
    /// to resolve; the key is encrypted first so the REQ-006 guard passes and the message under
    /// test is reached.
    /// </remarks>
    [Fact]
    public void For_AnOverlongPassphraseReference_IsTruncatedInTheDiagnostic()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "pilot-passphrase");

        // Well past the cap, and one repeated character throughout, so the assertions below are on
        // HOW MUCH survived rather than on where a word boundary happened to land.
        var overlong = "${secret:env/" + new string('A', 400) + "}";

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurityWithPassphrase(overlong)), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);

            Assert.DoesNotContain(overlong, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('A', 200), ex.Message, StringComparison.Ordinal);
            Assert.Contains("...", ex.Message, StringComparison.Ordinal);

            // Enough of it survives to be recognisable, which is the only reason to quote it.
            Assert.Contains("${secret:env/AAAA", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// HAZARD H3's THIRD property, which the cap alone does not give: the truncation NEVER SPLITS
    /// A SURROGATE PAIR. <c>limit</c> counts UTF-16 CODE UNITS, and a high surrogate
    /// (U+D800–U+DBFF) sits ABOVE the C1 range the escaping loop rewrites, so a pair cut in half
    /// puts a LONE high surrogate into the message raw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a cosmetic defect. These messages reach the §14 event stream and the JUnit XML renderer
    /// through <c>SecuredEndpointProbe</c>, and a lone surrogate throws
    /// <see cref="InvalidOperationException"/> at <c>GetString</c> there — an unrelated, unreadable
    /// failure raised inside the reporting layer while the run is already reporting an environment
    /// error, i.e. the diagnostic destroys the report that was carrying it.
    /// </para>
    /// <para>
    /// The boundary is COMPUTED, not guessed. An astral character is a surrogate PAIR in UTF-16,
    /// so the reference is padded until the pair's HIGH unit lands on the last unit the cap keeps
    /// and its LOW unit on the first unit dropped. The two placement assertions run BEFORE the
    /// behaviour is exercised, so a change to the cap fails this test loudly rather than leaving it
    /// silently vacuous — a truncation that no longer lands mid-pair would otherwise pass for the
    /// wrong reason.
    /// </para>
    /// </remarks>
    [Fact]
    public void For_APassphraseReferenceTruncatedMidSurrogatePair_QuotesNoLoneSurrogate()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "pilot-passphrase");

        // Mirrors DescribeReference's default cap for a reference. Restated here because that
        // method is private; the placement assertions below fail if the two ever drift.
        const int cap = 120;
        const string prefix = "${secret:env/";

        // U+1F600, written as a code point rather than as a literal astral character typed into
        // this source file — the same rule the control-character test above states. In UTF-16 this
        // is '\ud83d' followed by '\ude00'.
        var astral = char.ConvertFromUtf32(0x1F600);

        // Padded so the pair's HIGH unit is the LAST unit the cap keeps, and long enough after it
        // that the reference is unambiguously over the cap and truncation certainly runs.
        var padding = cap - 1 - prefix.Length;
        var hostile = prefix + new string('A', padding) + astral + new string('B', 300) + "}";

        Assert.True(
            char.IsHighSurrogate(hostile[cap - 1]),
            "the pair's high unit must sit on the last kept unit for this test to mean anything");
        Assert.True(
            char.IsLowSurrogate(hostile[cap]),
            "the pair's low unit must sit on the first dropped unit for this test to mean anything");

        // The null-accessor refusal again: it quotes the reference without needing it to resolve,
        // and the key is encrypted first so REQ-006's guard passes and that message is reached.
        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurityWithPassphrase(hostile)),
            bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);

            // Truncation ran at all — without this the assertion below would hold trivially.
            Assert.Contains("...", ex.Message, StringComparison.Ordinal);

            AssertNoLoneSurrogate(ex.Message);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Asserts a diagnostic carries no UNPAIRED surrogate — the shape that throws at
    /// <c>GetString</c> in the JUnit XML renderer.
    /// </summary>
    /// <remarks>
    /// Two checks, because neither alone is sufficient. The scan localises the fault to an index;
    /// the UTF-8 round trip is the property the reporting layer actually depends on, and it catches
    /// any other unencodable shape the scan was not written for, since the encoder substitutes
    /// U+FFFD for a lone surrogate and the strings then differ. NEITHER failure message quotes the
    /// offending text: reproducing a lone surrogate in an assertion message would carry it straight
    /// into the runner's own TRX writer, which is the very failure under test.
    /// </remarks>
    private static void AssertNoLoneSurrogate(string message)
    {
        for (var index = 0; index < message.Length; index++)
        {
            if (char.IsHighSurrogate(message[index]))
            {
                Assert.True(
                    index + 1 < message.Length && char.IsLowSurrogate(message[index + 1]),
                    $"unpaired HIGH surrogate at index {index} of the diagnostic");
                index++;
                continue;
            }

            Assert.False(
                char.IsLowSurrogate(message[index]),
                $"unpaired LOW surrogate at index {index} of the diagnostic");
        }

        Assert.Equal(message, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(message)));
    }

    /// <summary>
    /// The containment backstop holds on the PASSPHRASE read too: resolving
    /// <c>clientKeyPassword</c> READS the declared key file to classify its encryption, and no read
    /// of a declared path may precede the containment check.
    /// </summary>
    /// <remarks>
    /// Written against <c>ClientKeyPassword</c> specifically. <c>LoadClient</c> checks containment
    /// itself before it reads anything, so the certificate view fails closed whatever the resolver
    /// does; the passphrase-only consumer — librdkafka's shape — reaches the resolver with no other
    /// check in front of it, and that call site is what this pins. Without it the resolver reads a
    /// file outside the suite directory and answers with REQ-006's message instead, which is the
    /// wrong refusal for the wrong reason.
    /// </remarks>
    [Fact]
    public void For_ClientKeyEscapingTheSuiteDirectory_FailsClosedOnThePassphraseReadToo()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        var nested = Path.Combine(bed.SuiteDirectory, "nested");
        Directory.CreateDirectory(nested);

        // As in the path-getter containment test: an AST that never went through
        // EnvironmentSecurityValidator, whose traversal lands on a real file, so nothing but the
        // containment check can be what rejects it.
        var security = new SecuritySpec(
            Profile: "mtls",
            Endpoint: "8443",
            CaCert: Path.Combine("..", TestCertificateAuthority.CaFileName),
            ClientCert: Path.Combine("..", TestCertificateAuthority.ClientCertFileName),
            ClientKey: Path.Combine("..", TestCertificateAuthority.ClientKeyFileName),
            ServerArtifacts: null)
        {
            ClientKeyPassword = "${secret:env/CLIENT_KEY_PASS}",
        };

        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService("payments", security), nested, EnvironmentSecretAccessor());
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientKeyPassword);

            Assert.Contains(
                "environment.services.payments.security.clientKey",
                ex.Message,
                StringComparison.Ordinal);
            Assert.Contains("outside the suite directory", ex.Message, StringComparison.Ordinal);

            // SEC-7 again: the DECLARED form, and no host path in the message.
            Assert.DoesNotContain(nested, ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(bed.SuiteDirectory, ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    // ── The declared value must be a whole REFERENCE, and is never echoed (security MAJOR-1) ──

    /// <summary>
    /// A declared <c>clientKeyPassword</c> that is not a single, whole <c>${secret:}</c> reference
    /// is refused — and the value is NOT reported, because a value in that position may be the
    /// passphrase itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SecuritySpec.ClientKeyPassword</c>'s own remarks state that an embedder bypassing the
    /// schema CAN bind a literal here, so no consumer may assume the text is non-secret. Without
    /// this guard the literal reached <c>SecretAccessor.Resolve</c>, which raises
    /// "the secret reference '&lt;literal&gt;' is malformed…", and the accessor's catch quoted that
    /// message beside the declared text — the passphrase, twice, into a §14 environment-error
    /// event, the JSON stream, the HTML report and the terminal.
    /// </para>
    /// <para>
    /// NO LATER SCRUB REACHES IT, which is why the fix has to be a refusal rather than redaction:
    /// <c>ResolvedSecrets.Record</c> runs only after a SUCCESSFUL resolve, and this diagnostic
    /// fires INSTEAD of one, so the ledger is empty at the moment the value would be emitted.
    /// </para>
    /// <para>
    /// The bed's key is left UNENCRYPTED and the resolver COUNTS its calls, so this pins the guard
    /// ORDER as well as its behaviour: the shape refusal must fire ahead of REQ-006's
    /// encryption check (which would otherwise answer first) and ahead of any resolution attempt
    /// (which is what would leak the value).
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("hunter2")]
    [InlineData("correct horse battery staple")]
    [InlineData("s3cr3t ${secret:env/CLIENT_KEY_PASS}")]
    [InlineData("${secret:env/CLIENT_KEY_PASS}trailing-text")]
    [InlineData("${secret:env/CLIENT_KEY_PASS}\n")]
    [InlineData("${secret:no-slash-so-no-path}")]
    // A NESTED lead-in inside the path. TryParse ACCEPTS these — its whole-token match simply
    // spans the inner one — so before this guard was swapped from TryParse to
    // ValidateSecretBearingField they passed it, and the comment above the guard claimed passing
    // it "earned" the five downstream QuoteUntrusted sites the right to quote the declared text.
    // Not CLI-reachable (the validation scan refuses them first) but this guard exists precisely
    // for the direct-embedding path where that scan never runs.
    [InlineData("${secret:env/PA${secret:CANARY}")]
    [InlineData("${secret:nosuchsource/PA${secret:CANARY}")]
    // A whole, well-formed reference naming a source this engine cannot resolve. Newly refused
    // HERE by the same swap — it used to reach the resolver and fail there.
    [InlineData("${secret:nosuchsource/CLIENT_KEY_PASS}")]
    public void For_ADeclaredPassphraseTheEngineCannotAccept_IsRefusedWithoutEchoingIt(
        string declared)
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var secrets = new ThrowingSecretAccessor();

        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService("payments", MtlsSecurityWithPassphrase(declared)),
            bed.SuiteDirectory,
            secrets);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientKeyPassword);

            // THE FINDING: the declared text appears nowhere in the diagnostic.
            Assert.DoesNotContain(declared, ex.Message, StringComparison.Ordinal);

            Assert.Contains(
                "environment.services.payments.security.clientKeyPassword",
                ex.Message,
                StringComparison.Ordinal);
            // The clause is asserted, not just the prefix. It was added in the same commit that
            // broadened this guard to refuse an unknown SOURCE; without it the message would be
            // false for the '${secret:nosuchsource/CLIENT_KEY_PASS}' row below, which IS a single,
            // whole reference — and nothing would catch the regression.
            Assert.Contains(
                "not a single, whole secret reference naming a resolvable source",
                ex.Message,
                StringComparison.Ordinal);
            Assert.Contains("NOT REPORTED", ex.Message, StringComparison.Ordinal);

            // Nothing was resolved, so nothing COULD have been echoed by the resolution path — the
            // property the message assertion above states, measured at the other end.
            Assert.Equal(0, secrets.Calls);
            Assert.Null(ex.InnerException);

            // Guard ORDER: the key here is unencrypted, so REQ-006's contradiction check would
            // have answered had it run first. It must not — its message quotes the declared text.
            Assert.DoesNotContain("NOT an encrypted private key", ex.Message, StringComparison.Ordinal);

            // The certificate view is refused by the same guard, not merely the passphrase view.
            Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The FINDING itself, on the path that actually reaches the resolver: a literal passphrase
    /// declared against an ENCRYPTED key, with a REAL secret accessor behind it, is refused without
    /// the literal appearing anywhere in the diagnostic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from the theory above, and the separation is the point — MEASURED while
    /// mutation-testing that theory. Its rows run against the bed's UNENCRYPTED key, so with the
    /// shape guard removed they are answered by REQ-006's contradiction check, which quotes no
    /// value: the theory reddens under mutation on the message wording, proving the guard runs
    /// FIRST, but it never reproduces the leak. Only an ENCRYPTED key gets past the encryption
    /// check and into <c>SecretAccessor.Resolve</c>, whose own message is
    /// "the secret reference '&lt;literal&gt;' is malformed…" — and the accessor's catch then quotes
    /// the declared text beside it, putting the passphrase into the diagnostic TWICE.
    /// </para>
    /// <para>
    /// A real <c>SecretAccessor</c> over a real resolver, not a throwing double: a double that
    /// raised something of its own would have hidden the very message this is about.
    /// </para>
    /// </remarks>
    [Fact]
    public void For_ALiteralPassphraseAgainstAnEncryptedKey_NeverReachesTheResolverThatWouldEchoIt()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "hunter2");

        const string literal = "hunter2";

        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService("payments", MtlsSecurityWithPassphrase(literal)),
            bed.SuiteDirectory,
            EnvironmentSecretAccessor());
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientKeyPassword);

            // THE FINDING. Not once, let alone the twice the resolution path produced.
            Assert.DoesNotContain(literal, ex.Message, StringComparison.Ordinal);

            // And no nested resolver message either — the shape by which it leaked.
            Assert.DoesNotContain("is malformed", ex.Message, StringComparison.Ordinal);
            Assert.Null(ex.InnerException);

            Assert.Contains("NOT REPORTED", ex.Message, StringComparison.Ordinal);

            // The certificate view is refused by the same guard, before any load is attempted.
            var loadEx = Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);
            Assert.DoesNotContain(literal, loadEx.Message, StringComparison.Ordinal);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    // ── EDGE-004: a passphrase for a target with no key to decrypt ────────────────────────────

    /// <summary>
    /// EDGE-004 shape (a): <c>clientKeyPassword</c> beside a <c>clientCert</c> with NO
    /// <c>clientKey</c> is refused by name.
    /// </summary>
    /// <remarks>
    /// Refused on the PASSPHRASE read, which is the half the certificate load's half-a-pair check
    /// never covered: librdkafka reads the paths and the passphrase and never touches
    /// <c>ClientCertificate</c>, so before this guard a passphrase-only consumer resolved a secret
    /// for a target that has no key at all.
    /// </remarks>
    [Fact]
    public void For_APassphraseWithAClientCertButNoClientKey_IsRefusedNamingBothFields()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var security = MtlsSecurityWithPassphrase("${secret:env/CLIENT_KEY_PASS}") with
        {
            ClientKey = null,
        };

        var secrets = new ThrowingSecretAccessor();
        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService("payments", security), bed.SuiteDirectory, secrets);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientKeyPassword);

            Assert.Contains("clientKeyPassword", ex.Message, StringComparison.Ordinal);
            Assert.Contains("clientKey", ex.Message, StringComparison.Ordinal);
            Assert.Contains("without a matching", ex.Message, StringComparison.Ordinal);

            // Refused BEFORE resolution, so no secret is fetched for a target that cannot use one.
            Assert.Equal(0, secrets.Calls);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// EDGE-004 shape (b): <c>clientKeyPassword</c> beside a bare <c>caCert</c>. Both views refuse
    /// it — and the certificate view must THROW rather than return <see langword="null"/> (H2).
    /// </summary>
    [Fact]
    public void For_APassphraseWithOnlyACaCert_IsRefusedOnBothViewsRatherThanPresentingNothing()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var security = new SecuritySpec(
            Profile: "mtls",
            Endpoint: "8443",
            CaCert: TestCertificateAuthority.CaFileName,
            ClientCert: null,
            ClientKey: null,
            ServerArtifacts: null)
        {
            ClientKeyPassword = "${secret:env/CLIENT_KEY_PASS}",
        };

        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService("payments", security),
            bed.SuiteDirectory,
            new ThrowingSecretAccessor());
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;

            Assert.Contains(
                "without a matching",
                Assert.Throws<SecurityMaterialException>(() => certificates.ClientKeyPassword).Message,
                StringComparison.Ordinal);

            // The decisive half. `ClientCertificate == null` is this engine's wire signal for
            // "present no client identity", so answering this shape with null would let an
            // incoherent declaration pass against a listener that requests but does not enforce a
            // client certificate — the measured bypass the half-a-pair guard exists to close.
            Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);

            // The trust anchor beside it is untouched by the refusal.
            Assert.NotNull(certificates.CaCertificatePath);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// EDGE-004 shape (c): <c>clientKeyPassword</c> with NO path-valued field at all still
    /// materialises a certificate view, and that view refuses it.
    /// </summary>
    /// <remarks>
    /// This is the shape the projection used to DROP — the certificate view was synthesised only
    /// when a path-valued field was declared, so a passphrase alone was answered with silence
    /// before any guard could see it. Dropping it was defensible only while nothing refused the
    /// shape; once the missing-<c>clientKey</c> refusal existed, the drop became the one hole left
    /// in EDGE-004.
    /// </remarks>
    [Fact]
    public void For_APassphraseWithNoPathValuedFieldAtAll_MaterialisesAViewThatRefusesIt()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var security = new SecuritySpec(
            Profile: "mtls",
            Endpoint: "8443",
            CaCert: null,
            ClientCert: null,
            ClientKey: null,
            ServerArtifacts: null)
        {
            ClientKeyPassword = "${secret:env/CLIENT_KEY_PASS}",
        };

        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService("payments", security),
            bed.SuiteDirectory,
            new ThrowingSecretAccessor());
        try
        {
            var certificates = accessor.For("payments")!.Certificates;
            Assert.NotNull(certificates);

            var ex = Assert.Throws<SecurityMaterialException>(() => certificates!.ClientKeyPassword);
            Assert.Contains("clientKeyPassword", ex.Message, StringComparison.Ordinal);
            Assert.Contains("without a matching", ex.Message, StringComparison.Ordinal);

            Assert.Throws<SecurityMaterialException>(() => certificates!.ClientCertificate);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    // ── An unreadable key is not an unencrypted one (code-review MAJOR M1) ────────────────────

    /// <summary>
    /// A declared <c>clientKey</c> that cannot be READ is reported as unreadable, never as "not an
    /// encrypted private key".
    /// </summary>
    /// <remarks>
    /// <c>DescribeEncryptedPrivateKey</c> answers <see langword="null"/> both for a file carrying
    /// no encryption marker and for one it could not open. That conflation is harmless at its
    /// original call site — inside a catch already about to throw a well-formed diagnostic — and
    /// terminal at the passphrase resolver, where the return value IS the diagnostic and there is
    /// no later load attempt to correct the story. Before the split, a missing, locked or
    /// ACL-denied key was reported to the author as a contradiction in their own declaration.
    /// </remarks>
    [Fact]
    public void For_AnUnreadableClientKey_IsRefusedAsUnreadableRatherThanAsUnencrypted()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        // Deletion rather than an exclusive lock: an OS-level lock is advisory on Linux, so a
        // FileShare.None handle would make this test pass on Windows and vacuously succeed
        // elsewhere. A file that is not there fails to open on every platform.
        File.Delete(Path.Combine(bed.SuiteDirectory, TestCertificateAuthority.ClientKeyFileName));

        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService(
                "payments", MtlsSecurityWithPassphrase("${secret:env/CLIENT_KEY_PASS}")),
            bed.SuiteDirectory,
            new ThrowingSecretAccessor());
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientKeyPassword);

            Assert.Contains(
                "environment.services.payments.security.clientKeyPassword",
                ex.Message,
                StringComparison.Ordinal);
            Assert.Contains("could not be READ", ex.Message, StringComparison.Ordinal);

            // THE FINDING: the old message. An unreadable file says nothing about encryption.
            Assert.DoesNotContain("NOT an encrypted private key", ex.Message, StringComparison.Ordinal);

            // Still the DECLARED path only, and no host path from the platform's own I/O message.
            Assert.Contains(TestCertificateAuthority.ClientKeyFileName, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(bed.SuiteDirectory, ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    // ── The escaping is TOTAL (security MINOR-1/2/3) ──────────────────────────────────────────
    //
    // Three properties the C0/DEL/C1 predicate did not have, each pinned separately. All three use
    // the null-accessor refusal as their vehicle: it quotes the reference without needing it to
    // resolve, and the key is encrypted first so REQ-006's guard passes and that message is
    // reached. Every hostile character is written as a C# escape, never typed as a raw literal
    // into this source file.

    /// <summary>
    /// An UNPAIRED SURROGATE already present in the reference is escaped. The truncation back-off
    /// covers only the lone surrogate truncation CREATES; one supplied in the input sits above the
    /// C1 range and passed through raw.
    /// </summary>
    /// <remarks>
    /// A lone surrogate cannot be encoded as UTF-8 and throws
    /// <see cref="InvalidOperationException"/> at <c>GetString</c> in the JUnit XML renderer — so
    /// the diagnostic destroys the report that was carrying it. The reference stays WELL-FORMED
    /// (the grammar's path class is <c>[^}]+</c>, which admits a lone surrogate), so the
    /// reference-shape guard passes it through to the quoting this test is about.
    /// </remarks>
    [Fact]
    public void For_AReferenceCarryingALoneSurrogate_EscapesItRatherThanQuotingItRaw()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "pilot-passphrase");

        // A high surrogate with no low unit after it, and a low surrogate with no high unit before
        // it — both directions, well inside the cap so truncation plays no part.
        var hostile = "${secret:env/A\ud800B\udc00C}";
        Assert.True(SecretReference.TryParse(hostile, out _), "the fixture must be a WELL-FORMED reference");

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurityWithPassphrase(hostile)), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientKeyPassword);

            Assert.Contains("\\ud800", ex.Message, StringComparison.Ordinal);
            Assert.Contains("\\udc00", ex.Message, StringComparison.Ordinal);
            AssertNoLoneSurrogate(ex.Message);

            // Truncation is NOT what did it: the reference is well under the cap.
            Assert.DoesNotContain("...", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// U+2028/U+2029 and the bidi/format controls are escaped. They sit far above the C1 range, so
    /// the original predicate never saw them.
    /// </summary>
    /// <remarks>
    /// U+2028 LINE SEPARATOR is the measured one and it is YAML-REACHABLE — the schema pattern's
    /// path class <c>[^}]+</c> admits it — and it is a LINE BREAK in HTML, where #371 records
    /// <c>HtmlRenderer.HtmlEscape</c> passing control characters straight through. So a crafted
    /// reference forges an apparent line of engine output in the report, which is exactly the
    /// hazard the C0 arm exists to prevent, reached by a character the C0 arm cannot see. The bidi
    /// controls reorder what a reader SEES without changing what a machine reads.
    /// </remarks>
    /// <remarks>
    /// The rows carry CODE POINTS rather than characters, and the expected escape is DERIVED from
    /// the code point rather than restated beside it. An invisible character typed as a literal
    /// into a row is invisible to a reader of this file too, and a row whose declared character
    /// and declared escape disagreed would assert nothing while looking thorough.
    /// </remarks>
    [Theory]
    [InlineData(0x2028)] // LINE SEPARATOR - the measured one, and YAML-reachable.
    [InlineData(0x2029)] // PARAGRAPH SEPARATOR.
    [InlineData(0x202A)] // LEFT-TO-RIGHT EMBEDDING.
    [InlineData(0x202E)] // RIGHT-TO-LEFT OVERRIDE.
    [InlineData(0x2066)] // LEFT-TO-RIGHT ISOLATE.
    [InlineData(0x2069)] // POP DIRECTIONAL ISOLATE.
    [InlineData(0x200E)] // LEFT-TO-RIGHT MARK.
    [InlineData(0x200F)] // RIGHT-TO-LEFT MARK.
    [InlineData(0x00AD)] // SOFT HYPHEN - a format control far below the invisible BMP block.
    public void For_AReferenceCarryingALineOrFormatControl_EscapesIt(int codePoint)
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "pilot-passphrase");

        var hostileChar = (char)codePoint;
        var escaped = "\\u" + codePoint.ToString("x4", CultureInfo.InvariantCulture);
        var hostile = "${secret:env/A" + hostileChar + "B}";
        Assert.True(SecretReference.TryParse(hostile, out _), "the fixture must be a WELL-FORMED reference");

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurityWithPassphrase(hostile)), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientKeyPassword);

            Assert.Contains(escaped, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(hostileChar.ToString(), ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The DELIMITING apostrophe is escaped. The quoting helper supplies the surrounding quotes, so
    /// an apostrophe inside the reference closes them early and renders one quoted token as two
    /// apparently separate ones.
    /// </summary>
    [Fact]
    public void For_AReferenceCarryingAnApostrophe_EscapesTheDelimiterRatherThanSplittingTheToken()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "pilot-passphrase");

        const string hostile = "${secret:env/A'B}";
        Assert.True(SecretReference.TryParse(hostile, out _), "the fixture must be a WELL-FORMED reference");

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurityWithPassphrase(hostile)), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientKeyPassword);

            // The whole token, quoted ONCE, with the inner apostrophe escaped — asserted as one
            // string rather than as two independent facts, because what regressed is the token's
            // INTEGRITY and a pair of contains-checks would not see it break.
            Assert.Contains(
                "'${secret:env/A\\u0027B}'", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("'${secret:env/A'B}'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    // ── Every resolver fault is classified, not just SecretResolutionException (MINOR-4) ───────

    /// <summary>
    /// A resolver fault that is NOT a <c>SecretResolutionException</c> is still classified as a
    /// <c>SecurityMaterialException</c>.
    /// </summary>
    /// <remarks>
    /// The parameter is the INTERFACE, so the live shapes are an <c>ObjectDisposedException</c>
    /// from a Vault client, anything <c>HttpVaultKvClient</c> does not wrap, and whatever a
    /// third-party <c>ISecretAccessor</c> raises. An escaping exception leaves the probe's
    /// client-identity load by a path this repository has MEASURED and documented at
    /// <c>SecuredEndpointProbe</c>: a stack trace instead of a verdict, with no report artefacts
    /// and a non-taxonomy exit code.
    /// </remarks>
    [Theory]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(HttpRequestException))]
    public void For_ANonSecretResolutionFaultFromTheAccessor_IsClassifiedRatherThanEscaping(
        Type faultType)
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "pilot-passphrase");

        var fault = (Exception)Activator.CreateInstance(faultType, "the resolver fell over")!;

        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService(
                "payments", MtlsSecurityWithPassphrase("${secret:vault/kv/client#pass}")),
            bed.SuiteDirectory,
            new FaultingSecretAccessor(fault));
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientKeyPassword);

            Assert.Contains(
                "environment.services.payments.security.clientKeyPassword",
                ex.Message,
                StringComparison.Ordinal);
            Assert.Contains("could not be resolved", ex.Message, StringComparison.Ordinal);

            // The original fault is preserved rather than swallowed.
            Assert.Same(fault, ex.InnerException);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Cancellation is NOT reclassified: it must reach the run's cancellation plumbing as itself.
    /// </summary>
    [Fact]
    public void For_ACancelledResolution_PropagatesRatherThanBecomingAMaterialFault()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "pilot-passphrase");

        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService(
                "payments", MtlsSecurityWithPassphrase("${secret:vault/kv/client#pass}")),
            bed.SuiteDirectory,
            new FaultingSecretAccessor(new OperationCanceledException()));
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            Assert.Throws<OperationCanceledException>(() => certificates.ClientKeyPassword);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// This engine's OWN verdict-bearing type is not re-wrapped: a good diagnostic must not be
    /// buried inside a vaguer one.
    /// </summary>
    [Fact]
    public void For_AMaterialFaultRaisedByTheAccessor_IsNotReWrapped()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "pilot-passphrase");

        var fault = new SecurityMaterialException("an already-classified fault.");

        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService(
                "payments", MtlsSecurityWithPassphrase("${secret:vault/kv/client#pass}")),
            bed.SuiteDirectory,
            new FaultingSecretAccessor(fault));
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientKeyPassword);

            Assert.Same(fault, ex);
            Assert.DoesNotContain("could not be resolved", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The WIDER cap — the one applied to a nested resolver message — is pinned, exactly. Only the
    /// default was, which left the bound on text from an arbitrary third-party accessor as the one
    /// unpinned limit on this class's output.
    /// </summary>
    [Fact]
    public void For_AnOverlongResolverMessage_IsTruncatedAtTheResolverCap()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "pilot-passphrase");

        // One repeated character throughout, so the assertions are on HOW MUCH survived rather
        // than on where a word boundary happened to land.
        const int cap = 512;
        var fault = new InvalidOperationException(new string('Z', cap + 400));

        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService(
                "payments", MtlsSecurityWithPassphrase("${secret:vault/kv/client#pass}")),
            bed.SuiteDirectory,
            new FaultingSecretAccessor(fault));
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientKeyPassword);

            Assert.Contains("...", ex.Message, StringComparison.Ordinal);
            Assert.Contains(new string('Z', cap), ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('Z', cap + 1), ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    // ── Passphrase-section fixtures ───────────────────────────────────────────────────────────

    private static SecuritySpec MtlsSecurityWithPassphrase(string reference) =>
        MtlsSecurity("8443") with { ClientKeyPassword = reference };

    private static string EnvReference(string variable) => "${secret:env/" + variable + "}";

    private static SecretAccessor EnvironmentSecretAccessor() =>
        new SecretAccessor(new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));

    /// <summary>
    /// A real <c>SecretAccessor</c> over the Vault resolver with a stubbed transport, used for the
    /// one value the <c>env</c> source cannot express on Windows: the empty string.
    /// </summary>
    private static SecretAccessor VaultSecretAccessorReturning(string value) =>
        new SecretAccessor(
            new SecretSourceCatalog(
                new ISecretResolver[] { new VaultSecretResolver(new StubVaultKvClient(value)) }));

    /// <summary>
    /// An <see cref="ISecretAccessor"/> that throws on every call and counts them — the probe for
    /// REQ-009's "nothing resolves at <c>Build</c> time".
    /// </summary>
    /// <summary>
    /// The SUBSET direction of the shared source set, which nothing else pins: every source the
    /// validation pass knows must also be ACCEPTED by this run-time guard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The guard was changed to consult <c>ScenarioRunner.KnownSecretSources</c> so the two layers
    /// refuse identically. The <c>nosuchsource</c> row above pins the SUPERSET direction (the
    /// guard does not accept more than validate does). Nothing pinned the other way: silently
    /// dropping <c>vault</c> from that set would make every <c>${secret:vault/…}</c> passphrase
    /// fail at certificate-load time with a shape complaint about a reference whose shape is
    /// perfect — and the suite would still be green here.
    /// </para>
    /// <para>
    /// Asserted as "does NOT fail guard 2" rather than "loads successfully": resolution itself is
    /// out of scope (the accessor here throws by design), and pinning the guard's own verdict is
    /// what this test is for.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("env")]
    [InlineData("vault")]
    public void For_APassphraseNamingAnyValidateKnownSource_IsNotRefusedByTheShapeGuard(string source)
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        // The premise, measured rather than assumed: the validation pass knows this source.
        Assert.Contains(source, ScenarioRunner.KnownSecretSources);

        var accessor = SecurityConfigurationAccessor.Build(
            AstWithSecuredService("payments", MtlsSecurityWithPassphrase($"${{secret:{source}/KEY_PASS}}")),
            bed.SuiteDirectory,
            new ThrowingSecretAccessor());
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientKeyPassword);

            // It got PAST the shape/source guard and failed at resolution instead — which is the
            // ThrowingSecretAccessor doing its job, not this guard refusing a valid reference.
            Assert.DoesNotContain(
                "not a single, whole secret reference naming a resolvable source",
                ex.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    private sealed class ThrowingSecretAccessor : ISecretAccessor
    {
        internal int Calls { get; private set; }

        public SecretString Resolve(string reference)
        {
            Calls++;
            throw new SecretResolutionException(
                "env", "CLIENT_KEY_PASS", "this accessor exists to prove when resolution happens.");
        }
    }

    /// <summary>
    /// An <see cref="ISecretAccessor"/> that throws a CALLER-CHOSEN exception — the probe for the
    /// widened classification, which must hold for anything the interface's implementor raises and
    /// not only for <c>SecretResolutionException</c>.
    /// </summary>
    /// <remarks>
    /// The parameter really is the interface, so the shapes this stands in for are live: an
    /// <c>ObjectDisposedException</c> from a Vault client whose transport has been torn down,
    /// anything <c>HttpVaultKvClient</c> does not wrap, and whatever a third-party accessor
    /// supplied by an embedder decides to throw.
    /// </remarks>
    private sealed class FaultingSecretAccessor : ISecretAccessor
    {
        private readonly Exception _fault;

        internal FaultingSecretAccessor(Exception fault) => _fault = fault;

        public SecretString Resolve(string reference) => throw _fault;
    }

    /// <summary>
    /// A stub Vault transport returning one field with a caller-chosen value.
    /// </summary>
    private sealed class StubVaultKvClient : IVaultKvClient
    {
        private readonly string _value;

        internal StubVaultKvClient(string value) => _value = value;

        public IReadOnlyDictionary<string, string> ReadKeyValues(string kvPath) =>
            new Dictionary<string, string>(StringComparer.Ordinal) { ["pass"] = _value };
    }

    /// <summary>
    /// HALF a client pair fails closed rather than degrading to "present no identity".
    /// </summary>
    /// <remarks>
    /// Measured before this behaviour existed: a declared <c>mtls</c> profile with
    /// <c>clientKey</c> missing loaded no client certificate at all, and against a listener
    /// that REQUESTS but does not enforce one, the suite PASSED while presenting no identity.
    /// The JSON Schema closes this at authoring time (<c>profile: mtls</c> requires both
    /// fields), so it is unreachable from YAML — which is exactly the argument for fixing it
    /// here too: the only thing between an author and an unauthenticated pass must not be a
    /// layer the runtime never consults, and direct engine embedding bypasses the schema.
    /// </remarks>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void For_HalfDeclaredClientPair_FailsClosedRatherThanPresentingNoIdentity(
        bool declareCert, bool declareKey)
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var security = MtlsSecurity("8443") with
        {
            ClientCert = declareCert ? TestCertificateAuthority.ClientCertFileName : null,
            ClientKey = declareKey ? TestCertificateAuthority.ClientKeyFileName : null,
        };

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", security), bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.ClientCertificate);

            Assert.Contains(declareCert ? "clientKey" : "clientCert", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Certificate borrowing holds under concurrent resolution: every caller receives the SAME
    /// instances, and exactly one load happens however many resolve at once.
    /// </summary>
    /// <remarks>
    /// The production shape this stands in for is <c>--parallel</c>. Analysis says each scenario
    /// owns its own accessor and certificates — exactly one production
    /// <c>new ScriptGlobalVariables(</c> and one <c>SecurityConfigurationAccessor.Build</c> in
    /// <c>ScenarioRunner</c>, both per-scenario — so cross-scenario borrowing cannot arise; what
    /// CAN is many steps of one scenario resolving one target at once, which is what this
    /// exercises. The <see cref="Lazy{T}"/> guarding both loads is
    /// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> precisely for this, and
    /// nothing else in the type pins that choice.
    /// </remarks>
    [Fact]
    public async Task For_ResolvedConcurrently_HandsEveryCallerTheSameCertificateInstances()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurity("8443")), bed.SuiteDirectory);
        try
        {
            using var gate = new Barrier(8);
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                gate.SignalAndWait();
                var certificates = accessor.For("payments")!.Certificates!;
                return (certificates.ClientCertificate, certificates.CaCertificate);
            })));

            var (expectedClient, expectedCa) = results[0];
            Assert.NotNull(expectedClient);
            Assert.NotNull(expectedCa);

            foreach (var (client, ca) in results)
            {
                Assert.Same(expectedClient, client);
                Assert.Same(expectedCa, ca);
            }
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The accessor resolves declared paths against the base directory it is GIVEN, and two
    /// suite directories each holding their own <c>ca.pem</c> resolve to their own file.
    /// </summary>
    /// <remarks>
    /// This is the unit-level half of a defect measured end to end: on the multi-suite path the
    /// stage that validates containment and existence (<c>EnvironmentSecurityValidator</c>, via
    /// <c>ProviderPipeline.Compile</c>) was handed the per-scenario directory while this
    /// accessor was handed the suite-wide seed root — so a second suite's
    /// <c>caCert: certs/ca.pem</c> validated against its own directory and then loaded the
    /// FIRST suite's file. That INVERTS the trust decision: the run rejected the anchor the
    /// suite declared and accepted one it never named. <c>ScenarioRunner</c> now passes
    /// <c>scriptBaseDirectory ?? seedBaseDirectory</c> — the same expression
    /// <c>BuildReproducibilityEnvelope</c> uses, and the only one that equals "whatever
    /// <c>ProviderPipeline.Compile</c> was handed" on BOTH the multi-suite path (which threads
    /// a per-scenario directory) and the single-scenario paths (which never pass one at all, so
    /// bare <c>scriptBaseDirectory</c> would fall back to the process working directory).
    /// </remarks>
    [Fact]
    public void Build_WithTwoSuiteDirectories_ResolvesEachAgainstTheOneItWasGiven()
    {
        using var suiteA = TestCertificateAuthority.CreateSuiteDirectory();
        using var suiteB = TestCertificateAuthority.CreateSuiteDirectory();

        var ast = AstWithSecuredService("payments", MtlsSecurity("8443"));

        var accessorA = BuildWithNoSecretAccessor(ast, suiteA.SuiteDirectory);
        var accessorB = BuildWithNoSecretAccessor(ast, suiteB.SuiteDirectory);
        try
        {
            var certificatesA = accessorA.For("payments")!.Certificates!;
            var certificatesB = accessorB.For("payments")!.Certificates!;

            Assert.Equal(
                Path.Combine(suiteA.SuiteDirectory, TestCertificateAuthority.CaFileName),
                certificatesA.CaCertificatePath);
            Assert.Equal(
                Path.Combine(suiteB.SuiteDirectory, TestCertificateAuthority.CaFileName),
                certificatesB.CaCertificatePath);

            // The trust decision follows the anchor each was given, and only that one. Each
            // suite's own server certificate validates under its own accessor and is rejected
            // by the other — the inversion, stated as an assertion.
            using var serverA = new X509Certificate2(suiteA.ServerCertificate.Export(X509ContentType.Cert));
            using var serverB = new X509Certificate2(suiteB.ServerCertificate.Export(X509ContentType.Cert));

            Assert.True(certificatesA.TrustsRemoteCertificate(
                serverA, null, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(certificatesA.TrustsRemoteCertificate(
                serverB, null, SslPolicyErrors.RemoteCertificateChainErrors));

            Assert.True(certificatesB.TrustsRemoteCertificate(
                serverB, null, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(certificatesB.TrustsRemoteCertificate(
                serverA, null, SslPolicyErrors.RemoteCertificateChainErrors));
        }
        finally
        {
            (accessorA as IDisposable)?.Dispose();
            (accessorB as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The defence-in-depth containment re-check (REQ-003), reached only by a caller that
    /// skipped <c>EnvironmentSecurityValidator</c> entirely — which is exactly what it is for.
    /// </summary>
    /// <remarks>
    /// Be honest about its reach: it is MEASURED not to catch a base-directory divergence,
    /// because a path resolved against the wrong base is still contained within THAT base. It
    /// fails closed for an unvalidated AST; the fix for a divergence is the base-directory
    /// parameter, not this.
    /// </remarks>
    [Fact]
    public void For_DeclaredPathEscapingTheSuiteDirectory_FailsClosedAtLoadTime()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        var nested = Path.Combine(bed.SuiteDirectory, "nested");
        Directory.CreateDirectory(nested);

        // An AST that never went through EnvironmentSecurityValidator, declaring a traversal
        // out of the (nested) suite directory that lands on a real file.
        var security = new SecuritySpec(
            Profile: "tls",
            Endpoint: "8443",
            CaCert: Path.Combine("..", TestCertificateAuthority.CaFileName),
            ClientCert: null,
            ClientKey: null,
            ServerArtifacts: null);

        var accessor = BuildWithNoSecretAccessor(AstWithSecuredService("payments", security), nested);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;
            var ex = Assert.Throws<SecurityMaterialException>(() => certificates.CaCertificate);

            Assert.Contains("outside the suite directory", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(nested, ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The same backstop guards the PATH view, not only the object view. Slice E's librdkafka
    /// consumer reads exactly these three properties (<c>SslCaLocation</c>,
    /// <c>SslCertificateLocation</c>, <c>SslKeyLocation</c>) and never touches
    /// <see cref="ISecurityCertificateMaterial.CaCertificate"/> or
    /// <see cref="ISecurityCertificateMaterial.ClientCertificate"/> at all, so a guard present
    /// only on the loaders would be absent from the one view whose consumer it exists for.
    /// </summary>
    /// <remarks>
    /// Measured before the getters checked containment: all three returned the resolved
    /// absolute escaping path, no exception thrown. The message shape is the loaders' own —
    /// declared relative path, never the resolved absolute one, because it reaches the §14
    /// event stream through a provider's general catch where no scrubber can redact it.
    /// </remarks>
    [Fact]
    public void For_DeclaredPathsEscapingTheSuiteDirectory_FailClosedOnEveryPathGetterToo()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        var nested = Path.Combine(bed.SuiteDirectory, "nested");
        Directory.CreateDirectory(nested);

        // An AST that never went through EnvironmentSecurityValidator, declaring a traversal
        // out of the (nested) suite directory on ALL THREE path-valued fields, each landing on
        // a real file — so nothing but the containment check can be what rejects them.
        var security = new SecuritySpec(
            Profile: "mtls",
            Endpoint: "8443",
            CaCert: Path.Combine("..", TestCertificateAuthority.CaFileName),
            ClientCert: Path.Combine("..", TestCertificateAuthority.ClientCertFileName),
            ClientKey: Path.Combine("..", TestCertificateAuthority.ClientKeyFileName),
            ServerArtifacts: null);

        var accessor = BuildWithNoSecretAccessor(AstWithSecuredService("payments", security), nested);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;

            AssertFailsClosed("caCert", () => certificates.CaCertificatePath);
            AssertFailsClosed("clientCert", () => certificates.ClientCertificatePath);
            AssertFailsClosed("clientKey", () => certificates.ClientKeyPath);

            void AssertFailsClosed(string fieldName, Func<string?> getter)
            {
                var ex = Assert.Throws<SecurityMaterialException>(() => getter());

                Assert.Contains(
                    $"environment.services.payments.security.{fieldName}",
                    ex.Message,
                    StringComparison.Ordinal);
                Assert.Contains("outside the suite directory", ex.Message, StringComparison.Ordinal);

                // SEC-7: the DECLARED form, and no host path — neither the suite directory the
                // accessor was given nor the directory the traversal escapes into.
                Assert.DoesNotContain(nested, ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(bed.SuiteDirectory, ex.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The control for the test above: a CONTAINED declaration returns from all three path
    /// getters normally, so the backstop rejects the escape rather than the path view.
    /// </summary>
    [Fact]
    public void For_ContainedDeclaredPaths_ReturnFromEveryPathGetterWithoutThrowing()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var accessor = BuildWithNoSecretAccessor(
            AstWithSecuredService("payments", MtlsSecurity("8443")),
            bed.SuiteDirectory);
        try
        {
            var certificates = accessor.For("payments")!.Certificates!;

            Assert.Equal(
                Path.Combine(bed.SuiteDirectory, TestCertificateAuthority.CaFileName),
                certificates.CaCertificatePath);
            Assert.Equal(
                Path.Combine(bed.SuiteDirectory, TestCertificateAuthority.ClientCertFileName),
                certificates.ClientCertificatePath);
            Assert.Equal(
                Path.Combine(bed.SuiteDirectory, TestCertificateAuthority.ClientKeyFileName),
                certificates.ClientKeyPath);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// A service and a dependency sharing one name where BOTH declare security has no
    /// answer — a step's <c>target</c> is a bare name with no kind discriminator — so it fails
    /// closed rather than handing a step the other one's certificates. It fails on RESOLUTION,
    /// not on construction: raising it while building the accessor would fail a whole scenario
    /// for an ambiguity no step may reference, from a site outside the runner's own disposal
    /// <c>finally</c>. Raised from <c>For</c>, it lands inside a provider's guarded region and
    /// becomes a step-scoped environment error.
    /// </summary>
    [Fact]
    public void For_ServiceAndDependencyOfTheSameNameBothDeclaringSecurity_FailsClosedOnResolution()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        var security = MtlsSecurity("9093");

        var ast = new ScenarioAst(
            Metadata: null,
            Environment: new EnvironmentSpec(
                Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
                {
                    ["events"] = new ServiceSpec("acme/api:1", null, null, 8080, null) { Security = security },
                },
                Dependencies: new Dictionary<string, DependencySpec>(StringComparer.Ordinal)
                {
                    ["events"] = new DependencySpec("kafka", null, null) { Security = security },
                },
                Seed: null,
                ImageRegistry: null,
                ImagePullPolicy: null),
            Variables: new Dictionary<string, string>(StringComparer.Ordinal),
            Steps: Array.Empty<StepNode>());

        var accessor = BuildWithNoSecretAccessor(ast, bed.SuiteDirectory);
        try
        {
            var ex = Assert.Throws<SecurityMaterialException>(() => accessor.For("events"));
            Assert.Contains("'events'", ex.Message, StringComparison.Ordinal);

            // An UNAMBIGUOUS name in the same suite still resolves normally.
            Assert.Null(accessor.For("something-else"));
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }
}
