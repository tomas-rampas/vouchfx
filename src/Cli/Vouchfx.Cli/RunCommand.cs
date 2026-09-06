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
using Vouchfx.Cli.Selection;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;

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
            "Discover *.e2e.yaml scenarios under <path> (a directory, or a single "
            + "*.e2e.yaml file) and run them end-to-end against an orchestrated topology.");

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
        var eventsStreamOption = BuildEventsStreamOption();
        var noDecorationsOption = BuildNoDecorationsOption();
        var noTelemetryOption = BuildNoTelemetryOption();
        var shutdownOnStdinEofOption = BuildShutdownOnStdinEofOption();
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
        command.Add(eventsStreamOption);
        command.Add(noDecorationsOption);
        command.Add(noTelemetryOption);
        command.Add(shutdownOnStdinEofOption);

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
            var eventsStreamPath = parseResult.GetValue(eventsStreamOption);
            var noDecorations = parseResult.GetValue(noDecorationsOption);
            var noTelemetry = parseResult.GetValue(noTelemetryOption);
            var shutdownOnStdinEof = parseResult.GetValue(shutdownOnStdinEofOption);

            // Accessibility (S10-G-03a): decorate the terminal report (ANSI colour + per-verdict
            // shape glyph) ONLY for an interactive TTY that has not opted out.  Plain text is the
            // safe default for piped / redirected / CI / test output and for the NO_COLOR
            // convention, and is what a screen reader wants.  The verdict TEXT tokens (the
            // WCAG-1.4.1 guarantee) are unconditional and unaffected — this toggles only the
            // optional colour + glyph layer.  Computed HERE (the CLI), not in the renderer: the
            // renderer stays a pure function of its inputs and never probes the environment.
            var decorate =
                !noDecorations
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
                && !Console.IsOutputRedirected;

            // Opt-in, privacy-first telemetry (S10-G-04): the production hook over the real
            // per-user consent store + local outbox sink.  --no-telemetry (and the
            // VOUCHFX_NO_TELEMETRY env var, read inside the hook) opt this run out.  Everything
            // the hook does is wrapped so it can NEVER affect the suite verdict or exit code.
            var telemetryHook = TelemetryRunHook.CreateDefault(noTelemetry, Console.Error);

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
                eventsStreamPath,
                decorate,
                Console.Out,
                telemetryHook,
                cancellationToken,
                shutdownOnStdinEof);
        });

        return command;
    }

    /// <summary>
    /// The <c>--no-telemetry</c> flag (S10-G-04): opt THIS run out of telemetry, even when
    /// telemetry is enabled.  Equivalent to setting <c>VOUCHFX_NO_TELEMETRY</c> for the run.
    /// </summary>
    /// <remarks>
    /// Telemetry is OFF by default and opt-in (via <c>vouchfx telemetry enable</c>); this flag
    /// is the per-invocation opt-out for an otherwise-enabled install.  The
    /// <c>VOUCHFX_NO_TELEMETRY</c> environment variable is the equivalent opt-out for CI /
    /// automation that cannot pass a flag (and doubles as the production-run exclusion).
    /// </remarks>
    internal static Option<bool> BuildNoTelemetryOption() => new("--no-telemetry")
    {
        Description =
            "Opt this run out of anonymous usage telemetry, even if telemetry is enabled. "
            + "Telemetry is OFF by default and opt-in (vouchfx telemetry enable); set "
            + "VOUCHFX_NO_TELEMETRY=1 for the same effect in CI / automation.",
    };

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
    /// <remarks>
    /// The budget figure is INTERPOLATED from <see cref="SystemProcessRunner.DefaultBudget"/>
    /// rather than restated, so moving the constant moves this text with it. It is spelled as the
    /// budget rather than as the true worst case: a call that spends the whole budget on its reads
    /// is still given a one-second floor to reap the child, so the ceiling is a second above the
    /// figure shown. "About" carries that second — quoting 121s would be precise about a number no
    /// operator can act on.
    /// </remarks>
    internal static Option<string?> BuildChangedSinceOption() => new("--changed-since")
    {
        Description =
            "Select only scenarios whose file changed since this git ref (committed diff vs "
            + "the ref plus the dirty working tree). Each git call is bounded by a budget of about "
            + FormattableString.Invariant($"{SystemProcessRunner.DefaultBudget.TotalSeconds:0.###}s")
            + ", and can be cancelled with Ctrl+C. Requires a git repository.",
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
            "Treat an Environment-error verdict as a CI failure (exit 3). Off by default - only "
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
            "Treat an Inconclusive verdict as a CI failure (exit 4). Off by default - only Fail "
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
            + "primitives: Fail maps to failure, Environment-error to error, and Inconclusive "
            + "to skipped. Parent "
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
            + "stream the terminal / HTML / JUnit reports are rendered from, verbatim - one JSON "
            + "object per line, UTF-8 without a BOM. Parent directories are created as needed; an "
            + "existing file is overwritten. Aliased as --json. Omit for no events file (the "
            + "default).",
    };

    /// <summary>
    /// The <c>--events-stream</c> option (issue #258): incrementally APPEND the JSON Lines event
    /// stream to the given path in real time as each step and attempt completes, so a concurrent reader can tail it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a SEPARATE, opt-in file from <c>--events</c> / <c>--json</c>: that archive is
    /// still written exactly once, at the very end of the run, byte-for-byte unchanged (the
    /// existing frozen behaviour every downstream consumer — e.g. the telemetry capture path —
    /// already relies on). <c>--events-stream</c> may be used together with or independently of
    /// <c>--events</c>; the two never share a file and never affect one another.
    /// </para>
    /// <para>
    /// Liveness (issue #262): step and step-attempt records are emitted in real time as each
    /// step / attempt completes — via a host-side sink, not reconstructed after the compiled
    /// delegate returns — so a suite's lines land one-by-one as they happen rather than all at
    /// once at scenario end. In parallel mode, lines from concurrently-running scenarios
    /// interleave by arrival and are disambiguated by their <c>runId</c> + <c>stepId</c>; the
    /// authoritative <c>--events</c> archive is unaffected.
    /// </para>
    /// <para>
    /// Absent ⇒ no incremental stream (today's behaviour unchanged). Not wired into
    /// <c>--watch</c>, for the SAME reason <c>--events</c> / <c>--html</c> / <c>--junit</c> are
    /// not (see the scope note where watch mode is dispatched below): watch renders per re-run
    /// from its own topology, not from one suite-wide buffer.
    /// </para>
    /// </remarks>
    internal static Option<string?> BuildEventsStreamOption() => new("--events-stream")
    {
        Description =
            "Incrementally append the JSON Lines event stream to <path> in real time as each step "
            + "and attempt completes (per-step liveness), so a concurrent reader (e.g. `tail -f`) "
            + "sees lines as they land rather than only after the run finishes. Separate "
            + "from --events / --json, which still writes once, at the end, byte-for-byte "
            + "unchanged; the two may be used together or independently. Not wired into --watch. "
            + "Omit for no incremental events stream (the default).",
    };

    /// <summary>
    /// The <c>--no-decorations</c> flag (S10-G-03a): render the terminal report as PLAIN text —
    /// no ANSI colour and no per-verdict shape glyph — for a screen-reader / CI-clean view.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default.  When OFF, the report is decorated (colour + glyph) ONLY when the output
    /// is an interactive TTY and the <c>NO_COLOR</c> convention is not set; piped / redirected /
    /// CI output is plain regardless.  Setting this flag forces plain text even on a TTY.
    /// </para>
    /// <para>
    /// Decorations are PURELY additive and accessibility-redundant: the four §12.1 verdicts are
    /// always distinguished by their TEXT token (<c>PASS</c> / <c>FAIL</c> / <c>ENV_ERROR</c> /
    /// <c>INCONCLUSIVE</c>) — the WCAG-1.4.1 guarantee — so turning decorations off loses no
    /// information, only the colour + glyph convenience.
    /// </para>
    /// </remarks>
    internal static Option<bool> BuildNoDecorationsOption() => new("--no-decorations")
    {
        Description =
            "Render the terminal report as plain text - no ANSI colour and no per-verdict shape "
            + "glyph - for a screen-reader / CI-friendly view. Off by default; when off, colour + "
            + "glyph are added only for an interactive TTY with NO_COLOR unset. The verdict text "
            + "tokens (PASS / FAIL / ENV_ERROR / INCONCLUSIVE) are always shown, so this loses no "
            + "information.",
    };

    /// <summary>
    /// The shared force-termination budget, in seconds, for BOTH termination paths: Program.cs's
    /// non-watch <c>InvocationConfiguration.ProcessTerminationTimeout</c> (Ctrl-C / SIGTERM) and
    /// <see cref="ShutdownBackstop"/> (stdin EOF, vouchfx-mcp#17). Kept as ONE constant so the two
    /// budgets can never drift apart.
    /// </summary>
    /// <remarks>
    /// 30s comfortably covers the PATHOLOGICAL sum of <c>HeadlessTopology.DisposeAsync</c>'s two
    /// sequential bounded waits (§4.5): its own 15s <c>StopAsync</c> CTS, PLUS the subsequent
    /// (unconditional, un-timeout-wrapped in that method) <c>_app.DisposeAsync()</c> call, which
    /// DCP's own internal dispose/cleanup path can itself take up to ~10s to complete — a worst
    /// case of ~25s. An earlier 20s value covered only the 15s <c>StopAsync</c> bound in isolation
    /// and left a narrow re-leak window against that combined ~25s worst case; 30s clears it with
    /// headroom while remaining FINITE, so a genuinely wedged run still eventually terminates.
    /// </remarks>
    internal const int TeardownBudgetSeconds = 30;

    /// <summary>
    /// The <c>--shutdown-on-stdin-eof</c> flag (vouchfx-mcp#17): opt in to a graceful-shutdown
    /// seam for a host process (e.g. an MCP server) that spawns this CLI with a redirected
    /// standard input and wants to request a clean stop simply by closing that pipe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// STRICTLY opt-in and OFF by default. When off (the default), <see cref="ExecuteAsync"/>
    /// never touches standard input at all — behaviour is byte-for-byte identical to today. This
    /// matters because stdin is frequently already at end-of-file under CI / non-interactive
    /// redirection (and is never closed at all in a plain interactive terminal run); a non-opt-in
    /// watch would self-cancel those runs.
    /// </para>
    /// <para>
    /// <strong>Operator warning:</strong> do NOT combine this flag with a non-interactive or
    /// already-closed/redirected-null stdin (e.g. <c>vouchfx run --shutdown-on-stdin-eof &lt;
    /// /dev/null</c>) unless a LIVE pipe a host process actually controls is wired up instead — an
    /// already-EOF stdin is observed on the very first read, so the run is cancelled IMMEDIATELY.
    /// </para>
    /// <para>
    /// When set, <see cref="ExecuteAsync"/> starts a background <see cref="StdinShutdownWatcher"/>
    /// over <see cref="Console.OpenStandardInput()"/>. On end-of-file it cancels a LINKED
    /// <see cref="CancellationTokenSource"/> that every downstream runner then uses instead of the
    /// raw System.CommandLine <see cref="CancellationToken"/> — the SAME cancellation-PROPAGATION
    /// path a Ctrl-C / SIGTERM takes (the engine's teardown discipline, §4.5). This is
    /// <strong>not</strong> full signal-path parity, though:
    /// <see cref="CancellationTokenSource.CreateLinkedTokenSource(CancellationToken)"/> only
    /// propagates cancellation DOWNSTREAM, so cancelling the linked source never touches the
    /// ORIGINAL System.CommandLine token — <c>InvocationConfiguration.ProcessTerminationTimeout</c>
    /// (see Program.cs), armed only by a REAL OS Ctrl-C/SIGTERM, is never engaged by this path. The
    /// EOF callback therefore ALSO arms a dedicated <see cref="ShutdownBackstop"/>: if the process
    /// is still alive <see cref="TeardownBudgetSeconds"/> seconds after EOF (something is wedged
    /// and ignoring cancellation), the backstop force-exits the process itself — see
    /// <see cref="ExecuteAsync"/>'s wiring for the exit-code rationale. An MCP host that redirects
    /// this process's standard input can therefore request a graceful stop simply by closing the
    /// child's stdin, backed by the engine's OWN bounded force-exit guarantee, independent of
    /// System.CommandLine's.
    /// </para>
    /// </remarks>
    internal static Option<bool> BuildShutdownOnStdinEofOption() => new("--shutdown-on-stdin-eof")
    {
        Description =
            "Opt in to graceful shutdown on stdin end-of-file: when a host process (e.g. an MCP "
            + "server) closes this process's redirected standard input, cancel the run and, if it "
            + "has not finished within the teardown budget, force-exit the process. Off by "
            + "default; never touches stdin unless set, so behaviour is unchanged for CI / "
            + "non-interactive runs where stdin is often already at EOF. WARNING: do not combine "
            + "with a non-interactive or already-closed/redirected-null stdin (e.g. `< /dev/null`) "
            + "unless a live pipe a host process controls is actually wired up, or the run will "
            + "cancel immediately.",
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
    /// Whether two option-supplied paths resolve to the SAME file, for the
    /// <c>--events-stream</c> / <c>--events</c> / <c>--html</c> / <c>--junit</c> collision guard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each path is normalised via <see cref="Path.GetFullPath(string)"/> before comparison, so
    /// e.g. a relative and an equivalent absolute path are recognised as the same file. Comparison
    /// then uses the platform-appropriate case sensitivity: <see cref="StringComparison.OrdinalIgnoreCase"/>
    /// on Windows (whose filesystem is case-insensitive by default), <see cref="StringComparison.Ordinal"/>
    /// elsewhere. This is a best-effort, no-filesystem-access check (no symlink / hard-link
    /// resolution) — adequate for catching the literal "typed the same path twice" mistake this
    /// guard exists for.
    /// </para>
    /// <para>
    /// <see langword="false"/> when either input is <see langword="null"/> or empty (nothing to
    /// collide with), AND <see langword="false"/> whenever <see cref="Path.GetFullPath(string)"/>
    /// throws for EITHER operand — a path malformed enough to make <c>GetFullPath</c> throw
    /// cannot be opened by <c>FileReportWriter</c> or <c>EventStreamAppender</c> EITHER, so there
    /// is no successful archive for this guard to protect: both writers will independently fail
    /// visibly, at write time, with an accurate "bad path" diagnostic. Treating an unnormalisable
    /// path as "not comparable" (rather than falling back to a raw-string compare) also avoids a
    /// false positive: two DIFFERENT malformed paths could otherwise coincide on the same raw
    /// text by construction, and — the sharper case — two IDENTICAL malformed paths must NOT be
    /// treated as a collision here, since neither one is a file this guard can meaningfully
    /// reason about; this guard must never itself throw or manufacture a spurious usage error.
    /// </para>
    /// </remarks>
    internal static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        var normalisedA = NormalisePathForComparison(a);
        var normalisedB = NormalisePathForComparison(b);
        if (normalisedA is null || normalisedB is null)
        {
            // Either path failed to normalise: not comparable, so NOT a collision (defer to the
            // existing write-time diagnostics). Deliberately NOT "both null ⇒ equal" — a failed
            // normalisation is "unknown", never a match.
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(normalisedA, normalisedB, comparison);
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to its full form for <see cref="PathsEqual"/>, returning
    /// <see langword="null"/> when <see cref="Path.GetFullPath(string)"/> itself rejects it — a
    /// malformed path is not this guard's concern (both the eventual writers fail visibly, at
    /// write time, with their own accurate diagnostic), so it must never be treated as equal to
    /// anything, including another equally malformed path.
    /// </summary>
    private static string? NormalisePathForComparison(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
        {
            return null;
        }
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
        Description = "Directory to search recursively for *.e2e.yaml scenarios, or a "
            + "single *.e2e.yaml file. Defaults to '.'.",
        DefaultValueFactory = _ => ".",
    };

    /// <summary>
    /// The taxonomy backstop for a <c>run</c> invocation (issue #413): delegates the whole of the
    /// run to <see cref="ExecuteCoreAsync"/> and maps any exception that would otherwise escape
    /// the run onto <see cref="ExitCodes.Inconclusive"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What the absence of this catch actually did was exit 1 — TestFailure — and that is
    /// worse than the "non-taxonomy crash code" an earlier draft of this remark claimed.</strong>
    /// <c>Program.cs</c> invokes through a bare <see cref="InvocationConfiguration"/>, whose
    /// <c>EnableDefaultExceptionHandler</c> defaults to <see langword="true"/> on the pinned
    /// System.CommandLine 2.0.0 GA, so an exception escaping the action is caught by the framework,
    /// printed, and turned into exit code <strong>1</strong>. The exit code was therefore inside the
    /// taxonomy and telling CI the wrong thing: that the SUITE observed a product defect. Measured,
    /// not reasoned about, by
    /// <c>SystemCommandLineExitCodeTests.EscapingException_UnderTheDefaultHandler_ExitsOne</c>,
    /// which drives the pinned framework directly. Issue #413 was raised on one such route (a
    /// provider whose <c>Bind</c> threw out of <c>ProviderPipeline.Compile</c>); that route is
    /// closed at its own throw site, and this frame exists because the NEXT one has not been found.
    /// </para>
    /// <para>
    /// <strong>What this frame covers is the <c>run</c> PATH's escapes, and that is narrower than
    /// "every unexpected engine throw now lands inside the taxonomy".</strong> An exception raised
    /// inside a <c>--parallel</c> SLOT does not reach here at all:
    /// <c>ParallelSuiteRunner</c>'s per-slot catch-all absorbs it first and classifies it
    /// <see cref="Verdict.EnvironmentError"/>, which exits 0 when nothing executed. That
    /// classification is correct for a genuine infrastructure fault and wrong for an engine defect,
    /// and the frame that makes it cannot tell them apart — tracked as issue #466, not fixed here.
    /// So the accurate claim is: escapes on the run path land on
    /// <see cref="ExitCodes.Inconclusive"/>, and a throwing provider <c>Bind</c> does so on BOTH
    /// paths because it is converted to a verdict before either catch sees it.
    /// </para>
    /// <para>
    /// <strong>Inconclusive (4), not TestFailure (1), and not EnvironmentError (3).</strong> An
    /// unexpected throw is not a product defect the suite observed, which is exactly what the
    /// framework's 1 asserted. Nor is it infrastructure: <see cref="Verdict.EnvironmentError"/>
    /// exits 0 by default for a run that executed nothing (#390), which would report a green build
    /// over an engine crash. What actually happened is that the engine could not reach a verdict,
    /// which is §12.1's Inconclusive, and it is the same code <c>ShutdownBackstop</c> chooses for
    /// the same event.
    /// </para>
    /// <para>
    /// <strong>It cannot override a code the run already chose.</strong> Every deliberate exit —
    /// the usage errors, the discovery catch, <see cref="ComputeExitCode"/>'s own answer — is a
    /// RETURN from <see cref="ExecuteCoreAsync"/> and never reaches this catch. Only a throw does.
    /// </para>
    /// <para>
    /// <strong>"The run", not "the process".</strong> This frame covers
    /// <see cref="ExecuteCoreAsync"/> and everything below it, which is the whole of a <c>run</c>
    /// invocation's work — but not the option/argument binding the parse action performs before
    /// calling it, and not <c>Program.cs</c>'s own parse and exit-code resolution around it. A
    /// throw from either of those still reaches System.CommandLine's default handler and its exit
    /// 1. Widening the backstop to cover them would mean catching in <c>Program.cs</c>, where a
    /// <c>validate</c> or <c>list</c> invocation would be caught by the same frame and mapped to a
    /// code from <c>run</c>'s taxonomy; that is a different change with a different scope.
    /// </para>
    /// <para>
    /// <strong>Cancellation is filtered, not blanket-re-thrown, and the filter is the whole of the
    /// correctness here.</strong> <see cref="TaskCanceledException"/> derives from
    /// <see cref="OperationCanceledException"/> and is what a TIMEOUT raises — an
    /// <c>HttpClient</c> hitting its 100-second default, a <c>CancelAfter</c> budget expiring. An
    /// unfiltered <c>catch (OperationCanceledException) { throw; }</c> therefore sent every such
    /// timeout back out to the framework's default handler and the exit-1 measured above: a
    /// transport hiccup reported as a product defect, from the frame written to prevent precisely
    /// that. The filter is <c>cancellationToken.IsCancellationRequested</c> — the token
    /// System.CommandLine cancels on Ctrl-C / SIGTERM — so only a genuine USER cancellation is
    /// re-thrown and keeps the path it has today; a timeout, or any other cancellation nobody
    /// asked for, is mapped like every other unexpected fault.
    /// </para>
    /// <para>
    /// <strong>The stdin-EOF path maps to 4 rather than re-throwing, and reading the LINKED source
    /// here would break that.</strong> <c>--shutdown-on-stdin-eof</c> cancels a linked source
    /// created inside <see cref="ExecuteCoreAsync"/> and never touches this method's parameter, so
    /// the filter above is <see langword="false"/> for an EOF-driven stop and the run maps to
    /// <see cref="ExitCodes.Inconclusive"/> — the SAME code <c>ShutdownBackstop</c> force-exits
    /// with for the same event. Hoisting the linked source so this frame could consult it would
    /// make EOF re-throw into the framework's 1 and split them; the parameter is the right token
    /// precisely BECAUSE it is not the one EOF cancels.
    /// <strong>The agreement claimed is for a cancellation that ESCAPES the run</strong>, and only
    /// that. Where the EOF cancellation is instead ABSORBED lower down — a runner that observes the
    /// token, unwinds, and hands back an ordinary <see cref="SuiteResult"/> — no exception reaches
    /// this frame and the exit code is whatever <see cref="ComputeExitCode"/> derives, which for a
    /// suite that executed something can be 0. That is pre-existing and out of scope here.
    /// </para>
    /// <para>
    /// <strong>No report artefacts are synthesised here, and that is a stated residual rather than
    /// an oversight.</strong> The artefacts are written by the runners, from the event buffer they
    /// own; this frame has no buffer, no run id and no scenario list, and a synthetic empty report
    /// written over a partially-written real one would be worse than none. The routes that CAN
    /// produce artefacts must therefore keep converting their faults into verdicts BEFORE they
    /// reach here — which is exactly what the <c>Bind</c> guard does for #413's own route.
    /// </para>
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
        string? eventsStreamPath,
        bool decorate,
        TextWriter output,
        TelemetryRunHook? telemetryHook,
        CancellationToken cancellationToken,
        bool shutdownOnStdinEof = false)
    {
        ArgumentNullException.ThrowIfNull(output);

        try
        {
            return await ExecuteCoreAsync(
                path,
                criteria,
                parallel,
                watch,
                failOnEnvironmentError,
                failOnInconclusive,
                htmlReportPath,
                junitReportPath,
                eventsReportPath,
                eventsStreamPath,
                decorate,
                output,
                telemetryHook,
                cancellationToken,
                shutdownOnStdinEof).ConfigureAwait(false);
        }
        // A GENUINE USER CANCELLATION, AND NOTHING ELSE, KEEPS THE PATH IT HAS. The filter is
        // load-bearing: TaskCanceledException IS an OperationCanceledException and is what a
        // timeout raises, so an unfiltered rethrow here sent every timeout to System.CommandLine's
        // default handler and its exit 1 — a transport hiccup reported as a product Fail. See this
        // method's own remarks for the measurement and for why the token consulted is the
        // PARAMETER rather than the stdin-EOF path's linked source.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // THE DIAGNOSTIC IS BEST-EFFORT; THE EXIT CODE IS NOT. `output` is a caller-supplied
            // sink and is itself a candidate for the fault that got here (a full disk, a closed
            // pipe, a redirected stream the host tore down). Letting the report of the failure fail
            // the process would hand back the framework's exit 1 — the exact answer this catch
            // exists to replace — so the write is guarded and the code is returned either way.
            try
            {
                // Issue #266, Item 4: an engine exception's message can carry author-supplied YAML
                // spliced in by whatever composed it, so the whole composed line is sanitised
                // before it reaches the terminal / CI log — the same treatment every other
                // author-influenced diagnostic on this path gets.
                await output.WriteLineAsync(
                    DisplaySanitiser.SanitiseForDisplay(
                        $"vouchfx run: the engine failed unexpectedly and could not reach a verdict "
                        + $"({ex.GetType().FullName}: {ex.Message}).  This is an engine or provider "
                        + "defect, not a suite failure - please report it with the suite that "
                        + "triggered it.  Reported as Inconclusive (section 12.1)."))
                    .ConfigureAwait(false);
            }
            catch (Exception writeFailure) when (
                writeFailure is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
            {
                // Nothing left to report it to. Swallowed deliberately.
                //
                // THE FILTER IS THE SAME ONE THE OUTER CATCH USES, AND FOR THE SAME REASON — it was
                // written here as the narrower `is not OperationCanceledException`, which had the
                // defect one level down: a sink whose write TIMES OUT raises TaskCanceledException,
                // an OperationCanceledException, so the diagnostic's own failure escaped this frame
                // and took the run to System.CommandLine's exit 1 — with the taxonomy code already
                // computed and one `return` away. Caught by this file's own timeout row, which
                // drives a sink that throws on EVERY write including this one. Only a cancellation
                // the USER actually requested is left to propagate, matching the outer catch.
            }

            return ExitCodes.Inconclusive;
        }
    }

    /// <summary>
    /// The Docker-free orchestration of a <c>run</c> invocation: discovers scenarios, runs
    /// the parsed ones, folds parse-failures in as Inconclusive, and returns the exit code.
    /// </summary>
    /// <param name="path">The discovery root (already defaulted to <c>"."</c> by the parser).</param>
    /// <param name="output">The writer that receives diagnostics + the rendered report.</param>
    /// <param name="telemetryHook">
    /// The opt-in telemetry hook (S10-G-04), or <see langword="null"/> to disable all telemetry
    /// for this invocation (the unit-test default).  When non-<see langword="null"/>, the hook
    /// shows the one-time first-run notice (when consent is Undecided), captures the buffered
    /// event stream via the runner's <c>--events</c> seam, and — when consent is Enabled and the
    /// run is not opted out — builds the allowlisted telemetry event and appends it to the local
    /// outbox.  EVERY hook call is wrapped so telemetry can never change the suite verdict or exit
    /// code.
    /// </param>
    /// <param name="cancellationToken">
    /// The System.CommandLine action's own token (cancelled on Ctrl-C / SIGTERM, bounded by
    /// <c>InvocationConfiguration.ProcessTerminationTimeout</c> — see Program.cs). Propagated to
    /// the runner UNCHANGED unless <paramref name="shutdownOnStdinEof"/> is set — see its remarks.
    /// </param>
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
    /// <param name="eventsStreamPath">
    /// The <c>--events-stream</c> path (issue #258; per-step liveness #262): when
    /// non-<see langword="null"/>, the runner opens an
    /// <see cref="Vouchfx.Engine.Reporting.EventStreamAppender"/> over this path and
    /// appends — and flushes — each step and step-attempt event to it as it completes, so a
    /// concurrent reader can tail per-step liveness in real time rather than waiting for the run
    /// to finish. Entirely SEPARATE from <paramref name="eventsReportPath"/>, which is still
    /// written once, at the end, byte-for-byte unchanged. <see langword="null"/> ⇒ no incremental
    /// stream. Like <c>--html</c> / <c>--junit</c> / <c>--events</c> it is NOT wired into watch
    /// mode (same scope note as those flags below), and it is deliberately kept OUT of the
    /// telemetry capture path (which reads back <paramref name="eventsReportPath"/> only).
    /// </param>
    /// <param name="decorate">
    /// The computed terminal-decoration flag (S10-G-03a): when <see langword="true"/>, the
    /// non-watch runners decorate each step-verdict line with an ANSI colour + a per-verdict shape
    /// glyph; when <see langword="false"/> the report is plain text.  Computed by the caller from
    /// <c>--no-decorations</c> + the <c>NO_COLOR</c> convention + <see cref="Console.IsOutputRedirected"/>
    /// so the renderer stays a pure function of its inputs.  The verdict TEXT tokens (the WCAG-1.4.1
    /// guarantee) are unconditional and independent of this flag.  Like <c>--html</c> / <c>--junit</c>
    /// / <c>--events</c> it is NOT threaded into watch mode (same scope note as those flags below):
    /// the watch loop renders plain.
    /// </param>
    /// <param name="shutdownOnStdinEof">
    /// The <c>--shutdown-on-stdin-eof</c> flag (vouchfx-mcp#17). STRICTLY opt-in; OFF by default.
    /// <see langword="false"/> (the default): <paramref name="cancellationToken"/> flows through
    /// to every downstream runner completely UNCHANGED, and standard input is never opened,
    /// read, or otherwise touched — this path is byte-for-byte identical to before this flag
    /// existed.
    /// <see langword="true"/>: a background <see cref="StdinShutdownWatcher"/> is started over
    /// <see cref="Console.OpenStandardInput()"/>. On end-of-file (or a read error) it cancels a
    /// LINKED <see cref="CancellationTokenSource"/> derived from <paramref name="cancellationToken"/>
    /// — every downstream runner below is given THAT linked token instead, unwinding the SAME way
    /// a Ctrl-C / SIGTERM would (cancellation PROPAGATION parity). It is deliberately
    /// <strong>not</strong> full termination-enforcement parity, though: linking only propagates
    /// downstream, so the ORIGINAL System.CommandLine token is never cancelled and
    /// <c>ProcessTerminationTimeout</c> (Program.cs) — armed only by a real OS signal — never
    /// fires for this path. The EOF callback therefore ALSO arms a dedicated
    /// <see cref="ShutdownBackstop"/> with its OWN wall-clock budget, independent of the run's own
    /// (possibly-ignored) cancellation: if the process has not exited within
    /// <see cref="TeardownBudgetSeconds"/> seconds of EOF, the backstop force-exits it — see the
    /// wiring below for the exit-code choice. This gives a stdin-EOF shutdown request the same
    /// BOUNDED guarantee a signal gets, via its own dedicated mechanism rather than shared
    /// System.CommandLine plumbing. Lets a host process (e.g. an MCP server) that spawns this CLI
    /// with a redirected stdin request a graceful stop simply by closing that pipe.
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
    /// usage-error short-circuit) are each tested in isolation. The
    /// <c>--shutdown-on-stdin-eof</c> plumbing itself (the linked-token wiring, and
    /// <see cref="StdinShutdownWatcher"/> in isolation) is ALSO Docker-free and IS covered
    /// directly.
    /// </remarks>
    private static async Task<int> ExecuteCoreAsync(
        string path,
        SelectionCriteria criteria,
        int? parallel,
        bool watch,
        bool failOnEnvironmentError,
        bool failOnInconclusive,
        string? htmlReportPath,
        string? junitReportPath,
        string? eventsReportPath,
        string? eventsStreamPath,
        bool decorate,
        TextWriter output,
        TelemetryRunHook? telemetryHook,
        CancellationToken cancellationToken,
        bool shutdownOnStdinEof = false)
    {
        // First-run notice (S10-G-04): when telemetry consent is Undecided and the notice has not
        // been shown, print a one-time stderr notice (what's collected, that NOTHING is sent until
        // the user opts in, how to opt out).  The notice collects and sends nothing.  Wrapped
        // inside the hook so it can never break the run.
        telemetryHook?.MaybeShowFirstRunNotice(Console.Error);

        // Opt-in graceful-shutdown seam (vouchfx-mcp#17). STRICTLY additive: when the flag is
        // absent (the default), `linkedShutdownSource`, `shutdownBackstop` and
        // `stdinShutdownWatcher` are ALL null — nothing is constructed, standard input is never
        // opened or read, and `runCancellationToken` is the EXACT SAME token as
        // `cancellationToken` — byte-for-byte identical to today.
        //
        // When set, closing stdin fires the watcher's EOF (or read-error) callback, which does
        // TWO things:
        //   1. Cancels `linkedShutdownSource` — every downstream runner below observes this via
        //      `runCancellationToken` and unwinds the SAME way a Ctrl-C / SIGTERM would
        //      (HeadlessTopology.DisposeAsync's bounded StopAsync teardown, §4.5). This is
        //      cancellation-PROPAGATION parity only.
        //   2. Arms `shutdownBackstop` — a WALL-CLOCK, budget-bound force-exit timer.
        // Security-review finding (MAJOR-1): step 1 alone is NOT signal-path parity for
        // TERMINATION ENFORCEMENT. CancellationTokenSource.CreateLinkedTokenSource only
        // propagates cancellation DOWNSTREAM — cancelling `linkedShutdownSource` never touches
        // the ORIGINAL `cancellationToken`, so System.CommandLine's own
        // InvocationConfiguration.ProcessTerminationTimeout watchdog (Program.cs — armed only by
        // a REAL OS Ctrl-C/SIGTERM acting on THAT original token) is NEVER engaged by a
        // stdin-EOF-triggered stop. Without step 2, a run wedged somewhere that does not observe
        // cancellation promptly (a step/provider await, not just teardown) would hang forever
        // once stdin closes. `shutdownBackstop` closes that gap: if the process is still alive
        // TeardownBudgetSeconds after EOF, it force-exits via Environment.Exit — see the
        // exit-code rationale below.
        //
        // Declared in THIS order — linkedShutdownSource, shutdownBackstop, stdinShutdownWatcher —
        // so `await using` disposes in REVERSE: the WATCHER first (so no MORE EOF callbacks can
        // fire), `shutdownBackstop` SECOND (cancelling its timer if teardown already completed —
        // this is what guarantees a graceful EOF shutdown that finishes within budget never
        // force-exits), and the linked CTS LAST. `ShutdownBackstop.Arm`/`DisposeAsync` are
        // internally lock-serialised (see its remarks), so a race between an in-flight EOF
        // callback and this method's own normal-completion teardown can never leave the timer
        // either double-armed or armed against an already-disposed CancellationTokenSource.
        using var linkedShutdownSource = shutdownOnStdinEof
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        var runCancellationToken = linkedShutdownSource?.Token ?? cancellationToken;

        // Exit-code choice for a forced backstop exit: Inconclusive (4), not TestFailure (1) and
        // not EnvironmentError (3). The run was cancelled via a deliberate, legitimate
        // graceful-stop request (stdin EOF) — that is the engine failing to reach a verdict in
        // time (§12.1's Inconclusive: "timeout / … the engine could not determine correctness"),
        // NOT a certain product defect the suite observed, so this must never exit TestFailure.
        // It is arguably closer to infrastructure trouble (something wedged), but Inconclusive is
        // the SAFER default per §12.1 ("only Fail breaks CI by default") — cancellation is not
        // proof of either a product Fail or an environment fault, and a forced-exit stop must
        // never risk being conflated with either.
        await using var shutdownBackstop = shutdownOnStdinEof
            ? new ShutdownBackstop(
                TimeSpan.FromSeconds(TeardownBudgetSeconds),
                () => Environment.Exit(ExitCodes.Inconclusive))
            : null;

        await using var stdinShutdownWatcher = shutdownOnStdinEof
            ? StdinShutdownWatcher.Start(Console.OpenStandardInput(), () =>
              {
                  linkedShutdownSource!.Cancel();
                  shutdownBackstop!.Arm();
              })
            : null;

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

        // --events-stream must not collide with any END-OF-RUN file output (--events / --html /
        // --junit): EventStreamAppender holds its path open with FileShare.Read for the WHOLE
        // run, so FileReportWriter's later FileShare.None open on the SAME path would hit a
        // sharing violation — that archive would then be silently never written (only a
        // diagnostic, no verdict change), quietly breaking the documented "--events-stream may
        // be used together with --events" guarantee. Rejected here, at parse time, before
        // discovery/Docker/anything else runs — a usage error (exit 2), not a silent no-op.
        // Collisions AMONG --events / --html / --junit themselves are pre-existing and OUT OF
        // SCOPE (not this guard's concern). PathsEqual only fires when BOTH sides normalise
        // successfully to the SAME file — a malformed path (on either side, including two
        // IDENTICAL malformed paths) is deliberately treated as "not comparable", never as a
        // collision, and is left to the existing write-time diagnostics instead (see
        // PathsEqual's remarks).
        if (eventsStreamPath is not null
            && (PathsEqual(eventsStreamPath, eventsReportPath)
                || PathsEqual(eventsStreamPath, htmlReportPath)
                || PathsEqual(eventsStreamPath, junitReportPath)))
        {
            await output.WriteLineAsync(
                "--events-stream must not point at the same file as --events/--html/--junit.")
                .ConfigureAwait(false);
            return ExitCodes.UsageError;
        }

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
        // `catch (Exception ex)`: a genuinely unexpected fault must still propagate.
        catch (Exception ex) when (ex is DirectoryNotFoundException
            or ScenarioDiscoveryException
            or UnauthorizedAccessException
            or IOException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
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
        // scenarios by tag/owner/path/change-set (BP §16). Every way a --changed-since
        // change-set can fail to be computed — no repo, git missing, bad ref, a git call that
        // outlasts its process budget, a failed output capture — surfaces as a
        // ChangeSetException, which the catch below maps to a usage error (exit 2), not a crash.
        //
        // ISSUE #411'S RECOVERED METADATA IS SCOPED OUT OF THE WATCH PATH, and the argument is
        // `!watch` rather than a second selection call so there is still exactly one. Selection
        // runs here, BEFORE the watch branch below, and `WatchRunner.RunAsync` refuses any
        // selection whose count is not 1 — so a broken sibling carrying the filter's own tag,
        // which the recovery newly matches, took a filtered watch from 1 to 2 and turned a run
        // into exit 2. Watch builds no `UnbuiltDocument` at all (it returns before the split), so
        // the recovery has nothing to serve on this path and a regression is all it could
        // contribute. The UNFILTERED watch case is not touched by either the flag or the recovery:
        // a parse-failure is included there because no metadata filter is active, so a two-file
        // directory still resolves to 2 and still exits 2, exactly as it did before #411.
        IReadOnlyList<DiscoveredScenario> selected;
        try
        {
            selected = SelectScenarios(
                discovered, criteria, path, matchRecoveredMetadata: !watch, runCancellationToken);
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
        // re-using the kept topology while every input it was built from is unchanged.  Watching is
        // inherently single-file, so the selection must resolve to exactly one scenario; a
        // directory matching many files (or none that parses) is a usage error here.
        //
        // NOTE (S09-T3 / S10 / #258 scope): --html / --junit / --events / --events-stream /
        // --no-decorations are NOT wired into watch mode.  Watch renders per re-run (not from one
        // suite-wide buffer), and threading the report / events paths through WatchRunner /
        // WatchSession / RunPlannedScenarioAgainstKeptTopologyAsync — plus deciding the
        // overwrite-on-every-save semantics — is meaningful complexity for an interactive loop
        // whose value is the terminal feedback. --events, --events-stream, and the
        // --no-decorations `decorate` flag all follow the SAME scope as --html / --junit:
        // deliberately left out rather than half-wired (the watch loop renders plain).
        //
        // ISSUE #411'S CARVE-OUT IS ABSENT HERE TOO, and for the same reason as the report paths:
        // this returns BEFORE the parsed/failures split below, so no `UnbuiltDocument` is ever
        // built on this path and a `security` block in an unbuildable file contributes nothing to
        // a watch session. That is a documented divergence rather than a hole in the guarantee —
        // watch never calls `ExitCodes.FromVerdict`, so no verdict of any kind becomes an exit code
        // here and REQ-018 has nothing to say about it. (`WatchRunner.RunAsync` RETURNS
        // `ExitCodes.UsageError` or `ExitCodes.Success` and it is this method's return value; what
        // it never does is derive one from a verdict. Saying watch "derives no exit code at all"
        // would be false, and the false form is what let the selection change above regress a
        // filtered watch to exit 2 unnoticed — so the enumeration is kept accurate rather than
        // dropped. Since issue #413 those two are no longer the whole set of codes a watch
        // INVOCATION can produce: a throw escaping `WatchRunner.RunAsync` — which
        // `ProcessChangeGuardedAsync` catches per change, but which the surrounding session
        // plumbing does not cover in full — reaches this method's own backstop and becomes
        // `ExitCodes.Inconclusive`. That is still not a verdict-derived code, which is the claim
        // this paragraph makes.) Issue #412 already tracks the watch path's divergence from `run`.
        if (watch)
        {
            return await WatchRunner.RunAsync(discovered, registry, output, runCancellationToken)
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
        // Issue #266, Item 4: failure.ParseError embeds raw author YAML content verbatim
        // (AstBuilder's unknown-step-type / duplicate-step-id messages splice the offending
        // type/id straight from the document) — this is the MOST reachable human-terminal
        // surface for hostile suite content (a plain `vouchfx run` on a malformed suite), so
        // the whole composed line is sanitised before it ever reaches the terminal/CI log.
        // AbsolutePath is filesystem-derived (lower risk) but sanitised too for consistency —
        // sanitising clean text is a no-op.
        foreach (var failure in failures)
        {
            await output.WriteLineAsync(
                DisplaySanitiser.SanitiseForDisplay(
                    $"{failure.AbsolutePath}: {failure.ParseError} (Inconclusive)"))
                .ConfigureAwait(false);
        }

        // Telemetry capture path (S10-G-04): when telemetry will emit, ask the runner to write its
        // buffered event stream (the SAME verbatim v1 stream the renderers consume) so the hook can
        // read it back and derive the allowlisted telemetry event.  When the user passed --events,
        // that path is reused (no extra write); otherwise a private temp file is used.  When
        // telemetry will NOT emit, the runner's events path is the user's own (or null) — no
        // telemetry-driven write.  Resolving this here keeps every report path identical to before
        // for a non-telemetry run.
        var (runnerEventsPath, isTempEventsFile) = telemetryHook is not null
            ? telemetryHook.ResolveEventsCapturePath(eventsReportPath)
            : (eventsReportPath, false);

        Verdict suiteVerdict = Verdict.Pass;
        SecurityAssurance? securityAssurance = null;

        // #369. Captured beside the assurance and for the same reason: the SuiteResult is
        // scoped to the block below, while the exit-code decision is made after it. True until
        // a run says otherwise, so a path producing no SuiteResult is unaffected.
        var executedAnyScenario = true;
        if (parsed.Count > 0)
        {
            var asts = parsed.Select(p => p.Ast!).ToList();
            var names = parsed.Select(ScenarioName).ToList();
            var yamlTexts = parsed.Select(p => p.YamlText).ToList();

            // Per-scenario base directories (issue #268): each scenario's OWN directory. This
            // is what resolves a step's relative `file:` reference (e.g. script.csharp) — and
            // hashes it for that scenario's reproducibility-envelope script-file digest — at
            // COMPILE time, per scenario. Each scenario compiles independently
            // (ProviderPipeline.Compile runs once per scenario), so there is no reason a
            // non-first scenario's relative reference should resolve against a DIFFERENT
            // scenario's folder.
            var scenarioBaseDirectories = parsed
                .Select(p => Path.GetDirectoryName(p.AbsolutePath))
                .ToList();

            // The suite-wide SEED base directory stays rooted at the FIRST DISCOVERED scenario's
            // own directory (unchanged), matching WatchRunner's Path.GetDirectoryName(filePath)
            // convention. All scenarios in a sequential suite that pass schema validation share one
            // `environment` block (enforced below in ScenarioRunner), and
            // ScenarioRunner.RunSuiteAsync builds exactly ONE shared topology from that block and
            // applies its ONE seed ONCE against ONE base directory — environment.seed is genuinely
            // single-rooted there, unlike a step's own file: reference above.
            //
            // THE ENVIRONMENT IS NOT NECESSARILY scenarios[0]'s SINCE #451, and this value does not
            // follow it. The topology is built from the first SCHEMA-VALID scenario, while this
            // stays index 0 — the same scenario in every suite whose first document validates. When
            // they differ AND the two scenarios live in different directories AND the environment
            // declares a seed, ScenarioRunner REFUSES the suite rather than seeding from the wrong
            // folder (see its seed-root guard); so this line's "single-rooted" claim is enforced
            // rather than assumed. The parallel path
            // (ParallelSuiteRunner) needs no such single root — each scenario owns its own
            // topology, so it is passed scenarioBaseDirectories for EVERYTHING (seed included).
            var suiteBaseDirectory = scenarioBaseDirectories.Count > 0
                ? scenarioBaseDirectories[0]
                : null;

            // appHostAssemblyName = THIS executable's name ("vouchfx"): the Aspire host that
            // carries the embedded DCP metadata (Aspire.AppHost.Sdk + IsAspireHost). Passing
            // it explicitly avoids the GetEntryAssembly fallback (CLAUDE.md §"Aspire").
            var appHostAssemblyName = Assembly.GetExecutingAssembly().GetName().Name;

            // Issue #411: the documents that PARSED and were then refused by AstBuilder.Build.
            // They are not scenarios and never will be — they stay in `failures` and still fold in
            // as Inconclusive — but they BOUND, so what they declared is known and is handed to the
            // runner along with the text they were bound from.
            //
            // THE CARVE-OUT IS NOT DECIDED HERE, and that is the point of passing documents rather
            // than a conclusion. This site enumerates nothing, validates nothing, compares nothing
            // and answers no security question: the runner folds these into the SAME
            // SecuredTargets.Enumerate walk that fills `Declared` and applies the SAME schema door
            // its own scenarios pass through, so one predicate on one record still decides. A
            // second decision site is exactly what issue #401 existed to remove.
            //
            // The filter is the CLASS TEST: RecoveredDocument is non-null for this failure class
            // and null for the other three, by construction. (It is NOT chosen over a
            // "does it have an environment" filter for any behavioural reason — measured, the two
            // predicates agree on every reachable input, because YamlDocumentParser.ParseEnvironment
            // returns a non-null EnvironmentSpec for any `environment:` mapping whatever its
            // `security` node is. What the TEXT carried alongside is load-bearing — the schema door
            // in Assure reads it, and a `security:` node the schema rejects binds no
            // EnvironmentSpec member at all — but that is about what is passed, not about which
            // predicate selects it.)
            //
            // The other three discovery failure classes contribute nothing — that residual is
            // #411's own amended acceptance rather than an oversight; see
            // DiscoveredScenario.RecoveredDocument.
            var unbuiltDocuments = failures
                .Where(failure => failure.RecoveredDocument is not null)
                .Select(failure => new UnbuiltDocument(failure.YamlText, failure.RecoveredDocument!))
                .ToList();

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
                    seedBaseDirectory: suiteBaseDirectory,
                    seedBaseDirectories: scenarioBaseDirectories,
                    htmlReportPath: htmlReportPath,
                    junitReportPath: junitReportPath,
                    eventsReportPath: runnerEventsPath,
                    eventsStreamPath: eventsStreamPath,
                    decorate: decorate,
                    unbuiltDocuments: unbuiltDocuments,
                    cancellationToken: runCancellationToken).ConfigureAwait(false)
                : await ScenarioRunner.RunSuiteAsync(
                    asts,
                    names,
                    yamlTexts,
                    ProviderRegistryFactory.CoreProviderAssemblies(),
                    appHostAssemblyName,
                    output,
                    seedBaseDirectory: suiteBaseDirectory,
                    scenarioBaseDirectories: scenarioBaseDirectories,
                    htmlReportPath: htmlReportPath,
                    junitReportPath: junitReportPath,
                    eventsReportPath: runnerEventsPath,
                    eventsStreamPath: eventsStreamPath,
                    decorate: decorate,
                    unbuiltDocuments: unbuiltDocuments,
                    cancellationToken: runCancellationToken).ConfigureAwait(false);

            suiteVerdict = result.Verdict;

            // REQ-018: the one cause of a non-Fail verdict that breaks CI without an opt-in flag.
            // Read straight off the runner's own result rather than re-derived from the verdict or
            // sniffed out of the event stream — the whole point of the carve-out is that the
            // verdict is UNCHANGED and cannot distinguish this case.
            securityAssurance = result.Assurance;
            executedAnyScenario = result.ExecutedAnyScenario;
        }

        // Emit telemetry from the SAME buffered event stream the renderers consumed (S10-G-04).
        // The hook re-reads runnerEventsPath, builds the allowlisted TelemetryEvent, appends it to
        // the local outbox, and deletes the temp file when it owns it.  EmitAsync swallows every
        // exception (it is a no-op when telemetry is not emitting, when no path was captured, or on
        // any error), so telemetry can NEVER affect the verdict or the exit code below.
        if (telemetryHook is not null)
        {
            await telemetryHook.EmitAsync(runnerEventsPath, isTempEventsFile, runCancellationToken)
                .ConfigureAwait(false);
        }

        // THE EXIT CODE MUST NEVER BE THE ONLY EVIDENCE, and this is the ONE site that says so.
        //
        // It used to be two, one per schema door, each computing for itself whether the document
        // declared security. Both are gone: this reads the same assurance the exit code reads, so
        // a non-zero exit and its explanation cannot come apart — and it now covers every door
        // that raises, not only the schema one.
        //
        // WHICH SHAPES REACH IT IS A PROPERTY, NOT A LIST. An authoring refusal that left some
        // declared target unconfirmed, or a refusal located in the declaration itself, reaches
        // this line; which door it came from is not consulted, because `Unconfirmed` is derived
        // once from the assurance rather than decided per door — so the set grows with the doors
        // and needs no maintenance here. A list was written here first and was short the day it was written:
        // it named the step-level secret fault, the unresolvable `script.csharp file:`, the
        // `${conn:typo}` and the protocol conflict, and omitted the shared-`environment`
        // divergence guard, which prints this notice and exits 3 (measured, real CLI). Read the
        // property; the secured rows of SecurityAssuranceMatrixTests pin the shapes measured so far.
        //
        // Suppressed for a failed PROBE: that path already reports a measured security failure in
        // its own words, and a generic line after it would add nothing.
        if (securityAssurance is { Unconfirmed: true, Refusal: not SecurityAbortKind.ProbeUnconfirmed })
        {
            await output.WriteLineAsync(SecurityUnconfirmableNotice).ConfigureAwait(false);
        }

        // Fold the parse-failures into the suite verdict and map the result to a process exit
        // code — see ComputeExitCode for the issue #278 special case (an entirely-unparseable
        // set is unconditionally Inconclusive, never gated behind --fail-on-inconclusive).
        return ComputeExitCode(
            parsed.Count, failures.Count, suiteVerdict, failOnEnvironmentError, failOnInconclusive,
            securityAssurance,
            // #369: false only when the runner returned through its without-topology completion
            // path, so no container started and no step ran.
            executedAnyScenario: executedAnyScenario);
    }

    /// <summary>
    /// The line printed when a suite that declares <c>security</c> reaches the end of its run with
    /// that declaration unconfirmed — the reason it exits non-zero (REQ-018).
    /// </summary>
    /// <remarks>
    /// <para>
    /// An exit code is not evidence on this surface: several distinct doors all produce 4, and an
    /// entirely-unparseable set produces 4 through issue #278's rule, so a non-zero exit with no
    /// accompanying reason leaves an author guessing which rule fired.
    /// </para>
    /// <para>
    /// <strong>EVERY CLAUSE MUST BE TRUE ON EVERY PATH THAT REACHES THE PRINT, and the mechanism
    /// clause this line used to open with was not.</strong> It said the suite "was refused before
    /// any container started". Measured, with no flags and exit 3, the engine's own diagnostic
    /// printed immediately above it read <c>Current State: Running</c> with a <c>Start Time</c> —
    /// <c>EnvironmentMapper.Map</c>'s refusal arrives inside <c>StartAsync</c>, and under
    /// <c>--parallel</c> a sibling slot's containers can be up and past their health gate. The
    /// clause is gone rather than qualified: this site holds an assurance record, and that record
    /// knows what was DECLARED and what was CONFIRMED — it does not know what any container did.
    /// Stating the property the record can actually vouch for is both true everywhere and the
    /// thing an author needs to read.
    /// </para>
    /// <para>
    /// <strong>ONE line, at the one site that reads the assurance — and its wording RETRACTS a
    /// narrower one.</strong> Two schema-door copies used to say "the document was rejected before
    /// its security declaration could be validated at all", and that narrowness was deliberate: the
    /// broad statement described a rule the engine did not implement, because two LATER doors (the
    /// provider pipeline and the step secret pass) also refused a secured suite before any container
    /// started and exited 0. Both of those now exit non-zero, so the narrow statement would be false
    /// wherever this line now fires. The retracted clauses are recorded here rather than deleted,
    /// because a reader who remembers the old wording needs to know it was overturned rather than
    /// reworded.
    /// </para>
    /// <para>
    /// The out-of-block clause the old wording carried conditionally ("even though nothing reported
    /// above lies inside that block") is gone with them: it distinguished two schema-door cases, and
    /// this line is no longer a schema-door line.
    /// </para>
    /// <para>
    /// <strong>THE CLOSING PROMISE IS RETRACTED TOO, and it was a promise rather than a
    /// property.</strong> It read "Fix what is reported above and the suite will run its security
    /// checks normally" — a claim that ONE fix suffices. Measured false: a secured suite carrying
    /// both a schema error at <c>/steps/0</c> and a missing <c>clientCert</c> reaches the schema
    /// door first, which <c>continue</c>s before the merged authoring door runs at all, so only the
    /// schema error prints. The author fixes exactly what was reported and the next run exits 4
    /// again, this time on the cert. This site sits behind a CHAIN of doors, not the last one, and
    /// each door reports only the faults it reached. The replacement states that property and
    /// promises no outcome — "need not be the last fix" is true whether or not another door is
    /// waiting, which is the only shape that survives the rule above.
    /// </para>
    /// <para>
    /// <strong>Reach, measured:</strong> this line goes to stdout only. It is absent from
    /// <c>--junit</c> and <c>--events</c> on both run paths — consistent with the diagnostics it
    /// accompanies, which those artefacts also omit. A CI job reading only machine-readable
    /// artefacts still sees a bare non-zero exit; that gap is filed separately.
    /// </para>
    /// </remarks>
    internal const string SecurityUnconfirmableNotice =
        "This suite declares a 'security' block that this run could not confirm, so it exits "
        + "non-zero whatever the fault reported above was: a run that cannot confirm a declared "
        + "security assertion cannot vouch for it. Each door reports only the faults it reached, so "
        + "what is reported above need not be the last fix before a run can confirm this suite's "
        + "security block.";

    /// <summary>
    /// Maps the suite's outcome to a process exit code (§12.1), applying the issue #278
    /// special case: when NOTHING could be parsed — the discovered/selected set is entirely
    /// parse-failures — the run is unconditionally <see cref="Verdict.Inconclusive"/>
    /// (<see cref="ExitCodes.Inconclusive"/>, 4), the SAME exit code <c>validate</c>
    /// unconditionally returns for an all-invalid set (<see cref="ValidateCommand.Execute"/>).
    /// </summary>
    /// <param name="parsedCount">
    /// The number of scenarios that parsed and were handed to the runner. Zero means
    /// <paramref name="suiteVerdict"/> is still its untouched initial value (the runner never
    /// ran — see <see cref="ExecuteAsync"/>'s <c>if (parsed.Count &gt; 0)</c> guard).
    /// </param>
    /// <param name="parseFailureCount">The number of scenarios that failed to parse.</param>
    /// <param name="suiteVerdict">
    /// The aggregate verdict from the parsed scenarios that DID run (still
    /// <see cref="Verdict.Pass"/> when <paramref name="parsedCount"/> is zero — nothing ran,
    /// nothing failed).
    /// </param>
    /// <param name="failOnEnvironmentError">
    /// Passed through to <see cref="ExitCodes.FromVerdict"/> for a mixed or fully-parsed set.
    /// </param>
    /// <param name="failOnInconclusive">
    /// Passed through to <see cref="ExitCodes.FromVerdict"/> for a mixed or fully-parsed set.
    /// </param>
    /// <returns>The process exit code (see <see cref="ExitCodes"/>).</returns>
    /// <remarks>
    /// <para>
    /// <strong>Any parse failure</strong> (<paramref name="parseFailureCount"/> greater than
    /// 0): the run contains at least one document the engine could not read, and that is never
    /// reported as a clean <see cref="ExitCodes.Success"/> — a CI pipeline keying on <c>run</c>'s
    /// exit code must never see an unread file reported as clean. Where the verdict would
    /// otherwise map to Success it becomes <see cref="ExitCodes.Inconclusive"/> (4), REGARDLESS
    /// of <paramref name="failOnInconclusive"/>.
    /// </para>
    /// <para>
    /// This subsumes #278's entirely-unparseable rule rather than sitting beside it: that rule
    /// tested <paramref name="parsedCount"/> being 0, so one working file beside a malformed one
    /// returned the malformed one to the opt-in-gated path and the run exited 0 — the same fault
    /// and the same verdict as the all-failure case, decided by something unrelated to the fault.
    /// It also closes #425, without any security-specific exit policy: a malformed document that
    /// declared mTLS now exits non-zero because it was unreadable.
    /// </para>
    /// <para>
    /// It is deliberately DIFFERENT from a genuine execution-time
    /// <see cref="Verdict.Inconclusive"/> produced by a scenario that DID run (timeout /
    /// partition outlasted grace / upstream capture unmet) — THAT case stays opt-in-gated,
    /// unchanged. A file that could not be read is a deterministic authoring fault, not an
    /// undetermined outcome.
    /// </para>
    /// <para>
    /// <strong>Every other code is unchanged.</strong> A <see cref="Verdict.Fail"/> outranks a
    /// parse failure by precedence and still exits 1; an <see cref="Verdict.EnvironmentError"/>
    /// still exits by its own gate; and <paramref name="parseFailureCount"/> being 0 leaves the
    /// whole path untouched.
    /// </para>
    /// </remarks>
    internal static int ComputeExitCode(
        int parsedCount,
        int parseFailureCount,
        Verdict suiteVerdict,
        bool failOnEnvironmentError,
        bool failOnInconclusive,
        SecurityAssurance? securityAssurance = null,
        bool executedAnyScenario = true)
    {
        var aggregate = AggregateVerdict(suiteVerdict, parseFailureCount);
        var code = ExitCodes.FromVerdict(
            aggregate, failOnEnvironmentError, failOnInconclusive, securityAssurance);

        // A document the engine could not read is NEVER a clean Success (#425).
        //
        // This is one rule where there were two, and the second was #278's: an entirely
        // unparseable set returned ExitCodes.Inconclusive from its own early branch, on the
        // reasoning quoted in this method's remarks — a CI pipeline keying on `run`'s exit code
        // must never see an unparseable suite reported as clean. That reasoning never depended on
        // whether a SIBLING happened to parse; the file was unread either way. But the branch it
        // lived in tested `parsedCount == 0`, so adding one working file beside a malformed one
        // folded the malformed one into the ordinary opt-in-gated Inconclusive path and the run
        // exited 0. Same fault, same verdict, two exit codes, and the deciding factor was
        // unrelated to the fault.
        //
        // Stating it once subsumes #278 rather than competing with it: with parsedCount == 0 the
        // aggregate is Inconclusive, FromVerdict maps that to Success when ungated, and this
        // returns Inconclusive — #278's own answer, reached by the general rule.
        //
        // THE PROPERTY THIS GUARD ENFORCES IS "A PARSE FAILURE NEVER EXITS 0", NOT "A PARSE
        // FAILURE EXITS 4". The narrower reading is what the retracted trailing clause here ("the
        // same 4, still regardless of failOnInconclusive") invited: it was true in its own scope
        // — with parsedCount == 0 the aggregate can only be Inconclusive, so all four
        // gate/assurance combinations do land on 4 — and false the moment it is read as a
        // statement about parse failures generally, which is how it was in fact read. The guard
        // is conditioned on `code == ExitCodes.Success`, so it never overrides a code some other
        // rule already chose: with one parsed sibling that Fails the run still exits 1, and with
        // one whose topology fails under --fail-on-env-error it still exits 3.
        //
        // It deliberately does NOT touch a genuine execution-time Inconclusive from a scenario
        // that DID run (timeout / partition outlasted grace / upstream capture unmet). Those stay
        // opt-in-gated, which is the §12.1 distinction this method's remarks already draw: a file
        // that could not be read is an authoring fault, deterministic and reproducible, not an
        // undetermined outcome.
        //
        // Every other code stands as-is: a Fail outranks a parse failure by precedence
        // (ScenarioRunner.VerdictPrecedence: Fail 2 > Inconclusive 1) and still exits 1, and an
        // EnvironmentError still exits by its own gate. Only Success is unreachable here.
        //
        // This ALSO closes #425 without a security-specific exit policy. A malformed document
        // declaring mTLS now exits non-zero because it was unreadable, not because of anything
        // it declared — so no raw-YAML scan for a `security:` key is needed (DiscoveredScenario.
        // RecoveredDocument refuses one, twice, with reasons) and SecurityAssurance is untouched.
        if (parseFailureCount > 0 && code == ExitCodes.Success)
        {
            return ExitCodes.Inconclusive;
        }

        // A run in which NOTHING EXECUTED is never a clean Success either (#369).
        //
        // The rule above closed "did the YAML parse". This closes the category the design never
        // named: a suite that parsed FINE and was then refused before any topology was built — a
        // schema rejection, a secret-reference failure, a malformed `env:`, the both-families
        // protocol conflict. Every one starts no container and runs no step, and every one exited
        // 0 by default. As #369 puts it: the distinction the code drew was "did the YAML parse",
        // but the distinction its own remarks argued for was "did anything execute".
        //
        // SCOPED TO Inconclusive, AND THAT SCOPE IS THE WHOLE CARE OF THIS CHANGE. A topology
        // that fails to START also executes nothing and reaches the same completion path since
        // #407 — but it carries EnvironmentError, which keeps its own `--fail-on-env-error` gate
        // and is untouched here. Widening this to every verdict would silently close #390, which
        // is deliberately open precisely because it would redden every suite whose UNRELATED
        // container was slow to come up. An authoring fault the engine refused is not the same
        // event as an environment that did not come up, and this line is where that stays true.
        //
        // A scenario that DID run and could not conclude — timeout, partition outlasted grace,
        // upstream capture unmet — still exits 0 by default, because it executed.
        if (!executedAnyScenario
            && aggregate == Verdict.Inconclusive
            && code == ExitCodes.Success)
        {
            return ExitCodes.Inconclusive;
        }

        return code;
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
    /// <param name="matchRecoveredMetadata">
    /// Forwarded verbatim to <see cref="ScenarioSelector.Apply"/>: <see langword="false"/> keeps
    /// issue #411's recovered metadata out of the match, which is what <c>--watch</c> passes.
    /// </param>
    /// <returns>The selected subset, in discovery order.</returns>
    /// <param name="cancellationToken">
    /// The run's cancellation token, forwarded to the git shell-out. Only the
    /// <c>--changed-since</c> arm observes it — the filtering below is a pure in-memory pass over
    /// an already-built change-set, so <see cref="ScenarioSelector.Apply"/> neither takes it nor
    /// needs it.
    /// </param>
    /// <exception cref="ChangeSetException">
    /// Thrown when <c>--changed-since</c> is set but the change-set cannot be computed.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="cancellationToken"/> is signalled during the git shell-out.
    /// Deliberately NOT a <see cref="ChangeSetException"/>: the caller maps that to a usage error,
    /// and a Ctrl+C is not one.
    /// </exception>
    /// <remarks>
    /// Exposed as <see langword="internal"/> so the no-docker test can assert that an empty
    /// criteria selects everything and that the change-set is only built on demand.
    /// </remarks>
    internal static IReadOnlyList<DiscoveredScenario> SelectScenarios(
        IReadOnlyList<DiscoveredScenario> discovered,
        SelectionCriteria criteria,
        string discoveryRoot,
        bool matchRecoveredMetadata = true,
        CancellationToken cancellationToken = default)
    {
        IChangeSet changeSet = NullChangeSet.Instance;
        if (criteria.ChangedSinceRef is { } changedSinceRef)
        {
            var workingDirectory = ResolveWorkingDirectory(discoveryRoot);
            changeSet = new GitChangeSet(
                changedSinceRef, workingDirectory, SystemProcessRunner.Instance, cancellationToken);
        }

        return ScenarioSelector.Apply(discovered, criteria, changeSet, matchRecoveredMetadata);
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
