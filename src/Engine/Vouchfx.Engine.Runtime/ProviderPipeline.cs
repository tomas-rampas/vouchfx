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
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Orchestration;
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
/// One step's <see cref="IStepBinder{TModel}.Bind"/> result, retained across
/// <see cref="ProviderPipeline.Compile"/>'s two passes (M5 fix, fix round 2 — see the
/// class remarks for the redesign this replaces).
/// </summary>
/// <param name="Node">The step's normalised AST node.</param>
/// <param name="Instance">The resolved provider instance for this step's canonical type.</param>
/// <param name="Model">The bound step model, produced by exactly ONE <c>Bind</c> call.</param>
/// <param name="HostResources">
/// This step's <see cref="IHostResourceContributor{TModel}"/> contribution, materialised
/// (via <c>.ToList()</c>) once here rather than left as a lazy enumerable — it is read twice
/// downstream (once by <see cref="ProviderPipeline.BuildProjectContext"/> to derive
/// <c>DeclaredServices</c>, once by <see cref="ProviderPipeline.Compile"/>'s second pass to
/// populate <see cref="HostResourcePlanEntry"/>) and a lazy C# iterator would otherwise
/// re-execute its body — including <see cref="HostResourceRequirement"/>'s own constructor
/// validation — on each enumeration. When <see cref="HostResourcesFailure"/> is set, this is
/// an empty list (<see cref="ProviderPipeline.BindAllSteps"/>'s catch assigns a fresh
/// <see cref="List{T}"/>, not <see cref="Array.Empty{T}"/> — semantically identical, since
/// nothing distinguishes the two beyond identity, but named accurately here rather than
/// naming an API the code does not actually call) — never a partially-materialised list.
/// </param>
/// <param name="HostResourcesFailure">
/// G-A (gatekeeper, fix round 3): captures an exception thrown while materialising
/// <paramref name="HostResources"/> in <see cref="ProviderPipeline.BindAllSteps"/> (Pass 1),
/// rather than letting it propagate immediately — see <see cref="ProviderPipeline.BindAllSteps"/>'s
/// own remarks for why. <see langword="null"/> when materialisation succeeded (the overwhelming
/// majority of steps; every Core provider's <c>HostResources</c> is a pure, throw-free
/// projection). <see cref="ProviderPipeline.Compile"/>'s Pass 2 rethrows this — preserving the
/// original stack trace via <see cref="System.Runtime.ExceptionServices.ExceptionDispatchInfo"/>
/// — immediately after this step's own <c>Validate</c> has had a chance to produce a clean
/// diagnostic for whatever invalid model condition may have caused it.
/// </param>
internal sealed record BoundStep(
    Vouchfx.Engine.Authoring.Ast.StepNode Node,
    IStepProvider Instance,
    object Model,
    IReadOnlyList<HostResourceRequirement> HostResources,
    System.Runtime.ExceptionServices.ExceptionDispatchInfo? HostResourcesFailure = null);

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
    /// <see langword="true"/> for a failure raised by either of this pipeline's two
    /// pre-topology security-preflight checks — <see cref="EnvironmentSecurityValidator"/>
    /// (path containment/existence for a declared <c>security</c> artefact, REQ-003/REQ-004)
    /// or <see cref="SecurityProfileWiringValidator"/> (an unresolved <c>(profile, target-kind)</c>
    /// pair, REQ-022, G-MINOR-1) — both called from <see cref="ProviderPipeline.Compile"/>
    /// back-to-back, before any step is bound; <see langword="false"/> for every other
    /// <see cref="ValidationFailure"/> in this pipeline (a step's own bind/validate failure,
    /// the registry-lookup internal error, a host-resource collision, …). Init-only rather
    /// than a constructor parameter so every existing <c>new ValidationFailure(message)</c>
    /// call site keeps compiling unchanged and defaults to <see langword="false"/>; only
    /// these two producers' own failure sites set it <see langword="true"/> via an object
    /// initializer.
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
    /// <para>
    /// G-MINOR-1 (gatekeeper, this slice): <see cref="SecurityProfileWiringValidator"/>'s
    /// own failure site (added for REQ-022) also sets this marker true, widening it beyond
    /// its original "EnvironmentSecurityValidator only" scope. Recorded here as a DELIBERATE
    /// decision, not an oversight the doc comment merely failed to keep up with: a REQ-022
    /// wiring failure — a declared <c>security.profile</c> that resolves to no registered
    /// wiring for its target kind — is exactly as much a security-preflight rejection as a
    /// missing certificate file, and should not silently inherit the ordinary Inconclusive
    /// exit-code path REQ-018's later exit-code decision treats every OTHER validation
    /// failure as falling into by default.
    /// </para>
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
    /// <param name="securityProfileRegistry">
    /// The security-profile registry <see cref="SecurityProfileWiringValidator"/> checks every
    /// declared <c>security</c> block's <c>(profile, target-kind)</c> pair against (REQ-022).
    /// Defaults to <see cref="SecurityProfileRegistry.BuiltIn"/> when omitted — the production
    /// registry. Injectable (G-MINOR-8, gatekeeper) so a caller can prove REQ-022's own
    /// red-first behaviour end-to-end through THIS front door with a reduced/explicit registry,
    /// mirroring how <paramref name="registry"/> itself is always a parameter rather than a
    /// hardcoded <c>StepKindRegistry</c> default.
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
        string? suiteDirectory = null,
        SecurityProfileRegistry? securityProfileRegistry = null)
    {
        var resolvedSuiteDirectory = suiteDirectory ?? Directory.GetCurrentDirectory();
        var fragments = new List<StepCompilePlan>(ast.Steps.Count);
        var resourcePlan = new List<ResourcePlanEntry>();
        var hostResourcePlan = new List<HostResourcePlanEntry>();
        var compileRefLocations = new HashSet<string>(StringComparer.Ordinal);
        var compileRefPaths = new List<string>();

        // Environment-level security-artefact validation (authenticated-infrastructure-
        // mtls, PR A): path containment (REQ-003, EDGE-006) then existence (REQ-004) for
        // every DECLARED path-valued field under environment.services/dependencies'
        // 'security' blocks. Runs FIRST — before any step is bound — for two reasons.
        // It reads only ast.Environment, so it needs nothing from a bound model. And a
        // suite with a broken security artefact must report THAT, cleanly, even when it
        // also contains a step whose Bind throws: Bind is unguarded here (deliberately —
        // see BindAllSteps' remarks), so binding first would let a provider-bug exception
        // pre-empt a diagnosable environment error. Keeping this check ahead of Pass 1
        // preserves the failure precedence the previous pre-pass design happened to give,
        // without the speculative second Bind that design needed.
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

        // Security-profile wiring invariant (authenticated-infrastructure-mtls, slice C —
        // REQ-022): for every declared 'security' block, the (profile, target-kind) pair must
        // resolve to a registered wiring — closing the false-assurance gap REQ-021's
        // schema-level narrowing alone cannot (see SecurityProfileWiringValidator's own header
        // remarks). Runs immediately after the artefact preflight above, same stage, same
        // reasoning: reads only ast.Environment, so it needs nothing from a bound model.
        var securityProfileWiringFailure =
            SecurityProfileWiringValidator.Validate(ast, securityProfileRegistry ?? SecurityProfileRegistry.BuiltIn);
        if (securityProfileWiringFailure is not null)
        {
            return new PipelineResult(
                Assembled: null,
                ResourcePlan: Array.Empty<ResourcePlanEntry>(),
                CompileReferencePaths: Array.Empty<string>(),
                HostResourcePlan: Array.Empty<HostResourcePlanEntry>(),
                Failure: securityProfileWiringFailure);
        }

        // REQ-023 (amended 2026-08-04): one target, one staged form. A target addressed by BOTH
        // the HTTP family (which consumes an https:// URL) and the Kafka families (which consume
        // a bare host:port bootstrap authority) cannot be staged correctly for both, and picking a
        // winner would hand the loser a value it must transform to use — the thing that
        // requirement forbids — silently, at run time. Rejected here, at the same pre-topology
        // stage as the two checks above and for the same reason: it reads only the raw steps and
        // needs nothing from a bound model. This narrows nothing that ever worked; before REQ-023
        // was amended, a Kafka step naming a service failed as an EnvironmentError every time.
        //
        // SCOPE: this method compiles ONE scenario, so this call sees one scenario's steps. Where
        // one scenario IS the unit that gets a topology AND this method runs before that topology is
        // built, that is the complete check: the single-scenario `run` and `--parallel` (each
        // scenario owns its own topology via RunScenarioOwningTopologyAsync) — both MEASURED as
        // ordered Compile-then-StartAsync in ScenarioRunner.RunScenarioOwningTopologyAsync. It is
        // also the only check `vouchfx validate` can make, since ScenarioValidator treats each file
        // independently by design and never decides which files form a suite.
        //
        // TWO PATHS THIS CALL DOES NOT COVER, corrected by measurement (MAJOR-2, fix round five —
        // the previous wording claimed `--watch` among the complete-coverage set, which is false):
        //
        //   • The SHARED-topology `run` stages from the union across the RUNNABLE scenarios — those
        //     carrying no early verdict, since a scenario that executes nothing stages nothing — so
        //     a suite splitting the two families across two files is individually innocent per
        //     scenario and collectively in conflict. It therefore carries its OWN call to the same
        //     helper at its own seam (ScenarioRunner.RunSuiteAsync), from the SAME local it stages
        //     from, so the guard and the staging cannot disagree about the set. Both seams call
        //     this one helper, which owns the single spelling of the diagnostic (gatekeeper MAJOR,
        //     fix round four — before it, the message was written out at this call site alone).
        //
        //   • `--watch` runs this check AFTER the staging it protects rather than before it. The
        //     watch compile seam (WatchRunner.Compile) is YamlDocumentParser.Parse +
        //     AstBuilder.Build only; the topology — with kafkaSpeakingTargets already computed from
        //     the AST — is built at the watch BUILD seam, and this method is not reached until the
        //     RUN seam (ScenarioRunner.RunScenarioAgainstKeptTopologyAsync). A conflicting suite
        //     under `--watch` therefore starts containers and is rejected against them, rather than
        //     before them. It still fails closed with this same diagnostic — but the containers are
        //     NOT torn down with it, and the previous wording of this note claimed they were
        //     (gatekeeper MAJOR-2 + spec-compliance, fix round six; fix round five deleted one false
        //     `--watch` claim from this paragraph and replaced it with a different one).
        //
        //     MEASURED, three ways: WatchSession.OnChangeAsync disposes the kept topology ONLY on
        //     the `!canReuse` path, and `canReuse` is decided on the ENVIRONMENT HASH alone
        //     (WatchSession.cs — its own comment: "a steps-only edit keeps the hash"), while a
        //     protocol conflict is a steps-level fact; the run seam RETURNS Inconclusive rather
        //     than throwing; and WatchRunner.ProcessChangeGuardedAsync catches even a throw and
        //     keeps watching. So the cost is not the container time to build and discard a
        //     topology — it is that the topology stays up for the rest of the watch session, until
        //     the session ends or a later save changes the environment hash.
        //     Moving the watch compile seam onto the full validation stage is a wider, pre-existing
        //     change (it runs no DocumentValidator.Validate either) and is tracked separately.
        var protocolConflict = SuiteProtocolTargets.DescribeProtocolConflict(new[] { (ScenarioAst?)ast });
        if (protocolConflict is not null)
        {
            return new PipelineResult(
                Assembled: null,
                ResourcePlan: Array.Empty<ResourcePlanEntry>(),
                CompileReferencePaths: Array.Empty<string>(),
                HostResourcePlan: Array.Empty<HostResourcePlanEntry>(),
                Failure: new ValidationFailure(protocolConflict));
        }

        // ── Pass 1: Bind every step exactly ONCE, retaining the model and its
        // IHostResourceContributor contribution (M5 fix, fix round 2 — replaces the
        // previous speculative pre-pass that called Bind a SECOND time per step, purely
        // to discover host-resource names before the main loop reached that step). See
        // BuildProjectContext's own remarks for why deriving DeclaredServices needs
        // every step's host-resource contribution up front regardless of step order.
        var (boundSteps, registryFailure) = BindAllSteps(ast, registry);
        if (registryFailure is not null)
        {
            return new PipelineResult(
                Assembled: null,
                ResourcePlan: Array.Empty<ResourcePlanEntry>(),
                CompileReferencePaths: Array.Empty<string>(),
                HostResourcePlan: Array.Empty<HostResourcePlanEntry>(),
                Failure: registryFailure);
        }

        // Build the declared-services/dependencies project context from the retained
        // bindings above — no re-binding, no speculative second Bind call.
        var projectCtx = BuildProjectContext(
            ast, resolvedSuiteDirectory, boundSteps, out var hostResourceServiceCollision);

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

        // ── Pass 2: Validate / Resources / CompileReferences / Emit over the RETAINED
        // models from Pass 1 — no second Bind call for any step. ──────────────────────
        foreach (var bound in boundSteps)
        {
            var node = bound.Node;
            var instance = bound.Instance;
            var model = bound.Model;

            // S04-B-02 / S07-B-01a: pass the step's format-aware capture map
            // (varName → CaptureExpr) into the compile context so providers can emit
            // capture logic into the CSX block.  The context exposes both the typed
            // CaptureExprs view and the back-compatible expression-string Captures view.
            // REQ-023 (amended): DeclaredServices reaches Emit as well as Validate, from the SAME
            // projectCtx instance — a provider whose target may name a dependency or a service
            // (mq-publish.kafka / mq-expect.kafka, REQ-011) decides WHICH Vars key to emit here,
            // at compile time, rather than guessing at run time.
            var compileCtx = new RunCompileContext(
                node.Id,
                suiteNamespace,
                resolvedSuiteDirectory,
                node.Capture,
                projectCtx.DeclaredServices);

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

            // G-A (gatekeeper, fix round 3): a HostResources() throw captured back in Pass 1
            // (BindAllSteps) is rethrown HERE — after this exact step's OWN Validate, just
            // above, has already had its chance to turn whatever invalid model condition
            // triggered it into the clean ValidationFailure returned above instead. Reaching
            // this line means Validate found the model FINE, so whatever HostResources threw
            // is a genuine bug Validate does not already cover — surfaced as the same raw
            // exception it always was, just no longer able to pre-empt a diagnosable
            // environment/model error from a DIFFERENT, earlier-bound step (BindAllSteps runs
            // Pass 1 for every step before Pass 2 validates any of them). ExceptionDispatchInfo
            // preserves the original stack trace across the Pass 1 → Pass 2 deferral.
            bound.HostResourcesFailure?.Throw();

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
            // Reuses the SAME materialised list Pass 1 already collected — never
            // re-enumerated, so a lazy iterator's body (including
            // HostResourceRequirement's own ctor validation) runs exactly once per step
            // in total, not once per pass.
            foreach (var hostReq in bound.HostResources)
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
    /// Pass 1 of <see cref="Compile"/> (M5 fix, fix round 2): binds every step in
    /// <paramref name="ast"/> exactly ONCE, materialising each one's
    /// <see cref="IHostResourceContributor{TModel}"/> contribution alongside the model, and
    /// returns the retained list as <see cref="BoundStep"/> records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extracted into its own method — rather than inlined in <see cref="Compile"/> — so
    /// <see cref="BuildProjectContext"/>'s own unit tests (<c>Vouchfx.Engine.Runtime.Tests</c>,
    /// already granted <c>InternalsVisibleTo</c> access) can produce a real
    /// <see cref="BoundStep"/> list without needing a full <see cref="Compile"/> round trip,
    /// exactly the same reason <see cref="BuildProjectContext"/> itself stayed <c>internal</c>
    /// rather than <c>private</c>.
    /// </para>
    /// <para>
    /// R-1 residual (peer-review-critic, PR #349 fix round 4) — a cross-step trade the G-A
    /// fix (fix round 3) introduced and this remark documents rather than reverts. Deferring
    /// a throwing <c>HostResources()</c> onto ITS OWN step's
    /// <see cref="BoundStep.HostResourcesFailure"/> protects THAT step's own
    /// <see cref="ProviderPipeline.Compile"/> Pass-2 <c>Validate</c> call — see this method's
    /// own inline remarks above — but the catch below discards the WHOLE per-step
    /// <see cref="BoundStep.HostResources"/> list on any throw (<c>ToList()</c> does not
    /// return partial results, even when the throwing iterator had already produced some
    /// requirements before failing), so <see cref="BuildProjectContext"/> — which runs on
    /// EVERY bound step, between this pass and Pass 2, to derive
    /// <see cref="Vouchfx.Sdk.IProjectContext.DeclaredServices"/> — never sees that step's
    /// contribution at all. A DIFFERENT, EARLIER-ORDERED step that TARGETS the
    /// never-materialised name (e.g. a <c>webhook-listen.http</c> listener a
    /// <c>http.rest</c> step dials) then fails Pass 2's own <c>Validate</c> with a confident
    /// but WRONG "unknown target" diagnostic — naming the targeting step, not the one whose
    /// <c>HostResources()</c> actually threw — because Pass 2 returns on the FIRST failing
    /// step's <see cref="ValidationResult"/>, in step order, before the later, declaring
    /// step's own <see cref="BoundStep.HostResourcesFailure"/> is ever rethrown. This is
    /// accepted, not fixed: the alternative (propagate immediately, the pre-G-A behaviour)
    /// reintroduces the ORIGINAL bug G-A exists to prevent — a community provider's own
    /// model-shaped bug that its OWN <c>Validate</c> could have explained cleanly instead
    /// aborting the whole compile with a raw reflection exception, for EVERY step, not just
    /// the one whose <c>HostResources()</c> throws. Pinned by a two-step test in
    /// <c>ProviderPipelineTests</c> (<c>Compile_TargetingStepPrecedesThrowingListenerStep_
    /// ReturnsWrongTargetDiagnostic</c>), ordered exactly this way — the targeting step
    /// first, the declaring step second — so the trade does not decay into folklore.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The bound steps, and — only in the defensive "registry lookup failed after AST build
    /// already verified the type" case, which should never happen in practice — a non-null
    /// <see cref="ValidationFailure"/> instead (the bound-steps list is then incomplete and
    /// must not be used, mirroring every other early-return failure in this file).
    /// </returns>
    internal static (List<BoundStep> BoundSteps, ValidationFailure? RegistryFailure) BindAllSteps(
        ScenarioAst ast, StepKindRegistry registry)
    {
        var boundSteps = new List<BoundStep>(ast.Steps.Count);
        foreach (var node in ast.Steps)
        {
            if (!registry.TryGet(node.CanonicalType, out var rp) || rp is null)
            {
                // This should not happen: AstBuilder already verified the type.
                return (boundSteps, new ValidationFailure(
                    $"Internal error: provider '{node.CanonicalType}' missing from registry after AST build."));
            }

            var instance = rp.Instance;
            var bindingCtx = new RunBindingContext();

            // ── Bind (UNGUARDED — exactly as the pre-M5 main loop always called it:
            // IStepBinder<T>.Bind is not documented as safe to call more than once per
            // compile, so this redesign calls it EXACTLY once, and a throw here propagates
            // to Compile's own caller IMMEDIATELY, unchanged, same as it always did). A
            // throwing Bind means this step's own model could not even be constructed, so
            // there is no model for that step's Validate to examine — deferring it the way
            // HostResources' throw is deferred below would have nothing to defer TO. ──
            var model = ReflectBind(instance, node.RawNode, bindingCtx);

            // ── Host resources (tolerant, S07-F-01a) — GUARDED and DEFERRED (G-A,
            // gatekeeper, fix round 3; this half of the comment used to be missing — the
            // Bind half above justified only itself). Unlike Bind, a throwing HostResources
            // getter/enumerator is CAUGHT here rather than propagated: this method
            // (BindAllSteps) runs entirely in Pass 1, before ANY step's Validate has run, so
            // letting it propagate immediately — the pre-G-A behaviour — could abort the
            // whole compile before a community provider's OWN Validate ever got a chance to
            // turn whatever invalid model condition triggered the throw into a clean,
            // located diagnostic instead of a raw reflection exception. The exception is
            // captured onto this step's BoundStep and rethrown by Compile's Pass 2,
            // immediately after THAT step's own Validate has run (see BoundStep.
            // HostResourcesFailure's own remarks) — so a bad model that Validate can
            // explain never even reaches this rethrow, and only a genuine HostResources bug
            // Validate does NOT already cover surfaces as the raw exception it always was. ──
            List<HostResourceRequirement> hostResources;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? hostResourcesFailure = null;
            try
            {
                hostResources = ReflectHostResources(instance, model).ToList();
            }
            catch (Exception ex)
            {
                hostResources = new List<HostResourceRequirement>();
                hostResourcesFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }

            boundSteps.Add(new BoundStep(node, instance, model, hostResources, hostResourcesFailure));
        }

        return (boundSteps, null);
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
    /// <param name="boundSteps">
    /// Every step's RETAINED <see cref="BoundStep"/> from <see cref="Compile"/>'s Pass 1
    /// (M5 fix, fix round 2). This method no longer binds anything itself — it reads each
    /// step's already-materialised <see cref="BoundStep.HostResources"/> list, discovered
    /// regardless of step order, because host-resource contributions can be declared by a
    /// LATER step than the one that references them (e.g. <c>await-callback</c>, declaring
    /// listener <c>cb</c>, comes AFTER <c>trigger-callback</c>, which targets <c>cb</c>) — a
    /// single linear pass over <see cref="Compile"/>'s own Validate/Emit loop could not see
    /// step 3's contribution while validating step 2. Before this fix, discovering these
    /// ahead of step order required a SEPARATE, speculative pre-pass that called
    /// <c>Bind</c> a SECOND time per step (once here, once again in the main loop) — an
    /// undocumented obligation on the frozen v1 <c>IStepBinder&lt;T&gt;</c> contract this
    /// redesign removes entirely: one <c>Bind</c> call per step, full stop.
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
    /// service name (mapped to a <see cref="Vouchfx.Sdk.DeclaredServiceInfo"/> whose
    /// <see cref="Vouchfx.Sdk.DeclaredServiceInfo.EndpointNames"/> is populated via
    /// <see cref="ServiceEndpointNaming.DeclaredEndpointNames"/> — the SAME naming
    /// convention <c>EnvironmentMapper</c> uses to build the actual Aspire endpoints, so
    /// the two can never silently drift apart), MERGED with every dependency's own extra
    /// sidecar endpoint (M1 fix, fix round 3 — mailpit's SMTP endpoint, Kafka's optional
    /// schema-registry REST API — sourced from the SAME
    /// <see cref="EnvironmentMapper.GetDependencyServiceSidecarNames"/> enumeration the
    /// collision guard below already consults, so the two cannot disagree about which
    /// names are reachable), MERGED with every step's own host-resource
    /// contribution (its <c>EndpointNames</c> holding a single-element list carrying its
    /// own <see cref="HostResourceRequirement.Kind"/>, e.g. <c>["webhook-listener"]</c>).
    /// Returns <see cref="RunProjectContext.Empty"/> only
    /// when ALL FOUR sources are empty. When <paramref name="collisionFailure"/> is set,
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
        ScenarioAst ast, string suiteDirectory, IReadOnlyList<BoundStep> boundSteps,
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

        // m7 fix (fix round 2): a service and a dependency may not share a name. Before this
        // fix, a suite declaring both 'environment.services.orders' and
        // 'environment.dependencies.orders' validated PASS and only failed later, deep
        // inside Aspire's own AddContainer ("a resource with the same name already exists").
        // This method is the first place in the pipeline that holds BOTH name sets at once
        // (depMap, just built above, and the service names the loop below is about to
        // collect), so this is the natural, one-line home for the check — reported here,
        // before any builder mutation, exactly like every other eager cross-reference check
        // in this codebase's own established convention (see EnvironmentMapper.Map's own
        // service/dependency validation loops for the same idiom).
        if (services is not null)
        {
            foreach (var serviceName in services.Keys)
            {
                if (depMap.ContainsKey(serviceName))
                {
                    collisionFailure ??= new ValidationFailure(
                        $"'{serviceName}' is declared as both a service (environment.services." +
                        $"{serviceName}) and a dependency (environment.dependencies.{serviceName}). " +
                        "A service and a dependency cannot share a name — rename one of the two.");
                }
            }
        }

        var serviceMap = new Dictionary<string, Vouchfx.Sdk.DeclaredServiceInfo>(
            services?.Count ?? 0, StringComparer.Ordinal);

        // m1 fix (fix round 2): the guard below rejects a host resource whose VarName
        // collides with ANY svc::<name>-shaped key another surface already owns — NOT only
        // a declared service. The previous version guarded only DECLARED SERVICE names, on
        // the stated (but WRONG) reasoning that dependencies stage exclusively into
        // conn::<name>. That does not hold: two dependency kinds ALSO stage a svc::<name>
        // sidecar key — mailpit's SMTP endpoint (svc::<dep>-smtp) and kafka's optional
        // schema-registry sidecar (svc::<dep>-sr) — and ScenarioRunner routes both to
        // VarKeys.Service(...) (neither suffixed name is in DependencyNames), staging host
        // resources afterwards into the SAME dictionary, last write wins. Before this fix, a
        // listener named 'mail-smtp' alongside a mailpit dependency 'mail', or a listener
        // named 'bus-sr' alongside a kafka dependency 'bus' with 'schemaRegistry: true',
        // both validated PASS — and the '-sr' key specifically is read at run time by both
        // Kafka providers, so an Avro publish would have sent schema-registry traffic to the
        // engine's own listener. reservedSvcNames maps every such reserved name to a
        // human-readable description of what actually owns it, sourced from
        // EnvironmentMapper.GetDependencyServiceSidecarNames — the SAME declarative helper
        // the dependency Build lambdas' own sidecar-naming functions back onto (see that
        // method's remarks) — so this guard's name set cannot drift from what Configure
        // actually stages the way the old, hand-maintained "dependencies never collide"
        // assumption did.
        var reservedSvcNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (services is not null)
        {
            foreach (var kv in services)
            {
                serviceMap[kv.Key] = new Vouchfx.Sdk.DeclaredServiceInfo(ServiceEndpointNaming.DeclaredEndpointNames(kv.Value));
                reservedSvcNames[kv.Key] = $"a declared service (environment.services.{kv.Key})";
            }
        }

        if (deps is not null)
        {
            foreach (var (depName, depSpec) in deps)
            {
                foreach (var sidecarName in EnvironmentMapper.GetDependencyServiceSidecarNames(depName, depSpec))
                {
                    // m7 fix (fix round 3): the collision guard below (the one keyed off
                    // boundSteps' host resources) is one-directional — it checks a HOST
                    // RESOURCE's VarName against reservedSvcNames, but nothing previously
                    // checked a dependency's OWN sidecar name against a name a DECLARED
                    // SERVICE (the loop just above, which always runs first) already claims.
                    // A service named 'mail-smtp' declared alongside a mailpit dependency
                    // 'mail' (whose sidecar is ALSO named 'mail-smtp') collided silently,
                    // last write wins: this loop would overwrite the declared service's own
                    // reservedSvcNames/serviceMap entries with the dependency sidecar's —
                    // exactly the "a step could Pass having talked only to the wrong thing"
                    // risk the host-resource guard exists to prevent, just from the
                    // dependency-sidecar side instead of the host-resource side. Reported
                    // here, before any builder mutation, mirroring the service-vs-dependency
                    // NAME check three loops above (services.Keys vs depMap.Keys) — that
                    // check catches the two TOP-LEVEL names colliding; this one catches a
                    // dependency's own SIDECAR name colliding with anything already reserved
                    // (a declared service, or an earlier dependency's own sidecar).
                    if (reservedSvcNames.TryGetValue(sidecarName, out var sidecarOwner))
                    {
                        collisionFailure ??= new ValidationFailure(
                            $"dependency '{depName}' (type '{depSpec.Type}')'s own sidecar " +
                            $"endpoint '{sidecarName}' collides with {sidecarOwner}. A " +
                            "dependency's own sidecar endpoint cannot share a name with a " +
                            "declared service or with another dependency's own sidecar " +
                            "endpoint — rename one of the two.");
                        continue;
                    }

                    reservedSvcNames[sidecarName] =
                        $"dependency '{depName}' (type '{depSpec.Type}')'s own sidecar endpoint " +
                        $"(environment.dependencies.{depName})";

                    // M1 fix (fix round 3): a regression this branch introduced. Populating
                    // ONLY reservedSvcNames (above) made this sidecar name unreachable through
                    // ANY of the three narrowed HTTP-family providers (M1, fix round 2):
                    // ctx.DeclaredServices.ContainsKey(target) is exactly how http.rest/http.
                    // soap/metrics-assert.prometheus decide a target is real, and a name present
                    // only in reservedSvcNames (a set this method never returns to a caller) was
                    // invisible to that check — so a target naming mailpit's SMTP sidecar
                    // ('<dep>-smtp') or Kafka's schema-registry sidecar ('<dep>-sr'), BOTH of
                    // which EnvironmentMapper actually stages and BOTH of which resolve at run
                    // time, validated as if the name did not exist at all. Fixed at the cause
                    // the M1 (fix round 2) review note asked for: reservedSvcNames and
                    // serviceMap (DeclaredServices) are now derived from the SAME
                    // GetDependencyServiceSidecarNames enumeration, in the same loop, so the
                    // collision guard and the validator can no longer disagree about which
                    // svc::<name> keys are reachable. The endpoint-name value mirrors what
                    // EnvironmentMapper's own Build lambda actually stages the sidecar under —
                    // "http" for Kafka's schema-registry REST API (KafkaSchemaRegistryServiceName,
                    // EnvironmentMapper.cs), "smtp" for mailpit's SMTP endpoint
                    // (MailpitSmtpServiceName) — the same convention DeclaredServices already
                    // uses for a declared service's own endpoint names, above.
                    //
                    // R-3 residual (peer-review-critic, PR #349 fix round 4): the drift guard
                    // above (GetDependencyServiceSidecarNames as the single source of truth)
                    // covers only the sidecar NAME ("bus-sr", "mail-smtp") — the endpoint-name
                    // VALUE ("http"/"smtp") assigned below is a SEPARATE, hand-written
                    // dependency-type switch, not sourced from EnvironmentMapper at all.
                    // Correct today because both branches happen to match
                    // EnvironmentMapper's own literals, and nothing currently reads the value
                    // besides ContainsKey/Keys (providers never dereference it, and
                    // ProjectContextDescriptions.DescribeDeclaredSurfaces only lists names) —
                    // but a future third sidecar kind on a NON-HTTP endpoint would satisfy this
                    // guard's name check while silently carrying the wrong endpoint-name value
                    // here. Extending GetDependencyServiceSidecarNames to return the
                    // (name, endpointName) pair — rather than the name alone — would close this
                    // the same way the name-only drift was closed above; left open since nothing
                    // consumes the value yet.
                    serviceMap[sidecarName] = new Vouchfx.Sdk.DeclaredServiceInfo(new[]
                    {
                        string.Equals(depSpec.Type, "mailpit", StringComparison.Ordinal)
                            ? "smtp"
                            : ServiceEndpointNaming.HttpEndpointName,
                    });
                }
            }
        }

        // Merge every step's own IHostResourceContributor requirement (S07-F-01a) — ALSO a
        // valid svc::<name> target, regardless of step order (see this method's own
        // <param name="boundSteps"> remarks). No binding happens here: boundSteps already
        // carries each step's materialised HostResources list from Compile's Pass 1. A
        // community provider's throwing Bind would already have propagated out of Pass 1
        // before this method is ever called (Bind is unguarded — see BindAllSteps' own
        // remarks); a throwing HostResources() enumerator, by contrast, is CAUGHT in Pass 1
        // (G-A, gatekeeper, fix round 3) and deferred onto that step's own BoundStep.
        // HostResourcesFailure rather than propagated — so bound.HostResources here is
        // ALWAYS a safe, fully-materialised (possibly empty) list, never a live throw, either
        // way. There is nothing left here that can throw on a per-step basis, so no per-step
        // catch/continue is needed (contrast the pre-M5 pre-pass, which called Bind
        // speculatively a second time and had to swallow exactly that class of exception).
        // This method itself never reads HostResourcesFailure — Compile's Pass 2 rethrows it,
        // after that step's own Validate has had its chance (see BoundStep's own remarks).
        foreach (var bound in boundSteps)
        {
            foreach (var hostReq in bound.HostResources)
            {
                // Guarded on the COLLISION itself, never on 'collisionFailure is null':
                // a second colliding host resource must also take the 'continue' below
                // rather than fall through to the overwrite this guard exists to prevent.
                // (Unreachable in effect today — Compile returns the failure before this
                // context is read — but a guard whose correctness depends on its caller
                // is a trap for whoever edits that caller next. '??=' keeps the FIRST
                // collision's message, which is the one an author fixes first.)
                if (reservedSvcNames.TryGetValue(hostReq.VarName, out var owner))
                {
                    collisionFailure ??= new ValidationFailure(
                        $"host resource '{hostReq.VarName}' (kind '{hostReq.Kind}', declared by " +
                        $"step '{bound.Node.Id}') collides with {owner}. A host resource (e.g. a " +
                        "webhook listener) cannot share a name with a declared service or with a " +
                        "dependency's own sidecar endpoint — rename one of the two.");
                    continue;
                }

                serviceMap[hostReq.VarName] = new Vouchfx.Sdk.DeclaredServiceInfo(new[] { hostReq.Kind });
            }
        }

        if (deps is { Count: > 0 } || serviceMap.Count > 0)
        {
            return new RunProjectContext(depMap, suiteDirectory, serviceMap);
        }

        return RunProjectContext.Empty(suiteDirectory);
    }
}
