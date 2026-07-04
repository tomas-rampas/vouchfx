// Platform.Engine.Runtime — ProviderPipeline (S04-B-01).
//
// Extracts the per-step bind / validate / resources / emit reflection loop and
// the CSX assembly stage from ScenarioRunner into a dedicated static class so
// that each concern is tested in isolation.
//
// Design notes:
//   • ReflectResources is TOLERANT: providers that do not implement
//     IResourceContributor<TModel> silently contribute an empty list.
//   • ICompileReferenceContributor is also TOLERANT: providers that omit it
//     contribute no extra compile references.
//   • All other reflectors (Bind / Validate / Emit) throw on missing interface.
//   • RETRY is COMPILED, not rejected (Sprint 6): each step's VerifyMode and
//     Timeout are threaded into a StepCompilePlan so CsxAssembler can wrap RETRY
//     steps in the engine-owned polling loop (§7).  The execution-time rejection
//     guard that previously lived in ScenarioRunner has been removed.
//   • The ValidationFailure path mirrors the existing ScenarioRunner Inconclusive
//     pattern so callers need no conditional logic change.

using System.Reflection;
using Platform.Engine.Abstractions;
using Platform.Engine.Authoring.Ast;
using Platform.Engine.Compilation;
using Platform.Sdk;

namespace Platform.Engine.Runtime;

/// <summary>
/// Records a single resource requirement contributed by a provider step, together
/// with the identity of the contributing provider type.
/// </summary>
/// <param name="StepId">
/// The step identifier whose provider contributed this requirement.
/// </param>
/// <param name="Requirement">
/// The declared infrastructure resource requirement.
/// </param>
/// <param name="ProviderTypeName">
/// The fully-qualified name of the provider type that contributed this requirement.
/// </param>
internal sealed record ResourcePlanEntry(
    string StepId,
    ResourceRequirement Requirement,
    string ProviderTypeName);

/// <summary>
/// Records a single host-side resource requirement contributed by a provider step
/// (S07-F-01a), together with the contributing step's identity.
/// </summary>
/// <remarks>
/// This is the host-side counterpart to <see cref="ResourcePlanEntry"/>: where the latter
/// records a containerised Aspire resource, this records an in-process resource the engine
/// host must own in the Default <see cref="System.Runtime.Loader.AssemblyLoadContext"/> (e.g.
/// an ephemeral webhook listener), started at topology-up before any step runs (§5).
/// </remarks>
/// <param name="StepId">
/// The step identifier whose provider contributed this host-resource requirement.
/// </param>
/// <param name="Requirement">
/// The declared host-side resource requirement (kind + logical var name).
/// </param>
internal sealed record HostResourcePlanEntry(
    string StepId,
    HostResourceRequirement Requirement);

/// <summary>
/// Records a model-validation failure surfaced during the pipeline's validate stage.
/// </summary>
/// <param name="Message">
/// A human-readable description of the validation failure, suitable for inclusion in
/// the event stream and rendered output.
/// </param>
internal sealed record ValidationFailure(string Message);

/// <summary>
/// The result of running <see cref="ProviderPipeline.Compile"/> over a
/// <see cref="ScenarioAst"/>.
/// </summary>
/// <param name="Assembled">
/// The fully assembled CSX script ready for Roslyn compilation, or
/// <see langword="null"/> when <see cref="Failure"/> is non-null.
/// </param>
/// <param name="ResourcePlan">
/// The ordered list of infrastructure resource requirements collected from every
/// step that implements <see cref="IResourceContributor{TModel}"/>.  Empty when no
/// step contributes resources.
/// </param>
/// <param name="CompileReferencePaths">
/// Distinct absolute file paths of assemblies contributed by providers that
/// implement <see cref="ICompileReferenceContributor"/>.  These become additional
/// Roslyn <c>MetadataReference</c>s and must <strong>not</strong> be passed to
/// <c>RunIsolatedAsync</c>'s <c>collectibleProbingPaths</c> — they resolve from
/// the Default ALC, preserving the §5 memory-model invariant.
/// Empty when no provider contributes compile references.
/// </param>
/// <param name="HostResourcePlan">
/// The ordered list of host-side resource requirements collected from every step that
/// implements <see cref="IHostResourceContributor{TModel}"/> (S07-F-01a).  Empty when no
/// step contributes a host resource — in which case the runner starts no listener and the
/// <c>Webhooks</c> accessor stays the Null accessor (off the hot path).  These resources are
/// started by the runner in the Default <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
/// before any step runs, never inside the collectible script context (§5).
/// </param>
/// <param name="Failure">
/// Non-null when a model-validation failure was encountered during the pipeline.
/// The caller should map this to <c>Verdict.Inconclusive</c> (the step never ran;
/// this is an authoring error, not a product defect).
/// </param>
internal sealed record PipelineResult(
    AssembledScript? Assembled,
    IReadOnlyList<ResourcePlanEntry> ResourcePlan,
    IReadOnlyList<string> CompileReferencePaths,
    IReadOnlyList<HostResourcePlanEntry> HostResourcePlan,
    ValidationFailure? Failure);

