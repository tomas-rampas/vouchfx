// Platform.Engine.Runtime — ParallelSuiteRunner (S08-T1, scenario parallelism).
//
// Runs many scenarios CONCURRENTLY using the topology-per-scenario slot model: each scenario
// builds, owns, and disposes its OWN SuiteTopology by fanning the existing no-render core
// ScenarioRunner.RunScenarioOwningTopologyAsync across a bounded pool.  Because each scenario
// gets a fresh topology (and therefore a fresh, clean database), isolation is by construction —
// there is NO RespawnPostgresIsolation here (that holds a single shared connection and is NOT
// concurrency-safe).  The shared-environment validation and isolation-failure abort that
// RunSuiteAsync performs are deliberately NOT ported: both are shared-topology concerns and
// would be wrong for independent per-scenario topologies.
//
// Design pillars (system-architect spec, S08-T1):
//   • Slot model: topology-per-scenario.  Each scenario owns its topology + unique runId.
//   • Concurrency: SemaphoreSlim(degree, degree) gate; scenarios launched as Tasks; Task.WhenAll.
//     Default degree = Math.Max(1, Math.Min(ProcessorCount, 4)) — containers, not CPU, are scarce.
//   • Determinism (byte-stable output regardless of timing):
//       (1) results land in a FIXED slot index = declaration order (not an append list);
//       (2) each core call gets its OWN StringWriter for the raw early-exit diagnostics the core
//           writes directly to `output` (RunScenarioOwningTopologyAsync ~:264-266);
//       (3) after Task.WhenAll, in DECLARATION ORDER, flush each slot's captured raw text to the
//           real `output`, THEN concatenate the slot event buffers in declaration order and call
//           TerminalRenderer.Render ONCE.  No per-scenario rendering.
//   • Verdict/cancellation (complete-all, NO fail-fast):
//       - fold slot verdicts via ScenarioRunner.Elevate in declaration order;
//       - a failing/env-errored scenario does NOT cancel siblings;
//       - the external CancellationToken IS honoured (gate.WaitAsync(ct) + each core call);
//       - a cancelled scenario → synthesise Inconclusive ("Cancelled before completion"),
//         NEVER Fail (§12.1);
//       - a genuine exception escaping the core → synthesise an EnvironmentError slot, never crash;
//       - Task.WhenAll still awaits all launched tasks so every topology disposes (no leak on cancel).
//
// scenarioId on step events — DELIBERATELY NOT ADDED.  Each scenario has a distinct runId, so the
// reporting layer's (runId, stepId) step-kind cache already disambiguates an aggregated multi-run
// stream (TerminalRenderer keys its diff-lookup map by (runId, stepId)).  Adding scenarioId to the
// step events would be a schema change for no behavioural gain here; the T2/T3 schema-freeze ADR
// will record this decision.

using System.Reflection;
using System.Text.Json;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;
using Platform.Engine.Authoring.Ast;
using Platform.Engine.Reporting;
using Platform.Sdk;

namespace Platform.Engine.Runtime;

/// <summary>
/// Executes many scenarios CONCURRENTLY, each owning its own Aspire topology (S08-T1).
/// </summary>
/// <remarks>
/// <para>
/// This is the parallel counterpart to <see cref="ScenarioRunner.RunSuiteAsync"/>.  Where
/// <c>RunSuiteAsync</c> builds ONE shared topology and runs scenarios sequentially against it
/// (resetting Postgres between them), <see cref="RunParallelAsync"/> fans the no-render core
/// (<see cref="ScenarioRunner.RunScenarioOwningTopologyAsync"/>) across a bounded pool — each
/// scenario builds, owns and disposes its OWN topology.  A fresh topology is a clean database,
/// so scenarios are isolated <strong>by construction</strong>; there is no shared-state reset.
/// </para>
/// <para>
/// <strong>Determinism:</strong> results land in a fixed slot by declaration order and the
/// single <see cref="TerminalRenderer"/> render runs over the slots concatenated in declaration
/// order, so the rendered report is byte-stable regardless of which scenario finishes first.
/// </para>
/// <para>
/// <strong>Complete-all:</strong> a failing or env-errored scenario never cancels its siblings;
/// every launched task is awaited so every topology disposes (no container leak), even when the
/// external token is cancelled.
/// </para>
/// </remarks>
public static class ParallelSuiteRunner
{
    /// <summary>
    /// The default ceiling on concurrent scenarios when the caller does not specify one.
    /// Containers — not CPU — are the scarce resource, so the cap is the lesser of the machine's
    /// processor count and a small fixed bound (4).
    /// </summary>
    internal static int DefaultMaxConcurrency =>
        Math.Max(1, Math.Min(Environment.ProcessorCount, 4));

