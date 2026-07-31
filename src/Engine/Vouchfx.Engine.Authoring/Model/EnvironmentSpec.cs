// Vouchfx.Engine.Authoring — EnvironmentSpec (S03-B-01; Seed bound in S05-A-01).
//
// Strongly-typed records for the optional `environment` top-level section of a
// .e2e.yaml file (docs/02 §3.2).

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
/// <c>env:</c> block.
/// </param>
public sealed record ServiceSpec(
    string? Image,
    string? Project,
    string? ImagePullPolicy,
    int? HttpPort,
    IReadOnlyDictionary<string, string>? Env);

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
}
