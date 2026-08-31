// Vouchfx.Engine.Runtime — WatchIterationPlan (#370, #412).
//
// EVERYTHING ONE `--watch` SAVE DECIDES BEFORE A CONTAINER IS TOUCHED.
//
// #370 measured the ordering defect this type removes: the watch compile seam was
// YamlDocumentParser.Parse + AstBuilder.Build and nothing else, so DocumentValidator.Validate and
// ProviderPipeline.Compile — both of which `run` executes BEFORE SuiteTopology.StartAsync — did not
// run until the RUN seam, against a topology that was already up. Three consequences were recorded:
// a schema-invalid suite started containers; the security probe could be handed a target the engine
// had already decided was misconfigured, so the run blamed the broker for an authoring conflict;
// and SecuredEndpointProbe's unrecognised-profile refusal — documented unreachable by author input
// because SecurityProfileWiringValidator rejects an unregistered profile first — was reachable on
// this path alone, telling an author to register a profile in an internal engine dictionary.
//
// The fix is not "add a validate call to the watch seam". It is to make the pre-topology stage a
// VALUE the save produces, so the reuse-vs-rebuild decision and the run both consume the same
// decided thing. What that buys, beyond the ordering: the refusal is rendered from the plan, so a
// refused save never reaches the build seam at all, whether or not a topology is already kept.
//
// #412 IS THE SAME OBJECT'S SECOND JOB. The retired ordering — secret pass first, return at its
// first fault, THEN ProviderPipeline.Compile — survived here in a third spelling after #399 merged
// the two doors on `run` and `--parallel`. Create() calls the ONE extracted door
// (ScenarioRunner.RunPreTopologyAuthoringDoor), so a document with faults in both walks now reports
// both, in the run path's order, on all three paths. The cost is the one #399 already accepted and
// recorded: a document whose only fault is a step-secret fault pays a full in-memory CSX emit that
// is then thrown away. Guarding the Compile call on `stepSecretFault is null` would reinstate the
// divergence silently, which is why it is written down here rather than left to be "optimised".
//
// REQ-018 IS STILL ABSENT FROM THIS PATH, DELIBERATELY. `--watch` derives no process exit code
// (WatchRunner returns only UsageError or Success and never calls ExitCodes.FromVerdict), so there
// is nothing for a SecurityAssurance signal to accumulate INTO. Neither door below carries the
// signal — the schema door does not call RejectsASecurityDeclaration, and the authoring door drops
// ValidationFailure.IsSecurityPreflight — and inventing a flag with no consumer would read like
// coverage that does not exist. Watch mode's blanket absence of REQ-018 predates this type and is
// not narrowed by it. (This note moved here verbatim from the retired TryCompileForRun.)

using System;
using System.Collections.Generic;
using System.Linq;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Reporting;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Runtime;

/// <summary>
/// The decided pre-topology state of one <c>--watch</c> save: either a refusal (with the events and
/// diagnostic the author sees) or a compiled scenario plus the <see cref="TopologyRequest"/> and
/// fingerprint the session's reuse-vs-rebuild decision runs on (#370, #412).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every gate here is a pure function of the document.</strong> None of them needs a
/// topology, which is exactly why they can — and now do — run before one is built. The
/// topology-lifetime checks stay where they are: <c>SuiteTopology.StartAsync</c>'s security-accessor
/// guard, its pinned-port pre-flight, <c>EnvironmentMapper.Map</c> (including #348's
/// <c>TopologyAuthoringException</c> and the eager <c>${conn:…}</c> validation), the health gate,
/// REQ-005's probe, and the seed.
/// </para>
/// <para>
/// <strong>The multi-scenario guards have no counterpart here and must not grow one.</strong> The
/// shared-<c>environment</c> divergence guard, the security base-directory divergence guard and the
/// suite-level protocol-conflict guard all exist because ONE topology serves N scenarios. Watch
/// mode's unit is one file, so <see cref="ProviderPipeline"/>'s own per-scenario REQ-023 check is
/// complete for it — see that call site's scope note.
/// </para>
/// </remarks>
public sealed class WatchIterationPlan
{
    private WatchIterationPlan(
        string runId,
        bool isRefused,
        string? diagnostic,
        IReadOnlyList<string> eventLines,
        string topologyFingerprint,
        TopologyRequest request,
        ScenarioAst ast,
        string scenarioName,
        string? seedBaseDirectory,
        PipelineResult? pipeline)
    {
        RunId = runId;
        IsRefused = isRefused;
        Diagnostic = diagnostic;
        EventLines = eventLines;
        TopologyFingerprint = topologyFingerprint;
        Request = request;
        Ast = ast;
        ScenarioName = scenarioName;
        SeedBaseDirectory = seedBaseDirectory;
        Pipeline = pipeline;
    }

