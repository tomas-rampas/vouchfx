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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
    IReadOnlyList<string> DependencyNames)
{
    /// <summary>
    /// A LIVE view of the endpoint <see cref="ResolveServices"/> will read from, per staged
    /// <c>svc::&lt;name&gt;</c> key — empty until <see cref="Configure"/> has run, because that is
    /// when the resources it references are built.
    /// <para>
    /// NOT one entry per declared service: several dependency kinds stage a <c>svc::</c> key of
    /// their own here too, so a name in this dictionary is not necessarily an
    /// <c>environment.services</c> entry. Grep this file for assignments into
    /// <c>serviceEndpoints</c> to see the current set, rather than trusting a list here — an
    /// enumeration in this position was wrong on every name it gave, within one revision of
    /// being written.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>internal</c>, and a test seam by intent. The staging decision itself — WHICH endpoint a
    /// service resolves to — is otherwise unobservable without Docker: <see cref="ResolveServices"/>
    /// reads <c>EndpointReference.Url</c>, which throws until <c>StartAsync</c> has allocated a
    /// host port. Tests previously had to settle for proving the endpoint EXISTS on the resource,
    /// which cannot distinguish "the mapper staged the right one" from "the mapper staged nothing
    /// at all" — precisely the defect #348 was, undetected, for the whole project-form branch.
    /// </para>
    /// </remarks>
    internal IReadOnlyDictionary<string, EndpointReference> StagedServiceEndpoints { get; init; }
        = new Dictionary<string, EndpointReference>(StringComparer.Ordinal);

    /// <summary>
    /// Author-facing notices raised while <see cref="Configure"/> selected endpoints — today, the
    /// transport downgrade a `project:`-form service declaring BOTH an http and an https endpoint
    /// incurs. Empty until <see cref="Configure"/> has run, and empty for almost every suite.
    /// </summary>
    /// <remarks>
    /// Surfaced rather than logged because the mapper has no writer and must not acquire one:
    /// <c>SuiteTopology</c> republishes this and the runner prints it, exactly as it already does
    /// for <c>SecurityConfirmations</c>. Not terminal-only: the runner also maps this collection
    /// onto the frozen §14 <c>transport-notice</c> record, through the single producer
    /// <c>Vouchfx.Engine.Runtime.TransportNoticeEvents</c>. That still happens over there — this
    /// mapper has no event destination and must not acquire one.
    /// </remarks>
    internal IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices { get; init; }
        = Array.Empty<EndpointSelectionNotice>();

    /// <summary>
    /// Author-facing notices that a service's staged address resolves to an https listener the
    /// engine configures no client trust material for — whether it was named by the service's
    /// <c>endpoint:</c> or chosen by the engine's own rule. Empty until <see cref="Configure"/>
    /// has run, and empty for almost every suite.
    /// </summary>
    /// <remarks>
    /// A second list rather than a second case of <see cref="EndpointSelectionNotices"/> — see
    /// <see cref="EndpointTrustNotice"/>'s own header for the reason, which is that the two
    /// records carry different fields, not merely different wording. Surfaced and printed through
    /// exactly the same channel.
    /// </remarks>
    internal IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices { get; init; }
        = Array.Empty<EndpointTrustNotice>();
}

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
                        var srName = KafkaSchemaRegistryServiceName(name);
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
                        gates.Add(KafkaSchemaRegistryServiceName(name));
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
                    serviceEndpoints[MailpitSmtpServiceName(name)] = containerBuilder.GetEndpoint("smtp");

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
    /// <param name="suiteDirectory">
    /// The directory containing the suite's own <c>.e2e.yaml</c> file — the base every declared
    /// <c>security.serverArtifacts[].source</c> resolves against (REQ-003/REQ-016). Defaults to
    /// <see cref="Directory.GetCurrentDirectory"/>, mirroring
    /// <see cref="SuiteTopology.StartAsync"/>'s own <c>seedBaseDirectory</c> default.
    /// <para>
    /// It MUST be the same base <c>EnvironmentSecurityValidator</c> checked containment against.
    /// A path resolved against a DIFFERENT base is still contained within THAT base, so
    /// containment cannot detect the divergence — the run would simply copy a file the suite
    /// never named. Callers pass the first scenario's own directory, which is also the directory
    /// the one shared topology's <c>environment</c> block came from.
    /// </para>
    /// </param>
    /// <param name="kafkaSpeakingTargets">
    /// The declared target names the suite's own steps address with <c>mq-publish.kafka</c> /
    /// <c>mq-expect.kafka</c>, as computed by <see cref="SuiteProtocolTargets"/>. REQ-023 (as
    /// amended 2026-08-04) makes this the discriminator for the FORM a service's endpoint is
    /// staged in: a target the Kafka families address is staged as the bare bootstrap authority
    /// those clients expect, and every other service keeps its scheme-carrying URL. See
    /// <c>StageServiceEndpoint</c> for why the form — not merely the confirmation level — has to
    /// follow the protocol.
    /// <see langword="null"/> means "no Kafka step targets anything here", which is correct for a
    /// suite with no Kafka steps and for every caller that predates this parameter.
    /// </param>
    /// <param name="endpointConsumingTargets">
    /// The declared target names at least one step will read a STAGED ENDPOINT for, as computed by
    /// <see cref="SuiteProtocolTargets.EndpointConsuming(IEnumerable{Vouchfx.Engine.Authoring.Ast.ScenarioAst})"/>
    /// — a SUPERSET of <paramref name="kafkaSpeakingTargets"/> (it is the union of that set with
    /// the HTTP-family one). Threaded alongside it, from the same call sites, off the same
    /// scenarios; the two must always be derived from one input, or they can disagree about what
    /// the suite addresses.
    /// <para>
    /// Consulted for exactly one decision (#348): whether a <c>project:</c>-form service that
    /// declares no endpoint is a fault or a legitimate worker service. Named by a step → refused
    /// with an author-facing diagnostic; not named → left unstaged and otherwise untouched, which
    /// is what a <c>BackgroundService</c> consuming a queue has always needed and still gets.
    /// </para>
    /// <para>
    /// <see langword="null"/> means "no step reads a staged endpoint for anything here", the
    /// PERMISSIVE answer — chosen deliberately so a direct <see cref="EnvironmentSpec"/> embedder
    /// that passes nothing gets today's behaviour rather than a refusal it has no way to act on.
    /// </para>
    /// </param>
    /// <returns>
    /// A <see cref="MappedTopology"/> whose <see cref="MappedTopology.Configure"/> callback
    /// is safe to invoke against any <see cref="IDistributedApplicationBuilder"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown by <b>every</b> eager, pre-<see cref="MappedTopology.Configure"/> authoring check in
    /// this method — service and dependency shape, image and version resolution,
    /// <c>imagePullPolicy</c>, <c>security.endpoint</c> and health-check port selection, the
    /// service and dependency <c>env:</c> reference rules, the dependency engine-set-name refusal
    /// (REQ-004, see <see cref="s_engineSetEnvKeys"/>), and server-artefact resolution (REQ-016).
    /// <para>
    /// That is stated as a property rather than enumerated deliberately. An earlier revision of
    /// this tag listed eight causes "in the order the passes run"; the list was short by five and
    /// the ordering claim was wrong (env-level <c>imagePullPolicy</c> parses BEFORE both
    /// <c>env:</c> passes, not after them). A closed-looking enumeration on a contract
    /// <c>ScenarioRunner</c> catches against is worse than no enumeration, because it invites a
    /// reader to treat an absent cause as impossible — and nothing makes the list go red when a
    /// ninth check is added. If you need the current set, grep for <c>throw new
    /// ArgumentException</c> in this method AND in the validators it calls — <c>ValidateEnvValue</c>
    /// and <c>ParseImagePullPolicy</c> here, and <c>ServerArtifactInjection</c> in its own file.
    /// This method's own body raises none of the <c>env:</c>-reference, <c>imagePullPolicy</c> or
    /// server-artefact faults directly, so grepping it alone would miss three of the categories
    /// named above.
    /// </para>
    /// Every one of these is an authoring fault, which is why <c>ScenarioRunner</c> classifies an
    /// <see cref="ArgumentException"/> out of this method as Inconclusive rather than
    /// EnvironmentError (§12.1).
    /// <para>
    /// <strong>Some authoring faults are NOT raised eagerly, and they are the exception to the
    /// "every eager check" framing above rather than members of it.</strong> The property they
    /// share is that only Aspire can decide them, and only after it has built the resource — what
    /// endpoints a launch profile produces is the standing example (#348). Those are refused from
    /// inside the <see cref="MappedTopology.Configure"/> closure and throw
    /// <see cref="TopologyAuthoringException"/> — an <see cref="ArgumentException"/> subclass, so
    /// the classification above still holds — which <see cref="SuiteTopology.StartAsync"/>
    /// re-throws unwrapped for exactly that reason. For the current set, grep
    /// <c>throw new TopologyAuthoringException</c>; a roster written out here goes stale, which is
    /// how this paragraph went wrong once already. Every OTHER throw from that closure is an
    /// engine defect or an infrastructure fault and is correctly wrapped as an
    /// <see cref="OrchestrationException"/>.
    /// </para>
    /// </exception>
    public static MappedTopology Map(
        EnvironmentSpec? env,
        string? suiteDirectory = null,
        IReadOnlySet<string>? kafkaSpeakingTargets = null,
        IReadOnlySet<string>? endpointConsumingTargets = null)
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

        // REQ-023 (amended): captured once here rather than read through the nullable parameter
        // inside the resolver closure, so the staging rule is a plain set lookup at the one place
        // it is applied.
        var kafkaTargets = kafkaSpeakingTargets ?? EmptyProtocolTargets;

        // Transport-downgrade notices raised while Configure runs (security review). Populated
        // inside the closure, read after it — same lifecycle as serviceEndpoints, and the same
        // reason it cannot be computed eagerly: it depends on endpoints Aspire discovers.
        var endpointSelectionNotices = new List<EndpointSelectionNotice>();

        // The OTHER endpoint advisory, and a separate list because it is a separate record with a
        // separate field set (see EndpointTrustNotice's own header for why it is not a
        // discriminated case of the one above): a staged address that resolves to an https
        // listener the engine holds no trust material for. Same lifecycle, same reason.
        var endpointTrustNotices = new List<EndpointTrustNotice>();

        // #348: same treatment, same reason — captured once here so the endpoint-less project-form
        // refusal inside the Configure closure is a plain set lookup. Empty is the PERMISSIVE
        // default (nothing is targeted, so nothing is refused); see the parameter's own remarks.
        var endpointTargets = endpointConsumingTargets ?? EmptyProtocolTargets;

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

            // ── REQ-006 (SERVICE-FORM FIELDS): the two shapes where a field is meaningless on
            //    the form it sits on ──
            //
            // The qualifier is not decorative: this file carries a SECOND, unrelated REQ-006 —
            // the `${env:NAME}` passthrough further down (s_envRefPattern and its call sites),
            // from a different requirement set. A bare "REQ-006" grep over this file returns
            // both, so neither may be labelled with the bare id alone.
            //
            // Both shapes are already refused by $defs/service (the `image`/`endpoint` clause and
            // the project-form clause that forbids `httpPort`). These are the braces to those
            // belts, in the same relationship every other check in this loop has with its own
            // schema counterpart. ONE AUTHOR-REACHABLE PATH reaches this
            // method with no schema in front of it, and it is MEASURED, not theoretical: `--watch`,
            // whose compile seam is YamlDocumentParser.Parse + AstBuilder.Build with no
            // DocumentValidator.Validate call anywhere in it, and which then reaches here through
            // SuiteTopology.StartAsync (#370). An in-repo caller constructing an EnvironmentSpec
            // directly arrives the same way, which is how the checks below are pinned — see
            // EnvironmentMapperTests' "REQ-006 (service-form fields)" block.
            //
            // Without these two checks, that path accepts the author's field and silently drops
            // it — the accepted-and-ignored shape #448 exists to end, reproduced one layer down.
            // Eager, before any builder mutation, like every other check here.

            // `endpoint:` SELECTS AMONG ENDPOINTS THE ENGINE DISCOVERED, and an image-form service
            // has none to select among: its endpoint set is one the engine NAMES itself from the
            // service's own declaration (ServiceEndpointNaming.PlaintextEndpoints), rather than one
            // it discovers. So the field cannot mean anything here, whatever it says.
            //
            // `is not null`, not `is { }`: nothing needs the value bound in order to refuse the
            // field.
            if (spec.Image is not null && spec.Endpoint is not null)
            {
                throw new ArgumentException(
                    $"Service '{name}' declares both 'image' and 'endpoint'. 'endpoint' names one " +
                    "of the endpoints discovered from a 'project'-form service's launch profile, " +
                    "and an image-form service has none to name: the engine names its endpoints " +
                    "itself, from this service's own declaration. Remove the 'endpoint' line and " +
                    "select the port through 'httpPort', 'ports' or 'security.endpoint' instead.",
                    nameof(env));
            }

            // `httpPort` NEVER DID ANYTHING ON A PROJECT-FORM SERVICE — this is not a field being
            // narrowed away from some previous meaning. The services loop below reads it only for
            // an image-form service; a project-form one goes to Aspire's own AddProject, which
            // discovers the project's launch-profile endpoints and never consults this value. It
            // was accepted and silently ignored, which is precisely why refusing it is the fix and
            // there is no behaviour to preserve.
            //
            // The value IS bound, unlike the check above, because it is quoted back: an author
            // scanning a suite for `httpPort: 8080` finds the line from the message, and the
            // 'ports'-pinning sibling further down states its own port the same way.
            if (spec.Project is not null && spec.HttpPort is { } projectFormHttpPort)
            {
                throw new ArgumentException(
                    $"Service '{name}' declares 'httpPort: {projectFormHttpPort}' on a " +
                    "'project'-form service, where it has never had any effect: a project's " +
                    "endpoints are discovered from its own launch profile, never declared here, " +
                    "so the engine reads 'httpPort' for an 'image'-form service only. Remove the " +
                    "line. If the intent was to choose WHICH of the project's endpoints this " +
                    "service is addressed on, that is 'endpoint:', which names a listener rather " +
                    "than a port.",
                    nameof(env));
            }

            // ── REQ-023: the secured endpoint ────────────────────────────────────────────
            // Validated eagerly, before any builder mutation, for the same reason every other
            // check in this loop is: an unresolvable selector must fail with a located,
            // author-facing diagnostic naming what IS declared, never deep inside Aspire's own
            // GetEndpoint or — far worse — silently, by leaving the service reachable over
            // plaintext while its suite believes it is secured.
            if (spec.Security is not null)
            {
                if (spec.Project is not null)
                {
                    // A project-form service's endpoints come from the project's own launch
                    // profile, which this engine neither models nor names AT AUTHORING TIME (see
                    // ServiceEndpointNaming.DeclaredEndpointNames' own remarks — it returns an
                    // EMPTY list for a project-form service), so there is no endpoint HERE, in
                    // Map's eager pass, for REQ-023 to construct with an https scheme. Failing
                    // loudly at topology-build time is the only honest option: the alternative is
                    // a suite that validates, starts, and then presents no client certificate to
                    // anything.
                    //
                    // The services loop below DOES stage a svc::<name> value for a project-form
                    // service (#348), by reading the endpoints off the resource Aspire has just
                    // built — but that happens inside the Configure closure, later, and it can
                    // only report which endpoints Aspire discovered, never make one https. This
                    // refusal is unaffected by it.
                    throw new ArgumentException(
                        $"Service '{name}' declares 'security' on a 'project'-form service, which this " +
                        "release cannot secure: a project-form service's endpoints are discovered from " +
                        "the project's own launch profile, so the engine has no endpoint to construct " +
                        "with an 'https' scheme. Declare the system under test as an 'image'-form " +
                        "service to use 'security'.",
                        nameof(env));
                }

                if (ServiceEndpointNaming.ResolveSecuredPort(spec) is null)
                {
                    var declaredEndpoints = ServiceEndpointNaming.PlaintextEndpoints(spec);
                    var described = declaredEndpoints.Count == 0
                        ? "(none)"
                        : string.Join(", ", declaredEndpoints.Select(e => $"{e.Name} (port {e.Port})"));
                    throw new ArgumentException(
                        $"Service '{name}' declares 'security.endpoint: {spec.Security.Endpoint}', which is " +
                        "neither a port number (a bare decimal integer in 1..65535 with no leading zero) " +
                        $"nor the name of an endpoint this service declares. Declared endpoints: {described}.",
                        nameof(env));
                }
            }

            // REQ-025: an `httpPort` may not name a container port that `ports:` has PINNED.
            // Both produce an endpoint declaration on that port, so the pin has two candidates
            // and no rule distinguishes them: publishing both on one host port is impossible,
            // and publishing one silently is the arbitrary choice this engine does not make.
            // Refused eagerly, before any builder mutation, like every other cross-field check
            // in this loop — and it narrows nothing that ever worked, because pinning is new.
            if (spec.Image is not null
                && spec.HttpPort is { } pinnedHttpPortCandidate
                && spec.PinnedHostPorts is { } declaredPins
                && declaredPins.TryGetValue(pinnedHttpPortCandidate, out var pinnedFor))
            {
                throw new ArgumentException(
                    $"Service '{name}' declares 'httpPort: {pinnedHttpPortCandidate}' and also pins that " +
                    $"same container port in 'ports' (to host port {pinnedFor}). One container " +
                    "port cannot be declared twice. Remove the 'httpPort', or pin a different " +
                    "container port.",
                    nameof(env));
            }

            // REQ-009: cross-referencing healthCheck.port against the service's OWN declared
            // ports/httpPort is this mapper's job, not the schema's (see HealthCheckSpec's own
            // remarks) — mirrors the ${conn:name} referencing an unknown dependency precedent.
            // Validated eagerly, before any builder mutation, so a bad reference fails with a
            // clear, located diagnostic instead of deep inside Aspire's own GetEndpoint.
            //
            // G-M2 (gatekeeper): gated on 'spec.Image is not null' — 'healthCheck'/'ports' are
            // meaningless on a project-form service; the services loop below never reads
            // either field once 'spec.Project is not null' (Aspire's own AddProject
            // auto-discovers the project's launch-profile endpoints instead), so this
            // cross-reference check must not run for one either. Without this guard, a
            // project-form service that ALSO (uselessly) declared an inapplicable healthCheck
            // could hit the ports-shaped diagnostics below with author-facing advice
            // ("declare it under 'ports:'") that makes no sense for a service with no
            // 'ports:'/'httpPort:' behaviour at all. $defs/service's own schema now rejects
            // 'ports'/'healthCheck' on a project-form service outright (belt); this mapper-
            // level skip is the brace — still correct for a direct EnvironmentSpec embedding
            // that bypasses the schema (exactly the same belt-and-braces relationship every
            // other eager check in this loop has with its own schema-level counterpart).
            if (spec.Image is not null && spec.HealthCheck is { } healthCheck)
            {
                var isTcp = string.Equals(healthCheck.Type, "tcp", StringComparison.Ordinal);
                var isHttp = string.Equals(healthCheck.Type, "http", StringComparison.Ordinal);

                if (!isTcp && !isHttp)
                {
                    throw new ArgumentException(
                        $"Service '{name}' declares 'healthCheck.type' = " +
                        $"'{healthCheck.Type}', which is not recognised. Supported values: " +
                        "tcp, http.",
                        nameof(env));
                }

                if (isTcp)
                {
                    if (healthCheck.Port is not { } tcpPort)
                    {
                        throw new ArgumentException(
                            $"Service '{name}' declares 'healthCheck: {{ type: tcp }}' with no " +
                            "'port' field. A tcp health check must name the declared port to " +
                            "probe.",
                            nameof(env));
                    }

                    // m3 fix (fix round 3): for the HYBRID shape (ports + a sibling
                    // httpPort), declaredPorts previously omitted httpPort entirely — so a
                    // hybrid service could never tcp-probe its own httpPort: the port number
                    // was never "among the service's declared ports" by this check's own
                    // reckoning, even though it IS one of the service's declared ports (just
                    // declared via the httpPort field rather than the ports list). The
                    // resulting diagnostic then told the author to do the impossible —
                    // "declare it under 'ports:'" — when they had already declared it, as
                    // httpPort, and adding the SAME container port again under 'ports:' would
                    // double-declare it under two different endpoint names. ApplyHealthCheck's
                    // own endpoint-resolution fallback (below, in this same file) already
                    // handles a tcpPort that is the service's httpPort rather than a 'ports:'
                    // entry — resolving to the "http" endpoint — so admitting httpPort here is
                    // the only change needed: the previously-dead branch becomes reachable and
                    // already does the right thing.
                    //
                    // REQ-023: derived from ServiceEndpointNaming's own endpoint set rather
                    // than re-deriving the ports/httpPort shape here, so a service's SECURED
                    // port (which may be declared only by 'security.endpoint') is admitted
                    // too. For a service with no 'security' block the set is identical, port
                    // for port and in the same order, to the three-way expression this
                    // replaced — the hybrid list, the ports-only list, and the
                    // 'httpPort ?? 80' singleton.
                    var declaredPorts = ServiceEndpointNaming.EndpointDeclarations(spec)
                        .Select(e => e.Port)
                        .ToList();

                    if (!declaredPorts.Contains(tcpPort))
                    {
                        throw new ArgumentException(
                            $"Service '{name}' declares 'healthCheck: {{ type: tcp, port: " +
                            $"{tcpPort} }}', but {tcpPort} is not among the service's declared " +
                            $"ports ({string.Join(", ", declaredPorts)}). Declare it under " +
                            "'ports:' (or as 'httpPort' when the service has no 'ports:' at " +
                            "all) first.",
                            nameof(env));
                    }
                }
                else if (spec.Ports is { Count: > 0 } && spec.HttpPort is null)
                {
                    // 'type: http' targets the service's "http" endpoint, which exists when
                    // 'ports:' is absent (the implicit default) OR when 'httpPort' is
                    // explicitly declared alongside 'ports:' (the opt-in hybrid shape) — never
                    // when 'ports:' is declared with no sibling 'httpPort:' at all.
                    throw new ArgumentException(
                        $"Service '{name}' declares 'healthCheck: {{ type: http }}' but has no " +
                        "HTTP endpoint — it declares 'ports:' with no sibling 'httpPort:'. Add " +
                        "'httpPort:' to expose an HTTP endpoint for this health check to probe, " +
                        "or declare a 'tcp' health check against one of the declared 'ports:' " +
                        "instead.",
                        nameof(env));
                }
                else if (!ServiceEndpointNaming.EndpointDeclarations(spec).Any(
                             e => string.Equals(
                                 e.Name, ServiceEndpointNaming.HttpEndpointName, StringComparison.Ordinal)))
                {
                    // REQ-023: this service has no "http" endpoint for the check to probe. TWO
                    // distinct shapes reach here and the diagnostic must not assert the wrong
                    // one (the message previously claimed the HTTP port "is also" the
                    // security.endpoint, which is simply false for the second):
                    //
                    //   (a) the service declares an HTTP port that IS the secured port, so the
                    //       "http" endpoint was replaced by the secured "https" one; or
                    //   (b) the service declares `security` and no `httpPort`/`ports` at all,
                    //       so the implicit plaintext HTTP endpoint is suppressed outright
                    //       (PlaintextEndpoints' own REQ-023 rule) and there never was one.
                    //
                    // Either way an http health check against the mTLS listener could not pass:
                    // a container health check cannot present a client certificate (measured on
                    // the test bed — the listener answers 400 to an unauthenticated request),
                    // which is exactly why REQ-005's engine-side probe exists separately from
                    // health gating. The message names what the service actually declares
                    // rather than assuming shape (a).
                    var securedPort = ServiceEndpointNaming.ResolveSecuredPort(spec);
                    var cause = spec.HttpPort is { } declaredHttpPort && declaredHttpPort == securedPort
                        ? "its 'httpPort' is also its 'security.endpoint', so its only endpoint on that " +
                          "port is the secured one"
                        : "it declares 'security' with no separate 'httpPort', so it has no plaintext " +
                          "HTTP endpoint at all";

                    throw new ArgumentException(
                        $"Service '{name}' declares 'healthCheck: {{ type: http }}' but {cause}. A health " +
                        "check cannot present a client certificate; declare a 'tcp' health check against " +
                        "the secured port, or expose a separate unsecured health port under 'ports:' (or " +
                        "'httpPort:') and probe that.",
                        nameof(env));
                }
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
            // Why "" is absorbed here while "   " (below) is rejected, though both can come from
            // the same CI-templating accident: "" has a CONTRACT-level meaning — empty means
            // absent, matching Version's MN3 guard and the schema's own dangling-key promise —
            // whereas whitespace-only has none and is indistinguishable from a typo, so it gets
            // the loud rejection.
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
        // rejected outright (§17 — a secret is never baked into a container's environment,
        // whatever moment it would resolve at).  Validating before any builder mutation keeps Map() eager,
        // consistent with the two loops above.
        foreach (var (serviceName, spec) in env.Services ?? new Dictionary<string, ServiceSpec>())
        {
            if (spec.Env is null)
            {
                continue;
            }

            foreach (var (envKey, envValue) in spec.Env)
            {
                ValidateEnvValue(
                    "Service",
                    serviceName,
                    envKey,
                    envValue,
                    env.Dependencies ?? new Dictionary<string, DependencySpec>(),
                    connectionReferencesSupported: true);
            }
        }

        // dependency-env REQ-003: the SAME eager pass for every managed dependency's `env:`, in
        // the same place and on the same terms, because ApplyEnv and ValidateEnvValue are
        // UNCONNECTED paths — ApplyEnv calls BuildEnvExpression directly and never reaches this
        // method, so wiring only the apply path would ship both refusals silently absent.
        //
        // Two rules differ from the service arm, and both are refusals rather than resolutions:
        //   * `${conn:...}` is refused outright (decision 2) — a dependency is a connection
        //     SOURCE, not a consumer.
        //   * `${secret:...}` is refused for the service's own reason (§17): a container's
        //     environment is the wrong PLACE for a secret, because anyone who can run
        //     `docker inspect` reads it. Without this check the sigil matches NEITHER token
        //     pattern, so the value becomes a Literal and the raw text `${secret:vault/db/pw}`
        //     is written into the container verbatim — a green suite in which the secret was
        //     never delivered.
        // `${env:NAME}` behaves exactly as it does for a service, from this same pass.
        //
        // Engine-set names are not refused HERE, but they ARE refused: the per-type reserved-name
        // check is the separate loop below, over the same dependencies; see s_engineSetEnvKeys.
        //
        // The two loops are deliberately NOT collapsed into one. This pass runs to completion
        // first — over EVERY dependency, before the reserved-name loop sees its first one — so a
        // document carrying both faults throws the SECRET diagnostic: the author is told they put
        // a secret in a container's environment, which is the more serious of the two faults,
        // rather than merely that a variable is not theirs to set.
        //
        // Pinned by two tests, both of which declare a reserved-but-clean key BEFORE the
        // secret-bearing one so that a collapsed loop reports the COLLISION and goes red — within
        // one dependency by
        // Map_DependencyEnv_SecretReferenceOnAReservedKey_ReportsTheSecretFaultNotTheCollision,
        // and across two by
        // Map_DependencyEnv_ReservedCollisionOnAnEarlierDependency_StillReportsTheSecretFault
        // (which is what makes THIS pass's "every dependency first" span, not merely its
        // per-key precedence, the thing under test).
        foreach (var (dependencyName, spec) in env.Dependencies ?? new Dictionary<string, DependencySpec>())
        {
            if (spec.Env is null)
            {
                continue;
            }

            foreach (var (envKey, envValue) in spec.Env)
            {
                ValidateEnvValue(
                    "Dependency",
                    dependencyName,
                    envKey,
                    envValue,
                    env.Dependencies ?? new Dictionary<string, DependencySpec>(),
                    connectionReferencesSupported: false);
            }
        }

        // ----------------------------------------------------------------
        // Capture environment-level values used by the Configure closure.
        // ----------------------------------------------------------------
        var imageRegistry = env.ImageRegistry;
        var services = env.Services ?? new Dictionary<string, ServiceSpec>();
        var dependencies = env.Dependencies ?? new Dictionary<string, DependencySpec>();

        // dependency-env REQ-004: REFUSE every dependency `env:` key the engine sets for that
        // dependency's own `type:`, eagerly — the inputs are spec.Type and the key set, both known
        // here, so there is no reason to defer it into the Configure closure and every reason not
        // to (Map() is eager by discipline, and the refusal must reach the author on a run that
        // never gets as far as a container).
        //
        // Why a refusal and not an ordering. Aspire's WithEnvironment is LAST-WRITE-WINS, so
        // applying the author's map after the registration lambda would let it replace an engine
        // value the engine also advertises to every OTHER scenario through `${conn:...}`. The
        // interim shipped in T3 delivered engine-wins by silently dropping the key with a warning;
        // an author who writes ES_JAVA_OPTS and gets a green suite in which nothing happened is
        // the schema-acceptance-is-not-execution failure this feature exists to avoid reproducing,
        // so the entry is now refused outright and the suite never starts a container.
        //
        // ArgumentException out of Map is classified Inconclusive (§12.1: an authoring fault, not
        // an infrastructure one) by ScenarioRunner — the same classification every other eager
        // authoring check in this method gets, and no new machinery.
        var dependencyEnvToApply = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            StringComparer.Ordinal);
        foreach (var (name, spec) in dependencies)
        {
            if (spec.Env is not { Count: > 0 })
            {
                continue;
            }

            if (s_engineSetEnvKeys.TryGetValue(spec.Type, out var reservedForType))
            {
                foreach (var key in spec.Env.Keys)
                {
                    if (!reservedForType.Contains(key))
                    {
                        continue;
                    }

                    // The author's VALUE is deliberately absent from this message: two of the
                    // nine reserved names are passwords, and a diagnostic that echoed the
                    // rejected value would put author-supplied credential material into every
                    // log and report that carries the failure.
                    //
                    // The credential clause is SCOPED TO minio, and that scope is measured, not
                    // stylistic. Only MINIO_ROOT_USER/MINIO_ROOT_PASSWORD are spliced into a
                    // connection string `${conn:...}` hands to other scenarios (see the "minio"
                    // registration's depConnBuilders lambda above). elasticsearch's four names are
                    // host/port-only (s_hostPortOnlyParts), and the azureservicebus emulator's
                    // connection string is a fixed
                    // 'Endpoint=sb://…;SharedAccessKey=SAS_KEY_VALUE;…' in which none of ACCEPT_EULA
                    // / MSSQL_SA_PASSWORD / SQL_SERVER appears. An unscoped "some of them carry the
                    // credentials" handed an elasticsearch author refused for ES_JAVA_OPTS an
                    // argument with no bearing on their case. The load-bearing clause — "the shape
                    // every scenario shares" — is true for all nine and carries the message alone.
                    // Matches the wording shipped in the DSL spec, common-patterns and CHANGELOG.
                    throw new ArgumentException(
                        $"Dependency '{name}' (type '{spec.Type}') declares env entry '{key}', " +
                        "which the engine sets itself for this dependency type. That entry is " +
                        "REFUSED: the engine relies on its engine-set variables to bring this " +
                        "dependency up in the shape every scenario shares — and on 'minio' they " +
                        "are the credentials ${conn:<dependency>} advertises to every other " +
                        "scenario consuming it — so honouring an override would break other " +
                        "scenarios rather than only this one. Remove the entry, or declare the " +
                        "backend as a service with 'image:' if you need full control of its " +
                        "environment.",
                        nameof(env));
                }
            }

            // Nothing is dropped any more: every key that survives the refusal above is applied
            // verbatim, on the ten types with no reserved names and the three with them alike.
            dependencyEnvToApply[name] = spec.Env;
        }

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
        // Service name → the EndpointReference retained from the resource builder's GetEndpoint,
        // for the ONE endpoint svc::<name> resolves to. Populated for BOTH service forms: an
        // image-form service's primary endpoint comes from its own declared shape, a project-form
        // service's from the endpoints Aspire discovered on the built ProjectResource (#348).
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
        // REQ-016: resolve every declared security.serverArtifacts entry to an absolute host
        // path NOW — eagerly, before any builder mutation — so a blank/rooted/escaping/missing
        // source, or a target that is not an absolute in-container file path, fails Map() with a
        // located diagnostic. Deferring it into the Configure closure would place the throw after
        // earlier resources had already been added, which is the discipline every other eager
        // check in this method observes.
        // ----------------------------------------------------------------
        var resolvedSuiteDirectory = ResolveSuiteDirectory(suiteDirectory);
        var serviceArtifacts = new Dictionary<string, IReadOnlyList<ServerArtifactGroup>>(StringComparer.Ordinal);
        foreach (var (name, spec) in services)
        {
            serviceArtifacts[name] = ServerArtifactInjection.Plan(
                spec.Security, "services", name, resolvedSuiteDirectory);
        }

        var dependencyArtifacts = new Dictionary<string, IReadOnlyList<ServerArtifactGroup>>(StringComparer.Ordinal);
        foreach (var (name, spec) in dependencies)
        {
            dependencyArtifacts[name] = ServerArtifactInjection.Plan(
                spec.Security, "dependencies", name, resolvedSuiteDirectory);
        }

        // ----------------------------------------------------------------
        // Configure callback: builds the resource graph.
        // ----------------------------------------------------------------
        Action<IDistributedApplicationBuilder> configure = builder =>
        {
            // IDEMPOTENCE, to match the neighbours (peer-review MINOR). The three captured
            // dictionaries — serviceEndpoints, dependencyBuilders, depConnBuilders — are written
            // by KEYED ASSIGNMENT, so a second invocation of this closure overwrites rather than
            // accumulates. The two notice lists are Lists whose only write is an Add, so without
            // these Clears a second Configure DOUBLES every notice the author sees — measured by
            // deleting the line and re-running the pin below, which then reports
            // "Expected: 1 / Actual: 2".
            // There is exactly ONE production invocation today — HeadlessTopology's
            // `configureResources?.Invoke(builder)`, reached from SuiteTopology — so this closes a
            // latent inconsistency rather than a live defect. Pinned by
            // ProjectServiceEndpointStagingTests
            // .ConfigureInvokedTwice_DoesNotDuplicateTheTransportDowngradeNotice, and — for the
            // trust notice — .ConfigureInvokedTwice_DoesNotDuplicateTheTrustNotice.
            endpointSelectionNotices.Clear();
            endpointTrustNotices.Clear();

            var mostSpecificDependencyResources = new List<IResourceBuilder<IResource>>();

            foreach (var (name, spec) in dependencies)
            {
                var entry = s_dependencyRegistry[spec.Type];
                var (retained, mostSpecific) = entry.Build(
                    builder, name, spec, serviceEndpoints, depConnBuilders, imageRegistry, envPullPolicy);
                dependencyBuilders[name] = retained;
                mostSpecificDependencyResources.Add(mostSpecific);

                // The dependency's OWN container, resolved by name — the single target BOTH
                // consumers below need, and deliberately NOT `retained`, which for the four
                // database-backed types is the AddDatabase child (not a container, and no
                // IResourceWithEnvironment). Resolved LAZILY: ResolveDependencyContainer carries
                // a defensive throw of its own, so a dependency that declares neither artefacts
                // nor env: must never reach it. Must stay INSIDE this loop — see that method's
                // remarks for the loop-position invariant it depends on.
                IResourceBuilder<ContainerResource>? dependencyContainer = null;
                IResourceBuilder<ContainerResource> DependencyContainer() =>
                    dependencyContainer ??= ResolveDependencyContainer(builder, name, spec.Type);

                // REQ-016: the customer's broker image finds its keystore because the engine put
                // it there. Applied to the dependency's own CONTAINER, resolved by name, not to
                // `retained` (#426).
                //
                // WHICH DEPENDENCIES CAN REACH THIS, measured — NOT "all thirteen types", which
                // is what #426's own body claims after probing Map + Configure directly and so
                // bypassing every validator. Two independent gates confine `security` on a
                // dependency to `type: kafka`: REQ-021's schema clause ($defs/dependency's final
                // allOf, `security: false` for any type that is `not` kafka) and, on the compile
                // path, SecurityProfileWiringValidator (REQ-022) via ProviderPipeline.Compile.
                // So on `run`/`validate` the only dependency shape arriving here with artefacts
                // is a kafka one — the retained kafka builder was already container-typed, and
                // the pre-existing code was CORRECT for it.
                //
                // The retarget therefore is not a repair of a reachable authoring break. It is:
                // (a) correctness for the shipped kafka path, unchanged in behaviour; and (b)
                // defence for the widening the $defs/security description itself calls "a release
                // position rather than a permanent one: transport security for the remaining
                // dependency kinds is a 1.1 capability" — on that day the four database-backed
                // types would otherwise have thrown.
                //
                // ONE AUTHOR-REACHABLE CALLER TODAY, and it is a divergence, not a repair:
                // `--watch` (WatchRunner.Compile runs only YamlDocumentParser.Parse + AstBuilder
                // .Build — no DocumentValidator, no ProviderPipeline, no security validator — then
                // reaches EnvironmentMapper.Map/Configure). A `postgres` + serverArtifacts
                // document therefore changed under --watch from "refused at topology build" to
                // "accepted, files copied, containers started". That widens the watch/run gap
                // tracked by issue #370 ("--watch starts containers before schema validation and
                // the pre-topology guards"); it is recorded here rather than papered over, and
                // #370 owns the fix.
                //
                // Guarded on there being something to inject so an artefact-free dependency keeps
                // today's behaviour exactly: Apply's own early-return meant it never resolved a
                // container at all.
                var artifacts = dependencyArtifacts[name];
                if (artifacts.Count > 0)
                {
                    ServerArtifactInjection.Apply(DependencyContainer(), artifacts);
                }

                // dependency-env REQ-003. The map here has already cleared Map's eager passes: an
                // engine-set name for this dependency's type refused the suite outright
                // (REQ-004), and s_noEnvAccess is safe because the same passes refused every
                // `${conn:...}` on a dependency.
                if (dependencyEnvToApply.TryGetValue(name, out var dependencyEnv) &&
                    dependencyEnv.Count > 0)
                {
                    ApplyEnv(
                        "Dependency",
                        name,
                        DependencyContainer(),
                        dependencyEnv,
                        s_noEnvAccess);
                }
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

                    // REQ-008: 'ports' declared at all switches this service from the
                    // pre-existing HTTP-only-by-default shape to a generalised, non-HTTP-first
                    // one — the implicit default HTTP endpoint is suppressed (never assumed
                    // just because SOME container port exists) unless 'httpPort' is ALSO
                    // explicitly declared alongside 'ports' (the opt-in hybrid shape).
                    var hasExplicitPorts = spec.Ports is { Count: > 0 };

                    var containerBuilder = builder.AddContainer(name, fullImage);

                    // One endpoint set, computed by ServiceEndpointNaming, for BOTH the actual
                    // Aspire declarations below and IProjectContext.DeclaredServices (which
                    // projects the same computation's names) — the two cannot drift.
                    //
                    // For a service declaring no 'security' block this loop emits exactly the
                    // calls the three-way branch it replaced emitted, in the same order:
                    // WithEndpoint(tcp-<port>) per 'ports:' entry, then WithHttpEndpoint("http")
                    // for 'httpPort' when declared, or the single implicit
                    // WithHttpEndpoint("http") on 'httpPort ?? 80' when 'ports:' is absent.
                    // Generic WithEndpoint (scheme omitted) leaves Aspire's own "tcp" default in
                    // place (confirmed empirically against the pinned Aspire.Hosting 13.4.2
                    // DLL), satisfying REQ-008 without this file setting a scheme itself.
                    var endpointDeclarations = ServiceEndpointNaming.EndpointDeclarations(spec);
                    foreach (var endpoint in endpointDeclarations)
                    {
                        // REQ-025: the host port this container port publishes on, when the author
                        // pinned it, and null for every port declared as a bare integer — which
                        // leaves the orchestrator allocating one exactly as before. Threaded into
                        // all three overloads below rather than only the secured one, because the
                        // requirement is about a service's ports and says nothing about schemes.
                        //
                        // GUARDED ON THE PORT HAVING COME FROM `ports:`, not on its VALUE alone.
                        // EndpointDeclarations covers `httpPort` and the implicit HTTP endpoint as
                        // well as `ports:` entries, so a lookup keyed on the port number alone
                        // attaches a pin to an endpoint that never asked for one whenever the two
                        // numbers coincide — an `httpPort: 9093` beside `ports: ["19093:9093"]`
                        // would publish BOTH on host 19093. The remaining ambiguity, where those
                        // two numbers are equal, is refused outright by the eager check in Map's
                        // services loop, so reaching here means the association is unique.
                        //
                        // Passing null is what makes the default path byte-for-byte unchanged:
                        // `port` is `int?` on every one of these overloads and null is its own
                        // default, so an unpinned endpoint emits the identical call it always did.
                        int? pinnedHostPort =
                            spec.PinnedHostPorts is { } pins
                            && spec.Ports is { } declaredPorts
                            && declaredPorts.Contains(endpoint.Port)
                            && pins.TryGetValue(endpoint.Port, out var host)
                                ? host
                                : null;

                        if (endpoint.IsSecured)
                        {
                            // REQ-023. WithHttpsEndpoint sets EndpointAnnotation.UriScheme to
                            // "https" unconditionally, which is what makes the staged
                            // svc::<name> value begin "https://" and therefore what makes the
                            // three HTTP-family providers issue a TLS request at all — they
                            // derive their base URL solely from that string, so this fixes the
                            // transport scheme for all three with no provider change.
                            //
                            // It declares endpoint METADATA only: it does not make the
                            // container serve TLS. The system under test terminates TLS itself
                            // with material its author supplied, which is exactly the model
                            // this feature assumes (the client-side trust material is REQ-024).
                            containerBuilder = containerBuilder.WithHttpsEndpoint(
                                targetPort: endpoint.Port, port: pinnedHostPort, name: endpoint.Name);
                        }
                        else if (string.Equals(
                                     endpoint.Name,
                                     ServiceEndpointNaming.HttpEndpointName,
                                     StringComparison.Ordinal))
                        {
                            containerBuilder = containerBuilder.WithHttpEndpoint(
                                targetPort: endpoint.Port, port: pinnedHostPort, name: endpoint.Name);
                        }
                        else
                        {
                            containerBuilder = containerBuilder.WithEndpoint(
                                targetPort: endpoint.Port, port: pinnedHostPort, name: endpoint.Name);
                        }
                    }

                    containerBuilder = ApplyHealthCheck(
                        builder, containerBuilder, name, spec, hasExplicitPorts, endpointDeclarations);

                    containerBuilder = containerBuilder
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

                    ApplyEnv("Service", name, containerBuilder, spec.Env, envAccessByDependency);

                    // REQ-016: copy declared server artefacts into the system under test's own
                    // container — the server certificate/key a TLS-terminating SUT presents, the
                    // mirror of the client material REQ-024 gives the step.
                    ServerArtifactInjection.Apply(containerBuilder, serviceArtifacts[name]);

                    // Stage the primary endpoint for svc::<name> resolution (ResolveServices /
                    // env: refs).
                    //
                    // REQ-023: the SECURED endpoint wins outright when the service declares
                    // one. This is the decision the requirement turns on, so it is recorded
                    // here rather than inferred. `security.endpoint` is mandatory (REQ-002)
                    // precisely because an implicit default could resolve to a plaintext
                    // listener the infrastructure keeps open alongside the secured one — the
                    // customer's own broker does exactly that. So when an author has named a
                    // secured endpoint, every step targeting the service must reach THAT one:
                    // staging a sibling plaintext 'httpPort' here instead would produce a suite
                    // that passes every assertion having authenticated nothing (EDGE-004). A
                    // service declaring both 'security' and a plain 'httpPort' therefore keeps
                    // its plaintext endpoint declared (the SUT may genuinely serve one, and
                    // it remains available to a health check), but it is not what svc::<name>
                    // resolves to.
                    //
                    // With no 'security' block this is byte-for-byte today's rule: the "http"
                    // endpoint when one exists, otherwise the FIRST declared TCP port, so a
                    // purely non-HTTP service (REQ-008) still has a well-defined primary
                    // endpoint.
                    var securedEndpoint = endpointDeclarations.FirstOrDefault(e => e.IsSecured);
                    var primaryEndpointName = securedEndpoint is not null
                        ? securedEndpoint.Name
                        : hasExplicitPorts && spec.HttpPort is null
                            ? ServiceEndpointNaming.TcpEndpointName(spec.Ports![0])
                            : ServiceEndpointNaming.HttpEndpointName;
                    serviceEndpoints[name] = containerBuilder.GetEndpoint(primaryEndpointName);
                }
                else if (spec.Project is not null)
                {
                    // String overload only — §4 invariant (generic AddProject<T>() is forbidden).
                    var projectBuilder = builder.AddProject(name, spec.Project);

                    // §4 invariant: WaitFor the most-specific dependency resource.
                    foreach (var depBuilder in mostSpecificDependencyResources)
                        projectBuilder = projectBuilder.WaitFor(depBuilder);

                    ApplyEnv("Service", name, projectBuilder, spec.Env, envAccessByDependency);

                    // Stage the primary endpoint for svc::<name> resolution — the project-form
                    // analogue of the image branch's own staging above (#348). Without it a step
                    // whose `target` names a project-form service read a MISSING key, every
                    // HTTP-family provider fell back to the empty string, and `new Uri("")` threw
                    // UriFormatException at execution time — naming neither the service nor the
                    // cause, after the topology had come up and the step had validated.
                    //
                    // READ OFF THE BUILT RESOURCE, not off the ServiceSpec. The image branch knows
                    // its endpoints because the author declared them; a project's endpoints are
                    // Aspire's to discover from the project's own launch profile, and
                    // ServiceEndpointNaming.DeclaredEndpointNames returns an EMPTY list for a
                    // project-form service for exactly that reason. Measured against the pinned
                    // Aspire 13.4.2: AddProject(name, csprojPath) attaches its EndpointAnnotations
                    // SYNCHRONOUSLY, so they are readable here inside Configure, well before
                    // StartAsync — `Properties/launchSettings.json` with
                    // `"applicationUrl": "http://localhost:5111"` yields one annotation named
                    // "http" (UriScheme "http"), an https URL yields "https", both yield both in
                    // the order the applicationUrl lists them, and two http URLs yield "http" and
                    // "http2".
                    var projectEndpoints = projectBuilder.Resource.Annotations
                        .OfType<EndpointAnnotation>()
                        .ToList();

                    // THE AUTHOR'S OWN SELECTION, where one was declared. `endpoint:` names WHICH
                    // discovered listener svc::<name> resolves to, and it is the only override
                    // this form has — the fixed rule below is what runs in its absence.
                    //
                    // MATCHED ON THE ANNOTATION NAME, under Ordinal comparison, never on the
                    // scheme. Scheme matching is the fixed rule's business because that rule is a
                    // statement about TRANSPORT; this field is a statement about WHICH LISTENER,
                    // and a project declaring two http URLs has two listeners ("http", "http2" —
                    // measured) that no scheme can tell apart. Ordinal is also case-sensitive by
                    // construction, matching every other DSL vocabulary term: `endpoint: HTTPS`
                    // does not name an endpoint called "https", and is refused below rather than
                    // quietly folded into it.
                    //
                    // FIRST MATCH WINS, in annotation order — the same order the fixed rule
                    // iterates. No uniqueness is claimed for the orchestrator's naming; the rule
                    // is stated so that a duplicate name, if one ever appears, is deterministic
                    // rather than undefined.
                    //
                    // `is { }` — NULL IS THE ONLY SPELLING OF "ABSENT". Not
                    // `string.IsNullOrWhiteSpace`, and not `{ Length: > 0 }` either: every
                    // non-null value the parser can produce is a declaration the author wrote, so
                    // it takes the Find-then-throw path below and is named back to them. A
                    // whitespace-only value is schema-legal (`minLength: 1` refuses only the empty
                    // string), and the field's shipped description promises such a value is
                    // "refused at topology-build time like any other unmatched name".
                    //
                    // THE EMPTY STRING TAKES THE SAME PATH, and the schema is not what makes that
                    // matter — one author-reachable path reaches this mapper with no schema in
                    // front of it. A dangling `endpoint:` key (no value after the colon)
                    // round-trips through GetScalar as "", NOT as null: GetScalar returns
                    // `scalar.Value` verbatim, and only the separate GetScalarOrPlainNull helper
                    // collapses that spelling, which this field deliberately does not use.
                    // `--watch` never validates against the schema at all (measured:
                    // WatchRunner.Compile is YamlDocumentParser.Parse + AstBuilder.Build, with no
                    // DocumentValidator.Validate call) and is precisely the edit-and-save mode
                    // where a half-typed key exists. Under a `Length: > 0` test it would leave
                    // this null, fall through to the fixed rule, and accept the author's
                    // `endpoint:` key while silently ignoring it: the exact defect class #448
                    // exists to end, reproduced inside its own fix.
                    //
                    // MATCH FIRST, HAND GetEndpoint ONLY A NAME THE ORCHESTRATOR PRODUCED.
                    // Measured, and recorded again by the endpoint-less refusal below: GetEndpoint
                    // does NOT throw on a name no endpoint carries — it returns an
                    // EndpointReference whose Exists is false, deferring the failure past
                    // StartAsync into the unattributable UriFormatException shape #348 exists to
                    // remove. Passing the author's string straight through would reintroduce
                    // exactly that.
                    EndpointAnnotation? declaredProjectEndpoint = null;
                    if (spec.Endpoint is { } requestedEndpoint)
                    {
                        declaredProjectEndpoint = projectEndpoints.Find(
                            e => string.Equals(e.Name, requestedEndpoint, StringComparison.Ordinal));

                        if (declaredProjectEndpoint is null)
                        {
                            // REFUSED WHETHER OR NOT ANY STEP TARGETS THIS SERVICE, which is the
                            // one place this diagnostic deliberately parts company with the
                            // endpoint-less refusal further down. That one is gated on targeting
                            // because a .NET worker service legitimately declares no endpoint at
                            // all — silence there is correct. The reasoning does not transfer: an
                            // `endpoint:` naming something the project does not declare is a false
                            // statement the author wrote, and an untargeted worker carrying a
                            // stray one would otherwise pass silently — the accepted-and-ignored
                            // shape this field exists to end.
                            //
                            // IT ALSO WINS OVER THE ENDPOINT-LESS REFUSAL when the project
                            // declares nothing at all: the author named a selector, so naming it
                            // back is the more specific diagnostic. But the endpoint-less
                            // refusal's advice is still the fix, so this message carries it too
                            // in that case rather than sending the author to a shorter dead end.
                            //
                            // TopologyAuthoringException, for the reason spelled out at the
                            // endpoint-less throw below: anything else escaping the Configure
                            // closure is wrapped by SuiteTopology.StartAsync as
                            // OrchestrationException → EnvironmentError, which reports an
                            // authoring fault as an infrastructure one and hands CI a green run
                            // over a suite that never started.
                            //
                            // NO ADVICE TO DECLARE 'ports', 'httpPort' OR 'security' — none of
                            // the three is available on a project-form service, so suggesting any
                            // of them would send the author to a validation failure.
                            //
                            // BOTH FIXES ARE OFFERED IN THE NO-ENDPOINT CASE, because the two
                            // shapes that reach it need opposite ones. An author who meant to
                            // address this service over HTTP needs the 'applicationUrl'; an author
                            // whose worker service — the canonical case here: no launch profile,
                            // no step targeting it — simply carries a stray 'endpoint:' needs to
                            // DELETE that line, and nothing else. The message this one displaces
                            // said so ("a worker consuming a queue, say — needs no endpoint and is
                            // unaffected by this rule"); dropping that sentence would leave the
                            // larger of the two audiences with only advice that does not apply.
                            var describedProjectEndpoints = projectEndpoints.Count == 0
                                ? "(none)"
                                : string.Join(
                                    ", ",
                                    projectEndpoints.Select(e => $"{e.Name} ({e.UriScheme})"));

                            var noneAdvice = projectEndpoints.Count == 0
                                ? " This project declares no endpoint at all: they are discovered "
                                    + "from the launch profile in its "
                                    + "'Properties/launchSettings.json', so add an "
                                    + "'applicationUrl' to that profile (for example "
                                    + "\"applicationUrl\": \"http://localhost:5000\") and name "
                                    + "here the endpoint it produces. If instead this service is "
                                    + "not meant to be addressed at all — a worker consuming a "
                                    + "queue, say — remove the 'endpoint:' line: such a service "
                                    + "needs no endpoint and is unaffected by this rule."
                                : string.Empty;

                            // THE VALUE IS QUOTED. A whitespace-only selector otherwise renders as
                            // `declares 'endpoint:    '`, where the reader cannot tell the value
                            // from the spacing around it — and a whitespace-only value is exactly
                            // one of the shapes that reaches this throw.
                            throw new TopologyAuthoringException(
                                $"Service '{name}' declares 'endpoint: \"{requestedEndpoint}\"', "
                                + "which matches none of the endpoints its project "
                                + $"('{spec.Project}') declares. Discovered endpoints: "
                                + $"{describedProjectEndpoints}. The value is an endpoint NAME and "
                                + "is matched exactly, case included."
                                + noneAdvice,
                                nameof(env));
                        }
                    }

                    // Selection rule, project-form: "http", else "https", else the first declared.
                    //
                    // Measured, so the common case is on record rather than assumed: a stock
                    // `dotnet new webapi` ships TWO `commandName: Project` profiles — "http"
                    // (http only) and "https" (`https://...;http://...`) — and Aspire 13.4.2
                    // selects the FIRST, so the endpoint set for an unmodified template project is
                    // exactly one annotation named "http". The rule is therefore unambiguous for
                    // the shape most project-form services actually have.
                    //
                    // PLAINTEXT FIRST where both DO appear (the template's "https" profile chosen
                    // explicitly, or a hand-written profile listing both). Reasoning, not
                    // measurement: a project-form service cannot declare `security` at all
                    // (refused eagerly in the validation loop above), so the engine holds no
                    // client trust material for one and configures no trust on the step's own
                    // HttpClient — while the project's https listener is served with whatever
                    // certificate it arranges for itself, a Kestrel development certificate by
                    // default.
                    //
                    // FOLLOW THE COUNTERFACTUAL, because it is the actual justification and it is
                    // worse than "the request fails". Preferring "https" would fail the dev-cert
                    // handshake → HttpRequestException → the step is classified EnvironmentError,
                    // and EnvironmentError maps to exit 0 unless the caller passes
                    // `--fail-on-env-error` — §12.1's BASE rule, the one ExitCodes.FromVerdict
                    // implements, and NOT #390, which this note used to cite. #390 is about a run
                    // that EXECUTED NOTHING; the step in this counterfactual runs, reaches the
                    // listener and fails the handshake, so the base rule is the whole reason. The
                    // author would get a green build over a step that verified nothing. Plaintext
                    // at least exercises the application, and it is not the EDGE-004 bypass the
                    // image-form secured rule guards against: EDGE-004 is a suite that ASSERTED
                    // `security` and then authenticated nothing, whereas a project-form author
                    // made no security assertion to vouchfx at all — there is no claim here to
                    // silently falsify.
                    //
                    // The choice is not free, though, so it is ANNOUNCED rather than assumed
                    // acceptable: when the project declares both, the notice below tells the
                    // author their traffic went plaintext, names both endpoints, and says why.
                    //
                    // ALL OF THE ABOVE IS THE DEFAULT, NOT THE ONLY OUTCOME. `endpoint:` overrides
                    // it outright: the match is made above, and `??` short-circuits, so none of
                    // the operands below is evaluated when it succeeded. What follows is what an
                    // author who expressed no preference gets — which is why it stays the
                    // conservative choice.
                    //
                    // "https" is still taken when it is the ONLY endpoint, so an https-only
                    // project resolves to its one real listener rather than being refused; trust
                    // is then the author's to arrange, exactly as it is for an image-form service
                    // that terminates TLS itself. The first-declared fallback mirrors the image
                    // branch's own "otherwise the FIRST declared port" tie-break and covers any
                    // name Aspire produces that is neither — measured: two http URLs in one
                    // applicationUrl yield "http" and "http2".
                    // MATCHED ON UriScheme, NOT ON Name. Identical behaviour today — Aspire names
                    // the first endpoint of each scheme after that scheme — but the predicate now
                    // says what it means. "Prefer the plaintext endpoint" is a statement about
                    // TRANSPORT, and every argument above is about transport; a name match only
                    // happened to encode it, and would stop encoding it the moment Aspire named
                    // an endpoint "http2" (measured: it does, for a second http URL) or a future
                    // hook named one anything else.
                    //
                    // THE LEADING `declaredProjectEndpoint ??` IS NOT A FALLBACK FROM A FAILED
                    // MATCH. An `endpoint:` that matched nothing threw above, so this operand is
                    // non-null exactly when the author selected a listener, and `??` then
                    // short-circuits the whole fixed rule: the author's selection is final,
                    // whatever its scheme, and the transport argument above simply does not apply
                    // to a choice the author made deliberately.
                    var primaryProjectEndpoint = declaredProjectEndpoint
                        ?? projectEndpoints.Find(e => string.Equals(
                            e.UriScheme, ServiceEndpointNaming.HttpEndpointName, StringComparison.Ordinal))
                        ?? projectEndpoints.Find(e => string.Equals(
                            e.UriScheme, ServiceEndpointNaming.HttpsEndpointName, StringComparison.Ordinal))
                        ?? projectEndpoints.FirstOrDefault();

                    // REPORT THE DOWNGRADE (security review). A project declaring an https
                    // endpoint whose traffic the engine then sends in plaintext is a decision the
                    // author must be able to see: the step observation carries only status and
                    // expectation, so nothing else in the run's own step record says so. Emitted
                    // once per service, naming both endpoints and the reason, and ONLY when there
                    // was a real choice to make.
                    //
                    // It reaches the §14 event stream too (#450 / #453) — through a NEW record,
                    // TransportNoticeEvent, rather than an existing field. That distinction is the
                    // whole design: every EXISTING free-text field reaching --events/--junit/--html
                    // is a scenario-level CAUSE for a non-Pass verdict, so writing a healthy-run
                    // advisory into one would have changed what a green JUnit test displays or
                    // overwritten a real failure cause. Adding an optional record is what the v1
                    // freeze permits (it forbids renaming, retyping and re-wiring properties), and
                    // Vouchfx.Engine.Runtime.TransportNoticeEvents is its single producer; this
                    // mapper raises the notice and does nothing else with it.
                    // This mirrors SecurityConfirmations, which is surfaced off the topology and
                    // printed for the same reason and through the same channel — and, like it,
                    // travels as a TYPED record so the wording lives at the print site.
                    //
                    // GATED ON THE SERVICE BEING TARGETED. The message says "steps targeting it
                    // will use PLAINTEXT", which is simply untrue of a worker no step addresses —
                    // and emitting it there would also spend the notice's credibility on the case
                    // that has nothing to warn about.
                    //
                    // AND ONLY WHEN THE ENGINE MADE IT. `declaredProjectEndpoint is null` is the
                    // whole of that condition: this notice announces a choice the AUTHOR did not
                    // make, and announcing an author's own `endpoint: http` back to them spends
                    // the notice's credibility on the case that needs no warning — the same
                    // argument that gates it on targeting. An explicit plaintext selection is
                    // therefore SILENT; the author opted out knowingly.
                    if (declaredProjectEndpoint is null
                        && endpointTargets.Contains(name)
                        && primaryProjectEndpoint is not null
                        && string.Equals(
                            primaryProjectEndpoint.UriScheme,
                            ServiceEndpointNaming.HttpEndpointName,
                            StringComparison.Ordinal)
                        && projectEndpoints.Find(e => string.Equals(
                            e.UriScheme,
                            ServiceEndpointNaming.HttpsEndpointName,
                            StringComparison.Ordinal)) is { } securedSibling)
                    {
                        endpointSelectionNotices.Add(new EndpointSelectionNotice(
                            ServiceName: name,
                            SelectedEndpoint: primaryProjectEndpoint.Name,
                            RejectedEndpoint: securedSibling.Name));
                    }

                    // REPORT THE ABSENCE OF TRUST, which is the other half of the same disclosure
                    // and the one that survives the rule above. Silence on an explicit selection
                    // is right for `endpoint: http` — the author chose plaintext knowingly — and
                    // wrong for an https one, because it removes the ONLY thing in the run that
                    // says anything about transport while the author's likely reading of what
                    // they typed ("this is now secured") is exactly what it is not. Composed with
                    // a handshake failure landing as EnvironmentError, the plausible outcome is a
                    // green CI run over a suite that never verified anything. Announcing the
                    // absence of trust creates none of it.
                    //
                    // GATED ON THE SELECTED ENDPOINT, NOT ON WHO SELECTED IT (maintainer-approved,
                    // and this is the correction of a narrower rule that shipped with a hole in
                    // it). Gating on `declaredProjectEndpoint` — "the author chose this" — left an
                    // https-ONLY project with no `endpoint:` completely silent: the fixed rule
                    // above picks its https listener, the downgrade notice cannot fire because it
                    // requires an http selection, and this one could not fire because there was no
                    // author selection to gate on. The run then addressed an https listener the
                    // engine configures no trust material for, and said nothing — the identical
                    // silent-green shape this notice exists to close, reached from the other
                    // side. `primaryProjectEndpoint` covers the engine-picked case and the
                    // author-picked case with one condition.
                    //
                    // The `endpoint: http` path is unaffected: its selection's scheme is http, so
                    // the scheme test below excludes it, and an explicit plaintext choice stays
                    // silent exactly as decision 5 says it should.
                    //
                    // TESTED ON THE SELECTED ANNOTATION'S SCHEME, NOT ON THE AUTHOR'S STRING.
                    // `endpoint:` matches by NAME and a project may name an https listener
                    // anything; firing on the literal text "https" would warn about a plaintext
                    // listener that happens to be called "https" and stay silent on a TLS
                    // listener called "secure-api" — noisy in the harmless case and silent in the
                    // dangerous one, which is the wrong way round for a security advisory.
                    //
                    // GATED ON TARGETING for the same reason its sibling is: it describes traffic
                    // that will actually happen.
                    if (primaryProjectEndpoint is not null
                        && endpointTargets.Contains(name)
                        && string.Equals(
                            primaryProjectEndpoint.UriScheme,
                            ServiceEndpointNaming.HttpsEndpointName,
                            StringComparison.Ordinal))
                    {
                        endpointTrustNotices.Add(new EndpointTrustNotice(
                            ServiceName: name,
                            SelectedEndpoint: primaryProjectEndpoint.Name));
                    }

                    if (primaryProjectEndpoint is not null)
                    {
                        serviceEndpoints[name] = projectBuilder.GetEndpoint(primaryProjectEndpoint.Name);
                    }
                    else if (endpointTargets.Contains(name))
                    {
                        // FAIL LOUDLY, NAMING THE SERVICE — but ONLY for a service some step will
                        // actually read a staged endpoint for. That condition is the whole of the
                        // difference between a fault and a legitimate topology, and refusing
                        // without it was a worse regression than the bug being fixed: a .NET
                        // WORKER SERVICE (a BackgroundService consuming Kafka or a queue, no
                        // applicationUrl, no HTTP listener) declares no endpoint either. That
                        // shape is schema-legal, has no escape hatch — $defs/service's project-form
                        // clause refuses the port-shaping fields it names (grep that clause's
                        // `then` for the current roster rather than trusting a copy here, which is
                        // how this sentence went stale once already), and `security`, which
                        // carries an endpoint selector of its own, is refused separately and
                        // eagerly by the validation loop at the top of this method — so its author
                        // cannot declare a non-HTTP shape the way REQ-008 lets an image-form
                        // service — and is the canonical thing this product tests: the worker
                        // consuming the Kafka event in the one business transaction. It started
                        // fine before #348 and was simply never staged, which is correct, because
                        // nothing reads svc::<name> for a service no step targets.
                        //
                        // FAIL LOUDLY rather than staging something. The two alternatives were
                        // both worse. Staging GetEndpoint("http") unconditionally is not an
                        // option: measured, that call does NOT throw on an endpoint-less project
                        // — it returns an EndpointReference whose Exists is false, which defers
                        // the failure past StartAsync into the same unattributable shape #348
                        // exists to remove. Staging only when an endpoint exists leaves a
                        // TARGETED launch-profile-less project dying with that same
                        // UriFormatException.
                        //
                        // TopologyAuthoringException, not a bare ArgumentException: this throw
                        // escapes from inside the Configure closure, and SuiteTopology.StartAsync
                        // wraps ANYTHING else escaping there as OrchestrationException →
                        // EnvironmentError, which would report an authoring fault as an
                        // infrastructure one (§12.1's one forbidden direction) and — because an
                        // EnvironmentError that executed nothing still exits 0 (#390) — hand CI a
                        // green run over a suite that never started. That type is re-thrown
                        // unwrapped by StartAsync and lands in ScenarioRunner's ArgumentException
                        // catch: Inconclusive, nothing executed, non-zero exit (#369).
                        throw new TopologyAuthoringException(
                            $"Service '{name}' is declared as a 'project' ('{spec.Project}') and is " +
                            $"addressed by a step's 'target', but that project declares no endpoint, so " +
                            $"there is no address to reach '{name}' at. Aspire discovers a project's " +
                            "endpoints from the launch profile in its 'Properties/launchSettings.json': " +
                            "add an 'applicationUrl' to that profile (for example " +
                            "\"applicationUrl\": \"http://localhost:5000\") for an HTTP service, or declare " +
                            "it as an 'image'-form service — which is also the only form that can expose " +
                            "a non-HTTP listener such as a broker port, via 'ports:'. A project-form " +
                            "service that no step targets — a worker consuming a queue, say — needs no " +
                            "endpoint and is unaffected by this rule.",
                            nameof(env));
                    }

                    // else: an endpoint-less project-form service that NO step targets — a worker
                    // service. Stage nothing, throw nothing, touch nothing: byte-for-byte the
                    // behaviour it had before #348, which is the behaviour it needs. It is still
                    // built, still WaitFor's its dependencies, still carries its `env:`, and still
                    // appears in HealthGateResourceNames; it simply has no svc::<name> entry,
                    // because nothing would read one.
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
                    result[name] = StageServiceEndpoint(name, endpointRef, kafkaTargets);

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
            DependencyNames: dependencies.Keys.ToList())
        {
            // The SAME dictionary instance the Configure closure writes into and the
            // ResolveServices closure reads from — not a copy, so it reflects staging as it
            // happens rather than the empty state at Map's return.
            StagedServiceEndpoints = serviceEndpoints,
            EndpointSelectionNotices = endpointSelectionNotices,
            EndpointTrustNotices = endpointTrustNotices,
        };
    }

    /// <summary>
    /// The "no Kafka step targets anything" default for <see cref="Map"/>'s
    /// <c>kafkaSpeakingTargets</c> parameter.
    /// </summary>
    private static readonly IReadOnlySet<string> EmptyProtocolTargets =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Renders one resolved service endpoint into the form its own consumer can use (REQ-023, as
    /// amended 2026-08-04).
    /// </summary>
    /// <param name="name">The declared service name.</param>
    /// <param name="endpoint">The retained endpoint reference, resolved after <c>StartAsync</c>.</param>
    /// <param name="kafkaSpeakingTargets">
    /// The names the suite's own <c>mq-publish.kafka</c>/<c>mq-expect.kafka</c> steps address.
    /// </param>
    /// <returns>
    /// A bare <c>host:port</c> bootstrap authority for a Kafka-addressed target; the endpoint's
    /// own scheme-carrying URL for every other service — byte-identical to what this method
    /// replaced, which was <c>endpoint.Url</c> unconditionally.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>The requirement this implements changed, and the reason is worth keeping.</strong>
    /// REQ-023 originally mandated an <c>https://</c> scheme unconditionally. A Kafka broker
    /// authored as a SERVICE — the shape REQ-011 records the target deployment actually uses — was
    /// therefore staged as <c>https://host:port</c>, a scheme meaningless to a Kafka client, which
    /// then had to strip it back off. Forcing a scheme the consumer must undo is not a convenience:
    /// it is a transformation the engine imposes and every provider must reverse identically or
    /// diverge. The amended rule is the consumer's form, and a provider rewriting the staged value
    /// is now the proof that the engine staged the wrong one.
    /// </para>
    /// <para>
    /// <strong>Why the protocol source is the suite's own steps.</strong> It is the same inference
    /// <see cref="SuiteProtocolTargets"/> already performs to choose REQ-005's confirmation level,
    /// deliberately reused rather than re-derived, so the staging form and the confirmation level
    /// cannot disagree about what a target speaks.
    /// </para>
    /// <para>
    /// <strong>What is NOT changed, and why.</strong> The endpoint ANNOTATION is untouched — a
    /// secured endpoint keeps the <c>https</c> URI scheme <c>WithHttpsEndpoint</c> gives it, and an
    /// unsecured one keeps <c>tcp</c>/<c>http</c>. The endpoint's NAME
    /// (<c>ServiceEndpointNaming.HttpsEndpointName</c>) is a REQ-023 constant surfaced through
    /// <c>IProjectContext.DeclaredServices</c> and resolvable by a <c>healthCheck</c> selector, so
    /// making either the name or the scheme depend on which steps a suite happens to contain would
    /// buy nothing and make two author-visible surfaces protocol-dependent. Only the STAGED VALUE
    /// — the thing a step actually consumes — follows the protocol.
    /// </para>
    /// <para>
    /// MEASURED against the pinned Aspire 13.4.2 rather than assumed, twice.
    /// <c>EndpointReference</c> exposes <c>Host</c>, <c>Port</c>, <c>Scheme</c> and <c>Url</c> as
    /// separate members, so the authority is read directly and is not a string-surgery pass over
    /// <c>Url</c> — nothing here parses a URL apart in order to put it back together. And
    /// <c>Url</c> for a service declaring <c>ports: [9092]</c> renders <c>tcp://localhost:60081</c>
    /// (measured live, against a running container): the pre-existing staged value for a non-HTTP
    /// service carried a <c>tcp</c> scheme, not a bare authority, so a Kafka client could no more
    /// consume it than it could consume the <c>https</c> one a secured service received.
    /// </para>
    /// <para>
    /// <strong>Every consumer of the staged value, and what changes for each.</strong>
    /// <c>svc::&lt;name&gt;</c> in <c>Vars</c> is read by the three HTTP-family providers (unchanged
    /// — a target of theirs is never in this set) and, since REQ-011's fix, by the two Kafka
    /// providers for a service target (which is what this exists for). The same string is also the
    /// entry in <c>ScriptGlobalVariables.Services</c>, so a <c>script.csharp</c> step reading
    /// <c>Services["broker"]</c> for a Kafka-addressed service now sees <c>host:port</c> where it
    /// previously saw <c>tcp://host:port</c> — the same rule applied consistently, and the shape
    /// such a script wanted anyway. Nothing else reads it: <c>${conn:…}</c> in an <c>env:</c> block
    /// is resolved from the dependency builders directly and never from this map, and
    /// <c>{placeholder}</c> substitution cannot address a prefixed key at all.
    /// </para>
    /// <para>
    /// <strong>The authority is rendered through <see cref="AuthorityText"/></strong> (m1,
    /// peer-review critic, fix round eight), not by raw interpolation. That helper exists to
    /// eliminate exactly the <c>$"{host}:{port}"</c> spelling that used to stand here, and this is
    /// one of the values whose bracket-freeness is INFERRED rather than measured — it comes from
    /// Aspire's <c>EndpointReference.Host</c> — which is precisely the kind of caller
    /// <c>AuthorityText</c>'s own idempotency guard was written for. (No caller is proven
    /// bracket-free, including the probe's own resolver; see <c>AuthorityText</c>'s remarks, which
    /// this note previously contradicted by implying the others were.) Byte-identical for every shape
    /// reachable today (every host-published endpoint comes back as <c>localhost</c> or an IPv4
    /// literal); it changes only what an IPv6 host would produce, and there it produces the
    /// bracketed form a Kafka bootstrap actually parses instead of the ambiguous <c>::1:9093</c>.
    /// </para>
    /// </remarks>
    private static string StageServiceEndpoint(
        string name, EndpointReference endpoint, IReadOnlySet<string> kafkaSpeakingTargets) =>
        kafkaSpeakingTargets.Contains(name)
            ? AuthorityText.Format(endpoint.Host, endpoint.Port)
            : endpoint.Url;

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

    /// <summary>
    /// Matches a <c>${env:NAME}</c> reference (REQ-006) — a passthrough of the engine
    /// process's own environment, resolved at topology-build time. Group 1 is the
    /// environment-variable name. NAME follows the conventional POSIX shell identifier
    /// shape (a letter or underscore, then letters/digits/underscores) — the shape both
    /// the REQ-006 and EDGE-008 acceptance examples (<c>VOUCHFX_TEST_REGION</c>,
    /// <c>VOUCHFX_UNSET_VAR_XYZ</c>) already use. Deliberately a SEPARATE pattern from
    /// <see cref="s_connRefPattern"/> rather than one combined alternation: keeping the two
    /// independent means every existing <c>${conn:...}</c> call site
    /// (<see cref="CollectReferencedDependencyNames"/> in particular, which has no reason
    /// to know about <c>${env:...}</c> at all) needs zero changes; only
    /// <see cref="TokeniseEnvValue"/> interleaves matches from both patterns, and
    /// <see cref="ValidateEnvValue"/> scans both independently.
    /// </summary>
    private static readonly Regex s_envRefPattern = new(
        @"\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}",
        RegexOptions.Compiled);

    /// <summary>
    /// Matches ONLY the <c>${env:</c> sigil itself (m5 fix, fix round 2) — case-INSENSITIVE,
    /// unlike <see cref="s_envRefPattern"/> — used by <see cref="ValidateEnvValue"/> as a
    /// PRESENCE check, mirroring the existing <c>${secret:...}</c> sigil-presence check
    /// (<see cref="SecretReference.Sigil"/>'s own rationale): a malformed or wrong-case
    /// attempt at an <c>env:</c> reference must be rejected too, not silently pass through
    /// as opaque literal text. Case-insensitive specifically so a case-mistake on the
    /// reserved word itself (<c>${ENV:GOOD}</c> for the case-sensitive-canonical
    /// <c>${env:GOOD}</c>) is caught — every other malformed shape (<c>${env:}</c>,
    /// <c>${env:2BAD}</c>, <c>${env: SPACED }</c>) already contains the exact-case sigil, so
    /// case-insensitivity only widens what this check catches, never what
    /// <see cref="s_envRefPattern"/> itself accepts as well-formed.
    /// </summary>
    private static readonly Regex s_envSigilPattern = new(
        @"\$\{env:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
    /// The environment-variable NAMES this mapper's own dependency registrations set, keyed by
    /// dependency <c>type:</c> — the author-addressable half of the dependency-env spec's
    /// "The reserved set" table (nine names across three types).  A dependency <c>env:</c> key
    /// listed here for its own type is REFUSED (REQ-004): <see cref="Map"/> throws before any
    /// container starts, naming the variable, the dependency and the type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a refusal and not an ordering.</b>  Aspire's <c>WithEnvironment</c> registers an
    /// <c>EnvironmentCallbackAnnotation</c>; the callbacks run in registration order and write
    /// into ONE dictionary, so the LAST write wins.  Applying the author's map after
    /// <c>DependencyRegistration.Build</c> therefore lets the author silently replace an engine
    /// value that the engine also advertises to every other scenario through
    /// <c>${conn:...}</c>, and no ordering of the two writes can deliver "engine wins".  T3
    /// delivered it by never writing the key (a warned skip); REQ-004 tightened that to a refusal,
    /// because a variable an author declared and the engine silently discarded is the
    /// schema-acceptance-is-not-execution shape this feature exists to avoid reproducing.
    /// </para>
    /// <para>
    /// <b>NAMES only, never values.</b>  Each engine value keeps a single source in its own
    /// registration lambda above; only the name is duplicated here, which is exactly what
    /// REQ-004's census test is designed to police.
    /// </para>
    /// <para>
    /// <b>Per type, not global.</b>  A name reserved for <c>elasticsearch</c> is unreserved on
    /// <c>postgres</c>.  The ten types with no entry here reserve nothing, and every one of the
    /// nine names is asserted APPLIED on a type that does not reserve it — without that half the
    /// check would degrade to a global denylist.
    /// </para>
    /// <para>
    /// <b>Matching is ordinal and case-sensitive</b> (EDGE-005), because container environment
    /// variables are case-sensitive on Linux: folding case would refuse a legitimately distinct
    /// variable.  Both directions are asserted — <c>es_java_opts</c> on <c>elasticsearch</c> is
    /// applied, <c>ES_JAVA_OPTS</c> on <c>elasticsearch</c> is refused.  Flip the per-type NAME
    /// set's comparer below (the <see cref="HashSet{T}"/> one — not the outer type-keyed
    /// <see cref="Dictionary{TKey, TValue}"/>'s, which governs <c>type:</c> lookup) to
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> and the lower-case row goes red.
    /// </para>
    /// <para>
    /// <b>Boundary.</b>  This covers what THIS FILE sets.  Aspire's own <c>AddPostgres</c> /
    /// <c>AddRedis</c> / <c>AddSqlServer</c> (and the images themselves) set variables that never
    /// appear in this source; colliding with one of those is not detected here.
    /// </para>
    /// <para>
    /// The kafka schema-registry and azureservicebus SQL sidecars also receive engine-set
    /// variables, but they are named <c>&lt;name&gt;-sr</c> / <c>&lt;name&gt;-sqledge</c> — names
    /// no author can write — and a dependency's <c>env:</c> never reaches them.  They are
    /// deliberately absent so a later reader does not "complete" this table with unreachable
    /// names.
    /// </para>
    /// <para>
    /// Declared as a concrete <see cref="Dictionary{TKey, TValue}"/> rather than
    /// <c>IReadOnlyDictionary</c> because this project enforces CA1859 as an error; the values
    /// stay <see cref="IReadOnlySet{T}"/>, which CA1859 does not object to.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, IReadOnlySet<string>> s_engineSetEnvKeys =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["elasticsearch"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "discovery.type",
                "xpack.security.enabled",
                "ES_JAVA_OPTS",
                "cluster.routing.allocation.disk.threshold_enabled",
            },
            ["minio"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "MINIO_ROOT_USER",
                "MINIO_ROOT_PASSWORD",
            },
            ["azureservicebus"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "ACCEPT_EULA",
                "MSSQL_SA_PASSWORD",
                "SQL_SERVER",
            },
        };

    /// <summary>
    /// The empty <c>${conn:...}</c> accessor table passed to <see cref="ApplyEnv"/> for a
    /// DEPENDENCY's own <c>env:</c> — a dependency is a connection source, not a consumer, so
    /// <see cref="ValidateEnvValue"/> has already refused every <c>${conn:...}</c> reference by
    /// the time this is used.  Shared so the common path allocates nothing.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, DependencyEnvAccess> s_noEnvAccess =
        new Dictionary<string, DependencyEnvAccess>(StringComparer.Ordinal);

    /// <summary>
    /// Validates every <c>${conn:...}</c> / <c>${secret:...}</c> reference in a single
    /// service or dependency <c>env:</c> value, throwing <see cref="ArgumentException"/> on
    /// the first problem found.  Called eagerly, before any builder mutation (mirrors the
    /// service-shape and dependency-type validation above).
    /// </summary>
    /// <param name="ownerLabel">
    /// <c>"Service"</c> or <c>"Dependency"</c> — the subject NOUN of every diagnostic this
    /// method raises, mirroring the same widening <c>YamlDocumentParser.ParseEnvMap</c> received.
    /// With <c>"Service"</c> the messages below are byte-identical to the ones this method raised
    /// before dependency <c>env:</c> existed; changing service <c>env:</c> behaviour in any way,
    /// diagnostics included, is out of scope for that feature.
    /// </param>
    /// <param name="ownerName">The owning service's or dependency's logical (map-key) name.</param>
    /// <param name="connectionReferencesSupported">
    /// <see langword="true"/> for a SERVICE, whose <c>env:</c> may consume
    /// <c>${conn:name[.part]}</c>; <see langword="false"/> for a DEPENDENCY, where any
    /// <c>${conn:...}</c> is REFUSED outright, naming the reference.  A managed dependency is a
    /// connection SOURCE, not a consumer (dependency-env decision 2): barring the reference here
    /// removes self-reference and inter-dependency cycles, so the engine needs no build-order
    /// graph and no cycle detector.  It is deliberately a refusal rather than a lookup — strictly
    /// simpler than the service-side resolution, and it must not be relaxed later, because a
    /// reference that genuinely WORKED on a released engine could not be withdrawn inside v1.x.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when the value contains a <c>${secret:...}</c> reference (§17 — a container's
    /// environment is the wrong PLACE for a secret, whatever moment it would resolve at),
    /// contains a <c>${conn:...}</c> reference while
    /// <paramref name="connectionReferencesSupported"/> is <see langword="false"/>,
    /// names an unknown dependency,
    /// names an <c>azureservicebus</c> dependency (unsupported by <c>env:</c> in v1), or uses
    /// an unsupported <c>.part</c> accessor for the referenced dependency's kind.
    /// </exception>
    private static void ValidateEnvValue(
        string ownerLabel,
        string ownerName,
        string envKey,
        string envValue,
        IReadOnlyDictionary<string, DependencySpec> dependencies,
        bool connectionReferencesSupported)
    {
        // Sigil-PRESENCE check (mirrors SecretReference.ValidateField, §17), not a well-formed-
        // token regex match: env: supports NO secret references at all, not even well-formed
        // ones, so a malformed token such as '${secret:env}' (missing '/path') must be rejected
        // too — it would otherwise silently pass through as opaque literal text instead of
        // surfacing the author's mistake.
        //
        // THE RATIONALE BELOW IS ABOUT PLACE, AND IT USED TO BE ABOUT TIMING. The message opened
        // with "Secrets resolve at step-execution time, never at container-build time", which the
        // client-key-password series made false: environment-level `security.clientKeyPassword`
        // resolves at first use of the certificate material — after the topology is up and BEFORE
        // any step runs — so "step-execution time" was never a property of secret resolution, only
        // of a STEP's own field. See SecretReference's header for the invariant that survives (at
        // run time, never at compile time), and EnvironmentSecurityValidator's own sigil refusal
        // for the sibling that abandoned the same timing argument for a scoping one. The refusal
        // itself never rested on the timing claim: what makes a container's environment wrong for
        // a secret is that anyone who can run `docker inspect` reads it, which is true at every
        // moment. Do not reintroduce a resolution-moment argument here.
        // CASE-INSENSITIVE, and deliberately so (#428). `${SECRET:vault/db/pw}` used to
        // reach the container as opaque literal text: SecretReference.Sigil is lower-case
        // and this comparison was Ordinal, so the wrong-case attempt matched nothing and
        // passed straight through. No value leaked — nothing in the engine resolves
        // `${SECRET:` either — but the author believes they wrote a secret reference, the
        // suite is green, and the container holds the literal string. That is the exact
        // argument s_envSigilPattern already carries for its own IgnoreCase, and it applies
        // here verbatim. Widening is safe in a way it would NOT be on a secret-SUPPORTING
        // field: env: accepts no secret reference in any case, well-formed or not, so a
        // case-insensitive match can only ever turn a silent pass-through into a refusal.
        // The MESSAGE is deliberately unchanged: it is pinned byte-identical by
        // Map_ServiceEnv_SecretReference_MessageIsByteIdenticalToPreFeatureWording and
        // mirrored in the DSL spec and CHANGELOG. "references a ${secret:...} value"
        // names the fault correctly whatever case the author typed, so widening the
        // match needed no wording change to stay accurate.
        if (envValue.Contains(SecretReference.Sigil, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"{ownerLabel} '{ownerName}' env entry '{envKey}' references a ${{secret:...}} value. " +
                "A container's environment is the wrong PLACE for a secret, whenever it would " +
                "resolve (§17): baking a secret into a container's environment would expose it " +
                "via 'docker inspect' and corrupt the reproducibility envelope (which hashes the " +
                "reference, never the value). Configure the SUT to resolve the secret itself " +
                "instead.",
                nameof(envValue));
        }

        foreach (Match m in s_connRefPattern.Matches(envValue))
        {
            var depName = m.Groups[1].Value;
            var part = m.Groups[2].Success ? m.Groups[2].Value : null;

            // dependency-env decision 2: on a DEPENDENCY this is refused before the name is even
            // looked up, so an author writing '${conn:nosuch}' is told the construct is not
            // available here rather than being told the name is unknown — the second message
            // would imply that declaring the dependency would have made it work.
            if (!connectionReferencesSupported)
            {
                throw new ArgumentException(
                    $"{ownerLabel} '{ownerName}' env entry '{envKey}' references '{m.Value}'. " +
                    "A managed dependency is a connection SOURCE, not a consumer: " +
                    "'${conn:...}' is supported only in a service's own 'env:' values. Barring " +
                    "it on a dependency removes self-reference and inter-dependency cycles " +
                    "outright, so the engine needs no build-order graph and no cycle detector. " +
                    "Configure the value literally, via '${env:NAME}', or move the consumer into " +
                    "'environment.services'.",
                    nameof(envValue));
            }

            if (!dependencies.TryGetValue(depName, out var depSpec))
            {
                throw new ArgumentException(
                    $"{ownerLabel} '{ownerName}' env entry '{envKey}' references unknown dependency " +
                    $"'{depName}' via '{m.Value}'. Declared dependencies: " +
                    (dependencies.Count == 0
                        ? "(none)."
                        : string.Join(", ", dependencies.Keys.OrderBy(k => k, StringComparer.Ordinal)) + "."),
                    nameof(envValue));
            }

            if (string.Equals(depSpec.Type, "azureservicebus", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"{ownerLabel} '{ownerName}' env entry '{envKey}' references dependency " +
                    $"'{depName}' of type 'azureservicebus', which env: references do not support " +
                    "in v1 (the emulator has no stable container-native connection-string " +
                    "resolution path yet). Wire the SUT's Service Bus connection another way.",
                    nameof(envValue));
            }

            if (part is not null && !GetSupportedEnvParts(depSpec.Type).Contains(part))
            {
                throw new ArgumentException(
                    $"{ownerLabel} '{ownerName}' env entry '{envKey}' references unsupported part " +
                    $"'{part}' of dependency '{depName}' (type '{depSpec.Type}'). Supported parts: " +
                    $"{string.Join(", ", GetSupportedEnvParts(depSpec.Type).OrderBy(p => p, StringComparer.Ordinal))}.",
                    nameof(envValue));
            }
        }

        // m5 fix (fix round 2): sigil-PRESENCE check, mirroring the ${secret:...} check
        // above — env: previously used a well-formed-match-ONLY scan (below), which by
        // construction never sees anything malformed, so '${env:}', '${env:2BAD}',
        // '${ENV:GOOD}', and '${env: SPACED }' all validated PASS and reached the container
        // as opaque literal text: a silent wrong value from a typo, against EDGE-008's
        // stated purpose (surface the author's mistake, never guess). For every sigil-shaped
        // occurrence (matched case-insensitively — see s_envSigilPattern's own remarks),
        // confirm a WELL-FORMED s_envRefPattern match starts at that exact index; a mismatch
        // means the sigil was found but the token was not well-formed at that position.
        foreach (Match sigil in s_envSigilPattern.Matches(envValue))
        {
            var wellFormed = s_envRefPattern.Match(envValue, sigil.Index);
            if (!wellFormed.Success || wellFormed.Index != sigil.Index)
            {
                throw new ArgumentException(
                    $"{ownerLabel} '{ownerName}' env entry '{envKey}' contains a '${{env:...}}'-" +
                    $"shaped token at position {sigil.Index} that is not well-formed. An " +
                    "'env:' reference must exactly match '${env:NAME}' — a case-sensitive, " +
                    "lower-case 'env:' sigil (never 'ENV:'/'Env:'), immediately followed by a " +
                    "variable name starting with a letter or underscore (no spaces, no other " +
                    "leading characters), and a closing '}' with nothing else inside.",
                    nameof(envValue));
            }
        }

        // REQ-006/EDGE-008: ${env:NAME} passthrough — resolved from the engine PROCESS's own
        // environment at topology-build time (this eager pass, mirroring the ${conn:...} loop
        // above). EDGE-008 is strict: an UNSET variable fails the suite here, naming the
        // variable — never a silent empty-string substitution, which could turn a secured
        // configuration value into an empty (and possibly insecure-by-default) one with no
        // visible error. Environment.GetEnvironmentVariable returns null for an UNSET variable
        // and "" for one explicitly set empty, which is exactly EDGE-008's unset-vs-empty
        // distinction — an explicitly-empty value is a separate, author-visible choice the
        // engine honours as-is, so only a null (never an empty string) throws below.
        foreach (Match m in s_envRefPattern.Matches(envValue))
        {
            var varName = m.Groups[1].Value;

            if (Environment.GetEnvironmentVariable(varName) is null)
            {
                throw new ArgumentException(
                    $"{ownerLabel} '{ownerName}' env entry '{envKey}' references '{m.Value}', but the " +
                    $"engine process has no environment variable named '{varName}' (EDGE-008). Set " +
                    $"'{varName}' before running the suite, or set it to an explicit empty value if " +
                    "that is genuinely what the service should receive.",
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
        // Concrete Dictionary, not IReadOnlyDictionary: this is a private helper with a
        // single call site that already holds the concrete staging map, and the interface
        // buys nothing but an indirection on every lookup (CA1859). Read-only use here is
        // a convention of this method, not something the parameter type enforces.
        Dictionary<string, EndpointReference> serviceEndpoints)
    {
        if (string.Equals(dependencyType, "mailpit", StringComparison.Ordinal))
        {
            var smtp = serviceEndpoints[MailpitSmtpServiceName(name)];
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

    /// <summary>The kind of span <see cref="EnvValueToken"/> represents.</summary>
    private enum EnvValueTokenKind
    {
        /// <summary>Literal text, spliced verbatim (brace-escaped).</summary>
        Literal,

        /// <summary>A <c>${conn:name}</c> / <c>${conn:name.part}</c> dependency reference.</summary>
        ConnRef,

        /// <summary>A <c>${env:NAME}</c> process-environment reference (REQ-006).</summary>
        EnvVar,
    }

    /// <summary>
    /// A single literal-text, <c>${conn:...}</c>-reference, or <c>${env:...}</c>-reference
    /// span of an env value. For <see cref="EnvValueTokenKind.ConnRef"/>, <c>Name</c> is the
    /// dependency name and <c>Part</c> is the optional part accessor; for
    /// <see cref="EnvValueTokenKind.EnvVar"/>, <c>Name</c> is the environment-variable name
    /// and <c>Part</c> is unused (always <see langword="null"/>).
    /// </summary>
    private readonly record struct EnvValueToken(EnvValueTokenKind Kind, string Literal, string? Name, string? Part);

    /// <summary>
    /// Splits an env value into literal-text, <c>${conn:name[.part]}</c>-reference, and
    /// <c>${env:NAME}</c>-reference tokens, left to right — the two reference patterns are
    /// matched independently (see <see cref="s_envRefPattern"/>'s own remarks) and merged
    /// here into one interleaved stream. Shared by <see cref="BuildEnvExpression"/>
    /// (validation already ran in <see cref="ValidateEnvValue"/>, which scans the same two
    /// patterns).
    /// </summary>
    private static IEnumerable<EnvValueToken> TokeniseEnvValue(string value)
    {
        var pos = 0;
        while (pos < value.Length)
        {
            var connMatch = s_connRefPattern.Match(value, pos);
            var envMatch = s_envRefPattern.Match(value, pos);

            // Pick whichever pattern matches EARLIER (leftmost-wins); a tie is unreachable
            // in practice ('${conn:' and '${env:' cannot both start at the same index), but
            // ties toward conn for a deterministic, arbitrary-but-stable choice.
            Match next;
            EnvValueTokenKind kind;
            if (envMatch.Success && (!connMatch.Success || envMatch.Index < connMatch.Index))
            {
                next = envMatch;
                kind = EnvValueTokenKind.EnvVar;
            }
            else if (connMatch.Success)
            {
                next = connMatch;
                kind = EnvValueTokenKind.ConnRef;
            }
            else
            {
                yield return new EnvValueToken(EnvValueTokenKind.Literal, value[pos..], null, null);
                yield break;
            }

            if (next.Index > pos)
                yield return new EnvValueToken(EnvValueTokenKind.Literal, value[pos..next.Index], null, null);

            yield return kind == EnvValueTokenKind.EnvVar
                ? new EnvValueToken(EnvValueTokenKind.EnvVar, string.Empty, next.Groups[1].Value, null)
                : new EnvValueToken(
                    EnvValueTokenKind.ConnRef, string.Empty, next.Groups[1].Value,
                    next.Groups[2].Success ? next.Groups[2].Value : null);

            pos = next.Index + next.Length;
        }
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
    /// <param name="ownerLabel">
    /// <c>"Service"</c> or <c>"Dependency"</c> — the subject noun of the fail-closed messages
    /// below, matching <see cref="ValidateEnvValue"/>'s own widening so a throw from either pass
    /// names the same subject.
    /// </param>
    /// <param name="ownerName">
    /// The owning service's or dependency's own name, used ONLY to build a matching, fail-closed
    /// <see cref="ArgumentException"/> message (G-M1) if <paramref name="envKey"/>'s value
    /// somehow reaches this method referencing an unset <c>${env:NAME}</c> variable, or a
    /// <c>${conn:...}</c> dependency with no resolved accessor — see each case's own remarks
    /// below.
    /// </param>
    /// <param name="envKey">The owner's own env-map key <paramref name="value"/> is bound to.</param>
    /// <param name="value">The raw env value text to tokenise and splice.</param>
    private static ReferenceExpression BuildEnvExpression(
        string ownerLabel,
        string ownerName,
        string envKey,
        string value,
        IReadOnlyDictionary<string, DependencyEnvAccess> envAccessByDependency)
    {
        var builder = new ReferenceExpressionBuilder();
        foreach (var token in TokeniseEnvValue(value))
        {
            switch (token.Kind)
            {
                case EnvValueTokenKind.Literal:
                    if (token.Literal.Length > 0)
                        builder.AppendLiteral(EscapeLiteralBraces(token.Literal));
                    break;

                case EnvValueTokenKind.EnvVar:
                    // REQ-006 / EDGE-008: re-read the CURRENT process environment here rather
                    // than threading a value resolved during ValidateEnvValue's eager pass
                    // through the token stream — mirrors this file's existing two-pass shape
                    // (every env: value is independently re-scanned by BuildEnvExpression
                    // after ValidateEnvValue already validated it). ValidateEnvValue has
                    // already confirmed this variable is SET (non-null) before Configure (and
                    // so this method) ever runs, in the real Map()→Configure() pipeline.
                    //
                    // G-M1 (gatekeeper): the throw below is FAIL-CLOSED, not a defensive
                    // fallback — EDGE-008 is strict precisely because a silent empty-string
                    // substitution could turn a secured configuration value into an empty
                    // (and possibly insecure-by-default) one with no visible error (see
                    // ValidateEnvValue's own identical reasoning). A prior revision spliced
                    // '?? string.Empty' here on the reasoning that ValidateEnvValue's eager
                    // pass already makes this unreachable in the live pipeline — true today,
                    // but that made this branch FAIL OPEN for any caller that reaches
                    // BuildEnvExpression without going through that eager pass first (a
                    // direct unit test, or a future refactor that drops/reorders the eager
                    // check), silently defeating the very guarantee EDGE-008 exists to give
                    // authors. Throwing the SAME ArgumentException ValidateEnvValue itself
                    // raises keeps this branch correct on its own terms, independent of
                    // whatever ran before it.
                    var envVarValue = Environment.GetEnvironmentVariable(token.Name!)
                        ?? throw new ArgumentException(
                            $"{ownerLabel} '{ownerName}' env entry '{envKey}' references " +
                            $"'${{env:{token.Name}}}', but the engine process has no environment " +
                            $"variable named '{token.Name}' (EDGE-008). Set '{token.Name}' before " +
                            "running the suite, or set it to an explicit empty value if that is " +
                            "genuinely what the service should receive.",
                            nameof(value));
                    builder.AppendLiteral(EscapeLiteralBraces(envVarValue));
                    break;

                case EnvValueTokenKind.ConnRef:
                    // FAIL-CLOSED, for the same reason the ${env:NAME} branch directly above
                    // argues for itself. What the TryGetValue buys, stated exactly: a bare indexer
                    // here throws an opaque KeyNotFoundException that names nothing, and this
                    // turns the same defect into a LOCATED authoring diagnostic naming the owner,
                    // the env key and the reference. That is the whole of the gain.
                    //
                    // IT DOES NOT REPAIR THE §12.1 VERDICT CLASS, and an earlier draft of this
                    // comment asserted that it did. Measured: BuildEnvExpression is reachable only
                    // from ApplyEnv, only from inside the Configure closure, and
                    // SuiteTopology.StartAsync wraps HeadlessTopology.StartAsync in
                    // `catch (Exception ex)` → OrchestrationException. Since #348 that wrap has
                    // exactly ONE exemption, and a plain ArgumentException is not it: only
                    // TopologyAuthoringException is re-thrown unwrapped. Old throw and new throw
                    // therefore still surface identically, as an Environment error. Do not read
                    // this seam as taxonomy-safe — and if a future change wants it to be, the fix
                    // is to raise a TopologyAuthoringException here, not to widen that catch.
                    //
                    // The PRIMARY refusal is a DIFFERENT seam and is untouched by any of this:
                    // ValidateEnvValue refuses every ${conn:...} on a dependency eagerly, thrown
                    // from Map itself and NOT from the closure, so the taxonomy argument for that
                    // refusal stands on its own. This layer is defence-in-depth for the paths that
                    // bypass the eager pass — a direct unit test, or a future refactor that drops
                    // or reorders it — where the author would otherwise get an unnamed
                    // KeyNotFoundException in place of a diagnostic.
                    if (!envAccessByDependency.TryGetValue(token.Name!, out var access))
                    {
                        throw new ArgumentException(
                            $"{ownerLabel} '{ownerName}' env entry '{envKey}' references " +
                            $"'${{conn:{token.Name}}}', but no connection accessor was resolved " +
                            $"for dependency '{token.Name}'. A '${{conn:...}}' reference is " +
                            "supported only in a service's own 'env:' values, and only for a " +
                            "dependency declared in the same 'environment' block.",
                            nameof(value));
                    }

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
                    break;
            }
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
    /// Resolves the resource builder a managed dependency's OWN container-level configuration is
    /// applied to — its <c>env:</c> mapping and its <c>security.serverArtifacts</c> copies
    /// (<c>#426</c>) — namely the single <see cref="ContainerResource"/> named exactly the
    /// declared dependency name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not the retained builder.</b>  For the four database-backed types (<c>postgres</c>,
    /// <c>sqlserver</c>, <c>mysql</c>, <c>mongodb</c>) the retained builder is the
    /// <c>AddDatabase</c> CHILD, which is not a container and does not implement
    /// <see cref="IResourceWithEnvironment"/> at all — measured, so this is not a matter of
    /// taste: <c>PostgresDatabaseResource</c> reports <c>container=False withEnv=False</c>, and
    /// <c>IResourceBuilder&lt;out T&gt;</c> is covariant, so it converts UP and never back down.
    /// </para>
    /// <para>
    /// <b>Why by name.</b>  Measured across all thirteen dependency types: the resource named
    /// exactly the declared dependency name is a <see cref="ContainerResource"/> implementing
    /// <see cref="IResourceWithEnvironment"/>, and there is exactly one of it.  The two sidecars
    /// carry distinct names (<c>&lt;name&gt;-sr</c>, <c>&lt;name&gt;-sqledge</c>), so name
    /// equality cannot reach them.  The alternative — a third element on
    /// <c>DependencyRegistration.Build</c>'s tuple — is thirteen more hand-maintained entries with
    /// no compiler check that any of them names a container, on a tuple whose EXISTING two
    /// elements are already the wrong resource for four of the thirteen.
    /// </para>
    /// <para>
    /// <b>Call it INSIDE the dependency loop.</b>  That loop runs before the services loop, so no
    /// service container exists yet and a service sharing a dependency's name cannot be matched.
    /// That ordering is the weakest of three defences, not the only one: <c>ProviderPipeline</c>
    /// refuses a suite whose service and dependency share a name at validation time, and Aspire's
    /// own <c>AddResource</c> refuses a duplicate resource name (case-insensitively, via
    /// <c>StringComparers.ResourceName</c>), so the second registration would throw before this
    /// method ever ran.
    /// </para>
    /// <para>
    /// The <c>SingleOrDefault(...) ?? throw</c> asserts the name→resource invariant rather than
    /// assuming it, so a fourteenth dependency type whose registration breaks it fails loudly at
    /// topology-build time instead of silently no-op'ing the author's <c>env:</c> or artefact
    /// copies.  The <c>DependencyEnvCensusTests</c> gate catches it earlier still, in CI.
    /// </para>
    /// <para>
    /// <b>Resolve it only when a consumer needs it</b> — this throw is the reason. A dependency
    /// declaring neither <c>env:</c> nor <c>security.serverArtifacts</c> must not be exposed to
    /// it, so both call sites are guarded and share one lazily-resolved local.
    /// </para>
    /// </remarks>
    private static IResourceBuilder<ContainerResource> ResolveDependencyContainer(
        IDistributedApplicationBuilder builder,
        string name,
        string type)
    {
        var resource = builder.Resources
            .OfType<ContainerResource>()
            .SingleOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Dependency '{name}' (type '{type}') registered no container resource of its " +
                "own name, so its own container-level configuration ('env:', " +
                "'security.serverArtifacts') cannot be applied. This is an ENGINE defect, not an " +
                "authoring one: every dependency type must register exactly one container named " +
                "exactly the declared dependency name.");

        return builder.CreateResourceBuilder(resource);
    }

    /// <summary>
    /// Applies a service's or managed dependency's <c>env:</c> mapping (if any) to
    /// <paramref name="builder"/> via <c>WithEnvironment(name, ReferenceExpression)</c> — works
    /// identically for image-form (<see cref="ContainerResource"/>) and project-form
    /// (<c>ProjectResource</c>) services and for a dependency's own container resource, all of
    /// which implement <see cref="IResourceWithEnvironment"/>.
    /// </summary>
    /// <param name="ownerLabel">
    /// <c>"Service"</c> or <c>"Dependency"</c> — threaded through to
    /// <see cref="BuildEnvExpression"/> alongside <paramref name="ownerName"/>.
    /// </param>
    /// <param name="ownerName">
    /// The owning service's or dependency's own name — threaded through to
    /// <see cref="BuildEnvExpression"/> so a fail-closed <c>${env:NAME}</c> throw (G-M1) can name
    /// the same subject/key <see cref="ValidateEnvValue"/>'s own eager-pass throw would have
    /// named.
    /// </param>
    /// <param name="builder">The resource whose environment the mapping is written to.</param>
    /// <param name="env">
    /// The mapping to apply, or <see langword="null"/> when the owner declared none.  For a
    /// DEPENDENCY it has already cleared <see cref="Map"/>'s eager passes — an engine-set name
    /// for that dependency's type refused the suite outright, so none reaches here.
    /// </param>
    /// <param name="envAccessByDependency">
    /// The <c>${conn:...}</c> accessor table, or <see cref="s_noEnvAccess"/> for a DEPENDENCY,
    /// whose <c>${conn:...}</c> references <see cref="ValidateEnvValue"/> has already refused.
    /// </param>
    private static void ApplyEnv<T>(
        string ownerLabel,
        string ownerName,
        IResourceBuilder<T> builder,
        IReadOnlyDictionary<string, string>? env,
        IReadOnlyDictionary<string, DependencyEnvAccess> envAccessByDependency)
        where T : IResourceWithEnvironment
    {
        if (env is null)
            return;

        foreach (var (key, value) in env)
        {
            var expression = BuildEnvExpression(ownerLabel, ownerName, key, value, envAccessByDependency);
            builder.WithEnvironment(key, expression);
        }
    }

    // -----------------------------------------------------------------------
    // Authorable health checks (services-generalisation spec, REQ-009).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Applies <paramref name="spec"/>'s <see cref="ServiceSpec.HealthCheck"/> (or, when
    /// absent, the appropriate default) to <paramref name="containerBuilder"/>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><description>
    ///     No <c>healthCheck</c> declared, no explicit <c>ports</c> (the pre-existing
    ///     HTTP-only-by-default shape) — preserves today's EXACT default byte-for-byte:
    ///     <c>WithHttpHealthCheck(path: "/", endpointName: "http")</c> (existing
    ///     <c>EnvironmentMapperTests</c> pin this).
    ///   </description></item>
    ///   <item><description>
    ///     No <c>healthCheck</c> declared, WITH explicit <c>ports</c> AND a sibling
    ///     <c>httpPort</c> (the hybrid shape) — M4 fix (fix round 2): treat it as what it
    ///     actually is, an image-form HTTP service, and apply the SAME default HTTP probe
    ///     (<c>WithHttpHealthCheck(path: "/", endpointName: "http")</c>) rather than leaving
    ///     it ungated. Before this fix a hybrid service with no explicit <c>healthCheck</c>
    ///     fell into the "no implicit check" branch below purely because
    ///     <c>hasExplicitPorts</c> is true — <c>docs/02</c> never actually promised that for
    ///     the hybrid shape (it only documents the http-only and ports-only defaults), it was
    ///     simply an oversight in the boolean split.
    ///   </description></item>
    ///   <item><description>
    ///     No <c>healthCheck</c> declared, WITH explicit <c>ports</c> and NO <c>httpPort</c>
    ///     (ports-only) — M4 fix (fix round 2): default to a <c>tcp</c> probe on the FIRST
    ///     declared port (<c>spec.Ports[0]</c>) via the SAME <see cref="ApplyTcpHealthCheck"/>
    ///     helper the explicit <c>type: tcp</c> branch below uses. Before this fix the service
    ///     was left with NO <see cref="HealthCheckAnnotation"/> at all, so per the pinned
    ///     Aspire 13.4.2 XML docs for <c>WaitForResourceHealthyAsync</c>, a resource with no
    ///     health-check annotation "will be considered healthy once it reaches a Running
    ///     state" — combined with B1 (the tcp health check that could never fail), BOTH shapes
    ///     available to a non-HTTP system under test were ungated. A protocol-agnostic tcp
    ///     probe is strictly better than "Running" and costs nothing once B1's bounded
    ///     zero-byte-read discriminator makes a bare tcp probe meaningful.
    ///   </description></item>
    ///   <item><description>
    ///     <c>type: http</c> — <c>WithHttpHealthCheck(path: healthCheck.Path ?? "/",
    ///     endpointName: "http")</c>, the explicit spelling of the same default.
    ///   </description></item>
    ///   <item><description>
    ///     <c>type: tcp</c> — a raw TCP connect (plus B1's bounded zero-byte-read
    ///     discriminator — see <see cref="ApplyTcpHealthCheck"/>) against the resolved
    ///     endpoint, registered as a named async health check
    ///     (<c>IServiceCollection.AddHealthChecks().AddAsyncCheck</c>) and attached via
    ///     Aspire's generic <c>WithHealthCheck(key)</c>; issues NO HTTP request at all.
    ///     <c>Map()</c>'s eager validation loop has already confirmed
    ///     <see cref="HealthCheckSpec.Port"/> is set and matches a declared port/httpPort, so
    ///     both are used unchecked here.
    ///   </description></item>
    /// </list>
    /// </remarks>
    private static IResourceBuilder<ContainerResource> ApplyHealthCheck(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ContainerResource> containerBuilder,
        string serviceName,
        ServiceSpec spec,
        bool hasExplicitPorts,
        IReadOnlyList<ServiceEndpointDeclaration> endpoints)
    {
        var healthCheck = spec.HealthCheck;
        var securedEndpoint = endpoints.FirstOrDefault(e => e.IsSecured);

        if (healthCheck is null && securedEndpoint is not null)
        {
            // REQ-023 default: a TCP probe on the secured endpoint, never an HTTP one.
            //
            // For a service whose ONLY endpoint is the secured one, this is a ceiling. A
            // container health check cannot present a client certificate, so no generic
            // health-gating mechanism can confirm a mutual-TLS listener is correctly
            // configured — measured on the test bed, an unauthenticated request to an
            // `ssl_verify_client on` listener completes the TLS handshake in full and is
            // answered 400 at the HTTP layer, so an HTTP probe would hold the topology
            // unhealthy forever for a service that is working perfectly. A TCP probe proves a
            // listener accepted a connection and nothing more; confirming that the endpoint is
            // actually SECURED is REQ-005's engine-side probe, which presents the declared
            // client certificate and therefore cannot live in Aspire's health gating at all.
            //
            // For the one OTHER shape that reaches here — a secured service with a genuine
            // sibling plaintext `httpPort` on a DIFFERENT port — it is a TRADE, not a ceiling:
            // that plaintext endpoint is probeable, and an HTTP probe against it would be
            // strictly more informative than a TCP connect. Defaulting to TCP anyway keeps ONE
            // default for "a service that declares security" instead of a rule whose answer
            // depends on a second field, and an author who wants the stronger probe declares
            // `healthCheck: { type: http }` explicitly — which is accepted for exactly this
            // shape, because the "http" endpoint still exists on its own port. Same escape for
            // the ceiling case: an explicit healthCheck against a separate unsecured health
            // port, the shape real mTLS services use.
            return ApplyTcpHealthCheck(
                builder, containerBuilder, serviceName, securedEndpoint.Port, securedEndpoint.Name);
        }

        if (healthCheck is null)
        {
            if (!hasExplicitPorts)
            {
                return containerBuilder.WithHttpHealthCheck(
                    path: "/", endpointName: ServiceEndpointNaming.HttpEndpointName);
            }

            if (spec.HttpPort is not null)
            {
                // M4 fix: hybrid shape (ports + httpPort, no explicit healthCheck) — it IS an
                // image-form HTTP service, so it gets the same default HTTP probe one.
                return containerBuilder.WithHttpHealthCheck(
                    path: "/", endpointName: ServiceEndpointNaming.HttpEndpointName);
            }

            // M4 fix: ports-only, no explicit healthCheck — default to a tcp probe on the
            // first declared port rather than leaving the service ungated. This is
            // STRICTLY BETTER than Aspire's own "healthy once Running" default (it proves a
            // listener actually accepted a connection) but it is NOT a readiness guarantee:
            // a tcp probe is deliberately protocol-agnostic and never inspects
            // application-layer traffic, so it cannot detect a server whose listening
            // socket opens before its own internal startup finishes (a routine pattern, not
            // an edge case — e.g. a broker that starts accepting TCP connections a handful
            // of milliseconds before it is ready to serve its protocol). A non-HTTP system
            // under test with a slow post-listen startup may still need its first step
            // written to tolerate a brief warm-up (verifyMode: RETRY) — see docs/02
            // §3.2.6a's "What a tcp health check does not prove" note, which this default
            // inherits verbatim.
            var defaultPort = spec.Ports![0];
            var defaultEndpointName = ServiceEndpointNaming.TcpEndpointName(defaultPort);
            return ApplyTcpHealthCheck(builder, containerBuilder, serviceName, defaultPort, defaultEndpointName);
        }

        if (string.Equals(healthCheck.Type, "http", StringComparison.Ordinal))
        {
            var path = string.IsNullOrEmpty(healthCheck.Path) ? "/" : healthCheck.Path;
            return containerBuilder.WithHttpHealthCheck(
                path: path, endpointName: ServiceEndpointNaming.HttpEndpointName);
        }

        if (string.Equals(healthCheck.Type, "tcp", StringComparison.Ordinal))
        {
            // Map()'s eager validation loop guarantees Port is set and is one of this
            // service's own declared ports/httpPort before Configure (and so this method)
            // ever runs.
            var tcpPort = healthCheck.Port!.Value;

            // Resolve the SAME endpoint reference the port was registered under above, by
            // looking the port up in the one endpoint set both this method and the
            // declaration loop were handed: the dedicated tcp-<port> endpoint when the port
            // came from 'ports', the "http" endpoint when it is the service's httpPort, or
            // the secured "https" endpoint when it is the service's own security.endpoint
            // (REQ-023 — a raw TCP connect does not care about the target endpoint's URI
            // scheme, which is what makes probing the secured endpoint meaningful at all).
            // Identical, port for port, to the ports-contains test this replaced for every
            // service that declares no 'security' block.
            var endpointName = endpoints.FirstOrDefault(e => e.Port == tcpPort)?.Name
                ?? ServiceEndpointNaming.HttpEndpointName;

            return ApplyTcpHealthCheck(builder, containerBuilder, serviceName, tcpPort, endpointName);
        }

        // Unreachable once the schema's closed 'type' enum (tcp/http) is in place; defensive
        // fallback for a caller that bypasses schema validation (e.g. a hand-built ServiceSpec
        // in a unit test) — mirrors this file's existing style (e.g.
        // ResolveDependencyEnvAccess's own fallback throw).
        throw new ArgumentException(
            $"Service '{serviceName}' declares 'healthCheck.type' = '{healthCheck.Type}', which " +
            "is not recognised. Supported values: tcp, http.",
            nameof(spec));
    }

    /// <summary>
    /// Registers a raw-TCP health check for <paramref name="tcpPort"/> against
    /// <paramref name="containerBuilder"/>'s <paramref name="endpointName"/> endpoint, and
    /// attaches it via Aspire's generic <c>WithHealthCheck(key)</c>.
    /// </summary>
    /// <remarks>
    /// <b>B1 fix (fix round 2 — the tcp health check that could never fail).</b> A bare
    /// <c>ConnectAsync</c> success does NOT prove a backend is listening: the endpoint this
    /// method probes is the HOST-PUBLISHED, DCP-PROXIED address, and DCP's proxy accepts the
    /// TCP connection UNCONDITIONALLY — before it has even attempted to reach the real
    /// backend — and only then tries the backend itself. Measured (peer review of this
    /// branch's first commit, 4414931): a service with <c>ports: [9093]</c>,
    /// <c>healthCheck: { type: tcp, port: 9093 }</c>, and NOTHING listening on 9093 was
    /// declared "healthy" in ~18 seconds by the pre-fix connect-only check; the same dead port
    /// under a <c>type: http</c> control correctly reported unhealthy, but only after
    /// ~126 seconds.
    /// <para>
    /// This method's own job is the Aspire-specific wiring around the fix: resolve
    /// <paramref name="endpointName"/>, report <see cref="HealthCheckResult.Unhealthy(string?, System.Exception?, System.Collections.Generic.IReadOnlyDictionary{string, object}?)"/>
    /// up front when the endpoint is not yet allocated (pre-<c>StartAsync</c> — an Aspire-only
    /// concern a unit test against a real socket has no equivalent of), and register/attach the
    /// named health check. The actual connect/read discriminator that decides Healthy/Unhealthy
    /// is <see cref="ProbeAsync"/> — extracted into its own method (M4 fix, fix round 3) so it
    /// has a unit test that runs with no Docker/DCP dependency; see its own remarks for the
    /// branch-by-branch logic and what it proves.
    /// </para>
    /// </remarks>
    private static IResourceBuilder<ContainerResource> ApplyTcpHealthCheck(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ContainerResource> containerBuilder,
        string serviceName,
        int tcpPort,
        string endpointName)
    {
        var endpoint = containerBuilder.GetEndpoint(endpointName);

        var healthCheckKey = $"{serviceName}-tcp-{tcpPort}-health";
        builder.Services.AddHealthChecks().AddAsyncCheck(healthCheckKey, ct =>
        {
            if (!endpoint.IsAllocated)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("Endpoint not yet allocated."));
            }

            return ProbeAsync(endpoint.Host, endpoint.Port, ct);
        });

        return containerBuilder.WithHealthCheck(healthCheckKey);
    }

    /// <summary>
    /// Resolves <see cref="Map"/>'s <c>suiteDirectory</c> argument to an absolute path, defaulting
    /// to the current directory (REQ-016).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cref names the method WITHOUT a parameter list on purpose. It carried one —
    /// <c>Map(EnvironmentSpec?, string?)</c> — which stopped matching the moment
    /// <c>kafkaSpeakingTargets</c> was added as a third parameter, and nothing caught it: this
    /// project sets no <c>GenerateDocumentationFile</c>, so crefs here are never resolved and
    /// CS1574 cannot fire. There is exactly one <c>Map</c>, so the bare form is unambiguous and
    /// cannot rot the same way again.
    /// </para>
    /// A malformed value fails HERE, once, rather than inside
    /// <see cref="ServerArtifactInjection.Plan"/> once per declared artefact — the fault is in the
    /// base directory itself, not in any one author-declared field, and the two deserve different
    /// diagnostics. Mirrors <c>EnvironmentSecurityValidator.Validate</c>'s own guard around the
    /// same call. The diagnostic names the ARGUMENT, never its value; see the throw site.
    /// </remarks>
    private static string ResolveSuiteDirectory(string? suiteDirectory)
    {
        var candidate = string.IsNullOrWhiteSpace(suiteDirectory)
            ? Directory.GetCurrentDirectory()
            : suiteDirectory;

        try
        {
            return Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // THE VALUE IS NOT ECHOED (#357's rule, extended). Unlike the author-declared paths
            // elsewhere in this feature, `candidate` has no declared/resolved split to fall back
            // on — it IS an absolute host path in every production caller — and a Map()-time
            // ArgumentException reaches ScenarioRunner's catch, which stamps its text onto every
            // scenario's ScenarioCompletedEvent.message and so into the event stream, the JUnit
            // report and the HTML report. `paramName` names the offending argument and the inner
            // exception carries the fault, which is what the caller (an engine embedder, never a
            // suite author — the CLI's suite directory is the parent of an already-resolved
            // discovered file, so GetFullPath cannot throw on it) needs to act.
            throw new ArgumentException(
                $"the suite directory is not a valid path ({ex.Message}).",
                nameof(suiteDirectory),
                ex);
        }
    }

    /// <summary>
    /// The B1 discriminator (fix round 2 — the tcp health check that could never fail):
    /// connects to <paramref name="host"/>:<paramref name="port"/> and, on a successful
    /// connect, performs a bounded, small-buffer read to distinguish a live backend from
    /// whatever merely ACCEPTED the connection — in production, DCP's host-published TCP proxy,
    /// which accepts unconditionally before it has even attempted to reach the real backend
    /// (see <see cref="ApplyTcpHealthCheck"/>'s own remarks for the pre-fix false-positive this
    /// closes and its measured evidence).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <list type="bullet">
    ///   <item><description>
    ///     Read returns <c>0</c> bytes — whatever accepted the connection closed the pipe
    ///     immediately because the real backend refused/was unreachable →
    ///     <see cref="HealthCheckResult.Unhealthy(string?, System.Exception?, System.Collections.Generic.IReadOnlyDictionary{string, object}?)"/>.
    ///   </description></item>
    ///   <item><description>
    ///     Read times out (no bytes, connection stays open) — the overwhelming majority of
    ///     TCP protocols do not speak first, so a live backend's connection is simply held
    ///     open with nothing to read yet → <see cref="HealthCheckResult.Healthy"/>.
    ///   </description></item>
    ///   <item><description>
    ///     Read returns <c>&gt;0</c> bytes immediately — a server-speaks-first protocol (e.g.
    ///     SMTP, SSH) greeted us unprompted → definitely alive →
    ///     <see cref="HealthCheckResult.Healthy"/>.
    ///   </description></item>
    /// </list>
    /// Measured (fix round 2, on this exact tree): against RAW DOCKER-PUBLISHED PORTS, not
    /// through a DCP-proxied endpoint, three trials in each direction, run independently by two
    /// parties: <c>good=CONNECT_OK READ_TIMEOUT (alive-ish)</c>, <c>bad=CONNECT_OK
    /// READ_BYTES=0</c> — exactly the discriminator this method implements. <b>Measured on
    /// Docker Desktop for Windows only</b> — the Linux/DCP-on-Linux proxy-close-timing
    /// behaviour is UNVERIFIED; the same "accept unconditionally, close on backend failure"
    /// DCP proxy design is documented for both platforms, but this has not been re-confirmed
    /// on Linux CI.
    /// </para>
    /// <para>
    /// <b>What this proves, and what it does not.</b> A <see cref="HealthCheckResult.Healthy"/>
    /// result confirms a listener accepted the connection and stayed open — it is
    /// deliberately protocol-agnostic and never inspects application-layer traffic, so it
    /// cannot detect a server whose listening socket opens before its own internal startup
    /// finishes (a routine pattern for many servers, not an edge case). This is strictly
    /// better than Aspire's own "healthy once Running" default for a resource with no
    /// health-check annotation at all, but it is not a substitute for an application-level
    /// readiness signal; see docs/02 §3.2.6a's "What a tcp health check does not prove" note.
    /// </para>
    /// <para>
    /// <b>Side effect on the probed connection (n-1, fix round 3).</b> Every poll holds the
    /// connection open for up to the 1-second read bound before this method (and the caller's
    /// own disposal of the returned <c>TcpClient</c>) tears it down — and on a server-speaks-
    /// first protocol, consumes exactly one byte of whatever the server sent (the read buffer
    /// is a single byte) before disconnecting. A broker or service operator watching connection
    /// logs will see a client connect, receive at most one byte, then disconnect, once per poll
    /// interval — expected behaviour of this probe, not a malfunctioning client.
    /// </para>
    /// </remarks>
    /// <param name="host">The host to connect to.</param>
    /// <param name="port">The port to connect to.</param>
    /// <param name="ct">
    /// The ambient cancellation token honoured by the health-check infrastructure. Distinct
    /// from this method's own internal connect (3s) and read (1s) timeouts — each a separately
    /// linked <see cref="CancellationTokenSource"/> — so an ambient cancellation propagates to
    /// the caller unchanged (per the verdict taxonomy, a genuinely cancelled check is the
    /// caller's concern, never reported as this probe's own <see cref="HealthCheckResult.Unhealthy(string?, System.Exception?, System.Collections.Generic.IReadOnlyDictionary{string, object}?)"/>).
    /// </param>
    internal static async Task<HealthCheckResult> ProbeAsync(string host, int port, CancellationToken ct)
    {
        using var connectTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectTimeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

        using var tcpClient = new System.Net.Sockets.TcpClient();
        try
        {
            await tcpClient.ConnectAsync(host, port, connectTimeoutCts.Token)
                .ConfigureAwait(false);

            // B1 fix: connect succeeded — this proves only that whatever accepted the
            // connection did so, never that a real backend is listening behind it (see this
            // method's own remarks). Discriminate with a bounded zero-byte-buffer read.
            using var readTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readTimeoutCts.CancelAfter(TimeSpan.FromSeconds(1));

            try
            {
                var stream = tcpClient.GetStream();
                var buffer = new byte[1];
                var bytesRead = await stream.ReadAsync(buffer, readTimeoutCts.Token)
                    .ConfigureAwait(false);

                return bytesRead == 0
                    ? HealthCheckResult.Unhealthy(
                        $"TCP connect to {AuthorityText.Format(host, port)} succeeded, but the " +
                        "connection was closed immediately (zero bytes read) — no backend is " +
                        "listening behind the host-published proxy.")
                    // >0 bytes: a server-speaks-first protocol greeted us unprompted.
                    : HealthCheckResult.Healthy();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The read timed out waiting for data or EOF (readTimeoutCts fired, not the
                // ambient ct): the connection was held open with nothing to read yet — the
                // live-backend signature for the overwhelming majority of TCP protocols,
                // which do not speak first.
                return HealthCheckResult.Healthy();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Catches both a genuine connect failure (SocketException — refused,
            // unreachable, …) and a post-connect read failure other than a timeout (e.g. a
            // reset), plus the connect-stage LINKED timeout firing — the 'when' clause
            // re-throws only if the AMBIENT ct itself requested cancellation (the caller
            // genuinely cancelled the check), which HealthCheckService is expected to
            // observe rather than have swallowed into an Unhealthy result.
            // m3 (gatekeeper, fix round six): rendered through AuthorityText.Format, the same
            // bracket rule SecuredEndpointProbe's observed-address messages use. `host` is
            // bracket-free — that is what the caller above holds, NOT something the ConnectAsync
            // above requires (that reason was asserted across this branch and is refuted: measured
            // on net8.0, ConnectAsync accepts the bracketed literal and connects). It matters
            // HERE because a raw "{host}:{port}" renders an IPv6 literal as `::1:9093` —
            // unparseable and ambiguous about where the address ends. Localhost/IPv4 today; the
            // rule costs nothing and the branch should not close with a known-defective sibling
            // of a defect it fixed.
            return HealthCheckResult.Unhealthy(
                $"TCP probe of {AuthorityText.Format(host, port)} failed: {ex.Message}");
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

    // -----------------------------------------------------------------------
    // Dependency sidecar svc::-key naming (m1 fix, fix round 2).
    // -----------------------------------------------------------------------
    // A dependency's Build lambda ordinarily stages exactly one svc::<name>-shaped key
    // per kind (or none, for the pure dependency-only kinds) — see the class remarks'
    // "conn:: vs svc::" summary. TWO kinds are the exception: they stage an ADDITIONAL
    // sidecar endpoint into svc::<name>-<suffix>, alongside their own conn::<name>. These
    // two suffix-naming functions are the SINGLE source of truth for those extra names —
    // both the Build lambdas below AND GetDependencyServiceSidecarNames (consulted by
    // ProviderPipeline's host-resource-vs-declared-name collision guard) call them, so the
    // two can never drift apart the way the guard's own stated justification once did
    // (see that guard's remarks for the mailpit-smtp / kafka-sr false-negative it used to
    // let through).

    /// <summary>The svc::-staged key for a <c>mailpit</c> dependency's SMTP sidecar endpoint.</summary>
    private static string MailpitSmtpServiceName(string dependencyName) => dependencyName + "-smtp";

    /// <summary>
    /// The svc::-staged key for a <c>kafka</c> dependency's optional schema-registry
    /// sidecar endpoint (present only when <see cref="KafkaWantsSchemaRegistry"/> is true).
    /// </summary>
    private static string KafkaSchemaRegistryServiceName(string dependencyName) => dependencyName + "-sr";

    /// <summary>
    /// Returns the EXTRA <c>svc::&lt;name&gt;</c>-staged keys — beyond the dependency's own
    /// bare name — that a dependency of <paramref name="spec"/>'s type stages during
    /// <c>Configure</c>. Empty for every kind except <c>mailpit</c> (unconditionally, its
    /// SMTP sidecar) and <c>kafka</c> (conditionally, when <c>schemaRegistry: true</c>).
    /// </summary>
    /// <remarks>
    /// Purely declarative — reads only <paramref name="spec"/>, never invokes any dependency
    /// registration's <c>Build</c> lambda — so it is safe to call at validation time, before
    /// any <c>IDistributedApplicationBuilder</c> exists. <c>ProviderPipeline.
    /// BuildProjectContext</c>'s host-resource-vs-declared-name collision guard (m1 fix, fix
    /// round 2) calls this for every declared dependency so a host resource (e.g. a webhook
    /// listener) cannot silently shadow one of these sidecar keys the way a listener named
    /// <c>mail-smtp</c> alongside a <c>mailpit</c> dependency <c>mail</c>, or a listener named
    /// <c>bus-sr</c> alongside a <c>kafka</c> dependency <c>bus</c> with
    /// <c>schemaRegistry: true</c>, previously could — both used to validate PASS, and the
    /// <c>-sr</c> key specifically is read at run time by both Kafka providers, so an Avro
    /// publish would have sent schema-registry traffic to the engine's own listener instead.
    /// </remarks>
    public static IEnumerable<string> GetDependencyServiceSidecarNames(string name, DependencySpec spec) =>
        spec.Type switch
        {
            "mailpit" => new[] { MailpitSmtpServiceName(name) },
            "kafka" when KafkaWantsSchemaRegistry(spec.Extra) =>
                new[] { KafkaSchemaRegistryServiceName(name) },
            _ => Array.Empty<string>(),
        };

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
        // parses the same value first and throws for whitespace there (see its comment), so
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
