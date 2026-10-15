using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Vouchfx.TestSupport;

// Native fixture: invoked only by the isolated, opted-in hosted acceptance project.
// Each arm gets new keys/serials AND issuer names, avoiding same-process cache reuse.
public sealed class HostedHttpTlsCertificateBed : IDisposable
{
    private readonly List<X509Certificate2> _owned = new();
    private readonly HashSet<string> _thumbprints = new(StringComparer.OrdinalIgnoreCase);
    private HostedHttpTlsCertificateBed() => SuiteDirectory = Path.Combine(Path.GetTempPath(), "vouchfx-hosted-tls-" + Guid.NewGuid().ToString("N"));
    public string SuiteDirectory { get; }
    public X509Certificate2 Root { get; private set; } = null!;
    public X509Certificate2 Issuer { get; private set; } = null!;
    public X509Certificate2 Server { get; private set; } = null!;
    public X509Certificate2? Client { get; private set; }
    public string Nonce { get; } = Guid.NewGuid().ToString("N");

    public static HostedHttpTlsCertificateBed CreateAiaServer(string issuerUrl) => Create(issuerUrl, clientChain: false, candidate: "none");
    public static HostedHttpTlsCertificateBed CreateClientChain(string candidate) => Create(issuerUrl: null, clientChain: true, candidate);

    private static HostedHttpTlsCertificateBed Create(string? issuerUrl, bool clientChain, string candidate)
    {
        var bed = new HostedHttpTlsCertificateBed();
        try
        {
            Directory.CreateDirectory(bed.SuiteDirectory);
            using var rootKey = RSA.Create(2048);
            var rootRequest = Request(bed.AuthorityName("Root"), rootKey, ca: true, server: false);
            using var rootSigner = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            bed.Root = bed.Own(new X509Certificate2(rootSigner.Export(X509ContentType.Cert)));
            using var issuerKey = RSA.Create(2048);
            using var issuerPublic = Sign(Request(bed.AuthorityName("Issuer"), issuerKey, ca: true, server: false), rootSigner);
            bed.Issuer = bed.Own(new X509Certificate2(issuerPublic.Export(X509ContentType.Cert)));
            using var issuerSigner = issuerPublic.CopyWithPrivateKey(issuerKey);
            using var serverKey = RSA.Create(2048);
            var serverRequest = Request("CN=localhost", serverKey, ca: false, server: true);
            if (issuerUrl is not null) { serverRequest.CertificateExtensions.Add(Aia(issuerUrl)); }
            using var serverPublic = Sign(serverRequest, clientChain ? rootSigner : issuerSigner);
            using var serverWithKey = serverPublic.CopyWithPrivateKey(serverKey);
            bed.Server = bed.Own(LoadForTls(serverWithKey));
            File.WriteAllText(Path.Combine(bed.SuiteDirectory, "ca.pem"), bed.Root.ExportCertificatePem());
            if (clientChain)
            {
                using var clientKey = RSA.Create(2048);
                using var clientPublic = Sign(Request("CN=hosted-client-" + bed.Nonce, clientKey, ca: false, server: false), issuerSigner);
                bed.Client = bed.Own(new X509Certificate2(clientPublic.Export(X509ContentType.Cert)));
                var pem = bed.Client.ExportCertificatePem();
                if (candidate == "supplied") { pem += "\n" + bed.Issuer.ExportCertificatePem(); }
                else if (candidate == "mismatched")
                {
                    using var unrelatedKey = RSA.Create(2048);
                    var unrelatedRequest = Request(bed.AuthorityName("Unrelated"), unrelatedKey, ca: true, server: false);
                    using var unrelatedSigner = unrelatedRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
                    var unrelated = bed.Own(new X509Certificate2(unrelatedSigner.Export(X509ContentType.Cert)));
                    pem += "\n" + unrelated.ExportCertificatePem();
                }
                else if (candidate != "omitted") { throw new ArgumentException("Unknown candidate arm.", nameof(candidate)); }
                File.WriteAllText(Path.Combine(bed.SuiteDirectory, "client.pem"), pem);
                var keyPath = Path.Combine(bed.SuiteDirectory, "client-key.pem");
                File.WriteAllText(keyPath, clientKey.ExportPkcs8PrivateKeyPem());
                if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            }
            return bed;
        }
        catch { bed.Dispose(); throw; }
    }

    private string AuthorityName(string role) => "CN=Vouchfx Hosted TLS " + role + " " + Nonce + TestCertificateAuthority.ProcessTokenMarker;
    private X509Certificate2 Own(X509Certificate2 certificate)
    {
        _owned.Add(certificate);
        _thumbprints.Add(certificate.Thumbprint);
        return certificate;
    }

    private static CertificateRequest Request(string subject, RSA key, bool ca, bool server)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(ca ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (!ca)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, false));
            if (server)
            {
                var sans = new SubjectAlternativeNameBuilder();
                sans.AddDnsName("localhost");
                sans.AddIpAddress(System.Net.IPAddress.Loopback);
                request.CertificateExtensions.Add(sans.Build());
            }
        }
        return request;
    }

    private static X509Certificate2 Sign(CertificateRequest request, X509Certificate2 signer) =>
        request.Create(signer, DateTimeOffset.UtcNow.AddDays(-1), new DateTimeOffset(signer.NotAfter.ToUniversalTime()).AddMinutes(-1), RandomNumberGenerator.GetBytes(16));

    private static X509Certificate2 LoadForTls(X509Certificate2 certificate)
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var blob = certificate.Export(X509ContentType.Pkcs12, password);
        try { return new X509Certificate2(blob, password, X509KeyStorageFlags.DefaultKeySet); }
        finally { CryptographicOperations.ZeroMemory(blob); }
    }

    private static X509Extension Aia(string url)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence(); writer.PushSequence();
        writer.WriteObjectIdentifier("1.3.6.1.5.5.7.48.2");
        writer.WriteCharacterString(UniversalTagNumber.IA5String, url, new Asn1Tag(TagClass.ContextSpecific, 6));
        writer.PopSequence(); writer.PopSequence();
        return new X509Extension("1.3.6.1.5.5.7.1.1", writer.Encode(), false);
    }

    public void Dispose()
    {
        foreach (var certificate in _owned) { certificate.Dispose(); }
        _owned.Clear();
        TestCertificateBedPaths.DeleteQuietly(SuiteDirectory);
        TestCertificateStoreSweep.RemoveCachedCopies(_thumbprints.ToArray());
    }
}
