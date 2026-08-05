// Vouchfx.TestSupport — TestCertificateAuthority (authenticated-infrastructure-mtls, slice D).
//
// A private certificate authority generated in-process, plus the PEM files a suite would
// declare under `security:`. Shared by the REQ-014 accessor tests
// (Vouchfx.Engine.Runtime.Tests/SecurityConfigurationAccessorTests) and the REQ-024 execution
// tests (Vouchfx.Engine.Runtime.Tests/HttpsClientCertificateTests) so both exercise the SAME
// material and a fault in one is not masked by a different fixture in the other.
//
// Why generated rather than checked in: a committed certificate expires, and a fixture that
// starts failing on a date nobody chose is worse than no fixture. Generation costs a few
// milliseconds and pins validity to the run.
//
// Deliberately references NO Vouchfx type, preserving this project's stated property (see the
// csproj comment): it hands back file names and certificate objects, and each test project
// builds its own SecuritySpec from them.
using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Vouchfx.TestSupport;

/// <summary>
/// Generates a private CA, a server certificate for <c>localhost</c>, and a client certificate,
/// writing the PEM files a suite's <c>security:</c> block would declare into a temporary suite
/// directory.
/// </summary>
public static class TestCertificateAuthority
{
    /// <summary>Common name of the generated root CA.</summary>
    public const string CaSubjectCommonName = "Vouchfx Test Root CA";

    /// <summary>Common name of the generated client certificate.</summary>
    public const string ClientSubjectCommonName = "vouchfx-test-client";

    /// <summary>Common name of the generated server certificate.</summary>
    public const string ServerSubjectCommonName = "localhost";

    /// <summary>File name of the CA (trust anchor) PEM inside the suite directory.</summary>
    public const string CaFileName = "ca.pem";

    /// <summary>File name of the client certificate PEM inside the suite directory.</summary>
    public const string ClientCertFileName = "client.pem";

    /// <summary>File name of the client private-key PEM inside the suite directory.</summary>
    public const string ClientKeyFileName = "client-key.pem";

    /// <summary>
    /// File name of the broker's PEM key store — the server private key followed by its
    /// certificate, the single-file layout Kafka's <c>ssl.keystore.type=PEM</c> expects.
    /// </summary>
    public const string BrokerKeystoreFileName = "kafka.keystore.pem";

    /// <summary>
    /// File name of the broker's PEM trust store — the CA the broker validates presented client
    /// certificates against, which is the same anchor <see cref="CaFileName"/> holds.
    /// </summary>
    public const string BrokerTruststoreFileName = "kafka.truststore.pem";

    /// <summary>
    /// Common name of the FOREIGN root written by
    /// <see cref="OverwriteWithForeignClientIdentity"/> — deliberately distinct from every other
    /// subject this class mints.
    /// </summary>
    /// <remarks>
    /// The distinctness is operational, not cosmetic. Chain building on Windows can leave issuer
    /// certificates in the user's intermediate-CA store, and a stale entry sharing a subject with a
    /// live fixture's issuer produces chain failures that look like defects in whatever ran next —
    /// a trap this repository has already paid for once with a subject that WAS shared. A root
    /// nobody trusts, whose whole purpose is to be rejected, is the last one that should be
    /// confusable with the anchor a suite declares.
    /// </remarks>
    public const string ForeignCaSubjectCommonName = "Vouchfx Test Foreign Root CA";

    /// <summary>Common name of the foreign client certificate.</summary>
    public const string ForeignClientSubjectCommonName = "vouchfx-test-foreign-client";

    private static readonly Oid s_serverAuth = new("1.3.6.1.5.5.7.3.1");
    private static readonly Oid s_clientAuth = new("1.3.6.1.5.5.7.3.2");

