// Platform.Engine.Orchestration — EnvironmentMapper (S03-A-02, expanded Phase 0 batch).
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
//
// Provider registration table (s_dependencyRegistry):
//   Eleven dependency types are supported.  Each entry supplies:
//   • Build — called inside the configure delegate; mutates the Aspire builder, populates
//     serviceEndpoints for sidecar containers (kafka schema-registry, mailpit SMTP), populates
//     depConnBuilders for dependencies that need custom connection-string construction
//     (azureservicebus — whose emulator is a plain container, not an Aspire typed resource,
//     so it does not implement IResourceWithConnectionString), and returns
//     (Retained, MostSpecific) IResourceBuilder<IResource> pairs:
//     - Retained    → stored in dependencyBuilders[name]; used for connection-string resolution.
//     - MostSpecific → added to mostSpecificDependencyResources; services WaitFor these.
//     For database-backed types (postgres/sqlserver/mysql/mongodb) both are the *database* resource.
//     For server-only types (redis/elasticsearch/rabbitmq/nats/kafka/azureservicebus) both are the server resource.
//   • HealthGateNames — produces the ordered gate-name sequence for the topology fixture to await.
//   Adding a new dependency type = add one entry; Map() is unchanged.

using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Platform.Engine.Abstractions.Secrets;
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
/// Eleven types are supported via the internal registration table.  Database-backed types
/// (postgres, sqlserver, mysql, mongodb) gate on the <em>database</em> resource; server-only
/// types (redis, elasticsearch, rabbitmq, nats, kafka, azureservicebus) gate on the server itself.
/// </para>
/// <para>
/// <b>WaitFor rule (§4)</b>: every service resource calls <c>WaitFor</c> on every dependency's
/// most-specific resource (the database for database-backed types, the server for server-only types)
/// so the service starts only once its dependencies are healthy.
/// </para>
/// </remarks>
public static class EnvironmentMapper
{
    // -----------------------------------------------------------------------
    // Registration table — one entry per supported dependency type.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Per-type registration: how to build the Aspire resource graph entry and which
    /// resource names to await in the health gate.
    /// </summary>
    /// <remarks>
    /// The Build delegate's fifth parameter, <c>depConnBuilders</c>, is a mutable dictionary
    /// for dependencies whose connection string cannot be resolved via
    /// <see cref="IResourceWithConnectionString.GetConnectionStringAsync"/> — e.g., plain
    /// container-based dependencies like <c>azureservicebus</c>.  The Build lambda captures
    /// an <see cref="EndpointReference"/> and stores a factory lambda that constructs the
    /// connection string from the resolved host/port after <c>StartAsync</c> completes.
    /// Existing types that do not need this mechanism receive <c>_</c> for the parameter.
    /// </remarks>
    private sealed record DependencyRegistration(
        Func<IDistributedApplicationBuilder, string, DependencySpec,
             Dictionary<string, EndpointReference>,
             Dictionary<string, Func<CancellationToken, Task<string?>>>,
             (IResourceBuilder<IResource> Retained, IResourceBuilder<IResource> MostSpecific)> Build,
        Func<string, DependencySpec, IEnumerable<string>> HealthGateNames);

    private static readonly Dictionary<string, DependencyRegistration> s_dependencyRegistry =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ---- database-backed: gate on the database, not the server ----
            // §4 invariant: the server resource returns healthy before the DCP lifecycle
            // script finishes creating the database, causing intermittent failures on fast
            // hardware.  Retain the DATABASE builder for connection-string discovery too.

            ["postgres"] = new DependencyRegistration(
                Build: (builder, name, spec, _, _) =>
                {
                    var serverBuilder = builder.AddPostgres(name);
                    if (!string.IsNullOrEmpty(spec.Version))
                        serverBuilder = serverBuilder.WithImageTag(spec.Version);
                    var dbBuilder = serverBuilder.AddDatabase(name + "db");
                    var retainedDb = (IResourceBuilder<IResource>)(object)dbBuilder;
                    return (retainedDb, retainedDb);
                },
                HealthGateNames: (name, _) => new[] { name + "db" }),

            ["sqlserver"] = new DependencyRegistration(
                Build: (builder, name, spec, _, _) =>
                {
                    var serverBuilder = builder.AddSqlServer(name);
                    if (!string.IsNullOrEmpty(spec.Version))
                        serverBuilder = serverBuilder.WithImageTag(spec.Version);
                    var dbBuilder = serverBuilder.AddDatabase(name + "db");
                    var retainedDb = (IResourceBuilder<IResource>)(object)dbBuilder;
                    return (retainedDb, retainedDb);
                },
                HealthGateNames: (name, _) => new[] { name + "db" }),

            ["mysql"] = new DependencyRegistration(
                Build: (builder, name, spec, _, _) =>
                {
                    var serverBuilder = builder.AddMySql(name);
                    if (!string.IsNullOrEmpty(spec.Version))
                        serverBuilder = serverBuilder.WithImageTag(spec.Version);
                    var dbBuilder = serverBuilder.AddDatabase(name + "db");
                    var retainedDb = (IResourceBuilder<IResource>)(object)dbBuilder;
                    return (retainedDb, retainedDb);
                },
                HealthGateNames: (name, _) => new[] { name + "db" }),

            ["mongodb"] = new DependencyRegistration(
                Build: (builder, name, spec, _, _) =>
                {
                    var serverBuilder = builder.AddMongoDB(name);
                    if (!string.IsNullOrEmpty(spec.Version))
                        serverBuilder = serverBuilder.WithImageTag(spec.Version);
                    var dbBuilder = serverBuilder.AddDatabase(name + "db");
                    var retainedDb = (IResourceBuilder<IResource>)(object)dbBuilder;
                    return (retainedDb, retainedDb);
                },
                HealthGateNames: (name, _) => new[] { name + "db" }),

            // ---- server-only: gate on the server itself ----

            ["redis"] = new DependencyRegistration(
                Build: (builder, name, spec, _, _) =>
                {
                    var serverBuilder = builder.AddRedis(name);
                    if (!string.IsNullOrEmpty(spec.Version))
                        serverBuilder = serverBuilder.WithImageTag(spec.Version);
                    var retained = (IResourceBuilder<IResource>)(object)serverBuilder;
                    return (retained, retained);
                },
                HealthGateNames: (name, _) => new[] { name }),

            ["elasticsearch"] = new DependencyRegistration(
                Build: (builder, name, spec, _, _) =>
                {
                    var serverBuilder = builder.AddElasticsearch(name);
                    if (!string.IsNullOrEmpty(spec.Version))
                        serverBuilder = serverBuilder.WithImageTag(spec.Version);
                    // Stability environment variables: single-node discovery, security
                    // disabled (avoids TLS/credential setup in test environments), and
                    // bounded JVM heap to prevent OOM on CI runners.
                    serverBuilder = serverBuilder
                        .WithEnvironment("discovery.type", "single-node")
                        .WithEnvironment("xpack.security.enabled", "false")
                        .WithEnvironment("ES_JAVA_OPTS", "-Xms512m -Xmx512m");
                    var retained = (IResourceBuilder<IResource>)(object)serverBuilder;
                    return (retained, retained);
                },
                HealthGateNames: (name, _) => new[] { name }),

