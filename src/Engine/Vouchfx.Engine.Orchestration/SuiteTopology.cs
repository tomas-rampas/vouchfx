// Vouchfx.Engine.Orchestration — SuiteTopology (S03-A-01).
//
// Build-once-per-suite topology fixture (§4 / MVP §8.2).
//
// Pipeline:
//   1. EnvironmentMapper.Map(environment)       → MappedTopology (Configure, ResolveServices, HealthGateResourceNames)
//   2. HeadlessTopology.StartAsync(...)         → running DistributedApplication (DisableDashboard, DCP metadata)
//   3. Per-gate WaitForResourceHealthyAsync()   → most-specific resource first (§4: database, not server)
//   4. mapped.ResolveServices(app, ct)          → DiscoveredServices (endpoint URLs + connection strings)
//
// Dispose-on-failure guarantee: at every step that can throw, the inner HeadlessTopology is
// disposed before the classified OrchestrationException is propagated, so containers never leak.
//
// §12.1 Environment error invariant: every infrastructure exception is classified via
// OrchestrationErrorClassifier and wrapped in OrchestrationException.  Callers MUST catch
// OrchestrationException separately and map it to the EnvironmentError verdict, never to Fail.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Abstractions.Security;
using Vouchfx.Engine.Authoring.Model;

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// A per-suite fixture that builds the Aspire topology <b>once</b>, exposes the discovered
/// service endpoints and managed-dependency connection strings, and tears down cleanly when
/// the suite completes — enabling many scenario invocations to share one topology build
/// without rebuilding (§4 / MVP §8.2 build-once invariant).
/// </summary>
/// <remarks>
/// <para>
/// <b>Build-once intent:</b> topology construction (image pull, container start, health gate)
/// is expensive.  <see cref="SuiteTopology"/> is constructed once per suite (or xUnit class
/// fixture) and reused across all scenarios in that suite.  The compiled delegate for each
/// scenario receives endpoint URLs and connection strings via <see cref="DiscoveredServices"/>
/// rather than re-orchestrating the topology.
/// </para>
/// <para>
/// <b>§4 invariants enforced here:</b>
/// <list type="bullet">
///   <item>Resources are health-gated via <c>WaitForResourceHealthyAsync</c> in the order
///   supplied by <see cref="MappedTopology.HealthGateResourceNames"/>: database resources
///   before server resources before service containers (most-specific first).  This prevents
///   the Postgres server-vs-database race described in §4.</item>
///   <item>Every health gate uses a per-gate bounded <see cref="CancellationTokenSource"/> so
///   a single stalled container cannot block the entire suite start indefinitely.</item>
///   <item>All infrastructure failures are classified as <see cref="OrchestrationErrorKind"/>
///   via <see cref="OrchestrationErrorClassifier"/> and thrown as
///   <see cref="OrchestrationException"/> — never as the raw Aspire/DCP exception.
///   Conflating an Environment error with a test Fail destroys trust in the tool (CLAUDE.md).</item>
///   <item>The inner <see cref="HeadlessTopology"/> is disposed on <em>every</em> failure path
///   so Docker containers do not leak.</item>
/// </list>
/// </para>
/// <para>
/// <b>Usage:</b>
/// <code>
/// await using var suite = await SuiteTopology.StartAsync(environment, appHostAssemblyName);
/// var url = (string)suite.DiscoveredServices["myService"];
/// </code>
/// </para>
/// </remarks>
public sealed class SuiteTopology : IAsyncDisposable
{
    /// <summary>
    /// The "no step speaks a protocol the probe knows" set, shared so the common path allocates
    /// nothing.
    /// </summary>
    private static readonly IReadOnlySet<string> EmptyTargets =
        new HashSet<string>(StringComparer.Ordinal);

    private readonly HeadlessTopology _inner;

    // Retained so the seed can be RE-APPLIED against the kept topology between watch re-runs
    // (S08-T10): the environment (which carries environment.seed + dependency types) and the
    // base directory relative seed file paths resolve against.  Both are exactly the values
    // StartAsync used for the initial seed, so a re-seed reproduces the freshly-built baseline.
    private readonly EnvironmentSpec? _environment;
    private readonly string _seedBaseDirectory;

    private bool _disposed;

    private SuiteTopology(
        HeadlessTopology inner,
        IReadOnlyDictionary<string, object> discoveredServices,
        IReadOnlyList<string> dependencyNames,
        EnvironmentSpec? environment,
        string seedBaseDirectory,
        IReadOnlyList<SecurityConfirmation> securityConfirmations,
        IReadOnlyList<EndpointSelectionNotice> endpointSelectionNotices,
        IReadOnlyList<EndpointTrustNotice> endpointTrustNotices)
    {
        _inner = inner;
        DiscoveredServices = discoveredServices;
        DependencyNames = dependencyNames;
        DependencyTypes = BuildDependencyTypeMap(environment);
        _environment = environment;
        _seedBaseDirectory = seedBaseDirectory;
        SecurityConfirmations = securityConfirmations;
        EndpointSelectionNotices = endpointSelectionNotices;
        EndpointTrustNotices = endpointTrustNotices;
    }

    /// <summary>
    /// Gets the declared-versus-observed record of REQ-005's secured-confirmation probe, one
    /// entry per declared <c>security</c> block, or an empty list when the suite declares none.
    /// </summary>
    /// <remarks>
    /// Reaching this property at all means every declared block was confirmed: a failure aborts
    /// <see cref="StartAsync"/> with an <see cref="OrchestrationException"/> before any
    /// <see cref="SuiteTopology"/> is constructed. It is surfaced so a run's own output can show
    /// what was ASSERTED and what was CONFIRMED — a boolean would let a suite that confirmed only
    /// the transport read identically to one that confirmed an authenticated round trip, and the
    /// difference between those two is the entire subject of REQ-005.
    /// </remarks>
    public IReadOnlyList<SecurityConfirmation> SecurityConfirmations { get; }

    /// <summary>
    /// Gets the author-facing notices the mapper raised while selecting endpoints — today, the
    /// transport downgrade a `project:`-form service declaring BOTH an http and an https endpoint
    /// incurs (#348). Empty for almost every suite.
    /// </summary>
    /// <remarks>
    /// Surfaced for the same reason, and printed through the same channel, as
    /// <see cref="SecurityConfirmations"/>: the decision is invisible in the step observation, and
    /// a run whose traffic silently went plaintext should say so. No EXISTING v1 event field could
    /// carry an advisory about a healthy run, so it travels on a NEW record instead:
    /// <c>Vouchfx.Engine.Runtime.TransportNoticeEvents</c> maps this collection onto the frozen
    /// <c>transport-notice</c> record and is its SINGLE producer. Read that type before writing
    /// any new emission — a second producer would double every advisory.
    /// </remarks>
    public IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices { get; }

