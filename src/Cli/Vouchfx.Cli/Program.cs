// Vouchfx.Cli — Program (S07-C-01; watch teardown budget S08-T10).
//
// Entry point for the `vouchfx` executable. Builds the root command (with the `run`
// subcommand) and dispatches via System.CommandLine 2.0.x GA:
//   rootCommand.Parse(args).InvokeAsync(config, ct).
//
// System.CommandLine resolves --help / --version and parse errors itself (exit code 2 on
// a parse error). The `run` action returns the suite exit code (see ExitCodes).
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

// Ctrl-C teardown budget (S08-T10, S1): System.CommandLine's DEFAULT ProcessTerminationTimeout
// is ~2s — after that it force-kills the process even mid-DisposeAsync.  Watch mode keeps an
// Aspire topology alive across re-runs; a force-kill during its teardown LEAKS containers (the
// repo's known teardown-leak gotcha).  So for the watch path we DISABLE the timeout (null), giving
// the kept SuiteTopology a real teardown budget on Ctrl-C; every other path keeps the default.
var invocationConfiguration = new InvocationConfiguration();
if (IsWatchInvocation(args))
{
    invocationConfiguration.ProcessTerminationTimeout = null;
}

return await rootCommand.Parse(args).InvokeAsync(invocationConfiguration);

// Whether this invocation is a watch run (`--watch` present anywhere in the args).  A bare
// token scan is sufficient: --watch is the engine's only long-lived (kept-topology) mode, so a
// false positive only widens the teardown budget — never the wrong behaviour.
static bool IsWatchInvocation(string[] args) =>
    Array.Exists(args, a => string.Equals(a, "--watch", StringComparison.Ordinal));
