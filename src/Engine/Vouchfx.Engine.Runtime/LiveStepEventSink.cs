// Vouchfx.Engine.Runtime — LiveStepEventSink (§14, issue #262).
//
// The concrete, per-scenario IStepEventSink the runner wires into ScriptGlobalVariables when a
// live --events-stream conduit (LiveEventPump) is attached. Holds exactly the compile-time
// context RunScenarioCoreAsync already has available BEFORE the isolated run starts — the
// runId, a stepId → StepNode lookup, the G-01 capture-origin map, and the scenario's secret
// accessor — and turns each IStepEventSink signal into a JSON Lines event line via the SAME
// StepEventBuilder the post-run reconstruction uses, then posts it to the pump.
//
// Memory-model note (§5): every field here is a Default-ALC object (LiveEventPump, string,
// StepNode graph parsed from YAML, the captureOriginMap dictionary, the secret accessor) — none
// of them reference the collectible submission assembly or anything inside it. The emitted CSX
// reaches this instance only via ScriptGlobalVariables.StepEvents (an interface reference); this
// instance never captures globals, the submission's `this`, or any delegate closing over either.
// Its lifetime is a local in RunScenarioCoreAsync — it goes out of scope once that method
// returns, so it cannot outlive (or delay) the collectible ALC's `.Unload()`.
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Retry;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring.Ast;

namespace Vouchfx.Engine.Runtime;

/// <summary>
/// Per-scenario <see cref="IStepEventSink"/> that turns each host-side step-lifecycle signal
/// into a JSON Lines event line (via <see cref="StepEventBuilder"/>) and posts it to a
/// <see cref="LiveEventPump"/> in real time (issue #262).
/// </summary>
internal sealed class LiveStepEventSink : IStepEventSink
{
    private readonly LiveEventPump _pump;
    private readonly string _runId;
    private readonly Dictionary<string, StepNode> _stepsById;
    private readonly IReadOnlyDictionary<string, string> _captureOriginMap;
    private readonly ISecretAccessor _secretAccessor;

    /// <summary>
    /// The run's security-path disclosure ledger, or <see langword="null"/> when the constructing
    /// scenario holds none (issue #375).
    /// </summary>
    private readonly SecurityPathDisclosureLedger? _pathLedger;

    /// <summary>
    /// Constructs the sink for one scenario run.
    /// </summary>
    /// <param name="pump">The live conduit to post lines to.</param>
    /// <param name="runId">The run identifier stamped onto every line this sink emits.</param>
    /// <param name="steps">
    /// The scenario's steps, in declaration order — indexed by <see cref="StepNode.Id"/> for
    /// O(1) lookup from the raw step id each <see cref="IStepEventSink"/> call reports.
    /// </param>
    /// <param name="captureOriginMap">
    /// The scenario's G-01 capture-origin map (varName → declaring stepId), as built once by
    /// <c>ScenarioRunner.BuildCaptureOriginMap</c> — the SAME map the post-run reconstruction
    /// uses, so <see cref="StepEventBuilder.StepCompletedLine"/> derives identical substitution
    /// provenance regardless of which path calls it.
    /// </param>
    /// <param name="secretAccessor">The scenario's secret accessor, for observation scrubbing.</param>
    /// <param name="pathLedger">
    /// The run's security-path disclosure ledger, or <see langword="null"/> when the caller holds
    /// none.  REQUIRED rather than optional (issue #375): the live path and the post-run
    /// reconstruction must scrub identically, and an omitted argument here is a leak that shows up
    /// only under <c>--events</c> tailing, on the one channel no later scrubber can reach.
    /// </param>
    public LiveStepEventSink(
        LiveEventPump pump,
        string runId,
        IReadOnlyList<StepNode> steps,
        IReadOnlyDictionary<string, string> captureOriginMap,
        ISecretAccessor secretAccessor,
        SecurityPathDisclosureLedger? pathLedger)
    {
        ArgumentNullException.ThrowIfNull(pump);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(captureOriginMap);
        ArgumentNullException.ThrowIfNull(secretAccessor);

        _pump = pump;
        _runId = runId;
        _captureOriginMap = captureOriginMap;
        _secretAccessor = secretAccessor;
        _pathLedger = pathLedger;

        var byId = new Dictionary<string, StepNode>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            // Step ids are unique within a scenario (enforced upstream by AstBuilder); a
            // duplicate here would indicate an engine defect, not an author error — last-write
            // is a safe, non-throwing degradation rather than crashing event emission.
            byId[step.Id] = step;
        }

        _stepsById = byId;
    }

    /// <inheritdoc />
    public void OnStepStarted(string stepId)
    {
        // Defensive: these hooks are documented never-throw, and a script.csharp body (the
        // documented no-sandbox escape hatch) could call StepEvents.OnStepStarted directly with
        // a null or empty stepId — Dictionary<TKey,TValue>.TryGetValue/ContainsKey throw
        // ArgumentNullException on a null key, so the null-or-empty check must come FIRST,
        // before the step id ever reaches _stepsById.
        if (string.IsNullOrEmpty(stepId))
        {
            return;
        }

        if (!_stepsById.TryGetValue(stepId, out var node))
        {
            // Defensive: an unknown step id should not occur (the emitted CSX only ever
            // reports the ids of steps that were themselves compiled from this scenario's AST).
            // Never throw from a live-signal hook — degrade to a no-op.
            return;
        }

        _pump.Post(StepEventBuilder.StepStartedLine(_runId, DateTimeOffset.UtcNow, node));
    }

    /// <inheritdoc />
    public void OnStepAttempt(string stepId, AttemptRecord attempt)
    {
        // Defensive: same null-or-empty guard as OnStepStarted/OnStepCompleted — must run
        // BEFORE the _stepsById lookup, since Dictionary.ContainsKey throws on a null key.
        if (string.IsNullOrEmpty(stepId))
        {
            return;
        }

        // Security hardening: mirror the OnStepStarted / OnStepCompleted guard. Without it, a
        // script.csharp body (the documented no-sandbox escape hatch) could call
        // StepEvents.OnStepAttempt directly with an arbitrary stepId and forge step-attempt
        // lines into --events-stream for a step that was never actually compiled/declared in
        // this scenario. Every LEGITIMATE call — the CsxAssembler-emitted wrapper and
        // RetryRunner's sink-aware PollAsync overload — passes the raw, un-sanitised
        // StepNode.Id the engine itself compiled, which is always present in _stepsById, so
        // this can never suppress a real attempt.
        if (!_stepsById.ContainsKey(stepId))
        {
            return;
        }

        _pump.Post(StepEventBuilder.StepAttemptLine(
            _runId, DateTimeOffset.UtcNow, stepId, attempt, _secretAccessor, _pathLedger));
    }

    /// <inheritdoc />
    public void OnStepCompleted(string stepId, StepOutcome? outcome, string? captureStatus)
    {
        // Defensive: same null-or-empty guard as OnStepStarted/OnStepAttempt — must run BEFORE
        // the _stepsById lookup, since Dictionary.TryGetValue throws on a null key.
        if (string.IsNullOrEmpty(stepId))
        {
            return;
        }

        if (!_stepsById.TryGetValue(stepId, out var node))
        {
            return;
        }

        _pump.Post(StepEventBuilder.StepCompletedLine(
            _runId, DateTimeOffset.UtcNow, node, outcome, captureStatus, _captureOriginMap,
            _secretAccessor, _pathLedger));
    }
}
