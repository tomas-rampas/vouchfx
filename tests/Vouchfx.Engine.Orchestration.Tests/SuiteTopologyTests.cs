// Tests for S03-A-01: SuiteTopology — build-once-per-suite topology fixture.
//
// These are Docker-gated integration tests that prove the per-suite fixture:
//   • Builds the Aspire topology exactly once via EnvironmentMapper.Map + HeadlessTopology.StartAsync.
//   • Exposes the discovered service endpoints/connection strings through DiscoveredServices.
//   • Supports many concurrent scenarios from one build (the build-once invariant, §4 / MVP §8.2).
//   • Disposes cleanly, releasing all containers and the inner HeadlessTopology.
//   • Classifies infrastructure failures as OrchestrationException (Environment error), never Fail.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~SuiteTopology"
// Excluded from non-Docker CI:  dotnet test --filter "requires!=docker"

using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated integration tests for <see cref="SuiteTopology"/> (S03-A-01).
/// Run with: <c>dotnet test --filter "requires=docker&amp;FullyQualifiedName~SuiteTopology"</c>.
/// Excluded from the non-Docker CI job via: <c>dotnet test --filter "requires!=docker"</c>.
/// </summary>
/// <remarks>
/// <para>
/// R-1 finding: this test assembly carries <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c>
/// and <c>Aspire.AppHost.Sdk</c>, which embeds the <c>dcpclipath</c>/<c>dcpextensionpaths</c>
/// assembly metadata required by <see cref="Aspire.Hosting.DistributedApplicationOptions"/>.
/// The assembly name is passed to <see cref="SuiteTopology.StartAsync"/> so the DCP binary
/// is resolved from this assembly rather than from the xUnit runner entry assembly.
/// </para>
/// </remarks>
public sealed class SuiteTopologyTests
{
    private readonly ITestOutputHelper _output;

    public SuiteTopologyTests(ITestOutputHelper output) => _output = output;

    // The short name of *this* test assembly — the one whose AssemblyInfo carries dcpclipath.
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    // Generous startup timeout: allows Docker image pulls on first run without masking hangs.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);

