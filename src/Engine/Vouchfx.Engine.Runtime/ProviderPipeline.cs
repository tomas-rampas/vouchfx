// Vouchfx.Engine.Runtime — ProviderPipeline (S04-B-01).
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
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Runtime;

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
internal sealed record ValidationFailure(string Message)
{
    /// <summary>
    /// <see langword="true"/> only for a failure raised by
    /// <see cref="EnvironmentSecurityValidator"/>'s pre-topology security preflight
    /// (path containment/existence for a declared <c>security</c> artefact);
    /// <see langword="false"/> for every other <see cref="ValidationFailure"/> in this
    /// pipeline (a step's own bind/validate failure, the registry-lookup internal
    /// error, a host-resource collision, …). Init-only rather than a constructor
    /// parameter so every existing <c>new ValidationFailure(message)</c> call site
    /// keeps compiling unchanged and defaults to <see langword="false"/>; only
    /// <see cref="EnvironmentSecurityValidator"/>'s own failure sites set it
    /// <see langword="true"/> via an object initializer.
    /// </summary>
    /// <remarks>
    /// A narrow, distinguishable signal that survives untouched through
    /// <see cref="PipelineResult.Failure"/> (this record IS that field's value, so no
    /// separate plumbing is needed) to the exit-code decision in a LATER slice: PR D
    /// keys the REQ-018 unconditional non-zero exit on this marker; REQ-018's own
    /// mechanism list (§REQ-005/REQ-018) is illustrative, not exhaustive — this marker
    /// is the pipeline-path signal that distinguishes a security-preflight rejection
    /// from an ordinary authoring-error Inconclusive. This PR does not itself change
    /// any verdict mapping, exit code, or <c>ScenarioRunner</c> flow.
    /// </remarks>
    public bool IsSecurityPreflight { get; init; }
}

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
    /// <see cref="Vouchfx.Engine.Authoring.AstBuilder.Build"/>.
    /// </param>
    /// <param name="registry">
    /// The frozen provider registry used to look up the provider instance for each
    /// step's canonical type.
    /// </param>
    /// <param name="suiteNamespace">
    /// The C# namespace injected into every <see cref="RunCompileContext"/> during
    /// the emit stage.
    /// </param>
    /// <param name="suiteDirectory">
    /// The base directory relative file-path step fields (e.g.
    /// <c>script.csharp</c>'s <c>file</c>) are resolved against — the same base
    /// directory used to resolve <c>environment.seed</c> fixture paths. Pass
    /// <see langword="null"/> to fall back to the process's current directory.
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
        string suiteNamespace,
        string? suiteDirectory = null)
    {
        var resolvedSuiteDirectory = suiteDirectory ?? Directory.GetCurrentDirectory();
        var fragments = new List<StepCompilePlan>(ast.Steps.Count);
        var resourcePlan = new List<ResourcePlanEntry>();
        var hostResourcePlan = new List<HostResourcePlanEntry>();
        var compileRefLocations = new HashSet<string>(StringComparer.Ordinal);
        var compileRefPaths = new List<string>();

        // Build the declared-dependencies map once for the whole pipeline run.
        // This is derived from environment.dependencies (name → Type) and is
        // empty when the scenario omits the environment section (Sprint-4).
        var projectCtx = BuildProjectContext(
            ast, resolvedSuiteDirectory, registry, out var hostResourceServiceCollision);

        // G5 (gatekeeper MAJOR-5): a step's own host-resource contribution (e.g. a
        // webhook-listen.http listener) named identically to a DECLARED SERVICE must be
        // rejected here, before any Roslyn compile or topology build — see
        // BuildProjectContext's own remarks for why the collision is a real, silent
        // shadowing risk (ScenarioRunner stages both under the SAME 'svc::<name>' Vars
        // key, keyed only by name).
        if (hostResourceServiceCollision is not null)
        {
            return new PipelineResult(
                Assembled: null,
                ResourcePlan: Array.Empty<ResourcePlanEntry>(),
                CompileReferencePaths: Array.Empty<string>(),
                HostResourcePlan: Array.Empty<HostResourcePlanEntry>(),
                Failure: hostResourceServiceCollision);
        }

        // Environment-level security-artefact validation (authenticated-infrastructure-
        // mtls, PR A): path containment (REQ-003, EDGE-006) then existence (REQ-004) for
        // every DECLARED path-valued field under environment.services/dependencies'
        // 'security' blocks. Runs once, before any step's own bind/validate/emit, so a
        // suite with an escaping or missing security artefact fails here — at
        // `vouchfx validate` / pre-topology `vouchfx run` time — rather than surfacing
        // later as an opaque container-startup or TLS-handshake failure.
        var environmentSecurityFailure = EnvironmentSecurityValidator.Validate(ast, resolvedSuiteDirectory);
        if (environmentSecurityFailure is not null)
        {
            return new PipelineResult(
                Assembled: null,
                ResourcePlan: Array.Empty<ResourcePlanEntry>(),
                CompileReferencePaths: Array.Empty<string>(),
                HostResourcePlan: Array.Empty<HostResourcePlanEntry>(),
                Failure: environmentSecurityFailure);
        }

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
            var compileCtx = new RunCompileContext(node.Id, suiteNamespace, resolvedSuiteDirectory, node.Capture);

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

        // Cross-kind VarName collision guard: reject a VarName claimed by host resources of
        // MORE THAN ONE kind (e.g. a webhook-listen.http listener named "shared" alongside a
        // trace-expect.otlp receiver ALSO named "shared"). ScenarioRunner stages EVERY kind's
        // resource into the SAME three Vars keys (svc::<VarName> / <VarName> / <VarName>
        // + ContainerVarSuffix), keyed ONLY by VarName — it has no way to know the two
        // requirements came from different kinds — so two DISTINCT resources sharing one
        // VarName would silently last-write-wins collide (whichever host resource the runner
        // starts last overwrites the other's staged URL), exactly the class of bug the
        // "_container" alias guard below closes from the other direction. Checked BEFORE that
        // alias guard so the more direct collision is reported first.
        var varNamesByKind = hostResourcePlan
            .GroupBy(e => e.Requirement.VarName, StringComparer.Ordinal)
            .Where(g => g.Select(e => e.Requirement.Kind).Distinct(StringComparer.Ordinal).Count() > 1)
            .ToList();

        foreach (var group in varNamesByKind)
        {
            var kinds = group
                .Select(e => e.Requirement.Kind)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal);

            return new PipelineResult(
                Assembled: null,
                ResourcePlan: Array.Empty<ResourcePlanEntry>(),
                CompileReferencePaths: Array.Empty<string>(),
                HostResourcePlan: Array.Empty<HostResourcePlanEntry>(),
                Failure: new ValidationFailure(
                    $"host resource '{group.Key}' is declared by more than one kind " +
                    $"({string.Join(", ", kinds)}). Each host-resource VarName must be claimed " +
                    "by exactly one kind — a webhook listener and an OTLP receiver (or any two " +
                    "distinct host-resource kinds) cannot share the same name. Rename one of them."));
        }

        // SUT configuration surface (point 3): ScenarioRunner ALSO stages a container-reachable
        // alias of every webhook listener's / OTLP receiver's URL under "<VarName>_container"
        // (see ScenarioRunner.ContainerVarSuffix — the OTLP receiver added in Phase C stages the
        // SAME alias for the SAME host.docker.internal reason). Reject — HERE, before the
        // topology is even built — a suite where that engine-synthesised alias collides with
        // another, DISTINCT host resource's own VarName (of EITHER kind); without this guard the
        // two Vars writes would race (whichever staged last silently wins) and one resource's
        // real URL would be replaced by an unrelated resource's container-rewritten alias.
        // Scope: this guard is host-resource-VarName-vs-host-resource-VarName ONLY, across both
        // kinds. It deliberately does NOT check author `variables:` block entries or step
        // `capture:` names against the "<VarName>_container" alias — those follow the existing
        // forward-only Vars threading idiom (a later write legitimately overrides an earlier
        // one; see the "deliberately overrides it" comment where ScenarioRunner stages the plain
        // <VarName> key), which is a different, already-accepted collision model from the one
        // this guard closes.
        var distinctListenerVarNames = hostResourcePlan
            .Where(e =>
                string.Equals(e.Requirement.Kind, ScenarioRunner.WebhookListenerKind, StringComparison.Ordinal)
                || string.Equals(e.Requirement.Kind, ScenarioRunner.OtlpReceiverKind, StringComparison.Ordinal))
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
                        $"host resource '{containerVarName}' collides with the engine-" +
                        $"synthesised container-reachable alias of host resource '{varName}' (staged at " +
                        $"'{varName}{ScenarioRunner.ContainerVarSuffix}'). Rename one of the two " +
                        "host resources (webhook listeners / OTLP receivers) so the alias is unambiguous."));
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
    /// <c>environment.dependencies</c> (Sprint-4), <c>environment.services</c>
    /// (services-generalisation spec, REQ-010), AND every step's own
    /// <see cref="IHostResourceContributor{TModel}"/> contribution (S07-F-01a) — a
    /// webhook-listen.http <c>listener:</c> name or a trace-expect.otlp receiver name, for
    /// example — since those are ALSO valid <c>svc::&lt;name&gt;</c>-staged targets an
    /// <c>http.rest</c>/<c>http.soap</c>/<c>metrics-assert.prometheus</c> step may
    /// legitimately dial (see <c>ScenarioRunner</c>'s own remark that it "stages EVERY
    /// kind's resource into the SAME three Vars keys… keyed ONLY by VarName").
    /// </summary>
    /// <param name="ast">The normalised scenario AST.</param>
    /// <param name="suiteDirectory">
    /// The base directory relative file-path step fields are resolved against.
    /// </param>
    /// <param name="registry">
    /// The frozen step-kind registry, needed to bind each step's model and reflect its
    /// <see cref="IHostResourceContributor{TModel}"/> contribution (if any) — a lightweight
    /// PRE-PASS over every step, independent of and before the main
    /// <see cref="Compile"/> loop's own bind/validate/emit pass. This pre-pass is required
    /// because host-resource contributions can be declared by a LATER step than the one
    /// that references them (e.g. <c>await-callback</c>, declaring listener <c>cb</c>, comes
    /// AFTER <c>trigger-callback</c>, which targets <c>cb</c>) — a single linear pass
    /// could not see step 3's contribution while validating step 2.  <c>Bind</c> is safe to
    /// call twice per step (once here, once again in <see cref="Compile"/>'s own loop): every
    /// CORE provider's <c>Bind</c> is a defensive, side-effect-free data-binding function in
    /// this codebase's own established convention (a malformed node yields a "safe empty"
    /// model rather than throwing; <c>Validate</c> is what rejects it) — but this is NOT a
    /// universal guarantee (G6, gatekeeper): <c>http.rest</c>'s own <c>Bind</c> serialises a
    /// structured <c>body:</c> via unbounded recursion over the YAML tree
    /// (<c>HttpRestProvider.YamlToJsonElement</c>), which can throw on a sufficiently deep or
    /// malformed body — so the <c>try</c>/<c>catch</c> below is a LOAD-BEARING guard today,
    /// for at least this one shipped Core provider, not merely belt-and-braces for a
    /// hypothetical future one. The main <see cref="Compile"/> loop's OWN <c>Bind</c> call
    /// (immediately after, unchanged) remains the authoritative path that surfaces any real
    /// binding problem for THAT step; other steps' host resources are still discovered
    /// normally when one step's pre-pass throws.
    /// </param>
    /// <param name="collisionFailure">
    /// Set to a non-null <see cref="ValidationFailure"/> (G5, gatekeeper MAJOR-5) when some
    /// step's own host-resource contribution (e.g. a <c>webhook-listen.http</c> listener) is
    /// named identically to a DECLARED SERVICE (<c>environment.services.&lt;name&gt;</c>) —
    /// <see langword="null"/> otherwise. <c>ScenarioRunner</c> stages EVERY host resource and
    /// every declared service's endpoint under the SAME <c>svc::&lt;name&gt;</c> Vars key,
    /// keyed only by name, so an undetected collision would let a host resource silently
    /// shadow the declared service — an <c>http.rest</c> step targeting the service's name
    /// could then Pass having talked only to the engine's own listener. Callers must check
    /// this before trusting the returned <see cref="RunProjectContext"/> — see
    /// <see cref="Compile"/>'s own call site, which maps a non-null value straight to
    /// <see cref="PipelineResult.Failure"/> without using the (still-returned, but now
    /// unreliable) context at all.
    /// </param>
    /// <returns>
    /// A <see cref="RunProjectContext"/> whose
    /// <see cref="RunProjectContext.DeclaredDependencies"/> map contains every
    /// declared dependency name mapped to its type string, and whose
    /// <see cref="RunProjectContext.DeclaredServices"/> map contains every declared
    /// service name (mapped to the endpoint/port names it exposes, via
    /// <see cref="ServiceEndpointNaming.DeclaredEndpointNames"/> — the SAME naming
    /// convention <c>EnvironmentMapper</c> uses to build the actual Aspire endpoints, so
    /// the two can never silently drift apart) MERGED with every step's own host-resource
    /// contribution (mapped to its <see cref="HostResourceRequirement.Kind"/>, e.g.
    /// <c>["webhook-listener"]</c>).  Returns <see cref="RunProjectContext.Empty"/> only
    /// when ALL THREE sources are empty. When <paramref name="collisionFailure"/> is set,
    /// this value is still returned (never <see langword="null"/>) but callers must not
    /// use it — see that parameter's own remarks.
    /// </returns>
    /// <remarks>
    /// <c>internal</c> (not <c>private</c>) so <c>Vouchfx.Engine.Runtime.Tests</c> — already
    /// granted access via this assembly's <c>InternalsVisibleTo</c> — can exercise the
    /// services-derivation wiring directly, without needing a full <see cref="Compile"/>
    /// round trip through a stub provider that happens to capture its <c>IProjectContext</c>.
    /// </remarks>
    internal static RunProjectContext BuildProjectContext(
        ScenarioAst ast, string suiteDirectory, StepKindRegistry registry,
        out ValidationFailure? collisionFailure)
    {
        collisionFailure = null;
        var deps = ast.Environment?.Dependencies;
        var services = ast.Environment?.Services;

        var depMap = new Dictionary<string, string>(deps?.Count ?? 0, StringComparer.Ordinal);
        if (deps is not null)
        {
            foreach (var kv in deps)
                depMap[kv.Key] = kv.Value.Type;
        }

        var serviceMap = new Dictionary<string, IReadOnlyList<string>>(
            services?.Count ?? 0, StringComparer.Ordinal);
        // G5 (gatekeeper MAJOR-5): tracks which serviceMap keys came from a DECLARED
        // SERVICE, as opposed to a step's own host-resource contribution merged in by the
        // pre-pass below — the two are declared through completely different surfaces
        // (environment.services vs. a step's IHostResourceContributor) and must never
        // silently collide; see collisionFailure's own remarks.
        var declaredServiceNames = new HashSet<string>(StringComparer.Ordinal);
        if (services is not null)
        {
            foreach (var kv in services)
            {
                serviceMap[kv.Key] = ServiceEndpointNaming.DeclaredEndpointNames(kv.Value);
                declaredServiceNames.Add(kv.Key);
            }
        }

        // Pre-pass: every step's own IHostResourceContributor requirement (S07-F-01a) is
        // ALSO a valid svc::<name> target, regardless of step order (see this method's own
        // <param name="registry"> remarks for why a pre-pass, not the main Compile() loop
        // itself, must discover these).
        foreach (var node in ast.Steps)
        {
            if (!registry.TryGet(node.CanonicalType, out var rp) || rp is null)
                continue;

            var instance = rp.Instance;
            var bindingCtx = new RunBindingContext();
            try
            {
                var model = ReflectBind(instance, node.RawNode, bindingCtx);

                // G6 (gatekeeper MAJOR-6b): ReflectHostResources' own enumeration moved
                // INSIDE this try — HostResourceRequirement's constructor validates both
                // Kind and VarName (ArgumentException.ThrowIfNullOrEmpty), and since
                // IHostResourceContributor.HostResources is very plausibly implemented as
                // a C# iterator (yield return), that validation runs lazily, the instant
                // THIS foreach calls MoveNext() — which used to sit OUTSIDE the try, so a
                // community provider's HostResources() throwing on an unvalidated model
                // (a Bind-produced "safe empty" model with an empty/null field) would abort
                // this ENTIRE pre-pass uncaught, rather than merely omitting that one step's
                // own contribution the way a throwing Bind already does.
                foreach (var hostReq in ReflectHostResources(instance, model))
                {
                    // Guarded on the COLLISION itself, never on 'collisionFailure is null':
                    // a second colliding host resource must also take the 'continue' below
                    // rather than fall through to the overwrite this guard exists to prevent.
                    // (Unreachable in effect today — Compile returns the failure before this
                    // context is read — but a guard whose correctness depends on its caller
                    // is a trap for whoever edits that caller next. '??=' keeps the FIRST
                    // collision's message, which is the one an author fixes first.)
                    if (declaredServiceNames.Contains(hostReq.VarName))
                    {
                        collisionFailure ??= new ValidationFailure(
                            $"host resource '{hostReq.VarName}' (kind '{hostReq.Kind}', declared by " +
                            $"step '{node.Id}') collides with a DECLARED SERVICE of the same name " +
                            $"(environment.services.{hostReq.VarName}). A host resource (e.g. a " +
                            "webhook listener) cannot share a name with a declared service — rename " +
                            "one of the two.");
                        continue;
                    }

                    serviceMap[hostReq.VarName] = new[] { hostReq.Kind };
                }
            }
            catch
            {
                // See this method's own <param name="registry"> remarks: Bind is
                // defensive/non-throwing for every CORE provider today, but http.rest's own
                // Bind is a measured exception (unbounded YamlToJsonElement recursion), and a
                // community provider's HostResources() enumerator can likewise throw. Either
                // way, this pre-pass must never let one step's exception abort context
                // construction for every OTHER step — the main Compile() loop's OWN
                // Bind/HostResources calls (immediately after, unchanged) remain the
                // authoritative path that surfaces any real problem for THAT step.
                continue;
            }
        }

        if (deps is { Count: > 0 } || serviceMap.Count > 0)
        {
            return new RunProjectContext(depMap, suiteDirectory, serviceMap);
        }

        return RunProjectContext.Empty(suiteDirectory);
    }
}