/// <summary>
/// Provider-mediated CSX generation pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Walks each step in the <see cref="ScenarioAst"/>, dispatches the four reflection
/// operations (Bind → Validate → Resources → Emit) via closed generic interfaces,
/// collects the resource plan and compile-reference paths, then calls
/// <see cref="CsxAssembler.Assemble"/> to produce the final script.
/// </para>
/// <para>
/// <see cref="Compile"/> is the single entry point.  All other members are private
/// reflection helpers that mirror the pattern previously inlined in
/// <see cref="ScenarioRunner"/>.
/// </para>
/// </remarks>
internal static class ProviderPipeline
{
    /// <summary>
    /// Executes the full bind / validate / resources / emit pipeline over every step
    /// in <paramref name="ast"/> and assembles the resulting fragments into a
    /// <see cref="PipelineResult"/>.
    /// </summary>
    /// <param name="ast">
    /// The fully normalised scenario AST produced by
    /// <see cref="Platform.Engine.Authoring.AstBuilder.Build"/>.
    /// </param>
    /// <param name="registry">
    /// The frozen provider registry used to look up the provider instance for each
    /// step's canonical type.
    /// </param>
    /// <param name="suiteNamespace">
    /// The C# namespace injected into every <see cref="RunCompileContext"/> during
    /// the emit stage.
    /// </param>
    /// <returns>
    /// A <see cref="PipelineResult"/> whose <see cref="PipelineResult.Failure"/> is
    /// non-null when a model-validation failure is encountered (the caller should map
    /// this to <c>Verdict.Inconclusive</c> and return early), or whose
    /// <see cref="PipelineResult.Assembled"/> is set to the ready-to-compile script
    /// on success.
    /// </returns>
    internal static PipelineResult Compile(
        ScenarioAst ast,
        StepKindRegistry registry,
        string suiteNamespace)
    {
        var fragments = new List<StepCompilePlan>(ast.Steps.Count);
        var resourcePlan = new List<ResourcePlanEntry>();
        var hostResourcePlan = new List<HostResourcePlanEntry>();
        var compileRefLocations = new HashSet<string>(StringComparer.Ordinal);
        var compileRefPaths = new List<string>();

        // Build the declared-dependencies map once for the whole pipeline run.
        // This is derived from environment.dependencies (name → Type) and is
        // empty when the scenario omits the environment section (Sprint-4).
        var projectCtx = BuildProjectContext(ast);

        foreach (var node in ast.Steps)
        {
            if (!registry.TryGet(node.CanonicalType, out var rp) || rp is null)
            {
                // This should not happen: AstBuilder already verified the type.
                // Return a ValidationFailure so the caller can surface Inconclusive.
                return new PipelineResult(
                    Assembled: null,
                    ResourcePlan: Array.Empty<ResourcePlanEntry>(),
                    CompileReferencePaths: Array.Empty<string>(),
                    HostResourcePlan: Array.Empty<HostResourcePlanEntry>(),
                    Failure: new ValidationFailure(
                        $"Internal error: provider '{node.CanonicalType}' missing from registry after AST build."));
            }

            var instance = rp.Instance;
            var bindingCtx = new RunBindingContext();
            // S04-B-02 / S07-B-01a: pass the step's format-aware capture map
            // (varName → CaptureExpr) into the compile context so providers can emit
            // capture logic into the CSX block.  The context exposes both the typed
            // CaptureExprs view and the back-compatible expression-string Captures view.
            var compileCtx = new RunCompileContext(node.Id, suiteNamespace, node.Capture);

            // ── Bind ──────────────────────────────────────────────────────────
            var model = ReflectBind(instance, node.RawNode, bindingCtx);

            // ── Validate ──────────────────────────────────────────────────────
            var validResult = ReflectValidate(instance, model, projectCtx);
            if (!validResult.IsValid)
            {
                return new PipelineResult(
                    Assembled: null,
                    ResourcePlan: Array.Empty<ResourcePlanEntry>(),
                    CompileReferencePaths: Array.Empty<string>(),
                    HostResourcePlan: Array.Empty<HostResourcePlanEntry>(),
                    Failure: new ValidationFailure(
                        $"Step '{node.Id}' model validation failed: " +
                        string.Join("; ", validResult.Errors)));
            }

            // ── Resources (tolerant) ──────────────────────────────────────────
            foreach (var req in ReflectResources(instance, model))
            {
                resourcePlan.Add(new ResourcePlanEntry(
                    StepId: node.Id,
                    Requirement: req,
                    ProviderTypeName: instance.GetType().FullName
                        ?? instance.GetType().Name));
            }

            // ── Host resources (tolerant, S07-F-01a) ──────────────────────────
            // Providers that do not implement IHostResourceContributor<TModel>
            // contribute nothing; the runner starts a host-side resource (in the
            // Default ALC) for each requirement collected here, before any step runs.
            foreach (var hostReq in ReflectHostResources(instance, model))
            {
                hostResourcePlan.Add(new HostResourcePlanEntry(
                    StepId: node.Id,
                    Requirement: hostReq));
            }

            // ── Compile references (tolerant) ─────────────────────────────────
            if (instance is ICompileReferenceContributor cr)
            {
                foreach (var asm in cr.CompileReferenceAssemblies)
                {
                    var loc = asm.Location;
                    if (!string.IsNullOrEmpty(loc) && compileRefLocations.Add(loc))
                    {
                        compileRefPaths.Add(loc);
                    }
                }
            }

            // ── Emit ──────────────────────────────────────────────────────────
            var fragment = ReflectEmit(instance, model, compileCtx);
            fragments.Add(new StepCompilePlan(
                StepId: node.Id,
                Fragment: fragment,
                Retry: node.VerifyMode == VerifyMode.Retry,
                TimeoutMs: node.Timeout is { } t ? (long)t.TotalMilliseconds : null,
                PollIntervalMs: null));
        }

        // SUT configuration surface (point 3): ScenarioRunner ALSO stages a container-reachable
        // alias of every webhook listener's callback URL under "<VarName>_container" (see
        // ScenarioRunner.ContainerVarSuffix). Reject — HERE, before the topology is even built —
        // a suite where that engine-synthesised alias collides with another, DISTINCT listener's
        // own VarName; without this guard the two Vars writes would race (whichever staged last
        // silently wins) and one listener's real callback URL would be replaced by an unrelated
        // listener's container-rewritten alias.
        // Scope: this guard is listener-VarName-vs-listener-VarName ONLY. It deliberately does
        // NOT check author `variables:` block entries or step `capture:` names against the
        // "<VarName>_container" alias — those follow the existing forward-only Vars threading
        // idiom (a later write legitimately overrides an earlier one; see the "deliberately
        // overrides it" comment where ScenarioRunner stages the plain <VarName> key), which is a
        // different, already-accepted collision model from the one this guard closes.
        var distinctListenerVarNames = hostResourcePlan
            .Where(e => string.Equals(e.Requirement.Kind, ScenarioRunner.WebhookListenerKind, StringComparison.Ordinal))
            .Select(e => e.Requirement.VarName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var varName in distinctListenerVarNames)
        {
            var containerVarName = varName + ScenarioRunner.ContainerVarSuffix;
            if (distinctListenerVarNames.Contains(containerVarName))
            {
                return new PipelineResult(
                    Assembled: null,
                    ResourcePlan: Array.Empty<ResourcePlanEntry>(),
                    CompileReferencePaths: Array.Empty<string>(),
                    HostResourcePlan: Array.Empty<HostResourcePlanEntry>(),
                    Failure: new ValidationFailure(
                        $"webhook-listen listener '{containerVarName}' collides with the engine-" +
                        $"synthesised container-reachable alias of listener '{varName}' (staged at " +
                        $"'{varName}{ScenarioRunner.ContainerVarSuffix}'). Rename one of the two " +
                        "listeners so the alias is unambiguous."));
            }
        }

        var assembled = CsxAssembler.Assemble(fragments);

        return new PipelineResult(
            Assembled: assembled,
            ResourcePlan: resourcePlan,
            CompileReferencePaths: compileRefPaths,
            HostResourcePlan: hostResourcePlan,
            Failure: null);
    }

