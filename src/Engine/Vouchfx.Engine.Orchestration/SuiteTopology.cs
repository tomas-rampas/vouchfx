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
        string seedBaseDirectory)
    {
        _inner = inner;
        DiscoveredServices = discoveredServices;
        DependencyNames = dependencyNames;
        DependencyTypes = BuildDependencyTypeMap(environment);
        _environment = environment;
        _seedBaseDirectory = seedBaseDirectory;
    }

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
    /// <param name="cancellationToken">
    /// Propagated to <see cref="HeadlessTopology.StartAsync"/>, to each
    /// health-gate <c>WaitForResourceHealthyAsync</c> call, and to the seed
    /// applier.  Must be the last parameter (CA1068).
    /// </param>
    /// <returns>
    /// A fully started and health-gated <see cref="SuiteTopology"/> whose
    /// <see cref="DiscoveredServices"/> map is populated.
    /// </returns>
    /// <exception cref="OrchestrationException">
    /// Thrown (instead of the raw Aspire exception) when the topology fails to start,
    /// when a health gate times out or the resource enters a terminal unhealthy state,
    /// or when service discovery fails.  The <see cref="OrchestrationException.Info"/>
    /// property carries the structured diagnosis (kind, registry host, auth status, detail).
    /// This is always an Environment error (§12.1) — never a test Fail.
    /// </exception>
    public static async Task<SuiteTopology> StartAsync(
        EnvironmentSpec? environment,
        string? appHostAssemblyName,
        TimeSpan? startupTimeout = null,
        string? seedBaseDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var gateTimeout = startupTimeout ?? TimeSpan.FromSeconds(120);

        // ----------------------------------------------------------------
        // Step 1: Map the EnvironmentSpec → Configure callback + gate list + resolver.
        // EnvironmentMapper.Map is pure (no I/O) so exceptions here are ArgumentExceptions,
        // not OrchestrationExceptions — let them propagate as-is to the caller.
        // ----------------------------------------------------------------
        var mapped = EnvironmentMapper.Map(environment);

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
            // fixture is returned, INSIDE this try/catch (§3.2.2, S05-A-01/A-02).
            // SeedApplier dispatches each seeded dependency on its declared type:
            // a postgres dependency's sql seed is applied now (a sql seed on any
            // OTHER dependency type is rejected as a type mismatch); document
            // stores (mongodb, elasticsearch) and brokers → content-hash + record
            // via deferred seams, no actual write in M2; redis has no seed path at
            // all. Throws OrchestrationException (Provision kind) on any failure;
            // the outer catch below disposes the topology so containers do not
            // leak, and the failure surfaces as an Environment error (§12.1) —
            // never a misattributed assertion Fail.
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
            var resolvedSeedBaseDirectory = seedBaseDirectory ?? Directory.GetCurrentDirectory();
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
                resolvedSeedBaseDirectory);
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
    /// stores are skipped: there is no row-applied non-Postgres seed to restore (document-store
    /// and broker seeds are content-recorded via deferred seams; Redis has no seed path at all),
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
            if (!dependencyTypes.TryGetValue(dependencyName, out var declaredType) ||
                !string.Equals(declaredType, "postgres", StringComparison.OrdinalIgnoreCase))
            {
                // Non-Postgres (or undeclared) seed dependency: SeedApplier validates/dispatches
                // it; no SQL schema to reset here.  (Broker/document seeds are content-recorded,
                // not row-applied, so they have no persisted state to clear on the kept topology.)
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
            brokerSink: null,
            documentSink: null,
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
