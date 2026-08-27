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

        // The kept-topology isolation is rebuilt by the build seam each time a NEW topology is
        // built (it must bind to the new topology's connection string); held here so the run
        // seam can pass it to the engine.  Disposed alongside the topology it belongs to.
        IScenarioIsolation? isolation = null;

        // ── The watch SESSION's resolved-secret ledger (client-key-password EDGE-007) ──
        //
        // ONE ledger for the whole session, and SESSION scope is the load-bearing word. The
        // topology is KEPT across saves, so the probe below resolves `clientKeyPassword` ONCE per
        // topology while the step path runs on every save after it.
        //
        // BE PRECISE ABOUT THE BUILD SEAM: it is per-REBUILD, not per-save.
        // WatchSession.OnChangeAsync invokes it only when the environment hash CHANGES (or on the
        // first run) — a steps-only edit re-uses the topology and never reaches it. So a
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

        await using var session = new WatchSession<SuiteTopology>(
            // Compile seam: re-read happens in OnChangeAsync (file content is passed in); here we
            // validate + build the AST and compute the environment hash that drives reuse.
            compile: content => Compile(content, filePath, registry),

            // Build seam: stand up a fresh topology for the compiled scenario's environment, and
            // (re)build the matching isolation for it.
            buildTopologyAsync: async (compiled, ct) =>
            {
                var ast = ((CompiledScenario)compiled).Ast;
                var suiteDirectory = Path.GetDirectoryName(filePath);

                // REQ-005/REQ-014: the SAME resolved client security configuration `vouchfx run`
                // hands the probe. Omitted here until now, and the omission was invisible: an
                // optional parameter left off compiles and reads correctly, while every secured
                // suite became unrunnable under `--watch` — a `profile: tls` suite failing
                // PartialChain and a `profile: mtls` suite reporting "no 'clientCert'/'clientKey'
                // pair resolved" about files that exist and are valid. Fail-closed, but blaming the
                // author for the host's defect. SuiteTopology.StartAsync now refuses to start a
                // security-declaring suite with no accessor, so this cannot recur silently.
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
                // the environment hash changes); the LEDGER it records into is the SESSION's
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
                // `ScenarioRunner` site: THIS seam re-runs on every file save, so a suite whose
                // `Build` fails repeatedly leaks once per save for as long as `--watch` is left
                // running. The scope's own construction allocates two objects and touches
                // nothing, so a failure there leaves nothing to dispose.
                var probeSecrets = ScenarioRunner.CreateSecretAccessorScope(sessionSecretLedger);
                ISecurityConfigurationAccessor probeSecurity =
                    NullSecurityConfigurationAccessor.Instance;
                try
                {
                    probeSecurity = SecurityConfigurationAccessor.Build(
                        ast, suiteDirectory, probeSecrets.Accessor);

                    var topology = await SuiteTopology.StartAsync(
                        ast.Environment,
                        appHostAssemblyName,
                        startupTimeout: TimeSpan.FromSeconds(120),
                        seedBaseDirectory: suiteDirectory,
                        securityConfiguration: probeSecurity,
                        kafkaSpeakingTargets: SuiteProtocolTargets.KafkaSpeaking(ast),

                        // #348: the superset, from the same `ast`. Threaded HERE and not only in
                        // ScenarioRunner because `--watch` builds its own topology through this
                        // seam; omitting it would leave every service permissively unrefused under
                        // `--watch` while `run` refused, which is exactly the kind of divergence
                        // between the two paths this file's own history is full of.
                        endpointConsumingTargets: SuiteProtocolTargets.EndpointConsuming(ast),

                        // RESIDUAL, stated so it is not rediscovered as a regression: the rebuild
                        // trigger is the ENVIRONMENT hash alone, so a save that adds an http.rest
                        // step targeting an existing endpoint-less worker leaves the hash
                        // unchanged, reuses this topology, and never re-runs the refusal — that
                        // session sees #348's UriFormatException instead of the diagnostic. Plain
                        // `run` refuses correctly, and saving any `environment` change rebuilds.
                        // Widening the trigger to the steps is a --watch design change, not this
                        // fix.
                        cancellationToken: ct).ConfigureAwait(false);
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
                    // EDGE-007: the step path records into — and scrubs against — the SAME ledger
                    // the probe above resolved into. Omitting this compiles (the parameter is
                    // optional) and leaves the engine building a ledger of its own per re-run.
                    sharedLedger: sessionSecretLedger,
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
            // COVERAGE, PLAINLY: this lambda is the ONE sink pinned by text alone. Its body is
            // executed under test through ScrubThenSanitise, which the two catch sinks drive on a
            // real emission path; what a test cannot reach is the lambda's own construction,
            // because it happens inside RunAsync, past the Docker line. Extracting a factory to
            // make one line executable would move the sink's definition away from the constructor
            // argument that consumes it and add a production method whose only caller is a test —
            // a worse trade than naming the limit here and gating it with the census.
            report: line => output.WriteLine(ScrubThenSanitise(line, sessionSecretLedger)));

        // Issue #266, Item 4: `filePath` is author/CLI-supplied and reaches a terminal verbatim
        // here. Sanitised, not scrubbed: this line is written BEFORE the first run, so no probe
        // has resolved anything and there is nothing in the ledger a scrub could match. It is the
        // banner's only untrusted component.
        await output.WriteLineAsync(
            DisplaySanitiser.SanitiseForDisplay(
                $"Watching '{filePath}'.  Saving re-runs the suite (topology re-used while the "
                + "environment is unchanged).  Press Ctrl-C to stop.")).ConfigureAwait(false);

        // ── Initial run ───────────────────────────────────────────────────────
        await RunOnceFromDiskAsync(session, filePath, output, sessionSecretLedger, cancellationToken)
            .ConfigureAwait(false);

        // ── Watch loop ──────────────────────────────────────────────────────────
        await WatchUntilCancelledAsync(
                session, filePath, output, sessionSecretLedger, cancellationToken)
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
        // Non-nullable: the sole caller path always has the session's ledger in hand. Only
        // ProcessChangeGuardedAsync's parameter is nullable, and that is load-bearing rather than
        // defensive — it keeps the three pre-EDGE-007 three-argument tests compiling unchanged.
        ResolvedSecretLedger sessionSecretLedger,
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
                    $"--watch: could not read '{filePath}': {ex.Message}", sessionSecretLedger))
                .ConfigureAwait(false);
            return;
        }

        await ProcessChangeGuardedAsync(
            ct => session.OnChangeAsync(content, ct),
            output,
            cancellationToken,
            sessionSecretLedger).ConfigureAwait(false);
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
        ResolvedSecretLedger? sessionSecretLedger = null)
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
                ScrubThenSanitise($"--watch: environment error during run: {oex.Message}", sessionSecretLedger))
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
                    sessionSecretLedger))
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
    /// <remarks>
    /// <para>
    /// <strong>THE ORDER IS LOAD-BEARING, AND IT IS SCRUB FIRST.</strong>
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
    internal static string? ScrubThenSanitise(string? text, ResolvedSecretLedger? ledger)
    {
        // Null-in/null-out, matching BOTH components it composes — so it drops into any sink
        // that already tolerated the sanitiser's own nullable contract.
        var scrubbed = ledger is null ? text : ledger.Scrub(text);
        return DisplaySanitiser.SanitiseForDisplay(scrubbed);
    }

    /// <summary>
    /// Watches <paramref name="filePath"/> with a debounced <see cref="FileSystemWatcher"/>,
    /// re-running through the session on each coalesced save, until the token is cancelled.
    /// </summary>
    private static async Task WatchUntilCancelledAsync(
        WatchSession<SuiteTopology> session,
        string filePath,
        TextWriter output,
        // Non-nullable for the same reason as RunOnceFromDiskAsync's: it only ever forwards the
        // session's own ledger.
        ResolvedSecretLedger sessionSecretLedger,
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
                        session, filePath, output, sessionSecretLedger, cancellationToken)
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