    // ── Reflection dispatch helpers ────────────────────────────────────────────

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
    /// Reflects the closed <see cref="IResourceContributor{TModel}"/> interface on
    /// <paramref name="instance"/> and, when present, invokes <c>Resources</c>.
    /// </summary>
    /// <remarks>
    /// This method is <em>tolerant</em>: when <paramref name="instance"/> does not
    /// implement <see cref="IResourceContributor{TModel}"/> it returns an empty
    /// enumerable rather than throwing.
    /// </remarks>
    private static IEnumerable<ResourceRequirement> ReflectResources(
        IStepProvider instance,
        object model)
    {
        var contributorInterface = instance.GetType()
            .GetInterfaces()
            .FirstOrDefault(i =>
                i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IResourceContributor<>));

        if (contributorInterface is null)
            return Array.Empty<ResourceRequirement>();

        var resourcesMethod = contributorInterface.GetMethod(
            nameof(IResourceContributor<IStepModel>.Resources),
            BindingFlags.Public | BindingFlags.Instance);

        if (resourcesMethod is null)
            return Array.Empty<ResourceRequirement>();

        var result = resourcesMethod.Invoke(instance, new object[] { model });
        return result as IEnumerable<ResourceRequirement>
            ?? Array.Empty<ResourceRequirement>();
    }

    /// <summary>
    /// Reflects the closed <see cref="IHostResourceContributor{TModel}"/> interface on
    /// <paramref name="instance"/> and, when present, invokes <c>HostResources</c>
    /// (S07-F-01a).
    /// </summary>
    /// <remarks>
    /// This method is <em>tolerant</em>, exactly like <see cref="ReflectResources"/>: when
    /// <paramref name="instance"/> does not implement
    /// <see cref="IHostResourceContributor{TModel}"/> it returns an empty enumerable rather
    /// than throwing, so an absent host-resource contribution costs nothing and keeps the
    /// no-listener path off the hot path.
    /// </remarks>
    private static IEnumerable<HostResourceRequirement> ReflectHostResources(
        IStepProvider instance,
        object model)
    {
        var contributorInterface = instance.GetType()
            .GetInterfaces()
            .FirstOrDefault(i =>
                i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IHostResourceContributor<>));

        if (contributorInterface is null)
            return Array.Empty<HostResourceRequirement>();

        var hostResourcesMethod = contributorInterface.GetMethod(
            nameof(IHostResourceContributor<IStepModel>.HostResources),
            BindingFlags.Public | BindingFlags.Instance);

        if (hostResourcesMethod is null)
            return Array.Empty<HostResourceRequirement>();

        var result = hostResourcesMethod.Invoke(instance, new object[] { model });
        return result as IEnumerable<HostResourceRequirement>
            ?? Array.Empty<HostResourceRequirement>();
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

    /// <summary>
    /// Builds a <see cref="RunProjectContext"/> from the scenario AST's
    /// <c>environment.dependencies</c> section (Sprint-4).
    /// </summary>
    /// <param name="ast">The normalised scenario AST.</param>
    /// <returns>
    /// A <see cref="RunProjectContext"/> whose
    /// <see cref="RunProjectContext.DeclaredDependencies"/> map contains every
    /// declared dependency name mapped to its type string.  Returns
    /// <see cref="RunProjectContext.Empty"/> when the scenario omits the
    /// <c>environment.dependencies</c> section.
    /// </returns>
    private static RunProjectContext BuildProjectContext(ScenarioAst ast)
    {
        var deps = ast.Environment?.Dependencies;
        if (deps is null || deps.Count == 0)
            return RunProjectContext.Empty;

        var map = new Dictionary<string, string>(deps.Count, StringComparer.Ordinal);
        foreach (var kv in deps)
            map[kv.Key] = kv.Value.Type;

        return new RunProjectContext(map);
    }
}