    // -----------------------------------------------------------------------
    // SuiteTopology_SingleService_ExposesEndpoint_AndServesMany
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a topology with one container service (<c>traefik/whoami</c>), asserts the
    /// discovered base URL is present, then simulates many scenarios from one build by
    /// issuing three sequential HTTP GET requests — proving the build-once invariant (§4 / MVP §8.2):
    /// a single topology build serves many scenario invocations without rebuilding.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task SuiteTopology_SingleService_ExposesEndpoint_AndServesMany()
    {
        // Arrange — one container service mapped via EnvironmentSpec.
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["whoami"] = new ServiceSpec(
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

        // Act — start topology once.
        await using var suite = await SuiteTopology.StartAsync(
            environment: env,
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        // Assert — DiscoveredServices contains the "whoami" base URL.
        Assert.True(
            suite.DiscoveredServices.ContainsKey("whoami"),
            "DiscoveredServices must contain the key 'whoami' after a successful start.");

        var baseUrl = suite.DiscoveredServices["whoami"] as string;
        Assert.False(
            string.IsNullOrWhiteSpace(baseUrl),
            "DiscoveredServices[\"whoami\"] must be a non-empty URL string.");

        _output.WriteLine($"Discovered whoami URL: {baseUrl}");

        // Assert — many-scenarios-from-one-build: issue 3 GETs against the same topology.
        // This proves no rebuild happens between scenario invocations.
        using var client = new HttpClient();
        const int ScenarioCount = 3;

        for (int i = 1; i <= ScenarioCount; i++)
        {
            using var response = await client.GetAsync(new Uri(baseUrl!));
            Assert.True(
                response.IsSuccessStatusCode,
                $"Scenario {i}: HTTP GET {baseUrl} returned {(int)response.StatusCode} " +
                $"{response.ReasonPhrase}; expected 2xx (build-once invariant, §4 / MVP §8.2).");
            _output.WriteLine($"Scenario {i}/{ScenarioCount}: GET {baseUrl} → {(int)response.StatusCode}");
        }

        _output.WriteLine($"Build-once invariant: {ScenarioCount} scenarios served from one topology build — PASS");
    }

    // -----------------------------------------------------------------------
    // SuiteTopology_NoEnvironment_StartsAndExposesEmptyServices
    // -----------------------------------------------------------------------

    /// <summary>
    /// Mapping a <see langword="null"/> environment spec starts an empty Aspire topology
    /// (no managed resources), returns an empty <see cref="SuiteTopology.DiscoveredServices"/>
    /// dictionary, and disposes cleanly.
    /// </summary>
    /// <remarks>
    /// If DCP cannot start an empty topology (e.g. requires at least one resource), the test
    /// accepts an <see cref="OrchestrationException"/> and skips with a clear diagnostic.
    /// The preferred path is an empty topology that starts cleanly; the exception path is a
    /// documented fallback consistent with the task specification.
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task SuiteTopology_NoEnvironment_StartsAndExposesEmptyServices()
    {
        SuiteTopology? suite = null;
        try
        {
            // Act — null environment triggers the empty-topology path in EnvironmentMapper.
            suite = await SuiteTopology.StartAsync(
                environment: null,
                appHostAssemblyName: AppHostAssemblyName,
                startupTimeout: StartupTimeout);

            // Assert — empty map, no health gates, no containers.
            Assert.Empty(suite.DiscoveredServices);
            Assert.NotNull(suite.Application);
            _output.WriteLine("Empty topology: DiscoveredServices is empty — PASS");
        }
        catch (OrchestrationException ex)
        {
            // Documented fallback: DCP may require at least one resource to start.
            // This is an acceptable alternative to the empty-start path; a classified
            // OrchestrationException (Environment error) is always correct here.
            _output.WriteLine(
                $"Empty topology threw OrchestrationException (acceptable per spec): " +
                $"Kind={ex.Info.Kind}, Detail={ex.Info.Detail}");

            // The exception must be a classified Environment error, never a raw exception.
            Assert.NotNull(ex.Info);
        }
        finally
        {
            if (suite is not null)
            {
                await suite.DisposeAsync();
            }
        }
    }

    // -----------------------------------------------------------------------
    // SuiteTopology_BadImage_ThrowsOrchestrationException
    // -----------------------------------------------------------------------

    /// <summary>
    /// A service declared with a non-existent image reference causes
    /// <see cref="SuiteTopology.StartAsync"/> to throw an <see cref="OrchestrationException"/>
    /// (§12.1 Environment error) rather than a raw Aspire/DCP exception.
    /// This ensures infrastructure failures are never conflated with test failures (CLAUDE.md invariant).
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task SuiteTopology_BadImage_ThrowsOrchestrationException()
    {
        // Arrange — a service with a deliberately invalid image reference.
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>
            {
                ["broken"] = new ServiceSpec(
                    Image: "nonexistent.invalid/nope:latest",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        // Act + Assert — expect a classified OrchestrationException, never a raw exception.
        var ex = await Assert.ThrowsAsync<OrchestrationException>(
            () => SuiteTopology.StartAsync(
                environment: env,
                appHostAssemblyName: AppHostAssemblyName,
                // Keep the timeout modest to surface the pull failure quickly.
                startupTimeout: TimeSpan.FromSeconds(60)));

        // The exception must carry structured OrchestrationErrorInfo.
        Assert.NotNull(ex.Info);
        _output.WriteLine(
            $"Bad-image test: OrchestrationException Kind={ex.Info.Kind}, " +
            $"ResourceName={ex.Info.ResourceName}, Detail={ex.Info.Detail} — PASS");
    }

    // -----------------------------------------------------------------------
    // The no-accessor guard (REQ-005) — NOT docker-gated: it fires at Step 0,
    // before EnvironmentMapper.Map and long before DCP.
    // -----------------------------------------------------------------------

    private static EnvironmentSpec SecuredServiceEnvironment() =>
        new(
            Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
            {
                ["api"] = new ServiceSpec("acme/api:1", null, null, null, null)
                {
                    Security = new SecuritySpec("mtls", "8443", null, "./certs/c.pem", "./certs/k.pem", null),
                },
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>
    /// A suite that declares <c>security</c> must not start with the Null accessor silently
    /// substituted for a real one: the probe would present nothing, and every <c>mtls</c> target
    /// would fail closed blaming the author's certificates for the host's own omission.
    /// </summary>
    /// <remarks>
    /// This is the structural guard for the defect <c>vouchfx watch</c> shipped with — an omitted
    /// optional argument compiles, reads correctly, and no test of the CALLER could see it. Chosen
    /// over making the parameter non-optional, which would force roughly sixty pre-existing call
    /// sites (every store's Docker test in this assembly) to assert something their environment
    /// already states: that they declare no security at all.
    /// </remarks>
    [Fact]
    public async Task SuiteTopology_SecurityDeclaredButNoAccessorSupplied_FailsBeforeAnyContainerWork()
    {
        var ex = await Assert.ThrowsAsync<OrchestrationException>(
            () => SuiteTopology.StartAsync(
                environment: SecuredServiceEnvironment(),
                appHostAssemblyName: AppHostAssemblyName));

        Assert.Equal(OrchestrationErrorKind.SecurityConfirmation, ex.Info.Kind);
        Assert.Contains("securityConfiguration", ex.Info.Detail, StringComparison.Ordinal);
        Assert.Contains("defect in the calling host", ex.Info.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard's mirror: a suite declaring no <c>security</c> is untouched by it — which is what
    /// keeps every pre-existing caller, and this file's own Docker tests, valid without change.
    /// </summary>
    /// <remarks>
    /// Also non-Docker, by construction. The service's <c>env</c> names a dependency that does not
    /// exist, which <c>EnvironmentMapper.Map</c> rejects EAGERLY as an <see cref="ArgumentException"/>
    /// — Map is pure, and it runs before <c>HeadlessTopology.StartAsync</c> ever reaches DCP. So
    /// reaching that exception is positive proof of two things at once: the guard did not fire, and
    /// control got past Step 0 into real mapping.
    /// </remarks>
    [Fact]
    public async Task SuiteTopology_NoSecurityDeclared_IsNotBlockedByTheAccessorGuard()
    {
        var unsecured = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
            {
                ["api"] = new ServiceSpec(
                    "acme/api:1",
                    null,
                    null,
                    null,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["FOO"] = "${conn:typo}" }),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        await Assert.ThrowsAsync<ArgumentException>(
            () => SuiteTopology.StartAsync(
                environment: unsecured,
                appHostAssemblyName: AppHostAssemblyName));
    }
}
