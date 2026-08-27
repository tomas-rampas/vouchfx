// Vouchfx.Engine.Authoring — EnvironmentSpec (S03-B-01; Seed bound in S05-A-01).
//
// Strongly-typed records for the optional `environment` top-level section of a
// .e2e.yaml file (docs/02 §3.2).

using System.Text;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Authoring.Model;

/// <summary>
/// Infrastructure declaration for the test (§3.2).
/// </summary>
/// <remarks>
/// Declares the services under test and the managed dependencies (databases,
/// brokers, caches) that Aspire will provision.  All fields are optional.
/// </remarks>
/// <param name="Services">
/// Map from logical service name to its specification.  Services are the
/// customer's own code brought into the topology either as a container image
/// or a csproj path.
/// </param>
/// <param name="Dependencies">
/// Map from logical dependency name to its specification.  Dependencies are
/// managed resources (postgres, kafka, etc.) that Aspire knows how to provision.
/// </param>
/// <param name="Seed">
/// Optional seed block applied after the topology is healthy and before the
/// first step runs (docs/02 §3.2.2).  Bound to a strongly-typed
/// <see cref="SeedSpec"/> in S05-A-01; <see langword="null"/> when the
/// <c>seed</c> block is absent or empty.
/// </param>
/// <param name="ImageRegistry">
/// Optional default registry prefix applied to every un-prefixed image reference
/// in this environment (§3.2.1).
/// </param>
/// <param name="ImagePullPolicy">
/// Optional default pull policy for container images: <c>Always</c>,
/// <c>Missing</c>, or <c>Never</c>.  Overrides the engine default (§3.2.1).
/// </param>
public sealed record EnvironmentSpec(
    IReadOnlyDictionary<string, ServiceSpec>? Services,
    IReadOnlyDictionary<string, DependencySpec>? Dependencies,
    SeedSpec? Seed,
    string? ImageRegistry,
    string? ImagePullPolicy);

