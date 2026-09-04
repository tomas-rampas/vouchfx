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
    private readonly DcpFlightRecorder? _recorder;
    private bool _disposed;

    private HeadlessTopology(DistributedApplication app, DcpFlightRecorder? recorder)
    {
        _app = app;
        _recorder = recorder;
    }

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

        // The #420 flight recorder, armed for the duration of THIS start and nothing else. Null
        // only when VOUCHFX_DCP_CAPTURE=0 turned it off, in which case no provider and no filter
        // rule are registered at all. See DcpFlightRecorder for why it is armed by default: the
        // fault it captures clears before anyone can switch a diagnostic on.
        var recorder = DcpFlightRecorder.CreateUnlessDisabled();

        // Suppress HealthChecks log noise below Warning (§4 hard invariant), and Aspire's own
        // BELOW-WARNING lifecycle banners on the Aspire.Hosting.DistributedApplication category.
        //
        // Stated as a property, not as a roster, and the roster this comment used to carry is
        // exactly why: it enumerated the category's contents as "only the version banner,
        // starting/started, and Application host directory", and THIS change disproved it — the
        // "Unable to allocate a network port" line #420 turns on is a WARNING on that same
        // category, measured off a live capture. An enumeration nothing checks is a claim of
        // completeness, and this one was already false when it was written.
        //
        // The property the filter actually relies on: everything that category emits BELOW
        // Warning is lifecycle chatter with no diagnostic value here, and one item of it is
        // actively misleading — "Application host directory is: <path>" prints the
        // apphostprojectpath AssemblyMetadata baked at BUILD time, i.e. the release build
        // machine's directory (/home/runner/work/...) on every customer run of the packaged tool,
        // while nothing in the engine reads AppHostDirectory (scenario-relative paths resolve
        // against the suite's own directory). Warning and above still passes, which is what keeps
        // the port-allocation warning visible.
        // Aspire.Hosting.Dcp.DcpHost is deliberately NOT filtered: its "Starting DCP with
        // arguments" line shows THIS machine's resolved DCP path — primary diagnostic
        // evidence for the DCP-path support flow (docs/kb/dcp-orchestrator-portability.md).
        //
        // BESIDE those two suppressions, and deliberately not instead of them, sits the #420
        // flight recorder. The two do opposite jobs and must not be confused: the filters above
        // decide what a HUMAN sees on the console and are unchanged by anything below, while the
        // recorder is a private, in-memory, bounded sink that no console reads. Its own rules are
        // PROVIDER-SCOPED, so the console keeps exactly today's levels — DcpFlightRecorder.Register
        // holds those three rules and why each one is there.
        builder.Services.AddLogging(lb =>
        {
            lb.AddFilter(
                    "Microsoft.Extensions.Diagnostics.HealthChecks",
                    LogLevel.Warning)
                .AddFilter(
                    "Aspire.Hosting.DistributedApplication",
                    LogLevel.Warning);

            if (recorder is not null)
            {
                DcpFlightRecorder.Register(lb, recorder);
            }
        });

        DistributedApplication app;
        try
        {
            configureResources?.Invoke(builder);
            app = builder.Build();
        }
        catch
        {
            // Drop the recorder rather than flush it, and the distinction is not fussiness: DCP
            // is not launched until StartAsync, so a failure here — an authoring fault raised
            // from inside the configure closure, a malformed resource — cannot be the fault this
            // recorder exists to capture, and there is nothing DCP-related in the buffer to
            // write. The exception leaves unchanged and untyped-over, which is what lets
            // SuiteTopology's own TopologyAuthoringException catch still see it.
            recorder?.Dispose();
            throw;
        }

        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Flush the #420 flight recorder BEFORE anything else on this path. Disposing the
            // application logs further entries and can itself fail, and the exception being
            // carried is the one the classifier will read: the capture must be written and
            // annotated onto THIS instance while it is still the failure in hand.
            //
            // FlushOnFailureAsync never throws and always drops the recorder, so this line cannot
            // change which exception leaves this method, only what evidence travels with it.
            //
            // NOT on a cancellation the CALLER asked for. A run stopped by Ctrl-C or by a
            // caller's token is not a fault worth a capture file, and writing one would spend a
            // retention slot that a real occurrence of #420 may need. A health-gate timeout is
            // also an OperationCanceledException, but its token is the gate's own — hence the
            // test on the caller's token specifically, not on the exception type.
            //
            // KNOWN LIMIT, stated because the sentence above overclaims on its own: the test is
            // on WHATEVER TOKEN THIS METHOD WAS HANDED, which is only the same thing as "the
            // caller asked to stop" when that token carries no deadline of its own. It does not
            // for the production path — SuiteTopology forwards the runner's token untouched, so
            // the only way it trips there is a real user cancellation. It DOES for StubTopology,
            // which hands over a linked token with CancelAfter(startupTimeout): a start that
            // times out there suppresses the capture, and a start timeout is the #420 shape.
            //
            // Left as a documented limit rather than fixed, deliberately. Fixing it properly
            // means a SECOND CancellationToken parameter on this public method — one for
            // "deadline", one for "the user asked to stop" — which is API surface and CA1068
            // ordering churn for a gap that reaches only the in-repo stub fixture. If a
            // production caller ever passes a deadline-bearing token here, this becomes a real
            // defect and the parameter is the fix; that is the trigger to watch for.
            if (recorder is not null && !cancellationToken.IsCancellationRequested)
            {
                await DcpCapture
                    .FlushOnFailureAsync(recorder, ex, DateTimeOffset.UtcNow)
                    .ConfigureAwait(false);
            }
            else
            {
                recorder?.Dispose();
            }

            // StartAsync can fail with an Environment error (image-pull failure, DCP crash, partial
            // start).  The DistributedApplication is IAsyncDisposable and already holds resources
            // that must be released; dispose it before re-throwing so containers do not leak.
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // StartAsync returned. The recorder is deliberately STILL ARMED and is handed to the
        // returned topology — see the class remarks and DcpFlightRecorder's header for why the
        // window has to outlive this method: the #420 transcript's `fail:` line is a console
        // level token, so Aspire may be catching that throw internally and letting StartAsync
        // return, leaving the fault to surface at a health gate. Dropping here would buffer the
        // golden evidence and then discard it on exactly the fault this exists to capture.
        // SuiteTopology and StubTopology own the far end of the window; a caller that uses
        // HeadlessTopology directly drops it at DisposeAsync.
        return new HeadlessTopology(app, recorder);
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
    /// <para>
    /// Any failure to resolve the host assembly itself (bad name, load failure, no entry
    /// assembly) is deliberately swallowed: it preserves today's behaviour and lets
    /// Aspire's own <c>OptionsValidationException</c> surface exactly as it did before
    /// this self-heal existed, rather than risking a NEW failure mode in a path this
    /// method cannot itself diagnose.
    /// </para>
    /// <para>
    /// <c>internal</c> (not <c>private</c>) so the Orchestration test project
    /// (<c>HeadlessTopologySelfHealTests</c>, granted access via the assembly-level
    /// <c>InternalsVisibleTo</c> in this project's .csproj) can pin the GLUE between
    /// <see cref="DcpPathResolver"/>'s pure decision core and this method's real
    /// assembly-metadata / environment-variable reads and its <c>builder.Configuration</c>
    /// write — without starting Docker, DCP, or an Aspire host. No production caller outside
    /// <see cref="StartAsync"/> exists; the signature and behaviour are otherwise unchanged.
    /// </para>
    /// </remarks>
    internal static void ApplyDcpPathSelfHeal(IDistributedApplicationBuilder builder, string? appHostAssemblyName)
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

        static string ResolvePortableRid()
        {
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                Architecture.Arm64 => "arm64",
                _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            };

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"win-{arch}";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"osx-{arch}";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return $"linux-{arch}";
            return RuntimeInformation.RuntimeIdentifier;
        }

        var resolution = DcpPathResolver.Resolve(
            metadataDcpCliPath: metadataDcpCliPath,
            fileExists: File.Exists,
            nugetPackagesEnvironmentVariable: Environment.GetEnvironmentVariable("NUGET_PACKAGES"),
            userProfileDirectory: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            runtimeIdentifier: ResolvePortableRid(),
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

    /// <summary>
    /// Writes the #420 diagnostic capture for <paramref name="failure"/> and ends this
    /// topology's arming window. Safe to call more than once; the second call is a no-op.
    /// </summary>
    /// <param name="failure">
    /// The exception about to be classified. The capture summary is attached to THIS instance,
    /// so the call has to happen before <c>OrchestrationErrorClassifier.Classify</c> reads it if
    /// the evidence is to reach the Environment-error detail. Calling it later still writes the
    /// file, which is why the outer catch calls it too as a net.
    /// </param>
    /// <remarks>
    /// Never throws, and never changes which exception the caller goes on to rethrow.
    /// </remarks>
    internal Task FlushDiagnosticsAsync(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return _recorder is null
            ? Task.CompletedTask
            : DcpCapture.FlushOnFailureAsync(_recorder, failure, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Ends the arming window without writing anything: the topology is up and ready, so the
    /// buffered DCP traffic describes a start that worked and is of no use to anyone.
    /// </summary>
    /// <remarks>
    /// The caller that knows the topology is READY owns this call, which is not the same moment
    /// as StartAsync returning — see StartAsync's own remarks. Idempotent.
    /// </remarks>
    internal void DropDiagnostics() => _recorder?.Dispose();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Last line of defence for the arming window. A caller that never reported the topology
        // ready and never reported a failure — a direct HeadlessTopology user, or a path that
        // threw somewhere neither catch covers — still ends the window here, DROPPING rather
        // than writing: teardown is not evidence of a fault, and a capture written from here
        // would carry no failure to attach itself to.
        DropDiagnostics();

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

        // Guarded for the reason the catch above already states as a RULE — "teardown must never
        // throw into the verdict path (§12.1)" — which this line contradicted by being the one
        // teardown call left bare (issue #466). It is the final release, so nothing follows it
        // that a swallow can skip, and it runs from `finally`/`await using` frames on every run
        // path: an exception raised here does not merely add a fault, it REPLACES whatever
        // verdict or exception was already in flight, and under `--parallel` lands in the slot
        // catch-all as an EnvironmentError that exits 0. Aspire's own DisposeAsync is not
        // documented to be throw-free, and a DCP host wedged past its cleanup timeout is exactly
        // when it would not be. `_disposed` is already true above, so this is also the last
        // attempt by construction — there is nothing to retry and nothing left to release.
        try
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Deliberate, documented discard — same rule, same no-logging reasoning (CA1848) as
            // the StopAsync catch above. A container the dispose failed to remove is caught by
            // the Docker-gated TopologyTeardownLeakTests, which observes the DOCKER state rather
            // than this method's return.
        }
    }
}
