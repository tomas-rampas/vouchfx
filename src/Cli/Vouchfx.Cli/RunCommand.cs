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
using Vouchfx.Cli.Selection;

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

        // Selection options (S07-C-02): tag / owner / path / change-set. These bind into a
        // SelectionCriteria that filters the discovered scenarios BEFORE the runner sees
        // them — `metadata` (tag/owner) drives selection only, never execution (BP §16).
        var tagOption = BuildTagOption();
        var ownerOption = BuildOwnerOption();
        var pathOption = BuildPathOption();
        var changedSinceOption = BuildChangedSinceOption();
        var parallelOption = BuildParallelOption();
        var watchOption = BuildWatchOption();
        var failOnEnvironmentErrorOption = BuildFailOnEnvironmentErrorOption();
        var failOnInconclusiveOption = BuildFailOnInconclusiveOption();
        var htmlReportOption = BuildHtmlReportOption();
        var junitReportOption = BuildJunitReportOption();
        var eventsOption = BuildEventsOption();
        command.Add(tagOption);
        command.Add(ownerOption);
        command.Add(pathOption);
        command.Add(changedSinceOption);
        command.Add(parallelOption);
        command.Add(watchOption);
        command.Add(failOnEnvironmentErrorOption);
        command.Add(failOnInconclusiveOption);
        command.Add(htmlReportOption);
        command.Add(junitReportOption);
        command.Add(eventsOption);

        // SetAction(Func<ParseResult, CancellationToken, Task<int>>): the async, exit-code,
        // cancellation-aware overload (System.CommandLine 2.0.x GA).
        command.SetAction((parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(pathArgument) ?? ".";
            var criteria = BuildCriteria(parseResult, tagOption, ownerOption, pathOption, changedSinceOption);
            var parallel = parseResult.GetValue(parallelOption);
            var watch = parseResult.GetValue(watchOption);
            var failOnEnvironmentError = parseResult.GetValue(failOnEnvironmentErrorOption);
            var failOnInconclusive = parseResult.GetValue(failOnInconclusiveOption);
            var htmlReportPath = parseResult.GetValue(htmlReportOption);
            var junitReportPath = parseResult.GetValue(junitReportOption);
            var eventsReportPath = parseResult.GetValue(eventsOption);
            return ExecuteAsync(
                path,
                criteria,
                parallel,
                watch,
                failOnEnvironmentError,
                failOnInconclusive,
                htmlReportPath,
                junitReportPath,
                eventsReportPath,
                Console.Out,
                cancellationToken);
        });

        return command;
    }

    /// <summary>The repeatable <c>--tag</c> option: keep scenarios carrying any listed tag.</summary>
    internal static Option<string[]> BuildTagOption() => new("--tag")
    {
        Description =
            "Select scenarios whose metadata.tags contains this tag. Repeatable; a scenario "
            + "matches if it has ANY of the supplied tags (OR).",
        AllowMultipleArgumentsPerToken = true,
    };

    /// <summary>The repeatable <c>--owner</c> option: keep scenarios with any listed owner.</summary>
    internal static Option<string[]> BuildOwnerOption() => new("--owner")
    {
        Description =
            "Select scenarios whose metadata.owner is this value. Repeatable; a scenario "
            + "matches if its owner is ANY of the supplied owners (OR).",
        AllowMultipleArgumentsPerToken = true,
    };

    /// <summary>The <c>--path</c> option: a glob (or substring) over the scenario's path.</summary>
    internal static Option<string?> BuildPathOption() => new("--path")
    {
        Description =
            "Select scenarios whose (normalised) absolute path matches this glob. Supports "
            + "*, ** and ?; a pattern with no wildcard is matched as a substring.",
    };

    /// <summary>The <c>--changed-since</c> option: a git ref bounding the change-set.</summary>
    internal static Option<string?> BuildChangedSinceOption() => new("--changed-since")
    {
        Description =
            "Select only scenarios whose file changed since this git ref (committed diff vs "
            + "the ref plus the dirty working tree). Requires a git repository.",
    };

    /// <summary>
    /// The <c>--parallel</c> option: run up to N scenarios concurrently, each owning its own
    /// topology (S08-T1).  When absent, scenarios run sequentially against ONE shared topology
    /// (the default — explicit opt-in keeps the container cost a deliberate choice).
    /// </summary>
    /// <remarks>
    /// Parallelism is an explicit opt-in because each concurrent scenario stands up its OWN
    /// container topology: running N scenarios at <c>--parallel N</c> means up to N times the
    /// containers (and their pull / start cost) at once.  A value &lt; 1 is a usage error.
    /// </remarks>
    internal static Option<int?> BuildParallelOption() => new("--parallel")
    {
        Description =
            "Run up to N scenarios concurrently, each owning its OWN container topology (S08). "
            + "Must be 1 or greater. CAVEAT: each concurrent scenario stands up its own topology, "
            + "so --parallel N uses up to N times the containers at once. Omit to run sequentially "
            + "against a single shared topology (the default).",
    };

    /// <summary>
    /// The <c>--watch</c> flag (S08-C-01): run the suite once, then watch the <c>.e2e.yaml</c>
    /// file and re-run automatically on save, re-using the already-built topology while the
    /// <c>environment</c> block is unchanged (and rebuilding it only when it changes).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Watch mode targets the local edit-run loop for a SINGLE file: it keeps one topology alive
    /// across re-runs and skips the expensive container rebuild whenever only <c>steps</c> change.
    /// </para>
    /// <para>
    /// <c>--watch</c> is mutually exclusive with <c>--parallel</c>: the former keeps ONE topology
    /// alive for one file, the latter fans MANY scenarios across MANY topologies — combining them
    /// is incoherent, so the combination is rejected as a usage error.
    /// </para>
    /// </remarks>
    internal static Option<bool> BuildWatchOption() => new("--watch")
    {
        Description =
            "Run once, then watch the .e2e.yaml file and re-run automatically on save, re-using "
            + "the already-built topology while the `environment` block is unchanged (rebuilding "
            + "it only when it changes). For local iteration on a single file; press Ctrl-C to "
            + "stop. Cannot be combined with --parallel.",
    };

    /// <summary>
    /// The <c>--fail-on-env-error</c> flag (S09-C-03): opt in to gating CI on an
    /// <see cref="Verdict.EnvironmentError"/> aggregate verdict (exit
    /// <see cref="ExitCodes.EnvironmentError"/>, 3).
    /// </summary>
    /// <remarks>
    /// Off by default — per the verdict taxonomy (§12.1) only <see cref="Verdict.Fail"/> breaks
    /// CI by default, so an environment error (unhealthy container, image-pull / seed / tunnel
    /// failure) exits 0 unless this flag is set.  When set, it exits with the <em>distinct</em>
    /// code 3 so CI can tell infra breakage apart from a product Fail (1).
    /// </remarks>
    internal static Option<bool> BuildFailOnEnvironmentErrorOption() => new("--fail-on-env-error")
    {
        Description =
            "Treat an Environment-error verdict as a CI failure (exit 3). Off by default — only "
            + "Fail breaks CI. Use this to gate on infrastructure breakage (unhealthy container, "
            + "image-pull / seed / tunnel failure); the distinct code 3 keeps it separable from a "
            + "product Fail (1).",
    };

    /// <summary>
    /// The <c>--fail-on-inconclusive</c> flag (S09-C-03): opt in to gating CI on an
    /// <see cref="Verdict.Inconclusive"/> aggregate verdict (exit
    /// <see cref="ExitCodes.Inconclusive"/>, 4).
    /// </summary>
    /// <remarks>
    /// Off by default — per the verdict taxonomy (§12.1) only <see cref="Verdict.Fail"/> breaks
    /// CI by default, so an inconclusive result (timeout, partition outlasted grace, upstream
    /// capture unmet) exits 0 unless this flag is set.  When set, it exits with the
    /// <em>distinct</em> code 4 so CI can tell a timeout apart from a product Fail (1) and from
    /// infra breakage (3).
    /// </remarks>
    internal static Option<bool> BuildFailOnInconclusiveOption() => new("--fail-on-inconclusive")
    {
        Description =
            "Treat an Inconclusive verdict as a CI failure (exit 4). Off by default — only Fail "
            + "breaks CI. Use this to gate on results the engine could not decide (timeout, "
            + "partition outlasted grace, upstream capture unmet); the distinct code 4 keeps it "
            + "separable from a product Fail (1) and an Environment error (3).",
    };

    /// <summary>
    /// The <c>--html</c> option (S09-T3): write a self-contained HTML report to the given path.
    /// </summary>
    /// <remarks>
    /// Absent ⇒ no HTML artifact (today's behaviour unchanged).  When set, the report is written
    /// from the SAME buffered event stream + diff lookup the terminal renderer consumes, so the
    /// HTML view can never disagree with the terminal output (parity, S09-D-01).
    /// </remarks>
    internal static Option<string?> BuildHtmlReportOption() => new("--html")
    {
        Description =
            "Write a self-contained HTML report to <path>. Rendered from the same event stream as "
            + "the terminal output, so the two never disagree. Parent directories are created as "
            + "needed; an existing file is overwritten. Omit for no HTML report (the default).",
    };

    /// <summary>
    /// The <c>--junit</c> option (S09-T3): write a JUnit XML results file to the given path.
    /// </summary>
    /// <remarks>
    /// Absent ⇒ no JUnit artifact (today's behaviour unchanged).  When set, the file is written
    /// from the SAME buffered event stream the terminal renderer consumes, and maps the four
    /// §12.1 verdicts onto distinct JUnit primitives (Fail→failure, EnvError→error,
    /// Inconclusive→skipped) so CI never conflates infra breakage with a defect.
    /// </remarks>
    internal static Option<string?> BuildJunitReportOption() => new("--junit")
    {
        Description =
            "Write a JUnit XML results file to <path> for CI ingestion. Rendered from the same "
            + "event stream as the terminal output; the four verdicts map to distinct JUnit "
            + "primitives (Fail→failure, Environment-error→error, Inconclusive→skipped). Parent "
            + "directories are created as needed; an existing file is overwritten. Omit for no "
            + "JUnit report (the default).",
    };

    /// <summary>
    /// The <c>--events</c> option (S10), aliased as <c>--json</c>: write the raw buffered JSON
    /// Lines event stream to the given path, VERBATIM.
    /// </summary>
    /// <remarks>
    /// Absent ⇒ no events artifact (today's behaviour unchanged).  When set, the SAME buffered
    /// event stream the terminal / HTML / JUnit renderers consume is written byte-for-byte — one
    /// JSON object per line, UTF-8 without a BOM — so a downstream consumer (e.g. the VSCode Test
    /// Explorer) sees exactly the frozen v1 stream the engine emitted.  This is ADDITIVE: it
    /// re-emits the existing event records, it does NOT change any record or the wire contract.
    /// <c>--events</c> and <c>--json</c> are ONE option with two names (the alias mechanism), so
    /// either spelling binds the same value.
    /// </remarks>
    internal static Option<string?> BuildEventsOption() => new("--events", "--json")
    {
        Description =
            "Write the raw JSON Lines event stream to the given path. Re-emits the same event "
            + "stream the terminal / HTML / JUnit reports are rendered from, verbatim — one JSON "
            + "object per line, UTF-8 without a BOM. Parent directories are created as needed; an "
            + "existing file is overwritten. Aliased as --json. Omit for no events file (the "
            + "default).",
    };

    /// <summary>
    /// Folds the four selection options out of a <see cref="ParseResult"/> into the
    /// immutable <see cref="SelectionCriteria"/> the selector consumes.
    /// </summary>
    /// <remarks>Exposed as <see langword="internal"/> for the arg-parsing test.</remarks>
    internal static SelectionCriteria BuildCriteria(
        ParseResult parseResult,
        Option<string[]> tagOption,
        Option<string[]> ownerOption,
        Option<string?> pathOption,
        Option<string?> changedSinceOption)
    {
        var tags = parseResult.GetValue(tagOption) ?? Array.Empty<string>();
        var owners = parseResult.GetValue(ownerOption) ?? Array.Empty<string>();
        var pathGlob = Normalise(parseResult.GetValue(pathOption));
        var changedSince = Normalise(parseResult.GetValue(changedSinceOption));

        return new SelectionCriteria(tags, owners, pathGlob, changedSince);
    }

    /// <summary>Treats an empty/whitespace option value as "not supplied".</summary>
    private static string? Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

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
    /// <param name="parallel">
    /// The <c>--parallel</c> value: when non-<see langword="null"/>, run up to this many
    /// scenarios concurrently (each owning its own topology) via
    /// <see cref="ParallelSuiteRunner.RunParallelAsync"/>; when <see langword="null"/>, run
    /// sequentially against one shared topology via <see cref="ScenarioRunner.RunSuiteAsync"/>.
    /// A value &lt; 1 is a usage error (<see cref="ExitCodes.UsageError"/>).
    /// </param>
    /// <param name="watch">
    /// The <c>--watch</c> flag (S08-C-01): when <see langword="true"/>, run once and then watch
    /// the single selected <c>.e2e.yaml</c> file, re-running on each save and re-using the kept
    /// topology while the <c>environment</c> block is unchanged.  Mutually exclusive with
    /// <paramref name="parallel"/> (combining them is a usage error); requires the selection to
    /// resolve to exactly one file.
    /// </param>
    /// <param name="failOnEnvironmentError">
    /// The <c>--fail-on-env-error</c> flag (S09-C-03): when <see langword="true"/>, an aggregate
    /// <see cref="Verdict.EnvironmentError"/> exits with <see cref="ExitCodes.EnvironmentError"/>
    /// (3) instead of 0.  Off by default — only <see cref="Verdict.Fail"/> breaks CI.
    /// </param>
    /// <param name="failOnInconclusive">
    /// The <c>--fail-on-inconclusive</c> flag (S09-C-03): when <see langword="true"/>, an
    /// aggregate <see cref="Verdict.Inconclusive"/> exits with <see cref="ExitCodes.Inconclusive"/>
    /// (4) instead of 0.  Off by default — only <see cref="Verdict.Fail"/> breaks CI.
    /// </param>
    /// <param name="htmlReportPath">
    /// The <c>--html</c> path (S09-T3): when non-<see langword="null"/>, the runner writes a
    /// self-contained HTML report there from the same buffered event stream + diff lookup the
    /// terminal renderer uses.  <see langword="null"/> ⇒ no HTML artifact.
    /// </param>
    /// <param name="junitReportPath">
    /// The <c>--junit</c> path (S09-T3): when non-<see langword="null"/>, the runner writes a
    /// JUnit XML results file there from the same buffered event stream the terminal renderer
    /// uses.  <see langword="null"/> ⇒ no JUnit artifact.
    /// </param>
    /// <param name="eventsReportPath">
    /// The <c>--events</c> / <c>--json</c> path (S10): when non-<see langword="null"/>, the runner
    /// writes the raw buffered JSON Lines event stream there VERBATIM (the same buffer the terminal
    /// renderer uses, one object per line, UTF-8 without a BOM).  <see langword="null"/> ⇒ no events
    /// artifact.  Like <c>--html</c> / <c>--junit</c> it is NOT wired into watch mode (same scope
    /// note as those flags below).
    /// </param>
    /// <returns>The process exit code (see <see cref="ExitCodes"/>).</returns>
    /// <remarks>
    /// This calls <see cref="ScenarioRunner.RunSuiteAsync"/> (or
    /// <see cref="ParallelSuiteRunner.RunParallelAsync"/> when <paramref name="parallel"/> is
    /// supplied, or the watch loop when <paramref name="watch"/> is set), which starts an Aspire
    /// topology and therefore needs Docker — so this method is NOT exercised by the unit tests.
    /// Its Docker-free building blocks (<see cref="ScenarioDiscovery.Discover"/>,
    /// <see cref="ProviderRegistryFactory.BuildCoreRegistry"/>,
    /// <see cref="AggregateVerdict"/>, <see cref="ExitCodes.FromVerdict"/>, the
    /// <c>--parallel</c> / <c>--watch</c> arg-parse, and the <c>--watch</c>+<c>--parallel</c>
    /// usage-error short-circuit) are each tested in isolation.
    /// </remarks>
    internal static async Task<int> ExecuteAsync(
        string path,
        SelectionCriteria criteria,
        int? parallel,
        bool watch,
        bool failOnEnvironmentError,
        bool failOnInconclusive,
        string? htmlReportPath,
        string? junitReportPath,
        string? eventsReportPath,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        // --watch and --parallel are mutually exclusive: one keeps a SINGLE topology alive for
        // one file, the other fans MANY scenarios across MANY topologies.  Reject the combo as a
        // usage error (exit 2) up front — before discovering or running anything (no Docker).
        if (watch && parallel is not null)
        {
            await output.WriteLineAsync(
                "--watch cannot be combined with --parallel (watch keeps one topology alive for a "
                + "single file; --parallel fans many scenarios across many topologies).")
                .ConfigureAwait(false);
            return ExitCodes.UsageError;
        }

        // Validate --parallel up front: a value < 1 is a usage error (exit 2), not a crash.
        // (System.CommandLine binds the value; the >= 1 contract is the engine's, enforced here.)
        if (parallel is { } degree && degree < 1)
        {
            await output.WriteLineAsync("--parallel must be 1 or greater.").ConfigureAwait(false);
            return ExitCodes.UsageError;
        }

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

        // Apply the test-selection language BEFORE the runner: narrow the discovered
        // scenarios by tag/owner/path/change-set (BP §16). A bad --changed-since (no repo,
        // git missing, bad ref) is a usage error (exit 2), not a crash.
        IReadOnlyList<DiscoveredScenario> selected;
        try
        {
            selected = SelectScenarios(discovered, criteria, path);
        }
        catch (ChangeSetException ex)
        {
            await output.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitCodes.UsageError;
        }

        if (selected.Count == 0)
        {
            await output.WriteLineAsync(
                $"No scenarios matched the selection criteria (of {discovered.Count} discovered "
                + $"under '{path}').")
                .ConfigureAwait(false);
            // Nothing selected is not a failure — there was simply nothing to run.
            return ExitCodes.Success;
        }

        discovered = selected;

        // ── Watch mode (S08-C-01) ─────────────────────────────────────────────
        // `vouchfx run <file> --watch` watches a SINGLE file: run once, then re-run on save,
        // re-using the kept topology while the `environment` block is unchanged.  Watching is
        // inherently single-file, so the selection must resolve to exactly one scenario; a
        // directory matching many files (or none that parses) is a usage error here.
        //
        // NOTE (S09-T3 / S10 scope): --html / --junit / --events are NOT wired into watch mode.
        // Watch renders per re-run (not from one suite-wide buffer), and threading the report /
        // events paths through WatchRunner / WatchSession / RunScenarioAgainstKeptTopologyAsync —
        // plus deciding the overwrite-on-every-save semantics — is meaningful complexity for an
        // interactive loop whose value is the terminal feedback.  --events follows the SAME scope
        // as --html / --junit: deliberately left out rather than half-wired.
        if (watch)
        {
            return await WatchRunner.RunAsync(discovered, registry, output, cancellationToken)
                .ConfigureAwait(false);
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

            // --parallel N → run scenarios concurrently, each owning its OWN topology
            // (ParallelSuiteRunner, S08). Absent → run sequentially against ONE shared topology
            // (ScenarioRunner.RunSuiteAsync). Parallelism is an explicit opt-in because it
            // multiplies the concurrent container cost (one topology per in-flight scenario).
            SuiteResult result = parallel is { } parallelDegree
                ? await ParallelSuiteRunner.RunParallelAsync(
                    asts,
                    names,
                    yamlTexts,
                    ProviderRegistryFactory.CoreProviderAssemblies(),
                    appHostAssemblyName,
                    output,
                    maxConcurrency: parallelDegree,
                    seedBaseDirectory: null,
                    htmlReportPath: htmlReportPath,
                    junitReportPath: junitReportPath,
                    eventsReportPath: eventsReportPath,
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                : await ScenarioRunner.RunSuiteAsync(
                    asts,
                    names,
                    yamlTexts,
                    ProviderRegistryFactory.CoreProviderAssemblies(),
                    appHostAssemblyName,
                    output,
                    seedBaseDirectory: null,
                    htmlReportPath: htmlReportPath,
                    junitReportPath: junitReportPath,
                    eventsReportPath: eventsReportPath,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

            suiteVerdict = result.Verdict;
        }

        // Fold the parse-failures (Inconclusive) into the suite verdict so the exit code
        // reflects the whole discovery, not just the scenarios that compiled.
        var aggregate = AggregateVerdict(suiteVerdict, failures.Count);
        return ExitCodes.FromVerdict(aggregate, failOnEnvironmentError, failOnInconclusive);
    }

    /// <summary>
    /// Applies the <see cref="SelectionCriteria"/> to the discovered scenarios, constructing
    /// a git-backed <see cref="GitChangeSet"/> only when a <c>--changed-since</c> ref is set
    /// (otherwise the change-set dimension is inert via <see cref="NullChangeSet"/>).
    /// </summary>
    /// <param name="discovered">The discovered scenarios (parsed and parse-failures).</param>
    /// <param name="criteria">The selection criteria parsed from the CLI options.</param>
    /// <param name="discoveryRoot">
    /// The discovery root, used as git's working directory so <c>--changed-since</c> resolves
    /// against the repository the scenarios live in.
    /// </param>
    /// <returns>The selected subset, in discovery order.</returns>
    /// <exception cref="ChangeSetException">
    /// Thrown when <c>--changed-since</c> is set but the change-set cannot be computed.
    /// </exception>
    /// <remarks>
    /// Exposed as <see langword="internal"/> so the no-docker test can assert that an empty
    /// criteria selects everything and that the change-set is only built on demand.
    /// </remarks>
    internal static IReadOnlyList<DiscoveredScenario> SelectScenarios(
        IReadOnlyList<DiscoveredScenario> discovered,
        SelectionCriteria criteria,
        string discoveryRoot)
    {
        IChangeSet changeSet = NullChangeSet.Instance;
        if (criteria.ChangedSinceRef is { } changedSinceRef)
        {
            var workingDirectory = ResolveWorkingDirectory(discoveryRoot);
            changeSet = new GitChangeSet(changedSinceRef, workingDirectory, SystemProcessRunner.Instance);
        }

        return ScenarioSelector.Apply(discovered, criteria, changeSet);
    }

    /// <summary>
    /// Resolves the directory git should run in: the discovery root if it is itself a
    /// directory, else its containing directory, falling back to the current directory.
    /// </summary>
    private static string ResolveWorkingDirectory(string discoveryRoot)
    {
        var full = Path.GetFullPath(discoveryRoot);
        if (Directory.Exists(full))
        {
            return full;
        }

        var parent = Path.GetDirectoryName(full);
        return string.IsNullOrEmpty(parent) ? Directory.GetCurrentDirectory() : parent;
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