/// <summary>
/// Specification for a service under test (§3.2).
/// </summary>
/// <remarks>
/// Exactly one of <see cref="Image"/> or <see cref="Project"/> should be
/// supplied; the parser retains both as optional and defers validation to the
/// JSON Schema layer.
/// </remarks>
/// <param name="Image">
/// OCI image reference.  Preferred form for speed and isolation.
/// </param>
/// <param name="Project">
/// Relative path to a <c>.csproj</c> file.  The engine builds and runs this
/// project as part of suite startup.
/// </param>
/// <param name="ImagePullPolicy">
/// Service-level pull policy override (§3.2.1).
/// </param>
/// <param name="HttpPort">
/// Optional explicit HTTP port when the image does not expose a well-known port.
/// </param>
/// <param name="Env">
/// Optional environment-variable mapping applied to the service's container/project
/// (SUT configuration surface).  Each value is a literal string that may embed zero or
/// more <c>${conn:name}</c> / <c>${conn:name.part}</c> references to a dependency declared
/// under <see cref="EnvironmentSpec.Dependencies"/>; the orchestration-layer mapper resolves these to
/// Aspire-native, container-network values (never the host-published endpoint) via a
/// <c>ReferenceExpression</c>.  <see langword="null"/> when the service declares no
/// <c>env:</c> block — and, unlike <see cref="DependencySpec.Env"/>, ALSO
/// <see langword="null"/> for an empty <c>env: {}</c>, which the parser deliberately
/// collapses so that spelling stays indistinguishable from absent. The asymmetry is
/// deliberate: the change that added the dependency counterpart was fenced against altering
/// service <c>env:</c> behaviour in any way, so the collapse this field has always had stayed.
/// </param>
public sealed record ServiceSpec(
    string? Image,
    string? Project,
    string? ImagePullPolicy,
    int? HttpPort,
    IReadOnlyDictionary<string, string>? Env)
{
    /// <summary>
    /// Optional transport security declaration (TLS/mTLS) for this service's endpoint
    /// (authenticated-infrastructure-mtls spec, REQ-001). <see langword="null"/> when
    /// the service declares no <c>security:</c> block — today's unauthenticated-by-default
    /// behaviour is unchanged.
    /// </summary>
    /// <remarks>
    /// Declared as an init-only property rather than a positional record parameter,
    /// mirroring <see cref="DependencySpec.Image"/>'s own remarks: this record lives in a
    /// packable assembly, and inserting a new positional parameter would change the
    /// primary constructor's parameter order/arity and the compiler-generated
    /// <c>Deconstruct</c> — a binary-breaking change for any already-compiled caller. An
    /// init-only property is purely additive.
    /// </remarks>
    public SecuritySpec? Security { get; init; }

    /// <summary>
    /// One or more TCP ports this service exposes, beyond (or instead of) the implicit
    /// default HTTP endpoint (services-generalisation spec, REQ-008). <see langword="null"/>
    /// when the service declares no <c>ports:</c> list — today's HTTP-only-by-default
    /// behaviour (an implicit HTTP endpoint on <see cref="HttpPort"/>, or port 80) is
    /// unchanged. When declared, the service is brought up via Aspire's generic TCP
    /// endpoint for each port rather than an implicit <c>WithHttpEndpoint</c>, so a
    /// non-HTTP system under test (e.g. a customer-supplied Kafka broker) can be targeted
    /// at all; the implicit default HTTP endpoint is then suppressed unless
    /// <see cref="HttpPort"/> is ALSO explicitly declared (a hybrid service exposing both).
    /// </summary>
    /// <remarks>
    /// An init-only property for the same binary-compatibility reason as
    /// <see cref="Security"/> — see its own remarks.
    /// </remarks>
    public IReadOnlyList<int>? Ports { get; init; }

    /// <summary>
    /// The host port each pinned container port publishes on (REQ-025), keyed by CONTAINER
    /// port. <see langword="null"/> when no entry of <see cref="Ports"/> used the
    /// <c>"&lt;host&gt;:&lt;container&gt;"</c> form, and never containing an entry for a port
    /// declared as a bare integer — for which the orchestrator allocates a host port as before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A companion map rather than a richer <see cref="Ports"/> element type</strong>,
    /// and the reason is what reads <see cref="Ports"/>. Every consumer of that list wants the
    /// CONTAINER port and nothing else: <c>ServiceEndpointNaming</c> derives endpoint names from
    /// it (<c>tcp-9093</c>), <c>security.endpoint</c> resolves its selector against it (REQ-002),
    /// and the health-check cross-reference and default probe read it. Widening the element type
    /// would have made every one of those sites decide which half of a pair it meant, for a field
    /// that is <see langword="null"/> in every suite written before this requirement. Keeping
    /// <see cref="Ports"/> a list of container ports leaves all of them byte-for-byte unchanged
    /// and confines the new fact to the two sites that consume it: the mapper, which publishes
    /// the endpoints, and <c>SuiteTopology</c>'s pre-flight, which must refuse a pinned port this
    /// machine cannot bind before any container starts.
    /// </para>
    /// <para>
    /// Keyed by container port because that is the half the rest of the language already names —
    /// an endpoint is <c>tcp-9093</c> whether or not its host side is pinned.
    /// </para>
    /// <para>
    /// An init-only property for the same binary-compatibility reason as
    /// <see cref="Security"/> — see its own remarks.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<int, int>? PinnedHostPorts { get; init; }

    /// <summary>
    /// An author-declared health check for this service (services-generalisation spec,
    /// REQ-009), replacing the implicit default (<c>WithHttpHealthCheck(path: "/",
    /// endpointName: "http")</c>) applied when this is <see langword="null"/> on an
    /// HTTP-shaped service (no <see cref="Ports"/> declared). A <see cref="Ports"/>-declared
    /// (non-HTTP-by-default) service with no <see cref="HealthCheck"/> does NOT go unchecked
    /// (M4 fix, fix round 2 — this summary previously said it did, before that fix landed):
    /// it defaults to a <c>tcp</c> probe against the first declared port, never an HTTP
    /// request the service may not even be able to answer, but a real check rather than none
    /// at all — see docs/02 §3.2.6a for what a <c>tcp</c> probe does and does not prove.
    /// </summary>
    /// <remarks>
    /// An init-only property for the same binary-compatibility reason as
    /// <see cref="Security"/> — see its own remarks.
    /// </remarks>
    public HealthCheckSpec? HealthCheck { get; init; }

    /// <summary>
    /// Withholds <see cref="Security"/> from <c>ToString()</c> (#408), printing every other
    /// member exactly as the compiler-generated <c>PrintMembers</c> did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The compiler-generated <c>ToString()</c> expands every member, and one of them is a whole
    /// <see cref="SecuritySpec"/> — which, at the time, printed
    /// <see cref="SecuritySpec.ClientKeyPassword"/> in full. Measured against the built assembly
    /// with a canary in that field, the default gave:
    /// <code>
    /// ServiceSpec { Image = img:1, ..., Security = SecuritySpec
    ///   { Profile = mtls, ..., ClientKeyPassword = P@ssw0rd-LEAK-CANARY }, ... }
    /// </code>
    /// </para>
    /// <para>
    /// #408 was fixed at <see cref="SecuredTarget"/> and only there, leaving this record and
    /// <see cref="DependencySpec"/> — the two OTHER holders of the same type — disclosing the
    /// canary in full. The rule accordingly lives in <see cref="RecordSecurityPrinting"/> now, and
    /// applies to any member whose value is a <see cref="SecuritySpec"/>; see that type for why a
    /// hand-written override is acceptable for a redaction guard. <see cref="SecuritySpec"/> has
    /// withheld its own passphrase since 2026-08-27, so this override is the second line rather
    /// than the only one, and it stays for the same reason a redacted block is a better rendering
    /// here than an expanded one.
    /// </para>
    /// <para>
    /// <strong>Every member of this record is listed below, and that obligation is pinned by a
    /// test rather than by review:</strong> a hand-written <c>PrintMembers</c> cannot enumerate a
    /// member that does not exist yet, so
    /// <c>SecuritySpecDisclosureTests.ServiceSpec_HasExactlyTheMembersPrintMembersEnumerates</c>
    /// fails the moment one is added, forcing a conscious decision about whether it belongs in the
    /// rendering.
    /// </para>
    /// </remarks>
    private bool PrintMembers(StringBuilder builder) =>
        RecordSecurityPrinting.Print(
            builder,
            (nameof(Image), Image),
            (nameof(Project), Project),
            (nameof(ImagePullPolicy), ImagePullPolicy),
            (nameof(HttpPort), HttpPort),
            (nameof(Env), Env),
            (nameof(Security), Security),
            (nameof(Ports), Ports),
            (nameof(PinnedHostPorts), PinnedHostPorts),
            (nameof(HealthCheck), HealthCheck));
}

