// Vouchfx.Engine.Abstractions — IStepEventSink (§5, §14, issue #262).
//
// Host-side per-step live event sink. The emitted CSX wrappers (CsxAssembler.WrapForImmediate
// / WrapForRetry, and RetryRunner.PollAsync's per-attempt hook) call this interface's members —
// via ScriptGlobalVariables.StepEvents — AT THE MOMENT each step/attempt genuinely starts or
// resolves, INSIDE the isolated run (RoslynScriptCompiler.RunIsolatedAsync), rather than only
// after the whole scenario returns. This is what makes a `--events-stream` tail show per-step
// liveness (#262) instead of only the scenario-granular liveness #258 already provides.
//
// Deliberately narrow: every parameter type is already referenceable from an emitted CSX body
// (string, StepOutcome, Retry.AttemptRecord) — no new wire record is introduced, and no event
// line is built here. A concrete sink (Vouchfx.Engine.Runtime.LiveStepEventSink) turns these
// signals into the SAME frozen event-stream lines the post-run reconstruction builds (via the
// shared Vouchfx.Engine.Runtime.StepEventBuilder), so the frozen EventContractFreezeTests golden
// is untouched by this interface's existence.
//
// Memory-model note (§5): a concrete sink is a Default-ALC object (built and owned by the host,
// exactly like ISecretAccessor / IWebhookCaptureAccessor / ITraceCaptureAccessor) passed into
// ScriptGlobalVariables by reference. Only Default-ALC data types cross this interface's members
// (string, StepOutcome, AttemptRecord) — holding them never roots the collectible
// AssemblyLoadContext that the emitted script's submission assembly lives in.

namespace Vouchfx.Engine.Abstractions;

/// <summary>
/// Host-side per-step live event sink (§14, issue #262). An emitted CSX step block (via the
/// engine-owned <c>CsxAssembler</c> wrapper) reports its lifecycle through this interface,
/// reached only via <see cref="ScriptGlobalVariables.StepEvents"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every member is deliberately narrow: it carries only the runtime primitive the host cannot
/// derive at compile time (the final <see cref="StepOutcome"/>, a single
/// <see cref="Retry.AttemptRecord"/>, or the raw capture-status string). Compile-time context —
/// step kind, <c>verifyMode</c>, timeout, substitution provenance — is held host-side by the
/// concrete sink, which is why no new wire record is required here.
/// </para>
/// <para>
/// <see cref="OnStepAttempt"/> is called only for a <c>verifyMode: RETRY</c> step (once per
/// recorded poll, from inside the sink-aware overload of <see cref="Retry.RetryRunner"/>'s
/// polling loop); an IMMEDIATE step records no attempts and never calls it.
/// </para>
/// </remarks>
public interface IStepEventSink
{
    /// <summary>
    /// Called once, at the top of the emitted block, the moment a step begins execution —
    /// before its first (or only) attempt runs.
    /// </summary>
    /// <param name="stepId">
    /// The raw (un-sanitised) step identifier, exactly as authored in the <c>.e2e.yaml</c>
    /// (<c>StepNode.Id</c>) — emitted as a compile-time literal so the host can key its
    /// per-step lookup directly, with no runtime sanitisation round-trip.
    /// </param>
    void OnStepStarted(string stepId);

    /// <summary>
    /// Called once per recorded poll of a <c>verifyMode: RETRY</c> step, immediately after the
    /// attempt resolves (success or a caught, non-cancellation exception) — the only place with
    /// genuine per-attempt real-time timing.
    /// </summary>
    /// <param name="stepId">The raw (un-sanitised) step identifier.</param>
    /// <param name="attempt">The just-recorded attempt.</param>
    void OnStepAttempt(string stepId, Retry.AttemptRecord attempt);

    /// <summary>
    /// Called once, at the very end of the emitted block, after a step's outcome is FINAL
    /// (for RETRY: after <c>PollAsync</c> returns; for IMMEDIATE: after any step-timeout
    /// supersession has already been applied) — mirroring exactly what the post-run
    /// reconstruction reads back from <c>Vars</c>.
    /// </summary>
    /// <param name="stepId">The raw (un-sanitised) step identifier.</param>
    /// <param name="outcome">
    /// The step's final <see cref="StepOutcome"/>, or <see langword="null"/> when the emitted
    /// block never wrote one (defensive — should not occur in practice).
    /// </param>
    /// <param name="captureStatus">
    /// The raw per-capture matched-flag string the provider block wrote (e.g. <c>"1,0,1"</c>),
    /// or <see langword="null"/> when the step declares no <c>capture</c> entries.
    /// </param>
    void OnStepCompleted(string stepId, StepOutcome? outcome, string? captureStatus);
}
