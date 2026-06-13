// Platform.Sdk.Testing — ProviderTestHarness.
//
// The out-of-repo provider test harness.  Drives a single-step .e2e.yaml end to end
// through the PUBLIC engine surface, exactly as the engine's ScenarioRunner does for a
// dependency-free step, and returns the step's verdict as data.
//
// ─── How providers are driven (type-erased) ─────────────────────────────────────────
// The engine drives every provider WITHOUT knowing its TModel at compile time: its
// internal ProviderPipeline (Platform.Engine.Runtime) finds each closed generic
// interface (IStepBinder<TModel> / IStepValidator<TModel> / IStepCompiler<TModel>) on
// the provider instance via reflection and invokes Bind / Validate / Emit through the
// resulting MethodInfo.  ProviderPipeline is `internal` to Platform.Engine.Runtime, and
// this harness deliberately does NOT depend on Platform.Engine.Runtime (it depends only
// on Platform.Sdk + the three packable engine assemblies).  The harness therefore
// REUSES THE SAME MECHANISM by re-implementing that exact reflection dispatch here, over
// the public RegisteredProvider.Instance (an IStepProvider) — never via the fixture's
// concrete cast, so it works for ANY provider whose model type it does not know.
//
// ─── Memory model (§5) ───────────────────────────────────────────────────────────────
// ProviderTestHarness is STATELESS: it holds no static cache of registries, compiled
// scripts, or vars.  The registry, the compiled image, the vars map and the globals are
// all method-local, so nothing this type owns can root the collectible
// AssemblyLoadContext across the boundary.  RunIsolatedAsync compiles once, loads into a
// fresh collectible context, invokes, and unloads — returning memory to baseline.
using System.Reflection;
using Platform.Engine.Abstractions;
using Platform.Engine.Authoring;
using Platform.Engine.Authoring.Ast;
using Platform.Engine.Compilation;
using Platform.Engine.Compilation.Schema;
using Platform.Sdk;
using Platform.Sdk.Testing.Contexts;

namespace Platform.Sdk.Testing;

/// <summary>
/// A stateless harness that runs a single-step <c>.e2e.yaml</c> scenario end to end so an
/// out-of-repo provider author can prove a custom provider works against the published
/// engine, using only published packages and without Docker.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunSingleStepAsync"/> reproduces the engine's compile-and-run path for a
/// dependency-free step: schema-validate (<c>DocumentValidator</c>) → parse
/// (<c>YamlDocumentParser</c>) → build AST (<c>AstBuilder</c>) → bind → validate → emit →
/// assemble (<c>CsxAssembler</c>) → compile once (<c>RoslynScriptCompiler.CompileOnce</c>)
/// → run isolated (<c>RoslynScriptCompiler.RunIsolatedAsync</c>) → read the step's
/// <c>StepOutcome</c> back from <c>Vars</c>.
/// </para>
/// <para>
/// Providers are driven <strong>type-erased</strong>, exactly as the engine drives them:
/// the harness resolves the provider from the <see cref="StepKindRegistry"/> and invokes
/// its closed generic <c>IStepBinder&lt;T&gt;</c> / <c>IStepValidator&lt;T&gt;</c> /
/// <c>IStepCompiler&lt;T&gt;</c> interfaces by reflection, so it works for any provider
/// whose model type is unknown at compile time.
/// </para>
/// <para>
/// This type is stateless and holds no static caches (§5 memory model): every per-run
/// artefact — the registry, the compiled image, the vars map — is method-local.
/// </para>
/// </remarks>
public static class ProviderTestHarness
{
    /// <summary>
    /// The C# namespace into which the single-step suite is emitted.  Matches the
    /// engine's default suite namespace.
    /// </summary>
    private const string SuiteNamespace = "VouchfxGenerated";

