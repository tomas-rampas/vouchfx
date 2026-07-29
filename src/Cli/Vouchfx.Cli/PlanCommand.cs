// Vouchfx.Cli — PlanCommand (M3 Planner / planner-coverage-and-gap-report, REQ-002, REQ-010,
// EDGE-008, EDGE-009).
//
// The `vouchfx plan <path> [--events <path>] [--json] [--output <path>] [--fail-on-gap]
// [--stale-days N] [--flaky-min-runs N] [--fragile-min-env-errors N] [--inconclusive-min N]`
// subcommand: runs the Planner's coverage-and-gap analysis (Vouchfx.Engine.Planning.PlanExport)
// over a declared suite set, an optional event history, and the frozen Core StepKindRegistry,
// and renders either a human summary (default) or the schema-versioned JSON report
// (`--json`). `--output` always writes the same JSON document to a file, regardless of
// whether `--json` was passed — mirroring `schema`/`scaffold`'s `--output` semantics.
//
// Exit codes (REQ-010):
//   0 (ExitCodes.Success)     — a successful analysis, REGARDLESS of how many gaps were
//                               found (gaps are data — mirrors §12.1's "only a genuine Fail
//                               breaks CI"). Also the code when `--fail-on-gap` is set but
//                               no PlanFindingKinds.IsGap finding is present.
//   2 (ExitCodes.UsageError)  — PlanInputException (bad/missing suite path, EDGE-009 empty
//                               folder, or an out-of-range threshold — MINOR fix-round, e.g.
//                               --stale-days -1) or an --output path whose parent directory
//                               does not exist (EDGE-008).
//   3 (ExitCodes.EnvironmentError) — CatalogueExportException: incomplete catalogue metadata
//                               (a registered provider lacks a schema fragment) — the SAME
//                               fail-closed class `vouchfx list` / `vouchfx schema` map to
//                               exit 3.
//   5 (ExitCodes.GapsFound)   — ONLY when `--fail-on-gap` is set AND at least one finding
//                               satisfies PlanFindingKinds.IsGap. Never derived independently
//                               of that predicate (do not re-implement the gap set here).
//
// Docker-free: only freezes the Core StepKindRegistry and runs the Planner's read-only,
// deterministic analysis (Vouchfx.Engine.Planning — no model API, no suite-file writes).

using System.CommandLine;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Planning;
using Vouchfx.Engine.Planning.Report;
using Vouchfx.Sdk;

namespace Vouchfx.Cli;

/// <summary>
/// Builds and executes the <c>plan</c> subcommand.
/// </summary>
internal static class PlanCommand
{
    /// <summary>
    /// Builds the <c>plan</c> <see cref="Command"/>, wiring its async action to
    /// <see cref="Execute"/>.
    /// </summary>
    /// <returns>The configured <c>plan</c> command, ready to add to the root.</returns>
    public static Command Build()
    {
        var command = new Command(
            "plan",
            "Run the Planner's deterministic, read-only coverage-and-gap analysis over a "
            + "declared .e2e.yaml suite set, an optional event history, and this build's "
            + "live step catalogue. Reports which declared suites/steps/dependencies were "
            + "never exercised, which registered step type would assert a seam that is "
            + "exercised but never verified, and which steps have become stale/flaky/"
            + "fragile/inconclusive-prone. Never writes a suite file, never calls a model. "
            + "Default output is a human summary on stdout; use --json for the "
            + "schema-versioned JSON report, or --output to write it to a file.");

        var suitePathArgument = BuildSuitePathArgument();
        command.Add(suitePathArgument);

        var eventsOption = BuildEventsOption();
        command.Add(eventsOption);

        var jsonOption = BuildJsonOption();
        command.Add(jsonOption);

        var outputOption = BuildOutputOption();
        command.Add(outputOption);

        var failOnGapOption = BuildFailOnGapOption();
        command.Add(failOnGapOption);

        var staleDaysOption = BuildStaleDaysOption();
        command.Add(staleDaysOption);

        var flakyMinRunsOption = BuildFlakyMinRunsOption();
        command.Add(flakyMinRunsOption);

        var fragileMinEnvErrorsOption = BuildFragileMinEnvErrorsOption();
        command.Add(fragileMinEnvErrorsOption);

        var inconclusiveMinOption = BuildInconclusiveMinOption();
        command.Add(inconclusiveMinOption);

        command.SetAction((parseResult, _) =>
        {
            var suitePath = parseResult.GetValue(suitePathArgument) ?? ".";
            var eventsPath = parseResult.GetValue(eventsOption);
            var json = parseResult.GetValue(jsonOption);
            var outputPath = parseResult.GetValue(outputOption);
            var failOnGap = parseResult.GetValue(failOnGapOption);
            var thresholds = new PlanThresholds(
                StaleDays: parseResult.GetValue(staleDaysOption),
                FlakyMinRuns: parseResult.GetValue(flakyMinRunsOption),
                FragileMinEnvErrors: parseResult.GetValue(fragileMinEnvErrorsOption),
                InconclusiveMin: parseResult.GetValue(inconclusiveMinOption));

            return Task.FromResult(Execute(
                suitePath,
                eventsPath,
                json,
                outputPath,
                failOnGap,
                thresholds,
                Console.Out,
                Console.Error));
        });

        return command;
    }

