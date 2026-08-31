// Vouchfx.Cli — TelemetryCommand (S10-G-04).
//
// `vouchfx telemetry enable|disable|status`: the user's explicit consent surface for
// opt-in, privacy-first telemetry.  NOTHING is ever collected or sent until the user
// runs `telemetry enable`; `disable` opts out, deletes the install id, and clears the
// local outbox; `status` reports the current state WITHOUT printing the raw install id.
//
// All three subcommands are Docker-free and side-effect-scoped to the per-user config
// dir (via DefaultTelemetryPaths) — so they are simple, fast, and safe to run anywhere.

using System.CommandLine;
using System.Net.Http;
using Vouchfx.Engine.Telemetry;

namespace Vouchfx.Cli;

/// <summary>
/// Builds the <c>telemetry</c> subcommand group (<c>enable</c> / <c>disable</c> /
/// <c>status</c>) — the user's explicit opt-in consent surface (S10-G-04).
/// </summary>
/// <remarks>
/// Telemetry is OFF by default and opt-in: only <c>telemetry enable</c> can move the
/// stored consent to <see cref="TelemetryConsent.Enabled"/> (the sole state in which a
/// run may emit), and only then is an install id minted.  <c>telemetry disable</c>
/// deletes the install id and clears the local outbox immediately.
/// </remarks>
internal static class TelemetryCommand
{
    /// <summary>
    /// Builds the <c>telemetry</c> <see cref="Command"/> with its <c>enable</c>,
    /// <c>disable</c>, and <c>status</c> subcommands, each wired to operate on the
    /// real per-user consent store (<see cref="DefaultTelemetryPaths"/>).
    /// </summary>
    /// <returns>The configured <c>telemetry</c> command, ready to add to the root.</returns>
    public static Command Build()
    {
        var command = new Command(
            "telemetry",
            "Manage opt-in, privacy-first usage telemetry. Telemetry is OFF by default; nothing "
            + "is collected or sent until you run `telemetry enable`.");

        command.Add(BuildEnable());
        command.Add(BuildDisable());
        command.Add(BuildStatus());
        return command;
    }

    /// <summary>Builds <c>telemetry enable</c>.</summary>
    internal static Command BuildEnable()
    {
        var enable = new Command(
            "enable",
            "Opt IN to anonymous, aggregate usage telemetry. Mints an install id if one does "
            + "not exist and confirms the change.");

        enable.SetAction((_, _) =>
        {
            var store = new TelemetryConsentStore(new DefaultTelemetryPaths());
            var state = store.Enable();
            Console.Out.WriteLine(
                "Telemetry ENABLED. Anonymous, aggregate usage data (versions, verdict counts, "
                + "which built-in step kinds ran, startup timings) will be collected on each run. "
                + "Your test contents, captured values, secrets, URLs, image names, scenario "
                + "names and step ids are NEVER collected.");
            Console.Out.WriteLine(
                $"Install id: {ShortInstallId(state.InstallId)} (anonymous; identifies this "
                + "install only).");
            Console.Out.WriteLine("Opt out any time with: vouchfx telemetry disable");
            return Task.FromResult(ExitCodes.Success);
        });

        return enable;
    }

    /// <summary>Builds <c>telemetry disable</c>.</summary>
    internal static Command BuildDisable()
    {
        var disable = new Command(
            "disable",
            "Opt OUT of usage telemetry. Deletes the install id and clears the local outbox "
            + "immediately.");

        disable.SetAction(async (_, cancellationToken) =>
        {
            var paths = new DefaultTelemetryPaths();
            var store = new TelemetryConsentStore(paths);

            await DisableAsync(
                store,
                installId => TryForgetAsync(paths, installId, cancellationToken),
                Console.Out,
                cancellationToken).ConfigureAwait(false);

            return ExitCodes.Success;
        });

        return disable;
    }

