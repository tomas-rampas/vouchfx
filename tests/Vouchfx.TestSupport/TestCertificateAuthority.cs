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
using System.Security;
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
    /// <summary>
    /// Eight hexadecimal characters unique to THIS process, appended to the common name of every
    /// CA and INTERMEDIATE this class mints — and to no leaf.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The invariant: authority subjects are per-process unique; leaf subjects are
    /// stable.</strong> On Windows, <see cref="X509Chain.Build"/> caches the intermediates it
    /// encounters into the <c>CA</c> store, and that cache is keyed by SUBJECT. While every run
    /// minted a fresh authority under a CONSTANT common name, each run deposited another
    /// same-subject/different-key copy; once enough had accumulated, chain building failed
    /// deterministically for reasons that read as environmental (issue #374, which recurred twice
    /// and cost about an hour of diagnosis each time — 101 of 175 measured residue copies were
    /// <c>Vouchfx Test Issuing Intermediate</c>). A per-process token makes the cross-run
    /// collision impossible: two runs can no longer mint the same subject.
    /// </para>
    /// <para>
    /// Leaves are deliberately EXCLUDED. <see cref="ServerSubjectCommonName"/> is matched against
    /// the host name a probe connects to, and the client identities
    /// (<see cref="ClientSubjectCommonName"/>, <see cref="UnauthorisedClientSubjectCommonName"/>,
    /// <see cref="ForeignClientSubjectCommonName"/>) are matched by a broker's own authorisation
    /// rules — both are matched by VALUE, so suffixing them would break the thing under test to
    /// close a leak no leaf has been measured to cause: what CryptoAPI caches while assembling a
    /// path is the path's LINKS.
    /// </para>
    /// <para>
    /// The token is per PROCESS, not per bed: beds running side by side within one run keep the
    /// same-subject coexistence they have always had, which the thumbprint-keyed
    /// <c>TestCertificateStoreSweep</c> already handles. The collision this closes is the
    /// cross-run one. It doubles as the only safe key for a residue guard — any BROADER subject
    /// match (a bare common name, a <c>Vouchfx</c> prefix) would sweep in the cached intermediates
    /// of a concurrently running suite, which is a worse fault than the leak.
    /// </para>
    /// </remarks>
    public static readonly string ProcessToken = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// <see cref="ProcessToken"/> preceded by the separator every authority subject puts in front
    /// of it — the form anything SEARCHING for this process's certificates must match on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eight hex characters on their own are not rare inside a certificate store: real
    /// <c>CA</c> stores carry distinguished names containing long hexadecimal runs (TPM and
    /// platform attestation intermediates embed key identifiers that way), so a BARE token
    /// measurably collides with them, while the space-anchored form matched nothing on any host
    /// checked. A collision would make the residue guard delete a device attestation certificate,
    /// which is a far worse outcome than the leak it is closing — so the anchored form is the only
    /// supported way to match, and it is spelled ONCE, here.
    /// </para>
    /// <para>
    /// <see cref="WithProcessToken"/> builds every authority common name by appending exactly this
    /// string, so a search using it and a subject that was minted cannot drift apart.
    /// </para>
    /// <para>
    /// A computed property, not a field, and that is a safety property rather than a style choice.
    /// As <c>static readonly string ProcessTokenMarker = " " + ProcessToken;</c> its correctness
    /// depended on being declared BELOW <see cref="ProcessToken"/> — static field initialisers run
    /// in textual order, so reordering the two would have left the marker as a bare <c>" "</c>,
    /// and a guard searching for a single space matches every subject that contains one, then
    /// deletes them. Evaluating at call time removes the ordering dependency for the guard's
    /// needle — the destructive one. <see cref="CaSubjectCommonName"/> and
    /// <see cref="ForeignCaSubjectCommonName"/> still reach this from their own field
    /// initialisers via <see cref="WithProcessToken"/>, so they still require
    /// <see cref="ProcessToken"/> to be declared above them; a reorder there loses the token on
    /// those two subjects — not destructive, and reddened by
    /// <c>RootCaSubjectFieldAndTheMintedAnchorAgree</c> and
    /// <c>EveryAuthoritySubjectIsDistinctWithinTheProcess</c>.
    /// <c>TestCertificateAuthorityProcessTokenTests</c> pins the SHAPE (nine characters, a space
    /// then eight hex digits) so a future re-spelling that widens the match reddens.
    /// </para>
    /// </remarks>
    public static string ProcessTokenMarker => " " + ProcessToken;

    /// <summary>
    /// Common name of the generated root CA, carrying <see cref="ProcessToken"/>.
    /// </summary>
    /// <remarks>
    /// A field rather than a constant BECAUSE of the token: the value is not known until the
    /// process starts. The one external assertion over it
    /// (<c>SecurityConfigurationAccessorTests</c>) reads this same field, so mint and assertion
    /// cannot drift.
    /// </remarks>
    public static readonly string CaSubjectCommonName = WithProcessToken("Vouchfx Test Root CA");

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
    /// <para>
    /// The distinctness is operational, not cosmetic. Chain building on Windows can leave issuer
    /// certificates in the user's intermediate-CA store, and a stale entry sharing a subject with a
    /// live fixture's issuer produces chain failures that look like defects in whatever ran next —
    /// a trap this repository has already paid for once with a subject that WAS shared. A root
    /// nobody trusts, whose whole purpose is to be rejected, is the last one that should be
    /// confusable with the anchor a suite declares.
    /// </para>
    /// <para>
    /// It additionally carries <see cref="ProcessToken"/>, as every authority subject here does,
    /// which makes it unique across RUNS as well as across the subjects of one run. A field
    /// rather than a constant for that reason.
    /// </para>
    /// </remarks>
    public static readonly string ForeignCaSubjectCommonName =
        WithProcessToken("Vouchfx Test Foreign Root CA");

    /// <summary>Common name of the foreign client certificate.</summary>
    public const string ForeignClientSubjectCommonName = "vouchfx-test-foreign-client";

    /// <summary>
    /// Common name of a second client identity issued by the SAME authority as
    /// <see cref="ClientSubjectCommonName"/> — one that authenticates perfectly and is simply not
    /// granted anything by the broker's own authorisation rules.
    /// </summary>
    /// <remarks>
    /// Distinct from every other subject this class mints, for the operational reason recorded at
    /// <see cref="ForeignCaSubjectCommonName"/>. The distinctness matters more here than usual:
    /// this certificate's whole purpose is that it AUTHENTICATES IDENTICALLY to the authorised one
    /// — same issuer, same chain, same result at the handshake — so the only thing separating the
    /// two in a broker's logs and in an assertion is the common name. It is emphatically not
    /// indistinguishable on the wire: the serial, the public key, the subject key identifier and
    /// the DER length all differ, as they must for two certificates.
    /// </remarks>
    public const string UnauthorisedClientSubjectCommonName = "vouchfx-test-unauthorised";

    /// <summary>File name of the unauthorised client's certificate PEM.</summary>
    public const string UnauthorisedClientCertFileName = "unauthorised-client.pem";

    /// <summary>File name of the unauthorised client's private-key PEM.</summary>
    public const string UnauthorisedClientKeyFileName = "unauthorised-client-key.pem";

    /// <summary>
    /// File name of the server's certificate PEM, on its own — the form a TLS-terminating HTTP
    /// server wants, as distinct from the key-plus-certificate single file Kafka's PEM key store
    /// expects.
    /// </summary>
    public const string ServerCertFileName = "server.pem";

    /// <summary>File name of the server's private-key PEM, on its own.</summary>
    public const string ServerKeyFileName = "server-key.pem";

    private static readonly Oid s_serverAuth = new("1.3.6.1.5.5.7.3.1");
    private static readonly Oid s_clientAuth = new("1.3.6.1.5.5.7.3.2");

    /// <summary>
    /// Appends <see cref="ProcessTokenMarker"/> to an authority's common name — the ONE spelling
    /// of how the token is attached, so a subject minted inline, a subject exposed as a field, and
    /// a guard searching for either cannot disagree about the separator.
    /// </summary>
    private static string WithProcessToken(string commonName) => commonName + ProcessTokenMarker;

    /// <summary>
    /// Builds the distinguished name of a CA or intermediate. Every authority subject this class
    /// mints goes through here or through a field built by <see cref="WithProcessToken"/>; leaves
    /// deliberately do not (see <see cref="ProcessToken"/>).
    /// </summary>
    private static string AuthoritySubject(string commonName) => "CN=" + WithProcessToken(commonName);

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
    /// Re-exports the client key already written into <paramref name="suiteDirectory"/> as an
    /// ENCRYPTED PKCS#8 PEM under <paramref name="passphrase"/>, in place, so that encryption is
    /// the only thing that changed about the bed.
    /// </summary>
    /// <param name="suiteDirectory">
    /// A directory already populated by <see cref="CreateSuiteDirectory"/> (or
    /// <see cref="WriteKafkaBrokerSuiteDirectory"/>), containing <see cref="ClientKeyFileName"/>.
    /// </param>
    /// <param name="passphrase">The passphrase the re-exported key is encrypted under.</param>
    /// <remarks>
    /// <para>
    /// Lives HERE, beside the fixture that writes the key, rather than in either test class that
    /// wants it. It existed twice — once taking a <see cref="TestCertificateBed"/> and once taking
    /// a directory path, with byte-identical bodies — which is a seam rather than a design: the
    /// two arms of the same requirement were written in different classes, and the copy that
    /// changes first is the one whose divergence nobody notices. One spelling, in the shared home
    /// both classes already reference.
    /// </para>
    /// <para>
    /// The parameters mirror <see cref="WriteKafkaBrokerSuiteDirectory"/>'s rather than
    /// <see cref="CreateSuiteDirectory"/>'s: a directory, because that is what BOTH callers can
    /// supply (a bed exposes its directory; a directory cannot produce a bed).
    /// </para>
    /// <para>
    /// AES-256-CBC with SHA-256 and 100,000 iterations — a shape .NET's own PEM reader opens, so
    /// the fixture exercises the engine's load path rather than a limitation of the encoding.
    /// </para>
    /// </remarks>
    public static void EncryptClientKeyInPlace(string suiteDirectory, string passphrase)
    {
        var keyPath = Path.Combine(suiteDirectory, ClientKeyFileName);

        using var key = RSA.Create();
        key.ImportFromPem(File.ReadAllText(keyPath));
        File.WriteAllText(
            keyPath,
            key.ExportEncryptedPkcs8PrivateKeyPem(
                passphrase.AsSpan(),
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000)));
    }

    /// <summary>
    /// A per-test environment-variable name for a client-key passphrase.
    /// </summary>
    /// <remarks>
    /// Environment variables are process-global and xUnit runs test classes in parallel, so a
    /// shared name lets one arm read another's value — or, worse, read a value another arm has
    /// just cleared, which fails intermittently rather than outright. Hoisted for the same reason
    /// as <see cref="EncryptClientKeyInPlace"/>: it was named in one test class and inlined in
    /// another, so only one of the two stated why it was unique.
    /// </remarks>
    public static string UniqueClientKeyPassphraseVariableName() =>
        "VOUCHFX_TEST_CKP_" + Guid.NewGuid().ToString("N");

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
    /// material this method emits in <strong>well under a second</strong> with no external tooling
    /// at all. Measured on one host, Release, 10 samples after warm-up: <c>366 · 378 · 409 · 439 ·
    /// 523 · 526 · 598 · 602 · 729 · 758 ms</c> for the FOUR RSA-2048 key generations (authority,
    /// server, client, unauthorised client), issuance and PEM export. The spread is
    /// two-fold — RSA key generation is a rejection-sampling search for primes, so its cost is
    /// variable by construction — which is why the figure is quoted as a range and not a mean, and
    /// why an earlier single-number claim of "~200 ms" was retracted rather than adjusted: it
    /// predated the fourth key and no sample now falls inside the band it quoted.
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

        // A SECOND client identity from the SAME authority. It is written unconditionally rather
        // than on request because it costs one key generation and because the authorisation drills
        // need the two identities to differ in exactly one way THE BROKER CAN ACT ON: the name it
        // maps them to. (They differ in serial, key and every other per-certificate field, as any
        // two certificates must; none of those reaches an authorisation decision.)
        // Anything issued by a different CA, or with different extensions, would give a failing row
        // a second candidate explanation.
        var unauthorised = IssueLeaf(
            ca, UnauthorisedClientSubjectCommonName, includeLocalhostSans: false, now);

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

            // The SAME server leaf, split into the two separate files a TLS-terminating HTTP server
            // wants. Two shapes of one identity rather than two identities, deliberately: a suite
            // that secures both a broker and an HTTP API with one internal CA is the deployment's
            // own shape, and issuing a second leaf here would invent a difference the fixture does
            // not have.
            File.WriteAllText(
                Path.Combine(suiteDirectory, ServerCertFileName),
                server.Certificate.ExportCertificatePem() + "\n");

            var serverKeyPath = Path.Combine(suiteDirectory, ServerKeyFileName);
            File.WriteAllText(serverKeyPath, server.PrivateKeyPem);
            RestrictToOwner(serverKeyPath);

            File.WriteAllText(
                Path.Combine(suiteDirectory, UnauthorisedClientCertFileName),
                unauthorised.Certificate.ExportCertificatePem() + "\n");

            var unauthorisedKeyPath = Path.Combine(suiteDirectory, UnauthorisedClientKeyFileName);
            File.WriteAllText(unauthorisedKeyPath, unauthorised.PrivateKeyPem);
            RestrictToOwner(unauthorisedKeyPath);

            // The broker's trust store is the same anchor the suite declares as caCert, which is
            // what makes ssl.client.auth=required accept BOTH client certificates above — the
            // authorised one and the unauthorised one. That is the point: authentication cannot
            // be what separates them.
            File.WriteAllText(Path.Combine(suiteDirectory, BrokerTruststoreFileName), caPem);
        }
        finally
        {
            server.Certificate.Dispose();
            server.Loadable.Dispose();
            client.Certificate.Dispose();
            client.Loadable.Dispose();
            unauthorised.Certificate.Dispose();
            unauthorised.Loadable.Dispose();
        }
    }

    /// <summary>
    /// Points a suite directory's declared <c>clientCert</c>/<c>clientKey</c> at the SAME-AUTHORITY
    /// identity the broker grants nothing to, leaving every other file untouched.
    /// </summary>
    /// <remarks>
    /// The suite's YAML does not change: it still declares <c>client.pem</c> and
    /// <c>client-key.pem</c>, and those files still hold a certificate the broker authenticates
    /// without complaint. Only WHO it says you are changes — which is the single variable an
    /// authorisation control is allowed to move.
    /// </remarks>
    public static void SwitchToUnauthorisedClientIdentity(string suiteDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suiteDirectory);

        File.Copy(
            Path.Combine(suiteDirectory, UnauthorisedClientCertFileName),
            Path.Combine(suiteDirectory, ClientCertFileName),
            overwrite: true);

        var keyPath = Path.Combine(suiteDirectory, ClientKeyFileName);
        File.Copy(Path.Combine(suiteDirectory, UnauthorisedClientKeyFileName), keyPath, overwrite: true);
        RestrictToOwner(keyPath);
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
            AuthoritySubject("Vouchfx Test Offline Root"),
            rootKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        AddCaExtensions(rootRequest);
        using var root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));

        using var intermediateKey = RSA.Create(2048);
        var intermediateRequest = new CertificateRequest(
            AuthoritySubject("Vouchfx Test Issuing Intermediate"),
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
            AuthoritySubject("Imposter Root"), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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
            AuthoritySubject("Vouchfx AIA Root"), rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddCaExtensions(rootRequest);
        using var root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));

        using var intermediateKey = RSA.Create(2048);
        var intermediateRequest = new CertificateRequest(
            AuthoritySubject("Vouchfx AIA Intermediate"),
            intermediateKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
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

        // Subject AND thumbprint, read from the SAME live certificate in one statement, are handed
        // to the bed even though the bed does not RETAIN the intermediate: whether a chain builder
        // ever obtains it decides whether a copy gets cached, and this fixture exists precisely to
        // dangle it at one over the caIssuers URL. Taken as a pair because a thumbprint alone
        // identifies nothing a test can check against — the subject is what ties it to the leaf's
        // issuer. See TestAiaBed's constructor.
        var intermediate = (intermediateSigned.Subject, intermediateSigned.Thumbprint);

        return new TestAiaBed(
            suiteDirectory,
            new X509Certificate2(root.Export(X509ContentType.Cert)),
            leaf.Certificate,
            intermediate.Subject,
            intermediate.Thumbprint);
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
            AuthoritySubject("Unrelated Root"), otherCaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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

        // See TestTwoTierBed's copy of this: thumbprints captured while the certificates are
        // alive, so Dispose can remove this bed's own cached copies and only those. Measured on
        // this bed the sweep finds nothing — its anchor is a self-signed ROOT, and CryptoAPI
        // caches INTERMEDIATES — so this is the leak staying shut rather than the leak closing.
        _ownThumbprints = new[]
        {
            CaCertificate.Thumbprint,
            ServerCertificate.Thumbprint,
            ClientAuthOnlyServerCertificate.Thumbprint,
            NoEkuServerCertificate.Thumbprint,
        };
    }

    private readonly string[] _ownThumbprints;

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
        TestCertificateStoreSweep.RemoveCachedCopies(_ownThumbprints);
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

        // Captured while the certificates are alive, because a disposed X509Certificate2 cannot be
        // asked for its thumbprint — and because these strings are what makes the sweep in
        // Dispose match this bed's own material and nothing else. The intermediate is the one
        // MEASURED to be cached (it is the only certificate here a chain builder is handed as a
        // path link); the other three cost nothing to list and mean a future test that hands one
        // of them to a chain builder does not reopen this leak.
        _ownThumbprints = new[]
        {
            RootCertificate.Thumbprint,
            IntermediateCertificate.Thumbprint,
            ServerLeaf.Thumbprint,
            ServerLeafWithKey.Thumbprint,
        };
    }

    private readonly string[] _ownThumbprints;

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
        TestCertificateStoreSweep.RemoveCachedCopies(_ownThumbprints);
    }
}

