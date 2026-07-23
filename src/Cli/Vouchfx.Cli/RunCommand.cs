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
    /// The <c>--events-stream</c> option (issue #258): incrementally APPEND the JSON Lines event
    /// stream to the given path as each scenario completes, so a concurrent reader can tail it.
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
    /// Scope (approved): SCENARIO-granular liveness only. A scenario's step / step-attempt
    /// records are reconstructed from <c>Vars</c> after the compiled delegate returns, so a
    /// flush lands a whole scenario's lines at once — a single suite's step lines still appear
    /// together, at that scenario's completion, not one-by-one as each step executes. Genuine
    /// per-step streaming is a separate, larger change (tracked as a follow-up) and is
    /// deliberately out of scope here.
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
            "Incrementally append the JSON Lines event stream to <path> as each scenario "
            + "completes (scenario-granular liveness), so a concurrent reader (e.g. `tail -f`) "
            + "sees lines as they land rather than only after the whole run finishes. Separate "
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
            "Render the terminal report as plain text — no ANSI colour and no per-verdict shape "
            + "glyph — for a screen-reader / CI-friendly view. Off by default; when off, colour + "
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
    /// The <c>--events-stream</c> path (issue #258): when non-<see langword="null"/>, the runner
    /// opens an <see cref="Vouchfx.Engine.Reporting.EventStreamAppender"/> over this path and
    /// appends — and flushes — each scenario's event lines to it as that scenario completes, so a
    /// concurrent reader can tail scenario-granular liveness rather than waiting for the whole run
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
        // NOTE (S09-T3 / S10 / #258 scope): --html / --junit / --events / --events-stream /
        // --no-decorations are NOT wired into watch mode.  Watch renders per re-run (not from one
        // suite-wide buffer), and threading the report / events paths through WatchRunner /
        // WatchSession / RunScenarioAgainstKeptTopologyAsync — plus deciding the
        // overwrite-on-every-save semantics — is meaningful complexity for an interactive loop
        // whose value is the terminal feedback. --events, --events-stream, and the
        // --no-decorations `decorate` flag all follow the SAME scope as --html / --junit:
        // deliberately left out rather than half-wired (the watch loop renders plain).
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

            // The suite-wide SEED base directory stays rooted at the FIRST scenario's own
            // directory (unchanged), matching WatchRunner's Path.GetDirectoryName(filePath)
            // convention. All scenarios in a sequential suite already share one `environment`
            // block (validated below in ScenarioRunner), and ScenarioRunner.RunSuiteAsync
            // builds exactly ONE shared topology from scenarios[0].Environment and applies its
            // ONE seed ONCE against ONE base directory — environment.seed is genuinely
            // single-rooted there, unlike a step's own file: reference above. The parallel path
            // (ParallelSuiteRunner) needs no such single root — each scenario owns its own
            // topology, so it is passed scenarioBaseDirectories for EVERYTHING (seed included).
            var suiteBaseDirectory = scenarioBaseDirectories.Count > 0
                ? scenarioBaseDirectories[0]
                : null;

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
                    seedBaseDirectory: suiteBaseDirectory,
                    seedBaseDirectories: scenarioBaseDirectories,
                    htmlReportPath: htmlReportPath,
                    junitReportPath: junitReportPath,
                    eventsReportPath: runnerEventsPath,
                    eventsStreamPath: eventsStreamPath,
                    decorate: decorate,
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
                    cancellationToken: runCancellationToken).ConfigureAwait(false);

            suiteVerdict = result.Verdict;
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

        // Fold the parse-failures into the suite verdict and map the result to a process exit
        // code — see ComputeExitCode for the issue #278 special case (an entirely-unparseable
        // set is unconditionally Inconclusive, never gated behind --fail-on-inconclusive).
        return ComputeExitCode(parsed.Count, failures.Count, suiteVerdict, failOnEnvironmentError, failOnInconclusive);
    }

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
    /// <strong>Entirely parse-failures</strong> (<paramref name="parsedCount"/> is 0 AND
    /// <paramref name="parseFailureCount"/> is greater than 0): nothing could be parsed or run
    /// at all — this is unconditionally <see cref="ExitCodes.Inconclusive"/> (4), REGARDLESS of
    /// <paramref name="failOnInconclusive"/>. A CI pipeline keying on <c>run</c>'s exit code
    /// must never see a fully-unparseable suite reported as a clean <see cref="ExitCodes.Success"/>
    /// — the same reasoning <c>validate</c> already applies unconditionally to an all-invalid
    /// set. This is deliberately DIFFERENT from a genuine execution-time
    /// <see cref="Verdict.Inconclusive"/> produced by a scenario that DID run (timeout /
    /// partition outlasted grace / upstream capture unmet) — THAT case stays opt-in-gated
    /// below, unchanged.
    /// </para>
    /// <para>
    /// <strong>Mixed or fully-parsed set</strong> (<paramref name="parsedCount"/> greater than
    /// 0): today's existing behaviour, unchanged — fold the parse-failures into
    /// <paramref name="suiteVerdict"/> via <see cref="AggregateVerdict"/>, then map through
    /// <see cref="ExitCodes.FromVerdict"/> (opt-in gated for EnvironmentError / Inconclusive).
    /// <paramref name="parseFailureCount"/> being 0 falls through this SAME branch —
    /// <see cref="AggregateVerdict"/> is then a no-op — so the no-parse-failure case is also
    /// unaffected.
    /// </para>
    /// </remarks>
    internal static int ComputeExitCode(
        int parsedCount,
        int parseFailureCount,
        Verdict suiteVerdict,
        bool failOnEnvironmentError,
        bool failOnInconclusive)
    {
        if (parsedCount == 0 && parseFailureCount > 0)
        {
            return ExitCodes.Inconclusive;
        }

        var aggregate = AggregateVerdict(suiteVerdict, parseFailureCount);
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
