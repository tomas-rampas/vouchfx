// Vouchfx.Cli.Tests — ShutdownBackstop tests (vouchfx-mcp#17; security-review MAJOR-1 fix). No
// Docker, no Console, no real Environment.Exit.
//
// ShutdownBackstop is the wall-clock force-exit backstop the stdin-EOF graceful-shutdown seam
// arms alongside cancelling its linked CancellationTokenSource: CreateLinkedTokenSource only
// propagates cancellation DOWNSTREAM, so on its own it can never engage System.CommandLine's
// ProcessTerminationTimeout watchdog (armed only by a real OS Ctrl-C/SIGTERM) — a run wedged
// somewhere that ignores cancellation would otherwise hang forever once stdin closes. These tests
// drive it entirely via its injectable seam (a small TimeSpan budget + a plain Action instead of
// the real Environment.Exit), covering exactly the three scenarios the design must get right:
//   - EOF + a run that never completes → the force-exit action fires after the budget.
//   - EOF + a run that completes quickly → the force-exit action never fires (Dispose cancels it).
//   - Never armed (flag on, no EOF) → the force-exit action never fires.

using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ShutdownBackstopTests
{
    private static readonly TimeSpan SmallBudget = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan WaitBound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Arm_RunNeverCompletes_ForceExitFiresAfterBudget()
    {
        // "EOF + a run that never completes → the force-exit action is invoked after the budget."
        var forceExitSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backstop = new ShutdownBackstop(SmallBudget, () => forceExitSignal.TrySetResult(true));

        backstop.Arm();

        var completed = await Task.WhenAny(forceExitSignal.Task, Task.Delay(WaitBound));

        Assert.Same(forceExitSignal.Task, completed);
        Assert.True(await forceExitSignal.Task);
    }

    [Fact]
    public async Task Arm_ThenDisposedBeforeBudgetElapses_ForceExitNeverFires()
    {
        // "EOF + a run that completes quickly → force-exit action NOT invoked." Disposing
        // (mirroring RunCommand.ExecuteAsync's normal-completion teardown) must cancel the timer
        // before it can fire.
        var invoked = false;
        var backstop = new ShutdownBackstop(SmallBudget, () => invoked = true);

        backstop.Arm();
        await backstop.DisposeAsync();

        // Wait comfortably past the budget so a late, erroneous fire would have had time to happen.
        await Task.Delay(SmallBudget + SmallBudget);

        Assert.False(invoked);
    }

    [Fact]
    public async Task NeverArmed_DisposeAsync_ForceExitNeverFires()
    {
        // "flag on + no EOF → force-exit action NOT invoked." Arm() is only ever called from the
        // stdin-EOF callback; a run that finishes normally without EOF never arms the timer at all.
        var invoked = false;
        var backstop = new ShutdownBackstop(SmallBudget, () => invoked = true);

        await backstop.DisposeAsync();

        await Task.Delay(SmallBudget + SmallBudget);

        Assert.False(invoked);
    }

    [Fact]
    public async Task Arm_CalledMultipleTimes_OnlyStartsOneTimer_ForceExitFiresOnce()
    {
        // The stdin watcher's callback only ever needs to arm this once, but a second call must
        // be a harmless no-op — never a second, independent timer.
        var invokeCount = 0;
        var firstFireSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backstop = new ShutdownBackstop(SmallBudget, () =>
        {
            if (Interlocked.Increment(ref invokeCount) == 1)
            {
                firstFireSignal.TrySetResult(true);
            }
        });

        backstop.Arm();
        backstop.Arm();
        backstop.Arm();

        // Wait DETERMINISTICALLY for the first fire (bounded by WaitBound, not a fixed sleep),
        // then only a SHORT settle period comfortably longer than the budget — long enough for a
        // hypothetical second, spurious timer to also have fired by then, but far cheaper than
        // unconditionally waiting the full WaitBound regardless of how fast the first fire was.
        var completed = await Task.WhenAny(firstFireSignal.Task, Task.Delay(WaitBound));
        Assert.Same(firstFireSignal.Task, completed);

        await Task.Delay(SmallBudget + SmallBudget);

        Assert.Equal(1, invokeCount);
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsIdempotent_DoesNotThrow()
    {
        var backstop = new ShutdownBackstop(SmallBudget, () => { });

        await backstop.DisposeAsync();
        await backstop.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_AfterForceExitAlreadyFired_IsIdempotent_DoesNotThrow()
    {
        var forceExitSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backstop = new ShutdownBackstop(SmallBudget, () => forceExitSignal.TrySetResult(true));

        backstop.Arm();
        var completed = await Task.WhenAny(forceExitSignal.Task, Task.Delay(WaitBound));
        Assert.Same(forceExitSignal.Task, completed);
        Assert.True(await forceExitSignal.Task);

        // Disposing AFTER the timer has already fired must still be silent and safe.
        await backstop.DisposeAsync();
    }
}
