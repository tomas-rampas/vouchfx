// Vouchfx.Cli — Program (S07-C-01).
//
// Entry point for the `vouchfx` executable. Builds the root command (with the `run`
// subcommand) and dispatches via System.CommandLine 2.0.x GA:
//   rootCommand.Parse(args).InvokeAsync(ct).
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

// TODO(S08+): additional top-level subcommands (validate, list, …) attach here.

return await rootCommand.Parse(args).InvokeAsync();