/// <summary>
/// A suite directory whose declared anchor cannot reach the leaf without the missing
/// intermediate, and whose leaf advertises an Authority Information Access <c>caIssuers</c> URL
/// where a chain builder would go looking for it.
/// </summary>
public sealed class TestAiaBed : IDisposable
{
    internal TestAiaBed(
        string suiteDirectory,
        X509Certificate2 root,
        X509Certificate2 leaf,
        string intermediateSubject,
        string intermediateThumbprint)
    {
        SuiteDirectory = suiteDirectory;
        RootCertificate = root;
        LeafWithAuthorityInfoAccess = leaf;
        IntermediateSubject = intermediateSubject;
        IntermediateThumbprint = intermediateThumbprint;

        // See TestTwoTierBed's copy of this. The intermediate is ABSENT FROM THE BED — the whole
        // point of the fixture — but absent from the bed is not the same as absent from the host:
        // a chain builder that has not had downloads disabled fetches it over the caIssuers URL,
        // and CryptoAPI then caches what it fetched. That is the exact case this fixture provokes,
        // so the copy it can produce is this bed's to remove. It is listed by THUMBPRINT, captured
        // at mint time (#374/#419) — the bed knows the value because the bed minted the
        // certificate, which is all the sweep needs and is why retaining the certificate itself
        // would be the wrong fix.
        _ownThumbprints = new[]
        {
            RootCertificate.Thumbprint,
            LeafWithAuthorityInfoAccess.Thumbprint,
            IntermediateThumbprint,
        };
    }

