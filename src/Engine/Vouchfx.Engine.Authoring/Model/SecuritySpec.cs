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
/// deliberately lenient design (see that file's header remarks). <c>mode</c>/
/// <c>endpoint</c> requiredness, the <c>mtls</c>-requires-<c>clientCert</c>/
/// <c>clientKey</c> rule, and the <c>tls</c>-forbids-<c>clientCert</c>/<c>clientKey</c>
/// rule are all enforced by the JSON Schema layer
/// (<c>root-language-schema.json</c>'s <c>$defs/security</c>). Path containment
/// (suite-directory escape) and on-host existence for every declared path-valued
/// field are enforced separately, by <c>Vouchfx.Engine.Runtime.EnvironmentSecurityValidator</c>,
/// which runs pre-topology (the same stage the provider pipeline's bind/validate
/// pass runs in).
/// </para>
/// </remarks>
/// <param name="Mode">
/// The transport security mode as authored: <c>"tls"</c> or <c>"mtls"</c>. Retained
/// verbatim (never case-normalised) so an incorrectly-cased value (e.g. <c>"TLS"</c>)
/// is rejected by the schema's case-sensitive enum rather than silently accepted here.
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
/// for <c>mode: mtls</c>; not applicable to <c>mode: tls</c> (enforced by the schema).
/// </param>
/// <param name="ClientKey">
/// Path to the client private key file presented during a mutual-TLS handshake,
/// relative to the suite directory. Required together with <see cref="ClientCert"/>
/// for <c>mode: mtls</c>; not applicable to <c>mode: tls</c> (enforced by the schema).
/// </param>
/// <param name="ServerArtifacts">
/// Host files to be copied into the container at topology-build time (REQ-016's
/// authoring surface only — the actual container-file-copy orchestration is a later
/// PR). <see langword="null"/> when the security block declares no
/// <c>serverArtifacts</c> entries.
/// </param>
public sealed record SecuritySpec(
    string? Mode,
    string? Endpoint,
    string? CaCert,
    string? ClientCert,
    string? ClientKey,
    IReadOnlyList<SecurityServerArtifactSpec>? ServerArtifacts);

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
