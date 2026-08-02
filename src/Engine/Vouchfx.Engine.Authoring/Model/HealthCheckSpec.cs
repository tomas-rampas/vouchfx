// Vouchfx.Engine.Authoring — HealthCheckSpec (services-generalisation, PR B).
//
// Strongly-typed record for the optional `healthCheck` block on
// `environment.services.<name>` (services-generalisation spec REQ-009). Service-only:
// unlike `security` (REQ-001), a dependency's health gate is the engine's own concern
// (§4 invariant — WaitFor the most-specific dependency resource), not author-declarable.

namespace Vouchfx.Engine.Authoring.Model;

/// <summary>
/// The parsed <c>healthCheck</c> block declared on a service: an author-chosen health
/// check replacing the engine's implicit default (an HTTP GET against <c>/</c>).
/// </summary>
/// <remarks>
/// <para>
/// Every field is bound from its raw scalar text with no requiredness enforced by this
/// parser — mirroring <see cref="SecuritySpec"/>'s own deliberately lenient design (see
/// that record's header remarks, and <see cref="YamlDocumentParser"/>'s own). <c>type</c>
/// requiredness, and the <c>tcp</c>-requires-<c>port</c>/<c>tcp</c>-forbids-<c>path</c> and
/// <c>http</c>-forbids-<c>port</c> rules, are all enforced by the JSON Schema layer
/// (<c>root-language-schema.json</c>'s <c>$defs/serviceHealthCheck</c>). Cross-referencing
/// <see cref="Port"/> against the service's own declared <see cref="ServiceSpec.Ports"/>/
/// <see cref="ServiceSpec.HttpPort"/> is the ORCHESTRATION-layer mapper's job
/// (<c>EnvironmentMapper</c>), not the schema's — the schema validates shape, not
/// cross-field semantic consistency, exactly like <c>${conn:name}</c> referencing an
/// unknown dependency (see <c>EnvironmentMapper.ValidateEnvValue</c>'s own precedent).
/// </para>
/// <para>
/// Future additions to this shape MUST be init-only properties, never new positional
/// constructor parameters — see <see cref="SecuritySpec"/>'s own remarks for the full
/// binary-compatibility rationale (this record lives in the same packable assembly).
/// </para>
/// </remarks>
/// <param name="Type">
/// The health-check kind as authored: <c>"tcp"</c> or <c>"http"</c>. Retained verbatim
/// (never case-normalised) so an incorrectly-cased value is rejected by the schema's
/// case-sensitive enum rather than silently accepted here.
/// </param>
/// <param name="Path">
/// The HTTP path probed for <c>type: http</c>, e.g. <c>"/healthz"</c>. Defaults to
/// <c>"/"</c> when <see langword="null"/>. Not applicable to <c>type: tcp</c>.
/// </param>
/// <param name="Port">
/// The TCP port probed for <c>type: tcp</c>. Must name one of the service's own declared
/// ports (an entry under <see cref="ServiceSpec.Ports"/>, or <see cref="ServiceSpec.HttpPort"/>
/// when the service declares no <c>ports:</c> at all) — checked by
/// <c>EnvironmentMapper.Map</c>'s eager validation, not by this record. Not applicable to
/// <c>type: http</c>.
/// </param>
public sealed record HealthCheckSpec(
    string? Type,
    string? Path,
    int? Port);
