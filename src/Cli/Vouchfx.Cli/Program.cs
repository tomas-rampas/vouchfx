// Vouchfx.Cli — Program (S07-C-01; watch teardown budget S08-T10; `run`-scoped teardown budget
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

// Ctrl-C / SIGTERM teardown budget (S08-T10, S1; widened for vouchfx-mcp#17; 20s→30s AND scoped
// to `run` only per peer review): System.CommandLine's DEFAULT ProcessTerminationTimeout is ~2s
// — after that it force-kills the process even mid-DisposeAsync.  HeadlessTopology.DisposeAsync
// (reached via SuiteTopology — the engine's single teardown chokepoint, §4.5) calls
// _app.StopAsync with a fresh, bounded 15s CTS (WaitForResourceCleanup=true) to synchronously
// delete containers + the aspire-session-network-* network, THEN unconditionally calls
// _app.DisposeAsync() — a second, separate wait that DCP's own internal dispose/cleanup path can
// itself take up to ~10s to complete.  A ~2s force-kill guillotines any of that mid-flight and
// LEAKS containers on EVERY genuine Ctrl-C / SIGTERM during `vouchfx run` — not just watch mode's
// already-known teardown-leak gotcha.
//   - ONLY `run` stands up a container topology (via ScenarioRunner/ParallelSuiteRunner/WatchRunner)
//     — `validate` / `list` / `telemetry` (and a bare `--help` / `--version`) never touch Docker
//     or HeadlessTopology, so NONE of them needs (or should get) a widened budget.  Peer-review
//     finding: an earlier version of this gate widened the budget for EVERY non-watch invocation
//     (gating only on IsWatchInvocation), so a Ctrl-C on a slow `validate` would ALSO wait up to
//     the widened budget instead of the ~2s default — an accidental, unjustified widening this
//     scoping removes.  Only a `run` invocation touches ProcessTerminationTimeout at all; every
//     other subcommand is left at System.CommandLine's own default, exactly as before this
//     feature existed.
//   - Within `run`: watch mode keeps an Aspire topology alive across re-runs indefinitely, so it
//     still DISABLES the timeout entirely (null) — unchanged from before.  A plain `run` (whether
//     stopped by a human's Ctrl-C or by a host process's SIGTERM — e.g. an MCP server that spawns
//     this CLI) gets a BOUNDED, FINITE budget instead of the ~2s default. An earlier value here
//     was 20s — comfortably above the 15s StopAsync bound ALONE, but not above the PATHOLOGICAL
//     sum of that 15s StopAsync wait plus the subsequent ~10s DisposeAsync wait (~25s worst
//     case), leaving a narrow re-leak window. RunCommand.TeardownBudgetSeconds (30) clears that
//     combined worst case with headroom while staying deliberately NOT null, so a genuinely
//     wedged run still eventually gets force-killed rather than hanging forever. THE SAME
//     constant also bounds ShutdownBackstop — the stdin-EOF graceful-shutdown seam's own
//     force-exit timer (RunCommand.ExecuteAsync) — so both termination paths give a wedged `run`
//     an identical grace period.
var nonWatchTeardownBudget = TimeSpan.FromSeconds(RunCommand.TeardownBudgetSeconds);

// IsRunInvocation / IsWatchInvocation are factored into InvocationScope (not local functions
// here) specifically so they are directly unit-testable across assemblies — see its remarks.
var invocationConfiguration = new InvocationConfiguration();
if (InvocationScope.IsRunInvocation(args))
{
    invocationConfiguration.ProcessTerminationTimeout = InvocationScope.IsWatchInvocation(args)
        ? null
        : nonWatchTeardownBudget;
}

// Any NON-run invocation (validate / list / telemetry / a bare --help / --version) deliberately
// leaves ProcessTerminationTimeout untouched — System.CommandLine's own ~2s default applies,
// exactly as it did before this feature existed.

return await rootCommand.Parse(args).InvokeAsync(invocationConfiguration);