    /// <summary>
    /// Creates a temporary suite directory containing <see cref="CaFileName"/>,
    /// <see cref="ClientCertFileName"/> and <see cref="ClientKeyFileName"/>, together with a
    /// server certificate (private key included) chaining to the same CA.
    /// </summary>
    public static TestCertificateBed CreateSuiteDirectory()
    {
        var suiteDirectory = Path.Combine(
            Path.GetTempPath(), "vouchfx-mtls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(suiteDirectory);

        var now = DateTimeOffset.UtcNow;

        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(
            $"CN={CaSubjectCommonName}", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, 0, critical: true));
        caRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        caRequest.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, critical: false));

        using var ca = caRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));

        var serverCertificate = IssueLeaf(ca, ServerSubjectCommonName, includeLocalhostSans: true, now);
        var clientCertificate = IssueLeaf(ca, ClientSubjectCommonName, includeLocalhostSans: false, now);

        // Two extra server leaves from the SAME anchor, for the extended-key-usage arms of the
        // trust decision (slice D fix round one). Issued here rather than by a later helper
        // because the bed deliberately does not retain the CA's private key — see below.
        var clientAuthOnlyLeaf = IssueLeaf(
            ca,
            ServerSubjectCommonName,
            includeLocalhostSans: true,
            now,
            ekus: new OidCollection { s_clientAuth });
        var noEkuLeaf = IssueLeaf(
            ca, ServerSubjectCommonName, includeLocalhostSans: true, now, ekus: null, defaultEkus: false);

        try
        {
            File.WriteAllText(Path.Combine(suiteDirectory, CaFileName), ca.ExportCertificatePem());
            File.WriteAllText(
                Path.Combine(suiteDirectory, ClientCertFileName), clientCertificate.Certificate.ExportCertificatePem());
            File.WriteAllText(Path.Combine(suiteDirectory, ClientKeyFileName), clientCertificate.PrivateKeyPem);

            // The CA is re-created from its own DER so the bed does not hold the CA's PRIVATE
            // key alive: a trust anchor never needs one, and keeping one around invites a test
            // to sign something the engine should have refused.
            var anchor = new X509Certificate2(ca.Export(X509ContentType.Cert));

            return new TestCertificateBed(
                suiteDirectory,
                anchor,
                serverCertificate.Loadable,
                clientAuthOnlyLeaf.Certificate,
                noEkuLeaf.Certificate);
        }
        finally
        {
            clientCertificate.Certificate.Dispose();
            clientCertificate.Loadable.Dispose();
            clientAuthOnlyLeaf.Loadable.Dispose();
            noEkuLeaf.Loadable.Dispose();
        }
    }

    /// <summary>
    /// Writes into <paramref name="suiteDirectory"/> (created if absent) everything a mutual-TLS
    /// Kafka broker fixture needs on BOTH sides of the connection: the client material a suite
    /// declares under <c>security:</c> (<see cref="CaFileName"/>, <see cref="ClientCertFileName"/>,
    /// <see cref="ClientKeyFileName"/>) and the server material it delivers into the broker's own
    /// container under <c>security.serverArtifacts</c> (<see cref="BrokerKeystoreFileName"/>,
    /// <see cref="BrokerTruststoreFileName"/>) — all issued by ONE generated CA.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The directory is a PARAMETER, where <see cref="CreateSuiteDirectory"/> names its own: a
    /// fixture built here has to be runnable by path from outside the process that wrote it — a
    /// CLI drill against the suite a test materialised — and a randomly-named directory cannot be
    /// named afterwards.
    /// </para>
    /// <para>
    /// It returns NOTHING, where <see cref="CreateSuiteDirectory"/> returns a bed, and that is the
    /// consequence of the same difference: the caller supplied the directory, so it already knows
    /// the only thing a bed could tell it, and the file NAMES are the constants above — read by
    /// this method and by the caller's own suite YAML, which is where the single source of truth
    /// for them already lives. A bed exposing <c>BrokerKeystorePath</c> beside those constants
    /// would be a second spelling of the same fact, not a consolidation of it.
    /// </para>
    /// <para>
    /// <strong>Why PEM rather than a Java key store.</strong> Kafka has accepted
    /// <c>ssl.keystore.type=PEM</c> since 2.7, and <c>confluentinc/cp-kafka:7.6.1</c> ships Kafka
    /// 3.6, so the broker reads exactly the format .NET can write. A JKS would need
    /// <c>keytool</c> — a JDK on the test host, or a throwaway container per run — to produce
    /// material this method emits in <strong>~200 ms</strong> (measured warm: 178–206 ms for the
    /// three RSA-2048 key generations, issuance and PEM export) with no external tooling at all.
    /// Nothing about the engine path under test changes: <c>ServerArtifactInjection</c> copies
    /// bytes through the container runtime's own API and never inspects them (EDGE-007 is why it
    /// sets <c>SourcePath</c> and never <c>Contents</c>), so a PEM store exercises the same copy a
    /// JKS would.
    /// </para>
    /// <para>
    /// <strong>Line endings are LF, deliberately.</strong> Both files are read by a JVM inside a
    /// Linux container, and the broker's key store is additionally concatenated by hand here.
    /// .NET's own PEM writer emits <c>\n</c> and <see cref="File.WriteAllText(string,string)"/>
    /// performs no translation, so the only way CRLF could appear is a separator written as
    /// <see cref="Environment.NewLine"/> — which is why every separator below is an explicit
    /// <c>"\n"</c>.
    /// </para>
    /// <para>
    /// The server leaf carries <c>localhost</c>/<c>127.0.0.1</c> subject alternative names,
    /// because the address the engine reaches an engine-started container on is the published
    /// loopback endpoint and the probe does not relax hostname verification (REQ-024).
    /// </para>
    /// </remarks>
    public static void WriteKafkaBrokerSuiteDirectory(string suiteDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suiteDirectory);
        Directory.CreateDirectory(suiteDirectory);

        var now = DateTimeOffset.UtcNow;

        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest(
            $"CN={CaSubjectCommonName}", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddCaExtensions(caRequest);
        using var ca = caRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));

        var server = IssueLeaf(ca, ServerSubjectCommonName, includeLocalhostSans: true, now);
        var client = IssueLeaf(ca, ClientSubjectCommonName, includeLocalhostSans: false, now);

        try
        {
            var caPem = ca.ExportCertificatePem() + "\n";

            File.WriteAllText(Path.Combine(suiteDirectory, CaFileName), caPem);
            File.WriteAllText(
                Path.Combine(suiteDirectory, ClientCertFileName),
                client.Certificate.ExportCertificatePem() + "\n");

            var clientKeyPath = Path.Combine(suiteDirectory, ClientKeyFileName);
            File.WriteAllText(clientKeyPath, client.PrivateKeyPem);
            RestrictToOwner(clientKeyPath);

            // Kafka's PEM key store is one file: the private key, then the certificate chain.
            var keystorePath = Path.Combine(suiteDirectory, BrokerKeystoreFileName);
            File.WriteAllText(
                keystorePath,
                server.PrivateKeyPem + "\n" + server.Certificate.ExportCertificatePem() + "\n");
            RestrictToOwner(keystorePath);

            // The broker's trust store is the same anchor the suite declares as caCert, which is
            // what makes ssl.client.auth=required accept the client certificate above.
            File.WriteAllText(Path.Combine(suiteDirectory, BrokerTruststoreFileName), caPem);
        }
        finally
        {
            server.Certificate.Dispose();
            server.Loadable.Dispose();
            client.Certificate.Dispose();
            client.Loadable.Dispose();
        }
    }

    /// <summary>
    /// Creates a suite directory whose declared <c>ca.pem</c> is an OFFLINE ROOT, with a
    /// separate issuing INTERMEDIATE and a server leaf issued by that intermediate — the normal
    /// two-tier enterprise PKI shape, which cannot validate unless the peer-supplied
    /// intermediate reaches the rebuilt chain's <c>ExtraStore</c>.
    /// </summary>
    public static TestTwoTierBed CreateTwoTierSuiteDirectory()
    {
        var suiteDirectory = Path.Combine(
            Path.GetTempPath(), "vouchfx-mtls2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(suiteDirectory);

        var now = DateTimeOffset.UtcNow;

        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest(
            "CN=Vouchfx Test Offline Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddCaExtensions(rootRequest);
        using var root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));

        using var intermediateKey = RSA.Create(2048);
        var intermediateRequest = new CertificateRequest(
            "CN=Vouchfx Test Issuing Intermediate",
            intermediateKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        AddCaExtensions(intermediateRequest);
        using var intermediateSigned = intermediateRequest.Create(
            root, now.AddDays(-1), now.AddDays(1), RandomNumberGenerator.GetBytes(8));

        // Retain the intermediate's key only long enough to issue the leaf.
        using var intermediateWithKey = intermediateSigned.CopyWithPrivateKey(intermediateKey);
        using var intermediateSigner = new X509Certificate2(
            intermediateWithKey.Export(X509ContentType.Pkcs12));
        var leaf = IssueLeaf(intermediateSigner, ServerSubjectCommonName, includeLocalhostSans: true, now);

        File.WriteAllText(Path.Combine(suiteDirectory, CaFileName), root.ExportCertificatePem());

        return new TestTwoTierBed(
            suiteDirectory,
            new X509Certificate2(root.Export(X509ContentType.Cert)),
            new X509Certificate2(intermediateSigned.Export(X509ContentType.Cert)),
            leaf.Certificate,
            leaf.Loadable);
    }

    /// <summary>
    /// Creates a SELF-SIGNED certificate authority and a <c>localhost</c> leaf beneath it — the
    /// attacker's side of the <c>ExtraStore</c> negative control. Neither must ever become
    /// trusted by virtue of being handed to the engine as a peer-supplied "intermediate".
    /// </summary>
    public static (X509Certificate2 Root, X509Certificate2 Leaf) CreateImposterAuthority()
    {
        var now = DateTimeOffset.UtcNow;

        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Imposter Root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddCaExtensions(request);
        using var imposter = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));

        var leaf = IssueLeaf(imposter, ServerSubjectCommonName, includeLocalhostSans: true, now);
        leaf.Loadable.Dispose();

        return (new X509Certificate2(imposter.Export(X509ContentType.Cert)), leaf.Certificate);
    }

    /// <summary>
    /// Creates an anchor plus a leaf carrying an Authority Information Access <c>caIssuers</c>
    /// extension pointing at <paramref name="caIssuersUrl"/>, and whose ISSUER is deliberately
    /// absent — so a chain builder that has not had certificate downloads disabled must go to
    /// that URL looking for the missing link.
    /// </summary>
    public static TestAiaBed CreateAuthorityInfoAccessBed(string caIssuersUrl)
    {
        var suiteDirectory = Path.Combine(
            Path.GetTempPath(), "vouchfx-aia-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(suiteDirectory);

        var now = DateTimeOffset.UtcNow;

        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest(
            "CN=Vouchfx AIA Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddCaExtensions(rootRequest);
        using var root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));

        using var intermediateKey = RSA.Create(2048);
        var intermediateRequest = new CertificateRequest(
            "CN=Vouchfx AIA Intermediate", intermediateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddCaExtensions(intermediateRequest);
        using var intermediateSigned = intermediateRequest.Create(
            root, now.AddDays(-1), now.AddDays(1), RandomNumberGenerator.GetBytes(8));
        using var intermediateWithKey = intermediateSigned.CopyWithPrivateKey(intermediateKey);
        using var intermediateSigner = new X509Certificate2(
            intermediateWithKey.Export(X509ContentType.Pkcs12));

        var leaf = IssueLeaf(
            intermediateSigner, ServerSubjectCommonName, includeLocalhostSans: true, now, aiaUrl: caIssuersUrl);
        leaf.Loadable.Dispose();

        File.WriteAllText(Path.Combine(suiteDirectory, CaFileName), root.ExportCertificatePem());

        return new TestAiaBed(
            suiteDirectory, new X509Certificate2(root.Export(X509ContentType.Cert)), leaf.Certificate);
    }

    /// <summary>
    /// Replaces an existing suite directory's <see cref="ClientCertFileName"/> and
    /// <see cref="ClientKeyFileName"/> with a well-formed client identity issued by a SECOND,
    /// unrelated certificate authority — leaving every other file, and the suite's own YAML,
    /// untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The point of the variant this serves: the declared files EXIST, so the host-side preflight
    /// existence check passes and the run proceeds to build a topology and probe it. What is wrong
    /// is only their CONTENT — an identity no broker trusting the suite's own anchor can accept.
    /// The material is deliberately well-formed rather than corrupt: garbage bytes would be
    /// rejected while loading, by this side, and would prove nothing about the peer. Only a valid
    /// certificate from the wrong issuer forces the REMOTE end to make the decision.
    /// </para>
    /// <para>
    /// The foreign root is self-signed with no intermediate — the shortest chain that can be
    /// refused — and carries <see cref="ForeignCaSubjectCommonName"/>, which no other subject this
    /// class mints shares.
    /// </para>
    /// </remarks>
    /// <param name="suiteDirectory">
    /// A directory already populated by <see cref="WriteKafkaBrokerSuiteDirectory"/>; only the two
    /// client files are rewritten.
    /// </param>
    public static void OverwriteWithForeignClientIdentity(string suiteDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suiteDirectory);

        var now = DateTimeOffset.UtcNow;

        using var foreignCaKey = RSA.Create(2048);
        var foreignCaRequest = new CertificateRequest(
            $"CN={ForeignCaSubjectCommonName}",
            foreignCaKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        AddCaExtensions(foreignCaRequest);
        using var foreignCa = foreignCaRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));

        var client = IssueLeaf(
            foreignCa, ForeignClientSubjectCommonName, includeLocalhostSans: false, now);

        try
        {
            File.WriteAllText(
                Path.Combine(suiteDirectory, ClientCertFileName),
                client.Certificate.ExportCertificatePem() + "\n");

            var keyPath = Path.Combine(suiteDirectory, ClientKeyFileName);
            File.WriteAllText(keyPath, client.PrivateKeyPem);
            RestrictToOwner(keyPath);
        }
        finally
        {
            client.Certificate.Dispose();
            client.Loadable.Dispose();
        }
    }

    /// <summary>
    /// Narrows a private-key file to owner read/write where the platform has POSIX permissions.
    /// </summary>
    /// <remarks>
    /// These files sit in a world-readable system temp directory and outlive the run that wrote
    /// them, so the default mode is worth narrowing even for throwaway test material — the habit is
    /// what stops a copy of this fixture from leaking a real key. No-op on Windows, where
    /// <see cref="File.SetUnixFileMode(string, UnixFileMode)"/> throws
    /// <see cref="PlatformNotSupportedException"/>; the equivalent there is an ACL edit this
    /// fixture deliberately does not attempt, because a test that starts rewriting ACLs is a larger
    /// hazard than the one it closes.
    /// </remarks>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void AddCaExtensions(CertificateRequest request)
    {
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
    }

    /// <summary>
    /// Builds an Authority Information Access extension carrying a single <c>caIssuers</c>
    /// access description whose location is <paramref name="url"/>. Hand-encoded because .NET 8
    /// exposes no builder for this extension.
    /// </summary>
    private static X509Extension AuthorityInfoAccess(string url)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier("1.3.6.1.5.5.7.48.2"); // id-ad-caIssuers
                writer.WriteCharacterString(
                    UniversalTagNumber.IA5String, url, new Asn1Tag(TagClass.ContextSpecific, 6));
            }
        }

        return new X509Extension("1.3.6.1.5.5.7.1.1", writer.Encode(), critical: false);
    }

    /// <summary>
    /// Creates a leaf certificate under an UNRELATED, throwaway root — the negative control for
    /// any trust decision (it must never validate against the bed's own anchor).
    /// </summary>
    public static X509Certificate2 CreateUnrelatedLeaf()
    {
        var now = DateTimeOffset.UtcNow;

        using var otherCaKey = RSA.Create(2048);
        var otherCaRequest = new CertificateRequest(
            "CN=Unrelated Root", otherCaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        otherCaRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, 0, critical: true));
        using var otherCa = otherCaRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));

        var leaf = IssueLeaf(otherCa, "unrelated-leaf", includeLocalhostSans: true, now);
        leaf.Loadable.Dispose();
        return leaf.Certificate;
    }

    /// <param name="ekus">
    /// The extended key usages to stamp on the leaf. Defaults (when the caller passes nothing)
    /// to BOTH <c>serverAuth</c> and <c>clientAuth</c>: SChannel refuses a client certificate
    /// with no <c>clientAuth</c> EKU and a server certificate with no <c>serverAuth</c> EKU, so
    /// the bed's own leaves need both to be usable on either side. Pass an explicit set for the
    /// EKU arms of the trust decision, or <see langword="null"/> for a leaf carrying NO
    /// extended-key-usage extension at all (which means unconstrained, not unusable).
    /// </param>
    private static (X509Certificate2 Certificate, string PrivateKeyPem, X509Certificate2 Loadable) IssueLeaf(
        X509Certificate2 issuer,
        string commonName,
        bool includeLocalhostSans,
        DateTimeOffset now,
        OidCollection? ekus = null,
        string? aiaUrl = null,
        bool defaultEkus = true)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));

        var effectiveEkus = ekus ?? (defaultEkus ? new OidCollection { s_serverAuth, s_clientAuth } : null);
        if (effectiveEkus is not null)
        {
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(effectiveEkus, critical: false));
        }

        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        if (aiaUrl is not null)
        {
            request.CertificateExtensions.Add(AuthorityInfoAccess(aiaUrl));
        }

        if (includeLocalhostSans)
        {
            var sans = new SubjectAlternativeNameBuilder();
            sans.AddDnsName("localhost");
            sans.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(sans.Build());
        }

        var serial = RandomNumberGenerator.GetBytes(8);
        using var signed = request.Create(issuer, now.AddDays(-1), now.AddDays(1), serial);

        var certificate = new X509Certificate2(signed.Export(X509ContentType.Cert));
        using var withKey = signed.CopyWithPrivateKey(key);

        // The PKCS#12 round trip is the SAME one SecurityConfigurationAccessor performs, for
        // the same measured reason: a certificate carrying an ephemeral key completes neither
        // side of a TLS handshake on Windows. The bed's server certificate has to work, so it
        // gets the same treatment.
        var loadable = new X509Certificate2(withKey.Export(X509ContentType.Pkcs12));

        return (certificate, key.ExportPkcs8PrivateKeyPem(), loadable);
    }
}

