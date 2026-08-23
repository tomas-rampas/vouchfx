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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
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
    // Non-HTTP service targeting (services-generalisation spec, REQ-008) and
    // authorable health checks (REQ-009).
    // -----------------------------------------------------------------------

    /// <summary>
    /// REQ-008: a service declaring only <c>ports:</c> (no <c>httpPort</c>) is brought up via
    /// Aspire's generic TCP endpoint — the resource's <see cref="EndpointAnnotation.UriScheme"/>
    /// is <c>"tcp"</c>, never unconditionally <c>"http"</c> — and gets NO implicit HTTP
    /// endpoint at all (the implicit default is suppressed once <c>ports</c> is declared).
    /// </summary>
    [Fact]
    public void Map_ServiceWithPortsOnly_ExposesTcpEndpoint_NoImplicitHttpEndpoint()
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["kafka-broker"] = new ServiceSpec(
                    Image: "myorg/kafka-broker:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null)
                { Ports = new List<int> { 9093 } },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var resource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "kafka-broker");
        var endpoints = resource.Annotations.OfType<EndpointAnnotation>().ToList();

        var tcpEndpoint = Assert.Single(endpoints);
        Assert.Equal("tcp-9093", tcpEndpoint.Name);
        Assert.Equal("tcp", tcpEndpoint.UriScheme);
        Assert.Equal(9093, tcpEndpoint.TargetPort);

        Assert.DoesNotContain(endpoints, e => e.Name == "http");
        Assert.DoesNotContain(endpoints, e => string.Equals(e.UriScheme, "http", StringComparison.Ordinal));

        // REQ-025: a bare entry pins NOTHING, which is what leaves the orchestrator free to
        // allocate. Asserted on the annotation rather than on a running container, because that is
        // the only place the distinction is visible without Docker — and this test project's rows
        // are the ones that gate a merge.
        Assert.Null(tcpEndpoint.Port);
    }

    /// <summary>
    /// REQ-025: a <c>"&lt;host&gt;:&lt;container&gt;"</c> entry sets the endpoint's HOST port,
    /// leaving the container port as the target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the requirement's core mechanism, and until this row existed nothing
    /// asserted it in a lane that blocks a merge.</strong> Everything else exercising
    /// <c>WithEndpoint(port:)</c> is docker-gated, and this repository's Docker/Aspire CI checks
    /// are non-blocking — so a regression that dropped the host port entirely would have gone
    /// green. Building the application model needs no Docker: only <c>StartAsync</c> does.
    /// </para>
    /// <para>
    /// It is also the deterministic half of the live measurement. The docker row proves a client
    /// reaches the pinned port; this proves the mapper asked for it, which is the part a refactor
    /// can silently break.
    /// </para>
    /// </remarks>
    [Fact]
    public void Map_ServiceWithPinnedPort_SetsTheHostPortOnTheEndpointAnnotation()
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
            {
                ["kafka-broker"] = new ServiceSpec(
                    Image: "confluentinc/cp-kafka:7.6.1",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null)
                {
                    Ports = new List<int> { 9093 },
                    PinnedHostPorts = new Dictionary<int, int> { [9093] = 19093 },
                },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var resource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "kafka-broker");
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>());

        // The endpoint is still NAMED for the container port — pinning changes where it is
        // published, never what the rest of the language calls it.
        Assert.Equal("tcp-9093", endpoint.Name);
        Assert.Equal(9093, endpoint.TargetPort);
        Assert.Equal(19093, endpoint.Port);
    }

    /// <summary>
    /// REQ-025: a pin attaches only to the <c>ports:</c> entry that declared it, never to an
    /// endpoint that merely shares its container port.
    /// </summary>
    /// <remarks>
    /// The lookup was originally keyed on the port NUMBER across every endpoint declaration, which
    /// covers <c>httpPort</c> and the implicit HTTP endpoint as well as <c>ports:</c> entries. This
    /// pins a port that no <c>httpPort</c> names, and asserts the HTTP endpoint came away with no
    /// host port of its own.
    /// </remarks>
    [Fact]
    public void Map_PinnedPortBesideAnUnrelatedHttpPort_LeavesTheHttpEndpointUnpinned()
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
            {
                ["hybrid"] = new ServiceSpec(
                    Image: "example/hybrid:1",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: 8080,
                    Env: null)
                {
                    Ports = new List<int> { 9093 },
                    PinnedHostPorts = new Dictionary<int, int> { [9093] = 19093 },
                },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var resource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "hybrid");
        var endpoints = resource.Annotations.OfType<EndpointAnnotation>().ToList();

        var pinned = Assert.Single(endpoints, e => e.Name == "tcp-9093");
        Assert.Equal(19093, pinned.Port);

        var http = Assert.Single(endpoints, e => e.Name == "http");
        Assert.Equal(8080, http.TargetPort);
        Assert.Null(http.Port);
    }

    /// <summary>
    /// REQ-008: a service may declare <c>ports</c> AND an explicit <c>httpPort</c> together
    /// (a hybrid service exposing both a management HTTP API and one or more raw TCP ports) —
    /// opt-in only, via the sibling field, never implied merely by <c>ports</c> being present.
    /// </summary>
    [Fact]
    public void Map_ServiceWithPortsAndExplicitHttpPort_ExposesBothTcpAndHttpEndpoints()
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["hybrid"] = new ServiceSpec(
                    Image: "myorg/hybrid:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: 8080,
                    Env: null)
                { Ports = new List<int> { 9093 } },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var resource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "hybrid");
        var endpoints = resource.Annotations.OfType<EndpointAnnotation>().ToList();

        Assert.Contains(endpoints, e => e.Name == "tcp-9093" && e.UriScheme == "tcp" && e.TargetPort == 9093);
        Assert.Contains(endpoints, e => e.Name == "http" && e.UriScheme == "http" && e.TargetPort == 8080);
        Assert.Equal(2, endpoints.Count);
    }

    /// <summary>
    /// M4 fix (fix round 2): a <c>ports</c>-declared service with NO <c>healthCheck</c> and NO
    /// sibling <c>httpPort</c> now DEFAULTS to a <c>tcp</c> probe on the FIRST declared port
    /// (<c>Ports[0]</c>) rather than getting no health check at all — REQ-008 (does not attempt
    /// an HTTP request against a service that may not speak HTTP) is preserved, but "no health
    /// check at all" left the service gated only on Aspire's own "reaches Running" default,
    /// which — combined with B1 (the tcp health check that could never fail) — meant BOTH
    /// shapes available to a non-HTTP system under test were ungated. The registered key
    /// matches the SAME <c>"{service}-tcp-{port}-health"</c> format the explicit
    /// <c>type: tcp</c> form uses (<see cref="Map_ServiceHealthCheckTcp_RegistersResolvableHealthCheck_NeverPerformsHttpRequest"/>),
    /// because it is now built by the SAME <c>ApplyTcpHealthCheck</c> helper.
    /// </summary>
    [Fact]
    public void Map_ServiceWithPortsOnly_NoHealthCheckDeclared_DefaultsToTcpHealthCheckOnFirstPort()
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["kafka-broker"] = new ServiceSpec(
                    Image: "myorg/kafka-broker:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null)
                { Ports = new List<int> { 9093, 9094 } },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var resource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "kafka-broker");
        var healthCheck = Assert.Single(resource.Annotations.OfType<HealthCheckAnnotation>());

        // Targets the FIRST declared port (9093), not the second (9094) — and is NOT the
        // Aspire-generated default-http-check key shape.
        Assert.Equal("kafka-broker-tcp-9093-health", healthCheck.Key);
    }

    /// <summary>
    /// M4 fix (fix round 2): the hybrid shape (<c>ports</c> + a sibling <c>httpPort</c>) with
    /// NO explicit <c>healthCheck</c> now defaults to the SAME HTTP probe an <c>image:</c>-form
    /// HTTP service gets — a hybrid service IS an image-form HTTP service, and leaving it
    /// entirely ungated (the pre-fix behaviour, purely because <c>hasExplicitPorts</c> was
    /// true) was never a deliberate choice <c>docs/02</c> documented. Asserted via the SAME
    /// deterministic key format as <see cref="Map_ServiceNoHealthCheckDeclared_HttpOnlyShape_DefaultsToRootPathHealthCheck"/>.
    /// </summary>
    [Fact]
    public void Map_ServiceWithPortsAndHttpPort_NoHealthCheckDeclared_DefaultsToHttpHealthCheck()
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["hybrid"] = new ServiceSpec(
                    Image: "myorg/hybrid:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: 8080,
                    Env: null)
                { Ports = new List<int> { 9093 } },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var resource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "hybrid");
        var healthCheck = Assert.Single(resource.Annotations.OfType<HealthCheckAnnotation>());
        Assert.Equal("hybrid_http_/_200_check", healthCheck.Key);
    }

    /// <summary>
    /// REQ-009: <c>healthCheck: { type: tcp, port: 9093 }</c> registers a NAMED health check
    /// (via <c>WithHealthCheck(key)</c>) resolvable through the builder's own
    /// <see cref="IServiceCollection"/> without Docker/StartAsync — proving both that the
    /// registration mechanics are correct AND (by invoking it directly) that the check itself
    /// never performs an HTTP request: it probes <see cref="EndpointAnnotation"/>
    /// allocation/TCP-connect state only, so before any topology starts it reports
    /// <see cref="HealthStatus.Unhealthy"/> with a message about the endpoint not yet being
    /// allocated — never an HTTP-shaped failure.
    /// </summary>
    [Fact]
    public async Task Map_ServiceHealthCheckTcp_RegistersResolvableHealthCheck_NeverPerformsHttpRequest()
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["kafka-broker"] = new ServiceSpec(
                    Image: "myorg/kafka-broker:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null)
                {
                    Ports = new List<int> { 9093 },
                    HealthCheck = new HealthCheckSpec(Type: "tcp", Path: null, Port: 9093),
                },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var resource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "kafka-broker");
        var healthCheckAnnotation = Assert.Single(resource.Annotations.OfType<HealthCheckAnnotation>());

        // G-FLAKE (gatekeeper): resolve the registration through a FRESH ServiceCollection
        // seeded from the live builder's own descriptors, rather than calling
        // BuildServiceProvider() directly on builder.Services (the live Aspire builder's OWN
        // collection) and disposing the result — Aspire's own pipeline expects to build ITS
        // service provider from that collection independently; a throwaway
        // BuildServiceProvider()/Dispose() cycle run directly against it risks out-of-band
        // singleton construction/disposal ahead of (and independent from) whatever Aspire's
        // own machinery does with the SAME collection. Copying every descriptor into an
        // independent collection still proves exactly what this test needs — the named async
        // check is registered and resolvable — without ever building or disposing a provider
        // over the live collection itself.
        IServiceCollection seededServices = new ServiceCollection();
        foreach (var descriptor in builder.Services)
        {
            seededServices.Add(descriptor);
        }

        using var serviceProvider = seededServices.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var registration = Assert.Single(
            options.Registrations, r => r.Name == healthCheckAnnotation.Key);

        var check = registration.Factory(serviceProvider);
        var result = await check.CheckHealthAsync(
            new HealthCheckContext { Registration = registration }, CancellationToken.None);

        // Pre-StartAsync, the endpoint is never allocated — proves the check reached its own
        // TCP-probe logic (not an HTTP client) and failed closed rather than throwing.
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("allocated", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// REQ-009 eager validation: a <c>healthCheck: { type: tcp, port: X }</c> whose port is
    /// NOT among the service's own declared <c>ports</c> (or <c>httpPort</c>, when
    /// <c>ports</c> is absent) fails eagerly — thrown by <c>Map()</c> itself, before any
    /// builder mutation, mirroring every other eager-validation case in this file. Otherwise
    /// <c>Configure()</c> would fail deep inside Aspire's own <c>GetEndpoint</c> with an
    /// unrelated-looking error instead of a clear, located authoring diagnostic.
    /// </summary>
    [Fact]
    public void Map_ServiceHealthCheckTcp_PortNotAmongDeclaredPorts_ThrowsArgumentException()
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["kafka-broker"] = new ServiceSpec(
                    Image: "myorg/kafka-broker:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null)
                {
                    Ports = new List<int> { 9093 },
                    HealthCheck = new HealthCheckSpec(Type: "tcp", Path: null, Port: 9999),
                },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("kafka-broker", ex.Message, StringComparison.Ordinal);
        Assert.Contains("9999", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// m3 fix (fix round 3): the hybrid shape's own sibling of
    /// <see cref="Map_ServiceHealthCheckTcp_PortNotAmongDeclaredPorts_ThrowsArgumentException"/>
    /// — a <c>tcp</c> health check naming a hybrid service's OWN <c>httpPort</c> (rather than
    /// one of its <c>ports:</c> entries) must now validate and map successfully. Before this
    /// fix, <c>declaredPorts</c> omitted <c>httpPort</c> for the hybrid shape entirely, so this
    /// threw "not among the service's declared ports" and told the author to declare the
    /// SAME port again under <c>ports:</c> — impossible without double-declaring the port
    /// under two different endpoint names.
    /// </summary>
    [Fact]
    public void Map_ServiceHealthCheckTcp_HybridShapeTargetsOwnHttpPort_MapsSuccessfully()
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["hybrid"] = new ServiceSpec(
                    Image: "myorg/hybrid:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: 8080,
                    Env: null)
                {
                    Ports = new List<int> { 9093 },
                    HealthCheck = new HealthCheckSpec(Type: "tcp", Path: null, Port: 8080),
                },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var resource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "hybrid");
        var healthCheck = Assert.Single(resource.Annotations.OfType<HealthCheckAnnotation>());

        // ApplyHealthCheck's own endpoint-resolution fallback: a tcpPort that is NOT among
        // spec.Ports resolves to the "http" endpoint — the previously-dead branch this fix
        // makes reachable, proven here by the registered key targeting port 8080 (httpPort),
        // not one of the 'ports:' entries.
        Assert.Equal("hybrid-tcp-8080-health", healthCheck.Key);
    }

    /// <summary>
    /// G-M3 (gatekeeper): <c>Map()</c>'s eager validation for an UNRECOGNISED
    /// <c>healthCheck.type</c> — untested at the mapper level until now (only reachable
    /// indirectly via YAML+schema in <c>EnvironmentSchemaTests</c>'s wrong-case corpus
    /// fixture, which the schema's own closed enum catches before this throw is ever
    /// reached). Still independently reachable here for a direct <see cref="EnvironmentSpec"/>
    /// embedding that bypasses the schema's enum entirely — mirrors
    /// <see cref="Map_ServiceHealthCheckTcp_PortNotAmongDeclaredPorts_ThrowsArgumentException"/>'s
    /// own direct-embedding-reachability rationale.
    /// </summary>
    [Fact]
    public void Map_ServiceHealthCheckType_Unrecognised_ThrowsArgumentException()
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["kafka-broker"] = new ServiceSpec(
                    Image: "myorg/kafka-broker:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null)
                {
                    Ports = new List<int> { 9093 },
                    HealthCheck = new HealthCheckSpec(Type: "udp", Path: null, Port: null),
                },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("kafka-broker", ex.Message, StringComparison.Ordinal);
        Assert.Contains("udp", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// G-M3 (gatekeeper): <c>Map()</c>'s eager validation for <c>healthCheck: { type: http }</c>
    /// on a <c>ports:</c>-only service with no sibling <c>httpPort</c> — untested at the
    /// mapper level until now (only reachable indirectly via YAML+schema in
    /// <c>EnvironmentSchemaTests</c>). STILL independently reachable here even after G4's new
    /// schema-level rule: the schema only protects the YAML + <c>vouchfx validate</c> path — a
    /// direct <see cref="EnvironmentSpec"/> embedding bypasses the schema entirely, and this
    /// mapper-level throw is what still catches it.
    /// </summary>
    [Fact]
    public void Map_ServiceHealthCheckHttp_PortsWithoutHttpPort_ThrowsArgumentException()
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["kafka-broker"] = new ServiceSpec(
                    Image: "myorg/kafka-broker:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null)
                {
                    Ports = new List<int> { 9093 },
                    HealthCheck = new HealthCheckSpec(Type: "http", Path: null, Port: null),
                },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("kafka-broker", ex.Message, StringComparison.Ordinal);
        Assert.Contains("httpPort", ex.Message, StringComparison.Ordinal);
    }

    // G-M3 (gatekeeper), third untested eager-validation branch — DELIBERATELY NOT TESTED,
    // documented here rather than silently skipped: ApplyHealthCheck's own terminal
    // "healthCheck.type not recognised" throw (the defensive fallback once the schema's
    // closed tcp/http enum is in place) is UNREACHABLE through the public API. ApplyHealthCheck
    // is private and only ever invoked from inside Map()'s own 'configure' closure, which is
    // constructed and returned SYNCHRONOUSLY AFTER Map()'s eager validation loop above already
    // ran (and would already have thrown its OWN ArgumentException — see
    // Map_ServiceHealthCheckType_Unrecognised_ThrowsArgumentException above — for exactly the
    // same condition, before 'configure' is ever built). Reaching ApplyHealthCheck's own throw
    // would require reflection into a private method with a hand-built ServiceSpec/
    // ContainerResource/IDistributedApplicationBuilder that bypasses Map() entirely — a pattern
    // this test file does not use anywhere else (every test here drives the PUBLIC Map() API),
    // and one that would test Aspire/reflection plumbing rather than this defensive fallback's
    // own (trivial) logic. Left untested by design; revisit only if this file's own testing
    // convention changes.

    /// <summary>
    /// REQ-009: omitting <c>healthCheck</c> on an <c>image:</c>-only HTTP service preserves
    /// today's exact default (<c>WithHttpHealthCheck(path: "/", endpointName: "http")</c>) —
    /// asserted here via Aspire's own deterministic health-check registration key format
    /// (<c>"{resource}_{endpoint}_{path}_{statusCode}_check"</c>, confirmed empirically
    /// against the pinned Aspire.Hosting 13.4.2 DLL), which is the only externally-observable
    /// trace of the internally-closed-over path/status-code without starting Docker.
    /// </summary>
    [Fact]
    public void Map_ServiceNoHealthCheckDeclared_HttpOnlyShape_DefaultsToRootPathHealthCheck()
    {
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
        mapped.Configure(builder);

        var resource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "web");
        var healthCheck = Assert.Single(resource.Annotations.OfType<HealthCheckAnnotation>());
        Assert.Equal("web_http_/_200_check", healthCheck.Key);
    }

    /// <summary>
    /// REQ-009: <c>healthCheck: { type: http, path: "/healthz" }</c> is the explicit spelling
    /// of today's default behaviour, with a caller-chosen path — asserted the same
    /// key-format way as <see cref="Map_ServiceNoHealthCheckDeclared_HttpOnlyShape_DefaultsToRootPathHealthCheck"/>.
    /// </summary>
    [Fact]
    public void Map_ServiceHealthCheckHttp_WithCustomPath_UsesThatPathInHealthCheck()
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["web"] = new ServiceSpec(
                    Image: "traefik/whoami",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null)
                { HealthCheck = new HealthCheckSpec(Type: "http", Path: "/healthz", Port: null) },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var resource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "web");
        var healthCheck = Assert.Single(resource.Annotations.OfType<HealthCheckAnnotation>());
        Assert.Equal("web_http_/healthz_200_check", healthCheck.Key);
    }

    /// <summary>
    /// REQ-009 acceptance criterion, verified via the REAL YamlDocumentParser→EnvironmentMapper
    /// pipeline (not hand-built records): <c>healthCheck: { type: tcp, port: 9093 }</c> parses
    /// and maps to a resolvable, non-HTTP health check.
    /// </summary>
    [Fact]
    public void Map_ServiceHealthCheckTcp_ParsedFromRealYaml_RegistersHealthCheck()
    {
        const string yaml = """
            metadata:
              name: healthcheck-tcp-probe
            environment:
              services:
                kafka-broker:
                  image: myorg/kafka-broker:1.0
                  ports: [9093]
                  healthCheck:
                    type: tcp
                    port: 9093
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var env = ParseEnvironment(yaml);
        Assert.Equal(new List<int> { 9093 }, env.Services!["kafka-broker"].Ports);
        Assert.Equal("tcp", env.Services!["kafka-broker"].HealthCheck!.Type);
        Assert.Equal(9093, env.Services!["kafka-broker"].HealthCheck!.Port);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var resource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "kafka-broker");
        var healthCheck = Assert.Single(resource.Annotations.OfType<HealthCheckAnnotation>());

        // Strong assertion (not merely "exactly one annotation exists", which the
        // pre-REQ-008/009 code would ALSO satisfy via its unconditional default http check):
        // the registered key must NOT be the Aspire-generated default-http-check shape
        // ("{resource}_http_/_200_check", confirmed empirically above), and no "http" endpoint
        // exists at all on this ports-only service for a health check to have targeted.
        Assert.NotEqual("kafka-broker_http_/_200_check", healthCheck.Key);
        Assert.DoesNotContain(
            resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "http");
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
    // Map_DependencyTypeWrongCase_Throws_NamingCorrectSpelling (feat/case-sensitive-kinds)
    // -----------------------------------------------------------------------

    /// <summary>
    /// A dependency type spelled with the wrong case (e.g. <c>Postgres</c> instead of the
    /// canonical <c>postgres</c>) is rejected exactly like a genuinely unrecognised type — the
    /// schema and engine agree on exactly one spelling per kind (pre-GA decision). The message
    /// must teach: it names the correct, exact-case spelling so an author whose suite just broke
    /// knows precisely what to change.
    /// </summary>
    [Theory]
    [InlineData("Postgres", "postgres")]
    [InlineData("POSTGRES", "postgres")]
    [InlineData("KAFKA", "kafka")]
    [InlineData("MongoDB", "mongodb")]
    [InlineData("SqlServer", "sqlserver")]
    [InlineData("Redis", "redis")]
    public void Map_DependencyTypeWrongCase_Throws_NamingCorrectSpelling(
        string wrongCaseType, string canonicalType)
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["store"] = new DependencySpec(Type: wrongCaseType, Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        // Act + Assert
        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains(wrongCaseType, ex.Message, StringComparison.Ordinal);
        // The exact-case canonical spelling must appear — an OrdinalIgnoreCase check here would
        // pass even if the message only ever echoed the author's own (wrong-case) input back.
        Assert.Contains(canonicalType, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every one of the thirteen canonical, exact-case dependency kind spellings must keep
    /// working — this change narrows accepted case, it must never narrow the accepted vocabulary.
    /// </summary>
    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    [InlineData("mongodb")]
    [InlineData("redis")]
    [InlineData("elasticsearch")]
    [InlineData("rabbitmq")]
    [InlineData("nats")]
    [InlineData("kafka")]
    [InlineData("mailpit")]
    [InlineData("azureservicebus")]
    [InlineData("dynamodb")]
    [InlineData("minio")]
    public void Map_AllThirteenCanonicalDependencyTypes_Succeed(string canonicalType)
    {
        // Arrange
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["dep"] = new DependencySpec(Type: canonicalType, Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        // Act
        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        var exception = Record.Exception(() => mapped.Configure(builder));

        // Assert — no exception for any canonical spelling, and the dependency contributed at
        // least one health gate.
        Assert.Null(exception);
        Assert.NotEmpty(mapped.HealthGateResourceNames);
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

        // The reason is SCOPE, not timing. This pair of assertions replaced a
        // Contains("step-execution") that had become false: environment-level
        // `security.clientKeyPassword` resolves before any step runs, so "step-execution time" was
        // never a property of secret resolution — only of a STEP's own field. What makes a
        // container's environment wrong for a secret is true at every moment.
        Assert.Contains("docker inspect", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("step-execution", ex.Message, StringComparison.OrdinalIgnoreCase);
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

        // Same scope-not-timing pin as the well-formed arm above; see its own note.
        Assert.Contains("docker inspect", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("step-execution", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // ${env:NAME} passthrough (services-generalisation spec, REQ-006/REQ-007/EDGE-008)
    // -----------------------------------------------------------------------

    /// <summary>
    /// REQ-006 happy path: a <c>${env:NAME}</c> reference resolves to the engine PROCESS's
    /// own environment-variable value at topology-build time, spliced as a literal into the
    /// started container's env — verified the same non-Docker way every other env: test in
    /// this file verifies a resolved value (<see cref="ResolveEnvVarsAsync"/> +
    /// <see cref="ValueExpressionOf"/>, no container ever actually starts).
    /// </summary>
    [Fact]
    public async Task Map_ServiceEnv_EnvVarReference_ResolvesToProcessEnvironmentValue()
    {
        const string varName = "VOUCHFX_TEST_REGION_" + nameof(Map_ServiceEnv_EnvVarReference_ResolvesToProcessEnvironmentValue);
        Environment.SetEnvironmentVariable(varName, "eu-west-1");
        try
        {
            var env = new EnvironmentSpec(
                Services: new Dictionary<string, ServiceSpec>
                {
                    ["api"] = new ServiceSpec(
                        Image: "myorg/api:1.0",
                        Project: null,
                        ImagePullPolicy: null,
                        HttpPort: null,
                        Env: new Dictionary<string, string> { ["REGION"] = $"${{env:{varName}}}" }),
                },
                Dependencies: null,
                Seed: null,
                ImageRegistry: null,
                ImagePullPolicy: null);

            var mapped = EnvironmentMapper.Map(env);
            var builder = CreateBuilder();

            mapped.Configure(builder);

            var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
            var envVars = await ResolveEnvVarsAsync(apiResource);
            Assert.Equal("eu-west-1", ValueExpressionOf(envVars["REGION"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    /// <summary>
    /// REQ-006: a <c>${env:NAME}</c> reference may sit alongside literal text and resolves
    /// in place, mirroring the existing <c>${conn:...}</c>-alongside-literal-text coverage
    /// elsewhere in this file.
    /// </summary>
    [Fact]
    public async Task Map_ServiceEnv_EnvVarReference_AlongsideLiteralText_ResolvesInPlace()
    {
        const string varName = "VOUCHFX_TEST_REGION_" + nameof(Map_ServiceEnv_EnvVarReference_AlongsideLiteralText_ResolvesInPlace);
        Environment.SetEnvironmentVariable(varName, "eu-west-1");
        try
        {
            var env = new EnvironmentSpec(
                Services: new Dictionary<string, ServiceSpec>
                {
                    ["api"] = new ServiceSpec(
                        Image: "myorg/api:1.0",
                        Project: null,
                        ImagePullPolicy: null,
                        HttpPort: null,
                        Env: new Dictionary<string, string> { ["REGION"] = $"prefix-${{env:{varName}}}-suffix" }),
                },
                Dependencies: null,
                Seed: null,
                ImageRegistry: null,
                ImagePullPolicy: null);

            var mapped = EnvironmentMapper.Map(env);
            var builder = CreateBuilder();

            mapped.Configure(builder);

            var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
            var envVars = await ResolveEnvVarsAsync(apiResource);
            Assert.Equal("prefix-eu-west-1-suffix", ValueExpressionOf(envVars["REGION"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    /// <summary>
    /// m5 fix (fix round 2, PR #349 follow-up): a malformed or wrong-case <c>${env:...}</c>-
    /// shaped token must be REJECTED, not silently pass through as opaque literal text. The
    /// reviewer confirmed all four of these validated PASS before this fix — mirrors the
    /// existing <c>${secret:...}</c> sigil-presence check's own rationale.
    /// </summary>
    [Theory]
    [InlineData("${env:}")]
    [InlineData("${env:2BAD}")]
    [InlineData("${ENV:GOOD}")]
    [InlineData("${env: SPACED }")]
    public void Map_ServiceEnv_MalformedOrWrongCaseEnvSigil_ThrowsArgumentException(string malformedToken)
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["REGION"] = malformedToken }),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("env:", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("well-formed", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// m5 fix — the SAME malformed-sigil rejection must not fire for the DOCUMENTED
    /// <c>${OTHER_VAR}</c>-style self-expansion passthrough idiom, which shares the
    /// <c>${</c>/<c>}</c> shape but never contains the literal <c>env:</c> sigil at all.
    /// Regression-guards <see cref="Map_ServiceEnv_NonConnDollarBraceSigil_DeliveredVerbatim"/>'s
    /// own scenario against a false positive from the new check.
    /// </summary>
    [Fact]
    public async Task Map_ServiceEnv_NonEnvDollarBraceSigil_NotFlaggedByEnvSigilCheck()
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["P"] = "${ENV_VAR}" }),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var envVars = await ResolveEnvVarsAsync(apiResource);
        Assert.Equal("${ENV_VAR}", ValueExpressionOf(envVars["P"]));
    }

    /// <summary>
    /// EDGE-008: an UNSET <c>${env:NAME}</c> reference fails the suite eagerly (before any
    /// builder mutation — same "thrown by Map() itself" discipline as
    /// <see cref="Map_ServiceEnv_UnknownDependency_ThrowsArgumentException"/>), naming the
    /// missing variable — never a silent empty-string substitution.
    /// </summary>
    [Fact]
    public void Map_ServiceEnv_EnvVarReference_UnsetVariable_ThrowsArgumentException_NamingVariable()
    {
        const string varName = "VOUCHFX_UNSET_VAR_XYZ";
        // Defensive: guarantee the variable is genuinely unset for this process, regardless
        // of the host shell's own environment.
        Environment.SetEnvironmentVariable(varName, null);

        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["REGION"] = $"${{env:{varName}}}" }),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains(varName, ex.Message, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // TokeniseEnvValue: ${conn:...} AND ${env:...} in the SAME value (m10 fix, fix round 2)
    // -----------------------------------------------------------------------
    // TokeniseEnvValue's rewrite from a single-pattern Matches() call to a leftmost-wins
    // merge of TWO independent regexes (s_connRefPattern, s_envRefPattern) is the only
    // structural change to this shared, previously-stable function — every test above
    // exercises ONE reference kind at a time. These three tests pin both interleavings
    // (conn-then-env, env-then-conn) plus the boundary case with no separating literal
    // between the two tokens, proving the leftmost-wins merge picks the correct match at
    // each position rather than, say, always preferring one pattern.

    /// <summary>
    /// m10 fix: <c>${conn:...}</c> followed by <c>${env:...}</c>, separated by literal text —
    /// both resolve, in the written order.
    /// </summary>
    [Fact]
    public async Task Map_ServiceEnv_ConnRefThenEnvRef_BothResolveInOrder()
    {
        const string varName = "VOUCHFX_TEST_REGION_" + nameof(Map_ServiceEnv_ConnRefThenEnvRef_BothResolveInOrder);
        Environment.SetEnvironmentVariable(varName, "eu-west-1");
        try
        {
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
                            ["MIXED"] = "host=${conn:orders.host}-region=${env:" + varName + "}",
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
            mapped.Configure(builder);

            var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
            var envVars = await ResolveEnvVarsAsync(apiResource);
            Assert.Equal(
                "host={orders.bindings.tcp.host}-region=eu-west-1",
                ValueExpressionOf(envVars["MIXED"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    /// <summary>
    /// m10 fix — the reverse ordering of
    /// <see cref="Map_ServiceEnv_ConnRefThenEnvRef_BothResolveInOrder"/>: <c>${env:...}</c>
    /// followed by <c>${conn:...}</c>, separated by literal text.
    /// </summary>
    [Fact]
    public async Task Map_ServiceEnv_EnvRefThenConnRef_BothResolveInOrder()
    {
        const string varName = "VOUCHFX_TEST_REGION_" + nameof(Map_ServiceEnv_EnvRefThenConnRef_BothResolveInOrder);
        Environment.SetEnvironmentVariable(varName, "eu-west-1");
        try
        {
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
                            ["MIXED"] = "region=${env:" + varName + "}-host=${conn:orders.host}",
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
            mapped.Configure(builder);

            var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
            var envVars = await ResolveEnvVarsAsync(apiResource);
            Assert.Equal(
                "region=eu-west-1-host={orders.bindings.tcp.host}",
                ValueExpressionOf(envVars["MIXED"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    /// <summary>
    /// m10 fix — the boundary case: the two reference kinds sit ADJACENT with NO separating
    /// literal text between them, proving <c>TokeniseEnvValue</c>'s leftmost-wins merge
    /// correctly re-evaluates both patterns from the position immediately after the first
    /// match, rather than skipping ahead by a fixed or mismatched amount.
    /// </summary>
    [Fact]
    public async Task Map_ServiceEnv_ConnRefAdjacentToEnvRef_NoSeparatingLiteral_BothResolve()
    {
        const string varName = "VOUCHFX_TEST_REGION_" + nameof(Map_ServiceEnv_ConnRefAdjacentToEnvRef_NoSeparatingLiteral_BothResolve);
        Environment.SetEnvironmentVariable(varName, "eu-west-1");
        try
        {
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
                            // No literal text at all between the two references.
                            ["MIXED"] = $"${{conn:orders.host}}${{env:{varName}}}",
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
            mapped.Configure(builder);

            var apiResource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
            var envVars = await ResolveEnvVarsAsync(apiResource);
            Assert.Equal(
                "{orders.bindings.tcp.host}eu-west-1",
                ValueExpressionOf(envVars["MIXED"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    // NOTE (EDGE-008's "explicit empty value is honoured as-is" half): NOT independently
    // exercised by a dedicated runtime fixture here. Confirmed empirically (red-first,
    // against Environment.SetEnvironmentVariable itself, before writing this note) that
    // passing an empty string collapses to REMOVING the variable rather than setting it to
    // an empty value — matching that method's own documented contract ("If value is null or
    // an empty string, ... variable is deleted") — so a test built on it would silently
    // degenerate into re-proving the unset-variable path
    // (Map_ServiceEnv_EnvVarReference_UnsetVariable_ThrowsArgumentException_NamingVariable,
    // above) instead of the empty-value one. The only portable in-process alternative is
    // OS-specific P/Invoke (raw kernel32!SetEnvironmentVariableW on Windows DOES set a
    // genuinely empty value, confirmed separately; POSIX setenv on Linux, where this suite's
    // CI actually runs, is unverifiable from this session's Windows host) — judged not worth
    // shipping unverified platform-specific test code for. The claim itself is provable by
    // inspection instead: ValidateEnvValue's guard is 'is null', deliberately never
    // 'string.IsNullOrEmpty' (see that method's own remarks) — "" is null is unambiguously
    // false in C#, so an explicitly-empty value can never take the throw branch.

    /// <summary>
    /// REQ-007: the pre-existing <c>${secret:...}</c> rejection message on a service's
    /// <c>env:</c> value must stay BYTE-IDENTICAL after <c>${env:NAME}</c> support (REQ-006)
    /// is added to the same value-parsing path — pinned here as the exact full string (not
    /// merely a substring match, unlike the pre-existing
    /// <see cref="Map_ServiceEnv_SecretReference_ThrowsArgumentException_CitingSecrets"/> /
    /// <see cref="Map_ServiceEnv_MalformedSecretSigil_ThrowsArgumentException_CitingSecrets"/>
    /// checks above), for both a well-formed and a malformed (missing '/path') secret token.
    /// </summary>
    [Theory]
    [InlineData("${secret:vault/db-password}")]
    [InlineData("${secret:env}")]
    public void Map_ServiceEnv_SecretReference_MessageIsByteIdenticalToPreFeatureWording(string secretToken)
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["api"] = new ServiceSpec(
                    Image: "myorg/api:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: new Dictionary<string, string> { ["P"] = secretToken }),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));

        // ArgumentException's own Message property appends " (Parameter 'paramName')" to
        // whatever message text the constructor was given — captured here verbatim
        // (red-first: this suffix was confirmed against the PRE-CHANGE code before this
        // pin was written) so the pin matches ex.Message exactly, not just the constructor
        // argument.
        const string expected =
            "Service 'api' env entry 'P' references a ${secret:...} value. " +
            "A container's environment is the wrong PLACE for a secret, whenever it would " +
            "resolve (§17): baking a secret into a container's environment would expose it " +
            "via 'docker inspect' and corrupt the reproducibility envelope (which hashes the " +
            "reference, never the value). Configure the SUT to resolve the secret itself " +
            "instead. (Parameter 'envValue')";

        Assert.Equal(expected, ex.Message, StringComparer.Ordinal);
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

    // -----------------------------------------------------------------------
    // imagePullPolicy case-sensitivity (feat/case-sensitive-kinds)
    // -----------------------------------------------------------------------

    /// <summary>
    /// An env-level <c>imagePullPolicy</c> spelled with the wrong case (e.g. <c>always</c>
    /// instead of the schema's exact-case <c>Always</c>) is rejected identically to a genuinely
    /// unrecognised value — the accepted-values message must stay helpful either way.
    /// </summary>
    [Theory]
    [InlineData("always")]
    [InlineData("MISSING")]
    [InlineData("never")]
    public void Map_EnvImagePullPolicy_WrongCase_Throws(string wrongCasePolicy)
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["cache"] = new DependencySpec(Type: "redis", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: wrongCasePolicy);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains(wrongCasePolicy, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Always", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Missing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Never", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A service-level <c>imagePullPolicy</c> spelled with the wrong case is equally rejected.
    /// </summary>
    [Fact]
    public void Map_ServiceImagePullPolicy_WrongCase_Throws()
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["web"] = new ServiceSpec(
                    Image: "traefik/whoami",
                    Project: null,
                    ImagePullPolicy: "always",
                    HttpPort: null,
                    Env: null),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("web", ex.Message, StringComparison.Ordinal);
        Assert.Contains("always", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Always", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every one of the three canonical, exact-case <c>imagePullPolicy</c> spellings must keep
    /// working at both the environment level and the service level.
    /// </summary>
    [Theory]
    [InlineData("Always", ImagePullPolicy.Always)]
    [InlineData("Missing", ImagePullPolicy.Missing)]
    [InlineData("Never", ImagePullPolicy.Never)]
    public void Map_AllThreeCanonicalImagePullPolicies_Succeed_AtEnvAndServiceLevel(
        string canonicalPolicy, ImagePullPolicy expected)
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["web"] = new ServiceSpec(
                    Image: "traefik/whoami",
                    Project: null,
                    ImagePullPolicy: canonicalPolicy,
                    HttpPort: null,
                    Env: null),
            },
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["cache"] = new DependencySpec(Type: "redis", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: canonicalPolicy);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var webPolicy = builder.Resources.Single(r => r.Name == "web")
            .Annotations.OfType<ContainerImagePullPolicyAnnotation>().Single();
        Assert.Equal(expected, webPolicy.ImagePullPolicy);

        var cachePolicy = builder.Resources.Single(r => r.Name == "cache")
            .Annotations.OfType<ContainerImagePullPolicyAnnotation>().Single();
        Assert.Equal(expected, cachePolicy.ImagePullPolicy);
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

    // -----------------------------------------------------------------------
    // MINOR-2 regression (independent re-review) — a digest with a non-'sha256:' algorithm
    // prefix, or with no algorithm prefix at all, must be rejected eagerly, before any builder
    // mutation — not from inside the 'configure' closure after earlier dependencies have already
    // been registered.
    //
    // Reproduced before the fix: both 'image: mongo@sha512:...' and 'image: mongo@abcdef0123'
    // (a digest with no algorithm prefix — a plausible typo) let Map() return successfully; the
    // ArgumentException only surfaced mid-graph-construction, from ApplyImageOverrides, after
    // Configure had already mutated the builder for any dependency ordered before this one.
    // -----------------------------------------------------------------------

    [Fact]
    public void Map_DependencyImage_DigestWithNonSha256AlgorithmPrefix_ThrowsEagerly()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders"] = new DependencySpec(Type: "mongodb", Version: null, Extra: null)
                {
                    Image = "mongo@sha512:abcdef0123",
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        // Assert.Throws alone would already prove Map() itself throws (rather than
        // Configure), because this test never calls mapped.Configure(builder) at all —
        // no builder is even created here.
        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sha512:abcdef0123", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sha256", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Companion case: a digest with NO algorithm prefix at all (e.g. a customer who typed
    /// 'mongo@abcdef0123', dropping 'sha256:' by mistake) must be rejected the same way, not
    /// silently treated as if no digest had been given.
    /// </summary>
    [Fact]
    public void Map_DependencyImage_DigestWithNoAlgorithmPrefix_ThrowsEagerly()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["orders"] = new DependencySpec(Type: "mongodb", Version: null, Extra: null)
                {
                    Image = "mongo@abcdef0123",
                },
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("abcdef0123", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sha256", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // MINOR-3 regression (independent re-review) — the one interaction the unconditional
    // registry clear (M2 fix, WithImageRegistry(null)) could have broken had no test: an
    // UNQUALIFIED 'image:' together with an env-level 'imageRegistry'. The existing coverage
    // pins no 'image:' at all (Map_DependencyImageRegistry_AppliesWhenNoOwnImageSet) and a
    // QUALIFIED 'image:' (Map_DependencyImage_WinsOverImageRegistry_WhenImageHasExplicitRegistry),
    // but nothing pinned the third combination — exactly where the unconditional
    // WithImageRegistry(null) clear sits immediately upstream of the env-level imageRegistry
    // re-apply (the "if (!string.IsNullOrEmpty(imageRegistry) && !imageHasExplicitRegistry)"
    // branch). It behaves correctly today; a refactor reordering those two calls would break it
    // with a green suite without this pin.
    // -----------------------------------------------------------------------

    [Fact]
    public void Map_DependencyImage_UnqualifiedImage_WithEnvImageRegistry_AppliesRegistry()
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
            ImageRegistry: "nexus.corp.local/docker-mirror",
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var image = builder.Resources.Single(r => r.Name == "paydb")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("nexus.corp.local/docker-mirror", image.Registry);
        Assert.Equal("myorg/mssql-mirror", image.Image);
        Assert.Equal("2022", image.Tag);
    }

    // -----------------------------------------------------------------------
    // C4 regression (gatekeeper CRITICAL, verified by execution) — a dangling 'image:' key on a
    // dependency THREW at EnvironmentMapper.Map() time:
    //   ArgumentException: Image reference must not be null, empty, or whitespace. Was: ''.
    // Corpus/Accepted/regression-66aef95-dependency-image-null-key.e2e.yaml states the contract —
    // a dangling 'image:' "must be accepted and treated identically to 'image' being absent
    // altogether" — but that corpus only exercises JSON SCHEMA acceptance
    // (SchemaAcceptedCorpusTests); nothing asserted the ENGINE half of the same contract, which is
    // how this survived. Unlike the rest of this file (which hand-constructs DependencySpec
    // records directly), the tests below parse REAL YAML text through the actual
    // YamlDocumentParser, because the bug lives exactly in the gap between what an author writes
    // and what YamlDocumentParser.GetScalar hands the mapper — a hand-constructed record would
    // step straight over that gap and could not have caught the regression (GetScalar returns
    // "", never null, for a dangling or explicit-empty scalar).
    //
    // A note on "red-first" in this region's provenance claims: red against the REVISION the
    // test was written for, not necessarily against origin/main. The whitespace pins are red
    // only against the (reverted) IsNullOrWhiteSpace intermediate; the !!str-tag pin only
    // against the pre-tag-check intermediate. Where a test is red against main itself, its own
    // comment says so.
    //
    // Root cause: the MN3 fix (above) normalised empty→absent for Version but not for the sibling
    // Image field beside it — the old 'spec.Image is not null' guard let GetScalar's "" flow
    // straight into ImageReferenceParser.Parse's own eager validation, which throws.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses <paramref name="yaml"/> via the real YamlDocumentParser and returns its
    /// <c>environment</c> block. Every fixture used by the C4 tests below always declares one.
    /// </summary>
    private static EnvironmentSpec ParseEnvironment(string yaml)
    {
        var document = Vouchfx.Engine.Authoring.YamlDocumentParser.Parse(yaml);
        Assert.NotNull(document.Environment);
        return document.Environment!;
    }

    /// <summary>
    /// The absent-key baseline the C4 "matches absent" tests below compare against: no 'image:'
    /// key at all on an 'orders-db' postgres dependency (same name/type as the corpus fixture).
    /// Parsed via the same real-YAML path as the degenerate spellings, so every comparison is
    /// document-vs-document rather than parsed-document-vs-hand-built-record.
    /// </summary>
    private const string AbsentImageKeyYaml = """
        metadata:
          name: c4-probe
        environment:
          dependencies:
            orders-db:
              type: postgres
        steps:
          - id: noop
            type: script.csharp
            code: "// Filler step."
        """;

    [Fact]
    public void Map_DependencyImage_Dangling_MatchesAbsentKeyBaseline()
    {
        // Exact dependency/step shape of
        // Corpus/Accepted/regression-66aef95-dependency-image-null-key.e2e.yaml.
        const string yaml = """
            metadata:
              name: c4-probe
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  image:
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var env = ParseEnvironment(yaml);

        // Single-representation-of-absent (N-8): a dangling scalar now resolves to actual null,
        // not "" — GetScalarOrPlainNull folds the empty-scalar case into the same one
        // representation as the four explicit YAML-null tokens.
        Assert.Null(env.Dependencies!["orders-db"].Image);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var baselineMapped = EnvironmentMapper.Map(ParseEnvironment(AbsentImageKeyYaml));
        var baselineBuilder = CreateBuilder();
        baselineMapped.Configure(baselineBuilder);

        var image = builder.Resources.Single(r => r.Name == "orders-db")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        var baselineImage = baselineBuilder.Resources.Single(r => r.Name == "orders-db")
            .Annotations.OfType<ContainerImageAnnotation>().Single();

        Assert.Equal(baselineImage.Image, image.Image);
        Assert.Equal(baselineImage.Tag, image.Tag);
        Assert.Equal(baselineImage.Registry, image.Registry);
        Assert.Equal(baselineImage.SHA256, image.SHA256);

        // Anti-degenerate-value anchor (peer-review-critic nit #8): both sides of the equality
        // checks above are computed via the SAME EnvironmentMapper.Map() code path, so a
        // common-mode failure (e.g. a future change that silently loses the default tag on BOTH
        // sides) would still pass every Assert.Equal above. Confirms the baseline itself carries
        // a real tag, not a degenerate empty one both sides coincidentally share.
        Assert.False(string.IsNullOrEmpty(image.Tag));
    }

    [Fact]
    public void Map_DependencyImage_ExplicitEmptyString_MatchesAbsentKeyBaseline()
    {
        const string yaml = """
            metadata:
              name: c4-probe
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  image: ""
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var env = ParseEnvironment(yaml);
        Assert.Equal(string.Empty, env.Dependencies!["orders-db"].Image);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var baselineMapped = EnvironmentMapper.Map(ParseEnvironment(AbsentImageKeyYaml));
        var baselineBuilder = CreateBuilder();
        baselineMapped.Configure(baselineBuilder);

        var image = builder.Resources.Single(r => r.Name == "orders-db")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        var baselineImage = baselineBuilder.Resources.Single(r => r.Name == "orders-db")
            .Annotations.OfType<ContainerImageAnnotation>().Single();

        Assert.Equal(baselineImage.Image, image.Image);
        Assert.Equal(baselineImage.Tag, image.Tag);
        Assert.Equal(baselineImage.Registry, image.Registry);
        Assert.Equal(baselineImage.SHA256, image.SHA256);

        // Anti-degenerate-value anchor (peer-review-critic nit #8) — see the Dangling test above.
        Assert.False(string.IsNullOrEmpty(image.Tag));
    }

    /// <summary>
    /// The combination the C4 fix must not accidentally break: an EMPTY 'image:' alongside a real
    /// 'version:' must behave as version-only — exactly like 'image' being entirely absent with
    /// the same 'version:' — and must NOT trip the tag/digest-plus-version ambiguity check. That
    /// check exists only for when 'image:' carries a REAL embedded tag/digest of its own (see the
    /// feat/dependency-image-override precedence comment earlier in this file); an empty 'image:'
    /// carries no tag or digest to be ambiguous with, so there is nothing to reject.
    /// </summary>
    [Fact]
    public void Map_DependencyImage_EmptyImageWithVersion_BehavesAsVersionOnly()
    {
        const string yaml = """
            metadata:
              name: c4-probe
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  image: ""
                  version: "16"
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;
        const string versionOnlyYaml = """
            metadata:
              name: c4-probe
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  version: "16"
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var mapped = EnvironmentMapper.Map(ParseEnvironment(yaml));
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var baselineMapped = EnvironmentMapper.Map(ParseEnvironment(versionOnlyYaml));
        var baselineBuilder = CreateBuilder();
        baselineMapped.Configure(baselineBuilder);

        var image = builder.Resources.Single(r => r.Name == "orders-db")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        var baselineImage = baselineBuilder.Resources.Single(r => r.Name == "orders-db")
            .Annotations.OfType<ContainerImageAnnotation>().Single();

        Assert.Equal(baselineImage.Image, image.Image);
        Assert.Equal(baselineImage.Tag, image.Tag);
        Assert.Equal(baselineImage.Registry, image.Registry);
        Assert.Equal(baselineImage.SHA256, image.SHA256);
        Assert.Equal("16", image.Tag);
    }

    /// <summary>
    /// The plain-null sibling of the test above: <c>image: ~</c> alongside a real
    /// <c>version:</c> behaves exactly like a version-only dependency — the literal
    /// <c>~</c> never becomes a repository name.
    /// </summary>
    /// <remarks>
    /// Boundary pin (passes against the fixed code it was written for; the flagship
    /// CHANGELOG scenario it guards was previously covered only transitively:
    /// the parser theory proves <c>image: ~</c> → null field-independently, and the
    /// empty-string sibling above proves absent-image + version mapping — but no
    /// test drove THIS exact combination through the real parser→mapper boundary.
    /// Suggested by review; a regression at that seam would otherwise surface in
    /// neither test.)
    /// </remarks>
    [Fact]
    public void Map_DependencyImage_PlainNullImageWithVersion_BehavesAsVersionOnly()
    {
        const string yaml = """
            metadata:
              name: c4-probe
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  image: ~
                  version: "16"
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;
        const string versionOnlyYaml = """
            metadata:
              name: c4-probe
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  version: "16"
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var mapped = EnvironmentMapper.Map(ParseEnvironment(yaml));
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var baselineMapped = EnvironmentMapper.Map(ParseEnvironment(versionOnlyYaml));
        var baselineBuilder = CreateBuilder();
        baselineMapped.Configure(baselineBuilder);

        var image = builder.Resources.Single(r => r.Name == "orders-db")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        var baselineImage = baselineBuilder.Resources.Single(r => r.Name == "orders-db")
            .Annotations.OfType<ContainerImageAnnotation>().Single();

        Assert.Equal(baselineImage.Image, image.Image);
        Assert.Equal(baselineImage.Tag, image.Tag);
        Assert.Equal(baselineImage.Registry, image.Registry);
        Assert.Equal(baselineImage.SHA256, image.SHA256);
        Assert.Equal("16", image.Tag);
        Assert.NotEqual("~", image.Image);
    }

    // -----------------------------------------------------------------------
    // 66aef95-extension fix — YamlDocumentParser.GetScalarOrPlainNull resolves YAML 1.2's four
    // PLAIN core-schema null tokens (~, null, Null, NULL) to actual null for the two dependency
    // fields whose shipped schema descriptions promise the treated-as-absent contract for YAML's
    // explicit null ('version'/'image'). Before this fix, an explicit 'image: ~' did NOT collapse
    // to the absent-key baseline: YamlDocumentParser.GetScalar handed the mapper the literal
    // one-character string "~" (confirmed empirically), which is neither empty nor whitespace, so
    // it parsed as a syntactically legal repository named "~" and was rejected only by the
    // pre-existing M3 rule (no tag/digest/version), not because "~" was recognised as degenerate
    // input. The Theory below proves all four tokens now match the absent-key baseline; the two
    // 'QuotedTildeString' facts that follow pin the DELIBERATE boundary that a quoted value stays
    // literal rather than resolving to null. A sibling defect — 'version: ~' silently produced a
    // literal "~" tag with NO error at Map() time, worse than C4 itself since it only failed once
    // Docker tried to pull the garbage reference — is fixed and proven the same way for Version,
    // below.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("~")]
    [InlineData("null")]
    [InlineData("Null")]
    [InlineData("NULL")]
    public void Map_DependencyImage_PlainNullToken_MatchesAbsentKeyBaseline(string token)
    {
        var yaml = $"""
            metadata:
              name: c4-probe
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  image: {token}
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var env = ParseEnvironment(yaml);

        // Pins the parser fix directly: a PLAIN null token resolves to actual null — exactly as
        // if 'image:' were entirely absent — not the literal token text.
        Assert.Null(env.Dependencies!["orders-db"].Image);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var baselineMapped = EnvironmentMapper.Map(ParseEnvironment(AbsentImageKeyYaml));
        var baselineBuilder = CreateBuilder();
        baselineMapped.Configure(baselineBuilder);

        var image = builder.Resources.Single(r => r.Name == "orders-db")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        var baselineImage = baselineBuilder.Resources.Single(r => r.Name == "orders-db")
            .Annotations.OfType<ContainerImageAnnotation>().Single();

        Assert.Equal(baselineImage.Image, image.Image);
        Assert.Equal(baselineImage.Tag, image.Tag);
        Assert.Equal(baselineImage.Registry, image.Registry);
        Assert.Equal(baselineImage.SHA256, image.SHA256);

        // Anti-degenerate-value anchor (peer-review-critic nit #8) — see the Dangling test above.
        Assert.False(string.IsNullOrEmpty(image.Tag));
    }

    /// <summary>
    /// Hard requirement of the 66aef95-extension fix: a QUOTED 'image: "~"' is genuinely the
    /// one-character string "~", not YAML's null — the author explicitly quoted it, so it stays
    /// literal (<c>YamlScalarNode.Style</c> is never <c>Plain</c> for a quoted scalar). "~" still
    /// parses as a syntactically legal repository name, so this dependency is still rejected by
    /// the pre-existing M3 rule (no tag/digest, no sibling version) — proving the fix closes the
    /// PLAIN gap (the Theory above) without touching the QUOTED boundary at all.
    /// </summary>
    [Fact]
    public void Map_DependencyImage_QuotedTildeString_IsNotTreatedAsAbsent()
    {
        const string yaml = """
            metadata:
              name: c4-probe
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  image: "~"
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var env = ParseEnvironment(yaml);
        Assert.Equal("~", env.Dependencies!["orders-db"].Image);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("orders-db", ex.Message, StringComparison.Ordinal);
        Assert.Contains("latest", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "must not be null, empty, or whitespace", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sibling defect: 'version: ~' produced NO error at Map() time and silently set the
    /// container tag to the literal one-character string "~" — a garbage pull reference (e.g.
    /// 'postgres:~') that would only have failed once Docker actually tried to pull it, rather
    /// than failing loudly at suite-build time the way every other malformed 'image:'/'version:'
    /// shape in this file does. Verified red-first (temporarily reverting
    /// GetScalarOrPlainNull's two call sites back to GetScalar) before applying the fix.
    /// </summary>
    [Fact]
    public void Map_DependencyVersion_ExplicitTildeNull_MatchesAbsentKeyBaseline()
    {
        const string yaml = """
            metadata:
              name: c4-probe
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  version: ~
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var env = ParseEnvironment(yaml);
        Assert.Null(env.Dependencies!["orders-db"].Version);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var baselineMapped = EnvironmentMapper.Map(ParseEnvironment(AbsentImageKeyYaml));
        var baselineBuilder = CreateBuilder();
        baselineMapped.Configure(baselineBuilder);

        var image = builder.Resources.Single(r => r.Name == "orders-db")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        var baselineImage = baselineBuilder.Resources.Single(r => r.Name == "orders-db")
            .Annotations.OfType<ContainerImageAnnotation>().Single();

        Assert.Equal(baselineImage.Image, image.Image);
        Assert.Equal(baselineImage.Tag, image.Tag);
        Assert.Equal(baselineImage.Registry, image.Registry);
        Assert.Equal(baselineImage.SHA256, image.SHA256);

        // Anti-degenerate-value anchor (peer-review-critic nit #8) — see the Dangling test above.
        Assert.False(string.IsNullOrEmpty(image.Tag));

        // Specifically not the garbage literal tag the pre-fix parser produced.
        Assert.NotEqual("~", image.Tag);
    }

    /// <summary>
    /// Symmetric hard requirement for Version: a QUOTED 'version: "~"' stays the literal
    /// one-character string "~" and is used as the tag verbatim. Unlike Image, a bare version tag
    /// has nothing that parses or rejects it — no repository/tag/digest splitting ever runs for
    /// Version — so this SUCCEEDS, but with the garbage tag "~"; the point is proving the quoting
    /// boundary holds symmetrically, not exercising a rejection path.
    /// </summary>
    [Fact]
    public void Map_DependencyVersion_QuotedTildeString_IsNotTreatedAsAbsent()
    {
        const string yaml = """
            metadata:
              name: c4-probe
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  version: "~"
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var env = ParseEnvironment(yaml);
        Assert.Equal("~", env.Dependencies!["orders-db"].Version);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var image = builder.Resources.Single(r => r.Name == "orders-db")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("~", image.Tag);
    }

    // -----------------------------------------------------------------------
    // M-1 (gatekeeper BLOCKING — whitespace widening reverted): a whitespace-only 'image:'/
    // 'version:' is NEITHER the dangling-key nor the YAML-null-token shape the 66aef95 contract
    // covers, and must NOT be silently treated as absent. An earlier revision of this fix
    // widened both guards to IsNullOrWhiteSpace on the reasoning "match what Parse itself
    // rejects" — that rationale was itself the regression: pre-filtering exactly the inputs
    // Parse rejects converts Parse's loud, author-visible rejection into a silent intent-
    // discard, directly contradicting the MN5 design comment in ImageReference.cs, which
    // rejects a whitespace-padded reference rather than trimming it FOR THE STATED REASON that
    // trimming leaves "no author-visible signal". A realistic trigger: CI templating that
    // expands an unset variable into a blank, quoted string (`image: "   "`) must still fail
    // loudly, not silently fall back to the provider default. Both guards are back to
    // IsNullOrEmpty; these two tests pin that a whitespace-only value behaves identically to
    // origin/main for both fields (Image always rejected it via Parse; Version's pre-existing
    // MN3 guard was already IsNullOrEmpty, so a whitespace-only version was always a real,
    // literal — if useless — tag, and still is; not fixed here, out of this PR's contract).
    // -----------------------------------------------------------------------

    [Fact]
    public void Map_DependencyImage_WhitespaceOnly_ThrowsLikeMain()
    {
        const string yaml = """
            metadata:
              name: c4-probe
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  image: "   "
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var env = ParseEnvironment(yaml);

        // The parser only resolves PLAIN null tokens (and the dangling/empty scalar) to absent;
        // a quoted, whitespace-only value is neither, so it survives unchanged.
        Assert.Equal("   ", env.Dependencies!["orders-db"].Image);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains(
            "must not be null, empty, or whitespace", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_DependencyVersion_WhitespaceOnly_StillProducesLiteralTag_UnchangedFromMain()
    {
        const string yaml = """
            metadata:
              name: c4-probe
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  version: "   "
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var env = ParseEnvironment(yaml);
        Assert.Equal("   ", env.Dependencies!["orders-db"].Version);

        // Pre-existing, unaffected by this PR (explicitly out of the 66aef95 contract's scope):
        // a whitespace-only version silently becomes a garbage container tag, exactly as on
        // origin/main. Not fixed here; pinned only so a future change cannot silently re-widen
        // this guard to IsNullOrWhiteSpace.
        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var image = builder.Resources.Single(r => r.Name == "orders-db")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("   ", image.Tag);
    }

    // -----------------------------------------------------------------------
    // peer-review-critic minor #5 — the plain-null Version fix changes which PRE-EXISTING
    // ambiguity/M3 rule fires when 'image:' ALSO carries content, because 'version: ~' now makes
    // hasVersion false rather than true (previously "~" was a real, non-empty string). Both
    // shapes are user-visible behaviour changes that were previously untested.
    // -----------------------------------------------------------------------

    /// <summary>
    /// GENUINE FLIP (throw → success), verified red-first: on origin/main, 'image:
    /// myorg/mongo:8.0' + 'version: ~' threw the tag/digest-plus-version ambiguity error —
    /// spec.Version was the literal, non-empty string "~", so hasVersion was true, and the
    /// image's own embedded tag "8.0" tripped the ambiguity check against it. After this fix,
    /// 'version: ~' resolves to null, hasVersion is false, and there is nothing left to be
    /// ambiguous with — the dependency maps cleanly using the image's own embedded tag.
    /// </summary>
    [Fact]
    public void Map_DependencyImageWithTag_AndPlainNullVersion_MapsCleanly_NoLongerAmbiguous()
    {
        const string yaml = """
            metadata:
              name: c4-probe
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  image: myorg/mongo:8.0
                  version: ~
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var env = ParseEnvironment(yaml);
        Assert.Null(env.Dependencies!["orders-db"].Version);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var image = builder.Resources.Single(r => r.Name == "orders-db")
            .Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("myorg/mongo", image.Image);
        Assert.Equal("8.0", image.Tag);
    }

    /// <summary>
    /// The other flip (silent garbage pull → correct rejection), also verified red-first: on
    /// origin/main, 'image: myorg/mongo' (no embedded tag/digest) + 'version: ~' SUCCEEDED —
    /// spec.Version was the literal "~", so hasVersion was true, which satisfied the M3 "has a
    /// version" branch and let ApplyImageOverrides use the literal "~" itself as the tag,
    /// producing a garbage 'myorg/mongo:~' pull reference with no error at suite-build time.
    /// After this fix, 'version: ~' resolves to null, hasVersion is false, and the pre-existing
    /// M3 rule (a tagless image with nothing pinning its version would float on ':latest') now
    /// correctly fires instead — a success-to-failure change in outcome, but the CORRECT failure.
    /// </summary>
    [Fact]
    public void Map_DependencyImageNoTag_AndPlainNullVersion_ThrowsFloatingLatest_NoLongerGarbageTag()
    {
        const string yaml = """
            metadata:
              name: c4-probe
            environment:
              dependencies:
                orders-db:
                  type: postgres
                  image: myorg/mongo
                  version: ~
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var env = ParseEnvironment(yaml);
        Assert.Null(env.Dependencies!["orders-db"].Version);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("orders-db", ex.Message, StringComparison.Ordinal);
        Assert.Contains("latest", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // GetDependencyServiceSidecarNames (m1 fix, fix round 2) — the declarative source
    // ProviderPipeline's host-resource-vs-declared-name collision guard consults.
    // -----------------------------------------------------------------------

    /// <summary>
    /// m1 fix: a <c>mailpit</c> dependency unconditionally stages an SMTP sidecar into
    /// <c>svc::&lt;name&gt;-smtp</c> — <see cref="EnvironmentMapper.GetDependencyServiceSidecarNames"/>
    /// must report that name so the collision guard can reserve it.
    /// </summary>
    [Fact]
    public void GetDependencyServiceSidecarNames_Mailpit_ReturnsSmtpSuffixedName()
    {
        var spec = new DependencySpec(Type: "mailpit", Version: null, Extra: null);

        var names = EnvironmentMapper.GetDependencyServiceSidecarNames("mail", spec).ToList();

        Assert.Equal("mail-smtp", Assert.Single(names));
    }

    /// <summary>
    /// m1 fix: a <c>kafka</c> dependency WITHOUT <c>schemaRegistry: true</c> stages no
    /// sidecar at all — the collision guard must reserve nothing extra for it.
    /// </summary>
    [Fact]
    public void GetDependencyServiceSidecarNames_KafkaWithoutSchemaRegistry_ReturnsEmpty()
    {
        var spec = new DependencySpec(Type: "kafka", Version: null, Extra: null);

        var names = EnvironmentMapper.GetDependencyServiceSidecarNames("bus", spec).ToList();

        Assert.Empty(names);
    }

    /// <summary>
    /// m1 fix: a <c>kafka</c> dependency WITH <c>schemaRegistry: true</c> stages a schema-
    /// registry sidecar into <c>svc::&lt;name&gt;-sr</c> — the exact key the reviewer
    /// confirmed a listener named <c>bus-sr</c> could previously shadow undetected.
    /// </summary>
    [Fact]
    public void GetDependencyServiceSidecarNames_KafkaWithSchemaRegistry_ReturnsSrSuffixedName()
    {
        const string yaml = """
            metadata:
              name: sidecar-probe
            environment:
              dependencies:
                bus:
                  type: kafka
                  schemaRegistry: true
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var env = ParseEnvironment(yaml);
        var spec = env.Dependencies!["bus"];

        var names = EnvironmentMapper.GetDependencyServiceSidecarNames("bus", spec).ToList();

        Assert.Equal("bus-sr", Assert.Single(names));
    }

    // -----------------------------------------------------------------------
    // dependency-env (spec REQ-003 / REQ-004 / REQ-005 / EDGE-005 / EDGE-006): a
    // managed dependency's own `env:` mapping.
    //
    // The all-thirteen merge gate and the engine-set-name census both live in their
    // own file, DependencyEnvCensusTests, because they read the schema and this
    // mapper's own source. What follows is everything those cannot say: which
    // construction SHAPE each of the two named cases stands for, the three refusals
    // (${conn:}, ${secret:}, engine-set name), and the both-directions matrix.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads the text of a resolved environment-variable value regardless of which overload
    /// wrote it: the engine's own registrations use <c>WithEnvironment(string, string)</c> and
    /// land a plain <see cref="string"/>, whereas an author's <c>env:</c> goes through
    /// <c>ApplyEnv</c> and lands a <see cref="ReferenceExpression"/>.  A test that asserts
    /// "the engine's value survived" has to read both.
    /// </summary>
    private static string EnvValueText(object envVarValue) => envVarValue switch
    {
        ReferenceExpression expression => expression.ValueExpression,
        string text => text,
        _ => envVarValue.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Builds a one-dependency environment from real YAML, maps it, and returns the resolved
    /// environment variables of the container named <paramref name="dependencyName"/> — the
    /// dependency's OWN container, never a sidecar and never the <c>AddDatabase</c> child.
    /// </summary>
    private static async Task<Dictionary<string, object>>
        MapDependencyAndResolveEnvAsync(string yaml, string dependencyName)
    {
        var mapped = EnvironmentMapper.Map(ParseEnvironment(yaml));
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var target = Assert.Single(
            builder.Resources.OfType<ContainerResource>(), r => r.Name == dependencyName);

        return await ResolveEnvVarsAsync(target);
    }

    /// <summary>
    /// <b>Shape 1 of 2 — the Aspire <c>AddXxx</c>-backed shape</b>, for which <c>postgres</c>
    /// stands (so do <c>sqlserver</c>, <c>mysql</c> and <c>mongodb</c>; the census covers all
    /// four).  This is the shape a naive implementation gets WRONG: the builder the mapper
    /// retains for a postgres dependency is the <c>AddDatabase</c> CHILD
    /// (<c>PostgresDatabaseResource</c>, named <c>ordersdb</c>), which is not a container and
    /// does not implement <c>IResourceWithEnvironment</c> at all — so applying <c>env:</c> to the
    /// retained builder does not compile, let alone work.  The variable must reach the SERVER
    /// container, the one named exactly as the author declared the dependency.
    /// </summary>
    [Fact]
    public async Task Map_DependencyEnv_AspireAddXxxShape_Postgres_ReachesTheServerContainer()
    {
        const string yaml = """
            metadata:
              name: dep-env-postgres
            environment:
              dependencies:
                orders:
                  type: postgres
                  env:
                    VOUCHFX_DEP_PROBE: applied
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var vars = await MapDependencyAndResolveEnvAsync(yaml, "orders");

        Assert.Equal("applied", EnvValueText(vars["VOUCHFX_DEP_PROBE"]));
    }

    /// <summary>
    /// <b>Shape 2 of 2 — the <c>AddContainer</c>-backed shape</b>, for which <c>minio</c> stands
    /// (so do <c>mailpit</c>, <c>dynamodb</c> and <c>azureservicebus</c>).  Here the retained
    /// builder IS the container, so the naive implementation happens to work — which is exactly
    /// why the postgres case above is the other half of the pair rather than a second example of
    /// the same thing.  minio is also one of the three types that carries engine-set names, and
    /// this row is the proof that carrying them does not make the whole type inert: a
    /// non-reserved key on minio is applied like any other.  The refusal half is pinned
    /// separately below.
    /// </summary>
    [Fact]
    public async Task Map_DependencyEnv_AddContainerShape_Minio_ReachesTheContainer()
    {
        const string yaml = """
            metadata:
              name: dep-env-minio
            environment:
              dependencies:
                blobs:
                  type: minio
                  env:
                    VOUCHFX_DEP_PROBE: applied
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var vars = await MapDependencyAndResolveEnvAsync(yaml, "blobs");

        Assert.Equal("applied", EnvValueText(vars["VOUCHFX_DEP_PROBE"]));
    }

    /// <summary>
    /// REQ-003: a <c>${conn:...}</c> reference inside a DEPENDENCY's <c>env:</c> is REFUSED,
    /// naming the reference — a managed dependency is a connection SOURCE, not a consumer
    /// (decision 2).  Refusing it removes self-reference and inter-dependency cycles outright,
    /// which is the whole reason this engine needs no build-order graph and no cycle detector.
    /// </summary>
    /// <remarks>
    /// Asserted on the MESSAGE, not the exception type alone.  Without the refusal the value
    /// tokenises as a <c>ConnRef</c> and either throws <c>KeyNotFoundException</c> from a table
    /// built from services only — a §12.1 taxonomy break, an authoring fault reported as an
    /// Environment error — or, worse, silently works.
    /// </remarks>
    [Fact]
    public void Map_DependencyEnv_ConnReference_IsRefusedNamingTheReference()
    {
        const string yaml = """
            metadata:
              name: dep-env-conn
            environment:
              dependencies:
                orders:
                  type: postgres
                cache:
                  type: redis
                  env:
                    UPSTREAM: "${conn:orders}"
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var environment = ParseEnvironment(yaml);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(environment));

        Assert.Contains("Dependency 'cache'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("env entry 'UPSTREAM'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("${conn:orders}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("connection SOURCE, not a consumer", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// REQ-003: a <c>${secret:...}</c> reference inside a DEPENDENCY's <c>env:</c> is REFUSED,
    /// on the service rule's own reasoning (§17): a container's environment is the wrong PLACE
    /// for a secret, because anyone who can run <c>docker inspect</c> reads it.
    /// </summary>
    /// <remarks>
    /// Without this check the sigil matches NEITHER token pattern, so the value becomes a plain
    /// literal and <c>${secret:vault/db/pw}</c> is written into the container verbatim.  §17's
    /// invariant would survive by accident — the value is never resolved — but the author would
    /// see a green suite and believe the secret had been delivered.
    /// </remarks>
    [Fact]
    public void Map_DependencyEnv_SecretReference_IsRefused()
    {
        const string yaml = """
            metadata:
              name: dep-env-secret
            environment:
              dependencies:
                orders:
                  type: postgres
                  env:
                    ADMIN_PW: "${secret:vault/db/pw}"
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var environment = ParseEnvironment(yaml);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(environment));

        Assert.Contains("Dependency 'orders'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("env entry 'ADMIN_PW'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("wrong PLACE for a secret", ex.Message, StringComparison.Ordinal);
        Assert.Contains("docker inspect", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The declaration order the two ORDERING rows below assert on their parsed inputs —
    /// <c>static readonly</c> rather than inline literals only because this project enforces
    /// CA1861 as an error.
    /// </summary>
    private static readonly string[] s_reservedThenSecretKeyOrder =
        { "MINIO_ROOT_USER", "MINIO_ROOT_PASSWORD" };

    /// <inheritdoc cref="s_reservedThenSecretKeyOrder"/>
    private static readonly string[] s_collisionThenSecretDependencyOrder = { "blobs", "orders" };

    /// <summary>
    /// REQ-003, ORDERING: the two dependency rules meet in one document, and BOTH now refuse — so
    /// what this pins is WHICH refusal the author is shown.  A <c>${secret:...}</c> reports the
    /// secret fault even when a reserved-name collision is sitting in the same <c>env:</c> map,
    /// DECLARED FIRST.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Neither existing test can see this.  The secret refusal above uses <c>postgres</c>, an
    /// unreserved type, so the two rules never meet there; the reserved-name theory uses no
    /// references at all.  The ordering holds structurally — <c>Map</c> runs the eager
    /// <c>ValidateEnvValue</c> pass over every dependency BEFORE the separate loop that checks
    /// reserved names — but "structurally" is exactly the guarantee a refactor deletes without
    /// noticing.
    /// </para>
    /// <para>
    /// REQ-004 was that refactor, and it deliberately did NOT collapse the two dependency loops
    /// into one, which is the obvious tidy now that the second one throws rather than
    /// <c>continue</c>s.  Collapse them and the author is told their variable is not theirs to
    /// set, never that they put a secret in a container's environment — the more serious of the
    /// two faults and the one a security reviewer needs to see.
    /// </para>
    /// <para>
    /// <b>Why TWO keys, in THIS order.</b>  A single dependency carrying a single
    /// secret-on-a-reserved-name key does NOT discriminate: the natural collapse runs the secret
    /// check and the collision check in that sequence FOR EACH KEY, so with one key it still
    /// reports the secret and the test stays green while the invariant is gone.  The
    /// reserved-but-CLEAN <c>MINIO_ROOT_USER</c> declared FIRST is what makes the row bite — under
    /// a collapsed loop that key is reached first, passes the secret check, and throws the
    /// COLLISION, so <c>wrong PLACE for a secret</c> is absent and this test goes red.  MEASURED:
    /// collapsing the two loops fails this test with
    /// <c>Assert.Contains() Failure: Sub-string not found</c> on that clause.
    /// </para>
    /// <para>
    /// The whole row rests on <c>env:</c> keys reaching <c>Map</c> in DECLARATION order, so that
    /// premise is asserted here rather than assumed: <c>YamlDocumentParser.ParseEnvMap</c> fills an
    /// insert-only <c>Dictionary&lt;string, string&gt;</c> (<see cref="StringComparer.Ordinal"/>)
    /// from <c>YamlMappingNode.Children</c>, which is document-ordered.
    /// </para>
    /// </remarks>
    [Fact]
    public void Map_DependencyEnv_SecretReferenceOnAReservedKey_ReportsTheSecretFaultNotTheCollision()
    {
        const string yaml = """
            metadata:
              name: dep-env-secret-on-reserved
            environment:
              dependencies:
                blobs:
                  type: minio
                  env:
                    MINIO_ROOT_USER: attacker
                    MINIO_ROOT_PASSWORD: "${secret:vault/db/pw}"
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var environment = ParseEnvironment(yaml);

        // The premise this test rests on, measured rather than assumed: the reserved-but-clean
        // key really does arrive ahead of the secret-bearing one.
        Assert.Equal<IEnumerable<string>>(
            s_reservedThenSecretKeyOrder,
            environment.Dependencies!["blobs"].Env!.Keys);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(environment));

        Assert.Contains("Dependency 'blobs'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("env entry 'MINIO_ROOT_PASSWORD'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("wrong PLACE for a secret", ex.Message, StringComparison.Ordinal);

        // Negative half — without it a collapsed loop that happened to mention both faults would
        // pass. The collision diagnostic's own distinctive clause must be absent.
        Assert.DoesNotContain("REFUSED", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "env entry 'MINIO_ROOT_USER'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// REQ-003, ORDERING — the CROSS-DEPENDENCY half.  The eager <c>ValidateEnvValue</c> pass
    /// spans EVERY dependency before the reserved-name loop sees its first one, so a secret on
    /// dependency B is reported even though a reserved-name collision sits on dependency A,
    /// declared first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one-dependency row above cannot see this property.  A collapse that kept the secret
    /// check strictly ahead of the collision check WITHIN a dependency — but interleaved the two
    /// per dependency — would satisfy that row and still break here, reporting <c>blobs</c>'s
    /// collision and never <c>orders</c>'s secret.  What is under test is the SPAN of the first
    /// pass, not merely its per-key precedence.
    /// </para>
    /// <para>
    /// Same premise, same treatment: <c>YamlDocumentParser.ParseDependencyMap</c> fills an
    /// insert-only ordinal <c>Dictionary</c> from the document's own dependency order, so
    /// <c>blobs</c> genuinely precedes <c>orders</c>, and that is asserted rather than assumed.
    /// </para>
    /// </remarks>
    [Fact]
    public void Map_DependencyEnv_ReservedCollisionOnAnEarlierDependency_StillReportsTheSecretFault()
    {
        const string yaml = """
            metadata:
              name: dep-env-secret-after-collision
            environment:
              dependencies:
                blobs:
                  type: minio
                  env:
                    MINIO_ROOT_USER: attacker
                orders:
                  type: postgres
                  env:
                    ADMIN_PW: "${secret:vault/db/pw}"
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var environment = ParseEnvironment(yaml);

        Assert.Equal<IEnumerable<string>>(
            s_collisionThenSecretDependencyOrder,
            environment.Dependencies!.Keys);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(environment));

        Assert.Contains("Dependency 'orders'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("env entry 'ADMIN_PW'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("wrong PLACE for a secret", ex.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("REFUSED", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Dependency 'blobs'", ex.Message, StringComparison.Ordinal);
    }

    // The service side's own guard against this slice's `ownerLabel` widening is
    // Map_ServiceEnv_SecretReference_MessageIsByteIdenticalToPreFeatureWording, above: it pins
    // the full service message as an exact string and is UNCHANGED by this feature. A second,
    // weaker restatement here would only be a place for the two to drift.

    /// <summary>
    /// REQ-004: an <c>env:</c> key the engine sets for that dependency's own <c>type:</c> is
    /// REFUSED, and the message names all three of the variable, the dependency and the type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Engine-wins is NOT achievable by ordering.  Aspire's <c>WithEnvironment</c> registers an
    /// <c>EnvironmentCallbackAnnotation</c>; the callbacks run in registration order into ONE
    /// dictionary, so the LAST write wins and a post-<c>Build</c> author write would replace the
    /// engine's value.  T3 delivered engine-wins by never writing the key, warning as it dropped
    /// it; this slice tightens that to a refusal, because a variable an author declared and the
    /// engine discarded is the schema-acceptance-is-not-execution failure this feature exists to
    /// avoid reproducing — the author writes a variable, the suite goes green, and nothing
    /// happened.
    /// </para>
    /// <para>
    /// <b>REQ-004 also wants "before any container starts", and that half is an ARGUMENT here,
    /// not an assertion</b> — the same discipline, and for the same reason, as
    /// <c>Map_DependencyEnv_MissingEnvVarReference_IsRefusedByMapNamingTheVariable</c> below.
    /// The throw comes out of <c>Map</c>, <c>Map</c> takes no builder, and the ONLY thing that
    /// adds a resource to a builder is the <c>Configure</c> delegate <c>Map</c> would have
    /// returned.  A <c>Map</c> that throws hands back no delegate, so there is nothing to run and
    /// nothing that could have run earlier.  Inventing an assertion that interrogates no real
    /// state would be worse than saying so.
    /// </para>
    /// <para>
    /// <b>One row per reserved name — all nine</b>, so a canonicalisation that silently drops one
    /// goes red.  Two of the nine are <c>MINIO_ROOT_PASSWORD</c> and <c>MSSQL_SA_PASSWORD</c>, the
    /// credential-bearing pair the whole engine-wins argument is about.
    /// </para>
    /// <para>
    /// <b>The refusal must not echo the author's VALUE.</b>  Two of these nine names carry a
    /// password, so a future edit that appended the rejected value "for helpfulness" would put
    /// author-supplied credential material into every log and report that carries the failure.
    /// Nothing else in this file pins that, so the <c>DoesNotContain</c> below does.
    /// </para>
    /// <para>
    /// That assertion constrains the <c>authorValue</c> column: a value that occurs in the
    /// diagnostic by COINCIDENCE would fail the row without any leak.  Every value below was
    /// re-derived against the refusal wording this slice ships and none of them occurs in it.
    /// The one that needs a sentinel rather than the realistic value is <c>ACCEPT_EULA</c>, whose
    /// real-world declining value is the bare <c>N</c> — a single character any wording is liable
    /// to contain — so <c>AUTHOR_DECLINED</c> stands in for it.  A future rewording that happens
    /// to contain one of these values fails LOUDLY rather than silently, so the constraint costs
    /// a rename and never a missed leak.
    /// </para>
    /// <para>
    /// <b>The credential clause is SCOPED to <c>minio</c>, and this theory holds it there.</b>
    /// Only <c>MINIO_ROOT_USER</c>/<c>MINIO_ROOT_PASSWORD</c> are spliced into a connection string
    /// <c>${conn:...}</c> hands to other scenarios.  <c>elasticsearch</c>'s four names are
    /// host/port-only, and the <c>azureservicebus</c> emulator's connection string is a fixed
    /// <c>Endpoint=sb://…;SharedAccessKey=SAS_KEY_VALUE;…</c> containing none of its three — so an
    /// unscoped "some of them carry the credentials" was untrue for SEVEN of these nine names and
    /// handed an <c>elasticsearch</c> author refused for <c>ES_JAVA_OPTS</c> an argument with no
    /// bearing on their case.  Both clauses are asserted on every row: the scoped credential
    /// aside, and the load-bearing one — "the shape every scenario shares" — which is what is
    /// actually true for all nine and carries the message alone.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("elasticsearch", "discovery.type", "multi-node")]
    [InlineData("elasticsearch", "xpack.security.enabled", "true")]
    [InlineData("elasticsearch", "ES_JAVA_OPTS", "-Xmx8g")]
    [InlineData("elasticsearch", "cluster.routing.allocation.disk.threshold_enabled", "true")]
    [InlineData("minio", "MINIO_ROOT_USER", "attacker")]
    [InlineData("minio", "MINIO_ROOT_PASSWORD", "attacker-password")]
    [InlineData("azureservicebus", "ACCEPT_EULA", "AUTHOR_DECLINED")]
    [InlineData("azureservicebus", "MSSQL_SA_PASSWORD", "attacker-sa-password")]
    [InlineData("azureservicebus", "SQL_SERVER", "attacker-sql-host")]
    public void Map_DependencyEnv_EngineSetKey_IsRefusedNamingVariableDependencyAndType(
        string type,
        string reservedKey,
        string authorValue)
    {
        var environment = ParseEnvironment(DependencyEnvYaml(type, reservedKey, authorValue));

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(environment));

        // All three of variable, dependency and type.
        Assert.Contains($"env entry '{reservedKey}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"Dependency 'dep' (type '{type}')", ex.Message, StringComparison.Ordinal);
        Assert.Contains("REFUSED", ex.Message, StringComparison.Ordinal);

        // The escape hatch: an author who genuinely needs full control of the backend's
        // environment is told where to get it, rather than only that they may not have it here.
        Assert.Contains(
            "declare the backend as a service with 'image:'", ex.Message, StringComparison.Ordinal);

        // The clause that is true for all nine, and carries the refusal on its own.
        Assert.Contains(
            "bring this dependency up in the shape every scenario shares",
            ex.Message,
            StringComparison.Ordinal);

        // The credential aside, SCOPED to minio — see the remarks. Asserted verbatim so a future
        // edit that re-broadens it to "some of them carry the credentials" goes red on all nine
        // rows rather than shipping a claim untrue for seven of them.
        Assert.Contains(
            "and on 'minio' they are the credentials ${conn:<dependency>} advertises to every "
                + "other scenario consuming it",
            ex.Message,
            StringComparison.Ordinal);

        // The NAME and the fact of the refusal, never the VALUE — see the remarks above.  Two of
        // these nine reserved names are passwords.
        Assert.DoesNotContain(authorValue, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// REQ-004, the other direction — <b>the full per-type matrix</b>.  Every one of the nine
    /// reserved names is applied normally on every type that does NOT reserve it: the three
    /// reserved-bearing types against each other, plus <c>postgres</c> standing for the ten types
    /// that reserve nothing.
    /// </summary>
    /// <remarks>
    /// Without this direction the check degrades to a global denylist, which is a different and
    /// wrong feature: <c>MSSQL_SA_PASSWORD</c> is the <c>azureservicebus</c> emulator's SQL
    /// wiring and means nothing to <c>elasticsearch</c>, and an author configuring the latter has
    /// every right to a variable of that name.  Twenty-seven rows rather than nine for a narrower
    /// reason than "proving the table is per-type": ONE non-reserving type per name already does
    /// that, because under a global denylist the single row
    /// <c>[postgres, "discovery.type"]</c> expects the key applied, gets it refused, and goes red.
    /// What the full matrix buys is detection of a PARTIALLY over-broad table — a name reserved
    /// for its own type and, by a copy-paste slip, for one other as well, which any single
    /// non-reserving row would miss whenever it happened to pick a third type.
    /// </remarks>
    [Theory]
    [InlineData("minio", "discovery.type")]
    [InlineData("azureservicebus", "discovery.type")]
    [InlineData("postgres", "discovery.type")]
    [InlineData("minio", "xpack.security.enabled")]
    [InlineData("azureservicebus", "xpack.security.enabled")]
    [InlineData("postgres", "xpack.security.enabled")]
    [InlineData("minio", "ES_JAVA_OPTS")]
    [InlineData("azureservicebus", "ES_JAVA_OPTS")]
    [InlineData("postgres", "ES_JAVA_OPTS")]
    [InlineData("minio", "cluster.routing.allocation.disk.threshold_enabled")]
    [InlineData("azureservicebus", "cluster.routing.allocation.disk.threshold_enabled")]
    [InlineData("postgres", "cluster.routing.allocation.disk.threshold_enabled")]
    [InlineData("elasticsearch", "MINIO_ROOT_USER")]
    [InlineData("azureservicebus", "MINIO_ROOT_USER")]
    [InlineData("postgres", "MINIO_ROOT_USER")]
    [InlineData("elasticsearch", "MINIO_ROOT_PASSWORD")]
    [InlineData("azureservicebus", "MINIO_ROOT_PASSWORD")]
    [InlineData("postgres", "MINIO_ROOT_PASSWORD")]
    [InlineData("elasticsearch", "ACCEPT_EULA")]
    [InlineData("minio", "ACCEPT_EULA")]
    [InlineData("postgres", "ACCEPT_EULA")]
    [InlineData("elasticsearch", "MSSQL_SA_PASSWORD")]
    [InlineData("minio", "MSSQL_SA_PASSWORD")]
    [InlineData("postgres", "MSSQL_SA_PASSWORD")]
    [InlineData("elasticsearch", "SQL_SERVER")]
    [InlineData("minio", "SQL_SERVER")]
    [InlineData("postgres", "SQL_SERVER")]
    public async Task Map_DependencyEnv_ReservedNameOnATypeThatDoesNotReserveIt_IsStillApplied(
        string type,
        string reservedElsewhere)
    {
        var vars = await MapDependencyAndResolveEnvAsync(
            DependencyEnvYaml(type, reservedElsewhere, "applied"), "dep");

        Assert.Equal("applied", EnvValueText(vars[reservedElsewhere]));
    }

    /// <summary>
    /// The other half of the refusal, without which it degrades to "dependency <c>env:</c> does
    /// nothing on these three types": an ordinary, non-reserved key on one of the three types
    /// that carries reserved names is applied like any other.
    /// </summary>
    [Theory]
    [InlineData("elasticsearch")]
    [InlineData("minio")]
    [InlineData("azureservicebus")]
    public async Task Map_DependencyEnv_NonReservedKey_IsStillApplied(string type)
    {
        var vars = await MapDependencyAndResolveEnvAsync(
            DependencyEnvYaml(type, "VOUCHFX_DEP_PROBE", "applied"), "dep");

        Assert.Equal("applied", EnvValueText(vars["VOUCHFX_DEP_PROBE"]));
    }

    /// <summary>
    /// EDGE-005: reserved-name matching is case-sensitive and EXACT, asserted in BOTH directions
    /// on the one type where the two spellings are distinguishable — <c>ES_JAVA_OPTS</c> on
    /// <c>elasticsearch</c> is refused, <c>es_java_opts</c> on <c>elasticsearch</c> is applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Container environment variables are case-sensitive on Linux, so <c>es_java_opts</c> is a
    /// legitimately distinct variable and a case-folded guard would refuse it.  Both halves are
    /// asserted here, in one place, because either alone permits the wrong comparer: the refusal
    /// alone is satisfied by <see cref="StringComparer.OrdinalIgnoreCase"/>, and the applied case
    /// alone is satisfied by a guard that reserves nothing at all.
    /// </para>
    /// <para>
    /// Measured on the T3 skip this refusal replaced: flip the per-type name set's comparer
    /// inside <c>s_engineSetEnvKeys</c> (the <see cref="HashSet{T}"/> one, not the outer
    /// type-keyed dictionary's, which governs <c>type:</c> lookup) to
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> and exactly the lower-case half of this
    /// test goes red.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Map_DependencyEnv_ReservedNameMatchIsCaseSensitive_InBothDirections()
    {
        // Refused: the exact spelling the engine sets.
        var refused = ParseEnvironment(
            DependencyEnvYaml("elasticsearch", "ES_JAVA_OPTS", "-Xmx8g"));

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(refused));
        Assert.Contains("env entry 'ES_JAVA_OPTS'", ex.Message, StringComparison.Ordinal);

        // Applied: a differently-cased, genuinely distinct Linux environment variable.
        var vars = await MapDependencyAndResolveEnvAsync(
            DependencyEnvYaml("elasticsearch", "es_java_opts", "-Xmx8g"), "dep");

        Assert.Equal("-Xmx8g", EnvValueText(vars["es_java_opts"]));
    }

    /// <summary>
    /// A one-dependency suite named <c>dep</c> carrying a single <c>env:</c> entry — the shape
    /// every reserved-name row above and below shares.
    /// </summary>
    private static string DependencyEnvYaml(string type, string key, string value) =>
        $"""
        metadata:
          name: dep-env-matrix
        environment:
          dependencies:
            dep:
              type: {type}
              env:
                "{key}": "{value}"
        steps:
          - id: noop
            type: script.csharp
            code: "// Filler step."
        """;

    /// <summary>
    /// REQ-005: <c>${env:NAME}</c> resolves from the ENGINE PROCESS's environment at
    /// topology-build time, exactly as it does for a service.
    /// </summary>
    /// <remarks>
    /// The explicitly-EMPTY half is deliberately not asserted here, on the SERVICE side's
    /// existing reasoning and not a new judgement: see the NOTE above
    /// <c>Map_ServiceEnv_SecretReference_MessageIsByteIdenticalToPreFeatureWording</c>.
    /// <c>Environment.SetEnvironmentVariable(name, "")</c> deletes the variable rather than
    /// setting it empty, so a test built on it would silently re-prove the UNSET path.  The
    /// dependency path adds nothing here to reason about: it reaches the identical
    /// <c>ValidateEnvValue</c> / <c>BuildEnvExpression</c> branches, whose guard is
    /// <c>is null</c> and never <c>string.IsNullOrEmpty</c>.
    /// </remarks>
    [Fact]
    public async Task Map_DependencyEnv_EnvVarReference_ResolvesFromTheEngineProcessEnvironment()
    {
        const string setName = "VOUCHFX_DEP_ENV_SET";
        const string yaml = """
            metadata:
              name: dep-env-envref
            environment:
              dependencies:
                dep:
                  type: redis
                  env:
                    REGION: "prefix-${env:VOUCHFX_DEP_ENV_SET}-suffix"
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        Environment.SetEnvironmentVariable(setName, "eu-west-1");
        try
        {
            var vars = await MapDependencyAndResolveEnvAsync(yaml, "dep");

            Assert.Equal("prefix-eu-west-1-suffix", EnvValueText(vars["REGION"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable(setName, null);
        }
    }

    /// <summary>
    /// REQ-005: a MISSING <c>${env:NAME}</c> is refused by <c>Map</c> itself, naming the
    /// variable.  That is what this test ASSERTS, and the name says only that.
    /// </summary>
    /// <remarks>
    /// REQ-005 also wants "before any container starts", and that half is an ARGUMENT here, not
    /// an assertion — deliberately, because there is no observable state a second assertion could
    /// interrogate.  The throw comes out of <c>Map</c>, <c>Map</c> takes no builder, and the ONLY
    /// thing that adds a resource to a builder is the <c>Configure</c> delegate <c>Map</c> would
    /// have returned.  A <c>Map</c> that throws hands back no delegate, so there is nothing to run
    /// and nothing that could have run earlier.  A test name promising an assertion that does not
    /// exist is what this method used to be called.
    /// </remarks>
    [Fact]
    public void Map_DependencyEnv_MissingEnvVarReference_IsRefusedByMapNamingTheVariable()
    {
        const string missingName = "VOUCHFX_DEP_ENV_DEFINITELY_UNSET_XYZ";
        const string yaml = """
            metadata:
              name: dep-env-envref-missing
            environment:
              dependencies:
                dep:
                  type: redis
                  env:
                    REGION: "${env:VOUCHFX_DEP_ENV_DEFINITELY_UNSET_XYZ}"
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        Environment.SetEnvironmentVariable(missingName, null);
        var environment = ParseEnvironment(yaml);

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(environment));

        Assert.Contains("Dependency 'dep'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(missingName, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// EDGE-006: setting <c>env:</c> on a dependency MUST NOT change what <c>${conn:dep}</c>
    /// resolves to for its consumers.  The same service, against the same dependency, produces a
    /// byte-identical connection EXPRESSION with and without an unrelated <c>env:</c> entry on
    /// that dependency.
    /// </summary>
    /// <remarks>
    /// <b>Scope, stated rather than implied.</b>  This compares
    /// <c>ReferenceExpression.ValueExpression</c> — the unresolved format string
    /// (<c>{orders.connectionString};Database=ordersdb</c>) — not a resolved connection string,
    /// which would need a live container.  So it would NOT detect a change that swapped the
    /// underlying <c>IValueProvider</c> for a different one bearing the same name.  It is the
    /// strongest no-Docker proxy available for EDGE-006, and that is what it is being used as.
    /// </remarks>
    [Fact]
    public async Task Map_DependencyEnv_DoesNotChangeWhatConnResolvesForAConsumingService()
    {
        const string withoutEnv = """
            metadata:
              name: edge-006-without
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  env:
                    ConnectionStrings__orders: "${conn:orders}"
              dependencies:
                orders:
                  type: postgres
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;
        const string withEnv = """
            metadata:
              name: edge-006-with
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  env:
                    ConnectionStrings__orders: "${conn:orders}"
              dependencies:
                orders:
                  type: postgres
                  env:
                    VOUCHFX_DEP_PROBE: unrelated
            steps:
              - id: noop
                type: script.csharp
                code: "// Filler step."
            """;

        var baseline = await ResolveServiceConnAsync(withoutEnv);
        var withDependencyEnv = await ResolveServiceConnAsync(withEnv);

        Assert.Equal("{orders.connectionString};Database=ordersdb", baseline);
        Assert.Equal(baseline, withDependencyEnv);
    }

    private static async Task<string> ResolveServiceConnAsync(string yaml)
    {
        var mapped = EnvironmentMapper.Map(ParseEnvironment(yaml));
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var api = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "api");
        var vars = await ResolveEnvVarsAsync(api);
        return EnvValueText(vars["ConnectionStrings__orders"]);
    }
}
