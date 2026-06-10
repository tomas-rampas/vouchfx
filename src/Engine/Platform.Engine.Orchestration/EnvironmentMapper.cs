// Platform.Engine.Orchestration — EnvironmentMapper (S03-A-02).
//
// Maps a parsed EnvironmentSpec (from Platform.Engine.Authoring) to an Aspire resource graph,
// encapsulating the §4 hard invariants:
//   • String overloads only: AddContainer(name, image) / AddProject(name, csprojPath).
//     The generic AddProject<T>() is forbidden (compile-time coupling breaks the YAML-first premise).
//   • WaitFor the most-specific dependency resource (the database, not the server) — §4 invariant.
//     Aspire's Postgres server resource ("pg") returns healthy before the DCP lifecycle script
//     finishes creating the database ("pgdb"), causing intermittent failures on fast hardware.
//   • Retained IResourceBuilder<T> pattern for endpoint / connection-string discovery after start.
//     Never use app.GetEndpoint(name, scheme) or app.GetConnectionString(name) — both are absent
//     from DistributedApplication in Aspire 13.4.2.
//   • DisableDashboard = true path is enforced by HeadlessTopology; EnvironmentMapper is topology-agnostic.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Platform.Engine.Authoring.Model;
using YamlDotNet.RepresentationModel;

namespace Platform.Engine.Orchestration;

/// <summary>
/// The result of mapping an <see cref="EnvironmentSpec"/> to Aspire resources.
/// </summary>
/// <remarks>
/// <para>
/// All three members are produced together because they share captured closures over the
/// retained <see cref="IResourceBuilder{T}"/> instances allocated during
/// <see cref="Configure"/>.
/// </para>
/// <para>
/// Consumers (the <c>SuiteTopology</c> fixture) pass <see cref="Configure"/> to
/// <see cref="HeadlessTopology.StartAsync"/>, then await each name in
/// <see cref="HealthGateResourceNames"/> via
/// <c>ResourceNotifications.WaitForResourceHealthyAsync</c>, and finally call
/// <see cref="ResolveServices"/> to obtain a flat map of endpoint URLs and connection strings.
/// </para>
/// </remarks>
/// <param name="Configure">
/// Callback that adds all resources (services and managed dependencies) to the supplied
/// <see cref="IDistributedApplicationBuilder"/>.  Must be called exactly once, before
/// <see cref="Aspire.Hosting.DistributedApplication.StartAsync"/>.
/// </param>
/// <param name="ResolveServices">
/// Resolves endpoint URLs (for image/project services) and connection strings (for managed
/// dependencies) from the running <see cref="DistributedApplication"/>, after StartAsync
/// completes.  Returns a flat dictionary keyed by logical resource name.
/// </param>
/// <param name="HealthGateResourceNames">
/// Ordered list of resource names the fixture must await, most-specific first — database
/// resources (e.g. <c>"pgdb"</c>) appear before their server resources (e.g. <c>"pg"</c>)
/// and before plain service containers.
/// This ordering is the §4 invariant: health-gate the database resource, not the server.
/// </param>
/// <param name="DependencyNames">
/// The logical names of managed dependencies (e.g. postgres, kafka) as declared in the
/// <c>environment.dependencies</c> section.  Used by the runner's staging step to
/// distinguish dependency connection-string entries (keyed <c>conn::&lt;name&gt;</c>)
/// from service endpoint entries (keyed <c>svc::&lt;name&gt;</c>) within
/// <c>ScriptGlobalVariables.Vars</c>.
/// </param>
public sealed record MappedTopology(
    Action<IDistributedApplicationBuilder> Configure,
    Func<DistributedApplication, CancellationToken, Task<IReadOnlyDictionary<string, object>>> ResolveServices,
    IReadOnlyList<string> HealthGateResourceNames,
    IReadOnlyList<string> DependencyNames);

