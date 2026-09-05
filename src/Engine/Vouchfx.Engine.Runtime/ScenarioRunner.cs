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
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Abstractions.Reproducibility;
using Vouchfx.Engine.Abstractions.Retry;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Secrets.Vault;
using Vouchfx.Engine.Abstractions.Security;
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
    IReadOnlyList<(string ScenarioName, Verdict Verdict)> ScenarioVerdicts)
{
    /// <summary>
    /// <see langword="false"/> when the suite was refused before any topology was built, so no
    /// container started and no step ran (#369). <see langword="true"/> by default, which every
    /// construction outside the without-topology completion path keeps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set by <c>CompleteWithoutTopologyAsync</c> and nowhere else, deliberately: that method IS
    /// the set of paths that never reached a topology, so this is a property of the route rather
    /// than a hand-maintained list of doors — the shape <c>Assurance</c>'s own remarks describe
    /// going stale three times before it was derived instead of enumerated.
    /// </para>
    /// <para>
    /// <strong>It is not "the topology failed".</strong> A topology that fails to START also
    /// executes nothing, and reaches this same completion path since #407 — but it arrives
    /// carrying <see cref="Verdict.EnvironmentError"/>, and the exit rule that consumes this is
    /// scoped to <see cref="Verdict.Inconclusive"/> for exactly that reason. Widening it to every
    /// verdict would silently close #390, which is deliberately open because doing so would
    /// redden every suite whose unrelated container was slow to come up.
    /// </para>
    /// </remarks>
    public bool ExecutedAnyScenario { get; init; } = true;
    /// <summary>
    /// What this suite established about the <c>security</c> blocks it declared: what was declared,
    /// what REQ-005's probe confirmed, and which door (if any) refused
    /// (security-assurance-derivation, REQ-001).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>There is no producer list to maintain, and that is the change.</strong> This member
    /// used to be a <c>bool</c> whose meaning was documented here as an OPEN, hand-maintained
    /// enumeration of the doors that set it — a list that went stale three times, on the very
    /// surface three other doc sites designated as its authority. No door decides the OUTCOME now.
    /// <strong>How the two halves come together depends on which runner produced this record, and
    /// both do.</strong> Under <c>RunSuiteAsync</c> (the shared-topology path) a door records only
    /// <c>Refusal</c> — WHICH door — while <c>RunSuiteAsync</c> itself walks
    /// <c>SecuredTargets.Enumerate</c> ONCE for the suite to fill <c>Declared</c>, WHAT the document
    /// asserted. Under <c>ParallelSuiteRunner</c> this member is instead a FOLD of per-scenario
    /// assurances the core already completed: since issue #409 the core is HANDED the declaration
    /// and every one of its doors attaches it, so there a door carries both halves and nothing
    /// downstream repairs it. The invariant common to the two, and the one to carry away, is that a
    /// door never DERIVES what was declared.
    /// <see cref="SecurityAssurance.Unconfirmed"/> is then the single predicate over the two halves,
    /// whichever runner assembled them. Read that as scoped to the DERIVED case: two of
    /// <see cref="SecurityAbortKind"/>'s members are named in that predicate and hard-code their
    /// answer — <c>SecurityDeclarationRejected</c> always raises, <c>TopologyUnavailable</c> never
    /// does — each for a reason recorded on the member itself.
    /// </para>
    /// <para>
    /// Init-only rather than a positional parameter, so every existing
    /// <c>new SuiteResult(verdict, verdicts)</c> call site — and every test constructing one —
    /// keeps compiling and defaults to <see cref="SecurityAssurance.None"/>. It carries NO verdict
    /// semantics of its own: it is read by the CLI's exit-code decision alone
    /// (<c>ExitCodes.FromVerdict</c>) and never by the taxonomy, the renderers or the event stream.
    /// </para>
    /// </remarks>
    public SecurityAssurance Assurance { get; init; } = SecurityAssurance.None;
}

// ---------------------------------------------------------------------------
// ScenarioCoreResult
// ---------------------------------------------------------------------------

/// <summary>
/// What one topology-owning scenario run produced: its verdict, its complete event buffer, and
/// REQ-018's narrow security signal.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the <c>(Verdict, List&lt;string&gt;)</c> tuple this seam used before slice E, because
/// the parallel runner needs the security signal to reach its own
/// <see cref="SuiteResult.Assurance"/> — otherwise REQ-018's carve-out would
/// silently not apply under <c>--parallel</c>, which is the same green-pipeline-on-an-unverified-
/// security-suite failure the requirement exists to prevent, reached through an opt-in flag.
/// </para>
/// <para>
/// The implicit conversion from the old tuple shape is deliberate and is what keeps the change
/// narrow: every <c>return (verdict, buffer);</c> in the long core method, and every existing test
/// double built against the old shape, compiles unchanged and defaults the assurance to
/// <see cref="SecurityAssurance.None"/>.
/// </para>
/// <para>
/// <strong>The enumeration anchor this paragraph used to carry is GONE, not corrected.</strong> It
/// designated a grep for this record's constructor as the authoritative producer list — a grep
/// blind to the two implicit-conversion operators on this very record (both of which hard-coded
/// <c>false</c>) and to the tuple returns routing through them. A list that cannot enumerate
/// itself is the same class of defect as the doors it documented. There is no producer list under
/// the derived rule: see <see cref="SecurityAssurance"/>.
/// (The retired grep is described here, not quoted: a retraction that spells out the string it
/// retracts is still found by every future sweep for that string, which is how a dead claim gets
/// re-litigated. State what was retired; let the reader reconstruct it if they need it.)
/// </para>
/// </remarks>
/// <param name="Verdict">The scenario's aggregate verdict.</param>
/// <param name="Buffer">
/// The complete JSON Lines event buffer for the chosen path; non-empty, and always containing a
/// scenario-completed event.
/// </param>
public sealed record ScenarioCoreResult(Verdict Verdict, List<string> Buffer)
{
    /// <summary>
    /// What this scenario established about the <c>security</c> blocks its document declared: all
    /// three halves — what was declared, what REQ-005's probe confirmed, and which door (if any)
    /// refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Complete at every door the core returns from, and therefore self-describing for a
    /// caller that is not the aggregator — which the Docker drills are (issue #409).</strong>
    /// <see cref="SecurityAssurance.Declared"/> used to be left empty here on the grounds that the
    /// core validates (Step 2) before it parses (Step 3), so two of its doors return with no AST to
    /// walk. That was a true account of why the core could not DERIVE the declaration and a false
    /// account of whether it could REPORT one: it now takes the caller's walk as a parameter, so
    /// every door attaches the same declaration and no caller has to repair what it is handed.
    /// </para>
    /// <para>
    /// The repair that used to do it — <c>ParallelSuiteRunner</c> re-attaching its own walk over
    /// whatever the core returned — is deleted, not retained as belt-and-braces. It was a second
    /// corrector for one value, and the fact that its walk agreed with the core's by construction is
    /// exactly what hid the two doors where the core had no walk at all.
    /// </para>
    /// <para>
    /// <strong>The implicit tuple conversion below is how that completeness could be lost again, and
    /// it is named here rather than removed.</strong> It defaults this member to
    /// <see cref="SecurityAssurance.None"/> — an empty declaration — so a
    /// <c>return (verdict, buffer);</c> added to
    /// <c>ScenarioRunner.RunScenarioOwningTopologyAsync</c> would reintroduce #409 at that one door,
    /// silently, with nothing downstream repairing it any more. Every one of the core's six returns
    /// is an explicit object initialiser attaching <c>declaredTargets</c>; the conversion exists for
    /// the test doubles and for callers that never asked about security, and a new door in the core
    /// is not either of those.
    /// </para>
    /// <para>
    /// <strong>That hazard is enforced, not merely described</strong>, by
    /// <c>ScenarioCoreDeclarationCensusTests</c>: one row counts the core's returns against its
    /// attaches and requires them EQUAL, and a second refuses the bare tuple shape outright. They
    /// exist because the behavioural rows cover only the doors somebody wrote a row for — measured,
    /// a door that silently stopped attaching left the whole non-Docker suite green.
    /// </para>
    /// </remarks>
    public SecurityAssurance Assurance { get; init; } = SecurityAssurance.None;

    /// <summary>
    /// Converts the pre-slice-E <c>(Verdict, Buffer)</c> tuple shape, defaulting
    /// <see cref="Assurance"/> to <see cref="SecurityAssurance.None"/>.
    /// </summary>
    public static implicit operator ScenarioCoreResult((Verdict Verdict, List<string> Buffer) result) =>
        new(result.Verdict, result.Buffer);

    /// <summary>
    /// The named alternative to the implicit conversion operator, for callers and analysers that
    /// prefer one (CA2225).
    /// </summary>
    public static ScenarioCoreResult FromValueTuple((Verdict Verdict, List<string> Buffer) result) =>
        new(result.Verdict, result.Buffer);
}

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
    // is resolved lazily, at RUN time, by EnvironmentConfiguredVaultKvClient
    // (so ${secret:vault/...} validates at compile time even when VAULT_ADDR/VAULT_TOKEN
    // are not set at validation time; a missing config surfaces as an EnvironmentError
    // only if SOMETHING actually resolves a vault secret — a step's own field as that step
    // executes, or an environment-level `security.clientKeyPassword` at the certificate load,
    // which happens BEFORE any step runs.  The earlier wording said "only if a step actually
    // resolves" and was a false statement about behaviour once clientKeyPassword shipped, not
    // merely a stale adjective).
    private static readonly string[] s_knownSecretSources =
        BuildSecretResolvers().Select(r => r.Source).ToArray();

    /// <summary>
    /// The resolver source identifiers this engine can resolve, for in-assembly consumers that
    /// must refuse EXACTLY what the pre-compile validation pass refuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="internal"/> so <c>SecurityConfigurationAccessor</c>'s run-time
    /// <c>clientKeyPassword</c> guard can hold itself to the same predicate the validation pass
    /// applies. That guard's own remarks already claimed it "refuses exactly what
    /// <c>vouchfx validate</c> refuses, in one spelling rather than two"; sharing this set is what
    /// makes that true rather than approximately true.
    /// </para>
    /// <para>
    /// Exposed as a <see cref="ReadOnlyCollection{T}"/> over the array rather than as the array
    /// typed to an interface. <see cref="IReadOnlyList{T}"/> on a <c>string[]</c> is a promise the
    /// runtime does not keep — any in-assembly consumer can cast it back and rewrite the entries,
    /// and this particular array is a SECURITY ALLOWLIST, so a mutation would silently widen what
    /// both the validator and the run-time guard accept. No such cast exists today; the wrapper
    /// makes one impossible rather than merely absent.
    /// </para>
    /// </remarks>
    internal static IReadOnlyCollection<string> KnownSecretSources { get; } =
        Array.AsReadOnly(s_knownSecretSources);

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

    /// <summary>
    /// Builds a disposable <see cref="SecretAccessorScope"/> over
    /// <see cref="BuildSecretResolvers"/> — the accessor a caller resolves through, plus ownership
    /// of the resolvers' disposal (§17, client-key-password REQ-009).
    /// </summary>
    /// <param name="sharedLedger">
    /// The run-scoped <see cref="ResolvedSecretLedger"/> the new scope's accessor records into
    /// (REQ-010), or <see langword="null"/> for a ledger private to the scope.
    /// </param>
    /// <remarks>
    /// Internal rather than private because the <c>--watch</c> run path
    /// (<c>Vouchfx.Cli.Watch.WatchRunner</c>) builds its own topology, and therefore its own
    /// probe-time <c>SecurityConfigurationAccessor</c>, outside this class. Routing it through the
    /// SAME factory is what keeps the set of resolvable secret sources identical on both paths —
    /// the property <see cref="s_knownSecretSources"/> already relies on for the validation pass.
    /// </remarks>
    internal static SecretAccessorScope CreateSecretAccessorScope(
        ResolvedSecretLedger? sharedLedger = null) => new(BuildSecretResolvers(), sharedLedger);

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
            // This wrapper holds only YAML, so it walks the declaration through the adapter rather
            // than being handed one (issue #409). It then DISCARDS the assurance — it returns a
            // Verdict — so the walk buys this path nothing today. It is done anyway because the
            // alternative is passing an empty list that is a lie about the document: the next
            // caller to start reading `Assurance` here would inherit exactly the silent
            // false-negative #409 was filed for, and would have no reason to look for it.
            DeclaredTargetsOf(yamlText, registry),
            appHostAssemblyName,
            output,
            seedBaseDirectory,
            livePump: null,
            cancellationToken).ConfigureAwait(false);

        TerminalRenderer.Render(buffer, output, diffLookup);
        return verdict;
    }

    /// <summary>
    /// Walks the <c>security</c> declarations of a document a caller holds only as YAML — the
    /// adapter to <see cref="RunScenarioOwningTopologyAsync"/>'s <c>declaredTargets</c> parameter
    /// for a caller that has not already parsed (issue #409).
    /// </summary>
    /// <param name="yamlText">The raw text of a <c>.e2e.yaml</c> document.</param>
    /// <param name="registry">The frozen provider registry <c>AstBuilder</c> binds against.</param>
    /// <returns>
    /// The document's declared targets, or an empty list when it cannot be parsed into an AST at
    /// all.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>This is not a second spelling of "does this document declare security".</strong> It
    /// parses and then defers to <c>SecuredTargets.Enumerate</c> — the one canonical walk, the same
    /// one every other producer of a <c>Declared</c> set calls — so it cannot disagree with them
    /// about any document both can read. Recovering a declaration from a document neither can read
    /// would need a raw-YAML scan for a <c>security:</c> key, which IS a second spelling and is
    /// refused here as it is refused in <c>UnbuiltDocument.Assure</c>.
    /// </para>
    /// <para>
    /// <strong>The empty answer is weaker than "the document declared nothing", and the difference
    /// is a whole failure class.</strong> <c>AstBuilder.Build</c> also refuses documents whose
    /// <c>environment</c> block bound perfectly well — an unknown step type, a duplicate step id —
    /// and this returns empty for those too, because it has no AST to walk and will not guess from
    /// the text. That is not a hole: such a document is exactly what the CLI routes to the runner as
    /// an <c>UnbuiltDocument</c>, where the RECOVERED document is walked and its declaration is
    /// read (issue #411). This adapter is for a caller holding raw YAML and nothing else, which in
    /// this assembly is <see cref="RunAsync"/> and the direct-call tests.
    /// </para>
    /// <para>
    /// It swallows every exception from the parse deliberately: this is not a door and reports no
    /// fault. Whatever is wrong with the document, the caller is about to hand it to
    /// <see cref="RunScenarioOwningTopologyAsync"/>, whose own schema and parse doors diagnose it —
    /// once, in the event stream, with a verdict attached. Throwing here would move that diagnosis
    /// to a stack trace and lose the buffer with it.
    /// </para>
    /// <para>
    /// <strong>The catch FAILS OPEN, and the precondition that keeps it harmless is
    /// same-text-same-registry.</strong> An empty return is indistinguishable from "this document
    /// declares nothing", so if a parse failed for a reason the CORE will not also hit, a secured
    /// document would be handed on with an empty declaration and its refusal would stop raising —
    /// a silent exit 0 on an unconfirmed <c>security</c> block. That cannot happen for a
    /// DETERMINISTIC parse failure while both sides read the SAME <paramref name="yamlText"/>
    /// through the SAME <paramref name="registry"/>: the core runs the identical
    /// <c>YamlDocumentParser.Parse</c> + <c>AstBuilder.Build</c> pair, so anything that throws here
    /// throws there too and the core's own parse door refuses the document before the declaration
    /// matters.
    /// </para>
    /// <para>
    /// <strong>Two ways out of that equivalence, and both are named rather than assumed away.</strong>
    /// A DIFFERENT registry — one missing a provider — makes <c>AstBuilder</c> reject an unknown step
    /// type here and accept it there, which is the fail-open direction; that is what the precondition
    /// is for. And a NON-DETERMINISTIC failure — the OOM class: <c>OutOfMemoryException</c>,
    /// <c>StackOverflowException</c>'s cousins, a transient failure under memory pressure — can throw
    /// on this call and not on the core's, since the two parses are separate executions. Neither is
    /// worth a code change: the first is a caller error the precondition states, and the second is a
    /// process already in trouble, where a swallowed parse is not the defect that matters. The shape
    /// stays as it is — a targeted catch would still be silent about exactly these cases, and
    /// throwing would replace a diagnosed verdict with a stack trace.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<SecuredTarget> DeclaredTargetsOf(
        string yamlText, StepKindRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(yamlText);
        ArgumentNullException.ThrowIfNull(registry);

        try
        {
            var ast = AstBuilder.Build(YamlDocumentParser.Parse(yamlText), registry);
            return SecuredTargets.Enumerate(ast.Environment).ToArray();
        }
