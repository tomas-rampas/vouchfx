// REQ-014 — the per-target client-configuration accessor
// (authenticated-infrastructure-mtls, slice D).
//
// Non-Docker. Every certificate here is generated in-process (CertificateRequest) and written
// to a temp suite directory, so these tests exercise the REAL load path — including the
// PKCS#12 round trip the accessor performs — against real PEM files, not a stub.
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    /// REQ-014's first acceptance: for a suite declaring a client certificate on a service,
    /// BOTH views resolve and are usable — the host-path view (which librdkafka accepts and
    /// certificate objects do not) and the <see cref="X509Certificate2"/> view (which
    /// <c>HttpClientHandler</c> accepts and paths do not).
    /// </summary>
    [Fact]
    public void For_ServiceDeclaringAClientCertificate_ReturnsBothViewsUsable()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var accessor = SecurityConfigurationAccessor.Build(
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

        var accessor = SecurityConfigurationAccessor.Build(
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
        var accessor = SecurityConfigurationAccessor.Build(
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

        var accessor = SecurityConfigurationAccessor.Build(
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

        var accessor = SecurityConfigurationAccessor.Build(ast, Directory.GetCurrentDirectory());

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

        var accessor = SecurityConfigurationAccessor.Build(
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

        var accessor = SecurityConfigurationAccessor.Build(
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

        var accessor = SecurityConfigurationAccessor.Build(
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

        var accessor = SecurityConfigurationAccessor.Build(
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

        var accessor = SecurityConfigurationAccessor.Build(
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

        var accessor = SecurityConfigurationAccessor.Build(
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

        var accessor = SecurityConfigurationAccessor.Build(
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

        var accessor = SecurityConfigurationAccessor.Build(
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

        var accessor = SecurityConfigurationAccessor.Build(
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

        var accessor = SecurityConfigurationAccessor.Build(
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

        var accessor = SecurityConfigurationAccessor.Build(
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

        var accessorA = SecurityConfigurationAccessor.Build(ast, suiteA.SuiteDirectory);
        var accessorB = SecurityConfigurationAccessor.Build(ast, suiteB.SuiteDirectory);
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

        var accessor = SecurityConfigurationAccessor.Build(AstWithSecuredService("payments", security), nested);
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

        var accessor = SecurityConfigurationAccessor.Build(AstWithSecuredService("payments", security), nested);
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

        var accessor = SecurityConfigurationAccessor.Build(
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

        var accessor = SecurityConfigurationAccessor.Build(ast, bed.SuiteDirectory);
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