    /// <summary>
    /// Runs a single step of <paramref name="yaml"/> — discovered from
    /// <paramref name="providerAssembly"/> by the reflective
    /// <see cref="StepKindRegistry"/> — end to end and returns the result as data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Outcome encoding (expected failures are data, not exceptions):</strong>
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       A <em>schema-validation</em> failure halts the pipeline and returns a
    ///       <see cref="StepRunResult"/> with <see cref="StepRunResult.Verdict"/>
    ///       <see langword="null"/> and <see cref="StepRunResult.SchemaErrors"/>
    ///       populated.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       A <em>model-validation</em> failure halts the pipeline and returns a
    ///       <see cref="StepRunResult"/> with <see cref="StepRunResult.Verdict"/>
    ///       <see langword="null"/> and <see cref="StepRunResult.ValidationErrors"/>
    ///       populated.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Otherwise the step runs and the result carries its
    ///       <see cref="Verdict"/> and observation.
    ///     </description>
    ///   </item>
    /// </list>
    /// <para>
    /// <strong>Unexpected failures throw.</strong>  A genuine Roslyn compile error in the
    /// provider's emitted CSX (a contributor bug) surfaces as a
    /// <c>ScriptCompilationException</c> and is deliberately <em>not</em> swallowed, so the
    /// author sees it loudly.  Likewise a malformed YAML document, an unknown step type, or
    /// a provider that does not implement the required generic interfaces throws.
    /// </para>
    /// </remarks>
    /// <param name="yaml">
    /// The full text of a single-step <c>.e2e.yaml</c> scenario.  Must not be
    /// <see langword="null"/>.
    /// </param>
    /// <param name="providerAssembly">
    /// The assembly containing the provider under test.  The reflective
    /// <see cref="StepKindRegistry"/> discovers every <c>[StepProvider]</c>-decorated
    /// type (and <c>IProviderModule</c>) in it.  Must not be <see langword="null"/>.
    /// </param>
    /// <param name="stepId">
    /// The <c>id</c> of the step in <paramref name="yaml"/> whose outcome is returned.
    /// The scenario should contain exactly this one step.  Must not be
    /// <see langword="null"/> or empty.
    /// </param>
    /// <param name="cancellationToken">
    /// Propagated into the isolated script execution. The caller owns the timeout: pass a
    /// time-bounded token, e.g. <c>new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token</c>.
    /// Note that a CPU-bound infinite loop in a provider's emitted code cannot be
    /// cooperatively cancelled; a buggy provider can still hang and must be bounded at the
    /// caller.
    /// </param>
    /// <returns>
    /// A <see cref="StepRunResult"/> describing the terminal state of the pipeline.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="yaml"/> or <paramref name="providerAssembly"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="stepId"/> is <see langword="null"/> or empty.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the scenario contains no step with id <paramref name="stepId"/>, when
    /// the resolved provider does not implement the required generic interfaces, or when
    /// a stage returns an unexpected null.
    /// </exception>
    /// <exception cref="Platform.Engine.Compilation.ScriptCompilationException">
    /// Thrown when the assembled CSX fails Roslyn compilation — a bug in the provider's
    /// emitted code.  Deliberately not caught.
    /// </exception>
    public static async Task<StepRunResult> RunSingleStepAsync(
        string yaml,
        Assembly providerAssembly,
        string stepId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(providerAssembly);
        ArgumentException.ThrowIfNullOrEmpty(stepId);

        // Discover the provider purely from its assembly — no hand-registration.
        // Method-local: the frozen registry is never cached statically (§5).
        var registry = StepKindRegistry.BuildAndFreeze(new[] { providerAssembly });

        // 1. Schema-validate against the composed language schema.  An EXPECTED failure
        //    (an authoring error) is returned as data with a null verdict, never thrown.
        var schemaResult = DocumentValidator.Validate(yaml, registry);
        if (!schemaResult.IsValid)
        {
            var schemaErrors = schemaResult.Errors
                .Select(e => e.Message)
                .ToArray();
            return Halted(schemaErrors, Array.Empty<string>());
        }

        // 2. Parse → AST (front end).  A malformed document throws (it is not an
        //    "expected" provider outcome — it is a broken test input).
        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, registry);