/// <summary>
/// Specification for a managed Aspire dependency (§3.2).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Type"/> is mandatory (e.g. <c>postgres</c>, <c>kafka</c>).
/// <see cref="Version"/> is an optional string because some types accept
/// non-numeric version identifiers (e.g. <c>"16"</c> for Postgres).
/// </para>
/// <para>
/// Provider-specific fields (e.g. <c>schemaRegistry: true</c> for Kafka) are
/// retained verbatim in <see cref="Extra"/> for consumption by the relevant
/// resource contributor in a later workstream.
/// </para>
/// </remarks>
/// <param name="Type">
/// Dependency type identifier, e.g. <c>postgres</c>, <c>kafka</c>,
/// <c>elasticsearch</c>.
/// </param>
/// <param name="Version">
/// Optional version string for the dependency's container image tag. This is
/// only a tag fragment (e.g. <c>"16"</c>) applied to the provider's built-in
/// image repository — it cannot name a different repository or registry; see
/// <see cref="Image"/> for that.
/// </param>
/// <param name="Extra">
/// Raw YAML mapping node retaining any additional provider-specific fields.
/// <see langword="null"/> when no extra fields are present.
/// </param>
public sealed record DependencySpec(
    string Type,
    string? Version,
    YamlMappingNode? Extra)
{
    /// <summary>
    /// Optional full OCI image reference overriding the provider's default
    /// image repository (e.g. <c>nexus.example.com/mirror/mongo:7.0</c>, or
    /// with a digest, <c>myrepo/kafka@sha256:...</c>). Unlike
    /// <see cref="Version"/>, which is only a tag fragment applied to the
    /// provider's built-in repository, this names the repository (and
    /// registry) itself — the only way to point a dependency at a private
    /// registry (e.g. a customer's Nexus mirror holding their own mongo,
    /// sqlserver, or kafka images). May itself embed a tag or digest;
    /// reconciling an embedded tag against a sibling <see cref="Version"/> is
    /// the orchestration-layer mapper's responsibility, not this record's.
    /// <see langword="null"/> when the dependency does not override the image.
    /// </summary>
    /// <remarks>
    /// Declared as an init-only property rather than a positional record
    /// parameter deliberately: this record lives in a packable assembly, and
    /// inserting a new positional parameter would change the primary
    /// constructor's parameter order/arity and the compiler-generated
    /// <c>Deconstruct</c> — a binary-breaking change for any already-compiled
    /// caller. An init-only property is purely additive.
    /// </remarks>
    public string? Image { get; init; }

    /// <summary>
    /// Optional transport security declaration (TLS/mTLS) for this dependency's
    /// endpoint (authenticated-infrastructure-mtls spec, REQ-001). <see langword="null"/>
    /// when the dependency declares no <c>security:</c> block — today's
    /// unauthenticated-by-default behaviour is unchanged. Kind-generic: every one of
    /// the thirteen dependency kinds accepts this field, unlike <see cref="Extra"/>'s
    /// per-kind-restricted siblings (<c>schemaRegistry</c>, <c>queues</c>, <c>topics</c>).
    /// </summary>
    /// <remarks>
    /// An init-only property for the same binary-compatibility reason as
    /// <see cref="Image"/> — see its own remarks.
    /// </remarks>
    public SecuritySpec? Security { get; init; }

    /// <summary>
    /// Optional environment-variable mapping for this dependency's container
    /// (dependency-env spec, REQ-001) — the managed-resource counterpart to
    /// <see cref="ServiceSpec.Env"/>, by which a managed dependency whose image is configured
    /// through environment variables is configurable — e.g. <c>MSSQL_COLLATION</c>, on the
    /// image the engine's own <c>AddSqlServer</c> mapping starts.
    /// <para>
    /// <see langword="null"/> when the dependency declares no <c>env:</c> block, which is
    /// the shape every suite written before this field has. An empty <c>env: {}</c> yields
    /// an EMPTY dictionary here rather than <see langword="null"/> — unlike
    /// <see cref="ServiceSpec.Env"/>, whose empty spelling the parser collapses to
    /// <see langword="null"/> — so "declared, empty" stays distinguishable from
    /// "not declared".
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each value is retained here as its literal raw scalar text, exactly as
    /// <see cref="ServiceSpec.Env"/>'s is; this record applies no coercion and no
    /// validation of the value's CONTENT, and performs no reference resolution — this
    /// record deliberately describes only what it itself does.
    /// </para>
    /// <para>
    /// An init-only property for the same binary-compatibility reason as
    /// <see cref="Image"/> — see its own remarks: this record lives in a packable
    /// assembly (<c>Vouchfx.Engine.Authoring</c> is <c>IsPackable=true</c>), and inserting
    /// a new positional parameter would change the primary constructor's parameter
    /// order/arity and the compiler-generated <c>Deconstruct</c> — a binary-breaking
    /// change for any already-compiled caller. An init-only property is purely additive.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>
    /// Withholds <see cref="Security"/> from <c>ToString()</c> (#408), printing every other
    /// member exactly as the compiler-generated <c>PrintMembers</c> did.
    /// </summary>
    /// <remarks>
    /// The sibling of <see cref="ServiceSpec"/>'s own override, for the same measured defect and
    /// the same reasons — see it, and <see cref="RecordSecurityPrinting"/>, for the argument.
    /// Every member of this record is listed below, an obligation pinned by
    /// <c>SecuritySpecDisclosureTests.DependencySpec_HasExactlyTheMembersPrintMembersEnumerates</c>
    /// rather than by review.
    /// </remarks>
    private bool PrintMembers(StringBuilder builder) =>
        RecordSecurityPrinting.Print(
            builder,
            (nameof(Type), Type),
            (nameof(Version), Version),
            (nameof(Extra), Extra),
            (nameof(Image), Image),
            (nameof(Security), Security),
            (nameof(Env), Env));
}