            ["rabbitmq"] = new DependencyRegistration(
                Build: (builder, name, spec, _, _) =>
                {
                    var serverBuilder = builder.AddRabbitMQ(name);
                    if (!string.IsNullOrEmpty(spec.Version))
                        serverBuilder = serverBuilder.WithImageTag(spec.Version);
                    var retained = (IResourceBuilder<IResource>)(object)serverBuilder;
                    return (retained, retained);
                },
                HealthGateNames: (name, _) => new[] { name }),

            ["nats"] = new DependencyRegistration(
                Build: (builder, name, spec, _, _) =>
                {
                    // WithJetStream() appends the '-js' flag so the NATS container starts with
                    // JetStream enabled.  Without it, CreateStreamAsync / PublishAsync throw
                    // NatsJSApiException and every mq-publish.nats / mq-expect.nats step
                    // returns EnvironmentError — FIX B1.
                    var serverBuilder = builder.AddNats(name).WithJetStream();
                    if (!string.IsNullOrEmpty(spec.Version))
                        serverBuilder = serverBuilder.WithImageTag(spec.Version);
                    var retained = (IResourceBuilder<IResource>)(object)serverBuilder;
                    return (retained, retained);
                },
                HealthGateNames: (name, _) => new[] { name }),

            // ---- kafka: server + optional schema-registry sidecar ----
            // The SR sidecar is provisioned as a side effect of Build when
            // spec.Extra carries schemaRegistry: true.  Its HTTP endpoint goes into
            // serviceEndpoints; it does NOT appear in dependencyBuilders.
            // Gate ordering: broker first, then SR (SR depends on the broker).

            ["kafka"] = new DependencyRegistration(
                Build: (builder, name, spec, serviceEndpoints, _) =>
                {
                    var kafkaBuilder = builder.AddKafka(name);
                    if (!string.IsNullOrEmpty(spec.Version))
                        kafkaBuilder = kafkaBuilder.WithImageTag(spec.Version);

                    if (KafkaWantsSchemaRegistry(spec.Extra))
                    {
                        var srName = name + "-sr";
                        var internalEndpoint = kafkaBuilder.Resource.InternalEndpoint;
                        var bootstrapServers = ReferenceExpression.Create(
                            $"PLAINTEXT://{internalEndpoint.Property(EndpointProperty.Host)}:{internalEndpoint.Property(EndpointProperty.Port)}");
                        var srContainerBuilder = builder
                            .AddContainer(srName, "confluentinc/cp-schema-registry", "7.6.1")
                            .WithEnvironment("SCHEMA_REGISTRY_HOST_NAME", srName)
                            .WithEnvironment(
                                "SCHEMA_REGISTRY_KAFKASTORE_BOOTSTRAP_SERVERS",
                                bootstrapServers)
                            .WithEnvironment("SCHEMA_REGISTRY_LISTENERS", "http://0.0.0.0:8081")
                            .WithHttpEndpoint(targetPort: 8081, name: "http")
                            .WithHttpHealthCheck(path: "/subjects", endpointName: "http")
                            .WaitFor(kafkaBuilder);
                        serviceEndpoints[srName] = srContainerBuilder.GetEndpoint("http");
                    }

                    var retained = (IResourceBuilder<IResource>)(object)kafkaBuilder;
                    return (retained, retained);
                },
                HealthGateNames: (name, spec) =>
                {
                    var gates = new List<string> { name };
                    if (KafkaWantsSchemaRegistry(spec.Extra))
                        gates.Add(name + "-sr");
                    return gates;
                }),

            // ---- mailpit: SMTP capture container with HTTP API + SMTP port ----
            // Exposes:
            //   • HTTP port 8025 (REST API + UI) — staged via serviceEndpoints[name]
            //     → conn::<name> (dependency key; VarKeys.Connection).
            //   • SMTP port 1025 — staged via serviceEndpoints[name+"-smtp"]
            //     → svc::<name>-smtp (not in DependencyNames).
            // Health gate: container's /api/v1/info endpoint via HTTP health check.

            ["mailpit"] = new DependencyRegistration(
                Build: (builder, name, spec, serviceEndpoints, _) =>
                {
                    // Pin a stable tag for determinism (§4): never float on 'latest'.
                    // Authors may still override via the dependency's 'version' field.
                    var tag = string.IsNullOrEmpty(spec.Version) ? "v1.21" : spec.Version;
                    var containerBuilder = builder
                        .AddContainer(name, "axllent/mailpit", tag)
                        .WithHttpEndpoint(targetPort: 8025, name: "http")
                        .WithEndpoint(targetPort: 1025, name: "smtp")
                        .WithHttpHealthCheck(path: "/api/v1/info", endpointName: "http");

                    // Stage HTTP API URL as conn::<name> (picked up by mail-expect.smtp
                    // provider via VarKeys.Connection(model.Target)).
                    serviceEndpoints[name] = containerBuilder.GetEndpoint("http");
                    // Stage SMTP URL as svc::<name>-smtp for docker tests and SUT config.
                    serviceEndpoints[name + "-smtp"] = containerBuilder.GetEndpoint("smtp");

                    var retained = (IResourceBuilder<IResource>)(object)containerBuilder;
                    return (retained, retained);
                },
                HealthGateNames: (name, _) => new[] { name }),

            // ---- azureservicebus: emulator two-container topology ----
            // The Azure Service Bus emulator requires a SQL Server sidecar for persistence.
            // It does NOT implement IResourceWithConnectionString (it is a plain container),
            // so the connection string is constructed from the resolved AMQP host/port via
            // depConnBuilders — the custom connection-string builder mechanism.
            //
            // Container topology:
            //   <name>-sqledge  — mcr.microsoft.com/mssql/server:2022-latest (SQL persistence)
            //   <name>           — mcr.microsoft.com/azure-messaging/servicebus-emulator:1.1.2
            //     • AMQP port 5672   → resolved as conn::<name>
            //     • HTTP port 5300   → health check at /health
            //     • Config.json bind-mounted from a generated temp file
            //
            // Entity declarations: queues and topics are read from spec.Extra
            // (extra.queues: [...], extra.topics: [{name, subscriptions: [...]}]) and
            // written into Config.json.  Entities not declared here will not be available;
            // a missing entity surfaces as ServiceBusException → EnvironmentError (§12.1).
            //
            // Health gate: <name> only (the emulator's /health check requires SQL to be
            // ready, so the sidecar's "running" state is subsumed by the emulator gate).

