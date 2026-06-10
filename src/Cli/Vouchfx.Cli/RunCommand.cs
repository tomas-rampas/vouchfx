// Vouchfx.Cli — RunCommand (S07-C-01).
//
// The `vouchfx run [<path>]` subcommand. THIS sprint's surface is just the optional
// <path> argument (default "."). Selection flags (--tag / --owner / --changed / …) are a
// later task; the command is shaped so they slot in as Options without disturbing this.
//
// Flow:
//   1. Build the frozen Core provider registry (ProviderRegistryFactory).
//   2. Discover + parse *.e2e.yaml under <path> (ScenarioDiscovery).
//   3. Split discovery into parsed scenarios and parse-failures.
//   4. Hand the parsed scenarios to ScenarioRunner.RunSuiteAsync with appHostAssemblyName
//      = THIS executable ("vouchfx") so DCP metadata resolves to this host (CLAUDE.md
//      §"Aspire (§4, §19)" R-1 finding), not the GetEntryAssembly fallback.
//   5. Each parse-failure becomes an Inconclusive scenario (§12.1 — authoring error, the
//      scenario never ran), aggregated alongside the suite verdict.
//   6. Map the aggregate verdict to a process exit code (ExitCodes).
//
// The full `run` path needs Docker (RunSuiteAsync starts an Aspire topology) and is NOT
// exercised by the unit tests — the Docker-free seams (discovery, registry, exit-code
// mapping, verdict aggregation) are factored out as internal statics and tested directly.

using System.CommandLine;
using System.Reflection;
using Platform.Engine.Abstractions;
using Platform.Engine.Authoring.Ast;
using Platform.Engine.Runtime;
using Platform.Sdk;

namespace Vouchfx.Cli;

/// <summary>
/// Builds and executes the <c>run</c> subcommand.
/// </summary>
internal static class RunCommand
{
    /// <summary>
    /// Builds the <c>run</c> <see cref="Command"/>, wiring its async action to
    /// <see cref="ExecuteAsync"/>.
    /// </summary>
    /// <returns>
    /// The configured <c>run</c> command (with its <c>&lt;path&gt;</c> argument), ready to
    /// be added to the root command.
    /// </returns>
    public static Command Build()
    {
        var command = new Command(
            "run",
            "Discover *.e2e.yaml scenarios under <path> and run them end-to-end against an "
            + "orchestrated topology.");

        var pathArgument = BuildPathArgument();
        command.Add(pathArgument);

        // SetAction(Func<ParseResult, CancellationToken, Task<int>>): the async, exit-code,
        // cancellation-aware overload (System.CommandLine 2.0.x GA).
        command.SetAction((parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(pathArgument) ?? ".";
            return ExecuteAsync(path, Console.Out, cancellationToken);
        });

        // TODO(S08+): add selection options here (--tag / --owner / --changed / …); they
        // bind from parseResult and filter the discovered scenarios before RunSuiteAsync.
        return command;
    }

    /// <summary>
    /// Builds the optional positional <c>&lt;path&gt;</c> argument that defaults to the
    /// current directory.
    /// </summary>
    /// <remarks>
    /// Factored out (and <see langword="internal"/>) so the no-docker arg-parsing test can
    /// assert that <c>run &lt;path&gt;</c> resolves the supplied path and that a bare
    /// <c>run</c> defaults to <c>"."</c>.  In System.CommandLine 2.0.x GA the
    /// <see cref="Argument{T}"/> constructor takes the name only; the description and
    /// default come from init properties.
    /// </remarks>
    internal static Argument<string> BuildPathArgument() => new("path")
    {
        Description = "Directory to search for *.e2e.yaml scenarios (recursively). Defaults to '.'.",
        DefaultValueFactory = _ => ".",
    };

