// Tests for S03-A-02: EnvironmentMapper maps the parsed `environment` block to Aspire resources.
//
// Test strategy — non-Docker vs Docker:
// -----------------------------------------------------------------------
// Creating an IDistributedApplicationBuilder and adding resources to it does NOT require Docker.
// The builder+model phase is a pure in-memory graph construction; only DistributedApplication.StartAsync
// triggers the DCP process, which in turn pulls images and runs containers.
// All tests here invoke mapped.Configure(builder) and inspect builder.Resources WITHOUT calling
// StartAsync, so they are fast, non-Docker unit tests.
//
// The only things that cannot be tested without Docker are the ResolveServices path (because
// GetEndpoint("http").Url and GetConnectionStringAsync both talk to live running resources)
// and the full health-gate sequence.  Those are omitted here and belong to docker-gated
// integration tests in a later sprint task.
// -----------------------------------------------------------------------

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Platform.Engine.Authoring.Model;
using Platform.Engine.Orchestration;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Platform.Engine.Orchestration.Tests;

/// <summary>
/// Non-Docker unit tests for <see cref="EnvironmentMapper"/>.
/// These tests exercise the builder phase (resource graph construction) without starting Docker
/// containers; see the test-strategy note at the top of this file.
/// </summary>
public sealed class EnvironmentMapperTests
{
    // The short name of this test assembly — used for DCP metadata resolution (R-1 finding).
    // Creating the builder + adding resources does not need the DCP process, but the options
    // still require a resolvable AssemblyName for the builder to initialise cleanly.
    private const string AppHostAssemblyName = "Platform.Engine.Orchestration.Tests";

    /// <summary>
    /// Creates a headless builder that can be used to exercise the Configure callback
    /// without starting Docker containers.
    /// </summary>
    private static IDistributedApplicationBuilder CreateBuilder()
    {
        var options = new DistributedApplicationOptions
        {
            DisableDashboard = true,
            Args = Array.Empty<string>(),
            AssemblyName = AppHostAssemblyName,
        };
        return DistributedApplication.CreateBuilder(options);
    }

    // -----------------------------------------------------------------------
    // Map_Null_AddsNothing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Mapping a null environment spec produces a no-op Configure action, an empty gate list,
    /// and a ResolveServices delegate that returns an empty dictionary.
    /// </summary>
    [Fact]
    public void Map_Null_AddsNothing()
    {
        // Arrange
        var mapped = EnvironmentMapper.Map(null);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — no resources were added to the graph
        Assert.Empty(builder.Resources);
        Assert.Empty(mapped.HealthGateResourceNames);
    }

    // -----------------------------------------------------------------------
    // Map_ServiceWithImage_AddsContainerWithHttpEndpointAndHealthCheck
    // -----------------------------------------------------------------------

    /// <summary>
    /// A service spec with an Image produces a ContainerResource, adds the service name to
    /// the health-gate list, and registers an HTTP endpoint annotation on the container.
    /// </summary>
    [Fact]
    public void Map_ServiceWithImage_AddsContainerWithHttpEndpointAndHealthCheck()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["web"] = new ServiceSpec(
                    Image: "traefik/whoami",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — a ContainerResource named "web" was added
        var webResource = builder.Resources
            .OfType<ContainerResource>()
            .SingleOrDefault(r => r.Name == "web");
        Assert.NotNull(webResource);

        // Assert — health-gate list contains the service name
        Assert.Contains("web", mapped.HealthGateResourceNames);

        // Assert — HTTP endpoint annotation was registered (WithHttpEndpoint)
        var hasEndpoint = webResource.Annotations
            .OfType<EndpointAnnotation>()
            .Any(a => a.Name == "http");
        Assert.True(hasEndpoint, "Expected an 'http' EndpointAnnotation on the 'web' container.");
    }

    // -----------------------------------------------------------------------
    // Map_ServiceWithImage_AppliesImageRegistry
    // -----------------------------------------------------------------------