    /// <summary>
    /// Gets the author-facing notices that a service's staged address resolves to an https
    /// listener the engine configures no client trust material for — whether that listener was
    /// named by the service's <c>endpoint:</c> or chosen by the engine's own rule. Empty for
    /// almost every suite.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="EndpointSelectionNotices"/> because the two records are different
    /// facts with different fields — one says the engine picked plaintext when TLS was on offer,
    /// the other that the address is TLS this engine contributed no trust of its own to.
    /// Surfaced and printed through the same channel, for the same reasons.
    /// </remarks>
    public IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices { get; }

    /// <summary>
    /// Gets the flat map of discovered service endpoints and managed-dependency connection strings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each entry is keyed by the logical resource name declared in the
    /// <see cref="EnvironmentSpec"/>:
    /// <list type="bullet">
    ///   <item>Image/project services → base URL string (e.g. <c>"http://localhost:12345"</c>),
    ///   resolved from the retained <c>EndpointReference.Url</c> after
    ///   <see cref="Aspire.Hosting.DistributedApplication.StartAsync"/> completes.</item>
    ///   <item>Managed dependencies (postgres, kafka) → connection string, resolved via
    ///   <see cref="IResourceWithConnectionString.GetConnectionStringAsync"/> on the retained
    ///   resource builder (§4 invariant: never <c>app.GetConnectionString(name)</c>).</item>
    /// </list>
    /// </para>
    /// <para>
    /// Values are typed as <see cref="object"/> to accommodate both string URLs and string
    /// connection strings in the same dictionary; callers should cast to <c>string</c>.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, object> DiscoveredServices { get; }

    /// <summary>
    /// Gets the logical names of the managed dependencies declared in the scenario's
    /// <c>environment.dependencies</c> section.
    /// </summary>
    /// <remarks>
    /// Used by the runner's variable-staging step to distinguish dependency
    /// connection-string entries (staged under <c>conn::&lt;name&gt;</c>) from
    /// service endpoint entries (staged under <c>svc::&lt;name&gt;</c>).
    /// </remarks>
    public IReadOnlyList<string> DependencyNames { get; }

    /// <summary>
    /// Gets the logical-dependency-name → declared-<c>type</c> map for every
    /// dependency declared in <c>environment.dependencies</c> (e.g. <c>"postgres"</c>,
    /// <c>"sqlserver"</c>, <c>"kafka"</c>).
    /// </summary>
    /// <remarks>
    /// Populated once, at construction, via the same <see cref="BuildDependencyTypeMap"/>
    /// helper <see cref="ApplySeedAsync"/> and <see cref="ReseedAsync"/> already use for
    /// seed dispatch — so this map can never disagree with the seed applier about a
    /// dependency's declared type. Consumed by <c>ScenarioIsolationFactory.Create</c> to
    /// dispatch each dependency to its reset implementation by name+type rather than by
    /// sniffing the shape of its discovered connection string. Empty when the scenario
    /// declares no dependencies.
    /// </remarks>
    public IReadOnlyDictionary<string, string> DependencyTypes { get; }

    /// <summary>
    /// Gets the underlying <see cref="DistributedApplication"/> instance.
    /// Exposed for advanced callers and test assertions; do not cache across topology lifetimes.
    /// </summary>
    public DistributedApplication Application => _inner.Application;

