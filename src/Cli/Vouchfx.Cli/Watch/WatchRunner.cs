// Vouchfx.Cli — WatchRunner (S08-C-01, watch mode).
//
// The thin I/O shell around WatchSession: it validates that watch mode targets exactly ONE
// file, wires the real engine seams into a WatchSession<IKeptTopology>, runs once, then watches
// the file with a debounced FileSystemWatcher and re-runs on each save until Ctrl-C.  All the
// reuse-vs-rebuild logic lives in the (unit-tested) WatchSession; this layer is deliberately
// small — it only does the real file I/O, debounce, and Ctrl-C handling.
//
// NOTE, NARROWED BY #364: `RunAsync` — the file I/O, the FileSystemWatcher and the production
// topology starter — still needs Docker and is not exercised by the unit tests.  `CreateSession`
// is NOT in that set any more: it takes the topology starter as a parameter over the
// IKeptTopology seam, so a Docker-free test drives the real compile / build / run / dispose /
// report wiring against a double.  The reuse-vs-rebuild decision itself remains WatchSession's.

using System.Reflection;
using Vouchfx.Cli.Watch;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Security;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Runtime;
using Vouchfx.Engine.Runtime.Secrets;
using Vouchfx.Sdk;

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
    /// <remarks>
    /// REQ-018's verdict-to-exit-code carve-out has NO counterpart here, and its absence is
    /// deliberate rather than an omission: this method returns only
    /// <see cref="ExitCodes.UsageError"/> or <see cref="ExitCodes.Success"/> and never calls
    /// <c>ExitCodes.FromVerdict</c>, because the sentence above — watch mode does not break CI —
    /// is the whole design. A flag that carved an exit code out of a verdict here would have no
    /// consumer: the per-run verdict is reported, and the loop's exit says only whether the loop
    /// itself stopped cleanly.
    /// </remarks>
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
            // Issue #266, Item 4: scenario.ParseError embeds raw author YAML content verbatim
            // (see RunCommand's equivalent site); AbsolutePath is filesystem-derived but
            // sanitised too for consistency.
            await output.WriteLineAsync(
                DisplaySanitiser.SanitiseForDisplay(
                    $"--watch: '{scenario.AbsolutePath}' did not parse: {scenario.ParseError}"))
                .ConfigureAwait(false);
            return ExitCodes.UsageError;
        }

        var filePath = scenario.AbsolutePath;
        var appHostAssemblyName = Assembly.GetExecutingAssembly().GetName().Name;

        // ── The watch SESSION's resolved-secret ledger (client-key-password EDGE-007) ──
        //
        // ONE ledger for the whole session, and SESSION scope is the load-bearing word. The
        // topology is KEPT across saves, so the probe below resolves `clientKeyPassword` ONCE per
        // topology while the step path runs on every save after it.
        //
        // BE PRECISE ABOUT THE BUILD SEAM: it is per-REBUILD, not per-save.
        // WatchSession.OnChangeAsync invokes it only when the TOPOLOGY FINGERPRINT changes (or on
        // the first run) — a steps-only edit that changes no target name re-uses the topology and
        // never reaches it, and a save a pre-topology gate refuses never reaches it at all. So a
        // build-seam-scoped ledger would already be shared across every reusing save, and stating
        // the reason as "otherwise it would be per-save" would overstate the failure and invite a
        // maintainer to narrow the scope back on a premise that was never true.
        //
        // THE REASON THE SCOPE MUST BE THE SESSION IS CAPTURE ORDER, AND IT BITES ON THE REBUILD
        // SAVE ITSELF. The guard captures this variable BY VALUE before the build seam runs:
        // RunOnceFromDiskAsync reads it at the call site below, hands that instance to
        // ProcessChangeGuardedAsync, and only then does session.OnChangeAsync reach the build
        // seam. Under any narrower scope the rebuild save's probe resolves into ledger N and
        // throws, while the OrchestrationException catch — the one sink that exists to receive a
        // probe failure — is already holding ledger N-1 and scrubs against the wrong object. The
        // sink and the resolution must therefore share an instance that predates both.
        //
        // Every consumer below (the probe scope, the run seam, all four sinks) gets THIS instance.
        //
        // ── The costs, both of them, stated rather than argued away ──
        //
        // 1. RETAINED PLAINTEXT, FOR LONGER THAN ANYWHERE ELSE IN THE ENGINE.
        //    ResolvedSecretLedger holds a plaintext `string` copy of each revealed value for its
        //    own lifetime — unavoidable, since you cannot scrub a value you do not hold — and its
        //    own remarks note that a run-scoped ledger holds those copies "for the run, not for
        //    one scenario". A session-scoped one holds them for as long as `--watch` is left
        //    running, which is a developer's whole afternoon rather than a run's few minutes. The
        //    copies are Default-ALC and never serialised; they are NOT meaningfully reclaimed
        //    before exit, because this session ends only when Ctrl-C unwinds RunAsync and the
        //    process follows it out.
        //
        // 2. A WIDER SCRUB WINDOW, which is a correctness cost and not merely a memory one.
        //    The run seam feeds this same ledger to the step path, so EVERY secret any step
        //    resolves accumulates here too, for the session. Scrub replaces every ordinal
        //    occurrence of every recorded value, so a short or common value resolved on save 1
        //    goes on redacting unrelated substrings from every later save's output. Before
        //    EDGE-007 that accumulation was bounded by one re-run.
        //
        //    NOTHING CLEARS A RECORDED VALUE WITHIN A SESSION. An author who edits a
        //    `${secret:…}` reference mid-session leaves the OLD value recorded for the rest of
        //    it, so a value that was short or common goes on corrupting later diagnostics — a
        //    one-character secret redacts a single letter everywhere it appears. THE REMEDY IS
        //    TO RESTART `--watch`: the ledger is a plain field of this method's frame and a new
        //    session starts with an empty one. Deliberately no length floor: over-redaction
        //    fails safe and is conspicuous, whereas a floor would silently under-redact a short
        //    secret, which is the failure that matters.
        //
        // Both are accepted for the same reason: the alternative is a resolved passphrase
        // surviving into a rendered diagnostic on a later save, and that is worse than either.
        var sessionSecretLedger = new ResolvedSecretLedger();

        // The #375 sibling net, SESSION-scoped for exactly the reason the ledger above is: a
        // resolved security-material path handed to a client library while the topology was built
        // must be substitutable from text a step emits on a later save against that same kept
        // topology. Held here rather than per-rebuild for the same reason EDGE-007 moved the
        // secret ledger up.
        var sessionPathLedger = new SecurityPathDisclosureLedger();

        await using var session = CreateSession(
            filePath, registry, output, appHostAssemblyName, sessionSecretLedger,
            sessionPathLedger, StartTopologyAsync);

        // Issue #266, Item 4: `filePath` is author/CLI-supplied and reaches a terminal verbatim
        // here. Sanitised, not scrubbed: this line is written BEFORE the first run, so no probe
        // has resolved anything and there is nothing in the ledger a scrub could match. It is the
        // banner's only untrusted component.
        await output.WriteLineAsync(
            DisplaySanitiser.SanitiseForDisplay(
                $"Watching '{filePath}'.  Saving re-runs the suite (topology re-used while nothing "
                + "it was built from changes).  Press Ctrl-C to stop.")).ConfigureAwait(false);

        // ── Initial run ───────────────────────────────────────────────────────
        await RunOnceFromDiskAsync(
                session, filePath, output, sessionSecretLedger, sessionPathLedger, cancellationToken)
            .ConfigureAwait(false);

        // ── Watch loop ──────────────────────────────────────────────────────────
        await WatchUntilCancelledAsync(
                session, filePath, output, sessionSecretLedger, sessionPathLedger, cancellationToken)
            .ConfigureAwait(false);

        return ExitCodes.Success;
    }

    /// <summary>
    /// Wires the real engine seams into a <see cref="WatchSession{TTopology}"/> over the
    /// <see cref="IKeptTopology"/> construction seam (#364) — extracted from
    /// <see cref="RunAsync"/> so every one of them is reachable from a Docker-free test.
    /// </summary>
    /// <param name="filePath">The watched file (its directory is the suite directory).</param>
    /// <param name="registry">The frozen provider registry.</param>
    /// <param name="output">The writer that receives status, diagnostics and rendered reports.</param>
    /// <param name="appHostAssemblyName">The DCP-metadata-carrying assembly's short name.</param>
    /// <param name="sessionSecretLedger">
    /// The SESSION's resolved-secret ledger (EDGE-007) — the same instance the caller's sinks
    /// already captured. See its declaration in <see cref="RunAsync"/> for why the scope must be the
    /// session and not this method.
    /// </param>
    /// <param name="sessionPathLedger">
    /// The SESSION's security-path disclosure ledger (issue #375), scoped for the same reason and
    /// over the same lifetime as <paramref name="sessionSecretLedger"/>.
    /// </param>
    /// <param name="startTopologyAsync">
    /// Builds a topology from the plan's <see cref="TopologyRequest"/> and the resolved security
    /// accessor. The production value is <see cref="StartTopologyAsync"/>, which is
    /// <c>TopologyRequest.StartAsync</c> and nothing else; a test substitutes a starter returning a
    /// double, which is what makes everything downstream of this seam — confirmation rendering,
    /// transport-notice replay, reset/reseed ordering, the target sets the request carries —
    /// assertable at unit speed for the first time (#364).
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>The ACCESSOR is built here, not inside the starter</strong>, and that split is
    /// deliberate: it is derived from the AST and owns <c>X509Certificate2</c> instances this method
    /// disposes in its own <c>finally</c>, so a topology double must not have to construct
    /// certificates to stand in for a topology. It is nonetheless PASSED to the starter rather than
    /// captured by it, so a test can see that one arrives at all — the argument that was omitted
    /// once and left every secured suite unrunnable under <c>--watch</c>. That a suite DECLARING
    /// <c>security</c> gets a working accessor is a different claim and needs real certificate
    /// material: it belongs to
    /// <c>Vouchfx.Engine.Runtime.Tests.WatchProbeSecurityWiringTests</c>, which executes this same
    /// two-line composition against generated PEMs.
    /// </para>
    /// </remarks>
    internal static WatchSession<IKeptTopology> CreateSession(
        string filePath,
        StepKindRegistry registry,
        TextWriter output,
        string? appHostAssemblyName,
        ResolvedSecretLedger sessionSecretLedger,
        SecurityPathDisclosureLedger sessionPathLedger,
        Func<TopologyRequest, ISecurityConfigurationAccessor, CancellationToken, Task<IKeptTopology>>
            startTopologyAsync)
    {
        // The kept-topology isolation is rebuilt by the build seam each time a NEW topology is
        // built (it must bind to the new topology's connection string); held here so the run
        // seam can pass it to the engine.  Disposed alongside the topology it belongs to.
        IScenarioIsolation? isolation = null;

        return new WatchSession<IKeptTopology>(
            // Compile seam: re-read happens in OnChangeAsync (file content is passed in); here we
            // parse, run EVERY pre-topology gate, and compute the fingerprint that drives reuse.
            compile: content => Compile(content, filePath, registry, appHostAssemblyName, output),

            // Build seam: stand up a fresh topology for the planned scenario's request, and
            // (re)build the matching isolation for it.
            buildTopologyAsync: async (planned, ct) =>
            {
                var plan = (WatchIterationPlan)planned;
                var ast = plan.Ast;
                var suiteDirectory = Path.GetDirectoryName(filePath);

                // REQ-005/REQ-014: the SAME resolved client security configuration `vouchfx run`
                // hands the probe. Omitted here until #364's first defect, and the omission was
                // invisible: an optional parameter left off compiles and reads correctly, while
                // every secured suite became unrunnable under `--watch` — a `profile: tls` suite
                // failing PartialChain and a `profile: mtls` suite reporting "no
                // 'clientCert'/'clientKey' pair resolved" about files that exist and are valid.
                // Fail-closed, but blaming the author for the host's defect. SuiteTopology.StartAsync
                // now refuses to start a security-declaring suite with no accessor, so this cannot
                // recur silently — and, since it is handed to the starter below rather than built
                // inside it, a Docker-free test can now see that it arrives.
                //
                // Disposed in the finally below rather than by the topology: the accessor owns the
                // X509Certificate2 instances it loads and the topology does not own its lifetime —
                // matching ScenarioRunner exactly.
                //
                // The secret scope (client-key-password REQ-009) comes from ScenarioRunner's own
                // factory rather than a second spelling here, so `--watch` resolves
                // `clientKeyPassword` against exactly the sources `vouchfx run` does. Resolution
                // stays lazy: it happens inside the certificate load, which StartAsync reaches only
                // after the health gate.
                //
                // The SCOPE is per-REBUILD (it owns the resolvers, and this seam runs only when
                // the topology fingerprint changes); the LEDGER it records into is the SESSION's
                // (EDGE-007). That split is the same one ScenarioRunner makes per-scenario, for
                // the same reason: a passphrase resolved HERE must be scrubbable from text the
                // step path emits on a later save against this same kept topology — and, per the
                // capture-order note at the ledger's declaration, from the catch that receives
                // THIS seam's own failure.
                //
                // THE SCOPE OUTSIDE THE `try`, THE ACCESSOR INSIDE IT. `Build` can throw
                // (Path.GetFullPath on a malformed declared path), and constructed before the
                // `try` its failure skipped the `finally` below, leaking this scope's resolvers
                // and the Vault one's HttpClient. That matters more here than at either
                // `ScenarioRunner` site: THIS seam re-runs on every rebuild, so a suite whose
                // `Build` fails repeatedly leaks once per rebuild for as long as `--watch` is left
                // running. The scope's own construction allocates two objects and touches
                // nothing, so a failure there leaves nothing to dispose.
                var probeSecrets = ScenarioRunner.CreateSecretAccessorScope(sessionSecretLedger);
                ISecurityConfigurationAccessor probeSecurity =
                    NullSecurityConfigurationAccessor.Instance;
                try
                {
                    probeSecurity = SecurityConfigurationAccessor.Build(
                        ast, suiteDirectory, probeSecrets.Accessor, sessionPathLedger);

                    // ONE ARGUMENT LIST (#364). Both protocol target sets — REQ-005/REQ-011's
                    // Kafka-speaking set and #348's endpoint-consuming superset — were computed at
                    // this call site, from this `ast`, in a list maintained separately from the two
                    // in ScenarioRunner. Each of them was dropped from one of those three lists at
                    // some point. The plan's TopologyRequest is now the only list there is, and its
                    // two factories derive both sets from one input.
                    var topology = await startTopologyAsync(plan.Request, probeSecurity, ct)
                        .ConfigureAwait(false);
                    isolation = ScenarioRunner.BuildWatchIsolation(topology);
                    return topology;
                }
                finally
                {
                    (probeSecurity as IDisposable)?.Dispose();
                    probeSecrets.Dispose();
                }
            },

            // Run seam: run the latest-saved scenario against the kept topology.  `resetAndReseed`
            // is the reuse signal (S08-T10, B2): FALSE on the first run against a just-built+seeded
            // topology (no pre-reset, no re-seed — it already carries the fresh seed and no prior
            // writes, matching plain `vouchfx run`); TRUE on a reuse run, where the kept topology
            // holds the previous run's writes (reset them, then re-apply the seed).
            //
            // NOTHING IS COMPILED HERE ANY MORE (#370). The plan arrived already validated and
            // compiled, from before the reuse-vs-rebuild decision, so a save this seam receives is
            // one every pre-topology gate passed.
            runAgainstTopologyAsync: async (topology, planned, resetAndReseed, ct) =>
            {
                var plan = (WatchIterationPlan)planned;
                await ScenarioRunner.RunPlannedScenarioAgainstKeptTopologyAsync(
                    topology,
                    isolation ?? new NullScenarioIsolation(),
                    plan,
                    registry,
                    output,
                    resetAndReseed: resetAndReseed,
                    // EDGE-007: the step path records into — and scrubs against — the SAME ledger
                    // the probe above resolved into. Omitting this compiles (the parameter is
                    // optional) and leaves the engine building a ledger of its own per re-run.
                    sharedLedger: sessionSecretLedger,
                    // Issue #375's sibling of the line above, and omitted for the same cost: the
                    // parameter is optional, so leaving it out compiles and silently drops the
                    // path substitution from every event this seam emits.
                    sharedPathLedger: sessionPathLedger,
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

            // Issue #266, Item 4: this is the sink for WatchCompileResult's Error message (see
            // Compile below, "Parse / AST error: {ex.Message}") — the SAME AstBuilder-derived
            // author-content leak as RunCommand's parse-failure loop, just reached via the
            // watch-loop's re-run seam instead. Sanitising HERE, at the single sink, covers
            // this call today and any future WatchSession _report call without needing a
            // separate fix at each call site.
            //
            // EDGE-007: scrubbed through the session ledger first — see ScrubThenSanitise for why
            // that order, and not the other one, is the one that holds.
            //
            // A PRE-TOPOLOGY REFUSAL NEVER ARRIVES HERE (#370): the compile seam renders it — the
            // located diagnostic plus its event pair — and returns WatchCompileResult.Refused(),
            // whose Error is null, so WatchSession's guarded call prints nothing further. Only a
            // parse/AST failure, which has no events to render, still reaches this sink.
            report: line => output.WriteLine(
                ScrubThenSanitise(line, sessionSecretLedger, sessionPathLedger)));
    }

    /// <summary>
    /// The production topology starter: <c>TopologyRequest.StartAsync</c> and nothing else.
    /// </summary>
    /// <remarks>
    /// A named method rather than a lambda at the call site so its body is one statement a reader
    /// can check against the census that pins <c>SuiteTopology.StartAsync</c> to exactly one
    /// production call site — <c>Vouchfx.Engine.Runtime.Tests.SuiteProtocolTargetsTests
    /// .EverySuiteTopologyStartCallSite_PassesBothTargetSets</c>. That one call site is inside
    /// <c>TopologyRequest</c>, not here: this method calls <c>request.StartAsync</c>, which is a
    /// different symbol and does not move the census's count.
    /// </remarks>
    private static async Task<IKeptTopology> StartTopologyAsync(
        TopologyRequest request,
        ISecurityConfigurationAccessor securityConfiguration,
        CancellationToken cancellationToken)
        => await request.StartAsync(securityConfiguration, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reads the file from disk and processes one re-run through the session, catching any
    /// orchestration failure so a transient build error does not crash the loop.
    /// </summary>
    private static async Task RunOnceFromDiskAsync(
        WatchSession<IKeptTopology> session,
        string filePath,
        TextWriter output,
        // Non-nullable: the sole caller path always has the session's ledger in hand. Only
        // ProcessChangeGuardedAsync's parameter is nullable, and that is load-bearing rather than
        // defensive — it keeps the three pre-EDGE-007 three-argument tests compiling unchanged.
        ResolvedSecretLedger sessionSecretLedger,
        SecurityPathDisclosureLedger sessionPathLedger,
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
            //
            // Routed through the same helper as every other post-probe sink. It was raw — neither
            // scrubbed nor sanitised — and being raw is exactly why the sink census could not see
            // it: a gate that counts helper calls is blind to a site that calls no helper. Both
            // halves earn their place here. Sanitise: `filePath` is author/CLI-supplied and
            // `ex.Message` embeds it (issue #266, Item 4), and this is reached on EVERY save, not
            // just the first. Scrub: this fires after the topology is up, so the session ledger
            // may already hold a resolved passphrase, and defence-in-depth does not get to pick
            // which sink an unexpected value arrives at.
            await output.WriteLineAsync(
                ScrubThenSanitise(
                    $"--watch: could not read '{filePath}': {ex.Message}",
                    sessionSecretLedger,
                    sessionPathLedger))
                .ConfigureAwait(false);
            return;
        }

        await ProcessChangeGuardedAsync(
            ct => session.OnChangeAsync(content, ct),
            output,
            cancellationToken,
            sessionSecretLedger,
            sessionPathLedger).ConfigureAwait(false);
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
    /// <param name="sessionSecretLedger">
    /// The watch session's <see cref="ResolvedSecretLedger"/> (EDGE-007), which both messages
    /// below are scrubbed through before they are sanitised. <see langword="null"/> — the default
    /// — skips the scrub and leaves the pre-EDGE-007 behaviour exactly as it was.
    /// </param>
    /// <param name="sessionPathLedger">
    /// The watch session's <see cref="SecurityPathDisclosureLedger"/> (issue #375), applied to the
    /// same two messages between the value scrub and the sanitiser. <see langword="null"/> - the
    /// default - skips it, which is what the pre-#375 three-argument tests exercise.
    /// </param>
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
        CancellationToken cancellationToken,
        ResolvedSecretLedger? sessionSecretLedger = null,
        SecurityPathDisclosureLedger? sessionPathLedger = null)
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
            // Issue #266, Item 4: oex.Message reflects author environment.services/dependencies
            // config (e.g. an EnvironmentMapper diagnostic quoting a declared name) — sanitise.
            // EDGE-007: and scrub first — this is the sink a failed secured build reaches, and a
            // SecuredEndpointProbe failure folds a SecurityMaterialException's text into it.
            await output.WriteLineAsync(
                ScrubThenSanitise(
                    $"--watch: environment error during run: {oex.Message}",
                    sessionSecretLedger,
                    sessionPathLedger))
                .ConfigureAwait(false);
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
            // Issue #266, Item 4: ex.Message can carry author-declared config (e.g. the
            // ArgumentException from a malformed environment block) — sanitise before writing;
            // reachable on every save-triggered re-run.
            // EDGE-007: and scrub first. This catch takes whatever the platform or a provider
            // happens to throw, so it is the sink most likely to carry text nobody designed —
            // the "never any captured secret/token" claim above is a property of the message
            // SHAPE, which says nothing about what an arbitrary ex.Message interpolated.
            await output.WriteLineAsync(
                ScrubThenSanitise(
                    $"--watch: error during run ({ex.GetType().Name}): {ex.Message}",
                    sessionSecretLedger,
                    sessionPathLedger))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Redacts any secret value the watch session has resolved from <paramref name="text"/>, then
    /// renders what remains inert for a terminal — the one composition every post-probe sink in
    /// this class writes through (EDGE-007).
    /// </summary>
    /// <param name="text">The free-form diagnostic text about to be written.</param>
    /// <param name="ledger">
    /// The session ledger, or <see langword="null"/> to skip the scrub (pre-EDGE-007 behaviour,
    /// and what the tests that predate it exercise).
    /// </param>
    /// <param name="pathLedger">
    /// The session's security-path disclosure ledger (issue #375), or <see langword="null"/> to
    /// skip its substitution. Applied BETWEEN the value scrub and the sanitiser, for the reasons
    /// in the remarks below.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>BOTH NETS RUN BEFORE THE SANITISER, SECRETS FIRST, THEN PATHS.</strong> The
    /// secrets-before-paths order is argued at length on
    /// <c>ScenarioRunner.ScrubDiagnostic</c>; this seam follows that decision rather than
    /// restating it, and must not diverge from it.
    /// </para>
    /// <para>
    /// <strong>THE SCRUB-BEFORE-SANITISE ORDER IS LOAD-BEARING, AND IT IS SCRUB FIRST.</strong>
    /// <see cref="DisplaySanitiser"/> REWRITES text — control bytes and ANSI escape sequences are
    /// its whole subject — so a passphrase containing one no longer matches the ledger's recorded
    /// form once the sanitiser has been over it, and scrubbing second would leave the printable
    /// remainder on the terminal. Scrubbing first sees the value exactly as it was resolved,
    /// replaces it whole, and hands the sanitiser a redaction marker it passes through unchanged.
    /// (<c>ScenarioRunner</c>'s isolation-failure sink composes them in this same order, for this
    /// same reason.)
    /// </para>
    /// <para>
    /// <strong>The sanitiser is NOT redundant and is not being replaced.</strong> It is
    /// control-character/ANSI-aware only, by its own header — an ordinary printable passphrase
    /// passes through it unchanged, so it was never the guard against a secret. The ledger scrub
    /// is that guard; the sanitiser remains the guard against a terminal-corrupting byte. Each
    /// covers what the other cannot.
    /// </para>
    /// </remarks>
    internal static string? ScrubThenSanitise(
        string? text, ResolvedSecretLedger? ledger, SecurityPathDisclosureLedger? pathLedger)
    {
        // Null-in/null-out, matching BOTH components it composes — so it drops into any sink
        // that already tolerated the sanitiser's own nullable contract.
        var scrubbed = ledger is null ? text : ledger.Scrub(text);

        // Issue #375's net, and it belongs HERE rather than after the sanitiser for the same
        // reason the value scrub does: the sanitiser REWRITES text, so a resolved path carrying a
        // byte it neutralises would no longer match the recorded form once it had been over it.
        scrubbed = pathLedger is null ? scrubbed : pathLedger.Scrub(scrubbed);

        return DisplaySanitiser.SanitiseForDisplay(scrubbed);
    }

    /// <summary>
    /// Watches <paramref name="filePath"/> with a debounced <see cref="FileSystemWatcher"/>,
    /// re-running through the session on each coalesced save, until the token is cancelled.
    /// </summary>
    private static async Task WatchUntilCancelledAsync(
        WatchSession<IKeptTopology> session,
        string filePath,
        TextWriter output,
        // Non-nullable for the same reason as RunOnceFromDiskAsync's: it only ever forwards the
        // session's own ledger.
        ResolvedSecretLedger sessionSecretLedger,
        SecurityPathDisclosureLedger sessionPathLedger,
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

                await RunOnceFromDiskAsync(
                        session,
                        filePath,
                        output,
                        sessionSecretLedger,
                        sessionPathLedger,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            watcher.EnableRaisingEvents = false;
        }
    }

    /// <summary>
    /// Builds the AST for the current file contents, runs EVERY pre-topology gate, and computes the
    /// topology fingerprint that drives reuse — mapping a parse failure to a reported
    /// <see cref="WatchCompileResult"/> error and a gate refusal to an already-rendered
    /// <see cref="WatchCompileResult.Refused"/> (so the watch loop keeps running through an
    /// authoring slip either way).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>THIS IS #370'S FIX, AND ITS POSITION IS THE FIX.</strong> This seam used to be
    /// <c>YamlDocumentParser.Parse</c> + <c>AstBuilder.Build</c> and nothing else: schema validation
    /// and the provider-pipeline compile ran later, at the RUN seam, against a topology that was
    /// already up. So a schema-invalid save started containers; a both-families protocol conflict
    /// was reported by the security probe as "the broker did not answer a Kafka ApiVersions
    /// request", blaming the broker for an authoring fault; and <c>SecuredEndpointProbe</c>'s
    /// unrecognised-profile refusal — documented unreachable by author input, because
    /// <c>SecurityProfileWiringValidator</c> rejects an unregistered profile first — was reachable
    /// here alone. All three are gone because the gates run BEFORE
    /// <c>WatchSession.OnChangeAsync</c> can reach the build seam.
    /// </para>
    /// <para>
    /// The refusal is RENDERED HERE, once, and <see cref="WatchCompileResult.Refused"/> carries no
    /// message so the session's report sink prints nothing further. The alternative — returning the
    /// diagnostic as a bare <c>Error</c> — would drop the refusal's event pair, which is what a
    /// refused save has always emitted.
    /// </para>
    /// </remarks>
    private static WatchCompileResult Compile(
        string content,
        string filePath,
        StepKindRegistry registry,
        string? appHostAssemblyName,
        TextWriter output)
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

        var plan = WatchIterationPlan.Create(
            ast,
            content,
            ScenarioNameOf(ast, filePath),
            registry,
            appHostAssemblyName,
            Path.GetDirectoryName(filePath));

        if (plan.IsRefused)
        {
            ScenarioRunner.RenderWatchRefusal(plan, registry, output);
            return WatchCompileResult.Refused();
        }

        return WatchCompileResult.Success(plan.TopologyFingerprint, plan);
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
}
