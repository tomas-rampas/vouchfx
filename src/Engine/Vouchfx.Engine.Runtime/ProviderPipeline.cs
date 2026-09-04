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
//   • Every call INTO a provider from this pipeline — Bind / Validate / Resources /
//     HostResources / Emit reflectively, plus ICompileReferenceContributor.
//     CompileReferenceAssemblies directly — is GUARDED (issues #413 and #466): a provider
//     that throws produces a PipelineResult.Failure naming the step, the provider type and
//     the member, never an exception a caller has to classify. See DescribeProviderFault,
//     which owns the single spelling of that diagnostic, and Compile's own inline remarks for
//     why the alternative (classifying at ParallelSuiteRunner's slot catch-all) would move
//     EnvironmentError semantics for real infrastructure faults.
//   • AND SO IS CsxAssembler.Assemble, which is not a call into a provider at all but the
//     place provider-EMITTED CONTENT is refused (§13.3.1). CsxFragment performs no
//     constructor validation, so a fragment that breaks a rule is built cleanly inside the
//     provider's own Emit and no per-step guard can see it. Listed here beside the six
//     because it was found missing exactly by being absent from a list like this one; see
//     DescribeAssemblyFault for why its failure names the suite rather than a step.
//   • RETRY is COMPILED, not rejected (Sprint 6): each step's VerifyMode and
//     Timeout are threaded into a StepCompilePlan so CsxAssembler can wrap RETRY
//     steps in the engine-owned polling loop (§7).  The execution-time rejection
//     guard that previously lived in ScenarioRunner has been removed.
//   • The ValidationFailure path mirrors the existing ScenarioRunner Inconclusive
//     pattern so callers need no conditional logic change.

