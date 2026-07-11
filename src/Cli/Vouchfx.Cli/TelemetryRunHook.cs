// Vouchfx.Cli — TelemetryRunHook (S10-G-04).
//
// The CLI-side glue that connects a `vouchfx run` to the privacy-first telemetry
// subsystem.  It does THREE things, each wrapped so telemetry can NEVER affect the
// run's verdict or exit code:
//   1. Before the run: when consent is Undecided, show the one-time first-run notice.
//   2. Decide whether this run may emit (consent==Enabled AND not opted out) — if not,
//      it asks the runner to write NO telemetry events file (so we never even collect).
//      ENVIRONMENT-CONFIGURED mode (CI): when all three VOUCHFX_TELEMETRY_* variables
//      are set (endpoint, token, install id) that IS the opt-in for this environment —
//      the local consent store is bypassed and the event carries the supplied, stable
//      per-repo install id.  --no-telemetry / VOUCHFX_NO_TELEMETRY still always win.
//   3. After the run: from the SAME buffered event stream the renderers consumed
//      (re-read from the runner's --events output), build the allowlisted TelemetryEvent
//      and append it to the local outbox sink.
//
// HOW THE EVENT BUFFER IS REUSED WITHOUT TOUCHING A FROZEN CONTRACT:
//   The runner already exposes its buffered JSON Lines event stream verbatim via the
//   S10 `eventsReportPath` parameter (the same buffer the terminal/HTML/JUnit renderers
//   consume).  Rather than change any runner signature or event record, the hook hands
//   the runner a private TEMP events path when telemetry will emit, then reads those
//   exact lines back to build the telemetry event and deletes the temp file.  When the
//   user ALSO passed --events, that user path is used directly (no temp file, no second
//   write).  Either way the telemetry event is derived from the identical, frozen v1
//   stream — zero new fields, zero contract change.
//
// ALL public methods swallow every exception: telemetry must be invisible to the run.
//
// DEFERRED (v2): a per-file `metadata.telemetry: off` opt-out would need a new field on
// the frozen YAML metadata schema (blocked by the v1 schema freeze, SchemaFreezeTests).
// In v1, suppression is global consent + the --no-telemetry flag + VOUCHFX_NO_TELEMETRY.

using System.Net.Http;
using System.Reflection;
using Vouchfx.Engine.Telemetry;

namespace Vouchfx.Cli;

/// <summary>
/// Connects a <c>vouchfx run</c> invocation to the opt-in telemetry subsystem
/// (S10-G-04), deriving the allowlisted <see cref="TelemetryEvent"/> from the same
/// buffered event stream the renderers consume.
/// </summary>
/// <remarks>
/// Every method here is defensive: telemetry is a side concern that must never change
/// the run's verdict or exit code, so all failures are swallowed.
/// </remarks>
internal sealed class TelemetryRunHook
{
    private readonly TelemetryConsentStore _store;
    private readonly ITelemetryPaths _paths;
    private readonly ITelemetrySink _sink;
    private readonly bool _noTelemetryFlag;
    private readonly TextWriter _diagnostics;
    private readonly Guid? _environmentInstallId;

