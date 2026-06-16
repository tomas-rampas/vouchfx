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
using Platform.Engine.Telemetry;

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

        disable.SetAction((_, _) =>
        {
            var store = new TelemetryConsentStore(new DefaultTelemetryPaths());
            store.Disable();
            Console.Out.WriteLine(
                "Telemetry DISABLED. The install id has been deleted and the local outbox "
                + "cleared. Nothing will be collected or sent.");
            return Task.FromResult(ExitCodes.Success);
        });

        return disable;
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
        return n.Length >= 8 ? n[..8] + "…" : n;
    }
}