/// <summary>
/// Maps the parsed <see cref="EnvironmentSpec"/> block to an Aspire resource graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>Service mapping</b> (each logical name → <see cref="ServiceSpec"/>):
/// <list type="bullet">
///   <item>
///     <see cref="ServiceSpec.Image"/> non-null →
///     <c>AddContainer(name, FullImage).WithHttpEndpoint(targetPort, name:"http").WithHttpHealthCheck("/", "http")</c>.
///     The env-level <see cref="EnvironmentSpec.ImageRegistry"/> is prepended when the image
///     has no explicit registry component (Docker's rule: the first slash-delimited component
///     must contain <c>.</c> or <c>:</c> or equal <c>"localhost"</c>).
///     Per-service <see cref="ServiceSpec.ImagePullPolicy"/> and the env-level
///     <see cref="EnvironmentSpec.ImagePullPolicy"/> are recorded but Aspire 13.4.2 has no
///     direct pull-policy knob; enforcement is deferred to a later sprint (TODO).
///   </item>
///   <item>
///     <see cref="ServiceSpec.Project"/> non-null (and Image null) →
///     <c>AddProject(name, csprojPath)</c> (string overload only — §4 invariant).
///   </item>
///   <item>Both or neither set → <see cref="ArgumentException"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Dependency mapping</b> (each logical name → <see cref="DependencySpec"/>):
/// <list type="bullet">
///   <item>
///     <c>Type == "postgres"</c> →
///     <c>AddPostgres(name)</c> [optionally <c>.WithImageTag(version)</c>]
///     <c>.AddDatabase(name + "db")</c>.
///     Health gate is placed on the <em>database</em> resource (§4 invariant — avoid the
///     server-vs-database race: the server resource returns healthy before the DCP lifecycle
///     script finishes creating the database).
///   </item>
///   <item>
///     <c>Type == "kafka"</c> →
///     <c>AddKafka(name)</c> [optionally <c>.WithImageTag(version)</c>].
///     Health gate is on the Kafka server resource itself (there is no finer-grained resource).
///     When <c>Extra</c> carries <c>schemaRegistry: true</c>, an auxiliary
///     <c>confluentinc/cp-schema-registry</c> container <c>&lt;name&gt;-sr</c> is also added:
///     it reaches the broker over the container network via the broker's
///     <c>InternalEndpoint</c>, <c>WaitFor</c>s the broker, exposes HTTP port 8081, and is
///     health-gated immediately after the broker.  Its host-mapped URL is staged under
///     <c>svc::&lt;name&gt;-sr</c>.
///   </item>
///   <item>Unknown type → <see cref="ArgumentException"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>WaitFor rule (§4)</b>: every service resource calls <c>WaitFor</c> on every dependency's
/// most-specific resource (the database for postgres, the server for kafka) so the service
/// starts only once its dependencies are healthy.
/// </para>
/// </remarks>
public static class EnvironmentMapper
{
    /// <summary>
    /// Maps the supplied <see cref="EnvironmentSpec"/> to a <see cref="MappedTopology"/>.
    /// </summary>
    /// <param name="env">
    /// The environment declaration from the parsed <c>.e2e.yaml</c> file.
    /// May be <see langword="null"/> (treated as an empty environment).
    /// </param>
    /// <returns>
    /// A <see cref="MappedTopology"/> whose <see cref="MappedTopology.Configure"/> callback
    /// is safe to invoke against any <see cref="IDistributedApplicationBuilder"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when a service spec has both <see cref="ServiceSpec.Image"/> and
    /// <see cref="ServiceSpec.Project"/> set, or when a dependency type is unrecognised.
    /// </exception>
    public static MappedTopology Map(EnvironmentSpec? env)
    {
        // ----------------------------------------------------------------
        // Null / empty environment — no resources, no health gates.
        // ----------------------------------------------------------------
        if (env is null ||
            ((env.Services is null || env.Services.Count == 0) &&
             (env.Dependencies is null || env.Dependencies.Count == 0)))
        {
            return new MappedTopology(
                Configure: static _ => { },
                ResolveServices: static (_, _) => Task.FromResult<IReadOnlyDictionary<string, object>>(
                    new Dictionary<string, object>()),
                HealthGateResourceNames: Array.Empty<string>(),
                DependencyNames: Array.Empty<string>());
        }

        // ----------------------------------------------------------------
        // Validate all service specs eagerly so Map() throws before any builder
        // mutations are made.  The schema layer should catch these, but we are
        // defensive (§4 comment in CLAUDE.md).
        // ----------------------------------------------------------------
        foreach (var (name, spec) in env.Services ?? new Dictionary<string, ServiceSpec>())
        {
            if (spec.Image is not null && spec.Project is not null)
            {
                throw new ArgumentException(
                    $"Service '{name}' has both 'image' and 'project' set. " +
                    "Exactly one must be supplied — the schema should prevent this, but the " +
                    "mapper enforces it defensively.",
                    nameof(env));
            }

            if (spec.Image is null && spec.Project is null)
            {
                throw new ArgumentException(
                    $"Service '{name}' has neither 'image' nor 'project' set. " +
                    "Exactly one must be supplied.",
                    nameof(env));
            }
        }

        // Validate dependency types eagerly.
        foreach (var (name, spec) in env.Dependencies ?? new Dictionary<string, DependencySpec>())
        {
            if (!IsSupportedDependencyType(spec.Type))
            {
                throw new ArgumentException(
                    $"Unsupported dependency type '{spec.Type}' for dependency '{name}'. " +
                    $"Supported types: postgres, kafka.",
                    nameof(env));
            }
        }

        // ----------------------------------------------------------------
        // Capture environment-level values used by the Configure closure.
        // ----------------------------------------------------------------
        var imageRegistry = env.ImageRegistry;
        var services = env.Services ?? new Dictionary<string, ServiceSpec>();
        var dependencies = env.Dependencies ?? new Dictionary<string, DependencySpec>();

        // ----------------------------------------------------------------
        // Mutable dictionaries captured by the closures.
        // These are populated during Configure (which runs once, synchronously,
        // before StartAsync) and consumed by ResolveServices (which runs once,
        // asynchronously, after StartAsync).
        // ----------------------------------------------------------------
        // Service name → EndpointReference retained from the container builder's GetEndpoint("http").
        // Null for project-based services (no HTTP endpoint managed by this mapper).
        var serviceEndpoints = new Dictionary<string, EndpointReference>(
            StringComparer.Ordinal);

        // Dependency name → IResourceBuilder whose Resource implements IResourceWithConnectionString.
        // Postgres: database builder.  Kafka: server builder.
        var dependencyBuilders = new Dictionary<string, IResourceBuilder<IResource>>(
            StringComparer.Ordinal);

        // ----------------------------------------------------------------
        // Build the ordered health-gate name list.
        // §4 invariant: most-specific first — databases before servers before services.
        // ----------------------------------------------------------------
        var healthGateNames = new List<string>();

        // Dependency-level gates (databases before servers).
        foreach (var (name, spec) in dependencies)
        {
            switch (spec.Type)
            {
                case "postgres":
                    // The database resource is the most-specific resource for a postgres dependency.
                    healthGateNames.Add(name + "db");
                    break;
                case "kafka":
                    // Broker first…
                    healthGateNames.Add(name);
                    // …then, when requested, the auxiliary schema-registry container,
                    // which depends on (and starts after) the broker.
                    if (KafkaWantsSchemaRegistry(spec.Extra))
                    {
                        healthGateNames.Add(name + "-sr");
                    }

                    break;
            }
        }

        // Service-level gates (after all dependency gates).
        foreach (var (name, _) in services)
        {
            healthGateNames.Add(name);
        }

        // ----------------------------------------------------------------
        // Configure callback: builds the resource graph.
        // ----------------------------------------------------------------
        Action<IDistributedApplicationBuilder> configure = builder =>
        {
            // --- 1. Build dependency resources and collect most-specific builders ---
            // These are built first so services can WaitFor them.
            var mostSpecificDependencyResources = new List<IResourceBuilder<IResource>>();

            foreach (var (name, spec) in dependencies)
            {
                switch (spec.Type)
                {
                    case "postgres":
                        {
                            // AddPostgres(name) + optionally WithImageTag(version) + AddDatabase(name+"db").
                            // §4 invariant: retain the DATABASE builder, not the server builder,
                            // for both WaitFor and connection-string discovery.
                            var serverBuilder = builder.AddPostgres(name);
                            if (!string.IsNullOrEmpty(spec.Version))
                            {
                                serverBuilder = serverBuilder.WithImageTag(spec.Version);
                            }

                            var dbBuilder = serverBuilder.AddDatabase(name + "db");

                            // Capture the database builder for connection-string discovery.
                            dependencyBuilders[name] =
                                (IResourceBuilder<IResource>)(object)dbBuilder;

                            // The most-specific resource to WaitFor is the database.
                            mostSpecificDependencyResources.Add(
                                (IResourceBuilder<IResource>)(object)dbBuilder);
                            break;
                        }

                    case "kafka":
                        {
                            // AddKafka(name) + optionally WithImageTag(version).
                            var kafkaBuilder = builder.AddKafka(name);
                            if (!string.IsNullOrEmpty(spec.Version))
                            {
                                kafkaBuilder = kafkaBuilder.WithImageTag(spec.Version);
                            }

                            // Capture the kafka builder for connection-string discovery.
                            dependencyBuilders[name] =
                                (IResourceBuilder<IResource>)(object)kafkaBuilder;

                            // Kafka has no finer-grained resource; gate on the server itself.
                            // The broker stays the most-specific resource that existing
                            // publish/expect steps WaitFor — the registry below is an
                            // auxiliary resource and is intentionally NOT added here.
                            mostSpecificDependencyResources.Add(
                                (IResourceBuilder<IResource>)(object)kafkaBuilder);

                            // ----------------------------------------------------------------
                            // Optional Confluent Schema Registry (Sprint 6).
                            // When the dependency declares `schemaRegistry: true`, hand-roll a
                            // cp-schema-registry container (Aspire.Hosting.Kafka 13.4.2 has no
                            // built-in schema-registry resource — verified against the pinned
                            // package's public surface).
                            // ----------------------------------------------------------------
                            if (KafkaWantsSchemaRegistry(spec.Extra))
                            {
                                var srName = name + "-sr";

                                // The registry reaches the broker over the CONTAINER network.
                                //
                                // Broker-internal-address mechanism (decision, Sprint 6):
                                // Aspire.Hosting.Kafka 13.4.2 *does* expose the broker's
                                // container-network endpoint as a typed reference —
                                // `KafkaServerResource.InternalEndpoint` is the `EndpointReference`
                                // for the endpoint named "internal" on the container network
                                // (target port 9093); the external, host-mapped endpoint is named
                                // "tcp" (port 9092). We therefore PREFER a
                                // `ReferenceExpression` over that endpoint rather than hardcoding
                                // "<name>:9092": DCP resolves the host/port in the registry
                                // container's network context, so this is robust to any future
                                // change in how the broker is named/addressed on the network.
                                //
                                // Built as: PLAINTEXT://{internal.Host}:{internal.Port}
                                // (EndpointReference.Property(EndpointProperty.Host|Port) yields an
                                // EndpointReferenceExpression, which the ReferenceExpression
                                // interpolation handler accepts via IValueProvider).
                                var internalEndpoint = kafkaBuilder.Resource.InternalEndpoint;
                                var bootstrapServers = ReferenceExpression.Create(
                                    $"PLAINTEXT://{internalEndpoint.Property(EndpointProperty.Host)}:{internalEndpoint.Property(EndpointProperty.Port)}");

                                var srContainerBuilder = builder
                                    .AddContainer(srName, "confluentinc/cp-schema-registry", "7.6.1")
                                    // HOST_NAME is the registry's *advertised* (routable) hostname,
                                    // NOT a bind address: it must be a name resolvable by clients and
                                    // peer instances on the container network. The container's network
                                    // hostname IS its resource name, so advertise `srName`. (A bind-all
                                    // 0.0.0.0 here would advertise an unusable URL — that belongs only
                                    // in LISTENERS below.)
                                    .WithEnvironment("SCHEMA_REGISTRY_HOST_NAME", srName)
                                    .WithEnvironment(
                                        "SCHEMA_REGISTRY_KAFKASTORE_BOOTSTRAP_SERVERS",
                                        bootstrapServers)
                                    // LISTENERS is the bind address — 0.0.0.0 (all interfaces) is correct.
                                    .WithEnvironment("SCHEMA_REGISTRY_LISTENERS", "http://0.0.0.0:8081")
                                    .WithHttpEndpoint(targetPort: 8081, name: "http")
                                    // /subjects → 200 once the registry is serving (default 200 gate).
                                    .WithHttpHealthCheck(path: "/subjects", endpointName: "http")
                                    // Auxiliary resource: it must start AFTER the broker.
                                    .WaitFor(kafkaBuilder);

                                // Retain the HTTP endpoint so ResolveServices stages its
                                // host-mapped URL. The key is the bare logical name "<name>-sr";
                                // the runner stages service endpoints under VarKeys.Service (svc::),
                                // and "<name>-sr" is NOT a DependencyName, so the registry lands in
                                // Vars as `svc::<name>-sr` automatically (verified against
                                // ScenarioRunner's staging loop, which keys non-dependency
                                // DiscoveredServices entries via VarKeys.Service).
                                serviceEndpoints[srName] = srContainerBuilder.GetEndpoint("http");
                            }

                            break;
                        }
                }
            }

            // --- 2. Build service resources and wire WaitFor to dependency most-specific resources ---
            foreach (var (name, spec) in services)
            {
                if (spec.Image is not null)
                {
                    // Resolve full image reference: apply ImageRegistry when the image has no
                    // explicit registry component (Docker's rule: first slash-delimited
                    // component contains '.' or ':' or equals "localhost").
                    var fullImage = ResolveImage(spec.Image, imageRegistry);
                    var port = spec.HttpPort ?? 80;

                    // TODO (later sprint): honour spec.ImagePullPolicy and env.ImagePullPolicy
                    // once Aspire exposes a direct pull-policy knob.  Record intent here for now.
                    // The current Aspire 13.4.2 API does not expose a WithPullPolicy() method.

                    var containerBuilder = builder.AddContainer(name, fullImage)
                        .WithHttpEndpoint(targetPort: port, name: "http")
                        // Register a real HTTP health check so Aspire polls for an actual 200
                        // response (not merely port-mapped / Running state), closing the
                        // HTTP-serving race on loaded CI agents (§4 invariant, StubTopology).
                        .WithHttpHealthCheck(path: "/", endpointName: "http");

                    // §4 invariant: WaitFor the most-specific dependency resource (database, not server).
                    foreach (var depBuilder in mostSpecificDependencyResources)
                    {
                        containerBuilder = containerBuilder.WaitFor(depBuilder);
                    }

                    // Retain the endpoint reference for post-start URL discovery.
                    serviceEndpoints[name] = containerBuilder.GetEndpoint("http");
                }
                else if (spec.Project is not null)
                {
                    // String overload only — §4 invariant (generic AddProject<T>() is forbidden).
                    var projectBuilder = builder.AddProject(name, spec.Project);

                    // §4 invariant: WaitFor the most-specific dependency resource.
                    foreach (var depBuilder in mostSpecificDependencyResources)
                    {
                        projectBuilder = projectBuilder.WaitFor(depBuilder);
                    }

                    // Project services do not expose an HTTP endpoint through this mapper;
                    // their endpoint is discovered via WaitFor / environment injection.
                }
            }
        };

        // ----------------------------------------------------------------
        // ResolveServices callback: reads retained builders after StartAsync.
        // ----------------------------------------------------------------
        Func<DistributedApplication, CancellationToken, Task<IReadOnlyDictionary<string, object>>>
            resolveServices = async (_, ct) =>
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);