    /// <summary>
    /// The positional <c>&lt;path&gt;</c> argument: the declared suite set to analyse.
    /// Defaults to the current directory, mirroring <see cref="ValidateCommand.BuildPathArgument"/>
    /// / <see cref="RunCommand.BuildPathArgument"/>.
    /// </summary>
    internal static Argument<string> BuildSuitePathArgument() => new("path")
    {
        Description = "Directory to search recursively for *.e2e.yaml suites, or a single "
            + "*.e2e.yaml file — the declared universe the Planner analyses. Defaults to "
            + "'.'. An empty directory (zero discovered suites) is a usage error (exit 2): "
            + "there is no declared universe to analyse, which is a configuration problem, "
            + "not a finding.",
        DefaultValueFactory = _ => ".",
    };

    /// <summary>
    /// The optional <c>--events &lt;path&gt;</c> option: the exercised-reality input (a JSON
    /// Lines event history file, or a directory of such files).
    /// </summary>
    /// <remarks>
    /// <c>--events</c> means the OPPOSITE thing here to what it means on <c>run</c>: on
    /// <c>run</c>, <c>--events</c> (aliased <c>--json</c>) is an OUTPUT path the runner
    /// writes its raw event stream to; here it is an INPUT the Planner reads. The two
    /// commands never share a wiring — this option intentionally has no <c>--json</c> alias
    /// (this command's own <c>--json</c> is an unrelated boolean output-format flag; see
    /// <see cref="BuildJsonOption"/>). The help text below states the direction explicitly
    /// so the two meanings are never conflated.
    /// </remarks>
    internal static Option<string?> BuildEventsOption() => new("--events")
    {
        Description = "Path to a JSON Lines event history file, or a directory of *.jsonl "
            + "files — the artefact `vouchfx run --events` writes. Omit for no history: "
            + "every declared suite/step is then reported never-run, and history-health "
            + "findings are empty (a valid, successful analysis, not an error).",
    };

    /// <summary>
    /// The <c>--json</c> flag: emit the schema-versioned JSON report document to stdout
    /// instead of the human-readable summary.
    /// </summary>
    internal static Option<bool> BuildJsonOption() => new("--json")
    {
        Description = "Emit the schema-versioned JSON coverage-and-gap report document to "
            + "stdout instead of the human-readable summary.",
    };

    /// <summary>
    /// The optional <c>--output &lt;path&gt;</c> option: ALWAYS writes the JSON report
    /// document to this file, regardless of whether <c>--json</c> was also passed — mirrors
    /// <c>schema</c>/<c>scaffold</c>'s <c>--output</c> semantics. <c>--json</c> controls
    /// stdout only.
    /// </summary>
    internal static Option<string?> BuildOutputOption() => new("--output")
    {
        Description = "Write the JSON coverage-and-gap report document to this file path — "
            + "byte-identical to what --json would print to stdout — regardless of whether "
            + "--json was also passed (--json controls stdout only). The parent directory "
            + "must already exist.",
    };