    /// <summary>
    /// Runs the <c>telemetry disable</c> orchestration: read the install id, fire the
    /// best-effort backend <paramref name="forget"/> FIRST (so it can still read the id),
    /// then ALWAYS perform the local opt-out via <see cref="TelemetryConsentStore.Disable"/>.
    /// </summary>
    /// <param name="store">The consent store whose install id + outbox are cleared.</param>
    /// <param name="forget">
    /// The best-effort network forget for an install id (the production wiring posts to the
    /// backend; a test injects a fake).  It runs BEFORE the local delete clears the id.
    /// </param>
    /// <param name="output">The writer the confirmation line is written to.</param>
    /// <param name="cancellationToken">Cooperative cancellation (e.g. Ctrl-C).</param>
    /// <remarks>
    /// The local opt-out runs in a <c>finally</c>, so it ALWAYS happens — even when the
    /// advisory forget is cancelled, throws, or hangs.  Disabling is a privacy guarantee: a
    /// cancellation mid-forget must never leave telemetry enabled.  The forget itself is
    /// fail-silent (it swallows its own faults), so in practice the finally is reached
    /// cleanly; running the opt-out there is belt-and-braces against any unexpected throw.
    /// </remarks>
    internal static async Task DisableAsync(
        TelemetryConsentStore store,
        Func<Guid?, Task> forget,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(forget);
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            // BEFORE the local delete, fire the best-effort "forget" so the backend can erase
            // any data linked to this install (S12-G-01, criterion 3 — the client half).  It
            // runs FIRST so it can still read the install id (the local delete in the finally
            // clears it).  We do NOT guard the entry with ThrowIfCancellationRequested(): an
            // already-cancelled token must not abort `telemetry disable`, whose local opt-out
            // always succeeds.  The forget itself observes cancellation cooperatively (and the
            // production wrapper swallows OperationCanceledException), so a Ctrl-C interrupts
            // only the advisory network wait — never the local opt-out in the finally.
            var installId = store.Read().InstallId;
            await forget(installId).ConfigureAwait(false);
        }
        finally
        {
            // The LOCAL opt-out is the whole point of `telemetry disable` and a privacy
            // guarantee — it must ALWAYS run, even if the best-effort forget above was
            // cancelled (Ctrl-C), threw, or hung.  Running it in a finally means a
            // cancellation during the network forget can never leave telemetry enabled.
            store.Disable();
            output.WriteLine(
                "Telemetry DISABLED. The install id has been deleted and the local outbox "
                + "cleared. Nothing will be collected or sent.");
        }
    }

    // Fires a best-effort backend "forget" for the install id when the HTTP transport is
    // configured.  Fully fail-silent: any fault (offline endpoint, timeout, bad config) is
    // swallowed so the local opt-out (store.Disable) always proceeds.  No-op when telemetry
    // is unconfigured or no install id exists.  The token is carried header-only by
    // HttpOutboxClient and is never logged here.
    private static async Task TryForgetAsync(
        DefaultTelemetryPaths paths,
        Guid? installId,
        CancellationToken cancellationToken)
    {
        _ = paths; // reserved for future per-path config; the transport reads the environment.

        if (installId is not { } id)
        {
            return;
        }

        var options = TelemetryTransportOptions.FromEnvironment();
        if (!options.IsConfigured)
        {
            return;
        }

        // The forget orchestration (best-effort, fail-silent) lives in the engine's
        // OutboxForget so it is unit-tested directly; the CLI only owns the HttpClient
        // lifetime.  A swallowed fault returns false here and a one-line, redaction-safe
        // note is emitted (the exception TYPE name only — never the token/endpoint/id).
        //
        // CANCELLATION: unlike the engine drain (where a caller-requested cancel propagates),
        // this disable path swallows OperationCanceledException too.  Disabling is a privacy
        // command whose whole purpose is the LOCAL opt-out; a Ctrl-C during the advisory
        // network forget must NOT abort that opt-out.  The caller runs store.Disable() in a
        // finally regardless, and swallowing here lets `telemetry disable` still report
        // success cleanly instead of surfacing the cancel after the opt-out already happened.
        try
        {
            using var httpClient = new HttpClient();
            var client = new HttpOutboxClient(httpClient, options.Endpoint!, options.Token!);
            await OutboxForget.TryForgetAsync(client, id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is HttpRequestException
                or InvalidOperationException
                or OperationCanceledException
                or System.Net.Sockets.SocketException)
        {
            Console.Error.WriteLine($"telemetry: forget skipped ({ex.GetType().Name}).");
        }
    }

    /// <summary>Builds <c>telemetry status</c>.</summary>
    internal static Command BuildStatus()
    {
        var status = new Command(
            "status",
            "Show the current telemetry consent state, whether an install id exists, the outbox "
            + "path, and how to opt in / out.");

        status.SetAction((_, _) =>
        {
            var paths = new DefaultTelemetryPaths();
            var store = new TelemetryConsentStore(paths);
            var state = store.Read();

            Console.Out.WriteLine($"Telemetry consent : {DescribeConsent(state.Consent)}");
            // Report only WHETHER an install id exists (and a short, non-reversible prefix) —
            // never the full id, so `status` itself leaks no stable identifier to a log.
            Console.Out.WriteLine(
                state.InstallId is { } id
                    ? $"Install id        : present ({ShortInstallId(id)})"
                    : "Install id        : none");
            Console.Out.WriteLine($"Outbox path       : {paths.OutboxPath}");
            Console.Out.WriteLine("Opt in            : vouchfx telemetry enable");
            Console.Out.WriteLine(
                "Opt out           : vouchfx telemetry disable  (or set VOUCHFX_NO_TELEMETRY=1)");
            return Task.FromResult(ExitCodes.Success);
        });

        return status;
    }

    /// <summary>A human-readable label for a <see cref="TelemetryConsent"/> value.</summary>
    private static string DescribeConsent(TelemetryConsent consent) => consent switch
    {
        TelemetryConsent.Enabled => "enabled (opted in)",
        TelemetryConsent.Disabled => "disabled (opted out)",
        _ => "undecided (telemetry off; nothing collected)",
    };

    /// <summary>
    /// A short, non-reversible display form of an install id — the first 8 hex chars
    /// only — so neither <c>status</c> nor <c>enable</c> echoes the full identifier.
    /// </summary>
    private static string ShortInstallId(Guid? installId)
    {
        if (installId is not { } id)
        {
            return "none";
        }

        var n = id.ToString("n");
        return n.Length >= 8 ? n[..8] + "..." : n;
    }
}
