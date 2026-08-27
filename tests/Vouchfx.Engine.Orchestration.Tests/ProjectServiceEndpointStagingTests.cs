// Tests for issue #348: a `project:`-form service is staged into `svc::<name>` like an
// image-form one; a project that declares no endpoint AND is addressed by a step is refused
// loudly; and the same project with no step addressing it — a worker service — is left alone.
//
// That last case is the one that has to keep working, not merely the one that happens to. See
// Configure_UntargetedProjectServiceWithNoEndpoint_MapsCleanlyAndStagesNothing.
//
// Test strategy — non-Docker:
// -----------------------------------------------------------------------
// Everything here runs inside the Configure callback, which is a pure in-memory graph
// construction; only DistributedApplication.StartAsync needs DCP/Docker. Measured against the
// pinned Aspire 13.4.2: AddProject(name, csprojPath) attaches its EndpointAnnotations
// SYNCHRONOUSLY from the project's launch profile, so the staging decision under test is fully
// determined before StartAsync and observable here.
//
// The project fixtures are SYNTHESISED into a temp directory rather than committed. Aspire's
// AddProject only requires the .csproj file to EXIST and reads `Properties/launchSettings.json`
// beside it — it never builds the project at this phase — so a two-file temp fixture exercises
// the real code path exactly. It also keeps the repository free of a csproj that would have to be
// excluded from vouchfx.sln, from `dotnet format`, and from every tooling glob. Compiling
// throwaway artefacts at test time is established practice in this assembly (see
// HeadlessTopologySelfHealTests, which emits synthetic host assemblies with Roslyn).
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Authoring.Model;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Non-Docker unit tests pinning how a <c>project:</c>-form service resolves
/// <c>svc::&lt;name&gt;</c> (issue #348).
/// </summary>
public sealed class ProjectServiceEndpointStagingTests : IDisposable
{
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    private readonly ProjectFixtures _fixtures = new();

    private string CreateProjectFixture(string? applicationUrl) => _fixtures.Create(applicationUrl);