    /// <summary>
    /// The <c>--fail-on-gap</c> flag (REQ-010): opt in to a non-zero exit
    /// (<see cref="ExitCodes.GapsFound"/>, 5) when the report contains at least one
    /// <see cref="PlanFindingKinds.IsGap"/> finding. Absent, a successful analysis always
    /// exits 0 regardless of how many gaps were found — gaps are data.
    /// </summary>
    internal static Option<bool> BuildFailOnGapOption() => new("--fail-on-gap")
    {
        Description = "Exit with code 5 when the report contains at least one coverage or "
            + "vocabulary gap finding (history-health and suite-identity-ambiguity findings "
            + "never count). Omit to always exit 0 on a successful analysis regardless of "
            + "how many gaps were found — gaps are data, mirroring the verdict taxonomy's "
            + "'only a genuine Fail breaks CI' rule.",
    };

    /// <summary>
    /// The <c>--stale-days</c> threshold override (REQ-006). Default from
    /// <see cref="PlanThresholds.Defaults"/> — not re-declared here.
    /// </summary>
    internal static Option<int> BuildStaleDaysOption() => new("--stale-days")
    {
        Description = "A step is 'stale' when its last observed step-completed event is "
            + $"more than this many days before the newest event in the analysed history. "
            + $"Default {PlanThresholds.Defaults.StaleDays}.",
        DefaultValueFactory = _ => PlanThresholds.Defaults.StaleDays,
    };

    /// <summary>
    /// The <c>--flaky-min-runs</c> threshold override (REQ-006). Default from
    /// <see cref="PlanThresholds.Defaults"/> — not re-declared here.
    /// </summary>
    internal static Option<int> BuildFlakyMinRunsOption() => new("--flaky-min-runs")
    {
        Description = "A step is 'flaky' when it shows at least one Pass and at least one "
            + "Fail step-completed verdict across at least this many distinct runIds. "
            + $"Default {PlanThresholds.Defaults.FlakyMinRuns}.",
        DefaultValueFactory = _ => PlanThresholds.Defaults.FlakyMinRuns,
    };

    /// <summary>
    /// The <c>--fragile-min-env-errors</c> threshold override (REQ-006). Default from
    /// <see cref="PlanThresholds.Defaults"/> — not re-declared here.
    /// </summary>
    internal static Option<int> BuildFragileMinEnvErrorsOption() => new("--fragile-min-env-errors")
    {
        Description = "A step is 'fragile' when it shows at least this many "
            + "EnvironmentError step-completed outcomes. "
            + $"Default {PlanThresholds.Defaults.FragileMinEnvErrors}.",
        DefaultValueFactory = _ => PlanThresholds.Defaults.FragileMinEnvErrors,
    };

    /// <summary>
    /// The <c>--inconclusive-min</c> threshold override (REQ-006). Default from
    /// <see cref="PlanThresholds.Defaults"/> — not re-declared here.
    /// </summary>
    internal static Option<int> BuildInconclusiveMinOption() => new("--inconclusive-min")
    {
        Description = "A step is 'inconclusive-prone' when it shows at least this many "
            + "Inconclusive step-completed outcomes. "
            + $"Default {PlanThresholds.Defaults.InconclusiveMin}.",
        DefaultValueFactory = _ => PlanThresholds.Defaults.InconclusiveMin,
    };

