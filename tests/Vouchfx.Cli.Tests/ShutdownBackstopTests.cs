// Vouchfx.Cli.Tests — ShutdownBackstop tests (vouchfx-mcp#17; security-review MAJOR-1 fix). No
// Docker, no Console, no real Environment.Exit.
//
// ShutdownBackstop is the wall-clock force-exit backstop the stdin-EOF graceful-shutdown seam
// arms BEFORE cancelling its linked CancellationTokenSource: CreateLinkedTokenSource only
// propagates cancellation DOWNSTREAM, so on its own it can never engage System.CommandLine's
// ProcessTerminationTimeout watchdog (armed only by a real OS Ctrl-C/SIGTERM) — a run wedged
// somewhere that ignores cancellation would otherwise hang forever once stdin closes. These tests
// drive it entirely via its injectable seams (a small TimeSpan budget, a plain Action instead of
// the real Environment.Exit and — for the two rows that pin the deadline/teardown transition — the
// delay itself, so the budget elapses on demand rather than by the clock), covering exactly the
// four scenarios the design must get right:
//   - EOF + a run that never completes → the force-exit action fires after the budget.
//   - EOF + a run that completes quickly → the force-exit action never fires (Dispose cancels it).
//   - Never armed (flag on, no EOF) → the force-exit action never fires.
//   - EOF + a budget elapsing in the same instant as teardown → the two race for one lock and the
//     winner decides: teardown first means the action never fires, the deadline first means the
//     exit is committed and disposal must not wait on it.
//
// The LAST test in the file is none of those three: it is a source census over RunCommand.cs
// asserting that the EOF callback arms this backstop before it cancels. That ordering is the
// caller's guarantee, not this type's — every behavioural row here stays green while the callback
// arms too late to matter — and the failure it guards is a thread race no test can force. See its
// own remarks.

