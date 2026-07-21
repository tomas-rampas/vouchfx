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
// instance, cancelling the timer before it can ever fire. Budget and force-exit action are both
// injectable so this is fully unit-testable without a real Environment.Exit or a real 30-second
// wait.

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
/// safely <see langword="await"/> the timer task to completion without risking an indefinite
/// block.
/// </para>
/// </remarks>
internal sealed class ShutdownBackstop : IAsyncDisposable
{
    private readonly TimeSpan _budget;
    private readonly Action _forceExit;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancelSource = new();
    private Task? _timerTask;
    private bool _disposed;

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
    public ShutdownBackstop(TimeSpan budget, Action forceExit)
    {
        _budget = budget;
        _forceExit = forceExit;
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
            await Task.Delay(_budget, cancelToken).ConfigureAwait(false);
        }
        catch
        {
            // Cancelled (DisposeAsync ran first — graceful shutdown completed within budget) or
            // any other fault: either way, never force-exit. Task.Delay's cancellation is fully
            // reliable (see the class remarks), so this branch is taken promptly, not eventually.
            return;
        }

        try
        {
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
            timerTask = _timerTask;
        }

        if (timerTask is not null)
        {
            // Safe to await unconditionally: Task.Delay always honours cancellation promptly (see
            // the class remarks) — unlike StdinShutdownWatcher's real Console read, this can
            // never block DisposeAsync indefinitely.
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
