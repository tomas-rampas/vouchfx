// Vouchfx.Engine.Abstractions.Security — the per-target CLIENT-CONFIGURATION accessor
// (authenticated-infrastructure-mtls, slice D — REQ-014 as re-shaped 2026-08-03).
//
// Shape, and why it is this shape rather than the obvious one. The requirement that
// produced this file could have been satisfied by a certificate accessor —
// `Security.CertificatesFor(target)` — because certificates are the only mechanism 1.0
// wires. It is deliberately NOT that. `For(targetName)` returns the resolved CLIENT
// CONFIGURATION for that target under its declared profile, of which the certificate
// material below is ONE view; a 1.1+ profile adding a SASL mechanism and credential, a
// bearer token, or a Kerberos principal adds a sibling view to ISecurityConfiguration
// rather than a parallel accessor. If the narrow shape froze in provider-facing usage,
// every mechanism added later would need its own accessor and the provider ecosystem
// would carry both spellings permanently. ScriptGlobalVariables is NOT covered by the
// frozen v1 SDK contract (Vouchfx.Sdk) — the same non-freeze-gated status the Traces
// accessor already has — so shaping it correctly now costs nothing and is expensive to
// retrofit once out-of-tree providers read the narrow form.
//
// The two certificate views are BOTH required, and neither is sufficient alone (REQ-014):
// librdkafka (Confluent.Kafka's SslCaLocation/SslCertificateLocation/SslKeyLocation, slice
// E's REQ-015) accepts only host FILE PATHS, while HttpClientHandler.ClientCertificates
// (slice D's REQ-024) accepts only X509Certificate2 OBJECTS. Exposing one view would leave
// half the consumers unable to use the accessor at all.
//
// This material MUST NOT be exposed as a `Vars` key, under any prefix (REQ-014): `Vars`
// feeds the reported and §14 event surface, and a certificate or key PATH written there
// would leak past the SecretString redaction model. It reaches an emitted script block only
// through ScriptGlobalVariables.Security, by reference, exactly like Secrets/Webhooks/Traces.
using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Vouchfx.Engine.Abstractions.Secrets;

namespace Vouchfx.Engine.Abstractions.Security;

/// <summary>
/// Resolves the client-side security configuration declared for a named service or
/// dependency (authenticated-infrastructure-mtls, REQ-014).
/// </summary>
/// <remarks>
/// Reached from an emitted step block as <see cref="ScriptGlobalVariables.Security"/>. A
/// target with no declared <c>security</c> block resolves to <see langword="null"/> — the
/// unsecured default, which every consuming provider must treat as "configure nothing".
/// </remarks>
public interface ISecurityConfigurationAccessor
{
    /// <summary>
    /// Returns the resolved client security configuration for <paramref name="targetName"/>,
    /// or <see langword="null"/> when that target declares no <c>security</c> block (or is
    /// not declared at all).
    /// </summary>
    /// <param name="targetName">
    /// The declared service or dependency name a step's own <c>target</c> field names — the
    /// same bare name, never a <c>svc::</c>/<c>conn::</c>-prefixed <c>Vars</c> key.
    /// </param>
    [SuppressMessage(
        "Naming",
        "CA1716:Identifiers should not match keywords",
        Justification =
            "'For' collides with the Visual Basic 'For' keyword, which CA1716 flags for " +
            "cross-language implementers. The name is fixed by the specification this member " +
            "implements (REQ-014 names the call 'Security.For(targetName)' verbatim), it reads " +
            "correctly at the one call site that matters - an emitted C# script block - and no " +
            "part of this engine or its provider SDK is consumable from Visual Basic: the " +
            "emitted delegate is C# script compiled by Roslyn, and every provider implements a " +
            "C# interface contract. Renaming would trade a real, spec-pinned name for a " +
            "hypothetical consumer that cannot exist.")]
    ISecurityConfiguration? For(string targetName);
}