    /// <summary>
    /// The injectable seam matching the shape of
    /// <see cref="ScenarioRunner.RunScenarioOwningTopologyAsync"/>: given a frozen registry, a
    /// scenario's YAML, its name, the Aspire host assembly name, an output writer (for the core's
    /// raw early-exit diagnostics only), and a seed base directory, it builds/owns/disposes a
    /// topology, runs the single scenario, and returns the verdict plus the fully-populated event
    /// buffer.  The default is <see cref="ScenarioRunner.RunScenarioOwningTopologyAsync"/>; tests
    /// inject a fake to exercise the gather/render logic without a container.
    /// </summary>
    /// <param name="registry">The frozen provider registry.</param>
    /// <param name="yamlText">The scenario's raw YAML.</param>
    /// <param name="scenarioName">The scenario's name (used as the event-stream scenarioId).</param>
    /// <param name="appHostAssemblyName">The Aspire host assembly name (R-1; nullable).</param>
    /// <param name="output">The writer that receives the core's raw early-exit diagnostics.</param>
    /// <param name="seedBaseDirectory">Base directory for relative seed fixture paths.</param>
    /// <param name="ct">Propagated to all async operations in the core.</param>
    /// <returns>The scenario's verdict and complete event buffer.</returns>
    public delegate Task<(Verdict Verdict, List<string> Buffer)> ScenarioCoreFunc(
        StepKindRegistry registry,
        string yamlText,
        string scenarioName,
        string? appHostAssemblyName,
        TextWriter output,
        string? seedBaseDirectory,
        CancellationToken ct);

