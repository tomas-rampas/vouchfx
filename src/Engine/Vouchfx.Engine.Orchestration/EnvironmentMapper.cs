// Vouchfx.Engine.Orchestration — EnvironmentMapper (S03-A-02, expanded Phase 0 batch).
//
// Maps a parsed EnvironmentSpec (from Vouchfx.Engine.Authoring) to an Aspire resource graph,
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
//   Thirteen dependency types are supported.  Each entry supplies:
//   • Build — called inside the configure delegate; mutates the Aspire builder, populates
//     serviceEndpoints for sidecar containers (kafka schema-registry, mailpit SMTP) AND for
//     plain single-endpoint containers that need env: host/port access (mailpit, dynamodb,
//     minio), populates depConnBuilders for dependencies that need custom connection-string
//     construction (azureservicebus, dynamodb, minio — all plain containers, not Aspire typed
//     resources, so none implements IResourceWithConnectionString), and returns
//     (Retained, MostSpecific) IResourceBuilder<IResource> pairs:
//     - Retained    → stored in dependencyBuilders[name]; used for connection-string resolution.
//     - MostSpecific → added to mostSpecificDependencyResources; services WaitFor these.
//     For database-backed types (postgres/sqlserver/mysql/mongodb) both are the *database* resource.
//     For server-only types (redis/elasticsearch/rabbitmq/nats/kafka/azureservicebus) both are the server resource.
//     For plain-container types (mailpit/azureservicebus/dynamodb/minio) both are the container itself.
//   • HealthGateNames — produces the ordered gate-name sequence for the topology fixture to await.
//   Adding a new dependency type = add one entry; Map() is unchanged.