    private static IDistributedApplicationBuilder CreateBuilder() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true,
            Args = Array.Empty<string>(),
            AssemblyName = AppHostAssemblyName,
        });

    private static EnvironmentSpec EnvWithProject(string name, string csprojPath) =>
        new(
            Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
            {
                [name] = new ServiceSpec(
                    Image: null,
                    Project: csprojPath,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>
    /// The <c>endpointConsumingTargets</c> set a suite whose steps address <paramref name="names"/>
    /// would produce — what <c>SuiteProtocolTargets.EndpointConsuming</c> derives from the AST at
    /// the real call sites. Spelled directly here because these tests exercise the MAPPER's
    /// reaction to the set, not the derivation of it; the derivation has its own tests in
    /// <c>SuiteProtocolTargetsTests</c>.
    /// </summary>
    private static HashSet<string> Targeting(params string[] names) =>
        new(names, StringComparer.Ordinal);

    /// <summary>
    /// The defect itself (#348): a project-form service whose project declares an HTTP endpoint
    /// stages a <c>svc::&lt;name&gt;</c> value. Before the fix the project branch performed no
    /// staging at all, so the key was absent, every HTTP-family provider fell back to the empty
    /// string, and <c>new Uri("")</c> threw <c>UriFormatException</c> at step-execution time.
    /// </summary>
    /// <remarks>
    /// Asserted against the retained <see cref="EndpointReference"/> rather than its URL: the URL
    /// is unreadable until <c>StartAsync</c> allocates a host port. What makes the staged value
    /// NON-EMPTY is that a reference to a real, existing endpoint was retained at all — the
    /// endpoint's own <c>Exists</c> is checked here for exactly that reason, because
    /// <c>GetEndpoint</c> returns a reference whose <c>Exists</c> is <see langword="false"/> for
    /// an endpoint that was never declared (measured), and such a reference could never render a
    /// URL.
    /// </remarks>
    [Fact]
    public void Configure_ProjectServiceDeclaringAnHttpEndpoint_StagesANonEmptyServiceEndpoint()
    {
        var csproj = CreateProjectFixture("http://localhost:5111");
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj), endpointConsumingTargets: Targeting("api"));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        var staged = Assert.Contains("api", mapped.StagedServiceEndpoints);
        Assert.Equal("http", staged.EndpointName);
        Assert.True(staged.Exists, "the staged endpoint must exist on the resource");
        Assert.Equal("http", staged.Scheme);
    }

    /// <summary>
    /// A project declaring ONLY an https URL stages that endpoint: it is the project's one real
    /// listener, and refusing it would fail a suite that has a perfectly reachable service.
    /// Certificate trust is then the author's to arrange, exactly as it is for an image-form
    /// service that terminates TLS itself.
    /// </summary>
    [Fact]
    public void Configure_ProjectServiceDeclaringOnlyHttps_StagesTheHttpsEndpoint()
    {
        var csproj = CreateProjectFixture("https://localhost:7222");
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj), endpointConsumingTargets: Targeting("api"));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        var staged = Assert.Contains("api", mapped.StagedServiceEndpoints);
        Assert.Equal("https", staged.EndpointName);
        Assert.Equal("https", staged.Scheme);
    }

    /// <summary>
    /// THE SELECTION RULE, pinned in the shape where it is actually a choice: a launch profile
    /// whose <c>applicationUrl</c> lists both schemes — the stock <c>dotnet new webapi</c>
    /// template's "https" profile, and any hand-written profile of the same shape. "http" wins.
    /// </summary>
    /// <remarks>
    /// A project-form service cannot declare <c>security</c> (refused eagerly by the mapper), so
    /// the engine holds no client trust material for one and configures no trust on the step's
    /// HttpClient, while the project's https listener is served with whatever certificate it
    /// arranges for itself. Preferring "http" reaches the same application over the listener that
    /// needs no trust at all. Both orderings are exercised because the annotation order follows
    /// the <c>applicationUrl</c> order (measured), so a rule that accidentally depended on
    /// declaration order would pass one case and fail the other.
    /// </remarks>
    [Theory]
    [InlineData("https://localhost:7333;http://localhost:5333")]
    [InlineData("http://localhost:5333;https://localhost:7333")]
    // The SECOND-SAME-SCHEME shape, which is the entire reason the predicate matches UriScheme
    // rather than endpoint Name: measured, Aspire names these "http" and "http2", so a name-based
    // rule would classify "http2" as neither http nor https and fall through to the
    // first-declared tie-break. Here that happens to reach the same endpoint, but only by
    // accident of ordering — pinning it keeps the comment honest.
    [InlineData("http://localhost:5333;http://localhost:5334")]
    public void Configure_ProjectServiceDeclaringBothSchemes_StagesHttpWhicheverOrderTheyAreDeclaredIn(
        string applicationUrl)
    {
        var csproj = CreateProjectFixture(applicationUrl);
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj), endpointConsumingTargets: Targeting("api"));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        // TWO endpoints are declared on the resource in every case — the rule chooses between
        // them, it does not suppress one. Asserted by count rather than by name, because the
        // second endpoint's NAME differs across the cases ("https" for the mixed-scheme pair,
        // "http2" for the same-scheme one) while the property under test does not.
        var resource = builder.Resources.OfType<ProjectResource>().Single(r => r.Name == "api");
        var declared = resource.Annotations.OfType<EndpointAnnotation>().ToList();
        Assert.Equal(2, declared.Count);
        Assert.Contains(declared, e => e.Name == "http");

        // The staged endpoint is always the http-SCHEMED one, whatever it is named.
        var staged = Assert.Contains("api", mapped.StagedServiceEndpoints);
        Assert.Equal("http", staged.Scheme);
        Assert.Equal("http", staged.EndpointName);
    }

    /// <summary>
    /// THE DOWNGRADE IS ANNOUNCED. A project declaring BOTH schemes stages the plaintext endpoint
    /// (above) and says so — once, naming the service and both endpoints, and giving the reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Security review: staging http when the project also offers https is a transport downgrade
    /// the author never asked for, and before this nothing in the run disclosed it — the step
    /// observation carries only status and expectation, and no event record has a field for it.
    /// An undisclosed downgrade is the part that makes it a finding; the CHOICE itself is
    /// endorsed, because preferring https would fail the dev-certificate handshake and land as an
    /// EnvironmentError, which exits 0 by default (#390) — a green build over a step that
    /// verified nothing.
    /// </para>
    /// <para>
    /// The notice is terminal-only for now: every EXISTING free-text field reaching
    /// --events/--junit/--html is a scenario-level CAUSE for a non-Pass verdict. A new optional
    /// event field is a legitimate route the v1 freeze permits — deferred to #450, not ruled out.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("https://localhost:7333;http://localhost:5333")]
    [InlineData("http://localhost:5333;https://localhost:7333")]
    public void Configure_ProjectServiceDeclaringBothSchemes_AnnouncesTheTransportDowngrade(
        string applicationUrl)
    {
        var csproj = CreateProjectFixture(applicationUrl);
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj), endpointConsumingTargets: Targeting("api"));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        // Asserted on FIELDS, not on wording: the notice is a typed record precisely so a test
        // does not pin an English sentence as the contract.
        var notice = Assert.Single(mapped.EndpointSelectionNotices);
        Assert.Equal("api", notice.ServiceName);
        Assert.Equal("http", notice.SelectedEndpoint);
        Assert.Equal("https", notice.RejectedEndpoint);
    }

    /// <summary>
    /// NO NOISE WHERE THERE WAS NO CHOICE. A project declaring only http had nothing downgraded,
    /// so it produces no notice — and neither does an ordinary image-form service.
    /// </summary>
    /// <remarks>
    /// The pair of the theory above, and the half that keeps the notice worth reading: a warning
    /// every run emits is a warning every author learns to skip.
    /// </remarks>
    [Fact]
    public void Configure_ProjectServiceWithNothingToDowngrade_AnnouncesNothing()
    {
        var csproj = CreateProjectFixture("http://localhost:5111");
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj), endpointConsumingTargets: Targeting("api"));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        Assert.Empty(mapped.EndpointSelectionNotices);
    }

    /// <summary>
    /// An UNTARGETED service declaring both schemes announces nothing either — the notice says
    /// "steps targeting it will use PLAINTEXT", which is false when no step targets it.
    /// </summary>
    /// <remarks>
    /// It is also the case the "this does not fire on every run" defence rests on: a worker project
    /// built from a stock template would otherwise warn about traffic that never happens.
    /// </remarks>
    [Fact]
    public void Configure_UntargetedProjectServiceDeclaringBothSchemes_AnnouncesNothing()
    {
        var csproj = CreateProjectFixture("https://localhost:7333;http://localhost:5333");
        var mapped = EnvironmentMapper.Map(EnvWithProject("order-worker", csproj));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        Assert.Empty(mapped.EndpointSelectionNotices);

        // It IS still staged — staging never depended on targeting.
        Assert.True(mapped.StagedServiceEndpoints.ContainsKey("order-worker"));
    }

    /// <summary>
    /// A project declaring no endpoint that IS addressed by a step is refused with a diagnostic
    /// that NAMES THE SERVICE — the whole point of #348, whose symptom was a
    /// <c>UriFormatException</c> naming neither the service nor the cause.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The service is in <c>endpointConsumingTargets</c>, and that is what makes this a fault at
    /// all: the identical service with no step addressing it is a worker, and is left alone — see
    /// <see cref="Configure_UntargetedProjectServiceWithNoEndpoint_MapsCleanlyAndStagesNothing"/>,
    /// the pair of this one.
    /// </para>
    /// <para>
    /// Both endpoint-less shapes are covered: no <c>launchSettings.json</c> at all, and a launch
    /// profile that declares no <c>applicationUrl</c>. Measured under the pinned Aspire 13.4.2,
    /// both yield a ProjectResource with zero <see cref="EndpointAnnotation"/>s.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Configure_ProjectServiceDeclaringNoEndpoint_ThrowsNamingTheService(string? applicationUrl)
    {
        var csproj = CreateProjectFixture(applicationUrl);
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("orders-api", csproj), endpointConsumingTargets: Targeting("orders-api"));
        var builder = CreateBuilder();

        var ex = Assert.Throws<TopologyAuthoringException>(() => mapped.Configure(builder));

        Assert.Contains("orders-api", ex.Message, StringComparison.Ordinal);
        Assert.Contains("declares no endpoint", ex.Message, StringComparison.Ordinal);
        // The diagnostic must tell the author where the endpoints come from and how to add one;
        // "loud" is worth nothing if it is not actionable.
        Assert.Contains("launchSettings.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("applicationUrl", ex.Message, StringComparison.Ordinal);

        // #398 TRIPWIRE. The path asserted here is the author's OWN raw YAML scalar, echoed back
        // verbatim, so this diagnostic is not currently a producer of the absolute-host-path
        // disclosure #398 tracks. If #398 is ever closed by RESOLVING `project:` to a full path
        // earlier in the pipeline, this message becomes one — and this assertion would pin it
        // there. Revisit both together.
        Assert.Contains(csproj, ex.Message, StringComparison.Ordinal);

        // Nothing is staged for a service that was refused.
        Assert.False(mapped.StagedServiceEndpoints.ContainsKey("orders-api"));

        // The diagnostic must also tell the author why the identical-looking worker service in
        // the next suite is fine — otherwise the rule reads as "project-form services need HTTP",
        // which is exactly the false requirement the untargeted case disproves.
        Assert.Contains("no step targets", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE REGRESSION GATE. A .NET WORKER SERVICE — a project-form service declaring no endpoint
    /// that NO step targets — maps and configures cleanly: no throw, no <c>svc::</c> entry, and it
    /// remains a full member of the topology.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the test whose absence let an unconditional refusal through review. A
    /// <c>BackgroundService</c> consuming Kafka or a queue has no <c>applicationUrl</c> and no HTTP
    /// listener; it is schema-legal (<c>$defs/service</c> requires only <c>project</c>), it has no
    /// escape hatch (that same schema refuses <c>ports</c> and <c>healthCheck</c> on a project-form
    /// service, so its author cannot declare a non-HTTP shape the way REQ-008 lets an image-form
    /// service), and it is the canonical shape this product exists to test — the worker consuming
    /// the Kafka event in the one business transaction crossing REST, Kafka, a DB and a webhook.
    /// </para>
    /// <para>
    /// Both endpoint-less shapes are covered, and the assertions deliberately go past "did not
    /// throw": a refusal is not the only way to break this service. It must still be BUILT, still
    /// be health-gated, and simply carry no staged endpoint — byte-for-byte what it had before
    /// #348 introduced any staging for the project form at all.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Configure_UntargetedProjectServiceWithNoEndpoint_MapsCleanlyAndStagesNothing(
        string? applicationUrl)
    {
        var csproj = CreateProjectFixture(applicationUrl);

        // No endpointConsumingTargets at all — the shape of a suite whose steps address only a
        // Kafka dependency, never this worker. (Targeting() with a DIFFERENT name is covered by
        // the second case below.)
        var mapped = EnvironmentMapper.Map(EnvWithProject("order-worker", csproj));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        // Nothing staged — correct, because nothing reads svc::order-worker.
        Assert.False(mapped.StagedServiceEndpoints.ContainsKey("order-worker"));

        // Still a full member of the topology: built as a ProjectResource, and health-gated.
        Assert.NotNull(builder.Resources.OfType<ProjectResource>().SingleOrDefault(r => r.Name == "order-worker"));
        Assert.Contains("order-worker", mapped.HealthGateResourceNames);
    }

    /// <summary>
    /// The same worker, in a suite that DOES address other services: a non-empty target set that
    /// simply does not name this service leaves it alone exactly as an empty one does.
    /// </summary>
    /// <remarks>
    /// Distinct from the empty-set case above on purpose. An implementation that keyed the refusal
    /// on "any step targets anything" rather than on "a step targets THIS service" would pass that
    /// test and fail this one — and that is a plausible mistake, because the empty set is also what
    /// every caller predating the parameter supplies.
    /// </remarks>
    [Fact]
    public void Configure_ProjectServiceWithNoEndpoint_NotNamedInANonEmptyTargetSet_IsLeftAlone()
    {
        var csproj = CreateProjectFixture(applicationUrl: null);
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("order-worker", csproj),
            endpointConsumingTargets: Targeting("some-other-service", "and-another"));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        Assert.False(mapped.StagedServiceEndpoints.ContainsKey("order-worker"));
        Assert.Contains("order-worker", mapped.HealthGateResourceNames);
    }

    /// <summary>
    /// Staging is gated on the ENDPOINT existing, never on the service being targeted: an
    /// untargeted project-form service that does declare an endpoint is still staged.
    /// </summary>
    /// <remarks>
    /// Guards the over-correction — narrowing the staging itself to targeted services — which
    /// would leave <c>svc::</c> empty for anything the target-set derivation failed to spot, and
    /// so quietly reintroduce #348 through a different door.
    /// </remarks>
    [Fact]
    public void Configure_UntargetedProjectServiceWithAnEndpoint_IsStillStaged()
    {
        var csproj = CreateProjectFixture("http://localhost:5111");
        var mapped = EnvironmentMapper.Map(EnvWithProject("api", csproj));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        var staged = Assert.Contains("api", mapped.StagedServiceEndpoints);
        Assert.Equal("http", staged.EndpointName);
    }

    /// <summary>
    /// THE VERDICT CLASSIFICATION, pinned by type. The refusal above is raised from inside the
    /// Configure closure, and <c>SuiteTopology.StartAsync</c> wraps everything else escaping there
    /// as <c>OrchestrationException</c> → <c>EnvironmentError</c>. Deriving from
    /// <see cref="ArgumentException"/> is what routes this authoring fault to
    /// <c>ScenarioRunner</c>'s <c>catch (ArgumentException)</c> instead — Inconclusive, nothing
    /// executed, non-zero exit (#369).
    /// </summary>
    /// <remarks>
    /// Without this test the base class looks like an arbitrary detail and a later tidy-up could
    /// re-base it on <see cref="Exception"/>, silently converting an authoring fault into an
    /// infrastructure one that exits 0 (#390) — the one direction §12.1 must not bend, and
    /// invisible in every functional test because the diagnostic text would not change.
    /// </remarks>
    [Fact]
    public void TopologyAuthoringException_DerivesFromArgumentException_SoItIsClassifiedInconclusive()
    {
        Assert.True(
            typeof(ArgumentException).IsAssignableFrom(typeof(TopologyAuthoringException)),
            "TopologyAuthoringException must remain an ArgumentException — see the type's own remarks.");

        var csproj = CreateProjectFixture(applicationUrl: null);
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj), endpointConsumingTargets: Targeting("api"));
        var builder = CreateBuilder();

        // The shape ScenarioRunner actually uses: a `catch (ArgumentException)` must see it.
        // ThrowsAny, not Throws: xunit's Throws<T> demands an EXACT type match, which is the
        // opposite of the polymorphic catch this test exists to prove.
        var caught = Assert.ThrowsAny<ArgumentException>(() => mapped.Configure(builder));

        Assert.IsType<TopologyAuthoringException>(caught);
    }

    /// <summary>
    /// The image branch is untouched by the project-form staging rule: an image-form service with
    /// no explicit ports still stages its implicit <c>"http"</c> endpoint.
    /// </summary>
    [Fact]
    public void Configure_ImageService_StillStagesItsPrimaryEndpoint()
    {
        var env = new EnvironmentSpec(
            Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
            {
                ["web"] = new ServiceSpec(
                    Image: "myorg/web:1.0",
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: 8080,
                    Env: null),
            },
            Dependencies: null,
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        mapped.Configure(builder);

        var staged = Assert.Contains("web", mapped.StagedServiceEndpoints);
        Assert.Equal("http", staged.EndpointName);
    }

    /// <inheritdoc />
    public void Dispose() => _fixtures.Dispose();
}