            ["azureservicebus"] = new DependencyRegistration(
                Build: (builder, name, spec, _, depConnBuilders) =>
                {
                    var sidecarName = name + "-sqledge";

                    // SQL Server sidecar — required by the ASB emulator for persistence.
                    // MSSQL_SA_PASSWORD meets SQL Server complexity requirements.
                    // The "2022-latest" tag is intentionally floating: SQL Server 2022 minor/CU
                    // updates are backwards-compatible and this topology validates the ASB layer,
                    // not SQL internals — pinning would add upgrade churn with no safety benefit.
                    var sidecarBuilder = builder
                        .AddContainer(sidecarName, "mcr.microsoft.com/mssql/server", "2022-latest")
                        .WithEnvironment("ACCEPT_EULA", "Y")
                        .WithEnvironment("MSSQL_SA_PASSWORD", "Str0ng!P@ssword#1");

                    // Generate a Config.json that declares the ASB emulator namespace.
                    // Queues and topics are read from spec.Extra; if absent, an empty
                    // namespace is declared.  Entities not declared here will not be
                    // available; a missing entity surfaces as ServiceBusException → EnvironmentError.
                    var queues = ParseAsbQueues(spec.Extra);
                    var topics = ParseAsbTopics(spec.Extra);
                    var configJson = GenerateAsbConfigJson(queues, topics);
                    // Write to a vouchfx-prefixed temp subdirectory so the file is
                    // identifiable for manual cleanup.  The OS reclaims temp files on
                    // reboot; the emulator reads this path at container-start time and
                    // the normal teardown path (§4.5) removes the container before the
                    // engine exits, so the file is only retained on abnormal DCP exits.
                    var asbTempDir = Path.Combine(
                        Path.GetTempPath(), $"vouchfx-asb-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(asbTempDir);
                    var configPath = Path.Combine(asbTempDir, "Config.json");
                    File.WriteAllText(configPath, configJson);

                    var emulatorBuilder = builder
                        .AddContainer(name, "mcr.microsoft.com/azure-messaging/servicebus-emulator", "1.1.2")
                        .WithEnvironment("ACCEPT_EULA", "Y")
                        .WithEnvironment("MSSQL_SA_PASSWORD", "Str0ng!P@ssword#1")
                        .WithEnvironment("SQL_SERVER", sidecarName)
                        .WithBindMount(configPath, "/ServiceBus_Emulator/ConfigFiles/Config.json", isReadOnly: true)
                        .WithEndpoint(targetPort: 5672, name: "amqp")
                        .WithHttpEndpoint(targetPort: 5300, name: "health")
                        .WithHttpHealthCheck(path: "/health", endpointName: "health")
                        .WaitFor(sidecarBuilder);

                    if (!string.IsNullOrEmpty(spec.Version))
                        emulatorBuilder = emulatorBuilder.WithImageTag(spec.Version);

                    // Capture the AMQP endpoint reference; resolve after StartAsync.
                    // IResourceBuilder<T> is covariant (out T) in Aspire 13.x, so
                    // sidecarBuilder (IResourceBuilder<ContainerResource>) is accepted
                    // as IResourceBuilder<IResource> by WaitFor above.
                    var asbEndpoint = emulatorBuilder.GetEndpoint("amqp");

                    // Store a custom connection-string builder so ResolveServices can
                    // construct result[name] without IResourceWithConnectionString.
                    // The URL is parsed after StartAsync when the host port is known.
                    depConnBuilders[name] = _ =>
                    {
                        var url = asbEndpoint.Url;
                        var uri = new Uri(url);
                        var connStr =
                            $"Endpoint=sb://{uri.Host}:{uri.Port};" +
                            "SharedAccessKeyName=RootManageSharedAccessKey;" +
                            "SharedAccessKey=SAS_KEY_VALUE;" +
                            "UseDevelopmentEmulator=true;";
                        return Task.FromResult<string?>(connStr);
                    };

                    var retained = (IResourceBuilder<IResource>)(object)emulatorBuilder;
                    return (retained, retained);
                },
                HealthGateNames: (name, _) => new[] { name }),
        };

    // -----------------------------------------------------------------------
    // Map — public entry point
    // -----------------------------------------------------------------------

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

        // Validate dependency types eagerly against the registration table.
        foreach (var (name, spec) in env.Dependencies ?? new Dictionary<string, DependencySpec>())
        {
            if (!s_dependencyRegistry.ContainsKey(spec.Type))
            {
                throw new ArgumentException(
                    $"Unsupported dependency type '{spec.Type}' for dependency '{name}'. " +
                    $"Supported types: {string.Join(", ", s_dependencyRegistry.Keys)}.",
                    nameof(env));
            }
        }