    /// <summary>
    /// Builds, starts, and health-gates the Aspire topology described by
    /// <paramref name="environment"/>, then resolves all endpoint URLs and connection strings
    /// into <see cref="DiscoveredServices"/>.
    /// </summary>
    /// <param name="environment">
    /// The parsed <c>environment</c> block from the <c>.e2e.yaml</c> file, or
    /// <see langword="null"/> for an empty topology (no resources, no health gates).
    /// </param>
    /// <param name="appHostAssemblyName">
    /// The short name of the assembly carrying the <c>dcpclipath</c> /
    /// <c>dcpextensionpaths</c> <see cref="System.Reflection.AssemblyMetadataAttribute"/>
    /// attributes embedded by the <c>Aspire.Hosting.AppHost</c> build targets (R-1 finding,
    /// CLAUDE.md §"Aspire (§4, §19)").  Pass <c>typeof(YourTestClass).Assembly.GetName().Name</c>
    /// from the test project that carries <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c>.
    /// </param>
    /// <param name="startupTimeout">
    /// Maximum time allowed for each health gate.  Defaults to 120 seconds.
    /// The same timeout is applied individually to each resource in
    /// <see cref="MappedTopology.HealthGateResourceNames"/>.
    /// </param>
    /// <param name="seedBaseDirectory">
    /// The SUITE directory — the base every relative path this method resolves is measured from.
    /// Despite the name it has TWO consumers, not one (Copilot, fix round seven: the doc named only
    /// the first, and an embedder who passed a seed-only directory would silently break artefact
    /// containment):
    /// <list type="bullet">
    ///   <item><description>
    ///     relative <c>environment.seed</c> SQL and fixture paths (S05-A-01) — its original job, and
    ///     retained on the instance so a per-scenario reset re-seeds from the same base;
    ///   </description></item>
    ///   <item><description>
    ///     <c>security.serverArtifacts[].source</c> on every declared service AND dependency
    ///     (REQ-016), since slice E — this value reaches <c>EnvironmentMapper.Map</c> as its
    ///     <c>suiteDirectory</c>, which is also the root the artefact CONTAINMENT check measures
    ///     escape from. A wrong value here therefore does not merely mis-resolve a path; it moves
    ///     the boundary deciding which paths are legal at all.
    ///   </description></item>
    /// </list>
    /// Deliberately NOT the base for the probe's own client material: <c>caCert</c>,
    /// <c>clientCert</c> and <c>clientKey</c> arrive already resolved on the
    /// <paramref name="securityConfiguration"/> accessor, which the caller builds against the
    /// SCENARIO's directory (#268). Defaults to
    /// <see cref="Directory.GetCurrentDirectory"/> when <see langword="null"/>.
    /// </param>
    /// <param name="securityConfiguration">
    /// The resolved client security configuration (REQ-014) REQ-005's probe presents when
    /// confirming each declared <c>security</c> block. <see langword="null"/> — the default — is
    /// correct for a suite that declares no <c>security</c> at all and for every embedding caller
    /// that predates this parameter.
    /// <para>
    /// <strong>A suite that DOES declare security must pass one, and this method now REFUSES to
    /// start without it.</strong> Silently substituting the Null accessor is how a caller that
    /// simply forgot the argument produced a run in which every secured suite failed closed blaming
    /// the author's certificates — <c>vouchfx watch</c> did exactly that, and no test could see it,
    /// because an omitted optional argument compiles and reads correctly. The check below turns the
    /// omission into a loud engine fault naming the parameter, at the first suite that declares
    /// security.
    /// </para>
    /// </param>
    /// <param name="kafkaSpeakingTargets">
    /// The declared target names the suite's own steps address with <c>mq-publish.kafka</c> /
    /// <c>mq-expect.kafka</c>, as computed by <see cref="SuiteProtocolTargets"/>.
    /// <para>
    /// It decides TWO things, from one inference, so the two cannot disagree. They earn REQ-005's
    /// application-layer round trip even when declared as a SERVICE — the shape REQ-011 exists for.
    /// And (REQ-023, amended 2026-08-04) a SERVICE in this set is staged at <c>svc::&lt;name&gt;</c>
    /// as the bare <c>host:port</c> bootstrap authority a Kafka client consumes, rather than the
    /// scheme-carrying URL the HTTP family consumes — the value a step reads, not merely the level
    /// a confirmation claims.
    /// </para>
    /// <see langword="null"/> means "no Kafka step targets anything here", which
    /// is correct for a suite with no Kafka steps and for every caller that predates this parameter.
    /// </param>
    /// <param name="endpointConsumingTargets">
    /// The declared target names at least one step will read a STAGED ENDPOINT for, as computed by
    /// <see cref="SuiteProtocolTargets.EndpointConsuming(IEnumerable{Vouchfx.Engine.Authoring.Ast.ScenarioAst})"/>
    /// — a SUPERSET of <paramref name="kafkaSpeakingTargets"/>. Passed straight through to
    /// <see cref="EnvironmentMapper.Map"/>, which consults it for one decision only (#348):
    /// whether an endpoint-less <c>project:</c>-form service is a refused authoring fault or a
    /// legitimate, unstaged worker service.
    /// <para>
    /// <strong>DERIVE IT FROM THE SAME SCENARIOS AS <paramref name="kafkaSpeakingTargets"/>, at the
    /// same call site.</strong> The two are computed from one input by every production caller;
    /// deriving them from different scenario sets would let them disagree about what the suite
    /// addresses. There is no compiler help here — this project sets no
    /// <c>GenerateDocumentationFile</c>, and both parameters are optional — so a call site that
    /// passes one and forgets the other compiles silently and simply never refuses. The pairing is
    /// pinned instead by
    /// <c>SuiteProtocolTargetsTests.EverySuiteTopologyStartCallSite_PassesBothTargetSets</c>.
    /// </para>
    /// <see langword="null"/> is the PERMISSIVE default — no refusal — matching
    /// <see cref="EnvironmentMapper.Map"/>'s own.
    /// </param>
    /// <param name="cancellationToken">
    /// Propagated to <see cref="HeadlessTopology.StartAsync"/>, to each
    /// health-gate <c>WaitForResourceHealthyAsync</c> call, to REQ-005's probe, and to the seed
    /// applier.  Must be the last parameter (CA1068).
    /// </param>
    /// <returns>
    /// A fully started and health-gated <see cref="SuiteTopology"/> whose
    /// <see cref="DiscoveredServices"/> map is populated.
    /// </returns>
    /// <exception cref="OrchestrationException">
    /// Thrown (instead of the raw Aspire exception) when the topology fails to start,
    /// when a health gate times out or the resource enters a terminal unhealthy state,
    /// when service discovery fails, or when a declared <c>security</c> block cannot be confirmed
    /// (REQ-005; <c>Info.Kind</c> is <see cref="OrchestrationErrorKind.SecurityConfirmation"/>).
    /// The <see cref="OrchestrationException.Info"/>
    /// property carries the structured diagnosis (kind, registry host, auth status, detail).
    /// This is always an Environment error (§12.1) — never a test Fail.
    /// </exception>
    public static async Task<SuiteTopology> StartAsync(
        EnvironmentSpec? environment,
        string? appHostAssemblyName,
        TimeSpan? startupTimeout = null,
        string? seedBaseDirectory = null,
        ISecurityConfigurationAccessor? securityConfiguration = null,
        IReadOnlySet<string>? kafkaSpeakingTargets = null,
        // Ahead of cancellationToken, not after it: CA1068 requires the token to be last on an
        // externally-visible method. Verified before inserting — every call site in src/ and
        // tests/ passes `cancellationToken:` by name, and a caller that passed it positionally
        // would fail to compile rather than mis-bind (IReadOnlySet<string> and CancellationToken
        // do not convert), so the insertion cannot silently change any call's meaning. Same
        // reasoning, same wording, as RunScenarioAgainstKeptTopologyAsync's own note.
        IReadOnlySet<string>? endpointConsumingTargets = null,
        CancellationToken cancellationToken = default)
    {
        var gateTimeout = startupTimeout ?? TimeSpan.FromSeconds(120);

        // ----------------------------------------------------------------
        // Step 0: the guard that makes an omitted accessor impossible to ship silently.
        //
        // This runs BEFORE any container work, and it is deliberately a check on the DECLARATION
        // rather than a required parameter. Making the parameter non-optional would force ~60
        // pre-existing call sites — every store's Docker test — to pass
        // NullSecurityConfigurationAccessor.Instance explicitly, asserting something the
        // environment they hand in already states (they declare no security at all). That is
        // ceremony carrying no information, and ceremony is copied without reading. The invariant
        // that actually matters is narrower and is stated here directly: a suite that declares
        // security must be handed the accessor for it.
        // ----------------------------------------------------------------
        if (securityConfiguration is null && DeclaresSecurity(environment))
        {
            throw new OrchestrationException(
                new OrchestrationErrorInfo(
                    Kind: OrchestrationErrorKind.SecurityConfirmation,
                    ResourceName: "startup",
                    RegistryHost: null,
                    AuthStatus: null,
                    Detail: "this suite declares a 'security' block, but the caller started the "
                        + "topology without a resolved security configuration "
                        + $"('{nameof(securityConfiguration)}' was null). REQ-005's probe presents the "
                        + "same material a step will, and with no accessor it can present nothing: "
                        + "every 'mtls' target would fail closed blaming the author's certificates "
                        + "for an engine fault. This is a defect in the calling host, not in the "
                        + "suite."));
        }

        // ----------------------------------------------------------------
        // Step 0b: EDGE-012 — every PINNED host port must be free before any container starts.
        //
        // Pinning removes the orchestrator's freedom to route around a busy port, so it introduces
        // a failure mode the unpinned path does not have. MEASURED, before this check existed, on
        // a suite pinning a port the test process was holding: the run did not fail — it HUNG,
        // consuming its entire health-gate budget and ending after 3m15s with
        // `OperationCanceledException: Resource '<service>' failed to become healthy before the
        // operation was cancelled`. That message names the service and nothing else: not the port,
        // not the collision, not even that a port was involved. An author reading it would look at
        // their image, their health check and their container's logs — everything except the one
        // line that caused it.
        //
        // So the check is here, ahead of every container: bind each pinned port for an instant and
        // release it. A port this host cannot bind is one the orchestrator cannot publish on, and
        // saying so in a sentence costs milliseconds where discovering it costs the whole budget.
        //
        // WHAT THIS IS NOT: a reservation. The port is free when released and could be taken in the
        // window before DCP binds it, in which case the run fails the slow way again. That race is
        // not closable from here — only the orchestrator can hold a port it has not yet bound — and
        // a check that closes the overwhelmingly common case (something else on this machine is
        // already listening) is worth having without it. The alternative reading, that a
        // best-effort check is worthless, would leave the measured 3m15s hang in place.
        //
        // NEVER a silent fallback to a different port: that would resurrect the exact
        // unreachable-advertised-address defect REQ-025 exists to remove, because the broker's own
        // configuration has already been written with the pinned number in it.
        //
        // The call itself sits AFTER Map, below — see that site for why.

        // ----------------------------------------------------------------
        // Step 1: Map the EnvironmentSpec → Configure callback + gate list + resolver.
        // EnvironmentMapper.Map reads the process environment for ${env:...} references
        // (REQ-006) but performs no container/network I/O, so exceptions here are still
        // ArgumentExceptions, not OrchestrationExceptions — let them propagate as-is to
        // the caller.
        // ----------------------------------------------------------------
        // seedBaseDirectory is resolved BEFORE Map (rather than at its own use site further down)
        // because REQ-016's server-artefact paths resolve against the same base, and the two must
        // not be able to disagree: one directory, computed once, used by both.
        var resolvedSeedBaseDirectory = seedBaseDirectory ?? Directory.GetCurrentDirectory();

        // kafkaSpeakingTargets reaches Map as well as the probe (REQ-023, amended): it decides the
        // FORM a service endpoint is staged in, not only the confirmation level the probe claims.
        // One set, computed once by the caller from the suite's own steps, so the value a Kafka
        // step reads and the level the probe reports can never disagree about what a target speaks.
        var kafkaTargets = kafkaSpeakingTargets ?? EmptyTargets;

        // #348: the superset — every target some step reads a staged endpoint for — reaches Map
        // only. Its sole decision there is whether an endpoint-less project-form service is a
        // refused authoring fault or an untargeted worker service; the probe has no use for it.
        var endpointTargets = endpointConsumingTargets ?? EmptyTargets;
        var mapped = EnvironmentMapper.Map(
            environment, resolvedSeedBaseDirectory, kafkaTargets, endpointTargets);

        // EDGE-012's bind pre-flight, AFTER Map's eager validation and before any container. The
        // order matters to the author, not to the outcome: a document that is invalid for a plain
        // declarative reason — including a pinned port that clashes with a sibling `httpPort` —
        // should be told THAT, not told about a port collision it also happens to have.
        EnsurePinnedHostPortsAreFree(environment);

        // ----------------------------------------------------------------
        // Step 2: Start the headless Aspire host.
        // HeadlessTopology.StartAsync already disposes itself on failure and re-throws;
        // we classify that exception as OrchestrationException so callers receive a typed
        // Environment-error signal (§12.1) rather than a raw Aspire exception. The ONE
        // exemption is TopologyAuthoringException — see its own catch clause below.
        // ----------------------------------------------------------------
        HeadlessTopology topology;
        try
        {
            topology = await HeadlessTopology.StartAsync(
                appHostAssemblyName: appHostAssemblyName,
                configureResources: mapped.Configure,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OrchestrationException)
        {
            // Already classified — propagate as-is.
            throw;
        }
        catch (TopologyAuthoringException)
        {
            // An AUTHORING fault raised from inside Map's Configure closure — the only class of
            // fault that legitimately escapes there. Its members share one property: the fault is
            // the author's to fix but is only decidable once Aspire has built the resource
            // (#348's endpoint-less `project:` service is the original). Grep
            // `throw new TopologyAuthoringException` for the current set rather than trusting a
            // roster here; the roster this comment used to carry went stale.
            //
            // Propagate UNWRAPPED so it reaches ScenarioRunner's ArgumentException catch and is
            // classified Inconclusive, never EnvironmentError: reporting an authoring fault as an
            // infrastructure fault is the one direction §12.1 must not bend, and it also decides
            // the exit code — an EnvironmentError that executed nothing exits 0 (#390) while an
            // Inconclusive that executed nothing does not (#369).
            //
            // DELIBERATELY NARROW. The blanket wrap below stays exactly as it was for every other
            // exception escaping the closure — Map's defensive internal-error throws, Aspire's
            // own FormatException when it materialises a malformed env value, DCP, Docker — all
            // of which are genuine engine/infrastructure faults and belong in EnvironmentError.
            // Nothing has started at this point either way: Configure runs before builder.Build().
            throw;
        }
        catch (Exception ex)
        {
            // HeadlessTopology already disposed itself; classify and wrap.
            var info = OrchestrationErrorClassifier.Classify(
                ex, imageRef: null, resourceName: "startup");
            throw new OrchestrationException(info, ex);
        }

        // HeadlessTopology.StartAsync returned successfully.  Everything below runs
        // inside a try/catch that disposes the topology on failure so containers do not leak.
        try
        {
            var app = topology.Application;

            // ----------------------------------------------------------------
            // Step 3: Health-gate each resource in order (most-specific first — §4 invariant).
            // Each gate gets an independent, linked CancellationTokenSource with the per-gate
            // bounded timeout so a single stalled container does not block indefinitely.
            //
            // WaitBehavior.StopOnResourceUnavailable: if the resource enters FailedToStart,
            // Exited, or RuntimeUnhealthy, the gate throws immediately rather than hanging —
            // desired behaviour for the Environment-error classification path (§12.1).
            // ----------------------------------------------------------------
            foreach (var resourceName in mapped.HealthGateResourceNames)
            {
                try
                {
                    using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    gateCts.CancelAfter(gateTimeout);

                    await app.ResourceNotifications
                        .WaitForResourceHealthyAsync(
                            resourceName,
                            WaitBehavior.StopOnResourceUnavailable,
                            gateCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OrchestrationException)
                {
                    // Already classified — propagate as-is (will be caught by outer try/catch).
                    throw;
                }
                catch (Exception ex)
                {
                    // Classify the gate failure as an Environment error (§12.1).
                    // imageRef is null here because health-gate failures are not image-pull failures;
                    // the classifier uses the message heuristics (timeout / unhealthy / cancelled).
                    var info = OrchestrationErrorClassifier.Classify(
                        ex, imageRef: null, resourceName: resourceName);
                    throw new OrchestrationException(info, ex);
                }
            }

            // ----------------------------------------------------------------
            // Step 4: Resolve endpoints and connection strings from the running topology.
            // ResolveServices reads the retained EndpointReference.Url values and calls
            // IResourceWithConnectionString.GetConnectionStringAsync on retained builders.
            // §4 invariant: never use app.GetConnectionString(name) — absent in Aspire 13.4.2.
            // ----------------------------------------------------------------
            IReadOnlyDictionary<string, object> discoveredServices;
            try
            {
                discoveredServices = await mapped.ResolveServices(app, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OrchestrationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var info = OrchestrationErrorClassifier.Classify(
                    ex, imageRef: null, resourceName: "discovery");
                throw new OrchestrationException(info, ex);
            }

            // ----------------------------------------------------------------
            // Step 4½: Apply declarative seed data — AFTER discovery, BEFORE the
            // fixture is returned, INSIDE this try/catch (§3.2.5, S05-A-01).
            // SeedApplier dispatches each seeded dependency on its declared type:
            // 'sql' is the only seed kind in the v1 language (see SeedSpec.cs's
            // header remarks) and is applied now against a relational dependency
            // (postgres, sqlserver, or mysql) — a sql seed on any OTHER
            // dependency type is rejected as a type mismatch; every other
            // dependency type has no seed path at all. Throws
            // OrchestrationException (Provision kind) on any failure; the outer
            // catch below disposes the topology so containers do not leak, and
            // the failure surfaces as an Environment error (§12.1) — never a
            // misattributed assertion Fail.
            //
            // Multi-scenario note: this seeding runs ONCE at suite startup.  For
            // a SINGLE-scenario run the seeded rows are present for step 1.  For
            // a MULTI-scenario suite sharing one topology, the per-store isolation
            // (RespawnRelationalIsolation for postgres/sqlserver/mysql,
            // MongoScenarioIsolation, RedisScenarioIsolation, or
            // ElasticsearchScenarioIsolation) clears data between scenarios
            // (structure — tables, indexes, mappings — persists), so seeded
            // reference rows are also cleared after the first scenario. Persisting
            // reference data across scenarios is a future enhancement, OUT OF SCOPE
            // for A-01.
            // ----------------------------------------------------------------
            // ----------------------------------------------------------------
            // Step 4¾: REQ-005's fail-closed secured-confirmation probe — after the topology is
            // health-gated and discovered, BEFORE the seed and therefore before any step.
            //
            // Placed ahead of the seed deliberately: a suite that cannot have its declared
            // security confirmed should not first spend time writing rows into a database it is
            // about to abandon, and the two failures must stay distinguishable — a seed failure
            // is an ordinary environment error that still exits 0 by default, whereas this one is
            // REQ-018's carve-out and exits non-zero unconditionally.
            //
            // NOT an Aspire health check, and that separation is the point (REQ-005): a container
            // health check cannot present a client certificate, so no health-gating mechanism can
            // confirm a mutual-TLS listener — only that something accepted a socket. Health-gate
            // the container, then probe the security.
            //
            // Inside this try, so the outer catch disposes the topology on failure and containers
            // do not leak — the same guarantee every other stage here has.
            // ----------------------------------------------------------------
            var securityConfirmations = await SecuredEndpointProbe.ConfirmAsync(
                    environment,
                    discoveredServices,
                    securityConfiguration ?? NullSecurityConfigurationAccessor.Instance,
                    kafkaTargets,
                    perTargetTimeout: gateTimeout,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await ApplySeedAsync(
                    environment,
                    discoveredServices,
                    resolvedSeedBaseDirectory,
                    cancellationToken)
                .ConfigureAwait(false);

            return new SuiteTopology(
                topology,
                discoveredServices,
                mapped.DependencyNames,
                environment,
                resolvedSeedBaseDirectory,
                securityConfirmations,
                mapped.EndpointSelectionNotices,
                mapped.EndpointTrustNotices);
        }
        catch
        {
            // Any health-gate or discovery failure: dispose the already-started topology
            // so containers started by HeadlessTopology.StartAsync do not leak.
            await topology.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Returns this <strong>already-running</strong> topology's seeded dependencies to
    /// the freshly-built-and-seeded baseline (S08-T10, watch-mode reuse path).  For seeded
    /// Postgres dependencies, each database's <c>public</c> schema is reset to empty, then
    /// the declarative <c>environment.seed</c> is re-applied — exactly reproducing the
    /// fresh-container-then-seed sequence a plain <c>vouchfx run</c> performs. Non-Postgres
    /// stores (including a seeded sqlserver/mysql dependency — 'sql' applies to all three
    /// relational kinds, but this schema-reset step is currently Postgres-only) are skipped,
    /// and the caller's preceding isolation reset has already cleared their data.
    /// </summary>
    /// <param name="cancellationToken">Propagated to the schema reset and the seed applier.</param>
    /// <returns>A task that completes once the seed has been re-applied (a no-op when the
    /// scenario declares no seed).</returns>
    /// <exception cref="OrchestrationException">
    /// Thrown (Provision kind) when the schema reset or re-seed fails — always an Environment
    /// error (§12.1), never a test <c>Fail</c>.  Unlike the initial seed in
    /// <see cref="StartAsync"/>, the topology is <em>not</em> disposed here: the kept topology
    /// survives a re-seed failure so the watch loop can report it and continue (the caller owns
    /// the topology lifetime).
    /// </exception>
    /// <remarks>
    /// <para>
    /// Watch mode keeps ONE topology alive across re-runs.  Between reuse runs the kept topology
    /// carries the previous run's writes. A plain <c>vouchfx run</c> always starts from a fresh,
    /// EMPTY container and then applies the seed — so the seed SQL is written assuming an empty
    /// database (bare <c>CREATE TABLE</c>, not <c>CREATE TABLE IF NOT EXISTS</c>).  To match that
    /// initial state without rebuilding the container:
    /// </para>
    /// <para>
    /// For <strong>Postgres</strong> dependencies, this method first DROPS the <c>public</c> schema
    /// of each seeded database and recreates it empty (clearing both the prior run's writes and the
    /// prior seed's tables), then re-applies the seed against the now-empty schema — so the
    /// author's non-idempotent seed SQL re-runs cleanly, exactly as on a fresh build. This is
    /// necessary because Respawn preserves the schema (keeping the seed's tables), so a re-applied
    /// <c>CREATE TABLE</c> would collide.
    /// </para>
    /// <para>
    /// <strong>Non-Postgres stores</strong> (SQL Server, MySQL, MongoDB, Redis, Elasticsearch)
    /// are skipped here: the preceding per-store isolation reset has already cleared their data
    /// (real deletes — Respawn, <c>DeleteMany</c>, <c>FLUSHDB</c>, <c>_delete_by_query</c>), and
    /// there is no row-applied non-Postgres seed state to restore — document-store and broker
    /// seeds are content-recorded via deferred seams (no actual write in M2), and Redis has no
    /// seed path at all.
    /// </para>
    /// </remarks>
    public async Task ReseedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Nothing to restore when the scenario declares no seed: leave the topology untouched
        // (the caller's Respawn reset has already cleared the prior run's row-level writes).
        var seed = _environment?.Seed;
        if (seed is null || seed.Dependencies.Count == 0)
        {
            return;
        }

        // Return each seeded Postgres dependency's public schema to EMPTY before re-seeding, so
        // the seed's bare CREATE TABLE re-runs cleanly (mirrors a fresh container).  Only seeded
        // dependencies are reset — an unseeded dependency is left exactly as the run left it.
        var dependencyTypes = BuildDependencyTypeMap(_environment);
        foreach (var dependencyName in seed.Dependencies.Keys)
        {
            // Case-sensitive (Ordinal) — pre-GA decision, feat/case-sensitive-kinds: a
            // declaredType reaching here has already passed EnvironmentMapper.Map's eager,
            // case-sensitive validation, so it is always the exact-case canonical spelling.
            if (!dependencyTypes.TryGetValue(dependencyName, out var declaredType) ||
                !string.Equals(declaredType, "postgres", StringComparison.Ordinal))
            {
                // Non-Postgres (or undeclared) seed dependency: SeedApplier validates/dispatches
                // it; no schema reset happens here. A seeded sqlserver/mysql dependency's rows
                // are therefore NOT reset by this method before ApplySeedAsync below re-applies
                // its (non-idempotent) seed SQL — this schema-reset step is currently
                // Postgres-only, unlike SeedApplier's own 'sql' dispatch, which applies to all
                // three relational kinds (pre-existing scope gap, not touched here).
                continue;
            }

            if (DiscoveredServices.TryGetValue(dependencyName, out var value) &&
                value is string connectionString &&
                !string.IsNullOrWhiteSpace(connectionString))
            {
                await ResetPostgresSchemaAsync(dependencyName, connectionString, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await ApplySeedAsync(
                _environment,
                DiscoveredServices,
                _seedBaseDirectory,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resets a Postgres database's <c>public</c> schema to empty — <c>DROP SCHEMA public CASCADE;
    /// CREATE SCHEMA public;</c> — so the kept topology's database matches a freshly-built
    /// container before the seed is re-applied (S08-T10).  Any failure is wrapped as an
    /// <see cref="OrchestrationException"/> (Provision kind, §12.1: Environment error, never a
    /// test Fail).
    /// </summary>
    private static async Task ResetPostgresSchemaAsync(
        string dependencyName,
        string connectionString,
        CancellationToken cancellationToken)
    {
        var connection = new Npgsql.NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var command = connection.CreateCommand();
            try
            {
                // DROP … CASCADE removes every table/sequence/type the prior run + prior seed
                // created; CREATE SCHEMA restores an empty `public` schema (Postgres DDL is
                // transactional, so this is atomic).  Equivalent to a fresh container's DB.
                command.CommandText = "DROP SCHEMA public CASCADE; CREATE SCHEMA public;";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await command.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var info = new OrchestrationErrorInfo(
                Kind: OrchestrationErrorKind.Provision,
                ResourceName: dependencyName,
                RegistryHost: null,
                AuthStatus: null,
                Detail: $"watch re-seed could not reset the '{dependencyName}' schema: "
                        + TrimDetail(ex.Message));
            throw new OrchestrationException(info, ex);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns a trimmed, single-line summary of <paramref name="message"/> capped at 200
    /// characters for display in event streams and logs.
    /// </summary>
    private static string TrimDetail(string message)
    {
        var collapsed = (message ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return collapsed.Length > 200 ? collapsed[..200] : collapsed;
    }

    /// <summary>
    /// Applies the scenario's declarative <c>environment.seed</c> against the discovered
    /// services.  The single seed-apply path shared by the initial seed in
    /// <see cref="StartAsync"/> and the re-seed in <see cref="ReseedAsync"/>, so the two can
    /// never diverge in how they map dependency types or resolve seed file paths (S08-T10).
    /// </summary>
    private static Task ApplySeedAsync(
        EnvironmentSpec? environment,
        IReadOnlyDictionary<string, object> discoveredServices,
        string seedBaseDirectory,
        CancellationToken cancellationToken)
    {
        var dependencyTypes = BuildDependencyTypeMap(environment);
        return SeedApplier.ApplyAsync(
            environment?.Seed,
            discoveredServices,
            dependencyTypes,
            seedBaseDirectory,
            cancellationToken);
    }

    /// <summary>
    /// Builds the logical-dependency-name → declared-<c>type</c> map the seed
    /// applier dispatches on (S05-A-02).  Empty when no dependencies are declared.
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildDependencyTypeMap(EnvironmentSpec? environment)
    {
        var dependencies = environment?.Dependencies;
        if (dependencies is null || dependencies.Count == 0)
        {
            return EmptyDependencyTypes;
        }

        var map = new Dictionary<string, string>(dependencies.Count, StringComparer.Ordinal);
        foreach (var (name, spec) in dependencies)
        {
            map[name] = spec.Type;
        }

        return map;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyDependencyTypes =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// True when any declared service or dependency carries a <c>security</c> block — the
    /// condition under which <see cref="StartAsync"/> refuses to run without a resolved security
    /// configuration.
    /// </summary>
    /// <remarks>
    /// Reads the same declaration the probe walks — literally the same code (m5, fix round three):
    /// both are <see cref="SecuredTargets"/>, so the guard fires for exactly the suites the probe
    /// would otherwise probe with nothing in hand, and that agreement is now structural rather
    /// than asserted in prose.
    /// </remarks>
    private static bool DeclaresSecurity(EnvironmentSpec? environment) =>
        SecuredTargets.Any(environment);

    /// <summary>
    /// Fails the run (EDGE-012) when a service pins a host port this machine cannot bind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two stages, and the first exists because the second structurally cannot do its job alone:
    /// a suite-wide claim map refuses two entries pinning one host port (which a probe that binds
    /// and releases each pin in turn can never see), then every claim is probed by BINDING it, on
    /// an OS-dependent set of addresses, holding each listener until all are proved and releasing
    /// them together. <see cref="TryHold"/> carries the measurements behind that address set — the
    /// probe is deliberately STRICTER than a single wildcard bind, because a wildcard-only probe
    /// passes a port a squatter already holds on loopback.
    /// </para>
    /// <para>
    /// It runs AFTER <c>EnvironmentMapper.Map</c>'s eager validation, not before. Both orders
    /// reject the same suites; this one rejects them in the right order, so a document invalid for
    /// a plain declarative reason is told that, rather than being told about a port collision it
    /// also happens to have.
    /// </para>
    /// <para>
    /// <strong>Scope, stated because it is a real limit rather than an oversight.</strong> This is
    /// a topology-build-time check, so <c>vouchfx validate</c> — which never builds a topology —
    /// accepts a suite whose pins collide ACROSS services, and the author learns at <c>run</c>.
    /// Within a single service the parser catches it at validation, because the whole list is in
    /// front of it there. Moving the cross-service case earlier would need a validation stage that
    /// reasons about the suite as a whole rather than a document at a time — a larger change than
    /// this requirement warrants, and a deliberate deferral rather than a gap nobody noticed.
    /// </para>
    /// </remarks>
    internal static void EnsurePinnedHostPortsAreFree(EnvironmentSpec? environment)
    {
        if (environment?.Services is not { Count: > 0 } services)
        {
            return;
        }

        // ── 1. One SUITE-WIDE claim per host port, before any probing ─────────────────────────
        // A per-service loop cannot see two services pinning one port, and — because a probe that
        // binds and RELEASES cannot see its own siblings either — nor could the bind loop below on
        // its own. So the claims are collected first and duplicates refused by name. This is the
        // one collision class the author causes themselves, and the only one they can fix by
        // reading a sentence.
        var claims = new Dictionary<int, (string Service, int ContainerPort)>();

        foreach (var (serviceName, spec) in services)
        {
            if (spec.PinnedHostPorts is not { Count: > 0 } pins)
            {
                continue;
            }

            foreach (var (containerPort, hostPort) in pins)
            {
                if (claims.TryGetValue(hostPort, out var existing))
                {
                    throw Collision(
                        serviceName,
                        $"host port {hostPort} is pinned twice in this suite: by "
                        + $"'{existing.Service}' for container port {existing.ContainerPort}, and by "
                        + $"'{serviceName}' for container port {containerPort}. One host port "
                        + "publishes one container port. Give them different host ports.");
                }

                claims[hostPort] = (serviceName, containerPort);
            }
        }

        if (claims.Count == 0)
        {
            return;
        }

        // ── 2. Probe every claim, HOLDING each listener until all are probed ──────────────────
        // Holding rather than releasing as we go is what keeps the checks independent: a released
        // probe tells the next one nothing, and the window between the last release and the
        // orchestrator's own bind is the whole set's rather than one pin's.
        var held = new List<System.Net.Sockets.TcpListener>();
        try
        {
            foreach (var (hostPort, claim) in claims)
            {
                if (TryHold(hostPort, held, out var address, out var socketError))
                {
                    continue;
                }

                throw Collision(claim.Service, DescribeCollision(claim, hostPort, address, socketError));
            }
        }
        finally
        {
            foreach (var listener in held)
            {
                listener.Stop();
            }
        }
    }

    /// <summary>Builds the EDGE-012 environment error for one unusable pinned port.</summary>
    private static OrchestrationException Collision(string serviceName, string detail) =>
        new(new OrchestrationErrorInfo(
            Kind: OrchestrationErrorKind.Provision,
            ResourceName: serviceName,
            RegistryHost: null,
            AuthStatus: null,
            Detail: detail
                + " A pinned port cannot be substituted — the point of pinning is that something "
                + "else, such as a broker's advertised address, already names this number — so the "
                + "run stops here rather than starting a container that would be unreachable."));

    /// <summary>
    /// Explains one failed probe, naming a CAUSE only where the socket error establishes one.
    /// </summary>
    /// <remarks>
    /// The previous wording asserted "another process is almost certainly listening" for every
    /// failure. MEASURED on this host, that is false for a whole class: Windows reserves TCP
    /// ranges (<c>netsh interface ipv4 show excludedportrange protocol=tcp</c>) in which a bind
    /// returns <c>AccessDenied</c> with NOTHING listening. Telling an author to find and stop a
    /// process that does not exist is worse than telling them nothing.
    /// </remarks>
    private static string DescribeCollision(
        (string Service, int ContainerPort) claim,
        int hostPort,
        string address,
        System.Net.Sockets.SocketError socketError)
    {
        var opening = $"service '{claim.Service}' pins host port {hostPort} (publishing container "
            + $"port {claim.ContainerPort}), but this machine cannot bind it on {address}: "
            + $"{socketError}.";

        return socketError switch
        {
            System.Net.Sockets.SocketError.AddressAlreadyInUse =>
                opening + $" Another process is listening on {hostPort}; stop it, or pin a "
                + "different port and update whatever names it.",

            System.Net.Sockets.SocketError.AccessDenied =>
                opening + $" The port is reserved or privileged rather than in use — on Windows, "
                + "`netsh interface ipv4 show excludedportrange protocol=tcp` lists the reserved "
                + "ranges; on Linux, ports below 1024 need elevation. Pin a port outside them.",

            _ => opening + " Pin a different host port.",
        };
    }

    /// <summary>
    /// Binds <paramref name="port"/> on every address a client could reach it through, adding the
    /// listeners to <paramref name="held"/> so they stay bound until the caller releases them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Probing only the any-address is not enough, and the reason is a FALSE PASS rather
    /// than a missed diagnostic.</strong> Measured on Windows, with a process holding
    /// <c>127.0.0.1:P</c>, binding <c>0.0.0.0:P</c> SUCCEEDS — so a wildcard-only check reports the
    /// port free, the container publishes, and a client connecting to <c>localhost:P</c> reaches
    /// the squatter instead of the container: a step transacting against an unrelated local
    /// process and passing. The loopback halves matter specifically because
    /// <c>Dns.GetHostAddresses("localhost")</c> returns <c>::1</c> first on that host, so a
    /// squatter there intercepts exactly the connection a client makes.
    /// </para>
    /// <para>
    /// <strong>THE ADDRESS SET IS OS-CONDITIONAL, and that is not a portability nicety — a fixed
    /// four-address set is Linux-fatal.</strong> On Linux/BSD a held wildcard bind CONFLICTS with
    /// its own specific-address sibling; on Windows it does not. Measured on Debian 12 / .NET 8, on
    /// a port nothing held: <c>0.0.0.0</c> bound OK and <c>127.0.0.1</c> then failed
    /// <c>AddressAlreadyInUse</c> — against this check's OWN listener. Every pinned suite would
    /// have failed there, blaming a process that does not exist.
    /// </para>
    /// <para>
    /// So each platform gets the smallest set that is correct on it, measured on both:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <strong>Non-Windows — <c>{0.0.0.0, [::]}</c>.</strong> They coexist, and each still
    ///     catches a specific-address squatter in its own family precisely BECAUSE of the conflict
    ///     rule above: measured on Debian, a <c>127.0.0.1</c> squatter fails the <c>0.0.0.0</c>
    ///     bind and a <c>[::1]</c> squatter fails the <c>[::]</c> bind. All three squatter cases
    ///     detected, free port accepted.
    ///   </description></item>
    ///   <item><description>
    ///     <strong>Windows — all four.</strong> The wildcard does not conflict with a specific
    ///     bind there, so the two-address set MISSES both loopback squatters (measured), which is
    ///     the false pass this check exists to close.
    ///   </description></item>
    /// </list>
    /// <para>
    /// macOS is INFERRED, not measured: BSD shares Linux's wildcard-conflict rule, so it takes the
    /// non-Windows branch. If that inference is wrong the symptom is the Linux one — a free port
    /// refused — and it would be caught by
    /// <c>PinnedHostPortPreflightTests.FreePort_IsAccepted</c> on the first CI run on such a host.
    /// </para>
    /// <para>
    /// <c>SO_REUSEADDR</c> on every probe was considered and REJECTED. It makes the clean case pass
    /// on Linux without an OS branch, but it reintroduces the false pass: it lets the probe bind
    /// beside any squatter that also set <c>SO_REUSEADDR</c>, which is the standard listening idiom
    /// in Go, nginx and most server frameworks — precisely the processes most likely to be holding
    /// the port.
    /// </para>
    /// <para>
    /// The IPv6 entries are skipped where the OS has no IPv6, rather than failing a run for a stack
    /// that cannot host the collision being probed for.
    /// </para>
    /// </remarks>
    private static bool TryHold(
        int port,
        List<System.Net.Sockets.TcpListener> held,
        out string address,
        out System.Net.Sockets.SocketError socketError)
    {
        var addresses = new List<System.Net.IPAddress> { System.Net.IPAddress.Any };

        // Windows only: the specific-address probes are both necessary (the wildcard misses a
        // loopback squatter) and possible (the wildcard does not conflict with them). Everywhere
        // else they are neither.
        if (OperatingSystem.IsWindows())
        {
            addresses.Add(System.Net.IPAddress.Loopback);
        }

        if (System.Net.Sockets.Socket.OSSupportsIPv6)
        {
            addresses.Add(System.Net.IPAddress.IPv6Any);

            if (OperatingSystem.IsWindows())
            {
                addresses.Add(System.Net.IPAddress.IPv6Loopback);
            }
        }

        foreach (var candidate in addresses)
        {
            // The constructor validates the port, so it is INSIDE the try: PinnedHostPorts is a
            // public init-only property, so a programmatic caller (the SDK's testing doubles, an
            // in-tree stand-in) can hand over a value the parser would have refused, and an
            // ArgumentOutOfRangeException escaping here would leave StartAsync throwing something
            // that is not an OrchestrationException — therefore not the environment error this
            // requirement is about.
            System.Net.Sockets.TcpListener? listener = null;
            try
            {
                listener = new System.Net.Sockets.TcpListener(candidate, port);
                listener.Start();
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                // Disposed explicitly on the ABORTING path. TcpListener.Start calls Stop for you
                // only when Listen throws — a Bind failure, which is every case here, leaves the
                // socket for the finaliser. The held listeners take the other route: they are
                // released deterministically by the caller's finally.
                listener?.Dispose();
                address = Describe(candidate);
                socketError = ex.SocketErrorCode;
                return false;
            }
            catch (ArgumentOutOfRangeException)
            {
                listener?.Dispose();
                address = Describe(candidate);
                socketError = System.Net.Sockets.SocketError.InvalidArgument;
                return false;
            }

            held.Add(listener);
        }

        address = string.Empty;
        socketError = System.Net.Sockets.SocketError.Success;
        return true;

        static string Describe(System.Net.IPAddress candidate) =>
            candidate.Equals(System.Net.IPAddress.Any) ? "0.0.0.0"
            : candidate.Equals(System.Net.IPAddress.Loopback) ? "127.0.0.1"
            : candidate.Equals(System.Net.IPAddress.IPv6Any) ? "[::]"
            : "[::1]";
    }

    /// <summary>
    /// Disposes the inner <see cref="HeadlessTopology"/>, stopping all managed containers
    /// and releasing all Aspire resources.  Idempotent — safe to call multiple times.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