    /// <summary>
    /// The Docker-free orchestration of a <c>plan</c> invocation: freezes the Core registry
    /// (unless <paramref name="registry"/> is supplied), runs
    /// <see cref="PlanExport.BuildPlan"/>, writes <paramref name="outputPath"/> when given,
    /// renders stdout, and maps the result to a taxonomy-aware exit code.
    /// </summary>
    /// <param name="suitePath">The declared suite set to analyse (file or directory).</param>
    /// <param name="eventsPath">Optional event-history file or directory (see <see cref="BuildEventsOption"/>).</param>
    /// <param name="json">When <see langword="true"/>, emit the JSON report to stdout instead of the human summary.</param>
    /// <param name="outputPath">
    /// When non-null/non-empty, ALWAYS write the JSON report document to this path —
    /// regardless of <paramref name="json"/> — byte-identical to <see cref="PlanExport.SerializePlan"/>'s
    /// output. A missing parent directory is a usage error (EDGE-008); no partial file is written.
    /// </param>
    /// <param name="failOnGap">See <see cref="BuildFailOnGapOption"/>.</param>
    /// <param name="thresholds">The effective history-health thresholds for this analysis.</param>
    /// <param name="output">The writer that receives the human summary or the <c>--json</c> document (stdout).</param>
    /// <param name="errorOutput">The writer that receives usage-error / catalogue-error diagnostics (stderr).</param>
    /// <param name="registry">
    /// The frozen provider registry to analyse against. <see langword="null"/> (the
    /// production default) builds the real Core registry via
    /// <see cref="ProviderRegistryFactory.BuildCoreRegistry"/>. Exposed so tests can supply a
    /// deliberately incomplete registry to exercise the <see cref="CatalogueExportException"/>
    /// → exit 3 path without touching the shipped Core providers.
    /// </param>
    /// <returns>
    /// <see cref="ExitCodes.Success"/> (0) on a successful analysis, regardless of how many
    /// gaps were found (unless <paramref name="failOnGap"/> promotes a gap-bearing result to
    /// <see cref="ExitCodes.GapsFound"/>); <see cref="ExitCodes.UsageError"/> (2) on a bad
    /// suite path, an empty suite folder (EDGE-009), or a missing <paramref name="outputPath"/>
    /// parent directory (EDGE-008); <see cref="ExitCodes.EnvironmentError"/> (3) on incomplete
    /// catalogue metadata; <see cref="ExitCodes.GapsFound"/> (5) per
    /// <paramref name="failOnGap"/> / <see cref="ExitCodeFor"/>.
    /// </returns>
    /// <remarks>
    /// Exposed as <see langword="internal"/> so the no-docker CLI tests can assert the full
    /// REQ-010 exit-code matrix without going through <see cref="System.CommandLine"/> parsing.
    /// </remarks>
    internal static int Execute(
        string suitePath,
        string? eventsPath,
        bool json,
        string? outputPath,
        bool failOnGap,
        PlanThresholds thresholds,
        TextWriter output,
        TextWriter errorOutput,
        StepKindRegistry? registry = null)
    {
        registry ??= ProviderRegistryFactory.BuildCoreRegistry();

        PlanReportDocument document;
        try
        {
            var request = new PlanRequest(suitePath, eventsPath, thresholds);
            document = PlanExport.BuildPlan(request, registry, CliJsonContract.EngineVersion);
        }
        catch (PlanInputException ex)
        {
            // Bad/missing suite path, or zero discovered suites (EDGE-009) — a usage error,
            // the caller's actionable mistake.
            errorOutput.WriteLine(ex.Message);
            return ExitCodes.UsageError;
        }
        catch (CatalogueExportException ex)
        {
            // Incomplete catalogue metadata (a registered provider lacks a schema fragment)
            // fails closed — the SAME class of failure `vouchfx list` / `vouchfx schema` map
            // to exit 3.
            errorOutput.WriteLine(ex.Message);
            return ExitCodes.EnvironmentError;
        }

        // The library's own canonical serialisation — used for BOTH --json stdout and
        // --output, so the two are byte-identical to each other and to
        // PlanExport.SerializePlan by construction (REQ-001 CLI<->library parity; F-10).
        var reportJson = PlanExport.SerializePlan(document);

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            try
            {
                var fullPath = Path.GetFullPath(outputPath);
                var parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    // EDGE-008: fail clearly, write no partial file.
                    errorOutput.WriteLine(
                        $"Cannot write plan report: parent directory does not exist: '{parent}'.");
                    return ExitCodes.UsageError;
                }

                File.WriteAllText(fullPath, reportJson);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or DirectoryNotFoundException
                or PathTooLongException
                or NotSupportedException
                or ArgumentException
                or System.Security.SecurityException)
            {
                errorOutput.WriteLine($"Cannot write plan report to '{outputPath}': {ex.Message}");
                return ExitCodes.UsageError;
            }
        }

        if (json)
        {
            output.Write(reportJson);
        }
        else
        {
            WriteHumanSummary(document, failOnGap, output);
        }

        return ExitCodeFor(document, failOnGap);
    }

    /// <summary>
    /// Maps a completed <see cref="PlanReportDocument"/> to the REQ-010 exit code, in
    /// isolation from <see cref="PlanExport.BuildPlan"/> — mirroring
    /// <see cref="ExitCodes.FromVerdict"/>'s own pure, independently-testable seam. Tests can
    /// therefore exercise the gap/no-gap and fail-on-gap/not-fail-on-gap matrix against a
    /// hand-built synthetic <see cref="PlanReportDocument"/> without a real
    /// <see cref="PlanExport.BuildPlan"/> call.
    /// </summary>
    /// <param name="document">The completed report.</param>
    /// <param name="failOnGap">See <see cref="BuildFailOnGapOption"/>.</param>
    /// <returns>
    /// <see cref="ExitCodes.GapsFound"/> (5) when <paramref name="failOnGap"/> is
    /// <see langword="true"/> AND at least one finding satisfies
    /// <see cref="PlanFindingKinds.IsGap"/>; otherwise <see cref="ExitCodes.Success"/> (0).
    /// Uses <see cref="PlanFindingKinds.IsGap"/> directly — the gap set is never re-derived
    /// here.
    /// </returns>
    internal static int ExitCodeFor(PlanReportDocument document, bool failOnGap)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (failOnGap && document.Findings.Any(f => PlanFindingKinds.IsGap(f.Kind)))
            return ExitCodes.GapsFound;

        return ExitCodes.Success;
    }

    /// <summary>
    /// Renders the human-readable summary: the REQ-003 inventory counts, a finding-count
    /// breakdown by kind, and — when <paramref name="failOnGap"/> is set — an explicit note
    /// naming the gap count that drives the exit code.
    /// </summary>
    private static void WriteHumanSummary(PlanReportDocument document, bool failOnGap, TextWriter output)
    {
        var inventory = document.Inventory;

        output.WriteLine($"Suites analysed:         {inventory.Suites.Count} ({inventory.UnanalysableSuites.Count} unanalysable)");
        output.WriteLine($"Services declared:       {inventory.Services.Count}");
        output.WriteLine($"Dependencies declared:   {inventory.Dependencies.Count} ({inventory.UnmappableDependencies.Count} unmappable)");
        output.WriteLine($"Distinct step types:     {inventory.StepTypes.Count}");
        output.WriteLine($"Runs analysed:           {inventory.RunCount}");
        output.WriteLine($"Event history span:      {FormatTimestamp(inventory.FirstEventTs)} .. {FormatTimestamp(inventory.LastEventTs)}");
        output.WriteLine($"Skipped event lines:     {inventory.SkippedEventLines}");
        output.WriteLine($"Unmatched observations:  {inventory.UnmatchedObservations}");
        output.WriteLine(
            $"Thresholds:              stale>{document.Thresholds.StaleDays}d, "
            + $"flaky>={document.Thresholds.FlakyMinRuns} runs, "
            + $"fragile>={document.Thresholds.FragileMinEnvErrors} env-errors, "
            + $"inconclusive>={document.Thresholds.InconclusiveMin}");
        output.WriteLine();

        var gapCount = document.Findings.Count(f => PlanFindingKinds.IsGap(f.Kind));
        output.WriteLine($"Findings: {document.Findings.Count} total ({gapCount} gap(s)).");

        foreach (var kind in PlanFindingKinds.All)
        {
            var count = document.Findings.Count(f => f.Kind == kind);
            if (count > 0)
                output.WriteLine($"  {kind,-32} {count}");
        }

        if (failOnGap && gapCount > 0)
        {
            output.WriteLine();
            output.WriteLine(
                $"--fail-on-gap: {gapCount} gap finding(s) present — exiting {ExitCodes.GapsFound}.");
        }
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp?.ToString("O") ?? "n/a";
}
