// Vouchfx.Engine.Runtime — ScenarioRunner (Sprint 3 integration spine; updated S04-A-02).
//
// Wires all five layers into a single end-to-end pipeline:
//   1. Validate YAML against the composed JSON Schema (early-return on invalid input).
//   2. Parse YAML → E2eDocument; build E2eDocument → ScenarioAst.
//   3. Reflect-dispatch Bind / Validate / Emit for every step.
//   4. Assemble fragments → AssembledScript.
//   5. Start Aspire topology (build-once invariant, §4).
//   6. Stage service base URLs into ScriptGlobalVariables.Vars.
//   7. Compile-once (with full TPA reference list) + RunIsolatedAsync.
//   8. Read StepOutcome values from Vars; emit events; aggregate verdict.
//   9. Render event buffer via TerminalRenderer.
//
// Hard invariants preserved:
//   • CSharpScript.EvaluateAsync is NEVER called (§5 memory model).
//   • OrchestrationException maps to EnvironmentError, never to Fail (§12.1).
//   • Schema-invalid input maps to Inconclusive, never to Fail (the test never ran).
//   • No static handles bridge the ALC boundary; all state flows through ScriptGlobalVariables.
//
// S04-A-02 additions:
//   • RunScenarioAgainstTopologyAsync — private helper with the per-scenario execution body.
//     RunAsync delegates to it so all behaviour is preserved byte-for-byte.
//   • RunSuiteAsync — builds the topology ONCE, iterates scenarios, calls
//     ScenarioIsolationFactory.Create's result (RespawnRelationalIsolation /
//     CompositeScenarioIsolation / NullScenarioIsolation) between each.
//   • SuiteResult — aggregate record for RunSuiteAsync callers.
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Abstractions.Reproducibility;
using Vouchfx.Engine.Abstractions.Retry;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Secrets.Vault;
using Vouchfx.Engine.Abstractions.Traces;
using Vouchfx.Engine.Abstractions.Webhooks;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.HostResources;
using Vouchfx.Engine.Reporting;
using Vouchfx.Engine.Runtime.Secrets;
using Vouchfx.Engine.Runtime.Serialisation;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Runtime;

// ---------------------------------------------------------------------------
// SuiteResult
// ---------------------------------------------------------------------------

/// <summary>
/// The aggregate result of a multi-scenario suite run via
/// <see cref="ScenarioRunner.RunSuiteAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Verdict"/> is the suite-level aggregate, computed from all
/// per-scenario verdicts using the standard precedence rule
/// (<c>EnvironmentError &gt; Fail &gt; Inconclusive &gt; Pass</c>).
/// </para>
/// <para>
/// <see cref="ScenarioVerdicts"/> provides the per-scenario breakdown,
/// keyed by scenario name (or <c>"scenario-{n}"</c> when a name is not
/// available from the AST metadata).
/// </para>
/// </remarks>
/// <param name="Verdict">
/// Suite-level aggregate verdict (highest precedence across all scenarios).
/// </param>
/// <param name="ScenarioVerdicts">
/// Per-scenario verdicts in execution order.
/// </param>
public sealed record SuiteResult(
    Verdict Verdict,
    IReadOnlyList<(string ScenarioName, Verdict Verdict)> ScenarioVerdicts);

// ---------------------------------------------------------------------------
// ScenarioRunner
// ---------------------------------------------------------------------------