    /// <summary>
    /// Executes the supplied scenarios concurrently — each owning its own Aspire topology — and
    /// returns the per-scenario verdicts plus the suite-level aggregate (S08-T1).
    /// </summary>
    /// <param name="scenarios">
    /// The ordered scenario ASTs.  Unlike <see cref="ScenarioRunner.RunSuiteAsync"/>, the
    /// scenarios need NOT share an environment block — each builds its own topology.
    /// </param>
    /// <param name="scenarioNames">
    /// Per-scenario names (the <c>scenarioId</c> in each scenario's event stream).  Must have the
    /// same length as <paramref name="scenarios"/>.
    /// </param>
    /// <param name="yamlTexts">
    /// Per-scenario raw YAML (for schema validation + compilation inside the core).  Must have the
    /// same length as <paramref name="scenarios"/>.
    /// </param>
    /// <param name="providerAssemblies">The assemblies to scan for providers.</param>
    /// <param name="appHostAssemblyName">
    /// Short name of the Aspire host assembly carrying the DCP metadata (R-1, CLAUDE.md §"Aspire").
    /// </param>
    /// <param name="output">The writer that receives the single rendered terminal report.</param>
    /// <param name="maxConcurrency">
    /// The maximum number of scenarios to run at once.  When <see langword="null"/> the default
    /// (<see cref="DefaultMaxConcurrency"/>) is used.  Must be ≥ 1 when supplied.
    /// </param>
    /// <param name="seedBaseDirectory">
    /// Base directory for relative <c>environment.seed</c> fixture paths.  Defaults to the current
    /// working directory when <see langword="null"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Honoured throughout: a cancelled scenario is recorded as <see cref="Verdict.Inconclusive"/>
    /// (never <see cref="Verdict.Fail"/>, §12.1), and every launched task is still awaited so every
    /// topology disposes.
    /// </param>
    /// <returns>The <see cref="SuiteResult"/> (aggregate verdict + per-scenario breakdown).</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The parallel lists differ in length.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxConcurrency"/> is supplied and less than 1.
    /// </exception>
    public static async Task<SuiteResult> RunParallelAsync(
        IReadOnlyList<ScenarioAst> scenarios,
        IReadOnlyList<string> scenarioNames,
        IReadOnlyList<string> yamlTexts,
        IEnumerable<Assembly> providerAssemblies,
        string? appHostAssemblyName,
        TextWriter output,
        int? maxConcurrency = null,
        string? seedBaseDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(scenarioNames);
        ArgumentNullException.ThrowIfNull(yamlTexts);
        ArgumentNullException.ThrowIfNull(providerAssemblies);
        ArgumentNullException.ThrowIfNull(output);

        // Build the provider registry once (shared, read-only, across all scenarios) and the
        // render-time diff-lookup closure once — exactly as RunSuiteAsync does.
        var registry = StepKindRegistry.BuildAndFreeze(providerAssemblies);
        var diffLookup = ScenarioRunner.BuildParallelDiffLookup(registry);

        return await RunParallelCoreAsync(
            registry,
            scenarios,
            scenarioNames,
            yamlTexts,
            appHostAssemblyName,
            output,
            diffLookup,
            maxConcurrency,
            // Default seam: the real no-render core that builds/owns/disposes a topology.
            runScenario: ScenarioRunner.RunScenarioOwningTopologyAsync,
            seedBaseDirectory,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The testable core of <see cref="RunParallelAsync"/>: takes the already-built registry, the
    /// already-built diff-lookup closure, and the injectable <see cref="ScenarioCoreFunc"/> seam,
    /// so the gather/order/render/aggregate logic can be exercised with a fake core (no container).
    /// </summary>
    /// <param name="registry">The frozen provider registry (passed to each core call).</param>
    /// <param name="scenarios">The ordered scenario ASTs.</param>
    /// <param name="scenarioNames">Per-scenario names (same length as scenarios).</param>
    /// <param name="yamlTexts">Per-scenario raw YAML (same length as scenarios).</param>
    /// <param name="appHostAssemblyName">Aspire host assembly name (R-1; nullable).</param>
    /// <param name="output">The writer that receives the single rendered terminal report.</param>
    /// <param name="diffLookup">The render-time provider-diff lookup closure.</param>
    /// <param name="maxConcurrency">Concurrency ceiling; <see langword="null"/> → default; ≥ 1.</param>
    /// <param name="runScenario">The scenario-core seam (default = the real topology-owning core).</param>
    /// <param name="seedBaseDirectory">Base directory for relative seed fixture paths.</param>
    /// <param name="ct">The external cancellation token, honoured throughout.</param>
    /// <returns>The <see cref="SuiteResult"/>.</returns>
    internal static async Task<SuiteResult> RunParallelCoreAsync(
        StepKindRegistry registry,
        IReadOnlyList<ScenarioAst> scenarios,
        IReadOnlyList<string> scenarioNames,
        IReadOnlyList<string> yamlTexts,
        string? appHostAssemblyName,
        TextWriter output,
        Func<string, JsonElement, string?> diffLookup,
        int? maxConcurrency,
        ScenarioCoreFunc runScenario,
        string? seedBaseDirectory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(scenarioNames);
        ArgumentNullException.ThrowIfNull(yamlTexts);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diffLookup);
        ArgumentNullException.ThrowIfNull(runScenario);

        // Mirror RunSuiteAsync's arg-validation: empty → Pass; mismatched lengths → ArgumentException.
        if (scenarios.Count == 0)
        {
            return new SuiteResult(Verdict.Pass, Array.Empty<(string, Verdict)>());
        }

        if (scenarioNames.Count != scenarios.Count || yamlTexts.Count != scenarios.Count)
        {
            throw new ArgumentException(
                "scenarios, scenarioNames, and yamlTexts must all have the same length.",
                nameof(scenarios));
        }

        var degree = maxConcurrency ?? DefaultMaxConcurrency;
        if (degree < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrency), degree, "maxConcurrency must be 1 or greater.");
        }

        var count = scenarios.Count;

        // Fixed-slot arrays indexed by DECLARATION ORDER — never an append list — so the rendered
        // report and the per-scenario verdict list are byte-stable regardless of completion order.
        var slotVerdicts = new Verdict[count];
        var slotBuffers = new List<string>[count];
        // Each scenario writes its raw early-exit diagnostics to its OWN StringWriter; we flush
        // them to the real output in declaration order AFTER the gather (determinism point 2/3).
        var slotRawWriters = new StringWriter[count];

