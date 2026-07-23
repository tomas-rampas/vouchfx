// Vouchfx.Engine.Runtime — StepEventBuilder (§14, issue #262).
//
// Extracted, internal-static line-construction helpers shared by BOTH:
//   • the post-run reconstruction loop in ScenarioRunner.RunScenarioCoreAsync (reads the
//     final Vars state after RoslynScriptCompiler.RunIsolatedAsync returns and rebuilds the
//     complete event buffer for the `--events` archive — unchanged behaviour, #258's
//     scenario-granular liveness);
//   • LiveStepEventSink (issue #262), which calls the SAME builders in real time, from
//     inside the isolated run, as the emitted CSX reports each step's lifecycle through
//     ScriptGlobalVariables.StepEvents.
//
// This is the parity guarantee: because both paths construct their JSON Lines through this
// one shared set of methods, a live-streamed line and its later-reconstructed twin are
// byte-identical modulo the `ts` field (the live post happens at the real moment; the
// reconstruction is stamped with one shared `now9` batch timestamp after the run returns).
//
// No new wire record is introduced here — every method returns the EXACT SAME frozen
// Vouchfx.Engine.Abstractions.Events record types, via the SAME EventStreamJson.ToLine.  The
// EventContractFreezeTests golden is therefore unaffected by this extraction.
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Abstractions.Reproducibility;
using Vouchfx.Engine.Abstractions.Retry;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring.Ast;

namespace Vouchfx.Engine.Runtime;

/// <summary>
/// Shared, side-effect-free JSON Lines event-line builders (§14, issue #262). Every method is
/// a pure function of its arguments — no I/O, no <c>Vars</c> lookups beyond what is passed in
/// explicitly — so the reconstruction loop and <see cref="LiveStepEventSink"/> can each call
/// them with their own timestamp and get byte-identical output modulo that timestamp.
/// </summary>
internal static class StepEventBuilder
{
    /// <summary>
    /// Builds a <c>scenario-started</c> line.
    /// </summary>
    internal static string ScenarioStartedLine(string runId, DateTimeOffset timestamp, string scenarioId)
        => EventStreamJson.ToLine(new ScenarioStartedEvent
        {
            RunId = runId,
            Timestamp = timestamp,
            ScenarioId = scenarioId,
        });

    /// <summary>
    /// Builds a <c>scenario-completed</c> line.
    /// </summary>
    internal static string ScenarioCompletedLine(
        string runId, DateTimeOffset timestamp, string scenarioId, Verdict verdict, VerdictCounts counts)
        => EventStreamJson.ToLine(new ScenarioCompletedEvent
        {
            RunId = runId,
            Timestamp = timestamp,
            ScenarioId = scenarioId,
            Verdict = verdict,
            Counts = counts,
        });

    /// <summary>
    /// Builds a <c>reproducibility-envelope</c> line from an already-assembled
    /// <see cref="ReproducibilityEnvelope"/> (§17, docs/02 §3.2.2).
    /// </summary>
    internal static string ReproducibilityLine(
        string runId, DateTimeOffset timestamp, string scenarioId, ReproducibilityEnvelope envelope)
        => EventStreamJson.ToLine(new ReproducibilityEnvelopeEvent
        {
            RunId = runId,
            Timestamp = timestamp,
            ScenarioId = scenarioId,
            EnvSchemaVersion = envelope.SchemaVersion,
            SecretReferences = envelope.SecretReferences,
            Fixtures = envelope.Fixtures,
        });

    /// <summary>
    /// Builds a <c>step-started</c> line from a step's compile-time AST node.
    /// </summary>
    internal static string StepStartedLine(string runId, DateTimeOffset timestamp, StepNode node)
        => EventStreamJson.ToLine(new StepStartedEvent
        {
            RunId = runId,
            Timestamp = timestamp,
            StepId = node.Id,
            Kind = node.CanonicalType,
            VerifyMode = node.VerifyMode.ToString().ToUpperInvariant(),
            TimeoutMs = node.Timeout is { } t ? (long)t.TotalMilliseconds : null,
        });

