// REQ-023 — a service declaring `security` is staged with an `https` scheme
// (authenticated-infrastructure-mtls, slice D).
//
// Non-Docker, exactly like EnvironmentMapperTests: building the resource graph is pure
// in-memory work; only DistributedApplication.StartAsync needs DCP. Every assertion here reads
// the EndpointAnnotation the mapper actually produced, never a string the mapper was asked to
// produce — the scheme is what makes the staged svc::<name> value begin "https://", and it is
// the one fact the whole requirement reduces to.
//
// What these tests deliberately do NOT claim. The staged value is only readable after
// StartAsync (EndpointReference.Url throws InvalidOperationException before allocation, and it
// is the DCP-allocated host port that appears in it), so the URL itself is proven at the
// annotation level: EndpointAnnotation.UriScheme is what Aspire renders as the scheme of
// "<scheme>://<address>:<port>", and there is no other input to that component. The
// end-to-end reachability of an https:// URL is REQ-024's execution test.
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// REQ-023 tests: the endpoint a service declaring <c>security</c> exposes, and what a service
/// declaring none still exposes.
/// </summary>
public sealed class SecuredServiceEndpointTests
{
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    // Shared `ports:` literals for the collision tests below (CA1861: a constant array argument
    // in a repeatedly-called method belongs in a static readonly field).
    private static readonly int[] s_securedPortOnly = { 8443 };
    private static readonly int[] s_duplicateSecuredPort = { 8443, 8443 };