#pragma warning disable CA1031 // Deliberate: see the remarks — this is an adapter, not a door.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Array.Empty<SecuredTarget>();
        }
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
    /// <param name="declaredTargets">
    /// WHAT this document declared: the caller's <c>SecuredTargets.Enumerate</c> walk of the AST it
    /// already holds. Supplied rather than derived so that the assurance this method returns is
    /// correct at EVERY door, including the two that return before Step 3 has parsed anything
    /// (issue #409). Empty for an unsecured document, and empty is also the honest answer for a
    /// caller that could not build an AST at all — see <see cref="DeclaredTargetsOf"/>, the adapter
    /// for a caller holding only YAML.
    /// <para>
    /// <strong>PRECONDITION: it must be walked from the SAME document as <paramref name="yamlText"/>,
    /// and the failure is silent and fail-open.</strong> The success door pairs THIS set, supplied by
    /// the caller, with the <c>Confirmed</c> set this method derives from the topology's own probe —
    /// and <c>SecurityAssurance.Unconfirmed</c> compares the two by
    /// <c>SecuredTargets.IdentityOf</c>. Identities from two different documents are not comparable,
    /// so a mismatched set makes the comparison meaningless rather than merely wrong. The dangerous
    /// direction is the NARROWER one: supply fewer targets than the document declares and every
    /// remaining one is confirmed, so a secured suite the engine refused reports a clean exit. A
    /// WIDER set fails closed and is merely noisy. Nothing here can check this — the method has no
    /// AST until Step 3, which is the whole reason the parameter exists — so it is stated rather
    /// than validated, and every in-tree call site derives the two from one string.
    /// </para>
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
    internal static async Task<ScenarioCoreResult> RunScenarioOwningTopologyAsync(
        StepKindRegistry registry,
        string yamlText,
        string scenarioName,
        IReadOnlyList<SecuredTarget> declaredTargets,
        string? appHostAssemblyName,
        TextWriter output,
        string? seedBaseDirectory,
        LiveEventPump? livePump,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(declaredTargets);

        var runId = Guid.NewGuid().ToString("n");
        var buffer = new List<string>();

        // ── The RUN's one scrub ledger (client-key-password REQ-010) ──────────
        // Created here, at the top of the run, and handed to BOTH accessors this method
        // causes to exist: the topology probe's (below) and — threaded down through
        // RunScenarioAgainstTopologyAsync — the per-scenario step accessor's. The two
        // accessors stay separate because their LIFETIMES differ (the probe's is released
        // the moment the topology is up; the scenario's lives for the scenario); the ledger
        // is the one thing that must span both, because "which values must never appear in
        // emitted text" is a property of the RUN, not of either scope.
        //
        // Without this, a passphrase resolved for the probe was recorded in a ledger no
        // step-path scrubber ever read, so it survived into a step observation — and a
        // passphrase resolved for a step was invisible to the environment-error emission
        // path below. Both directions are now covered by one net.
        //
        // Cost, stated rather than discovered later: the ledger retains a plaintext copy of
        // every value it records (you cannot scrub a value you do not hold) for the whole
        // run rather than for one scope. Default-ALC, never serialised, collected with this
        // method's frame.
        var runSecretLedger = new ResolvedSecretLedger();

        // The #375 sibling net, created and scoped exactly as the value ledger above is: ONE per
        // run, shared by the topology probe's security accessor and every scenario's, so a
        // resolved security-material path handed out while the probe ran is substitutable from
        // text emitted on a step path. Separate from `runSecretLedger` deliberately — a path is
        // not a secret and its substitution is the field's DECLARED text, not a redaction marker
        // (see SecurityPathDisclosureLedger's own header).
        var runPathLedger = new SecurityPathDisclosureLedger();

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

            // #372 stamped at THIS seam too. The terminal keeps one line per error below; the
            // record carries them joined, in the same shape WatchIterationPlan's schema door
            // already joins its own list, because the record has one message field and an author
            // triaging from a JUnit publisher needs every error the door found, not the first.
            buffer.Add(StepEventBuilder.ScenarioCompletedLine(
                runId,
                DateTimeOffset.UtcNow,
                scenarioName,
                Verdict.Inconclusive,
                new VerdictCounts { Inconclusive = 1 },
                runSecretLedger,
                runPathLedger,
                string.Join("; ", validationResult.Errors.Select(e => e.Message))));

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

            // THE SPECULATIVE RE-PARSE THIS BRANCH USED TO CARRY IS GONE, and its absence is the
            // check that the derivation moved (security-assurance-derivation, REQ-002). It existed
            // for one reason: the flag was decided HERE, and deciding it needed "does this document
            // declare security" — a question with no AST to ask, because this method validates
            // (Step 2) before it parses (Step 3). Nothing here decides any longer, and nothing here
            // re-derives: this door records WHICH door refused and attaches the `declaredTargets`
            // its CALLER walked, which is the half no door of this method can compute for itself.
            //
            // ISSUE #409 IS WHAT THE `Declaring` CALL BELOW CLOSES. This door used to return
            // `None.Refusing(...)` with an empty `Declared`, so a secured document rejected here
            // read `Unconfirmed == false` — and only ParallelSuiteRunner, re-walking the AST it
            // holds as a parameter, made it read true. Two callers, two answers about one document,
            // with the Docker drills on the wrong side of the divergence because they call this
            // method directly. There is no repair step now: the caller supplies the declaration
            // ONCE, on entry, and every door of this method is correct without help.
            //
            // The located-in-the-block distinction survives as EVIDENCE rather than as a decision:
            // a schema error at or inside a `security` node is a refusal OF the declaration, which
            // is the one shape the canonical walk cannot see (`security: mtls` binds no
            // SecuritySpec at all). See SecurityAbortKind.SecurityDeclarationRejected.
            var errorIsInsideTheSecurityBlock = RejectsASecurityDeclaration(validationResult.Errors);

            return new ScenarioCoreResult(Verdict.Inconclusive, buffer)
            {
                Assurance = SecurityAssurance.None
                    .Declaring(declaredTargets)
                    .Refusing(
                        errorIsInsideTheSecurityBlock
                            ? SecurityAbortKind.SecurityDeclarationRejected
                            : SecurityAbortKind.AuthoringFault),
            };
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

            var parseFault = $"Parse / AST error: {ex.Message}";

            buffer.Add(StepEventBuilder.ScenarioCompletedLine(
                runId,
                DateTimeOffset.UtcNow,
                scenarioName,
                Verdict.Inconclusive,
                new VerdictCounts { Inconclusive = 1 },
                runSecretLedger,
                runPathLedger,
                parseFault));

            // Issue #266, Item 4: ex.Message can echo untrusted YAML/AST-builder text back
            // verbatim — sanitise it before it reaches the terminal/CI log. The RECORD above
            // carries the unsanitised text deliberately (#372): sanitising is terminal hygiene,
            // and the renderers escape for their own formats.
            await output.WriteLineAsync(
                DisplaySanitiser.SanitiseForDisplay(parseFault))
                .ConfigureAwait(false);

            livePump?.PostRange(buffer);

            // An unparseable document is an authoring fault before any container started, like
            // every other pre-topology refusal. `declaredTargets` is attached here for the same
            // reason it is at the schema door — this method does not decide what a document
            // declared — and NOT because this door expects it to be non-empty. A document whose
            // YAML this method cannot parse is one whose caller almost certainly could not parse it
            // either, so the walk that reached here yields nothing and this raises nothing: the
            // honest answer, and byte-for-byte the behaviour that shipped. What changes is that the
            // emptiness is now the WALK's finding rather than this door's silence, so the two
            // callers cannot disagree about it: under the CLI they never could, since it parses the
            // same text with the same parser and reaches here only when its own parse failed too.
            //
            // COUPLED TO #411'S DOCUMENTED RESIDUAL, and the coupling runs one way. That residual is
            // that three discovery failure classes bind nothing, so no declaration is recovered for
            // them — deliberately, because recovering one would need a raw-YAML scan for a
            // `security:` key, a second spelling of "does this document declare security". If that
            // decision is ever revisited and a caller starts supplying a declaration for a document
            // it could not parse, THIS DOOR STARTS RAISING with no edit here: the refusal is already
            // AuthoringFault, `Confirmed` is empty by construction, and `Unconfirmed`'s first
            // disjunct then holds. That is the correct behaviour for such a change and is exactly
            // why nothing is hard-coded here — but it is a live exit-code consequence sitting behind
            // someone else's decision, so it is written down rather than discovered.
            return new ScenarioCoreResult(Verdict.Inconclusive, buffer)
            {
                Assurance = SecurityAssurance.None
                    .Declaring(declaredTargets)
                    .Refusing(SecurityAbortKind.AuthoringFault),
            };
        }

        // THE LOCAL THAT USED TO SIT HERE — a second `SecuredTargets.Enumerate(ast.Environment)`
        // walk, for the returns from this point on — IS GONE, and its absence is what closes #409.
        //
        // It bought correctness from the AST onwards and nothing before it, because the two doors
        // above return before any AST exists. So this method returned an assurance that was right
        // at four doors and wrong at two, and ParallelSuiteRunner patched the difference afterwards
        // with a `result.Assurance.Declaring(declared)` re-attach. That repair was the second source
        // of truth: two walks, two callers, and only one of them corrected — a direct caller would
        // read `Unconfirmed == false` for a secured document the aggregator reported as unconfirmed.
        //
        // MEASURED BY RunParallelAsyncTests
        // .RunScenarioOwningTopologyAsync_SecuredWithAnOutOfBlockSchemaError_IsUnconfirmed, NOT by
        // the Docker drills — the distinction matters and this comment used to blur it. The drills
        // ARE direct callers, which is what made the divergence consequential rather than academic,
        // but every row they run reaches a post-parse door, so not one of them ever observed it.
        // That is the point: the walks agreed by construction, so nothing was wrong anywhere a test
        // happened to look, and a row had to be written at a pre-parse door to see it at all.
        //
        // `declaredTargets` is now a PARAMETER, walked once by the caller that holds the AST, and
        // this method answers WHAT the document declared identically at every door it can return
        // from. The repair in ParallelSuiteRunner is deleted rather than kept as belt-and-braces:
        // a second corrector for a value that no longer needs correcting is how the two spellings
        // come back. Pinned by RunParallelAsyncTests
        // .RunScenarioOwningTopologyAsync_SecuredWithAnOutOfBlockSchemaError_IsUnconfirmed, the row
        // that was measured red against the pre-fix core.

        // ── Steps 4 + 5c: the pre-topology authoring passes, run TOGETHER ─────
        //
        // Provider pipeline (bind / validate / resources / emit) and the central, provider-uniform
        // secret-reference pass over every substitutable field text. Both run BEFORE the topology
        // is started and BEFORE CompileOnce, so an authoring fault costs no containers — the
        // scenario never ran, so the verdict is Inconclusive, not Fail.
        //
        // THEY ARE ONE DOOR REPORTING BOTH FAULTS, and that is #399's actual remainder. They used
        // to be two sequential doors, and the shared-topology path ran them in the OPPOSITE order,
        // so a document with a security preflight fault AND a step secret fault reported the
        // preflight one under `--parallel` and the step one under `run` — two paths naming
        // different faults in the same document, with an exit code (4, from at least four doors on
        // this surface) that could not tell an author which. Neither pass READS THE OTHER'S RESULT
        // — that independence is what makes running both sound — and running both and reporting
        // both makes the reported diagnosis a property of the DOCUMENT rather than of which pass
        // ran first, which is the same rule the assurance derivation applies to the exit code.
        //
        // "BOTH ARE PURE WALKS OVER THE SAME AST" IS RETRACTED: it was true of the secret pass and
        // false of ProviderPipeline.Compile, which reads the filesystem (Directory.
        // GetCurrentDirectory, then EnvironmentSecurityValidator's existence checks) and calls
        // ReflectBind. THE EXPOSURE THAT ACCEPTANCE NAMED IS NOW CLOSED, and the history is kept
        // because the trade was recorded here: this door used to reach the secret pass first on the
        // `run` path and return before Compile ran at all — MEASURED at baseline, where two
        // fixtures printed only the step-secret fault — so merging the doors gave `run` the
        // unguarded-Bind exposure `--parallel` always had, and a throwing Bind aborted with a stack
        // trace instead of a verdict. Issue #413 closed both halves: BindAllSteps now returns a
        // throwing Bind as a ValidationFailure (so it arrives at the pipeline-failure branch like
        // any other compile fault, on every path), and RunCommand.ExecuteAsync grew the top-level
        // catch whose absence was the other half. Nothing about the merged door itself changed —
        // reporting a document's faults as a property of the document, rather than of which pass ran
        // first, is still what it is for.
        //
        // THE DOOR IS NOW ONE HELPER RATHER THAN THIS SITE'S OWN THREE LINES (#412). It was written
        // out here and at the shared-topology seam, and the `--watch` seam kept the RETIRED order in
        // a third spelling — the exact way a duplicated door decays. Everything about the door's
        // behaviour, including the discarded `fromSecurityDeclaration` and the thrown-away CSX emit
        // a step-secret-only document pays for, is recorded on RunPreTopologyAuthoringDoor. What is
        // local to THIS site is only what it does with the answer, below.
        var (pipelineResult, authoringFault) =
            RunPreTopologyAuthoringDoor(ast, registry, seedBaseDirectory);
        if (authoringFault is not null)
        {
            var nowAuthoring = DateTimeOffset.UtcNow;
            buffer.Add(EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = runId,
                Timestamp = nowAuthoring,
                ScenarioId = scenarioName,
            }));
            buffer.Add(StepEventBuilder.ScenarioCompletedLine(
                runId,
                nowAuthoring,
                scenarioName,
                Verdict.Inconclusive,
                new VerdictCounts { Inconclusive = 1 },
                runSecretLedger,
                runPathLedger,
                authoringFault));
            // Issue #266, Item 4: both halves can echo untrusted YAML content (a step id, a field
            // value, a secret reference) back verbatim — sanitise before writing.
            await output.WriteLineAsync(
                DisplaySanitiser.SanitiseForDisplay(authoringFault))
                .ConfigureAwait(false);
            livePump?.PostRange(buffer);

            // EDGE-010(a) and every other pre-topology refusal alike: an authoring fault before any
            // container started. NO CLASSIFICATION IS ADJUDICATED HERE. `IsSecurityPreflight` is not
            // consulted, and neither is which field the fault sat in — under the derived rule a
            // secured document refused at this door is unconfirmable whatever the refusal was
            // about, and a document declaring no security raises nothing whatever it was about. The
            // narrower questions these two doors used to ask are what let an unresolvable
            // `script.csharp file:`, and a step-level secret fault, in a secured suite exit 0.
            return new ScenarioCoreResult(Verdict.Inconclusive, buffer)
            {
                Assurance = SecurityAssurance.None
                    .Declaring(declaredTargets)
                    .Refusing(SecurityAbortKind.AuthoringFault),
            };
        }

        // ── Step 6: Start Aspire topology ─────────────────────────────────────
        // REQ-005's probe runs inside StartAsync (after the health gate, before the seed) and
        // needs the SAME client security configuration a step would use, so it is built here and
        // handed in. Disposed in this method's own finally rather than inside StartAsync: the
        // accessor owns the X509Certificate2 instances it loads, and the topology does not own the
        // accessor's lifetime. A scenario declaring no `security` block gets the shared Null
        // accessor, which allocates nothing and has nothing to dispose.
        // seedBaseDirectory is this path's only base directory — RunScenarioOwningTopologyAsync
        // already hands the same value to ProviderPipeline.Compile above, so the probe resolves
        // each declared path to exactly the file EnvironmentSecurityValidator checked.
        //
        // The probe's own secret scope (REQ-009): a declared `clientKeyPassword` resolves LAZILY,
        // inside the certificate load, which on this path happens inside StartAsync AFTER the
        // health gate — so §17's resolve-at-execution-time rule holds unchanged and no earlier
        // resolution pass is introduced.
        //
        // REQ-010's answer, and it is the LEDGER that is shared, not the scope: this scope is
        // still constructed BEFORE the per-scenario SecretAccessor built in
        // RunScenarioAgainstTopologyAsync, but both are handed `runSecretLedger`, so a passphrase
        // resolved for the probe is scrubbable from text emitted on the step path and vice versa.
        // Sharing the SCOPE instead was rejected: a scope owns the Vault resolver's HttpClient and
        // is disposed in the `finally` below, the moment the topology is up — a scenario running
        // for minutes afterwards must not depend on it.
        //
        // THE SCOPE IS BUILT OUTSIDE THE `try` AND THE ACCESSOR INSIDE IT, and the split is
        // deliberate. `Build` can throw (Path.GetFullPath on a malformed declared path), and
        // constructed before the `try` its failure skipped the `finally` below — leaking this
        // scope's resolvers, the Vault one's HttpClient included. The scope's own construction
        // allocates two objects and touches nothing, so a failure there leaves nothing to
        // dispose. This is the rule the per-scenario site already states in full at its own
        // construction (see "Constructed INSIDE the try, not before it").
        //
        // `using var` is NOT the alternative: it would hold the resolvers for the whole method
        // rather than releasing them once the topology is up, which the `finally` below
        // deliberately does.
        var probeSecrets = CreateSecretAccessorScope(runSecretLedger);
        ISecurityConfigurationAccessor probeSecurity = NullSecurityConfigurationAccessor.Instance;
        SuiteTopology suite;
        try
        {
            probeSecurity = SecurityConfigurationAccessor.Build(
                ast, seedBaseDirectory, probeSecrets.Accessor, runPathLedger);

            // ONE ARGUMENT LIST (#364). `TopologyRequest.ForScenario` derives BOTH protocol target
            // sets from this one `ast` — REQ-005/REQ-011's Kafka-speaking set, so a broker declared
            // as a SERVICE earns the authenticated round trip a `kafka` dependency does, and #348's
            // superset that decides whether an endpoint-less `project:`-form service is a refused
            // authoring fault or an untargeted worker. Written out at three call sites, those two
            // were dropped once each; there is now nothing at a call site to drop.
            //
            // The request reads `ast.Environment`, where this site read `doc.Environment`. Same
            // reference, not merely an equal value: AstBuilder.Build carries `doc.Environment`
            // straight onto the ScenarioAst it returns (AstBuilder.cs — `new ScenarioAst(doc.
            // Metadata, doc.Environment, …)`), and this `ast` was built from this `doc`.
            suite = await TopologyRequest
                .ForScenario(ast, appHostAssemblyName, seedBaseDirectory)
                .StartAsync(probeSecurity, runPathLedger, cancellationToken)
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
            // Scope note: this catches EnvironmentMapper.Map's OWN eager, PRE-Configure
            // validation (SuiteTopology.StartAsync's Step 1, called before HeadlessTopology.
            // StartAsync/DCP is ever reached — see that method's Step 1 comment: "Map is pure
            // ... let them propagate as-is"), PLUS the authoring faults that can only be
            // discovered inside the Configure closure and opt back onto this side deliberately:
            // TopologyAuthoringException, an ArgumentException subclass that SuiteTopology.Step 2
            // re-throws unwrapped. The PROPERTY, which is stable, is that each such fault is one
            // Aspire alone can determine and only after it has built the resource — a project's
            // launch-profile endpoints are the standing example. For the current set, grep
            // `throw new TopologyAuthoringException`; enumerating them here goes stale, which is
            // how this sentence went wrong once already. Both are the same class of problem as
            // the schema-invalid / parse-AST / pipeline-compile / secret-reference failures
            // handled above: an author edits the suite and it goes away.
            //
            // Map()'s Configure CLOSURE also carries a few defensive throws of its own, reachable
            // only by defect (e.g. ResolveDependencyEnvAccess's internal-error fallback,
            // RequirePasswordParameter, BuildEnvExpression's unresolved-${conn:}
            // ArgumentException, and ResolveDependencyContainer's InvalidOperationException when
            // a dependency type registers no container of its own name) — those run LATER, and
            // SuiteTopology.cs Step 2 wraps every exception except TopologyAuthoringException as
            // OrchestrationException before it ever reaches this method; they are therefore
            // unreachable by construction here (ValidateEnvValue's eager checks, the
            // dependency-env census gate, and the CURRENT Aspire.Hosting.Redis/.Nats behaviour of
            // always provisioning a password parameter, already prevent them from firing in
            // practice) and would correctly surface as EnvironmentError via the
            // OrchestrationException catch below, not this one — a genuine, if never-yet-observed,
            // infrastructure/engine fault.
            var now = DateTimeOffset.UtcNow;
            buffer.Add(EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = runId,
                Timestamp = now,
                ScenarioId = scenarioName,
            }));
            // Scrubbed AT CONSTRUCTION, not only at the stamp: this catch sits INSIDE the try
            // that builds the probe's security accessor and calls StartAsync, so a
            // `clientKeyPassword` may already have resolved into `runSecretLedger` by the time an
            // ArgumentException escapes. The stamping chokepoint below scrubs the record; this
            // scrub is what covers the TERMINAL write, and building the text once means the two
            // channels can never carry different redactions. DisplaySanitiser alone would not do
            // it — an ordinary printable passphrase passes through it unchanged.
            //
            // BOTH NETS, in ScrubDiagnostic's order (issue #375). The event counterpart below is
            // handed `runPathLedger` as well as `runSecretLedger`; a terminal write that applied
            // only one of them would print a resolved certificate path the archived record does
            // not contain, which is the divergence building the text once exists to prevent.
            var environmentFault = runPathLedger.Scrub(
                runSecretLedger.Scrub($"Environment configuration error: {aex.Message}")!)!;
            buffer.Add(StepEventBuilder.ScenarioCompletedLine(
                runId,
                now,
                scenarioName,
                Verdict.Inconclusive,
                new VerdictCounts { Inconclusive = 1 },
                runSecretLedger,
                runPathLedger,
                environmentFault));
            // Issue #266, Item 4: aex.Message can echo untrusted YAML content (an
            // environment.services/dependencies reference) back verbatim — sanitise.
            await output.WriteLineAsync(
                DisplaySanitiser.SanitiseForDisplay(environmentFault))
                .ConfigureAwait(false);
            livePump?.PostRange(buffer);

            // THE BOUNDARY IS "NO CONTAINER STARTED", NOT "BEFORE THE StartAsync CALL". Map's
            // eager validation runs as StartAsync's Step 1, ahead of DCP, so a `${conn:typo}` or
            // an unknown dependency starts nothing — and this return, which had no way to raise
            // the old flag at all, was the least defensible of the five that could not.
            return new ScenarioCoreResult(Verdict.Inconclusive, buffer)
            {
                Assurance = SecurityAssurance.None
                    .Declaring(declaredTargets)
                    .Refusing(SecurityAbortKind.AuthoringFault),
            };
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
            // Scrubbed (REQ-010): this is the PROBE's own failure path — a `clientKeyPassword`
            // that resolved and then failed the load arrives here with the resolved value folded
            // into oex.Info.Detail (SecuredEndpointProbe folds SecurityMaterialException.Message
            // into the probe-failure text). `runSecretLedger` is the ledger the probe recorded
            // into, so this is where that value is caught.
            buffer.Add(EnvironmentErrorLine(
                runSecretLedger, runPathLedger, oex.Info, runId, now));

            // #372 stamped here too, and it is NOT a duplicate of the environment-error line
            // above: that record is what the HTML renderer reads for its own environment-error
            // section, while JUnit's `message` attribute and the events stream's scenario-level
            // cause come from THIS one. Before this, the `--parallel` spelling of a topology that
            // would not come up wrote a scenario record with no cause while the suite path (since
            // #407) wrote one — the same document, two artefacts. Scrubbed by the chokepoint
            // against the ledger the probe recorded into.
            buffer.Add(StepEventBuilder.ScenarioCompletedLine(
                runId,
                now,
                scenarioName,
                Verdict.EnvironmentError,
                new VerdictCounts { EnvError = 1 },
                runSecretLedger,
                runPathLedger,
                oex.Message));
            livePump?.PostRange(buffer);

            // REQ-018: exactly ONE cause of an Environment error exits non-zero without
            // --fail-on-env-error, and THIS DISCRIMINATOR IS UNTOUCHED by the derivation. The
            // classified kind on the exception decides — not the verdict and not the message — so
            // an unhealthy container, an unpullable image and an unrelated seed failure all reach
            // this same catch, record TopologyUnavailable, and still exit 0 by default. That is
            // #390, and it is deliberately still open (see SecurityAbortKind.TopologyUnavailable).
            return new ScenarioCoreResult(Verdict.EnvironmentError, buffer)
            {
                Assurance = SecurityAssurance.None
                    .Declaring(declaredTargets)
                    .Refusing(
                        oex.Info.Kind == OrchestrationErrorKind.SecurityConfirmation
                            ? SecurityAbortKind.ProbeUnconfirmed
                            : SecurityAbortKind.TopologyUnavailable),
            };
        }
        finally
        {
            // The probe is this accessor's only consumer and it has finished, on every path
            // including the two that return from a catch above. Disposing here rather than at the
            // end of the method releases each loaded client certificate's key-store entry as soon
            // as the topology is up, instead of holding it for the whole run alongside the
            // separate per-scenario accessor built below.
            (probeSecurity as IDisposable)?.Dispose();

            // The scope outlives nothing beyond that accessor — the passphrase, if any, is already
            // inside the loaded certificate — so its resolvers are released here too.
            probeSecrets.Dispose();
        }

        await using (suite.ConfigureAwait(false))
        {
            // REQ-005's declared-versus-observed report, on THIS path too — it is the core the
            // `--parallel` runner drives, and without it an operator running in parallel saw
            // enforcement fire but never saw what was confirmed, so a TransportConfirmed run read
            // identically to an AuthenticatedRoundTrip one. That indistinguishability is the exact
            // outcome REQ-005 gives named levels instead of a boolean to prevent. Written to this
            // scenario's own writer, which ParallelSuiteRunner flushes in declaration order ahead
            // of the rendered report — the same position RunSuiteAsync prints it in.
            foreach (var confirmation in suite.SecurityConfirmations)
            {
                await output.WriteLineAsync(
                        DisplaySanitiser.SanitiseForDisplay(confirmation.ToString()))
                    .ConfigureAwait(false);
            }

            // #348 (security review): the transport downgrade a project-form service declaring
            // both schemes incurs. Printed beside the security confirmations because it answers
            // the same question — what did this run actually talk to, and how. Empty for almost
            // every suite, so ordinary output is unchanged.
            foreach (var notice in suite.EndpointSelectionNotices)
            {
                await output.WriteLineAsync(DisplaySanitiser.SanitiseForDisplay(notice.ToString()))
                    .ConfigureAwait(false);
            }

            // The other endpoint advisory, and a DIFFERENT fact: the address staged for the
            // service is an https listener the engine configures no client trust material for —
            // whoever selected it. Printed here rather than through a bespoke Console.WriteLine so
            // it goes through the same sanitiser every other author-influenced string does — the
            // notice carries a service name the author supplied.
            foreach (var notice in suite.EndpointTrustNotices)
            {
                await output.WriteLineAsync(DisplaySanitiser.SanitiseForDisplay(notice.ToString()))
                    .ConfigureAwait(false);
            }

            // #450 / #453: the same two advisories onto the §14 event stream, so a CI job reading
            // --events sees what the terminal above just said. Until this, both were terminal-only
            // — which matters most for the trust notice, because the handshake failure it warns
            // about lands as an EnvironmentError and exits 0 by default, so the artefacts showed a
            // green run with no explanation in them.
            //
            // SCOPED TO --events / --events-stream, and that scope is the whole of what this
            // closes. HtmlRenderer and JunitXmlRenderer both take their `default: break` arm on an
            // unrecognised type, so a run whose only artefacts are --junit and --html still shows
            // that green run with no explanation in it. Rendering the record there is out of scope
            // this pass (decision 2: the stream is the contract).
            //
            // NOT scrubbed and NOT sanitised, deliberately — the WHICH-members analysis behind
            // both decisions is written out on TransportNoticeEvents, which is the one producer.
            //
            // `replayed: false` ⇒ the field is left OFF the wire (TransportNoticeEvents.ToLines
            // maps it to null, not to false); only the --watch replay sets it.
            //
            // POSTED AS WELL AS BUFFERED, and both halves are load-bearing here. The buffer is
            // this scenario's reconstruction for the end-of-run --events archive; the pump is the
            // live --events-stream file. This method is the core `--parallel` fans out into, and
            // ParallelSuiteRunner deliberately does NOT re-post the returned buffer (it would
            // duplicate every line), so a line added to the buffer alone would never reach the
            // live stream. `livePump` is null whenever no --events-stream destination was asked
            // for; the null-conditional is what makes that a no-op rather than a crash.
            //
            // ONE topology, so ONE runId: this method builds a topology for a single scenario, so
            // the scenario's own runId identifies the build unambiguously and the advisory
            // correlates with that scenario's other events.
            var transportNoticeLines = TransportNoticeEvents.ToLines(
                suite.EndpointSelectionNotices,
                suite.EndpointTrustNotices,
                runId,
                DateTimeOffset.UtcNow,
                replayed: false);
            buffer.AddRange(transportNoticeLines);
            livePump?.PostRange(transportNoticeLines);

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
                livePump: livePump,
                sharedLedger: runSecretLedger,
                sharedPathLedger: runPathLedger).ConfigureAwait(false);

            // The topology came up and every declared block was confirmed — reaching here at all
            // means that (a failure aborts StartAsync). What was confirmed is carried rather than
            // discarded: a boolean could not tell an authenticated round trip from a transport-only
            // confirmation, which is the whole subject of REQ-005.
            return new ScenarioCoreResult(verdict, buffer)
            {
                Assurance = SecurityAssurance.None
                    .Declaring(declaredTargets)
                    .Confirming(suite.SecurityConfirmations),
            };
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
    /// working directory when <see langword="null"/>. This stays rooted wherever the CALLER
    /// rooted it — in practice the first DISCOVERED scenario's directory — regardless of
    /// <paramref name="scenarioBaseDirectories"/> (issue #268): the shared topology is built ONCE
    /// and seeded ONCE against ONE base directory, so <c>environment.seed</c> is genuinely
    /// single-rooted in this sequential, shared-topology path, unlike a step's own <c>file:</c>
    /// reference (see <paramref name="scenarioBaseDirectories"/>).
    /// <para>
    /// <strong>The seed root and the topology's ENVIRONMENT can come from different scenarios, and
    /// that combination is REFUSED (issue #451).</strong> The environment is taken from the first
    /// scenario that passes SCHEMA validation, which is <c>scenarios[0]</c> unless a
    /// schema-rejected document sits ahead of it; this parameter is the caller's own choice and,
    /// for <c>RunCommand</c>, is <c>scenarios[0]</c>'s directory unconditionally. A MULTI-DIRECTORY
    /// suite whose first document is schema-rejected would therefore seed against that document's
    /// folder while building from a later one's environment, and a relative
    /// <c>environment.seed</c> path then names a different file in each.
    /// <para>
    /// <strong>Refusing is the only safe answer because the RESOLVING case is the harmful one.</strong>
    /// Where the named fixture is absent from the seed root the run fails loudly and names the
    /// file, which is survivable; where a SAME-NAMED fixture exists in both folders — the ordinary
    /// shape when two scenario directories were copied from one another — the suite silently seeds
    /// from the rejected document's copy, runs green, and asserts against the wrong data. No
    /// diagnostic distinguishes that from a correct run. So the suite is refused before the
    /// topology is built, carrying <see cref="Verdict.Inconclusive"/> — an authoring fault the
    /// author fixes by repairing the rejected document or co-locating the files.
    /// </para>
    /// <para>
    /// <strong>Four conditions, and the fourth is what stops the guard over-refusing.</strong> The
    /// baseline must have moved (a rejected document sits ahead of it), the environment must
    /// declare a seed, the two directories must differ, AND the two documents' <c>seed</c> SECTIONS
    /// must differ. Multi-directory suites are legal (#268), so without that last condition an
    /// ordinary suite — two folders, a byte-identical environment, a seed, and a steps-level typo
    /// in the first file — would be refused although the baseline supplies character-for-character
    /// the paths the first document supplied, against the same root as before: nothing has moved,
    /// and refusing there is the very over-refusal issue #451 exists to remove. The comparison is
    /// on the <c>seed</c> section rather than the whole environment because the hazard is about
    /// WHICH FILE a relative path names, which the seed strings alone decide.
    /// </para>
    /// <para>
    /// So a single-directory suite cannot trip it, nor can one whose first document validates, nor
    /// one whose environment declares no seed, nor one whose seed section is unchanged. A SECURED
    /// suite is additionally covered further down by
    /// <c>TryFindSecurityBaseDirectoryDivergence</c>, which refuses any secured suite whose
    /// scenarios do not share one root at all.
    /// </para>
    /// </para>
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
    /// <param name="unbuiltDocuments">
    /// The documents the CALLER refused before this method saw them — documents that PARSED but
    /// whose AST could not be built, so they are absent from <paramref name="scenarios"/> entirely
    /// (issue #411). <see langword="null"/> or empty (the default) means the caller discovered no
    /// such document, which is every caller that does no discovery of its own.
    /// <para>
    /// <strong>Documents, not names and not <c>SecuredTarget</c>s.</strong> This method already
    /// holds every scenario's <c>EnvironmentSpec</c> and every scenario's raw text (through
    /// <paramref name="scenarios"/> and <paramref name="yamlTexts"/>), so the shape crosses no
    /// boundary it does not already cross — and it keeps both the walk of
    /// <c>SecuredTargets.Enumerate</c> and the schema door's own classification on THIS side,
    /// rather than letting a caller decide any part of the security question for itself.
    /// </para>
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
    /// <strong>Shared-environment assumption:</strong> all scenarios in the suite that
    /// pass schema validation are expected to declare the same <c>environment</c> block.
    /// The topology is built from the FIRST such scenario's <c>Environment</c> — which is
    /// <paramref name="scenarios"/>[0] unless a schema-rejected document sits ahead of it
    /// (issue #451). If a later schema-valid scenario's environment differs (detected by
    /// structural equality on the serialised JSON), the suite short-circuits with
    /// <see cref="Verdict.EnvironmentError"/> — running heterogeneous topologies in one suite
    /// would produce unpredictable results. A schema-rejected scenario is not compared: it
    /// carries its own located verdict and executes nothing.
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
        IReadOnlyList<UnbuiltDocument>? unbuiltDocuments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(scenarioNames);
        ArgumentNullException.ThrowIfNull(yamlTexts);
        ArgumentNullException.ThrowIfNull(providerAssemblies);
        ArgumentNullException.ThrowIfNull(output);

        // THIS ARM RETURNS BEFORE THE ASSURANCE WALK BELOW, SO IT ANSWERS THE SAME QUESTION HERE
        // (Copilot, PR #416). It used to DISCARD `unbuiltDocuments` and return a bare
        // `SuiteResult(Pass, …)` whose `Assurance` therefore defaulted to
        // `SecurityAssurance.None` — measured: a secured unbuilt document on which
        // `UnbuiltDocument.Assure` reports `Declared=[legacy] Refusal=AuthoringFault
        // Unconfirmed=True` came back from this method reading `Unconfirmed=False`, which
        // `ExitCodes.FromVerdict` maps to 0. A silently dropped parameter on a PUBLIC method is a
        // false negative for any caller that is not the CLI.
        //
        // ANSWERING IS PREFERRED OVER THROWING, and the shape it answers with is not invented here:
        // the call below reaches the SAME per-document fold — `UnbuiltDocument.AssureAll`, one whole
        // assurance per document folded by `SecurityAssurance.Worse` — that the main path applies
        // further down, with the scenarios' own (empty) contribution removed. It is NOT a
        // union-then-refusal pair: that is the shape declaration-confirmation-matching REQ-001
        // removed from both, because it let one document's declaration meet another's refusal.
        // `Verdict.Pass` is unchanged and is
        // the consistent answer rather than a concession — an unbuilt document contributes to
        // `Assurance` and to nothing else on the non-empty path either (it is absent from
        // `ScenarioVerdicts` by construction), so with no scenario verdicts to aggregate the
        // identity stands. Rejecting the input instead would refuse a perfectly meaningful call.
        //
        // UNREACHABLE FROM THE CLI, AND STILL WORTH ANSWERING: `RunCommand` builds
        // `unbuiltDocuments` INSIDE its own `if (parsed.Count > 0)` block and calls both runners
        // from there, so it cannot reach this arm with a non-empty list; issue #278's
        // all-parse-failure rule owns the no-scenario case and exits 4 ahead of both runners. That
        // rule is untouched — nothing here decides an exit code, and no second answer to #278's
        // question is added: this fills evidence, exactly as every door below does.
        if (scenarios.Count == 0)
        {
            return new SuiteResult(Verdict.Pass, Array.Empty<(string, Verdict)>())
            {
                Assurance = AssureUnbuiltDocumentsAlone(unbuiltDocuments, providerAssemblies),
            };
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

        // ── The suite's ONE security assurance (security-assurance-derivation, REQ-002) ───────
        //
        // `Declared` is walked ONCE, here, from the canonical SecuredTargets.Enumerate — before
        // any door runs and from a PARAMETER, so no early return below can skip it.
        //
        // THE WALK IS OVER EVERY SCENARIO, NOT scenarios[0], and the difference is a measured exit-0
        // false negative rather than tidiness. Every scenario in a shared-topology suite is required
        // to declare a byte-identical `environment` block — but the gate that enforces that is the
        // one door BELOW that reads this value, so at that door the requirement is precisely what
        // has not been established. Deriving from scenarios[0] there meant a suite whose SECOND file
        // carried the `security` block reached the divergence gate with an empty declaration and
        // exited 0, while the same two files with the block in the other one exited 3: a rename
        // flipped a CI build's colour. Everywhere else this value is read the environments are
        // already byte-identical, so the union equals scenarios[0]'s walk and costs nothing —
        // measured as the blast radius of the change. IDENTITIES are deduplicated by `Declaring`
        // (issue #415 retyped the list from names), so the N byte-identical copies a conforming
        // suite contributes collapse to one set — and two same-named declarations asserting
        // DIFFERENT things now survive as two entries rather than collapsing into one, which is
        // the point: the probe can confirm one of them and not the other.
        //
        // The doors below record only WHICH door refused. Nothing about the exit code is decided
        // at any of them: that is SecurityAssurance.Unconfirmed's single job, and it is why this
        // replaced a bool that nine door-local sites each set for themselves — and, because those
        // doors are mutually exclusive early returns, never all set.
        // THE WALK COVERS SCENARIOS AND ONLY SCENARIOS. Documents that never became scenarios
        // (issue #411) declare exactly as loudly, but they do NOT belong in this union — they are
        // folded in WHOLE, below, as their own assurances.
        var assurance = SecurityAssurance.None.Declaring(
            scenarios.SelectMany(s => SecuredTargets.Enumerate(s.Environment)).ToArray());

        // ── What documents that never became scenarios contribute (issue #415) ────────────────
        //
        // ONE WHOLE ASSURANCE PER DOCUMENT, FOLDED BY `SecurityAssurance.Worse` — the shape
        // `ParallelSuiteRunner` always used, and the shape this path did NOT. This path used to
        // concatenate each unbuilt document's declaration into the union above and take only its
        // `Refusal`, which put an unbuilt document's declaration into a `Declared` that a SIBLING's
        // probe confirmation could satisfy. Measured on the built CLI: a broken secured file among
        // siblings that come up and confirm the same target exited 0 under `run` and non-zero under
        // `run --parallel N` — a flag flipping the answer.
        //
        // WHY A UNION WAS DEFENSIBLE FOR SCENARIOS AND IS NOT FOR THESE. This path builds ONE
        // shared topology from ONE environment block, so a declaration REQ-005's probe confirmed on
        // that topology is confirmed for every scenario in the suite — and the shared-`environment`
        // divergence guard below is what makes "one environment block" true of the scenarios, for
        // the ones that PASS SCHEMA VALIDATION. Issue #451 added that qualification: the guard now
        // runs below the schema pass and skips a scenario the schema rejected, so such a scenario's
        // declaration reaches this walk without having been held against the environment that
        // started. THAT GAP IS CLOSED BELOW rather than tolerated here — see
        // `rejectedDivergentAssurance`, which folds such a scenario whole, with `Confirmed` empty,
        // whenever its serialised environment differs from the baseline's. Leaving it to the digest
        // alone would NOT have been enough: `SecuredTargets.DigestOf` hashes the `endpoint:`
        // selector's text and not its resolution, so a rejected scenario declaring the same block
        // against a different `httpPort:` shares the baseline's identity exactly. An
        // unbuilt document is by construction absent from `scenarios`, so that loop never compares
        // its environment with anything: it bypasses the guard entirely. Nothing downstream of such
        // a document ran, and no guard ever proved its environment is the one that started, so its
        // declaration is never confirmed. That is the fail-closed direction, taken deliberately:
        // its worst case is a suite containing a broken secured file exiting non-zero, against the
        // alternative's green CI on an `mtls` assertion nothing exercised.
        //
        // THE FOLD IS AT THE END, NOT HERE, and that is forced rather than stylistic. `Worse`
        // selects a WHOLE assurance, so folding at this line would discard either the scenarios'
        // assurance in its final state (this `Declared`, plus the `Refusal` each door below stamps
        // on with `Refusing` — which sets `Refusal` and leaves `Declared` alone — plus, on the
        // success path, the probe's `Confirming`) or the documents' evidence. The value is therefore
        // computed once, now, and applied at each site that hands back a `SuiteResult` — the same
        // arrangement `ParallelSuiteRunner` reaches by folding into `RenderAndAggregate`.
        //
        // WHAT a document contributes stays `UnbuiltDocument.Assure`'s single job and the FOLD stays
        // `UnbuiltDocument.AssureAll`'s, so this path and `ParallelSuiteRunner` cannot answer
        // differently for the same document. Two spellings of one security rule is exactly the
        // drift `SecurityAssurance` exists to remove.
        //
        // NOTHING IS CONTRIBUTED BY A DOCUMENT THAT DECLARED NOTHING — and what that guard buys is
        // NOT issue #390's fence, which is what this note used to claim (T2 review, m3).
        //
        // THE RETIRED CLAIM, and why it is retired. Under the UNION this fold replaced, `Assure`
        // returning `Declaring([]).Refusing(AuthoringFault)` unconditionally would have stamped an
        // unsecured document's refusal onto a `Declared` holding a SIBLING's declaration, and that
        // pair raises — overriding the fence. Under a whole-value fold the pairing is structurally
        // impossible: the value stays whole, `Unconfirmed` is `AuthoringFault ∧
        // SomeDeclaredTargetWentUnconfirmed`, and an empty `Declared` has no unconfirmed member, so
        // that value does not raise. `Worse` can select it only when the other side does not raise
        // either, so the suite's `Unconfirmed` stays false and the run still exits 0. `--parallel`
        // has always used this fold and has always neutralised the effect.
        //
        // WHAT THE GUARD DOES STILL DO, measured by removing it and rebuilding: it keeps
        // `SecurityAssurance.None` the fold's IDENTITY ELEMENT rather than a refusal looking for a
        // declaration, and it therefore governs which refusal and which declaration the suite
        // REPORTS. Unguarded, `AuthoringFault` outranks `TopologyUnavailable` in `Worse`, so the
        // unsecured document's empty-declaration value wins the fold outright and the suite's own
        // evidence is displaced: `RunSuiteAsync_UnsecuredUnbuiltDocument_LeavesAFailedTopologyExitingZero`
        // fails with `Assert.Contains() … Collection: [] Not found: "api"` (the SCENARIO's
        // declaration gone), and `RunSuiteAsync_NoScenariosBesideAnUnsecuredUnbuiltDocument_ContributesNothing`
        // fails with `Expected: null Actual: AuthoringFault`. NO `Unconfirmed` assertion moved in
        // either run — which is the whole of the correction above, stated as a measurement.
        //
        // Both directions of the contribution itself are pinned end to end, without a container, by
        // `RunSuiteAsyncTests.RunSuiteAsync_UnsecuredUnbuiltDocument_*` and
        // `…_SecuredUnbuiltDocument_*`, which use a pinned host port the test itself holds to fail
        // the topology.
        var unbuiltAssurance = UnbuiltDocument.AssureAll(unbuiltDocuments, registry);

        // ── Per-scenario compilation (pre-topology), in TWO passes ─────────────
        // Validate + compile each scenario's YAML before we pay the topology build cost.
        //
        // TWO PASSES WITH THE SHARED-`environment` DIVERGENCE GUARD BETWEEN THEM (issue #451),
        // where there used to be one loop with the guard entirely ABOVE it. Schema validation
        // runs for EVERY scenario first; the guard then compares only the documents that
        // individually validate; provider-pipeline compilation runs after it, in the same order
        // relative to the guard as before.
        //
        // WHY THE ORDER MOVED. The guard compares the SERIALISED `environment` block, and #353
        // gave `SecuritySpec` an `Extra` bucket, so an unknown key inside a `security:` block —
        // a plain authoring typo, which the schema rejects with a located diagnostic naming the
        // key — now SURVIVES into the AST and serialises. Two scenarios differing only by that
        // typo therefore diverged, and the suite took the divergence door: a suite-level
        // `EnvironmentError` about a topology nobody could point at, in place of the located
        // authoring error, with the sibling scenario refused along with it. §12.1 is explicit
        // that reporting an authoring fault as an infrastructure fault is the one direction the
        // taxonomy must not bend — the same principle that made #348 introduce
        // `TopologyAuthoringException` rather than let a `Configure`-time throw become an
        // `EnvironmentError`.
        //
        // WHAT DID NOT MOVE, and it is the constraint the guard exists for: it still runs BEFORE
        // the topology is built, so a genuinely divergent suite is still refused without starting
        // a container. Only its INPUT narrowed — from every scenario to the schema-valid ones.
        // Compilation still runs AFTER it, so a pipeline or secret-reference fault still does not
        // pre-empt the divergence diagnostic.
        var compilations = new List<(
            string ScenarioName,
            ScenarioAst Ast,
            PipelineResult? Pipeline,
            Verdict? EarlyVerdict,
            string? EarlyMessage,
            string? ScenarioBaseDirectory)>(scenarios.Count);

        // Pass A's answer, per scenario, in declaration order. Read by the divergence guard (to
        // decide what it may compare) and by Pass B (to decide what it may compile). Deliberately
        // NOT re-derived from `compilations[i].EarlyVerdict`: after Pass B that flag also covers
        // pipeline and secret-reference faults, and the guard's rule is specifically "documents
        // that pass the SCHEMA", which is the one thing it can require of a document it compares.
        var schemaValid = new bool[scenarios.Count];

        // The abort kind Pass A recorded for each scenario it REJECTED, retained per scenario rather
        // than only accumulated into `assurance`. The whole-value fold below needs the kind that
        // belongs to ONE scenario, and `Refusing` deliberately keeps only the highest-precedence
        // kind across the suite — so reading it back off the accumulator would attribute a
        // sibling's refusal. Retained rather than recomputed because `RejectsASecurityDeclaration`
        // needs the validation errors, and re-running `DocumentValidator.Validate` to get them back
        // costs a full schema recomposition per scenario (~24 ms — see `UnbuiltDocument.Assure`'s
        // own measurement of the same call).
        var schemaRefusalKinds = new SecurityAbortKind?[scenarios.Count];

        for (int i = 0; i < scenarios.Count; i++)
        {
            var name = scenarioNames[i];
            var yaml = yamlTexts[i];
            var ast = scenarios[i];

            // Issue #268: each scenario compiles against its OWN directory — falling back to
            // the shared seed base directory only when the caller supplied no per-scenario
            // list (pre-#268 callers). This is the value script.csharp's `file:` resolves
            // against (via ProviderPipeline.Compile in Pass B) and the value the reproducibility
            // envelope later hashes this scenario's script-file digest against — it does NOT
            // affect the ONE shared topology's seed, which stays rooted at seedBaseDirectory.
            var scenarioBaseDirectory = scenarioBaseDirectories?[i] ?? seedBaseDirectory;

            // Schema-validate the YAML.
            var validationResult = DocumentValidator.Validate(yaml, registry);
            if (!validationResult.IsValid)
            {
                // THREE ADJACENT DOORS USED TO GIVE THREE DIFFERENT ANSWERS TO THE SAME QUESTION,
                // and this was the widest of them: it asked "does this document declare security,
                // or is the error inside the block". Its neighbour below asked "is the fault in a
                // declared `security` block". The third asked "was it the security preflight". No
                // principle distinguished them — only which door was patched when.
                //
                // All three now record the SAME thing, because it is the same fact: an authoring
                // fault refused this scenario before any container started. Whether that raises is
                // decided once, from `Declared`, by SecurityAssurance.Unconfirmed.
                //
                // The located-in-the-block distinction survives as EVIDENCE, not as a decision: a
                // schema error at or inside a `security` node is a refusal OF the declaration, and
                // is the one shape `Declared` cannot see (`security: mtls` binds no SecuritySpec).
                var errorIsInsideTheSecurityBlock = RejectsASecurityDeclaration(validationResult.Errors);
                schemaRefusalKinds[i] = errorIsInsideTheSecurityBlock
                    ? SecurityAbortKind.SecurityDeclarationRejected
                    : SecurityAbortKind.AuthoringFault;
                assurance = assurance.Refusing(schemaRefusalKinds[i]!.Value);

                // The notice that used to be spliced onto this message has moved to the ONE site
                // that reads the assurance (RunCommand): the exit code must never be the only
                // evidence, and it is now non-zero at doors this branch cannot see.
                compilations.Add((name, ast, null, Verdict.Inconclusive,
                    string.Join("; ", validationResult.Errors.Select(e => e.Message)),
                    scenarioBaseDirectory));
                continue;
            }

            schemaValid[i] = true;
            compilations.Add((name, ast, null, null, null, scenarioBaseDirectory));
        }

        // ── Validate shared-environment assumption ─────────────────────────────
        // Every scenario that PASSED SCHEMA VALIDATION must share the environment declared by the
        // FIRST such scenario. If any of them diverges, return EnvironmentError for the whole suite.
        //
        // THE BASELINE IS THE FIRST SCHEMA-VALID SCENARIO, NOT `scenarios[0]`, and that is forced
        // rather than chosen. Comparing against a document the schema rejected would report the
        // divergence against an environment that will never be built — issue #451 one index along —
        // and the ONE shared topology is built from this SAME baseline below, precisely so the
        // environment every runnable scenario was proved to match is the environment that starts.
        // (Before this, the topology was built from `scenarios[0]` and the guard compared against
        // it, so the two agreed by construction; they still do, through one index.)
        //
        // A SCHEMA-INVALID DOCUMENT IS SKIPPED, NOT TRUSTED. It carries its own located verdict
        // from Pass A and executes nothing, so it stages nothing into the shared topology and has
        // no environment worth comparing — its `environment` block did not validate.
        var baselineIndex = Array.IndexOf(schemaValid, true);

        // ── What a SCHEMA-REJECTED scenario contributes when its environment is not the one that
        // will start (issue #451, security review) ────────────────────────────────────────────────
        //
        // COMPUTED HERE — the first line after the baseline is known, and BEFORE the divergence
        // guard below — rather than after it (peer-review MAJOR-1 + MINOR-3). The value is captured
        // by `WithUnbuiltDocuments`, which every door from the guard onward calls; computing it
        // later meant the closure read a mutable local that a door inserted in between would have
        // silently seen as `None`. Nothing between here and the topology build can now get the
        // wrong value, because there is no window in which a wrong value exists.
        //
        // WHY IT EXISTS. Moving the divergence guard below the schema pass narrowed what that guard
        // proves from "every scenario shares the environment that starts" to "every SCHEMA-VALID
        // scenario does". A rejected scenario still contributes its declaration to the canonical
        // walk above — deliberately, so a declaration is never silently dropped — and
        // `Unconfirmed`'s `AuthoringFault` disjunct is satisfied only when some DECLARED IDENTITY
        // went unconfirmed. `SecuredTargets.DigestOf` hashes the `endpoint:` selector's TEXT and
        // never its resolution, so a rejected scenario declaring a byte-identical `security:` block
        // on a service with a DIFFERENT `httpPort:` shares the baseline's identity — and the running
        // sibling's probe confirmation then satisfies a declaration the probe never tested: mTLS
        // asserted on a port nothing confirmed, exiting 0. Before the reorder the guard refused that
        // suite outright.
        //
        // THE GATE IS ENVIRONMENT EQUALITY, NOT SCHEMA VALIDITY, and that is what keeps #451's
        // improvement rather than trading it back. See `FoldRejectedDivergent`'s own remarks, and
        // `FoldRejectedDivergentTests`, which tables all four cells.
        //
        // WHAT IS PINNED AND WHAT IS NOT — stated so nobody reads the tests as covering more than
        // they do. The DECISION (which scenarios are folded, with which refusal) is pinned
        // Docker-free by `FoldRejectedDivergentTests`, whose four cells were each shown to redden
        // under a mutation of this method. The WIRING — the outer `Worse(…,
        // rejectedDivergentAssurance)` term below, which carries that value into the suite's answer
        // — is NOT observable without a container, and that is a property of the predicate rather
        // than a gap in the tests. MEASURED ON THE PRIOR TREE (#467, 2026-08-28, when the non-Docker
        // suite stood at 4347): deleting that term left the whole of it green. The count is
        // date-stamped rather than refreshed because the claim is a historical one and the tree has
        // since moved (4370 as of 2026-08-30) — and because it no longer needs re-running: the fold
        // row described below now pins the same term end to end, and a re-measurement of the
        // Docker-free half would only re-confirm that it cannot see this.
        // The reason it cannot is structural. At every pre-topology door `Confirmed` is
        // empty while `Declared` already holds the rejected scenario's own declaration (the
        // canonical walk covers every scenario), and some door has recorded a refusal — so the
        // union RAISES; `Refusing` keeps the highest-precedence kind across the whole suite, so the
        // union's precedence is never below this fold's; and `Worse` therefore returns the union
        // whenever both raise. The fold can only change the answer where the union does NOT raise,
        // which requires a probe to have CONFIRMED the declaration — exactly the shape issue #410
        // exists for, and exactly the shape it records as needing a Docker arm ("every Docker-free
        // row in the matrix is blind to this by construction"). Do not add a Docker-free row here
        // and call the wiring covered.
        //
        // THAT DOCKER ARM NOW EXISTS, and the paragraph above is kept because it is still the
        // reason the arm has to be a Docker one:
        // `KafkaSecurityConfirmationDrillDockerTests.RejectedDivergentSiblingBesideAConfirmedProbe_
        // RaisesThroughTheFold` builds the one suite where the union cannot raise — a schema-rejected
        // sibling whose `security` block is byte-identical to the baseline's (so the walk holds one
        // identity, which the probe confirmed) on an `environment` that differs elsewhere — and
        // asserts a non-zero exit. Its sibling rows cover the two neighbouring cells: the carve-out
        // (identical environment → 0, no notice) and a sibling whose DECLARATION also differs, where
        // the union raises on its own (the fold fires there too — both terms do — which is exactly
        // why that row cannot attribute anything to the wiring).
        //
        // MEASURED by mutating THIS line: with the term replaced by `SecurityAssurance.None` the
        // fold row drops to exit 0 and fails while the different-declaration row stays green in the
        // same run. That differential is what makes the fold row a measurement of the wiring rather
        // than one more suite that happens to redden.
        var rejectedDivergentAssurance =
            FoldRejectedDivergent(scenarios, schemaValid, schemaRefusalKinds, baselineIndex);

        // The suite's answer: the scenarios' own assurance folded against the unbuilt documents' and
        // against any schema-rejected scenario whose environment was never held against the
        // baseline's. Applied at EVERY site below that hands back a `SuiteResult`, because `Worse`
        // must see the scenarios' assurance in its final state — with its door recorded and, on the
        // success path, with the probe's confirmations attached.
        //
        // BOTH captured values are ASSIGNED BEFORE THIS DECLARATION, so this closure reads two
        // settled values rather than a local it races the rest of the method for.
        SecurityAssurance WithUnbuiltDocuments(SecurityAssurance scenarioAssurance) =>
            SecurityAssurance.Worse(
                SecurityAssurance.Worse(scenarioAssurance, unbuiltAssurance),
                rejectedDivergentAssurance);

        if (baselineIndex >= 0)
        {
            var baselineEnvJson = SerialiseEnvironment(scenarios[baselineIndex].Environment);
            for (int i = baselineIndex + 1; i < scenarios.Count; i++)
            {
                if (!schemaValid[i])
                {
                    continue;
                }

                var envJson = SerialiseEnvironment(scenarios[i].Environment);
                if (string.Equals(envJson, baselineEnvJson, StringComparison.Ordinal))
                {
                    continue;
                }

                // The message names BOTH scenarios rather than "the first scenario": the baseline
                // is no longer necessarily the first one declared, and an author reading "the
                // first scenario" would compare against the wrong file.
                var divergentEnvironment =
                    $"RunSuiteAsync: scenario '{scenarioNames[i]}' declares a different " +
                    $"environment block than scenario '{scenarioNames[baselineIndex]}'.  All " +
                    "scenarios in a suite must share one topology.  Suite aborted with " +
                    "EnvironmentError.";

                // Issue #266, Item 4: the scenario names are author-controlled (a scenario's
                // metadata.name or file-derived identity) — sanitise before writing. Printed HERE,
                // once for the suite, and the completion path is told so it prints no duplicate —
                // the same two-halves arrangement the two guards below use.
                await output.WriteLineAsync(
                    DisplaySanitiser.SanitiseForDisplay(divergentEnvironment))
                    .ConfigureAwait(false);

                // One of the five returns that could not raise the old flag at all. Scenarios
                // declaring different environment blocks is an authoring fault, and no container
                // has started, so a SECURED suite refused here is unconfirmable on exactly the
                // terms every other pre-topology refusal is. An unsecured one is untouched.
                //
                // RETURNED THROUGH CompleteWithoutTopologyAsync, NOT AS A BARE SuiteResult
                // (peer-review MAJOR-1, fix round ten). This was the THIRD instance of the defect
                // that branch already diagnosed and fixed at the two seams below — a bare return
                // skips the ScenarioStarted/Completed events, the --events-stream pump, the
                // terminal render and FileReportWriter.WriteFileReports. MEASURED on this shape
                // with --junit/--html/--events all requested: `junit exists = False, html exists =
                // False, events exists = False`, beside an exit of 3 for the secured spelling — a
                // red build with an empty results directory, which the same seam's own note two
                // guards below calls out as the worst combination of the two.
                //
                // IT NOW SHARES StampWhereUnjudged WITH ITS NEIGHBOURS (issue #451). It used to
                // synthesise the scenario list from the PARAMETERS, because it ran above the
                // compilation loop and there was nothing compiled to stamp; running below Pass A
                // there is, and a scenario the SCHEMA already refused keeps its own located
                // message instead of having the suite-level cause written over it. Every other
                // scenario is stamped with the divergence, exactly as the synthesised list did.
                // THE WRAP IS PINNED, AND THE PIN NEEDS UNSECURED SCENARIOS TO MEAN ANYTHING
                // (T2 review, MAJOR). Dropping `WithUnbuiltDocuments` at any of these doors is
                // silently exit 0 on a suite that contains a broken SECURED file — and a suite
                // whose own scenarios declare security cannot detect it, because their assurance
                // raises here regardless. The row that turns red is
                // `SharedEnvironmentDivergence` of `RunSuiteAsyncTests
                // .RunSuiteAsync_UnsecuredScenariosBesideASecuredUnbuiltDocument_RaisesAtEveryPreTopologyDoor`,
                // which reaches this door with unsecured scenarios beside a secured unbuilt
                // document; that theory's own remarks map every row to its door and name the one
                // door still unpinned (the success return at the bottom of this method, which
                // needs a container).
                return await CompleteWithoutTopologyAsync(
                        StampWhereUnjudged(
                            compilations, Verdict.EnvironmentError, divergentEnvironment),
                        WithUnbuiltDocuments(assurance.Refusing(SecurityAbortKind.AuthoringFault)),
                        output,
                        decorate,
                        diffLookup,
                        htmlReportPath,
                        junitReportPath,
                        eventsReportPath,
                        eventsStreamPath,
                        runSecretLedger: null,
                        runPathLedger: null,
                        alreadyPrintedMessage: divergentEnvironment)
                    .ConfigureAwait(false);
            }
        }

        // The scenario the ONE shared topology — and the REQ-005 probe's security configuration —
        // is built from: the same baseline every runnable scenario was just proved to match. The
        // fallback is unreachable in practice (no schema-valid scenario means every scenario
        // carries an early verdict, and the all-early guard below returns before the topology is
        // built) and exists so the index is total rather than conditional.
        var topologyIndex = baselineIndex >= 0 ? baselineIndex : 0;

        // ── Pass B: per-scenario compilation (pre-topology) ────────────────────
        for (int i = 0; i < scenarios.Count; i++)
        {
            if (!schemaValid[i])
            {
                continue;
            }

            var name = compilations[i].ScenarioName;
            var ast = scenarios[i];
            var scenarioBaseDirectory = compilations[i].ScenarioBaseDirectory;

            // The pre-topology authoring passes, run TOGETHER and reported together — the twin of
            // the merged door in RunScenarioOwningTopologyAsync, in the SAME order, which is the
            // whole point of merging them. Provider pipeline compile (which resolves `file:`/other
            // relative step fields against THIS scenario's own directory (#268), not the suite-wide
            // seed base directory) and the secret-reference pass (§17, S05-B-01), both before the
            // topology is built so an authoring fault costs no containers.
            //
            // THIS PATH USED TO RUN THEM IN THE OPPOSITE ORDER TO THE OTHER PATH, and each stopped
            // at its first fault, so a document carrying a security preflight fault AND a step
            // secret fault reported a DIFFERENT one on each path (#399's remainder). Both faults
            // are real, both are computed, both are reported.
            //
            // The `fromSecurityDeclaration` out-value is DISCARDED, and `IsSecurityPreflight` is not
            // consulted: a step-level fault in a SECURED document is an authoring fault like any
            // other, which is the widening this change makes — MEASURED as taking that shape from
            // exit 0 to exit 4 on both run paths.
            //
            // A PROVIDER WHOSE `Bind` THREW ARRIVES HERE AS A `PipelineResult.Failure` TOO
            // (issue #413), rather than as an exception unwinding past this loop. It is a provider
            // defect and not an authoring fault, but it is reported through this door because the
            // taxonomy answer is the same: the step was never compiled, nothing ran, and the
            // scenario is Inconclusive.
            //
            // ONE HELPER, THREE CALLERS (#412) — this seam, the single-scenario core, and the
            // `--watch` iteration plan. The two clauses above and the ordering they describe are
            // properties of RunPreTopologyAuthoringDoor now, not of this loop.
            var (pipelineResult, authoringFault) =
                RunPreTopologyAuthoringDoor(ast, registry, scenarioBaseDirectory);
            if (authoringFault is not null)
            {
                assurance = assurance.Refusing(SecurityAbortKind.AuthoringFault);

                compilations[i] = (name, ast, null, Verdict.Inconclusive, authoringFault,
                    scenarioBaseDirectory);
                continue;
            }

            compilations[i] = (name, ast, pipelineResult, null, null, scenarioBaseDirectory);
        }

        // ── Stop here when NO scenario can run ─────────────────────────────────
        // REQ-004's acceptance names "the pre-topology stage of `vouchfx run`", and EDGE-010(a)
        // says the suite "never reaches topology build". Without this guard neither held on the
        // `run` path: every scenario could carry an early verdict and the topology was still
        // built, health-gated and torn down, so a suite whose clientCert file is missing spent two
        // minutes starting containers and then reported a health-gate timeout — burying the
        // preflight message the engine had already computed and turning an exit 4 into an exit 3.
        //
        // The single-scenario core (RunScenarioOwningTopologyAsync, which `--parallel` drives)
        // already returns before StartAsync on a pipeline failure; this brings the shared-topology
        // path into line with it rather than inventing a new behaviour.
        //
        // MIXED SUITES ARE UNAFFECTED BY CONSTRUCTION: the condition is "EVERY scenario has an
        // early verdict". One valid scenario alongside one that failed preflight leaves this false,
        // the topology builds exactly as before, and the valid scenario runs.
        if (compilations.Count > 0 && compilations.TrueForAll(c => c.EarlyVerdict is not null))
        {
            return await CompleteWithoutTopologyAsync(
                    compilations,
                    WithUnbuiltDocuments(assurance),
                    output,
                    decorate,
                    diffLookup,
                    htmlReportPath,
                    junitReportPath,
                    eventsReportPath,
                    eventsStreamPath,
                    runSecretLedger: null,
                    runPathLedger: null)
                .ConfigureAwait(false);
        }

        // ── The seed root and the topology's environment must come from ONE scenario ──────────
        //
        // #451 made these separable for the first time. `seedBaseDirectory` is the CALLER's choice
        // and, for `RunCommand`, is the FIRST DISCOVERED scenario's directory unconditionally; the
        // environment — and therefore the `environment.seed` file list read against that root — now
        // comes from the first SCHEMA-VALID scenario. They are the same scenario in every suite
        // whose first document validates, which is every suite with no rejected document ahead of
        // the baseline. When they are not, and the scenarios live in DIFFERENT directories, a
        // relative seed path such as `./fixtures/orders.sql` names two different files.
        //
        // THE HARMFUL CASE IS THE ONE THAT RESOLVES, which is why this refuses rather than relying
        // on a missing file to fail loudly: where a same-named fixture exists in BOTH folders, the
        // suite silently seeds from the rejected document's copy — the wrong data, no diagnostic,
        // and a green run. Before #451 the shape was unreachable, because the divergence guard held
        // every scenario (rejected ones included) to one environment and the topology was built
        // from `scenarios[0]`, so the two roots could not come apart.
        //
        // Inconclusive, not EnvironmentError, on the same reasoning `TopologyAuthoringException`
        // records for deriving from ArgumentException: the author fixes this by editing the suite
        // (repair the rejected document, or co-locate the files), nothing infrastructural failed,
        // and an EnvironmentError that started no container exits 0 — silence on a suite refused
        // for seeding from the wrong folder.
        //
        // THE FOURTH CLAUSE IS WHAT KEEPS THIS FROM BEING THE VERY OVER-REFUSAL #451 REMOVED
        // (peer-review, final round). Multi-directory suites are legal since #268, so without it
        // this guard fired on a perfectly ordinary shape: two scenarios in two folders declaring a
        // BYTE-IDENTICAL environment, a seed, and a steps-level typo in the first file. Nothing has
        // moved there — the seed strings the baseline supplies are character-for-character the ones
        // scenarios[0] supplied, resolved against the same root as before #451 — yet the innocent
        // sibling would be refused for its neighbour's typo, which is precisely the outcome this
        // whole issue exists to prevent.
        //
        // IT COMPARES THE `seed` SECTIONS, NOT THE WHOLE ENVIRONMENT, and the narrower comparison is
        // the correct one because it is exactly the guard's stated hazard: this refusal is about
        // WHICH FILE a relative seed path names, and that is decided by the seed strings alone. Two
        // environments differing only in, say, a service image still supply the same seed list to
        // the same root, so refusing them would again refuse a suite where nothing moved. The whole
        // -environment form would have been the conservative choice and is deliberately not taken:
        // its extra refusals are all of that shape.
        //
        // scenarios[0] IS THE COMPARISON POINT because `seedBaseDirectory` is documented as the
        // first discovered scenario's directory and `RunCommand` passes exactly that. A caller that
        // roots the seed somewhere else entirely is outside this heuristic — the DirectoriesEqual
        // clause above still anchors on the real value, so such a caller is not refused for a
        // divergence, only left unprotected against a hazard it opted into.
        if (baselineIndex > 0
            && scenarios[topologyIndex].Environment?.Seed is not null
            && !DirectoriesEqual(compilations[topologyIndex].ScenarioBaseDirectory, seedBaseDirectory)
            && !string.Equals(
                SerialiseSeed(scenarios[0].Environment),
                SerialiseSeed(scenarios[topologyIndex].Environment),
                StringComparison.Ordinal))
        {
            // Issue #266, Item 4: the scenario name is author-controlled — sanitise before writing.
            // Printed HERE, once for the suite, and handed to the completion path as
            // `alreadyPrintedMessage` so the per-scenario walk prints no duplicate — the same
            // two-halves arrangement every neighbouring suite-level guard uses.
            var seedRootSplit =
                $"RunSuiteAsync: scenario '{scenarioNames[topologyIndex]}' supplies this suite's "
                + "environment (it is the first scenario that passes schema validation), but it "
                + "lives in a different directory from the one this suite's environment.seed file "
                + "paths resolve against, and it declares a DIFFERENT seed section from the "
                + "scenario that directory belongs to.  A relative seed path therefore names a "
                + "different file in each of the two, so the suite is refused rather than seeded "
                + "from the wrong folder.  "
                + "Fix the earlier scenario the schema rejected, or put the suite's scenarios in "
                + "one directory.";

            await output.WriteLineAsync(DisplaySanitiser.SanitiseForDisplay(seedRootSplit))
                .ConfigureAwait(false);

            return await CompleteWithoutTopologyAsync(
                    StampWhereUnjudged(compilations, Verdict.Inconclusive, seedRootSplit),
                    WithUnbuiltDocuments(assurance.Refusing(SecurityAbortKind.AuthoringFault)),
                    output,
                    decorate,
                    diffLookup,
                    htmlReportPath,
                    junitReportPath,
                    eventsReportPath,
                    eventsStreamPath,
                    runSecretLedger: null,
                    runPathLedger: null,
                    alreadyPrintedMessage: seedRootSplit)
                .ConfigureAwait(false);
        }

        // ── Build topology once ────────────────────────────────────────────────
        // REQ-005's probe runs inside StartAsync (after the health gate, before the seed) and
        // presents the SAME client security configuration a step will, which is what makes its
        // verdict evidence about the step rather than about the probe. Built from
        // scenarios[topologyIndex] — the divergence guard's own baseline, and therefore the
        // scenario whose environment block every OTHER schema-valid scenario in the suite has just
        // been required to match exactly — against that same scenario's own directory, so the
        // probe resolves each declared path to the same file EnvironmentSecurityValidator checked.
        // (It is `scenarios[0]` in every suite whose first document validates, which is every
        // suite that has no rejected document at all; the index exists so the topology and the
        // guard cannot disagree about which environment was proved shared — see #451.)
        //
        // …and that last clause is only true while every scenario shares one directory. Scenarios
        // are required to share a byte-identical `environment` block, NOT a folder (#268), so
        // `caCert: ./certs/ca.pem` in two scenarios one directory apart names two different files:
        // the probe would present the baseline's copy while a later scenario's steps present their
        // own. Both fail closed, so nothing passes on the wrong material — but the probe would no
        // longer be evidence about those steps, which is the whole basis of its verdict. Refused
        // rather than silently picked, and refused only for suites that declare security at all.
        //
        // RETURNED THROUGH CompleteWithoutTopologyAsync, NOT AS A BARE SuiteResult (gatekeeper
        // MAJOR-1 + security MINOR-1, fix round six). This guard is 40 lines above the protocol
        // -conflict seam that fix round five moved onto the shared completion path, and it had the
        // SAME hole: a bare return skips the ScenarioStarted/Completed events, the --events-stream
        // pump, the terminal render and FileReportWriter.WriteFileReports. MEASURED on the
        // divergence shape with --junit/--html/--events all requested: `junit exists = False,
        // html exists = False, events exists = False` — and unlike the conflict seam this one exits
        // NON-ZERO, so a CI job gets a red build beside an empty results directory and a JUnit
        // publisher reporting "no test results". Verdict and exit code are unchanged here; only the
        // artefacts now exist.
        //
        // THE LITERAL `true` THIS GUARD USED TO PASS IS GONE, and its absence is the point. It
        // bypassed the accumulating local entirely — the one producer that could not be read off
        // the local the doc comments designated as the account of the flag — because its refusal
        // was unconditional where the others were accumulated. It records an authoring refusal like
        // every other door now, and raises for the same reason they do: this guard only fires for a
        // suite that declares security, so `Declared` is non-empty by construction whenever it runs.
        //
        // WHY THIS ONE ITERATES ALL COMPILATIONS WHILE ITS NEIGHBOUR FILTERS TO RUNNABLE
        // (m-runnability, gatekeeper, fix round nine). Two adjacent guards apply opposite
        // runnability rules to the same list, both correctly, and the difference is what each one
        // protects. The protocol-conflict guard below protects STAGING — what the shared topology
        // puts in front of steps that will actually execute — so a scenario that runs nothing
        // stages nothing and must not veto one that does. This guard protects the DECLARATION: a
        // secured suite must be single-rooted whatever subset of it runs, because the material a
        // divergent scenario declares is evidence about what this suite's security means, and the
        // probe presents one root's material on behalf of whatever executes. It therefore fails
        // closed over the whole declaration, including scenarios carrying an early verdict — and
        // note that `compilations[0]`, the baseline both arms compare against, may itself carry
        // one, so filtering here would also change which directory is the reference.
        //
        // AND `compilations[0]` STAYS THE REFERENCE HERE WHILE THE ENVIRONMENT ARGUMENT USES
        // `topologyIndex` — deliberate, not an oversight of #451's reindexing (peer-review Q3). The
        // two cannot disagree about a suite this guard has anything to say about: it requires EVERY
        // compilation's directory to be equal, so on any suite it passes, `compilations[0]` and
        // `compilations[topologyIndex]` name the same directory, and on any suite it fails the run
        // is refused whichever of the two was the reference. Only the `environment` argument —
        // which decides whether the suite declares security AT ALL — needs to be the one that will
        // actually start, and that is why only it moved.
        if (TryFindSecurityBaseDirectoryDivergence(
                scenarios[topologyIndex].Environment, compilations, seedBaseDirectory, out var divergence))
        {
            // Issue #266, Item 4: the message splices scenario names straight from untrusted YAML.
            // Printed HERE, once, for the suite; the completion path is told what was printed so it
            // does not repeat it when it walks the per-scenario messages (see MAJOR-3's note on
            // StampWhereUnjudged).
            await output.WriteLineAsync(DisplaySanitiser.SanitiseForDisplay(divergence))
                .ConfigureAwait(false);

            return await CompleteWithoutTopologyAsync(
                    StampWhereUnjudged(compilations, Verdict.Inconclusive, divergence),
                    WithUnbuiltDocuments(assurance.Refusing(SecurityAbortKind.AuthoringFault)),
                    output,
                    decorate,
                    diffLookup,
                    htmlReportPath,
                    junitReportPath,
                    eventsReportPath,
                    eventsStreamPath,
                    runSecretLedger: null,
                    runPathLedger: null,
                    alreadyPrintedMessage: divergence)
                .ConfigureAwait(false);
        }

        // REQ-023 (amended): the suite half of the both-families rejection (gatekeeper MAJOR, fix
        // round four). ProviderPipeline.Compile already rejects a target addressed by both families
        // — but it runs once PER SCENARIO, so it only ever sees one scenario's steps, while the
        // staging it protects is suite-level: the StartAsync call below passes
        // SuiteProtocolTargets.KafkaSpeaking over the union of the RUNNABLE scenarios (see m7
        // below for why runnable and not all), because ONE shared topology serves them all. When
        // this guard was written that union was taken across every scenario; the rule it enforces
        // is unchanged by the narrowing, since both reads come from the one local built below.
        // A two-scenario suite whose first scenario addresses
        // 'broker' over http.rest and whose second addresses it over mq-publish.kafka therefore
        // passed both compilations (neither scenario alone addresses both families) and then staged
        // the bare host:port bootstrap authority for 'broker' — handing the HTTP step a value with
        // no scheme, which is exactly the outcome the requirement forbids, reached by exactly the
        // route the per-scenario guard was written to close. MEASURED, red first, by
        // ProtocolTargetConflictValidationTests' split row.
        //
        // THE INPUT IS THE RUNNABLE SCENARIOS, NOT ALL OF THEM (m7, peer-review critic, fix round
        // eight), and it is ONE LOCAL passed to both this guard and the KafkaSpeaking staging call
        // below — so the two literally cannot disagree about which targets speak what. That parity
        // is the invariant this slice re-broke five times; binding it to a single variable is what
        // makes it structural rather than a convention two call sites have to keep.
        //
        // WHY RUNNABLE ONLY. The union decides what the ONE shared topology stages, and a scenario
        // carrying an early verdict stages nothing because it executes nothing. Including it meant a
        // scenario that cannot run could veto one that can: a malformed `mq-publish.kafka` scenario
        // beside a valid `http.rest` one addressing the same target produced a suite-level protocol
        // conflict and refused the whole suite, so the valid scenario never ran — while the shape
        // one field away (fix the typo, and both scenarios are runnable) is a genuine conflict that
        // still refuses. This restores the rule the all-early guard above already states: a suite
        // mixing one broken scenario with one good one runs the good one, and reports the broken one
        // on its own terms.
        //
        // NOT A NARROWING OF THE GUARD'S REACH for any suite that could run. Two runnable scenarios
        // that split the families across themselves are exactly as rejected as before — that is the
        // split row this guard was written for, and both its scenarios are runnable by construction.
        //
        // CLASSIFICATION — AND THIS BRANCH'S OWN WRITTEN RATIONALE IS OVERTURNED HERE, deliberately
        // and on the record rather than quietly dropped. It used to argue: "a protocol conflict is
        // an authoring error, not a failure to confirm a security assertion", and declined to raise.
        // The schema door, elsewhere in this same method, argued the opposite of the same question
        // and widened. (No distance is quoted: an earlier wording said "forty lines above", which
        // is the base-directory guard's distance from this seam — correctly stated twice elsewhere
        // in this file — and not the schema door's, which is ~188 lines at HEAD and was ~195 at the
        // branch point. Two neighbouring true uses of the figure are what made the false one read
        // as checked.)
        // Both were sound in isolation; they cannot both be the rule, and while both shipped, door
        // ORDER decided an exit code.
        //
        // THE WIDE READING WINS (security-assurance-derivation, decision 3). A secured document
        // that aborts before any container starts is unconfirmable WHATEVER aborted it, because
        // nothing downstream of the refusal ever runs to confirm it. What the old rationale got
        // right is preserved by `Declared`, not by this branch: a suite declaring no security is
        // untouched here, exactly as before — exit 0 by default, exit 4 under
        // --fail-on-inconclusive — because an empty `Declared` raises nothing.
        //
        // USER-VISIBLE: a secured suite whose scenarios split the two protocol families now reddens
        // a build that was green. That is the widening, stated rather than discovered.
        //
        // RETURNED THROUGH CompleteWithoutTopologyAsync, NOT AS A BARE SuiteResult (gatekeeper
        // MAJOR, fix round five). Parity between the two seams is what this whole guard is for, and
        // an earlier form of this branch delivered it at the diagnostic layer only: a bare return
        // skips the ScenarioStarted/Completed events, the --events-stream pump, the terminal render
        // and — the one that reaches CI — FileReportWriter.WriteFileReports. MEASURED, red first:
        // `--junit results.xml` over the SPLIT spelling produced exit 0 (Inconclusive, by design)
        // and NO results.xml, so a JUnit publisher reported "no test results" and the build went
        // green; the single-scenario spelling of the identical error wrote the file. Nothing about
        // the verdict or the exit code changes here — Elevate(Pass, Inconclusive) is Inconclusive,
        // which is what the bare return said — only that the artefacts now exist.
        var runnableScenarios = compilations
            .Where(c => c.EarlyVerdict is null)
            .Select(c => (ScenarioAst?)c.Ast)
            .ToList();

        if (SuiteProtocolTargets.DescribeProtocolConflict(runnableScenarios) is { } protocolConflict)
        {
            // Issue #266, Item 4: the diagnostic splices target names straight from untrusted
            // YAML — sanitise before it reaches the terminal/CI log, exactly as the per-scenario
            // seam's own print site does.
            //
            // Printed HERE, once for the suite. The conflict is ONE suite-level fact, so the
            // terminal must show it once however many scenarios the suite has — but each scenario
            // is Inconclusive BECAUSE of it, so each scenario's own record must carry it as its
            // cause. Both properties are delivered together (MAJOR-3, fix round six): the stamp
            // below puts the text on every scenario that has no more specific message of its own,
            // and `alreadyPrintedMessage` tells the completion path that this exact text has
            // already reached the terminal so it prints no duplicate.
            //
            // MEASURED, the shape that made this a defect: scenario A addresses the target with
            // BOTH families (so A alone conflicts and the per-scenario seam stores the conflict
            // text verbatim as A's EarlyMessage) alongside a valid scenario B. The all-early guard
            // does not fire, this guard does, and the completion path then replayed A's preserved
            // message — two byte-identical prints from one fact.
            await output.WriteLineAsync(DisplaySanitiser.SanitiseForDisplay(protocolConflict))
                .ConfigureAwait(false);

            // The wrap here is pinned by the `ProtocolConflict` row of the theory named at the
            // shared-`environment` divergence door above — same defect, same reason it takes
            // unsecured scenarios to see it.
            return await CompleteWithoutTopologyAsync(
                    StampWhereUnjudged(compilations, Verdict.Inconclusive, protocolConflict),
                    WithUnbuiltDocuments(assurance.Refusing(SecurityAbortKind.AuthoringFault)),
                    output,
                    decorate,
                    diffLookup,
                    htmlReportPath,
                    junitReportPath,
                    eventsReportPath,
                    eventsStreamPath,
                    runSecretLedger: null,
                    runPathLedger: null,
                    alreadyPrintedMessage: protocolConflict)
                .ConfigureAwait(false);
        }

        // The SUITE's one scrub ledger (client-key-password REQ-010), and the same answer the
        // single-scenario path gives in full at its own construction site: the LEDGER is shared
        // across the run, the SCOPE is not. It is handed to the probe scope below and, per
        // scenario, to the step accessor built inside RunScenarioCoreAsync.
        //
        // ONE ledger for the WHOLE suite, not one per scenario, and the difference is deliberate:
        // the probe runs ONCE for the shared topology while N scenarios run against it, so a
        // per-scenario ledger could not contain the probe's values at all.
        //
        // THE COST, stated rather than argued away: a value scenario A resolved is blanked from
        // EVERY other scenario's text for the rest of the run. ResolvedSecretLedger.Record
        // rejects only null/empty/whitespace — there is no length floor — so a secret resolving
        // to a low-entropy string such as `8080`, `admin` or `5432` is recorded, and every later
        // occurrence of that string anywhere in the run is replaced by the redaction marker even
        // where it is an unrelated coincidence (a port in another scenario's HTTP observation).
        // Run-scoping widens that collision window from one scenario to N. It is accepted here
        // because the probe/step cross-path leak REQ-010 exists to close cannot be closed any
        // other way, and a corrupted diagnostic is recoverable where a leaked secret is not.
        // Do NOT "fix" it with a minimum-length floor in Record: a short secret is still a
        // secret, and silently declining to scrub it is a behaviour change of its own.
        //
        // The scope outside the `try`, the accessor inside it, for the reason given in full at the
        // single-scenario site above: a throwing `Build` must not skip the `finally` that disposes
        // these resolvers.
        var runSecretLedger = new ResolvedSecretLedger();

        // The #375 sibling net, run-scoped for the same reason and with the same lifetime as
        // the ledger above — see RunAsync's copy of this pair for why the two are separate types.
        var runPathLedger = new SecurityPathDisclosureLedger();

        var probeSecrets = CreateSecretAccessorScope(runSecretLedger);
        ISecurityConfigurationAccessor probeSecurity = NullSecurityConfigurationAccessor.Instance;

        SuiteTopology suite;
        try
        {
            probeSecurity = SecurityConfigurationAccessor.Build(
                scenarios[topologyIndex],
                compilations.Count > 0
                    ? compilations[topologyIndex].ScenarioBaseDirectory
                    : seedBaseDirectory,
                probeSecrets.Accessor,
                runPathLedger);

            // ONE ARGUMENT LIST (#364). `TopologyRequest.ForSuite` derives BOTH protocol target
            // sets from the SAME `runnableScenarios` list the protocol-conflict guard above was
            // handed — one variable, so the guard and the staging cannot disagree about the set,
            // which is the invariant this seam re-broke five times before it was bound to a single
            // local. See that guard's own note (m7) for why scenarios carrying an early verdict are
            // excluded: one executes nothing, so it stages nothing, and it must not be able to make
            // a project-form service look targeted (#348) for a step that was never going to run.
            suite = await TopologyRequest
                .ForSuite(
                    scenarios[topologyIndex].Environment,
                    runnableScenarios,
                    appHostAssemblyName,
                    seedBaseDirectory)
                .StartAsync(probeSecurity, runPathLedger, cancellationToken)
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
            // infrastructure faults an author cannot fix by editing the YAML). This catches Map's
            // eager, pre-Configure validation, plus the Configure-closure TopologyAuthoringException
            // that SuiteTopology.Step 2 re-throws unwrapped (#348); Map's Configure-closure
            // defensive throws (unreachable by construction given that same eager validation)
            // would surface as OrchestrationException instead, via the catch below.
            // SCRUBBED here, SANITISED at the print — the same composition, in the same order,
            // as the OrchestrationException sibling below, and it is a BLOCKER fix rather than
            // tidiness. This text is stamped onto every scenario record by the return below, so
            // since #372 it lands in the events stream, the JUnit `message` attribute and the
            // HTML report; a `clientKeyPassword` that resolved through `probeSecrets` (which
            // records into `runSecretLedger`) and then folded into an ArgumentException message
            // was written to all three, unscrubbed and unrecoverable. DisplaySanitiser does not
            // redact it — it neutralises control bytes and ANSI sequences, so an ordinary
            // printable passphrase passed straight through.
            //
            // Scrubbing HERE as well as at the stamping chokepoint is deliberate, not
            // belt-and-braces: the terminal write below and the stamp must be the SAME string,
            // because `alreadyPrintedMessage` compares them ordinally to suppress the duplicate
            // print. Scrub is idempotent (it replaces exact occurrences of recorded values), so
            // the chokepoint's own pass over this text finds nothing left to remove.
            //
            // Issue #266, Item 4: aex.Message can echo untrusted YAML content back
            // verbatim — sanitise before writing.
            var environmentFault = runPathLedger.Scrub(
                runSecretLedger.Scrub(
                    $"RunSuiteAsync: environment configuration error - {aex.Message}")!)!;

            await output.WriteLineAsync(DisplaySanitiser.SanitiseForDisplay(environmentFault))
                .ConfigureAwait(false);


            // The boundary is "no container started", not "before the StartAsync call": Map is
            // eager and runs ahead of DCP, and the Configure closure that raises #348's
            // TopologyAuthoringException runs before builder.Build(), so both refusals start
            // nothing and are authoring faults like every pre-topology one. It used to carry the
            // accumulated flag through
            // UNCHANGED — a return that could observe the fault and had no way to record it.
            //
            // The wrap here is pinned by the `EnvironmentMapperArgumentFault` row of the theory
            // named at the shared-`environment` divergence door above.
            // RETURNED THROUGH CompleteWithoutTopologyAsync, NOT AS A BARE SuiteResult. This was
            // the last bare return of the four the #407 family covers, and it needed the same
            // treatment for the same reason: a bare return skips the ScenarioStarted/Completed
            // events, the --events-stream pump, the terminal render and
            // FileReportWriter.WriteFileReports, so a CI job got a verdict with no artefacts
            // describing it.
            //
            // It also carries #369's answer for free. This catch's own boundary — stated two
            // paragraphs above as "no container started, not before the StartAsync call" — is
            // exactly ExecutedAnyScenario being false, and the completion path sets it because
            // that path IS the without-topology route. Nothing here has to remember to.
            //
            // Measured before this: `${conn:typo}` exited 0 under a bare `run` and 4 under
            // `--parallel 1`, because the parallel runner derives the same answer from its event
            // buffers while this return said nothing at all. A sequential/parallel divergence on
            // an exit code is a defect class this codebase has been bitten by before.
            return await CompleteWithoutTopologyAsync(
                    StampWhereUnjudged(compilations, Verdict.Inconclusive, environmentFault),
                    WithUnbuiltDocuments(assurance.Refusing(SecurityAbortKind.AuthoringFault)),
                    output,
                    decorate,
                    diffLookup,
                    htmlReportPath,
                    junitReportPath,
                    eventsReportPath,
                    eventsStreamPath,
                    runSecretLedger,
                    runPathLedger,
                    alreadyPrintedMessage: environmentFault)
                .ConfigureAwait(false);
        }
        catch (OrchestrationException oex)
        {
            // Scrubbed through the run ledger, THEN display-sanitised (client-key-password
            // REQ-010 + issue #266 Item 4) — the same composition the diagnosis write uses.
            //
            // This write is the only place a suite-path topology failure's TEXT is reported: the
            // return below emits no environment-error event (SuiteResult carries verdicts and a
            // flag, nothing free-form), so the chokepoint that scrubs that event never runs on
            // this path — it is one sink, not one of two. `OrchestrationException.Message`
            // interpolates `OrchestrationErrorInfo.Detail` verbatim (OrchestrationError.cs:164-165),
            // the same member the chokepoint scrubs, and a probe that reached the client material
            // resolved `clientKeyPassword` through `probeSecrets` — which records into
            // `runSecretLedger`. DisplaySanitiser alone would not redact it: it neutralises
            // control bytes and ANSI sequences, so an ordinary printable passphrase passes
            // through unchanged.
            // SCRUBBED here, SANITISED at the print — the two are not interchangeable and the
            // split matches the protocol-conflict seam above. Scrubbing is the security
            // requirement (REQ-010): it removes a resolved `clientKeyPassword` that reached
            // oex.Info.Detail, and the stamped cause below carries this text onto every scenario
            // record, into --junit/--html/--events, so it must be scrubbed BEFORE it is stamped.
            // Sanitising is display hygiene (#266 Item 4) — it neutralises control bytes and ANSI
            // sequences for a terminal, and would not redact an ordinary printable passphrase.
            var topologyFailure = runPathLedger.Scrub(
                runSecretLedger.Scrub(
                    $"RunSuiteAsync: topology failed to start - {oex.Message}"));

            await output.WriteLineAsync(DisplaySanitiser.SanitiseForDisplay(topologyFailure))
                .ConfigureAwait(false);

            // REQ-018: AT THIS CATCH, the classified kind on the exception is the whole
            // discriminator — not the message text and not the verdict — and IT IS UNTOUCHED by
            // the derivation. An unhealthy container, an unpullable image and an unrelated seed
            // failure all reach this same catch, record TopologyUnavailable, and still exit 0 by
            // default. That is #390, deliberately still open.
            //
            // Scoped to this catch deliberately. An earlier wording said "exactly ONE cause of an
            // Environment error exits non-zero without --fail-on-env-error", which was true when
            // the probe was the only one; the shared-`environment` divergence gate is now a second,
            // and it never reaches this catch at all — it returns an EnvironmentError verdict with
            // no exception to classify. A count of causes stated from inside one catch goes stale
            // the moment a cause appears outside it.
            //
            // `Refusing` keeps the more consequential refusal, so a scenario already refused at a
            // compile-time door does not have its record overwritten by a topology that then
            // failed to come up.
            // RETURNED THROUGH CompleteWithoutTopologyAsync, NOT AS A BARE SuiteResult (#407).
            // This was the LAST of the four instances of the defect the two seams above diagnosed:
            // a bare return skips the ScenarioStarted/Completed events, the --events-stream pump,
            // the terminal render and FileReportWriter.WriteFileReports. Measured on a suite whose
            // topology fails its health gate with --junit/--html/--events all requested: no
            // `Scenario '<id>' started` line and not one of the three artefacts written.
            //
            // It mattered more here than at the other seams once a secured suite began exiting 3
            // on this path: a red build sitting beside an empty results directory reads as a broken
            // runner rather than a real refusal, so the failure was correct but unattributable.
            //
            // Verdict and exit code are UNCHANGED, exactly as at the seams above — the aggregate
            // over N EnvironmentErrors is EnvironmentError, which is what the bare return said.
            // Only the artefacts now exist. The stamp carries the suite-level cause onto every
            // scenario that has no more specific message of its own, and `alreadyPrintedMessage`
            // tells the completion path this text already reached the terminal, so the print above
            // is not duplicated.
            //
            // The classified kind on the exception still decides the refusal, untouched: an
            // unhealthy container, an unpullable image and an unrelated seed failure all record
            // TopologyUnavailable and still exit 0 by default. That is #390, deliberately open —
            // this change is about what a run REPORTS, not about what it exits.
            return await CompleteWithoutTopologyAsync(
                    StampWhereUnjudged(compilations, Verdict.EnvironmentError, topologyFailure),
                    WithUnbuiltDocuments(assurance.Refusing(
                        oex.Info.Kind == OrchestrationErrorKind.SecurityConfirmation
                            ? SecurityAbortKind.ProbeUnconfirmed
                            : SecurityAbortKind.TopologyUnavailable)),
                    output,
                    decorate,
                    diffLookup,
                    htmlReportPath,
                    junitReportPath,
                    eventsReportPath,
                    eventsStreamPath,
                    runSecretLedger,
                    runPathLedger,
                    alreadyPrintedMessage: topologyFailure)
                .ConfigureAwait(false);
        }
        finally
        {
            // The probe is this accessor's only consumer and has finished on every path,
            // including the two catches that return above. Each scenario builds its own accessor
            // for its own steps inside RunScenarioAgainstTopologyAsync.
            (probeSecurity as IDisposable)?.Dispose();
            probeSecrets.Dispose();
        }

        await using (suite.ConfigureAwait(false))
        {
            // REQ-005: report declared-versus-observed, so a run's own output shows what was
            // ASSERTED and what was CONFIRMED. Emitted only when the suite declares security at
            // all, so an ordinary run's output is unchanged. Reaching here means every declared
            // block passed — a failure aborted StartAsync above — but "passed" is not one thing:
            // an authenticated application-layer round trip and a transport-only confirmation are
            // different assurances, and printing a bare "confirmed" for both would recreate the
            // over-claim this requirement exists to remove.
            foreach (var confirmation in suite.SecurityConfirmations)
            {
                await output.WriteLineAsync(
                        DisplaySanitiser.SanitiseForDisplay(confirmation.ToString()))
                    .ConfigureAwait(false);
            }

            // #348 (security review): the transport downgrade a project-form service declaring
            // both schemes incurs. Printed beside the security confirmations because it answers
            // the same question — what did this run actually talk to, and how. Empty for almost
            // every suite, so ordinary output is unchanged.
            //
            // The matching §14 records are emitted below, once `allBuffers` and the live pump
            // exist — see the "#450 / #453" block after the pump is opened.
            foreach (var notice in suite.EndpointSelectionNotices)
            {
                await output.WriteLineAsync(DisplaySanitiser.SanitiseForDisplay(notice.ToString()))
                    .ConfigureAwait(false);
            }

            // The other endpoint advisory, and a DIFFERENT fact: the address staged for the
            // service is an https listener the engine configures no client trust material for —
            // whoever selected it. Printed here rather than through a bespoke Console.WriteLine so
            // it goes through the same sanitiser every other author-influenced string does — the
            // notice carries a service name the author supplied.
            foreach (var notice in suite.EndpointTrustNotices)
            {
                await output.WriteLineAsync(DisplaySanitiser.SanitiseForDisplay(notice.ToString()))
                    .ConfigureAwait(false);
            }

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

            // #450 / #453: the two transport advisories printed above, onto the §14 event stream.
            // Written here rather than beside the prints because `allBuffers` (the --events
            // archive) and the pump (the live --events-stream file) are both opened between the
            // two points; the prints and this block are held together by the source gate in
            // TransportNoticeEventEmissionTests, not by adjacency.
            //
            // NOT scrubbed and NOT sanitised, deliberately — the WHICH-members analysis behind
            // both decisions is written out on TransportNoticeEvents, the one producer.
            //
            // ONE RECORD PER NOTICE PER TOPOLOGY BUILD, WHICH IS ONCE HERE. This method builds a
            // single topology for the WHOLE suite, so a suite of twelve scenarios reports each
            // advisory once — matching the terminal above, which also prints it once. Emitting
            // inside the scenario loop below would produce twelve copies of one fact.
            //
            // A MINTED runId, and it belongs to no scenario ON PURPOSE. Every other event on this
            // path carries the runId of the scenario that produced it, minted per iteration of the
            // loop below (§14: "each scenario has a distinct runId"). This advisory is not a
            // scenario's — it is the topology's, and the topology outlives every scenario in the
            // suite. Attributing it to one arbitrary scenario would state something false about
            // the other eleven, so it gets an id of its own; a consumer joining on runId finds
            // nothing to join to, which is the correct answer. The correlation key a consumer
            // actually wants is `service`, which is why the record carries it.
            var transportNoticeLines = TransportNoticeEvents.ToLines(
                suite.EndpointSelectionNotices,
                suite.EndpointTrustNotices,
                Guid.NewGuid().ToString("n"),
                DateTimeOffset.UtcNow,
                replayed: false);
            allBuffers.AddRange(transportNoticeLines);
            livePump?.PostRange(transportNoticeLines);

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
                        // #372 AT THE MIXED-SUITE SEAM — the gap #372 left, and the one an author
                        // is most likely to hit: `vouchfx run tests/` with ONE broken file beside
                        // runnable siblings does not reach CompleteWithoutTopologyAsync (the
                        // all-early guard requires EVERY scenario to carry a verdict), so this is
                        // the only door the broken file passes through. Before this it printed
                        // `earlyMessage` on the very next line and wrote it to nothing: the same
                        // document alone in a directory got a JUnit/HTML/events cause and beside a
                        // sibling got none.
                        buffer.Add(StepEventBuilder.ScenarioCompletedLine(
                            runId,
                            now,
                            name,
                            earlyVerdict.Value,
                            new VerdictCounts { Inconclusive = 1 },
                            runSecretLedger,
                            runPathLedger,
                            earlyMessage));
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
                        // Scrubbed through the suite ledger (REQ-010): the isolation reset's
                        // Detail folds the store client's own exception message, and by this
                        // point the probe and every earlier scenario have already recorded
                        // whatever they resolved.
                        buffer.Add(EnvironmentErrorLine(
                            runSecretLedger, runPathLedger, oex.Info, runId, now));

                        // The SAME text the terminal write below carries, built once so the two
                        // channels cannot diverge; the chokepoint scrubs it against the suite
                        // ledger exactly as that write does.
                        var isolationFault =
                            $"Isolation.BeginScenarioAsync failed for '{name}': {oex.Message}; " +
                            "aborting suite.";
                        buffer.Add(StepEventBuilder.ScenarioCompletedLine(
                            runId,
                            now,
                            name,
                            Verdict.EnvironmentError,
                            new VerdictCounts { EnvError = 1 },
                            runSecretLedger,
                            runPathLedger,
                            isolationFault));
                        results.Add((name, Verdict.EnvironmentError));
                        suiteAggregate = Elevate(suiteAggregate, Verdict.EnvironmentError);
                        allBuffers.AddRange(buffer);
                        livePump?.PostRange(buffer);

                        // Isolation failure → abort the suite (subsequent scenarios
                        // would run against an unknown DB state).
                        //
                        // Issue #266, Item 4: 'name' is author-controlled and oex.Message may
                        // echo untrusted content — sanitise before writing. And scrubbed first
                        // (REQ-010): oex.Message interpolates the SAME Detail the event line
                        // above scrubs, so scrubbing one sink and not the other would leave the
                        // value on the terminal/CI log.
                        await output.WriteLineAsync(
                            DisplaySanitiser.SanitiseForDisplay(
                                runPathLedger.Scrub(runSecretLedger.Scrub(isolationFault))))
                            .ConfigureAwait(false);
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
                        livePump: livePump,
                        sharedLedger: runSecretLedger,
                        sharedPathLedger: runPathLedger).ConfigureAwait(false);

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
                        // Scrubbed through the suite ledger (REQ-010) — same reason as the
                        // BeginScenarioAsync path above, and this one runs AFTER the scenario,
                        // so the ledger is at its fullest here.
                        var isolationFailureLine = EnvironmentErrorLine(
                            runSecretLedger, runPathLedger, oex.Info, runId, DateTimeOffset.UtcNow);
                        allBuffers.Add(isolationFailureLine);
                        var isolationFailureLines = new[] { isolationFailureLine };
                        livePump?.PostRange(isolationFailureLines);

                        // Issue #266, Item 4: 'name' is author-controlled and oex.Message may
                        // echo untrusted content — sanitise before writing. And scrubbed first
                        // through the ledger the comment above calls "at its fullest here" —
                        // oex.Message interpolates the same Detail that event line scrubbed.
                        await output.WriteLineAsync(
                            DisplaySanitiser.SanitiseForDisplay(
                                runPathLedger.Scrub(
                                    runSecretLedger.Scrub(
                                        $"Isolation.EndScenarioAsync failed after '{name}': {oex.Message}; "
                                        + "aborting suite - subsequent scenarios may run against "
                                        + "unclean state."))))
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

            return new SuiteResult(suiteAggregate, results)
            {
                // Threaded on the normal-completion path too, and REACHABLE carrying a refusal —
                // corrected in fix round eight (m3, peer-review critic). This note twice claimed
                // the opposite, and the reasoning is recorded rather than the conclusion, because
                // "unreachable" is what licenses a later reader to delete the assignment.
                //
                // The old argument ran: a `security` block lives in the `environment`, every
                // scenario in a suite must declare a byte-identical one, so a security rejection
                // takes all of a suite's scenarios or none — and the all-of-them case returns
                // above, before the topology is built.
                //
                // What it misses is that the byte-identical gate compares the parsed AST
                // (SerialiseEnvironment over ScenarioAst.Environment), not the YAML. The SCHEMA
                // door fires on shapes the AST cannot carry, and one is enough: a $defs/security
                // property declared with a NON-SCALAR value (`caCert: {}`) is a schema error
                // located inside that block, while GetScalar yields null for it and the key is on
                // ParseSecurity's exclusion list, so it reaches no member and no bucket either.
                // A scenario declaring it is therefore AST-indistinguishable from a sibling that
                // declares no `caCert` at all. The environment gate passes, that scenario alone
                // takes an early verdict at the schema branch of the compilation loop (recording a
                // refusal), the all-early guard does not fire because a sibling is still runnable,
                // and the suite runs to this return carrying it. Measured, not reasoned about:
                // SchemaSecuritySurfaceClosureTests
                // .ASecurityPropertyWithANonScalarValue_IsSchemaRejectedAndInvisibleInTheAst.
                //
                // AN UNKNOWN KEY IS A SECOND SHAPE THAT REACHES THIS RETURN, and its history is
                // why the route-ordering question was settled rather than left open. `$defs/security`
                // closes with `unevaluatedProperties: false`, and before #353 SecuritySpec had no
                // catch-all member, so the key was dropped and two scenarios differing only by it
                // were AST-identical — exactly like the non-scalar shape above. #353 gave
                // SecuritySpec an `Extra` bucket, the key began to SURVIVE into the AST, the two
                // environments diverged, and for as long as the shared-`environment` guard ran
                // ABOVE per-scenario schema validation that suite aborted EnvironmentError with
                // "declares a different environment block" instead: an authoring fault reported as
                // an infrastructure one, with the sibling refused alongside it. Issue #451 moved
                // the guard BELOW the schema pass, so this shape reaches the schema door again and
                // arrives back here with a located error naming the key. What never moved through
                // either route is the only thing this assignment is responsible for: every door
                // calls `assurance.Refusing(...)`, so a suite that declares `security` exits
                // non-zero whichever one it takes.
                //
                // The argument for THIS assignment therefore needs a shape that reaches this
                // return with a runnable sibling, and there are now two of them.
                //
                // So this is not defence in depth: it is the path REQ-018 takes for a mixed suite,
                // and dropping it would report exit 0 on a rejected security declaration.
                //
                // `Confirming` records what the probe established for the suite that DID run, so a
                // TransportConfirmed run is distinguishable from an AuthenticatedRoundTrip one by
                // something other than the terminal text.
                Assurance = WithUnbuiltDocuments(
                    assurance.Confirming(suite.SecurityConfirmations)),
            };
        }
    }

    /// <summary>
    /// The assurance a suite of NO scenarios and some unbuilt documents establishes: ONE document's
    /// whole assurance — its own declaration beside its own refusal — selected from the documents by
    /// <see cref="SecurityAssurance.Worse"/> (issue #411; Copilot, PR #416).
    /// </summary>
    /// <param name="unbuiltDocuments">
    /// <see cref="RunSuiteAsync"/>'s parameter of the same name. <see langword="null"/> or empty
    /// yields <see cref="SecurityAssurance.None"/> WITHOUT building a registry, so the ordinary
    /// empty call — every caller that does no discovery of its own — pays nothing and behaves
    /// byte-identically to before.
    /// </param>
    /// <param name="providerAssemblies">
    /// <see cref="RunSuiteAsync"/>'s parameter of the same name. The registry is built here rather
    /// than passed because the caller's own <c>BuildAndFreeze</c> runs BELOW the empty arm; this
    /// method's guard is what keeps that build off the empty path.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>The SAME per-document fold the main path applies, through the SAME function.</strong>
    /// Only the scenarios' contribution is missing, because there are none. WHAT a document
    /// contributes stays <see cref="UnbuiltDocument.Assure"/>'s single job and the fold stays
    /// <see cref="UnbuiltDocument.AssureAll"/>'s, so this arm cannot answer differently from the
    /// main path, from <c>ParallelSuiteRunner</c>, or from <c>ParallelSuiteRunner</c>'s own empty
    /// arm — the four call sites <see cref="UnbuiltDocument.AssureAll"/>'s own remarks enumerate,
    /// of which this method is one. (An earlier wording said "its own empty arm", which is this
    /// method: an arm cannot differ from itself.)
    /// </para>
    /// <para>
    /// <strong>What comes back is ONE document's whole assurance, not an aggregate.</strong>
    /// <see cref="UnbuiltDocument.AssureAll"/> folds whole values by
    /// <see cref="SecurityAssurance.Worse"/> and returns the worst of them, so the declaration and
    /// the refusal in the returned value belong to the SAME document — which is the point, since
    /// all-declarations-beside-all-refusals is exactly the union the paragraph below records as
    /// removed.
    /// </para>
    /// <para>
    /// This used to union every document's declaration into one <see cref="SecurityAssurance"/> and
    /// keep only each document's <see cref="SecurityAssurance.Refusal"/>, which paired one
    /// document's declaration with another document's refusal — the cross-document pairing
    /// <see cref="SecurityAssurance.Worse"/> exists to prevent (declaration-confirmation-matching,
    /// REQ-001; EDGE-002). It was harmless only while <c>Assure</c> contributed no refusal for a
    /// document that declared nothing, i.e. it rested on a property of a DIFFERENT method.
    /// </para>
    /// </remarks>
    private static SecurityAssurance AssureUnbuiltDocumentsAlone(
        IReadOnlyList<UnbuiltDocument>? unbuiltDocuments,
        IEnumerable<Assembly> providerAssemblies) =>
        unbuiltDocuments is not { Count: > 0 }
            ? SecurityAssurance.None
            : UnbuiltDocument.AssureAll(
                unbuiltDocuments, StepKindRegistry.BuildAndFreeze(providerAssemblies));

    /// <summary>
    /// True when any schema-validation error is located inside a declared <c>security</c> block —
    /// REQ-018's carve-out reached through the schema door rather than the preflight one.
    /// </summary>
    /// <remarks>
    /// Keyed on the JSON Pointer rather than on message text, which is the structural half of the
    /// error and the half a reworded diagnostic does not move. The classification fails CLOSED: a
    /// false positive costs a non-zero exit on a suite that was already invalid, while a false
    /// negative is a green pipeline on a rejected security declaration — the outcome REQ-018 exists
    /// to prevent. Failing closed is not, however, a licence to over-match: see
    /// <see cref="LocatesADeclaredSecurityBlock"/> for the measured cost of the substring test this
    /// once used.
    /// <para>
    /// <strong><see langword="internal"/> rather than private, because
    /// <see cref="UnbuiltDocument.Assure"/> asks the same question of a document this method's own
    /// caller never iterates.</strong> A document refused by <c>AstBuilder</c> is absent from
    /// <c>scenarios</c> by construction, so the schema door in the compilation loop cannot reach
    /// it. Sharing this method rather than re-deriving the answer there is what keeps the engine to
    /// ONE spelling of "is this refusal located in a declared <c>security</c> block" — the same
    /// discipline <c>SecuredTargets</c> imposes on "which targets declared one".
    /// </para>
    /// </remarks>
    internal static bool RejectsASecurityDeclaration(IReadOnlyList<SchemaValidationError> errors)
    {
        foreach (var error in errors)
        {
            if (LocatesADeclaredSecurityBlock(error.InstanceLocation))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="instanceLocation"/> points AT, or inside, a declared
    /// <c>security</c> block — the one structural shape the language gives that block:
    /// <c>/environment/{services|dependencies}/&lt;name&gt;/security</c> and anything beneath it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Segment equality, never a substring.</strong> An earlier form asked
    /// <c>InstanceLocation.Contains("/security")</c>, which also matched a NAME segment merely
    /// beginning with those characters. Measured, one pair of suites differing only in the service
    /// name, neither declaring any <c>security</c> block, both carrying the same ordinary
    /// <c>additionalProperties</c> error: a service called <c>security-gateway</c> exited 4 and one
    /// called <c>gateway</c> exited 0. That made an unrelated naming choice decide REQ-018's
    /// carve-out on a suite with ZERO security surface — a direct violation of the requirement's own
    /// mechanism clause ("MUST NOT alter the general Verdict-to-exit-code mapping used for ordinary
    /// environment errors"), and a bank naming a service <c>security-api</c> is precisely the
    /// target deployment. The block's location is fixed by the schema (<c>$defs/security</c> is
    /// referenced from <c>$defs/service</c> and <c>$defs/dependency</c> and nowhere else), so the
    /// shape can be anchored exactly rather than approximated.
    /// </para>
    /// <para>
    /// <strong>Form follows <see cref="SchemaErrorCollector.TryGetStepScope"/></strong>, which
    /// parses <c>/steps/&lt;N&gt;</c> out of the same kind of pointer for the same kind of decision:
    /// split, length-guard, then compare fixed segments by index. The one deliberate difference is
    /// the split option — <c>None</c> here, where that method uses
    /// <see cref="StringSplitOptions.RemoveEmptyEntries"/>. An EMPTY pointer segment is legal
    /// (RFC 6901) and reachable: <c>environment.services</c> and <c>environment.dependencies</c> are
    /// open objects declaring no <c>propertyNames</c> constraint, so a service named <c>""</c> is
    /// schema-legal and yields <c>/environment/services//security</c>. Dropping that empty segment
    /// would shift every index left and classify a GENUINE security block as unrelated — a false
    /// negative, the one direction this predicate must never fail in. Keeping empty segments costs
    /// nothing elsewhere: a rooted pointer's leading <c>/</c> always yields the empty
    /// <c>segments[0]</c> the guard below requires.
    /// </para>
    /// <para>
    /// <strong>Split the RAW pointer, before any RFC 6901 decoding.</strong> A name containing
    /// <c>/</c> arrives ESCAPED as <c>~1</c> (and <c>~</c> as <c>~0</c>) — JsonSchema.Net escapes
    /// the names it reports, which is exactly the defect slice C fixed in
    /// <c>DocumentValidator</c> — so a slash inside a name can never introduce a spurious segment
    /// here. Comparing the still-escaped segment against the literal <c>"security"</c> is
    /// nonetheless exact in both directions, because RFC 6901 escaping is injective and
    /// <c>security</c> contains no escapable character: the only name whose escaped form is
    /// <c>security</c> is <c>security</c> itself. Decoding first would BREAK that — measured, a
    /// service named <c>a/b</c> is reported as the single segment <c>a~1b</c> (and one named
    /// <c>c~d</c> as <c>c~0d</c>); decoding <c>a~1b</c> back to <c>a/b</c> before splitting would
    /// turn that one segment into two and shift every index after it.
    /// </para>
    /// </remarks>
    /// <param name="instanceLocation">
    /// A <see cref="SchemaValidationError.InstanceLocation"/>: an RFC 6901 JSON Pointer, rooted
    /// (leading <c>/</c>) unless it is the empty document-root pointer.
    /// </param>
    /// <returns>
    /// <see langword="true"/> for <c>/environment/services/app/security</c>,
    /// <c>/environment/dependencies/events-kafka/security/serverArtifacts/0/bogus</c>, and every
    /// pointer between; <see langword="false"/> for <c>/environment/services/security-gateway/bogus</c>
    /// and <c>/environment/services/security/bogus</c> — a service NAMED <c>security</c> that
    /// declares no such block is not a security declaration.
    /// </returns>
    internal static bool LocatesADeclaredSecurityBlock(string instanceLocation)
    {
        var segments = instanceLocation.Split('/');

        return segments.Length >= 5
            && segments[0].Length == 0
            && string.Equals(segments[1], "environment", StringComparison.Ordinal)
            && (string.Equals(segments[2], "services", StringComparison.Ordinal) ||
                string.Equals(segments[2], "dependencies", StringComparison.Ordinal))
            // segments[3] is the owner's own NAME — deliberately unconstrained, and deliberately
            // not compared against anything: it is the segment the substring form conflated with
            // the block below it.
            && string.Equals(segments[4], "security", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reports a suite in which every scenario carries an early verdict, without building the
    /// topology (REQ-004's "pre-topology stage of <c>vouchfx run</c>", EDGE-010(a)).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Emits the same per-scenario started/completed pair, the same diagnostic line, the same live
    /// stream, the same terminal render and the same file reports the main loop's early-exit branch
    /// emits — the only difference being that no container was started to produce them. Extracted
    /// rather than duplicated so the two can never diverge in what a reader of the events stream
    /// sees.
    /// </para>
    /// <para>
    /// FOUR CALLERS, and the latter three are why every scenario must be handed in already carrying
    /// a verdict rather than that being read off the compilation loop: the all-early-verdict guard
    /// above; and the three suite-level guards — shared-<c>environment</c> divergence,
    /// base-directory divergence and protocol conflict — which all STAMP their own suite-level
    /// verdict onto the scenarios that carry none of their own before calling (see
    /// <see cref="StampWhereUnjudged"/>). The <c>environment</c>-divergence guard joined that set
    /// with issue #451, which moved it BELOW per-scenario schema validation: it used to run above
    /// the whole compilation loop, where there was nothing compiled to stamp, and synthesised the
    /// list from its parameters instead. A caller that leaves an <c>EarlyVerdict</c>
    /// null gets an <see cref="InvalidOperationException"/> here, deliberately — <c>earlyVerdict</c>
    /// is a <c>Nullable&lt;Verdict&gt;</c>, and <c>.Value</c> on an empty one throws
    /// <c>"Nullable object must have a value"</c>, not a <see cref="NullReferenceException"/> (m1,
    /// gatekeeper, fix round six: the type was named wrongly here, which matters because it is the
    /// exception a caller would have to catch or a maintainer would have to recognise in a stack
    /// trace). A scenario with no verdict has no place in a completion that never runs anything.
    /// </para>
    /// <para>
    /// The emitted <c>counts</c> are DERIVED from that verdict rather than hardcoded (security
    /// NIT-3, same fix round): the previous form paired a parameterised
    /// <c>Verdict = earlyVerdict.Value</c> with a fixed <c>Inconclusive = 1</c>, so the first caller
    /// to stamp anything other than Inconclusive would have emitted a record whose verdict and
    /// counts contradicted each other. THAT CALLER NOW EXISTS: the shared-<c>environment</c>
    /// divergence guard stamps <see cref="Verdict.EnvironmentError"/>, and its scenarios emit
    /// <c>envError = 1</c> — the trap was removed one fix round before the path that would have
    /// sprung it.
    /// </para>
    /// </remarks>
    /// <param name="runSecretLedger">
    /// The suite's resolved-secret ledger, or <see langword="null"/> when the caller holds none.
    /// The four guards that run ABOVE the topology-build seam pass <see langword="null"/> and that
    /// is correct rather than lazy: no resolver exists on those paths — the compilation loop
    /// validates secret REFERENCES and never resolves one — so there is nothing to scrub against.
    /// The two <c>StartAsync</c> catches hold the real ledger and pass it.
    /// </param>
    /// <param name="alreadyPrintedMessage">
    /// A suite-level diagnostic the CALLER has already written to <paramref name="output"/>, or
    /// <see langword="null"/> when the caller printed nothing. Any scenario whose
    /// <c>EarlyMessage</c> is ordinally equal to it is skipped for TERMINAL printing only — the
    /// message stays on the scenario's own record, and since #372 that record IS a rendered
    /// channel: it reaches the events stream, the JUnit <c>message</c> attribute and the HTML
    /// report. This is what lets a suite-level fact be
    /// stamped as every affected scenario's cause
    /// while still appearing on the terminal exactly once (MAJOR-3, fix round six). The
    /// all-early-verdict caller passes <see langword="null"/> and is therefore byte-identically
    /// unchanged: per-scenario messages that merely happen to coincide are still each printed.
    /// </param>
    private static async Task<SuiteResult> CompleteWithoutTopologyAsync(
        // Concrete List, not IReadOnlyList (CA1859): every call site already holds the
        // concrete list — either `compilations` itself or StampWhereUnjudged's List return
        // — so the interface buys nothing but an indirection on Count/indexer.
        List<(
            string ScenarioName,
            ScenarioAst Ast,
            PipelineResult? Pipeline,
            Verdict? EarlyVerdict,
            string? EarlyMessage,
            string? ScenarioBaseDirectory)> compilations,
        SecurityAssurance assurance,
        TextWriter output,
        bool decorate,
        Func<string, JsonElement, string?> diffLookup,
        string? htmlReportPath,
        string? junitReportPath,
        string? eventsReportPath,
        string? eventsStreamPath,
        ResolvedSecretLedger? runSecretLedger,
        SecurityPathDisclosureLedger? runPathLedger,
        string? alreadyPrintedMessage = null)
    {
        var results = new List<(string ScenarioName, Verdict Verdict)>(compilations.Count);
        var suiteAggregate = Verdict.Pass;
        var allBuffers = new List<string>();

        await using var livePump = eventsStreamPath is not null
            ? new LiveEventPump(eventsStreamPath, output)
            : null;

        foreach (var (name, _, _, earlyVerdict, earlyMessage, _) in compilations)
        {
            var runId = Guid.NewGuid().ToString("n");
            var now = DateTimeOffset.UtcNow;
            var buffer = new List<string>
            {
                EventStreamJson.ToLine(new ScenarioStartedEvent
                {
                    RunId = runId,
                    Timestamp = now,
                    ScenarioId = name,
                }),
                // #372: the cause reaches the WRITTEN artefacts, not only the terminal.
                //
                // Carried whether or not the terminal print below is suppressed. Suppression
                // exists so ONE suite-level fact is not printed N times; it says nothing about
                // whether the record should carry the cause, and every scenario stamped with a
                // suite-level abort has exactly that text as its own cause. Passing
                // `alreadyPrintedMessage` here instead would blank the artefact for precisely
                // the runs that most need explaining.
                //
                // Scrubbing is the CHOKEPOINT's job now, not this site's and not the producers'.
                // This site used to assert that every producer of an EarlyMessage had already
                // scrubbed it; measured, one had not — RunSuiteAsync's ArgumentException catch
                // stamped `environmentFault` raw while its OrchestrationException sibling thirty
                // lines below scrubbed `topologyFailure`. Two sites disagreeing about whether to
                // scrub is the defect, so the obligation moved into
                // StepEventBuilder.ScenarioCompletedLine, which no stamping caller can bypass.
                StepEventBuilder.ScenarioCompletedLine(
                    runId,
                    now,
                    name,
                    earlyVerdict!.Value,
                    CountsFor(earlyVerdict.Value),
                    runSecretLedger,
                    runPathLedger,
                    earlyMessage),
            };

            // Terminal print, suppressed only for the exact text the caller has already written.
            // The message is NOT cleared from the record — it is this scenario's cause, and
            // suppressing the duplicate must not cost anything rendered from the record itself.
            if (!string.IsNullOrEmpty(earlyMessage)
                && !string.Equals(earlyMessage, alreadyPrintedMessage, StringComparison.Ordinal))
            {
                // Issue #266, Item 4: earlyMessage carries a schema/pipeline/secret-reference
                // diagnostic that may echo untrusted YAML content verbatim — sanitise.
                await output.WriteLineAsync(DisplaySanitiser.SanitiseForDisplay(earlyMessage))
                    .ConfigureAwait(false);
            }

            results.Add((name, earlyVerdict.Value));
            suiteAggregate = Elevate(suiteAggregate, earlyVerdict.Value);
            allBuffers.AddRange(buffer);
            livePump?.PostRange(buffer);
        }

        TerminalRenderer.Render(allBuffers, output, decorate, diffLookup);
        FileReportWriter.WriteFileReports(
            allBuffers, diffLookup, htmlReportPath, junitReportPath, output, eventsPath: eventsReportPath);

        return new SuiteResult(suiteAggregate, results)
        {
            Assurance = assurance,

            // #369: this method IS the without-topology path — no container started and no step
            // ran on any route that reaches here. Set once, at the one place that is true, rather
            // than enumerated door by door.
            ExecutedAnyScenario = false,
        };
    }

    /// <summary>
    /// The per-verdict step counts emitted for a scenario that never ran a step, derived from the
    /// verdict it was stamped with so the two can never contradict each other (security NIT-3).
    /// </summary>
    /// <remarks>
    /// A scenario stopped before the topology ran no steps at all, so these counts are not a tally
    /// of anything — they are the scenario's own single outcome expressed in the shape the wire
    /// contract's <c>counts</c> object has. Every stamp WAS Inconclusive when this was extracted,
    /// which is why it reproduced the previously-hardcoded <c>{ Inconclusive = 1 }</c> byte for
    /// byte; the shared-<c>environment</c> divergence guard now stamps
    /// <see cref="Verdict.EnvironmentError"/> and is the live path that needs the other arm.
    /// </remarks>
    private static VerdictCounts CountsFor(Verdict verdict) => verdict switch
    {
        Verdict.Pass => new VerdictCounts { Pass = 1 },
        Verdict.Fail => new VerdictCounts { Fail = 1 },
        Verdict.EnvironmentError => new VerdictCounts { EnvError = 1 },
        _ => new VerdictCounts { Inconclusive = 1 },
    };

    /// <summary>
    /// THE merged pre-topology authoring door (#399, #412): the provider-pipeline compile and the
    /// secret-reference walk, both run, neither short-circuiting the other, joined into one
    /// diagnostic.
    /// </summary>
    /// <param name="ast">The scenario to gate.</param>
    /// <param name="registry">The frozen provider registry.</param>
    /// <param name="baseDirectory">
    /// The directory relative step file fields (and <c>security</c> artefact paths) resolve
    /// against — THIS scenario's own directory (#268), which for the shared-topology suite path is
    /// not necessarily the suite-wide seed root.
    /// </param>
    /// <returns>
    /// The pipeline result (always computed, even when a fault is reported) and the joined fault,
    /// or <see langword="null"/> when the document passed both walks.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Extracted so "which fault an author sees first" cannot be a property of which path
    /// they took.</strong> Before #399 the two verdict-producing paths ran the two walks in opposite
    /// orders and each returned at its first fault, so the same document was diagnosed differently
    /// by <c>run</c> and by <c>run --parallel</c>. That was fixed by writing the merged door out at
    /// both sites; #412 then found the retired ordering surviving in a THIRD spelling on the
    /// <c>--watch</c> seam, which is what a duplicated door is for. There is now one.
    /// </para>
    /// <para>
    /// <strong>A document whose only fault is a step-secret fault still pays a full in-memory CSX
    /// emit, which is then thrown away.</strong> Recorded so it is not "optimised" back into a
    /// short-circuit: guarding the <c>Compile</c> call on <c>stepSecretFault is null</c> restores
    /// exactly the divergence #399 was filed for, silently, because both paths would still refuse.
    /// The cost is bounded — one in-memory emit for a scenario that is about to be refused; no
    /// topology, no container, no Roslyn ALC load.
    /// </para>
    /// <para>
    /// <strong>The <c>fromSecurityDeclaration</c> out-value is discarded</strong>, and that is the
    /// rule rather than an oversight: a secured document refused before any container started is
    /// unconfirmable whatever the fault was, and an unsecured one is not, whatever the fault was.
    /// Callers that derive REQ-018's signal do so from the refusal, not from the fault's shape;
    /// <c>--watch</c> derives none at all. The helper keeps the out-parameter for its own tests.
    /// </para>
    /// <para>
    /// <strong>A provider whose <c>Bind</c> threw arrives here as a <c>PipelineResult.Failure</c></strong>
    /// (#413) rather than as an exception unwinding past the door. It is a provider defect and not
    /// an authoring fault, but the taxonomy answer is the same: nothing compiled, nothing ran.
    /// </para>
    /// </remarks>
    internal static (PipelineResult Pipeline, string? Fault) RunPreTopologyAuthoringDoor(
        ScenarioAst ast, StepKindRegistry registry, string? baseDirectory)
    {
        var pipelineResult = ProviderPipeline.Compile(ast, registry, SuiteNamespace, baseDirectory);
        var stepSecretFault = TryValidateSecretReferences(ast, out var secretError, out _)
            ? secretError
            : null;

        return (
            pipelineResult,
            pipelineResult.Failure is null && stepSecretFault is null
                ? null
                : JoinAuthoringFaults(pipelineResult.Failure?.Message, stepSecretFault));
    }

    /// <summary>
    /// The single diagnostic for the merged pre-topology authoring door: every fault it found, in
    /// a fixed order, joined the way the schema door already joins its own error list.
    /// </summary>
    /// <param name="pipelineFailure">The provider-pipeline refusal, or <see langword="null"/>.</param>
    /// <param name="stepSecretFault">The secret-reference refusal, or <see langword="null"/>.</param>
    /// <remarks>
    /// <para>
    /// The ORDER is fixed here rather than at the two call sites, because "which fault is reported
    /// first" being a property of the call site is the defect this exists to remove: the two run
    /// paths ran the two passes in opposite orders and each reported only its first, so the same
    /// document was diagnosed differently by <c>run</c> and by <c>run --parallel</c>.
    /// </para>
    /// <para>
    /// At least one argument is non-empty at every call site (both are guarded by
    /// <c>failure is not null || fault is not null</c>), so this never returns the empty string on
    /// a live path.
    /// </para>
    /// <para>
    /// <strong>The separator is a NEWLINE, not <c>"; "</c>, because both halves are complete
    /// sentences ending in a full stop.</strong> The semicolon form rendered
    /// <c>…no-such-cert.pem').; step 'call': …</c> — a stop and a semicolon adjacent, mid-line, in
    /// the one diagnostic an author reads to find two unrelated faults. The security half of
    /// <see cref="TryValidateSecretReferences"/> already joins its own two messages with <c>"\n"</c>
    /// for exactly this reason.
    /// </para>
    /// <para>
    /// <c>"\n"</c> is available HERE, and the constraint that forbids it there does not apply: that
    /// site's string reaches the schema-versioned <c>validate --json</c> document, where
    /// <c>Environment.NewLine</c> would make one document differ between a Windows and a Linux
    /// runner — and <c>"\n"</c> is the platform-independent answer to that, not an escape from it.
    /// This helper's two call sites are BOTH run-path terminal: one writes straight to the run's
    /// output writer through <c>DisplaySanitiser</c> (which preserves <c>\n</c> and
    /// strips <c>\r</c>), the other becomes an <c>EarlyMessage</c>, which #372 carried onto
    /// <see cref="ScenarioCompletedEvent.Message"/> — so this string DOES reach the event stream,
    /// the JUnit <c>message</c> attribute and the HTML report. (An earlier revision of this remark
    /// said the opposite, and it was true only until #372 landed on this same branch.)
    /// </para>
    /// </remarks>
    private static string JoinAuthoringFaults(string? pipelineFailure, string? stepSecretFault) =>
        string.Join(
            "\n",
            new[] { pipelineFailure, stepSecretFault }.Where(m => !string.IsNullOrEmpty(m)));

    /// <summary>
    /// The assurance contributed by every SCHEMA-REJECTED scenario whose serialised
    /// <c>environment</c> differs from the baseline's: one whole value per such scenario — its own
    /// declaration beside its own refusal, with <see cref="SecurityAssurance.Confirmed"/> empty by
    /// construction — folded by <see cref="SecurityAssurance.Worse"/> (issue #451, security review).
    /// </summary>
    /// <param name="scenarios">The suite's parsed ASTs, in declaration order.</param>
    /// <param name="schemaValid">
    /// Pass A's per-scenario answer: <see langword="true"/> where <c>DocumentValidator</c> accepted
    /// the document. Only the <see langword="false"/> entries are candidates here.
    /// </param>
    /// <param name="schemaRefusalKinds">
    /// The abort kind Pass A recorded for each REJECTED scenario, per scenario. Read rather than
    /// re-derived from the accumulated assurance, which keeps only the highest-precedence kind
    /// across the whole suite and would therefore attribute a SIBLING's refusal to this scenario.
    /// A <see langword="null"/> entry for a rejected scenario falls back to
    /// <see cref="SecurityAbortKind.AuthoringFault"/> — defensive only; Pass A sets every one it
    /// rejects.
    /// </param>
    /// <param name="baselineIndex">
    /// The index of the first schema-valid scenario — the environment the shared topology is built
    /// from — or <c>-1</c> when the suite has none.
    /// </param>
    /// <returns>
    /// <see cref="SecurityAssurance.None"/> when nothing qualifies, which is the fold's identity
    /// element and contributes nothing at any call site.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Extracted rather than inlined so the DECISION can be tabled, not merely the folded
    /// value</strong> (peer-review MAJOR-1). Its four cells — rejected+divergent+declaring,
    /// rejected+equal-environment, rejected+divergent+unsecured, schema-valid — are pinned by
    /// <c>FoldRejectedDivergentTests</c> without a container. Inline, only the third of those was
    /// reachable from an end-to-end test on this machine, and inverting the equality gate left the
    /// whole suite green.
    /// </para>
    /// <para>
    /// <strong>The gate is environment EQUALITY, not schema validity, and the direction matters.</strong>
    /// A rejected scenario whose environment EQUALS the baseline's is exactly as covered by the
    /// suite's own probe as its siblings are — that is the ordinary typo shape #451 exists to
    /// report as an authoring fault — so it stays in the canonical union and a confirmation may
    /// satisfy it. Only a rejected scenario whose environment DIFFERS is folded here, because
    /// nothing held that environment against the one that started.
    /// </para>
    /// <para>
    /// <strong>A scenario that declared no <c>security</c> block contributes NOTHING</strong>, the
    /// same guard <see cref="UnbuiltDocument.Assure"/> applies and for the same reason: an empty
    /// declaration paired with a refusal would let a scenario that asserted nothing displace a
    /// sibling's evidence in the fold, and <see cref="SecurityAssurance.None"/> must stay the
    /// fold's identity element rather than becoming a refusal looking for a declaration.
    /// </para>
    /// <para>
    /// <c>internal</c> (not <c>private</c>) so <c>Vouchfx.Engine.Runtime.Tests</c> — already granted
    /// access by this assembly's <c>InternalsVisibleTo</c> — can drive the four cells directly,
    /// exactly as <c>BindAllSteps</c> and <c>BuildProjectContext</c> are driven.
    /// </para>
    /// </remarks>
    internal static SecurityAssurance FoldRejectedDivergent(
        IReadOnlyList<ScenarioAst> scenarios,
        IReadOnlyList<bool> schemaValid,
        IReadOnlyList<SecurityAbortKind?> schemaRefusalKinds,
        int baselineIndex)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        ArgumentNullException.ThrowIfNull(schemaValid);
        ArgumentNullException.ThrowIfNull(schemaRefusalKinds);

        // No schema-valid scenario means no baseline and no topology: the all-early guard returns
        // before anything starts, so nothing can be confirmed and the canonical union already
        // raises for whatever was declared.
        if (baselineIndex < 0)
        {
            return SecurityAssurance.None;
        }

        var baselineEnvJson = SerialiseEnvironment(scenarios[baselineIndex].Environment);
        var folded = SecurityAssurance.None;

        for (int i = 0; i < scenarios.Count; i++)
        {
            if (schemaValid[i]
                || string.Equals(
                    SerialiseEnvironment(scenarios[i].Environment),
                    baselineEnvJson,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var declaredHere = SecuredTargets.Enumerate(scenarios[i].Environment).ToArray();
            if (declaredHere.Length == 0)
            {
                continue;
            }

            folded = SecurityAssurance.Worse(
                folded,
                SecurityAssurance.None
                    .Declaring(declaredHere)
                    .Refusing(schemaRefusalKinds[i] ?? SecurityAbortKind.AuthoringFault));
        }

        return folded;
    }


    /// <summary>
    /// Stamps the caller's <paramref name="verdict"/> and a suite-level <paramref name="cause"/>
    /// onto every compilation that carries no early verdict of its own, leaving the ones that do
    /// untouched — the shared preamble every suite-level guard runs before
    /// <see cref="CompleteWithoutTopologyAsync"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The verdict is the caller's, not <see cref="Verdict.Inconclusive"/></strong>, and
    /// this summary said otherwise until issue #451 gave the method callers that pass something
    /// else. Counted at HEAD: SIX call sites, of which four pass <see cref="Verdict.Inconclusive"/>
    /// (seed-root split, base-directory divergence, protocol conflict, the <c>Map</c>-time
    /// <c>ArgumentException</c>) and two pass <see cref="Verdict.EnvironmentError"/> (the
    /// shared-<c>environment</c> divergence guard, and the topology-failure catch). Grep the call
    /// sites rather than trusting that count — it is a census, and a census goes stale exactly the
    /// way the sentence it replaced did.
    /// </para>
    /// <para>
    /// EXTRACTED RATHER THAN COPIED (MAJOR-1, fix round six). The protocol-conflict guard grew this
    /// loop in fix round five; the base-directory-divergence guard forty lines above it still
    /// returned a bare <see cref="SuiteResult"/> and had to grow the same one. Two copies of a
    /// completion preamble is precisely how those two seams diverged in the first place, so there
    /// is one.
    /// </para>
    /// <para>
    /// A scenario that ALREADY carries an early verdict keeps it AND its own message: that message
    /// is the more specific fact about that scenario (a schema error, a secret-reference error, a
    /// preflight rejection), and the suite-level cause would say less about it. Every other
    /// scenario is Inconclusive BECAUSE of the suite-level fact, so it is stamped as that
    /// scenario's own cause rather than left null — a per-scenario record with no cause cannot
    /// explain itself to anything rendered from it.
    /// </para>
    /// <para>
    /// <strong>The stamped cause is rendered, and that makes it load-bearing.</strong> #372 carried
    /// <c>EarlyMessage</c> onto <see cref="ScenarioCompletedEvent.Message"/>, so the text stamped
    /// here reaches the event stream, the JUnit <c>message</c> attribute and the HTML report as
    /// well as <see cref="CompleteWithoutTopologyAsync"/>'s terminal-print suppression check.
    /// Whatever composes a <c>cause</c> for this method owns that channel's constraints — no
    /// resolved secret value and no absolute host path in the text (see
    /// <see cref="ScenarioCompletedEvent.Message"/>'s own remarks). An earlier revision of this
    /// paragraph said nothing rendered it; that was true only until #372 landed on this branch.
    /// </para>
    /// <para>
    /// Stamping the cause is what makes it a duplicate on the TERMINAL, which the caller resolves
    /// by handing the same text to <c>CompleteWithoutTopologyAsync</c>'s
    /// <c>alreadyPrintedMessage</c>. The two halves belong together and are documented together
    /// for that reason.
    /// </para>
    /// </remarks>
    private static List<(
        string ScenarioName,
        ScenarioAst Ast,
        PipelineResult? Pipeline,
        Verdict? EarlyVerdict,
        string? EarlyMessage,
        string? ScenarioBaseDirectory)> StampWhereUnjudged(
        // Concrete List, not IReadOnlyList (CA1859) — see CompleteWithoutTopologyAsync.
        List<(
            string ScenarioName,
            ScenarioAst Ast,
            PipelineResult? Pipeline,
            Verdict? EarlyVerdict,
            string? EarlyMessage,
            string? ScenarioBaseDirectory)> compilations,
        Verdict verdict,
        string cause)
    {
        var stamped = new List<(
            string ScenarioName,
            ScenarioAst Ast,
            PipelineResult? Pipeline,
            Verdict? EarlyVerdict,
            string? EarlyMessage,
            string? ScenarioBaseDirectory)>(compilations.Count);

        foreach (var compilation in compilations)
        {
            stamped.Add(compilation.EarlyVerdict is not null
                ? compilation
                : (compilation.ScenarioName,
                   compilation.Ast,
                   compilation.Pipeline,
                   verdict,
                   cause,
                   compilation.ScenarioBaseDirectory));
        }

        return stamped;
    }

    /// <summary>
    /// Detects the divergences a shared <c>environment</c> block cannot rule out: declared security
    /// paths that would resolve against DIFFERENT directories depending on which consumer resolves
    /// them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Scenario against scenario.</strong> Scenarios in a suite must declare a
    /// byte-identical <c>environment</c>, but they need not live in the same folder (#268), and
    /// every <c>security</c> path is resolved relative to the scenario's own directory (REQ-003).
    /// So one <c>caCert: ./certs/ca.pem</c> can name two files. The probe presents ONE of them, on
    /// behalf of steps that would present the other; both fail closed, so nothing passes on the
    /// wrong material, but the probe stops being evidence about those steps.
    /// </para>
    /// <para>
    /// <strong>Scenario against seed root</strong> (m4, peer-review critic, fix round eight). The
    /// CLIENT material — the probe's and the steps' <c>caCert</c>/<c>clientCert</c>/<c>clientKey</c>
    /// — resolves against <c>compilations[topologyIndex].ScenarioBaseDirectory</c> (the scenario the
    /// topology is built from: the first schema-valid one, index 0 unless a rejected document sits
    /// ahead of it — issue #451), while REQ-016's SERVER
    /// artefacts resolve against <c>SuiteTopology.StartAsync</c>'s own <c>seedBaseDirectory</c>.
    /// Those are two parameters, and a divergent pair splits one suite's security material across
    /// two roots — the client half trusting an anchor from one directory while the server half is
    /// handed a keystore from another.
    /// </para>
    /// <para>
    /// MEASURED before choosing between "pass one value to both" and "assert equality": the engine
    /// has exactly ONE production caller of <c>RunSuiteAsync</c>, <c>RunCommand</c>, and it derives
    /// <c>suiteBaseDirectory</c> as <c>scenarioBaseDirectories[0]</c> — literally the same string,
    /// so the pair is equal by construction on every CLI path. Every test caller passes neither and
    /// takes the null-to-null fallback. Collapsing the two parameters into one was therefore
    /// rejected: it is unreachable on the only path that exists, and it would silently re-root a
    /// library embedder's SEED, which is genuinely single-rooted by design and is not this
    /// guard's business. Asserting instead refuses exactly the configuration whose security
    /// material is ambiguous and touches nothing else.
    /// </para>
    /// <para>
    /// Both arms are checked only when the suite declares security at all, so an ordinary
    /// multi-directory suite is untouched.
    /// </para>
    /// <para>
    /// <strong>Comparison is normalised</strong> (n1, peer-review critic, fix round eight). It used
    /// to be a raw <see cref="StringComparison.Ordinal"/> compare of two strings that reach here
    /// from different producers, so <c>D:\Suite</c> against <c>d:\suite\</c> — the same directory —
    /// refused a correct suite. <see cref="NormaliseDirectoryForComparison"/> applies
    /// <see cref="Path.GetFullPath(string)"/>, trims a trailing separator, and compares
    /// case-insensitively on Windows, mirroring <c>RunCommand.PathsEqual</c> (which cannot be
    /// shared: it lives in the CLI, which references this assembly and not the reverse). It differs
    /// from that helper in one deliberate respect — an unnormalisable path is treated as DIVERGENT
    /// rather than as "not comparable", because this guard's failure direction must be closed.
    /// </para>
    /// </remarks>
    private static bool TryFindSecurityBaseDirectoryDivergence(
        EnvironmentSpec? environment,
        // Concrete List, not IReadOnlyList (CA1859) — see CompleteWithoutTopologyAsync.
        List<(
            string ScenarioName,
            ScenarioAst Ast,
            PipelineResult? Pipeline,
            Verdict? EarlyVerdict,
            string? EarlyMessage,
            string? ScenarioBaseDirectory)> compilations,
        string? seedBaseDirectory,
        out string message)
    {
        message = string.Empty;

        if (!EnvironmentSecurityValidator.DeclaresSecurity(environment) || compilations.Count == 0)
        {
            return false;
        }

        var first = compilations[0];

        // ORDER IS DELIBERATE. The scenario-against-scenario arm runs FIRST because it is the
        // author's cause — something about the suite's own layout, fixable by moving a file — while
        // the seed-root arm below is an EMBEDDER's cause, fixable only in the calling host's code.
        // A fixture can trip both at once (a suite with per-scenario directories and no
        // seedBaseDirectory does), and the author-facing diagnostic is the more useful of the two.
        if (compilations.Count >= 2)
        {
            foreach (var candidate in compilations)
            {
                if (DirectoriesEqual(candidate.ScenarioBaseDirectory, first.ScenarioBaseDirectory))
                {
                    continue;
                }

                message =
                    $"RunSuiteAsync: this suite declares a 'security' block, but scenario "
                    + $"'{candidate.ScenarioName}' resolves its declared security paths against a "
                    + $"different directory than '{first.ScenarioName}' does. Every security path is "
                    + "resolved relative to its own scenario's directory (REQ-003), so one declared "
                    + "'caCert'/'clientCert'/'clientKey' would name two different files - and REQ-005's "
                    + "probe can only present one of them, on behalf of steps that would present the "
                    + "other. Put the scenarios of a secured suite in one directory.";
                return true;
            }
        }

        // The seed-root arm, checked for a single-scenario suite too — it is a property of the two
        // PARAMETERS, not of how many scenarios there are.
        if (!DirectoriesEqual(first.ScenarioBaseDirectory, seedBaseDirectory))
        {
            message =
                "RunSuiteAsync: this suite declares a 'security' block, but the directory its "
                + "scenarios resolve declared security paths against is not the directory the "
                + "topology resolves 'security.serverArtifacts' against. The client material "
                + "('caCert'/'clientCert'/'clientKey') would be read from one root and the server "
                + "artefacts written from another, so one declared relative path would name two "
                + "different files. Pass the same base directory as both 'seedBaseDirectory' and "
                + "the scenarios' own base directory (the CLI does - it derives one from the other; "
                + "a direct engine embedder must too).";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether two base directories denote the same directory, for
    /// <see cref="TryFindSecurityBaseDirectoryDivergence"/>. Two nulls are equal (both fall back to
    /// the same root); one null is not.
    /// </summary>
    private static bool DirectoriesEqual(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        var left = NormaliseDirectoryForComparison(a);
        var right = NormaliseDirectoryForComparison(b);

        // Fail CLOSED: an unnormalisable path is not proven equal, so it is treated as divergent.
        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(
            left,
            right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves a directory to its full form with any trailing separator removed, or
    /// <see langword="null"/> when <see cref="Path.GetFullPath(string)"/> itself rejects it.
    /// </summary>
    private static string? NormaliseDirectoryForComparison(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
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
    /// An uppercase hex SHA-256 over the same serialisation <see cref="RunSuiteAsync"/> uses for
    /// its shared-environment check, so the two can never disagree about what counts as "the same
    /// environment".  An empty string for a <see langword="null"/> environment.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Derived from the private <see cref="SerialiseEnvironment"/> helper — the SAME canonical
    /// form the suite runner already compares for its shared-topology assumption — so the reuse
    /// decision here and the suite's equality check are guaranteed consistent.
    /// </para>
    /// <para>
    /// <strong>It DIGESTS that form rather than returning it, and the name was the honest one
    /// before this method was (#353, review round two).</strong> It used to return the serialised
    /// JSON itself: plaintext, carrying <c>"ClientKeyPassword"</c> with whatever the author
    /// declared and, since #353, the two untyped <c>Extra</c> buckets beside it. A
    /// <see langword="public"/> method returning declared secret material under a name that
    /// promises a digest is a disclosure waiting for the first caller who trusts the name; #353
    /// did not create it but did enlarge its payload.
    /// </para>
    /// <para>
    /// <strong>Behaviour-preserving, which is why it could be fixed here rather than filed.</strong>
    /// This method has exactly ONE call site outside tests —
    /// <see cref="ComputeTopologyFingerprint"/> — and that chain compares the value and does
    /// nothing else: the fingerprint reaches <c>WatchCompileResult</c>, whose get-only
    /// <c>TopologyFingerprint</c> property <c>WatchSession</c> reads for exactly one ordinal
    /// <c>string.Equals</c> against the fingerprint of the topology it is keeping. No persistence,
    /// no file write, no event field, no rendered line. A digest therefore answers every question
    /// the plaintext answered. (Until #370 the call site was <c>WatchRunner.Compile</c> and this
    /// value WAS the reuse key; it is now one of six inputs to it, which changes nothing about the
    /// disclosure argument.)
    /// </para>
    /// <para>
    /// <strong><see cref="RunSuiteAsync"/>'s shared-environment guard is NOT a consumer of this
    /// value</strong>, and this note claimed it was until review measured the call sites. That
    /// guard calls the private <see cref="SerialiseEnvironment"/> helper directly and compares the
    /// PLAINTEXT JSON, so it neither gained nor lost anything here. What the two share is the
    /// SERIALISATION, not this method's return value — which is exactly what the consistency
    /// claimed above rests on, and is the whole of the relationship.
    /// </para>
    /// <para>
    /// The bytes are the string's UTF-16 code units REINTERPRETED, not transcoded — for
    /// consistency with <c>SecuredTargets.IdentityOf</c>, which hashes code units because it takes
    /// RAW declared strings and an <see cref="System.Text.Encoding"/> replacement fallback there
    /// really does map two distinct <c>caCert</c> values onto one (a CLI test pins a pair differing
    /// only by a lone surrogate). That reason does not transfer to THIS input and must not be
    /// restated as if it did: the text arriving here has already been through
    /// <see cref="JsonSerializer"/>, which substitutes U+FFFD for an unpaired surrogate itself —
    /// measured, two field values differing only in WHICH lone high surrogate they carry both
    /// serialise to one identical JSON string, the surrogate replaced before this method is
    /// reached. The collision is collapsed upstream, so the choice here buys uniformity rather
    /// than injectivity.
    /// </para>
    /// </remarks>
    public static string ComputeEnvironmentHash(EnvironmentSpec? environment) =>
        SerialiseEnvironment(environment) is { Length: > 0 } json
            ? Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(json.AsSpan())))
            : string.Empty;

    /// <summary>
    /// The reuse key for a kept topology: a digest over EVERY input
    /// <see cref="SuiteTopology.StartAsync"/> would receive, not over the <c>environment</c> block
    /// alone (#370's recorded residual).
    /// </summary>
    /// <param name="request">The request a rebuild would be made from.</param>
    /// <remarks>
    /// <para>
    /// <strong>WHY THE ENVIRONMENT HASH ALONE WAS NOT ENOUGH, twice over.</strong> Both target sets
    /// are derived from the <c>steps</c>, and both change what the built topology IS:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <c>endpointConsumingTargets</c> decides in <c>EnvironmentMapper.Map</c> whether an
    ///     endpoint-less <c>project:</c>-form service is refused (#348) or is a legitimate
    ///     untargeted worker. Under the environment-hash-only trigger, a save that added a step
    ///     targeting a previously-untargeted worker left the hash unchanged, reused the topology,
    ///     and never re-ran the refusal — that session saw #348's <c>UriFormatException</c> instead
    ///     of the located diagnostic, for the rest of the session. `WatchRunner` carried this as a
    ///     stated RESIDUAL; it is deleted with the residual.
    ///   </description></item>
    ///   <item><description>
    ///     <c>kafkaSpeakingTargets</c> decides the STAGED FORM, not merely the confirmation level
    ///     (REQ-023): a Kafka-speaking service is staged as a bare <c>host:port</c> authority and
    ///     any other as a scheme-carrying URL. A save adding the FIRST Kafka step against a service
    ///     that previously had no steps grows that set without tripping the protocol-conflict guard
    ///     — only one family addresses the target — so the kept topology went on serving that step
    ///     a URL where it expects an authority. Not previously reported; closed by the same widening.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <strong>re-VALIDATE and re-BUILD are different questions, and the middle category is
    /// empty.</strong> Re-validation is unconditional — every gate in
    /// <see cref="WatchIterationPlan"/> is a pure function of the document and runs on every save
    /// whatever this digest says. Re-building is what this digest decides. There is no input that
    /// needs re-checking against the topology but not a rebuild, because every non-security argument
    /// <c>StartAsync</c> receives is an input to <c>Map</c>, and Map's decisions are baked into the
    /// topology it produced.
    /// </para>
    /// <para>
    /// <strong>The cost is bounded and is stated rather than argued away.</strong> The digest moves
    /// only when the SET OF TARGETED RESOURCE NAMES changes — not when a step's body, assertion,
    /// header or URL path changes. The common steps-only edit still reuses the kept topology. The
    /// set converges to a fixed point within a session, so the worst case is one extra rebuild per
    /// newly-targeted service.
    /// </para>
    /// </remarks>
    public static string ComputeTopologyFingerprint(TopologyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var material = request.ComputeFingerprintInput(ComputeEnvironmentHash(request.Environment));

        // THE BYTES ARE THE STRING'S UTF-16 CODE UNITS REINTERPRETED, NEVER TRANSCODED, and that
        // choice is what makes the length framing above worth having. MEASURED: an earlier form
        // used Encoding.UTF8.GetBytes, which substitutes U+FFFD for a lone surrogate — so
        // "a\uD800", "a\uD801" and the literal "a�" produce byte-identical output of
        // identical length. Framing separates values whose CHARACTERS differ; it cannot separate
        // values an encoder has already collapsed to the same characters, and a target name is
        // unconstrained author text that can carry a lone surrogate. An injective encoding fed to
        // a lossy transcoder is not injective.
        //
        // This is the SAME rule ComputeEnvironmentHash applies immediately above, and for a
        // stronger reason here: that method's input has already been through JsonSerializer, which
        // replaces an unpaired surrogate itself, so its choice buys uniformity. THIS input has not
        // — the target sets are raw declared strings — so here the choice buys injectivity, which
        // is exactly the argument SecuredTargets.IdentityOf makes about its own raw declared input.
        return Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(material.AsSpan())));
    }

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
    public static IScenarioIsolation BuildWatchIsolation(IKeptTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        return BuildIsolation(topology);
    }

    /// <summary>
    /// Runs an ALREADY-PLANNED scenario against an <strong>already-built</strong>
    /// <see cref="IKeptTopology"/> (the no-rebuild re-run seam for watch mode, S08-C-01):
    /// replay the build-time advisories → optionally reset+reseed → run → render. The topology is
    /// neither built nor disposed here — the watch session owns its lifetime so it survives across
    /// re-runs while the topology fingerprint is unchanged.
    /// </summary>
    /// <param name="topology">
    /// The kept topology to run against (built by the caller). Typed as the narrow
    /// <see cref="IKeptTopology"/> seam rather than the concrete <see cref="SuiteTopology"/>, which
    /// is what makes everything below assertable without Docker (#364).
    /// </param>
    /// <param name="isolation">
    /// The state-reset strategy applied <em>before</em> the run when
    /// <paramref name="resetAndReseed"/> is set, so a reuse run starts from a known-clean
    /// dependency state.  Pass <see cref="BuildWatchIsolation"/>'s result.
    /// </param>
    /// <param name="plan">
    /// The save's decided pre-topology state. It is a PRECONDITION that
    /// <see cref="WatchIterationPlan.IsRefused"/> is <see langword="false"/>: a refused save is
    /// rendered by <see cref="RenderWatchRefusal"/> and never reaches a topology at all, which is
    /// #370's whole subject. The plan also carries the run id, so one save mints one run id whether
    /// it is refused or executed.
    /// </param>
    /// <param name="registry">The frozen provider registry (drives the diff renderer).</param>
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
    /// <param name="sharedLedger">
    /// The watch SESSION's <see cref="ResolvedSecretLedger"/> (client-key-password EDGE-007), so a
    /// passphrase resolved by the caller's topology probe is scrubbable from text emitted on this
    /// scenario's step path, and vice versa. <see langword="null"/> — the default — is the
    /// pre-EDGE-007 shape: this method's own step accessor then gets a ledger private to it, and
    /// nothing the probe resolved can be recognised here.
    /// </param>
    /// <param name="cancellationToken">Propagated to all async operations.</param>
    /// <returns>The scenario's aggregate <see cref="Verdict"/>.</returns>
    /// <remarks>
    /// <para>
    /// <strong>The ledger parameter is additive and optional by design.</strong> Requiring it
    /// would force every existing call site to state a value, and defaulting it to a non-null
    /// ledger would silently change what an existing caller's scenario scrubs against. Defaulting
    /// to <see langword="null"/> leaves every existing caller byte-identical and makes the sharing
    /// an opt-in the <c>--watch</c> shell takes. Measured, so the rule is not mistaken for a
    /// compatibility promise: <c>Vouchfx.Engine.Runtime</c> is <c>IsPackable=false</c> and is not
    /// among the six packages the release workflow publishes, and it carries no golden gate over
    /// its public API. This is a design rule about in-tree churn, not an external contract.
    /// </para>
    /// <para>
    /// Re-validating and re-compiling on every re-run is deliberate: in watch mode the file
    /// changes between runs, so the kept topology may be re-used but the SCENARIO must be
    /// re-read from the latest save.  Only the topology build is skipped on reuse, never the
    /// compile. That work now happens in <see cref="WatchIterationPlan.Create"/>, BEFORE the
    /// reuse-vs-rebuild decision rather than after it (#370) — which is why this method takes a
    /// plan and no longer compiles anything itself.
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
    public static async Task<Verdict> RunPlannedScenarioAgainstKeptTopologyAsync(
        IKeptTopology topology,
        IScenarioIsolation isolation,
        WatchIterationPlan plan,
        StepKindRegistry registry,
        TextWriter output,
        bool resetAndReseed,
        // NO `seedBaseDirectory` PARAMETER. The plan already carries the suite directory it resolved
        // its own compile against (WatchIterationPlan.SeedBaseDirectory), and a parameter beside it
        // would be a second spelling of one value that nothing binds together: a caller passing a
        // different directory would seed from one root and have compiled `file:` fields against
        // another, silently. One value, read from the plan below.
        //
        // Ahead of cancellationToken, not after it: CA1068 requires the token to be last on an
        // externally-visible method. Every caller passes `cancellationToken:` by name, and a caller
        // that passed it positionally would fail to compile rather than mis-bind (the types do not
        // convert), so the insertion cannot silently change any call's meaning.
        ResolvedSecretLedger? sharedLedger = null,
        SecurityPathDisclosureLedger? sharedPathLedger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(isolation);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(output);

        if (plan.IsRefused)
        {
            // Not defensive tidiness: a refused plan carries no pipeline, so reaching the run below
            // would dereference null. Stating it as a precondition violation names the caller's
            // mistake — a refusal must be rendered by RenderWatchRefusal and must never reach a
            // topology, which is the ordering #370 exists to fix.
            throw new InvalidOperationException(
                "a refused WatchIterationPlan must be rendered through RenderWatchRefusal and must "
                + "never reach a topology; running one would defeat the pre-topology gate stack.");
        }

        var diffLookup = BuildDiffLookup(registry);
        var runId = plan.RunId;
        var ast = plan.Ast;
        var scenarioName = plan.ScenarioName;
        var buffer = new List<string>();

        // ── REQ-005: the declared-versus-observed confirmations (watch parity) ─
        // The same lines RunSuiteAsync prints once its topology is up, in the same position —
        // ahead of the run — because the reason REQ-005 gives named LEVELS instead of a boolean is
        // that an operator can tell a transport-only confirmation from an authenticated round trip.
        // A path that confirms the level and then never renders it defeats that reason: measured
        // before this, `--watch` showed a green run with no indication of which level was reached,
        // on both the build arm and the reuse arm.
        //
        // Printed on EVERY re-run, not once per topology, because each re-run's output block is
        // read on its own and a reader who cannot see the level cannot tell the two apart.
        //
        // n6 (peer review, fix round three): they are NOT re-measured per re-run, and they are
        // printed BEFORE the run rather than after it, so describing them as "a property of the
        // infrastructure this run just ran against" — as this comment previously did — over-claimed
        // their freshness twice over. They are the build-time probe's output, replayed. The
        // qualifying line below says so in the output itself rather than only here: a stale
        // confirmation is only misleading if nothing tells the reader it is stale, and a watch
        // session can hold one topology across many edits.
        if (topology.SecurityConfirmations.Count > 0)
        {
            await output.WriteLineAsync(
                    // The remedy clause names what actually rebuilds, which is wider than the
                    // 'environment' block it used to name: since #370 the trigger is every input
                    // the topology was built from, so changing which services the steps target
                    // rebuilds too. Naming only the environment block would send an author who had
                    // just retargeted a step looking for a change they had already made.
                    "security: confirmed once when this topology was built, and replayed here - "
                    + "the endpoints are not re-probed per re-run. Save a change to the "
                    + "'environment' block, or to which services the steps target, to rebuild the "
                    + "topology and re-confirm.")
                .ConfigureAwait(false);

            foreach (var confirmation in topology.SecurityConfirmations)
            {
                await output.WriteLineAsync(
                        DisplaySanitiser.SanitiseForDisplay(confirmation.ToString()))
                    .ConfigureAwait(false);
            }
        }

        // #348 (security review): replayed with the confirmations above and for the same reason —
        // this topology may be held across many edits, and the transport it selected does not
        // change until it is rebuilt.
        //
        // QUALIFIED IN THE OUTPUT, not merely here, on the same principle as the confirmations
        // line above: a stale statement is only misleading if nothing tells the reader it is
        // stale. It is worse for this one. The rebuild trigger is the TOPOLOGY FINGERPRINT — the
        // `environment` block plus every other input the topology was built from, including the set
        // of resource names the steps target (#370) — and a project's endpoints come from
        // `Properties/launchSettings.json`, which is not part of the YAML at all. Widening the
        // trigger did not narrow this: editing a launch profile still moves no input the fingerprint
        // covers, so an author can delete the https URL, save, and keep being told about a downgrade
        // that no longer happens, indefinitely.
        //
        // BOTH ADVISORIES, under the one qualifier, because the qualifier is true of both. An
        // author-declared `endpoint:` does live in the `environment` block and so does rebuild the
        // topology when edited — but the SCHEME that makes it a trust notice comes from the launch
        // profile, which does not, so a trust notice goes stale by exactly the same route. On an
        // https-only project, where the engine selected the listener and no `endpoint:` exists at
        // all, the notice depends on the launch profile and nothing else.
        if (topology.EndpointSelectionNotices.Count > 0 || topology.EndpointTrustNotices.Count > 0)
        {
            await output.WriteLineAsync(
                    "transport: selected once when this topology was built, and replayed here - "
                    + "endpoints are not re-read per re-run. A project's endpoints come from its "
                    + "launch profile, which is not part of the 'environment' block, so editing "
                    + "one does not rebuild the topology: restart --watch to re-select.")
                .ConfigureAwait(false);

            foreach (var notice in topology.EndpointSelectionNotices)
            {
                await output.WriteLineAsync(DisplaySanitiser.SanitiseForDisplay(notice.ToString()))
                    .ConfigureAwait(false);
            }

            foreach (var notice in topology.EndpointTrustNotices)
            {
                await output.WriteLineAsync(DisplaySanitiser.SanitiseForDisplay(notice.ToString()))
                    .ConfigureAwait(false);
            }

            // #450 / #453, with `replayed: true` — THE ONE PATH THAT SETS IT. The terminal
            // qualifier printed above says these advisories may be stale, and that qualification
            // cannot travel on an unqualified record: a consumer that could not tell a replay from
            // a fresh selection would read a downgrade that stopped happening several saves ago as
            // current. A replay against a kept topology therefore emits EVERY re-run, exactly as it
            // prints every re-run — distinguishable from the build that first raised it rather than
            // suppressed as a duplicate.
            //
            // NOT scrubbed and NOT sanitised, deliberately — the analysis is on
            // TransportNoticeEvents, the one producer.
            //
            // ADDED TO `buffer`, WHICH IS THIS PATH'S ONLY EVENT DESTINATION, and that is the whole
            // of what "emit" can mean here today: --events / --events-stream / --junit / --html are
            // deliberately not wired into watch mode (RunCommand.cs:376/650/661 say so, and #456
            // tracks that gap), so `buffer` goes to TerminalRenderer and nowhere else. The
            // environment-error emission in the reset-failure catch below does exactly the same
            // thing on this same path, through the same buffer, for the same reason.
            // The record is built, carries the run's id and reaches the run's event buffer;
            // when watch mode gains an event destination it arrives with the rest, and no second
            // producer has to be remembered then. TerminalRenderer ignores an unrecognised type
            // (its `default:` arm — the §14 forward-compatibility guarantee), so what an author
            // sees is unchanged.
            buffer.AddRange(TransportNoticeEvents.ToLines(
                topology.EndpointSelectionNotices,
                topology.EndpointTrustNotices,
                runId,
                DateTimeOffset.UtcNow,
                replayed: true));
        }

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
        //      skipped: there is no row-applied non-Postgres seed to restore, and the isolation
        //      reset in step 1 already cleared them ('sql' is the only seed kind in the v1
        //      language — see SeedSpec.cs's header remarks — so there is no broker/document seed
        //      to reconcile here either). A no-op when the scenario declares no seed.
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
                // Scrubbed through the SESSION ledger the caller supplies (EDGE-007). This line is
                // the reason that parameter exists: this is the `--watch` kept-topology entry
                // point, so nothing THIS method owns has resolved anything by here — the probe
                // ran in WatchRunner's own build seam, on an earlier save, and the step accessor
                // is built DOWNSTREAM in RunScenarioCoreAsync, after this reset. The only value
                // that can be in flight at this line is one the probe resolved on an earlier
                // save, against the topology this method has been handed — which is precisely
                // what a session-scoped ledger carries and a ledger scoped to the caller's build
                // seam does not reach. A null caller (every non-watch caller) keeps the
                // pre-EDGE-007 behaviour.
                buffer.Add(EnvironmentErrorLine(
                    sharedLedger, sharedPathLedger, oex.Info, runId, nowR));

                // Same ledger, same reason (EDGE-007) — and the same reset failure is now the
                // scenario's own recorded cause, not only an environment-error record beside it.
                buffer.Add(StepEventBuilder.ScenarioCompletedLine(
                    runId,
                    nowR,
                    scenarioName,
                    Verdict.EnvironmentError,
                    new VerdictCounts { EnvError = 1 },
                    sharedLedger,
                    sharedPathLedger,
                    oex.Message));
                TerminalRenderer.Render(buffer, output, diffLookup);
                return Verdict.EnvironmentError;
            }
        }

        // ── Run against the kept topology ─────────────────────────────────────
        // No compile door here any more. Everything that could refuse this save ran in
        // WatchIterationPlan.Create, BEFORE the reuse-vs-rebuild decision — so a refused save never
        // reaches this method and, on the first save of a session, never reaches a container (#370).
        var pipeline = plan.Pipeline!;
        var verdict = await RunScenarioAgainstTopologyAsync(
            ast,
            scenarioName,
            runId,
            topology,
            pipeline.Assembled!,
            pipeline.CompileReferencePaths,
            pipeline.HostResourcePlan,
            buffer,
            new NullScenarioIsolation(), // isolation reset handled above.
            output,
            plan.SeedBaseDirectory,
            cancellationToken,
            // Named, because the two parameters between here and it (scriptBaseDirectory,
            // livePump) are both optional and both stay at their defaults on this path.
            sharedLedger: sharedLedger,
            sharedPathLedger: sharedPathLedger).ConfigureAwait(false);

        TerminalRenderer.Render(buffer, output, diffLookup);
        return verdict;
    }

    /// <summary>
    /// Renders a REFUSED <see cref="WatchIterationPlan"/>: the sanitised diagnostic, then the
    /// refusal's own event pair through <see cref="TerminalRenderer"/> — byte for byte what a
    /// refused save produced when the doors ran after the topology was up (#370).
    /// </summary>
    /// <param name="plan">The refused plan. Refusing is a precondition, not a branch.</param>
    /// <param name="registry">The frozen provider registry (drives the diff renderer).</param>
    /// <param name="output">The writer that receives the diagnostic and the rendered report.</param>
    /// <remarks>
    /// <para>
    /// <strong>SANITISED, NOT SCRUBBED — considered under EDGE-007 and deliberately left so.</strong>
    /// </para>
    /// <para>
    /// BE EXACT ABOUT WHY, because the tempting reason is false on this path. It is NOT that nothing
    /// has resolved yet: under <c>--watch</c> the probe resolved <c>clientKeyPassword</c> at
    /// topology-start time on an EARLIER save, so when this line runs on save N the session ledger
    /// is already non-empty. That is the very error EDGE-007 corrected one sink over — the retracted
    /// comment there reasoned from what the METHOD had done rather than from what the PATH had done.
    /// </para>
    /// <para>
    /// The true reason is narrower and is a property of the TEXT, not of the timing:
    /// <see cref="WatchIterationPlan.Diagnostic"/> cannot CONTAIN a resolved value. It has exactly
    /// three sources — schema validation errors, <c>TryValidateSecretReferences</c>'s message, and
    /// <c>ProviderPipeline</c>'s compile failure — and all three are produced before any step
    /// executes. The move that #370 makes STRENGTHENS this rather than weakening it: on the first
    /// save of a session the doors now run before any probe has run at all.
    /// </para>
    /// <para>
    /// "ALL THREE COME FROM YAML TEXT" WAS THE PREMISE AND IS NO LONGER EXACTLY TRUE (issue #413),
    /// so it is restated rather than left to decay. The third source now also carries a
    /// PROVIDER-authored exception message: a throwing <c>Bind</c> is reported as
    /// <c>{ExceptionType}: {Message}</c>, and that text is the provider's, not the document's. The
    /// CONCLUSION is unchanged, and for a stronger reason than the old premise gave — <c>Bind</c>
    /// runs at COMPILE time, before any topology and before any secret is resolved, and the v1
    /// <c>IStepBinder&lt;T&gt;</c> contract hands it a YamlNode and an IBindingContext and no secret
    /// accessor at all. There is no resolved value in scope for that message to contain. Sanitising
    /// still applies to it, and for the same reason it applies to the other two: it is untrusted
    /// text reaching a terminal.
    /// </para>
    /// <para>
    /// Not scrubbed as belt-and-braces either, and that is a judgement rather than an oversight.
    /// This is the author's primary feedback channel under <c>--watch</c>: it is the message telling
    /// them what is wrong with the YAML they just saved. Scrubbing it would expose exactly that
    /// message to the over-redaction the session-scoped ledger makes possible (see
    /// <c>WatchRunner</c>'s cost note — a short or stale recorded value rewrites unrelated
    /// substrings for the rest of the session), corrupting the diagnostic the author needs to act
    /// on. It would also diverge from <see cref="RunSuiteAsync"/>'s identical early-exit sink, which
    /// this deliberately matches byte for byte; the two must not drift apart on a judgement only one
    /// of them records.
    /// </para>
    /// <para>
    /// The <c>EveryEnvironmentErrorEmission_</c> gate covers <c>EnvironmentErrorLine</c> and does NOT
    /// reach here, which is why the reasoning lives at the site.
    /// </para>
    /// </remarks>
    public static void RenderWatchRefusal(
        WatchIterationPlan plan, StepKindRegistry registry, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(output);

        if (!plan.IsRefused)
        {
            throw new InvalidOperationException(
                "RenderWatchRefusal was handed a plan that was not refused; a plan that passed "
                + "every pre-topology gate must be RUN, not rendered as a refusal.");
        }

        if (!string.IsNullOrEmpty(plan.Diagnostic))
        {
            // Issue #266, Item 4: the diagnostic may echo untrusted YAML content verbatim.
            output.WriteLine(DisplaySanitiser.SanitiseForDisplay(plan.Diagnostic));
        }

        // SYNCHRONOUS, to match TerminalRenderer.Render beside it and the compile seam that calls
        // this: WatchSession's compile seam is a `Func<string, WatchCompileResult>`, and making it
        // async to accommodate one WriteLineAsync would push a Task through the reuse decision for
        // no gain. The run path's own early-exit sink is async only because the method around it is.
        TerminalRenderer.Render(plan.EventLines, output, BuildDiffLookup(registry));
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
        IKeptTopology suite,
        AssembledScript assembled,
        IReadOnlyList<string> compileReferencePaths,
        IReadOnlyList<HostResourcePlanEntry> hostResourcePlan,
        List<string> buffer,
        IScenarioIsolation isolation,
        TextWriter output,
        string? seedBaseDirectory,
        CancellationToken cancellationToken,
        string? scriptBaseDirectory = null,
        LiveEventPump? livePump = null,
        ResolvedSecretLedger? sharedLedger = null,
        SecurityPathDisclosureLedger? sharedPathLedger = null)
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
                livePump: livePump,
                sharedLedger: sharedLedger,
                sharedPathLedger: sharedPathLedger).ConfigureAwait(false);
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
    /// <param name="sharedLedger">
    /// The RUN's <see cref="ResolvedSecretLedger"/> (client-key-password REQ-010), so this
    /// scenario's step accessor records into the same net the topology probe recorded into.
    /// On the <c>--watch</c> path it is the SESSION's ledger, threaded in from
    /// <c>WatchRunner</c> through <see cref="RunPlannedScenarioAgainstKeptTopologyAsync"/> (EDGE-007).
    /// <see langword="null"/> gives the accessor a ledger private to this scenario — the
    /// pre-REQ-010 shape. No PRODUCTION caller now takes it; two do
    /// (<c>KafkaServiceTargetDockerTests</c> and <c>KafkaStepTimeoutBoundDockerTests</c> call the
    /// kept-topology entry point without a ledger), so the null branch is live and not dead code.
    /// </param>
    private static async Task<Verdict> RunScenarioCoreAsync(
        ScenarioAst ast,
        string scenarioName,
        string runId,
        IKeptTopology suite,
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
        LiveEventPump? livePump = null,
        ResolvedSecretLedger? sharedLedger = null,
        SecurityPathDisclosureLedger? sharedPathLedger = null)
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
        // HttpClient).  The scope owns them, and the finally below disposes it at scenario
        // end — no HttpClient leaks across the per-scenario boundary, and no static handle
        // holds the connection open.
        //
        // The SCOPE is per-scenario (it owns the resolvers); the LEDGER it records into is the
        // RUN's, when the caller supplies one (client-key-password REQ-010). That is what makes a
        // passphrase resolved by the topology probe scrubbable from THIS scenario's observations,
        // and a passphrase resolved here scrubbable from an environment-error line the suite loop
        // emits after this scenario returns.
        var secretScope = CreateSecretAccessorScope(sharedLedger);
        var secretAccessor = secretScope.Accessor;

        // ── Per-target client security configuration (REQ-014) ────────────────
        // Built here, in the Default ALC, from this scenario's OWN declared `security`
        // blocks, and passed into the boundary BY REFERENCE — exactly like the secret
        // accessor above.
        //
        // The base directory is `scriptBaseDirectory ?? seedBaseDirectory` — the SAME
        // expression BuildReproducibilityEnvelope already uses for the same reason, and it must
        // stay that way, because the ONE invariant is "whatever this scenario's
        // ProviderPipeline.Compile was handed", and the two run paths spell that differently:
        //
        //   • RunSuiteAsync (multi-suite) compiles against the per-scenario
        //     scenarioBaseDirectory and threads it here as scriptBaseDirectory (#268).
        //   • RunAsync and the watch/kept-topology path compile against seedBaseDirectory and
        //     never pass scriptBaseDirectory at all, so it arrives null.
        //
        // Both halves are load-bearing, and each was MEASURED to invert the trust decision on
        // its own path. Using seedBaseDirectory alone: a second suite's `caCert: certs/ca.pem`
        // validated against its own directory and then loaded the FIRST suite's file — the run
        // rejected the anchor it declared and accepted one it never named. Using
        // scriptBaseDirectory alone: on the single-scenario paths the null falls back to
        // Directory.GetCurrentDirectory(), so the anchor is read from wherever the process
        // happens to be running rather than from the suite directory the validator checked.
        // The accessor owns the X509Certificate2 instances
        // it loads (lazily, only for a target some step actually resolves) and is disposed in
        // the same finally as the secret resolvers below; a scenario declaring no `security`
        // block gets the shared Null accessor and allocates nothing.
        //
        // Nothing here is written to Vars, deliberately and by construction: this accessor is
        // reachable ONLY as globals.Security. A certificate or key path in Vars would reach
        // the reported and §14 event surface past the SecretString redaction model (REQ-014).
        //
        // Constructed INSIDE the try, not before it: the finally below is the one place that
        // disposes both the secret resolvers and this accessor, so anything that can throw
        // during construction must be inside its scope or a failure here leaks the resolvers.
        ISecurityConfigurationAccessor securityAccessor = NullSecurityConfigurationAccessor.Instance;
        try
        {
            // The scenario's OWN accessor is handed the scenario's OWN secret accessor (REQ-009),
            // so a `clientKeyPassword` resolved for a step is recorded in exactly the
            // ResolvedSecrets ledger the runner's diagnostic and observation scrubbers read.
            // REQ-010 extends that to the probe: when the caller supplied a shared ledger, the
            // probe's separately-scoped accessor records into this same net, so neither path's
            // resolved value can escape through the other's emitted text.
            securityAccessor = SecurityConfigurationAccessor.Build(
                ast, scriptBaseDirectory ?? seedBaseDirectory, secretAccessor, sharedPathLedger);

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
                : new LiveStepEventSink(
                    livePump, runId, ast.Steps, captureOriginMap, secretAccessor, sharedPathLedger);

            var globals = new ScriptGlobalVariables(
                vars,
                suite.DiscoveredServices,
                secretAccessor,
                webhookAccessor,
                traceAccessor,
                liveSink,
                securityAccessor);

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

                // The record carries the SAME reference-only text the terminal gets (#372),
                // built once. It is reference-only by construction, so the ledger pass over it
                // finds nothing — but the ledger is still handed in, because "this particular
                // text is safe" is not a property a future edit to the format preserves.
                var secretFault =
                    "Secret resolution failed (EnvironmentError): " +
                    $"source '{sre.SecretSource}', path '{sre.SecretPath}'.";
                var seCompletedLine = StepEventBuilder.ScenarioCompletedLine(
                    runId,
                    nowSE,
                    scenarioName,
                    Verdict.EnvironmentError,
                    new VerdictCounts { EnvError = 1 },
                    LedgerOf(secretAccessor),
                    sharedPathLedger,
                    secretFault);
                buffer.Add(seCompletedLine);
                // Issue #262: the matching scenario-started was already posted live at the top
                // of this method; post the completion now so a live tail never sees an orphan.
                livePump?.Post(seCompletedLine);

                await output.WriteLineAsync(
                    DisplaySanitiser.SanitiseForDisplay(secretFault))
                    .ConfigureAwait(false);

                return Verdict.EnvironmentError;
            }
            catch (Exception ex)
            {
                var nowCE = DateTimeOffset.UtcNow;
                buffer.Add(StepEventBuilder.ScenarioStartedLine(runId, nowCE, scenarioName));

                // Built BEFORE the emit, not after it, so the record can carry it (#372). A CSX
                // compilation failure is the scenario's cause and belongs in --junit/--html/
                // --events, not only on the terminal.
                var diagnosis = ex is ScriptCompilationException sce
                    ? $"CSX compilation failed: {sce.Message}"
                    : $"{ex.GetType().Name}: {ex.Message}";

                // The ledger matters HERE more than anywhere: `ex.Message` is the one free-form
                // surface a script.csharp body can interpolate a Reveal()ed value into, and this
                // text is now written to disk. The terminal write below composes the same scrub
                // through ScrubDiagnostic; both read the accessor's own ledger.
                var ceCompletedLine = StepEventBuilder.ScenarioCompletedLine(
                    runId,
                    nowCE,
                    scenarioName,
                    Verdict.Inconclusive,
                    new VerdictCounts { Inconclusive = 1 },
                    LedgerOf(secretAccessor),
                    sharedPathLedger,
                    diagnosis);
                buffer.Add(ceCompletedLine);
                livePump?.Post(ceCompletedLine);

                // §17 defence-in-depth (S11-B-01): a secret value resolved during execution
                // can land verbatim in an exception MESSAGE (e.g. the script.csharp body throws
                // with an interpolated Reveal()).  This diagnostic is written to the HUMAN output
                // stream (the developer terminal / CI log) — an exfiltration surface every bit as
                // real as the event stream — so scrub it through the SAME ledger the observation
                // path uses before it leaves the engine.  Type-based redaction stays primary.
                //
                // Issue #266, Item 4: composed with DisplaySanitiser.SanitiseForDisplay so ALL
                // nets run on this write — ScrubDiagnostic redacts resolved secret VALUES and
                // substitutes the declared form over any resolved security-material path
                // (issue #375), then SanitiseForDisplay strips control characters / neutralises
                // ANSI escape sequences the (already-scrubbed) text might still carry (e.g. from
                // a hostile step id or an author exception message), before any of it ever
                // reaches the terminal/CI log.
                var scrubbedDiagnosis = ScrubDiagnostic(secretAccessor, sharedPathLedger, diagnosis);
                await output.WriteLineAsync(
                    "Compile/run error (Inconclusive): "
                    + DisplaySanitiser.SanitiseForDisplay(scrubbedDiagnosis))
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
                buffer.AddRange(BuildAttemptEventLines(
                    runId, now9, node.Id, vars, secretAccessor, sharedPathLedger));

                // Issue #262: StepEventBuilder.StepCompletedLine is the SAME method
                // LiveStepEventSink.OnStepCompleted calls in real time — this call reproduces,
                // byte-for-byte, the pre-#262 inline Captured/Substitutions/Observation
                // construction.
                var stepCompletedLine = StepEventBuilder.StepCompletedLine(
                    runId, now9, node, outcome, captureStatusRaw, captureOriginMap, secretAccessor,
                    sharedPathLedger);
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

            // No message: a scenario that RAN has its cause in its step records, and a bare
            // `message` repeating "one step failed" would add nothing an artefact cannot already
            // read off the stream. The scenario-level field is for causes that exist only at
            // scenario level — a refusal, an abort, a topology that never came up.
            var scenarioCompletedLine = StepEventBuilder.ScenarioCompletedLine(
                runId,
                DateTimeOffset.UtcNow,
                scenarioName,
                aggregate,
                finalCounts,
                LedgerOf(secretAccessor),
                sharedPathLedger,
                message: null);
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
            // Disposes any resolver that owns disposable state (the Vault resolver's
            // client owns an HttpClient).  Runs on EVERY exit path — normal completion,
            // the EnvironmentError/Inconclusive early returns above, and any unexpected
            // throw — so no HttpClient leaks across the per-scenario boundary (§5).
            secretScope.Dispose();

            // Same contract for the security accessor's loaded certificates (REQ-014): each
            // X509Certificate2 wraps an OS key handle, and on Windows a PKCS#12 re-import
            // materialises a key container that only Dispose releases. The Null accessor
            // (no `security` block declared, the common path) is not IDisposable, so this
            // costs an unsecured run one type test.
            (securityAccessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Builds the appropriate <see cref="IScenarioIsolation"/> for the given
    /// <paramref name="topology"/> via <c>ScenarioIsolationFactory.Create</c>: proper
    /// name+type dispatch over <see cref="IKeptTopology.DependencyNames"/>,
    /// <see cref="IKeptTopology.DependencyTypes"/>, and
    /// <see cref="IKeptTopology.DiscoveredServices"/> — resetting EVERY resettable
    /// dependency the topology declares (composed when there is more than one),
    /// rather than sniffing the shape of a discovered connection string.
    /// </summary>
    private static IScenarioIsolation BuildIsolation(IKeptTopology topology) =>
        ScenarioIsolationFactory.Create(
            topology.DependencyNames,
            topology.DependencyTypes,
            topology.DiscoveredServices);

    /// <summary>
    /// <see cref="JsonSerializerOptions"/> used by <see cref="SerialiseEnvironment"/>.
    /// Allocated once (static initialiser) to avoid repeated reflection overhead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registers <see cref="YamlNodeJsonConverter"/> so that a <see cref="YamlMappingNode"/> in the
    /// serialised graph becomes a deterministic JSON object rather than throwing
    /// <see cref="InvalidOperationException"/> (S11-B-02).
    /// </para>
    /// <para>
    /// <strong>TWO structurally independent properties depend on this registration, not one.</strong>
    /// <see cref="DependencySpec.Extra"/> is the one it was added for (S11-B-02);
    /// <see cref="SecuritySpec.Extra"/> (#353) is the second, reached through a service's or
    /// dependency's <c>security</c> block instead. Narrowing this registration to the dependency
    /// path — a plausible cleanup while the prose named only that property — would throw on any
    /// caller that serialises an environment the schema has not closed over, and the parser is
    /// lenient exactly where the schema is closed.
    /// <para>
    /// <strong><c>--watch</c> IS STILL SUCH A CALLER, and #370 did not change that.</strong> An
    /// earlier revision of this note claimed the watch loop "now validates first"; that is false,
    /// measured at <c>WatchIterationPlan.Create</c>, which computes the topology fingerprint —
    /// and therefore <see cref="SerialiseEnvironment"/> — BEFORE its schema door runs, deliberately,
    /// so that a refused save still carries a fingerprint. What #370 moved ahead of the TOPOLOGY
    /// BUILD it did not move ahead of this serialisation, and the two are independent orderings. So
    /// a document carrying an unbound <c>security</c> key still reaches this serialiser on a shipped
    /// CLI path, exactly as it did before, and a throw here would surface as a spurious failure on a
    /// document the engine was about to refuse for a correctly-stated reason.
    /// <c>EnvironmentHashTests</c> states the same thing from the test side.
    /// </para>
    /// </para>
    /// </remarks>
    private static readonly JsonSerializerOptions s_envSerialiserOptions =
        new() { Converters = { new YamlNodeJsonConverter() } };

    /// <summary>
    /// Serialises an <see cref="EnvironmentSpec"/> to a stable JSON string for
    /// equality comparison across suite scenarios (shared-environment validation).
    /// Returns an empty string for a <see langword="null"/> environment.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="s_envSerialiserOptions"/>, which registers
    /// <see cref="YamlNodeJsonConverter"/> — required because a <see cref="YamlMappingNode"/>
    /// is a type <see cref="JsonSerializer"/> cannot handle without a custom converter
    /// (S11-B-02), and TWO properties in this graph are one: <see cref="DependencySpec.Extra"/>
    /// and <see cref="SecuritySpec.Extra"/> (#353). Mapping keys within any such node are
    /// emitted in ordinal sort order, so two Extra mappings that contain the same pairs in different
    /// declaration order produce identical JSON.  Top-level <c>Services</c> and
    /// <c>Dependencies</c> collections are serialised by STJ in enumeration order and
    /// retain their YAML declaration order (which is stable: it comes from the same parsed
    /// YAML).
    /// </remarks>
    private static string SerialiseEnvironment(EnvironmentSpec? env) =>
        env is null
            ? string.Empty
            : JsonSerializer.Serialize(env, s_envSerialiserOptions);

    /// <summary>
    /// The canonical JSON for an environment's <c>seed</c> section alone — the seed-root guard's
    /// comparison, and nothing wider.
    /// </summary>
    /// <remarks>
    /// Shares <see cref="s_envSerialiserOptions"/> with <see cref="SerialiseEnvironment"/> so the
    /// two cannot canonicalise the same sub-graph differently: a seed section that compares equal
    /// here is a seed section that contributes an identical substring there. A <see langword="null"/>
    /// seed (and a null environment) yields the empty string, so two suites that declare no seed
    /// compare equal — which is right, and is also unreachable from the one call site, whose
    /// preceding clause already requires the baseline's seed to be non-null.
    /// </remarks>
    private static string SerialiseSeed(EnvironmentSpec? env) =>
        env?.Seed is null
            ? string.Empty
            : JsonSerializer.Serialize(env.Seed, s_envSerialiserOptions);

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
        // BOTH REQUIRED, and `secretAccessor` lost its default to get there. C# requires optional
        // parameters to be trailing, so a required `pathLedger` could not sit after an optional
        // `secretAccessor`; the first attempt at this fix left `pathLedger` optional instead and
        // justified it as "bounded — exactly one production caller passes it".
        //
        // THAT JUSTIFICATION WAS A COUNT, NOT A CONSTRUCTION, and this repository has already paid
        // for the difference once: #364's `securityConfiguration` was optional, had one production
        // caller too, and a SECOND caller (WatchRunner) was added without it — compiling cleanly
        // and leaving every secured suite unrunnable under `--watch`. A default is exactly the
        // shape that lets the next caller omit the net in silence. The two test callers that
        // relied on the old default now pass `null` explicitly, which is the point: writing it
        // is a decision, inheriting it is not.
        ISecretAccessor? secretAccessor,
        SecurityPathDisclosureLedger? pathLedger)
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
            lines.Add(StepEventBuilder.StepAttemptLine(
                runId, timestamp, stepId, a, secretAccessor, pathLedger));
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
    internal static JsonElement? BuildStepObservation(
        ISecretAccessor accessor,
        SecurityPathDisclosureLedger? pathLedger,
        string? rawObservation)
        => ParseObservation(ScrubDiagnostic(accessor, pathLedger, rawObservation));

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
    internal static string? ScrubDiagnostic(
        ISecretAccessor accessor, SecurityPathDisclosureLedger? pathLedger, string? text)
    {
        var scrubbed = accessor is SecretAccessor concrete
            ? concrete.ResolvedSecrets.Scrub(text)
            : text;

        // The SECOND net (issue #375): a resolved security-material path this engine handed to a
        // client library whose diagnostics it does not write. The value ledger above cannot see
        // it — a path is not a resolved secret and is never recorded there — and the substitution
        // is the field's own DECLARED text rather than a redaction marker, which is why it is a
        // separate ledger rather than a second use of the first.
        //
        // THE ORDER IS SECRETS FIRST, AND IT IS LOAD-BEARING. THIS IS THE ONE PLACE THAT ARGUMENT
        // IS WRITTEN OUT; every other composition of the two nets in this engine points here.
        //
        // Both nets replace EXACT occurrences, so whichever runs first can destroy the other's
        // match. The two orders therefore fail differently, and the choice is which failure to
        // accept:
        //
        //   SECRETS FIRST (what this does). If a recorded secret VALUE happens to occur inside a
        //   recorded resolved PATH — a passphrase that is also a directory name is the realistic
        //   shape, and an author who names a folder after the credential in it has done nothing
        //   this engine forbids — the value scrub rewrites the middle of the path, so the path
        //   ledger no longer finds its recorded form and the remainder survives:
        //   `C:\work\[REDACTED]\ca.pem`. That is a partial HOST-LAYOUT disclosure. It is the
        //   failure this order accepts.
        //
        //   PATHS FIRST. If a recorded resolved PATH occurs inside a recorded secret VALUE — any
        //   credential that embeds a file location, a connection string with a certificate path
        //   in it, a JSON blob a Vault resolver returned whole — the path substitution rewrites
        //   the secret's interior, the value ledger's exact match then misses, and what reaches
        //   the archived event stream is a MANGLED SECRET rather than a redacted one. Most of the
        //   value is still there and is still readable.
        //
        // A partially-redacted path discloses where a file lives on a build agent. A mangled
        // secret discloses most of a credential. The second is strictly worse, so the order is
        // fixed at secrets-first and must not be reversed without a reason that outweighs that.
        //
        // WHAT WOULD ACTUALLY REMOVE THE TRADE-OFF, stated so nobody mistakes this for a defence
        // of the current shape: one pass that considers both nets' recorded forms together,
        // longest-first, and never re-scans text it has already replaced. That is a change to how
        // the two ledgers compose, not to their order, and it is out of scope here.
        return pathLedger?.Scrub(scrubbed) ?? scrubbed;
    }

    /// <summary>
    /// The <see cref="ResolvedSecretLedger"/> <paramref name="accessor"/> records into, or
    /// <see langword="null"/> for an accessor that records nothing.
    /// </summary>
    /// <remarks>
    /// Read from the ACCESSOR rather than from the <c>sharedLedger</c> parameter its scope was
    /// built with, and the difference is load-bearing: a scope built without a shared ledger is
    /// given one PRIVATE to itself, so on every path where the caller supplies none the accessor
    /// still accumulates values that a stamped cause must be scrubbed against. Reading the
    /// parameter would scrub against nothing on exactly those paths.
    /// </remarks>
    private static ResolvedSecretLedger? LedgerOf(ISecretAccessor accessor)
        => accessor is SecretAccessor concrete ? concrete.ResolvedSecrets : null;

    /// <summary>
    /// Builds the §14 <c>environment-error</c> event line for <paramref name="info"/>, scrubbing
    /// its free-form text through <paramref name="sharedLedger"/> first (client-key-password
    /// REQ-010).  <strong>The single place this assembly may call
    /// <see cref="EnvironmentErrorEvents.ToLine"/> from.</strong>
    /// </summary>
    /// <param name="sharedLedger">
    /// The run's shared ledger of revealed secret values, or <see langword="null"/> on a path
    /// that owns no ledger (nothing has been resolved that this path could scrub).
    /// </param>
    /// <param name="info">The classified orchestration failure.</param>
    /// <param name="runId">The identifier of the current engine run.</param>
    /// <param name="timestamp">The emission timestamp, supplied by the caller for determinism.</param>
    /// <returns>The compact JSON Lines string for the environment-error event.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Why this exists at all — measured, T4 security review, 2026-08-12.</strong> The
    /// probe's failure text reaches the event stream through
    /// <see cref="EnvironmentErrorEvents.ToLine"/>, and NOTHING on that path called
    /// <see cref="ScrubDiagnostic"/>: that scrub sits on the step-observation and diagnosis
    /// writes only.  So sharing one ledger between the probe and step scopes does not, on its
    /// own, make a probe-time passphrase scrubbable — there has to be something on the probe's
    /// emission path to do the scrubbing.  This is it.
    /// </para>
    /// <para>
    /// <strong>WHICH members are scrubbed, and why only those.</strong> Only
    /// <see cref="OrchestrationErrorInfo.Detail"/>.  It is the sole free-form member: every
    /// construction site in <c>Vouchfx.Engine.Orchestration</c> folds an underlying exception
    /// message into it (<c>OrchestrationErrorClassifier.BuildDetail</c>, <c>SeedApplier</c>,
    /// <c>ScenarioIsolationErrors</c>, and — the case REQ-010 exists for —
    /// <c>SecuredEndpointProbe</c>, which splices a <c>SecurityMaterialException.Message</c>
    /// verbatim).  <c>ResourceName</c> is a declared service/dependency name or an engine
    /// literal, <c>RegistryHost</c> is parsed out of an image reference, and <c>AuthStatus</c>
    /// is one of a closed set of engine tokens — none can carry a resolved secret value, and
    /// scrubbing a declared NAME would corrupt the diagnosis for no gain.
    /// </para>
    /// <para>
    /// <strong>Why the scrub is here and not in <see cref="EnvironmentErrorEvents"/>.</strong>
    /// That factory lives in <c>Vouchfx.Engine.Orchestration</c>, which does not (and must not)
    /// reference the secret subsystem.  The chokepoint therefore has to be Runtime-local.
    /// </para>
    /// <para>
    /// <strong>The ledger's structural limit, and where the OTHER guard lives.</strong>
    /// <c>SecretAccessor.Resolve</c> records into the ledger only after a SUCCESSFUL resolve.
    /// Every diagnostic raised INSTEAD of a resolve — a malformed reference, a null accessor, a
    /// resolution failure, a passphrase declared against an unencrypted key — fires with the
    /// ledger holding nothing for it, and this scrub can never redact it.  Redaction on those
    /// paths comes from the throw sites not echoing the value in the first place (T4's
    /// don't-echo guards in <c>SecurityConfigurationAccessor</c>), not from here.  A reader who
    /// believes this ledger covers everything will site the next guard in the wrong place.
    /// </para>
    /// <para>
    /// It changes event FIELD CONTENT only — never a property name, CLR type or
    /// <c>[JsonPropertyName]</c>.
    /// </para>
    /// <para>
    /// <strong>The scrub redacts EXACT occurrences, so any transform of a recorded value
    /// defeats it — including the engine's own.</strong> Six upstream sites transform the text
    /// that becomes <see cref="OrchestrationErrorInfo.Detail"/>.  Three TRUNCATE:
    /// <c>OrchestrationErrorClassifier</c> (256 characters), <c>ScenarioIsolationErrors.TrimDetail</c>
    /// (200) and <c>SecuredEndpointProbe.Summarise</c> (200).  A recorded value straddling one of
    /// those caps arrives here as a PREFIX of itself, which no recorded form matches, and survives.
    /// It is not reachable on the REQ-010 probe path — <c>SecuredEndpointProbe.Failure</c> builds
    /// its <c>Detail</c> without truncating.
    /// </para>
    /// <para>
    /// <strong>Three more arrived with the #420 DCP flight recorder, and one of them is a
    /// different SHAPE of defeat.</strong>  This warning is a live inventory, not decoration, so
    /// they are named here rather than left for the next reader to rediscover:
    /// <list type="bullet">
    ///   <item><c>DcpFlightRecorder.TailLineChars</c> (120) truncates each captured warning line
    ///   before it is charged against the tail budget.</item>
    ///   <item><c>DcpCapture.MaxAnnexLength</c> truncates the composed annex — note, remedy,
    ///   location and tail together — appended to the classifier's already-truncated detail.  It
    ///   is sized to clear the measured worst case with headroom, and a test recomputes that
    ///   worst case from the constants, so today this cap does not fire on any composition the
    ///   engine can build; it is listed because it is still a truncation and a future part would
    ///   bring it back into play.  No figure is quoted here — it moved once already.</item>
    ///   <item><c>DcpFlightEntry.Create</c> MUTATES rather than truncates: every character
    ///   outside printable ASCII becomes <c>?</c> (issue #379's console-codepage rule, applied at
    ///   capture time).  A recorded value carrying even one non-ASCII byte therefore reaches this
    ///   ledger at FULL LENGTH but altered, so the exact match misses and the value survives —
    ///   a truncation cap is not what has to be straddled for this one to fire.</item>
    /// </list>
    /// <strong>They defeat the <see cref="SecurityPathDisclosureLedger"/> the same way, and that
    /// half is pinned rather than argued.</strong>  This method runs BOTH ledgers, and the path
    /// ledger's whole job is substituting declared text back over resolved security-material
    /// paths — while paths are exactly the shape a DCP tail line carries.  A DECLARED path
    /// straddling <c>TailLineChars</c>, or carrying one non-ASCII character, reaches the ledger in
    /// a form no recorded value matches and survives as an absolute host path.  Both directions
    /// are exercised on the real emission path by
    /// <c>SecretObservationLeakPenetrationTests.DcpCaptureTail_CarryingADeclaredSecurityPath_IsSubstitutedByThePathLedger</c>
    /// and its <c>…_CarryingANonAsciiDeclaredPath_DefeatsThePathLedgerToo</c> twin.
    /// </para>
    /// <para>
    /// An earlier version of this paragraph cited a real capture's
    /// <c>…\AppData\Local\Temp\aspire-dcp…\kubeconfig</c> line as the evidence.  That was the
    /// wrong evidence for this claim and is removed: this ledger records DECLARED security
    /// material only, so a DCP temp path was never in it and could not have been missed by it.
    /// The kubeconfig line is a genuine host-path disclosure, but it is one NEITHER ledger covers,
    /// and it is recorded where it belongs — in <c>DcpCapture.DescribeLocation</c>'s remarks —
    /// rather than as a defeat of a ledger it never reached.
    /// </para>
    /// <para>
    /// Naming only the secret ledger above would still understate the inventory by half.
    /// What bounds the exposure is not this scrub but what the tail carries: warning-level DCP
    /// lines from a topology that failed to come up, on an engine whose
    /// <c>EnvironmentMapper</c> refuses the <c>${secret:</c> sigil outright in both
    /// <c>services[].env</c> and <c>dependencies[].env</c>, so no resolved secret reaches a
    /// container specification for that layer to log in the first place.  The tail is documented
    /// as best-effort-scrubbed for exactly this reason — see <c>DcpCapture.BuildSummary</c>.
    /// A new truncation, a new mutation, or a new caller routing transformed text here would
    /// reopen the general hole silently.
    /// </para>
    /// </remarks>
    internal static string EnvironmentErrorLine(
        ResolvedSecretLedger? sharedLedger,
        SecurityPathDisclosureLedger? sharedPathLedger,
        OrchestrationErrorInfo info,
        string runId,
        DateTimeOffset timestamp)
    {
        if (sharedLedger is null && sharedPathLedger is null)
        {
            return EnvironmentErrorEvents.ToLine(info, runId, timestamp);
        }

        // `with` rather than mutation: OrchestrationErrorInfo is a record and the caller's
        // instance is also carried on the exception it came from (and re-read by REQ-018's
        // Kind check), so the scrubbed copy must be local to this line.
        //
        // TWO NETS, SECRETS FIRST (issue #375). The value ledger redacts revealed secret values;
        // the path ledger substitutes the DECLARED text back over a resolved security-material
        // path a third-party client library quoted. THE ORDER IS LOAD-BEARING and must match
        // every other composition of the pair: see `ScrubDiagnostic`, which argues it once — in
        // short, whichever net runs first can destroy the other's exact match, and the failure
        // secrets-first accepts (a partially-redacted path) is strictly less bad than the one
        // paths-first accepts (a mangled, still-readable secret).
        var scrubbed = sharedLedger?.Scrub(info.Detail) ?? info.Detail;
        scrubbed = sharedPathLedger?.Scrub(scrubbed) ?? scrubbed;
        return EnvironmentErrorEvents.ToLine(
            info with { Detail = scrubbed }, runId, timestamp);
    }

    /// <summary>
    /// Runs the central secret-reference validation pass over every substitutable
    /// field of every step in <paramref name="ast"/> (§17, S05-B-01), and over the one
    /// secret-bearing field outside <c>steps</c> — every declared
    /// <c>security.clientKeyPassword</c> (client-key-password EDGE-003, #387).
    /// </summary>
    /// <param name="ast">The parsed scenario to validate.</param>
    /// <param name="error">
    /// On failure, an actionable British-English message; otherwise <see langword="null"/>. It
    /// names the offending STEP and field problem for a step fault, and the offending
    /// environment FIELD PATH for a security-declaration fault — which names no step at all. When
    /// the document carries both, it carries BOTH messages, step first, newline-separated.
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
    /// <para>
    /// <strong>Two scans, not one (EDGE-003, #387):</strong> steps first, then every declared
    /// <c>security</c> block's <c>clientKeyPassword</c>. #387 measured the asymmetry the second
    /// scan closes — <c>${secret:nosuchsource/TOKEN}</c> in a step's field was diagnosed by name,
    /// while the same token in a <c>security</c> field was mistaken for a filename, because this
    /// pass walked <c>ast.Steps</c> alone. The second scan is scoped to <c>clientKeyPassword</c>
    /// BY SCOPE, not by stage order: it is the one REFERENCE-valued security field, and its
    /// path-valued siblings are not scanned here at all, so no double-report is possible whatever
    /// order the two passes run in. (Order is genuinely not fixed — <c>ScenarioValidator</c> and
    /// <see cref="RunAsync"/> reach <c>EnvironmentSecurityValidator</c> first, while
    /// <see cref="RunSuiteAsync"/> and the watch seam reach THIS pass first.)
    /// </para>
    /// <para>
    /// <strong>A consequence worth knowing before it surprises someone:</strong> a document
    /// carrying BOTH a REQ-011 fault (a <c>${secret:}</c> in a path-valued security field) and an
    /// EDGE-003 one (a bad <c>clientKeyPassword</c> reference) reports DIFFERENT faults under
    /// <c>vouchfx validate</c> and under <c>vouchfx run</c>, because those two reach the two
    /// passes in opposite orders. Both exit 4, both faults are real, and fixing either leaves the
    /// other reported next — an author is never sent in a circle. No field is scanned by both
    /// passes, so the two can never double-report the SAME fault.
    /// </para>
    /// </remarks>
    /// <param name="fromSecurityDeclaration">
    /// <see langword="true"/> when THE DOCUMENT CONTAINS a secret fault in a declared
    /// <c>security</c> block.
    /// <para>
    /// <strong>NEITHER RUN PATH READS THIS ANY LONGER</strong> (security-assurance-derivation): it
    /// was the input to a door-local classification, and no door classifies now — a secured
    /// document refused before any container started is unconfirmable whatever the fault was, and
    /// an unsecured one is not, whatever the fault was. Both call sites discard it with
    /// <c>out _</c>. It is retained because it is directly asserted by
    /// <c>ScenarioValidatorTests</c> and states a true fact about the document.
    /// </para>
    /// <para>
    /// It deliberately does NOT mean "the failure reported through <c>error</c> was a security
    /// one". Those two readings come apart whenever a document carries BOTH a step fault and a
    /// security fault. Both halves therefore always run; only which MESSAGE is reported is
    /// first-wins — and that REPORTING property still matters and is still what the unconditional
    /// security half below delivers.
    /// </para>
    /// </param>
    internal static bool TryValidateSecretReferences(
        ScenarioAst ast, out string? error, out bool fromSecurityDeclaration)
    {
        // BOTH halves run, ALWAYS, and the security half runs FIRST so no early return can skip
        // it. `fromSecurityDeclaration` answers "does this document contain a security-declaration
        // secret fault?", which is a property of the DOCUMENT — not "was the first fault I found a
        // security one?", which is a property of the search order. The step half used to return
        // before this walk, and a document with a step fault AND a security fault then exited 0.
        var securityFault = TryFindSecurityDeclarationSecretFault(ast, out var securityError);
        fromSecurityDeclaration = securityFault;

        // The step half decides only the ORDER messages appear in, never the flag. Step-first is
        // retained because it is the pre-existing behaviour and nothing here depends on it.
        //
        // BOTH are reported when both exist. An earlier form returned the step message alone and
        // dropped `securityError` on the floor: two documents differing only in whether a
        // `clientKeyPassword` fault was present produced BYTE-IDENTICAL rendered output and
        // differed only in exit code, so the fault that turned the build red was never printed.
        // On a surface whose whole job is refusing false assurance, an unexplained red is barely
        // better than a wrong green. Concatenation is safe here: the security half withholds its
        // declared value on every path that could carry one (see ValidateSecretBearingField), so
        // joining the two strings cannot disclose anything the security half would not have
        // printed on its own.
        foreach (var node in ast.Steps)
        {
            foreach (var text in CollectSubstitutableTexts(node))
            {
                if (!SecretReference.ValidateField(text, s_knownSecretSources, out var fieldError))
                {
                    // "\n", never Environment.NewLine: this string reaches the schema-versioned
                    // `validate --json` document, where a platform-dependent separator would make
                    // one document differ between a Windows and a Linux runner for the same input.
                    // The terminal path is unaffected either way — DisplaySanitiser strips '\r'.
                    var stepError = $"step '{node.Id}': {fieldError}";
                    error = securityFault ? stepError + "\n" + securityError : stepError;
                    return true;
                }
            }
        }

        error = securityError;
        return securityFault;
    }

    /// <summary>
    /// Scans every declared <c>security</c> block's <c>clientKeyPassword</c> for a secret-reference
    /// fault, returning the first found (EDGE-003, #387).
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="TryValidateSecretReferences"/> so that pass can run this half
    /// UNCONDITIONALLY — the REQ-018 signal is a property of the document, and a half that only
    /// runs when the step half found nothing cannot report one.
    /// </remarks>
    private static bool TryFindSecurityDeclarationSecretFault(ScenarioAst ast, out string? error)
    {
        // SecuredTargets.Enumerate is the ONE canonical walk of declared `security` blocks
        // (services then dependencies, each in declaration order) — the same walk
        // BuildReproducibilityEnvelope's section 1b reuses for this same field. Its own header
        // records why it exists: a security predicate that had grown three spellings in three
        // assemblies, each asserting in prose that it agreed with the others.
        foreach (var target in SecuredTargets.Enumerate(ast.Environment))
        {
            if (target.Security.ClientKeyPassword is not { } clientKeyPassword)
            {
                continue;
            }

            // ValidateSecretBearingField applies BOTH rules this field is held to — the
            // whole-token rule and the known-source rule — atomically and in that order, and
            // withholds the declared value on every failing path except the unknown-source one,
            // where the value is a whole well-formed reference and §17 says a reference is a
            // pointer, never a secret.
            //
            // Atomic on purpose: the two rules used to be two statements here, and getting their
            // ORDER wrong discloses the value. Composing them at a call site is also how the
            // second defect arrived — a predicate written here tried to guess which of
            // ValidateField's branches would fire, and `${secret:nosuchsource/PASS${secret:LEAKED}`
            // (schema-valid, so CLI-reachable) defeats that guess. The method that owns the
            // grammar owns both decisions now.
            if (!SecretReference.ValidateSecretBearingField(
                    clientKeyPassword, s_knownSecretSources, out var fieldError))
            {
                // The step half's `step '{id}': …` prefix has no sensible environment-level form,
                // so this half carries its own, spelled the way EnvironmentSecurityValidator
                // spells every security field path — an author sees ONE convention across the two
                // stages that report on this block, not two. SecuredTargets.PluralFor owns the
                // plural.
                error =
                    $"environment.{SecuredTargets.PluralFor(target)}.{target.Name}"
                    + $".security.clientKeyPassword: {fieldError}";
                return true;
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
    /// <strong>Two scans, not one (client-key-password REQ-010):</strong> the step scan above,
    /// plus an environment-level pass over every declared <c>security</c> block's
    /// <c>clientKeyPassword</c> — the only secret-bearing field outside <c>steps</c>. Both feed
    /// the same reference list and the same resolver-free hashing. See the method body for why
    /// the sibling path-valued security fields are excluded.
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
        // set of STEP references in the envelope never drifts from the set the engine
        // actually recognises.  Compute() dedupes by Raw.
        //
        // Section 1b below adds an environment-level scan, and TryValidateSecretReferences has a
        // matching one (EDGE-003, #387): both walk SecuredTargets.Enumerate for
        // `security.clientKeyPassword`, so the two scans see the same set of references and no
        // reference reaches this receipt without having passed the known-source check first.
        var references = new List<SecretReference>();
        foreach (var node in ast.Steps)
        {
            foreach (var text in CollectSubstitutableTexts(node))
            {
                references.AddRange(SecretReference.FindAll(text));
            }
        }

        // ── 1b. Environment-level `security` references (client-key-password REQ-010) ──
        // The step scan above sees only ast.Steps, so an environment-level
        // `security.clientKeyPassword` reference was invisible to the envelope — a secret the
        // run genuinely depends on, silently absent from the reproducibility receipt. §17 says
        // the envelope hashes the reference for secrets generally; an omission here is a
        // reproducibility gap, not a scoping choice.
        //
        // SecuredTargets.Enumerate is the ONE canonical walk of declared `security` blocks
        // (its own header records that a second spelling is how two security rules drift), so
        // it is reused rather than respelled.
        //
        // ONLY ClientKeyPassword is scanned. The path-valued fields — `caCert`, `clientCert`,
        // `clientKey` and `serverArtifacts[].source` — are deliberately excluded: REQ-011
        // REFUSES a `${secret:}` in any of them, so a reference found there names a value the
        // engine is about to reject, and hashing it would put an entry in the receipt for a run
        // that never happened. The omission is a rule, not an oversight.
        //
        // Still resolver-free: SecretReference.FindAll reads the verbatim token text, exactly as
        // the step scan does. The §17 headline guarantee in this method's remarks — the resolver
        // is never invoked here — is unchanged.
        foreach (var target in SecuredTargets.Enumerate(ast.Environment))
        {
            if (target.Security.ClientKeyPassword is { } clientKeyPassword)
            {
                references.AddRange(SecretReference.FindAll(clientKeyPassword));
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
    /// the <c>sql</c> files of each seeded dependency, the only seed kind in the
    /// v1 language — and computes each one's content hash via
    /// <c>SeedFixtures.ComputeContentHash</c>, in declared order.
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
            // SQL fixtures (postgres, sqlserver, mysql) — the only seed kind in
            // the v1 language.
            if (dependency.Sql is not null)
            {
                foreach (var sqlPath in dependency.Sql)
                {
                    digests.Add(HashFixtureOrNull(baseDir, sqlPath));
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
    /// throwing) whenever the fixture cannot be hashed at envelope-build time — because the
    /// file is absent, and since issue #466 also because it is unreadable, inaccessible, or
    /// named by a path the filesystem rejects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>KNOWN RESIDUAL, and it is a property of <see cref="FixtureDigest"/> rather than
    /// of this method (SEC-MINOR-2).</strong> The envelope distinguishes "no fixture" (no row)
    /// from "a fixture that could not be hashed" (a row with a <see langword="null"/>
    /// <see cref="FixtureDigest.ContentHash"/>), but it does NOT distinguish the REASONS: an
    /// absent file and a present-but-unreadable one both record <see langword="null"/>. Two
    /// runs whose fixture was unreadable therefore compare EQUAL on that row even though the
    /// bytes may have differed, which weakens the input-equivalence claim the envelope exists
    /// to make. Widening the catch above did not create that conflation — an absent file
    /// already recorded <see langword="null"/> — but it did enlarge the set of causes that
    /// land in it, so it is stated here rather than left to be rediscovered. Fixing it means
    /// changing the <see cref="FixtureDigest"/> shape, which is a frozen-adjacent trust
    /// artefact and not something to reshape inside a bug fix; it is filed as a follow-up.
    /// </para>
    /// </remarks>
    private static FixtureDigest HashFixtureOrNull(string baseDirectory, string relativePath)
    {
        try
        {
            var hash = SeedFixtures.ComputeContentHash(baseDirectory, relativePath);
            return new FixtureDigest(relativePath, hash);
        }
        catch (Exception ex) when (
            ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            // The envelope must never crash a run: a missing seed fixture is already
            // classified as an Environment error by the seed applier (§12.1).  Record
            // the reference without a hash so the envelope remains a faithful account.
            //
            // WIDENED FROM FileNotFoundException ALONE (issue #466), because that single type
            // did not cover the routes ComputeContentHash actually has to the outside world,
            // and an escape from HERE is not a diagnostic — the envelope is built on the run
            // path, so the throw unwound into ParallelSuiteRunner's slot catch-all and became
            // an EnvironmentError that exits 0 on a run that executed nothing (#390). The
            // reachable set, read off ComputeContentHash: Path.GetFullPath raises
            // ArgumentException (invalid characters), PathTooLongException (an IOException) and
            // NotSupportedException; ArgumentException.ThrowIfNullOrEmpty raises
            // ArgumentException for an empty declared path; and File.ReadAllBytes raises
            // UnauthorizedAccessException, or a plain IOException when the file is locked or is
            // deleted in the window between the File.Exists check and the read. FileNotFoundException
            // is an IOException, so the original case is a subset rather than a sibling.
            //
            // NOT a bare `catch (Exception)`: an OutOfMemoryException or a cancellation raised
            // through this frame is not "the fixture could not be hashed" and must not be
            // recorded as a null hash. This is the IO family, named.
            //
            // THIS IS THE OPPOSITE ANSWER TO ProviderPipeline's SIX PROVIDER GUARDS, WHICH CATCH
            // EVERYTHING. The two are not in conflict, and the reason is written out ONCE, in
            // ProviderPipeline.DescribeProviderFault's remarks — the short version being that
            // over-catching there costs a sentence and over-catching HERE corrupts the
            // reproducibility envelope while the run continues. Deliberately not restated: this
            // comment used to carry its own copy of that argument, complete with a headline
            // ("the escape destinations differ") that was false in both copies, and two
            // independently-maintained statements of one rule is the drift class this repository
            // treats as a defect. If the trade changes, it changes in one place.
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
