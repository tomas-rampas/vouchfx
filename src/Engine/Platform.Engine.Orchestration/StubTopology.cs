using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Platform.Engine.Orchestration;

/// <summary>
/// A pre-wired, health-gated Aspire topology used by the S01-A-02 and S01-A-03 acceptance tests.
/// Assembles one managed Postgres dependency and one HTTP container service, then waits
/// until both are fully healthy before returning.
/// </summary>
/// <remarks>
/// <para>
/// This topology demonstrates the two distinct endpoint-discovery paths mandated by §4:
/// <list type="bullet">
///   <item>
///     <b>Managed dependency connection string</b> — retrieved via
///     <see cref="GetConnectionStringAsync"/> which calls
///     <see cref="IResourceWithConnectionString.GetConnectionStringAsync"/> on the retained
///     database resource builder's <c>Resource</c>, using the Aspire application-model API
///     rather than the testing-infrastructure helper.
///   </item>
///   <item>
///     <b>Service HTTP endpoint</b> — retrieved through the <em>retained</em>
///     <see cref="IResourceBuilder{ContainerResource}"/> returned by
///     <c>AddContainer(name, image)</c>, accessed as
///     <c>containerBuilder.GetEndpoint("http").Url</c> after
///     <see cref="DistributedApplication.StartAsync"/> completes.
///     <br/>
///     <b>Note:</b> <c>app.GetEndpoint(name, scheme)</c> does <em>not</em> exist as a
///     non-extension member on <see cref="DistributedApplication"/> — this is the §4 footgun.
///     The retained-builder pattern avoids it entirely and does not depend on the
///     <c>Aspire.Hosting.Testing</c> namespace.
///   </item>
/// </list>
/// </para>
/// </remarks>
public sealed class StubTopology : IAsyncDisposable
{
    private readonly HeadlessTopology _inner;

    // Retained database resource — used to call GetConnectionStringAsync without depending
    // on Aspire.Hosting.Testing extension methods.
    private readonly IResourceBuilder<PostgresDatabaseResource> _dbBuilder;

    // Retained container endpoint reference — allocated by the DCP process after StartAsync.
    private readonly EndpointReference _webEndpoint;

    private bool _disposed;

    private StubTopology(
        HeadlessTopology inner,
        IResourceBuilder<PostgresDatabaseResource> dbBuilder,
        EndpointReference webEndpoint,
        StartupTiming timing)
    {
        _inner = inner;
        _dbBuilder = dbBuilder;
        _webEndpoint = webEndpoint;
        Timing = timing;
    }

    /// <summary>Gets the underlying <see cref="HeadlessTopology"/> instance.</summary>
    public HeadlessTopology Inner => _inner;

    /// <summary>
    /// Gets the wall-clock timing captured during <see cref="StartAsync"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="StartupTiming.Total"/> covers the full build, start, and both health gates.
    /// <see cref="StartupTiming.DatabaseReady"/> is the cumulative time until the
    /// <c>"appdb"</c> gate completed.  <see cref="StartupTiming.ServiceReady"/> is the
    /// additional time between the database gate and the <c>"web"</c> gate.
    /// </remarks>
    public StartupTiming Timing { get; }

