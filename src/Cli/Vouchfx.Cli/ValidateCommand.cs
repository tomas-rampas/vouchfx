// Vouchfx.Cli — ValidateCommand (#260).
//
// The `vouchfx validate [<path>] [--json]` subcommand: discovers *.e2e.yaml scenarios
// under <path> (a directory, or a single *.e2e.yaml file — mirrors `run`'s discovery
// semantics via ScenarioDiscovery) and runs each one through
// Vouchfx.Engine.Runtime.ScenarioValidator's topology-free compile-validation pipeline
// (schema → parse/AST → provider pipeline → Roslyn compile-once). NO Aspire topology is
// ever started and NO container runtime is touched — this is the compile-level gate a
// scenario passes through BEFORE `run` would ever call SuiteTopology.StartAsync.
//
// Exit codes (taxonomy-preserving, §12.1 spirit — "never conflate an authoring problem
// with a genuine defect"):
//   0 (ExitCodes.Success)      — every discovered scenario is valid (vacuously true when
//                                nothing was discovered — mirrors `run`'s "nothing ran,
//                                nothing failed" convention).
//   2 (ExitCodes.UsageError)   — a bad/missing <path> (identical mapping to `run`).
//   4 (ExitCodes.Inconclusive) — one or more scenarios is invalid. Deliberately NOT 1
//                                (ExitCodes.TestFailure): 1 is reserved for a genuine RUN
//                                Fail (a step's assertion did not hold against a live
//                                system); an invalid scenario here never ran at all — the
//                                same "authoring error, not a product defect" class the
//                                engine already calls Inconclusive (§12.1).

using System.CommandLine;
using System.Text.Json.Serialization;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;

namespace Vouchfx.Cli;

/// <summary>
/// Builds and executes the <c>validate</c> subcommand.
/// </summary>
internal static class ValidateCommand
{
    /// <summary>
    /// Builds the <c>validate</c> <see cref="Command"/>, wiring its async action to
    /// <see cref="Execute"/>.
    /// </summary>
    /// <returns>The configured <c>validate</c> command, ready to add to the root.</returns>
    public static Command Build()
    {
        var command = new Command(
            "validate",
            "Compile-validate *.e2e.yaml scenarios under <path> (a directory, or a single "
            + "*.e2e.yaml file) WITHOUT starting any container topology: schema validation, "
            + "parse/AST build, the provider pipeline's bind/validate/emit, and a Roslyn "
            + "compile-once. No Docker / Aspire activity occurs.");

        var pathArgument = BuildPathArgument();
        command.Add(pathArgument);

        var jsonOption = BuildJsonOption();
        command.Add(jsonOption);

        command.SetAction((parseResult, _) =>
        {
            var path = parseResult.GetValue(pathArgument) ?? ".";
            var json = parseResult.GetValue(jsonOption);
            return Task.FromResult(Execute(path, json, Console.Out, Console.Error));
        });

        return command;
    }

    /// <summary>
    /// Builds the optional positional <c>&lt;path&gt;</c> argument that defaults to the
    /// current directory. Mirrors <see cref="RunCommand.BuildPathArgument"/>.
    /// </summary>
    internal static Argument<string> BuildPathArgument() => new("path")
    {
        Description = "Directory to search recursively for *.e2e.yaml scenarios, or a "
            + "single *.e2e.yaml file. Defaults to '.'.",
        DefaultValueFactory = _ => ".",
    };

    /// <summary>
    /// The <c>--json</c> flag: emit a single schema-versioned JSON document to stdout
    /// instead of the human-readable report.
    /// </summary>
    internal static Option<bool> BuildJsonOption() => new("--json")
    {
        Description = "Emit a single schema-versioned JSON document to stdout instead of "
            + "the human-readable report.",
    };