using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring.Model;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Orchestration;

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
/// Thirteen types are supported via the internal registration table.  Database-backed types
/// (postgres, sqlserver, mysql, mongodb) gate on the <em>database</em> resource; server-only
/// types (redis, elasticsearch, rabbitmq, nats, kafka, azureservicebus) gate on the server itself;
/// plain-container types with no dedicated Aspire integration (mailpit, azureservicebus,
/// dynamodb, minio) gate on the container resource itself.
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
    /// <para>
    /// The sixth and seventh parameters (<c>imageRegistry</c>, <c>pullPolicy</c>) are the
    /// env-level <see cref="EnvironmentSpec.ImageRegistry"/>/<see cref="EnvironmentSpec.ImagePullPolicy"/>
    /// overrides (feat/dependency-image-override), threaded through so every registration's
    /// Build lambda can apply them via <see cref="ApplyImageOverrides{T}"/> — previously
    /// <c>imageRegistry</c> was captured by <see cref="Map"/> but consumed only inside the
    /// services loop, so dependencies never saw it at all.
    /// </para>
    /// </remarks>
    private sealed record DependencyRegistration(
        Func<IDistributedApplicationBuilder, string, DependencySpec,
             Dictionary<string, EndpointReference>,
             Dictionary<string, Func<CancellationToken, Task<string?>>>,
             string?,
             ImagePullPolicy?,
             (IResourceBuilder<IResource> Retained, IResourceBuilder<IResource> MostSpecific)> Build,
        Func<string, DependencySpec, IEnumerable<string>> HealthGateNames);

    // Pre-GA decision (feat/case-sensitive-kinds): Ordinal, not OrdinalIgnoreCase — a dependency
    // `type` has exactly one canonical spelling (the lower-case keys below), matching the JSON
    // Schema's own treatment of `imagePullPolicy` and the DSL's `verifyMode`. Widening this to
    // accept every case variant would make editor completion noisy ("Postgres"/"postgres"/
    // "POSTGRES") and stop the vocabulary from being a clean, single-spelling statement.
    private static readonly Dictionary<string, DependencyRegistration> s_dependencyRegistry =
        new(StringComparer.Ordinal)
        {
            // ---- database-backed: gate on the database, not the server ----
            // §4 invariant: the server resource returns healthy before the DCP lifecycle
            // script finishes creating the database, causing intermittent failures on fast
            // hardware.  Retain the DATABASE builder for connection-string discovery too.

            ["postgres"] = new DependencyRegistration(
                Build: (builder, name, spec, _, _, imageRegistry, pullPolicy) =>
                {
                    var serverBuilder = ApplyImageOverrides(builder.AddPostgres(name), spec, imageRegistry, pullPolicy);
                    var dbBuilder = serverBuilder.AddDatabase(name + "db");
                    var retainedDb = (IResourceBuilder<IResource>)(object)dbBuilder;
                    return (retainedDb, retainedDb);
                },
                HealthGateNames: (name, _) => new[] { name + "db" }),

            ["sqlserver"] = new DependencyRegistration(
                Build: (builder, name, spec, _, _, imageRegistry, pullPolicy) =>
                {
                    var serverBuilder = ApplyImageOverrides(builder.AddSqlServer(name), spec, imageRegistry, pullPolicy);
                    var dbBuilder = serverBuilder.AddDatabase(name + "db");
                    var retainedDb = (IResourceBuilder<IResource>)(object)dbBuilder;
                    return (retainedDb, retainedDb);
                },
                HealthGateNames: (name, _) => new[] { name + "db" }),

            ["mysql"] = new DependencyRegistration(
                Build: (builder, name, spec, _, _, imageRegistry, pullPolicy) =>
                {
                    var serverBuilder = ApplyImageOverrides(builder.AddMySql(name), spec, imageRegistry, pullPolicy);
                    var dbBuilder = serverBuilder.AddDatabase(name + "db");
                    var retainedDb = (IResourceBuilder<IResource>)(object)dbBuilder;
                    return (retainedDb, retainedDb);
                },
                HealthGateNames: (name, _) => new[] { name + "db" }),

            ["mongodb"] = new DependencyRegistration(
                Build: (builder, name, spec, _, _, imageRegistry, pullPolicy) =>
                {
                    var serverBuilder = ApplyImageOverrides(builder.AddMongoDB(name), spec, imageRegistry, pullPolicy);
                    var dbBuilder = serverBuilder.AddDatabase(name + "db");
                    var retainedDb = (IResourceBuilder<IResource>)(object)dbBuilder;
                    return (retainedDb, retainedDb);
                },
                HealthGateNames: (name, _) => new[] { name + "db" }),

            // ---- server-only: gate on the server itself ----

            ["redis"] = new DependencyRegistration(
                Build: (builder, name, spec, _, _, imageRegistry, pullPolicy) =>
                {
                    var serverBuilder = ApplyImageOverrides(builder.AddRedis(name), spec, imageRegistry, pullPolicy);
                    var retained = (IResourceBuilder<IResource>)(object)serverBuilder;
                    return (retained, retained);
                },
                HealthGateNames: (name, _) => new[] { name }),

            ["elasticsearch"] = new DependencyRegistration(
                Build: (builder, name, spec, _, _, imageRegistry, pullPolicy) =>
                {
                    var serverBuilder = ApplyImageOverrides(builder.AddElasticsearch(name), spec, imageRegistry, pullPolicy);
                    // Stability environment variables: single-node discovery, security
                    // disabled (avoids TLS/credential setup in test environments), and
                    // bounded JVM heap to prevent OOM on CI runners.  Disk-watermark
                    // allocation thresholds are disabled because CI runners routinely
                    // exceed the default ~90% high watermark after pulling this suite's
                    // images — above it Elasticsearch accepts index creation but never
                    // allocates the primary shard, so every write 503s while the rest of
                    // the topology looks healthy.  The data is ephemeral test state on a
                    // throwaway container, so the safeguard protects nothing here.
                    serverBuilder = serverBuilder
                        .WithEnvironment("discovery.type", "single-node")
                        .WithEnvironment("xpack.security.enabled", "false")
                        .WithEnvironment("ES_JAVA_OPTS", "-Xms512m -Xmx512m")
                        .WithEnvironment("cluster.routing.allocation.disk.threshold_enabled", "false");
                    var retained = (IResourceBuilder<IResource>)(object)serverBuilder;
                    return (retained, retained);
                },
                HealthGateNames: (name, _) => new[] { name }),

            ["rabbitmq"] = new DependencyRegistration(
                Build: (builder, name, spec, _, _, imageRegistry, pullPolicy) =>
                {
                    var serverBuilder = ApplyImageOverrides(builder.AddRabbitMQ(name), spec, imageRegistry, pullPolicy);
                    var retained = (IResourceBuilder<IResource>)(object)serverBuilder;
                    return (retained, retained);
                },
                HealthGateNames: (name, _) => new[] { name }),

            ["nats"] = new DependencyRegistration(
                Build: (builder, name, spec, _, _, imageRegistry, pullPolicy) =>
                {
                    // WithJetStream() appends the '-js' flag so the NATS container starts with
                    // JetStream enabled.  Without it, CreateStreamAsync / PublishAsync throw
                    // NatsJSApiException and every mq-publish.nats / mq-expect.nats step
                    // returns EnvironmentError — FIX B1.
                    var serverBuilder = ApplyImageOverrides(
                        builder.AddNats(name).WithJetStream(), spec, imageRegistry, pullPolicy);
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
                Build: (builder, name, spec, serviceEndpoints, _, imageRegistry, pullPolicy) =>
                {
                    var kafkaBuilder = ApplyImageOverrides(builder.AddKafka(name), spec, imageRegistry, pullPolicy);

                    if (KafkaWantsSchemaRegistry(spec.Extra))
                    {
                        var srName = name + "-sr";
                        var internalEndpoint = kafkaBuilder.Resource.InternalEndpoint;
                        var bootstrapServers = ReferenceExpression.Create(
                            $"PLAINTEXT://{internalEndpoint.Property(EndpointProperty.Host)}:{internalEndpoint.Property(EndpointProperty.Port)}");
                        // feat/dependency-image-override (§ item 6, sidecars out of scope): this
                        // sidecar has no independent identity in the YAML — spec.Image names only
                        // the BROKER (the retained/mostSpecific resource matching the dependency
                        // name). The author cannot point the schema-registry sidecar at their own
                        // mirror even after this change. imageRegistry/pullPolicy are env-level
                        // policies (not per-dependency image identity), so they still apply here.
                        var srContainerBuilder = ApplySidecarRegistryAndPullPolicy(
                            builder
                                .AddContainer(srName, "confluentinc/cp-schema-registry", "7.6.1")
                                .WithEnvironment("SCHEMA_REGISTRY_HOST_NAME", srName)
                                .WithEnvironment(
                                    "SCHEMA_REGISTRY_KAFKASTORE_BOOTSTRAP_SERVERS",
                                    bootstrapServers)
                                .WithEnvironment("SCHEMA_REGISTRY_LISTENERS", "http://0.0.0.0:8081")
                                .WithHttpEndpoint(targetPort: 8081, name: "http")
                                .WithHttpHealthCheck(path: "/subjects", endpointName: "http")
                                .WaitFor(kafkaBuilder),
                            imageRegistry,
                            pullPolicy);
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
                Build: (builder, name, spec, serviceEndpoints, _, imageRegistry, pullPolicy) =>
                {
                    // Pin a stable tag for determinism (§4): never float on 'latest'.
                    // Authors may still override via the dependency's 'version' field, or now
                    // via 'image:' (feat/dependency-image-override) — ApplyImageOverrides applies
                    // spec.Image/spec.Version/imageRegistry/pullPolicy on top of this default.
                    var containerBuilder = ApplyImageOverrides(
                        builder.AddContainer(name, "axllent/mailpit", "v1.21"),
                        spec,
                        imageRegistry,
                        pullPolicy)
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
                Build: (builder, name, spec, _, depConnBuilders, imageRegistry, pullPolicy) =>
                {
                    var sidecarName = name + "-sqledge";

                    // SQL Server sidecar — required by the ASB emulator for persistence.
                    // MSSQL_SA_PASSWORD meets SQL Server complexity requirements.
                    // The "2022-latest" tag is intentionally floating: SQL Server 2022 minor/CU
                    // updates are backwards-compatible and this topology validates the ASB layer,
                    // not SQL internals — pinning would add upgrade churn with no safety benefit.
                    // feat/dependency-image-override (§ item 6, sidecars out of scope): this
                    // sidecar has no independent identity in the YAML — spec.Image names only the
                    // emulator itself (below), never this SQL persistence sidecar; the author
                    // cannot point it at their own mirror even after this change. imageRegistry/
                    // pullPolicy are env-level policies (not per-dependency image identity), so
                    // they still apply here.
                    var sidecarBuilder = ApplySidecarRegistryAndPullPolicy(
                        builder
                            .AddContainer(sidecarName, "mcr.microsoft.com/mssql/server", "2022-latest")
                            .WithEnvironment("ACCEPT_EULA", "Y")
                            .WithEnvironment("MSSQL_SA_PASSWORD", "Str0ng!P@ssword#1"),
                        imageRegistry,
                        pullPolicy);

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

                    emulatorBuilder = ApplyImageOverrides(emulatorBuilder, spec, imageRegistry, pullPolicy);

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

            // ---- dynamodb: DynamoDB Local, a plain container with no HTTP health path ----
            // amazon/dynamodb-local has no dedicated health-check endpoint: GET "/" returns
            // HTTP 400 ("Malformed HTTP request") because the request is not a valid AWS
            // SigV4 API call — but a 400 response still PROVES the Jetty listener is up and
            // answering, exactly the liveness signal a health check needs. Reflection against
            // the pinned Aspire.Hosting 13.4.2 DLL confirms WithHttpHealthCheck's 'statusCode'
            // parameter is literally "the result code to interpret as healthy" — so passing
            // statusCode: 400 is the correct, documented way to health-gate this container;
            // no TCP/endpoint-existence workaround is needed.
            //
            // Like azureservicebus, dynamodb-local is a plain container (not an Aspire typed
            // resource) and does NOT implement IResourceWithConnectionString, so the
            // connection string is synthesised via depConnBuilders once the endpoint's host/
            // port are known (post-StartAsync). dynamodb-local ignores credentials entirely,
            // so the synthesised form carries fixed dummy values — never real AWS keys and
            // never §17 secret material:
            //   ServiceURL=http://<host>:<port>;AccessKey=local;SecretKey=local
            // db-assert.dynamodb parses this exact key=value;… form (documented in its own
            // provider header) into AmazonDynamoDBConfig(ServiceURL) + BasicAWSCredentials.
            //
            // The endpoint is ALSO staged into serviceEndpoints[name] (a late-bound
            // EndpointReference, resolved in the CONTAINER network context, not the
            // host-published one depConnBuilders reads) so env: host/port references work
            // for a containerised SUT exactly as they do for kafka/elasticsearch/mailpit.

            ["dynamodb"] = new DependencyRegistration(
                Build: (builder, name, spec, serviceEndpoints, depConnBuilders, imageRegistry, pullPolicy) =>
                {
                    // Pin a specific tag (§4) — verified to exist on Docker Hub before use.
                    // Authors may override via 'version', or now via 'image:'
                    // (feat/dependency-image-override) — ApplyImageOverrides applies
                    // spec.Image/spec.Version/imageRegistry/pullPolicy on top of this default.
                    var containerBuilder = ApplyImageOverrides(
                        builder.AddContainer(name, "amazon/dynamodb-local", "2.5.2"),
                        spec,
                        imageRegistry,
                        pullPolicy)
                        .WithHttpEndpoint(targetPort: 8000, name: "http")
                        .WithHttpHealthCheck(path: "/", statusCode: 400, endpointName: "http");

                    var httpEndpoint = containerBuilder.GetEndpoint("http");
                    serviceEndpoints[name] = httpEndpoint;

                    depConnBuilders[name] = _ =>
                    {
                        var url = httpEndpoint.Url;
                        var connStr = $"ServiceURL={url};AccessKey=local;SecretKey=local";
                        return Task.FromResult<string?>(connStr);
                    };

                    var retained = (IResourceBuilder<IResource>)(object)containerBuilder;
                    return (retained, retained);
                },
                HealthGateNames: (name, _) => new[] { name }),

            // ---- minio: an S3-API-compatible object store, plain container ----
            // MinIO ships documented health paths; the cluster readiness path
            // (/minio/health/cluster) returns 200 only once the object layer can serve
            // requests, whereas /minio/health/live merely proves the process started —
            // so readiness is the correct gate here, and — unlike dynamodb-local — the
            // default 200-status WithHttpHealthCheck applies unchanged.
            // WithArgs (reflection-verified against the pinned Aspire.Hosting 13.4.2 DLL:
            // ResourceBuilderExtensions.WithArgs(IResourceBuilder<T>, string[])) supplies the
            // 'server /data' command MinIO requires to start in server mode.
            //
            // Like dynamodb-local, minio is a plain container with no
            // IResourceWithConnectionString, so its connection string is synthesised via
            // depConnBuilders in the same key=value;… form:
            //   ServiceURL=http://<host>:<port>;AccessKey=<user>;SecretKey=<password>
            // MINIO_ROOT_USER/MINIO_ROOT_PASSWORD are fixed LOCAL TEST credentials (never
            // real production secrets), mirroring how the postgres/sqlserver/mysql
            // registrations above use fixed default test credentials.

            ["minio"] = new DependencyRegistration(
                Build: (builder, name, spec, serviceEndpoints, depConnBuilders, imageRegistry, pullPolicy) =>
                {
                    // Pin a specific tag (§4) — verified to exist on Docker Hub before use.
                    // Authors may override via 'version', or now via 'image:'
                    // (feat/dependency-image-override) — ApplyImageOverrides applies
                    // spec.Image/spec.Version/imageRegistry/pullPolicy on top of this default.
                    const string accessKey = "vouchfx-minio";
                    const string secretKey = "vouchfx-minio-secret";
                    var containerBuilder = ApplyImageOverrides(
                        builder.AddContainer(name, "minio/minio", "RELEASE.2025-09-07T16-13-09Z"),
                        spec,
                        imageRegistry,
                        pullPolicy)
                        .WithArgs("server", "/data")
                        .WithEnvironment("MINIO_ROOT_USER", accessKey)
                        .WithEnvironment("MINIO_ROOT_PASSWORD", secretKey)
                        .WithHttpEndpoint(targetPort: 9000, name: "http")
                        .WithHttpHealthCheck(path: "/minio/health/cluster", endpointName: "http");

                    var httpEndpoint = containerBuilder.GetEndpoint("http");
                    serviceEndpoints[name] = httpEndpoint;

                    depConnBuilders[name] = _ =>
                    {
                        var url = httpEndpoint.Url;
                        var connStr = $"ServiceURL={url};AccessKey={accessKey};SecretKey={secretKey}";
                        return Task.FromResult<string?>(connStr);
                    };

                    var retained = (IResourceBuilder<IResource>)(object)containerBuilder;
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
                // Dependency types are matched case-sensitively (Ordinal, above). When the
                // author's spelling matches a known type in every way except case, name the
                // exact-case fix directly — an author whose suite just broke on this change
                // deserves to be told what to write, not merely that what they wrote is wrong.
                var caseInsensitiveMatch = s_dependencyRegistry.Keys.FirstOrDefault(
                    k => string.Equals(k, spec.Type, StringComparison.OrdinalIgnoreCase));
                var supportedTypes = string.Join(
                    ", ", s_dependencyRegistry.Keys.OrderBy(k => k, StringComparer.Ordinal));

                var message = caseInsensitiveMatch is not null
                    ? $"Unsupported dependency type '{spec.Type}' for dependency '{name}'. " +
                      $"Dependency types are case-sensitive — did you mean '{caseInsensitiveMatch}'? " +
                      $"Supported types: {supportedTypes}."
                    : $"Unsupported dependency type '{spec.Type}' for dependency '{name}'. " +
                      $"Supported types: {supportedTypes}.";

                throw new ArgumentException(message, nameof(env));
            }

            // feat/dependency-image-override — decided precedence (§5): an 'image:' that already
            // carries its own tag or digest, together with a sibling 'version:', is ambiguous —
            // reject it outright rather than silently picking one, which is how customers lose
            // hours chasing the wrong image. Runs after the type check above so the dependency is
            // already known-registered; ImageReferenceParser.Parse itself throws ArgumentException
            // on a malformed 'image:' string, which is left to surface unwrapped.
            //
            // C4 fix: a dangling 'image:' (no value) or an explicit 'image: ""' must be treated
            // identically to 'image' being absent altogether — mirrors the MN3 fix for Version a
            // few lines below (and ApplyImageOverrides' own mirror of this same guard). Before
            // this fix the guard was 'spec.Image is not null'.
            //
            // This IsNullOrEmpty check is LOAD-BEARING, not merely defensive: YamlDocumentParser.
            // GetScalarOrPlainNull (§66aef95-extension) only collapses PLAIN "no real content"
            // spellings to null — a dangling key, and the four YAML-null tokens. A QUOTED
            // 'image: ""' is a real, common AUTHORED shape (e.g. CI templating that renders an
            // unset variable into a quoted string) that the parser deliberately leaves as the
            // literal "" — quoting is the author's explicit opt-out from null-token resolution
            // (see GetScalarOrPlainNull's own remarks). This guard is what actually catches THAT
            // shape; do not simplify it to 'is not null' on the assumption "the parser already
            // handles every empty case" — it does not, for a quoted empty string, and narrowing
            // this guard would reintroduce the exact ArgumentException this fix closes for that
            // authored input. (It also covers a hand-constructed DependencySpec carrying ""
            // directly, as several tests in this area do, but that is the secondary reason.)
            //
            // REJECTED (do not re-introduce): widening this guard to IsNullOrWhiteSpace, on the
            // reasoning "match what ImageReferenceParser.Parse itself rejects, so spec.Image can
            // never reach Parse in a state Parse would reject". That rationale IS the regression
            // it looks like it prevents: pre-filtering exactly the inputs Parse rejects converts
            // Parse's loud, author-visible rejection into a silent intent-discard. A realistic
            // trigger — CI templating expanding an unset variable into 'image: "   "' — would
            // silently fall back to the provider's default image, exactly the "no author-visible
            // signal" failure mode the MN5 design comment in ImageReference.cs explicitly rejects
            // trimming for. The 66aef95 contract covers a dangling key and YAML's explicit null
            // only; whitespace-only text is neither, and must keep failing loudly via Parse's own
            // IsNullOrWhiteSpace check below — which is exactly what IsNullOrEmpty here preserves.
            if (!string.IsNullOrEmpty(spec.Image))
            {
                var parsedImage = ImageReferenceParser.Parse(spec.Image);

                // MINOR-2 fix (independent re-review, feat/dependency-image-override): the
                // digest's own algorithm prefix must be validated HERE, in this eager pass, not
                // inside ApplyImageOverrides. Reproduced before the fix: 'image: mongo@sha512:...'
                // and 'image: mongo@abcdef0123' (a digest with no algorithm prefix at all — a
                // plausible typo) both let Map() return successfully, and were rejected only far
                // downstream, inside the 'configure' closure, AFTER earlier dependencies in
                // iteration order had already mutated the builder — violating the "reject before
                // any builder mutation" discipline every other malformed-'image:' case in this
                // loop (and this method's own doc comment) is held to.
                // ImageReferenceParser.Parse only validates that the hash BODY is present and
                // hex; it deliberately does not care which algorithm prefixes the digest, so that
                // check alone does not catch this. 'sha256:' is the only digest algorithm
                // Aspire's ContainerImageAnnotation.SHA256 field supports (confirmed by
                // decompiling Aspire.Hosting 13.4.2 — see ApplyImageOverrides' own remarks at its
                // digest branch), so any other (or absent) prefix is rejected eagerly here
                // instead. ApplyImageOverrides no longer re-checks this — every call site is
                // reached only after this loop has already validated the same DependencySpec.
                if (parsedImage.Digest is not null &&
                    !parsedImage.Digest.StartsWith("sha256:", StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Dependency '{name}' image digest '{parsedImage.Digest}' does not use " +
                        "the 'sha256:' algorithm prefix — the only digest algorithm Aspire's " +
                        "container image annotation supports.",
                        nameof(env));
                }

                // MN3 fix: treat 'version: ""' the same as 'version:' absent everywhere this
                // method reasons about it — the mutation site (ApplyImageOverrides) does the
                // same normalisation, so both branches agree on what "no version" means.
                // IsNullOrEmpty, deliberately NOT IsNullOrWhiteSpace: a whitespace-only
                // 'version: "   "' is real, if useless, text the author actually wrote — it
                // becomes a literal (garbage) container tag, unchanged from origin/main and
                // outside the 66aef95 contract's scope (that contract covers a dangling key and
                // YAML's explicit null only). A prior revision of this fix widened this guard to
                // IsNullOrWhiteSpace "for symmetry with Image" — see the REJECTED note on the
                // Image guard above for why that symmetry was itself a regression.
                var hasVersion = !string.IsNullOrEmpty(spec.Version);

                if ((parsedImage.Tag is not null || parsedImage.Digest is not null) && hasVersion)
                {
                    var carries = parsedImage.Digest is not null
                        ? $"digest '{parsedImage.Digest}'"
                        : $"tag '{parsedImage.Tag}'";
                    throw new ArgumentException(
                        $"Dependency '{name}' sets both 'image: {spec.Image}' (which already " +
                        $"carries a {carries}) and 'version: {spec.Version}'. This is ambiguous — " +
                        "specify the tag/digest in exactly one place: either embed it in 'image:', " +
                        "or use 'version:' alone.",
                        nameof(env));
                }

                // M3 fix: a tagless, digestless 'image:' with no sibling 'version:' would
                // otherwise reach ApplyImageOverrides' WithImage(repository) call with no tag
                // argument at all — confirmed empirically against the pinned Aspire.Hosting
                // 13.4.2 packages, that overload writes ContainerImageAnnotation.Tag = "latest",
                // silently discarding whatever pinned default tag the provider's own AddXxx/
                // AddContainer registration established. This violates the §4 determinism
                // invariant every AddContainer-based registration in this file restates verbatim
                // three lines above its own call ("Pin a stable tag for determinism (§4): never
                // float on 'latest'"), it is the single most obvious thing an author will type
                // (a bare 'image: mongo'), and — before this fix — it had no test and no
                // documentation. Reject it eagerly, before any builder mutation, exactly like
                // the ambiguity check above.
                if (parsedImage.Tag is null && parsedImage.Digest is null && !hasVersion)
                {
                    throw new ArgumentException(
                        $"Dependency '{name}' sets 'image: {spec.Image}' with no tag or digest, " +
                        "and no sibling 'version:' either. This would silently float on the " +
                        "':latest' tag, defeating the §4 determinism invariant. Either embed a " +
                        $"tag in 'image:' (e.g. 'image: {spec.Image}:<tag>'), or add a " +
                        "'version:' field.",
                        nameof(env));
                }
            }
        }

        // feat/dependency-image-override — validate the env-level imagePullPolicy eagerly,
        // rejecting an unrecognised value loudly rather than silently ignoring it (air-gapped
        // users rely on Never/Missing actually taking effect). Each SERVICE's own override is
        // validated once, below, by the servicePullPolicies-building loop (N3 tidy: that loop
        // already calls ParseImagePullPolicy for every service with its own override and runs
        // entirely within Map(), before Configure is ever invoked, so a second dedicated
        // validate-only pass over the same services here was pure duplicate parsing).
        ImagePullPolicy? envPullPolicy = string.IsNullOrEmpty(env.ImagePullPolicy)
            ? null
            : ParseImagePullPolicy(env.ImagePullPolicy, "The environment-level 'imagePullPolicy'");

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

        // Effective per-service pull policy: the service's own override when set (parsed, and
        // validated, right here — the only place a service-level imagePullPolicy is parsed), else
        // the env-level default (§3.2.1, parsed above). Still runs entirely within Map(), before
        // Configure is ever invoked, so an unrecognised value is still rejected eagerly.
        var servicePullPolicies = new Dictionary<string, ImagePullPolicy?>(StringComparer.Ordinal);
        foreach (var (name, spec) in services)
        {
            servicePullPolicies[name] = string.IsNullOrEmpty(spec.ImagePullPolicy)
                ? envPullPolicy
                : ParseImagePullPolicy(spec.ImagePullPolicy, $"The 'imagePullPolicy' on service '{name}'");
        }

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
                    builder, name, spec, serviceEndpoints, depConnBuilders, imageRegistry, envPullPolicy);
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

                    // feat/dependency-image-override (§ item 4): apply the effective pull policy
                    // (service-level override, else the env-level default) — WithImagePullPolicy
                    // requires T : ContainerResource, which only the image-form branch satisfies;
                    // a project-form service has no container image at all, so pull policy is
                    // meaningless there and is never applied in the 'else if Project' branch below.
                    var servicePullPolicy = servicePullPolicies[name];
                    if (servicePullPolicy is not null)
                        containerBuilder = containerBuilder.WithImagePullPolicy(servicePullPolicy.Value);

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
    /// disables security (<c>xpack.security.enabled=false</c>), so it is never enforced;
    /// dynamodb-local ignores credentials entirely (any AccessKey/SecretKey is accepted); minio
    /// DOES enforce MINIO_ROOT_USER/MINIO_ROOT_PASSWORD, but those are fixed values this
    /// registration sets itself (not per-instance generated parameters), so they are exposed to
    /// providers only via the synthesised <c>depConnBuilders</c> connection string, not as a
    /// separate <c>${conn:name.username/password}</c> part.
    /// </summary>
    private static readonly string[] s_hostPortOnlyParts = { "host", "port" };

    /// <summary>
    /// Returns the <c>${conn:name.part}</c> accessor names supported for a dependency of
    /// <paramref name="dependencyType"/>.  Matched case-sensitively: by the time a
    /// <see cref="DependencySpec.Type"/> reaches here it has already passed <see cref="Map"/>'s
    /// eager, case-sensitive validation against <see cref="s_dependencyRegistry"/>, so it is
    /// always the exact-case canonical spelling.  Empty for any type this feature does not
    /// support (currently only <c>azureservicebus</c>, which is rejected earlier and never
    /// reaches this lookup in practice).
    /// </summary>
    private static string[] GetSupportedEnvParts(string dependencyType) =>
        dependencyType switch
        {
            "postgres" or "mysql" or "sqlserver" or "mongodb" => s_dbKindParts,
            "rabbitmq" => s_rabbitmqParts,
            "nats" => s_natsParts,
            "redis" => s_redisParts,
            "kafka" or "elasticsearch" or "mailpit" or "dynamodb" or "minio" => s_hostPortOnlyParts,
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

            if (string.Equals(depSpec.Type, "azureservicebus", StringComparison.Ordinal))
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
        if (string.Equals(dependencyType, "mailpit", StringComparison.Ordinal))
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

        if (string.Equals(dependencyType, "dynamodb", StringComparison.Ordinal)
            || string.Equals(dependencyType, "minio", StringComparison.Ordinal))
        {
            // Both are plain containers (§4) whose Build lambda stages its single HTTP
            // endpoint into serviceEndpoints[name] for exactly this purpose — mirrors the
            // mailpit branch above, minus the "-smtp" suffix (one endpoint, not two).
            var http = serviceEndpoints[name];
            return new DependencyEnvAccess(
                FullConnection: BuildHostPortExpression(http),
                Host: http.Property(EndpointProperty.Host),
                Port: http.Property(EndpointProperty.Port),
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
    /// Escapes <c>{</c>/<c>}</c> in a literal span before it is passed to
    /// <see cref="ReferenceExpressionBuilder.AppendLiteral"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>BLOCKER fix (peer review, runtime-probed against the pinned Aspire 13.4.2 DLL):</b>
    /// <c>ReferenceExpressionBuilder.AppendLiteral</c> appends the literal text VERBATIM into
    /// the internal composite-format string that <see cref="ReferenceExpression.Build"/>later
    /// materialises via <c>string.Format</c> — it does NOT escape braces itself (unlike the
    /// compiler-generated interpolated-handler path used elsewhere in this file, e.g. the
    /// kafka schema-registry sidecar's <c>ReferenceExpression.Create($"...")</c>, which the C#
    /// compiler itself escapes via <c>EscapeUnescapedBraces</c>). Left un-escaped, an author's
    /// literal brace corrupts the result in one of two ways, both confirmed empirically:
    /// <list type="bullet">
    ///   <item><description>
    ///     A literal that happens to look like a format placeholder — e.g.
    ///     <c>env: { X: "a{0}b-${conn:pg.host}" }</c> — collides with the SAME placeholder
    ///     index <c>${conn:pg.host}</c> compiles to, so <c>string.Format</c> silently
    ///     substitutes the resolved host into BOTH the intended slot and the author's literal
    ///     text (<c>a{pg.bindings.tcp.host}b-{pg.bindings.tcp.host}</c>) — silent corruption,
    ///     no exception.
    ///   </description></item>
    ///   <item><description>
    ///     Any other unbalanced/non-index brace — e.g. a literal JSON value
    ///     <c>env: { CONFIG: '{"level":"debug"}' }</c>, or the shell/Make self-expansion idiom
    ///     <c>env: { P: "${DB_PASSWORD}" }</c> (not a <c>${conn:...}</c> reference at all) —
    ///     throws <c>FormatException</c> when Aspire materialises the env var during
    ///     <c>StartAsync</c>, which <c>SuiteTopology.StartAsync</c> wraps as
    ///     <see cref="Vouchfx.Engine.Orchestration.OrchestrationException"/> →
    ///     <c>Verdict.EnvironmentError</c> — the exact §12.1 misclassification the M2 fix
    ///     (ArgumentException → Inconclusive) targeted, just one layer further downstream.
    ///   </description></item>
    /// </list>
    /// Both failure modes pass <see cref="ValidateEnvValue"/> unchanged — it validates
    /// <c>${conn:...}</c>/<c>${secret:...}</c> shape, not brace balance in the surrounding
    /// literal text — so this is the only place the corruption can be prevented.
    /// </para>
    /// </remarks>
    private static string EscapeLiteralBraces(string literal) =>
        literal.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);

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
                    builder.AppendLiteral(EscapeLiteralBraces(token.Literal));
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
    /// <remarks>
    /// The string-literal branch escapes braces via <see cref="EscapeLiteralBraces"/> too
    /// (defensive: today's literals — mysql's fixed 'root' username, a database resource
    /// name — never contain braces, but this stays correct if that ever changes, and it keeps
    /// every <c>AppendLiteral</c> call site in this file consistent — see
    /// <see cref="EscapeLiteralBraces"/> for why an un-escaped literal corrupts the result).
    /// </remarks>
    private static void AppendPart(ReferenceExpressionBuilder builder, object part)
    {
        if (part is string literal)
            builder.AppendLiteral(EscapeLiteralBraces(literal));
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

    // -----------------------------------------------------------------------
    // feat/dependency-image-override — per-dependency image/registry/pull-policy overrides.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Applies a dependency's image/tag override (<see cref="DependencySpec.Image"/>, else
    /// <see cref="DependencySpec.Version"/>) plus the env-level <c>imageRegistry</c>/pull-policy
    /// overrides to a container-backed Aspire resource builder.  Collapses the
    /// <c>if (!string.IsNullOrEmpty(spec.Version)) ... WithImageTag(...)</c> duplication
    /// that used to appear in every one of the 13 dependency registrations, and additionally
    /// wires up <see cref="DependencySpec.Image"/>/<c>imageRegistry</c>/<c>pullPolicy</c> support.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Precedence (Map's eager validation block has already rejected both ambiguous
    /// combinations — an <c>image:</c> that already carries its own tag or digest together with
    /// a sibling <c>version:</c>, and a tagless/digestless <c>image:</c> with NO sibling
    /// <c>version:</c> either (the M3 fix — such a combination would float on <c>:latest</c>) —
    /// before <c>Configure</c> ever runs, so at most one of (embedded tag, embedded digest,
    /// <paramref name="spec"/>.Version) survives to reach here, and an image-with-neither-tag-
    /// nor-digest case reaching here always has a non-empty <paramref name="spec"/>.Version):
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <see cref="DependencySpec.Image"/> set → <c>WithImage(repository[, tag])</c> (a digest
    ///     routes to <c>WithImageSHA256</c> instead). When the image carries no tag/digest of its
    ///     own, <see cref="DependencySpec.Version"/> supplies the tag (Map's eager validation
    ///     guarantees it is present and non-empty in this case — see the M3 fix above).
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="DependencySpec.Image"/> unset, <see cref="DependencySpec.Version"/> set (and
    ///     non-empty — MN3 fix: an empty <c>version: ""</c> is treated identically to an absent
    ///     one, matching the <see cref="DependencySpec.Image"/>-set branch's own treatment) →
    ///     <c>WithImageTag(version)</c> (today's pre-existing behaviour, unchanged).
    ///   </description></item>
    ///   <item><description>
    ///     Neither set → this method makes no image/tag call at all, leaving whatever default
    ///     the resource's own <c>AddXxx</c>/<c>AddContainer</c> call already established (a
    ///     provider's built-in default, or this file's own pinned default tag for the
    ///     <c>AddContainer</c>-based kinds).
    ///   </description></item>
    /// </list>
    /// <para>
    /// <paramref name="imageRegistry"/> then applies via <c>WithImageRegistry</c> UNLESS the
    /// resource's CURRENT image annotation — after the tag/digest handling above — already names
    /// an explicit registry component (the same "first path segment contains '.'/':' or equals
    /// 'localhost'" rule <see cref="ResolveImage"/> already applies for services), in which case
    /// any pre-existing registry is explicitly CLEARED (<c>WithImageRegistry(null)</c>) instead.
    /// Two independent sources can supply that explicit registry component, and both are checked
    /// (M1/M2 fix — see the inline remarks at each check site below):
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="DependencySpec.Image"/> itself names one (e.g.
    ///     <c>"nexus.corp.local:5000/platform/mongo:8.0"</c>) — checked against the PARSED
    ///     <see cref="ImageReference.Repository"/>, and the registry is cleared UNCONDITIONALLY
    ///     whenever <see cref="DependencySpec.Image"/> is set at all (M2 fix: 'image:' always
    ///     means exactly what it says, never inheriting a provider's built-in registry default —
    ///     previously this clear only fired when the image ITSELF carried a registry, so an
    ///     unqualified sqlserver image silently inherited AddSqlServer's own "mcr.microsoft.com"
    ///     default and resolved to a path that does not exist).
    ///   </description></item>
    ///   <item><description>
    ///     No <see cref="DependencySpec.Image"/> override at all, but the CURRENT
    ///     <see cref="ContainerImageAnnotation.Image"/> the resource already carries embeds a
    ///     registry component directly in its image string rather than in the separate
    ///     <see cref="ContainerImageAnnotation.Registry"/> field (M1 fix — this is exactly the
    ///     azureservicebus shape: both its <c>AddContainer</c> calls pass
    ///     <c>"mcr.microsoft.com/..."</c> as the image argument itself, so
    ///     <c>ContainerImageAnnotation.Registry</c> stays null and a check against ONLY
    ///     <see cref="DependencySpec.Image"/> — which is null here — never fires; an env-level
    ///     <c>imageRegistry</c> would otherwise double-prefix
    ///     <c>"artifactory.example.com/mcr.microsoft.com/..."</c>).
    ///   </description></item>
    /// </list>
    /// <para>
    /// This matters because reflection against the pinned Aspire.Hosting.* 13.4.2 packages
    /// confirms every one of the 9 helper <c>AddXxx</c> calls (<c>AddPostgres</c>/
    /// <c>AddMongoDB</c>/...) ALREADY calls <c>WithImageRegistry</c> internally with its own
    /// built-in default ("docker.io" for most, "mcr.microsoft.com" for SqlServer) — and
    /// <c>WithImage</c> folds an embedded registry straight into the <c>Image</c> annotation
    /// field, never into the separate <c>Registry</c> field.
    /// <see cref="Aspire.Hosting.ApplicationModel.ResourceExtensions.TryGetContainerImageName"/>
    /// (the method that actually assembles the pull reference) unconditionally prepends
    /// <c>Registry + "/"</c> whenever <c>Registry</c> is non-null — so leaving either kind of
    /// pre-existing default in place would silently double-prefix the pull reference, even
    /// without an env-level <c>imageRegistry</c> at all, corrupting exactly the customer-Nexus/
    /// air-gapped scenarios this feature exists for. <paramref name="pullPolicy"/>, when supplied,
    /// always applies last via <c>WithImagePullPolicy</c>.
    /// </para>
    /// </remarks>
    private static IResourceBuilder<T> ApplyImageOverrides<T>(
        IResourceBuilder<T> builder,
        DependencySpec spec,
        string? imageRegistry,
        ImagePullPolicy? pullPolicy)
        where T : ContainerResource
    {
        var imageHasExplicitRegistry = false;

        // C4 fix: same empty-is-absent normalisation as Map()'s own eager validation loop above
        // (see its comment for the full rationale, including why this is IsNullOrEmpty and NOT
        // IsNullOrWhiteSpace) — both places must agree on what "no image" means, or a dependency
        // could pass eager validation as "absent" and yet still reach WithImage/WithImageSHA256
        // here with a degenerate ("") repository. A whitespace-only spec.Image can NEVER actually
        // reach this method via the real Map()→Configure() pipeline: Map()'s own eager loop
        // Parses the same value first and throws for whitespace there (see its comment), so
        // Configure — and therefore this method — never runs at all for that dependency
        // (Map_DependencyImage_WhitespaceOnly_ThrowsLikeMain proves it). The Parse call below is
        // defence-in-depth for any caller that reaches ApplyImageOverrides WITHOUT having gone
        // through Map()'s eager validation first (e.g. a future direct unit test of this method,
        // or a refactor that adds another call site) — not a live path within Map() itself.
        if (!string.IsNullOrEmpty(spec.Image))
        {
            var parsedImage = ImageReferenceParser.Parse(spec.Image);
            imageHasExplicitRegistry = HasExplicitRegistryComponent(parsedImage.Repository);

            if (parsedImage.Digest is not null)
            {
                // WithImageSHA256 stores its argument into ContainerImageAnnotation.SHA256
                // VERBATIM (no prefix handling at all — confirmed by decompiling Aspire.Hosting
                // 13.4.2), but TryGetContainerImageName reconstructs the pull reference as
                // "{Image}@sha256:{SHA256}" — i.e. SHA256 must be the BARE hex digest. This
                // mirrors exactly what WithImage's OWN embedded-digest handling does when an
                // image STRING carries a "name@sha256:..." suffix: strip the "sha256:" algorithm
                // prefix before storing. ImageReferenceParser.Digest always retains that prefix
                // (its own documented contract), so it must be stripped here too — passing it
                // through unstripped would double the prefix.
                //
                // MINOR-2 fix: the prefix is no longer VALIDATED here — Map()'s eager
                // dependency-validation loop (before Configure is ever built) already rejects any
                // spec.Image whose digest lacks the 'sha256:' prefix, and every call site below
                // is reached only from inside that same Configure closure, always after Map()'s
                // loop has already validated this exact DependencySpec. Re-checking here would
                // just be dead code on the only path that reaches it.

                var bareDigest = parsedImage.Digest["sha256:".Length..];
                builder = builder.WithImage(parsedImage.Repository).WithImageSHA256(bareDigest);
            }
            else
            {
                // MN3 fix: normalise 'version: ""' to "no version" here too, matching the guard
                // the Image-unset branch below has always used — previously this line used
                // spec.Version raw, so 'image: myrepo/mongo' + 'version: ""' set Tag = "" (an
                // empty-but-non-null tag) while the SAME image with 'version' entirely absent set
                // Tag = null (→ WithImage(repository) → Aspire defaults it to "latest"). Both now
                // resolve identically. IsNullOrEmpty deliberately, not IsNullOrWhiteSpace — a
                // whitespace-only 'version: "   "' is real text the author wrote and still
                // becomes the literal tag, unchanged from origin/main (see the REJECTED note on
                // the Image guard in Map()'s eager validation loop for why widening this was
                // reverted).
                var effectiveVersion = string.IsNullOrEmpty(spec.Version) ? null : spec.Version;
                var tag = parsedImage.Tag ?? effectiveVersion;
                builder = tag is not null
                    ? builder.WithImage(parsedImage.Repository, tag)
                    : builder.WithImage(parsedImage.Repository);
            }

            // M2 fix: clear the registry UNCONDITIONALLY whenever 'image:' is set — regardless of
            // whether the author's own image string carries an explicit registry component.
            // Previously this only ran inside `if (imageHasExplicitRegistry)`, so an UNQUALIFIED
            // image (e.g. sqlserver + 'image: myorg/mssql-mirror:2022') left the provider's own
            // built-in registry default in place — harmless for the 12 kinds whose default is
            // "docker.io" (the implicit default anyway), but for sqlserver AddSqlServer's own
            // default is "mcr.microsoft.com", so the customer's own mirror image silently
            // resolved to "mcr.microsoft.com/myorg/mssql-mirror:2022" — a path that does not
            // exist. Net rule: 'image:' always means exactly what it says, optionally re-prefixed
            // by the env-level imageRegistry check below (which still consults
            // imageHasExplicitRegistry to decide whether re-applying imageRegistry on top would
            // double-prefix an image that names its OWN registry, e.g. the Nexus scenario).
            builder = builder.WithImageRegistry(null);
        }
        else
        {
            // MN3 fix: same empty-string normalisation as the Image-set branch above. IsNullOrEmpty
            // deliberately, not IsNullOrWhiteSpace: a whitespace-only 'version:' is real text the
            // author wrote and becomes the literal tag, unchanged from origin/main.
            if (!string.IsNullOrEmpty(spec.Version))
            {
                builder = builder.WithImageTag(spec.Version);
            }

            // M1 fix: no 'image:' override was supplied, so nothing above touched the registry —
            // but the resource's CURRENT image annotation (whatever its own AddXxx/AddContainer
            // registration already established) may still embed a registry component directly in
            // its Image string rather than in the separate Registry field. This is exactly the
            // azureservicebus shape: both its AddContainer calls
            // (AddContainer(name, "mcr.microsoft.com/azure-messaging/servicebus-emulator", ...) and
            // the SQL Edge sidecar's AddContainer(sidecarName, "mcr.microsoft.com/mssql/server", ...))
            // pass the registry embedded in the image argument itself, so
            // ContainerImageAnnotation.Registry is null and — before this fix — the check below
            // (which only ever inspected DependencySpec.Image, itself null here) never caught it,
            // so an env-level imageRegistry unconditionally applied via WithImageRegistry and
            // double-prefixed the pull reference
            // ("artifactory.example.com/mcr.microsoft.com/azure-messaging/servicebus-emulator:1.1.2").
            // Reading the CURRENT annotation (rather than re-deriving it from a raw image string
            // this method never receives here) is the most robust fix: it works uniformly for
            // every dependency kind and every AddXxx/AddContainer registration without needing to
            // touch — or keep in sync with — each registration's own hardcoded image literal.
            var currentImage = builder.Resource.Annotations
                .OfType<ContainerImageAnnotation>()
                .LastOrDefault();
            if (currentImage is not null)
            {
                imageHasExplicitRegistry = HasExplicitRegistryComponent(currentImage.Image);
            }
        }

        if (!string.IsNullOrEmpty(imageRegistry) && !imageHasExplicitRegistry)
        {
            builder = builder.WithImageRegistry(imageRegistry);
        }

        if (pullPolicy is not null)
        {
            builder = builder.WithImagePullPolicy(pullPolicy.Value);
        }

        return builder;
    }

    /// <summary>
    /// Applies ONLY the env-level <c>imageRegistry</c>/pull-policy overrides to a sidecar
    /// container that has no independent per-dependency image identity of its own — the kafka
    /// schema-registry sidecar and the azureservicebus SQL Edge sidecar (§ item 6: sidecars are
    /// deliberately out of scope for <see cref="DependencySpec.Image"/>; see the comments at
    /// their call sites). Never consults <see cref="DependencySpec.Image"/>: the author cannot
    /// name these containers even after feat/dependency-image-override. <paramref name="imageRegistry"/>
    /// and <paramref name="pullPolicy"/> are broad, environment-level policies rather than
    /// per-dependency image identity, so they still apply uniformly here — an air-gapped
    /// customer's <c>imagePullPolicy: Never</c> must reach every container, sidecars included.
    /// </summary>
    /// <remarks>
    /// M1 fix: <paramref name="imageRegistry"/> is skipped when the sidecar's OWN hardcoded image
    /// literal already embeds a registry component — the azureservicebus SQL Edge sidecar is
    /// registered as <c>AddContainer(sidecarName, "mcr.microsoft.com/mssql/server", "2022-latest")</c>,
    /// so <c>ContainerImageAnnotation.Registry</c> is null (nothing for a naive
    /// "spec.Image only" check to see) while <c>ContainerImageAnnotation.Image</c> already carries
    /// "mcr.microsoft.com/...". Applying <paramref name="imageRegistry"/> unconditionally on top
    /// would double-prefix the pull reference
    /// ("artifactory.example.com/mcr.microsoft.com/mssql/server:2022-latest") — exactly the same
    /// failure mode <see cref="ApplyImageOverrides{T}"/>'s M1 fix addresses for the emulator
    /// container itself, checked the same way (the CURRENT annotation, not a raw image string this
    /// method never receives). The kafka schema-registry sidecar's own image
    /// ("confluentinc/cp-schema-registry") carries no such component, so imageRegistry still
    /// applies to it exactly as before.
    /// </remarks>
    private static IResourceBuilder<T> ApplySidecarRegistryAndPullPolicy<T>(
        IResourceBuilder<T> builder,
        string? imageRegistry,
        ImagePullPolicy? pullPolicy)
        where T : ContainerResource
    {
        if (!string.IsNullOrEmpty(imageRegistry))
        {
            var currentImage = builder.Resource.Annotations
                .OfType<ContainerImageAnnotation>()
                .LastOrDefault();
            var imageHasExplicitRegistry =
                currentImage is not null && HasExplicitRegistryComponent(currentImage.Image);

            if (!imageHasExplicitRegistry)
            {
                builder = builder.WithImageRegistry(imageRegistry);
            }
        }

        if (pullPolicy is not null)
        {
            builder = builder.WithImagePullPolicy(pullPolicy.Value);
        }

        return builder;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="repository"/>'s first slash-delimited
    /// component is an explicit registry host — contains a <c>.</c> or a <c>:</c> (a port), or
    /// equals <c>"localhost"</c> — the same heuristic <see cref="ResolveImage"/> already applies
    /// for services (and that <c>OrchestrationErrorClassifier.ParseRegistryHost</c> and
    /// <see cref="ImageReferenceParser"/>'s own header remarks independently document). Callers
    /// pass either an ALREADY tag/digest-stripped <see cref="ImageReference.Repository"/> (from a
    /// parsed <see cref="DependencySpec.Image"/>), or a resource's CURRENT
    /// <see cref="ContainerImageAnnotation.Image"/> (the M1 fix — Aspire's own annotation shape
    /// keeps <c>Image</c> tag/digest-free the same way, so both are equally valid inputs here).
    /// </summary>
    private static bool HasExplicitRegistryComponent(string repository)
    {
        var slashIndex = repository.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex < 0)
        {
            return false;
        }

        var firstComponent = repository[..slashIndex];
        return firstComponent.Contains('.', StringComparison.Ordinal) ||
               firstComponent.Contains(':', StringComparison.Ordinal) ||
               string.Equals(firstComponent, "localhost", StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses an author-supplied <c>imagePullPolicy</c> string (env-level or service-level) into
    /// Aspire's <see cref="ImagePullPolicy"/> enum, matching the exact author-facing casing.
    /// </summary>
    /// <param name="value">The raw string as authored, e.g. <c>"Missing"</c>.</param>
    /// <param name="subject">
    /// A human-readable description of where the value came from (e.g. <c>"The 'imagePullPolicy'
    /// on service 'web'"</c>), spliced into the exception message so the author can find it.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is not one of the three author-facing values the JSON Schema
    /// accepts (<c>Always</c>, <c>Missing</c>, <c>Never</c>) — this rejects both genuinely
    /// unrecognised strings and Aspire's own internal-only <see cref="ImagePullPolicy.Default"/>
    /// enum member, which is not a value an author ever writes in YAML. Pre-GA decision
    /// (feat/case-sensitive-kinds): matching is case-sensitive — the JSON Schema enum already
    /// only ever listed the exact-case forms below, so a value differing only by case (e.g.
    /// <c>"always"</c>) is rejected identically to a genuinely unrecognised one.
    /// </exception>
    private static ImagePullPolicy ParseImagePullPolicy(string value, string subject)
    {
        if (Enum.TryParse<ImagePullPolicy>(value, ignoreCase: false, out var parsed) &&
            parsed != ImagePullPolicy.Default)
        {
            return parsed;
        }

        throw new ArgumentException(
            $"{subject} has an unrecognised value '{value}'. Accepted values: Always, Missing, Never.",
            nameof(value));
    }
}