    /// <summary>
    /// Gets the run identifier minted for this save — ONE per save, shared by the refusal events
    /// below and by the run that follows when there is no refusal, so a renderer's
    /// <c>(runId, stepId)</c> cache sees one run per save rather than two.
    /// </summary>
    public string RunId { get; }

    /// <summary>
    /// Gets a value indicating whether a pre-topology gate refused this save. When
    /// <see langword="true"/>, no topology may be built or reused for it.
    /// </summary>
    public bool IsRefused { get; }

    /// <summary>
    /// Gets the author-facing diagnostic: the joined schema errors, or
    /// <c>JoinAuthoringFaults(compile fault, step-secret fault)</c>. <see langword="null"/> when
    /// nothing was refused.
    /// </summary>
    public string? Diagnostic { get; }

    /// <summary>
    /// Gets the event lines a refusal produced — the same <c>ScenarioStarted</c> +
    /// <c>ScenarioCompleted</c> (Inconclusive) pair a refused save emitted before this type existed.
    /// Empty when nothing was refused.
    /// </summary>
    public IReadOnlyList<string> EventLines { get; }

    /// <summary>
    /// Gets the reuse key: a digest over the WHOLE <see cref="TopologyRequest"/>, not the
    /// <c>environment</c> block alone.
    /// </summary>
    /// <remarks>
    /// See <see cref="ScenarioRunner.ComputeTopologyFingerprint"/> for what widening it closes and
    /// what it costs.
    /// </remarks>
    public string TopologyFingerprint { get; }

    /// <summary>Gets the one argument list the topology for this save would be built from.</summary>
    public TopologyRequest Request { get; }

    /// <summary>Gets the parsed scenario this plan was computed for.</summary>
    public ScenarioAst Ast { get; }

    /// <summary>Gets the report-facing scenario name (the event stream's <c>scenarioId</c>).</summary>
    public string ScenarioName { get; }

    /// <summary>Gets the suite directory relative seed and step file paths resolve against.</summary>
    public string? SeedBaseDirectory { get; }

    /// <summary>
    /// Gets the compiled pipeline result, or <see langword="null"/> when
    /// <see cref="IsRefused"/>. Internal because <see cref="PipelineResult"/> is: the CLI threads
    /// this plan through <c>WatchSession</c> as an opaque payload and never inspects it.
    /// </summary>
    internal PipelineResult? Pipeline { get; }

