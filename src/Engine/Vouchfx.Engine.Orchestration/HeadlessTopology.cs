using System.Reflection;
using System.Runtime.InteropServices;
using Aspire.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vouchfx.Engine.Orchestration;

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
/// Packaged-tool DCP path self-heal: the embedded <c>dcpclipath</c> metadata is an ABSOLUTE,
/// machine-specific path baked in at build time. A <c>vouchfx</c> global tool packed on CI
/// carries the CI runner's path, which does not exist on a user's machine even when their
/// own NuGet cache holds the matching <c>aspire.hosting.orchestration.&lt;their-rid&gt;</c>
/// package. Before <c>builder.Build()</c>, <see cref="StartAsync"/> probes whether the
/// embedded path exists here and, if not, overrides <c>DcpPublisher:CliPath</c> with the
/// equivalent package resolved from the current machine's own cache — UNLESS the user has
/// set <c>ASPIRE_DCP_PATH</c> themselves, in which case this self-heal steps aside entirely
/// and lets Aspire's own (higher-precedence-than-metadata) resolution of that variable run
/// untouched. See <see cref="DcpPathResolver"/> for the full rationale and decision logic.
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
            // Prevent Aspire from forwarding the xUnit/test-host process argv (e.g. "--filter",
            // "--list-tests") to the DCP process, which does not understand those arguments.
            Args = Array.Empty<string>(),
            // Provide the name of the assembly carrying the dcpclipath/dcpextensionpaths
            // metadata attributes so Aspire can locate the DCP binary.
            // Null falls back to Assembly.GetEntryAssembly() — correct for a real AppHost executable.
            AssemblyName = appHostAssemblyName,
        };

        var builder = DistributedApplication.CreateBuilder(options);

        // Packaged-tool DCP path self-heal (see the class remarks and DcpPathResolver for
        // the full rationale). Must run before builder.Build() so the DcpPublisher:CliPath
        // override (when applied) is visible to Aspire's own DcpOptions configuration pass.
        ApplyDcpPathSelfHeal(builder, options.AssemblyName);

        // Make DCP's stop synchronous so it deletes containers/networks before the host exits
        // (§4.5 teardown discipline). The default is false: DCP fires a "Stopping" PATCH and
        // returns immediately, so the detached DCP apiserver finishes deletion AFTER the process
        // exits → orphaned containers + the aspire-session-network-* network. Setting this to true
        // mirrors Aspire 13.4.2's own Aspire.Hosting.Testing.DistributedApplicationFactory, which
        // is the only teardown path in the box that does not leak.
        builder.Configuration["DcpPublisher:WaitForResourceCleanup"] = "true";

        // Suppress HealthChecks log noise below Warning (§4 hard invariant).
        builder.Services.AddLogging(lb =>
            lb.AddFilter(
                "Microsoft.Extensions.Diagnostics.HealthChecks",
                LogLevel.Warning));

        configureResources?.Invoke(builder);

        var app = builder.Build();
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // StartAsync can fail with an Environment error (image-pull failure, DCP crash, partial
            // start).  The DistributedApplication is IAsyncDisposable and already holds resources
            // that must be released; dispose it before re-throwing so containers do not leak.
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new HeadlessTopology(app);
    }

    /// <summary>
    /// Applies the packaged-tool DCP path self-heal (see the class remarks and
    /// <see cref="DcpPathResolver"/>). Resolves the same host assembly Aspire itself will
    /// resolve from <paramref name="appHostAssemblyName"/>, reads its <c>DcpCliPath</c>
    /// metadata, and — only when that path does not exist on THIS machine — overrides
    /// <c>DcpPublisher:CliPath</c> with the equivalent package found under this machine's
    /// own NuGet cache, or throws a classified <see cref="OrchestrationException"/> when
    /// no fallback can be found either.
    /// </summary>
    /// <remarks>
    /// Any failure to resolve the host assembly itself (bad name, load failure, no entry
    /// assembly) is deliberately swallowed: it preserves today's behaviour and lets
    /// Aspire's own <c>OptionsValidationException</c> surface exactly as it did before
    /// this self-heal existed, rather than risking a NEW failure mode in a path this
    /// method cannot itself diagnose.
    /// </remarks>
    private static void ApplyDcpPathSelfHeal(IDistributedApplicationBuilder builder, string? appHostAssemblyName)
    {
        Assembly? hostAssembly;
        try
        {
            hostAssembly = appHostAssemblyName is null
                ? Assembly.GetEntryAssembly()
                : Assembly.Load(new AssemblyName(appHostAssemblyName));
        }
        catch
        {
            hostAssembly = null;
        }

        if (hostAssembly is null)
        {
            return;
        }

        var metadataDcpCliPath = hostAssembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(m => string.Equals(m.Key, "DcpCliPath", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        var aspireHostingVersion = typeof(DistributedApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        var resolution = DcpPathResolver.Resolve(
            metadataDcpCliPath: metadataDcpCliPath,
            fileExists: File.Exists,
            nugetPackagesEnvironmentVariable: Environment.GetEnvironmentVariable("NUGET_PACKAGES"),
            userProfileDirectory: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            runtimeIdentifier: RuntimeInformation.RuntimeIdentifier,
            aspireHostingInformationalVersion: aspireHostingVersion,
            // Read BEFORE any of the above are consulted (DcpPathResolver checks it first):
            // ASPIRE_DCP_PATH is the user's own escape hatch, honoured by Aspire itself at
            // priority 2 — this self-heal must never pre-empt it with an Unresolvable throw
            // nor outrank it with a DcpPublisher:CliPath override. See DcpPathResolver's
            // "DO NOT SHADOW" remarks.
            aspireDcpPathEnvironmentVariable: Environment.GetEnvironmentVariable("ASPIRE_DCP_PATH"));

        switch (resolution.Kind)
        {
            case DcpPathResolutionKind.Override:
                // ExtensionsPath is auto-derived by Aspire's own ConfigureDefaultDcpOptions
                // from CliPath's directory once this key is set — no need to set it here.
                builder.Configuration["DcpPublisher:CliPath"] = resolution.OverridePath;
                break;

            case DcpPathResolutionKind.Unresolvable:
                // A structurally-known failure (no message-heuristic guesswork needed): the
                // embedded path is dead and no local fallback exists. Kind=Provision — this
                // is neither an image pull, a health gate, nor a discovery failure; it is an
                // infrastructure-provisioning problem with the orchestrator itself. Always an
                // Environment error (§12.1), never a test Fail.
                throw new OrchestrationException(new OrchestrationErrorInfo(
                    Kind: OrchestrationErrorKind.Provision,
                    ResourceName: "dcp",
                    RegistryHost: null,
                    AuthStatus: null,
                    Detail: resolution.UnresolvableDetail!));

            case DcpPathResolutionKind.UseEmbedded:
            default:
                break;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // This is the single teardown chokepoint for the engine: SuiteTopology and StubTopology
        // both delegate their DisposeAsync to this HeadlessTopology, so the fix below covers every
        // production teardown path. Stop the application (graceful, bounded) BEFORE disposing so
        // DCP — with WaitForResourceCleanup=true set in StartAsync — synchronously deletes the
        // containers and the session network instead of racing process exit (§4.5 teardown
        // discipline). This mirrors Aspire's own DistributedApplicationFactory, which calls
        // StopAsync before DisposeAsync; DisposeAsync alone does NOT call StopAsync in Aspire 13.4.2.
        // The 15s bound sits above DCP's own internal ~10s dispose/cleanup timeout, so under normal
        // operation StopAsync returns well before the CTS fires and the CTS only trips on a wedged
        // stop — do not trim it below DCP's timeout or teardown would race DCP again.
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await _app.StopAsync(stopCts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Deliberate, documented discard: teardown must never throw into the verdict path
            // (§12.1). A stuck StopAsync is bounded by stopCts, and DisposeAsync still runs below as
            // the final release, so swallowing here can never leave the app un-disposed. We do NOT
            // log: the headless engine host wires no Debug sink, and an ad-hoc ILogger call trips
            // this repo's CA1848 (LoggerMessage-delegate) gate — not worth the ceremony for a
            // best-effort teardown stop. A regression of the leak is caught by the Docker-gated
            // TopologyTeardownLeakTests, not by a runtime log.
        }

        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
