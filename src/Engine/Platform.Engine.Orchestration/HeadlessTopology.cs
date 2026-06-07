using Aspire.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Platform.Engine.Orchestration;

/// <summary>
/// Constructs and manages a headless, programmatic Aspire <see cref="DistributedApplication"/>
/// for use inside the vouchfx engine (i.e. without the <c>aspire run</c> CLI or the Aspire Dashboard).
/// </summary>
/// <remarks>
/// <para>
/// The wrapper enforces the hard invariants from §4 of the engineering blueprint:
/// <list type="bullet">
///   <item>The application is built with <c>DisableDashboard = true</c>; the dashboard requires
///   environment variables that are only injected by <c>aspire run</c>.</item>
///   <item><c>Microsoft.Extensions.Diagnostics.HealthChecks</c> log entries below
///   <see cref="LogLevel.Warning"/> are suppressed — they are cosmetic startup noise.</item>
///   <item>Resources are added via <c>AddContainer(name, image)</c> /
///   <c>AddProject(name, csprojPath)</c> string overloads only.
///   The generic <c>AddProject&lt;T&gt;()</c> variant is forbidden (compile-time coupling that
///   breaks the YAML-first premise).</item>
/// </list>
/// </para>
/// <para>
/// R-1 finding (S01-A-01): a plain <c>Aspire.Hosting</c> library reference is insufficient.
/// The DCP binary path (<c>dcpclipath</c> assembly metadata) must be resolvable at host-start
/// time. The recommended approach is to set <see cref="DistributedApplicationOptions.AssemblyName"/>
/// to the name of an assembly that carries the DCP metadata attributes — typically the calling
/// test-or-AppHost assembly that references <c>Aspire.Hosting.AppHost</c> directly with
/// <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c> and the <c>Aspire.AppHost.Sdk</c>.
/// </para>
/// <para>
/// This type is intentionally minimal for S01-A-01. The full Postgres / Kafka / service topology
/// is wired in subsequent tasks (S01-A-02 onwards). Extend by supplying a
/// <paramref name="configureResources"/> callback.
/// </para>
/// </remarks>
public sealed class HeadlessTopology : IAsyncDisposable
{
    private readonly DistributedApplication _app;
    private bool _disposed;

    private HeadlessTopology(DistributedApplication app) => _app = app;

    /// <summary>
    /// Gets the underlying <see cref="DistributedApplication"/> instance.
    /// Exposed for test assertions; do not cache across topology lifetimes.
    /// </summary>
    public DistributedApplication Application => _app;

    /// <summary>
    /// Builds and returns a <see cref="HeadlessTopology"/> whose Aspire host is already started.
    /// </summary>
    /// <param name="appHostAssemblyName">
    /// The short name of the assembly that carries the <c>dcpclipath</c> / <c>dcpextensionpaths</c>
    /// <see cref="System.Reflection.AssemblyMetadataAttribute"/> attributes embedded by
    /// <c>Aspire.Hosting.AppHost</c> build targets.  When <see langword="null"/>,
    /// <see cref="System.Reflection.Assembly.GetEntryAssembly()"/> is used, which is correct in a
    /// genuine AppHost executable but will typically be the xUnit runner DLL in a test process.
    /// Pass <c>typeof(YourTestClass).Assembly.GetName().Name</c> from the test project that
    /// holds the <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c> property.
    /// </param>
    /// <param name="configureResources">
    /// Optional callback invoked after the builder is created and logging is configured
    /// but before <see cref="DistributedApplication.StartAsync"/> is called.
    /// Use this to add containers, projects and dependencies.
    /// </param>
    /// <param name="cancellationToken">Propagated to <see cref="DistributedApplication.StartAsync"/>.</param>
    /// <returns>A started <see cref="HeadlessTopology"/> that must be disposed when the test ends.</returns>
    public static async Task<HeadlessTopology> StartAsync(
        string? appHostAssemblyName = null,
        Action<IDistributedApplicationBuilder>? configureResources = null,
        CancellationToken cancellationToken = default)
    {
        var options = new DistributedApplicationOptions
        {
            DisableDashboard = true,
            Args = Array.Empty<string>(),
            // Provide the name of the assembly carrying the dcpclipath/dcpextensionpaths
            // metadata attributes so Aspire can locate the DCP binary.
            // Null falls back to Assembly.GetEntryAssembly() — correct for a real AppHost executable.
            AssemblyName = appHostAssemblyName,
        };

        var builder = DistributedApplication.CreateBuilder(options);

        // Suppress HealthChecks log noise below Warning (§4 hard invariant).
        builder.Services.AddLogging(lb =>
            lb.AddFilter(
                "Microsoft.Extensions.Diagnostics.HealthChecks",
                LogLevel.Warning));

        configureResources?.Invoke(builder);

        var app = builder.Build();
        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        return new HeadlessTopology(app);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