using System.Reflection;
using System.Text.Encodings.Web;
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
/// projection). <see cref="ProviderPipeline.Compile"/>'s Pass 2 reads this — immediately after
/// this step's own <c>Validate</c> has had a chance to produce a clean diagnostic for whatever
/// invalid model condition may have caused it — and turns it into a
/// <see cref="PipelineResult.Failure"/> naming the provider (issue #466). It used to RETHROW it
/// there instead, which escaped <see cref="ProviderPipeline.Compile"/> altogether and reached
/// <c>ParallelSuiteRunner</c>'s slot catch-all, where a provider defect was mislabelled as an
/// infrastructure fault — and, on a run where nothing executed, exited 0. (Only on such a run:
/// a sibling scenario that Fails still elevates the suite to <c>Verdict.Fail</c> and exits 1.
/// The unqualified "exited 0" this sentence used to carry was the outlier among the three
/// sites that state it.)
/// </param>
/// <remarks>
/// <para>
/// <strong><see cref="Exception"/>, not
/// <see cref="System.Runtime.ExceptionServices.ExceptionDispatchInfo"/>, since #466.</strong>
/// The EDI was here for one capability: <c>Throw()</c> rethrowing in Pass 2 with the Pass-1
/// stack intact. There is no rethrow any more — <c>grep -rn "\.Throw()" src/Engine/</c> finds
/// nothing but prose — and every remaining consumer reads the exception's type and message,
/// which a plain reference carries identically. Keeping the EDI would have been a type
/// signalling a rethrow that no longer exists, which is the same false-signal class as a stale
/// comment.
/// </para>
/// </remarks>
internal sealed record BoundStep(
    Vouchfx.Engine.Authoring.Ast.StepNode Node,
    IStepProvider Instance,
    object Model,
    IReadOnlyList<HostResourceRequirement> HostResources,
    Exception? HostResourcesFailure = null);

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
        // also contains a step whose Bind throws: a throwing Bind returns from Pass 1 on the
        // first offending step (issue #413 turned it into a ValidationFailure rather than a
        // propagating exception, but it still pre-empts everything ordered after it), so
        // binding first would let a provider bug displace a diagnosable environment error
        // instead of merely following it. Keeping this check ahead of Pass 1
        // preserves the failure precedence the previous pre-pass design happened to give,
        // without the speculative second Bind that design needed.
        var environmentSecurityFailure = EnvironmentSecurityValidator.Validate(ast, resolvedSuiteDirectory);
        if (environmentSecurityFailure is not null)
        {
            return Refuse(environmentSecurityFailure);
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
            return Refuse(securityProfileWiringFailure);
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
        // built, that is the complete check. THAT NOW HOLDS ON ALL THREE EXECUTING PATHS:
        //
        //   • the single-scenario `run` and `--parallel` (each scenario owns its own topology via
        //     RunScenarioOwningTopologyAsync) — MEASURED as ordered Compile-then-StartAsync there;
        //
        //   • `--watch`, since #370. Its compile seam (WatchRunner.Compile) used to be
        //     YamlDocumentParser.Parse + AstBuilder.Build only, so this method was not reached until
        //     the RUN seam — after the topology was already up, with kafkaSpeakingTargets already
        //     computed from the same AST. A conflicting suite therefore started containers and was
        //     rejected against them; it failed closed with this same diagnostic, but the containers
        //     stayed up for the rest of the watch session, because WatchSession disposes the kept
        //     topology only when it rebuilds. That seam now runs DocumentValidator.Validate and this
        //     method (via WatchIterationPlan.Create) BEFORE the reuse-vs-rebuild decision, so the
        //     conflict is refused before any container on the first save and without touching the
        //     kept topology on a later one.
        //
        // It is also the only check `vouchfx validate` can make, since ScenarioValidator treats each
        // file independently by design and never decides which files form a suite.
        //
        // ONE PATH THIS CALL DOES NOT COVER, and it is a property of the SUITE rather than of
        // ordering: the SHARED-topology `run` stages from the union across the RUNNABLE scenarios —
        // those carrying no early verdict, since a scenario that executes nothing stages nothing —
        // so a suite splitting the two families across two files is individually innocent per
        // scenario and collectively in conflict. It therefore carries its OWN call to the same
        // helper at its own seam (ScenarioRunner.RunSuiteAsync), from the SAME local it stages from,
        // so the guard and the staging cannot disagree about the set. Both seams call this one
        // helper, which owns the single spelling of the diagnostic (gatekeeper MAJOR, fix round four
        // — before it, the message was written out at this call site alone).
        var protocolConflict = SuiteProtocolTargets.DescribeProtocolConflict(new[] { (ScenarioAst?)ast });
        if (protocolConflict is not null)
        {
            return Refuse(protocolConflict);
        }

        // ── Pass 1: Bind every step exactly ONCE, retaining the model and its
        // IHostResourceContributor contribution (M5 fix, fix round 2 — replaces the
        // previous speculative pre-pass that called Bind a SECOND time per step, purely
        // to discover host-resource names before the main loop reached that step). See
        // BuildProjectContext's own remarks for why deriving DeclaredServices needs
        // every step's host-resource contribution up front regardless of step order.
        var (boundSteps, registryFailure) = BindAllSteps(ast, registry, resolvedSuiteDirectory);
        if (registryFailure is not null)
        {
            return Refuse(registryFailure);
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
            return Refuse(hostResourceServiceCollision);
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

            // ── Validate (GUARDED — issue #466) ───────────────────────────────
            // Unguarded, a throwing Validate unwound past Compile, past the runner, and into
            // ParallelSuiteRunner's per-slot catch-all, which classifies ANY escape as
            // EnvironmentError + TopologyUnavailable — and on a run that executed nothing that
            // is exit 0 (#390). A provider defect produced a GREEN build, exactly the shape
            // #413's own rationale condemns. Same channel and same reasoning as Bind's guard:
            // the fault is a provider defect rather than an authoring one, but the taxonomy
            // answer is identical — the step was never compiled, nothing ran, Inconclusive.
            //
            // NARROWING, NOT RECLASSIFYING. The slot catch-all is deliberately untouched (see
            // its own remarks): it sees only an exception type and cannot tell a genuine
            // infrastructure fault — for which EnvironmentError is CORRECT, §12.1 — from an
            // engine defect, and TopologyUnavailable sits outside every SecurityAssurance
            // Unconfirmed disjunct, so moving it would move security semantics. What changes
            // here is only what can still REACH that frame.
            //
            // THE CATCH IS UNFILTERED for the same reason Bind's is: no cancellation token
            // reaches IStepValidator<T>.Validate — the v1 contract passes a model and an
            // IProjectContext and nothing else — so a cancellation surfacing here is not a stop
            // anybody requested, and there is nothing for a filter to preserve.
            //
            // THAT BOUNDS WHAT THE ENGINE ASKED FOR; IT SAYS NOTHING ABOUT WHOSE FAULT THE
            // CANCELLATION IS. A provider may hold its own CancellationTokenSource, and an
            // HttpClient's default timeout surfaces as TaskCanceledException, which IS an
            // OperationCanceledException. Calling those a provider defect would send an author
            // to audit code that is not at fault — the failure mode the unwrappedIsProviderFault
            // split exists to avoid. The ATTRIBUTION is DescribeProviderFault's to make, and
            // IsHostCondition is where it is made.
            ValidationResult validResult;
            try
            {
                validResult = ReflectValidate(instance, model, projectCtx);
            }
            catch (Exception ex)
            {
                return Refuse(
                    DescribeProviderFault(
                        node,
                        instance,
                        "Validate",
                        ex,
                        unwrappedIsProviderFault: false,
                        resolvedSuiteDirectory));
            }

            if (!validResult.IsValid)
            {
                // SCRUBBED (SEC-MAJOR-1 follow-up, issue #466). `validResult.Errors` is
                // FREE-FORM PROVIDER-AUTHORED TEXT — the same category as the exception
                // messages the six guards splice, on the same channel, reaching the same
                // archived artefacts — and it reached them unscrubbed while the guard three
                // lines above did not. That a provider RETURNED this string rather than THREW
                // it changes nothing an archived artefact can tell apart.
                //
                // NOT AN ACADEMIC CASE, and "every Core provider is careful" is not the
                // argument: the provider model exists so that out-of-tree providers exist, a
                // `Validate` is handed `ctx.SuiteDirectory` and routinely resolves paths
                // against it (ScriptCsharpProvider's own not-found guard is the in-tree
                // example of a Validate that had to be written carefully to avoid exactly
                // this), and #357's rule is stated absolutely because nothing downstream can
                // redact an artefact that has already been uploaded.
                return Refuse(
                    $"Step '{node.Id}' model validation failed: " +
                    ScrubSuiteDirectory(
                        string.Join("; ", validResult.Errors), resolvedSuiteDirectory));
            }

            // G-A (gatekeeper, fix round 3): a HostResources() throw captured back in Pass 1
            // (BindAllSteps) is surfaced HERE — after this exact step's OWN Validate, just
            // above, has already had its chance to turn whatever invalid model condition
            // triggered it into the clean ValidationFailure returned above instead. Reaching
            // this line means Validate found the model FINE, so whatever HostResources threw
            // is a genuine bug Validate does not already cover.
            //
            // THE POSITION IS G-A's AND IS LOAD-BEARING; THE TERMINAL SHAPE IS #466's. This
            // line still sits exactly where the rethrow sat — after this step's Validate, so a
            // model problem still becomes a clean ValidationFailure first, and still unable to
            // pre-empt a diagnosable error from a DIFFERENT, earlier-bound step (BindAllSteps
            // runs Pass 1 for every step before Pass 2 validates any of them; pinned by
            // ProviderPipelineTests.Compile_TargetingStepPrecedesThrowingListenerStep_
            // ReturnsWrongTargetDiagnostic). What changed is where it goes: it used to be
            // `bound.HostResourcesFailure?.Throw()`, an ExceptionDispatchInfo rethrow that
            // preserved the Pass-1 stack and then escaped Compile entirely — and on the
            // --parallel path the slot catch-all mislabelled that escape as an infrastructure
            // fault worth exit 0 (#466). It is a PROVIDER defect, so it now lands in the same
            // ValidationFailure channel as the other five guarded provider calls. Pass 1 now
            // carries the plain Exception rather than an ExceptionDispatchInfo: the EDI existed
            // for Throw()'s stack preservation, and with no rethrow left there is nothing it
            // does that a reference does not (see BoundStep.HostResourcesFailure's remarks).
            //
            // UNWRAPPED IS A PROVIDER FAULT HERE. ReflectHostResources is tolerant of a missing
            // interface and returns a possibly-lazy IEnumerable, so BindAllSteps' ToList() runs
            // the provider's own iterator body after Invoke has already returned — there is no
            // TargetInvocationException to unwrap on that path, and blaming the engine for it
            // would be wrong.
            if (bound.HostResourcesFailure is { } hostResourcesFailure)
            {
                return Refuse(
                    DescribeProviderFault(
                        node,
                        instance,
                        "HostResources",
                        hostResourcesFailure,
                        unwrappedIsProviderFault: true,
                        resolvedSuiteDirectory));
            }

            // ── Resources (tolerant, GUARDED — issue #466) ────────────────────
            // The try WRAPS THE foreach RATHER THAN THE CALL, and that is the whole point:
            // ReflectResources hands back the provider's own IEnumerable, which for a C#
            // iterator does not execute a line of its body until this loop pulls the first
            // element. A try around the call alone would have caught nothing a real provider
            // throws. Same channel, same reasoning, same unwrapped-is-the-provider's-fault
            // reading as the host-resource guard above.
            try
            {
                foreach (var req in ReflectResources(instance, model))
                {
                    resourcePlan.Add(new ResourcePlanEntry(
                        StepId: node.Id,
                        Requirement: req,
                        ProviderTypeName: instance.GetType().FullName
                            ?? instance.GetType().Name));
                }
            }
            catch (Exception ex)
            {
                return Refuse(
                    DescribeProviderFault(
                        node,
                        instance,
                        "Resources",
                        ex,
                        unwrappedIsProviderFault: true,
                        resolvedSuiteDirectory));
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

            // ── Compile references (tolerant, GUARDED — issue #466) ───────────
            // THE FIFTH PROVIDER CALL IN THIS LOOP, and the one #466's own list does not name
            // because it is not dispatched reflectively — it is a direct call on a provider's
            // ICompileReferenceContributor. That distinction does not reach the failure mode:
            // the code being entered is still the provider's, an escape still unwinds past
            // Compile, and ParallelSuiteRunner's slot catch-all still classifies it as
            // EnvironmentError → exit 0. Guarding the four reflective surfaces and leaving this
            // one bare would close the hole for the members a provider declares through an
            // interface method and leave it open for the one it declares through an interface
            // PROPERTY, which is not a distinction anybody could defend afterwards.
            //
            // BOTH LINES INSIDE THE try ARE PROVIDER-REACHABLE. The property getter runs the
            // provider's own code; and Assembly.Location raises NotSupportedException for a
            // dynamic or single-file-bundled assembly, which is likewise the provider's choice
            // of what to hand back. The enumerable may be lazy, so — exactly as for Resources
            // above — the try wraps the foreach, not the property read.
            try
            {
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
            }
            catch (Exception ex)
            {
                return Refuse(
                    DescribeProviderFault(
                        node,
                        instance,
                        "CompileReferenceAssemblies",
                        ex,
                        unwrappedIsProviderFault: true,
                        resolvedSuiteDirectory));
            }

            // ── Emit (GUARDED — issue #466) ───────────────────────────────────
            // The LAST reflective surface, and the one a provider is most likely to throw from
            // in practice (Emit is where string composition, path resolution and model
            // projection all happen). Same channel and same reasoning as the three above.
            //
            // IT IS NOT THE LAST PLACE PROVIDER CONTENT CAN FAIL THE COMPILE, and the guard
            // around CsxAssembler.Assemble below is the other half: CsxFragment performs no
            // constructor validation, so a fragment that breaks a §13.3.1 rule is built
            // successfully HERE and refused THERE.
            CsxFragment fragment;
            try
            {
                fragment = ReflectEmit(instance, model, compileCtx);
            }
            catch (Exception ex)
            {
                return Refuse(
                    DescribeProviderFault(
                        node,
                        instance,
                        "Emit",
                        ex,
                        unwrappedIsProviderFault: false,
                        resolvedSuiteDirectory));
            }

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

            // SCRUBBED for the reason the census in ScrubSuiteDirectory's remarks records:
            // VarName and Kind come off a HostResourceRequirement the PROVIDER constructed.
            // They are identifiers rather than free-form diagnostics, and nothing in tree puts
            // a path in one - but nothing validates their shape beyond non-empty either, so
            // this closes the category rather than the instance. The scrub is a targeted
            // substitution and a no-op when the directory does not occur.
            return Refuse(ScrubSuiteDirectory(
                $"host resource '{group.Key}' is declared by more than one kind " +
                $"({string.Join(", ", kinds)}). Each host-resource VarName must be claimed " +
                "by exactly one kind - a webhook listener and an OTLP receiver (or any two " +
                "distinct host-resource kinds) cannot share the same name. Rename one of them.",
                resolvedSuiteDirectory));
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
                // Scrubbed: same provider-supplied VarName provenance as the guard above.
                return Refuse(ScrubSuiteDirectory(
                    $"host resource '{containerVarName}' collides with the engine-" +
                    $"synthesised container-reachable alias of host resource '{varName}' (staged at " +
                    $"'{varName}{ScenarioRunner.ContainerVarSuffix}'). Rename one of the two " +
                    "host resources (webhook listeners / OTLP receivers) so the alias is unambiguous.",
                    resolvedSuiteDirectory));
            }
        }

        // ── CSX assembly (GUARDED — GATE-MAJOR-1, issue #466) ─────────────────────
        // THE SIXTH ESCAPE ROUTE, and the one the six per-step guards above cannot reach.
        // CsxAssembler.Assemble refuses provider-EMITTED content that breaks §13.3.1 — a
        // RequiredUsings entry that is not a bare namespace, or two fragments declaring one
        // helper class with different source text — and CsxFragment performs NO constructor
        // validation, so the offending fragment is constructed cleanly inside the provider's
        // own Emit and the Emit guard sees nothing. The CsxAssemblyException lands HERE, on
        // this line, in the same frame as the six guards, and took the identical unguarded
        // route to ParallelSuiteRunner's slot catch-all → EnvironmentError → exit 0. Nothing
        // in src/ or tests/ catches CsxAssemblyException outside CsxAssembler itself, and this
        // seam had never been exercised through Compile at all — only at the assembler's own
        // unit level, where a throw is the asserted outcome rather than an escape.
        //
        // ATTRIBUTED TO THE SUITE, NOT GUESSED ONTO A STEP: see DescribeAssemblyFault, which
        // records why the exception cannot name a fragment.
        //
        // Broad catch, for the reason DescribeProviderFault's remarks give for the six above:
        // the alternative destination for anything that gets past this is the exit-0 path this
        // whole issue closes.
        AssembledScript assembled;
        try
        {
            assembled = CsxAssembler.Assemble(fragments);
        }
        catch (Exception ex)
        {
            return Refuse(DescribeAssemblyFault(ex, resolvedSuiteDirectory));
        }

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
    /// Builds the standard "nothing could be compiled" <see cref="PipelineResult"/> around a
    /// diagnostic message: no assembled script and no partial plan of any kind.
    /// </summary>
    /// <param name="message">The already-composed, already-scrubbed diagnostic.</param>
    /// <remarks>
    /// Extracted because <see cref="Compile"/> returns this exact nine-line shape from six
    /// provider-fault sites and half a dozen guard sites, and six copies of one rule read as six
    /// rules. Every failure return in this file discards the partially-built plan for the same
    /// reason: the caller must not be handed a resource plan derived from a compile that never
    /// finished.
    /// </remarks>
    private static PipelineResult Refuse(ValidationFailure failure) =>
        new(Assembled: null,
            ResourcePlan: Array.Empty<ResourcePlanEntry>(),
            CompileReferencePaths: Array.Empty<string>(),
            HostResourcePlan: Array.Empty<HostResourcePlanEntry>(),
            Failure: failure);

    /// <summary>
    /// <see cref="Refuse(ValidationFailure)"/> for a plain message — an ordinary failure with
    /// <see cref="ValidationFailure.IsSecurityPreflight"/> left <see langword="false"/>.
    /// </summary>
    /// <param name="message">The already-composed, already-scrubbed diagnostic.</param>
    /// <remarks>
    /// <para>
    /// The <see cref="ValidationFailure"/> overload above exists so the FOUR sites that already
    /// HOLD a failure object — the two security preflights, the registry lookup, and the
    /// host-resource/service collision (<see cref="BuildProjectContext"/>'s <c>out</c>
    /// parameter is typed <see cref="ValidationFailure"/>, so it binds that overload too) —
    /// pass it through intact instead of rebuilding it from its <c>Message</c>. Overload
    /// resolution already sends each site to the right one; this is descriptive, not a rule the
    /// call sites have to remember.
    /// </para>
    /// <para>
    /// <strong>WHY, STATED IN THE RIGHT TENSE.</strong> The only field a rebuild would lose is
    /// <see cref="ValidationFailure.IsSecurityPreflight"/>, and that flag has <em>no production
    /// consumer today</em> — MEASURED: <c>grep -rn "IsSecurityPreflight" src/</c> finds writers,
    /// this declaration, and comments, and nothing that reads it.
    /// <c>ExitCodes.FromVerdict</c> keys REQ-018 on <c>securityAssurance?.Unconfirmed</c>, never
    /// on this flag, and <c>ScenarioRunner.RunPreTopologyAuthoringDoor</c> discards the record
    /// and returns a plain string. The flag's own remarks name PR D as the intended consumer;
    /// that wiring does not exist. An earlier revision of THIS remark asserted the overload
    /// "protects the marker REQ-018's exit-code decision keys on" — present tense, and false.
    /// </para>
    /// <para>
    /// The shape still stands on its own terms: preserving the identity of a record you were
    /// handed is the correct default, and rebuilding it from one field would be the wrong one
    /// on the day a consumer does land — which is exactly when nobody would be looking. That is
    /// a smaller claim than the one it replaces, and it is the true one.
    /// </para>
    /// </remarks>
    private static PipelineResult Refuse(string message) =>
        Refuse(new ValidationFailure(message));

    /// <summary>
    /// Replaces every occurrence of the resolved suite directory in free-form diagnostic text
    /// with the literal words <c>the suite directory</c> — in both the raw spelling and the
    /// JSON-escaped one (SEC-MAJOR-1, issue #466).
    /// </summary>
    /// <param name="text">The exception message as the provider (or the BCL) wrote it.</param>
    /// <param name="resolvedSuiteDirectory">
    /// The absolute host directory <see cref="Compile"/> resolves this scenario's relative
    /// paths against — the value already in scope for the whole of Pass 2.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>Why this is needed at all, given the scrub chokepoint downstream.</strong> This
    /// message reaches <c>ScenarioCompletedEvent.Message</c>, and from there the §14 event
    /// stream, the <c>--events</c> artifact, the JUnit <c>message</c> attribute and the HTML
    /// report — every one archived and uploaded. It does pass <c>ScenarioRunner</c>'s scrub
    /// chokepoint, and the two nets there run in the right order, but neither can help: this
    /// door runs PRE-TOPOLOGY, and <c>SecurityPathDisclosureLedger</c> is populated only at
    /// topology-build time by <c>SecurityConfigurationAccessor</c> — so both ledgers are empty
    /// by construction here, and that ledger only ever holds security-material paths in any
    /// case. As <c>ScriptCsharpProvider</c> puts it for the same class of leak: a net cannot
    /// replace what was never recorded into it.
    /// </para>
    /// <para>
    /// <strong>The route is live and in-tree.</strong> <c>ScriptCsharpProvider.Emit</c> reads
    /// <c>File.ReadAllText(Path.GetFullPath(Path.Combine(ctx.SuiteDirectory, model.File)))</c>
    /// and its own comment accepts the TOCTOU race against <c>Validate</c>'s existence check. A
    /// file deleted in that window yields <c>Could not find file 'D:\…\suite\x.csx'.</c> — a BCL
    /// message carrying the absolute host path, straight into the guard this scrub protects.
    /// </para>
    /// <para>
    /// <strong>Substitution, not redaction, and the wording is not new.</strong> Naming the
    /// CONCEPT the path resolves against keeps a relative path diagnosable while disclosing
    /// nothing about the host's layout. <c>EnvironmentSecurityValidator</c> ("resolves outside
    /// the suite directory") and <c>ScriptCsharpProvider</c> ("relative to the suite directory")
    /// already write exactly this phrase for exactly this reason (#357), and
    /// <see cref="SecurityPathDisclosureLedger"/> chose substitution over <c>[REDACTED]</c> on
    /// the same argument.
    /// </para>
    /// <para>
    /// <strong>Both spellings, and the escaped one first.</strong> A provider message that
    /// embeds serialised JSON carries the path with doubled separators
    /// (<c>D:\\src\\…</c>), which a raw-only match does not see — and which any consumer of the
    /// on-disk <c>--events</c> artifact recovers by JSON-decoding. That bypass has shipped once
    /// already; <see cref="SecurityPathDisclosureLedger"/>'s own remarks record it. The escaped
    /// form is replaced first because it is the longer, more specific one; the two cannot in
    /// fact overlap (the raw form is not a substring of the escaped form — <c>D:\s</c> does not
    /// occur inside <c>D:\\s</c>), so the order is defensive rather than load-bearing.
    /// </para>
    /// <para>
    /// <strong>THE FULL CENSUS OF WHAT REACHES A <see cref="ValidationFailure"/> IN THIS FILE,
    /// so the next reviewer does not have to re-derive it.</strong> Every construction site was
    /// classified by the PROVENANCE of each value it interpolates. Routed through this scrub:
    /// the six <see cref="DescribeProviderFault"/> sites and
    /// <see cref="DescribeAssemblyFault"/> (a provider's exception message);
    /// <see cref="Compile"/>'s model-validation failure (<c>ValidationResult.Errors</c> —
    /// free-form provider-authored text, the same category as an exception message and on the
    /// same channel); and the three sites that interpolate a
    /// <see cref="HostResourceRequirement"/>'s <c>VarName</c>/<c>Kind</c> (two in
    /// <see cref="Compile"/>'s collision guards, one in
    /// <see cref="BuildProjectContext"/>) — identifiers rather than diagnostics, and nothing
    /// in tree puts a path in one, but nothing validates their shape beyond non-empty either.
    /// Deliberately NOT routed, because no value they interpolate is provider-authored:
    /// <c>EnvironmentSecurityValidator</c>'s and <c>SecurityProfileWiringValidator</c>'s own
    /// failures; <c>SuiteProtocolTargets.DescribeProtocolConflict</c>; the registry-lookup
    /// internal error (<c>node.CanonicalType</c>); and <see cref="BuildProjectContext"/>'s
    /// other two collision messages, which interpolate
    /// <c>environment.services</c>/<c>environment.dependencies</c> map keys and engine-composed
    /// owner text only.
    /// </para>
    /// <para>
    /// <strong>ONE OF THOSE DOES QUOTE THE SUITE DIRECTORY, AND IT IS A DELIBERATE EXCEPTION
    /// RATHER THAN AN OVERSIGHT — do not "fix" it.</strong>
    /// <c>EnvironmentSecurityValidator</c>'s malformed-base-directory guard writes
    /// <c>suite directory '&lt;suiteDirectory&gt;' is not a valid path (&lt;ex.Message&gt;)</c>
    /// and reaches the archived stream unscrubbed. The census's governing reason still holds —
    /// both values are engine- and BCL-supplied, neither is provider-authored — and the message
    /// is exempt on its own terms: its entire SUBJECT is that the suite directory is malformed,
    /// so substituting the words "the suite directory" for it would produce the tautology
    /// <c>suite directory 'the suite directory' is not a valid path</c> and leave the author
    /// nothing to correct. Note also that #357's no-resolved-path rule is stated inside that
    /// file against the two containment/existence messages that FOLLOW it; that guard sits
    /// above it and is not one of them. An earlier revision of this census claimed those
    /// failures were "already bound by #357's rule at their own sites", which was false for
    /// this one.
    /// </para>
    /// <para>
    /// <strong>RESIDUAL, stated rather than implied: a path OUTSIDE the suite directory is NOT
    /// handled.</strong> A provider that names a temp file, a NuGet cache entry, a home
    /// directory or any other absolute path still discloses it verbatim. This scrub closes the
    /// one path the engine itself computed and handed the provider; it is not a general
    /// path-disclosure net, and there is no general one to reach for here — the ledger that
    /// would be the candidate is empty at this stage and scoped to security material anyway.
    /// The comparison is <see cref="StringComparison.Ordinal"/>, matching
    /// <see cref="SecurityPathDisclosureLedger"/>'s, so a differently-cased spelling of the same
    /// directory on a case-insensitive filesystem is likewise unhandled; in practice the path in
    /// the message is the one the engine composed from this same string.
    /// </para>
    /// <para>
    /// <strong>NO PATH-BOUNDARY CHECK, AND THAT IS A DECISION RATHER THAN AN OMISSION.</strong>
    /// The replace is an unbounded substring match, so a suite at <c>D:\a\suite</c> rewrites a
    /// diagnostic's mention of the SIBLING <c>D:\a\suite-backup\x.json</c> into
    /// <c>the suite directory-backup\x.json</c> — a misnamed path. Not fixed, on the severity
    /// reasoning <see cref="SecurityPathDisclosureLedger"/> already wrote for its own
    /// substitution and which applies here verbatim: the substitution only ever makes text
    /// SHORTER and LESS specific, so the failure mode is a misnamed path in a diagnostic and
    /// never a disclosure. The direction of the error is the safe one.
    /// </para>
    /// <para>
    /// The ledger's own single-pass longest-first scan is NOT the precedent to copy here, and
    /// the distinction matters: it solves a MULTI-FORM problem (a replacement containing a later
    /// form's recorded string, so a second pass rewrites text the first pass produced), and this
    /// method has exactly one form and cannot have that fault. What would be needed here instead
    /// is a boundary predicate — "the character after the match is a separator, or a quote, or a
    /// space, or end-of-input" — which has to be right in the JSON-ESCAPED spelling too, where
    /// the separator is doubled. That is a bespoke character-class scanner inside a
    /// security-relevant function, traded against a cosmetic defect whose worst outcome is a
    /// confusing filename. Revisit if a real diagnostic is ever observed to be mangled this way.
    /// </para>
    /// <para>
    /// <strong>AND A SUITE DIRECTORY THAT IS ITSELF A FILESYSTEM ROOT IS DELIBERATELY NOT
    /// SUBSTITUTED</strong> — see the guard's own comment for why replacing <c>/</c> or
    /// <c>C:\</c> would corrupt the diagnostic rather than protect it. The rule is "the
    /// directory equals its own root", so it also covers a bare UNC share
    /// (<c>\\server\share</c>); a share name IS a host fact, so that one is a real if narrow
    /// residual, accepted because a suite rooted at a share root is not a shape the engine has
    /// any other reason to expect.
    /// </para>
    /// </remarks>
    private static string ScrubSuiteDirectory(string text, string resolvedSuiteDirectory)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(resolvedSuiteDirectory))
        {
            return text;
        }

        // REFUSE TO SUBSTITUTE A FILESYSTEM ROOT. A suite run from `/` or `C:\` would make this
        // a search-and-replace for one or three characters that occur in every path, every URL
        // and most punctuation in the diagnostic - the substitution would corrupt the message
        // far worse than the disclosure it is removing, and `/` in particular appears in text
        // that has nothing to do with the filesystem. A root also discloses nothing: it is not
        // a fact about the host worth hiding. `IsNullOrEmpty` above does not cover this, since
        // a root is a perfectly ordinary non-empty path string.
        if (Path.GetPathRoot(resolvedSuiteDirectory) is { } root
            && string.Equals(
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                resolvedSuiteDirectory.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.Ordinal))
        {
            return text;
        }

        const string Concept = "the suite directory";

        var escaped = JavaScriptEncoder.Default.Encode(resolvedSuiteDirectory);
        var scrubbed = string.Equals(escaped, resolvedSuiteDirectory, StringComparison.Ordinal)
            ? text
            : text.Replace(escaped, Concept, StringComparison.Ordinal);

        return scrubbed.Replace(resolvedSuiteDirectory, Concept, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ONE spelling of the diagnostic every guarded provider call reports (issue #466):
    /// the five reflective surfaces <c>Bind</c>, <c>Validate</c>, <c>Resources</c>,
    /// <c>HostResources</c> and <c>Emit</c>, plus the directly-called
    /// <c>ICompileReferenceContributor.CompileReferenceAssemblies</c>.
    /// </summary>
    /// <param name="node">The step whose provider threw.</param>
    /// <param name="instance">The provider instance, for its <see cref="Type.FullName"/>.</param>
    /// <param name="member">
    /// The member that threw, spelled exactly as the provider author declares it (<c>Bind</c> /
    /// <c>Validate</c> / <c>Resources</c> / <c>HostResources</c> / <c>Emit</c> /
    /// <c>CompileReferenceAssemblies</c>).
    /// </param>
    /// <param name="ex">The exception as caught, wrapper and all.</param>
    /// <param name="unwrappedIsProviderFault">
    /// How to read an exception that is NOT a <see cref="TargetInvocationException"/>, and the
    /// answer differs by surface rather than being a caller preference:
    /// <list type="bullet">
    /// <item><description>
    /// <see langword="false"/> for <c>Bind</c>/<c>Validate</c>/<c>Emit</c>. Those three resolve
    /// the closed generic interface and the <see cref="MethodInfo"/> through
    /// <see cref="FindGenericInterface"/> BEFORE invoking anything, so an unwrapped throw came
    /// from the engine's own plumbing and blaming the provider would send a reader to read a
    /// method that never ran.
    /// </description></item>
    /// <item><description>
    /// <see langword="true"/> for <c>Resources</c>/<c>HostResources</c>. Both are TOLERANT of a
    /// missing interface (they return an empty sequence rather than throwing) and both return a
    /// possibly-LAZY <see cref="IEnumerable{T}"/>, so the provider's iterator body runs during
    /// the caller's enumeration — long after <see cref="MethodInfo.Invoke"/> returned, with no
    /// wrapper to unwrap. An unwrapped throw there IS the provider's own.
    /// </description></item>
    /// <item><description>
    /// <see langword="true"/> for <c>CompileReferenceAssemblies</c>. It is not dispatched
    /// reflectively at all — a direct interface PROPERTY read — so there is never a wrapper, and
    /// both the getter and the <c>Assembly.Location</c> read the engine performs on what it
    /// returns are the provider's own choices.
    /// </description></item>
    /// </list>
    /// </param>
    /// <param name="resolvedSuiteDirectory">
    /// Threaded through to <see cref="ScrubSuiteDirectory"/>; see that method for why the
    /// exception's message cannot be spliced verbatim.
    /// </param>
    /// <returns>
    /// A message naming the step id, the canonical step type, the provider type's full name,
    /// the member, and the originating exception's own type and (scrubbed) message — enough to
    /// identify the broken provider from CI output alone, without a debugger.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Only <see cref="TargetInvocationException"/> is unwrapped, and only one level, because
    /// that wrapper is the only one the engine itself introduces. The provider's OWN inner
    /// chain below that point is then WALKED and appended (see
    /// <see cref="DescribeCauseChain"/>): a provider that wraps its real failure —
    /// <c>InvalidOperationException("save failed", inner: SocketException)</c> — otherwise
    /// reported nothing but "save failed", which names the symptom and hides the cause.
    /// </para>
    /// <para>
    /// <strong>THE STACK IS STILL DROPPED, and on a §17 argument rather than a convenience
    /// one.</strong> A stack trace carries PDB source paths — the build machine's directory
    /// layout, not merely this run's suite directory — into the same archived artefacts
    /// <see cref="ScrubSuiteDirectory"/> exists to protect, and no scrub in this engine covers
    /// them. Inner-exception MESSAGES carry no such risk: they are the same category as the
    /// outer message and go through the same scrub, message by message.
    /// </para>
    /// <para>
    /// <strong>THE ATTRIBUTION SENTENCE IS CONDITIONED ON THE TYPE, because the catches that
    /// feed it are deliberately unfiltered.</strong> Three arms, in order of how confidently
    /// blame can be assigned:
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="IsHostCondition"/> — <see cref="OutOfMemoryException"/> /
    /// <see cref="OperationCanceledException"/>: a process- or host-level condition that can
    /// surface through ANY frame.
    /// </description></item>
    /// <item><description>
    /// <see cref="IsEnvironmentalCondition"/> — the filesystem family: reported as a filesystem
    /// condition that MAY be a provider defect or may be the host's or the author's. This arm is
    /// not hypothetical: the single most likely production trigger of the <c>Emit</c> guard is
    /// <c>ScriptCsharpProvider.Emit</c>'s accepted TOCTOU race against its own <c>Validate</c>
    /// existence check, so a <c>.csx</c> deleted, locked by antivirus, or on an unreadable share
    /// produces a <see cref="FileNotFoundException"/> here — and telling the author that
    /// <c>Vouchfx.Steps.Script.Csharp.ScriptCsharpProvider</c> is defective would be both false
    /// and an accusation against a Core provider.
    /// </description></item>
    /// <item><description>
    /// Everything else keeps the strong, actionable claim, which is the whole value of the
    /// diagnostic for the ordinary case.
    /// </description></item>
    /// </list>
    /// The two conditional arms are SIBLINGS rather than one widened predicate: they say
    /// different things, and a filesystem fault is a materially different investigation from an
    /// OOM.
    /// </para>
    /// <para>
    /// <strong>Why the catches stay broad here while
    /// <c>ScenarioRunner.HashFixtureOrNull</c>'s is deliberately narrow — the two are not in
    /// conflict, and the reason is the COST OF OVER-CATCHING, not the destination.</strong> An
    /// earlier revision of this paragraph headlined it as "the escape destinations differ",
    /// which is false: an exception escaping EITHER guard reaches <c>ParallelSuiteRunner</c>'s
    /// slot catch-all and can exit 0. What differs is what an over-broad catch COSTS. Here it
    /// costs a slightly over-confident sentence — mitigated by the three arms above — while
    /// catching too little costs the green build this issue exists to prevent. There it is
    /// recorded as a null content hash in the reproducibility envelope and the run CONTINUES,
    /// so an over-broad catch silently corrupts a trust artefact rather than mis-wording a
    /// refusal that was going to stop the run anyway. Broad plus careful wording here; narrow
    /// and named there.
    /// </para>
    /// <para>
    /// This is the CANONICAL statement of that trade. <c>ScenarioRunner.HashFixtureOrNull</c>
    /// points here rather than restating it — two independently-maintained copies of one
    /// argument is the prose-drift class this repository treats as a defect, and this branch's
    /// own history is that the second copy is the one that goes stale.
    /// </para>
    /// </remarks>
    private static string DescribeProviderFault(
        Vouchfx.Engine.Authoring.Ast.StepNode node,
        IStepProvider instance,
        string member,
        Exception ex,
        bool unwrappedIsProviderFault,
        string resolvedSuiteDirectory)
    {
        var providerTypeName = instance.GetType().FullName ?? instance.GetType().Name;

        var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
        var providerFault = ex is TargetInvocationException || unwrappedIsProviderFault;
        var causeText = DescribeCauseChain(cause, resolvedSuiteDirectory);

        if (!providerFault)
        {
            // NOT "the provider is fine, the engine is broken". This arm covers the reflective
            // plumbing failing BEFORE a provider's method body runs, and most of what reaches
            // it is the PROVIDER's doing: ReflectValidate/ReflectEmit's own "returned null for
            // provider 'X'", an InvalidCastException from a member declared with the wrong
            // return type, FindGenericInterface's "does not implement the required generic
            // interface". Those are provider defects that happen to be detected by the engine
            // rather than thrown from inside the provider. What the arm really says is WHERE
            // the fault was detected and that the suite is not at fault; the quoted cause text
            // is what tells the reader which of the two it is.
            return $"step '{node.Id}': the engine could not invoke {member} on the "
                + $"'{node.CanonicalType}' provider ({providerTypeName}) "
                + $"({cause.GetType().Name}: {causeText}).  The fault is in the provider or in "
                + "how it is packaged, or in the engine's own dispatch - not in the suite. Read "
                + "the message above to tell which. The step was never compiled and never ran.";
        }

        string attribution;
        if (IsHostCondition(cause))
        {
            attribution = $"{cause.GetType().Name} is a process- or host-level condition rather "
                + $"than necessarily a defect in the provider ({providerTypeName}) - it can be "
                + "raised through any frame. The step was never compiled and never ran.";
        }
        else if (IsEnvironmentalCondition(cause))
        {
            attribution = $"{cause.GetType().Name} is a filesystem condition: it may be a defect "
                + $"in the provider ({providerTypeName}), or it may be the host's or the "
                + "suite's - a file removed, locked or unreadable while the step was being "
                + "compiled produces exactly this. The step was never compiled and never ran.";
        }
        else
        {
            attribution = $"This is a defect in the provider ({providerTypeName}), not in the "
                + "suite - the step was never compiled and never ran.";
        }

        return $"step '{node.Id}': the '{node.CanonicalType}' provider's {member} threw "
            + $"{cause.GetType().Name}: {causeText}  {attribution}";
    }

    /// <summary>
    /// Renders <paramref name="cause"/>'s message followed by its inner-exception chain, each
    /// message scrubbed, to a bounded depth.
    /// </summary>
    /// <param name="cause">The exception the engine decided to blame, already unwrapped once.</param>
    /// <param name="resolvedSuiteDirectory">Passed to <see cref="ScrubSuiteDirectory"/> per link.</param>
    /// <remarks>
    /// <para>
    /// <strong>THE BOUND IS FOUR LINKS, AND IT IS A BOUND ON THE DIAGNOSTIC, NOT ON THE
    /// EXCEPTION.</strong> Three reasons to have one at all: a chain is attacker-influenced in
    /// the sense that a provider composes it, this text lands in an archived artefact and a
    /// JUnit XML attribute where an unbounded string is a denial-of-service on the reader and
    /// on the renderer, and <see cref="AggregateException"/>-shaped or self-referential chains
    /// exist. Four is chosen because the shape this exists for — a provider's own wrapper over
    /// a client library's wrapper over the real transport fault — is three deep, so four leaves
    /// one link of headroom without inviting a wall of text.
    /// </para>
    /// <para>
    /// A chain truncated by the bound says so explicitly rather than trailing off, so a reader
    /// who needs the rest knows there IS a rest. Each link is scrubbed individually: a nested
    /// message quotes a path exactly as readily as the outer one does.
    /// </para>
    /// </remarks>
    private static string DescribeCauseChain(Exception cause, string resolvedSuiteDirectory)
    {
        const int MaxLinks = 4;

        var text = ScrubSuiteDirectory(cause.Message, resolvedSuiteDirectory);
        var inner = cause.InnerException;
        var depth = 1;

        while (inner is not null && depth < MaxLinks)
        {
            text += $" -> {inner.GetType().Name}: "
                + ScrubSuiteDirectory(inner.Message, resolvedSuiteDirectory);
            inner = inner.InnerException;
            depth++;
        }

        if (inner is not null)
        {
            text += " -> (inner exception chain truncated)";
        }

        return text;
    }

    /// <summary>
    /// The suite-level counterpart of <see cref="DescribeProviderFault"/>, for a failure of
    /// <see cref="CsxAssembler.Assemble"/> over the fragments the providers emitted
    /// (GATE-MAJOR-1, issue #466).
    /// </summary>
    /// <param name="ex">The exception <c>Assemble</c> threw.</param>
    /// <param name="resolvedSuiteDirectory">Threaded to <see cref="ScrubSuiteDirectory"/>.</param>
    /// <remarks>
    /// <para>
    /// <strong>ATTRIBUTED TO THE SUITE, NOT TO A STEP, AND THAT IS A LIMITATION OF THE
    /// EXCEPTION RATHER THAN A CHOICE.</strong> <see cref="CsxAssemblyException"/> carries a
    /// message and nothing else, and neither of its two throw sites records which fragment was
    /// at fault: the helper-collision site names the CLASS declared twice, and the bare-namespace
    /// site names the offending <c>RequiredUsings</c> ENTRY. Guessing a step from either would be
    /// a confident wrong answer of exactly the kind <c>BindAllSteps</c>' own R-1 remark documents
    /// the cost of. The entry or class name the exception does carry is spliced through, so the
    /// author still has the string to grep their providers for.
    /// </para>
    /// <para>
    /// <strong>Why this seam needed a guard at all.</strong> It is provider-EMITTED content
    /// failing a §13.3.1 rule — a <c>RequiredUsings</c> entry that is not a bare namespace, or
    /// two fragments declaring one helper class with different source text — and
    /// <c>CsxFragment</c> performs no constructor validation, so the fragment is built cleanly
    /// inside <c>Emit</c> and the Emit guard cannot see it. The throw lands at the
    /// <c>Assemble</c> call and took the identical route to exit 0 that the six per-step guards
    /// close. Nothing in <c>src/</c> or <c>tests/</c> catches
    /// <see cref="CsxAssemblyException"/> outside <c>CsxAssembler</c> itself, and it had never
    /// been exercised through <see cref="Compile"/>.
    /// </para>
    /// </remarks>
    private static string DescribeAssemblyFault(Exception ex, string resolvedSuiteDirectory)
    {
        var causeText = DescribeCauseChain(ex, resolvedSuiteDirectory);

        return ex is CsxAssemblyException
            ? "suite CSX assembly failed: one of this suite's providers emitted a CsxFragment "
                + $"the assembler refused ({ex.GetType().Name}: {causeText}).  This is a defect "
                + "in a provider, not in the suite. The exception does not identify which "
                + "fragment, so no step is named - nothing was compiled and no step ran."
            : "suite CSX assembly failed: the engine could not assemble the emitted fragments "
                + $"({ex.GetType().Name}: {causeText}).  Nothing was compiled and no step ran.";
    }

    /// <summary>
    /// <see langword="true"/> for the exception types that can be raised through ANY frame and
    /// are therefore never, on their own, evidence that the frame they surfaced in is defective.
    /// </summary>
    /// <remarks>
    /// Deliberately a short, named list rather than a heuristic.
    /// <see cref="OutOfMemoryException"/> covers <see cref="InsufficientMemoryException"/>, and
    /// <see cref="OperationCanceledException"/> covers
    /// <see cref="System.Threading.Tasks.TaskCanceledException"/>. <c>StackOverflowException</c>
    /// is absent because it cannot be caught at all on .NET Core, so listing it would suggest a
    /// coverage this method does not have.
    /// </remarks>
    private static bool IsHostCondition(Exception cause) =>
        cause is OutOfMemoryException or OperationCanceledException;

    /// <summary>
    /// <see langword="true"/> for the filesystem family — an exception that may equally be a
    /// provider defect, a host condition, or the suite's own doing, so the diagnostic must not
    /// pick one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A SIBLING OF <see cref="IsHostCondition"/>, NOT A WIDENING OF IT.</strong> The two
    /// produce different sentences because they describe different investigations: an
    /// <see cref="OutOfMemoryException"/> tells an author to look at the host, a
    /// <see cref="FileNotFoundException"/> tells them to look at a file — which might be the
    /// provider's bug, their own missing fixture, or an antivirus scanner holding a lock. Folding
    /// the filesystem family into <see cref="IsHostCondition"/> would have made both read as
    /// "process- or host-level", which is wrong for the second.
    /// </para>
    /// <para>
    /// <strong>The most likely production trigger of the <c>Emit</c> guard is in this family.</strong>
    /// <c>ScriptCsharpProvider.Emit</c> reads its <c>file:</c> reference with
    /// <c>File.ReadAllText</c> and its own comment accepts the TOCTOU race against the existence
    /// check <c>Validate</c> performed earlier; the test double
    /// <c>StubSuitePathLeakingEmitProvider</c> models that route verbatim. Without this arm a
    /// deleted or locked <c>.csx</c> produced "This is a defect in the provider
    /// (Vouchfx.Steps.Script.Csharp.ScriptCsharpProvider)" — false, and an accusation against a
    /// Core provider.
    /// </para>
    /// <para>
    /// <see cref="IOException"/> is matched by base type deliberately, so
    /// <see cref="FileNotFoundException"/>, <see cref="DirectoryNotFoundException"/>,
    /// <see cref="PathTooLongException"/> and the rest of the family come with it;
    /// <see cref="UnauthorizedAccessException"/> is named separately because it does not derive
    /// from <see cref="IOException"/>. Note the contrast with
    /// <c>ScenarioRunner.HashFixtureOrNull</c>'s catch filter, which enumerates a similar family
    /// for a different purpose: there the set decides WHETHER to swallow, here it decides only
    /// what the message SAYS.
    /// </para>
    /// </remarks>
    private static bool IsEnvironmentalCondition(Exception cause) =>
        cause is IOException or UnauthorizedAccessException;

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
    /// step's own <see cref="BoundStep.HostResourcesFailure"/> is ever reported. This is
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
    /// The bound steps, and a non-null <see cref="ValidationFailure"/> instead in either of two
    /// cases: the defensive "registry lookup failed after AST build already verified the type"
    /// case, which should never happen in practice; or a provider whose <c>Bind</c> THREW
    /// (issue #413), which is a provider defect rather than an authoring fault but is reported
    /// through the same channel so it reaches a taxonomy verdict instead of escaping the run.
    /// In both cases the bound-steps list is incomplete and must not be used, mirroring every
    /// other early-return failure in this file.
    /// </returns>
    /// <param name="ast">The normalised scenario AST.</param>
    /// <param name="registry">The frozen provider registry.</param>
    /// <param name="resolvedSuiteDirectory">
    /// The absolute host directory this scenario's relative paths resolve against, threaded in
    /// solely so a throwing <c>Bind</c>'s message can have it substituted out before it reaches
    /// an archived artefact (SEC-MAJOR-1 — see <see cref="ScrubSuiteDirectory"/>). REQUIRED
    /// rather than defaulted: an optional parameter here would let a future caller silently opt
    /// out of a disclosure scrub, which is the one kind of default this file should not offer.
    /// </param>
    internal static (List<BoundStep> BoundSteps, ValidationFailure? RegistryFailure) BindAllSteps(
        ScenarioAst ast, StepKindRegistry registry, string resolvedSuiteDirectory)
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

            // ── Bind (GUARDED — issue #413; it was UNGUARDED, and the comment here used to
            // document that propagation as deliberate). IStepBinder<T>.Bind is still called
            // EXACTLY once per step per compile: nothing about the call count changed, only
            // what happens when it throws.
            //
            // WHY A FAILURE RATHER THAN A DEFERRAL. HostResources' throw is DEFERRED onto the
            // step's own BoundStep (below) so that step's own Validate gets first refusal on
            // whatever model condition triggered it. There is nothing to defer to here: a
            // throwing Bind means the model was never constructed, so no Validate can examine
            // it. The throw becomes a ValidationFailure instead, returned exactly the way the
            // registry-lookup failure above is, and Compile's caller maps it to
            // Verdict.Inconclusive with the rest of the pre-topology authoring/compile faults.
            //
            // WHY IT IS CAUGHT AT ALL (§12.1). Unguarded, this exception escaped every caller —
            // ScenarioRunner, ParallelSuiteRunner, RunCommand — so a provider defect produced no
            // verdict and no --junit/--html/--events artefacts. AND THE EXIT CODE WAS 1, NOT some
            // code outside the taxonomy, which is the version of this claim to keep: the CLI
            // invokes through a bare InvocationConfiguration, whose EnableDefaultExceptionHandler
            // defaults to true on the pinned System.CommandLine 2.0.0 GA, so the framework caught
            // the escape and returned TestFailure — a provider crash telling CI the SUITE observed
            // a product defect. Measured by Vouchfx.Cli.Tests' SystemCommandLineExitCodeTests.
            // `--parallel` was worse still: it caught the throw further out and classified it as an
            // EnvironmentError, which exits 0 for a run that executed nothing (#390) — a green CI
            // build over a provider bug, and a flag deciding between 1 and 0 for one fault. Both
            // are closed here, at the throw site, so the two run paths cannot answer differently.
            //
            // WHY Inconclusive AND NOT EnvironmentError. EnvironmentError is reserved for
            // infrastructure an author cannot fix by editing the suite — a container, an image,
            // a network — and, on a run that started nothing, it exits 0. A provider whose Bind
            // throws is neither infrastructure nor the author's fault: it is a defect that left
            // the engine unable to reach a verdict, which is exactly §12.1's Inconclusive. It is
            // the same reasoning TopologyAuthoringException records for its own base class.
            //
            // THE CATCH IS DELIBERATELY UNFILTERED, including OperationCanceledException, and that
            // differs from RunCommand's own backstop for a reason rather than by accident: no
            // cancellation token reaches IStepBinder<T>.Bind — the v1 contract passes a YamlNode
            // and an IBindingContext and nothing else — so a cancellation surfacing in here is
            // not a stop anybody requested, and there is nothing for a filter to preserve.
            // RunCommand's frame DOES receive a token and must tell a user stop from a timeout;
            // this one has no such distinction to make.
            //
            // WHAT THAT ARGUMENT DOES NOT ESTABLISH is whose fault the cancellation is. "The
            // engine passed no token" bounds what was REQUESTED; a provider holding its own CTS,
            // or an HttpClient default timeout arriving as TaskCanceledException (an
            // OperationCanceledException), is neither a requested stop nor evidence of a defect.
            // The diagnostic's ATTRIBUTION is DescribeProviderFault's job, and IsHostCondition
            // is where OperationCanceledException is given a sentence that is true of it.
            //
            // ORDERING IS UNCHANGED: this returns on the FIRST throwing step, in step order,
            // exactly where the propagation used to unwind from.
            object model;
            try
            {
                model = ReflectBind(instance, node.RawNode, bindingCtx);
            }
            catch (Exception ex)
            {
                // TWO FAULTS ARRIVE HERE AND THEY ARE NOT THE SAME THING (peer-review NIT-1).
                // ReflectBind does the closed-generic interface lookup and the MethodInfo resolve
                // BEFORE it invokes anything, so a failure in either is an ENGINE plumbing fault —
                // it is not "Bind threw", and reporting it as such would send a reader to read a
                // provider's Bind that never ran. MethodInfo.Invoke, and only MethodInfo.Invoke,
                // wraps a provider's own exception in TargetInvocationException; that wrapper is
                // therefore the discriminator, and unwrapping it is what gets the author the
                // provider's own message rather than "Exception has been thrown by the target of
                // an invocation". Both map to the same ValidationFailure channel and the same
                // Inconclusive verdict — what differs is only which component the diagnostic
                // blames, which is the whole value of a diagnostic here.
                //
                // The two-armed wording moved into DescribeProviderFault when issue #466 gave the
                // other five provider calls the same guard: SIX call sites, one spelling.
                // `unwrappedIsProviderFault: false` is the Bind/Validate/Emit reading of an
                // UNWRAPPED exception — those three go through FindGenericInterface first, so an
                // unwrapped throw is engine plumbing.
                return (boundSteps, new ValidationFailure(
                    DescribeProviderFault(
                        node,
                        instance,
                        "Bind",
                        ex,
                        unwrappedIsProviderFault: false,
                        resolvedSuiteDirectory)));
            }

            // ── Host resources (tolerant, S07-F-01a) — GUARDED and DEFERRED (G-A,
            // gatekeeper, fix round 3; this half of the comment used to be missing — the
            // Bind half above justified only itself). Unlike Bind, a throwing HostResources
            // getter/enumerator is CAUGHT here rather than propagated: this method
            // (BindAllSteps) runs entirely in Pass 1, before ANY step's Validate has run, so
            // letting it propagate immediately — the pre-G-A behaviour — could abort the
            // whole compile before a community provider's OWN Validate ever got a chance to
            // turn whatever invalid model condition triggered the throw into a clean,
            // located diagnostic instead of a raw reflection exception. The exception is
            // captured onto this step's BoundStep and reported by Compile's Pass 2,
            // immediately after THAT step's own Validate has run (see BoundStep.
            // HostResourcesFailure's own remarks) — so a bad model that Validate can
            // explain never even reaches that point, and only a genuine HostResources bug
            // Validate does NOT already cover surfaces there, as a ValidationFailure naming
            // the provider (issue #466 changed the terminal shape from a rethrow; the
            // POSITION, after this step's own Validate, is G-A's and is unchanged). ──
            List<HostResourceRequirement> hostResources;
            Exception? hostResourcesFailure = null;
            try
            {
                hostResources = ReflectHostResources(instance, model).ToList();
            }
            catch (Exception ex)
            {
                hostResources = new List<HostResourceRequirement>();
                hostResourcesFailure = ex;
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
                        "A service and a dependency cannot share a name - rename one of the two.");
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
                            "endpoint - rename one of the two.");
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
        // community provider's throwing Bind would already have returned Pass 1's own
        // ValidationFailure before this method is ever called (issue #413 — see BindAllSteps'
        // remarks); a throwing HostResources() enumerator, by contrast, is CAUGHT in Pass 1
        // (G-A, gatekeeper, fix round 3) and deferred onto that step's own BoundStep.
        // HostResourcesFailure rather than propagated — so bound.HostResources here is
        // ALWAYS a safe, fully-materialised (possibly empty) list, never a live throw, either
        // way. There is nothing left here that can throw on a per-step basis, so no per-step
        // catch/continue is needed (contrast the pre-M5 pre-pass, which called Bind
        // speculatively a second time and had to swallow exactly that class of exception).
        // This method itself never reads HostResourcesFailure — Compile's Pass 2 turns it into
        // a ValidationFailure, after that step's own Validate has had its chance (see
        // BoundStep's own remarks).
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
                    // Scrubbed: VarName and Kind are PROVIDER-supplied (this method's
                    // suiteDirectory parameter is the same value Compile scrubs against). The
                    // other two collision messages in this method interpolate only
                    // environment.services / environment.dependencies map keys and
                    // engine-composed owner text, so they carry nothing a provider chose and
                    // are deliberately left alone.
                    collisionFailure ??= new ValidationFailure(ScrubSuiteDirectory(
                        $"host resource '{hostReq.VarName}' (kind '{hostReq.Kind}', declared by " +
                        $"step '{bound.Node.Id}') collides with {owner}. A host resource (e.g. a " +
                        "webhook listener) cannot share a name with a declared service or with a " +
                        "dependency's own sidecar endpoint - rename one of the two.",
                        suiteDirectory));
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