    /// <summary>
    /// The Docker-free orchestration of a <c>validate</c> invocation: discovers scenarios,
    /// runs each through <see cref="ScenarioValidator"/>, renders either the human report
    /// or the <c>--json</c> document, and returns the exit code.
    /// </summary>
    /// <param name="path">The discovery root (already defaulted to <c>"."</c> by the parser).</param>
    /// <param name="json">When <see langword="true"/>, emit the JSON document instead of the human report.</param>
    /// <param name="output">The writer that receives the report / JSON document (stdout).</param>
    /// <param name="errorOutput">
    /// The writer that receives usage-error / discovery-error diagnostic TEXT (stderr) when
    /// <paramref name="json"/> is <see langword="true"/> — keeping stdout pure (either the
    /// JSON document, or nothing at all on a usage error). When <paramref name="json"/> is
    /// <see langword="false"/>, the diagnostic still goes to <paramref name="output"/> (the
    /// human report's own stream) so today's non-JSON UX is unchanged.
    /// </param>
    /// <returns>The process exit code (see <see cref="ExitCodes"/>).</returns>
    /// <remarks>
    /// Exposed as <see langword="internal"/> so the no-docker CLI tests can exercise the
    /// full orchestration (including the bad-path usage error and the "nothing found"
    /// success path) without going through <see cref="System.CommandLine"/> parsing.
    /// </remarks>
    internal static int Execute(string path, bool json, TextWriter output, TextWriter errorOutput)
    {
        StepKindRegistry registry = ProviderRegistryFactory.BuildCoreRegistry();

        IReadOnlyList<DiscoveredScenario> discovered;
        try
        {
            discovered = ScenarioDiscovery.Discover(path, registry);
        }
        // Broadened beyond DirectoryNotFoundException/ScenarioDiscoveryException (Copilot
        // review finding, #260 follow-up): Discover's OWN Path.GetFullPath(root) call can
        // throw ArgumentException / NotSupportedException / PathTooLongException on a
        // malformed path string, and its Directory.EnumerateFiles(..., AllDirectories) walk
        // can throw UnauthorizedAccessException / IOException / SecurityException when the
        // root itself (or a directory beneath it) is inaccessible. Every one of these is
        // caused by a bad USER-SUPPLIED path, not a genuine engine fault, so it maps to the
        // SAME clean usage error (exit 2) as the two exceptions already handled — never an
        // unhandled-exception crash with a non-taxonomy exit code. Deliberately NOT a bare
        // `catch (Exception ex)`: a genuinely unexpected fault must still propagate. Kept in
        // lock-step with RunCommand.ExecuteAsync's identically-broadened discovery catch.
        catch (Exception ex) when (ex is DirectoryNotFoundException
            or ScenarioDiscoveryException
            or UnauthorizedAccessException
            or IOException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
        {
            // --json must leave stdout carrying ONLY a JSON document (or nothing, on a
            // usage error) — a tool parsing stdout as one document must never choke on a
            // stray text line. Route the diagnostic to stderr in that mode; otherwise it
            // stays on `output` (today's plain-text UX, unchanged).
            (json ? errorOutput : output).WriteLine(ex.Message);
            return ExitCodes.UsageError;
        }

        if (discovered.Count == 0)
        {
            // Nothing discovered ⇒ vacuously valid — mirrors `run`'s "nothing ran, nothing
            // failed" convention (§12.1): there is nothing to report as invalid.
            if (json)
            {
                CliJsonContract.Write(
                    new ValidateJsonDocument(
                        CliJsonContract.SchemaVersion,
                        CliJsonContract.EngineVersion,
                        Array.Empty<ValidateJsonScenario>(),
                        Valid: true),
                    output);
            }
            else
            {
                output.WriteLine(
                    $"No {ScenarioDiscovery.ScenarioGlob} scenarios found under '{path}'.");
            }

            return ExitCodes.Success;
        }

        // Only scenarios that discovery itself parsed cleanly are handed to the validator.
        // A scenario discovery already marked Failed (an unreadable file, or a YAML/AST
        // build failure) carries its OWN accurate ParseError — re-running it through
        // ScenarioValidator would re-validate a possibly EMPTY YamlText (the I/O-failure
        // case leaves it string.Empty) and surface a misleading generic schema diagnostic
        // ("document is empty") instead of discovery's real message (e.g. "Could not read
        // file: Access to the path '...' is denied.").
        var parsed = discovered.Where(s => !s.Failed).ToList();

        // Each scenario's OWN directory (issue #268) — applied to THAT scenario's compile
        // only, deliberately matching RunCommand.ExecuteAsync's per-scenario
        // scenarioBaseDirectories exactly (Path.GetDirectoryName(p.AbsolutePath), threaded
        // into ScenarioRunner.RunSuiteAsync / ParallelSuiteRunner.RunParallelAsync as the
        // scenario's own base directory). This base directory is a COMPILE-TIME input:
        // script.csharp resolves a `file:` reference (and existence-checks it) against it at
        // emit time — so validate MUST resolve each scenario's own directory identically to
        // run's per-scenario compile, or a scenario several directories away from another
        // could validate against a DIFFERENT base than run will actually use, letting
        // validate pass what run would reject (or vice versa). A suite-wide base directory
        // (every scenario resolved against the FIRST scenario's folder) would reproduce the
        // #268 bug this fix removes: a non-first scenario's relative `file:` would resolve
        // against the wrong folder. (The ONE nuance run itself preserves: in an unfiltered
        // sequential run the shared topology's `environment.seed` is still seeded ONCE, from
        // the FIRST scenario — but that is `run`'s single-topology seed concern, not a
        // compile-time `file:` resolution concern, so it has no bearing on this per-scenario
        // validator, which never starts a topology at all.)
        var sources = parsed
            .Select(s => new ScenarioSource(
                s.AbsolutePath, s.YamlText, Path.GetDirectoryName(s.AbsolutePath)))
            .ToList();

        var validated = ScenarioValidator.Validate(sources, registry).Scenarios;

        // Merge back in original discovery order: a Failed scenario becomes a direct
        // Parse-stage diagnostic carrying discovery's own ParseError verbatim; a scenario
        // discovery parsed cleanly gets its validated result.
        var results = new List<ScenarioValidationResult>(discovered.Count);
        var validatedIndex = 0;
        foreach (var scenario in discovered)
        {
            results.Add(scenario.Failed
                ? new ScenarioValidationResult(
                    scenario.AbsolutePath,
                    IsValid: false,
                    new[] { new ValidationDiagnostic(ValidationStage.Parse, scenario.ParseError!) })
                : validated[validatedIndex++]);
        }

        var report = new ValidationReport(results);

        if (json)
        {
            CliJsonContract.Write(ToJsonDocument(report), output);
        }
        else
        {
            WriteHumanReport(report, output);
        }

        return report.IsValid ? ExitCodes.Success : ExitCodes.Inconclusive;
    }

    /// <summary>
    /// Renders the human-readable report: one PASS/FAIL line per scenario (path relative
    /// to the current directory), each diagnostic indented beneath it, then a summary line.
    /// </summary>
    private static void WriteHumanReport(ValidationReport report, TextWriter output)
    {
        var cwd = Directory.GetCurrentDirectory();

        foreach (var scenario in report.Scenarios)
        {
            var verdict = scenario.IsValid ? "PASS" : "FAIL";
            var displayPath = SafeRelativePath(cwd, scenario.Path);
            output.WriteLine($"{verdict}  {displayPath}");

            foreach (var diagnostic in scenario.Diagnostics)
            {
                output.WriteLine($"    [{diagnostic.Stage}] {diagnostic.Message}");
            }
        }

        var validCount = report.Scenarios.Count(s => s.IsValid);
        var invalidCount = report.Scenarios.Count - validCount;
        output.WriteLine(
            $"{validCount} valid, {invalidCount} invalid (of {report.Scenarios.Count} discovered).");
    }

    /// <summary>
    /// Computes <paramref name="path"/> relative to <paramref name="basePath"/> for display,
    /// falling back to the absolute path when the two are on different roots (e.g.
    /// different drives on Windows) rather than throwing.
    /// </summary>
    private static string SafeRelativePath(string basePath, string path)
    {
        try
        {
            return Path.GetRelativePath(basePath, path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>Projects the engine's <see cref="ValidationReport"/> into the frozen JSON wire shape.</summary>
    private static ValidateJsonDocument ToJsonDocument(ValidationReport report) => new(
        CliJsonContract.SchemaVersion,
        CliJsonContract.EngineVersion,
        report.Scenarios
            .Select(s => new ValidateJsonScenario(
                s.Path,
                s.IsValid,
                s.Diagnostics
                    .Select(d => new ValidateJsonDiagnostic(d.Stage, d.Message))
                    .ToList()))
            .ToList(),
        report.IsValid);
}

// ── `validate --json` wire shapes (frozen contract, pinned by ValidateJsonGoldenTests) ──
//
// {
//   "schemaVersion": 1,
//   "engineVersion": "...",
//   "scenarios": [
//     { "path": "...", "valid": true|false, "diagnostics": [ { "stage": "schema|parse|pipeline|roslyn", "message": "..." } ] }
//   ],
//   "valid": true|false
// }
//
// Property order is declaration order (System.Text.Json's reflection-based default) —
// keep the declared order below in lock-step with the comment above and the golden file.

/// <summary>The top-level <c>validate --json</c> document.</summary>
internal sealed record ValidateJsonDocument(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] string EngineVersion,
    [property: JsonPropertyOrder(2)] IReadOnlyList<ValidateJsonScenario> Scenarios,
    [property: JsonPropertyOrder(3)] bool Valid);

/// <summary>A single scenario entry in the <c>validate --json</c> document.</summary>
internal sealed record ValidateJsonScenario(
    [property: JsonPropertyOrder(0)] string Path,
    [property: JsonPropertyOrder(1)] bool Valid,
    [property: JsonPropertyOrder(2)] IReadOnlyList<ValidateJsonDiagnostic> Diagnostics);

/// <summary>A single diagnostic entry in the <c>validate --json</c> document.</summary>
internal sealed record ValidateJsonDiagnostic(
    [property: JsonPropertyOrder(0)] ValidationStage Stage,
    [property: JsonPropertyOrder(1)] string Message);