    private readonly string[] _ownThumbprints;

    /// <summary>The temporary directory holding <c>ca.pem</c>.</summary>
    public string SuiteDirectory { get; }

    /// <summary>The declared anchor.</summary>
    public X509Certificate2 RootCertificate { get; }

    /// <summary>The leaf carrying the <c>caIssuers</c> URL, whose issuer is deliberately absent.</summary>
    public X509Certificate2 LeafWithAuthorityInfoAccess { get; }

    /// <summary>
    /// Distinguished name of the intermediate that signed
    /// <see cref="LeafWithAuthorityInfoAccess"/>, captured from the same certificate instance and
    /// in the same statement as <see cref="IntermediateThumbprint"/>.
    /// </summary>
    /// <remarks>
    /// The pair is what makes the capture checkable: a thumbprint on its own could have been read
    /// off any certificate the bed minted and still look right, whereas a subject can be compared
    /// with <c>LeafWithAuthorityInfoAccess.Issuer</c>. Because the two are read together, a
    /// subject that matches the leaf's issuer is evidence the thumbprint beside it came from the
    /// missing link too.
    /// </remarks>
    public string IntermediateSubject { get; }

    /// <summary>
    /// Thumbprint of the intermediate that signed <see cref="LeafWithAuthorityInfoAccess"/> —
    /// the certificate a chain builder would fetch from the <c>caIssuers</c> URL, and therefore
    /// the one it would cache.
    /// </summary>
    /// <remarks>
    /// A thumbprint and not a certificate, on purpose: the bed must not be able to hand anyone
    /// the intermediate, because its absence is the fixture. This is exactly the amount of it the
    /// teardown sweep needs, and it is the entry the sweep uses.
    /// </remarks>
    public string IntermediateThumbprint { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        RootCertificate.Dispose();
        LeafWithAuthorityInfoAccess.Dispose();
        TestCertificateBedPaths.DeleteQuietly(SuiteDirectory);
        TestCertificateStoreSweep.RemoveCachedCopies(_ownThumbprints);
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

/// <summary>
/// Removes the copies of a bed's own certificates that Windows leaves behind in the
/// intermediate-CA stores, matching on THUMBPRINT alone.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why anything is left behind at all.</strong> Nothing here installs a certificate. When
/// <see cref="X509Chain.Build"/> runs on Windows, CryptoAPI CACHES the intermediates it encounters
/// while assembling a path — including one handed to it through
/// <see cref="X509ChainPolicy.ExtraStore"/> — into the <c>CA</c> store, so the next build can find
/// it without going back to the network. The two-tier fixture supplies exactly such an
/// intermediate, so every run of it deposits one. Measured on this repository: one
/// <c>Vouchfx Test Issuing Intermediate</c> per run of Vouchfx.Engine.Runtime.Tests, and 175
/// accumulated copies once produced two failures that read as environmental for several sessions
/// before the cause was found. The cure is therefore a teardown sweep, not a change to what the
/// fixture installs.
/// </para>
/// <para>
/// <strong>Why the sweep alone was not enough.</strong> A cache entry only DAMAGES anything when
/// two of them share a subject, and while every run minted its authorities under constant common
/// names, any run whose process died before teardown left one that a later run could collide
/// with. <see cref="TestCertificateAuthority.ProcessToken"/> removes the collision at the source
/// (#374); this sweep is what keeps the store from growing ALONG THE PATH WHERE
/// <c>Dispose</c> RUNS.
/// </para>
/// <para>
/// <strong>What is knowingly left behind.</strong> Nothing here reclaims residue from a process
/// that was killed, cancelled, or crashed before teardown — not this sweep, which runs in
/// <c>Dispose</c>, and not <c>TestCertificateStoreGuard</c>, which only ever sees its own live
/// process's token. Such residue is now harmless rather than merely rarer: its subject is unique
/// to the dead run, so no later run can collide with it, which is the whole point of the token.
/// It is litter, and it is accepted as litter. The certificates stop being time-valid quickly —
/// intermediates and leaves are minted <c>notAfter = now + 1 day</c> and roots <c>+ 2 days</c> —
/// but expiry does NOT remove them from the store, and whether an expired copy is still offered
/// as a chain-building candidate has not been measured here, so no claim is made either way.
/// Reclaiming abandoned residue is tracked on #459; deliberately not built into this change.
/// </para>
/// <para>
/// <strong>Why the match is by thumbprint and never by subject.</strong> Within one process every
/// bed mints a FRESH intermediate under the same common name — the token is per process, not per
/// bed — and xUnit runs test classes in parallel. A subject-matched sweep would delete a
/// concurrently-running bed's cached intermediate, and removing one mid-run can make another
/// test's chain build fail, which would trade a cosmetic leak for an intermittent suite. A
/// thumbprint identifies one certificate, so a bed can only ever remove its own.
/// </para>
/// <para>
/// <strong>Why every failure is swallowed.</strong> Writing to <c>LocalMachine\CA</c> needs
/// elevation, which a test process usually lacks — this happens to be where the cached copy lands
/// when the process IS elevated, which is why both locations are swept. A teardown that threw
/// would turn the leak into a red suite, which is strictly worse than the leak. The catch is
/// narrowed to the three types a store operation can raise rather than left bare, so a genuine
/// defect in this method still surfaces.
/// </para>
/// </remarks>
internal static class TestCertificateStoreSweep
{
    /// <summary>
    /// Removes any certificate whose thumbprint appears in <paramref name="thumbprints"/> from the
    /// current user's and the local machine's intermediate-CA stores. No-op off Windows, where
    /// nothing performs this caching.
    /// </summary>
    internal static void RemoveCachedCopies(IReadOnlyList<string> thumbprints)
    {
        if (!OperatingSystem.IsWindows() || thumbprints.Count == 0)
        {
            return;
        }

        RemoveFrom(StoreLocation.CurrentUser, thumbprints);
        RemoveFrom(StoreLocation.LocalMachine, thumbprints);
    }

    private static void RemoveFrom(StoreLocation location, IReadOnlyList<string> thumbprints)
    {
        try
        {
            // Read first, and open for writing only when there is something to remove. Opening a
            // machine store for WRITING fails without elevation, so probing read-only keeps the
            // usual case — a bed whose certificates were never cached — free of both the failed
            // open and the exception it raises.
            X509Certificate2Collection cached;
            using (var probe = new X509Store(StoreName.CertificateAuthority, location))
            {
                probe.Open(OpenFlags.ReadOnly);
                cached = Matching(probe, thumbprints);
            }

            if (cached.Count == 0)
            {
                return;
            }

            // Matching() hands back fresh X509Certificate2 instances, each owning its own native
            // CertContext handle, so they must be disposed once removal is done — a sweep that
            // leaked handles while closing a certificate leak would be a poor joke. Disposal is
            // in a finally because Remove itself can throw (the unelevated machine store), and
            // the handles must go back either way.
            try
            {
                using var store = new X509Store(StoreName.CertificateAuthority, location);
                store.Open(OpenFlags.ReadWrite);

                foreach (var certificate in cached)
                {
                    store.Remove(certificate);
                }
            }
            finally
            {
                foreach (var certificate in cached)
                {
                    certificate.Dispose();
                }
            }
        }
        catch (Exception ex) when (
            ex is CryptographicException or UnauthorizedAccessException or SecurityException)
        {
            // Best effort: an unelevated process cannot write the machine store, and a cached
            // copy nobody could remove is a cosmetic leak. Failing here would be the real damage.
        }
    }

    /// <remarks>
    /// <para>
    /// The wanted thumbprints are de-duplicated, and not defensively: a bed legitimately lists the
    /// SAME certificate twice. <c>IssueLeaf</c> hands back a certificate and a PKCS#12-reloaded
    /// <c>Loadable</c> copy of it, and <c>TestTwoTierBed</c> captures both (<c>ServerLeaf</c> and
    /// <c>ServerLeafWithKey</c>) — a thumbprint is computed over the certificate DER, so the
    /// private key does not change it and the two entries are identical. Empty thumbprints are
    /// dropped for the same reason: they are a lookup nothing can usefully match.
    /// </para>
    /// <para>
    /// The store is enumerated ONCE and every non-matching instance is disposed on the spot. The
    /// earlier shape called <c>store.Certificates.Find</c> per thumbprint, which enumerates the
    /// whole store again each time and leaves each intermediate collection's native
    /// <c>CertContext</c> handles to the finaliser; a four-thumbprint bed swept across two store
    /// locations paid for eight full enumerations, and the AIA fixture's arrival made that worse.
    /// Enumerating once and disposing eagerly is the same pattern
    /// <c>TestCertificateStoreGuard</c> uses, for the same reason.
    /// </para>
    /// </remarks>
    private static X509Certificate2Collection Matching(X509Store store, IReadOnlyList<string> thumbprints)
    {
        var wanted = new HashSet<string>(
            thumbprints.Where(t => !string.IsNullOrEmpty(t)), StringComparer.OrdinalIgnoreCase);

        var matches = new X509Certificate2Collection();
        if (wanted.Count == 0)
        {
            return matches;
        }

        // Hoisted: X509Store.Certificates materialises a NEW collection of live handles on every
        // access, so the throw path below needs the same instance the loop is walking.
        var all = store.Certificates;

        try
        {
            foreach (var certificate in all)
            {
                if (wanted.Contains(certificate.Thumbprint))
                {
                    matches.Add(certificate);
                }
                else
                {
                    certificate.Dispose();
                }
            }
        }
        catch
        {
            // Reading a certificate can throw (a malformed DN, a broken store entry), and the
            // handles this method never reached are as much its responsibility as the ones it
            // matched. Dispose EVERYTHING; double-dispose is a no-op.
            foreach (var certificate in all)
            {
                certificate.Dispose();
            }

            throw;
        }

        return matches;
    }
}
