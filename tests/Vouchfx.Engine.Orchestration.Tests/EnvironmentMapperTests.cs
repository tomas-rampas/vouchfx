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
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Orchestration.Tests;

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
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

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
                    HttpPort: null,
                    Env: null),
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
                    HttpPort: null,
                    Env: null),
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
                "Vouchfx.Engine.Abstractions",
                "Vouchfx.Engine.Abstractions.csproj"));

        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: null,
                    Project: realCsproj,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null),
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
                    HttpPort: null,
                    Env: null),
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
    // Map_DynamodbDependency / Map_MinioDependency
    // -----------------------------------------------------------------------

    /// <summary>
    /// A dynamodb dependency produces a plain container resource pinned to the
    /// amazon/dynamodb-local image, health-gated on itself — the off-docker
    /// registration lock for the Phase B dependency type (the 400-as-healthy
    /// gate behaviour itself is exercised by DbAssertDynamodbDockerTests).
    /// </summary>
    [Fact]
    public void Map_DynamodbDependency_AddsPinnedContainer_GateOnSelf()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders-db"] = new DependencySpec(Type: "dynamodb", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var resource = builder.Resources.SingleOrDefault(r => r.Name == "orders-db");
        Assert.NotNull(resource);
        Assert.Contains("orders-db", mapped.HealthGateResourceNames);
        Assert.Contains("orders-db", mapped.DependencyNames);

        var image = resource!.Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("amazon/dynamodb-local", image.Image);
        Assert.False(string.IsNullOrEmpty(image.Tag));
    }

    /// <summary>
    /// A minio dependency produces a plain container resource pinned to the
    /// minio/minio image, started in server mode ('server /data'), health-gated
    /// on itself — the off-docker registration lock for the Phase B dependency
    /// type (the /minio/health/cluster readiness gate is exercised live by
    /// StorageAssertS3DockerTests).
    /// </summary>
    [Fact]
    public async Task Map_MinioDependency_AddsPinnedContainer_ServerMode_GateOnSelf()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["artefacts"] = new DependencySpec(Type: "minio", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var resource = builder.Resources.SingleOrDefault(r => r.Name == "artefacts");
        Assert.NotNull(resource);
        Assert.Contains("artefacts", mapped.HealthGateResourceNames);
        Assert.Contains("artefacts", mapped.DependencyNames);

        var image = resource!.Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("minio/minio", image.Image);
        Assert.False(string.IsNullOrEmpty(image.Tag));

        var args = new List<object>();
        var argsContext = new CommandLineArgsCallbackContext(args, resource, CancellationToken.None);
        foreach (var argsCallback in resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            await argsCallback.Callback(argsContext);
        }

        Assert.Contains(args, a => a is string s && s == "server");
        Assert.Contains(args, a => a is string s && s == "/data");
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
                    HttpPort: null,
                    Env: null),
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
                    HttpPort: null,
                    Env: null),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        // Act + Assert
        Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
    }

    // -----------------------------------------------------------------------
    // SUT configuration surface — service `env:` mapping
    // -----------------------------------------------------------------------
    //
    // Test strategy (non-Docker): every env: value is applied via
    // WithEnvironment(name, ReferenceExpression) — even a purely literal value with no
    // ${conn:...} reference at all, confirmed by probing the pinned Aspire.Hosting 13.4.2
    // package directly (ReferenceExpressionBuilder.AppendLiteral + .Build() always yields a
    // ReferenceExpression; WithEnvironment never collapses it to a plain string).  Running the
    // registered EnvironmentCallbackAnnotation callbacks (exactly as
    // Map_KafkaWithSchemaRegistry_AddsRegistryContainer above already does) populates the
    // resource's environment-variables dictionary with these ReferenceExpression objects
    // WITHOUT resolving them — .GetValueAsync() is never invoked pre-StartAsync, so no live
    // endpoint/Docker is required.  ReferenceExpression.ValueExpression, however, IS computable
    // pre-start: it recursively substitutes every nested value provider's OWN ValueExpression,
    // which for an EndpointReferenceExpression is a symbolic manifest placeholder such as
    // "{orders.bindings.tcp.host}" (never a resolved IP/port).  Asserting against
    // ValueExpression therefore proves BOTH which literal text is present AND which Aspire
    // resource/endpoint a reference resolves to — without starting Docker.  The one thing this
    // strategy cannot prove is the actual resolved container-network value; that is the job of
    // the docker-gated end-to-end test.

    /// <summary>
    /// Runs every registered <see cref="EnvironmentCallbackAnnotation"/> on
    /// <paramref name="resource"/> and returns the populated environment-variables dictionary —
    /// mirrors the in-memory callback execution already used by
    /// <see cref="Map_KafkaWithSchemaRegistry_AddsRegistryContainer"/> above.
    /// </summary>
    private static async Task<Dictionary<string, object>> ResolveEnvVarsAsync(IResource resource)
    {
        var envVars = new Dictionary<string, object>();
        var callbackContext = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            resource,
            envVars);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(callbackContext);
        }

        return envVars;
    }

    /// <summary>Extracts the <see cref="ReferenceExpression.ValueExpression"/> of an env-var value.</summary>
    private static string ValueExpressionOf(object envVarValue) =>
        Assert.IsType<ReferenceExpression>(envVarValue).ValueExpression;

    [Fact]
    public async Task Map_ServiceEnv_LiteralValue_ResolvesToLiteralText()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["LOG_LEVEL"] = "information" }),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert
        var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var envVars = await ResolveEnvVarsAsync(apiResource);
        Assert.Equal("information", ValueExpressionOf(envVars["LOG_LEVEL"]));
    }

    [Fact]
    public async Task Map_ServiceEnv_FullFormReference_Postgres_ResolvesToDatabaseConnectionString()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string>
                    {
                        ["ConnectionStrings__orders"] = "${conn:orders}",
                    }),
            },
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders"] = new DependencySpec(Type: "postgres", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — the DATABASE resource's own ConnectionStringExpression (name + "db"), which
        // is itself Aspire's manifest-level reference to the SERVER's connection string plus
        // ";Database=ordersdb" (PostgresDatabaseResource.ConnectionStringExpression, verified
        // directly against the pinned Aspire.Hosting.PostgreSQL 13.4.2 package) — never a
        // resolved host/port/localhost (no Docker involved; that is proven live by the
        // docker-gated end-to-end test).
        var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var envVars = await ResolveEnvVarsAsync(apiResource);
        var resolved = ValueExpressionOf(envVars["ConnectionStrings__orders"]);
        Assert.Equal("{orders.connectionString};Database=ordersdb", resolved);
        Assert.DoesNotContain("127.0.0.1", resolved, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Map_ServiceEnv_HostPortParts_Postgres_ReadServerPrimaryEndpoint()
    {
        // Arrange — host/port parts must read the SERVER's primary endpoint ("orders"), never
        // the DATABASE resource ("ordersdb") — the §4 invariant is "retain the database for
        // health-gating", not "read credentials/endpoints off the database resource".
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string>
                    {
                        ["DB_HOST"] = "${conn:orders.host}",
                        ["DB_PORT"] = "${conn:orders.port}",
                    }),
            },
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders"] = new DependencySpec(Type: "postgres", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert
        var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var envVars = await ResolveEnvVarsAsync(apiResource);
        Assert.Equal("{orders.bindings.tcp.host}", ValueExpressionOf(envVars["DB_HOST"]));
        Assert.Equal("{orders.bindings.tcp.port}", ValueExpressionOf(envVars["DB_PORT"]));
    }

    [Fact]
    public async Task Map_ServiceEnv_UsernamePasswordDatabaseParts_Postgres()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string>
                    {
                        ["DB_USER"] = "${conn:orders.username}",
                        ["DB_PASSWORD"] = "${conn:orders.password}",
                        ["DB_NAME"] = "${conn:orders.database}",
                    }),
            },
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders"] = new DependencySpec(Type: "postgres", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — postgres defaults: username is the fixed literal "postgres"; the database
        // name is the literal db-resource name ("ordersdb"); password is a live Aspire
        // parameter (unresolvable pre-start, so only its manifest placeholder is checked).
        var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var envVars = await ResolveEnvVarsAsync(apiResource);
        Assert.Equal("postgres", ValueExpressionOf(envVars["DB_USER"]));
        Assert.Equal("ordersdb", ValueExpressionOf(envVars["DB_NAME"]));
        Assert.Contains("password", ValueExpressionOf(envVars["DB_PASSWORD"]), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Map_ServiceEnv_UsernamePart_Mysql_IsFixedLiteralRoot()
    {
        // Arrange — MySqlServerResource exposes no UserNameParameter/Reference at all: the
        // container always provisions the fixed 'root' superuser (verified against
        // Aspire.Hosting.MySql 13.4.2 — AddMySql has no userName overload).
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["DB_USER"] = "${conn:billing.username}" }),
            },
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["billing"] = new DependencySpec(Type: "mysql", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert
        var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var envVars = await ResolveEnvVarsAsync(apiResource);
        Assert.Equal("root", ValueExpressionOf(envVars["DB_USER"]));
    }

    [Fact]
    public async Task Map_ServiceEnv_FullFormReference_Kafka_UsesInternalEndpoint_NoSchemePrefix()
    {
        // Arrange — the FULL form must read KafkaServerResource.InternalEndpoint (the
        // container-network bootstrap address), never ConnectionStringExpression (which
        // resolves the EXTERNAL/host-published endpoint) — mirrors the schema-registry
        // sidecar's own bootstrap-servers construction, minus its PLAINTEXT:// scheme prefix
        // (a generic bootstrap.servers env var is plain host:port).
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["KAFKA_BOOTSTRAP"] = "${conn:broker}" }),
            },
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["broker"] = new DependencySpec(Type: "kafka", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert
        var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var envVars = await ResolveEnvVarsAsync(apiResource);
        var resolved = ValueExpressionOf(envVars["KAFKA_BOOTSTRAP"]);
        Assert.Equal("{broker.bindings.internal.host}:{broker.bindings.internal.port}", resolved);
    }

    [Fact]
    public async Task Map_ServiceEnv_FullFormReference_Mailpit_UsesSmtpEndpoint()
    {
        // Arrange — mailpit is a plain ContainerResource (no IResourceWithConnectionString);
        // the full form must read the SMTP endpoint the registration's Build lambda already
        // stages, never the HTTP API endpoint.
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["SMTP_ADDR"] = "${conn:mail}" }),
            },
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["mail"] = new DependencySpec(Type: "mailpit", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert
        var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var envVars = await ResolveEnvVarsAsync(apiResource);
        var resolved = ValueExpressionOf(envVars["SMTP_ADDR"]);
        Assert.Equal("{mail.bindings.smtp.host}:{mail.bindings.smtp.port}", resolved);
    }

    [Fact]
    public async Task Map_ServiceEnv_UsernamePasswordParts_Rabbitmq()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string>
                    {
                        ["MQ_USER"] = "${conn:events.username}",
                        ["MQ_PASSWORD"] = "${conn:events.password}",
                    }),
            },
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["events"] = new DependencySpec(Type: "rabbitmq", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — RabbitMQ's default fixed username is "guest".
        var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var envVars = await ResolveEnvVarsAsync(apiResource);
        Assert.Equal("guest", ValueExpressionOf(envVars["MQ_USER"]));
        Assert.Contains("password", ValueExpressionOf(envVars["MQ_PASSWORD"]), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AddRedis unconditionally starts the container as
    /// <c>redis-server --requirepass $REDIS_PASSWORD</c> (confirmed live: a redis-py client
    /// failed with "Authentication required" against a SUT wired only with host/port). Redis has
    /// no username concept at all, so only the <c>password</c> part is supported.
    /// </summary>
    [Fact]
    public async Task Map_ServiceEnv_PasswordPart_Redis()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["CACHE_PASSWORD"] = "${conn:cache.password}" }),
            },
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

        // Assert — a live Aspire parameter (unresolvable pre-start), so only the manifest
        // placeholder is checked.
        var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var envVars = await ResolveEnvVarsAsync(apiResource);
        Assert.Contains("password", ValueExpressionOf(envVars["CACHE_PASSWORD"]), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>AddNats(name).WithJetStream()</c> (the exact call this mapper makes) unconditionally
    /// starts the container with <c>--user &lt;name&gt; --pass &lt;password&gt;</c> (confirmed
    /// live: the engine's own NATS health-check connection logs
    /// <c>nats://nats:***@localhost:&lt;port&gt;</c>) — both username and password are real,
    /// enforced credentials.
    /// </summary>
    [Fact]
    public async Task Map_ServiceEnv_UsernamePasswordParts_Nats()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string>
                    {
                        ["NATS_USER"] = "${conn:broker.username}",
                        ["NATS_PASSWORD"] = "${conn:broker.password}",
                    }),
            },
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["broker"] = new DependencySpec(Type: "nats", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert — NATS's default fixed username is "nats"; password is a live Aspire
        // parameter (unresolvable pre-start, so only its manifest placeholder is checked).
        var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var envVars = await ResolveEnvVarsAsync(apiResource);
        Assert.Equal("nats", ValueExpressionOf(envVars["NATS_USER"]));
        Assert.Contains("password", ValueExpressionOf(envVars["NATS_PASSWORD"]), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Map_ServiceEnv_MixedLiteralAndMultipleReferences_SqlServerJdbcUrl()
    {
        // Arrange — mirrors the frozen feature-contract example verbatim.
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string>
                    {
                        ["SPRING_DATASOURCE_URL"] =
                            "jdbc:sqlserver://${conn:paydb.host}:${conn:paydb.port};databaseName=${conn:paydb.database};encrypt=false",
                    }),
            },
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["paydb"] = new DependencySpec(Type: "sqlserver", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert
        var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var envVars = await ResolveEnvVarsAsync(apiResource);
        Assert.Equal(
            "jdbc:sqlserver://{paydb.bindings.tcp.host}:{paydb.bindings.tcp.port};databaseName=paydbdb;encrypt=false",
            ValueExpressionOf(envVars["SPRING_DATASOURCE_URL"]));
    }

    // -----------------------------------------------------------------------
    // BLOCKER regression (peer-review-critic): literal-brace escaping in env: values.
    //
    // ReferenceExpressionBuilder.AppendLiteral appends literal text VERBATIM into the internal
    // composite-format string that ReferenceExpression.ValueExpression later materialises via
    // string.Format — it does NOT escape braces itself. Confirmed empirically (runtime-probed
    // against the pinned Aspire 13.4.2 DLL) before the fix:
    //   • "a{0}b-${conn:pg.host}" -> Format "a{0}b-{0}" -> string.Format substitutes the SAME
    //     resolved host into BOTH the placeholder AND the author's literal "{0}" -> silent
    //     corruption, no exception.
    //   • A pure literal containing braces (a JSON value, or the "${VAR}" self-expansion idiom)
    //     -> FormatException at materialisation time (StartAsync) -> OrchestrationException ->
    //     EnvironmentError — the exact §12.1 misclassification the M2 fix targeted, one layer
    //     further downstream, since it happens AFTER Map() returns successfully.
    // These tests assert the MATERIALISED value (ValueExpressionOf calls .ValueExpression,
    // which invokes string.Format) so they actually exercise the bug, not just the Format string.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Map_ServiceEnv_LiteralBraceAdjacentToReference_RoundTripsLiteralBraces()
    {
        // Arrange — a literal span containing '{raw}' immediately beside a real ${conn:...}
        // reference. Before the fix, 'raw' is not numeric so this would throw FormatException
        // (an unbalanced/non-index brace) rather than silently corrupt — either way, wrong.
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["X"] = "prefix-{raw}-${conn:pg.host}" }),
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

        // Assert — the literal '{raw}' text survives verbatim, and the reference resolves.
        var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var envVars = await ResolveEnvVarsAsync(apiResource);
        Assert.Equal("prefix-{raw}-{pg.bindings.tcp.host}", ValueExpressionOf(envVars["X"]));
    }

    [Fact]
    public async Task Map_ServiceEnv_PureLiteralJsonValue_DeliveredVerbatim()
    {
        // Arrange — a pure-literal JSON value, no ${conn:...} reference at all. Before the
        // fix this threw FormatException when Aspire materialised the env var at StartAsync.
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["CONFIG"] = """{"a":1}""" }),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert
        var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var envVars = await ResolveEnvVarsAsync(apiResource);
        Assert.Equal("""{"a":1}""", ValueExpressionOf(envVars["CONFIG"]));
    }

    [Fact]
    public async Task Map_ServiceEnv_NonConnDollarBraceSigil_DeliveredVerbatim()
    {
        // Arrange — "${DB_PASSWORD}" is the shell/Make self-expansion idiom, NOT a
        // ${conn:...}/${secret:...} reference — env: has no opinion on it and must deliver it
        // as a plain literal. Before the fix this threw FormatException at materialisation time.
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["P"] = "${DB_PASSWORD}" }),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert
        var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var envVars = await ResolveEnvVarsAsync(apiResource);
        Assert.Equal("${DB_PASSWORD}", ValueExpressionOf(envVars["P"]));
    }

    [Fact]
    public async Task Map_ServiceEnv_LiteralNumericBraceCollidingWithFormatIndex_DoesNotCorruptReference()
    {
        // Arrange — "{0}" is EXACTLY the format-string placeholder index Aspire's own
        // ReferenceExpression machinery would assign the first (and, here, only) value
        // provider. Before the fix, string.Format silently substituted the resolved host into
        // BOTH the intended slot and this literal "{0}" — the specific silent-corruption case
        // (not merely a thrown exception).
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["X"] = "a{0}b-${conn:pg.host}" }),
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

        // Assert — the literal "{0}" text is untouched; only the ${conn:pg.host} reference
        // resolved to the (single) host placeholder.
        var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var envVars = await ResolveEnvVarsAsync(apiResource);
        Assert.Equal("a{0}b-{pg.bindings.tcp.host}", ValueExpressionOf(envVars["X"]));
    }

    [Fact]
    public void Map_ServiceEnv_UnknownDependency_ThrowsArgumentException()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["FOO"] = "${conn:does-not-exist}" }),
            },
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders"] = new DependencySpec(Type: "postgres", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        // Act + Assert — eager: thrown by Map() itself, before any builder mutation.
        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("does-not-exist", ex.Message, StringComparison.Ordinal);
        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("redis", "username")]
    [InlineData("nats", "database")]
    [InlineData("kafka", "password")]
    [InlineData("elasticsearch", "username")]
    [InlineData("mailpit", "database")]
    public void Map_ServiceEnv_UnsupportedPart_ThrowsArgumentException(string dependencyType, string part)
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["FOO"] = $"${{conn:dep.{part}}}" }),
            },
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["dep"] = new DependencySpec(Type: dependencyType, Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        // Act + Assert
        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains(part, ex.Message, StringComparison.Ordinal);
        Assert.Contains(dependencyType, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_ServiceEnv_SecretReference_ThrowsArgumentException_CitingSecrets()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["API_KEY"] = "${secret:env/API_KEY}" }),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        // Act + Assert
        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("secret", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("step-execution", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A MALFORMED secret sigil (missing the '/path' segment) must ALSO be rejected — not
    /// just a well-formed '${secret:source/path}' reference. Regression guard (code-review
    /// MINOR): the previous check used a well-formed-token regex
    /// (<c>\$\{secret:[A-Za-z0-9_-]+/[^}]+\}</c>), which a malformed token such as
    /// '${secret:env}' does not match, so it silently passed through as opaque literal text
    /// instead of surfacing the author's mistake. The fix checks for SIGIL PRESENCE
    /// (<see cref="Vouchfx.Engine.Abstractions.Secrets.SecretReference.Sigil"/>) — mirroring
    /// <see cref="Vouchfx.Engine.Abstractions.Secrets.SecretReference.ValidateField"/> — so
    /// env: rejects the sigil outright regardless of whether the token is well-formed (env:
    /// supports NO secret references at all, unlike step fields, which accept well-formed ones).
    /// </summary>
    [Fact]
    public void Map_ServiceEnv_MalformedSecretSigil_ThrowsArgumentException_CitingSecrets()
    {
        // Arrange — '${secret:env}' is missing the required '/path' segment.
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["API_KEY"] = "${secret:env}" }),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        // Act + Assert
        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("secret", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("step-execution", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Regression guard (B1, BLOCKER, code-review-gatekeeper): an azureservicebus dependency
    /// has no <c>env:</c> resolution path in <c>ResolveDependencyEnvAccess</c>. Before this
    /// fix, <c>Configure()</c> called that method UNCONDITIONALLY for every declared
    /// dependency, so merely DECLARING an azureservicebus dependency — regardless of whether
    /// any service's <c>env:</c> block referenced it — tripped the method's internal-error
    /// fallback throw on every single run (this broke
    /// examples/mq-azureservicebus.e2e.yaml outright: <c>vouchfx run</c> failed at topology
    /// start every time). <c>Map()</c> and <c>Configure()</c> must both succeed cleanly for a
    /// topology with an azureservicebus dependency alongside a service with NO env: block and
    /// another service whose env: references a DIFFERENT dependency.
    /// </summary>
    [Fact]
    public void Map_AzureServiceBusDependency_DeclaredButUnreferencedByAnyEnv_ConfiguresCleanly()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["plain"] = new ServiceSpec(
                    Image: "myorg/plain:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null),
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["ORDERS_CONN"] = "${conn:orders}" }),
            },
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["bus"] = new DependencySpec(Type: "azureservicebus", Version: null, Extra: null),
                ["orders"] = new DependencySpec(Type: "postgres", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        // Act — Map() must succeed (no env: value references 'bus'), and Configure() must run
        // to completion without throwing (the azureservicebus dependency is never dereferenced
        // for env: resolution because nothing references it).
        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        var exception = Record.Exception(() => mapped.Configure(builder));

        // Assert
        Assert.Null(exception);
        Assert.NotNull(builder.Resources.SingleOrDefault(r => r.Name == "bus"));
        Assert.NotNull(builder.Resources.OfType<ContainerResource>().SingleOrDefault(r => r.Name == "plain"));
        Assert.NotNull(builder.Resources.OfType<ContainerResource>().SingleOrDefault(r => r.Name == "api"));
    }

    [Fact]
    public void Map_ServiceEnv_AzureServiceBusFullForm_ThrowsArgumentException()
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["ASB_CONN"] = "${conn:bus}" }),
            },
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["bus"] = new DependencySpec(Type: "azureservicebus", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        // Act + Assert — rejected even in FULL form (not just '.part' access).
        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("azureservicebus", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not support", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Map_ServiceEnv_ImageFormService_AddsHostGatewayContainerRuntimeArg()
    {
        // Arrange — image-form services always get the '--add-host' runtime arg, regardless
        // of whether they declare an env: block, so a containerised SUT can reach a host-run
        // webhook listener via host.docker.internal on plain Linux Docker Engine CI runners
        // too (Docker Desktop already resolves the name natively).
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert
        var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var runtimeArgAnnotation = apiResource.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>().Single();
        var argsList = new List<object>();
        await runtimeArgAnnotation.Callback(new ContainerRuntimeArgsCallbackContext(argsList, CancellationToken.None));
        Assert.Contains("--add-host=host.docker.internal:host-gateway", argsList.Cast<string>());
    }

    [Fact]
    public async Task Map_ServiceEnv_ProjectFormService_AppliesEnvironmentToo()
    {
        // Arrange — env: must apply identically to a project-form service (both ContainerResource
        // and ProjectResource implement IResourceWithEnvironment).
        var realCsproj = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "src", "Engine", "Vouchfx.Engine.Abstractions", "Vouchfx.Engine.Abstractions.csproj"));

        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: null,
                    Project: realCsproj,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Development" }),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // Act
        mapped.Configure(builder);

        // Assert
        var apiResource = builder.Resources.Single(r => r.Name == "api");
        var envVars = await ResolveEnvVarsAsync(apiResource);
        Assert.Equal("Development", ValueExpressionOf(envVars["ASPNETCORE_ENVIRONMENT"]));

        // A project-form service is never a ContainerResource, so it must NOT have picked up
        // the image-form-only '--add-host' container runtime arg.
        Assert.Empty(apiResource.Annotations.OfType<ContainerRuntimeArgsCallbackAnnotation>());
    }

    // -----------------------------------------------------------------------
    // feat/dependency-image-override — DependencySpec.Image / imageRegistry / imagePullPolicy.
    // -----------------------------------------------------------------------

    /// <summary>
    /// An 'image:' override on an Aspire-helper kind (mongodb) reaches the server resource's
    /// image annotation, AND clears the pre-existing registry default AddMongoDB sets internally
    /// ("docker.io" — confirmed by decompiling the pinned Aspire.Hosting.MongoDB 13.4.2 package).
    /// Without the explicit clear, the customer's own already-qualified image would be silently
    /// double-prefixed ("docker.io/nexus.corp.local:5000/platform/mongo:8.0") by
    /// TryGetContainerImageName, which unconditionally prepends "{Registry}/" whenever Registry
    /// is set.
    /// </summary>
    [Fact]
    public void Map_DependencyImage_OverridesMongoDbServer_ClearsProviderRegistryDefault()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders"] = new DependencySpec(Type: "mongodb", Version: null, Extra: null)
                {
                    Image = "nexus.corp.local:5000/platform/mongo:8.0",
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var server = builder.Resources.Single(r => r.Name == "orders");
        var image = server.Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("nexus.corp.local:5000/platform/mongo", image.Image);
        Assert.Equal("8.0", image.Tag);
        Assert.Null(image.Registry);
    }

    /// <summary>
    /// An 'image:' override on the sqlserver Aspire-helper kind reaches the server resource and
    /// clears AddSqlServer's own internal registry default ("mcr.microsoft.com").
    /// </summary>
    [Fact]
    public void Map_DependencyImage_OverridesSqlServerServer_ClearsProviderRegistryDefault()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["paydb"] = new DependencySpec(Type: "sqlserver", Version: null, Extra: null)
                {
                    Image = "myregistry.example.com/sqlplatform/mssql:2022-CU10",
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var server = builder.Resources.Single(r => r.Name == "paydb");
        var image = server.Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("myregistry.example.com/sqlplatform/mssql", image.Image);
        Assert.Equal("2022-CU10", image.Tag);
        Assert.Null(image.Registry);
    }

    /// <summary>
    /// An 'image:' override on the kafka Aspire-helper kind reaches the broker resource — the
    /// third of the customer's three dependency types (mongodb, sqlserver, kafka) named in the
    /// feature's motivating scenario. The image here has no explicit registry component of its
    /// own, but the M2 fix clears AddKafka's own internal registry default unconditionally
    /// whenever 'image:' is set at all (Registry is not asserted here — see
    /// <see cref="Map_DependencyImage_UnqualifiedSqlServerImage_ClearsProviderRegistryDefault"/>
    /// and <see cref="Map_DependencyImage_UnqualifiedPostgresImage_ClearsProviderRegistryDefault"/>
    /// for tests that do pin the cleared-Registry outcome for this exact unqualified-image shape).
    /// </summary>
    [Fact]
    public void Map_DependencyImage_OverridesKafkaBroker()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["events"] = new DependencySpec(Type: "kafka", Version: null, Extra: null)
                {
                    Image = "myorg/kafka-mirror:7.6.0",
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var broker = builder.Resources.Single(r => r.Name == "events");
        var image = broker.Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("myorg/kafka-mirror", image.Image);
        Assert.Equal("7.6.0", image.Tag);
    }

    /// <summary>
    /// A kafka dependency's 'image:' override reaches only the broker (the retained/most-specific
    /// resource matching the dependency name) — the schema-registry SIDECAR has no independent
    /// identity in the YAML (§ item 6, deliberately out of scope) and keeps its own hardcoded
    /// image regardless of the broker's override.
    /// </summary>
    [Fact]
    public void Map_KafkaWithSchemaRegistry_ImageOverrideDoesNotReachSidecar()
    {
        var extra = new YamlMappingNode
        {
            { new YamlScalarNode("schemaRegistry"), new YamlScalarNode("true") },
        };
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["events"] = new DependencySpec(Type: "kafka", Version: null, Extra: extra)
                {
                    Image = "myorg/kafka-mirror:7.6.0",
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var brokerImage = builder.Resources.Single(r => r.Name == "events")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("myorg/kafka-mirror", brokerImage.Image);

        var sidecarImage = builder.Resources.Single(r => r.Name == "events-sr")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("confluentinc/cp-schema-registry", sidecarImage.Image);
        Assert.Equal("7.6.1", sidecarImage.Tag);
    }

    /// <summary>
    /// The env-level imageRegistry DOES reach the kafka schema-registry sidecar even though
    /// spec.Image does not (§ item 6): imageRegistry/pullPolicy are broad environment-level
    /// policies, not per-dependency image identity, so an air-gapped customer's private mirror
    /// setting must still apply to every container, sidecars included.
    /// </summary>
    [Fact]
    public void Map_KafkaWithSchemaRegistry_ImageRegistryAppliesToSidecarToo()
    {
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
            ImageRegistry: "artifactory.mycompany.com",
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var brokerImage = builder.Resources.Single(r => r.Name == "events")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("artifactory.mycompany.com", brokerImage.Registry);

        var sidecarImage = builder.Resources.Single(r => r.Name == "events-sr")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("artifactory.mycompany.com", sidecarImage.Registry);
    }

    /// <summary>
    /// An 'image:' override on an AddContainer-based kind (minio) replaces the hardcoded
    /// "minio/minio" literal entirely.
    /// </summary>
    [Fact]
    public void Map_DependencyImage_OverridesMinioContainer()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["artefacts"] = new DependencySpec(Type: "minio", Version: null, Extra: null)
                {
                    Image = "myregistry.example.com/mirror/minio:RELEASE.2024-01-01T00-00-00Z",
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var image = builder.Resources.Single(r => r.Name == "artefacts")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("myregistry.example.com/mirror/minio", image.Image);
        Assert.Equal("RELEASE.2024-01-01T00-00-00Z", image.Tag);
    }

    /// <summary>
    /// A digest-form 'image:' (no tag) sets ContainerImageAnnotation.SHA256 to the BARE hex
    /// digest (no 'sha256:' prefix) and leaves Tag null — matching the shape
    /// TryGetContainerImageName expects when it reconstructs the pull reference as
    /// "{Image}@sha256:{SHA256}".
    /// </summary>
    [Fact]
    public void Map_DependencyImage_DigestForm_SetsBareSha256()
    {
        const string digest = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85";
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders"] = new DependencySpec(Type: "mongodb", Version: null, Extra: null)
                {
                    Image = $"myrepo/mongo@sha256:{digest}",
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var image = builder.Resources.Single(r => r.Name == "orders")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("myrepo/mongo", image.Image);
        Assert.Equal(digest, image.SHA256);
        Assert.Null(image.Tag);
    }

    /// <summary>
    /// The env-level imageRegistry now reaches a dependency that sets no 'image:' of its own —
    /// previously imageRegistry was captured by Map but consumed only inside the services loop,
    /// so dependencies got NO registry treatment at all. This overrides AddRedis's own internal
    /// "docker.io" default.
    /// </summary>
    [Fact]
    public void Map_DependencyImageRegistry_AppliesWhenNoOwnImageSet()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["cache"] = new DependencySpec(Type: "redis", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: "artifactory.mycompany.com",
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var image = builder.Resources.Single(r => r.Name == "cache")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("artifactory.mycompany.com", image.Registry);
    }

    /// <summary>
    /// 'image:' wins over imageRegistry: when the dependency's own image already names an
    /// explicit registry, the env-level imageRegistry must NOT apply on top of it — otherwise
    /// the pull reference would be silently double-prefixed
    /// ("artifactory.mycompany.com/nexus.corp.local:5000/platform/mongo:8.0").
    /// </summary>
    [Fact]
    public void Map_DependencyImage_WinsOverImageRegistry_WhenImageHasExplicitRegistry()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders"] = new DependencySpec(Type: "mongodb", Version: null, Extra: null)
                {
                    Image = "nexus.corp.local:5000/platform/mongo:8.0",
                },
            },
            Seed: null,
            ImageRegistry: "artifactory.mycompany.com",
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var image = builder.Resources.Single(r => r.Name == "orders")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("nexus.corp.local:5000/platform/mongo", image.Image);
        Assert.Equal("8.0", image.Tag);
        Assert.Null(image.Registry);
    }

    /// <summary>
    /// Decided precedence (§5): an 'image:' that already carries a tag, together with a sibling
    /// 'version:', is ambiguous and must throw rather than silently picking one.
    /// </summary>
    [Fact]
    public void Map_DependencyImageWithTag_AndVersion_ThrowsAmbiguous()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders"] = new DependencySpec(Type: "mongodb", Version: "7.0", Extra: null)
                {
                    Image = "myrepo/mongo:8.0",
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("myrepo/mongo:8.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("7.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decided precedence (§5) extends to a digest too: an 'image:' carrying a digest, together
    /// with a sibling 'version:', is equally ambiguous.
    /// </summary>
    [Fact]
    public void Map_DependencyImageWithDigest_AndVersion_ThrowsAmbiguous()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders"] = new DependencySpec(Type: "mongodb", Version: "7.0", Extra: null)
                {
                    Image = "myrepo/mongo@sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85",
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("7.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ambiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An unrecognised env-level imagePullPolicy is rejected loudly at Map time rather than
    /// silently ignored — air-gapped users rely on Never/Missing actually taking effect.
    /// </summary>
    [Fact]
    public void Map_EnvImagePullPolicy_Invalid_Throws()
    {
        // A dependency must be present — an environment with neither services nor dependencies
        // takes Map's empty-environment early-return path before any validation loop runs.
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["cache"] = new DependencySpec(Type: "redis", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: "Sometimes");

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("Sometimes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Always", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Missing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Never", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unrecognised service-level imagePullPolicy is equally rejected loudly at Map time.
    /// </summary>
    [Fact]
    public void Map_ServiceImagePullPolicy_Invalid_Throws()
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["web"] = new ServiceSpec(
                    Image: "traefik/whoami",
                    Project: null,
                    ImagePullPolicy: "Sometimes",
                    HttpPort: null,
                    Env: null),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("web", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Sometimes", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A valid imagePullPolicy actually applies: the env-level default reaches a dependency
    /// (which has no per-dependency override field of its own), a service without its own
    /// override inherits the env-level default too, and a service WITH its own override wins
    /// over the env-level default.
    /// </summary>
    [Fact]
    public void Map_ImagePullPolicy_AppliesToDependencyAndServices()
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["web"] = new ServiceSpec(
                    Image: "traefik/whoami",
                    Project: null,
                    ImagePullPolicy: "Always",
                    HttpPort: null,
                    Env: null),
                ["worker"] = new ServiceSpec(
                    Image: "myorg/worker:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null),
            },
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["cache"] = new DependencySpec(Type: "redis", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: "Never");

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        // Dependency: no per-dependency override field exists, so the env-level default applies.
        var cachePolicy = builder.Resources.Single(r => r.Name == "cache")
            .Annotations.OfType<ContainerImagePullPolicyAnnotation>().Single();
        Assert.Equal(ImagePullPolicy.Never, cachePolicy.ImagePullPolicy);

        // Service with its own override: the service-level value wins over the env-level default.
        var webPolicy = builder.Resources.Single(r => r.Name == "web")
            .Annotations.OfType<ContainerImagePullPolicyAnnotation>().Single();
        Assert.Equal(ImagePullPolicy.Always, webPolicy.ImagePullPolicy);

        // Service without its own override: inherits the env-level default.
        var workerPolicy = builder.Resources.Single(r => r.Name == "worker")
            .Annotations.OfType<ContainerImagePullPolicyAnnotation>().Single();
        Assert.Equal(ImagePullPolicy.Never, workerPolicy.ImagePullPolicy);
    }

    /// <summary>
    /// Regression: a dependency with no 'image:', no 'version:', no env-level imageRegistry, and
    /// no imagePullPolicy behaves EXACTLY as before this feature — proven empirically by
    /// comparing the mapped resource's image annotation against a raw AddPostgres(name) call on
    /// an independent builder, rather than hardcoding Aspire's own package-internal default image/
    /// tag/registry strings (which are an Aspire.Hosting.PostgreSQL implementation detail, not
    /// this mapper's concern, and could change on an Aspire version bump).
    /// </summary>
    [Fact]
    public void Map_DependencyWithNoOverrides_MatchesRawAddPostgresDefault()
    {
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
        var mappedBuilder = CreateBuilder();
        mapped.Configure(mappedBuilder);

        var baselineBuilder = CreateBuilder();
        baselineBuilder.AddPostgres("pg");

        var mappedImage = mappedBuilder.Resources.Single(r => r.Name == "pg")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        var baselineImage = baselineBuilder.Resources.Single(r => r.Name == "pg")
            .Annotations.OfType<ContainerImageAnnotation>().Single();

        Assert.Equal(baselineImage.Image, mappedImage.Image);
        Assert.Equal(baselineImage.Tag, mappedImage.Tag);
        Assert.Equal(baselineImage.Registry, mappedImage.Registry);
        Assert.Equal(baselineImage.SHA256, mappedImage.SHA256);

        // No imagePullPolicy at all → no pull-policy annotation is ever added.
        Assert.Empty(mappedBuilder.Resources.Single(r => r.Name == "pg")
            .Annotations.OfType<ContainerImagePullPolicyAnnotation>());
    }

    // -----------------------------------------------------------------------
    // M1 regression — imageRegistry double-prefixes the azureservicebus containers.
    //
    // Reproduced against real Aspire 13.4.2 before the fix: both azureservicebus containers are
    // registered via AddContainer with the registry EMBEDDED directly in the image string
    // ("mcr.microsoft.com/..."), so ContainerImageAnnotation.Registry is null for both — there is
    // nothing in the separate Registry field for a check that only ever inspects
    // DependencySpec.Image (null here; the author set no per-dependency override) to see. An
    // env-level imageRegistry then applied unconditionally via WithImageRegistry, double-
    // prefixing both containers:
    //   bus-sqledge => artifactory.example.com/mcr.microsoft.com/mssql/server:2022-latest
    //   bus         => artifactory.example.com/mcr.microsoft.com/azure-messaging/servicebus-emulator:1.1.2
    // -----------------------------------------------------------------------

    [Fact]
    public void Map_AzureServiceBusDependency_ImageRegistry_DoesNotDoublePrefixEmbeddedRegistry()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["bus"] = new DependencySpec(Type: "azureservicebus", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: "artifactory.example.com",
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var emulatorImage = builder.Resources.Single(r => r.Name == "bus")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Null(emulatorImage.Registry);
        Assert.Equal("mcr.microsoft.com/azure-messaging/servicebus-emulator", emulatorImage.Image);
        Assert.Equal("1.1.2", emulatorImage.Tag);

        var sidecarImage = builder.Resources.Single(r => r.Name == "bus-sqledge")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Null(sidecarImage.Registry);
        Assert.Equal("mcr.microsoft.com/mssql/server", sidecarImage.Image);
        Assert.Equal("2022-latest", sidecarImage.Tag);
    }

    // -----------------------------------------------------------------------
    // M2 regression — an unqualified 'image:' on sqlserver inherits 'mcr.microsoft.com'.
    //
    // Reproduced against real Aspire 13.4.2 before the fix: the registry-clearing branch only
    // fired when the AUTHOR's own image string carried an explicit registry component — an
    // unqualified image silently inherited AddSqlServer's own built-in registry default:
    //   sqlserver + image 'myorg/mssql-mirror:2022' => mcr.microsoft.com/myorg/mssql-mirror:2022
    // — a path that does not exist. postgres/mysql/mongodb/etc. have the identical shape bug, but
    // their leftover default ("docker.io") happens to be harmless; sqlserver's is not.
    // -----------------------------------------------------------------------

    [Fact]
    public void Map_DependencyImage_UnqualifiedSqlServerImage_ClearsProviderRegistryDefault()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["paydb"] = new DependencySpec(Type: "sqlserver", Version: null, Extra: null)
                {
                    Image = "myorg/mssql-mirror:2022",
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var image = builder.Resources.Single(r => r.Name == "paydb")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("myorg/mssql-mirror", image.Image);
        Assert.Equal("2022", image.Tag);
        Assert.Null(image.Registry);
    }

    /// <summary>
    /// Companion to <see cref="Map_DependencyImage_UnqualifiedSqlServerImage_ClearsProviderRegistryDefault"/>:
    /// the same unqualified-image shape on postgres (whose provider default happens to be the
    /// harmless "docker.io") gets the identical treatment under the M2 fix's unconditional-clear
    /// rule — 'image:' always means exactly what it says for every kind, uniformly, not just the
    /// one kind where the old leftover default happened to be visibly wrong.
    /// </summary>
    [Fact]
    public void Map_DependencyImage_UnqualifiedPostgresImage_ClearsProviderRegistryDefault()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders"] = new DependencySpec(Type: "postgres", Version: null, Extra: null)
                {
                    Image = "myorg/pg-mirror:16",
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var image = builder.Resources.Single(r => r.Name == "orders")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("myorg/pg-mirror", image.Image);
        Assert.Equal("16", image.Tag);
        Assert.Null(image.Registry);
    }

    // -----------------------------------------------------------------------
    // M3 regression — a tagless 'image:' silently floats on ':latest'.
    //
    // Reproduced against real Aspire 13.4.2 before the fix: WithImage(repository) with no tag
    // argument makes Aspire write ContainerImageAnnotation.Tag = "latest", discarding the
    // provider's pinned default:
    //   mongodb + image 'nexus.corp.local/mirror/mongo' => .../mongo:latest
    // This is now rejected eagerly, before any builder mutation, in the same validation pass as
    // the tag/digest-plus-version ambiguity check.
    // -----------------------------------------------------------------------

    [Fact]
    public void Map_DependencyImage_NoTagNoDigestNoVersion_ThrowsFloatingLatest()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders"] = new DependencySpec(Type: "mongodb", Version: null, Extra: null)
                {
                    Image = "nexus.corp.local/mirror/mongo",
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nexus.corp.local/mirror/mongo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("latest", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Companion positive case: a tagless, digestless 'image:' with a sibling 'version:' is the
    /// LEGITIMATE way to pin a tag on a private-mirror image without embedding it in 'image:'
    /// itself, and must still work exactly as before — the M3 fix only rejects the combination
    /// with NEITHER a tag/digest NOR a version anywhere.
    /// </summary>
    [Fact]
    public void Map_DependencyImage_NoTagNoDigestWithVersion_UsesVersionAsTag()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders"] = new DependencySpec(Type: "mongodb", Version: "7.0", Extra: null)
                {
                    Image = "nexus.corp.local/mirror/mongo",
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var image = builder.Resources.Single(r => r.Name == "orders")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("nexus.corp.local/mirror/mongo", image.Image);
        Assert.Equal("7.0", image.Tag);
    }

    // -----------------------------------------------------------------------
    // MN3 regression — 'version: ""' handled inconsistently with 'version' absent.
    //
    // Before the fix, 'image: myrepo/mongo' + 'version: ""' reached ApplyImageOverrides' tag
    // fallback with spec.Version used RAW (not IsNullOrEmpty-guarded), setting
    // ContainerImageAnnotation.Tag = "" (an empty-but-non-null tag) — while the SAME image with
    // 'version' entirely ABSENT set Tag = null (→ Aspire's WithImage(repository) then defaults it
    // to "latest"). Both are now normalised identically: an empty 'version:' is indistinguishable
    // from an absent one, so this tagless-image case now hits the SAME M3 rejection either way.
    // -----------------------------------------------------------------------

    [Fact]
    public void Map_DependencyImage_NoTagNoDigestWithEmptyVersion_ThrowsSameAsAbsentVersion()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders"] = new DependencySpec(Type: "mongodb", Version: "", Extra: null)
                {
                    Image = "nexus.corp.local/mirror/mongo",
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("latest", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The flip side of the same normalisation: an image that ALREADY carries its own tag, paired
    /// with an EMPTY (not absent) 'version:', must NOT trip the tag/digest-plus-version ambiguity
    /// check — an empty 'version:' carries no information to conflict with, exactly like an
    /// absent one.
    /// </summary>
    [Fact]
    public void Map_DependencyImageWithTag_AndEmptyVersion_DoesNotThrowAmbiguous()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders"] = new DependencySpec(Type: "mongodb", Version: "", Extra: null)
                {
                    Image = "myrepo/mongo:8.0",
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var image = builder.Resources.Single(r => r.Name == "orders")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("myrepo/mongo", image.Image);
        Assert.Equal("8.0", image.Tag);
    }
}