                // Service endpoints: read EndpointReference.Url (allocated by DCP after start).
                foreach (var (name, endpointRef) in serviceEndpoints)
                {
                    result[name] = endpointRef.Url;
                }

                // Dependency connection strings: call IResourceWithConnectionString.GetConnectionStringAsync
                // on the retained database/kafka resource builder's Resource.
                // §4 invariant: never use app.GetConnectionString(name) — it does not exist on
                // DistributedApplication in Aspire 13.4.2 (spike S01-A-03 finding).
                foreach (var (name, depBuilder) in dependencyBuilders)
                {
                    if (depBuilder.Resource is IResourceWithConnectionString cs)
                    {
                        var connStr = await cs.GetConnectionStringAsync(ct).ConfigureAwait(false);
                        if (connStr is not null)
                        {
                            result[name] = connStr;
                        }
                    }
                }

                return result;
            };

        return new MappedTopology(
            Configure: configure,
            ResolveServices: resolveServices,
            HealthGateResourceNames: healthGateNames,
            DependencyNames: dependencies.Keys.ToList());
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="type"/> is a supported
    /// managed-dependency type.
    /// </summary>
    private static bool IsSupportedDependencyType(string type) =>
        string.Equals(type, "postgres", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "kafka", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when a kafka dependency's <see cref="DependencySpec.Extra"/>
    /// mapping carries a scalar <c>schemaRegistry</c> whose value is <c>true</c>
    /// (case-insensitive), requesting an auxiliary Confluent Schema Registry container.
    /// </summary>
    /// <param name="extra">
    /// The raw YAML mapping node from <see cref="DependencySpec.Extra"/>; may be
    /// <see langword="null"/> (no extra fields → no registry).
    /// </param>
    /// <remarks>
    /// Only a scalar value equal to <c>true</c> opts in; a missing key, a non-scalar value,
    /// or any other scalar (including <c>false</c>) returns <see langword="false"/>.
    /// </remarks>
    private static bool KafkaWantsSchemaRegistry(YamlMappingNode? extra)
    {
        if (extra is null)
        {
            return false;
        }

        if (!extra.Children.TryGetValue(new YamlScalarNode("schemaRegistry"), out var node))
        {
            return false;
        }

        return node is YamlScalarNode { Value: { } value } &&
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the fully-qualified image reference by applying <paramref name="registry"/>
    /// as a prefix when the image has no explicit registry component.
    /// </summary>
    /// <param name="image">
    /// The image reference from the service spec (e.g. <c>"traefik/whoami"</c>,
    /// <c>"registry.example.com/app:1.0"</c>).
    /// </param>
    /// <param name="registry">
    /// The env-level default registry to prepend (e.g. <c>"registry.example.com"</c>).
    /// <see langword="null"/> or empty — no prefix is applied.
    /// </param>
    /// <returns>
    /// The image reference with the registry prepended if applicable, otherwise unchanged.
    /// </returns>
    /// <remarks>
    /// Uses Docker's own rule for determining whether an image already has an explicit
    /// registry component: the first slash-delimited component is an explicit registry host
    /// only when it contains a <c>.</c> or a <c>:</c>, or equals <c>"localhost"</c>.
    /// This is the same rule used by <see cref="OrchestrationErrorClassifier.ParseRegistryHost"/>.
    /// </remarks>
    internal static string ResolveImage(string image, string? registry)
    {
        if (string.IsNullOrEmpty(registry))
        {
            return image;
        }

        // Strip any digest suffix to avoid the '@' character confusing the slash-split.
        var withoutDigest = image;
        var atIndex = image.IndexOf('@', StringComparison.Ordinal);
        if (atIndex >= 0)
        {
            withoutDigest = image[..atIndex];
        }

        var slashIndex = withoutDigest.IndexOf('/', StringComparison.Ordinal);

        if (slashIndex < 0)
        {
            // No slash — bare image name like "ubuntu:22.04"; no explicit registry.
            return $"{registry}/{image}";
        }

        var firstComponent = withoutDigest[..slashIndex];

        // Docker's rule: explicit registry when first component contains '.' or ':', or is "localhost".
        var hasRegistry =
            firstComponent.Contains('.', StringComparison.Ordinal) ||
            firstComponent.Contains(':', StringComparison.Ordinal) ||
            string.Equals(firstComponent, "localhost", StringComparison.Ordinal);

        return hasRegistry ? image : $"{registry}/{image}";
    }
}