    /// <summary>
    /// Builds, starts, and health-gates a stub topology containing:
    /// <list type="bullet">
    ///   <item>A managed Postgres server (<c>"pg"</c>) with a database (<c>"appdb"</c>).</item>
    ///   <item>An HTTP container (<c>"web"</c>) on port 80, defaulting to
    ///   <c>traefik/whoami</c>.</item>
    /// </list>
    /// </summary>
    /// <param name="appHostAssemblyName">
    /// Passed through to <see cref="HeadlessTopology.StartAsync"/> — must name the assembly
    /// carrying the DCP metadata attributes (R-1 finding, CLAUDE.md §"Aspire (§4, §19)").
    /// </param>
    /// <param name="startupTimeout">
    /// Maximum time allowed for the Aspire build-and-start phase (before the health gates).
    /// Defaults to 120 seconds.  Also used as the default for <paramref name="databaseHealthTimeout"/>
    /// and <paramref name="serviceHealthTimeout"/> when those are not specified.
    /// </param>
    /// <param name="databaseHealthTimeout">
    /// Maximum time allowed for the <c>"appdb"</c> health gate to complete after
    /// <see cref="HeadlessTopology.StartAsync"/> returns.  Defaults to
    /// <paramref name="startupTimeout"/> (or 120 seconds if that is also <see langword="null"/>).
    /// </param>
    /// <param name="serviceHealthTimeout">
    /// Maximum time allowed for the <c>"web"</c> health gate to complete after the database
    /// gate passes.  Defaults to <paramref name="startupTimeout"/> (or 120 seconds if that is
    /// also <see langword="null"/>).
    /// </param>
    /// <param name="webImage">
    /// Docker image reference for the <c>"web"</c> container.  Defaults to
    /// <c>"traefik/whoami"</c>.  Override in tests to inject a bad image and exercise
    /// the <see cref="OrchestrationException"/> / Environment-error classification path
    /// (§12.1, S02-A-02).
    /// </param>
    /// <param name="cancellationToken">Propagated to the underlying host. Must be the last parameter (CA1068).</param>
    /// <returns>A fully started and health-gated <see cref="StubTopology"/>.</returns>
    /// <exception cref="OrchestrationException">
    /// Thrown (instead of the raw Aspire exception) when <see cref="HeadlessTopology.StartAsync"/>
    /// fails or either health gate fails.  The <see cref="OrchestrationException.Info"/> property
    /// carries the structured diagnosis (kind, registry host, auth status, detail).
    /// </exception>
    public static async Task<StubTopology> StartAsync(
        string? appHostAssemblyName = null,
        TimeSpan? startupTimeout = null,
        TimeSpan? databaseHealthTimeout = null,
        TimeSpan? serviceHealthTimeout = null,
        string webImage = "traefik/whoami",
        CancellationToken cancellationToken = default)
    {
        var timeout = startupTimeout ?? TimeSpan.FromSeconds(120);
        var dbTimeout = databaseHealthTimeout ?? timeout;
        var svcTimeout = serviceHealthTimeout ?? timeout;

        var sw = Stopwatch.StartNew();

        // Retain the database builder so we can call GetConnectionStringAsync on its Resource
        // without depending on Aspire.Hosting.Testing extension methods.
        IResourceBuilder<PostgresDatabaseResource>? dbBuilder = null;

        // Retain the container builder so we can retrieve its endpoint URL after StartAsync.
        // This is the ONLY correct approach — DistributedApplication has no non-extension
        // GetEndpoint(name, scheme) member (§4 footgun).  The retained IResourceBuilder<T>
        // exposes GetEndpoint("http").Url once endpoints are allocated by the DCP process.
        IResourceBuilder<ContainerResource>? webBuilder = null;

        using var startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startCts.CancelAfter(timeout);

        // ----------------------------------------------------------------
        // Start the headless topology.  HeadlessTopology.StartAsync already
        // disposes itself on failure and rethrows; we classify that exception
        // as an OrchestrationException so callers always receive a typed
        // Environment-error signal (§12.1) rather than a raw Aspire exception.
        // ----------------------------------------------------------------
        HeadlessTopology topology;
        try
        {
            topology = await HeadlessTopology.StartAsync(
                appHostAssemblyName: appHostAssemblyName,
                configureResources: b =>
                {
                    // ----------------------------------------------------------------
                    // Managed dependency: Postgres server → database.
                    // §4 hard invariant: WaitFor must target the DATABASE resource
                    // ("appdb"), not the server ("pg").  The server resource returns
                    // healthy before Aspire's lifecycle script finishes creating the
                    // database, which causes intermittent failures on fast hardware.
                    // See: CLAUDE.md §"Aspire (§4, §19)" — server-vs-database race.
                    // ----------------------------------------------------------------
                    var pgServer = b.AddPostgres("pg");
                    dbBuilder = pgServer.AddDatabase("appdb");

                    // ----------------------------------------------------------------
                    // Service container — image is injected via webImage parameter
                    // (default: traefik/whoami).  Tests can pass a bad image ref to
                    // exercise the OrchestrationException / ENV_ERROR path (S02-A-02).
                    // Use string overload AddContainer(name, image) only — never the
                    // generic AddProject<T>() which creates compile-time coupling
                    // (§4 hard invariant, CLAUDE.md).
                    // ----------------------------------------------------------------
                    webBuilder = b.AddContainer("web", webImage)
                        .WithHttpEndpoint(targetPort: 80, name: "http")
                        // Register a real HTTP health check on "/" so Aspire polls for an
                        // actual 200 response (not merely port-mapped / Running state).
                        // This closes the HTTP-serving race on loaded CI agents (S2).
                        // Signature (Aspire 9.x / package 13.4.2):
                        //   WithHttpHealthCheck(string path, int? statusCode, string endpointName)
                        .WithHttpHealthCheck(path: "/", endpointName: "http")
                        // §4: gate on the *database* resource, not the server,
                        // to avoid the fast-hardware server-vs-database race.
                        .WaitFor(dbBuilder);
                },
                cancellationToken: startCts.Token).ConfigureAwait(false);
        }
        catch (OrchestrationException)
        {
            // Already classified — propagate as-is.
            throw;
        }
        catch (Exception ex)
        {
            // HeadlessTopology already disposed itself; classify and wrap.
            var info = OrchestrationErrorClassifier.Classify(ex, webImage, "web");
            throw new OrchestrationException(info, ex);
        }

        // HeadlessTopology.StartAsync returned successfully: live containers are running.
        // If anything below throws (health-gate timeout, endpoint resolution failure) the
        // started topology must be disposed so containers do not leak (BLOCKER B1).
        try
        {
            // Both builders are guaranteed non-null: the configureResources callback always assigns them.
            var webEndpoint = webBuilder!.GetEndpoint("http");

            var app = topology.Application;

            // ----------------------------------------------------------------
            // Health-gate 1: Postgres DATABASE ("appdb").
            // §4 invariant: wait on the database resource, not the server resource.
            // "The server returns before Aspire's lifecycle script creates the DB →
            //  intermittent failures on fast hardware." — CLAUDE.md
            //
            // WaitBehavior.StopOnResourceUnavailable (Aspire 9.x default, made explicit here):
            // if the resource enters FailedToStart / Exited / RuntimeUnhealthy, the gate
            // throws DistributedApplicationException immediately rather than hanging.
            // This is the DESIRED behaviour — a resource that fails to start is an
            // Environment error (§12.1) and must surface, not block indefinitely.
            //
            // Uses dbTimeout (defaults to startupTimeout) for a bounded, per-resource window.
            // ----------------------------------------------------------------
            try
            {
                using var healthCts1 = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                healthCts1.CancelAfter(dbTimeout);
                await app.ResourceNotifications
                    .WaitForResourceHealthyAsync("appdb", WaitBehavior.StopOnResourceUnavailable, healthCts1.Token)
                    .ConfigureAwait(false);
            }
            catch (OrchestrationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var info = OrchestrationErrorClassifier.Classify(ex, imageRef: null, resourceName: "appdb");
                throw new OrchestrationException(info, ex);
            }

            var databaseReadyElapsed = sw.Elapsed;

            // ----------------------------------------------------------------
            // Health-gate 2: "web" container.
            // WithHttpHealthCheck("/", endpointName: "http") added above registers a real
            // HTTP probe on "/", so this gate waits for an actual 200 response rather than
            // merely waiting for the container to reach KnownResourceStates.Running
            // (port-mapped but possibly not yet serving HTTP).  This closes the
            // HTTP-serving race on loaded CI agents (S2 fix).
            //
            // WaitBehavior.StopOnResourceUnavailable: same fail-fast semantics as gate 1 —
            // a container that exits or fails to start throws immediately.
            //
            // Uses svcTimeout (defaults to startupTimeout) for a bounded, per-resource window.
            // ----------------------------------------------------------------
            try
            {
                using var healthCts2 = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                healthCts2.CancelAfter(svcTimeout);
                await app.ResourceNotifications
                    .WaitForResourceHealthyAsync("web", WaitBehavior.StopOnResourceUnavailable, healthCts2.Token)
                    .ConfigureAwait(false);
            }
            catch (OrchestrationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var info = OrchestrationErrorClassifier.Classify(ex, imageRef: webImage, resourceName: "web");
                throw new OrchestrationException(info, ex);
            }

            sw.Stop();
            var timing = new StartupTiming(
                Total: sw.Elapsed,
                DatabaseReady: databaseReadyElapsed,
                ServiceReady: sw.Elapsed - databaseReadyElapsed);

            return new StubTopology(topology, dbBuilder!, webEndpoint, timing);
        }
        catch
        {
            // A health-gate failure is an Environment error (§12.1).  Dispose the already-started
            // topology so containers started by HeadlessTopology.StartAsync do not leak.
            await topology.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Returns the connection string for the managed Postgres database resource.
    /// </summary>
    /// <param name="cancellationToken">Propagated to the underlying Aspire connection-string resolver.</param>
    /// <returns>
    /// The connection string, or <see langword="null"/> if the resource does not expose one.
    /// </returns>
    /// <remarks>
    /// Calls <see cref="IResourceWithConnectionString.GetConnectionStringAsync"/> on the
    /// retained database resource builder's <c>Resource</c>.  This uses the Aspire
    /// application-model API and does not depend on <c>Aspire.Hosting.Testing</c>.
    /// The §4-mandated separation: this is the managed-dependency discovery path.
    /// </remarks>
    public ValueTask<string?> GetConnectionStringAsync(CancellationToken cancellationToken = default)
    {
        if (_dbBuilder.Resource is not IResourceWithConnectionString cs)
        {
            throw new InvalidOperationException(
                $"Resource '{_dbBuilder.Resource.Name}' does not implement {nameof(IResourceWithConnectionString)}. " +
                "Verify the database resource was created with AddDatabase() on a managed Postgres server.");
        }

        return cs.GetConnectionStringAsync(cancellationToken);
    }

    /// <summary>
    /// Returns the HTTP endpoint URL of the <c>"web"</c> container service.
    /// </summary>
    /// <returns>
    /// The fully-qualified URL (e.g. <c>http://localhost:12345</c>) allocated by the DCP process.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Retrieved from the <em>retained</em> <see cref="IResourceBuilder{ContainerResource}"/>
    /// captured during resource configuration, via <c>GetEndpoint("http").Url</c>.
    /// </para>
    /// <para>
    /// <b>§4 footgun note:</b> <c>app.GetEndpoint(name, scheme)</c> does <em>not</em> exist
    /// as a non-extension member on <see cref="DistributedApplication"/>.  The retained-builder
    /// approach used here is the correct path that does not depend on
    /// <c>Aspire.Hosting.Testing</c> extension methods.
    /// </para>
    /// </remarks>
    public string GetHttpEndpointUrl() => _webEndpoint.Url;

    /// <inheritdoc />
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
