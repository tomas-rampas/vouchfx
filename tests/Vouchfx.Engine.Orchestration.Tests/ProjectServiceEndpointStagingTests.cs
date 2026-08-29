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

    /// <summary>
    /// A one-service environment declaring <paramref name="name"/> as a project-form service,
    /// optionally with an author-declared <paramref name="endpoint"/>.
    /// </summary>
    /// <param name="endpoint">
    /// The raw <c>endpoint:</c> scalar, or <see langword="null"/> for a service that declares
    /// none. The default is <see langword="null"/> deliberately: every test written before this
    /// field existed keeps calling the two-argument form, so those tests now double as the pin
    /// that an absent <c>endpoint:</c> leaves the fixed selection rule byte-for-byte unchanged.
    /// </param>
    private static EnvironmentSpec EnvWithProject(
        string name,
        string csprojPath,
        string? endpoint = null) =>
        new(
            Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
            {
                [name] = new ServiceSpec(
                    Image: null,
                    Project: csprojPath,
                    ImagePullPolicy: null,
                    HttpPort: null,
                    Env: null)
                {
                    Endpoint = endpoint,
                },
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
    /// observation carries only status and expectation, so nothing else in the run's own step
    /// record says so.
    /// An undisclosed downgrade is the part that makes it a finding; the CHOICE itself is
    /// endorsed, because preferring https would fail the dev-certificate handshake and land as an
    /// EnvironmentError, which exits 0 unless the caller passes <c>--fail-on-env-error</c>
    /// (§12.1's base rule, not #390 — that issue is about a run that executed nothing, and this
    /// step runs) — a green build over a step that verified nothing.
    /// </para>
    /// <para>
    /// It reaches the §14 event stream too (#450 / #453) — through a NEW record,
    /// <c>TransportNoticeEvent</c>, rather than an existing field, because every EXISTING
    /// free-text field reaching --events/--junit/--html is a scenario-level CAUSE for a non-Pass
    /// verdict. Adding an optional record is what the v1 freeze permits, and
    /// <c>Vouchfx.Engine.Runtime.TransportNoticeEvents</c> is its single producer;
    /// <c>EnvironmentMapper</c> raises the notice and does nothing else with it. The reach is
    /// <c>--events</c> and
    /// <c>--events-stream</c> only: <c>JunitXmlRenderer</c> and <c>HtmlRenderer</c> both take
    /// their <c>default:</c> arm on an unrecognised type, so a run whose only artefacts are
    /// <c>--junit</c> and <c>--html</c> still shows that green run with nothing in it about the
    /// transport.
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
    /// <c>Configure</c> is IDEMPOTENT in what it publishes: invoking it twice yields the same
    /// notice count as invoking it once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three dictionaries the closure captures are written by keyed assignment and so were
    /// already idempotent; <c>endpointSelectionNotices</c> is a list whose only write is an
    /// <c>Add</c>, so before the <c>Clear</c> at the top of the closure a second invocation
    /// doubled every notice. Nothing invokes it twice in production today — HeadlessTopology
    /// calls it once — so this pins a property rather than repairing a live defect, and it is the
    /// property that stops the next caller (or the next test) discovering the inconsistency the
    /// hard way.
    /// </para>
    /// <para>
    /// A FRESH builder for the second call, deliberately: re-running the closure over the SAME
    /// builder would re-add resources under names it already holds, which is a different question
    /// from the one asked here.
    /// </para>
    /// </remarks>
    [Fact]
    public void ConfigureInvokedTwice_DoesNotDuplicateTheTransportDowngradeNotice()
    {
        var csproj = CreateProjectFixture("https://localhost:7333;http://localhost:5333");
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj), endpointConsumingTargets: Targeting("api"));

        mapped.Configure(CreateBuilder());
        var afterOne = mapped.EndpointSelectionNotices.Count;

        mapped.Configure(CreateBuilder());

        Assert.Equal(1, afterOne);
        Assert.Equal(afterOne, mapped.EndpointSelectionNotices.Count);

        // Still the SAME notice, not a survivor of a clear that dropped the real one.
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
    /// escape hatch (that same schema's project-form clause forbids every field an image-form
    /// service would use to declare a non-HTTP shape — grep that clause's <c>then</c> for the
    /// current roster rather than trusting a copy here — so its author cannot do what REQ-008 lets
    /// an image-form author do), and it is the canonical shape this product exists to test — the worker consuming
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

    // ─────────────────────────────────────────────────────────────────────────
    // `endpoint:` — the author's own selection (#448).
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE FIELD'S POINT: a project serving both schemes is addressed over the one the AUTHOR
    /// named, not the one the fixed rule would have picked. Without <c>endpoint:</c> this exact
    /// project stages "http" — pinned by
    /// <see cref="Configure_ProjectServiceDeclaringBothSchemes_StagesHttpWhicheverOrderTheyAreDeclaredIn"/>.
    /// </summary>
    [Theory]
    [InlineData("http://localhost:5333;https://localhost:7333")]
    [InlineData("https://localhost:7333;http://localhost:5333")]
    public void Configure_ProjectServiceDeclaringEndpointHttps_StagesTheHttpsListener(
        string applicationUrl)
    {
        var csproj = CreateProjectFixture(applicationUrl);
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj, endpoint: "https"),
            endpointConsumingTargets: Targeting("api"));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        var staged = Assert.Contains("api", mapped.StagedServiceEndpoints);
        Assert.Equal("https", staged.EndpointName);
        Assert.Equal("https", staged.Scheme);
        Assert.True(staged.Exists, "the staged endpoint must exist on the resource");
    }

    /// <summary>
    /// THE CASE NO SCHEME CAN EXPRESS, and the reason the match is on the endpoint NAME: a project
    /// declaring two http URLs has two listeners the fixed rule cannot tell apart. Measured under
    /// the pinned Aspire 13.4.2, they are named "http" and "http2"; <c>endpoint: http2</c> reaches
    /// the second, which nothing else in the language could.
    /// </summary>
    [Fact]
    public void Configure_ProjectServiceDeclaringEndpointHttp2_StagesTheSecondHttpListener()
    {
        var csproj = CreateProjectFixture("http://localhost:5333;http://localhost:5334");
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj, endpoint: "http2"),
            endpointConsumingTargets: Targeting("api"));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        // The naming this test depends on, asserted rather than assumed: if Aspire ever names the
        // second same-scheme listener something else, this fails HERE with a readable cause rather
        // than as an inexplicable staging miss.
        var resource = builder.Resources.OfType<ProjectResource>().Single(r => r.Name == "api");
        var declared = resource.Annotations.OfType<EndpointAnnotation>().Select(e => e.Name).ToList();
        Assert.Collection(
            declared,
            first => Assert.Equal("http", first),
            second => Assert.Equal("http2", second));

        var staged = Assert.Contains("api", mapped.StagedServiceEndpoints);
        Assert.Equal("http2", staged.EndpointName);
        Assert.True(staged.Exists, "the staged endpoint must exist on the resource");
    }

    /// <summary>
    /// EDGE-002: naming the project's ONLY endpoint is a no-op that stages the same reference the
    /// fixed rule would have — no throw, and no notice of either kind.
    /// </summary>
    [Fact]
    public void Configure_ProjectServiceDeclaringTheOnlyEndpointItHas_StagesItAndSaysNothing()
    {
        var csproj = CreateProjectFixture("http://localhost:5111");
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj, endpoint: "http"),
            endpointConsumingTargets: Targeting("api"));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        var staged = Assert.Contains("api", mapped.StagedServiceEndpoints);
        Assert.Equal("http", staged.EndpointName);
        Assert.Empty(mapped.EndpointSelectionNotices);
        Assert.Empty(mapped.EndpointTrustNotices);
    }

    /// <summary>
    /// THE FIXED RULE IS UNTOUCHED where no <c>endpoint:</c> is declared: the same both-schemes
    /// project stages "http", raises its one transport-downgrade notice, and raises no trust
    /// notice — the new record is not emitted for a selection the author did not make.
    /// </summary>
    [Fact]
    public void Configure_ProjectServiceWithNoDeclaredEndpoint_AppliesTheFixedRuleUnchanged()
    {
        var csproj = CreateProjectFixture("http://localhost:5333;https://localhost:7333");
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj), endpointConsumingTargets: Targeting("api"));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        var staged = Assert.Contains("api", mapped.StagedServiceEndpoints);
        Assert.Equal("http", staged.EndpointName);
        Assert.Single(mapped.EndpointSelectionNotices);
        Assert.Empty(mapped.EndpointTrustNotices);
    }

    /// <summary>
    /// AN UNMATCHED <c>endpoint:</c> IS REFUSED, naming the service, the string the author wrote,
    /// and every endpoint the project actually declares with its scheme — the diagnostic that
    /// makes a typo a two-second fix instead of a topology cycle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both a TARGETED and an UNTARGETED service, because the difference is the requirement
    /// (EDGE-001). The endpoint-LESS refusal below is deliberately gated on targeting — a worker
    /// service legitimately declares nothing — but that reasoning does not transfer: an
    /// <c>endpoint:</c> naming something the project does not declare is a false statement the
    /// author wrote, and leaving it unremarked on an untargeted service is precisely the
    /// accepted-and-silently-ignored shape this field exists to end.
    /// </para>
    /// <para>
    /// The refusal is <c>TopologyAuthoringException</c> for the reason its sibling is: anything
    /// else escaping the Configure closure is wrapped as an <c>OrchestrationException</c> →
    /// EnvironmentError, which reports an authoring fault as an infrastructure one and exits 0
    /// when nothing executed.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Configure_ProjectServiceDeclaringAnUnmatchedEndpoint_ThrowsNamingEveryDeclaredOne(
        bool targeted)
    {
        var csproj = CreateProjectFixture("http://localhost:5333;https://localhost:7333");
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj, endpoint: "grpc"),
            endpointConsumingTargets: targeted ? Targeting("api") : null);
        var builder = CreateBuilder();

        var ex = Assert.Throws<TopologyAuthoringException>(() => mapped.Configure(builder));

        Assert.Contains("api", ex.Message, StringComparison.Ordinal);
        Assert.Contains("grpc", ex.Message, StringComparison.Ordinal);
        // Every discovered endpoint, as `name (scheme)`.
        Assert.Contains("http (http)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("https (https)", ex.Message, StringComparison.Ordinal);

        // NOT advice to reach for a field this form does not have. All three are refused on a
        // project-form service, so suggesting any of them would send the author to a validation
        // failure — a diagnostic that costs a cycle is barely better than none.
        Assert.DoesNotContain("'ports'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'httpPort'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'security'", ex.Message, StringComparison.Ordinal);

        Assert.False(mapped.StagedServiceEndpoints.ContainsKey("api"));
    }

    /// <summary>
    /// EDGE-005: the match is Ordinal, so it is case-sensitive. <c>endpoint: HTTPS</c> does not
    /// name an endpoint called "https" — it names nothing, and is refused like any other unmatched
    /// value.
    /// </summary>
    /// <remarks>
    /// One canonical spelling per DSL vocabulary term is the pre-GA decision this follows
    /// (dependency type, imagePullPolicy, verifyMode, security.profile). Case-folding here would
    /// also be unsound in a way those are not: the value is matched against names the ORCHESTRATOR
    /// produced, and nothing guarantees two of them cannot differ only by case.
    /// </remarks>
    [Fact]
    public void Configure_ProjectServiceDeclaringEndpointInTheWrongCase_IsRefused()
    {
        var csproj = CreateProjectFixture("http://localhost:5333;https://localhost:7333");
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj, endpoint: "HTTPS"),
            endpointConsumingTargets: Targeting("api"));
        var builder = CreateBuilder();

        var ex = Assert.Throws<TopologyAuthoringException>(() => mapped.Configure(builder));

        Assert.Contains("HTTPS", ex.Message, StringComparison.Ordinal);
        Assert.Contains("https (https)", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A WHITESPACE-ONLY <c>endpoint:</c> IS REFUSED, not treated as absent. The schema's
    /// <c>minLength: 1</c> stops the empty string and nothing else, so <c>endpoint: "   "</c>
    /// validates — and the field's shipped description promises such a value is refused at
    /// topology-build time naming what the project declared.
    /// </summary>
    /// <remarks>
    /// The implementation detail this pins is the presence test: <c>is { }</c> rather than
    /// <c>string.IsNullOrWhiteSpace</c>. The latter would silently fall through to the fixed rule,
    /// stage "http", pass, and make that shipped description false — the failure mode is a suite
    /// that quietly ignores what its author wrote, which is the whole complaint this field answers.
    /// </remarks>
    [Fact]
    public void Configure_ProjectServiceDeclaringAWhitespaceOnlyEndpoint_IsRefusedNotIgnored()
    {
        var csproj = CreateProjectFixture("http://localhost:5333;https://localhost:7333");
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj, endpoint: "   "),
            endpointConsumingTargets: Targeting("api"));
        var builder = CreateBuilder();

        var ex = Assert.Throws<TopologyAuthoringException>(() => mapped.Configure(builder));

        Assert.Contains("http (http)", ex.Message, StringComparison.Ordinal);

        // The value is QUOTED in the message, so a reader can tell it from the spacing around it —
        // the whole reason this shape needed pinning separately from an ordinary typo.
        Assert.Contains("'endpoint: \"   \"'", ex.Message, StringComparison.Ordinal);

        // The falling-through failure mode, asserted directly: nothing staged, no notice of either
        // kind raised — the fixed rule would have staged "http" AND announced the downgrade, and
        // an https selection would have announced the absence of trust, so both are checked.
        Assert.False(mapped.StagedServiceEndpoints.ContainsKey("api"));
        Assert.Empty(mapped.EndpointSelectionNotices);
        Assert.Empty(mapped.EndpointTrustNotices);
    }

    /// <summary>
    /// AN EMPTY <c>endpoint:</c> IS REFUSED TOO, on the same Find-then-throw path, and this is the
    /// case the schema cannot be relied on for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Constructed as an <c>EnvironmentSpec</c> DIRECTLY, because that is the point: the schema's
    /// <c>minLength: 1</c> refuses the empty string, so a suite that goes through
    /// <c>DocumentValidator</c> never reaches here with one. One author-reachable path does not go
    /// through it: <c>--watch</c> performs no schema validation at all (<c>WatchRunner.Compile</c>
    /// is <c>YamlDocumentParser.Parse</c> + <c>AstBuilder.Build</c>), and a dangling
    /// <c>endpoint:</c> key — no value after the colon — round-trips through <c>GetScalar</c>
    /// as <c>""</c> rather than <see langword="null"/>. Constructing the spec directly, as this
    /// test does, reaches the same code by the same route.
    /// </para>
    /// <para>
    /// Treating <c>""</c> as absent would run the fixed rule and stage "http": the author's
    /// <c>endpoint:</c> key accepted and silently ignored, which is the exact defect class this
    /// field exists to end.
    /// </para>
    /// </remarks>
    [Fact]
    public void Configure_ProjectServiceDeclaringAnEmptyEndpoint_IsRefusedNotIgnored()
    {
        var csproj = CreateProjectFixture("http://localhost:5333;https://localhost:7333");
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj, endpoint: string.Empty),
            endpointConsumingTargets: Targeting("api"));
        var builder = CreateBuilder();

        var ex = Assert.Throws<TopologyAuthoringException>(() => mapped.Configure(builder));

        Assert.Contains("api", ex.Message, StringComparison.Ordinal);
        Assert.Contains("http (http)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("https (https)", ex.Message, StringComparison.Ordinal);

        Assert.False(mapped.StagedServiceEndpoints.ContainsKey("api"));
        Assert.Empty(mapped.EndpointSelectionNotices);
        Assert.Empty(mapped.EndpointTrustNotices);
    }

    /// <summary>
    /// EDGE-009 (and EDGE-001 for the untargeted half): an <c>endpoint:</c> on a project that
    /// declares NO endpoint at all is refused with THIS message — the selector one — whether or
    /// not a step targets the service, and it carries BOTH of the endpoint-less refusal's escapes,
    /// because either can be the fix.
    /// </summary>
    /// <remarks>
    /// Two refusals could fire here. The more specific one wins: the author wrote an explicit
    /// selector, so naming it back is the more useful diagnostic — but it would be a worse
    /// diagnostic than the one it displaces if it dropped the actionable sentences that one had,
    /// so it carries them. Both matter, and they point opposite ways: an author who meant to
    /// address this service needs the <c>applicationUrl</c>; the canonical author here — a worker
    /// service with no launch profile, no step targeting it and a stray <c>endpoint:</c> — needs
    /// to delete that line. Untargeted matters independently: without EDGE-001's rule such a
    /// service would pass silently.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Configure_ProjectServiceWithNoEndpointsButADeclaredSelector_RefusesWithTheSelectorMessage(
        bool targeted)
    {
        var csproj = CreateProjectFixture(applicationUrl: null);
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("order-worker", csproj, endpoint: "http"),
            endpointConsumingTargets: targeted ? Targeting("order-worker") : null);
        var builder = CreateBuilder();

        var ex = Assert.Throws<TopologyAuthoringException>(() => mapped.Configure(builder));

        // The SELECTOR message, distinguished from the endpoint-less one by naming the value the
        // author wrote and reporting the discovered set as "(none)".
        Assert.Contains("order-worker", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'endpoint: \"http\"'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("(none)", ex.Message, StringComparison.Ordinal);

        // ...carrying BOTH escapes the message it displaces would have given. The second one is
        // the fix for this test's own fixture — a worker service — and the message would be
        // advice-that-does-not-apply without it.
        Assert.Contains("launchSettings.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("applicationUrl", ex.Message, StringComparison.Ordinal);
        Assert.Contains("remove the 'endpoint:' line", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// AN EXPLICIT PLAINTEXT SELECTION IS SILENT. The transport-downgrade notice announces a
    /// choice the ENGINE made; announcing an author's own <c>endpoint: http</c> back to them spends
    /// the notice's credibility on the one case that needs no warning.
    /// </summary>
    [Fact]
    public void Configure_ProjectServiceDeclaringEndpointHttpBesideAnHttpsSibling_AnnouncesNothing()
    {
        var csproj = CreateProjectFixture("http://localhost:5333;https://localhost:7333");
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj, endpoint: "http"),
            endpointConsumingTargets: Targeting("api"));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        Assert.Empty(mapped.EndpointSelectionNotices);
        Assert.Empty(mapped.EndpointTrustNotices);
    }

    /// <summary>
    /// AN EXPLICIT HTTPS SELECTION IS NOT SILENT, and this is the half of the rule that is not
    /// symmetric with the one above.
    /// </summary>
    /// <remarks>
    /// Suppressing here would remove the ONLY thing in the run that says anything about transport,
    /// while the author's likely reading of what they typed — "this is now secured" — is exactly
    /// what it is not: a project-form service cannot declare <c>security</c>, so no trust material
    /// exists: the certificate that listener presents is checked against this host's default trust
    /// store and nothing else, and vouchfx asserts nothing about the outcome. Composed with a
    /// handshake failure landing as an EnvironmentError, the plausible outcome is a green CI run
    /// over a suite that verified nothing. It is a DIFFERENT record from the downgrade notice
    /// because it is a different fact — "the address is TLS and this engine configured no trust
    /// for it", not "the engine picked plaintext for you".
    /// </remarks>
    [Fact]
    public void Configure_ProjectServiceDeclaringEndpointHttps_AnnouncesTheAbsenceOfTrust()
    {
        var csproj = CreateProjectFixture("http://localhost:5333;https://localhost:7333");
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj, endpoint: "https"),
            endpointConsumingTargets: Targeting("api"));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        // Asserted on FIELDS, not on wording — the record is typed precisely so no test pins an
        // English sentence as the contract.
        var notice = Assert.Single(mapped.EndpointTrustNotices);
        Assert.Equal("api", notice.ServiceName);
        Assert.Equal("https", notice.SelectedEndpoint);

        // And NOT the downgrade notice: the two must stay tellable apart, or a reader draws the
        // opposite conclusion about what the run proved.
        Assert.Empty(mapped.EndpointSelectionNotices);
    }

    /// <summary>
    /// AN HTTPS-ONLY PROJECT WITH NO <c>endpoint:</c> ALSO ANNOUNCES THE ABSENCE OF TRUST — the
    /// case the notice's first gate missed, and the reason that gate is now on the SELECTED
    /// endpoint rather than on who selected it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing here was authored: the service declares no <c>endpoint:</c>, the fixed rule reaches
    /// the project's one https listener because there is no http one to prefer, and the run then
    /// addresses a TLS listener the engine configured no trust material for. Gating on "the author
    /// selected something" left this silent — the downgrade notice cannot fire (it requires an http
    /// selection) and the trust notice could not (there was no selection to gate on), so the whole
    /// run said nothing about transport.
    /// </para>
    /// <para>
    /// User-visible consequence, pinned deliberately: this suite emits an advisory it did not emit
    /// before. Terminal-only — no verdict, no exit code, no artefact change.
    /// </para>
    /// </remarks>
    [Fact]
    public void Configure_TargetedHttpsOnlyProjectWithNoDeclaredEndpoint_AnnouncesTheAbsenceOfTrust()
    {
        var csproj = CreateProjectFixture("https://localhost:7222");
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj), endpointConsumingTargets: Targeting("api"));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        var notice = Assert.Single(mapped.EndpointTrustNotices);
        Assert.Equal("api", notice.ServiceName);
        Assert.Equal("https", notice.SelectedEndpoint);

        // The downgrade notice still cannot fire — there is no plaintext endpoint to have been
        // preferred — which is exactly why this one had to.
        Assert.Empty(mapped.EndpointSelectionNotices);
    }

    /// <summary>
    /// THE SAME PROJECT, UNTARGETED, STAYS SILENT. The targeting gate survives the widening: the
    /// notice describes traffic that will actually happen, and no step addresses this service.
    /// </summary>
    [Fact]
    public void Configure_UntargetedHttpsOnlyProjectWithNoDeclaredEndpoint_AnnouncesNothing()
    {
        var csproj = CreateProjectFixture("https://localhost:7222");
        var mapped = EnvironmentMapper.Map(EnvWithProject("api", csproj));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        Assert.Empty(mapped.EndpointTrustNotices);
        Assert.Empty(mapped.EndpointSelectionNotices);
    }

    /// <summary>
    /// THE TEST THAT PROVES THE SCHEME TEST IS ON THE ANNOTATION, NOT ON THE AUTHOR'S STRING: an
    /// https listener that is NOT named "https" still produces the trust notice.
    /// </summary>
    /// <remarks>
    /// <c>endpoint:</c> matches by NAME, so a project may address a TLS listener under any name
    /// the orchestrator gives it — here "https2", the second https URL in one
    /// <c>applicationUrl</c>. An implementation comparing the author's literal text against
    /// "https" would stay silent here (dangerous case, no warning) and would warn about a
    /// plaintext listener that happened to be called "https" (harmless case, noise) — the wrong
    /// way round for a security advisory, and invisible to every test that only ever writes
    /// <c>endpoint: https</c>.
    /// </remarks>
    [Fact]
    public void Configure_ProjectServiceSelectingAnHttpsListenerNotNamedHttps_StillAnnouncesTheAbsenceOfTrust()
    {
        var csproj = CreateProjectFixture("https://localhost:7333;https://localhost:7334");
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj, endpoint: "https2"),
            endpointConsumingTargets: Targeting("api"));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        // The naming this test rests on, asserted rather than assumed.
        var resource = builder.Resources.OfType<ProjectResource>().Single(r => r.Name == "api");
        var declared = resource.Annotations.OfType<EndpointAnnotation>().ToList();
        Assert.Collection(
            declared.Select(e => e.Name),
            first => Assert.Equal("https", first),
            second => Assert.Equal("https2", second));
        Assert.All(declared, e => Assert.Equal("https", e.UriScheme));

        var staged = Assert.Contains("api", mapped.StagedServiceEndpoints);
        Assert.Equal("https2", staged.EndpointName);

        var notice = Assert.Single(mapped.EndpointTrustNotices);
        Assert.Equal("https2", notice.SelectedEndpoint);
    }

    /// <summary>
    /// AN UNTARGETED SERVICE ANNOUNCES NEITHER. Both notices describe traffic that will happen;
    /// neither is true of a service no step addresses.
    /// </summary>
    [Fact]
    public void Configure_UntargetedProjectServiceDeclaringEndpointHttps_AnnouncesNothing()
    {
        var csproj = CreateProjectFixture("http://localhost:5333;https://localhost:7333");
        var mapped = EnvironmentMapper.Map(EnvWithProject("api", csproj, endpoint: "https"));
        var builder = CreateBuilder();

        mapped.Configure(builder);

        Assert.Empty(mapped.EndpointTrustNotices);
        Assert.Empty(mapped.EndpointSelectionNotices);

        // Staged all the same — staging never depended on targeting.
        var staged = Assert.Contains("api", mapped.StagedServiceEndpoints);
        Assert.Equal("https", staged.EndpointName);
    }

    /// <summary>
    /// The trust notice carries the same idempotence guarantee its sibling does: invoking
    /// <c>Configure</c> twice yields the same notice count as invoking it once.
    /// </summary>
    /// <remarks>
    /// The list's only write is an <c>Add</c>, so without the <c>Clear</c> at the top of the
    /// closure a second invocation doubles it. A FRESH builder for the second call, as in the
    /// sibling test: re-running the closure over the SAME builder would re-add resources under
    /// names it already holds, which is a different question.
    /// </remarks>
    [Fact]
    public void ConfigureInvokedTwice_DoesNotDuplicateTheTrustNotice()
    {
        var csproj = CreateProjectFixture("http://localhost:5333;https://localhost:7333");
        var mapped = EnvironmentMapper.Map(
            EnvWithProject("api", csproj, endpoint: "https"),
            endpointConsumingTargets: Targeting("api"));

        mapped.Configure(CreateBuilder());
        var afterOne = mapped.EndpointTrustNotices.Count;

        mapped.Configure(CreateBuilder());

        Assert.Equal(1, afterOne);
        Assert.Equal(afterOne, mapped.EndpointTrustNotices.Count);

        // Still the SAME notice, not a survivor of a clear that dropped the real one.
        var notice = Assert.Single(mapped.EndpointTrustNotices);
        Assert.Equal("api", notice.ServiceName);
        Assert.Equal("https", notice.SelectedEndpoint);
    }

    /// <summary>
    /// THE ADVISORY'S OWN TEXT IS PINNED HERE, because published documents make claims about what
    /// this line says that no assertion on the record's fields can check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactly two assertions in this file are on a NOTICE's rendered LINE: this one, and
    /// <see cref="SelectionNoticeToString_DisclosesEveryClauseThePublishedDocsClaimItDoes"/>
    /// below. Every OTHER notice assertion here is on FIELDS, on the stated ground that a typed
    /// record exists precisely so no test pins an English sentence as the contract. (The refusal
    /// tests above do pin fragments of their exception messages — a count is deliberately not
    /// given here, because one in a comment rots at the next assertion added — and they are right
    /// to: a diagnostic has no fields, so its wording is the whole of what a test can
    /// hold.) The two line pins are the deliberate exception, and each earns it the same way: a
    /// published document states what the line discloses, and a reword dropping that clause
    /// leaves every field assertion in this file green.
    /// </para>
    /// <para>
    /// What THIS string is load-bearing for. <see cref="EndpointTrustNotice.ToString"/> holds the
    /// only copy of the disclosure that a TLS-addressed <c>project:</c>-form service gets no
    /// engine-configured trust, and THREE published documents make claims about what it says:
    /// CHANGELOG's transport-advisory entry (“naming the service and the selected endpoint and
    /// stating what the engine did not do”), docs/security-matrix.md (“vouchfx contributes no
    /// trust anchor, pins no peer, presents no client identity and asserts nothing about the
    /// transport”), and docs/02's §3.2 transport-advisories paragraph (“no trust anchor, no peer
    /// pinning, no client identity, and no assertion about the outcome”). The count was “two”
    /// here until #454 was worked and the third was found by grep; the DSL specification is the
    /// document an author is likeliest to read, so it was the worst one to have missed. A reword
    /// dropping, say, the client-identity clause silently falsifies a published security
    /// disclosure in all three. Nothing else would catch it.
    /// </para>
    /// <para>
    /// SUBSTANCE, NOT SENTENCE. The pins below are short meaning-bearing fragments of each
    /// disclosed absence and of the exit-code clause, not the sentence they sit in, so the
    /// wording stays free to change; the test fails only when a clause is actually dropped —
    /// which is exactly the change that should require a deliberate decision rather than passing
    /// unnoticed.
    /// </para>
    /// </remarks>
    [Fact]
    public void TrustNoticeToString_DisclosesEveryAbsenceThePublishedDocsClaimItDoes()
    {
        // Values chosen so the identity assertions below cannot be satisfied by the message's own
        // boilerplate: "https" appears in the sentence regardless, an endpoint named "tls-in" does
        // not.
        var line = new EndpointTrustNotice("orders-api", "tls-in").ToString();

        // WHICH service and WHICH address. An advisory that does not identify the affected
        // endpoint cannot be acted on, and both are the notice's only two fields.
        Assert.Contains("orders-api", line, StringComparison.Ordinal);
        Assert.Contains("tls-in", line, StringComparison.Ordinal);

        // Absence 1 of 4 — no trust anchor is contributed.
        Assert.Contains("trust anchor", line, StringComparison.OrdinalIgnoreCase);

        // Absence 2 of 4 — no peer pinning. Two fragments rather than one phrase, because the
        // clause is equally true as "pins the peer", "pins no peer" or "peer pinning".
        Assert.Contains("peer", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pin", line, StringComparison.OrdinalIgnoreCase);

        // Absence 3 of 4 — no client identity is presented. This is the clause whose loss would
        // be least visible and most consequential: it is the difference between "vouchfx did not
        // authenticate itself" and a reader assuming mutual TLS happened. An alternation for the
        // same reason absence 4 is one: the substance is that vouchfx authenticates itself to
        // nothing, and "identity", "certificate" and "credential" all say it.
        Assert.True(
            line.Contains("client identity", StringComparison.OrdinalIgnoreCase)
                || line.Contains("client cert", StringComparison.OrdinalIgnoreCase)
                || line.Contains("client credential", StringComparison.OrdinalIgnoreCase),
            "The advisory must still disclose that vouchfx presents no client identity. "
                + $"Got: {line}");

        // Absence 4 of 4 — vouchfx asserts nothing about the transport. Spelled as an
        // alternation because this one has several natural renderings and no single noun phrase.
        Assert.True(
            line.Contains("asserts nothing", StringComparison.OrdinalIgnoreCase)
                || line.Contains("no assertion", StringComparison.OrdinalIgnoreCase)
                || line.Contains("makes no claim", StringComparison.OrdinalIgnoreCase),
            "The advisory must still disclose that vouchfx asserts nothing about the transport. "
                + $"Got: {line}");

        // And the consequence that makes the four absences matter: a failed handshake here is an
        // environment error, which leaves the run GREEN unless the operator opted in. Dropping
        // this clause would leave a reader believing the absence is at least loud.
        Assert.Contains("environment error", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--fail-on-env-error", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE DOWNGRADE ADVISORY'S OWN TEXT IS PINNED HERE, for the reason its sibling above is
    /// pinned: published documents make claims about what this line says, and a reword can
    /// falsify them while every assertion on the record's fields stays green (#454).
    /// </summary>
    /// <remarks>
    /// <para>
    /// TWO published documents, THREE claim sites. docs/02's §3.2 transport-advisories paragraph
    /// is the nearest paraphrase and the one that matters most — the downgrade notice “names both
    /// endpoints and states that steps targeting the service will use plaintext”. The CHANGELOG
    /// says it twice: the #348 entry (“the notice reports what steps addressing the service will
    /// use”, which is what makes the silence for an untargeted service correct rather than a bug)
    /// and the transport-advisory entry's closing contrast, which distinguishes this notice from
    /// its sibling by saying it reports “the opposite choice”. docs/security-matrix.md describes
    /// the fixed selection rule but makes no claim about this notice's text, so it is not on the
    /// list.
    /// </para>
    /// <para>
    /// THE NAMES-BOTH-ENDPOINTS CLAUSE IS THE LOAD-BEARING ONE, and it is the specific gap #454
    /// was filed for. Two assertions elsewhere in this file check that the record CARRIES a
    /// rejected endpoint; they say nothing about whether
    /// <see cref="EndpointSelectionNotice.ToString"/> still RENDERS it. A reword that drops the
    /// rejected endpoint from the line passes every one of them while falsifying docs/02 and
    /// gutting the advisory — an author told only that the http listener was staged learns
    /// nothing they did not already know, where the whole point is that an https listener was
    /// available and not taken.
    /// </para>
    /// <para>
    /// SUBSTANCE, NOT SENTENCE, and DELIBERATELY LOOSE WHERE NOTHING IS CLAIMED. The pins below
    /// are short meaning-bearing fragments, not the sentence they sit in. Four things are left
    /// loose on purpose. (1) The whole second sentence — that a <c>project:</c>-form service
    /// cannot declare <c>security</c>, so the engine holds no trust material and a request would
    /// fail the handshake — is the notice's RATIONALE for the choice, and no published document
    /// claims the notice states it; the CHANGELOG gives that reasoning in its own voice, about
    /// the engine's behaviour, not as a report of this string's contents. (2) The scheme labels
    /// <c>(http)</c> and <c>(https)</c>: docs/02 claims the notice names both endpoints, not that
    /// it annotates either. (3) The transport word is an alternation over “plaintext”, “plain
    /// text”, “cleartext” and “unencrypted”, and case-insensitive: the disclosure is that the
    /// traffic is not encrypted, and each of those says it. (4) The grammar joining “steps” to
    /// the service is unpinned — “steps targeting it”, “steps that address it” and “steps
    /// addressing the service” are the same claim. The trust pin above had to be loosened once
    /// for over-pinning exactly this way: it fixed the phrase “client identity”, and a reword to
    /// “client certificate” would have failed while preserving the disclosure whole.
    /// </para>
    /// </remarks>
    [Fact]
    public void SelectionNoticeToString_DisclosesEveryClauseThePublishedDocsClaimItDoes()
    {
        // Endpoint names chosen so no assertion below can be satisfied by the message's own
        // boilerplate, in either direction: "http" and "https" appear in the sentence regardless
        // but "http-in" and "tls-in" do not, and neither name contains any fragment the
        // transport alternation looks for.
        var line = new EndpointSelectionNotice("orders-api", "http-in", "tls-in").ToString();

        // Clause 1 of 2 — WHICH service, and BOTH endpoints. The rejected one is the fragment
        // this whole test exists for: it is the only field whose loss from the rendered line is
        // invisible to every other assertion in this file.
        Assert.Contains("orders-api", line, StringComparison.Ordinal);
        Assert.Contains("http-in", line, StringComparison.Ordinal);
        Assert.Contains("tls-in", line, StringComparison.Ordinal);

        // Clause 2 of 2 — what steps addressing the service will actually use. Three fragments
        // rather than one phrase: the noun, the transport, and the fact that the two are
        // connected. The grammar joining them is not pinned, and neither is any one spelling of
        // "not encrypted".
        Assert.True(
            line.Contains("plaintext", StringComparison.OrdinalIgnoreCase)
                || line.Contains("plain text", StringComparison.OrdinalIgnoreCase)
                || line.Contains("cleartext", StringComparison.OrdinalIgnoreCase)
                || line.Contains("unencrypted", StringComparison.OrdinalIgnoreCase),
            "The advisory must still disclose that the staged transport is unencrypted — that is "
                + $"the whole content of the downgrade it reports. Got: {line}");
        Assert.Contains("step", line, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            line.Contains("target", StringComparison.OrdinalIgnoreCase)
                || line.Contains("address", StringComparison.OrdinalIgnoreCase),
            "The advisory must still say WHICH steps the plaintext transport applies to — the "
                + "ones targeting or addressing the service — because the notice is silent for a "
                + $"service no step targets, and that silence is only correct if it does. Got: {line}");
    }

    /// <inheritdoc />
    public void Dispose() => _fixtures.Dispose();
}