    /// <summary>
    /// Creates a hook over the supplied telemetry seams.  The production caller passes
    /// <see cref="DefaultTelemetryPaths"/> + a <see cref="LocalFileTelemetrySink"/>;
    /// the parameters are injectable so the CLI test project can drive it with a temp
    /// directory.
    /// </summary>
    /// <param name="store">The consent + install-id store.</param>
    /// <param name="paths">The on-disk path seam (consent store + outbox).</param>
    /// <param name="sink">The telemetry transport (local outbox in v1).</param>
    /// <param name="noTelemetryFlag">The <c>--no-telemetry</c> run flag.</param>
    /// <param name="diagnostics">An optional sink for one-line, redaction-safe notices.</param>
    /// <param name="environmentInstallIdRaw">
    /// The raw <see cref="TelemetryInstallId.EnvVar"/> value (pass
    /// <c>Environment.GetEnvironmentVariable</c>; injectable so tests never mutate
    /// process env).  Together with <paramref name="transportConfigured"/> it selects
    /// ENVIRONMENT-CONFIGURED mode: a valid GUID here + a configured transport is the
    /// opt-in for this environment, and the event carries this id instead of the
    /// store's.  The local consent store is bypassed entirely in that mode.
    /// </param>
    /// <param name="transportConfigured">
    /// Whether the HTTP transport is configured
    /// (<see cref="TelemetryTransportOptions.IsConfigured"/>) — environment-configured
    /// mode requires all three <c>VOUCHFX_TELEMETRY_*</c> variables, not just the id.
    /// </param>
    public TelemetryRunHook(
        TelemetryConsentStore store,
        ITelemetryPaths paths,
        ITelemetrySink sink,
        bool noTelemetryFlag,
        TextWriter diagnostics,
        string? environmentInstallIdRaw = null,
        bool transportConfigured = false)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _noTelemetryFlag = noTelemetryFlag;
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));

        if (!TelemetryInstallId.TryParse(environmentInstallIdRaw, out var envId))
        {
            // Invalid id: warn once (redaction-safe — never echo the raw value) and fall
            // through to the store-based behaviour; telemetry must never break a run.
            _diagnostics.WriteLine(
                $"telemetry: {TelemetryInstallId.EnvVar} must be a non-empty, "
                + "non-all-zero GUID — ignored.");
        }
        else if (envId is { } && transportConfigured)
        {
            _environmentInstallId = envId;
        }
    }

    /// <summary>
    /// Creates the production hook over the real per-user <see cref="DefaultTelemetryPaths"/>.
    /// The transport is a <see cref="LocalFileTelemetrySink"/> by default; when the HTTP
    /// transport is configured (<see cref="TelemetryTransportOptions.IsConfigured"/>) it is
    /// wrapped in a <see cref="DrainingTelemetrySink"/> that drains the outbox best-effort
    /// over HTTP and enforces the outbox cap (S12-G-01).
    /// </summary>
    /// <param name="noTelemetryFlag">The <c>--no-telemetry</c> run flag.</param>
    /// <param name="diagnostics">The diagnostics writer (e.g. <c>Console.Error</c>).</param>
    /// <returns>A ready production hook.</returns>
    public static TelemetryRunHook CreateDefault(bool noTelemetryFlag, TextWriter diagnostics)
    {
        var paths = new DefaultTelemetryPaths();
        var local = new LocalFileTelemetrySink(paths);

        // The single transport wiring seam (S12-G-01): an UNCONFIGURED endpoint keeps the
        // bare local sink (zero behaviour change — criterion 6); a CONFIGURED endpoint wraps
        // it so the outbox is drained over HTTP and capped.  When configured but the endpoint
        // is unreachable, the draining sink is fully fail-silent and falls back to local-only
        // behaviour, so wrapping is always safe.
        var options = TelemetryTransportOptions.FromEnvironment();
        ITelemetrySink sink = options.IsConfigured
            ? new DrainingTelemetrySink(
                local,
                new HttpOutboxClient(SharedHttpClient, options.Endpoint!, options.Token!),
                paths,
                OutboxCap.FromOptions(options))
            : local;

        return new TelemetryRunHook(
            new TelemetryConsentStore(paths),
            paths,
            sink,
            noTelemetryFlag,
            diagnostics,
            Environment.GetEnvironmentVariable(TelemetryInstallId.EnvVar),
            options.IsConfigured);
    }

    // A single shared HttpClient for the process: HttpClient is designed to be reused, and
    // the draining sink runs once per `vouchfx run` so one instance is ample.  The bearer
    // token is NEVER set as a default header here — HttpOutboxClient attaches it per request,
    // header-only, so it is never logged or echoed (§17).
    private static readonly HttpClient SharedHttpClient = new();

    /// <summary>
    /// Whether this run is permitted to emit telemetry: consent is
    /// <see cref="TelemetryConsent.Enabled"/> AND no opt-out signal is present.  Reads
    /// the consent store and the <see cref="TelemetrySuppression.OptOutEnvVar"/>.
    /// Returns <see langword="false"/> on any error (fail-closed).
    /// </summary>
    public bool ShouldEmit()
    {
        try
        {
            // ENVIRONMENT-CONFIGURED mode: all three VOUCHFX_TELEMETRY_* variables present
            // and valid IS the opt-in for this environment (CI) — the local consent store
            // is not consulted.  The --no-telemetry flag and VOUCHFX_NO_TELEMETRY keep
            // absolute precedence via the same suppression conjunction.
            var consent = _environmentInstallId is not null
                ? TelemetryConsent.Enabled
                : _store.Read().Consent;
            var envOptOut = Environment.GetEnvironmentVariable(TelemetrySuppression.OptOutEnvVar);
            return TelemetrySuppression.ShouldEmit(consent, _noTelemetryFlag, envOptOut);
        }
        catch (Exception ex)
        {
            // Fail-closed: any error in the consent path means DO NOT emit.
            _diagnostics.WriteLine($"telemetry: consent check skipped ({ex.GetType().Name}).");
            return false;
        }
    }

    /// <summary>
    /// Shows the one-time first-run notice when consent is Undecided and it has not
    /// been shown before.  Never throws.
    /// </summary>
    /// <param name="stderr">The writer the notice is written to (stderr by convention).</param>
    public void MaybeShowFirstRunNotice(TextWriter stderr)
    {
        try
        {
            if (_environmentInstallId is not null)
            {
                // Environment-configured telemetry is active: the store-based "you have
                // not decided yet" notice would be misleading (and, on ephemeral CI
                // runners, would nag on every run because noticeShown never persists).
                return;
            }

            _store.ShowFirstRunNoticeIfNeeded(stderr);
        }
        catch (Exception ex)
        {
            // The notice must never break the run; swallow and continue.
            _diagnostics.WriteLine($"telemetry: notice skipped ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Resolves the events-file path the runner should write so the hook can later read
    /// the buffered event stream back: the user's own <c>--events</c> path when set
    /// (no extra write), otherwise a fresh private temp path.  Returns
    /// <see langword="null"/> when telemetry will not emit OR when the temp path could
    /// not be created — in which case the runner writes no telemetry-driven events file.
    /// </summary>
    /// <param name="userEventsPath">
    /// The path the user passed via <c>--events</c> / <c>--json</c> (or
    /// <see langword="null"/> if they did not).
    /// </param>
    /// <returns>
    /// A tuple of the events path the runner should use and whether it is a private
    /// temp file the hook owns (and must delete).  A <see langword="null"/> path means
    /// "do not collect for telemetry".
    /// </returns>
    public (string? EventsPath, bool IsTempFile) ResolveEventsCapturePath(string? userEventsPath)
    {
        if (!ShouldEmit())
        {
            // No emission → never even ask the runner to write an events file for us.
            return (userEventsPath, false);
        }

        if (!string.IsNullOrEmpty(userEventsPath))
        {
            // The user already gets the verbatim stream; reuse it, no temp file.
            return (userEventsPath, false);
        }

        try
        {
            // A private temp path the runner writes the verbatim buffer to and the hook
            // reads back, then deletes.  Created under the system temp dir, not the
            // user's working tree.
            var temp = Path.Combine(
                Path.GetTempPath(),
                $"vouchfx-telemetry-{Guid.NewGuid():n}.jsonl");
            return (temp, true);
        }
        catch (Exception ex)
        {
            _diagnostics.WriteLine($"telemetry: capture path skipped ({ex.GetType().Name}).");
            return (null, false);
        }
    }

    /// <summary>
    /// After the run: reads the buffered event stream back from
    /// <paramref name="eventsPath"/>, builds the allowlisted
    /// <see cref="TelemetryEvent"/>, and appends it to the local outbox sink.  Deletes
    /// the events file afterwards IF it is the hook-owned temp file
    /// (<paramref name="isTempFile"/>).  Never throws.
    /// </summary>
    /// <param name="eventsPath">
    /// The events file the runner wrote (the verbatim buffered stream), or
    /// <see langword="null"/> when no capture happened (then this is a no-op).
    /// </param>
    /// <param name="isTempFile">
    /// <see langword="true"/> when <paramref name="eventsPath"/> is the hook's private
    /// temp file and should be deleted after reading; <see langword="false"/> when it is
    /// the user's own <c>--events</c> file (left in place).
    /// </param>
    /// <param name="cancellationToken">Propagated to the async sink write.</param>
    public async Task EmitAsync(
        string? eventsPath,
        bool isTempFile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(eventsPath))
        {
            return;
        }

        try
        {
            // Re-check emission at write time too (defence in depth: consent could be
            // anything, and ShouldEmit is the single gate).
            if (!ShouldEmit())
            {
                return;
            }

            if (!File.Exists(eventsPath))
            {
                return;
            }

            var lines = await File.ReadAllLinesAsync(eventsPath, cancellationToken)
                .ConfigureAwait(false);

            // Environment-configured mode supplies the id directly (stable per repo);
            // otherwise the id comes from the local consent store as before.
            var installId = _environmentInstallId ?? _store.Read().InstallId;
            if (installId is not { } id)
            {
                // Enabled but no install id (should not happen) → do not emit an
                // unidentified event.
                return;
            }

            var telemetryEvent = TelemetryEventBuilder.Build(
                lines,
                id,
                TelemetryVersions.ToolVersion(Assembly.GetExecutingAssembly()),
                TelemetryVersions.EngineVersion(),
                TelemetryVersions.DotnetVersion(),
                DateTimeOffset.UtcNow);

            await _sink.SendAsync(telemetryEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Telemetry must be invisible to the run: swallow EVERYTHING, leaving only a
            // redaction-safe, type-name-only notice on the diagnostics sink.
            _diagnostics.WriteLine($"telemetry: emit skipped ({ex.GetType().Name}).");
        }
        finally
        {
            if (isTempFile)
            {
                TryDeleteTemp(eventsPath);
            }
        }
    }

    private void TryDeleteTemp(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or NotSupportedException
                or ArgumentException)
        {
            _diagnostics.WriteLine($"telemetry: temp cleanup skipped ({ex.GetType().Name}).");
        }
    }
}