    private static IDistributedApplicationBuilder CreateBuilder() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true,
            Args = Array.Empty<string>(),
            AssemblyName = AppHostAssemblyName,
        });

    private static SecuritySpec Mtls(string endpoint) =>
        new(
            Profile: "mtls",
            Endpoint: endpoint,
            CaCert: "./certs/ca.pem",
            ClientCert: "./certs/client.pem",
            ClientKey: "./certs/client-key.pem",
            ServerArtifacts: null);

    private static EnvironmentSpec EnvWith(string name, ServiceSpec spec) =>
        new(
            Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal) { [name] = spec },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    private static ServiceSpec ImageService(
        int? httpPort = null,
        IReadOnlyList<int>? ports = null,
        SecuritySpec? security = null,
        HealthCheckSpec? healthCheck = null) =>
        new(
            Image: "acme/payments:1.2",
            Project: null,
            ImagePullPolicy: null,
            HttpPort: httpPort,
            Env: null)
        {
            Ports = ports,
            Security = security,
            HealthCheck = healthCheck,
        };

    private static List<EndpointAnnotation> EndpointsOf(
        IDistributedApplicationBuilder builder, string serviceName) =>
        builder.Resources.OfType<ContainerResource>()
            .Single(r => r.Name == serviceName)
            .Annotations.OfType<EndpointAnnotation>()
            .ToList();

    // ── The requirement itself ────────────────────────────────────────────────────────────

    /// <summary>
    /// REQ-023's core acceptance: a service declaring <c>security</c> with a numeric
    /// <c>endpoint</c> exposes an endpoint whose <see cref="EndpointAnnotation.UriScheme"/> is
    /// <c>"https"</c>, on the declared port.
    /// </summary>
    [Fact]
    public void Map_ServiceDeclaringSecurity_ExposesHttpsSchemedEndpointOnTheDeclaredPort()
    {
        var env = EnvWith("payments", ImageService(security: Mtls("8443")));

        var builder = CreateBuilder();
        EnvironmentMapper.Map(env).Configure(builder);

        var endpoint = Assert.Single(EndpointsOf(builder, "payments"));
        Assert.Equal("https", endpoint.UriScheme);
        Assert.Equal(ServiceEndpointNaming.HttpsEndpointName, endpoint.Name);
        Assert.Equal(8443, endpoint.TargetPort);
    }

    /// <summary>
    /// The secured endpoint is the one <c>svc::&lt;name&gt;</c> resolves to, which is what makes
    /// the staged value begin <c>https://</c>: <c>EnvironmentMapper</c> retains exactly one
    /// <c>EndpointReference</c> per service, and Aspire renders its URL as
    /// <c>&lt;scheme&gt;://&lt;address&gt;:&lt;port&gt;</c> from the annotation's own scheme.
    /// Proven here by name identity between the retained reference and the https-schemed
    /// annotation, because the URL itself is unreadable before <c>StartAsync</c> allocates a
    /// host port.
    /// </summary>
    [Fact]
    public void Map_ServiceDeclaringSecurity_StagesTheSecuredEndpointAsThePrimaryReference()
    {
        // A service with BOTH a plain httpPort and a separate secured port: the case where
        // picking the wrong endpoint would produce a suite that passes every assertion over
        // plaintext (EDGE-004), so the choice must be pinned rather than assumed.
        var env = EnvWith("payments", ImageService(httpPort: 8080, security: Mtls("8443")));

        var builder = CreateBuilder();
        var mapped = EnvironmentMapper.Map(env);
        mapped.Configure(builder);

        var endpoints = EndpointsOf(builder, "payments");
        Assert.Contains(endpoints, e => e.Name == "http" && e.UriScheme == "http" && e.TargetPort == 8080);
        Assert.Contains(endpoints, e => e.Name == "https" && e.UriScheme == "https" && e.TargetPort == 8443);
        Assert.Equal(2, endpoints.Count);

        // The retained reference — the one ResolveServices reads .Url from for svc::payments.
        var resource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "payments");
        var reference = new EndpointReference(resource, ServiceEndpointNaming.HttpsEndpointName);
        Assert.Equal("https", reference.Scheme);
    }

    /// <summary>
    /// A secured port that is ALSO one of the service's declared <c>ports:</c> is declared ONCE,
    /// as the secured endpoint — never twice, under two names with two schemes. An author who
    /// names a port as their secured endpoint has said that port is secured; leaving a
    /// second, plaintext-schemed endpoint on the same container port would contradict them.
    /// </summary>
    [Fact]
    public void Map_SecuredPortAlsoDeclaredUnderPorts_ReplacesThePlaintextEndpointRatherThanAddingOne()
    {
        var env = EnvWith(
            "broker",
            ImageService(ports: new List<int> { 9092, 9093 }, security: Mtls("9093")));

        var builder = CreateBuilder();
        EnvironmentMapper.Map(env).Configure(builder);

        var endpoints = EndpointsOf(builder, "broker");
        Assert.Equal(2, endpoints.Count);
        Assert.Contains(endpoints, e => e.Name == "tcp-9092" && e.UriScheme == "tcp" && e.TargetPort == 9092);
        Assert.Contains(endpoints, e => e.Name == "https" && e.UriScheme == "https" && e.TargetPort == 9093);
        Assert.DoesNotContain(endpoints, e => e.Name == "tcp-9093");
    }

    /// <summary>
    /// The <c>endpoint</c> selector's second legal shape (REQ-002): the NAME of an endpoint the
    /// service's own declaration produces. It resolves to that endpoint's container port and
    /// secures it.
    /// </summary>
    [Fact]
    public void Map_SecurityEndpointNamingADeclaredEndpoint_SecuresThatEndpointsPort()
    {
        var env = EnvWith(
            "broker",
            ImageService(ports: new List<int> { 9092, 9093 }, security: Mtls("tcp-9093")));

        var builder = CreateBuilder();
        EnvironmentMapper.Map(env).Configure(builder);

        var endpoints = EndpointsOf(builder, "broker");
        Assert.Contains(endpoints, e => e.Name == "https" && e.UriScheme == "https" && e.TargetPort == 9093);
        Assert.Contains(endpoints, e => e.Name == "tcp-9092" && e.UriScheme == "tcp");
        Assert.Equal(2, endpoints.Count);
    }

    /// <summary>
    /// The secured endpoint appears in <c>IProjectContext.DeclaredServices</c> too, because the
    /// mapper and that surface project the SAME computation
    /// (<see cref="ServiceEndpointNaming.EndpointDeclarations"/>) — the drift this class exists
    /// to prevent.
    /// </summary>
    [Fact]
    public void DeclaredEndpointNames_ForASecuredService_MatchTheEndpointsTheMapperDeclares()
    {
        var spec = ImageService(httpPort: 8080, security: Mtls("8443"));
        var env = EnvWith("payments", spec);

        var builder = CreateBuilder();
        EnvironmentMapper.Map(env).Configure(builder);

        var mapperNames = EndpointsOf(builder, "payments").Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal);
        var declaredNames = ServiceEndpointNaming.DeclaredEndpointNames(spec).OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(declaredNames, mapperNames);
    }

    // ── The unchanged half of the requirement ─────────────────────────────────────────────

    /// <summary>
    /// REQ-023's second half, stated as its own test rather than left implied: a service
    /// declaring NO <c>security</c> block is unchanged in every declared shape — the implicit
    /// default, the ports-only shape, and the hybrid shape — and in particular gains no
    /// <c>https</c>-schemed endpoint from anywhere.
    /// </summary>
    [Theory]
    [InlineData(null, null, "http", "http", 80)]
    [InlineData(8080, null, "http", "http", 8080)]
    [InlineData(null, 9093, "tcp-9093", "tcp", 9093)]
    public void Map_ServiceDeclaringNoSecurity_IsUnchanged(
        int? httpPort, int? singlePort, string expectedName, string expectedScheme, int expectedPort)
    {
        var env = EnvWith(
            "plain",
            ImageService(httpPort: httpPort, ports: singlePort is { } p ? new List<int> { p } : null));

        var builder = CreateBuilder();
        EnvironmentMapper.Map(env).Configure(builder);

        var endpoint = Assert.Single(EndpointsOf(builder, "plain"));
        Assert.Equal(expectedName, endpoint.Name);
        Assert.Equal(expectedScheme, endpoint.UriScheme);
        Assert.Equal(expectedPort, endpoint.TargetPort);
        Assert.NotEqual("https", endpoint.UriScheme);
    }

    // ── Health gating ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A secured service with no explicit <c>healthCheck</c> is gated by a TCP probe on its
    /// secured endpoint, never an HTTP one. Measured on the mutual-TLS test bed: an
    /// unauthenticated request to an <c>ssl_verify_client on</c> listener completes the TLS
    /// handshake in full and is answered <c>400</c>, so an HTTP probe — which cannot present a
    /// client certificate — would hold a perfectly healthy topology unhealthy forever.
    /// </summary>
    [Fact]
    public void Map_SecuredServiceWithNoHealthCheck_GatesOnATcpProbeOfTheSecuredEndpoint()
    {
        var env = EnvWith("payments", ImageService(security: Mtls("8443")));

        var builder = CreateBuilder();
        EnvironmentMapper.Map(env).Configure(builder);

        var resource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "payments");
        var healthChecks = resource.Annotations.OfType<HealthCheckAnnotation>().ToList();

        var healthCheck = Assert.Single(healthChecks);
        Assert.Equal("payments-tcp-8443-health", healthCheck.Key);
    }

    /// <summary>
    /// An explicit <c>tcp</c> health check may name the secured port — it is one of the
    /// service's declared ports, declared by <c>security.endpoint</c> rather than by
    /// <c>ports:</c>, and a raw TCP connect does not care about the endpoint's URI scheme.
    /// </summary>
    [Fact]
    public void Map_SecuredServiceWithExplicitTcpHealthCheckOnTheSecuredPort_IsAccepted()
    {
        var env = EnvWith(
            "payments",
            ImageService(
                security: Mtls("8443"),
                healthCheck: new HealthCheckSpec(Type: "tcp", Path: null, Port: 8443)));

        var builder = CreateBuilder();
        EnvironmentMapper.Map(env).Configure(builder);

        var resource = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "payments");
        var healthCheck = Assert.Single(resource.Annotations.OfType<HealthCheckAnnotation>());
        Assert.Equal("payments-tcp-8443-health", healthCheck.Key);
    }

    /// <summary>
    /// A secured service keeping a SEPARATE plaintext health port — the shape real mTLS
    /// services use, and the one the test bed's own nginx uses — still gets its explicit HTTP
    /// health check, against the plaintext endpoint.
    /// </summary>
    [Fact]
    public void Map_SecuredServiceWithASeparatePlaintextHttpPort_KeepsItsExplicitHttpHealthCheck()
    {
        var env = EnvWith(
            "payments",
            ImageService(
                httpPort: 8080,
                security: Mtls("8443"),
                healthCheck: new HealthCheckSpec(Type: "http", Path: "/healthz", Port: null)));

        var builder = CreateBuilder();
        EnvironmentMapper.Map(env).Configure(builder);

        var endpoints = EndpointsOf(builder, "payments");
        Assert.Contains(endpoints, e => e.Name == "http" && e.UriScheme == "http" && e.TargetPort == 8080);
        Assert.Contains(endpoints, e => e.Name == "https" && e.UriScheme == "https" && e.TargetPort == 8443);
    }

    // ── The authoring path, end to end ────────────────────────────────────────────────────

    /// <summary>
    /// The whole path from AUTHORED YAML to the Aspire endpoint, through the real parser rather
    /// than a hand-built <see cref="ServiceSpec"/>. Every other test in this file starts from a
    /// record, which proves the mapper and nothing about whether an author's
    /// <c>security:</c> block ever reaches it — precisely the divergence this feature's earlier
    /// slice was caught by: a document the schema accepts is not evidence that anything
    /// downstream can execute it.
    /// </summary>
    [Fact]
    public void Map_FromAuthoredYaml_ProducesTheHttpsSchemedEndpoint()
    {
        const string Yaml = """
            metadata:
              name: secured-service-probe
            environment:
              services:
                payments:
                  image: acme/payments:1.2
                  security:
                    profile: mtls
                    endpoint: 8443
                    caCert: ./certs/ca.pem
                    clientCert: ./certs/client.pem
                    clientKey: ./certs/client-key.pem
            steps:
              - id: call
                type: http.rest
                target: payments
                method: GET
                path: /
            """;

        var document = Vouchfx.Engine.Authoring.YamlDocumentParser.Parse(Yaml);
        Assert.NotNull(document.Environment);

        // The parser really bound the block — asserted before the mapper runs, so a silent
        // drop would fail here rather than as a confusing missing-endpoint further down.
        var parsedService = document.Environment!.Services!["payments"];
        Assert.NotNull(parsedService.Security);
        Assert.Equal("mtls", parsedService.Security!.Profile);
        Assert.Equal("8443", parsedService.Security.Endpoint);

        var builder = CreateBuilder();
        EnvironmentMapper.Map(document.Environment).Configure(builder);

        var endpoint = Assert.Single(EndpointsOf(builder, "payments"));
        Assert.Equal("https", endpoint.UriScheme);
        Assert.Equal(8443, endpoint.TargetPort);
    }

    // ── Fail-closed rejections ────────────────────────────────────────────────────────────

    /// <summary>
    /// An <c>endpoint</c> selector that resolves to nothing is rejected eagerly, naming what the
    /// service DOES declare. Silence here would leave the service reachable over plaintext
    /// while its suite believed it was secured.
    /// </summary>
    [Theory]
    [InlineData("tcp-9999")]
    [InlineData("not-an-endpoint")]
    [InlineData("70000")]
    [InlineData("08443")]
    public void Map_UnresolvableSecurityEndpoint_ThrowsNamingTheDeclaredEndpoints(string selector)
    {
        var env = EnvWith(
            "broker",
            ImageService(ports: new List<int> { 9092 }, security: Mtls(selector)));

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("security.endpoint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("tcp-9092 (port 9092)", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>security</c> on a <c>project</c>-form service fails loudly at topology-build time. The
    /// engine neither models nor names a project's launch-profile endpoints, so there is no
    /// endpoint for REQ-023 to construct with an https scheme — and no <c>svc::&lt;name&gt;</c>
    /// value is staged for a project-form service at all. Silence would produce a suite that
    /// validates, starts, and then presents no client certificate to anything.
    /// </summary>
    [Fact]
    public void Map_SecurityOnAProjectFormService_ThrowsRatherThanSilentlySecuringNothing()
    {
        var env = EnvWith(
            "payments",
            new ServiceSpec(
                Image: null,
                Project: "./src/Payments/Payments.csproj",
                ImagePullPolicy: null,
                HttpPort: null,
                Env: null)
            {
                Security = Mtls("8443"),
            });

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("'project'-form service", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An explicit <c>http</c> health check on a service whose HTTP port IS its secured port is
    /// rejected: the <c>http</c> endpoint that check would probe no longer exists, and a health
    /// check could not present a client certificate to the secured one in any case.
    /// </summary>
    [Fact]
    public void Map_HttpHealthCheckOnAServiceWhoseHttpPortIsSecured_Throws()
    {
        var env = EnvWith(
            "payments",
            ImageService(
                httpPort: 8443,
                security: Mtls("8443"),
                healthCheck: new HealthCheckSpec(Type: "http", Path: "/", Port: null)));

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));
        Assert.Contains("'httpPort' is also its 'security.endpoint'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A secured service declaring NO <c>httpPort</c> and no <c>ports</c> is rejected for an
    /// <c>http</c> health check too, but for a DIFFERENT reason, and the diagnostic must say
    /// the true one.
    /// </summary>
    /// <remarks>
    /// This shape has no HTTP port at all — declaring <c>security</c> suppresses the implicit
    /// plaintext endpoint — so the message that its "HTTP port is also its security.endpoint"
    /// was simply false here, and an author reading it would go looking for a conflict that
    /// does not exist.
    /// </remarks>
    [Fact]
    public void Map_HttpHealthCheckOnASecuredServiceWithNoHttpPort_ThrowsNamingTheRealCause()
    {
        var env = EnvWith(
            "payments",
            ImageService(
                security: Mtls("8443"),
                healthCheck: new HealthCheckSpec(Type: "http", Path: "/", Port: null)));

        var ex = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(env));

        Assert.Contains("no separate 'httpPort'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("is also its 'security.endpoint'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A service declaring <c>ports: [P]</c> AND <c>httpPort: P</c> AND
    /// <c>security.endpoint: P</c> ends up with exactly ONE endpoint on P — the secured one.
    /// </summary>
    /// <remarks>
    /// The schema permits this document: <c>$defs/service</c>'s three <c>allOf</c> clauses cover
    /// <c>image</c>/<c>project</c> exclusivity, the ports-without-httpPort health-check rule and
    /// the project-form restrictions, and none cross-checks <c>httpPort</c> against the
    /// <c>ports</c> items (<c>uniqueItems</c> rejects <c>[P, P]</c> and says nothing about
    /// <c>httpPort</c>). Measured before the fix, it produced
    /// <c>https@8443 SECURED | http@8443 PLAINTEXT</c>, with three consequences: the mapper
    /// emitted BOTH <c>WithHttpsEndpoint</c> and <c>WithHttpEndpoint</c> on one port; the
    /// REQ-023 http-health-check guard did not fire because an "http" endpoint still existed,
    /// so the default probe was aimed at the mTLS listener — which answers 400 unauthenticated,
    /// holding a working topology unhealthy forever; and <c>DeclaredServices</c> advertised a
    /// plaintext endpoint on a port the author declared secured. The dedup half matters as much
    /// as the replacement half: emitting on every match instead would have produced two
    /// endpoints both named "https" on one port.
    /// </remarks>
    [Fact]
    public void Map_HttpPortEqualToTheSecuredPort_LeavesNoPlaintextEndpointOnThatPort()
    {
        var spec = ImageService(httpPort: 8443, ports: s_securedPortOnly, security: Mtls("8443"));

        var declarations = ServiceEndpointNaming.EndpointDeclarations(spec);
        var declaration = Assert.Single(declarations);
        Assert.Equal(ServiceEndpointNaming.HttpsEndpointName, declaration.Name);
        Assert.Equal(8443, declaration.Port);
        Assert.True(declaration.IsSecured);

        // The names surfaced through IProjectContext.DeclaredServices agree, with no duplicate.
        Assert.Equal(
            new[] { ServiceEndpointNaming.HttpsEndpointName },
            ServiceEndpointNaming.DeclaredEndpointNames(spec));

        // And the topology the mapper actually builds carries one endpoint on that port.
        var builder = CreateBuilder();
        EnvironmentMapper.Map(EnvWith("payments", spec)).Configure(builder);

        var endpoints = EndpointsOf(builder, "payments");
        var endpoint = Assert.Single(endpoints);
        Assert.Equal(ServiceEndpointNaming.HttpsEndpointName, endpoint.Name);
        Assert.Equal("https", endpoint.UriScheme);
        Assert.Equal(8443, endpoint.TargetPort);
    }

    /// <summary>
    /// The same collapse for a duplicate <c>ports</c> entry — reachable only through a
    /// hand-built <see cref="EnvironmentSpec"/>, since the schema's <c>uniqueItems</c> rejects
    /// it, but the engine must not depend on the schema for a fail-closed property.
    /// </summary>
    [Fact]
    public void EndpointDeclarations_DuplicatePortsEntryOnTheSecuredPort_CollapsesToOneSecuredEndpoint()
    {
        var spec = ImageService(ports: s_duplicateSecuredPort, security: Mtls("8443"));

        var declaration = Assert.Single(ServiceEndpointNaming.EndpointDeclarations(spec));
        Assert.Equal(ServiceEndpointNaming.HttpsEndpointName, declaration.Name);
        Assert.True(declaration.IsSecured);
    }

    /// <summary>
    /// The control for the two tests above: a sibling plaintext <c>httpPort</c> on a DIFFERENT
    /// port survives untouched. Only the endpoint ON the secured port is replaced.
    /// </summary>
    [Fact]
    public void EndpointDeclarations_HttpPortOnADifferentPort_KeepsThePlaintextEndpoint()
    {
        var spec = ImageService(httpPort: 8080, ports: s_securedPortOnly, security: Mtls("8443"));

        var declarations = ServiceEndpointNaming.EndpointDeclarations(spec);
        Assert.Equal(2, declarations.Count);

        var secured = Assert.Single(declarations, d => d.IsSecured);
        Assert.Equal(ServiceEndpointNaming.HttpsEndpointName, secured.Name);
        Assert.Equal(8443, secured.Port);

        var plaintext = Assert.Single(declarations, d => !d.IsSecured);
        Assert.Equal(ServiceEndpointNaming.HttpEndpointName, plaintext.Name);
        Assert.Equal(8080, plaintext.Port);
    }
}