using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

    /// <summary>
    /// A budget that elapses AFTER <see cref="ShutdownBackstop.DisposeAsync"/> has taken the lock
    /// still never force-exits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The row <c>Arm_ThenDisposedBeforeBudgetElapses…</c> cannot see this.</strong> That
    /// one pins the easy half — the delay is still PENDING when disposal cancels it, so
    /// cancellation alone decides. The half that was broken is the other one: a delay that
    /// completes on its own the instant before teardown, leaving the timer's continuation parked
    /// between "the budget elapsed" and "fire" while <c>DisposeAsync</c> cancels, marks the
    /// instance disposed and returns. The force-exit delegate then fired over a run that FINISHED
    /// NORMALLY — <c>Environment.Exit(ExitCodes.Inconclusive)</c> in production, so a completed
    /// run reported Inconclusive.
    /// </para>
    /// <para>
    /// <strong>Deterministic, not timed.</strong> The delay is injected rather than clocked, so
    /// this row does not out-sleep a race and hope: <c>DisposeAsync</c> is an async method and
    /// therefore runs synchronously until its first await — the await OF the timer task — which
    /// means that by the time the call below hands back its <see cref="ValueTask"/>, disposal has
    /// already taken the lock and set its flag. Completing the deadline only afterwards pins
    /// exactly the losing interleaving, every run. The injected delay deliberately IGNORES the
    /// token: were it to observe cancellation, the timer would exit through its cancelled-delay
    /// path and this row would pass against the unfixed code, proving nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Deadline_ElapsingAfterDisposalTookTheLock_ForceExitNeverFires()
    {
        var deadline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var invokeCount = 0;
        var backstop = new ShutdownBackstop(
            SmallBudget,
            () => Interlocked.Increment(ref invokeCount),
            (_, _) => deadline.Task);

        backstop.Arm();

        var disposal = backstop.DisposeAsync();
        deadline.SetResult(true);
        await disposal.AsTask().WaitAsync(WaitBound);

        Assert.Equal(0, Volatile.Read(ref invokeCount));
    }

    /// <summary>
    /// A deadline that wins the transition COMMITS the exit, and disposal does not wait on it.
    /// </summary>
    /// <remarks>
    /// The other side of the same lock. Here the budget elapses first, so the force-exit is
    /// genuine and goes ahead; the timer task is then parked INSIDE that delegate —
    /// <see cref="Environment.Exit(int)"/> in production, which does not return. A
    /// <c>DisposeAsync</c> that awaited the timer task unconditionally would block there for as
    /// long as the process took to die, so it must read the claim instead and return. The blocking
    /// force-exit delegate below stands in for that never-returning exit; a regression shows up as
    /// this row timing out on <c>WaitAsync</c>, not as a hung suite.
    /// </remarks>
    [Fact]
    public async Task Deadline_ClaimedBeforeDisposal_DisposeDoesNotWaitOnTheCommittedExit()
    {
        var deadline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backstop = new ShutdownBackstop(
            SmallBudget,
            () =>
            {
                exitEntered.TrySetResult(true);
                releaseExit.Task.GetAwaiter().GetResult();
            },
            (_, _) => deadline.Task);

        try
        {
            backstop.Arm();

            // Completed only AFTER Arm has returned, so the timer resumes on the thread pool and
            // never runs the blocking delegate inline under Arm's own lock.
            deadline.SetResult(true);

            var entered = await Task.WhenAny(exitEntered.Task, Task.Delay(WaitBound));
            Assert.Same(exitEntered.Task, entered);

            await backstop.DisposeAsync().AsTask().WaitAsync(WaitBound);
        }
        finally
        {
            releaseExit.TrySetResult(true);
        }
    }

    /// <summary>
    /// The stdin-EOF callback ARMS this backstop before it cancels the linked source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A SOURCE CENSUS, BECAUSE THE DEFECT IS A RACE AND THE OTHER ROWS IN THIS FILE
    /// CANNOT SEE IT.</strong> <c>CancellationTokenSource.Cancel</c> runs its registrations
    /// synchronously and can resume the awaited pipeline before the next statement in the callback
    /// is reached, so with the arm SECOND <c>RunCommand.ExecuteCoreAsync</c> may unwind and dispose
    /// this backstop first — after which <c>Arm()</c> is a no-op by design (see
    /// <c>Arm_CalledMultipleTimes…</c> and the type's own remarks) and the force-exit budget is
    /// silently never armed at all. Every row above drives <see cref="ShutdownBackstop"/> directly
    /// and would stay green through that, because the type behaves correctly; it is the CALLER's
    /// ordering that is the guarantee.
    /// </para>
    /// <para>
    /// <strong>Why not a behavioural row.</strong> Reaching the callback means a real stdin —
    /// <c>RunCommand.ExecuteCoreAsync</c> passes <c>Console.OpenStandardInput()</c>, which
    /// <c>Console.SetIn</c> does not redirect. What no test can force is the RACE: a resumption
    /// landing between the two statements. The ORDERING is a different property and is not
    /// unobservable in principle — were that lambda an internal factory taking the source and the
    /// backstop, a row could register a continuation on the token that asks "is the backstop
    /// armed?" while <c>Cancel()</c> runs its registrations. That refactor is not made, so the
    /// ordering is pinned HERE, by the statement order in the source, rather than by a behavioural
    /// row.
    /// </para>
    /// <para>
    /// VACUITY FIRST: the single <c>StdinShutdownWatcher.Start</c> call site and both statements
    /// are asserted to exist before any conclusion is drawn from their order. A census that stops
    /// finding its needle otherwise reports no offence and passes for free.
    /// </para>
    /// </remarks>
    [Fact]
    public void StdinEofCallback_ArmsTheBackstop_BeforeCancellingTheLinkedSource()
    {
        var root = ParsedRunCommand();

        var starts = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "Start",
                Expression: IdentifierNameSyntax { Identifier.ValueText: "StdinShutdownWatcher" },
            })
            .ToList();

        Assert.True(
            starts.Count == 1,
            $"Expected exactly 1 StdinShutdownWatcher.Start call in RunCommand.cs, found "
            + $"{starts.Count}. Zero means this census stopped matching and guards nothing.");

        var callback = starts[0].ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<AnonymousFunctionExpressionSyntax>()
            .SingleOrDefault();

        Assert.True(
            callback?.Block is not null,
            "The EOF callback is no longer a lambda with a statement block, so its statement order "
            + "cannot be read here. Re-express this census against whatever replaced it.");

        var statements = callback!.Block!.Statements;
        var armIndex = IndexOfCallTo(statements, "Arm");
        var cancelIndex = IndexOfCallTo(statements, "Cancel");

        Assert.True(armIndex >= 0, "The EOF callback no longer calls Arm(). The force-exit budget "
            + "the --shutdown-on-stdin-eof flag promises is then never armed at all.");
        Assert.True(cancelIndex >= 0, "The EOF callback no longer calls Cancel(). Nothing "
            + "downstream then observes the graceful stop.");

        Assert.True(
            armIndex < cancelIndex,
            "The EOF callback cancels the linked source before arming the backstop. Cancel() can "
            + "resume the awaited pipeline synchronously, so ExecuteCoreAsync may dispose the "
            + "backstop before Arm() runs — and Arm() on a disposed backstop is a silent no-op, "
            + "leaving a provider or teardown that ignores cancellation to outlive the force-exit "
            + "budget. Arm first.");
    }

    /// <summary>
    /// The index of the first statement invoking a member called <paramref name="member"/>, or -1.
    /// </summary>
    private static int IndexOfCallTo(SyntaxList<StatementSyntax> statements, string member)
    {
        for (var i = 0; i < statements.Count; i++)
        {
            var found = statements[i].DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Any(invocation => invocation.Expression is MemberAccessExpressionSyntax access
                    && access.Name.Identifier.ValueText == member);

            if (found)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The parsed syntax tree of the CLI's <c>RunCommand.cs</c>.</summary>
    private static CompilationUnitSyntax ParsedRunCommand()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        var path = Path.Combine(
            dir!.FullName, "src", "Cli", "Vouchfx.Cli", "RunCommand.cs");

        Assert.True(File.Exists(path), $"'{path}' not found; this census cannot run.");

        return CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)
            .GetCompilationUnitRoot();
    }
}