/// <summary>
/// A temporary suite directory holding a generated CA, client certificate and client key, plus
/// the matching server certificate for an in-process TLS listener.
/// </summary>
public sealed class TestCertificateBed : IDisposable
{
    internal TestCertificateBed(
        string suiteDirectory,
        X509Certificate2 anchor,
        X509Certificate2 serverCertificate,
        X509Certificate2 clientAuthOnlyServerCertificate,
        X509Certificate2 noEkuServerCertificate)
    {
        SuiteDirectory = suiteDirectory;
        CaCertificate = anchor;
        ServerCertificate = serverCertificate;
        ClientAuthOnlyServerCertificate = clientAuthOnlyServerCertificate;
        NoEkuServerCertificate = noEkuServerCertificate;
    }

    /// <summary>
    /// A leaf from the SAME anchor, for <c>localhost</c>, carrying <c>EKU = clientAuth</c> ONLY.
    /// In mutual TLS the CA that signs the server signs every client, so this is precisely what
    /// a client-certificate holder could present while impersonating the server — and it is
    /// what a rebuilt chain with no application policy accepts.
    /// </summary>
    public X509Certificate2 ClientAuthOnlyServerCertificate { get; }

    /// <summary>
    /// A leaf from the SAME anchor, for <c>localhost</c>, carrying NO extended-key-usage
    /// extension at all. An absent EKU means UNCONSTRAINED, so this must stay trusted — it is
    /// the control proving the <c>serverAuth</c> requirement breaks no legitimate configuration.
    /// </summary>
    public X509Certificate2 NoEkuServerCertificate { get; }

