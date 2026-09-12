// Vouchfx.Cli — ShutdownBackstop (vouchfx-mcp#17; security-review MAJOR-1 fix).
//
// The wall-clock force-exit backstop for the opt-in `--shutdown-on-stdin-eof` graceful-shutdown
// seam. CancellationTokenSource.CreateLinkedTokenSource only propagates cancellation DOWNSTREAM:
// on stdin EOF, RunCommand.ExecuteAsync cancels its OWN linked CancellationTokenSource, but the
// ORIGINAL System.CommandLine action token is never touched — so System.CommandLine's own
// InvocationConfiguration.ProcessTerminationTimeout watchdog (armed only by the real OS
// Ctrl-C/SIGTERM handler acting on THAT original token — see Program.cs) is NEVER engaged by a
// stdin-EOF-triggered stop. Without a backstop of its own, a run wedged somewhere that does not
// observe cancellation promptly (a step/provider await, not just teardown) would hang forever
// once stdin closes.
//
// This type is that backstop. Armed exactly once, when the EOF callback fires, it starts a
// wall-clock timer — deliberately bound to ITS OWN CancellationTokenSource, never the run's own,
// possibly-ignored, cancellation token — that force-exits the process if it is still alive once
// the budget elapses. If graceful teardown completes first, RunCommand.ExecuteAsync disposes this
// instance, cancelling the timer before it can ever fire. Budget, force-exit action and the delay
// itself are all injectable, so this is fully unit-testable without a real Environment.Exit, a
// real 30-second wait, or a row that can only assert the deadline/teardown race by out-sleeping it.

namespace Vouchfx.Cli;

/// <summary>
/// A one-shot, wall-clock force-exit timer: <see cref="Arm"/> starts it; if the process is still
/// alive when the configured budget elapses, the configured force-exit delegate is invoked.
/// Disposing before that cancels the timer so it never fires.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately independent of any RUN cancellation token: the whole point of this type is to
/// terminate the process even while something inside the run is ignoring cancellation, so its
/// internal timer is bound to its OWN <see cref="CancellationTokenSource"/>, cancelled only by
/// <see cref="DisposeAsync"/> — never by the run's own token.
/// </para>
/// <para>
/// <see cref="Arm"/> is idempotent (only the FIRST call starts the timer) and safe to call
/// concurrently with <see cref="DisposeAsync"/>: both are serialised on a private lock, so an EOF
/// callback racing the run's own normal-completion teardown can never leave the timer either
/// double-armed or armed against an already-disposed <see cref="CancellationTokenSource"/> — the
/// exact lazy-token-read race <see cref="StdinShutdownWatcher"/>'s constructor documents and
/// guards against is closed here the same way (the token is captured under the lock, before the
/// timer's first <see langword="await"/>).
/// </para>
/// <para>
/// Unlike <see cref="StdinShutdownWatcher"/>'s real Console-stream read (which a
/// <see cref="CancellationToken"/> cannot always interrupt), <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
/// is a BCL timer whose cancellation support is fully reliable — so <see cref="DisposeAsync"/> can
/// safely <see langword="await"/> a timer task that is still IN that delay, without risking an
/// indefinite block.
/// </para>
/// <para>
/// The DEADLINE and the DISPOSAL are one atomic transition on that same lock, and whichever
/// reaches it first decides. When its delay completes on its own, the timer re-reads
/// <c>_disposed</c> UNDER the lock before firing: if teardown won, the run already finished
/// normally and the timer returns — which is what makes "after <see cref="DisposeAsync"/> returns,
/// the force-exit delegate is never invoked" a property of this type rather than of how the two
/// threads happened to interleave. If the deadline won it CLAIMS the transition instead, because
/// the budget genuinely elapsed before teardown finished and that is a real force-exit, not a
/// spurious one; a later <see cref="DisposeAsync"/> reads that claim and knows the exit is already
/// committed rather than believing it prevented one, so it does NOT await a timer task that is by
/// then sitting inside <see cref="Environment.Exit(int)"/> — which does not return. The delegate is
/// always invoked OUTSIDE the lock, for that same reason.
/// </para>
/// </remarks>
internal sealed class ShutdownBackstop : IAsyncDisposable
{
    private readonly TimeSpan _budget;
    private readonly Action _forceExit;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancelSource = new();
    private Task? _timerTask;
    private bool _disposed;
    private bool _deadlineClaimed;