    /// <summary>
    /// Runs every pre-topology gate for one save, in the order <c>run</c> runs them, and returns the
    /// decided plan.
    /// </summary>
    /// <param name="ast">The AST the compile seam already built from the saved content.</param>
    /// <param name="yamlText">The raw saved YAML (the schema door reads the document, not the AST).</param>
    /// <param name="scenarioName">The report-facing name.</param>
    /// <param name="registry">The frozen provider registry.</param>
    /// <param name="appHostAssemblyName">The DCP-metadata-carrying assembly's short name.</param>
    /// <param name="suiteDirectory">
    /// The watched file's directory — the seed root, the artefact-containment root, and the base
    /// relative step file paths resolve against.
    /// </param>
    public static WatchIterationPlan Create(
        ScenarioAst ast,
        string yamlText,
        string scenarioName,
        StepKindRegistry registry,
        string? appHostAssemblyName,
        string? suiteDirectory)
    {
        ArgumentNullException.ThrowIfNull(ast);
        ArgumentNullException.ThrowIfNull(yamlText);
        ArgumentNullException.ThrowIfNull(scenarioName);
        ArgumentNullException.ThrowIfNull(registry);

        var runId = Guid.NewGuid().ToString("n");

        // Built BEFORE the gates so a refusal still carries it: WatchSession compares the
        // fingerprint only on a successful compile today, but computing it unconditionally keeps
        // the value a property of the save rather than of whether the save happened to pass.
        //
        // ONE CONSEQUENCE, NAMED SO IT IS NOT MISREAD AS CLOSED: this line SERIALISES the
        // `environment` block (ComputeTopologyFingerprint → ComputeEnvironmentHash →
        // SerialiseEnvironment) before Door 1 below has validated anything, so `--watch` still
        // reaches the environment serialiser ahead of the schema — as it always did. #370 moved the
        // gates ahead of the TOPOLOGY BUILD; it did not move them ahead of THIS, and the two are
        // independent orderings. The serialiser must therefore stay tolerant of what the parser
        // accepts and the schema does not (see ScenarioRunner's s_envSerialiserOptions remarks and
        // EnvironmentHashTests, which both rest on this line's position).
        var request = TopologyRequest.ForScenario(ast, appHostAssemblyName, suiteDirectory);
        var fingerprint = ScenarioRunner.ComputeTopologyFingerprint(request);

        WatchIterationPlan Refused(string diagnostic) => new(
            runId,
            isRefused: true,
            diagnostic,
            RefusalEventLines(runId, scenarioName, diagnostic),
            fingerprint,
            request,
            ast,
            scenarioName,
            suiteDirectory,
            pipeline: null);

        // ── Door 1: the schema ────────────────────────────────────────────────
        var validationResult = DocumentValidator.Validate(yamlText, registry);
        if (!validationResult.IsValid)
        {
            return Refused(string.Join("; ", validationResult.Errors.Select(e => e.Message)));
        }

        // ── Door 2: the merged authoring door (#412) ──────────────────────────
        // ProviderPipeline.Compile AND the secret-reference walk, both run, both reported, in the
        // run path's order — the same helper `run` and `--parallel` call.
        var (pipeline, authoringFault) =
            ScenarioRunner.RunPreTopologyAuthoringDoor(ast, registry, suiteDirectory);
        if (authoringFault is not null)
        {
            return Refused(authoringFault);
        }

        return new WatchIterationPlan(
            runId,
            isRefused: false,
            diagnostic: null,
            Array.Empty<string>(),
            fingerprint,
            request,
            ast,
            scenarioName,
            suiteDirectory,
            pipeline);
    }

    /// <summary>
    /// The exact event pair a refused save emitted before this type existed: a
    /// <see cref="ScenarioStartedEvent"/> and an Inconclusive <c>scenario-completed</c> carrying the
    /// diagnostic as its cause (#372).
    /// </summary>
    /// <remarks>
    /// <c>ledger: null</c> is deliberate and carries the reasoning the retired site recorded: all
    /// three fault sources produce their text from YAML and from secret REFERENCES, before any step
    /// executes, so the text cannot CONTAIN a resolved value — and scrubbing it against the
    /// session-scoped ledger a <c>--watch</c> run accumulates would expose the author's primary
    /// diagnostic to over-redaction (a short or stale recorded value rewriting unrelated substrings
    /// for the rest of the session). The terminal sink makes the same call for the same reason.
    /// </remarks>
    private static string[] RefusalEventLines(
        string runId, string scenarioName, string diagnostic)
    {
        var now = DateTimeOffset.UtcNow;
        return new[]
        {
            EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = runId,
                Timestamp = now,
                ScenarioId = scenarioName,
            }),
            StepEventBuilder.ScenarioCompletedLine(
                runId,
                now,
                scenarioName,
                Verdict.Inconclusive,
                new VerdictCounts { Inconclusive = 1 },
                ledger: null,
                diagnostic),
        };
    }
}