    /// <summary>The temporary directory the PEM files were written into.</summary>
    public string SuiteDirectory { get; }

    /// <summary>The generated root CA, without its private key.</summary>
    public X509Certificate2 CaCertificate { get; }

    /// <summary>
    /// The server certificate (private key included, PKCS#12-loadable) for
    /// <c>localhost</c>/<c>127.0.0.1</c>, chaining to <see cref="CaCertificate"/>.
    /// </summary>
    public X509Certificate2 ServerCertificate { get; }

    /// <summary>Absolute path of the CA PEM inside <see cref="SuiteDirectory"/>.</summary>
    public string CaPath => Path.Combine(SuiteDirectory, TestCertificateAuthority.CaFileName);

    /// <summary>Absolute path of the client certificate PEM inside <see cref="SuiteDirectory"/>.</summary>
    public string ClientCertPath => Path.Combine(SuiteDirectory, TestCertificateAuthority.ClientCertFileName);

    /// <summary>Absolute path of the client key PEM inside <see cref="SuiteDirectory"/>.</summary>
    public string ClientKeyPath => Path.Combine(SuiteDirectory, TestCertificateAuthority.ClientKeyFileName);

    /// <summary>Removes the temporary suite directory and disposes the generated certificates.</summary>
    public void Dispose()
    {
        CaCertificate.Dispose();
        ServerCertificate.Dispose();
        ClientAuthOnlyServerCertificate.Dispose();
        NoEkuServerCertificate.Dispose();

        TestCertificateBedPaths.DeleteQuietly(SuiteDirectory);
    }
}