    /// <summary>Creates a backstop that has not yet started counting down — see <see cref="Arm"/>.</summary>
    /// <param name="budget">
    /// How long the process is given to finish, from the moment <see cref="Arm"/> is called,
    /// before <paramref name="forceExit"/> fires. Production uses
    /// <see cref="RunCommand.TeardownBudgetSeconds"/> — the SAME budget
    /// <c>InvocationConfiguration.ProcessTerminationTimeout</c> (Program.cs) uses for the
    /// Ctrl-C/SIGTERM path, so both termination paths give a wedged run the same grace period.
    /// Tests inject a small delay so the budget never needs to be waited out for real.
    /// </param>
    /// <param name="forceExit">
    /// Invoked at most once, on a background thread, if the budget elapses before
    /// <see cref="DisposeAsync"/> cancels the timer first. Production passes
    /// <c>() =&gt; Environment.Exit(ExitCodes.Inconclusive)</c> — see
    /// <see cref="RunCommand.ExecuteAsync"/>'s wiring for the exit-code rationale. Tests pass a
    /// side-effect-only delegate (recording that it fired) — NEVER the real
    /// <see cref="Environment.Exit(int)"/>, which would tear down the test process itself.
    /// </param>
    /// <param name="delay">
    /// How the wall-clock wait itself is performed; defaults to
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>, which is what production always uses.
    /// A test overrides it to complete the deadline ON DEMAND — in particular AFTER
    /// <see cref="DisposeAsync"/> has already claimed the lock — which is the only way to assert
    /// the deadline/teardown transition deterministically instead of by out-sleeping a race. A
    /// substitute is not obliged to observe the token: the timer treats any faulted or cancelled
    /// delay as "never force-exit".
    /// </param>
    public ShutdownBackstop(
        TimeSpan budget,
        Action forceExit,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _budget = budget;
        _forceExit = forceExit;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// Starts the wall-clock countdown. The FIRST call arms it; every subsequent call — including
    /// one racing a concurrent <see cref="DisposeAsync"/> — is a silent no-op, since the stdin
    /// watcher's EOF callback only ever needs to arm this once.
    /// </summary>
    public void Arm()
    {
        lock (_gate)
        {
            if (_disposed || _timerTask is not null)
            {
                return;
            }

            // Captured HERE, under the lock and before RunAsync's first `await` — mirrors
            // StdinShutdownWatcher's constructor: reading `_cancelSource.Token` any later (e.g.
            // lazily inside a scheduled continuation) risks the source having been disposed by a
            // concurrent DisposeAsync first. The CancellationToken VALUE remains perfectly usable
            // even after its source is later disposed.
            var cancelToken = _cancelSource!.Token;
            _timerTask = RunAsync(cancelToken);
        }
    }

    private async Task RunAsync(CancellationToken cancelToken)
    {
        try
        {
            await _delay(_budget, cancelToken).ConfigureAwait(false);
        }
        catch
        {
            // Cancelled (DisposeAsync ran first — graceful shutdown completed within budget) or
            // any other fault: either way, never force-exit. Task.Delay's cancellation is fully
            // reliable (see the class remarks), so this branch is taken promptly, not eventually.
            return;
        }

        lock (_gate)
        {
            // THE DEADLINE AND THE DISPOSAL ARE ONE ATOMIC TRANSITION, AND CANCELLATION ALONE DOES
            // NOT DECIDE IT. A delay that completes on its own the instant before DisposeAsync
            // takes this lock leaves this continuation parked here while teardown cancels, marks
            // the instance disposed and returns — so without this re-read the delegate fires over
            // a run that FINISHED, and in production that delegate exits the process Inconclusive.
            if (_disposed)
            {
                return;
            }

            // The deadline reached the transition first: the budget really did elapse before
            // teardown completed, so this is a genuine force-exit. Claim it under the same lock so
            // a later DisposeAsync knows the exit is committed rather than believing it prevented
            // one — see DisposeAsync, which must then not wait on this task.
            _deadlineClaimed = true;
        }

        try
        {
            // OUTSIDE the lock, always: in production this is Environment.Exit, which does not
            // return, and holding _gate across it would wedge every concurrent Arm/DisposeAsync.
            _forceExit();
        }
        catch
        {
            // A background timer must never itself crash the process on the way to (possibly)
            // ending it deliberately.
        }
    }

    /// <summary>
    /// Cancels the timer (if armed) before it can fire, and releases its
    /// <see cref="CancellationTokenSource"/>. Idempotent; never throws.
    /// </summary>
    /// <remarks>
    /// <strong>THE GUARANTEE IS ABOUT THE TRANSITION, NOT ABOUT THE RETURN.</strong> A timer that
    /// had NOT claimed the transition by the time this took the gate never invokes the force-exit
    /// delegate: it reads <c>_disposed</c> at the gate and returns. A timer that HAD claimed it
    /// does invoke the delegate, and may do so AFTER this method has returned — the claim is
    /// released before the call, precisely because that call does not return in production.
    /// Disposal neither waits for it nor revokes it, and revoking would be the wrong behaviour:
    /// the budget really did elapse before teardown reached the gate, so it is a genuine
    /// force-exit rather than one racing a run that had finished. An earlier version of this
    /// summary promised the delegate was never invoked once this returned, which was false for
    /// exactly that branch and contradicted the claim comment in <c>RunAsync</c>.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        Task? timerTask;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cancelSource!.Cancel();

            // Setting _disposed above is what makes the guarantee: any timer whose delay has
            // already completed but has not yet reached the lock will read it there and return
            // without firing. A timer that DID reach the lock first claimed the transition, and
            // that claim is the one case this must not wait on — see below.
            timerTask = _deadlineClaimed ? null : _timerTask;
        }

        if (timerTask is not null)
        {
            // Safe to await: an unclaimed timer is by definition still inside its delay, and
            // Task.Delay always honours cancellation promptly (see the class remarks) — unlike
            // StdinShutdownWatcher's real Console read, this can never block DisposeAsync
            // indefinitely. A CLAIMED timer is a different animal — it is already inside the
            // force-exit delegate, i.e. Environment.Exit in production, which never returns — so
            // it is deliberately not awaited at all.
            try
            {
                await timerTask.ConfigureAwait(false);
            }
            catch
            {
                // RunAsync already swallows its own faults; belt-and-braces only.
            }
        }

        lock (_gate)
        {
            _cancelSource?.Dispose();
            _cancelSource = null;
        }
    }
}