        // SemaphoreSlim gate caps concurrency at the configured degree.  Disposed after WhenAll.
        var gate = new SemaphoreSlim(degree, degree);

        // Launch every scenario as a task; the gate (not the launch loop) bounds concurrency.
        // We do NOT pass `ct` to Task.Run / the launch loop in a way that would abandon tasks:
        // every launched task is included in WhenAll so its topology disposes (complete-all).
        var tasks = new Task[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                var index = i;
                var name = scenarioNames[index];
                var yaml = yamlTexts[index];
                slotRawWriters[index] = new StringWriter();

                tasks[index] = RunOneSlotAsync(
                    registry,
                    name,
                    yaml,
                    appHostAssemblyName,
                    slotRawWriters[index],
                    seedBaseDirectory,
                    runScenario,
                    gate,
                    index,
                    slotVerdicts,
                    slotBuffers,
                    ct);
            }

            // Await ALL launched tasks (never fail-fast): every topology must dispose, even when
            // a sibling fails or the external token is cancelled.  RunOneSlotAsync never throws —
            // it folds cancellation into Inconclusive and any other exception into EnvironmentError
            // — so WhenAll completes normally and the gather is total.
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            gate.Dispose();
        }

        // Determinism tail: flush each slot's raw early-exit text to the real output in declaration
        // order, then render the concatenated slot buffers ONCE and fold the verdicts in order.
        return RenderAndAggregate(
            scenarioNames, slotVerdicts, slotBuffers, slotRawWriters, output, diffLookup);
    }

    /// <summary>
    /// Runs a single scenario slot: waits on the concurrency gate (honouring the external token),
    /// invokes the core, and records the verdict + buffer into the FIXED slot index.  This method
    /// NEVER throws — it folds cancellation into <see cref="Verdict.Inconclusive"/> and any other
    /// escaping exception into <see cref="Verdict.EnvironmentError"/>, so the caller's
    /// <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/> always completes and
    /// every topology disposes (complete-all, no fail-fast).
    /// </summary>
    private static async Task RunOneSlotAsync(
        StepKindRegistry registry,
        string scenarioName,
        string yamlText,
        string? appHostAssemblyName,
        StringWriter rawWriter,
        string? seedBaseDirectory,
        ScenarioCoreFunc runScenario,
        SemaphoreSlim gate,
        int index,
        Verdict[] slotVerdicts,
        List<string>[] slotBuffers,
        CancellationToken ct)
    {
        // Yield so the launch loop completes promptly and all slots are pending before any runs —
        // the gate (not launch interleaving) is what bounds concurrency.
        await Task.Yield();

        try
        {
            // Honour the external token while waiting for a slot; a cancel here means this scenario
            // never started → Inconclusive (the topology was never built, so nothing to dispose).
            await gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            slotVerdicts[index] = Verdict.Inconclusive;
            slotBuffers[index] = BuildCancelledBuffer(scenarioName);
            return;
        }

        try
        {
            var (verdict, buffer) = await runScenario(
                registry,
                yamlText,
                scenarioName,
                appHostAssemblyName,
                rawWriter,
                seedBaseDirectory,
                ct).ConfigureAwait(false);

            slotVerdicts[index] = verdict;
            slotBuffers[index] = buffer;
        }
        catch (OperationCanceledException)
        {
            // The external token cancelled this scenario mid-flight.  A cancelled scenario is
            // Inconclusive, NEVER Fail (§12.1) — the engine could not determine correctness.
            slotVerdicts[index] = Verdict.Inconclusive;
            slotBuffers[index] = BuildCancelledBuffer(scenarioName);
        }
        catch (Exception ex)
        {
            // Defence-in-depth: the core already maps its known failure modes to per-scenario
            // verdicts and never throws on them.  A genuine exception ESCAPING the core is an
            // engine/infra fault, not a product defect → synthesise an EnvironmentError slot so
            // the gather never crashes and a real Fail is never manufactured from an infra fault.
            // Leave a minimal, redaction-safe trace (exception TYPE only, never the message — §17)
            // on this slot's raw writer so a genuine engine fault is at least diagnosable; the raw
            // writers flush in declaration order, so this stays deterministic.
            rawWriter.WriteLine(
                $"[environment-error] scenario '{scenarioName}' did not complete: {ex.GetType().Name}");
            slotVerdicts[index] = Verdict.EnvironmentError;
            slotBuffers[index] = BuildEnvironmentErrorBuffer(scenarioName);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The shared render-and-aggregate tail used after the gather completes: flushes each slot's
    /// raw early-exit diagnostics to <paramref name="output"/> in declaration order, concatenates
    /// the slot event buffers in declaration order, renders them ONCE via
    /// <see cref="TerminalRenderer"/>, folds the slot verdicts via <see cref="ScenarioRunner.Elevate"/>,
    /// and returns the <see cref="SuiteResult"/>.
    /// </summary>
    private static SuiteResult RenderAndAggregate(
        IReadOnlyList<string> scenarioNames,
        Verdict[] slotVerdicts,
        List<string>[] slotBuffers,
        StringWriter[] slotRawWriters,
        TextWriter output,
        Func<string, JsonElement, string?> diffLookup)
    {
        var allBuffers = new List<string>();
        var perScenario = new List<(string ScenarioName, Verdict Verdict)>(scenarioNames.Count);
        var aggregate = Verdict.Pass;

        for (var i = 0; i < scenarioNames.Count; i++)
        {
            // (1) Flush the raw early-exit diagnostics for this slot, in declaration order, BEFORE
            //     the rendered report — reproducing, byte-for-byte, the single-scenario ordering
            //     (the core writes raw text to `output`, then the caller renders the buffer).
            var raw = slotRawWriters[i]?.ToString();
            if (!string.IsNullOrEmpty(raw))
            {
                output.Write(raw);
            }

            // (2) Concatenate this slot's event buffer in declaration order for the single render.
            if (slotBuffers[i] is { } buffer)
            {
                allBuffers.AddRange(buffer);
            }

            // (3) Fold the verdict in declaration order + record the per-scenario entry.
            perScenario.Add((scenarioNames[i], slotVerdicts[i]));
            aggregate = ScenarioRunner.Elevate(aggregate, slotVerdicts[i]);
        }

        // ONE render over the declaration-order concatenation — never per-scenario.
        TerminalRenderer.Render(allBuffers, output, diffLookup);

        return new SuiteResult(aggregate, perScenario);
    }

    /// <summary>
    /// Builds the minimal event buffer for a scenario that was cancelled before completion: a
    /// scenario-started + scenario-completed pair with verdict <see cref="Verdict.Inconclusive"/>
    /// (§12.1 — a cancelled scenario is Inconclusive, never Fail).
    /// </summary>
    private static List<string> BuildCancelledBuffer(string scenarioName)
    {
        var runId = Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow;
        return new List<string>
        {
            EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = runId,
                Timestamp = now,
                ScenarioId = scenarioName,
            }),
            EventStreamJson.ToLine(new ScenarioCompletedEvent
            {
                RunId = runId,
                Timestamp = now,
                ScenarioId = scenarioName,
                Verdict = Verdict.Inconclusive,
                Counts = new VerdictCounts { Inconclusive = 1 },
            }),
        };
    }

    /// <summary>
    /// Builds the minimal event buffer for a scenario whose core threw an unexpected exception
    /// (defence-in-depth): a scenario-started + scenario-completed pair with verdict
    /// <see cref="Verdict.EnvironmentError"/>.  The structured event records the EnvironmentError
    /// outcome only; the caller writes the exception TYPE name (never the message — §17) to the
    /// slot's raw diagnostic writer.
    /// </summary>
    private static List<string> BuildEnvironmentErrorBuffer(string scenarioName)
    {
        var runId = Guid.NewGuid().ToString("n");
        var now = DateTimeOffset.UtcNow;
        return new List<string>
        {
            EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = runId,
                Timestamp = now,
                ScenarioId = scenarioName,
            }),
            EventStreamJson.ToLine(new ScenarioCompletedEvent
            {
                RunId = runId,
                Timestamp = now,
                ScenarioId = scenarioName,
                Verdict = Verdict.EnvironmentError,
                Counts = new VerdictCounts { EnvError = 1 },
            }),
        };
    }
}
