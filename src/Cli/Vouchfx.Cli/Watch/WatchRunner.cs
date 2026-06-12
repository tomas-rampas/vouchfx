// Vouchfx.Cli — WatchRunner (S08-C-01, watch mode).
//
// The thin I/O shell around WatchSession: it validates that watch mode targets exactly ONE
// file, wires the real engine seams into a WatchSession<SuiteTopology>, runs once, then watches
// the file with a debounced FileSystemWatcher and re-runs on each save until Ctrl-C.  All the
// reuse-vs-rebuild logic lives in the (unit-tested) WatchSession; this layer is deliberately
// small — it only does the real file I/O, debounce, and Ctrl-C handling.
//
// NOTE: this class starts an Aspire topology (via the build seam) and therefore needs Docker;
// it is NOT exercised by the unit tests.  Its non-I/O decision logic is WatchSession, which is
// fully unit-tested with fakes.

using System.Reflection;
using Platform.Engine.Abstractions;
using Platform.Engine.Authoring;
using Platform.Engine.Authoring.Ast;
using Platform.Engine.Orchestration;
using Platform.Engine.Runtime;
using Platform.Sdk;
using Vouchfx.Cli.Watch;

namespace Vouchfx.Cli;

/// <summary>
/// Drives <c>vouchfx run &lt;file&gt; --watch</c>: validates the single-file target, wires the
/// real engine seams into a <see cref="WatchSession{TTopology}"/>, and runs the debounced
/// watch loop (S08-C-01).
/// </summary>
internal static class WatchRunner
{
    // Debounce window: editors often emit several change events per save (write + flush +
    // metadata).  Coalesce a burst into one re-run by waiting this long after the LAST event
    // before re-running.
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Runs the watch loop for the single selected scenario file.
    /// </summary>
    /// <param name="selected">The selection result; watch mode requires exactly one file.</param>
    /// <param name="registry">The frozen provider registry.</param>
    /// <param name="output">The writer that receives status + rendered reports.</param>
    /// <param name="cancellationToken">Cancelled by Ctrl-C to stop watching.</param>
    /// <returns>
    /// The process exit code: <see cref="ExitCodes.UsageError"/> when the selection is not
    /// exactly one parseable file; otherwise <see cref="ExitCodes.Success"/> when the watch loop
    /// exits cleanly (watch mode does not break CI — the last verdict is reported per run, but
    /// the loop's own exit is a clean stop).
    /// </returns>
    internal static async Task<int> RunAsync(
        IReadOnlyList<DiscoveredScenario> selected,
        StepKindRegistry registry,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        // Watch mode is single-file: a directory matching many scenarios (or none that parses)
        // is a usage error — there is exactly one file to watch and one topology to keep alive.
        if (selected.Count != 1)
        {
            await output.WriteLineAsync(
                $"--watch requires exactly one .e2e.yaml file (the selection resolved to "
                + $"{selected.Count}).  Point `vouchfx run` at a single file to watch it.")
                .ConfigureAwait(false);
            return ExitCodes.UsageError;
        }

        var scenario = selected[0];
        if (scenario.Failed)
        {
            await output.WriteLineAsync(
                $"--watch: '{scenario.AbsolutePath}' did not parse: {scenario.ParseError}")
                .ConfigureAwait(false);
            return ExitCodes.UsageError;
        }

        var filePath = scenario.AbsolutePath;
        var appHostAssemblyName = Assembly.GetExecutingAssembly().GetName().Name;

        // The kept-topology isolation is rebuilt by the build seam each time a NEW topology is
        // built (it must bind to the new topology's connection string); held here so the run
        // seam can pass it to the engine.  Disposed alongside the topology it belongs to.
        IScenarioIsolation? isolation = null;

        await using var session = new WatchSession<SuiteTopology>(
            // Compile seam: re-read happens in OnChangeAsync (file content is passed in); here we
            // validate + build the AST and compute the environment hash that drives reuse.
            compile: content => Compile(content, filePath, registry),

            // Build seam: stand up a fresh topology for the compiled scenario's environment, and
            // (re)build the matching isolation for it.
            buildTopologyAsync: async (compiled, ct) =>
            {
                var ast = ((CompiledScenario)compiled).Ast;
                var topology = await SuiteTopology.StartAsync(
                    ast.Environment,
                    appHostAssemblyName,
                    startupTimeout: TimeSpan.FromSeconds(120),
                    seedBaseDirectory: Path.GetDirectoryName(filePath),
                    cancellationToken: ct).ConfigureAwait(false);
                isolation = ScenarioRunner.BuildWatchIsolation(topology);
                return topology;
            },

            // Run seam: run the latest-saved scenario against the kept topology.  `resetAndReseed`
            // is the reuse signal (S08-T10, B2): FALSE on the first run against a just-built+seeded
            // topology (no pre-reset, no re-seed — it already carries the fresh seed and no prior
            // writes, matching plain `vouchfx run`); TRUE on a reuse run, where the kept topology
            // holds the previous run's writes (reset them, then re-apply the seed).
            runAgainstTopologyAsync: async (topology, compiled, resetAndReseed, ct) =>
            {
                var payload = (CompiledScenario)compiled;
                await ScenarioRunner.RunScenarioAgainstKeptTopologyAsync(
                    topology,
                    isolation ?? new NullScenarioIsolation(),
                    registry,
                    payload.Ast,
                    payload.YamlText,
                    payload.ScenarioName,
                    output,
                    resetAndReseed: resetAndReseed,
                    seedBaseDirectory: Path.GetDirectoryName(filePath),
                    cancellationToken: ct).ConfigureAwait(false);
            },

            // Dispose seam: tear down the topology AND its bound isolation together.
            disposeTopologyAsync: async topology =>
            {
                if (isolation is IAsyncDisposable d)
                {
                    await d.DisposeAsync().ConfigureAwait(false);
                }

                isolation = null;
                await topology.DisposeAsync().ConfigureAwait(false);
            },

            report: line => output.WriteLine(line));

        await output.WriteLineAsync(
            $"Watching '{filePath}'.  Saving re-runs the suite (topology re-used while the "
            + "environment is unchanged).  Press Ctrl-C to stop.").ConfigureAwait(false);

        // ── Initial run ───────────────────────────────────────────────────────
        await RunOnceFromDiskAsync(session, filePath, output, cancellationToken).ConfigureAwait(false);

        // ── Watch loop ──────────────────────────────────────────────────────────
        await WatchUntilCancelledAsync(session, filePath, output, cancellationToken)
            .ConfigureAwait(false);

        return ExitCodes.Success;
    }

