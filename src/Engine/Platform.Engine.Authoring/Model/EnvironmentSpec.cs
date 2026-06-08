// Platform.Engine.Authoring — EnvironmentSpec (S03-B-01).
//
// Strongly-typed records for the optional `environment` top-level section of a
// .e2e.yaml file (docs/02 §3.2).

using YamlDotNet.RepresentationModel;

namespace Platform.Engine.Authoring.Model;

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
/// first step runs.  Kept as a raw <see cref="YamlMappingNode"/> because its
/// structure is heterogeneous across dependency types; a later task will bind it.
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
    YamlMappingNode? Seed,
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
public sealed record ServiceSpec(
    string? Image,
    string? Project,
    string? ImagePullPolicy,
    int? HttpPort);

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
/// Optional version string for the dependency's container image tag.
/// </param>
/// <param name="Extra">
/// Raw YAML mapping node retaining any additional provider-specific fields.
/// <see langword="null"/> when no extra fields are present.
/// </param>
public sealed record DependencySpec(
    string Type,
    string? Version,
    YamlMappingNode? Extra);