/// <summary>
/// A suite directory whose declared <c>ca.pem</c> is an OFFLINE ROOT, plus the issuing
/// INTERMEDIATE and the server leaf that intermediate signed — the two-tier enterprise PKI
/// shape a private CA normally takes.
/// </summary>
public sealed class TestTwoTierBed : IDisposable
{
    internal TestTwoTierBed(
        string suiteDirectory,
        X509Certificate2 root,
        X509Certificate2 intermediate,
        X509Certificate2 serverLeaf,
        X509Certificate2 serverLeafWithKey)
    {
        SuiteDirectory = suiteDirectory;
        RootCertificate = root;
        IntermediateCertificate = intermediate;
        ServerLeaf = serverLeaf;
        ServerLeafWithKey = serverLeafWithKey;
    }

    /// <summary>The temporary directory holding <c>ca.pem</c> (the ROOT, not the intermediate).</summary>
    public string SuiteDirectory { get; }

    /// <summary>The offline root — the certificate written to <c>ca.pem</c> and declared as <c>caCert</c>.</summary>
    public X509Certificate2 RootCertificate { get; }

    /// <summary>The issuing intermediate, which a real server sends alongside its own leaf.</summary>
    public X509Certificate2 IntermediateCertificate { get; }

    /// <summary>The server leaf, issued by <see cref="IntermediateCertificate"/>, public part only.</summary>
    public X509Certificate2 ServerLeaf { get; }

