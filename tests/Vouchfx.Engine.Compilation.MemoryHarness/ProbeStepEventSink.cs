// Vouchfx.Engine.Compilation.MemoryHarness — ProbeStepEventSink (issue #262, §5).
//
// A tiny, in-harness stub IStepEventSink used by the closure leak gate's #262 extension. It
// captures every StepOutcome / AttemptRecord / string passed across the collectible-ALC
// boundary via ScriptGlobalVariables.StepEvents, so the closure probe CSX can exercise the NEW
// StepEvents.OnStepStarted / OnStepAttempt / OnStepCompleted call sites — proving that read
// path does not pin the collectible AssemblyLoadContext, exactly as ProbeWebhookAccessor /
// ProbeTraceAccessor already prove for Webhooks / Traces.
//
// Memory-model note (§5): this is a by-reference Default-ALC instance (it lives in this
// harness, exactly like the real host-owned LiveStepEventSink lives in
// Vouchfx.Engine.Runtime). It holds only a bounded, periodically-reset counter/last-seen state —
// no static state, and nothing here ever references the collectible submission assembly. The
// closure CSX reaches it only through ScriptGlobalVariables.StepEvents; a pin through either
// the interface dispatch or the StepOutcome/AttemptRecord record graph would surface as
// per-cycle heap growth in MemoryProbe.RunClosureAsync.
using System;
using System.Threading;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Retry;

namespace Vouchfx.Engine.Compilation.MemoryHarness;

/// <summary>
/// A stub <see cref="IStepEventSink"/> for the closure leak gate (issue #262). Records only
/// bounded counters — never retains the actual <see cref="StepOutcome"/> / <see cref="AttemptRecord"/>
/// instances or step-id strings it observes — so reuse across thousands of closure iterations
/// never itself becomes a source of accumulation independent of the ALC lifecycle under test.
/// </summary>
/// <remarks>
/// Created once and reused across every closure iteration, exactly like
/// <see cref="ProbeWebhookAccessor"/> / <see cref="ProbeTraceAccessor"/>: a single long-lived
/// Default-ALC instance with no mutable state that could itself grow (only fixed-width
/// counters), so reuse is safe and matches how the real host-owned
/// <c>Vouchfx.Engine.Runtime.LiveStepEventSink</c> is a single per-scenario instance.
/// </remarks>
public sealed class ProbeStepEventSink : IStepEventSink
{
    private long _started;
    private long _attempts;
    private long _completed;

    /// <summary>Total <see cref="OnStepStarted"/> calls observed so far.</summary>
    public long StartedCount => Interlocked.Read(ref _started);

    /// <summary>Total <see cref="OnStepAttempt"/> calls observed so far.</summary>
    public long AttemptCount => Interlocked.Read(ref _attempts);

    /// <summary>Total <see cref="OnStepCompleted"/> calls observed so far.</summary>
    public long CompletedCount => Interlocked.Read(ref _completed);

    /// <inheritdoc />
    public void OnStepStarted(string stepId) => Interlocked.Increment(ref _started);

    /// <inheritdoc />
    public void OnStepAttempt(string stepId, AttemptRecord attempt) => Interlocked.Increment(ref _attempts);

    /// <inheritdoc />
    public void OnStepCompleted(string stepId, StepOutcome? outcome, string? captureStatus) =>
        Interlocked.Increment(ref _completed);
}
