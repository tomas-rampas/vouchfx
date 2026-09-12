// Vouchfx.Cli.Tests — ShutdownBackstop tests (vouchfx-mcp#17; security-review MAJOR-1 fix). No
// Docker, no Console, no real Environment.Exit.
//
// ShutdownBackstop is the wall-clock force-exit backstop the stdin-EOF graceful-shutdown seam
// arms BEFORE cancelling its linked CancellationTokenSource: CreateLinkedTokenSource only
// propagates cancellation DOWNSTREAM, so on its own it can never engage System.CommandLine's
// ProcessTerminationTimeout watchdog (armed only by a real OS Ctrl-C/SIGTERM) — a run wedged
// somewhere that ignores cancellation would otherwise hang forever once stdin closes. These tests
// drive it entirely via its injectable seam (a small TimeSpan budget + a plain Action instead of
// the real Environment.Exit), covering exactly the three scenarios the design must get right:
//   - EOF + a run that never completes → the force-exit action fires after the budget.
//   - EOF + a run that completes quickly → the force-exit action never fires (Dispose cancels it).
//   - Never armed (flag on, no EOF) → the force-exit action never fires.
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