/// <summary>
/// The resolved client-side security configuration for one declared target, under the
/// <c>security.profile</c> that target declares.
/// </summary>
/// <remarks>
/// Deliberately a configuration rather than a certificate bundle: <see cref="Certificates"/>
/// is the ONE view 1.0's <c>tls</c>/<c>mtls</c> profiles populate, and a later profile adds a
/// sibling view here (a SASL mechanism and credential, a bearer token, a Kerberos principal)
/// without any consumer needing a second accessor. See this file's header for why that
/// distinction is the whole point of the re-shaped REQ-014.
/// </remarks>
public interface ISecurityConfiguration
{
    /// <summary>
    /// The declared <c>security.profile</c> verbatim as authored (<c>"tls"</c> or
    /// <c>"mtls"</c> at 1.0; an open discriminator, REQ-019).
    /// </summary>
    string Profile { get; }

    /// <summary>
    /// The certificate view of this configuration, or <see langword="null"/> when the target
    /// declares no path-valued certificate field at all (e.g. <c>profile: tls</c> with no
    /// <c>caCert</c> — a normal, valid configuration in which the platform's own default
    /// trust store applies and the engine synthesises nothing, REQ-001/REQ-024).
    /// </summary>
    ISecurityCertificateMaterial? Certificates { get; }
}

/// <summary>
/// The certificate view of a target's security configuration, in BOTH the forms real client
/// libraries demand: host file paths and loaded <see cref="X509Certificate2"/> objects
/// (REQ-014).
/// </summary>
/// <remarks>
/// <para>
/// Every path is already resolved against the suite directory and existence-checked at
/// validation time (REQ-003/REQ-004, <c>EnvironmentSecurityValidator</c>) before any
/// implementation of this interface is constructed.
/// </para>
/// <para>
/// <strong>Containment is re-checked on BOTH views, on every read.</strong> Every PATH-VALUED
/// member below — the paths themselves, and the certificate objects derived from them — throws
/// <see cref="SecurityMaterialException"/> for a declared path that resolves outside the suite
/// directory, so the guarantee does not depend on which view a consumer happens to want. Stated
/// as a property of the member rather than as a count of members, so that a member added later
/// cannot silently falsify it: one surfacing no path at all — <see cref="ClientKeyPassword"/>, a
/// passphrase — is outside this rule rather than an exception to it.
/// </para>
/// <para>
/// The two views matter because they have disjoint consumers: librdkafka
/// reads only the paths and never the objects. It is DEFENCE IN DEPTH, not the primary
/// control — <c>EnvironmentSecurityValidator</c> has already rejected an escaping path on
/// every production route, before any container starts — and it is measured NOT to catch a
/// base-directory divergence, since a path resolved against the wrong base is still contained
/// within THAT base. Its value is failing closed for a caller that reaches an implementation
/// without the validator having run at all.
/// </para>
/// <para>
/// The <see cref="X509Certificate2"/> instances are OWNED by the accessor and live for the
/// scenario. A consumer BORROWS them — it must never dispose one, because the next step
/// resolving the same target receives the same instance. Measured against the pinned
/// runtime: handing the same instance to two successive
/// <c>HttpClient(handler, disposeHandler: true)</c> clients and disposing both leaves the
/// certificate fully usable, so borrowing is safe (see <c>HttpsClientCertificateTests</c>).
/// </para>
/// </remarks>
public interface ISecurityCertificateMaterial
{
    /// <summary>
    /// Resolved host path of the declared trust-anchor (CA) file, or <see langword="null"/>
    /// when <c>caCert</c> is not declared. <see langword="null"/> means ABSENT, never
    /// "defaulted": the engine must not synthesise a path (REQ-001, REQ-024).
    /// </summary>
    /// <exception cref="SecurityMaterialException">
    /// The declared path resolves outside the suite directory (REQ-003).
    /// </exception>
    string? CaCertificatePath { get; }

    /// <summary>
    /// Resolved host path of the declared client certificate, or <see langword="null"/> for a
    /// profile that presents no client identity (<c>profile: tls</c>).
    /// </summary>
    /// <exception cref="SecurityMaterialException">
    /// The declared path resolves outside the suite directory (REQ-003).
    /// </exception>
    string? ClientCertificatePath { get; }

