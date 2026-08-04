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
        IReadOnlyList<SecurityConfirmation> securityConfirmations)
    {
        _inner = inner;
        DiscoveredServices = discoveredServices;
        DependencyNames = dependencyNames;
        DependencyTypes = BuildDependencyTypeMap(environment);
        _environment = environment;
        _seedBaseDirectory = seedBaseDirectory;
        SecurityConfirmations = securityConfirmations;
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
    /// The base directory against which relative <c>environment.seed</c> SQL file
    /// paths are resolved (S05-A-01).  Defaults to
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
    /// They earn REQ-005's application-layer round trip even when declared as a SERVICE — the shape
    /// REQ-011 exists for. <see langword="null"/> means "no Kafka step targets anything here", which
    /// is correct for a suite with no Kafka steps and for every caller that predates this parameter.
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
        var mapped = EnvironmentMapper.Map(environment, resolvedSeedBaseDirectory);

        // ----------------------------------------------------------------
        // Step 2: Start the headless Aspire host.
        // HeadlessTopology.StartAsync already disposes itself on failure and re-throws;
        // we classify that exception as OrchestrationException so callers always receive
        // a typed Environment-error signal (§12.1) rather than a raw Aspire exception.
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
                    kafkaSpeakingTargets ?? EmptyTargets,
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
                securityConfirmations);
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
    /// Reads the same declaration <c>SecuredEndpointProbe.EnumerateSecuredTargets</c> walks, so the
    /// guard fires for exactly the suites the probe would otherwise probe with nothing in hand.
    /// </remarks>
    private static bool DeclaresSecurity(EnvironmentSpec? environment)
    {
        if (environment?.Services is { } services)
        {
            foreach (var (_, spec) in services)
            {
                if (spec.Security is not null)
                {
                    return true;
                }
            }
        }

        if (environment?.Dependencies is { } dependencies)
        {
            foreach (var (_, spec) in dependencies)
            {
                if (spec.Security is not null)
                {
                    return true;
                }
            }
        }

        return false;
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