    /// <summary>The same leaf with its private key, PKCS#12-loadable, for an in-process listener.</summary>
    public X509Certificate2 ServerLeafWithKey { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        RootCertificate.Dispose();
        IntermediateCertificate.Dispose();
        ServerLeaf.Dispose();
        ServerLeafWithKey.Dispose();
        TestCertificateBedPaths.DeleteQuietly(SuiteDirectory);
    }
}

/// <summary>
/// A suite directory whose declared anchor cannot reach the leaf without the missing
/// intermediate, and whose leaf advertises an Authority Information Access <c>caIssuers</c> URL
/// where a chain builder would go looking for it.
/// </summary>
public sealed class TestAiaBed : IDisposable
{
    internal TestAiaBed(string suiteDirectory, X509Certificate2 root, X509Certificate2 leaf)
    {
        SuiteDirectory = suiteDirectory;
        RootCertificate = root;
        LeafWithAuthorityInfoAccess = leaf;
    }

    /// <summary>The temporary directory holding <c>ca.pem</c>.</summary>
    public string SuiteDirectory { get; }

    /// <summary>The declared anchor.</summary>
    public X509Certificate2 RootCertificate { get; }

    /// <summary>The leaf carrying the <c>caIssuers</c> URL, whose issuer is deliberately absent.</summary>
    public X509Certificate2 LeafWithAuthorityInfoAccess { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        RootCertificate.Dispose();
        LeafWithAuthorityInfoAccess.Dispose();
        TestCertificateBedPaths.DeleteQuietly(SuiteDirectory);
    }
}

internal static class TestCertificateBedPaths
{
    internal static void DeleteQuietly(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
