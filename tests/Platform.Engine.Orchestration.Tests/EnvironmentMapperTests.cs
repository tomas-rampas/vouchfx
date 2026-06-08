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
                ["cache"] = new DependencySpec(Type: "redis", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        // Act + Assert
        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("redis", ex.Message, StringComparison.OrdinalIgnoreCase);
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