    /// <summary>
    /// Reads the file from disk and processes one re-run through the session, catching any
    /// orchestration failure so a transient build error does not crash the loop.
    /// </summary>
    private static async Task RunOnceFromDiskAsync(
        WatchSession<SuiteTopology> session,
        string filePath,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        string content;
        try
        {
            content = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // A save in progress can briefly lock the file; report and wait for the next event.
            await output.WriteLineAsync($"--watch: could not read '{filePath}': {ex.Message}")
                .ConfigureAwait(false);
            return;
        }

        await ProcessChangeGuardedAsync(
            ct => session.OnChangeAsync(content, ct),
            output,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one change-processing <paramref name="processChange"/> action under the watch loop's
    /// exception policy (S08-T10, B1): <see cref="OperationCanceledException"/> propagates (so
    /// Ctrl-C stops the loop), and EVERY other exception — including a raw
    /// <see cref="ArgumentException"/> from a malformed <c>environment</c> block — is reported
    /// concisely and SWALLOWED so the watcher KEEPS WATCHING.
    /// </summary>
    /// <param name="processChange">
    /// The change-processing action (in production, <c>session.OnChangeAsync(content, ct)</c>);
    /// receives the cancellation token so it cancels with the loop.
    /// </param>
    /// <param name="output">The writer that receives the concise error line on a caught failure.</param>
    /// <param name="cancellationToken">Cancels the action; an OCE is re-thrown, never swallowed.</param>
    /// <remarks>
    /// Extracted so the keep-watching policy is exercised at the unit level (B1) without a
    /// FileSystemWatcher or a container: a test passes a fake action that throws a non-OCE
    /// exception and asserts it was reported (and did not escape, so the loop would continue).
    /// The catch order is load-bearing — <see cref="OperationCanceledException"/> MUST be caught
    /// before the general <see cref="Exception"/>, or cancellation would be swallowed as a run
    /// error and the loop would never stop on Ctrl-C.
    /// </remarks>
    internal static async Task ProcessChangeGuardedAsync(
        Func<CancellationToken, Task> processChange,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        try
        {
            await processChange(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl-C during a run: let the loop unwind.  MUST be caught BEFORE the general
            // Exception below, or cancellation would be swallowed as a run error.
            throw;
        }
        catch (OrchestrationException oex)
        {
            // A topology build/reset failure is an environment problem (§12.1): report it and
            // KEEP WATCHING so the next save can retry, rather than crashing the loop.
            await output.WriteLineAsync(
                $"--watch: environment error during run: {oex.Message}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // CATCH-ALL (S08-T10, B1): a build/run can throw something OTHER than
            // OrchestrationException — e.g. a malformed `environment` block makes
            // EnvironmentMapper.Map throw a raw ArgumentException, which is documented to
            // propagate as-is through SuiteTopology.StartAsync.  Without this the watcher would
            // DIE on the next bad save.  The loop must survive ANY run/build error and only stop
            // on Ctrl-C — so report a CONCISE message (type + message only; never any captured
            // secret/token) and KEEP WATCHING.
            await output.WriteLineAsync(
                $"--watch: error during run ({ex.GetType().Name}): {ex.Message}")
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Watches <paramref name="filePath"/> with a debounced <see cref="FileSystemWatcher"/>,
    /// re-running through the session on each coalesced save, until the token is cancelled.
    /// </summary>
    private static async Task WatchUntilCancelledAsync(
        WatchSession<SuiteTopology> session,
        string filePath,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = Directory.GetCurrentDirectory();
        }

        var fileName = Path.GetFileName(filePath);

        // A SemaphoreSlim(0) acts as a one-shot "a change is pending" signal; the debounce loop
        // waits on it, then drains a quiet window before re-running (coalescing event bursts).
        using var changeSignal = new SemaphoreSlim(0);

        using var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };

        void OnEvent(object? _, FileSystemEventArgs __)
        {
            // Release at most one permit so the waiter wakes exactly once per quiet window.
            try
            {
                if (changeSignal.CurrentCount == 0)
                {
                    changeSignal.Release();
                }
            }
            catch (SemaphoreFullException)
            {
                // Already signalled; nothing to do.
            }
        }

        watcher.Changed += OnEvent;
        watcher.Created += OnEvent;
        watcher.Renamed += OnEvent;
        watcher.EnableRaisingEvents = true;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await changeSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Debounce: wait out a quiet window so a multi-event save coalesces into one run.
                try
                {
                    await Task.Delay(DebounceWindow, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await RunOnceFromDiskAsync(session, filePath, output, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            watcher.EnableRaisingEvents = false;
        }
    }

    /// <summary>
    /// Validates + builds the AST for the current file contents and computes the
    /// environment-reuse hash, mapping any failure to a reported <see cref="WatchCompileResult"/>
    /// error (so the watch loop keeps running through an authoring slip).
    /// </summary>
    private static WatchCompileResult Compile(string content, string filePath, StepKindRegistry registry)
    {
        ScenarioAst ast;
        try
        {
            var doc = YamlDocumentParser.Parse(content);
            ast = AstBuilder.Build(doc, registry);
        }
        catch (Exception ex)
        {
            return WatchCompileResult.Failure($"Parse / AST error: {ex.Message}");
        }

        var scenarioName = ScenarioNameOf(ast, filePath);
        var envHash = ScenarioRunner.ComputeEnvironmentHash(ast.Environment);
        return WatchCompileResult.Success(
            envHash, new CompiledScenario(ast, content, scenarioName));
    }

    /// <summary>
    /// Derives the report-facing scenario name: the <c>metadata.name</c> when present, else the
    /// file name without its <c>.e2e.yaml</c> extension (mirrors <see cref="RunCommand.ScenarioName"/>).
    /// </summary>
    private static string ScenarioNameOf(ScenarioAst ast, string filePath)
    {
        var metaName = ast.Metadata?.Name;
        if (!string.IsNullOrWhiteSpace(metaName))
        {
            return metaName;
        }

        var name = Path.GetFileName(filePath);
        const string suffix = ".e2e.yaml";
        return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^suffix.Length]
            : name;
    }

    /// <summary>
    /// The opaque compiled-scenario payload threaded through <see cref="WatchSession{TTopology}"/>:
    /// the parsed AST plus the raw YAML and the report-facing name the run seam needs.  The
    /// session never inspects it — it only flows from the compile seam to the build/run seams.
    /// </summary>
    private sealed record CompiledScenario(ScenarioAst Ast, string YamlText, string ScenarioName);
}