    /// <summary>
    /// Resolved host path of the declared client private key, or <see langword="null"/> for a
    /// profile that presents no client identity (<c>profile: tls</c>).
    /// </summary>
    /// <exception cref="SecurityMaterialException">
    /// The declared path resolves outside the suite directory (REQ-003).
    /// </exception>
    string? ClientKeyPath { get; }

    /// <summary>
    /// The declared trust anchor loaded as a certificate object, or <see langword="null"/>
    /// when <c>caCert</c> is not declared.
    /// </summary>
    /// <exception cref="SecurityMaterialException">
    /// The declared file exists but cannot be read as a certificate, or its declared path
    /// resolves outside the suite directory (REQ-003).
    /// </exception>
    X509Certificate2? CaCertificate { get; }

    /// <summary>
    /// The declared client certificate loaded with its private key, ready to present during a
    /// mutual-TLS handshake — or <see langword="null"/> for a profile that presents no client
    /// identity.
    /// </summary>
    /// <exception cref="SecurityMaterialException">
    /// The declared certificate/key pair exists but cannot be loaded, the key does not match
    /// the certificate, or a declared path resolves outside the suite directory (REQ-003).
    /// </exception>
    X509Certificate2? ClientCertificate { get; }

    /// <summary>
    /// The resolved passphrase for an encrypted client private key, or
    /// <see langword="null"/> when the target declares no <c>clientKeyPassword</c> — the
    /// ordinary case, an unencrypted key (client-key-password spec, REQ-004).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="SecretString"/> rather than a <see cref="string"/> BECAUSE THE TYPE IS THE
    /// REDACTION CONTROL (§17): its <c>ToString()</c> returns
    /// <see cref="SecretString.RedactedMarker"/>, it is deliberately not
    /// <see cref="System.IFormattable"/>, its JSON converter writes the marker rather than the
    /// value, and it exposes no public length. A plain <c>string?</c> on an interface an
    /// emitted CSX block names by type would put a plaintext passphrase one interpolation away
    /// from the §14 event stream. A consumer that genuinely needs the characters — librdkafka
    /// takes a passphrase only as text — calls <see cref="SecretString.Reveal"/> explicitly,
    /// and that call is the deliberate, greppable audit point.
    /// </para>
    /// <para>
    /// <strong>The obligation that comes with that call</strong>, which the generic
    /// <see cref="SecretString.Reveal"/> documentation cannot state because it does not know
    /// where its value came from: the value MUST have been resolved through
    /// <see cref="ISecretAccessor.Resolve"/>, whose <see cref="SecretAccessor.ResolvedSecrets"/>
    /// ledger is what the runner's diagnostic and observation scrubbers read; a passphrase
    /// resolved by calling a resolver directly is invisible to them. Feed the revealed string
    /// straight into its sink — never into a local that outlives the call, a <c>Vars</c> key, or
    /// any diagnostic string.
    /// </para>
    /// <para>
    /// <strong>What the type does NOT give is ledger membership</strong>, and an earlier draft
    /// of these remarks claimed otherwise. <see cref="SecretString"/>'s constructor is
    /// <see langword="internal"/> (<c>SecretString.cs</c>), with <c>InternalsVisibleTo</c>
    /// granted only to <c>Vouchfx.Engine.Abstractions.Tests</c>, so the load path cannot MINT a
    /// <see cref="SecretString"/>: it must obtain one from a resolver. That is a strictly
    /// narrower guarantee than "the value is in the scrub ledger", because a resolver called
    /// DIRECTLY registers nothing — <c>EnvironmentSecretResolver</c> is a public sealed class
    /// whose public <c>Resolve</c> ends by constructing a <see cref="SecretString"/>
    /// (<c>Secrets/ISecretResolver.cs</c>), <c>VaultSecretResolver</c> has the same shape, and
    /// <c>ScenarioRunner.BuildSecretResolvers</c> already constructs both inside
    /// <c>Vouchfx.Engine.Runtime</c>. Only <see cref="ISecretAccessor.Resolve"/> records into
    /// <see cref="SecretAccessor.ResolvedSecrets"/> (<c>Secrets/ISecretAccessor.cs</c>). The
    /// obligation stated above is therefore a DISCIPLINE that NO type enforces — not this one,
    /// and not any other in the engine. It holds only for as long as callers keep it.
    /// </para>
    /// <para>
    /// DEFAULT-IMPLEMENTED rather than abstract, per CLAUDE.md's rule for evolving an
    /// engine-supplied interface others implement: every existing implementor — the production
    /// material and the test doubles — keeps compiling untouched, and the default's own
    /// <see langword="null"/> means "no passphrase declared", never "not yet loaded".
    /// </para>
    /// </remarks>
    SecretString? ClientKeyPassword => null;