    /// <summary>
    /// Builds a single <c>step-attempt</c> line for one <see cref="AttemptRecord"/> (§7, §14).
    /// Called once per record by the reconstruction loop's <c>BuildAttemptEventLines</c> (batch,
    /// post-run) and once per attempt, in real time, by <see cref="LiveStepEventSink.OnStepAttempt"/>.
    /// </summary>
    /// <param name="secretAccessor">
    /// The scenario's secret accessor, used to scrub the attempt's observation text (§17,
    /// S11-B-01) before it is parsed into the wire's structured <c>observation</c> field.
    /// <see langword="null"/> defaults to <see cref="NullSecretAccessor.Instance"/> (nothing to
    /// scrub), matching the pre-#262 default on <c>BuildAttemptEventLines</c>.
    /// </param>
    internal static string StepAttemptLine(
        string runId,
        DateTimeOffset timestamp,
        string stepId,
        AttemptRecord attempt,
        ISecretAccessor? secretAccessor)
        => EventStreamJson.ToLine(new StepAttemptEvent
        {
            RunId = runId,
            Timestamp = timestamp,
            StepId = stepId,
            Attempt = attempt.Attempt,
            TMs = attempt.TMs,
            Outcome = attempt.Verdict,
            Observation = ScenarioRunner.BuildStepObservation(
                secretAccessor ?? NullSecretAccessor.Instance, attempt.Observation),
        });

    /// <summary>
    /// Builds a <c>step-completed</c> line (§14.4), replicating exactly what the pre-#262
    /// reconstruction loop computed inline: the capture provenance from
    /// <paramref name="captureStatusRaw"/> + the step's <c>capture</c> declarations, the
    /// compile-time substitution provenance, and the scrubbed structured observation.
    /// </summary>
    /// <param name="node">The step's compile-time AST node.</param>
    /// <param name="outcome">
    /// The step's final <see cref="StepOutcome"/>, or <see langword="null"/> when none was
    /// written (defensive — degrades to <see cref="Verdict.Inconclusive"/> / zero duration).
    /// </param>
    /// <param name="captureStatusRaw">
    /// The raw per-capture matched-flag string (e.g. <c>"1,0,1"</c>), or <see langword="null"/>.
    /// Ignored when <paramref name="node"/> declares no <c>capture</c> entries.
    /// </param>
    /// <param name="captureOriginMap">
    /// Map of captured variable name → originating step id (G-01 provenance), as built once per
    /// scenario by <c>ScenarioRunner.BuildCaptureOriginMap</c>.
    /// </param>
    /// <param name="secretAccessor">The scenario's secret accessor, for observation scrubbing.</param>
    internal static string StepCompletedLine(
        string runId,
        DateTimeOffset timestamp,
        StepNode node,
        StepOutcome? outcome,
        string? captureStatusRaw,
        IReadOnlyDictionary<string, string> captureOriginMap,
        ISecretAccessor secretAccessor)
    {
        var stepVerdict = outcome?.Verdict ?? Verdict.Inconclusive;
        var durationMs = outcome?.DurationMs ?? 0L;

        IReadOnlyList<CapturedVar>? capturedList = null;
        if (node.Capture.Count > 0)
        {
            var flagTokens = captureStatusRaw?.Split(',') ?? Array.Empty<string>();
            var capturedVars = new List<CapturedVar>(node.Capture.Count);
            var captureKeys = node.Capture.Keys.ToArray();
            var captureVals = node.Capture.Values.Select(e => e.Expression).ToArray();

            for (var ci = 0; ci < captureKeys.Length; ci++)
            {
                var matched = ci < flagTokens.Length && flagTokens[ci] == "1";
                capturedVars.Add(new CapturedVar(
                    Name: captureKeys[ci],
                    Path: captureVals[ci],
                    Matched: matched));
            }

            capturedList = capturedVars;
        }

        var substitutionsList = ScenarioRunner.DeriveSubstitutionProvenance(node, captureOriginMap);

        return EventStreamJson.ToLine(new StepCompletedEvent
        {
            RunId = runId,
            Timestamp = timestamp,
            StepId = node.Id,
            Verdict = stepVerdict,
            DurationMs = durationMs,
            Captured = capturedList,
            Substitutions = substitutionsList,
            Observation = ScenarioRunner.BuildStepObservation(secretAccessor, outcome?.Observation),
        });
    }
}