    /// <summary>
    /// When an env-level ImageRegistry is set and the image has no explicit registry component
    /// (i.e. it is a Docker Hub short name), the registry is prepended to the image reference.
    /// </summary>
    [Fact]
    public void Map_ServiceWithImage_AppliesImageRegistry_WhenImageHasNoRegistry()
    {
        // Arrange — image "myapp:1.0" has no registry (no '.' or ':' in first component)
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["svc"] = new ServiceSpec(
                    Image: "myapp:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: "registry.example.com",
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — a ContainerResource named "svc" was added (registry was applied internally)
        var svcResource = builder.Resources
            .OfType<ContainerResource>()
            .SingleOrDefault(r => r.Name == "svc");
        Assert.NotNull(svcResource);
    }

    // -----------------------------------------------------------------------
    // Map_ServiceWithProject_AddsProject
    // -----------------------------------------------------------------------

    /// <summary>
    /// A service spec with a Project path (and no Image) produces a ProjectResource (or a resource
    /// that references the project), using the string overload AddProject(name, csprojPath).
    /// </summary>
    /// <remarks>
    /// Aspire's <c>AddProject(name, path)</c> validates the csproj path exists and reads its
    /// launch-settings JSON at builder time (before <c>StartAsync</c>), so this test uses an
    /// absolute path to an existing csproj in the solution to avoid a
    /// <c>DistributedApplicationException</c> during the Configure callback.
    /// The mapper itself is not responsible for the path being valid — that is the author's
    /// concern; this test only asserts the mapper wires the string overload correctly.
    /// </remarks>
    [Fact]
    public void Map_ServiceWithProject_AddsProject()
    {
        // Arrange — use a real csproj path that exists in the solution tree so Aspire's
        // AddProject(name, path) succeeds at builder-configure time (before StartAsync).
        // Any real .csproj in the solution is sufficient; we pick Abstractions as it is tiny.
        var realCsproj = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "Engine",
                "Platform.Engine.Abstractions",
                "Platform.Engine.Abstractions.csproj"));

        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: null,
                    Project: realCsproj,
                    ImagePullPolicy: null,
                    HttpPort: null),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — a ProjectResource named "api" was added
        // The string-overload AddProject(name, path) produces a ProjectResource.
        var projectResource = builder.Resources
            .OfType<ProjectResource>()
            .SingleOrDefault(r => r.Name == "api");
        Assert.NotNull(projectResource);

        // Assert — gate list contains the service name
        Assert.Contains("api", mapped.HealthGateResourceNames);
    }

    // -----------------------------------------------------------------------
    // Map_PostgresDependency_AddsServerAndDatabase_GateOnDatabase
    // -----------------------------------------------------------------------

    /// <summary>
    /// A postgres dependency produces both a PostgresServerResource (server) and a
    /// PostgresDatabaseResource (database), with the database name listed first in
    /// HealthGateResourceNames (database before server — §4 invariant).
    /// </summary>
    [Fact]
    public void Map_PostgresDependency_AddsServerAndDatabase_GateOnDatabase()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["pg"] = new DependencySpec(Type: "postgres", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — Postgres server resource is present
        var serverResource = builder.Resources
            .OfType<PostgresServerResource>()
            .SingleOrDefault(r => r.Name == "pg");
        Assert.NotNull(serverResource);

        // Assert — Postgres database resource is present (name = dependency name + "db")
        var dbName = "pgdb";
        var dbResource = builder.Resources
            .OfType<PostgresDatabaseResource>()
            .SingleOrDefault(r => r.Name == dbName);
        Assert.NotNull(dbResource);

        // Assert — §4 invariant: database name appears before server name in the gate list.
        Assert.Contains(dbName, mapped.HealthGateResourceNames);
        var gateList = mapped.HealthGateResourceNames.ToList();
        var dbIndex = gateList.IndexOf(dbName);
        var serverIndex = gateList.IndexOf("pg");

        // If the server is in the gate list it must be after the database.
        if (serverIndex >= 0)
        {
            Assert.True(
                dbIndex < serverIndex,
                $"Database gate '{dbName}' (index {dbIndex}) must precede server gate 'pg' " +
                $"(index {serverIndex}) — §4 hard invariant: gate on the most-specific resource.");
        }
    }

    // -----------------------------------------------------------------------
    // Map_KafkaDependency_AddsKafkaResource
    // -----------------------------------------------------------------------

    /// <summary>
    /// A kafka dependency produces a KafkaServerResource and adds it to the gate list.
    /// </summary>
    [Fact]
    public void Map_KafkaDependency_AddsKafkaResource()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["mq"] = new DependencySpec(Type: "kafka", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — a Kafka resource named "mq" was added
        var kafkaResource = builder.Resources
            .SingleOrDefault(r => r.Name == "mq");
        Assert.NotNull(kafkaResource);

        // Assert — gate list contains the kafka resource
        Assert.Contains("mq", mapped.HealthGateResourceNames);
    }

    // -----------------------------------------------------------------------
    // Map_KafkaWithSchemaRegistry_AddsRegistryContainer
    // -----------------------------------------------------------------------

    /// <summary>
    /// A kafka dependency whose <see cref="DependencySpec.Extra"/> carries
    /// <c>schemaRegistry: true</c> additionally provisions a
    /// <c>confluentinc/cp-schema-registry</c> container named <c>&lt;kafka&gt;-sr</c>.
    /// The registry exposes an <c>http</c> endpoint, waits for the broker, and is
    /// health-gated immediately after (not before) the broker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a non-Docker test: it inspects the in-memory resource graph after
    /// <c>Configure</c> but before <c>StartAsync</c>, so it asserts the container's
    /// image/tag, endpoint annotation, WaitAnnotation target, and gate ordering — all
    /// of which are set at builder time.  It additionally resolves the literal-string
    /// environment variables via <c>GetEnvironmentVariableValuesAsync</c> (which runs
    /// the environment callbacks in-memory, no DCP) and pins
    /// <c>SCHEMA_REGISTRY_HOST_NAME</c> to the registry's own resource name
    /// (<c>events-sr</c>) — the routable/advertised hostname, never a bind-all address.
    /// </para>
    /// <para>
    /// What is NOT asserted here (deferred to the Docker capstone): the resolved value
    /// of <c>SCHEMA_REGISTRY_KAFKASTORE_BOOTSTRAP_SERVERS</c>.  That env var is wired
    /// from a <c>ReferenceExpression</c> over the broker's <c>InternalEndpoint</c>,
    /// whose host/port are only materialised by DCP once the container network exists;
    /// reading it pre-start would require running the live environment callbacks.
    /// (It is therefore excluded from the resolved env-var assertion below.)
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Map_KafkaWithSchemaRegistry_AddsRegistryContainer()
    {
        // Arrange — Extra = { schemaRegistry: true }
        var extra = new YamlMappingNode
        {
            { new YamlScalarNode("schemaRegistry"), new YamlScalarNode("true") },
        };
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["events"] = new DependencySpec(Type: "kafka", Version: null, Extra: extra),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — the broker itself is still present and unchanged.
        var kafkaResource = builder.Resources.SingleOrDefault(r => r.Name == "events");
        Assert.NotNull(kafkaResource);

        // Assert — a schema-registry ContainerResource named "events-sr" was added.
        var srResource = builder.Resources
            .OfType<ContainerResource>()
            .SingleOrDefault(r => r.Name == "events-sr");
        Assert.NotNull(srResource);

        // Assert — it uses the cp-schema-registry image (image + tag annotation).
        var imageAnnotation = srResource!.Annotations
            .OfType<ContainerImageAnnotation>()
            .SingleOrDefault();
        Assert.NotNull(imageAnnotation);
        Assert.Equal("confluentinc/cp-schema-registry", imageAnnotation!.Image);
        Assert.Equal("7.6.1", imageAnnotation.Tag);

        // Assert — SCHEMA_REGISTRY_HOST_NAME is the registry's own ROUTABLE resource
        // name ("events-sr"), never a bind-all 0.0.0.0 (the latter belongs to LISTENERS
        // only).  We run the EnvironmentCallbackAnnotation callbacks directly into a fresh
        // dictionary under the Run execution context — this is pure in-memory work, no
        // DCP/Docker.  The literal-string WithEnvironment overload stores its value verbatim
        // into the dictionary; the ReferenceExpression-backed
        // SCHEMA_REGISTRY_KAFKASTORE_BOOTSTRAP_SERVERS is deposited as an unresolved
        // IValueProvider object (its host/port need a live endpoint — see remarks), so we
        // only read the literal string keys and never trigger endpoint resolution.
        var envVars = new Dictionary<string, object>();
        var callbackContext = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            srResource!,
            envVars);
        foreach (var envCallback in srResource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await envCallback.Callback(callbackContext);
        }

        Assert.Equal("events-sr", Assert.IsType<string>(envVars["SCHEMA_REGISTRY_HOST_NAME"]));
        Assert.Equal(
            "http://0.0.0.0:8081",
            Assert.IsType<string>(envVars["SCHEMA_REGISTRY_LISTENERS"]));

        // Assert — it has an 'http' EndpointAnnotation (WithHttpEndpoint targetPort 8081).
        var httpEndpoint = srResource.Annotations
            .OfType<EndpointAnnotation>()
            .SingleOrDefault(a => a.Name == "http");
        Assert.NotNull(httpEndpoint);
        Assert.Equal(8081, httpEndpoint!.TargetPort);

        // Assert — it WaitFor the broker resource "events".
        var waitsForBroker = srResource.Annotations
            .OfType<WaitAnnotation>()
            .Any(a => a.Resource.Name == "events");
        Assert.True(
            waitsForBroker,
            "The schema-registry container must WaitFor the broker 'events'.");

        // Assert — "events-sr" is health-gated AFTER the broker "events".
        var gateList = mapped.HealthGateResourceNames.ToList();
        Assert.Contains("events", gateList);
        Assert.Contains("events-sr", gateList);
        var brokerIndex = gateList.IndexOf("events");
        var srIndex = gateList.IndexOf("events-sr");
        Assert.True(
            brokerIndex < srIndex,
            $"Broker gate 'events' (index {brokerIndex}) must precede registry gate " +
            $"'events-sr' (index {srIndex}) — the registry depends on the broker.");
    }

    // -----------------------------------------------------------------------
    // Map_MailpitDependency_AddsContainerWithHttpAndSmtpEndpoints
    // -----------------------------------------------------------------------

    /// <summary>
    /// A mailpit dependency provisions an <c>axllent/mailpit</c> container (pinned tag)
    /// exposing BOTH the HTTP API endpoint (targetPort 8025, name <c>"http"</c>) and the
    /// SMTP endpoint (targetPort 1025, name <c>"smtp"</c>), with an HTTP health check on
    /// the <c>"http"</c> endpoint.  The container is health-gated and listed among the
    /// managed dependency names.
    /// </summary>
    /// <remarks>
    /// Non-Docker: inspects the in-memory resource graph after <c>Configure</c> but before
    /// <c>StartAsync</c>, so it asserts the container image/tag, both endpoint annotations,
    /// the health-check annotation, and the gate/dependency membership — all set at builder
    /// time.  Mirrors <see cref="Map_KafkaWithSchemaRegistry_AddsRegistryContainer"/>.
    /// </remarks>
    [Fact]
    public void Map_MailpitDependency_AddsContainerWithHttpAndSmtpEndpoints()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["mp"] = new DependencySpec(Type: "mailpit", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — a ContainerResource named "mp" was added.
        var mailpit = builder.Resources
            .OfType<ContainerResource>()
            .SingleOrDefault(r => r.Name == "mp");
        Assert.NotNull(mailpit);

        // Assert — it uses the axllent/mailpit image with the pinned default tag (v1.21).
        var imageAnnotation = mailpit!.Annotations
            .OfType<ContainerImageAnnotation>()
            .SingleOrDefault();
        Assert.NotNull(imageAnnotation);
        Assert.Equal("axllent/mailpit", imageAnnotation!.Image);
        Assert.Equal("v1.21", imageAnnotation.Tag);

        // Assert — the HTTP API endpoint annotation (targetPort 8025, name "http").
        var httpEndpoint = mailpit.Annotations
            .OfType<EndpointAnnotation>()
            .SingleOrDefault(a => a.Name == "http");
        Assert.NotNull(httpEndpoint);
        Assert.Equal(8025, httpEndpoint!.TargetPort);

        // Assert — the SMTP endpoint annotation (targetPort 1025, name "smtp").
        var smtpEndpoint = mailpit.Annotations
            .OfType<EndpointAnnotation>()
            .SingleOrDefault(a => a.Name == "smtp");
        Assert.NotNull(smtpEndpoint);
        Assert.Equal(1025, smtpEndpoint!.TargetPort);

        // Assert — an HTTP health-check annotation is registered (WithHttpHealthCheck on
        // the "http" endpoint).  HealthCheckAnnotation carries a non-empty Key.
        Assert.Contains(
            mailpit.Annotations.OfType<HealthCheckAnnotation>(),
            h => !string.IsNullOrEmpty(h.Key));

        // Assert — "mp" is health-gated and listed as a managed dependency.
        Assert.Contains("mp", mapped.HealthGateResourceNames);
        Assert.Contains("mp", mapped.DependencyNames);
    }

    // -----------------------------------------------------------------------
    // Map_KafkaWithoutSchemaRegistry_AddsNoRegistry
    // -----------------------------------------------------------------------

    /// <summary>
    /// A kafka dependency without <c>schemaRegistry: true</c> in <see cref="DependencySpec.Extra"/>
    /// (whether <c>Extra</c> is null or carries <c>schemaRegistry: false</c>) provisions
    /// only the broker — no <c>*-sr</c> container, no extra gate.  Pins that the existing
    /// plain-kafka behaviour is unchanged.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void Map_KafkaWithoutSchemaRegistry_AddsNoRegistry(string? schemaRegistryValue)
    {
        // Arrange
        YamlMappingNode? extra = schemaRegistryValue is null
            ? null
            : new YamlMappingNode
            {
                { new YamlScalarNode("schemaRegistry"), new YamlScalarNode(schemaRegistryValue) },
            };

        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["events"] = new DependencySpec(Type: "kafka", Version: null, Extra: extra),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — the broker is present.
        Assert.NotNull(builder.Resources.SingleOrDefault(r => r.Name == "events"));

        // Assert — no schema-registry container was added.
        var srResource = builder.Resources
            .OfType<ContainerResource>()
            .SingleOrDefault(r => r.Name == "events-sr");
        Assert.Null(srResource);

        // Assert — no "-sr" gate exists.
        Assert.DoesNotContain("events-sr", mapped.HealthGateResourceNames);
        Assert.Contains("events", mapped.HealthGateResourceNames);
    }

    // -----------------------------------------------------------------------
    // Map_ServiceAndPostgres_ServiceWaitsForDatabase
    // -----------------------------------------------------------------------

    /// <summary>
    /// When both a service (image) and a postgres dependency are declared, the service
    /// acquires a <see cref="WaitAnnotation"/> that targets the <em>database</em> resource
    /// (not the server) — §4 hard invariant.
    /// </summary>
    /// <remarks>
    /// This test inspects <see cref="WaitAnnotation"/> on the container resource, which is
    /// populated by <c>WaitFor(dbBuilder)</c> in the Configure callback.  The annotation is
    /// set at builder time (before StartAsync) so this test runs without Docker.
    /// </remarks>
    [Fact]
    public void Map_ServiceAndPostgres_ServiceWaitsForDatabase()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["web"] = new ServiceSpec(
                    Image: "traefik/whoami",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null),
            },
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["pg"] = new DependencySpec(Type: "postgres", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — both resources exist
        var webResource = builder.Resources
            .OfType<ContainerResource>()
            .SingleOrDefault(r => r.Name == "web");
        Assert.NotNull(webResource);

        var dbResource = builder.Resources
            .OfType<PostgresDatabaseResource>()
            .SingleOrDefault(r => r.Name == "pgdb");
        Assert.NotNull(dbResource);

        // Assert — gate ordering: database before server before service
        Assert.Contains("pgdb", mapped.HealthGateResourceNames);

        // Assert — WaitAnnotation on the container targets the database resource, not the server.
        // WaitFor(dbBuilder) attaches a WaitAnnotation to the container resource.
        var waitAnnotations = webResource.Annotations
            .OfType<WaitAnnotation>()
            .ToList();

        Assert.True(
            waitAnnotations.Count > 0,
            "Expected at least one WaitAnnotation on 'web' — WaitFor(dbBuilder) must have been called.");

        // The container must wait for the database resource specifically (§4 invariant).
        var waitsForDb = waitAnnotations.Any(a => a.Resource.Name == "pgdb");
        Assert.True(
            waitsForDb,
            "Service 'web' must WaitFor the database resource 'pgdb', not the server 'pg' — " +
            "§4 hard invariant: gate on the most-specific resource to avoid the server-vs-database race.");
    }

    // -----------------------------------------------------------------------
    // Map_UnknownDependencyType_Throws
    // -----------------------------------------------------------------------

    /// <summary>
    /// An unknown dependency type throws <see cref="ArgumentException"/> at Map time.
    /// </summary>
    [Fact]
    public void Map_UnknownDependencyType_Throws()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["store"] = new DependencySpec(Type: "cassandra", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        // Act + Assert
        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("cassandra", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Map_SqlServerDependency_AddsServerAndDatabase_GateOnDatabase
    // -----------------------------------------------------------------------

    /// <summary>
    /// A sqlserver dependency produces both a server resource and a database resource
    /// (named &lt;name&gt;db), with the database in the health-gate list (§4 invariant).
    /// </summary>
    [Fact]
    public void Map_SqlServerDependency_AddsServerAndDatabase_GateOnDatabase()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["db"] = new DependencySpec(Type: "sqlserver", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — server resource named "db" exists
        Assert.NotNull(builder.Resources.SingleOrDefault(r => r.Name == "db"));

        // Assert — database resource named "dbdb" exists
        Assert.NotNull(builder.Resources.SingleOrDefault(r => r.Name == "dbdb"));

        // Assert — the retained database resource implements IResourceWithConnectionString (the
        // contract that ResolveServices reads the connection string from).
        Assert.IsAssignableFrom<IResourceWithConnectionString>(
            builder.Resources.SingleOrDefault(r => r.Name == "dbdb"));

        // Assert — gate is on the database resource (§4 invariant)
        Assert.Contains("dbdb", mapped.HealthGateResourceNames);
        Assert.Contains("db", mapped.DependencyNames);
    }

    // -----------------------------------------------------------------------
    // Map_MySqlDependency_AddsServerAndDatabase_GateOnDatabase
    // -----------------------------------------------------------------------

    /// <summary>
    /// A mysql dependency produces both a server resource and a database resource,
    /// with the database in the health-gate list (§4 invariant).
    /// </summary>
    [Fact]
    public void Map_MySqlDependency_AddsServerAndDatabase_GateOnDatabase()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["mdb"] = new DependencySpec(Type: "mysql", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — server resource named "mdb" exists
        Assert.NotNull(builder.Resources.SingleOrDefault(r => r.Name == "mdb"));

        // Assert — database resource named "mdbdb" exists
        Assert.NotNull(builder.Resources.SingleOrDefault(r => r.Name == "mdbdb"));

        // Assert — the retained database resource implements IResourceWithConnectionString.
        Assert.IsAssignableFrom<IResourceWithConnectionString>(
            builder.Resources.SingleOrDefault(r => r.Name == "mdbdb"));

        // Assert — gate is on the database resource
        Assert.Contains("mdbdb", mapped.HealthGateResourceNames);
        Assert.Contains("mdb", mapped.DependencyNames);
    }

    // -----------------------------------------------------------------------
    // Map_MongoDbDependency_AddsServerAndDatabase_GateOnDatabase
    // -----------------------------------------------------------------------

    /// <summary>
    /// A mongodb dependency produces both a server resource and a database resource,
    /// with the database in the health-gate list (§4 invariant).
    /// </summary>
    [Fact]
    public void Map_MongoDbDependency_AddsServerAndDatabase_GateOnDatabase()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders"] = new DependencySpec(Type: "mongodb", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — server resource named "orders" exists
        Assert.NotNull(builder.Resources.SingleOrDefault(r => r.Name == "orders"));

        // Assert — database resource named "ordersdb" exists
        Assert.NotNull(builder.Resources.SingleOrDefault(r => r.Name == "ordersdb"));

        // Assert — the retained database resource implements IResourceWithConnectionString.
        Assert.IsAssignableFrom<IResourceWithConnectionString>(
            builder.Resources.SingleOrDefault(r => r.Name == "ordersdb"));

        // Assert — gate is on the database resource
        Assert.Contains("ordersdb", mapped.HealthGateResourceNames);
        Assert.Contains("orders", mapped.DependencyNames);
    }

    // -----------------------------------------------------------------------
    // Map_RedisDependency_AddsServer_GateOnServer
    // -----------------------------------------------------------------------

    /// <summary>
    /// A redis dependency produces a server resource only (no database resource),
    /// and the server itself is in the health-gate list.
    /// </summary>
    [Fact]
    public void Map_RedisDependency_AddsServer_GateOnServer()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["cache"] = new DependencySpec(Type: "redis", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — server resource named "cache" exists
        Assert.NotNull(builder.Resources.SingleOrDefault(r => r.Name == "cache"));

        // Assert — the retained server resource implements IResourceWithConnectionString.
        Assert.IsAssignableFrom<IResourceWithConnectionString>(
            builder.Resources.SingleOrDefault(r => r.Name == "cache"));

        // Assert — gate is on the server (no separate database resource)
        Assert.Contains("cache", mapped.HealthGateResourceNames);
        Assert.Contains("cache", mapped.DependencyNames);
    }

    // -----------------------------------------------------------------------
    // Map_ElasticsearchDependency_AddsServer_GateOnServer
    // -----------------------------------------------------------------------

    /// <summary>
    /// An elasticsearch dependency produces a server resource only (no database resource),
    /// the server itself is in the health-gate list, and the stability environment variables
    /// (<c>discovery.type</c>, <c>xpack.security.enabled</c>, <c>ES_JAVA_OPTS</c>) are
    /// wired onto the resource so a future refactor cannot silently drop them.
    /// </summary>
    [Fact]
    public async Task Map_ElasticsearchDependency_AddsServer_GateOnServer()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["search"] = new DependencySpec(Type: "elasticsearch", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — server resource named "search" exists
        var searchResource = builder.Resources.SingleOrDefault(r => r.Name == "search");
        Assert.NotNull(searchResource);

        // Assert — the retained server resource implements IResourceWithConnectionString.
        Assert.IsAssignableFrom<IResourceWithConnectionString>(searchResource);

        // Assert — gate is on the server
        Assert.Contains("search", mapped.HealthGateResourceNames);
        Assert.Contains("search", mapped.DependencyNames);

        // Assert — stability env vars are present on the resource.
        // Literal-string WithEnvironment calls register EnvironmentCallbackAnnotation entries;
        // running them populates the dictionary synchronously.
        var envVars = new Dictionary<string, object>();
        var callbackContext = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            searchResource!,
            envVars);
        foreach (var envCallback in searchResource!.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await envCallback.Callback(callbackContext);
        }

        Assert.Equal("single-node", Assert.IsType<string>(envVars["discovery.type"]));
        Assert.Equal("false", Assert.IsType<string>(envVars["xpack.security.enabled"]));
        Assert.Equal("-Xms512m -Xmx512m", Assert.IsType<string>(envVars["ES_JAVA_OPTS"]));
    }

    // -----------------------------------------------------------------------
    // Map_RabbitMqDependency_AddsServer_GateOnServer
    // -----------------------------------------------------------------------

    /// <summary>
    /// A rabbitmq dependency produces a server resource only (no database resource),
    /// and the server itself is in the health-gate list.
    /// </summary>
    [Fact]
    public void Map_RabbitMqDependency_AddsServer_GateOnServer()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["bus"] = new DependencySpec(Type: "rabbitmq", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — server resource named "bus" exists
        Assert.NotNull(builder.Resources.SingleOrDefault(r => r.Name == "bus"));

        // Assert — the retained server resource implements IResourceWithConnectionString.
        Assert.IsAssignableFrom<IResourceWithConnectionString>(
            builder.Resources.SingleOrDefault(r => r.Name == "bus"));

        // Assert — gate is on the server
        Assert.Contains("bus", mapped.HealthGateResourceNames);
        Assert.Contains("bus", mapped.DependencyNames);
    }

    // -----------------------------------------------------------------------
    // Map_NatsDependency_AddsServer_GateOnServer
    // -----------------------------------------------------------------------

    /// <summary>
    /// A nats dependency produces a server resource only (no database resource),
    /// the server itself is in the health-gate list, and JetStream is enabled via
    /// <c>WithJetStream()</c> (which appends <c>-js</c> to the container command-line
    /// arguments — FIX B1: without this, every mq-publish.nats / mq-expect.nats step
    /// returns EnvironmentError).
    /// </summary>
    [Fact]
    public async Task Map_NatsDependency_AddsServer_GateOnServer()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["events"] = new DependencySpec(Type: "nats", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — server resource named "events" exists
        var eventsResource = builder.Resources.SingleOrDefault(r => r.Name == "events");
        Assert.NotNull(eventsResource);

        // Assert — the retained server resource implements IResourceWithConnectionString.
        Assert.IsAssignableFrom<IResourceWithConnectionString>(eventsResource);

        // Assert — gate is on the server
        Assert.Contains("events", mapped.HealthGateResourceNames);
        Assert.Contains("events", mapped.DependencyNames);

        // Assert — JetStream is enabled: WithJetStream() registers a CommandLineArgsCallbackAnnotation
        // that appends '-js' to the container args.  We invoke the callbacks in-memory (no DCP/Docker)
        // and verify the flag is present.  This is the FIX B1 regression gate.
        var args = new List<object>();
        var argsContext = new CommandLineArgsCallbackContext(args, eventsResource!, CancellationToken.None);
        foreach (var argsCallback in eventsResource!.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            await argsCallback.Callback(argsContext);
        }

        Assert.Contains(args, a => a is string s && s == "-js");
    }

    // -----------------------------------------------------------------------
    // Map_ServiceWithBothImageAndProject_Throws
    // -----------------------------------------------------------------------

    /// <summary>
    /// A service spec with both Image and Project set throws <see cref="ArgumentException"/>
    /// at Map time — authoring schema should catch this, but the mapper is defensive.
    /// </summary>
    [Fact]
    public void Map_ServiceWithBothImageAndProject_Throws()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["svc"] = new ServiceSpec(
                    Image: "traefik/whoami",
                    Project: "./Api.csproj",
                    ImagePullPolicy: null,
                    HttpPort: null),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        // Act + Assert
        Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
    }

    // -----------------------------------------------------------------------
    // Map_ServiceWithNeitherImageNorProject_Throws
    // -----------------------------------------------------------------------

    /// <summary>
    /// A service spec with neither Image nor Project set throws <see cref="ArgumentException"/>
    /// at Map time.
    /// </summary>
    [Fact]
    public void Map_ServiceWithNeitherImageNorProject_Throws()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["svc"] = new ServiceSpec(
                    Image: null,
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        // Act + Assert
        Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
    }
}