        // Validate every service's `env:` mapping eagerly (SUT configuration surface) — every
        // ${conn:name}/${conn:name.part} reference must name a declared dependency and, for the
        // '.part' form, a part supported by that dependency's kind; a ${secret:...} reference is
        // rejected outright (§17 — secrets resolve at step-execution time, never baked into a
        // container's environment).  Validating before any builder mutation keeps Map() eager,
        // consistent with the two loops above.
        foreach (var (serviceName, spec) in env.Services ?? new Dictionary<string, ServiceSpec>())
        {
            if (spec.Env is null)
            {
                continue;
            }

            foreach (var (envKey, envValue) in spec.Env)
            {
                ValidateEnvValue(serviceName, envKey, envValue, env.Dependencies ?? new Dictionary<string, DependencySpec>());
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
        var dependencyBuilders = new Dictionary<string, IResourceBuilder<IResource>>(
            StringComparer.Ordinal);

        // Dependency name → custom connection-string factory.
        // Used for dependencies that are plain containers (not Aspire typed resources) and
        // therefore do not implement IResourceWithConnectionString (e.g., azureservicebus).
        // The factory lambda is stored during Configure and invoked by ResolveServices after
        // StartAsync so the resolved host/port is available.
        var depConnBuilders = new Dictionary<string, Func<CancellationToken, Task<string?>>>(
            StringComparer.Ordinal);

        // ----------------------------------------------------------------
        // Build the ordered health-gate name list.
        // §4 invariant: most-specific first — databases before servers before services.
        // ----------------------------------------------------------------
        var healthGateNames = new List<string>();

        // Dependency-level gates — use the registration table for ordering.
        foreach (var (name, spec) in dependencies)
        {
            var entry = s_dependencyRegistry[spec.Type];
            foreach (var gate in entry.HealthGateNames(name, spec))
                healthGateNames.Add(gate);
        }

        // Service-level gates (after all dependency gates).
        foreach (var (name, _) in services)
            healthGateNames.Add(name);

        // ----------------------------------------------------------------
        // Configure callback: builds the resource graph.
        // ----------------------------------------------------------------
        Action<IDistributedApplicationBuilder> configure = builder =>
        {
            var mostSpecificDependencyResources = new List<IResourceBuilder<IResource>>();

            foreach (var (name, spec) in dependencies)
            {
                var entry = s_dependencyRegistry[spec.Type];
                var (retained, mostSpecific) = entry.Build(
                    builder, name, spec, serviceEndpoints, depConnBuilders);
                dependencyBuilders[name] = retained;
                mostSpecificDependencyResources.Add(mostSpecific);
            }

            // SUT configuration surface: build the per-dependency container-native accessor
            // table ONCE, now that every dependency resource exists, so every service's `env:`
            // mapping can resolve `${conn:name}` / `${conn:name.part}` references below without
            // re-deriving the same server/database resource repeatedly.
            //
            // Resolve ONLY for dependencies actually REFERENCED by some service's env: value —
            // NEVER unconditionally for every declared dependency (BUG FIX: a dependency kind
            // with no env: resolution path, currently only azureservicebus, has no matching
            // case in ResolveDependencyEnvAccess; calling it unconditionally meant merely
            // DECLARING an azureservicebus dependency — with or without any env: block —
            // tripped the method's defensive fallback throw on EVERY run, breaking
            // examples/mq-azureservicebus.e2e.yaml outright). ValidateEnvValue has already
            // rejected, eagerly, any env: value that actually references an azureservicebus
            // dependency, so restricting resolution to referenced dependencies is always safe
            // — and it also avoids wasted resolution work for unreferenced dependencies.
            var referencedDependencyNames = CollectReferencedDependencyNames(services);
            var envAccessByDependency = new Dictionary<string, DependencyEnvAccess>(StringComparer.Ordinal);
            foreach (var (name, spec) in dependencies)
            {
                if (!referencedDependencyNames.Contains(name))
                {
                    continue;
                }

                envAccessByDependency[name] = ResolveDependencyEnvAccess(
                    name, spec.Type, dependencyBuilders[name], serviceEndpoints);
            }

            foreach (var (name, spec) in services)
            {
                if (spec.Image is not null)
                {
                    var fullImage = ResolveImage(spec.Image, imageRegistry);
                    var port = spec.HttpPort ?? 80;
                    var containerBuilder = builder.AddContainer(name, fullImage)
                        .WithHttpEndpoint(targetPort: port, name: "http")
                        .WithHttpHealthCheck(path: "/", endpointName: "http")
                        // SUT configuration surface (point 2): a containerised SUT can reach a
                        // host-run resource (e.g. the webhook listener, which binds 0.0.0.0) via
                        // host.docker.internal on Docker Desktop already; '--add-host' makes the
                        // SAME hostname resolve on plain Linux Docker Engine (CI runners), which
                        // has no built-in host.docker.internal DNS entry.
                        .WithContainerRuntimeArgs("--add-host=host.docker.internal:host-gateway");

                    // §4 invariant: WaitFor the most-specific dependency resource.
                    foreach (var depBuilder in mostSpecificDependencyResources)
                        containerBuilder = containerBuilder.WaitFor(depBuilder);

                    ApplyEnv(containerBuilder, spec.Env, envAccessByDependency);

                    serviceEndpoints[name] = containerBuilder.GetEndpoint("http");
                }
                else if (spec.Project is not null)
                {
                    // String overload only — §4 invariant (generic AddProject<T>() is forbidden).
                    var projectBuilder = builder.AddProject(name, spec.Project);

                    // §4 invariant: WaitFor the most-specific dependency resource.
                    foreach (var depBuilder in mostSpecificDependencyResources)
                        projectBuilder = projectBuilder.WaitFor(depBuilder);

                    ApplyEnv(projectBuilder, spec.Env, envAccessByDependency);
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

                foreach (var (name, endpointRef) in serviceEndpoints)
                    result[name] = endpointRef.Url;

                // §4 invariant: never use app.GetConnectionString(name) — it does not exist on
                // DistributedApplication in Aspire 13.4.2 (spike S01-A-03 finding).
                foreach (var (name, depBuilder) in dependencyBuilders)
                {
                    if (depBuilder.Resource is IResourceWithConnectionString cs)
                    {
                        var connStr = await cs.GetConnectionStringAsync(ct).ConfigureAwait(false);
                        if (connStr is not null)
                            result[name] = connStr;
                    }
                }

                // Custom connection-string builders for dependencies that are plain containers
                // (not Aspire typed resources) and do not implement IResourceWithConnectionString.
                // The factory lambda resolves the host/port from the EndpointReference (available
                // after StartAsync) and constructs the full connection string.
                foreach (var (name, connBuilder) in depConnBuilders)
                {
                    var connStr = await connBuilder(ct).ConfigureAwait(false);
                    if (connStr is not null)
                        result[name] = connStr;
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
    // SUT configuration surface (`env:`) — validation and ReferenceExpression construction.
    //
    // This is DELIBERATELY separate from the ResolveServices/dependencyBuilders machinery
    // above: ResolveServices resolves the HOST-published endpoint/connection string (via
    // GetConnectionStringAsync / EndpointReference.Url) for CSX step assertions running on the
    // HOST in the Default ALC.  A containerised SUT can never reach that host-published
    // localhost:randomport from inside the Aspire-managed Docker network, so `env:` values are
    // built as Aspire ReferenceExpressions and applied via WithEnvironment(name, ReferenceExpression)
    // — Aspire's OWN env-var materialisation resolves these to the container-network host/port
    // when the consuming resource (the service) and the referenced resource (the dependency)
    // share the same builder, exactly as WithReference()/WithEnvironment(EndpointReference)
    // are designed to do.  Every Aspire API used here (ReferenceExpressionBuilder,
    // EndpointReference.Property, WithEnvironment(ReferenceExpression), WithContainerRuntimeArgs,
    // and every resource type's Host/Port/UserNameReference/PasswordParameter/DatabaseName
    // members) was verified by reflection against the pinned Aspire.Hosting* 13.4.2 (13.3.0 for
    // Elasticsearch) packages before use — see the sprint notes for the verification transcript.
    // -----------------------------------------------------------------------

    /// <summary>Matches a <c>${conn:name}</c> or <c>${conn:name.part}</c> reference.</summary>
    /// <remarks>Group 1 is the dependency name; group 2 (optional) is the part accessor.</remarks>
    private static readonly Regex s_connRefPattern = new(
        @"\$\{conn:([A-Za-z0-9_-]+)(?:\.([A-Za-z0-9_-]+))?\}",
        RegexOptions.Compiled);

    /// <summary>The <c>${conn:name.part}</c> accessors supported by database-backed kinds.</summary>
    private static readonly string[] s_dbKindParts = { "host", "port", "username", "password", "database" };

    /// <summary>The <c>${conn:name.part}</c> accessors supported by the <c>rabbitmq</c> kind.</summary>
    private static readonly string[] s_rabbitmqParts = { "host", "port", "username", "password" };

    /// <summary>
    /// The <c>${conn:name.part}</c> accessors supported by the <c>nats</c> kind.  NATS
    /// (<c>AddNats(name).WithJetStream()</c>, the exact call this mapper makes) unconditionally
    /// starts the container with <c>--user &lt;name&gt; --pass &lt;password&gt;</c> — a real,
    /// enforced credential, verified by inspecting the resource's actual container startup args
    /// (not just whether a <c>ParameterResource</c> object exists).
    /// </summary>
    private static readonly string[] s_natsParts = { "host", "port", "username", "password" };

    /// <summary>
    /// The <c>${conn:name.part}</c> accessors supported by the <c>redis</c> kind.  <c>AddRedis</c>
    /// unconditionally starts the container as <c>redis-server --requirepass $REDIS_PASSWORD</c>
    /// — a real, enforced credential; redis has no username concept at all, so only
    /// <c>password</c> (plus host/port) is exposed.
    /// </summary>
    private static readonly string[] s_redisParts = { "host", "port", "password" };

    /// <summary>
    /// The <c>${conn:name.part}</c> accessors supported by single-endpoint kinds that carry no
    /// ENFORCED author-facing credential: kafka has no credential mechanism at all; mailpit is a
    /// plain container with none; elasticsearch provisions a password but this registration
    /// disables security (<c>xpack.security.enabled=false</c>), so it is never enforced.
    /// </summary>
    private static readonly string[] s_hostPortOnlyParts = { "host", "port" };

    /// <summary>
    /// Returns the <c>${conn:name.part}</c> accessor names supported for a dependency of
    /// <paramref name="dependencyType"/> (case-insensitive).  Empty for any type this feature
    /// does not support (currently only <c>azureservicebus</c>, which is rejected earlier and
    /// never reaches this lookup in practice).
    /// </summary>
    private static string[] GetSupportedEnvParts(string dependencyType) =>
        dependencyType.ToLowerInvariant() switch
        {
            "postgres" or "mysql" or "sqlserver" or "mongodb" => s_dbKindParts,
            "rabbitmq" => s_rabbitmqParts,
            "nats" => s_natsParts,
            "redis" => s_redisParts,
            "kafka" or "elasticsearch" or "mailpit" => s_hostPortOnlyParts,
            _ => Array.Empty<string>(),
        };

    /// <summary>
    /// Validates every <c>${conn:...}</c> / <c>${secret:...}</c> reference in a single
    /// service <c>env:</c> value, throwing <see cref="ArgumentException"/> on the first
    /// problem found.  Called eagerly, before any builder mutation (mirrors the
    /// service-shape and dependency-type validation above).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the value contains a <c>${secret:...}</c> reference (§17 — secrets resolve
    /// at step-execution time, never at container-build time), names an unknown dependency,
    /// names an <c>azureservicebus</c> dependency (unsupported by <c>env:</c> in v1), or uses
    /// an unsupported <c>.part</c> accessor for the referenced dependency's kind.
    /// </exception>
    private static void ValidateEnvValue(
        string serviceName,
        string envKey,
        string envValue,
        IReadOnlyDictionary<string, DependencySpec> dependencies)
    {
        // Sigil-PRESENCE check (mirrors SecretReference.ValidateField, §17), not a well-formed-
        // token regex match: env: supports NO secret references at all, not even well-formed
        // ones, so a malformed token such as '${secret:env}' (missing '/path') must be rejected
        // too — it would otherwise silently pass through as opaque literal text instead of
        // surfacing the author's mistake.
        if (envValue.Contains(SecretReference.Sigil, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Service '{serviceName}' env entry '{envKey}' references a ${{secret:...}} value. " +
                "Secrets resolve at step-execution time, never at container-build time (§17): " +
                "baking a secret into a container's environment would expose it via 'docker " +
                "inspect' and corrupt the reproducibility envelope (which hashes the reference, " +
                "never the value). Configure the SUT to resolve the secret itself instead.",
                nameof(envValue));
        }

        foreach (Match m in s_connRefPattern.Matches(envValue))
        {
            var depName = m.Groups[1].Value;
            var part = m.Groups[2].Success ? m.Groups[2].Value : null;

            if (!dependencies.TryGetValue(depName, out var depSpec))
            {
                throw new ArgumentException(
                    $"Service '{serviceName}' env entry '{envKey}' references unknown dependency " +
                    $"'{depName}' via '{m.Value}'. Declared dependencies: " +
                    (dependencies.Count == 0
                        ? "(none)."
                        : string.Join(", ", dependencies.Keys.OrderBy(k => k, StringComparer.Ordinal)) + "."),
                    nameof(envValue));
            }

            if (string.Equals(depSpec.Type, "azureservicebus", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Service '{serviceName}' env entry '{envKey}' references dependency " +
                    $"'{depName}' of type 'azureservicebus', which env: references do not support " +
                    "in v1 (the emulator has no stable container-native connection-string " +
                    "resolution path yet). Wire the SUT's Service Bus connection another way.",
                    nameof(envValue));
            }

            if (part is not null && !GetSupportedEnvParts(depSpec.Type).Contains(part))
            {
                throw new ArgumentException(
                    $"Service '{serviceName}' env entry '{envKey}' references unsupported part " +
                    $"'{part}' of dependency '{depName}' (type '{depSpec.Type}'). Supported parts: " +
                    $"{string.Join(", ", GetSupportedEnvParts(depSpec.Type).OrderBy(p => p, StringComparer.Ordinal))}.",
                    nameof(envValue));
            }
        }
    }

    /// <summary>
    /// Collects the set of dependency names referenced by ANY service's <c>env:</c> value
    /// (via <c>${conn:name}</c> or <c>${conn:name.part}</c>), across every service.
    /// </summary>
    /// <remarks>
    /// Used so <see cref="Map"/>'s <c>configure</c> closure resolves a
    /// <see cref="DependencyEnvAccess"/> ONLY for dependencies actually referenced — never
    /// unconditionally for every declared dependency. A dependency kind with no env:
    /// resolution path (currently only <c>azureservicebus</c>) has no matching case in
    /// <see cref="ResolveDependencyEnvAccess"/>; resolving unconditionally meant merely
    /// DECLARING such a dependency tripped that method's defensive fallback throw on every
    /// run, regardless of whether any service actually referenced it.
    /// </remarks>
    private static HashSet<string> CollectReferencedDependencyNames(
        IReadOnlyDictionary<string, ServiceSpec> services)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, spec) in services)
        {
            if (spec.Env is null)
            {
                continue;
            }

            foreach (var (_, envValue) in spec.Env)
            {
                foreach (Match m in s_connRefPattern.Matches(envValue))
                {
                    names.Add(m.Groups[1].Value);
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Per-dependency container-native accessor table entry consumed by
    /// <see cref="BuildEnvExpression"/>.  Every non-null member is either a <see cref="string"/>
    /// literal or an Aspire value-provider object (implementing both <c>IValueProvider</c> and
    /// <c>IManifestExpressionProvider</c> — <see cref="EndpointReferenceExpression"/>,
    /// <see cref="ReferenceExpression"/>, or <see cref="ParameterResource"/>); a
    /// <see langword="null"/> member means that accessor is unsupported for this dependency's
    /// kind (already rejected by <see cref="ValidateEnvValue"/>, so a null is never actually
    /// dereferenced at build time).
    /// </summary>
    private sealed record DependencyEnvAccess(
        ReferenceExpression FullConnection,
        object? Host,
        object? Port,
        object? Username,
        object? Password,
        object? Database);

    /// <summary>
    /// Builds the <see cref="DependencyEnvAccess"/> accessor table entry for dependency
    /// <paramref name="name"/>, reading whichever Aspire resource type <paramref name="retained"/>
    /// wraps.  See the per-kind semantics table in the sprint notes; summary:
    /// <list type="bullet">
    ///   <item><description>
    ///     postgres/mysql/sqlserver/mongodb — <paramref name="retained"/> IS the DATABASE
    ///     resource (§4 invariant: retain the database, not the server); host/port/username/
    ///     password parts read the SERVER (<c>.Parent</c>) — mysql has no username parameter at
    ///     all (the container always provisions the fixed 'root' superuser), so its Username is
    ///     a plain literal.
    ///   </description></item>
    ///   <item><description>
    ///     kafka — the full form and host/port parts read <c>KafkaServerResource.InternalEndpoint</c>
    ///     (never <c>ConnectionStringExpression</c>, which resolves the EXTERNAL/host-published
    ///     endpoint) — mirrors the schema-registry sidecar pattern above.
    ///   </description></item>
    ///   <item><description>
    ///     mailpit — a plain <c>ContainerResource</c> with no <c>IResourceWithConnectionString</c>;
    ///     host/port and the full form read the SMTP <see cref="EndpointReference"/> the
    ///     registration's Build lambda already staged into <paramref name="serviceEndpoints"/>.
    ///   </description></item>
    ///   <item><description>
    ///     redis/rabbitmq/nats/elasticsearch — <paramref name="retained"/> IS the server
    ///     resource; database is unsupported (not a database kind). Credential parts are
    ///     included only where the Aspire integration ACTUALLY enforces one by default —
    ///     verified by inspecting each resource's <c>CommandLineArgsCallbackAnnotation</c>
    ///     (the real container startup args), not just whether a <c>ParameterResource</c>
    ///     object exists: <c>AddRedis</c> unconditionally passes
    ///     <c>redis-server --requirepass $REDIS_PASSWORD</c> and <c>AddNats(...).WithJetStream()</c>
    ///     (the exact call this mapper makes) unconditionally passes <c>--user &lt;name&gt; --pass
    ///     &lt;password&gt;</c> — both real, SUT-breaking credentials — so both get a
    ///     <c>password</c> part (nats additionally gets <c>username</c>, whose fixed default is
    ///     the literal "nats"; redis has no username concept at all). Elasticsearch's own
    ///     <c>PasswordParameter</c> is likewise real, but THIS registration's Build lambda
    ///     unconditionally sets <c>xpack.security.enabled=false</c>, so the password is
    ///     provisioned but never enforced — no part is exposed for it. Kafka carries no
    ///     credential mechanism at all (no PasswordParameter/UserNameParameter on
    ///     <see cref="KafkaServerResource"/>) — plaintext, matching the schema-registry
    ///     sidecar's own PLAINTEXT bootstrap assumption.
    ///   </description></item>
    /// </list>
    /// </summary>
    private static DependencyEnvAccess ResolveDependencyEnvAccess(
        string name,
        string dependencyType,
        IResourceBuilder<IResource> retained,
        IReadOnlyDictionary<string, EndpointReference> serviceEndpoints)
    {
        if (string.Equals(dependencyType, "mailpit", StringComparison.OrdinalIgnoreCase))
        {
            var smtp = serviceEndpoints[name + "-smtp"];
            return new DependencyEnvAccess(
                FullConnection: BuildHostPortExpression(smtp),
                Host: smtp.Property(EndpointProperty.Host),
                Port: smtp.Property(EndpointProperty.Port),
                Username: null,
                Password: null,
                Database: null);
        }

        if (retained.Resource is KafkaServerResource kafka)
        {
            var internalEndpoint = kafka.InternalEndpoint;
            return new DependencyEnvAccess(
                FullConnection: BuildHostPortExpression(internalEndpoint),
                Host: internalEndpoint.Property(EndpointProperty.Host),
                Port: internalEndpoint.Property(EndpointProperty.Port),
                Username: null,
                Password: null,
                Database: null);
        }

        if (retained.Resource is PostgresDatabaseResource pgDb)
        {
            var server = pgDb.Parent;
            return new DependencyEnvAccess(
                pgDb.ConnectionStringExpression, server.Host, server.Port,
                server.UserNameReference, server.PasswordParameter, pgDb.DatabaseName);
        }

        if (retained.Resource is MySqlDatabaseResource mySqlDb)
        {
            var server = mySqlDb.Parent;
            return new DependencyEnvAccess(
                mySqlDb.ConnectionStringExpression, server.Host, server.Port,
                // MySqlServerResource exposes no UserNameParameter/Reference: the container
                // always provisions the fixed 'root' superuser (verified against
                // Aspire.Hosting.MySql 13.4.2 — no username override is possible).
                "root", server.PasswordParameter, mySqlDb.DatabaseName);
        }

        if (retained.Resource is SqlServerDatabaseResource sqlDb)
        {
            var server = sqlDb.Parent;
            return new DependencyEnvAccess(
                sqlDb.ConnectionStringExpression, server.Host, server.Port,
                server.UserNameReference, server.PasswordParameter, sqlDb.DatabaseName);
        }

        if (retained.Resource is MongoDBDatabaseResource mongoDb)
        {
            var server = mongoDb.Parent;
            return new DependencyEnvAccess(
                mongoDb.ConnectionStringExpression, server.Host, server.Port,
                server.UserNameReference, server.PasswordParameter, mongoDb.DatabaseName);
        }

        if (retained.Resource is RabbitMQServerResource rabbitmq)
        {
            return new DependencyEnvAccess(
                rabbitmq.ConnectionStringExpression, rabbitmq.Host, rabbitmq.Port,
                rabbitmq.UserNameReference, rabbitmq.PasswordParameter, null);
        }

        if (retained.Resource is RedisResource redis)
        {
            // AddRedis unconditionally starts the container as
            // 'redis-server --requirepass $REDIS_PASSWORD' (verified via the resource's
            // CommandLineArgsCallbackAnnotation against the exact 'builder.AddRedis(name)' call
            // this mapper makes) — a real, enforced credential a SUT must supply. Redis has no
            // username concept at all, so only 'password' is exposed.
            return new DependencyEnvAccess(
                redis.ConnectionStringExpression, redis.Host, redis.Port,
                null, RequirePasswordParameter(name, "redis", redis.PasswordParameter), null);
        }

        if (retained.Resource is NatsServerResource nats)
        {
            // AddNats(name).WithJetStream() (the exact call this mapper makes) unconditionally
            // starts the container with '--user <name> --pass <password>' (verified the same
            // way as redis above) — both are real, enforced credentials.
            return new DependencyEnvAccess(
                nats.ConnectionStringExpression, nats.Host, nats.Port,
                nats.UserNameReference, RequirePasswordParameter(name, "nats", nats.PasswordParameter), null);
        }

        if (retained.Resource is ElasticsearchResource elasticsearch)
        {
            // ElasticsearchResource.PasswordParameter is real, but this registration's Build
            // lambda unconditionally sets 'xpack.security.enabled=false' above — the password
            // is provisioned but never enforced, so no credential part is exposed here.
            return new DependencyEnvAccess(
                elasticsearch.ConnectionStringExpression,
                elasticsearch.PrimaryEndpoint.Property(EndpointProperty.Host),
                elasticsearch.PrimaryEndpoint.Property(EndpointProperty.Port),
                null, null, null);
        }

        // Unreachable for any type accepted by s_dependencyRegistry other than
        // azureservicebus (rejected in ValidateEnvValue before this method is ever reached for
        // that kind) — defensive fallback only, e.g. if a future dependency kind is registered
        // without a matching case here.
        throw new ArgumentException(
            $"Internal error: dependency '{name}' of type '{dependencyType}' has no env: " +
            "resolution path in ResolveDependencyEnvAccess.",
            nameof(dependencyType));
    }

    /// <summary>
    /// Returns <paramref name="password"/> or throws when it is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Both callers (redis, nats) rely on the CURRENT <c>Aspire.Hosting.Redis</c>/
    /// <c>Aspire.Hosting.Nats</c> 13.4.2 behaviour of unconditionally provisioning a
    /// <see cref="ParameterResource"/> for <c>PasswordParameter</c> — verified by reflection
    /// (see the sprint notes). Should a future Aspire version ever make either optional, this
    /// fails fast with the same "unsupported part" shape <see cref="ValidateEnvValue"/> already
    /// uses, rather than propagating a <see cref="NullReferenceException"/> from deep inside
    /// <see cref="BuildEnvExpression"/> — so behaviour stays deterministic either way.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="password"/> is <see langword="null"/>.</exception>
    private static ParameterResource RequirePasswordParameter(
        string dependencyName, string dependencyType, ParameterResource? password)
    {
        if (password is not null)
        {
            return password;
        }

        throw new ArgumentException(
            $"Dependency '{dependencyName}' (type '{dependencyType}') has no password parameter " +
            "at build time, so the 'password' part is unsupported for this instance. The current " +
            "Aspire.Hosting integration always provisions one for this kind; this fails fast " +
            "instead of embedding a null reference, so behaviour stays deterministic.",
            nameof(password));
    }

    /// <summary>
    /// Builds a <c>{host}:{port}</c> <see cref="ReferenceExpression"/> from an
    /// <see cref="EndpointReference"/> — the literal shape mq clients / TCP dial targets expect,
    /// with no URI scheme prefix.
    /// </summary>
    private static ReferenceExpression BuildHostPortExpression(EndpointReference endpoint)
    {
        var builder = new ReferenceExpressionBuilder();
        builder.AppendValueProvider(endpoint.Property(EndpointProperty.Host), null);
        builder.AppendLiteral(":");
        builder.AppendValueProvider(endpoint.Property(EndpointProperty.Port), null);
        return builder.Build();
    }

    /// <summary>A single literal-text or <c>${conn:...}</c>-reference span of an env value.</summary>
    private readonly record struct EnvValueToken(bool IsReference, string Literal, string? DependencyName, string? Part);

    /// <summary>
    /// Splits an env value into literal-text and <c>${conn:name[.part]}</c>-reference tokens,
    /// left to right.  Shared by <see cref="BuildEnvExpression"/> (validation already ran in
    /// <see cref="ValidateEnvValue"/>, which uses the same <see cref="s_connRefPattern"/>).
    /// </summary>
    private static IEnumerable<EnvValueToken> TokeniseEnvValue(string value)
    {
        var pos = 0;
        foreach (Match m in s_connRefPattern.Matches(value))
        {
            if (m.Index > pos)
                yield return new EnvValueToken(false, value[pos..m.Index], null, null);

            yield return new EnvValueToken(
                true, string.Empty, m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null);
            pos = m.Index + m.Length;
        }

        if (pos < value.Length)
            yield return new EnvValueToken(false, value[pos..], null, null);
    }

    /// <summary>
    /// Builds the Aspire <see cref="ReferenceExpression"/> for a single <c>env:</c> value,
    /// splicing literal spans and dependency parts (or the whole connection, for a bare
    /// <c>${conn:name}</c> reference) via <see cref="ReferenceExpressionBuilder"/>.
    /// </summary>
    private static ReferenceExpression BuildEnvExpression(
        string value,
        IReadOnlyDictionary<string, DependencyEnvAccess> envAccessByDependency)
    {
        var builder = new ReferenceExpressionBuilder();
        foreach (var token in TokeniseEnvValue(value))
        {
            if (!token.IsReference)
            {
                if (token.Literal.Length > 0)
                    builder.AppendLiteral(token.Literal);
                continue;
            }

            var access = envAccessByDependency[token.DependencyName!];
            var part = token.Part switch
            {
                null => (object)access.FullConnection,
                "host" => access.Host!,
                "port" => access.Port!,
                "username" => access.Username!,
                "password" => access.Password!,
                "database" => access.Database!,
                // Unreachable: ValidateEnvValue already rejected any other part name.
                _ => throw new ArgumentException($"Unsupported env: part '{token.Part}'.", nameof(value)),
            };
            AppendPart(builder, part);
        }

        return builder.Build();
    }

    /// <summary>
    /// Appends a resolved dependency part to <paramref name="builder"/>: a plain literal for a
    /// <see cref="string"/> (e.g. mysql's fixed 'root' username, or a database name), or an
    /// Aspire value provider for anything else (late-bound via
    /// <see cref="ReferenceExpressionBuilder.AppendValueProvider"/> so this helper stays
    /// agnostic to which concrete provider type — <see cref="EndpointReferenceExpression"/>,
    /// <see cref="ReferenceExpression"/>, or <see cref="ParameterResource"/> — was supplied).
    /// </summary>
    private static void AppendPart(ReferenceExpressionBuilder builder, object part)
    {
        if (part is string literal)
            builder.AppendLiteral(literal);
        else
            builder.AppendValueProvider(part, null);
    }

    /// <summary>
    /// Applies a service's <c>env:</c> mapping (if any) to <paramref name="builder"/> via
    /// <c>WithEnvironment(name, ReferenceExpression)</c> — works identically for image-form
    /// (<see cref="ContainerResource"/>) and project-form (<c>ProjectResource</c>) services,
    /// both of which implement <see cref="IResourceWithEnvironment"/>.
    /// </summary>
    private static void ApplyEnv<T>(
        IResourceBuilder<T> builder,
        IReadOnlyDictionary<string, string>? env,
        IReadOnlyDictionary<string, DependencyEnvAccess> envAccessByDependency)
        where T : IResourceWithEnvironment
    {
        if (env is null)
            return;

        foreach (var (key, value) in env)
        {
            var expression = BuildEnvExpression(value, envAccessByDependency);
            builder.WithEnvironment(key, expression);
        }
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns <see langword="true"/> when a kafka dependency's <see cref="DependencySpec.Extra"/>
    /// mapping carries a scalar <c>schemaRegistry</c> whose value is <c>true</c>
    /// (case-insensitive), requesting an auxiliary Confluent Schema Registry container.
    /// </summary>
    /// <param name="extra">
    /// The raw YAML mapping node from <see cref="DependencySpec.Extra"/>; may be
    /// <see langword="null"/> (no extra fields → no registry).
    /// </param>
    /// <summary>Cached options for <see cref="GenerateAsbConfigJson"/> serialisation (CA1869).</summary>
    private static readonly JsonSerializerOptions s_asbConfigJsonOptions =
        new JsonSerializerOptions { WriteIndented = true };

    private static bool KafkaWantsSchemaRegistry(YamlMappingNode? extra)
    {
        if (extra is null)
            return false;

        if (!extra.Children.TryGetValue(new YamlScalarNode("schemaRegistry"), out var node))
            return false;

        return node is YamlScalarNode { Value: { } value } &&
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses the <c>extra.queues</c> sequence from an Azure Service Bus dependency's
    /// <see cref="DependencySpec.Extra"/> YAML node.
    /// </summary>
    private static IReadOnlyList<string> ParseAsbQueues(YamlMappingNode? extra)
    {
        if (extra is null)
            return Array.Empty<string>();
        if (!extra.Children.TryGetValue(new YamlScalarNode("queues"), out var node))
            return Array.Empty<string>();
        if (node is not YamlSequenceNode seq)
            return Array.Empty<string>();
        return seq.Children
            .OfType<YamlScalarNode>()
            .Where(s => s.Value is not null)
            .Select(s => s.Value!)
            .ToList();
    }

    /// <summary>
    /// Parses the <c>extra.topics</c> sequence from an Azure Service Bus dependency's
    /// <see cref="DependencySpec.Extra"/> YAML node.
    /// Each entry may carry a <c>name</c> scalar and an optional <c>subscriptions</c> sequence.
    /// </summary>
    private static IReadOnlyList<(string Name, IReadOnlyList<string> Subscriptions)> ParseAsbTopics(
        YamlMappingNode? extra)
    {
        if (extra is null)
            return Array.Empty<(string, IReadOnlyList<string>)>();
        if (!extra.Children.TryGetValue(new YamlScalarNode("topics"), out var node))
            return Array.Empty<(string, IReadOnlyList<string>)>();
        if (node is not YamlSequenceNode seq)
            return Array.Empty<(string, IReadOnlyList<string>)>();

        var result = new List<(string, IReadOnlyList<string>)>();
        foreach (var item in seq.Children.OfType<YamlMappingNode>())
        {
            if (!item.Children.TryGetValue(new YamlScalarNode("name"), out var nameNode) ||
                nameNode is not YamlScalarNode { Value: { } topicName })
                continue;

            var subs = new List<string>();
            if (item.Children.TryGetValue(new YamlScalarNode("subscriptions"), out var subsNode) &&
                subsNode is YamlSequenceNode subsSeq)
            {
                subs.AddRange(subsSeq.Children
                    .OfType<YamlScalarNode>()
                    .Where(s => s.Value is not null)
                    .Select(s => s.Value!));
            }

            result.Add((topicName, subs));
        }

        return result;
    }

    /// <summary>
    /// Generates the Config.json content for the Azure Service Bus emulator using
    /// <see cref="JsonSerializer"/> so special characters in queue/topic names are
    /// correctly escaped and manual <c>EscapeJson</c> is avoided (FIX 5).
    /// </summary>
    private static string GenerateAsbConfigJson(
        IReadOnlyList<string> queues,
        IReadOnlyList<(string Name, IReadOnlyList<string> Subscriptions)> topics)
    {
        // The "Logging" section is REQUIRED by the emulator — omitting it causes a
        // NullReferenceException at startup ("Logging config cannot be null").
        // "Console" is the safest type for non-persistent containers.
        var config = new
        {
            UserConfig = new
            {
                Namespaces = new[]
                {
                    new
                    {
                        Name = "sbemulatorns",
                        Queues = queues
                            .Select(q => new { Name = q, Properties = new { } })
                            .ToArray(),
                        Topics = topics
                            .Select(t => new
                            {
                                Name = t.Name,
                                Properties = new { },
                                Subscriptions = t.Subscriptions
                                    .Select(s => new { Name = s, Properties = new { } })
                                    .ToArray(),
                            })
                            .ToArray(),
                    },
                },
                Logging = new { Type = "Console" },
            },
        };

        return JsonSerializer.Serialize(config, s_asbConfigJsonOptions);
    }

    /// <summary>
    /// Resolves the fully-qualified image reference by applying <paramref name="registry"/>
    /// as a prefix when the image has no explicit registry component.
    /// </summary>
    internal static string ResolveImage(string image, string? registry)
    {
        if (string.IsNullOrEmpty(registry))
            return image;

        var withoutDigest = image;
        var atIndex = image.IndexOf('@', StringComparison.Ordinal);
        if (atIndex >= 0)
            withoutDigest = image[..atIndex];

        var slashIndex = withoutDigest.IndexOf('/', StringComparison.Ordinal);

        if (slashIndex < 0)
            return $"{registry}/{image}";

        var firstComponent = withoutDigest[..slashIndex];

        var hasRegistry =
            firstComponent.Contains('.', StringComparison.Ordinal) ||
            firstComponent.Contains(':', StringComparison.Ordinal) ||
            string.Equals(firstComponent, "localhost", StringComparison.Ordinal);

        return hasRegistry ? image : $"{registry}/{image}";
    }
}
