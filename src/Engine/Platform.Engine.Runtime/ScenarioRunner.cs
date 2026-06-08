// Platform.Engine.Runtime — ScenarioRunner (Sprint 3 integration spine; updated S04-A-02).
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
//     RespawnPostgresIsolation (or NullScenarioIsolation) between each.
//   • SuiteResult — aggregate record for RunSuiteAsync callers.
using System.Reflection;
using System.Text.RegularExpressions;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;
using Platform.Engine.Authoring;
using Platform.Engine.Authoring.Ast;
using Platform.Engine.Authoring.Model;
using Platform.Engine.Compilation;
using Platform.Engine.Compilation.Schema;
using Platform.Engine.Orchestration;
using Platform.Engine.Reporting;
using Platform.Sdk;

namespace Platform.Engine.Runtime;

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
/// <strong>Not yet implemented (future sprints):</strong>
/// <list type="bullet">
///   <item>
///     <description>
///       <c>verifyMode: RETRY</c> polling loop — scheduled for Sprint 6.
///       Any scenario that contains a RETRY step is rejected with
///       <see cref="Verdict.Inconclusive"/> until then.
///     </description>
///   </item>
///   <item>
///     <description>
///       Per-step timeout enforcement — also Sprint 6+.
///     </description>
///   </item>
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
    private const string SuiteNamespace = "VouchfxGenerated";

    // Compiled-once regex that matches {identifier} placeholder tokens (S04-G-01).
    // Identical pattern to Substitute_Helpers inside the CSX — used here for
    // compile-time provenance derivation only; never used to read runtime values.
    private static readonly Regex s_placeholderRegex =
        new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(yamlText);
        ArgumentNullException.ThrowIfNull(scenarioName);
        ArgumentNullException.ThrowIfNull(providerAssemblies);
        ArgumentNullException.ThrowIfNull(output);

        var runId = Guid.NewGuid().ToString("n");
        var buffer = new List<string>();

        // ── Step 1: Build provider registry ──────────────────────────────────
        var registry = StepKindRegistry.BuildAndFreeze(providerAssemblies);

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
                await output.WriteLineAsync(error.Message).ConfigureAwait(false);
            }

            TerminalRenderer.Render(buffer, output);
            return Verdict.Inconclusive;
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

            await output.WriteLineAsync(
                $"Parse / AST error: {ex.Message}").ConfigureAwait(false);

            TerminalRenderer.Render(buffer, output);
            return Verdict.Inconclusive;
        }

        // ── Step 4: Provider pipeline — bind / validate / resources / emit ───
        var pipelineResult = ProviderPipeline.Compile(ast, registry, SuiteNamespace);
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
            await output.WriteLineAsync(pipelineResult.Failure.Message)
                .ConfigureAwait(false);
            TerminalRenderer.Render(buffer, output);
            return Verdict.Inconclusive;
        }

        // ── Step 5b: Reject RETRY until Sprint 6 implements the polling loop ──
        var retryStep = ast.Steps.FirstOrDefault(
            s => s.VerifyMode == VerifyMode.Retry);
        if (retryStep is not null)
        {
            var now5b = DateTimeOffset.UtcNow;
            buffer.Add(EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = runId,
                Timestamp = now5b,
                ScenarioId = scenarioName,
            }));
            buffer.Add(EventStreamJson.ToLine(new ScenarioCompletedEvent
            {
                RunId = runId,
                Timestamp = now5b,
                ScenarioId = scenarioName,
                Verdict = Verdict.Inconclusive,
                Counts = new VerdictCounts { Inconclusive = 1 },
            }));
            await output.WriteLineAsync(
                $"step '{retryStep.Id}': verifyMode RETRY is not yet supported " +
                "(lands in Sprint 6); use IMMEDIATE.")
                .ConfigureAwait(false);
            TerminalRenderer.Render(buffer, output);
            return Verdict.Inconclusive;
        }

        // ── Step 6: Start Aspire topology ─────────────────────────────────────
        SuiteTopology suite;
        try
        {
            suite = await SuiteTopology.StartAsync(
                doc.Environment,
                appHostAssemblyName,
                startupTimeout: TimeSpan.FromSeconds(120),
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);
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
            TerminalRenderer.Render(buffer, output);
            return Verdict.EnvironmentError;
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
                buffer,
                isolation,
                output,
                cancellationToken).ConfigureAwait(false);

            TerminalRenderer.Render(buffer, output);
            return verdict;
        }
    }

    /// <summary>
    /// Executes many scenarios against a topology that is built <strong>once</strong>
    /// and torn down after all scenarios complete, resetting mutable dependency state
    /// (Postgres) between scenarios via <see cref="RespawnPostgresIsolation"/> (S04-A-02).
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

        if (scenarioNames.Count != scenarios.Count || yamlTexts.Count != scenarios.Count)
        {
            throw new ArgumentException(
                "scenarios, scenarioNames, and yamlTexts must all have the same length.",
                nameof(scenarios));
        }

        // Build the provider registry once (shared across all scenarios).
        var registry = StepKindRegistry.BuildAndFreeze(providerAssemblies);

        // ── Validate shared-environment assumption ─────────────────────────────
        // All scenarios must share the environment declared in scenario[0].
        // If any scenario diverges, return EnvironmentError for the whole suite.
        var firstEnvJson = SerialiseEnvironment(scenarios[0].Environment);
        for (int i = 1; i < scenarios.Count; i++)
        {
            var envJson = SerialiseEnvironment(scenarios[i].Environment);
            if (!string.Equals(envJson, firstEnvJson, StringComparison.Ordinal))
            {
                await output.WriteLineAsync(
                    $"RunSuiteAsync: scenario '{scenarioNames[i]}' declares a different " +
                    "environment block than the first scenario.  All scenarios in a suite " +
                    "must share one topology.  Suite aborted with EnvironmentError.")
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
            string? EarlyMessage)>();

        for (int i = 0; i < scenarios.Count; i++)
        {
            var name = scenarioNames[i];
            var yaml = yamlTexts[i];
            var ast = scenarios[i];

            // Schema-validate the YAML.
            var validationResult = DocumentValidator.Validate(yaml, registry);
            if (!validationResult.IsValid)
            {
                compilations.Add((name, ast, null, Verdict.Inconclusive,
                    string.Join("; ", validationResult.Errors.Select(e => e.Message))));
                continue;
            }

            // RETRY guard.
            var retryStep = ast.Steps.FirstOrDefault(s => s.VerifyMode == VerifyMode.Retry);
            if (retryStep is not null)
            {
                compilations.Add((name, ast, null, Verdict.Inconclusive,
                    $"step '{retryStep.Id}': verifyMode RETRY is not yet supported (lands in Sprint 6); use IMMEDIATE."));
                continue;
            }

            // Provider pipeline compile.
            var pipelineResult = ProviderPipeline.Compile(ast, registry, SuiteNamespace);
            if (pipelineResult.Failure is not null)
            {
                compilations.Add((name, ast, null, Verdict.Inconclusive,
                    pipelineResult.Failure.Message));
                continue;
            }

            compilations.Add((name, ast, pipelineResult, null, null));
        }

        // ── Build topology once ────────────────────────────────────────────────
        SuiteTopology suite;
        try
        {
            suite = await SuiteTopology.StartAsync(
                scenarios[0].Environment,
                appHostAssemblyName,
                startupTimeout: TimeSpan.FromSeconds(120),
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OrchestrationException oex)
        {
            await output.WriteLineAsync(
                $"RunSuiteAsync: topology failed to start — {oex.Message}")
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
            // If a postgres dependency is present, use RespawnPostgresIsolation.
            // Otherwise NullScenarioIsolation preserves the existing behaviour.
            IScenarioIsolation isolation = BuildIsolation(suite);

            var results = new List<(string ScenarioName, Verdict Verdict)>(compilations.Count);
            var suiteAggregate = Verdict.Pass;
            var allBuffers = new List<string>();

            for (int i = 0; i < compilations.Count; i++)
            {
                var (name, ast, pipeline, earlyVerdict, earlyMessage) = compilations[i];
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
                        await output.WriteLineAsync(earlyMessage).ConfigureAwait(false);
                    }

                    results.Add((name, earlyVerdict.Value));
                    suiteAggregate = Elevate(suiteAggregate, earlyVerdict.Value);
                    allBuffers.AddRange(buffer);
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

                    // Isolation failure → abort the suite (subsequent scenarios
                    // would run against an unknown DB state).
                    await output.WriteLineAsync(
                        $"Isolation.BeginScenarioAsync failed for '{name}': {oex.Message}; " +
                        "aborting suite.").ConfigureAwait(false);
                    break;
                }

                // ── Run scenario ───────────────────────────────────────────────
                var scenarioVerdict = await RunScenarioAgainstTopologyAsync(
                    ast,
                    name,
                    runId,
                    suite,
                    pipeline!.Assembled!,
                    pipeline.CompileReferencePaths,
                    buffer,
                    new NullScenarioIsolation(), // isolation already handled above/below
                    output,
                    cancellationToken).ConfigureAwait(false);

                results.Add((name, scenarioVerdict));
                suiteAggregate = Elevate(suiteAggregate, scenarioVerdict);
                allBuffers.AddRange(buffer);

                // ── EndScenario (isolation / reset) ────────────────────────────
                try
                {
                    await isolation.EndScenarioAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OrchestrationException oex)
                {
                    await output.WriteLineAsync(
                        $"Isolation.EndScenarioAsync failed after '{name}': {oex.Message}; " +
                        "aborting suite — subsequent scenarios may run against unclean state.")
                        .ConfigureAwait(false);
                    suiteAggregate = Elevate(suiteAggregate, Verdict.EnvironmentError);
                    break;
                }
            }

            // Dispose the isolation connection when the topology is torn down.
            if (isolation is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }

            TerminalRenderer.Render(allBuffers, output);
            return new SuiteResult(suiteAggregate, results);
        }
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
    private static async Task<Verdict> RunScenarioAgainstTopologyAsync(
        ScenarioAst ast,
        string scenarioName,
        string runId,
        SuiteTopology suite,
        AssembledScript assembled,
        IReadOnlyList<string> compileReferencePaths,
        List<string> buffer,
        IScenarioIsolation isolation,
        TextWriter output,
        CancellationToken cancellationToken)
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

        // ── Stage the `variables` block constants (DSL §3) ────────────────────
        // Pre-loaded into the shared context under their bare names (no prefix) so
        // {placeholder} substitution and capture reads resolve them uniformly.
        // Staged before execution as the baseline; a capture writing the same name
        // later in the run legitimately overrides the constant.
        foreach (var kv in ast.Variables)
        {
            vars[kv.Key] = kv.Value;
        }

        var globals = new ScriptGlobalVariables(vars, suite.DiscoveredServices);

        // ── Compile-once + RunIsolatedAsync ───────────────────────────────────
        var tpaPaths = BclReferencePaths()
            .Concat(compileReferencePaths)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        CompiledScript compiled;
        try
        {
            compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource,
                additionalOptions: null,
                additionalReferencePaths: tpaPaths);

            await RoslynScriptCompiler.RunIsolatedAsync(
                compiled,
                globals,
                runLabel: scenarioName,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var nowCE = DateTimeOffset.UtcNow;
            buffer.Add(EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = runId,
                Timestamp = nowCE,
                ScenarioId = scenarioName,
            }));
            buffer.Add(EventStreamJson.ToLine(new ScenarioCompletedEvent
            {
                RunId = runId,
                Timestamp = nowCE,
                ScenarioId = scenarioName,
                Verdict = Verdict.Inconclusive,
                Counts = new VerdictCounts { Inconclusive = 1 },
            }));

            var diagnosis = ex is ScriptCompilationException sce
                ? $"CSX compilation failed: {sce.Message}"
                : $"{ex.GetType().Name}: {ex.Message}";

            await output.WriteLineAsync(
                $"Compile/run error (Inconclusive): {diagnosis}")
                .ConfigureAwait(false);

            return Verdict.Inconclusive;
        }

        // ── Emit events from outcomes + aggregate verdict ─────────────────────
        var now9 = DateTimeOffset.UtcNow;
        buffer.Add(EventStreamJson.ToLine(new ScenarioStartedEvent
        {
            RunId = runId,
            Timestamp = now9,
            ScenarioId = scenarioName,
        }));

        var aggregate = Verdict.Pass;
        var counts = new int[4];

        // Build a map of varName → stepId for all captures defined in this scenario,
        // so that substitution provenance (G-01) can trace each placeholder's origin.
        // Only steps that appear BEFORE the current step contribute (captures are
        // forward-threading: a step can only read what a prior step captured).
        // We build the full map once and use it as a lookup in the loop below.
        var captureOriginMap = BuildCaptureOriginMap(ast.Steps);

        foreach (var node in ast.Steps)
        {
            var safeId = CsxFragment.SanitiseId(node.Id);

            buffer.Add(EventStreamJson.ToLine(new StepStartedEvent
            {
                RunId = runId,
                Timestamp = now9,
                StepId = node.Id,
                Kind = node.CanonicalType,
                VerifyMode = node.VerifyMode.ToString().ToUpperInvariant(),
                TimeoutMs = null,
            }));

            var outcomeKey = VarKeys.Outcome(safeId);
            var outcome = vars.TryGetValue(outcomeKey, out var raw)
                ? raw as StepOutcome
                : null;

            var stepVerdict = outcome?.Verdict ?? Verdict.Inconclusive;
            var durationMs = outcome?.DurationMs ?? 0L;

            // ── G-01: build Captured provenance ──────────────────────────────
            IReadOnlyList<CapturedVar>? capturedList = null;
            if (node.Capture.Count > 0)
            {
                // Read the matched-flag string written by the emitted block:
                // format is "1,0,1" — one flag per capture in declaration order.
                string? captureStatusRaw = null;
                if (vars.TryGetValue(VarKeys.CaptureStatus(safeId), out var csRaw)
                    && csRaw is string csStr)
                {
                    captureStatusRaw = csStr;
                }

                var flagTokens = captureStatusRaw?.Split(',') ?? Array.Empty<string>();
                var capturedVars = new List<CapturedVar>(node.Capture.Count);
                var captureKeys = node.Capture.Keys.ToArray();
                var captureVals = node.Capture.Values.ToArray();

                for (int ci = 0; ci < captureKeys.Length; ci++)
                {
                    var matched = ci < flagTokens.Length && flagTokens[ci] == "1";
                    capturedVars.Add(new CapturedVar(
                        Name: captureKeys[ci],
                        Path: captureVals[ci],
                        Matched: matched));
                }
                capturedList = capturedVars;
            }

            // ── G-01: build Substitutions provenance (compile-time) ───────────
            // Scan every substitutable field in the step's raw YAML for {name}
            // tokens.  This is compile-time derivation — no runtime value is read.
            IReadOnlyList<SubstitutionRef>? substitutionsList = null;
            var substitutableTexts = CollectSubstitutableTexts(node);
            if (substitutableTexts.Count > 0)
            {
                var seenPlaceholders = new HashSet<string>(StringComparer.Ordinal);
                var subs = new List<SubstitutionRef>();
                foreach (var text in substitutableTexts)
                {
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
                            SecretDerived: false)); // no secret resolution this sprint
                    }
                }
                if (subs.Count > 0)
                    substitutionsList = subs;
            }

            buffer.Add(EventStreamJson.ToLine(new StepCompletedEvent
            {
                RunId = runId,
                Timestamp = now9,
                StepId = node.Id,
                Verdict = stepVerdict,
                DurationMs = durationMs,
                Captured = capturedList,
                Substitutions = substitutionsList,
            }));

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

        buffer.Add(EventStreamJson.ToLine(new ScenarioCompletedEvent
        {
            RunId = runId,
            Timestamp = DateTimeOffset.UtcNow,
            ScenarioId = scenarioName,
            Verdict = aggregate,
            Counts = finalCounts,
        }));

        return aggregate;
    }

    /// <summary>
    /// Builds the appropriate <see cref="IScenarioIsolation"/> for the given
    /// <paramref name="topology"/>.  Uses <see cref="RespawnPostgresIsolation"/>
    /// when the topology has at least one Postgres dependency; otherwise falls
    /// back to <see cref="NullScenarioIsolation"/>.
    /// </summary>
    private static IScenarioIsolation BuildIsolation(SuiteTopology topology)
    {
        // Look for the first dependency name whose DiscoveredServices value looks
        // like a Postgres connection string (contains "Host=" — ADO.NET convention).
        foreach (var name in topology.DependencyNames)
        {
            if (topology.DiscoveredServices.TryGetValue(name, out var value) &&
                value is string connStr &&
                connStr.Contains("Host=", StringComparison.OrdinalIgnoreCase))
            {
                return new RespawnPostgresIsolation(connStr);
            }
        }

        return new NullScenarioIsolation();
    }

    /// <summary>
    /// Serialises an <see cref="EnvironmentSpec"/> to a stable JSON string for
    /// equality comparison across suite scenarios (shared-environment validation).
    /// Returns an empty string for a <see langword="null"/> environment.
    /// </summary>
    private static string SerialiseEnvironment(EnvironmentSpec? env) =>
        env is null
            ? string.Empty
            : System.Text.Json.JsonSerializer.Serialize(env);

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
    private static Dictionary<string, string> BuildCaptureOriginMap(
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
    /// Returns the set of raw field values from <paramref name="node"/> that are
    /// subject to <c>{placeholder}</c> substitution at runtime (B-03).  These are
    /// the fields whose emitted CSX wraps the value in
    /// <c>Substitute_Helpers.Resolve(Vars, …)</c>.
    /// </summary>
    /// <remarks>
    /// The implementation uses the raw YAML mapping node to extract the same fields
    /// that the provider emitters wrap.  For <c>http.rest</c> this is <c>path</c>
    /// (and header values when present); for <c>db-assert.postgres</c> this is
    /// <c>query</c> and each parameter value.
    /// <para>
    /// This is a best-effort compile-time scan: it reads the known substitutable
    /// YAML keys for recognised step types.  Unknown provider types are skipped.
    /// </para>
    /// </remarks>
    private static List<string> CollectSubstitutableTexts(StepNode node)
    {
        var texts = new List<string>();
        var raw = node.RawNode;

        // http.rest: 'path' and each header value are substitutable (B-03).
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
        }
        // db-assert.postgres: 'query' and each parameter value are substitutable (B-03).
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
    private static string[] BclReferencePaths() =>
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
}