    /// <summary>
    /// Decides whether a remote (server) certificate is trusted under THIS target's declared
    /// trust anchor — the transport-agnostic shape every
    /// <see cref="System.Net.Security.RemoteCertificateValidationCallback"/>-style consumer
    /// needs (<c>HttpClientHandler.ServerCertificateCustomValidationCallback</c>,
    /// <c>SslStream</c>, and any client library taking the same delegate).
    /// </summary>
    /// <param name="remoteCertificate">The certificate the peer presented.</param>
    /// <param name="platformBuiltChain">
    /// The chain the PLATFORM built for this handshake, exactly as the validation callback
    /// received it, or <see langword="null"/> for a consumer that has none.
    /// <strong>Untrusted input</strong> — see the provenance remarks below for the one thing
    /// it is used for and the one thing it must never do.
    /// </param>
    /// <param name="sslPolicyErrors">The platform's own verdict on that certificate.</param>
    /// <returns>
    /// <see langword="true"/> when the certificate is acceptable.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>A declared <c>caCert</c> is a PIN, not additive trust.</strong> When a trust
    /// anchor is declared, it is consulted on EVERY path — including
    /// <see cref="SslPolicyErrors.None"/>. A certificate the machine's own trust store already
    /// accepts but that does NOT chain to the declared anchor is REJECTED. An author writing
    /// <c>caCert</c> for a private CA reads it as "only this CA", and the gap between the word
    /// and the behaviour is where trust in a tool is lost; the configuration pinning would
    /// break — a private anchor declared for a peer that in fact chains publicly — is
    /// incoherent rather than legitimate. With NO <c>caCert</c> declared this reduces to the
    /// platform's own verdict (<paramref name="sslPolicyErrors"/> must be
    /// <see cref="SslPolicyErrors.None"/>): the engine adds no trust anchor, narrows nothing
    /// and relaxes nothing.
    /// </para>
    /// <para>
    /// Only <see cref="SslPolicyErrors.RemoteCertificateChainErrors"/> is forgivable, and only
    /// by rebuilding the chain against the declared CA as a CUSTOM ROOT
    /// (<see cref="X509ChainTrustMode.CustomRootTrust"/>) — the failure a private enterprise
    /// CA produces, measured as <c>PartialChain</c> against the platform's default trust
    /// store. <see cref="SslPolicyErrors.RemoteCertificateNameMismatch"/> and
    /// <see cref="SslPolicyErrors.RemoteCertificateNotAvailable"/> are NEVER forgiven: a
    /// declared CA says which issuer to trust, not which hostname, and treating a name
    /// mismatch as acceptable would turn "trust this CA" into "trust anything it ever
    /// signed, presented by anyone".
    /// </para>
    /// <para>
    /// <strong>The rebuilt chain must be at least as strict as the platform's, never less.</strong>
    /// A custom callback replaces the platform's verdict wholesale, so every constraint the
    /// platform would have applied has to be re-applied by hand or it is silently dropped. Two
    /// that matter, both measured: the <c>serverAuth</c> application policy
    /// (<c>1.3.6.1.5.5.7.3.1</c>) — without it a chain accepts a server certificate carrying
    /// <c>EKU = clientAuth</c> only, and in mutual TLS the CA that signs the server signs every
    /// client, so that is a server-impersonation path — and disabled certificate downloads,
    /// because suppressing revocation checking does NOT suppress AIA <c>caIssuers</c> fetching,
    /// which is governed separately and otherwise issues an outbound request to a
    /// PEER-CONTROLLED URL during the handshake, on the rejection path.
    /// </para>
    /// <para>
    /// <strong><paramref name="platformBuiltChain"/> provenance — it is a SUPERSET of the
    /// wire.</strong> Its <c>ChainElements</c> are the platform's own BUILT chain, not a
    /// transcript of what the peer sent: the platform contributes whatever else it can find
    /// locally. Measured on a Windows host, both directions: a handshake where the server sent
    /// ONLY its leaf produced a two-element chain, the root having come from a local cache; and
    /// a two-tier server that withheld its intermediate still validated, the platform having
    /// cached that intermediate from an earlier connection. Do not read this parameter as
    /// evidence of what a peer transmitted, and do not write a test that infers transmission
    /// from it without per-run unique subject names to defeat that cache.
    /// </para>
    /// <para>
    /// That breadth carries no security consequence, which is why the parameter is taken at all.
    /// Its certificates are added to the rebuilt chain's <c>ExtraStore</c> ONLY, which lets path
    /// building find the intermediates a two-tier PKI (offline root, issuing intermediate)
    /// requires — the normal enterprise CA shape, which cannot validate without them. They are
    /// candidate links, never anchors: the rebuilt chain terminates only at the DECLARED
    /// anchor in its <c>CustomTrustStore</c>, so a self-signed root supplied by the peer as an
    /// "intermediate" does not become trusted by virtue of appearing here (measured, and
    /// pinned by test). A later profile that lets an author declare intermediates of their own
    /// contributes them to the same <c>ExtraStore</c> from the accessor's own state, so no
    /// second parameter and no second overload is needed for it.
    /// </para>
    /// </remarks>
    bool TrustsRemoteCertificate(
        X509Certificate2? remoteCertificate, X509Chain? platformBuiltChain, SslPolicyErrors sslPolicyErrors);
}

