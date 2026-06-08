// Platform.Engine.Runtime — ScenarioRunner (Sprint 3 integration spine).
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
using System.Reflection;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;
using Platform.Engine.Authoring;
using Platform.Engine.Compilation;
using Platform.Engine.Compilation.Schema;
using Platform.Engine.Orchestration;
using Platform.Engine.Reporting;
using Platform.Sdk;

namespace Platform.Engine.Runtime;

/// <summary>
/// Executes the full vouchfx end-to-end pipeline for a single scenario:
/// validate → parse → AST → bind/validate/emit → assemble → compile-once →
/// build topology → run → emit events → render verdict.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ScenarioRunner"/> is the integration spine introduced in Sprint 3.
/// It is deliberately provider-agnostic: the caller supplies the provider
/// assemblies to scan via <paramref name="providerAssemblies"/>, so the runner
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
///       Per-step timeout enforcement — also Sprint 6+.  The authored
///       <c>timeout</c> value is parsed and stored on the AST node but is
///       not enforced at runtime; <c>StepStartedEvent.TimeoutMs</c> is
///       emitted as <see langword="null"/> to avoid advertising behaviour
///       that does not happen.
///     </description>
///   </item>
///   <item>
///     <description>
///       <c>continueOnFailure</c> abort semantics — the field is parsed but
///       the runner does not yet short-circuit on step failure when the flag
///       is <see langword="false"/>.
///     </description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public static class ScenarioRunner
{
    // Fixed suite namespace injected into every ICompileContext during emit.
    private const string SuiteNamespace = "VouchfxGenerated";

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
    /// <see cref="System.Reflection.Assembly.GetEntryAssembly"/>.
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
        Platform.Engine.Authoring.Ast.ScenarioAst ast;
        Platform.Engine.Authoring.Model.E2eDocument doc;
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

        // ── Step 4: Bind / validate / emit each step via reflection ──────────
        var fragments = new List<(string StepId, CsxFragment Fragment)>(ast.Steps.Count);
        foreach (var node in ast.Steps)
        {
            if (!registry.TryGet(node.CanonicalType, out var rp) || rp is null)
            {
                // This should not happen: AstBuilder already verified the type.
                await output.WriteLineAsync(
                    $"Internal error: provider '{node.CanonicalType}' missing from registry after AST build.")
                    .ConfigureAwait(false);
                TerminalRenderer.Render(buffer, output);
                return Verdict.Inconclusive;
            }

            var instance = rp.Instance;
            var bindingCtx = new RunBindingContext();
            var projectCtx = new RunProjectContext();
            var compileCtx = new RunCompileContext(node.Id, SuiteNamespace);

            // Reflect closed generic IStepBinder<TModel> → object model
            var model = ReflectBind(instance, node.RawNode, bindingCtx);

            // Reflect closed generic IStepValidator<TModel> → ValidationResult
            var validResult = ReflectValidate(instance, model, projectCtx);
            if (!validResult.IsValid)
            {
                // Authoring / model validation error → Inconclusive for this step.
                await output.WriteLineAsync(
                    $"Step '{node.Id}' model validation failed: " +
                    string.Join("; ", validResult.Errors))
                    .ConfigureAwait(false);
                TerminalRenderer.Render(buffer, output);
                return Verdict.Inconclusive;
            }

            // Reflect closed generic IStepCompiler<TModel> → CsxFragment
            var fragment = ReflectEmit(instance, model, compileCtx);
            fragments.Add((node.Id, fragment));
        }

        // ── Step 5: Assemble fragments → AssembledScript ──────────────────────
        var assembled = CsxAssembler.Assemble(fragments);

        // ── Step 5b: Reject RETRY until Sprint 6 implements the polling loop ──
        // Emitting RETRY events when the engine runs each step exactly once is a
        // §12.1 trust hazard: the stream would claim behaviour that does not happen.
        // Until the RETRY loop lands (Sprint 6), reject RETRY-marked steps honestly.
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
            // ── Step 7: Stage service URLs into Vars ──────────────────────────
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var kv in suite.DiscoveredServices)
            {
                vars[VarKeys.Service(kv.Key)] = kv.Value;
            }

            var globals = new ScriptGlobalVariables(vars, suite.DiscoveredServices);

            // ── Step 8: Compile-once + RunIsolatedAsync ───────────────────────
            // Failure to compile or run engine-generated code is Inconclusive
            // (§12.1): the test could not be executed.  A compile failure is an
            // engine/provider bug, not a product defect; propagating it as an
            // unhandled throw would give the caller no verdict.
            var tpaPaths = BclReferencePaths();

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

                TerminalRenderer.Render(buffer, output);
                return Verdict.Inconclusive;
            }

            // ── Step 9: Emit events from outcomes + aggregate verdict ─────────
            var now9 = DateTimeOffset.UtcNow;
            buffer.Add(EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = runId,
                Timestamp = now9,
                ScenarioId = scenarioName,
            }));

            var aggregate = Verdict.Pass;
            var counts = new int[4]; // [Pass, Fail, EnvironmentError, Inconclusive]

            foreach (var node in ast.Steps)
            {
                var safeId = CsxFragment.SanitiseId(node.Id);

                // Per-step timeouts are not yet enforced (Sprint 6+); emitting a
                // non-null TimeoutMs would advertise behaviour that does not
                // happen.  All surviving steps are IMMEDIATE (RETRY is rejected
                // above), so VerifyMode is always "IMMEDIATE" here — honest.
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

                buffer.Add(EventStreamJson.ToLine(new StepCompletedEvent
                {
                    RunId = runId,
                    Timestamp = now9,
                    StepId = node.Id,
                    Verdict = stepVerdict,
                    DurationMs = durationMs,
                }));

                // Tally counts
                counts[(int)stepVerdict]++;

                // Aggregate with precedence: EnvironmentError > Fail > Inconclusive > Pass
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

            // ── Step 10: Render + return ──────────────────────────────────────
            TerminalRenderer.Render(buffer, output);
            return aggregate;
        }
    }

    // ── Verdict aggregation ────────────────────────────────────────────────────

    /// <summary>
    /// Elevates <paramref name="current"/> when <paramref name="next"/> has
    /// higher precedence.  Precedence (highest first):
    /// <c>EnvironmentError &gt; Fail &gt; Inconclusive &gt; Pass</c>.
    /// </summary>
    private static Verdict Elevate(Verdict current, Verdict next) =>
        VerdictPrecedence(next) > VerdictPrecedence(current) ? next : current;

    private static int VerdictPrecedence(Verdict v) => v switch
    {
        Verdict.Pass => 0,
        Verdict.Inconclusive => 1,
        Verdict.Fail => 2,
        Verdict.EnvironmentError => 3,
        _ => 0,
    };

    // ── Full TPA reference list for compile ───────────────────────────────────

    /// <summary>
    /// Returns the full Trusted-Platform-Assemblies (TPA) list as an
    /// <see cref="IReadOnlyList{T}"/> of absolute file paths, suitable for
    /// passing to <see cref="RoslynScriptCompiler.CompileOnce"/> as
    /// <c>additionalReferencePaths</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The TPA list is split on the platform path separator (<c>;</c> on Windows,
    /// <c>:</c> on Unix) and empty entries are removed.  This gives Roslyn access
    /// to every BCL assembly — including <c>System.Net.Http</c>,
    /// <c>System.Text.Json</c>, <c>System.Net.Primitives</c>,
    /// <c>System.Globalization</c>, and <c>System.Private.Uri</c> — which the
    /// http.rest provider's emitted CSX body requires.
    /// </para>
    /// <para>
    /// These are compile-time metadata references only; they do not load additional
    /// assemblies into the collectible ALC, so the memory-model invariant (§5) is
    /// preserved.
    /// </para>
    /// </remarks>
    private static string[] BclReferencePaths() =>
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

    // ── Reflection dispatch: Bind / Validate / Emit ───────────────────────────

    /// <summary>
    /// Reflects the closed <see cref="IStepBinder{TModel}"/> interface on
    /// <paramref name="instance"/>, invokes <c>Bind</c>, and returns the model
    /// as <see cref="object"/>.
    /// </summary>
    private static object ReflectBind(
        IStepProvider instance,
        YamlDotNet.RepresentationModel.YamlNode rawNode,
        RunBindingContext ctx)
    {
        var binderInterface = FindGenericInterface(instance, typeof(IStepBinder<>));
        var bindMethod = binderInterface.GetMethod(
            nameof(IStepBinder<IStepModel>.Bind),
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Method 'Bind' not found on {binderInterface}.");

        return bindMethod.Invoke(instance, new object[] { rawNode, ctx })
            ?? throw new InvalidOperationException(
                $"IStepBinder.Bind returned null for provider '{instance.GetType().Name}'.");
    }

    /// <summary>
    /// Reflects the closed <see cref="IStepValidator{TModel}"/> interface on
    /// <paramref name="instance"/>, invokes <c>Validate</c>, and returns the
    /// <see cref="ValidationResult"/>.
    /// </summary>
    private static ValidationResult ReflectValidate(
        IStepProvider instance,
        object model,
        RunProjectContext ctx)
    {
        var validatorInterface = FindGenericInterface(instance, typeof(IStepValidator<>));
        var validateMethod = validatorInterface.GetMethod(
            nameof(IStepValidator<IStepModel>.Validate),
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Method 'Validate' not found on {validatorInterface}.");

        return (ValidationResult)(validateMethod.Invoke(instance, new object[] { model, ctx })
            ?? throw new InvalidOperationException(
                $"IStepValidator.Validate returned null for provider '{instance.GetType().Name}'."));
    }

    /// <summary>
    /// Reflects the closed <see cref="IStepCompiler{TModel}"/> interface on
    /// <paramref name="instance"/>, invokes <c>Emit</c>, and returns the
    /// <see cref="CsxFragment"/>.
    /// </summary>
    private static CsxFragment ReflectEmit(
        IStepProvider instance,
        object model,
        RunCompileContext ctx)
    {
        var compilerInterface = FindGenericInterface(instance, typeof(IStepCompiler<>));
        var emitMethod = compilerInterface.GetMethod(
            nameof(IStepCompiler<IStepModel>.Emit),
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Method 'Emit' not found on {compilerInterface}.");

        return (CsxFragment)(emitMethod.Invoke(instance, new object[] { model, ctx })
            ?? throw new InvalidOperationException(
                $"IStepCompiler.Emit returned null for provider '{instance.GetType().Name}'."));
    }

    /// <summary>
    /// Locates the first closed generic interface on <paramref name="instance"/>
    /// whose open generic definition matches <paramref name="openGenericType"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the provider does not implement the required generic interface.
    /// </exception>
    private static Type FindGenericInterface(IStepProvider instance, Type openGenericType)
    {
        return instance.GetType()
            .GetInterfaces()
            .FirstOrDefault(i =>
                i.IsGenericType &&
                i.GetGenericTypeDefinition() == openGenericType)
            ?? throw new InvalidOperationException(
                $"Provider '{instance.GetType().FullName}' does not implement " +
                $"the required generic interface '{openGenericType.Name}'.");
    }
}
