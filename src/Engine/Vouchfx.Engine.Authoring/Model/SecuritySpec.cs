// Vouchfx.Engine.Authoring — SecuritySpec (authenticated-infrastructure-mtls, PR A).
//
// Strongly-typed records for the optional `security` block on both
// `environment.services.<name>` and `environment.dependencies.<name>` (spec
// REQ-001/REQ-002/REQ-003). Kind-generic: the same shape applies to every
// dependency kind and every service — there is no per-kind restriction, unlike
// e.g. Kafka's `schemaRegistry` or Azure Service Bus's `queues`/`topics`.

namespace Vouchfx.Engine.Authoring.Model;

/// <summary>
/// The parsed <c>security</c> block declared on a service or dependency: transport
/// security (TLS or mutual TLS) for that endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Every field is bound from its raw scalar text with no requiredness enforced by
/// this parser — mirroring the rest of <see cref="YamlDocumentParser"/>'s
/// deliberately lenient design (see that file's header remarks). <c>profile</c>/
/// <c>endpoint</c> requiredness, the <c>mtls</c>-requires-<c>clientCert</c>/
/// <c>clientKey</c> rule, and the <c>tls</c>-forbids-<c>clientCert</c>/<c>clientKey</c>
/// rule are all enforced by the JSON Schema layer
/// (<c>root-language-schema.json</c>'s <c>$defs/security</c>). Path containment
/// (suite-directory escape) and on-host existence for every declared path-valued
/// field are enforced separately, by <c>Vouchfx.Engine.Runtime.EnvironmentSecurityValidator</c>,
/// which runs pre-topology (the same stage the provider pipeline's bind/validate
/// pass runs in).
/// </para>
/// <para>
/// Future additions to this shape MUST be init-only properties, never new positional
/// constructor parameters: <c>Vouchfx.Engine.Authoring</c> is a packable assembly, and
/// inserting a new positional parameter would change this record's primary
/// constructor's parameter order/arity and its compiler-generated <c>Deconstruct</c> —
/// a binary-breaking change for any already-compiled caller. This is the same
/// binary-compatibility precedent <see cref="Vouchfx.Engine.Authoring.Model.DependencySpec.Image"/>
/// already established for its own record; an init-only property is purely additive.
/// </para>
/// </remarks>
/// <param name="Profile">
/// The transport security mechanism as authored: <c>"tls"</c> or <c>"mtls"</c> (an open
/// discriminator, REQ-019 — a dotted <c>&lt;vendor&gt;.&lt;name&gt;</c> form is also
/// schema-legal, reserved for an out-of-tree profile). Retained verbatim (never
/// case-normalised) so an incorrectly-cased value (e.g. <c>"TLS"</c>) is rejected by the
/// schema's case-sensitive pattern rather than silently accepted here; an unrecognised
/// profile name is rejected separately, at validation time, against the engine's
/// security-profile registry (<c>DocumentValidator.CollectUnknownSecurityProfileErrors</c>),
/// not by this record.
/// </param>
/// <param name="Endpoint">
/// The secured endpoint as authored: either a port number or a declared endpoint
/// name. Kept as raw scalar text rather than parsed to <see cref="int"/> — unlike
/// <see cref="ServiceSpec.HttpPort"/>, which always means a port number,
/// <c>endpoint</c> may equally name a declared endpoint (a non-numeric string), so
/// pre-parsing here would lose that second, equally valid shape. Resolving which
/// shape a given value is belongs to a later stage (the REQ-005 probe), not this
/// authoring-surface PR.
/// </param>
/// <param name="CaCert">
/// Path to a CA certificate (trust anchor) file, relative to the suite directory.
/// Optional for both <c>tls</c> and <c>mtls</c>; <see langword="null"/> when the
/// author never declares it — REQ-004(b) requires that an undeclared <c>caCert</c>
/// is treated as absent, never as a missing-but-implied field.
/// </param>
/// <param name="ClientCert">
/// Path to the client certificate file presented during a mutual-TLS handshake,
/// relative to the suite directory. Required together with <see cref="ClientKey"/>
/// for <c>profile: mtls</c>; not applicable to <c>profile: tls</c> (enforced by the schema).
/// </param>
/// <param name="ClientKey">
/// Path to the client private key file presented during a mutual-TLS handshake,
/// relative to the suite directory. Required together with <see cref="ClientCert"/>
/// for <c>profile: mtls</c>; not applicable to <c>profile: tls</c> (enforced by the schema).
/// </param>
/// <param name="ServerArtifacts">
/// Host files to be copied into the container at topology-build time (REQ-016's
/// authoring surface only — the actual container-file-copy orchestration is a later
/// PR). <see langword="null"/> when the security block declares no
/// <c>serverArtifacts</c> entries.
/// </param>
public sealed record SecuritySpec(
    string? Profile,
    string? Endpoint,
    string? CaCert,
    string? ClientCert,
    string? ClientKey,
    IReadOnlyList<SecurityServerArtifactSpec>? ServerArtifacts)
{
    /// <summary>
    /// The passphrase for an encrypted <see cref="ClientKey"/>, as DECLARED TEXT: a
    /// <c>${secret:&lt;source&gt;/&lt;path&gt;}</c> reference, retained verbatim and
    /// UNRESOLVED at this layer (client-key-password spec, REQ-003).
    /// <see langword="null"/> when the author declares no <c>clientKeyPassword</c> — the
    /// ordinary case, an unencrypted key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference is never resolved here, and on any schema-validated path this record
    /// holds only a REFERENCE, never a passphrase value: that the text is a single whole
    /// <c>${secret:}</c> reference and never a literal is enforced by the JSON Schema layer's
    /// own <c>pattern</c> (<c>root-language-schema.json</c>'s <c>$defs/security</c>), like
    /// every sibling field's shape. A direct engine embedder that bypasses the schema CAN bind
    /// a literal here — this parser enforces no shape, by design, and
    /// <c>SecuritySpecBindingTests.Parse_ClientKeyPasswordLiteral_IsStillBound_ParserStaysLenient</c>
    /// pins exactly that — so no consumer may assume the text is non-secret UNTIL IT HAS PROVED
    /// OTHERWISE. The proof is <c>SecretReference.ValidateSecretBearingField</c> RETURNING TRUE.
    /// Only then is the text known to be a pointer, and §17 permits quoting a pointer — which is
    /// why that method's unknown-source diagnostic may name the reference it refuses.
    /// </para>
    /// <para>
    /// <strong><c>SecretReference.TryParse</c> alone is NOT that proof</strong>, and a consumer
    /// writing <c>if (TryParse(v)) quote(v);</c> reproduces a disclosure defect this branch has
    /// already had to fix once. <c>TryParse</c> asks only whether one whole token spans the
    /// value; because a reference path terminates at the first closing brace and is otherwise
    /// unrestricted, a value can satisfy that while still containing a further lead-in swallowed
    /// inside the path — and everything after that lead-in is then arbitrary author text that
    /// <c>TryParse</c> has said nothing about. <c>ValidateSecretBearingField</c> applies the
    /// remaining rule and withholds such a value.
    /// </para>
    /// <para>
    /// The rule, stated once: PROVE it is a pointer with
    /// <c>ValidateSecretBearingField</c>, then you may quote it. Never "assume it is a pointer",
    /// and never "parse it and assume".
    /// </para>
    /// <para>
    /// §17 requires resolution at first USE of the certificate material — which for this field is
    /// the certificate load, after the topology is up — so that no secret value is ever baked into
    /// the compiled script's IL and the reproducibility envelope hashes the reference rather than
    /// the value.
    /// </para>
    /// <para>
    /// This record's compiler-generated <c>ToString()</c> prints this property, so never
    /// interpolate a <see cref="SecuritySpec"/> whole into a diagnostic, event or report —
    /// name the field you need. Deliberately NOT closed by a hand-written
    /// <c>PrintMembers</c> override: an explicit override must enumerate all seven members, so
    /// a future eighth field would be silently dropped from <c>ToString()</c> — a worse drift
    /// trap than the hazard it closes.
    /// </para>
    /// <para>
    /// Declared as an init-only property rather than a positional record parameter, per
    /// this record's own binary-compatibility rule in the remarks above: an init-only
    /// property is purely additive, whereas a seventh positional parameter would change
    /// the primary constructor's arity and the compiler-generated <c>Deconstruct</c>.
    /// </para>
    /// </remarks>
    public string? ClientKeyPassword { get; init; }
}

/// <summary>
/// A single <c>{ source, target }</c> pair declared under a <see cref="SecuritySpec.ServerArtifacts"/>
/// list (REQ-016's authoring surface): a host file copied into the container at the
/// declared in-container path.
/// </summary>
/// <param name="Source">
/// Host path of the file to copy, relative to the suite directory. Must resolve
/// inside that directory (REQ-003, EDGE-006) and must exist on the host (REQ-004)
/// when declared — checked by <c>Vouchfx.Engine.Runtime.EnvironmentSecurityValidator</c>,
/// never by this record itself.
/// </param>
/// <param name="Target">
/// The absolute path inside the container the file is copied to. A container-side
/// path, never resolved or existence-checked against the host filesystem.
/// </param>
public sealed record SecurityServerArtifactSpec(
    string? Source,
    string? Target);