/// <summary>
/// Raised when a declared, existence-checked security artefact cannot be loaded as usable
/// certificate material — a malformed PEM, a key that does not match its certificate, an
/// unreadable file.
/// </summary>
/// <remarks>
/// Distinct from the validation-time failures <c>EnvironmentSecurityValidator</c> reports
/// (containment and existence, which run before any container starts): by the time this can
/// be raised the paths are known to exist, so the fault is in the CONTENT. A provider
/// catching it reports an environment error, not a test failure.
/// </remarks>
public sealed class SecurityMaterialException : Exception
{
    /// <summary>Initialises a new instance with a message.</summary>
    public SecurityMaterialException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance with a message and the underlying cause.</summary>
    public SecurityMaterialException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Initialises a new instance with no message.</summary>
    public SecurityMaterialException()
    {
    }
}

/// <summary>
/// The no-op <see cref="ISecurityConfigurationAccessor"/> a run with no declared
/// <c>security</c> block uses: every target resolves to <see langword="null"/>.
/// </summary>
/// <remarks>
/// Mirrors <see cref="Vouchfx.Engine.Abstractions.Webhooks.NullWebhookCaptureAccessor"/> and
/// <see cref="Vouchfx.Engine.Abstractions.Traces.NullTraceCaptureAccessor"/>: a Null-object
/// default keeps every existing <see cref="ScriptGlobalVariables"/> call site compiling
/// unchanged and costs a run with no security nothing.
/// </remarks>
public sealed class NullSecurityConfigurationAccessor : ISecurityConfigurationAccessor
{
    /// <summary>The shared singleton instance.</summary>
    public static NullSecurityConfigurationAccessor Instance { get; } = new();

    /// <inheritdoc />
    public ISecurityConfiguration? For(string targetName) => null;
}