/// <summary>
/// Executes the full vouchfx end-to-end pipeline for a single scenario or a
/// multi-scenario suite.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ScenarioRunner"/> is the integration spine introduced in Sprint 3
/// and extended in Sprint 4 (S04-A-02).
/// It is deliberately provider-agnostic: the caller supplies the provider
/// assemblies to scan via the <c>providerAssemblies</c> parameter, so the runner
/// does not take a compile-time dependency on any concrete provider.
/// </para>
/// <para>
/// The runner emits a structured JSON Lines event buffer using the
/// <c>EventStreamJson.ToLine</c> helpers and renders it via
/// <see cref="TerminalRenderer"/> at the end of the run.
/// </para>
/// <para>
/// <strong>Verdict precedence (§12.1):</strong>
/// <c>EnvironmentError &gt; Fail &gt; Inconclusive &gt; Pass</c>.
/// Only <c>Fail</c> breaks CI by default.
/// </para>
/// <para>
/// <strong>Engine-owned RETRY (Sprint 6):</strong> a step declaring
/// <c>verifyMode: RETRY</c> is now compiled (its <see cref="Verdict"/> threads
/// through <c>StepCompilePlan.Retry</c> into the <c>CsxAssembler</c> polling
/// loop) and executed like any other step.  Each poll emits one
/// <c>step-attempt</c> event so the backoff timeline is renderable offline
/// (§14).  A RETRY step that never satisfies its assertion within the polling
/// window aggregates as <see cref="Verdict.Inconclusive"/> (not
/// <see cref="Verdict.Fail"/>), because the RETRY runner writes
/// <c>Inconclusive</c> as its final outcome on timeout.
/// </para>
/// <para>
/// <strong>Not yet implemented (future sprints):</strong>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>continueOnFailure</c> abort semantics — the field is parsed but
///       not yet enforced.
///     </description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public static class ScenarioRunner
{
    // Fixed suite namespace injected into every ICompileContext during emit.
    // internal (not private, #260): ScenarioValidator's topology-free compile-only
    // pipeline reuses this SAME constant so a validated scenario's namespace can never
    // drift from what a real run would use.
    internal const string SuiteNamespace = "VouchfxGenerated";

    // Compiled-once regex that matches {identifier} placeholder tokens (S04-G-01).
    // Identical pattern to Substitute_Helpers inside the CSX — used here for
    // compile-time provenance derivation only; never used to read runtime values.
    private static readonly Regex s_placeholderRegex =
        new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    // Secret-reference sources the engine can resolve (§17, S05-B-01 / S05-B-02 / S08-B-01).
    // MVP sources: 'env' (S05) + 'vault' (S08).  Both the pre-compile validation pass
    // (this field) and the runtime accessor (the per-scenario catalog) derive from the
    // SAME BuildSecretResolvers() factory, so they can never disagree about which
    // sources are available.
    //
    // Static-init safety: BuildSecretResolvers() must NOT read the environment or open a
    // connection at construction — it only NAMES the sources here.  The Vault connection
    // is resolved lazily, at step-execution time, by EnvironmentConfiguredVaultKvClient
    // (so ${secret:vault/...} validates at compile time even when VAULT_ADDR/VAULT_TOKEN
    // are not set at validation time; a missing config surfaces as an EnvironmentError
    // only if a step actually resolves a vault secret).
    private static readonly string[] s_knownSecretSources =
        BuildSecretResolvers().Select(r => r.Source).ToArray();

    /// <summary>
    /// Builds the run's secret resolvers (§17).  Single source of truth shared by the
    /// pre-compile validation pass (<see cref="s_knownSecretSources"/>) and the runtime
    /// <see cref="SecretSourceCatalog"/> built per scenario, so the known-source set is
    /// guaranteed consistent.
    /// </summary>
    /// <remarks>
    /// Constructs the resolvers WITHOUT touching the environment or opening any
    /// connection — the Vault connection is created lazily on first resolve by
    /// <see cref="EnvironmentConfiguredVaultKvClient"/>.  The returned array may contain
    /// <see cref="IDisposable"/> resolvers (the Vault client owns an
    /// <see cref="System.Net.Http.HttpClient"/>); the per-scenario caller disposes them.
    /// </remarks>
    private static ISecretResolver[] BuildSecretResolvers()
        => new ISecretResolver[]
        {
            new EnvironmentSecretResolver(),
            new VaultSecretResolver(new EnvironmentConfiguredVaultKvClient()),
        };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes the full vouchfx pipeline for a single scenario and returns the
    /// aggregate <see cref="Verdict"/>.
    /// </summary>
    /// <param name="yamlText">
    /// The raw text of a <c>.e2e.yaml</c> scenario file.
    /// </param>
    /// <param name="scenarioName">
    /// A human-readable name for the scenario, used as the <c>scenarioId</c>
    /// in the event stream and as the Roslyn ALC run label.
    /// </param>
    /// <param name="providerAssemblies">
    /// The assemblies to scan for <see cref="StepProviderAttribute"/>-decorated
    /// provider classes.  The runner is provider-agnostic; the caller supplies
    /// the Core (and any additional) provider assemblies.
    /// </param>
    /// <param name="appHostAssemblyName">
    /// The short assembly name of the test project that carries
    /// <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c> and the embedded
    /// DCP metadata attributes (R-1 finding, CLAUDE.md §"Aspire (§4, §19)").
    /// Pass <see langword="null"/> to let Aspire fall back to
    /// <see cref="Assembly.GetEntryAssembly"/>.
    /// </param>
    /// <param name="output">
    /// The <see cref="TextWriter"/> that receives the rendered terminal output.
    /// </param>
    /// <param name="seedBaseDirectory">
    /// The base directory against which relative <c>environment.seed</c> SQL file
    /// paths are resolved (S05-A-01).  Defaults to the current working directory
    /// when <see langword="null"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Propagated to all async operations in the pipeline.
    /// </param>
    /// <returns>
    /// The aggregate <see cref="Verdict"/> for the scenario, aggregated with
    /// precedence <c>EnvironmentError &gt; Fail &gt; Inconclusive &gt; Pass</c>.
    /// </returns>
    public static async Task<Verdict> RunAsync(
        string yamlText,
        string scenarioName,
        IEnumerable<Assembly> providerAssemblies,
        string? appHostAssemblyName,
        TextWriter output,
        string? seedBaseDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(yamlText);
        ArgumentNullException.ThrowIfNull(scenarioName);
        ArgumentNullException.ThrowIfNull(providerAssemblies);
        ArgumentNullException.ThrowIfNull(output);

        // ── Build provider registry + render-time diff-lookup closure ─────────
        // Rendering is this wrapper's responsibility (the no-render core returns a
        // fully-populated event buffer instead), so the registry and the diff-lookup
        // closure it feeds are built here.
        //
        // Render-time diff-lookup closure (S07-G-01): resolves a step's kind to its
        // provider's IStepDiffRenderer (when implemented) so the terminal renderer can
        // draw an expected-vs-observed diff under a failed step.  Built once over the
        // frozen registry and threaded into the single TerminalRenderer.Render call.
        var registry = StepKindRegistry.BuildAndFreeze(providerAssemblies);
        var diffLookup = BuildDiffLookup(registry);

        // Delegate to the no-render core, which builds its own topology, runs the
        // single scenario, and returns the fully-populated event buffer + verdict for
        // whichever path it took (every early-exit included).  This wrapper then renders
        // that buffer exactly ONCE — reproducing, byte-for-byte, what the per-path
        // renders did before this extraction (S07-C-03 foundation).
        var (verdict, buffer) = await RunScenarioOwningTopologyAsync(
            registry,
            yamlText,
            scenarioName,
            appHostAssemblyName,
            output,
            seedBaseDirectory,
            livePump: null,
            cancellationToken).ConfigureAwait(false);

        TerminalRenderer.Render(buffer, output, diffLookup);
        return verdict;
    }

    /// <summary>
    /// Executes the full vouchfx pipeline for a single scenario — building and
    /// owning its own Aspire topology — and returns the fully-populated event
    /// buffer together with the aggregate <see cref="Verdict"/>, <strong>without
    /// rendering</strong>.  This is the no-render, owns-its-own-topology entry
    /// point that a future <c>ParallelSuiteRunner</c> (Sprint 8, S07-C-03) calls
    /// once per scenario, then renders all returned buffers itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method contains exactly the logic that previously lived inline in
    /// <see cref="RunAsync"/> (build own topology → run one scenario → every
    /// early-exit path), <strong>except</strong> rendering and the render-time
    /// diff-lookup construction — those are render concerns owned by the caller.
    /// Every place that previously did
    /// <c>TerminalRenderer.Render(buffer, output, diffLookup); return verdict;</c>
    /// here instead does <c>return (verdict, buffer);</c>: the returned buffer is the
    /// complete event stream for the chosen path, so a single caller-side
    /// <see cref="TerminalRenderer.Render(IEnumerable{string}, TextWriter, Func{string, JsonElement, string?}?)"/>
    /// reproduces the previous per-path render byte-for-byte.
    /// </para>
    /// <para>
    /// <paramref name="output"/> is retained <strong>only</strong> for the raw
    /// diagnostic text the early-exit paths write directly (schema-validation
    /// errors, parse errors, the pipeline-failure message, the secret-reference
    /// error).  Those raw writes are NOT event-stream lines — they are not in the
    /// returned buffer and are not reproduced by rendering — so to preserve output
    /// byte-for-byte they must still be written here, before the caller renders.
    /// This method never calls <see cref="TerminalRenderer"/>.
    /// </para>
    /// </remarks>
    /// <param name="registry">
    /// The frozen provider registry (built by the caller from the provider
    /// assemblies).  Used for schema validation and the provider pipeline.
    /// </param>
    /// <param name="yamlText">The raw text of a <c>.e2e.yaml</c> scenario file.</param>
    /// <param name="scenarioName">
    /// Human-readable scenario name, used as the <c>scenarioId</c> in the event
    /// stream and as the Roslyn ALC run label.
    /// </param>
    /// <param name="appHostAssemblyName">
    /// Short name of the Aspire host assembly (R-1 finding, CLAUDE.md §"Aspire").
    /// </param>
    /// <param name="output">
    /// The <see cref="TextWriter"/> that receives the raw early-exit diagnostic
    /// text (never rendered event lines).
    /// </param>
    /// <param name="seedBaseDirectory">
    /// Base directory for relative <c>environment.seed</c> SQL file paths (S05-A-01).
    /// </param>
    /// <param name="cancellationToken">Propagated to all async operations.</param>
    /// <returns>
    /// A tuple of the aggregate <see cref="Verdict"/> and the complete event buffer
    /// for the chosen path; the buffer is non-empty and always contains a
    /// scenario-completed event.
    /// </returns>
    internal static async Task<(Verdict Verdict, List<string> Buffer)> RunScenarioOwningTopologyAsync(
        StepKindRegistry registry,
        string yamlText,
        string scenarioName,
        string? appHostAssemblyName,
        TextWriter output,
        string? seedBaseDirectory,
        LiveEventPump? livePump,
        CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid().ToString("n");
        var buffer = new List<string>();

        // ── Step 2: Validate YAML against composed JSON Schema ────────────────
        var validationResult = DocumentValidator.Validate(yamlText, registry);
        if (!validationResult.IsValid)
        {
            // Schema-invalid → Inconclusive (the scenario never ran; this is an
            // authoring error, not a product defect).
            buffer.Add(EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = runId,
                Timestamp = DateTimeOffset.UtcNow,
                ScenarioId = scenarioName,
            }));

            buffer.Add(EventStreamJson.ToLine(new ScenarioCompletedEvent
            {
                RunId = runId,
                Timestamp = DateTimeOffset.UtcNow,
                ScenarioId = scenarioName,
                Verdict = Verdict.Inconclusive,
                Counts = new VerdictCounts { Inconclusive = 1 },
            }));

            foreach (var error in validationResult.Errors)
            {
                // Issue #266, Item 4: a schema error's Message can echo untrusted YAML
                // content (a field value, a step id) back verbatim — sanitise it before it
                // reaches the terminal/CI log.
                await output.WriteLineAsync(DisplaySanitiser.SanitiseForDisplay(error.Message))
                    .ConfigureAwait(false);
            }

            // Issue #262: this early-exit buffer is never reached by RunScenarioCoreAsync's
            // live streaming (no topology, no compiled script, no steps), so the caller must
            // still post it explicitly for a live tail to observe it.
            livePump?.PostRange(buffer);
            return (Verdict.Inconclusive, buffer);
        }

        // ── Step 3: Parse YAML → E2eDocument → ScenarioAst ───────────────────
        ScenarioAst ast;
        E2eDocument doc;
        try
        {
            doc = YamlDocumentParser.Parse(yamlText);
            ast = AstBuilder.Build(doc, registry);
        }
        catch (Exception ex)
        {
            buffer.Add(EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = runId,
                Timestamp = DateTimeOffset.UtcNow,
                ScenarioId = scenarioName,
            }));

            buffer.Add(EventStreamJson.ToLine(new ScenarioCompletedEvent
            {
                RunId = runId,
                Timestamp = DateTimeOffset.UtcNow,
                ScenarioId = scenarioName,
                Verdict = Verdict.Inconclusive,
                Counts = new VerdictCounts { Inconclusive = 1 },
            }));

            // Issue #266, Item 4: ex.Message can echo untrusted YAML/AST-builder text back
            // verbatim — sanitise it before it reaches the terminal/CI log.
            await output.WriteLineAsync(
                DisplaySanitiser.SanitiseForDisplay($"Parse / AST error: {ex.Message}"))
                .ConfigureAwait(false);

            livePump?.PostRange(buffer);
            return (Verdict.Inconclusive, buffer);
        }

        // ── Step 4: Provider pipeline — bind / validate / resources / emit ───
        var pipelineResult = ProviderPipeline.Compile(ast, registry, SuiteNamespace, seedBaseDirectory);
        if (pipelineResult.Failure is not null)
        {
            buffer.Add(EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = runId,
                Timestamp = DateTimeOffset.UtcNow,
                ScenarioId = scenarioName,
            }));
            buffer.Add(EventStreamJson.ToLine(new ScenarioCompletedEvent
            {
                RunId = runId,
                Timestamp = DateTimeOffset.UtcNow,
                ScenarioId = scenarioName,
                Verdict = Verdict.Inconclusive,
                Counts = new VerdictCounts { Inconclusive = 1 },
            }));
            // Issue #266, Item 4: the pipeline-failure Message can echo untrusted YAML
            // content (a step id, a field value) back verbatim — sanitise before writing.
            await output.WriteLineAsync(
                DisplaySanitiser.SanitiseForDisplay(pipelineResult.Failure.Message))
                .ConfigureAwait(false);
            livePump?.PostRange(buffer);
            return (Verdict.Inconclusive, buffer);
        }

        // ── Step 5c: Validate secret references (§17, S05-B-01) ──────────────
        // A central, provider-uniform pass over every substitutable field text.
        // Runs BEFORE the topology is started and BEFORE CompileOnce so a bad
        // secret reference is caught without spinning up any containers — the
        // scenario never ran, so the verdict is Inconclusive, not Fail.
        if (TryValidateSecretReferences(ast, out var secretError))
        {
            var now5c = DateTimeOffset.UtcNow;
            buffer.Add(EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = runId,
                Timestamp = now5c,
                ScenarioId = scenarioName,
            }));
            buffer.Add(EventStreamJson.ToLine(new ScenarioCompletedEvent
            {
                RunId = runId,
                Timestamp = now5c,
                ScenarioId = scenarioName,
                Verdict = Verdict.Inconclusive,
                Counts = new VerdictCounts { Inconclusive = 1 },
            }));
            // Issue #266, Item 4: secretError names the offending step id/field text from
            // untrusted YAML — sanitise before writing.
            await output.WriteLineAsync(DisplaySanitiser.SanitiseForDisplay(secretError))
                .ConfigureAwait(false);
            livePump?.PostRange(buffer);
            return (Verdict.Inconclusive, buffer);
        }

        // ── Step 6: Start Aspire topology ─────────────────────────────────────
        SuiteTopology suite;
        try
        {
            suite = await SuiteTopology.StartAsync(
                doc.Environment,
                appHostAssemblyName,
                startupTimeout: TimeSpan.FromSeconds(120),
                seedBaseDirectory: seedBaseDirectory,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException aex)
        {
            // A Map()-time authoring error (malformed env: reference, unknown dependency,
            // secret-in-env, unsupported .part, etc.) is the SAME class of problem as the
            // schema-invalid / parse-AST / pipeline-compile / secret-reference failures handled
            // above (Steps 2–5c): the scenario never ran, so this is Inconclusive (§12.1) — NOT
            // EnvironmentError, which the taxonomy reserves for genuine infrastructure faults
            // (container/image/network) an author cannot fix by editing the YAML; misclassifying
            // a permanent typo as an infra fault could drive auto-retry/alerting that will never
            // self-heal.
            //
            // Scope note: this catches ONLY EnvironmentMapper.Map's OWN eager, PRE-Configure
            // validation (SuiteTopology.StartAsync's Step 1, called before HeadlessTopology.
            // StartAsync/DCP is ever reached — see that method's Step 1 comment: "Map is pure
            // ... let them propagate as-is"). Map()'s Configure CLOSURE also carries a few
            // defensive ArgumentException throws of its own (e.g. ResolveDependencyEnvAccess's
            // internal-error fallback, RequirePasswordParameter) — those run LATER, inside
            // HeadlessTopology.StartAsync's own try/catch (SuiteTopology.cs Step 2), which wraps
            // ANY exception as OrchestrationException before it ever reaches this method; they
            // are therefore unreachable by construction here (ValidateEnvValue's eager checks,
            // and the CURRENT Aspire.Hosting.Redis/.Nats behaviour of always provisioning a
            // password parameter, already prevent them from firing in practice) and would
            // correctly surface as EnvironmentError via the OrchestrationException catch below,
            // not this one — a genuine, if never-yet-observed, infrastructure/engine fault.
            var now = DateTimeOffset.UtcNow;
            buffer.Add(EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = runId,
                Timestamp = now,
                ScenarioId = scenarioName,
            }));
            buffer.Add(EventStreamJson.ToLine(new ScenarioCompletedEvent
            {
                RunId = runId,
                Timestamp = now,
                ScenarioId = scenarioName,
                Verdict = Verdict.Inconclusive,
                Counts = new VerdictCounts { Inconclusive = 1 },
            }));
            // Issue #266, Item 4: aex.Message can echo untrusted YAML content (an
            // environment.services/dependencies reference) back verbatim — sanitise.
            await output.WriteLineAsync(
                DisplaySanitiser.SanitiseForDisplay($"Environment configuration error: {aex.Message}"))
                .ConfigureAwait(false);
            livePump?.PostRange(buffer);
            return (Verdict.Inconclusive, buffer);
        }
        catch (OrchestrationException oex)
        {
            var now = DateTimeOffset.UtcNow;
            buffer.Add(EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = runId,
                Timestamp = now,
                ScenarioId = scenarioName,
            }));
            buffer.Add(EnvironmentErrorEvents.ToLine(oex.Info, runId, now));
            buffer.Add(EventStreamJson.ToLine(new ScenarioCompletedEvent
            {
                RunId = runId,
                Timestamp = now,
                ScenarioId = scenarioName,
                Verdict = Verdict.EnvironmentError,
                Counts = new VerdictCounts { EnvError = 1 },
            }));
            livePump?.PostRange(buffer);
            return (Verdict.EnvironmentError, buffer);
        }

        await using (suite.ConfigureAwait(false))
        {
            // Use NullScenarioIsolation for single-scenario RunAsync — no state reset needed.
            IScenarioIsolation isolation = new NullScenarioIsolation();
            var verdict = await RunScenarioAgainstTopologyAsync(
                ast,
                scenarioName,
                runId,
                suite,
                pipelineResult.Assembled!,
                pipelineResult.CompileReferencePaths,
                pipelineResult.HostResourcePlan,
                buffer,
                isolation,
                output,
                seedBaseDirectory,
                cancellationToken,
                livePump: livePump).ConfigureAwait(false);

            return (verdict, buffer);
        }
    }

    /// <summary>
    /// Executes many scenarios against a topology that is built <strong>once</strong>
    /// and torn down after all scenarios complete, resetting mutable dependency state
    /// between scenarios via <see cref="BuildIsolation"/>'s result — every resettable
    /// dependency the topology declares (S04-A-02, generalised beyond Postgres).
    /// </summary>
    /// <param name="scenarios">
    /// The ordered list of fully-parsed scenario ASTs to execute.  All scenarios
    /// must share the same <c>environment</c> block — the topology is built from
    /// the first scenario's environment; a mismatch is reported as
    /// <see cref="Verdict.EnvironmentError"/>.
    /// </param>
    /// <param name="scenarioNames">
    /// Human-readable names for each scenario, used as the <c>scenarioId</c>
    /// in the event stream.  Must have the same length as <paramref name="scenarios"/>.
    /// </param>
    /// <param name="yamlTexts">
    /// The raw YAML text for each scenario (used for schema validation and compilation).
    /// Must have the same length as <paramref name="scenarios"/>.
    /// </param>
    /// <param name="providerAssemblies">
    /// The assemblies to scan for provider classes.
    /// </param>
    /// <param name="appHostAssemblyName">
    /// Short name of the Aspire host assembly (R-1 finding, CLAUDE.md §"Aspire (§4, §19)").
    /// </param>
    /// <param name="output">
    /// The <see cref="TextWriter"/> that receives the rendered terminal output.
    /// </param>
    /// <param name="seedBaseDirectory">
    /// The base directory against which the ONE shared topology's
    /// <c>environment.seed</c> SQL file paths are resolved (S05-A-01), and against which the
    /// reproducibility envelope's seed-fixture digests are hashed. Defaults to the current
    /// working directory when <see langword="null"/>. This stays rooted at the FIRST
    /// scenario's own directory regardless of <paramref name="scenarioBaseDirectories"/>
    /// (issue #268): the shared topology is built ONCE from <c>scenarios[0].Environment</c>
    /// and seeded ONCE against ONE base directory — <c>environment.seed</c> is genuinely
    /// single-rooted in this sequential, shared-topology path, unlike a step's own <c>file:</c>
    /// reference (see <paramref name="scenarioBaseDirectories"/>).
    /// </param>
    /// <param name="scenarioBaseDirectories">
    /// Per-scenario base directories (issue #268), in the same order as
    /// <paramref name="scenarios"/>: each scenario's OWN directory, used to resolve that
    /// scenario's <c>script.csharp</c> <c>file:</c> reference at compile time (via
    /// <see cref="ProviderPipeline.Compile"/>'s <c>suiteDirectory</c> parameter) and to hash it
    /// for that scenario's reproducibility-envelope script-file digest. Unlike
    /// <paramref name="seedBaseDirectory"/> (the shared topology's single seed root),
    /// <c>script.csharp</c> is compiled PER SCENARIO with no single-root constraint, so a
    /// non-first scenario's relative <c>file:</c> reference must resolve against ITS OWN
    /// directory, never the first scenario's. <see langword="null"/> (the default) or a
    /// <see langword="null"/> element falls back to <paramref name="seedBaseDirectory"/> for
    /// that scenario, preserving pre-#268 behaviour for callers that do not supply this list.
    /// When supplied, must have the same length as <paramref name="scenarios"/>.
    /// </param>
    /// <param name="htmlReportPath">
    /// Optional destination for a self-contained HTML report (S09-D-01, T3).  When
    /// non-<see langword="null"/>, the report is written from the SAME event buffer and
    /// diff lookup as the terminal render (parity).  <see langword="null"/> ⇒ no HTML report.
    /// </param>
    /// <param name="junitReportPath">
    /// Optional destination for a JUnit XML results file (S09-D-01, T3).  When
    /// non-<see langword="null"/>, the file is written from the SAME event buffer as the
    /// terminal render.  <see langword="null"/> ⇒ no JUnit report.
    /// </param>
    /// <param name="eventsReportPath">
    /// Optional destination for the raw JSON Lines event stream (S10).  When
    /// non-<see langword="null"/>, the SAME event buffer the terminal render consumed is written
    /// there <em>verbatim</em> (one line per element, UTF-8 without a BOM) — an additive raw
    /// passthrough of the frozen v1 stream for a downstream consumer.  <see langword="null"/> ⇒ no
    /// events artifact.
    /// </param>
    /// <param name="eventsStreamPath">
    /// Optional destination for the INCREMENTAL, tailable JSON Lines event stream (issue #258;
    /// per-step/per-attempt liveness issue #262). When non-<see langword="null"/>, a
    /// <see cref="LiveEventPump"/> (wrapping an <see cref="Reporting.EventStreamAppender"/>) is
    /// opened ONCE before the scenario loop. Each scenario's <c>step-started</c> /
    /// <c>step-attempt</c> / <c>step-completed</c> lines are posted to it in REAL TIME, from
    /// inside the isolated run, via a per-scenario <see cref="LiveStepEventSink"/>; the
    /// scenario-framing lines are posted alongside the archive write. An early-exit scenario
    /// (never reaching the core) still posts its own buffer explicitly. This is a SEPARATE file
    /// from <paramref name="eventsReportPath"/> — the <c>--events</c> / <c>--json</c> archive is
    /// still written ONCE, at the end, from the complete <c>allBuffers</c>, byte-for-byte
    /// unchanged. <see langword="null"/> ⇒ no incremental stream (the default).
    /// </param>
    /// <param name="decorate">
    /// Accessibility decoration flag (S10-G-03a): when <see langword="true"/>, the single suite-level
    /// terminal render decorates each step-verdict line with an ANSI colour + a per-verdict shape
    /// glyph; when <see langword="false"/> (the default) the render is plain text — byte-identical to
    /// the pre-S10-G-03a output.  The verdict TEXT tokens (the WCAG-1.4.1 guarantee) are unconditional
    /// and independent of this flag; only the optional colour + glyph layer is gated.  The caller
    /// (CLI) computes it from <c>--no-decorations</c> + <c>NO_COLOR</c> + output redirection.
    /// </param>
    /// <param name="cancellationToken">
    /// Propagated to all async operations.
    /// </param>
    /// <returns>
    /// A <see cref="SuiteResult"/> containing the per-scenario verdicts and the
    /// suite-level aggregate verdict.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Shared-environment assumption:</strong> all scenarios in the suite
    /// are expected to declare the same <c>environment</c> block.  The topology is
    /// built from <paramref name="scenarios"/>[0].Environment.  If a later scenario's
    /// environment differs (detected by structural equality on the serialised JSON),
    /// the suite short-circuits with <see cref="Verdict.EnvironmentError"/> — running
    /// heterogeneous topologies in one suite would produce unpredictable results.
    /// </para>
    /// <para>
    /// <strong>Isolation failure → EnvironmentError:</strong> any
    /// <see cref="OrchestrationException"/> thrown by
    /// <see cref="IScenarioIsolation.BeginScenarioAsync"/> or
    /// <see cref="IScenarioIsolation.EndScenarioAsync"/> causes the affected scenario
    /// to receive <see cref="Verdict.EnvironmentError"/> and the suite to abort —
    /// because subsequent scenarios would run against an unknown DB state.
    /// </para>
    /// </remarks>
    public static async Task<SuiteResult> RunSuiteAsync(
        IReadOnlyList<ScenarioAst> scenarios,
        IReadOnlyList<string> scenarioNames,
        IReadOnlyList<string> yamlTexts,
        IEnumerable<Assembly> providerAssemblies,
        string? appHostAssemblyName,
        TextWriter output,
        string? seedBaseDirectory = null,
        IReadOnlyList<string?>? scenarioBaseDirectories = null,
        string? htmlReportPath = null,
        string? junitReportPath = null,
        string? eventsReportPath = null,
        string? eventsStreamPath = null,
        bool decorate = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(scenarioNames);
        ArgumentNullException.ThrowIfNull(yamlTexts);
        ArgumentNullException.ThrowIfNull(providerAssemblies);
        ArgumentNullException.ThrowIfNull(output);

        if (scenarios.Count == 0)
        {
            return new SuiteResult(Verdict.Pass, Array.Empty<(string, Verdict)>());
        }

        if (scenarioNames.Count != scenarios.Count
            || yamlTexts.Count != scenarios.Count
            || (scenarioBaseDirectories is not null && scenarioBaseDirectories.Count != scenarios.Count))
        {
            throw new ArgumentException(
                "scenarios, scenarioNames, yamlTexts, and (when supplied) scenarioBaseDirectories "
                + "must all have the same length.",
                nameof(scenarios));
        }

        // Build the provider registry once (shared across all scenarios).
        var registry = StepKindRegistry.BuildAndFreeze(providerAssemblies);

        // Render-time diff-lookup closure (S07-G-01), built once over the frozen
        // registry and threaded into the suite-level TerminalRenderer.Render call.
        var diffLookup = BuildDiffLookup(registry);

        // ── Validate shared-environment assumption ─────────────────────────────
        // All scenarios must share the environment declared in scenario[0].
        // If any scenario diverges, return EnvironmentError for the whole suite.
        var firstEnvJson = SerialiseEnvironment(scenarios[0].Environment);
        for (int i = 1; i < scenarios.Count; i++)
        {
            var envJson = SerialiseEnvironment(scenarios[i].Environment);
            if (!string.Equals(envJson, firstEnvJson, StringComparison.Ordinal))
            {
                // Issue #266, Item 4: scenarioNames[i] is author-controlled (a scenario's
                // metadata.name or file-derived identity) — sanitise before writing.
                await output.WriteLineAsync(
                    DisplaySanitiser.SanitiseForDisplay(
                        $"RunSuiteAsync: scenario '{scenarioNames[i]}' declares a different " +
                        "environment block than the first scenario.  All scenarios in a suite " +
                        "must share one topology.  Suite aborted with EnvironmentError."))
                    .ConfigureAwait(false);

                return new SuiteResult(
                    Verdict.EnvironmentError,
                    Array.Empty<(string, Verdict)>());
            }
        }

        // ── Per-scenario compilation (pre-topology) ───────────────────────────
        // Validate + compile each scenario's YAML before we pay the topology build cost.
        var compilations = new List<(
            string ScenarioName,
            ScenarioAst Ast,
            PipelineResult? Pipeline,
            Verdict? EarlyVerdict,
            string? EarlyMessage,
            string? ScenarioBaseDirectory)>();

        for (int i = 0; i < scenarios.Count; i++)
        {
            var name = scenarioNames[i];
            var yaml = yamlTexts[i];
            var ast = scenarios[i];

            // Issue #268: each scenario compiles against its OWN directory — falling back to
            // the shared seed base directory only when the caller supplied no per-scenario
            // list (pre-#268 callers). This is the value script.csharp's `file:` resolves
            // against (via ProviderPipeline.Compile below) and the value the reproducibility
            // envelope later hashes this scenario's script-file digest against — it does NOT
            // affect the ONE shared topology's seed, which stays rooted at seedBaseDirectory.
            var scenarioBaseDirectory = scenarioBaseDirectories?[i] ?? seedBaseDirectory;

            // Schema-validate the YAML.
            var validationResult = DocumentValidator.Validate(yaml, registry);
            if (!validationResult.IsValid)
            {
                compilations.Add((name, ast, null, Verdict.Inconclusive,
                    string.Join("; ", validationResult.Errors.Select(e => e.Message)),
                    scenarioBaseDirectory));
                continue;
            }

            // Secret-reference validation (§17, S05-B-01) — engine-level, runs
            // before the topology is built so a bad reference costs no containers.
            if (TryValidateSecretReferences(ast, out var secretError))
            {
                compilations.Add((name, ast, null, Verdict.Inconclusive, secretError, scenarioBaseDirectory));
                continue;
            }

            // Provider pipeline compile — resolves `file:`/other relative step fields against
            // THIS scenario's own directory (#268), not the suite-wide seed base directory.
            var pipelineResult = ProviderPipeline.Compile(ast, registry, SuiteNamespace, scenarioBaseDirectory);
            if (pipelineResult.Failure is not null)
            {
                compilations.Add((name, ast, null, Verdict.Inconclusive,
                    pipelineResult.Failure.Message, scenarioBaseDirectory));
                continue;
            }

            compilations.Add((name, ast, pipelineResult, null, null, scenarioBaseDirectory));
        }

        // ── Build topology once ────────────────────────────────────────────────
        SuiteTopology suite;
        try
        {
            suite = await SuiteTopology.StartAsync(
                scenarios[0].Environment,
                appHostAssemblyName,
                startupTimeout: TimeSpan.FromSeconds(120),
                seedBaseDirectory: seedBaseDirectory,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException aex)
        {
            // Mirrors RunAsync's classification above (see that catch block's full scope note):
            // a Map()-time authoring error (malformed env: reference, unknown dependency,
            // secret-in-env, unsupported .part, etc.) is the SAME class of problem as the
            // per-scenario secret-reference / pipeline-compile failures already handled as
            // Inconclusive elsewhere in this loop (via earlyVerdict) — the suite never ran, so
            // every scenario is Inconclusive, NOT EnvironmentError (reserved for genuine
            // infrastructure faults an author cannot fix by editing the YAML). This catches ONLY
            // Map's eager, pre-Configure validation; Map's Configure-closure defensive throws
            // (unreachable by construction given that same eager validation) would surface as
            // OrchestrationException instead, via the catch below.
            // Issue #266, Item 4: aex.Message can echo untrusted YAML content back
            // verbatim — sanitise before writing.
            await output.WriteLineAsync(
                DisplaySanitiser.SanitiseForDisplay(
                    $"RunSuiteAsync: environment configuration error — {aex.Message}"))
                .ConfigureAwait(false);

            var inconclusiveVerdicts = compilations
                .Select(c => (c.ScenarioName, Verdict.Inconclusive))
                .ToList();
            return new SuiteResult(Verdict.Inconclusive, inconclusiveVerdicts);
        }
        catch (OrchestrationException oex)
        {
            // Issue #266, Item 4: oex.Message is usually an infra-fault message, but is
            // sanitised for consistency with every sibling diagnostic write in this method.
            await output.WriteLineAsync(
                DisplaySanitiser.SanitiseForDisplay(
                    $"RunSuiteAsync: topology failed to start — {oex.Message}"))
                .ConfigureAwait(false);

            // Every scenario receives EnvironmentError.
            var errVerdicts = compilations
                .Select(c => (c.ScenarioName, Verdict.EnvironmentError))
                .ToList();
            return new SuiteResult(Verdict.EnvironmentError, errVerdicts);
        }

        await using (suite.ConfigureAwait(false))
        {
            // ── Construct isolation ────────────────────────────────────────────
            // Resets EVERY resettable dependency the topology declares (composed
            // when there is more than one). NullScenarioIsolation preserves the
            // existing behaviour when none is resettable.
            IScenarioIsolation isolation = BuildIsolation(suite);

            var results = new List<(string ScenarioName, Verdict Verdict)>(compilations.Count);
            var suiteAggregate = Verdict.Pass;
            var allBuffers = new List<string>();

            // ── Incremental events stream (issue #258; per-step liveness issue #262) ──────
            // Opened ONCE, guarded on a non-null path, and disposed automatically when this
            // `await using` block's own scope is left (return or exception) — BEFORE `suite`
            // (the enclosing `await using (suite.ConfigureAwait(false))` resource) tears down.
            // Completely separate from `eventsReportPath`: the --events / --json archive is
            // still written once, at the end, from the finished `allBuffers` (see
            // FileReportWriter.WriteFileReports below) — byte-for-byte unchanged.
            //
            // Issue #262: EventStreamAppender is now wrapped by LiveEventPump, a bounded,
            // non-blocking conduit — RunScenarioAgainstTopologyAsync (via RunScenarioCoreAsync)
            // threads this SAME pump down to a per-scenario LiveStepEventSink so per-step /
            // per-attempt lines stream the moment they happen, not only after the scenario
            // returns. Early-exit scenarios below (which never reach the core) still post
            // their own buffer explicitly.
            await using var livePump = eventsStreamPath is not null
                ? new LiveEventPump(eventsStreamPath, output)
                : null;

            try
            {
                for (int i = 0; i < compilations.Count; i++)
                {
                    var (name, ast, pipeline, earlyVerdict, earlyMessage, scenarioBaseDirectory) = compilations[i];
                    var runId = Guid.NewGuid().ToString("n");
                    var buffer = new List<string>();

                    // Handle early-exit scenarios (validation / RETRY / pipeline failure).
                    if (earlyVerdict is not null)
                    {
                        var now = DateTimeOffset.UtcNow;
                        buffer.Add(EventStreamJson.ToLine(new ScenarioStartedEvent
                        {
                            RunId = runId,
                            Timestamp = now,
                            ScenarioId = name,
                        }));
                        buffer.Add(EventStreamJson.ToLine(new ScenarioCompletedEvent
                        {
                            RunId = runId,
                            Timestamp = now,
                            ScenarioId = name,
                            Verdict = earlyVerdict.Value,
                            Counts = new VerdictCounts { Inconclusive = 1 },
                        }));
                        if (!string.IsNullOrEmpty(earlyMessage))
                        {
                            // Issue #266, Item 4: earlyMessage carries a schema/pipeline/
                            // secret-reference diagnostic that may echo untrusted YAML
                            // content verbatim — sanitise before writing.
                            await output.WriteLineAsync(
                                DisplaySanitiser.SanitiseForDisplay(earlyMessage))
                                .ConfigureAwait(false);
                        }

                        results.Add((name, earlyVerdict.Value));
                        suiteAggregate = Elevate(suiteAggregate, earlyVerdict.Value);
                        allBuffers.AddRange(buffer);
                        livePump?.PostRange(buffer);
                        continue;
                    }

                    // ── BeginScenario (isolation) ──────────────────────────────────
                    try
                    {
                        await isolation.BeginScenarioAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OrchestrationException oex)
                    {
                        var now = DateTimeOffset.UtcNow;
                        buffer.Add(EventStreamJson.ToLine(new ScenarioStartedEvent
                        {
                            RunId = runId,
                            Timestamp = now,
                            ScenarioId = name,
                        }));
                        buffer.Add(EnvironmentErrorEvents.ToLine(oex.Info, runId, now));
                        buffer.Add(EventStreamJson.ToLine(new ScenarioCompletedEvent
                        {
                            RunId = runId,
                            Timestamp = now,
                            ScenarioId = name,
                            Verdict = Verdict.EnvironmentError,
                            Counts = new VerdictCounts { EnvError = 1 },
                        }));
                        results.Add((name, Verdict.EnvironmentError));
                        suiteAggregate = Elevate(suiteAggregate, Verdict.EnvironmentError);
                        allBuffers.AddRange(buffer);
                        livePump?.PostRange(buffer);

                        // Isolation failure → abort the suite (subsequent scenarios
                        // would run against an unknown DB state).
                        //
                        // Issue #266, Item 4: 'name' is author-controlled and oex.Message may
                        // echo untrusted content — sanitise before writing.
                        await output.WriteLineAsync(
                            DisplaySanitiser.SanitiseForDisplay(
                                $"Isolation.BeginScenarioAsync failed for '{name}': {oex.Message}; " +
                                "aborting suite.")).ConfigureAwait(false);
                        break;
                    }

                    // ── Run scenario ───────────────────────────────────────────────
                    // seedBaseDirectory (unchanged): the ONE shared topology's seed root.
                    // scenarioBaseDirectory (#268): THIS scenario's own directory, for its
                    // script.csharp file: digest in the reproducibility envelope — matching
                    // the base directory ProviderPipeline.Compile already resolved file:
                    // against, above.
                    var scenarioVerdict = await RunScenarioAgainstTopologyAsync(
                        ast,
                        name,
                        runId,
                        suite,
                        pipeline!.Assembled!,
                        pipeline.CompileReferencePaths,
                        pipeline.HostResourcePlan,
                        buffer,
                        new NullScenarioIsolation(), // isolation already handled above/below
                        output,
                        seedBaseDirectory,
                        cancellationToken,
                        scriptBaseDirectory: scenarioBaseDirectory,
                        livePump: livePump).ConfigureAwait(false);

                    results.Add((name, scenarioVerdict));
                    suiteAggregate = Elevate(suiteAggregate, scenarioVerdict);
                    allBuffers.AddRange(buffer);
                    // Issue #262: NO livePump?.PostRange(buffer) here — RunScenarioAgainstTopologyAsync
                    // (via RunScenarioCoreAsync) already streamed this scenario's lines live, as they
                    // happened, through the per-scenario LiveStepEventSink + explicit framing posts.
                    // Re-posting the reconstructed buffer here would DUPLICATE every line in the live
                    // file. `allBuffers` above still receives the complete, unaffected reconstruction
                    // for the end-of-run `--events` archive.

                    // ── EndScenario (isolation / reset) ────────────────────────────
                    try
                    {
                        await isolation.EndScenarioAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OrchestrationException oex)
                    {
                        // Emit the structured environment-error observation into the
                        // event stream (the scenario's own events are already in
                        // allBuffers), so every renderer sees WHICH dependency's
                        // reset failed — parity with the BeginScenarioAsync path.
                        var isolationFailureLine = EnvironmentErrorEvents.ToLine(
                            oex.Info, runId, DateTimeOffset.UtcNow);
                        allBuffers.Add(isolationFailureLine);
                        var isolationFailureLines = new[] { isolationFailureLine };
                        livePump?.PostRange(isolationFailureLines);

                        // Issue #266, Item 4: 'name' is author-controlled and oex.Message may
                        // echo untrusted content — sanitise before writing.
                        await output.WriteLineAsync(
                            DisplaySanitiser.SanitiseForDisplay(
                                $"Isolation.EndScenarioAsync failed after '{name}': {oex.Message}; " +
                                "aborting suite — subsequent scenarios may run against unclean state."))
                            .ConfigureAwait(false);
                        suiteAggregate = Elevate(suiteAggregate, Verdict.EnvironmentError);
                        break;
                    }
                }

            }
            finally
            {
                // Dispose the isolation connection when the topology is torn down —
                // in a finally so an exception escaping the scenario loop (e.g. a
                // genuine cancellation out of Begin/End or the scenario body, which
                // is deliberately NOT wrapped as an OrchestrationException) can never
                // leak the connection.
                if (isolation is IAsyncDisposable disposable)
                {
                    await disposable.DisposeAsync().ConfigureAwait(false);
                }
            }

            TerminalRenderer.Render(allBuffers, output, decorate, diffLookup);

            // Optional file reports (S09-D-01, T3; S10 events): write the HTML / JUnit artifacts —
            // and the raw JSON Lines events stream — from the SAME buffer + diffLookup the terminal
            // render just consumed, so every renderer (and the raw events file) sees byte-identical
            // input (parity).  A null path writes nothing.  `output` is the diagnostics sink: a bad
            // --html / --junit / --events path is caught PER FILE and reported there, so report
            // writing can NEVER change the already-computed verdict / exit code.
            FileReportWriter.WriteFileReports(
                allBuffers, diffLookup, htmlReportPath, junitReportPath, output, eventsPath: eventsReportPath);

            return new SuiteResult(suiteAggregate, results);
        }
    }

    // ── Watch-mode seams (S08-C-01) ─────────────────────────────────────────────

    /// <summary>
    /// Computes a stable hash of a scenario's <c>environment</c> block for watch-mode
    /// topology reuse (S08-C-01): two scenarios whose <c>environment</c> blocks are
    /// structurally equal produce the same string, so the watch loop can decide
    /// "environment unchanged → re-use the kept topology" with a plain string compare.
    /// </summary>
    /// <param name="environment">
    /// The parsed <c>environment</c> block, or <see langword="null"/> when the scenario
    /// declares none (an empty topology).
    /// </param>
    /// <returns>
    /// A stable string key derived from the same serialisation
    /// <see cref="RunSuiteAsync"/> uses for its shared-environment check, so the two can
    /// never disagree about what counts as "the same environment".  An empty string for a
    /// <see langword="null"/> environment.
    /// </returns>
    /// <remarks>
    /// Reuses the private <see cref="SerialiseEnvironment"/> helper — the SAME canonical
    /// form the suite runner already compares for its shared-topology assumption — so the
    /// reuse decision here and the suite's equality check are guaranteed consistent.
    /// </remarks>
    public static string ComputeEnvironmentHash(EnvironmentSpec? environment) =>
        SerialiseEnvironment(environment);

    /// <summary>
    /// Builds the per-topology isolation for watch mode (S08-C-01) via
    /// <c>ScenarioIsolationFactory.Create</c> — every resettable dependency the kept
    /// topology declares, composed when there is more than one, or
    /// <see cref="NullScenarioIsolation"/> when none is resettable.  Watch mode keeps
    /// ONE topology alive across re-runs, so it must reset mutable dependency state
    /// between re-runs exactly as <see cref="RunSuiteAsync"/> does between scenarios.
    /// </summary>
    /// <param name="topology">The kept topology to build isolation for.</param>
    /// <returns>The isolation strategy appropriate to the topology's dependencies.</returns>
    public static IScenarioIsolation BuildWatchIsolation(SuiteTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        return BuildIsolation(topology);
    }

    /// <summary>
    /// Runs a single scenario against an <strong>already-built</strong>
    /// <see cref="SuiteTopology"/> (the no-rebuild re-run seam for watch mode, S08-C-01):
    /// optionally reset+reseed → validate → compile → run → render.  The topology is neither
    /// built nor disposed here — the watch session owns its lifetime so it survives across
    /// re-runs while the environment is unchanged.
    /// </summary>
    /// <param name="topology">The kept topology to run against (built by the caller).</param>
    /// <param name="isolation">
    /// The state-reset strategy applied <em>before</em> the run when
    /// <paramref name="resetAndReseed"/> is set, so a reuse run starts from a known-clean
    /// dependency state.  Pass <see cref="BuildWatchIsolation"/>'s result.
    /// </param>
    /// <param name="registry">The frozen provider registry (schema validation + pipeline).</param>
    /// <param name="ast">The parsed scenario AST (its steps drive the event stream).</param>
    /// <param name="yamlText">The raw YAML (re-validated + re-compiled on every re-run).</param>
    /// <param name="scenarioName">The scenario name (event-stream <c>scenarioId</c>).</param>
    /// <param name="output">The writer that receives the rendered report + raw diagnostics.</param>
    /// <param name="resetAndReseed">
    /// Whether to reset (Respawn) and re-apply the seed BEFORE the run.  Pass <see langword="false"/>
    /// on the FIRST run against a freshly-built+seeded topology — <see cref="SuiteTopology.StartAsync"/>
    /// already applied the seed and there are no prior-run writes to clear, so a reset here would
    /// truncate the seed (and Respawn throws on a schema-via-<c>script.csharp</c> DB with no user
    /// tables yet).  Pass <see langword="true"/> on a REUSE run (same topology as the previous run,
    /// which left its writes behind): the reset clears those writes and the re-seed restores the
    /// freshly-seeded baseline, so every watch run sees the same initial state as a fresh
    /// <c>vouchfx run</c>.
    /// </param>
    /// <param name="seedBaseDirectory">Base directory for relative seed fixture paths.</param>
    /// <param name="cancellationToken">Propagated to all async operations.</param>
    /// <returns>The scenario's aggregate <see cref="Verdict"/>.</returns>
    /// <remarks>
    /// <para>
    /// Re-validating and re-compiling on every re-run is deliberate: in watch mode the file
    /// changes between runs, so the kept topology may be re-used but the SCENARIO must be
    /// re-read from the latest save.  Only the topology build is skipped on reuse, never the
    /// compile.
    /// </para>
    /// <para>
    /// <strong>Reset+reseed semantics (S08-T10):</strong> on the build path
    /// (<paramref name="resetAndReseed"/> = <see langword="false"/>) there is NO pre-reset and NO
    /// re-seed — matching <see cref="RunSuiteAsync"/>'s first scenario, whose <c>Begin</c> is a
    /// per-store isolation no-op against a just-seeded topology.  On the reuse path
    /// (<paramref name="resetAndReseed"/> = <see langword="true"/>) the kept topology is reset via
    /// <see cref="IScenarioIsolation.EndScenarioAsync"/> (clearing the prior run's writes via
    /// store-specific resets — relational DELETE orders, Mongo document deletion, Redis FLUSHDB,
    /// Elasticsearch delete_by_query — INCLUDING the seed rows, the documented "reset-clears-seed"
    /// behaviour) and then RE-SEEDED via <see cref="SuiteTopology.ReseedAsync"/>, so the run sees
    /// the freshly-seeded baseline — identical to a fresh <c>vouchfx run</c>.  A reset or re-seed
    /// failure surfaces as <see cref="Verdict.EnvironmentError"/> (§12.1), never a Fail.
    /// </para>
    /// </remarks>
    public static async Task<Verdict> RunScenarioAgainstKeptTopologyAsync(
        SuiteTopology topology,
        IScenarioIsolation isolation,
        StepKindRegistry registry,
        ScenarioAst ast,
        string yamlText,
        string scenarioName,
        TextWriter output,
        bool resetAndReseed,
        string? seedBaseDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(isolation);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(ast);
        ArgumentNullException.ThrowIfNull(yamlText);
        ArgumentNullException.ThrowIfNull(scenarioName);
        ArgumentNullException.ThrowIfNull(output);

        var diffLookup = BuildDiffLookup(registry);
        var runId = Guid.NewGuid().ToString("n");
        var buffer = new List<string>();

        // ── Reset + re-seed BEFORE a REUSE re-run (S08-T10) ───────────────────
        // ONLY on the reuse path, where the kept topology carries the previous re-run's writes.
        // Two complementary resets restore the SAME initial state a fresh `vouchfx run` sees:
        //   1. isolation.EndScenarioAsync — per-store isolation clears the prior run's writes
        //      across ALL data (relational DELETE orders, Mongo document deletion, Redis FLUSHDB,
        //      Elasticsearch delete_by_query — the right reset for unseeded dependencies and
        //      for data a prior run's script.csharp step created).
        //   2. topology.ReseedAsync — for SEEDED Postgres dependencies, drops and recreates the
        //      public schema then re-applies the seed, so the author's (non-idempotent) seed SQL
        //      re-runs cleanly and the seeded baseline is restored. Non-Postgres stores are
        //      skipped: there is no row-applied non-Postgres seed to restore (document-store and
        //      broker seeds are content-recorded via deferred seams; Redis has no seed path), and
        //      the isolation reset in step 1 already cleared them. A no-op when the scenario
        //      declares no seed.
        // Skipped ENTIRELY on the build path (resetAndReseed=false): StartAsync just applied the
        // seed and there are no prior writes, so a reset would wrongly truncate the seed (and
        // relational reset throws on a schema-via-script.csharp DB that has no user tables yet —
        // exactly why the normal path defers the reset to AFTER the first run).  A reset or
        // re-seed failure is an Environment error (§12.1), never a Fail — and we render before returning
        // so the verdict is still reported.
        if (resetAndReseed)
        {
            try
            {
                await isolation.EndScenarioAsync(cancellationToken).ConfigureAwait(false);
                await topology.ReseedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OrchestrationException oex)
            {
                var nowR = DateTimeOffset.UtcNow;
                buffer.Add(EventStreamJson.ToLine(new ScenarioStartedEvent
                {
                    RunId = runId,
                    Timestamp = nowR,
                    ScenarioId = scenarioName,
                }));
                buffer.Add(EnvironmentErrorEvents.ToLine(oex.Info, runId, nowR));
                buffer.Add(EventStreamJson.ToLine(new ScenarioCompletedEvent
                {
                    RunId = runId,
                    Timestamp = nowR,
                    ScenarioId = scenarioName,
                    Verdict = Verdict.EnvironmentError,
                    Counts = new VerdictCounts { EnvError = 1 },
                }));
                TerminalRenderer.Render(buffer, output, diffLookup);
                return Verdict.EnvironmentError;
            }
        }

        // ── Validate + compile the (latest-saved) scenario ────────────────────
        if (TryCompileForRun(
                registry, yamlText, ast, scenarioName, runId, buffer, seedBaseDirectory,
                out var pipeline, out var earlyVerdict, out var earlyMessage))
        {
            if (!string.IsNullOrEmpty(earlyMessage))
            {
                // Issue #266, Item 4: earlyMessage carries a schema/pipeline/secret-reference
                // diagnostic that may echo untrusted YAML content verbatim — sanitise before
                // writing.
                await output.WriteLineAsync(DisplaySanitiser.SanitiseForDisplay(earlyMessage))
                    .ConfigureAwait(false);
            }

            TerminalRenderer.Render(buffer, output, diffLookup);
            return earlyVerdict;
        }

        // ── Run against the kept topology ─────────────────────────────────────
        var verdict = await RunScenarioAgainstTopologyAsync(
            ast,
            scenarioName,
            runId,
            topology,
            pipeline!.Assembled!,
            pipeline.CompileReferencePaths,
            pipeline.HostResourcePlan,
            buffer,
            new NullScenarioIsolation(), // isolation reset handled above.
            output,
            seedBaseDirectory,
            cancellationToken).ConfigureAwait(false);

        TerminalRenderer.Render(buffer, output, diffLookup);
        return verdict;
    }

    /// <summary>
    /// Validates and compiles <paramref name="yamlText"/> for execution, emitting the
    /// Inconclusive scenario-started/completed pair into <paramref name="buffer"/> on any
    /// early-exit (schema-invalid, bad secret reference, pipeline failure).  Shared by the
    /// watch re-run path so it reproduces the suite runner's early-exit event shape exactly.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when an early-exit occurred (<paramref name="earlyVerdict"/> /
    /// <paramref name="earlyMessage"/> are set and <paramref name="pipeline"/> is
    /// <see langword="null"/>); <see langword="false"/> when compilation succeeded
    /// (<paramref name="pipeline"/> is set).
    /// </returns>
    private static bool TryCompileForRun(
        StepKindRegistry registry,
        string yamlText,
        ScenarioAst ast,
        string scenarioName,
        string runId,
        List<string> buffer,
        string? seedBaseDirectory,
        out PipelineResult? pipeline,
        out Verdict earlyVerdict,
        out string? earlyMessage)
    {
        pipeline = null;
        earlyVerdict = Verdict.Inconclusive;
        earlyMessage = null;

        void EmitInconclusive()
        {
            var now = DateTimeOffset.UtcNow;
            buffer.Add(EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = runId,
                Timestamp = now,
                ScenarioId = scenarioName,
            }));
            buffer.Add(EventStreamJson.ToLine(new ScenarioCompletedEvent
            {
                RunId = runId,
                Timestamp = now,
                ScenarioId = scenarioName,
                Verdict = Verdict.Inconclusive,
                Counts = new VerdictCounts { Inconclusive = 1 },
            }));
        }

        var validationResult = DocumentValidator.Validate(yamlText, registry);
        if (!validationResult.IsValid)
        {
            EmitInconclusive();
            earlyMessage = string.Join("; ", validationResult.Errors.Select(e => e.Message));
            return true;
        }

        if (TryValidateSecretReferences(ast, out var secretError))
        {
            EmitInconclusive();
            earlyMessage = secretError;
            return true;
        }

        var pipelineResult = ProviderPipeline.Compile(ast, registry, SuiteNamespace, seedBaseDirectory);
        if (pipelineResult.Failure is not null)
        {
            EmitInconclusive();
            earlyMessage = pipelineResult.Failure.Message;
            return true;
        }

        pipeline = pipelineResult;
        return false;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Executes the per-scenario compilation + isolated Roslyn run against an
    /// already-started topology, populating <paramref name="buffer"/> with event
    /// lines and returning the aggregate <see cref="Verdict"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method contains the body that was previously inlined inside
    /// <see cref="RunAsync"/> (after the topology was built).  Extracting it
    /// allows <see cref="RunSuiteAsync"/> to call it for each scenario without
    /// duplicating the event-emission logic.
    /// </para>
    /// <para>
    /// Note: <paramref name="isolation"/> is always <see cref="NullScenarioIsolation"/>
    /// when called from both <see cref="RunAsync"/> and the inner loop of
    /// <see cref="RunSuiteAsync"/> (the suite loop handles isolation externally).
    /// The parameter exists for future flexibility and to preserve the call-site shape.
    /// </para>
    /// </remarks>
    /// <param name="seedBaseDirectory">
    /// The base directory the reproducibility envelope hashes this scenario's seed-fixture
    /// digests against (unchanged by issue #268 — a single scenario, or the ONE shared suite
    /// topology's seed root, in every caller).
    /// </param>
    /// <param name="scriptBaseDirectory">
    /// The base directory the reproducibility envelope hashes THIS scenario's
    /// <c>script.csharp</c> <c>file:</c> digest against (issue #268). <see langword="null"/>
    /// (the default) falls back to <paramref name="seedBaseDirectory"/> — the single-scenario
    /// callers (<see cref="RunAsync"/>, watch mode) never pass this explicitly because their
    /// one scenario's own directory already equals its seed base directory. Only
    /// <see cref="RunSuiteAsync"/>'s per-scenario loop supplies a distinct value, when a
    /// non-first scenario's own directory differs from the shared seed root.
    /// </param>
    private static async Task<Verdict> RunScenarioAgainstTopologyAsync(
        ScenarioAst ast,
        string scenarioName,
        string runId,
        SuiteTopology suite,
        AssembledScript assembled,
        IReadOnlyList<string> compileReferencePaths,
        IReadOnlyList<HostResourcePlanEntry> hostResourcePlan,
        List<string> buffer,
        IScenarioIsolation isolation,
        TextWriter output,
        string? seedBaseDirectory,
        CancellationToken cancellationToken,
        string? scriptBaseDirectory = null,
        LiveEventPump? livePump = null)
    {
        // isolation.BeginScenarioAsync is called by the suite loop (or is a no-op for RunAsync).
        _ = isolation;

        // ── Stage service URLs and dependency connection strings ──────────────
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in suite.DiscoveredServices)
        {
            var varKey = suite.DependencyNames.Contains(kv.Key, StringComparer.Ordinal)
                ? VarKeys.Connection(kv.Key)
                : VarKeys.Service(kv.Key);
            vars[varKey] = kv.Value;
        }

        // ── Host resources (S07-F-01a, §5) ────────────────────────────────────
        // BEFORE staging the `variables` block and BEFORE running any step, start each
        // host-side resource declared by a provider (e.g. an ephemeral webhook listener)
        // IN THE DEFAULT ALC, owned here by the runner.  Stage each listener's bound URL
        // at svc::<VarName> so it is available before step 1 — an EARLIER step can hand
        // that URL to the SUT (forward-only Vars threading preserved) — and register its
        // buffer so a LATER assertion step can read captures via globals.Webhooks.
        //
        // Off the hot path: when no step contributes a host resource, no listener starts
        // and Webhooks stays the Null accessor.  Listeners are disposed in the finally
        // below; because each scenario run starts FRESH listeners+buffers and disposes
        // them at the end, a webhook captured in scenario A can never satisfy an assertion
        // in scenario B within a shared topology (strictly stronger than a buffer clear).
        var listeners = new List<WebhookListener>();
        var otlpReceivers = new List<OtlpReceiver>();
        IWebhookCaptureAccessor webhookAccessor = NullWebhookCaptureAccessor.Instance;
        ITraceCaptureAccessor traceAccessor = NullTraceCaptureAccessor.Instance;
        try
        {
            if (hostResourcePlan.Count > 0)
            {
                var buffers = new Dictionary<string, WebhookCaptureBuffer>(StringComparer.Ordinal);
                var traceBuffers = new Dictionary<string, TraceCaptureBuffer>(StringComparer.Ordinal);
                // De-duplicate by VarName: many steps may reference the same logical listener.
                foreach (var entry in hostResourcePlan)
                {
                    var req = entry.Requirement;

                    if (string.Equals(req.Kind, OtlpReceiverKind, StringComparison.Ordinal))
                    {
                        if (traceBuffers.ContainsKey(req.VarName))
                        {
                            continue;
                        }

                        var traceBuffer = new TraceCaptureBuffer();
                        var receiver = await OtlpReceiver
                            .StartAsync(traceBuffer, cancellationToken)
                            .ConfigureAwait(false);
                        otlpReceivers.Add(receiver);
                        traceBuffers[req.VarName] = traceBuffer;

                        // Stage the receiver's base URL under the SAME two keys the webhook
                        // listener uses below (svc::<VarName> for target:-style resolution and
                        // the plain <VarName> for {placeholder} substitution), so a suite
                        // author can hand it straight to environment.services[].env, e.g.
                        // OTEL_EXPORTER_OTLP_ENDPOINT: "{svc::traces}" (the OTel SDK appends
                        // "/v1/traces" to this base URL itself — see OtlpReceiver.Url remarks).
                        vars[VarKeys.Service(req.VarName)] = receiver.Url;
                        vars[req.VarName] = receiver.Url;

                        // Container-reachable alias, mirroring the webhook listener exactly —
                        // the receiver binds 0.0.0.0 for the same host.docker.internal reason.
                        vars[req.VarName + ContainerVarSuffix] = RewriteHostForContainer(receiver.Url);
                        continue;
                    }

                    if (!string.Equals(req.Kind, WebhookListenerKind, StringComparison.Ordinal))
                    {
                        // Unknown host-resource kind: ignore tolerantly (a future kind may be
                        // handled by a later sprint's runner without breaking this one).
                        continue;
                    }

                    if (buffers.ContainsKey(req.VarName))
                    {
                        continue;
                    }

                    var capBuffer = new WebhookCaptureBuffer();
                    var listener = await WebhookListener
                        .StartAsync(capBuffer, cancellationToken)
                        .ConfigureAwait(false);
                    listeners.Add(listener);
                    buffers[req.VarName] = capBuffer;

                    // Stage the listener URL under TWO keys BEFORE step 1, so an EARLIER
                    // step can hand the callback URL to the SUT via either access path:
                    //   • svc::<VarName>  — the discovered-service slot an http.rest step
                    //     reaches via target:, identical to any orchestrated endpoint.
                    //   • <VarName>       — a PLAIN Vars entry so {placeholder} substitution
                    //     can reach it: an author writes {<listener>} in a request body/field
                    //     to interpolate the callback URL.  {placeholder} substitution scans
                    //     bare identifiers ([A-Za-z_]…) only and CANNOT reach a svc:: key, so
                    //     this second staging is what makes the URL author-interpolable.
                    // Both point at the same listener.Url.  The plain key is staged here,
                    // before the `variables` block, so an author-declared variable of the
                    // same name (rare) deliberately overrides it (forward-only Vars threading).
                    vars[VarKeys.Service(req.VarName)] = listener.Url;
                    vars[req.VarName] = listener.Url;

                    // SUT configuration surface (point 3): ALSO stage a container-reachable
                    // form of the SAME callback URL, with the loopback host rewritten to
                    // host.docker.internal, so a suite author can hand {<listener>_container} to
                    // a containerised SUT in a request body. The listener binds 0.0.0.0 (see the
                    // WebhookListener remarks), so host.docker.internal:<port> reaches it from
                    // inside the Aspire-managed Docker network on both Docker Desktop and (via
                    // the '--add-host' runtime arg EnvironmentMapper adds to every image-form
                    // service) plain Linux Docker Engine CI runners. A VarName that would
                    // collide with this synthesised suffix (an author names one listener "x"
                    // and another "x_container") is rejected earlier, in
                    // ProviderPipeline.Compile, before the topology is even built — so this
                    // assignment can never silently overwrite an unrelated listener's URL.
                    vars[req.VarName + ContainerVarSuffix] = RewriteHostForContainer(listener.Url);
                }

                if (buffers.Count > 0)
                {
                    webhookAccessor = new WebhookCaptureAccessor(buffers);
                }

                if (traceBuffers.Count > 0)
                {
                    traceAccessor = new TraceCaptureAccessor(traceBuffers);
                }
            }

            return await RunScenarioCoreAsync(
                ast,
                scenarioName,
                runId,
                suite,
                assembled,
                compileReferencePaths,
                vars,
                webhookAccessor,
                traceAccessor,
                buffer,
                output,
                seedBaseDirectory,
                cancellationToken,
                scriptBaseDirectory: scriptBaseDirectory,
                livePump: livePump).ConfigureAwait(false);
        }
        finally
        {
            // Dispose every started listener (stops Kestrel, releases the port) regardless of
            // the scenario's verdict or any thrown exception.  Disposal of fresh per-scenario
            // listeners is the per-scenario teardown that isolates scenarios in a shared topology.
            foreach (var listener in listeners)
            {
                try
                {
                    await listener.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort teardown: a failed listener dispose must not mask the verdict.
                }
            }

            // Same best-effort per-scenario teardown for every started OTLP receiver (Phase C).
            foreach (var receiver in otlpReceivers)
            {
                try
                {
                    await receiver.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort teardown: a failed receiver dispose must not mask the verdict.
                }
            }
        }
    }

    // The host-resource kinds handled by this runner (S07-F-01a; otlp-receiver added Phase C).
    // internal (not private): ProviderPipeline.Compile references these constants to detect a
    // listener/receiver VarName collision (see ContainerVarSuffix below) before the topology is
    // built.
    internal const string WebhookListenerKind = "webhook-listener";

    /// <summary>
    /// The host-resource kind for the ephemeral OTLP/HTTP receiver backing
    /// <c>trace-expect.otlp</c> (Phase C). Mirrors <see cref="WebhookListenerKind"/> exactly:
    /// staged at <c>svc::&lt;VarName&gt;</c> / <c>Vars[&lt;VarName&gt;]</c> /
    /// <c>Vars[&lt;VarName&gt;_container]</c> before step 1, started in the Default ALC, and
    /// disposed per-scenario in the <c>finally</c> above.
    /// </summary>
    internal const string OtlpReceiverKind = "otlp-receiver";

    /// <summary>
    /// The suffix appended to a webhook listener's <c>VarName</c> to stage the
    /// container-reachable form of its callback URL (SUT configuration surface, point 3).
    /// <see cref="ProviderPipeline"/> rejects any suite whose listener <c>VarName</c>s would
    /// make two DISTINCT listeners collide on this suffix before the topology is built.
    /// </summary>
    internal const string ContainerVarSuffix = "_container";

    /// <summary>
    /// Rewrites the host of a loopback webhook listener URL (<c>127.0.0.1</c>/<c>localhost</c>)
    /// to <c>host.docker.internal</c>, leaving the port and the unguessable token path segment
    /// untouched, so a containerised SUT can reach the host-owned listener from inside the
    /// Aspire-managed Docker network.
    /// </summary>
    private static string RewriteHostForContainer(string url)
    {
        var builder = new UriBuilder(url) { Host = "host.docker.internal" };
        return builder.Uri.ToString();
    }

    /// <summary>
    /// Executes the per-scenario compilation + isolated Roslyn run against an already-started
    /// topology, with the host-resource staging already applied to <paramref name="vars"/> and
    /// the webhook accessor already built.  Extracted from
    /// <see cref="RunScenarioAgainstTopologyAsync"/> so the host-listener lifecycle (start /
    /// stage / dispose) wraps this body in a single try/finally without duplicating the many
    /// early-return event-emission paths (S07-F-01a).
    /// </summary>
    /// <param name="scriptBaseDirectory">
    /// Per-scenario <c>script.csharp</c> <c>file:</c> digest base directory (issue #268) — see
    /// <see cref="RunScenarioAgainstTopologyAsync"/>'s parameter of the same name. Threaded
    /// through unchanged to <see cref="BuildReproducibilityEnvelope"/>.
    /// </param>
    private static async Task<Verdict> RunScenarioCoreAsync(
        ScenarioAst ast,
        string scenarioName,
        string runId,
        SuiteTopology suite,
        AssembledScript assembled,
        IReadOnlyList<string> compileReferencePaths,
        Dictionary<string, object?> vars,
        IWebhookCaptureAccessor webhookAccessor,
        ITraceCaptureAccessor traceAccessor,
        List<string> buffer,
        TextWriter output,
        string? seedBaseDirectory,
        CancellationToken cancellationToken,
        string? scriptBaseDirectory = null,
        LiveEventPump? livePump = null)
    {
        // ── Issue #262: live scenario-started signal ──────────────────────────
        // Posted immediately, before anything else, using its OWN real-time timestamp —
        // entirely separate from the batch `now9`-stamped copy the success path below still
        // adds to `buffer` for the end-of-run archive (unaffected, byte-identical to
        // pre-#262 whether or not a live pump is attached).  A null livePump makes this a
        // no-op; every early-exit path below reports its own scenario-completed line to the
        // SAME pump (see the two catch blocks), so a live tail never sees an orphaned
        // scenario-started with no matching completion.
        livePump?.Post(StepEventBuilder.ScenarioStartedLine(runId, DateTimeOffset.UtcNow, scenarioName));

        // ── Stage the `variables` block constants (DSL §3) ────────────────────
        // Pre-loaded into the shared context under their bare names (no prefix) so
        // {placeholder} substitution and capture reads resolve them uniformly.
        // Staged before execution as the baseline; a capture writing the same name
        // later in the run legitimately overrides the constant.
        foreach (var kv in ast.Variables)
        {
            vars[kv.Key] = kv.Value;
        }

        // ── Secret subsystem (§17, S05-B-02 / S08-B-01) ───────────────────────
        // Build the catalog + accessor here, in the Default ALC, and pass them into
        // the boundary BY REFERENCE.  No static handle bridges the boundary — the
        // accessor is an instance the script reaches only via globals.Secrets.
        // Resolution happens at step-execution time inside the emitted CSX, never at
        // compile time, so no secret value is ever baked into the emitted IL.
        //
        // Some resolvers own disposable state (the Vault resolver's client owns an
        // HttpClient).  Retain the array so the finally below disposes any IDisposable
        // resolver at scenario end — no HttpClient leaks across the per-scenario
        // boundary, and no static handle holds the connection open.
        var secretResolvers = BuildSecretResolvers();
        var secretCatalog = new SecretSourceCatalog(secretResolvers);
        var secretAccessor = new SecretAccessor(secretCatalog);
        try
        {
            // Built once, up front (moved ahead of its former use inside the step loop below)
            // because the live sink needs it at construction time, issue #262: the map of
            // captured varName → declaring stepId is a pure compile-time derivation over
            // ast.Steps, so hoisting it here changes nothing about what it computes.
            var captureOriginMap = BuildCaptureOriginMap(ast.Steps);

            // ── §5 boundary construction (S07-F-01a; traceAccessor added Phase C; StepEvents
            // sink added issue #262) ───────────────────────────────────────────────────────
            // The secret accessor, the webhook-capture accessor, the OTLP trace-capture
            // accessor, and (when a live pump is attached) the step-event sink are all
            // instances built in the Default ALC and passed by-reference into the sole
            // host↔script boundary.  The webhook listener / OTLP receiver + buffers they
            // project live in the Default ALC (owned by this runner); the emitted script
            // reaches captures ONLY via globals.Webhooks / globals.Traces / globals.StepEvents
            // — no static handle bridges the collectible boundary, preserving the memory model.
            // A null livePump (no --events-stream conduit) yields a null StepEvents, which
            // every emitted `?.` call short-circuits — the run is behaviourally IDENTICAL to
            // pre-#262 in that case.
            IStepEventSink? liveSink = livePump is null
                ? null
                : new LiveStepEventSink(livePump, runId, ast.Steps, captureOriginMap, secretAccessor);

            var globals = new ScriptGlobalVariables(
                vars, suite.DiscoveredServices, secretAccessor, webhookAccessor, traceAccessor, liveSink);

            // ── Compile-once + RunIsolatedAsync ───────────────────────────────────
            var tpaPaths = BclReferencePaths()
                .Concat(compileReferencePaths)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            CompiledScript compiled;
            try
            {
                // Bound the compile-once call to a fresh token LINKED to this scenario's own
                // cancellationToken — cancelling the run cancels the compile immediately —
                // and ALSO time-boxed to RoslynScriptCompiler.DefaultCompileBudget, so a
                // slow-but-legitimate compile cannot silently consume the run's whole
                // budget. Scoped to a `using` block around ONLY this call (compiled once,
                // above), so its timer cannot fire during the later RunIsolatedAsync
                // execution below.
                //
                // Partial mitigation only (see RoslynScriptCompiler.CompileOnce's
                // cancellationToken remarks for the full picture): this token is consulted
                // during Emit only. It does NOT help a hostile body that HANGS inside the
                // earlier GetCompilation call (unbounded — that call simply never returns,
                // so CompileOnce never reaches Emit at all), and it CANNOT intercept a
                // hostile body that overflows the native stack during Emit itself
                // (uncatchable). ScriptCsharpProvider.Validate applies only a plain 64 KiB
                // size bound before this method is ever reached — a resource limit, not a
                // guard against either failure mode. Whatever THIS budget's own expiry does
                // produce (OperationCanceledException, for the in-between case of a
                // legitimately slow compile) is handled uniformly by the generic
                // `catch (Exception ex)` below, exactly like any other compile failure — no
                // special-casing needed.
                using (var compileBudget =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    compileBudget.CancelAfter(RoslynScriptCompiler.DefaultCompileBudget);
                    compiled = RoslynScriptCompiler.CompileOnce(
                        assembled.CsxSource,
                        additionalOptions: null,
                        additionalReferencePaths: tpaPaths,
                        cancellationToken: compileBudget.Token);
                }

                await RoslynScriptCompiler.RunIsolatedAsync(
                    compiled,
                    globals,
                    runLabel: scenarioName,
                    cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SecretResolutionException sre)
            {
                // Defence-in-depth backstop (§17): every Core provider already guards its
                // own secret resolution and maps a failure to a per-step EnvironmentError.
                // This catch only fires if a FUTURE provider forgets that guard and lets a
                // SecretResolutionException escape the Roslyn submission delegate. A secret
                // that cannot be resolved is an environment/configuration problem, NOT a
                // product defect — so we surface it as a scenario-level EnvironmentError
                // (consistent with the verdict taxonomy §12.1; EnvironmentError is already a
                // first-class scenario verdict used by the topology-start path above).
                // REFERENCE-ONLY: only the source/path coordinates are written to output —
                // never sre.Message verbatim (here Message carries only the path, but we keep
                // the surface reference-only for consistency and future-proofing, §17).
                //
                // Issue #266, Item 4: SecretSource is restricted to [A-Za-z0-9_-]+ (safe), but
                // SecretPath's grammar is [^}]+ — ANY character except '}', including control
                // characters and ANSI escape sequences an author's `${secret:source/path}`
                // field value could embed — so it is sanitised the same as every other
                // author-controlled text reaching this human output stream.
                var nowSE = DateTimeOffset.UtcNow;
                buffer.Add(StepEventBuilder.ScenarioStartedLine(runId, nowSE, scenarioName));
                var seCompletedLine = StepEventBuilder.ScenarioCompletedLine(
                    runId, nowSE, scenarioName, Verdict.EnvironmentError, new VerdictCounts { EnvError = 1 });
                buffer.Add(seCompletedLine);
                // Issue #262: the matching scenario-started was already posted live at the top
                // of this method; post the completion now so a live tail never sees an orphan.
                livePump?.Post(seCompletedLine);

                await output.WriteLineAsync(
                    DisplaySanitiser.SanitiseForDisplay(
                        "Secret resolution failed (EnvironmentError): " +
                        $"source '{sre.SecretSource}', path '{sre.SecretPath}'."))
                    .ConfigureAwait(false);

                return Verdict.EnvironmentError;
            }
            catch (Exception ex)
            {
                var nowCE = DateTimeOffset.UtcNow;
                buffer.Add(StepEventBuilder.ScenarioStartedLine(runId, nowCE, scenarioName));
                var ceCompletedLine = StepEventBuilder.ScenarioCompletedLine(
                    runId, nowCE, scenarioName, Verdict.Inconclusive, new VerdictCounts { Inconclusive = 1 });
                buffer.Add(ceCompletedLine);
                livePump?.Post(ceCompletedLine);

                var diagnosis = ex is ScriptCompilationException sce
                    ? $"CSX compilation failed: {sce.Message}"
                    : $"{ex.GetType().Name}: {ex.Message}";

                // §17 defence-in-depth (S11-B-01): a secret value resolved during execution
                // can land verbatim in an exception MESSAGE (e.g. the script.csharp body throws
                // with an interpolated Reveal()).  This diagnostic is written to the HUMAN output
                // stream (the developer terminal / CI log) — an exfiltration surface every bit as
                // real as the event stream — so scrub it through the SAME ledger the observation
                // path uses before it leaves the engine.  Type-based redaction stays primary.
                //
                // Issue #266, Item 4: composed with DisplaySanitiser.SanitiseForDisplay so BOTH
                // nets run on this write — ScrubDiagnostic redacts resolved secret VALUES first,
                // then SanitiseForDisplay strips control characters / neutralises ANSI escape
                // sequences the (already-scrubbed) text might still carry (e.g. from a hostile
                // step id or an author exception message), before either ever reaches the
                // terminal/CI log.
                await output.WriteLineAsync(
                    $"Compile/run error (Inconclusive): " +
                    $"{DisplaySanitiser.SanitiseForDisplay(ScrubDiagnostic(secretAccessor, diagnosis))}")
                    .ConfigureAwait(false);

                return Verdict.Inconclusive;
            }

            // ── Emit events from outcomes + aggregate verdict ─────────────────────
            var now9 = DateTimeOffset.UtcNow;
            buffer.Add(StepEventBuilder.ScenarioStartedLine(runId, now9, scenarioName));

            var aggregate = Verdict.Pass;
            var counts = new int[4];

            // NOTE: captureOriginMap (varName → declaring stepId, G-01 provenance) was already
            // built ABOVE, before globals construction, so the live sink and this
            // reconstruction loop share the identical map (issue #262).

            foreach (var node in ast.Steps)
            {
                var safeId = CsxFragment.SanitiseId(node.Id);

                // Issue #262: StepEventBuilder.StepStartedLine is the SAME method
                // LiveStepEventSink.OnStepStarted calls in real time — this reconstruction
                // call is unchanged in effect from the pre-#262 inline construction.
                buffer.Add(StepEventBuilder.StepStartedLine(runId, now9, node));

                var outcomeKey = VarKeys.Outcome(safeId);
                var outcome = vars.TryGetValue(outcomeKey, out var raw)
                    ? raw as StepOutcome
                    : null;

                // Read the matched-flag string written by the emitted block ("1,0,1" — one
                // flag per capture in declaration order); StepEventBuilder.StepCompletedLine
                // ignores this when node.Capture is empty, matching the pre-#262 behaviour.
                string? captureStatusRaw = null;
                if (vars.TryGetValue(VarKeys.CaptureStatus(safeId), out var csRaw)
                    && csRaw is string csStr)
                {
                    captureStatusRaw = csStr;
                }

                // ── RETRY (Sprint 6): one step-attempt event per recorded poll ────
                // The engine-owned RETRY runner writes a List<AttemptRecord> to
                // Vars[VarKeys.Attempts(safeId)]; emit one step-attempt event per
                // record so the polling timeline is renderable offline (§14).  An
                // IMMEDIATE step writes no attempts list, so this is a no-op for it.
                buffer.AddRange(BuildAttemptEventLines(runId, now9, node.Id, vars, secretAccessor));

                // Issue #262: StepEventBuilder.StepCompletedLine is the SAME method
                // LiveStepEventSink.OnStepCompleted calls in real time — this call reproduces,
                // byte-for-byte, the pre-#262 inline Captured/Substitutions/Observation
                // construction.
                var stepCompletedLine = StepEventBuilder.StepCompletedLine(
                    runId, now9, node, outcome, captureStatusRaw, captureOriginMap, secretAccessor);
                buffer.Add(stepCompletedLine);

                var stepVerdict = outcome?.Verdict ?? Verdict.Inconclusive;
                counts[(int)stepVerdict]++;
                aggregate = Elevate(aggregate, stepVerdict);
            }

            var finalCounts = new VerdictCounts
            {
                Pass = counts[(int)Verdict.Pass],
                Fail = counts[(int)Verdict.Fail],
                EnvError = counts[(int)Verdict.EnvironmentError],
                Inconclusive = counts[(int)Verdict.Inconclusive],
            };

            // ── Reproducibility envelope (§17, docs/02 §3.2.2, S05-B-03) ──────────
            // Emitted once per scenario, alongside ScenarioCompletedEvent.  Built from
            // reference text + fixture content ONLY — the secret resolver is never
            // invoked here, so by construction no resolved secret value can enter the
            // envelope.  Reuses SeedFixtures.ComputeContentHash for fixture digests.
            // seedBaseDirectory roots the seed-fixture digests (the shared topology's single
            // seed root, unchanged); scriptBaseDirectory roots THIS scenario's own
            // script.csharp file: digest (issue #268) — falls back to seedBaseDirectory when
            // null (every single-scenario caller).
            var envelope = BuildReproducibilityEnvelope(ast, seedBaseDirectory, scriptBaseDirectory);
            var reproLine = StepEventBuilder.ReproducibilityLine(
                runId, DateTimeOffset.UtcNow, scenarioName, envelope);
            buffer.Add(reproLine);

            var scenarioCompletedLine = StepEventBuilder.ScenarioCompletedLine(
                runId, DateTimeOffset.UtcNow, scenarioName, aggregate, finalCounts);
            buffer.Add(scenarioCompletedLine);

            // Issue #262: stream the scenario-completed + reproducibility-envelope framing
            // live too, for a coherent tail (INCLUDED per the #262 design decision) — posted
            // using the EXACT SAME line strings just added to `buffer`, so there is no
            // possibility of live/archive drift for these two framing lines. Every per-step
            // line above was ALREADY streamed live (if a sink was attached) as the run
            // progressed, via LiveStepEventSink; this is only the end-of-scenario framing.
            livePump?.Post(reproLine);
            livePump?.Post(scenarioCompletedLine);

            return aggregate;
        }
        finally
        {
            // Dispose any resolver that owns disposable state (the Vault resolver's
            // client owns an HttpClient).  Runs on EVERY exit path — normal completion,
            // the EnvironmentError/Inconclusive early returns above, and any unexpected
            // throw — so no HttpClient leaks across the per-scenario boundary (§5).
            DisposeSecretResolvers(secretResolvers);
        }
    }

    /// <summary>
    /// Disposes any <see cref="IDisposable"/> resolvers in <paramref name="resolvers"/>
    /// (e.g. the Vault resolver's client owns an
    /// <see cref="System.Net.Http.HttpClient"/>).  Stateless resolvers (such as the
    /// <c>env</c> resolver) are skipped.  Never throws into the verdict path.
    /// </summary>
    private static void DisposeSecretResolvers(IReadOnlyList<ISecretResolver> resolvers)
    {
        foreach (var resolver in resolvers)
        {
            if (resolver is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    /// <summary>
    /// Builds the appropriate <see cref="IScenarioIsolation"/> for the given
    /// <paramref name="topology"/> via <c>ScenarioIsolationFactory.Create</c>: proper
    /// name+type dispatch over <see cref="SuiteTopology.DependencyNames"/>,
    /// <see cref="SuiteTopology.DependencyTypes"/>, and
    /// <see cref="SuiteTopology.DiscoveredServices"/> — resetting EVERY resettable
    /// dependency the topology declares (composed when there is more than one),
    /// rather than sniffing the shape of a discovered connection string.
    /// </summary>
    private static IScenarioIsolation BuildIsolation(SuiteTopology topology) =>
        ScenarioIsolationFactory.Create(
            topology.DependencyNames,
            topology.DependencyTypes,
            topology.DiscoveredServices);

    /// <summary>
    /// <see cref="JsonSerializerOptions"/> used by <see cref="SerialiseEnvironment"/>.
    /// Allocated once (static initialiser) to avoid repeated reflection overhead.
    /// Registers <see cref="YamlNodeJsonConverter"/> so that a
    /// <see cref="DependencySpec.Extra"/> field (type <see cref="YamlMappingNode"/>)
    /// is serialised to a deterministic JSON object rather than throwing
    /// <see cref="InvalidOperationException"/> (S11-B-02).
    /// </summary>
    private static readonly JsonSerializerOptions s_envSerialiserOptions =
        new() { Converters = { new YamlNodeJsonConverter() } };

    /// <summary>
    /// Serialises an <see cref="EnvironmentSpec"/> to a stable JSON string for
    /// equality comparison across suite scenarios (shared-environment validation).
    /// Returns an empty string for a <see langword="null"/> environment.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="s_envSerialiserOptions"/> which registers
    /// <see cref="YamlNodeJsonConverter"/> — required because
    /// <see cref="DependencySpec.Extra"/> is a <see cref="YamlMappingNode"/> that
    /// <see cref="JsonSerializer"/> cannot handle without a custom converter (S11-B-02).
    /// Mapping keys within each <see cref="DependencySpec.Extra"/> block are emitted in
    /// ordinal sort order, so two Extra mappings that contain the same pairs in different
    /// declaration order produce identical JSON.  Top-level <c>Services</c> and
    /// <c>Dependencies</c> collections are serialised by STJ in enumeration order and
    /// retain their YAML declaration order (which is stable: it comes from the same parsed
    /// YAML).
    /// </remarks>
    private static string SerialiseEnvironment(EnvironmentSpec? env) =>
        env is null
            ? string.Empty
            : JsonSerializer.Serialize(env, s_envSerialiserOptions);

    // ── Render-time diff lookup (S07-G-01) ──────────────────────────────────────

    /// <summary>
    /// Builds the render-time diff-lookup closure passed to
    /// <see cref="TerminalRenderer.Render(IEnumerable{string}, TextWriter, Func{string, JsonElement, string?}?)"/>.
    /// </summary>
    /// <param name="registry">The frozen provider registry to resolve the kind against.</param>
    /// <returns>
    /// A delegate that, given a step <c>kind</c> and structured observation, resolves
    /// the provider for that kind and — when it implements
    /// <see cref="IStepDiffRenderer"/> and recognises the observation — returns its
    /// rendered expected-vs-observed diff, or <see langword="null"/> otherwise.
    /// </returns>
    /// <remarks>
    /// The diff renderer runs in the Default <c>AssemblyLoadContext</c> (the provider
    /// instance is held by the frozen registry), so this raises no §5 memory-model
    /// concern.  The closure is the sole bridge between the decoupled
    /// <c>Vouchfx.Engine.Reporting</c> layer (which knows only <see cref="Func{T1, T2, TResult}"/>)
    /// and the <c>IStepDiffRenderer</c> SDK type.
    /// </remarks>
    private static Func<string, JsonElement, string?> BuildDiffLookup(StepKindRegistry registry)
        => (kind, observation) =>
            registry.TryGet(kind, out var rp)
            && rp?.Instance is IStepDiffRenderer renderer
            && renderer.CanRender(observation)
                ? renderer.RenderDiff(observation)
                : null;

    /// <summary>
    /// Exposes <see cref="BuildDiffLookup"/> to <see cref="ParallelSuiteRunner"/> (same assembly)
    /// so the parallel runner builds the identical render-time diff-lookup closure this runner
    /// uses — the two cannot drift in how a failed step's expected-vs-observed diff is resolved.
    /// </summary>
    internal static Func<string, JsonElement, string?> BuildParallelDiffLookup(
        StepKindRegistry registry) => BuildDiffLookup(registry);

    // ── Verdict aggregation ────────────────────────────────────────────────────

    /// <summary>
    /// Elevates <paramref name="current"/> when <paramref name="next"/> has
    /// higher precedence.  Precedence (highest first):
    /// <c>EnvironmentError &gt; Fail &gt; Inconclusive &gt; Pass</c>.
    /// </summary>
    internal static Verdict Elevate(Verdict current, Verdict next) =>
        VerdictPrecedence(next) > VerdictPrecedence(current) ? next : current;

    internal static int VerdictPrecedence(Verdict v) => v switch
    {
        Verdict.Pass => 0,
        Verdict.Inconclusive => 1,
        Verdict.Fail => 2,
        Verdict.EnvironmentError => 3,
        _ => 0,
    };

    // ── Full TPA reference list for compile ───────────────────────────────────

    /// <summary>
    /// Builds a map from variable name to the step identifier that first declares
    /// that variable in its <c>capture</c> block.  Used for G-01 provenance
    /// (substitution origin tracing).
    /// </summary>
    /// <param name="steps">All steps in the scenario, in declaration order.</param>
    /// <returns>
    /// A dictionary mapping each captured variable name to the <c>id</c> of the
    /// step that declares it.  When the same name is declared by multiple steps
    /// (overwriting), the first declaration wins (it is the origin).
    /// </returns>
    internal static Dictionary<string, string> BuildCaptureOriginMap(
        IReadOnlyList<StepNode> steps)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            foreach (var varName in step.Capture.Keys)
            {
                if (!map.ContainsKey(varName))
                    map[varName] = step.Id;
            }
        }
        return map;
    }

    /// <summary>
    /// Builds the ordered <c>step-attempt</c> event lines for a single step from
    /// the per-step <see cref="AttemptRecord"/> list written by the engine-owned
    /// RETRY runner (§7, §14).
    /// </summary>
    /// <param name="runId">The run identifier stamped onto every emitted event.</param>
    /// <param name="timestamp">
    /// The timestamp stamped onto every emitted event (the runner emits the whole
    /// step batch with one shared timestamp).
    /// </param>
    /// <param name="stepId">
    /// The author-facing (un-sanitised) step identifier; sanitised internally to
    /// form the <c>Vars</c> lookup key and emitted verbatim on the wire.
    /// </param>
    /// <param name="vars">
    /// The shared <c>ScriptGlobalVariables.Vars</c> dictionary after the isolated
    /// run, read for <c>VarKeys.Attempts(safeId)</c>.
    /// </param>
    /// <returns>
    /// One JSON Lines <c>step-attempt</c> string per <see cref="AttemptRecord"/>,
    /// in list order; an empty list when the step recorded no attempts (e.g. an
    /// IMMEDIATE step, which writes no attempts list).
    /// </returns>
    /// <remarks>
    /// Extracted as an <see langword="internal static"/> helper so the no-docker
    /// RETRY-event tests can exercise the attempt-event construction directly,
    /// without standing up a topology.  The observation string recorded by the
    /// RETRY runner is parsed via <see cref="ParseObservation"/> into a
    /// <see cref="JsonElement"/> so the wire field is a structured object rather
    /// than an escaped string; an unparseable observation degrades to omission
    /// rather than crashing the run.
    /// </remarks>
    internal static IReadOnlyList<string> BuildAttemptEventLines(
        string runId,
        DateTimeOffset timestamp,
        string stepId,
        IReadOnlyDictionary<string, object?> vars,
        ISecretAccessor? secretAccessor = null)
    {
        var safeId = CsxFragment.SanitiseId(stepId);

        if (!vars.TryGetValue(VarKeys.Attempts(safeId), out var raw)
            || raw is not List<AttemptRecord> attempts)
        {
            return Array.Empty<string>();
        }

        // Issue #262: delegate line construction to the shared StepEventBuilder — the SAME
        // method LiveStepEventSink.OnStepAttempt calls in real time, per attempt, from inside
        // the isolated run.  This is the parity guarantee: both paths produce byte-identical
        // lines (modulo `ts`) because both call this one builder.
        var lines = new List<string>(attempts.Count);
        foreach (var a in attempts)
        {
            lines.Add(StepEventBuilder.StepAttemptLine(runId, timestamp, stepId, a, secretAccessor));
        }

        return lines;
    }

    /// <summary>
    /// Parses a RETRY-runner observation string into a <see cref="JsonElement"/>
    /// for inclusion in a <see cref="StepAttemptEvent.Observation"/>.
    /// </summary>
    /// <param name="json">
    /// The small JSON string recorded by the RETRY runner for an attempt (e.g.
    /// <c>{"matched":false}</c>), or <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A cloned <see cref="JsonElement"/> when <paramref name="json"/> is non-empty
    /// and parses as JSON; otherwise <see langword="null"/> (for a null/empty input
    /// or a parse failure — the observation is best-effort diagnostic context and
    /// must never crash event emission).
    /// </returns>
    internal static JsonElement? ParseObservation(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the structured <see cref="StepCompletedEvent.Observation"/> from a provider's
    /// raw observation text, applying the §17 defence-in-depth scrub net (S11-B-01) BEFORE
    /// parsing: any verbatim occurrence of a value the run's <paramref name="accessor"/>
    /// revealed is replaced with the redaction marker.
    /// </summary>
    /// <param name="accessor">
    /// The Default-ALC secret accessor for this scenario.  When it is a
    /// <see cref="SecretAccessor"/>, its <see cref="SecretAccessor.ResolvedSecrets"/> ledger
    /// supplies the values to scrub; any other <see cref="ISecretAccessor"/> (e.g. the Null
    /// accessor) resolves nothing, so there is nothing to scrub and the text is parsed as-is.
    /// </param>
    /// <param name="rawObservation">
    /// The provider's raw observation string — for <c>script.csharp</c> this can be a thrown
    /// exception's message spliced verbatim (<c>__obs = __ex.Message;</c>), the one
    /// event-stream surface the engine cannot type-check.  May be <see langword="null"/>.
    /// </param>
    /// <returns>
    /// The parsed, scrubbed observation as a <see cref="JsonElement"/>, or
    /// <see langword="null"/> when the input is null/empty or does not parse as JSON
    /// (an unparseable observation degrades to omission rather than crashing).
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Why a scrub here, not deeper.</strong>  Every STRUCTURED event surface
    /// (verdicts, captured-var provenance, substitution refs, the reproducibility envelope)
    /// is already redacted by construction — it carries only the <see cref="SecretString"/>
    /// carrier or non-sensitive metadata.  The observation is the lone exception: it is
    /// free-form text a provider builds, so it cannot be type-checked.  The scrub is a
    /// targeted net over THAT text only; type-based redaction remains the primary mechanism.
    /// </para>
    /// <para>
    /// <strong>What it deliberately does NOT do.</strong>  It does not chase TRANSFORMS of a
    /// revealed value (base64, an HMAC signature, a substring): those arise only from a
    /// deliberate <see cref="SecretString.Reveal"/> followed by author code reshaping the
    /// bytes — the documented, auditable escape hatch (§17) and the author's responsibility.
    /// The net catches the realistic accident (a revealed value appearing verbatim in an
    /// exception message), not every theoretical encoding.
    /// </para>
    /// </remarks>
    internal static JsonElement? BuildStepObservation(ISecretAccessor accessor, string? rawObservation)
        => ParseObservation(ScrubDiagnostic(accessor, rawObservation));

    /// <summary>
    /// Applies the §17 defence-in-depth scrub net (S11-B-01) to a free-form diagnostic /
    /// observation <paramref name="text"/>: every verbatim occurrence of a value the run's
    /// <paramref name="accessor"/> revealed is replaced with the redaction marker.  This is
    /// the SINGLE scrub call shared by both the step-observation path
    /// (<see cref="BuildStepObservation"/>) and the scenario-level diagnostic writes to the
    /// human <c>output</c> stream (the CSX-compile / unexpected-exception catch sites in
    /// <see cref="RunScenarioCoreAsync"/>), so a secret that surfaces in an exception message
    /// cannot reach the developer's terminal / CI log any more than it can reach the event
    /// stream.
    /// </summary>
    /// <param name="accessor">
    /// The Default-ALC secret accessor for this scenario.  When it is a
    /// <see cref="SecretAccessor"/>, its <see cref="SecretAccessor.ResolvedSecrets"/> ledger
    /// supplies the values to scrub; any other <see cref="ISecretAccessor"/> (e.g. the Null
    /// accessor) resolves nothing, so there is nothing to scrub and the text is returned
    /// unchanged.
    /// </param>
    /// <param name="text">The free-form text to scrub.  May be <see langword="null"/>.</param>
    /// <returns>
    /// The scrubbed text, or the original reference unchanged when no recorded value occurs in
    /// it (the scrub is a targeted net, never a blanket rewrite).  <see langword="null"/> in,
    /// <see langword="null"/> out.
    /// </returns>
    /// <remarks>
    /// Type-based <see cref="SecretString"/> redaction remains the PRIMARY mechanism; this is
    /// the backstop for the one free-form surface the engine cannot type-check.  It does not
    /// chase TRANSFORMS of a revealed value (base64, an HMAC, a substring) — those arise only
    /// from a deliberate <see cref="SecretString.Reveal"/> followed by author code reshaping
    /// the bytes, the documented escape hatch and the author's responsibility (§17).
    /// </remarks>
    internal static string? ScrubDiagnostic(ISecretAccessor accessor, string? text)
        => accessor is SecretAccessor concrete
            ? concrete.ResolvedSecrets.Scrub(text)
            : text;

    /// <summary>
    /// Runs the central secret-reference validation pass over every substitutable
    /// field of every step in <paramref name="ast"/> (§17, S05-B-01).
    /// </summary>
    /// <param name="ast">The parsed scenario to validate.</param>
    /// <param name="error">
    /// On the first failure, an actionable British-English message naming the
    /// offending step and field problem; otherwise <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a validation error was found (the caller should
    /// short-circuit with <see cref="Verdict.Inconclusive"/>); <see langword="false"/>
    /// when every field's secret references are well-formed and use a known source.
    /// </returns>
    /// <remarks>
    /// This pass is engine-level and provider-uniform — it does not require any
    /// change to the frozen <c>IStepValidator&lt;T&gt;</c> interface. It reuses
    /// <see cref="CollectSubstitutableTexts"/> so the set of validated fields stays
    /// in lock-step with the set of fields the providers actually substitute.
    /// internal (not private, #260): this pass is entirely topology-free (it scans
    /// AST text only, never resolves a secret), so <see cref="ScenarioValidator"/>
    /// reuses it verbatim rather than duplicating the scan.
    /// </remarks>
    internal static bool TryValidateSecretReferences(ScenarioAst ast, out string? error)
    {
        foreach (var node in ast.Steps)
        {
            foreach (var text in CollectSubstitutableTexts(node))
            {
                if (!SecretReference.ValidateField(text, s_knownSecretSources, out var fieldError))
                {
                    error = $"step '{node.Id}': {fieldError}";
                    return true;
                }
            }
        }

        error = null;
        return false;
    }

    /// <summary>
    /// Derives the compile-time substitution provenance for a single step
    /// (S04-G-01 + S05-G-01) by scanning every substitutable field of
    /// <paramref name="node"/> for two distinct token kinds:
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>{placeholder}</c> tokens — each unique placeholder becomes a
    ///     <see cref="SubstitutionRef"/> with <see cref="SubstitutionRef.SecretDerived"/>
    ///     <see langword="false"/>.  Whether a placeholder's value <em>happens</em> to
    ///     have come from a secret is not determinable here in the general case, so we
    ///     never speculatively taint a plain placeholder — its origin (if any) is the
    ///     prior capture step recorded in <paramref name="captureOriginMap"/>.
    ///   </description></item>
    ///   <item><description>
    ///     <c>${secret:source/path}</c> references — each unique reference becomes a
    ///     <see cref="SubstitutionRef"/> with <see cref="SubstitutionRef.SecretDerived"/>
    ///     <see langword="true"/>, <see cref="SubstitutionRef.OriginStepId"/>
    ///     <see langword="null"/> (a secret does not originate from a prior capture),
    ///     and <see cref="SubstitutionRef.Placeholder"/> set to the non-sensitive
    ///     reference label <c>"{source}/{path}"</c> (e.g. <c>"env/API_TOKEN"</c>).
    ///   </description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the report-layer redaction hook (§17, docs/02 §14.5): the engine lights
    /// up <see cref="SubstitutionRef.SecretDerived"/> using only the secret
    /// <em>reference</em> (source/path), which is intentionally shown in reports — the
    /// resolved value is never read at compile time and never enters this record.
    /// </para>
    /// <para>
    /// Deduplication is per-step and per token kind: placeholders dedupe on the
    /// placeholder name, secret references dedupe on the reference label, and the two
    /// kinds never collide because their grammars cannot overlap (the <c>${secret:</c>
    /// sigil can never be produced by a bare <c>{name}</c> placeholder, S05-B-01).
    /// </para>
    /// <para>
    /// Extracted as an <see langword="internal"/> method so the no-docker provenance
    /// tests (S05-G-01) can exercise the <see cref="SubstitutionRef.SecretDerived"/>
    /// wiring directly, without standing up a topology.
    /// </para>
    /// </remarks>
    /// <param name="node">The step whose substitutable fields are scanned.</param>
    /// <param name="captureOriginMap">
    /// Map of captured variable name → originating step id, used to populate
    /// <see cref="SubstitutionRef.OriginStepId"/> for plain placeholders.
    /// </param>
    /// <returns>
    /// The list of substitution-provenance records for the step, or
    /// <see langword="null"/> when no substitutable field contains any placeholder or
    /// secret reference (so the wire field is omitted entirely).
    /// </returns>
    internal static IReadOnlyList<SubstitutionRef>? DeriveSubstitutionProvenance(
        StepNode node,
        IReadOnlyDictionary<string, string> captureOriginMap)
    {
        var substitutableTexts = CollectSubstitutableTexts(node);
        if (substitutableTexts.Count == 0)
            return null;

        var seenPlaceholders = new HashSet<string>(StringComparer.Ordinal);
        var seenSecretRefs = new HashSet<string>(StringComparer.Ordinal);
        var subs = new List<SubstitutionRef>();

        foreach (var text in substitutableTexts)
        {
            // 1. Plain {placeholder} tokens — never speculatively tainted as secret.
            foreach (System.Text.RegularExpressions.Match m in
                s_placeholderRegex.Matches(text))
            {
                var placeholder = m.Groups[1].Value;
                if (!seenPlaceholders.Add(placeholder))
                    continue; // deduplicate within step

                captureOriginMap.TryGetValue(placeholder, out var originStepId);
                subs.Add(new SubstitutionRef(
                    Placeholder: placeholder,
                    OriginStepId: originStepId,
                    SecretDerived: false));
            }

            // 2. ${secret:source/path} references — lit up as secret-derived using
            //    the non-sensitive reference label only (never the value, §17).
            foreach (var secretRef in SecretReference.FindAll(text))
            {
                var label = $"{secretRef.Source}/{secretRef.Path}";
                if (!seenSecretRefs.Add(label))
                    continue; // deduplicate within step

                subs.Add(new SubstitutionRef(
                    Placeholder: label,
                    OriginStepId: null,
                    SecretDerived: true));
            }
        }

        return subs.Count > 0 ? subs : null;
    }

    /// <summary>
    /// Assembles the reproducibility envelope for a scenario (§17, docs/02 §3.2.2,
    /// S05-B-03): the hash of every distinct secret <em>reference</em> across all
    /// steps' substitutable fields, plus the content hash of every applied seed
    /// fixture.
    /// </summary>
    /// <param name="ast">The scenario whose steps and seed block are scanned.</param>
    /// <param name="seedBaseDirectory">
    /// The base directory against which relative seed fixture paths are resolved
    /// (S05-A-01).  When <see langword="null"/>, the current working directory is
    /// used — matching <see cref="SuiteTopology.StartAsync"/>.
    /// </param>
    /// <param name="scriptBaseDirectory">
    /// The base directory against which THIS scenario's <c>script.csharp</c> <c>file:</c>
    /// reference is hashed (issue #268) — the scenario's OWN directory, which for a
    /// non-first scenario in a shared-topology suite can differ from
    /// <paramref name="seedBaseDirectory"/> (the suite's single seed root).
    /// <see langword="null"/> (the default) falls back to <paramref name="seedBaseDirectory"/>,
    /// preserving the pre-#268 single-scenario behaviour where the two directories are one
    /// and the same.
    /// </param>
    /// <returns>
    /// The assembled <see cref="ReproducibilityEnvelope"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Secret-safe by construction (§17 — the headline guarantee):</strong>
    /// the envelope is built from REFERENCE TEXT and FIXTURE CONTENT only.  The
    /// secret resolver (<c>ISecretResolver</c>) is never invoked on this code path,
    /// so there is no mechanism by which a resolved secret value can enter the
    /// envelope.  Secret references are discovered via the same
    /// <see cref="CollectSubstitutableTexts"/> + <see cref="SecretReference.FindAll"/>
    /// scan used for provenance and pre-compile validation, and hashed from
    /// <see cref="SecretReference.Raw"/> (the verbatim token) so the digest is
    /// stable across runs (the reproducibility property).
    /// </para>
    /// <para>
    /// Fixture hashing reuses <c>SeedFixtures.ComputeContentHash</c> (Orchestration)
    /// so the envelope records the SAME hash the seed applier computes — no
    /// duplicate hashing routine, no project cycle (the Runtime layer references
    /// both Abstractions and Orchestration).
    /// </para>
    /// <para>
    /// <strong>Missing-fixture behaviour:</strong> if a fixture file is absent at
    /// envelope-build time, the fixture is recorded with a <see langword="null"/>
    /// content hash rather than throwing — the envelope must never crash a run, and
    /// a missing seed file is already classified as an Environment error by the seed
    /// applier (§12.1).  Recording the reference without a hash keeps the envelope a
    /// faithful, non-fatal account of what the run referenced.
    /// </para>
    /// <para>
    /// Exposed as <see langword="internal"/> so the no-docker S05-B-03 tests can
    /// assemble the envelope exactly as the runner does, without standing up a
    /// topology.
    /// </para>
    /// </remarks>
    internal static ReproducibilityEnvelope BuildReproducibilityEnvelope(
        ScenarioAst ast,
        string? seedBaseDirectory,
        string? scriptBaseDirectory = null)
    {
        // ── 1. Distinct secret references across every substitutable field ──────
        // Reuse the exact compile-time scan used for provenance + validation so the
        // set of references in the envelope never drifts from the set the engine
        // actually recognises.  Compute() dedupes by Raw.
        var references = new List<SecretReference>();
        foreach (var node in ast.Steps)
        {
            foreach (var text in CollectSubstitutableTexts(node))
            {
                references.AddRange(SecretReference.FindAll(text));
            }
        }

        // ── 2. Fixture content hashes from the seed block + script.csharp file: refs ────
        // Seed fixtures stay rooted at seedBaseDirectory (the suite's single seed root);
        // script.csharp file: digests root at THIS scenario's own directory (issue #268),
        // falling back to seedBaseDirectory when the caller supplies no distinct value.
        var fixtures = CollectFixtureDigests(ast.Environment?.Seed, seedBaseDirectory)
            .Concat(CollectScriptFileDigests(ast.Steps, scriptBaseDirectory ?? seedBaseDirectory))
            .ToList();

        // Compute() is pure: reference text + fixture digests only, no resolver.
        return ReproducibilityEnvelope.Compute(references, fixtures);
    }

    /// <summary>
    /// Enumerates every seed fixture file referenced by <paramref name="seed"/> —
    /// SQL files, broker-publish payload <c>from</c> files, and document <c>from</c>
    /// files — and computes each one's content hash via
    /// <c>SeedFixtures.ComputeContentHash</c> (S05-A-02), in declared order.
    /// </summary>
    /// <param name="seed">
    /// The scenario's seed block, or <see langword="null"/> when no seed is declared
    /// (yielding an empty fixture list).
    /// </param>
    /// <param name="seedBaseDirectory">
    /// The base directory for relative fixture paths; the current working directory
    /// when <see langword="null"/>.
    /// </param>
    /// <returns>
    /// One <see cref="FixtureDigest"/> per referenced fixture file.  A fixture whose
    /// file is absent is recorded with a <see langword="null"/> content hash (the
    /// envelope never throws — see <see cref="BuildReproducibilityEnvelope"/>).
    /// </returns>
    private static IReadOnlyList<FixtureDigest> CollectFixtureDigests(
        SeedSpec? seed,
        string? seedBaseDirectory)
    {
        if (seed is null || seed.Dependencies.Count == 0)
        {
            return Array.Empty<FixtureDigest>();
        }

        var baseDir = seedBaseDirectory ?? Directory.GetCurrentDirectory();
        var digests = new List<FixtureDigest>();

        foreach (var dependency in seed.Dependencies.Values)
        {
            // SQL fixtures (postgres) — A-01.
            if (dependency.Sql is not null)
            {
                foreach (var sqlPath in dependency.Sql)
                {
                    digests.Add(HashFixtureOrNull(baseDir, sqlPath));
                }
            }

            // Broker-publish payload fixtures — A-02 (wired-but-deferred seam).
            if (dependency.Publish is not null)
            {
                foreach (var publish in dependency.Publish)
                {
                    digests.Add(HashFixtureOrNull(baseDir, publish.PayloadFrom));
                }
            }

            // Document-store fixtures — A-02 (wired-but-deferred seam).
            if (dependency.Documents is not null)
            {
                foreach (var document in dependency.Documents)
                {
                    digests.Add(HashFixtureOrNull(baseDir, document.From));
                }
            }
        }

        return digests;
    }

    /// <summary>
    /// Enumerates every <c>script.csharp</c> step's <c>file</c> reference (when
    /// present) and computes its content hash, so that editing a referenced
    /// <c>.csx</c> file registers as a suite change in the reproducibility
    /// envelope — the same property <see cref="CollectFixtureDigests"/> already
    /// gives seed fixtures. Steps using inline <c>code</c> contribute nothing
    /// here (their body is already part of the compiled CSX, which the caller
    /// hashes separately).
    /// </summary>
    /// <param name="steps">The scenario's step nodes.</param>
    /// <param name="seedBaseDirectory">
    /// The base directory relative <c>file</c> paths are resolved against — the
    /// same base directory used for seed fixtures; the current working
    /// directory when <see langword="null"/>.
    /// </param>
    private static List<FixtureDigest> CollectScriptFileDigests(
        IReadOnlyList<StepNode> steps,
        string? seedBaseDirectory)
    {
        var baseDir = seedBaseDirectory ?? Directory.GetCurrentDirectory();
        var digests = new List<FixtureDigest>();

        foreach (var node in steps)
        {
            if (!string.Equals(node.CanonicalType, "script.csharp", StringComparison.Ordinal))
                continue;

            if (TryGetScalar(node.RawNode, "file", out var file) && !string.IsNullOrEmpty(file))
            {
                digests.Add(HashFixtureOrNull(baseDir, file));
            }
        }

        return digests;
    }

    /// <summary>
    /// Computes a fixture's content hash via the shared
    /// <c>SeedFixtures.ComputeContentHash</c> routine, returning a
    /// <see cref="FixtureDigest"/> with a <see langword="null"/> hash (rather than
    /// throwing) when the file is absent at envelope-build time.
    /// </summary>
    private static FixtureDigest HashFixtureOrNull(string baseDirectory, string relativePath)
    {
        try
        {
            var hash = SeedFixtures.ComputeContentHash(baseDirectory, relativePath);
            return new FixtureDigest(relativePath, hash);
        }
        catch (FileNotFoundException)
        {
            // The envelope must never crash a run: a missing seed fixture is already
            // classified as an Environment error by the seed applier (§12.1).  Record
            // the reference without a hash so the envelope remains a faithful account.
            return new FixtureDigest(relativePath, ContentHash: null);
        }
    }

    /// <summary>
    /// Returns the set of raw field values from <paramref name="node"/> that are
    /// subject to <c>{placeholder}</c> substitution at runtime (B-03).  These are
    /// the fields whose emitted CSX wraps the value in
    /// <c>Substitute_Helpers.Resolve(Vars, …)</c>.
    /// </summary>
    /// <remarks>
    /// The implementation uses the raw YAML mapping node to extract the same fields
    /// that the provider emitters wrap.  For <c>http.rest</c> this is <c>path</c>
    /// (and header values when present); for <c>db-assert.postgres</c> this is
    /// <c>query</c>, each parameter value, AND each <c>expect.row</c> value — every
    /// text the <c>DbAssertPostgresProvider.Emit</c> path resolves at runtime via
    /// <c>Substitute_Helpers.Resolve</c>/<c>ResolveIdentifier</c>.  Keeping this set
    /// in lock-step with the provider is load-bearing: a field the provider
    /// substitutes but this scan omits would let a malformed/unknown-source secret
    /// reference reach execution un-caught, defeating the compile-time guarantee.
    /// <para>
    /// This is a best-effort compile-time scan: it reads the known substitutable
    /// YAML keys for recognised step types.  Unknown provider types are skipped.
    /// </para>
    /// <para>
    /// <c>script.csharp</c> is intentionally absent: its <c>code</c> body is spliced
    /// into the CSX submission verbatim (it is Turing-complete C#, not a substitutable
    /// template) — the engine performs NO <c>{placeholder}</c> or <c>${secret:…}</c>
    /// substitution on it, so there is nothing here to collect or validate.  A future
    /// reviewer should not re-flag this as a gap.
    /// </para>
    /// </remarks>
    private static List<string> CollectSubstitutableTexts(StepNode node)
    {
        var texts = new List<string>();
        var raw = node.RawNode;

        // http.rest: 'path', each header value, AND the request 'body' are substitutable.
        // path/headers since B-03; 'body' since S07-B-02a, when HttpRestProvider.Emit began
        // routing the body through Secret_Helpers.ResolveTemplate at runtime.  S07-B-02b:
        // the body was previously OMITTED from this scan, so a ${secret:…} in an http.rest
        // body was resolved at runtime but invisible to provenance, the pre-compile secret-
        // validation pass, AND the reproducibility envelope — the exact "field the provider
        // substitutes but this scan omits" hazard this method's remarks warn against.  A
        // scalar body is collected here; a structured (mapping/sequence) body is bound to a
        // serialised JSON string by the provider — its placeholders/secrets live inside that
        // string, which this scan does not reconstruct (a structured-body secret remains a
        // known, narrower follow-up, but the common scalar/inline-JSON body is now covered).
        if (string.Equals(node.CanonicalType, "http.rest", StringComparison.Ordinal))
        {
            if (TryGetScalar(raw, "path", out var path) && !string.IsNullOrEmpty(path))
                texts.Add(path);

            if (raw.Children.TryGetValue(
                    new YamlDotNet.RepresentationModel.YamlScalarNode("headers"),
                    out var headersNode)
                && headersNode is YamlDotNet.RepresentationModel.YamlMappingNode headersMap)
            {
                foreach (var kv in headersMap.Children)
                {
                    if (kv.Value is YamlDotNet.RepresentationModel.YamlScalarNode sv
                        && !string.IsNullOrEmpty(sv.Value))
                    {
                        texts.Add(sv.Value);
                    }
                }
            }

            // 'body' as a scalar (raw string / inline JSON): the provider keeps it verbatim
            // as a template and resolves it via Secret_Helpers.ResolveTemplate at runtime, so
            // its {placeholder}/${secret:…} tokens must be recognised here too.
            if (TryGetScalar(raw, "body", out var body) && !string.IsNullOrEmpty(body))
                texts.Add(body);
        }
        // db-assert.postgres: 'query', each parameter value, AND each expect.row
        // value are substitutable (B-03).  DbAssertPostgresProvider.Emit wraps all
        // three in Substitute_Helpers.Resolve/ResolveIdentifier at runtime, so all
        // three must be collected here (the expect.row values were the under-collected
        // gap fixed in S05-B-01: a ${secret:…} there is resolved at runtime but was
        // previously invisible to both this scan and the secret-validation pass).
        else if (string.Equals(
            node.CanonicalType, "db-assert.postgres", StringComparison.Ordinal))
        {
            if (TryGetScalar(raw, "query", out var query) && !string.IsNullOrEmpty(query))
                texts.Add(query);

            if (raw.Children.TryGetValue(
                    new YamlDotNet.RepresentationModel.YamlScalarNode("parameters"),
                    out var paramsNode)
                && paramsNode is YamlDotNet.RepresentationModel.YamlMappingNode paramsMap)
            {
                foreach (var kv in paramsMap.Children)
                {
                    if (kv.Value is YamlDotNet.RepresentationModel.YamlScalarNode sv
                        && !string.IsNullOrEmpty(sv.Value))
                    {
                        texts.Add(sv.Value);
                    }
                }
            }

            // expect.row: a map of column name → expected value.  The provider binds
            // this from RawNode["expect"]["row"] and wraps each VALUE (not the column
            // name) in Substitute_Helpers.Resolve — mirror that traversal exactly so
            // the collected set never drifts from what the provider resolves.
            if (raw.Children.TryGetValue(
                    new YamlDotNet.RepresentationModel.YamlScalarNode("expect"),
                    out var expectNode)
                && expectNode is YamlDotNet.RepresentationModel.YamlMappingNode expectMap
                && expectMap.Children.TryGetValue(
                    new YamlDotNet.RepresentationModel.YamlScalarNode("row"),
                    out var rowNode)
                && rowNode is YamlDotNet.RepresentationModel.YamlMappingNode rowMap)
            {
                foreach (var kv in rowMap.Children)
                {
                    if (kv.Value is YamlDotNet.RepresentationModel.YamlScalarNode sv
                        && !string.IsNullOrEmpty(sv.Value))
                    {
                        texts.Add(sv.Value);
                    }
                }
            }
        }

        return texts;
    }

    /// <summary>
    /// Tries to read a scalar string value from <paramref name="mapping"/> for
    /// <paramref name="key"/>.
    /// </summary>
    private static bool TryGetScalar(
        YamlDotNet.RepresentationModel.YamlMappingNode mapping,
        string key,
        out string value)
    {
        if (mapping.Children.TryGetValue(
                new YamlDotNet.RepresentationModel.YamlScalarNode(key), out var node)
            && node is YamlDotNet.RepresentationModel.YamlScalarNode scalar
            && scalar.Value is not null)
        {
            value = scalar.Value;
            return true;
        }
        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns the full Trusted-Platform-Assemblies (TPA) list as an
    /// <see cref="IReadOnlyList{T}"/> of absolute file paths, suitable for
    /// passing to <see cref="RoslynScriptCompiler.CompileOnce"/> as
    /// <c>additionalReferencePaths</c>.
    /// </summary>
    /// <remarks>
    /// internal (not private, #260): promoted to a shared helper so
    /// <see cref="ScenarioValidator"/>'s topology-free <c>CompileOnce</c> call passes the
    /// SAME TPA list a real run would, rather than duplicating this lookup.
    /// </remarks>
    internal static string[] BclReferencePaths() =>
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
}
