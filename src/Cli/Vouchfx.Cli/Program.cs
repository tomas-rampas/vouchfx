// Vouchfx.Cli — Program (S07-C-01; watch teardown budget S08-T10; non-watch teardown budget
// widened for vouchfx-mcp#17).
//
// Entry point for the `vouchfx` executable. Builds the root command (with the `run`
// subcommand) and dispatches via System.CommandLine 2.0.x GA:
//   rootCommand.Parse(args).InvokeAsync(config, ct).
//
// System.CommandLine resolves --help / --version and parse errors itself (its default:
// exit code 1 on a parse error such as an unrecognised option). A subcommand action returns
// the app's taxonomy-aware exit code — see ExitCodes (e.g. 2 for the app's own usage errors
// such as a bad or missing path, which are detected in the action, not by the parser).
//
// This assembly is the Aspire host (Aspire.AppHost.Sdk + IsAspireHost in the csproj); its
// name "vouchfx" is what RunCommand passes to ScenarioRunner.RunSuiteAsync as
// appHostAssemblyName, so DCP metadata resolves to THIS executable.

using System.CommandLine;
using Vouchfx.Cli;

var rootCommand = new RootCommand(
    "vouchfx — compile and run declarative .e2e.yaml integration tests end-to-end.");

rootCommand.Add(RunCommand.Build());

// Opt-in, privacy-first telemetry consent surface (S10-G-04):
//   vouchfx telemetry enable|disable|status
rootCommand.Add(TelemetryCommand.Build());

// Topology-free compile-validation (#260):
//   vouchfx validate [<path>] [--json]
rootCommand.Add(ValidateCommand.Build());

// Catalogue listing (#260):
//   vouchfx list [--step-types] [--json]
rootCommand.Add(ListCommand.Build());

// Ctrl-C / SIGTERM teardown budget (S08-T10, S1; widened for vouchfx-mcp#17; 20s→30s per
// security review): System.CommandLine's DEFAULT ProcessTerminationTimeout is ~2s — after that
// it force-kills the process even mid-DisposeAsync.  HeadlessTopology.DisposeAsync (reached via
// SuiteTopology — the engine's single teardown chokepoint, §4.5) calls _app.StopAsync with a
// fresh, bounded 15s CTS (WaitForResourceCleanup=true) to synchronously delete containers + the
// aspire-session-network-* network, THEN unconditionally calls _app.DisposeAsync() — a second,
// separate wait that DCP's own internal dispose/cleanup path can itself take up to ~10s to
// complete.  A ~2s force-kill guillotines any of that mid-flight and LEAKS containers on EVERY
// genuine Ctrl-C / SIGTERM during `vouchfx run` — not just watch mode's already-known
// teardown-leak gotcha.
//   - Watch mode keeps an Aspire topology alive across re-runs indefinitely, so it still DISABLES
//     the timeout entirely (null) — unchanged from before.
//   - Every OTHER path (a plain `run`, whether stopped by a human's Ctrl-C or by a host process's
//     SIGTERM — e.g. an MCP server that spawns this CLI) now gets a BOUNDED, FINITE budget instead
//     of the ~2s default. An earlier value here was 20s — comfortably above the 15s StopAsync
//     bound ALONE, but not above the PATHOLOGICAL sum of that 15s StopAsync wait plus the
//     subsequent ~10s DisposeAsync wait (~25s worst case), leaving a narrow re-leak window.
//     RunCommand.TeardownBudgetSeconds (30) clears that combined worst case with headroom while
//     staying deliberately NOT null, so a genuinely wedged run still eventually gets force-killed
//     rather than hanging forever. THE SAME constant also bounds ShutdownBackstop — the
//     stdin-EOF graceful-shutdown seam's own force-exit timer (RunCommand.ExecuteAsync) — so
//     both termination paths give a wedged run an identical grace period.
var nonWatchTeardownBudget = TimeSpan.FromSeconds(RunCommand.TeardownBudgetSeconds);

var invocationConfiguration = new InvocationConfiguration();
if (IsWatchInvocation(args))
{
    invocationConfiguration.ProcessTerminationTimeout = null;
}
else
{
    invocationConfiguration.ProcessTerminationTimeout = nonWatchTeardownBudget;
}

return await rootCommand.Parse(args).InvokeAsync(invocationConfiguration);

// Whether this invocation is a watch run (`--watch` present anywhere in the args).  A bare
// token scan is sufficient: --watch is the engine's only long-lived (kept-topology) mode, so a
// false positive only widens the teardown budget — never the wrong behaviour.
static bool IsWatchInvocation(string[] args) =>
    Array.Exists(args, a => string.Equals(a, "--watch", StringComparison.Ordinal));