    /// <summary>
    /// The Docker-free orchestration of a <c>run</c> invocation: discovers scenarios, runs
    /// the parsed ones, folds parse-failures in as Inconclusive, and returns the exit code.
    /// </summary>
    /// <param name="path">The discovery root (already defaulted to <c>"."</c> by the parser).</param>
    /// <param name="output">The writer that receives diagnostics + the rendered report.</param>
    /// <param name="cancellationToken">Propagated to the runner.</param>
    /// <returns>The process exit code (see <see cref="ExitCodes"/>).</returns>
    /// <remarks>
    /// This calls <see cref="ScenarioRunner.RunSuiteAsync"/>, which starts an Aspire
    /// topology and therefore needs Docker — so this method is NOT exercised by the unit
    /// tests.  Its Docker-free building blocks (<see cref="ScenarioDiscovery.Discover"/>,
    /// <see cref="ProviderRegistryFactory.BuildCoreRegistry"/>,
    /// <see cref="AggregateVerdict"/>, <see cref="ExitCodes.FromVerdict"/>) are each tested
    /// in isolation.
    /// </remarks>
    internal static async Task<int> ExecuteAsync(
        string path,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        StepKindRegistry registry = ProviderRegistryFactory.BuildCoreRegistry();

        IReadOnlyList<DiscoveredScenario> discovered;
        try
        {
            discovered = ScenarioDiscovery.Discover(path, registry);
        }
        catch (DirectoryNotFoundException ex)
        {
            await output.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitCodes.UsageError;
        }

        if (discovered.Count == 0)
        {
            await output.WriteLineAsync(
                $"No {ScenarioDiscovery.ScenarioGlob} scenarios found under '{path}'.")
                .ConfigureAwait(false);
            // Nothing ran; nothing failed. Success per §12.1 (only Fail breaks CI).
            return ExitCodes.Success;
        }

        // Split into runnable scenarios and parse-failures.
        var parsed = new List<DiscoveredScenario>(discovered.Count);
        var failures = new List<DiscoveredScenario>();
        foreach (var scenario in discovered)
        {
            if (scenario.Failed)
            {
                failures.Add(scenario);
            }
            else
            {
                parsed.Add(scenario);
            }
        }

        // Report each parse-failure as an Inconclusive scenario (it never ran — §12.1).
        foreach (var failure in failures)
        {
            await output.WriteLineAsync(
                $"{failure.AbsolutePath}: {failure.ParseError} (Inconclusive)")
                .ConfigureAwait(false);
        }

        Verdict suiteVerdict = Verdict.Pass;
        if (parsed.Count > 0)
        {
            var asts = parsed.Select(p => p.Ast!).ToList();
            var names = parsed.Select(ScenarioName).ToList();
            var yamlTexts = parsed.Select(p => p.YamlText).ToList();

            // appHostAssemblyName = THIS executable's name ("vouchfx"): the Aspire host that
            // carries the embedded DCP metadata (Aspire.AppHost.Sdk + IsAspireHost). Passing
            // it explicitly avoids the GetEntryAssembly fallback (CLAUDE.md §"Aspire").
            var appHostAssemblyName = Assembly.GetExecutingAssembly().GetName().Name;

            SuiteResult result = await ScenarioRunner.RunSuiteAsync(
                asts,
                names,
                yamlTexts,
                ProviderRegistryFactory.CoreProviderAssemblies(),
                appHostAssemblyName,
                output,
                seedBaseDirectory: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            suiteVerdict = result.Verdict;
        }

        // Fold the parse-failures (Inconclusive) into the suite verdict so the exit code
        // reflects the whole discovery, not just the scenarios that compiled.
        var aggregate = AggregateVerdict(suiteVerdict, failures.Count);
        return ExitCodes.FromVerdict(aggregate);
    }

    /// <summary>
    /// Elevates <paramref name="suiteVerdict"/> by the discovery parse-failures, each of
    /// which counts as an <see cref="Verdict.Inconclusive"/> scenario (§12.1).
    /// </summary>
    /// <param name="suiteVerdict">The aggregate verdict from the parsed scenarios.</param>
    /// <param name="parseFailureCount">The number of files that failed to parse.</param>
    /// <returns>
    /// The suite verdict elevated to at least <see cref="Verdict.Inconclusive"/> when any
    /// file failed to parse, using the standard precedence
    /// (<c>EnvironmentError &gt; Fail &gt; Inconclusive &gt; Pass</c>).
    /// </returns>
    /// <remarks>
    /// Reuses <see cref="ScenarioRunner.Elevate"/> so the CLI and the runner can never
    /// disagree about verdict precedence.  Exposed as <see langword="internal"/> for the
    /// no-docker aggregation test.
    /// </remarks>
    internal static Verdict AggregateVerdict(Verdict suiteVerdict, int parseFailureCount) =>
        parseFailureCount > 0
            ? ScenarioRunner.Elevate(suiteVerdict, Verdict.Inconclusive)
            : suiteVerdict;

    /// <summary>
    /// Derives the report-facing scenario name: the <c>metadata.name</c> when present,
    /// else the file name without its <c>.e2e.yaml</c> extension.
    /// </summary>
    /// <remarks>Exposed as <see langword="internal"/> for the no-docker naming test.</remarks>
    internal static string ScenarioName(DiscoveredScenario scenario)
    {
        var metaName = scenario.Ast?.Metadata?.Name;
        if (!string.IsNullOrWhiteSpace(metaName))
        {
            return metaName;
        }

        var fileName = Path.GetFileName(scenario.AbsolutePath);
        const string suffix = ".e2e.yaml";
        return fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^suffix.Length]
            : fileName;
    }
}