        var node = ast.Steps.FirstOrDefault(s =>
            string.Equals(s.Id, stepId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"No step with id '{stepId}' was found in the scenario. " +
                $"Steps present: [{string.Join(", ", ast.Steps.Select(s => s.Id))}].");

        // 3. Resolve the provider FROM THE REGISTRY and drive Bind → Validate → Emit
        //    TYPE-ERASED (the same reflection mechanism the engine's ProviderPipeline
        //    uses), so the harness never needs to know the provider's TModel.
        if (!registry.TryGet(node.CanonicalType, out var registered) || registered is null)
        {
            throw new InvalidOperationException(
                $"Provider '{node.CanonicalType}' was not found in the registry after the " +
                "AST was built. This indicates the provider assembly does not register the " +
                "step type the YAML declares.");
        }

        var instance = registered.Instance;

        // ── Bind ──────────────────────────────────────────────────────────────────────
        var model = ReflectBind(instance, node.RawNode, new TestBindingContext());

        // ── Validate ───────────────────────────────────────────────────────────────────
        // An EXPECTED validation failure is returned as data with a null verdict.
        var validation = ReflectValidate(instance, model, new TestProjectContext());
        if (!validation.IsValid)
        {
            return Halted(Array.Empty<string>(), validation.Errors.ToArray());
        }

        // ── Emit ────────────────────────────────────────────────────────────────────────
        var compileCtx = new TestCompileContext(node.Id, SuiteNamespace, node.Capture);
        var fragment = ReflectEmit(instance, model, compileCtx);

        // 4. Assemble the fragment and compile ONCE (§5: compile-once / isolate / unload).
        //    A genuine compile error throws ScriptCompilationException — NOT swallowed.
        var assembled = CsxAssembler.Assemble(new[] { (node.Id, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource);

        // 5. Run in a fresh collectible context; the step writes its StepOutcome to Vars.
        //    Every artefact here is method-local — nothing roots the collectible ALC.
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler
            .RunIsolatedAsync(compiled, globals, runLabel: node.Id, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // 6. Read the outcome back from Vars under the engine-reserved outcome key.
        var safeId = CsxFragment.SanitiseId(node.Id);
        var outcomeKey = VarKeys.Outcome(safeId);

        if (!vars.TryGetValue(outcomeKey, out var raw) || raw is not StepOutcome outcome)
        {
            throw new InvalidOperationException(
                $"The step '{node.Id}' did not write a {nameof(StepOutcome)} under " +
                $"'{outcomeKey}'. A provider's emitted block must record its outcome via " +
                $"Vars[VarKeys.Outcome(sanitisedStepId)]. Keys present: " +
                $"[{string.Join(", ", vars.Keys)}].");
        }

        return new StepRunResult(
            Verdict: outcome.Verdict,
            Observation: outcome.Observation,
            DurationMs: outcome.DurationMs,
            SchemaErrors: Array.Empty<string>(),
            ValidationErrors: Array.Empty<string>());
    }

    /// <summary>
    /// Builds a halted <see cref="StepRunResult"/> (null verdict, zero duration) for an
    /// expected authoring failure surfaced before the step ran.
    /// </summary>
    private static StepRunResult Halted(
        IReadOnlyList<string> schemaErrors,
        IReadOnlyList<string> validationErrors) =>
        new(
            Verdict: null,
            Observation: null,
            DurationMs: 0,
            SchemaErrors: schemaErrors,
            ValidationErrors: validationErrors);

    // ── Type-erased reflection dispatch (mirrors Platform.Engine.Runtime.ProviderPipeline) ──

    /// <summary>
    /// Invokes the provider's closed <see cref="IStepBinder{TModel}"/> <c>Bind</c> method
    /// by reflection and returns the bound model as <see cref="object"/>.
    /// </summary>
    private static object ReflectBind(
        IStepProvider instance,
        YamlDotNet.RepresentationModel.YamlNode rawNode,
        IBindingContext ctx)
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
    /// Invokes the provider's closed <see cref="IStepValidator{TModel}"/> <c>Validate</c>
    /// method by reflection and returns the <see cref="ValidationResult"/>.
    /// </summary>
    private static ValidationResult ReflectValidate(
        IStepProvider instance,
        object model,
        IProjectContext ctx)
    {
        var validatorInterface = FindGenericInterface(instance, typeof(IStepValidator<>));
        var validateMethod = validatorInterface.GetMethod(
            nameof(IStepValidator<IStepModel>.Validate),
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Method 'Validate' not found on {validatorInterface}.");

        return (ValidationResult)(validateMethod.Invoke(instance, new[] { model, ctx })
            ?? throw new InvalidOperationException(
                $"IStepValidator.Validate returned null for provider '{instance.GetType().Name}'."));
    }

    /// <summary>
    /// Invokes the provider's closed <see cref="IStepCompiler{TModel}"/> <c>Emit</c>
    /// method by reflection and returns the <see cref="CsxFragment"/>.
    /// </summary>
    private static CsxFragment ReflectEmit(
        IStepProvider instance,
        object model,
        ICompileContext ctx)
    {
        var compilerInterface = FindGenericInterface(instance, typeof(IStepCompiler<>));
        var emitMethod = compilerInterface.GetMethod(
            nameof(IStepCompiler<IStepModel>.Emit),
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Method 'Emit' not found on {compilerInterface}.");

        return (CsxFragment)(emitMethod.Invoke(instance, new[] { model, ctx })
            ?? throw new InvalidOperationException(
                $"IStepCompiler.Emit returned null for provider '{instance.GetType().Name}'."));
    }

    /// <summary>
    /// Locates the first closed generic interface on <paramref name="instance"/> whose
    /// open generic definition matches <paramref name="openGenericType"/>.
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
