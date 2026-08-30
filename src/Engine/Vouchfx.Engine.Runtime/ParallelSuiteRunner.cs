// Vouchfx.Engine.Runtime — ParallelSuiteRunner (S08-T1, scenario parallelism).
//
// Runs many scenarios CONCURRENTLY using the topology-per-scenario slot model: each scenario
// builds, owns, and disposes its OWN SuiteTopology by fanning the existing no-render core
// ScenarioRunner.RunScenarioOwningTopologyAsync across a bounded pool.  Because each scenario
// gets a fresh topology (and therefore a fresh, clean database), isolation is by construction —
// there is NO RespawnRelationalIsolation (or any IScenarioIsolation) here: the resetters hold a
// single shared connection each and are NOT concurrency-safe.  The shared-environment validation
// and isolation-failure abort that
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
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Reporting;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Runtime;

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
    /// scenario's YAML, its name, its declared secured targets, the Aspire host assembly name, an
    /// output writer (for the core's raw early-exit diagnostics only), and a seed base directory, it
    /// builds/owns/disposes a topology, runs the single scenario, and returns the verdict plus the
    /// fully-populated event buffer.  The default is
    /// <see cref="ScenarioRunner.RunScenarioOwningTopologyAsync"/>; tests inject a fake to exercise
    /// the gather/render logic without a container.
    /// </summary>
    /// <param name="registry">The frozen provider registry.</param>
    /// <param name="yamlText">The scenario's raw YAML.</param>
    /// <param name="scenarioName">The scenario's name (used as the event-stream scenarioId).</param>
    /// <param name="declaredTargets">
    /// This scenario's <c>SecuredTargets.Enumerate</c> walk, taken from the AST this class holds as
    /// a parameter and supplied to the core so its result is correct at every door — including the
    /// two that return before the core has parsed anything (issue #409). A test's fake core is free
    /// to ignore it; the real core attaches it to every assurance it returns.
    /// </param>
    /// <param name="appHostAssemblyName">The Aspire host assembly name (R-1; nullable).</param>
    /// <param name="output">The writer that receives the core's raw early-exit diagnostics.</param>
    /// <param name="seedBaseDirectory">Base directory for relative seed fixture paths.</param>
    /// <param name="livePump">
    /// The optional live events-stream conduit (issue #262). When non-<see langword="null"/>,
    /// the real core (<see cref="ScenarioRunner.RunScenarioOwningTopologyAsync"/>) threads this
    /// down to a per-scenario <see cref="LiveStepEventSink"/> so per-step / per-attempt lines
    /// stream in real time; a fake core injected by a test is free to ignore it. When the core
    /// itself streams every line for a completed slot (the real core's success path does),
    /// <see cref="RunOneSlotAsync"/> does NOT additionally post that slot's returned buffer —
    /// see its remarks.
    /// </param>
    /// <param name="ct">Propagated to all async operations in the core.</param>
    /// <returns>The scenario's verdict and complete event buffer.</returns>
    public delegate Task<ScenarioCoreResult> ScenarioCoreFunc(
        StepKindRegistry registry,
        string yamlText,
        string scenarioName,
        IReadOnlyList<SecuredTarget> declaredTargets,
        string? appHostAssemblyName,
        TextWriter output,
        string? seedBaseDirectory,
        LiveEventPump? livePump,
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
    /// Base directory for relative <c>environment.seed</c> / <c>script.csharp</c> <c>file:</c>
    /// paths, broadcast to every scenario.  Defaults to the current working directory when
    /// <see langword="null"/>.  Superseded per-scenario by <paramref name="seedBaseDirectories"/>
    /// when supplied; retained as the fallback for a caller that has not moved to the
    /// per-scenario list (issue #268).
    /// </param>
    /// <param name="seedBaseDirectories">
    /// Per-scenario base directories (issue #268), in the same order as
    /// <paramref name="scenarios"/>: each scenario's OWN directory.  Unlike
    /// <see cref="ScenarioRunner.RunSuiteAsync"/>'s equivalent parameter — where the shared
    /// topology's single seed stays rooted at the FIRST scenario regardless — HERE every
    /// scenario owns its OWN topology, so its OWN directory roots <em>everything</em> for that
    /// scenario: both its <c>environment.seed</c> AND its <c>script.csharp</c> <c>file:</c>
    /// reference. <see langword="null"/> (the default), or a <see langword="null"/> element,
    /// falls back to the broadcast <paramref name="seedBaseDirectory"/> for that scenario,
    /// preserving pre-#268 behaviour for callers that do not supply this list. When supplied,
    /// must have the same length as <paramref name="scenarios"/>.
    /// </param>
    /// <param name="htmlReportPath">
    /// Optional destination for a self-contained HTML report (S09-D-01, T3).  When
    /// non-<see langword="null"/>, the report is written from the SAME concatenated event buffer
    /// and diff lookup as the single terminal render (parity).  <see langword="null"/> ⇒ no HTML
    /// report.
    /// </param>
    /// <param name="junitReportPath">
    /// Optional destination for a JUnit XML results file (S09-D-01, T3).  When
    /// non-<see langword="null"/>, the file is written from the SAME concatenated event buffer as
    /// the single terminal render.  <see langword="null"/> ⇒ no JUnit report.
    /// </param>
    /// <param name="eventsReportPath">
    /// Optional destination for the raw JSON Lines event stream (S10).  When
    /// non-<see langword="null"/>, the SAME concatenated event buffer the single terminal render
    /// consumed is written there <em>verbatim</em> (one line per element, UTF-8 without a BOM) — an
    /// additive raw passthrough of the frozen v1 stream.  <see langword="null"/> ⇒ no events
    /// artifact.
    /// </param>
    /// <param name="eventsStreamPath">
    /// Optional destination for the INCREMENTAL, tailable JSON Lines event stream (issue #258
    /// scenario-granular liveness; per-step/per-attempt liveness issue #262). When
    /// non-<see langword="null"/>, a <see cref="LiveEventPump"/> (wrapping an
    /// <see cref="Reporting.EventStreamAppender"/>) is opened ONCE and shared across every
    /// concurrently-running slot. A REAL slot's <c>step-started</c> / <c>step-attempt</c> /
    /// <c>step-completed</c> lines stream in real time, from inside that scenario's isolated
    /// run, via a per-scenario <see cref="LiveStepEventSink"/> — interleaved by arrival across
    /// concurrently-running scenarios, disambiguated by each scenario's distinct <c>runId</c>
    /// (mirroring #258's documented "completion order, not declaration order" contract). A slot
    /// that never reaches the core (cancelled before start, or an exception escaping the core)
    /// posts its own synthesised buffer explicitly. Unrelated to the deterministic,
    /// declaration-order <paramref name="eventsReportPath"/> archive, which is still written
    /// ONCE, at the end, from the declaration-order concatenation. <see langword="null"/> ⇒ no
    /// incremental stream (the default).
    /// </param>
    /// <param name="decorate">
    /// Accessibility decoration flag (S10-G-03a): when <see langword="true"/>, the single render over
    /// the declaration-order concatenation decorates each step-verdict line with an ANSI colour + a
    /// per-verdict shape glyph; when <see langword="false"/> (the default) the render is plain text.
    /// The verdict TEXT tokens (the WCAG-1.4.1 guarantee) are unconditional and independent of this
    /// flag.  Computed by the caller (CLI) from <c>--no-decorations</c> + <c>NO_COLOR</c> + output
    /// redirection so the renderer stays a pure function of its inputs.
    /// </param>
    /// <param name="unbuiltDocuments">
    /// The documents the CALLER refused before this method saw them — documents that PARSED but
    /// whose AST could not be built, so they are absent from <paramref name="scenarios"/> entirely
    /// (issue #411). <see langword="null"/> or empty (the default) means the caller discovered no
    /// such document.
    /// <para>
    /// Folded as ONE WHOLE ASSURANCE PER DOCUMENT via <see cref="SecurityAssurance.Worse"/> — the
    /// same discipline every slot here is folded by, and deliberately NOT the suite-wide union
    /// <see cref="ScenarioRunner.RunSuiteAsync"/> uses. Scenarios under <c>--parallel</c> need not
    /// share an environment and each owns its own topology, so a declaration this run never gave a
    /// topology to cannot have been confirmed by a sibling's probe, and pairing it with a sibling's
    /// evidence would be the very cross-scenario pairing <see cref="SecurityAssurance.Worse"/>
    /// exists to prevent.
    /// </para>
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
        IReadOnlyList<string?>? seedBaseDirectories = null,
        string? htmlReportPath = null,
        string? junitReportPath = null,
        string? eventsReportPath = null,
        string? eventsStreamPath = null,
        bool decorate = false,
        IReadOnlyList<UnbuiltDocument>? unbuiltDocuments = null,
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
            htmlReportPath,
            junitReportPath,
            eventsReportPath,
            eventsStreamPath,
            decorate,
            seedBaseDirectories: seedBaseDirectories,
            unbuiltDocuments: unbuiltDocuments,
            ct: cancellationToken).ConfigureAwait(false);
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
    /// <param name="seedBaseDirectory">
    /// Base directory for relative seed fixture paths, broadcast to every scenario. Superseded
    /// per-scenario by <paramref name="seedBaseDirectories"/> when supplied (issue #268).
    /// </param>
    /// <param name="htmlReportPath">Optional HTML report destination (S09-T3); null ⇒ none.</param>
    /// <param name="junitReportPath">Optional JUnit XML report destination (S09-T3); null ⇒ none.</param>
    /// <param name="eventsReportPath">Optional raw JSON Lines events destination (S10); null ⇒ none.</param>
    /// <param name="eventsStreamPath">
    /// Optional destination for the INCREMENTAL, tailable JSON Lines event stream (issue #258;
    /// per-step liveness issue #262); a <see cref="LiveEventPump"/> is opened once (guarded on
    /// non-null), passed into every slot's <paramref name="runScenario"/> call, and shared for
    /// the whole gather. <see langword="null"/> ⇒ no incremental stream (the default).
    /// </param>
    /// <param name="decorate">
    /// Accessibility decoration flag (S10-G-03a): decorate the single render with ANSI colour + a
    /// per-verdict shape glyph when <see langword="true"/>; plain text when <see langword="false"/>
    /// (the default).  The verdict TEXT tokens are unconditional regardless.
    /// </param>
    /// <param name="seedBaseDirectories">
    /// Per-scenario base directories (issue #268), in the same order as
    /// <paramref name="scenarios"/>: each scenario's OWN directory, used for EVERYTHING that
    /// scenario's core call resolves relative paths against — both its
    /// <c>environment.seed</c> and its <c>script.csharp</c> <c>file:</c> reference — because
    /// each scenario here owns its OWN topology (unlike the shared-topology sequential runner,
    /// there is no single suite-wide seed root to preserve). <see langword="null"/> (the
    /// default), or a <see langword="null"/> element, falls back to the broadcast
    /// <paramref name="seedBaseDirectory"/> for that scenario. When supplied, must have the
    /// same length as <paramref name="scenarios"/>.
    /// </param>
    /// <param name="unbuiltDocuments">
    /// The documents that parsed but could not be built into an AST, and are therefore absent from
    /// <paramref name="scenarios"/> (issue #411). Seeds the assurance fold below; see
    /// <see cref="RunParallelAsync"/>'s parameter of the same name.
    /// </param>
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
        string? htmlReportPath = null,
        string? junitReportPath = null,
        string? eventsReportPath = null,
        string? eventsStreamPath = null,
        bool decorate = false,
        IReadOnlyList<string?>? seedBaseDirectories = null,
        IReadOnlyList<UnbuiltDocument>? unbuiltDocuments = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(scenarioNames);
        ArgumentNullException.ThrowIfNull(yamlTexts);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diffLookup);
        ArgumentNullException.ThrowIfNull(runScenario);

        // Mirror RunSuiteAsync's arg-validation: empty → Pass; mismatched lengths → ArgumentException.
        //
        // …and mirror its empty arm's ANSWER too (Copilot, PR #416). This arm returns before the
        // gather, so it used to discard `unbuiltDocuments` and hand back a default
        // `SecurityAssurance.None`; the fold below is the same one the gather calls, so a caller
        // that supplies documents with no scenarios gets the same evidence on both run paths
        // instead of silence on both. The verdict is unchanged — an unbuilt document contributes
        // to `Assurance` and to nothing else here either.
        if (scenarios.Count == 0)
        {
            return new SuiteResult(Verdict.Pass, Array.Empty<(string, Verdict)>())
            {
                Assurance = UnbuiltDocument.AssureAll(unbuiltDocuments, registry),
            };
        }

        if (scenarioNames.Count != scenarios.Count
            || yamlTexts.Count != scenarios.Count
            || (seedBaseDirectories is not null && seedBaseDirectories.Count != scenarios.Count))
        {
            throw new ArgumentException(
                "scenarios, scenarioNames, yamlTexts, and (when supplied) seedBaseDirectories "
                + "must all have the same length.",
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

        // REQ-018's per-scenario security evidence, one slot each for the same determinism reason.
        //
        // THIS IS THE SITE THAT WALKS `SecuredTargets.Enumerate` (security-assurance-derivation,
        // REQ-002). The core cannot: it validates before it parses, so two of its doors return
        // with no AST to ask — and asking there is exactly what forced the speculative re-parse
        // this change removed. `scenarios` is a PARAMETER here, so the walk happens once per
        // scenario on EVERY path, including the ones the core aborts before parsing anything.
        //
        // THE WALK IS NOW PASSED INTO THE CORE RATHER THAN RE-ATTACHED TO WHAT IT RETURNS
        // (issue #409). Same walk, same site, one consumer instead of two: the core's own result is
        // correct at every door, so this class no longer repairs it afterwards. The slots below
        // still carry `declared` for the three assurances this class SYNTHESISES itself — the two
        // cancellation paths and the escaped-exception path, where no core ran to be handed
        // anything.
        //
        // Per scenario rather than per suite, unlike the shared-topology runner: scenarios under
        // `--parallel` need NOT declare a common environment block, so each one's declaration is
        // its own. That is also why the fold below keeps whole assurances rather than unioning
        // their fields — see SecurityAssurance.Worse.
        var slotAssurances = new SecurityAssurance[count];
        // Each scenario writes its raw early-exit diagnostics to its OWN StringWriter; we flush
        // them to the real output in declaration order AFTER the gather (determinism point 2/3).
        var slotRawWriters = new StringWriter[count];

        // SemaphoreSlim gate caps concurrency at the configured degree.  Disposed after WhenAll.
        var gate = new SemaphoreSlim(degree, degree);

        // ── Incremental events stream (issue #258; per-step liveness issue #262) ──────
        // Opened ONCE, guarded on a non-null path, and disposed (via `await using`) once this
        // method returns — after every slot has completed and RenderAndAggregate has run.
        // Entirely separate from `eventsReportPath`: the --events / --json archive is still
        // written once, at the end, by RenderAndAggregate's FileReportWriter.WriteFileReports
        // call, from the declaration-order concatenation.
        //
        // Issue #262: LiveEventPump wraps the same EventStreamAppender, now behind a bounded,
        // non-blocking conduit. It is passed into EVERY slot's `runScenario` call — the real
        // core (ScenarioRunner.RunScenarioOwningTopologyAsync) threads it down to a
        // per-scenario LiveStepEventSink so per-step / per-attempt lines from EVERY
        // concurrently-running scenario stream to the SAME file as they happen (interleaved by
        // arrival — each scenario's distinct runId disambiguates, exactly as #258 already
        // documents for scenario-granular completion order).
        await using var livePump = eventsStreamPath is not null
            ? new LiveEventPump(eventsStreamPath, output)
            : null;

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

                // Issue #268: each scenario owns its OWN topology here, so its OWN directory
                // (when supplied) roots EVERYTHING for that scenario's core call — both its
                // environment.seed and its script.csharp file: reference. Falls back to the
                // broadcast seedBaseDirectory for a caller that has not moved to the
                // per-scenario list.
                var scenarioBaseDirectory = seedBaseDirectories?[index] ?? seedBaseDirectory;

                tasks[index] = RunOneSlotAsync(
                    registry,
                    name,
                    yaml,
                    appHostAssemblyName,
                    slotRawWriters[index],
                    scenarioBaseDirectory,
                    runScenario,
                    gate,
                    index,
                    slotVerdicts,
                    slotBuffers,
                    slotAssurances,
                    SecuredTargets.Enumerate(scenarios[index].Environment).ToArray(),
                    livePump,
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
            scenarioNames, slotVerdicts, slotBuffers, slotAssurances, slotRawWriters, output,
            diffLookup, UnbuiltDocument.AssureAll(unbuiltDocuments, registry),
            htmlReportPath, junitReportPath, eventsReportPath, decorate);
    }

    /// <summary>
    /// Runs a single scenario slot: waits on the concurrency gate (honouring the external token),
    /// invokes the core, and records the verdict + buffer into the FIXED slot index.  This method
    /// NEVER throws — it folds cancellation into <see cref="Verdict.Inconclusive"/> and any other
    /// escaping exception into <see cref="Verdict.EnvironmentError"/>, so the caller's
    /// <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/> always completes and
    /// every topology disposes (complete-all, no fail-fast).
    /// </summary>
    /// <param name="livePump">
    /// The optional live events-stream conduit (issue #258 scenario-granular liveness; #262
    /// per-step liveness). Passed into <paramref name="runScenario"/> — the real core threads it
    /// down to a per-scenario <see cref="LiveStepEventSink"/>, so a completing REAL slot's lines
    /// were already streamed live, as they happened, and are NOT re-posted here (see the success
    /// branch below). The two buffers this method synthesises ITSELF — the cancelled-before-start
    /// and the exception-escaped slot buffers — are never seen by any core, so THOSE are posted
    /// explicitly. The pump is shared across every concurrently-running slot and is internally
    /// non-blocking + thread-safe, so concurrent calls here can never interleave mid-record.
    /// </param>
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
        SecurityAssurance[] slotAssurances,
        IReadOnlyList<SecuredTarget> declared,
        LiveEventPump? livePump,
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
            slotBuffers[index] = BuildCancelledBuffer(
                scenarioName, "Cancelled before this scenario started; no topology was built.");
            // Cancellation is not a REFUSAL: the engine did not reject the declaration, it never
            // got to it. No refusal recorded ⇒ nothing raised, which is the behaviour that shipped.
            slotAssurances[index] = SecurityAssurance.None.Declaring(declared);
            // This buffer is synthesised HERE (the core never ran), so it must be posted
            // explicitly — nothing else will ever stream it.
            livePump?.PostRange(slotBuffers[index]);
            return;
        }

        try
        {
            var result = await runScenario(
                registry,
                yamlText,
                scenarioName,
                declared,
                appHostAssemblyName,
                rawWriter,
                seedBaseDirectory,
                livePump,
                ct).ConfigureAwait(false);

            slotVerdicts[index] = result.Verdict;
            slotBuffers[index] = result.Buffer;

            // REQ-018: written into a fixed slot rather than folded into shared state, for the
            // same reason every other per-scenario value here is — slots complete in arbitrary
            // order and a shared read-modify-write across them would be a data race. The
            // aggregation tail folds the array once the gather has joined.
            //
            // TAKEN WHOLE, NOT REPAIRED (issue #409). This line used to read
            // `result.Assurance.Declaring(declared)`, re-attaching the walk over whatever the core
            // returned because the core left `Declared` empty at its two pre-parse doors. `declared`
            // now goes IN, above, so the core's answer is already complete and re-attaching it here
            // would be a second corrector for a value that no longer needs correcting — which is
            // how one rule comes to have two spellings again.
            //
            // THE ONE VISIBLE CONSEQUENCE, stated because it is a test-double contract: a fake core
            // is now BELIEVED about what its document declared. Before, a fake returning
            // `SecurityAssurance.None` had this class's own walk stamped onto it; now it reports an
            // empty declaration, and a fake standing in for the real core must attach the
            // `declared` it is handed if the slot is meant to declare anything. No existing fake
            // does — every one of them drives an unsecured AST, whose walk is empty either way —
            // so nothing silently changed meaning; a future one that needs a declaration has to say
            // so, which is the right way round.
            slotAssurances[index] = result.Assurance;
            // Issue #262: NO livePump?.PostRange(buffer) here. The real core
            // (ScenarioRunner.RunScenarioOwningTopologyAsync) already streamed every one of this
            // slot's lines live — as they happened — via the per-scenario LiveStepEventSink plus
            // its own explicit early-exit/framing posts, all threaded through the SAME livePump
            // just passed in above. Re-posting the reconstructed buffer here would duplicate
            // every line in the live file. A test's FAKE core that ignores `livePump` simply
            // streams nothing for that slot — acceptable, since no test combines a fake core
            // with a real live pump. `slotBuffers[index]` above still carries the complete,
            // unaffected reconstruction for the end-of-run `--events` archive.
        }
        catch (OperationCanceledException)
        {
            // The external token cancelled this scenario mid-flight.  A cancelled scenario is
            // Inconclusive, NEVER Fail (§12.1) — the engine could not determine correctness.
            slotVerdicts[index] = Verdict.Inconclusive;
            slotBuffers[index] = BuildCancelledBuffer(
                scenarioName, "Cancelled while this scenario was running.");
            slotAssurances[index] = SecurityAssurance.None.Declaring(declared);
            livePump?.PostRange(slotBuffers[index]);
        }
        catch (Exception ex)
        {
            // Defence-in-depth: the core already maps its known failure modes to per-scenario
            // verdicts and never throws on them.  A genuine exception ESCAPING the core is an
            // engine/infra fault, not a product defect → synthesise an EnvironmentError slot so
            // the gather never crashes and a real Fail is never manufactured from an infra fault.
            //
            // WHAT THIS CATCH CANNOT TELL APART, TRACKED AS ISSUE #466. `EnvironmentError` is the
            // RIGHT classification for a genuine infrastructure fault and the WRONG one for an
            // engine or provider defect, which — on a run that executed nothing — exits 0. This
            // frame cannot distinguish the two: it sees only an exception type. Issue #413 closed
            // the one route that was known to arrive here (a throwing provider `Bind`, now a
            // `PipelineResult.Failure` returned before this frame is ever reached), and #466 holds
            // the general question, because reclassifying every escape would move `EnvironmentError`
            // semantics for real infra faults too and that is a design decision rather than a fix.
            // Do NOT widen this catch without reading it.
            // Leave a minimal, redaction-safe trace (exception TYPE only, never the message — §17)
            // on this slot's raw writer so a genuine engine fault is at least diagnosable; the raw
            // writers flush in declaration order, so this stays deterministic.
            //
            // Issue #266, Item 4: scenarioName is author-controlled (metadata.name) and this raw
            // writer's content is flushed VERBATIM to the terminal by RenderAndAggregate below
            // (output.Write(raw)) — it bypasses TerminalRenderer's own GetStr choke entirely, so
            // it must be sanitised at THIS write site.
            rawWriter.WriteLine(
                DisplaySanitiser.SanitiseForDisplay(
                    $"[environment-error] scenario '{scenarioName}' did not complete: {ex.GetType().Name}"));
            slotVerdicts[index] = Verdict.EnvironmentError;
            slotBuffers[index] = BuildEnvironmentErrorBuffer(scenarioName, ex.GetType().Name);
            // An engine fault escaping the core is not a refusal of the declaration either — it is
            // the same class as a topology that would not come up, and raises nothing.
            slotAssurances[index] = SecurityAssurance.None
                .Declaring(declared)
                .Refusing(SecurityAbortKind.TopologyUnavailable);
            livePump?.PostRange(slotBuffers[index]);
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
    /// writes the optional HTML / JUnit file reports — and the raw JSON Lines events stream (S10) —
    /// from that SAME concatenated buffer + diff lookup (S09-D-01, T3), and returns the
    /// <see cref="SuiteResult"/>.
    /// </summary>
    private static SuiteResult RenderAndAggregate(
        IReadOnlyList<string> scenarioNames,
        Verdict[] slotVerdicts,
        List<string>[] slotBuffers,
        SecurityAssurance[] slotAssurances,
        StringWriter[] slotRawWriters,
        TextWriter output,
        Func<string, JsonElement, string?> diffLookup,
        SecurityAssurance unbuiltAssurance,
        string? htmlReportPath = null,
        string? junitReportPath = null,
        string? eventsReportPath = null,
        bool decorate = false)
    {
        var allBuffers = new List<string>();
        var perScenario = new List<(string ScenarioName, Verdict Verdict)>(scenarioNames.Count);
        var aggregate = Verdict.Pass;

        // The fold SEEDS from the unbuilt documents' assurance rather than from
        // SecurityAssurance.None (issue #411). `Worse` is what folds each slot in below, and it is
        // order-independent in exactly the two projections read off the result — `Unconfirmed` and
        // `Refusal` — so seeding rather than appending changes nothing but where the value enters.
        // SecurityAssurance.None is the identity when the caller had no unbuilt document, which is
        // every caller that does no discovery of its own.
        var assurance = unbuiltAssurance;

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

            // (4) REQ-018: fold the security evidence AFTER the gather has joined, so this read of
            //     the slot array is single-threaded. One scenario that could not have its declared
            //     security confirmed is enough to break CI without --fail-on-env-error.
            //
            //     WHOLE assurances are folded, never field-by-field: a scenario's declaration must
            //     never be paired with a DIFFERENT scenario's refusal, which under `--parallel`
            //     (where scenarios need not share an environment) would redden a suite that no
            //     single scenario reddens. See SecurityAssurance.Worse.
            assurance = SecurityAssurance.Worse(assurance, slotAssurances[i]);
        }

        // ONE render over the declaration-order concatenation — never per-scenario.  When
        // `decorate` is set (interactive TTY, S10-G-03a) the step-verdict lines carry the colour +
        // shape-glyph accessibility decorations; otherwise the render is plain text.
        TerminalRenderer.Render(allBuffers, output, decorate, diffLookup);

        // Optional file reports (S09-D-01, T3; S10 events): write the HTML / JUnit artifacts — and
        // the raw JSON Lines events stream — from the SAME concatenated buffer + diffLookup the
        // single terminal render just consumed, so the parallel run's file reports are byte-for-byte
        // the parity of its terminal output (and of the sequential path's reports for the same
        // buffer).  A null path writes nothing.  `output` is the diagnostics sink: a bad --html /
        // --junit / --events path is caught PER FILE and reported there, so report writing can NEVER
        // change the already-computed verdict / exit code.
        FileReportWriter.WriteFileReports(
            allBuffers, diffLookup, htmlReportPath, junitReportPath, output, eventsPath: eventsReportPath);

        return new SuiteResult(aggregate, perScenario)
        {
            Assurance = assurance,

            // #369, and DERIVED rather than flagged. The sequential path can set this from the
            // route it took, because CompleteWithoutTopologyAsync IS its without-topology path.
            // There is no such single route here: under --parallel every slot owns its OWN
            // topology, so "did anything execute" is a property of the whole fan-out and not of
            // any one return.
            //
            // A step event is the evidence, because it exists if and only if a step ran. Reading
            // it off the same concatenated buffer the renderers consume also means this answer
            // cannot disagree with the artefacts, which is the property a second flag would have
            // been free to break.
            //
            // The sequential/parallel DIVERGENCE is the point: without this, `--parallel 1` on a
            // suite refused before any topology exited 0 while the identical bare run exited 4.
            // This codebase has measured that exact shape before and treats it as its own defect
            // class, not as a nuance.
            ExecutedAnyScenario = allBuffers.Exists(ContainsStepEvent),
        };
    }

    /// <summary>
    /// Whether an event line is a <c>step-started</c> record — the evidence that a step actually
    /// ran (#369).
    /// </summary>
    /// <remarks>
    /// A substring test over the serialised line rather than a parse. What makes that safe is the
    /// SERIALISER, not the shape of the data: <c>EventStreamJson.Options</c> is frozen with
    /// <c>WriteIndented = false</c> (pinned by <c>EventEnvelopeTests.ToLine_Output_ContainsNoEmbeddedNewlines</c>,
    /// since an indented writer emits newlines between members), and STJ's encoder escapes a
    /// quote inside any string value. So the needle cannot re-form inside a field's VALUE even
    /// when an author plants it there deliberately — measured, with a service property named
    /// exactly <c>"type":"step-started"</c>, whose schema diagnostic reaches this buffer's
    /// <c>message</c> and does NOT flip the exit code.
    /// <para>
    /// That guarantee covers values, not property NAMES: the needle also forms from any serialised
    /// object carrying a member literally named <c>type</c> whose value is <c>step-started</c>.
    /// Such an object IS reachable — <c>StepAttemptEvent.Observation</c> and
    /// <c>StepCompletedEvent.Observation</c> are <c>JsonElement?</c>, assigned on the produce side
    /// from <c>ScenarioRunner.BuildStepObservation</c>, and a <c>JsonElement</c> serialises
    /// property names verbatim, so SUT-controlled content can form the needle.
    /// <para>
    /// <strong>What makes that harmless is WHERE it can appear, not that it cannot.</strong>
    /// <c>Observation</c> rides only on step events, so a needle formed inside one sits on a line
    /// that already IS a step event — the predicate's answer is right for the wrong reason, but it
    /// is right. The hazard would become real the day a <c>JsonElement</c>, or any member carrying
    /// unescaped property names, is added to a NON-step event. If that happens, this predicate
    /// must become a structured read before that event ships.
    /// </para>
    /// <para>
    /// The two other candidates are closed for the reason the previous draft gave:
    /// <c>CorrelationIds</c> is declared but never assigned anywhere in <c>src/</c>, so
    /// <c>WhenWritingNull</c> omits it, and <c>Extra</c> is <c>[JsonExtensionData]</c> populated
    /// only on <c>FromLine</c>. That enumeration was incomplete when first written, which is worth
    /// recording: the point of this remark is that the ORIGINAL defence was incomplete, and it was
    /// replaced once by another incomplete one.
    /// </para>
    /// <para>
    /// Do not defend this by arguing a false positive "would still mean a step event was
    /// described" — that was the original reasoning and it does not hold: a schema error is not a
    /// step event, and this predicate decides an exit code (#369). The escaping is the guarantee;
    /// the coupling to it is pinned by
    /// <c>NothingExecutedExitCodeParityTests.ExecuteAsync_RefusalDiagnosticQuotingTheStepEventToken_StillExitsInconclusive</c>.
    /// </para>
    /// <para>
    /// A structured read is viable if that coupling ever becomes inconvenient: this runs after
    /// <c>TerminalRenderer.Render</c> and <c>FileReportWriter.WriteFileReports</c> have each
    /// already deserialised the same buffer, so there is no hot path here to protect. It would
    /// need <c>FromLine</c>'s per-line malformed-input tolerance, which those renderers have and
    /// this call site does not.
    /// </para>
    /// The constant is referenced, never spelled, so a rename moves this with it.
    /// </remarks>
    private static bool ContainsStepEvent(string eventLine) =>
        eventLine.Contains("\"type\":\"" + EventTypes.StepStarted + "\"", StringComparison.Ordinal);
    /// <summary>
    /// Builds the minimal event buffer for a scenario that was cancelled before completion: a
    /// scenario-started + scenario-completed pair with verdict <see cref="Verdict.Inconclusive"/>
    /// (§12.1 — a cancelled scenario is Inconclusive, never Fail).
    /// </summary>
    /// <param name="cause">
    /// Which of the two cancellation moments this was (#372).  Both are cancellations, but they
    /// are not the same fact and an artefact reader cannot tell them apart from a bare
    /// Inconclusive: one scenario never started, the other was stopped mid-flight with a topology
    /// already up.
    /// </param>
    private static List<string> BuildCancelledBuffer(string scenarioName, string cause)
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

            // No ledger: this buffer is synthesised by the fan-out itself, which owns no secret
            // accessor — the per-scenario one lives inside the core that never ran or was
            // abandoned — and the text is a fixed literal with nothing interpolated into it.
            StepEventBuilder.ScenarioCompletedLine(
                runId,
                now,
                scenarioName,
                Verdict.Inconclusive,
                new VerdictCounts { Inconclusive = 1 },
                ledger: null,
                cause),
        };
    }

    /// <summary>
    /// Builds the minimal event buffer for a scenario whose core threw an unexpected exception
    /// (defence-in-depth): a scenario-started + scenario-completed pair with verdict
    /// <see cref="Verdict.EnvironmentError"/>.  The structured event records the EnvironmentError
    /// outcome only; the caller writes the exception TYPE name (never the message — §17) to the
    /// slot's raw diagnostic writer.
    /// </summary>
    /// <param name="exceptionTypeName">
    /// The escaping exception's TYPE name, never its message (§17) — the same half the slot's raw
    /// writer gets. Stamped so the written artefacts name the fault too (#372): before this a
    /// slot that crashed produced a bare EnvironmentError in --junit/--html/--events and the only
    /// clue was a terminal line the artefacts do not carry.
    /// </param>
    private static List<string> BuildEnvironmentErrorBuffer(string scenarioName, string exceptionTypeName)
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

            // No ledger, and no message to scrub against one: the exception's own message is
            // deliberately NOT carried here (§17), only its type name.
            StepEventBuilder.ScenarioCompletedLine(
                runId,
                now,
                scenarioName,
                Verdict.EnvironmentError,
                new VerdictCounts { EnvError = 1 },
                ledger: null,
                $"Scenario did not complete: {exceptionTypeName}."),
        };
    }
}
