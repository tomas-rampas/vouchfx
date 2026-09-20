// Vouchfx.Cli.Tests — SystemProcessRunner process lifetime and bounding (#481).
//
// SystemProcessRunner is the single production IProcessRunner. Three defects lived in it, and
// these rows are the cover that keeps each one closed:
//
//   1. The Process was never disposed on any path — a handle leak per call.
//   2. The launch sat outside any try that owned cleanup, so a throw out of ReadToEnd() or
//      WaitForExit() abandoned a live child with nothing holding it.
//   3. WaitForExit() was unbounded — and, more to the point, `StandardOutput.ReadToEnd()` is the
//      FIRST blocking call and therefore the real hang site. Issue #392 measured exactly that
//      shape: a child that exits promptly while leaving a grandchild holding the inherited pipe
//      handles leaves the reads pending indefinitely, so the pending read, not the pending exit,
//      is what wedges the caller.
//
// WHY THESE ROWS CANNOT SIMPLY CALL Run AND WAIT
// ──────────────────────────────────────────────
// This assembly is a blocking CI gate and neither it nor xunit imposes a per-test timeout. A row
// that called Run against a child which never releases the pipes would not FAIL, it would HANG the
// gate — a regression that wedges CI is worse than no cover at all. So every hang row launches Run
// on a background Task and asserts FIRST that the task COMPLETED inside a bounded wall-clock
// window, and only then inspects what it completed with. Ordering the two assertions that way is
// what keeps a re-broken runner a ten-second failure rather than an infinite one.
//
// The abandoned child is killed by the TEST, in a `finally`, on every path WHERE A PID WAS
// CAPTURED — including the path where a later assertion has already thrown. A failing run must not
// leak a live child onto the CI agent. The child announces its own pid into a scratch file
// precisely so the teardown has something to kill: Run hands back no handle, and there is no
// portable way to recover one after the fact. ChildProcess.KillTreeQuietly (Vouchfx.TestSupport) is
// the shared guarded tree-kill. The gap is the pid assertion itself: if the child never publishes a
// pid, teardown has nothing to kill and the only backstop is the child's own bounded
// ChildLifetimeSeconds, which is why that lifetime is finite rather than infinite.
//
// EVERY ROW THAT CAN REACH THAT GAP TAKES ONE LATE LOOK BEFORE ITS CHILD BECOMES UNREACHABLE — rows
// 1 and 2 in their `finally`, row 5 in front of its pid assertion and, on the paths that end it
// before that, in its `finally` too, and the two rows that launch a shape directly
// (PidFileWriters_PublishAPidTheReaderAccepts, TheGuardsAnchors_StayInTheOrderARealChildProduces)
// in theirs —
// so the slice of the gap where the write was merely LATE is closed, because a late write becomes
// observable and therefore killable. See
// ReclaimPidForTeardownAsync. What remains of the gap is a child that never writes a pid AT ALL, and
// ChildLifetimeSeconds is still its only backstop.
//
// SINCE #524 THAT GAP IS WIDER BY A FACTOR OF BudgetAttempts, and both escalating rows sit in it,
// not just the one whose remarks mention it. An attempt that ends without a pid is retried, so rows
// 1 and 2 can each leave up to BudgetAttempts unannounced children behind — worst case four per
// row, eight per run of this file. The two directly-launching rows add at most one more EACH, and
// only on their GRANDCHILD parametrisation: a `ping` whose pid never landed is named by this file
// alone, so KillTreeQuietly(null) kills nothing and ChildLifetime is again the only backstop.
// Their CHILD parametrisations carry no such residual, and that asymmetry is what launching
// directly buys — the handle reaches the shell whatever the file says, so the only shape that can
// strand a process is the one whose survivor is a GRANDCHILD.
//
// THOSE CHILDREN WERE ALL DESCRIBED ABOVE AS "a child SystemProcessRunner tree-killed" UNTIL PR
// #532, AND THAT WAS AN ASSUMPTION WEARING A FACT'S CLOTHES. Run only ever ISSUES the kill, and
// an attempt that saw no pid never looked at the child at all — not looking is not evidence it
// died. Follow the shape the review named: the runner's tree-kill is broken AND the child's first
// write is merely late, so the attempt hands back `false` over an empty pid file, the `finally`
// calls KillTreeQuietly(null) and kills nothing, and that child is now live and unreferenced. The
// NEXT attempt gets a pid, its kill happens to work, every assertion passes and the row goes GREEN
// — a leak row passing over a leak it caused. That is an assertion passing for a reason it does not
// claim, not a housekeeping wrinkle.
//
// SO TEARDOWN LOOKS ONCE MORE BEFORE IT GIVES UP, AND ON ROWS 1 AND 2 IT IS THE `finally` THAT LOOKS
// (#539). ReclaimPidForTeardownQuietlyAsync re-polls the pid file for LateReadWindow and hands
// whatever lands to the KillTreeQuietly a line below it — the quiet wrapper is what those two
// `finally` blocks call, ReclaimPidForTeardownAsync being the body form row 5 still uses. That is
// exactly the mechanism above: a late write becomes observable and therefore killable. THE PLACEMENT
// IS THE WHOLE OF #539: the look used to sit on the no-pid return, so it ran only when the attempt
// ended by REACHING that return. An attempt can instead end at `Assert.True(finished, …)` — the
// wedged-runner finding these rows exist to make — and that path skipped the look entirely, killed
// nothing, and left a child licensed to live for ChildLifetime. A `finally` cannot be skipped by an
// assertion, so siting the look there covers both endings without asking which one happened. What
// survives is narrower and is stated at that method — a child that publishes NO pid inside the
// widened window and whose kill also failed. Nothing in this file can name a process that never
// named itself, so ChildLifetime remains the ceiling on that residual, which is why that lifetime is
// finite rather than infinite.
//
// THAT PLACEMENT IS PINNED BY THREE ROWS RATHER THAN ASSERTED HERE, AND BETWEEN THEM THEY LEAVE
// ONE HALF OPEN. EveryFinallyThatKills_ReclaimsAPidFirst is a Roslyn census over this file's own
// source: each killing `finally` must carry a `pid is null`-guarded reclaim ahead of its kill, and
// there must be exactly five of them. TheLateLookFlag_IsArmedOnlyAfterTheLookCompletes is its
// counterpart in the BODY: row 5's `lateLookTaken = true` must sit after the loud look it records,
// because a flag armed first suppresses the `finally`'s only look on the path where that look
// throws. The third row pins the quiet wrapper the placement census names — a pid
// line left TORN and completed a second later is picked up inside LateReadWindow, so the reader
// has to look again after refusing one, and a file that never appears costs the whole window and
// yields null. What NONE of them establishes is that the kill a line below reaches
// a LIVE survivor: that half needs a child which defeats the production tree-kill (WMI re-parenting
// on Windows, setsid on Linux) and no portable one exists, so it was measured once as a
// before/after drill and recorded in #539's commit message (ebddeca) rather than carried as a
// permanent row. Each of the two rows states this at its own declaration too.
//
// THE SAME MULTIPLIER APPLIES TO SCRATCH DIRECTORIES, and that one leaves litter on disk rather
// than in the process table. Every attempt calls CreateScratchDirectory, and TryDeleteDirectory is
// best-effort by design: on Windows a child still holding the directory as its working directory
// makes Directory.Delete throw, and the catch swallows it so that teardown never replaces the real
// failure with its own. So a bad run can leave up to BudgetAttempts %TEMP%\vouchfx-procrunner-*
// directories per escalating row — eight, where before the escalation it was two. Each is a few
// bytes holding at most a pid file. It is named here because this repository sweeps after a run as
// a standing rule, and a sweep is only reliable if the thing being swept has been written down.
//
// The two directly-launching rows can each leave one more directory per parametrisation, so the
// four of them add as many as four to the eight above. Their `finally` attempts the delete on
// EVERY path, and
// what decides the outcome is whether the process holding the directory has gone by then. Two
// mechanisms sit in front of that delete and they cover different paths. A kill only ISSUES a
// termination (see DeathWindow), so after killing the row polls until the target is gone, for at
// most DeathWindow — which covers a path where the kill HAD a target, and is the green one. The
// poll samples before it sleeps, so a target already gone ends it without a sleep; DeathWindow is
// the ceiling on that wait and not its cost. Where no pid ever landed there is no
// target: KillTreeQuietly(null) kills nothing, the wait returns on its first sample, and on the
// grandchild shape the `ping` still holding that directory is precisely what nothing here can
// name.
//
// MEASURED on the maintainer's Windows host, 2026-09-20, before and after that wait was added, and
// it moved NONE of these figures because every path that leaks is a no-pid path: the green run of
// the whole class left none, the writer-mutation drill left one — the grandchild row's, that row
// alone leaving one and the child row alone none — and the reader-mutation drill over the whole
// class left six. What the wait closes is the kill-then-delete race on the paths that DO have a
// target, which has not been observed to fire. Those figures are #541's, taken when it was the
// only directly-launching row; #529's row is built the same way and re-measured green at none.
//
// WHAT IS NOT COVERED HERE, STATED RATHER THAN IMPLIED
// ───────────────────────────────────────────────────
// A read that FAULTS mid-capture (issue #481's closing request) has no row in this file. Provoking
// a genuine fault on a pending anonymous-pipe read needs a handle these rows do not own: the pipe
// belongs to the Process the runner created and never hands out, and closing it from outside would
// mean either a production seam that exists only for a test or reaching into Process's private
// state. What IS covered is the half that turned the fault into a crash — the mapping: `RunGit`
// caught only ProcessLaunchException and ProcessTimeoutException, so a faulted read escaped as a
// raw IOException, and GitChangeSetTests.GitOutputCaptureFails_SurfacesChangeSetException_NotCrash
// pins the new ProcessCaptureException to a ChangeSetException. The runner's own conversion of the
// faulted read into that type is covered by inspection only.
//
// THE BUDGET IS INJECTED, NOT INHERITED
// ─────────────────────────────────────
// Rows 1 and 2 construct their own runner rather than using SystemProcessRunner.Instance, which
// carries the production ceiling (minutes). Coupling a test's wall-clock to the production ceiling
// would make this file slow in order to prove nothing extra, and it would force the grace window to
// track a constant chosen for a cold `git status` on a huge repository. Rows 3, 4, 4b, 6 and 7 keep
// using Instance: they exercise the happy path, the two launch-failure assertions, the argument
// guard and the confined environment, where the budget is never approached and the shared instance
// is the thing shipped. Row 5
// injects a budget for the OPPOSITE reason — one so long it cannot be reached, so that a call
// which ends is one the cancellation token ended.
//
// …AND ON ROWS 1 AND 2 IT IS ESCALATED RATHER THAN GUESSED (#524)
// ───────────────────────────────────────────────────────────────
// WHAT THE INJECTED BUDGET IS RACING. Rows 1 and 2 judge nothing until the child has published its
// pid, and the runner tree-kills that child the moment the budget expires. Two clocks therefore run
// against each other: the child has from Process.Start until the budget expires to be scheduled and
// reach its first write, and if the kill gets there first the pid file is never written AT ALL. No
// length of pid wait recovers that — there is nothing left alive to do the writing. MEASURED rather
// than reasoned: with the budget cut to 50ms, the row fails after twenty seconds of polling with
// the scratch directory still EMPTY (`pidFileExists=False dirEntries=[]`). So a fixed budget is a
// bet that the operating system will schedule an unrelated process inside a constant, and #524 is
// that bet losing under load.
//
// WHY NO CONSTANT IS THE RIGHT ONE, AND THE ANSWER IS THE TAIL. The old budget was three seconds,
// chosen as roughly fifteen times a warm start of 190-220ms. That start was re-measured on the
// maintainer's 20-core host at four load levels, eight samples each, taken twenty seconds after the
// load had settled:
//
//     idle           154-190ms
//     32 burners     532-649ms
//     64 burners     1092-1230ms
//     128 burners    2335-2511ms in six samples, and 9100ms and 13627ms in the other two
//
// READ THAT AS A LOWER BOUND ON THE SPREAD AND AS NOTHING ELSE. Eight samples cannot bound a tail,
// and this probe demonstrably failed to find the one that matters: #524 was filed from 32 burners
// on a 20-core host, one run in nine red. At THAT load — the second line, where the probe's worst
// reading was 649ms — the real distribution therefore reaches past three seconds often enough to
// redden roughly one run in nine. The probe never saw it. The 128-burner line is the same
// phenomenon caught in the act rather than a different one, and it is quoted here only because two
// samples in eight happening to land at 9.1s and 13.6s is what a heavy tail looks like when a small
// sample does catch it.
//
// So the table establishes that start-up under contention is heavy-tailed and that eight samples
// understate it at every load. What it cannot establish is a number — and that is the argument
// against choosing one. A constant sized on samples anybody has collected is sized on the part of
// the distribution that does not cause the failure.
//
// SO THE BUDGET IS MEASURED INSTEAD. An attempt in which the child never announced itself is not a
// verdict about SystemProcessRunner; it is a measurement saying THIS budget was too short for THIS
// host at THIS moment. WithEscalatingBudget treats it as one: it discards that attempt and repeats
// it with the budget doubled. Nothing about the property under test depends on the number — "the
// budget is enforced and the tree is killed" is as true at twenty-four seconds as at three — so
// enlarging it costs the row nothing but time, and only on a host that has just demonstrated it
// needs the time.
//
// THE ESCALATION CANNOT MASK A FAILED ASSERTION, which is the objection that has to be answered
// before a retry is allowed anywhere near a leak test. A larger budget makes the child MORE likely
// to publish its pid, and the pid is precisely what arms the death assertion; the only outcome
// escalation can convert is "the row could not establish its premise" into "the row established its
// premise and judged the runner". It never converts a judgement into a retry: once a pid is in
// hand, the assertions run to completion and a failure among them is final. A run whose every
// attempt failed to establish the premise FAILS; it is never reported as a pass.
//
// THE HEADING ABOVE SAID "CANNOT MASK A LIVE CHILD" UNTIL PR #532, AND THAT WAS A CLAIM ABOUT
// PROCESSES RESTING ON AN ARGUMENT ABOUT ASSERTIONS. The two come apart: a discarded attempt can
// leave a live PROCESS behind even though it cannot leave a live FINDING behind. Most of that is now
// closed by the late look above — a discarded attempt's `finally` reclaims and kills a late-writing
// survivor before the loop sees the discard, and since #539 an attempt that threw instead of
// returning is covered by the same `finally` rather than by nothing at all. What is NOT closed, and
// what the old heading therefore could not have carried even post-fix, is a child that publishes no
// pid at all inside budget+8s and whose kill also failed: escalation still discards it unseen,
// ChildLifetime is still its only backstop, and no wording in this paragraph changes that.
//
// THE PID WAIT ENDS ON AN EVENT, NOT ON A CLOCK
// ─────────────────────────────────────────────
// Waiting a fixed twenty seconds for the pid file was dead time in exactly the case it was written
// for: once Run has returned, the child it killed will never write anything, so the rest of the
// wait polls a corpse. MEASURED, at the drill's own 50ms budget: it waited the full 20.02s with the
// run already settled, so all but about 50ms of that wait had no live target. DERIVED for the
// production budget by the same subtraction: seventeen of the twenty. Seventeen is arithmetic, not
// a reading — nobody probed the 3s case — and it is written down that way here so the two are not
// later quoted as one measurement. WaitForPid now gives up a short settle after the RUN ITSELF has
// settled, so a failed premise is detected in about one budget rather than in a constant, and the
// escalation above is affordable; the one path that deliberately keeps waiting after that is the
// late look, and only once the attempt is over — on rows 1 and 2 in the `finally`, whether the
// attempt gave its premise up or an assertion ended it. Row 5's Run does not
// settle on its own — its budget is unreachable and it is the row that cancels — so that row still
// carries an absolute ceiling, and it is not alone in carrying one: the two rows that launch a
// shape directly (#541, #529) have no Run to settle at all. UnracedPidCeiling records why an
// absolute figure is defensible for all three and was not here.
//
// ROW 6 IS NOT ABOUT #481 AT ALL
// ──────────────────────────────
// It pins the seam's OTHER contract, the one #499 added and left as prose: fileName must be FULLY
// QUALIFIED, because an unqualified name is resolved by the operating system's own search, which on
// Windows reaches the calling executable's directory and the calling process's current directory
// ahead of PATH. #499's defect was a caller passing the bare name `git`, so the requirement is now
// enforced in SystemProcessRunner.Run and asserted here. Rooting the Windows child shapes below
// (powershell.exe, cmd.exe) is a consequence of that guard rather than bookkeeping: until it landed,
// these rows were reaching their own children through the very search the change forbids.
//
// PORTABILITY
// ───────────
// Every row runs on both operating systems; none is skipped anywhere. Row 6 asserts one extra
// spelling on Windows — the drive-relative `C:git`, which POSIX would refuse for an unrelated
// reason — but the row itself runs everywhere. CI is Linux-only today
// (#366) but the maintainer develops on Windows, so an OS-conditional skip would silently retire
// half the coverage on whichever host mattered. The OS branch follows the established pattern in
// Vouchfx.Engine.Orchestration.Tests/ChildProcessKillTreeTests.cs: OperatingSystem.IsWindows()
// selecting a Windows command against a /bin/sh one. PowerShell rather than cmd.exe carries the
// two lifetime shapes because cmd.exe cannot report a pid — neither its own nor a backgrounded
// grandchild's — and a shape whose child cannot be killed in teardown is exactly the leak these
// rows exist to avoid.
//
// No Docker, no trait: these rows belong to the fast `requires!=docker` lane.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Vouchfx.Cli.Selection;
using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Cli.Tests;

/// <summary>
/// Lifetime and bounding cover for <see cref="SystemProcessRunner"/> (#481).
/// </summary>
public sealed class SystemProcessRunnerTests
{
    /// <summary>
    /// Where <see cref="TheGuardsAnchors_StayInTheOrderARealChildProduces"/> reports the skew it
    /// observed.
    /// </summary>
    /// <remarks>
    /// The one row in this file that has something to say on a GREEN run, and the only reason
    /// this class takes a constructor at all. Every other number here reaches a reader through an
    /// assertion message, which a passing row never prints — fine for a figure that only matters
    /// when something is wrong, useless for one whose whole purpose is to arrive from a host the
    /// maintainer cannot measure (#529, the Linux lane).
    /// </remarks>
    private readonly ITestOutputHelper _output;

    public SystemProcessRunnerTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// How long <see cref="SystemProcessRunner.Run"/> is given to do its post-trigger work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure latency of a re-broken runner. It covers only what <c>Run</c> does AFTER
    /// whatever ends the call has already fired: tree-kill the child, dispose two captured streams,
    /// throw. That work is the same however long the call waited first, which is why this is a flat
    /// slack rather than a multiple of anything — see <see cref="GraceFor"/>, where rows that must
    /// also sit out a budget add the budget to it, and row 5, whose token fires at once and which
    /// therefore waits for this and nothing else.
    /// </para>
    /// <para>
    /// Ten seconds for an unwind measured in milliseconds is not a latency target; it is the margin
    /// that keeps a loaded agent from being reported as a hang. A genuine hang is unbounded and so
    /// still fails here.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan RunUnwindSlack = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The budget rows 1 and 2 inject into the runner under test on their FIRST attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Short on purpose: the property under test is "the budget is enforced and the tree is
    /// killed", which is indifferent to the number. The production ceiling
    /// (<see cref="SystemProcessRunner.DefaultBudget"/>) is sized for a cold <c>git status</c> on
    /// a very large repository and is not something a unit test should sit through.
    /// </para>
    /// <para>
    /// <strong>Its floor is not the runner, it is the CHILD</strong> — and that is what makes it a
    /// STARTING point rather than a choice. Both rows wait for the child to publish its pid before
    /// they judge anything, and a budget that expires first kills the child before it ever writes
    /// the file. The window that matters is process start to first write, measured for the Windows
    /// shape (<c>powershell.exe -NoProfile -NonInteractive</c>) on the maintainer's 20-core host at
    /// 154-190ms idle, 532-649ms under 32 CPU burners and 1092-1230ms under 64. Three seconds clears
    /// all of those, which is why this row was usually green. It does NOT clear the tail: at 128
    /// burners six of eight samples sat at 2.3-2.5s but the remaining two took 9.1s and 13.6s.
    /// Three is therefore not defended here as sufficient — nothing is — but as the cheapest budget
    /// that works on a host which is not thrashing, with <see cref="WithEscalatingBudget"/>
    /// supplying the rest when it is. The <c>/bin/sh</c> shape CI runs has NOT been measured at any
    /// load, and no figure is claimed for it — a number carried over from the Windows shape would
    /// inherit an authority it never earned in the lane that gates merges, and the escalation is
    /// what makes that unmeasured gap survivable rather than a second guess.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan StartingBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How many doublings of <see cref="StartingBudget"/> a row may spend establishing its premise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four gives 3s, 6s, 12s and 24s. Sized against the tail rather than the median, because the
    /// tail is what the shorter budgets lose to: the worst start-up measured anywhere in
    /// <see cref="StartingBudget"/>'s figures was 13.6s, at a load of 128 CPU burners against 20
    /// cores, and 24s clears that sample. Four rather than more because the ceiling is
    /// <see cref="ChildLifetime"/> and it is the whole attempt that has to fit under it, not the
    /// budget alone: 24s of budget plus the unwind slack plus the death poll comes to 36s of the
    /// available 60, and one more doubling would not. A host that cannot start a shell inside the
    /// largest budget is thrashing rather than busy, and a red row is then telling the truth about
    /// the host.
    /// </para>
    /// <para>
    /// Paid only on a host that has already failed the shorter budgets — an unloaded run never
    /// leaves the first, so the common case costs exactly what it did before.
    /// </para>
    /// </remarks>
    private const int BudgetAttempts = 4;

    /// <summary>The budget the last attempt uses.</summary>
    /// <remarks>
    /// <para>
    /// Derived rather than written down, so it cannot drift from <see cref="BudgetAttempts"/> or
    /// <see cref="StartingBudget"/>.
    /// </para>
    /// <para>
    /// <strong>A property rather than a <see langword="static"/> <see langword="readonly"/> field,
    /// and not as a matter of taste.</strong> A field initialiser reading
    /// <see cref="StartingBudget"/> would be evaluated in TEXTUAL order: move that declaration below
    /// this one and the field initialises from <see cref="TimeSpan.Zero"/>, leaving this at zero,
    /// the drift guard in <see cref="WithEscalatingBudget"/> satisfied trivially, and the "cannot
    /// drift" above false in the one way nothing would report. A property is evaluated on use, so
    /// declaration order stops mattering at no cost.
    /// </para>
    /// </remarks>
    private static TimeSpan LargestBudget => StartingBudget * (1 << (BudgetAttempts - 1));

    /// <summary>
    /// How long <see cref="WaitForPid"/> keeps polling after <c>Run</c> itself has settled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not zero, because <c>Run</c> returns having only ISSUED the tree-kill — both
    /// <c>TerminateProcess</c> and <c>SIGKILL</c> return once the request is queued (the same fact
    /// <see cref="DeathWindow"/> exists for). A child killed at the instant it was about to write
    /// may still get its write in, and a row that stopped looking the moment <c>Run</c> returned
    /// would discard a pid that was on its way and then have nothing to kill in teardown.
    /// </para>
    /// <para>
    /// Not long either, because on the path where it is actually consumed the child is already dead
    /// and every millisecond of it is waste multiplied by <see cref="BudgetAttempts"/>. Anything
    /// this window misses is caught by the next, larger attempt.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan PidSettleWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How much longer <see cref="ReclaimPidForTeardownAsync"/> looks after a wait has given up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Paid only on a path that has ALREADY failed to observe its child, so a healthy run
    /// never reaches it.</strong> Rows 1 and 2 pay it in their <c>finally</c>, and since #539 that
    /// is two paths rather than one, costing differently. The first is an attempt about to hand its
    /// budget back to be doubled: at most <see cref="BudgetAttempts"/> times, twenty seconds added
    /// to a row already spending forty-five on budgets alone. The second is the one #539 added and
    /// it is RED — an attempt that ended at an assertion with no pid in hand ends the row outright,
    /// so the window is paid once and delays a failure being reported by five seconds. That is
    /// <c>Assert.True(finished, …)</c> against a wedged runner, and equally either of the two
    /// assertions after it (<c>Assert.ThrowsAsync&lt;ProcessTimeoutException&gt;</c> and the budget
    /// equality) against the #512 launch-fault or budget-not-enforced shapes, where the run HAS
    /// settled. On row 5 it is likewise paid at most once, but since #539 at either of two sites:
    /// in the body immediately before an assertion that is going to fail anyway, or — when one of
    /// the two paths that skip that body look ended the row instead — in the <c>finally</c>, after
    /// the failure is already settled. A <c>lateLookTaken</c> flag is what makes it "either" rather
    /// than "both", and it is set only once the body look has RUN TO COMPLETION: the body look
    /// uses the loud reader, so an ACL fault escapes it, and on that path the <c>finally</c> is
    /// meant to look. "At most once" survives that anyway, because the quiet wrapper meets the
    /// same fault on its own first read and answers at once rather than sitting out a window. No
    /// green path on any row reaches it. The fourth and fifth payers, added by #541 and #529, are
    /// the cheapest to account for: <see cref="PidFileWriters_PublishAPidTheReaderAccepts"/> and
    /// <see cref="TheGuardsAnchors_StayInTheOrderARealChildProduces"/> each pay it once, in their
    /// <c>finally</c>, only when no pid arrived — which is that row failing — so it delays a
    /// report by five seconds and buys a grandchild that wrote late a teardown that can name it.
    /// </para>
    /// <para>
    /// It is on no assertion's critical path within its own attempt: on rows 1 and 2 it runs in the
    /// <c>finally</c>, by which point the attempt has already returned or thrown, and on row 5
    /// either in front of an assertion already determined to fail or, from that row's own
    /// <c>finally</c>, after one already has. It does sit ahead of the NEXT attempt, and
    /// on the red path ahead of the report of a failure, so the wall clock it adds is real; what it
    /// cannot do is alter a finding that has already been made.
    /// </para>
    /// <para>
    /// <strong>Five rather than reusing <see cref="PidSettleWindow"/>'s three, because the two are
    /// sized against different children.</strong> <see cref="PidSettleWindow"/> is on the hot path
    /// of every attempt and is sized for a write already in flight from a child that has just been
    /// killed. This one is sized for a child that is still ALIVE because the tree-kill did not reach
    /// it, and whose first write is therefore still ahead of it. The figure that matters is the
    /// TOTAL observation window — the budget, plus <see cref="PidSettleWindow"/>, plus this — which
    /// comes to 11s on the first attempt, 14s on the second, 20s on the third and 32s on the last.
    /// Unmoved by #539 relocating the look into the <c>finally</c>, whenever the wait ended on its
    /// SETTLE arm — which is the clean discarded path, and the two steps now between the wait and
    /// the look return at once there for two DIFFERENT reasons. The grace race returns at once
    /// because that arm only fires <see cref="PidSettleWindow"/> after <c>work</c> completed, so
    /// the race is handed an already-finished task; it is not <c>Assert.True(finished, …)</c> that
    /// makes it immediate, which sits AFTER the race and cannot be its cause. The hoisted
    /// <c>Assert.ThrowsAsync</c> is the step that assertion accounts for: reaching it means
    /// <c>finished</c> was true, so the task it awaits is already complete. A wait that ended on
    /// its CEILING instead (<c>grace + PidSettleWindow</c>) with <c>work</c> still pending is the
    /// corner the first clause excludes: a run settling inside the race's own
    /// <c>Task.Delay(grace)</c> reaches the discarded return having spent real time there — at a 3s
    /// budget, a <c>Run</c> taking 16-29s. That LENGTHENS the observation window rather than
    /// shortening it, so the figures above are a floor and the conclusion they support is safe.
    /// The slowest process start this file has ever measured is 13.6s (128 CPU burners against 20
    /// cores; see <see cref="StartingBudget"/>), so from the SECOND attempt on the window clears
    /// even that sample. The escalation and this window therefore widen together, which is what
    /// makes a constant defensible here where <see cref="StartingBudget"/> argues at length that one
    /// is not defensible for the budget itself. Row 5's arithmetic is simpler and never in doubt:
    /// <see cref="UnracedPidCeiling"/> plus this, 35s, against a child nothing is racing to kill.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan LateReadWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The absolute ceiling on a pid wait whose <c>Run</c> is not going to settle by itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>THREE consumers, and they need a figure for the same reason: there is no
    /// <c>Run</c> whose settling could end the wait.</strong> Row 5's runner carries
    /// <see cref="UnreachableBudget"/> and the row itself is what ends the call, so "wait until
    /// Run settles" would wait for something the row has not done yet. The two rows that launch a
    /// shape directly — <see cref="PidFileWriters_PublishAPidTheReaderAccepts"/> (#541) and
    /// <see cref="TheGuardsAnchors_StayInTheOrderARealChildProduces"/> (#529) — have no
    /// <c>Run</c> at all, and each passes this as BOTH of <see cref="WaitForPid"/>'s windows; the
    /// first of them also quotes it in the failure it produces.
    /// </para>
    /// <para>
    /// <strong>An absolute figure is defensible HERE for the reason it was not defensible on rows 1
    /// and 2: nothing is racing to destroy the child.</strong> On those rows the constant had to
    /// beat a competing deadline that was already killing the thing being waited for, so any host
    /// slow enough to lose the race produced an empty directory and a red row. Neither consumer
    /// here has a killer — row 5's child has five minutes of budget, the theory's has none at all
    /// — so a slow host merely DELAYS the write being waited for. What this ceiling has to clear
    /// is therefore a start-up TAIL, not a race.
    /// </para>
    /// <para>
    /// <strong>MEASURED against that tail rather than argued from "a shell start-up", because
    /// #541 gave it a second shape to cover.</strong> Process.Start to an accepted pid line, on
    /// the maintainer's 20-core Windows 11 host, 2026-09-20, polled at 5ms so the figure is the
    /// writer's and not the poll's; <see cref="NeverExitingChild"/> first,
    /// <see cref="GrandchildHoldingPipesChild"/> second, and each median the mean of the two
    /// middle samples, rounded at the half, so that every delta below is re-derivable from the
    /// line above it:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     unloaded, 8 samples per shape — 154-292ms (median 164) against 185-214ms (median 199);
    ///   </description></item>
    ///   <item><description>
    ///     32 CPU burners, 8 per shape — 713-838ms (median 770) against 743-903ms (median 829);
    ///   </description></item>
    ///   <item><description>
    ///     64 burners, 8 per shape — 1218-1366ms (median 1323) against 1395-7542ms (median 1490),
    ///     where the second shape's tail opens;
    ///   </description></item>
    ///   <item><description>
    ///     128 burners, 24 per shape — 2495-4822ms (median 2886) against 2680-12232ms
    ///     (median 3265), five of those twenty-four past 6s and no child sample past 4.9s.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The MEDIANS answer the question row 2's remarks left open: the grandchild shape costs
    /// 35ms more unloaded, 59ms more at 32 burners, 167ms at 64 and 379ms at 128 — more, at
    /// every load, and never a different order. The
    /// TAIL is where the two shapes part, and the reason is structural — a SECOND process
    /// creation has to be scheduled before that shape's first write, so it is exposed twice to
    /// whatever stalls one. Worst sample anywhere, across both shapes and every load: 12.2s,
    /// beside the 13.6s already on record for the child shape in
    /// <see cref="StartingBudget"/>'s figures.
    /// </para>
    /// <para>
    /// <strong>THIRTY STAYS, and the headroom is the argument: 2.5x the worst sample measured
    /// here and 2.2x the worst on record.</strong> Raising it buys only a host thrashing harder
    /// than 128 burners on 20 cores, whose whole lane is failing for other reasons — and it is
    /// not free, since the failing path costs this ceiling PLUS <see cref="LateReadWindow"/>:
    /// every second added is paid five times on a fully red run (the four parametrisations of the
    /// two directly-launching rows, and row
    /// 5), and comes out of the margin under <see cref="ChildLifetime"/> that row 5's guard
    /// defends. A measured case for a change would be a sample inside 2x of thirty; none of the
    /// 96 taken here is — and 96 samples no more bound this tail than eight bounded the one the
    /// file header reads as a LOWER BOUND ON THE SPREAD AND NOTHING ELSE, the probe that missed
    /// the load #524 was filed from. What carries the decision is not the sample count but the
    /// paragraph above it: with no killer in the race, a host slower than anything sampled here
    /// merely delays the write, and the row that reports it is telling the truth about the host.
    /// It must stay below <see cref="ChildLifetime"/> so that a wait which ran
    /// to the ceiling is known to have been waiting on a live child rather than on one that had
    /// already exited — asserted at the top of row 5, which is no longer the only row this
    /// ceiling governs but is the one whose guard weighs the same sum the theory spends, this
    /// ceiling PLUS <see cref="LateReadWindow"/>. The theory adds no second copy of that
    /// assertion because there would be nothing new in it; the build already fails on the sum,
    /// and a doc-comment does not. The bound on this constant alone follows from the sum and is
    /// not separately asserted.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan UnracedPidCeiling = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long teardown waits for the abandoned <c>Run</c> to unwind once its child is dead.
    /// </summary>
    private static readonly TimeSpan DrainWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a row waits for a killed child to actually disappear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A poll, not a single sample, because the kill is ASYNCHRONOUS.</strong>
    /// <c>KillTreeQuietly</c> issues the kill and does not <c>WaitForExit</c>; both
    /// <c>TerminateProcess</c> on Windows and <c>SIGKILL</c> on POSIX return once the request is
    /// queued, not once the target is gone. Sampling liveness the instant <c>Run</c> returns can
    /// therefore observe a child that is dying, and on POSIX the corpse additionally lingers as a
    /// zombie until it is reaped. A single sample passing on the maintainer's Windows host is not
    /// evidence about the Linux lane that gates merges, where the reaping mechanism differs, so
    /// the window closes the class rather than resting on the host that happened to be green.
    /// </para>
    /// <para>
    /// <strong>FOUR CONSUMERS, AND THE LAST TWO WANT A DIFFERENT THING FROM THE ANSWER.</strong>
    /// Rows 1 and 5 assert on it: the window is the budget for "the runner abandoned its child"
    /// to be believed, and a dead sample inside it is the verdict. The two directly-launching
    /// rows (#541, #529) take the same wait in their <c>finally</c> and DISCARD the answer —
    /// nothing there fails on it — because what they want is the directory released before the
    /// delete a line later, not a verdict. Two seconds suits both kinds: it is the same
    /// asynchronous kill being waited out either way.
    /// </para>
    /// <para>
    /// Costs nothing measurable on the green path, and the reason is the sampling ORDER: the poll
    /// samples before it sleeps, so a target already gone ends it in one sample. The
    /// directly-launching rows are the sharpest test of that, since they kill immediately before
    /// waiting rather than with work in between — MEASURED on the maintainer's Windows host,
    /// 2026-09-20, ten green samples per shape against #541's row: one poll every time, 20 of 20,
    /// the whole wait taking 0-10ms. Two seconds is the
    /// ceiling on a failure being believed, not a latency the healthy case pays.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan DeathWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How much later than its own pid file's write time a process may have started and still be
    /// treated as the one that wrote it (#529).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>It absorbs a CLOCK difference, not a scheduling one.</strong> The true child
    /// starts, then writes, so the honest value of <c>StartTime - LastWriteTimeUtc</c> is
    /// negative and this tolerance is never consulted. What it is for is the two quantities being
    /// read from different sources: a process start time out of the OS process table, a write
    /// time out of the filesystem. Each has its own granularity and, on some platforms, its own
    /// derivation.
    /// </para>
    /// <para>
    /// <strong>MEASURED on the maintainer's 20-core Windows 11 host, 2026-09-20, NTFS
    /// <c>%TEMP%</c></strong> — <c>StartTime - LastWriteTimeUtc</c> in milliseconds, twenty
    /// samples per shape from a standalone probe and six more per shape from
    /// <see cref="TheGuardsAnchors_StayInTheOrderARealChildProduces"/> itself:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="NeverExitingChild"/> — -214.8 to -157.1 across the twenty-six. The shell
    ///     writes its OWN pid, so the gap is a whole interpreter start-up.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="GrandchildHoldingPipesChild"/> — -52.6 to -25.2. The announced process is
    ///     spawned immediately before the write, so this shape is the one that sets the margin:
    ///     25.2ms at its tightest, and all fifty-two samples negative.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <strong>In BOTH rows only the tight end is a figure worth holding.</strong> The other end
    /// is whatever the host's load made of a start-up on the day, and later runs have already
    /// read past it: -70.6ms on the grandchild shape, against a range that stopped at -52.6. That
    /// direction is further from the tolerance rather than nearer it, which is why nothing tracks
    /// it. Neither range is a bound, and the child shape's is no more one for not having been
    /// exceeded yet.
    /// </para>
    /// <para>
    /// The two probes are quoted together because they disagreed in the direction that matters:
    /// the standalone one bottomed out at -27.3ms and the row's own runs went 2ms tighter within
    /// six samples. Neither is a bound on the tail, and the row is there to keep extending the
    /// sample rather than to confirm the probe.
    /// </para>
    /// <para>
    /// <strong>The filesystem is named because the arithmetic turns on it, and FAT is worse than
    /// "coarse".</strong> NTFS carries a write time fine enough for the figures above to mean
    /// something. FAT and exFAT truncate mtime DOWNWARD to even seconds, so <c>announcedUtc</c>
    /// can read up to 2s EARLIER than the true write — which adds up to 2s to the computed skew.
    /// Against the tightest real skew measured here, the grandchild shape's -25.2ms, that is up
    /// to +1.975s against a 2s tolerance: about 25ms of margin left. The error direction is
    /// toward REFUSING the real child, so nothing a stranger could exploit — but a refusal is
    /// the expensive mistake here, for the reason <see cref="TryOpen"/>'s remarks give.
    /// </para>
    /// <para>
    /// <strong>And the truncation is PER WRITE, which is what makes this the vacuous-pass
    /// residual rather than a tail of it.</strong>
    /// <see cref="TheGuardsAnchors_StayInTheOrderARealChildProduces"/> cannot miss its OWN child:
    /// it asserts <c>skew &lt;= PidStartTimeTolerance</c> and <see cref="TryOpen"/> refuses on
    /// <c>start &gt; write + PidStartTimeTolerance</c> — the same inequality over the same two
    /// readings of the same process and the same file, so for that child the row is red exactly
    /// when the guard refuses. What it cannot see is ANOTHER child. Each write is truncated
    /// independently into its own two-second bucket, so the offset is a fresh draw per file.
    /// </para>
    /// <para>
    /// <strong>And the draw refuses nothing by itself: what it removes is the HEADROOM.</strong>
    /// A real child's skew is negative, which is the invariant every sample above asserts and the
    /// canary's own message calls the order a real child produces — so even the worst draw lands
    /// SHORT of the tolerance, by exactly that child's own skew. The ~25ms left in the paragraph
    /// before this one IS that shortfall, at the tightest figure measured here. Truncation
    /// therefore spends almost the whole tolerance, per child and independently, and leaves
    /// nothing in reserve against anything else: a further adverse skew larger than that child's
    /// own — a boot-time rounding on Linux, two clocks disagreeing, load — tips THAT child into
    /// refusal, while the canary's child, holding a smaller draw, stays green and prints a
    /// comfortable figure. Which is the per-child half of the residual <see cref="TryOpen"/>'s
    /// remarks name rather than the systematic half the canary exists to catch, and a stronger
    /// argument for a three-way answer than FAT alone would be: the filesystem does not have to
    /// cause the fault, only to leave nothing absorbing one. INFERRED from the filesystems'
    /// documented resolution, not measured: every lane this runs on is NTFS, ext4, overlayfs or
    /// tmpfs, and no FAT host was available to try.
    /// </para>
    /// <para>
    /// <strong>TWO SECONDS, and the Windows measurement is not what sizes it — the Linux lane
    /// is.</strong> On this host, anything from about 50ms up would do: the tightest real margin
    /// is 25.2ms and one system-clock tick is about 16ms. The lane that gates merges is Linux,
    /// where <see cref="Process.StartTime"/> is a DIFFERENT quantity: .NET derives it in
    /// <c>Process.Unix.cs</c> from <c>/proc/&lt;pid&gt;/stat</c> field 22, the process's start in
    /// clock ticks since boot, added to a boot time the runtime estimates — and the usual source
    /// for that estimate, <c>/proc/stat</c>'s <c>btime</c>, carries WHOLE SECONDS. A tolerance
    /// under a second would therefore risk refusing a real child there for a rounding artefact.
    /// INFERRED, not measured: no Linux host was available here, which is exactly why
    /// <see cref="TheGuardsAnchors_StayInTheOrderARealChildProduces"/> records the observed skew
    /// on whatever host runs it rather than leaving this paragraph as the last word.
    /// </para>
    /// <para>
    /// <strong>TOO TIGHT IS WORSE THAN A LEAK, AND THIS IS THE SENTENCE THAT USED TO STOP AT
    /// "it leaks".</strong> A refusal makes <see cref="KillTreeQuietly"/> kill nothing, so the
    /// process lives out <see cref="ChildLifetime"/> — and the same refusal makes
    /// <see cref="IsAlive"/> answer <see langword="false"/>, because that method reads
    /// <see cref="TryOpen"/> returning <see langword="null"/> as "gone" and cannot tell it from
    /// "refused". <see cref="WaitForDeath"/> then answers <see langword="true"/> at its first
    /// sample, and rows 1 and 5's <c>dead</c> assertion — the whole point of the #481 leak cover
    /// — passes over a child that is still running. <see cref="ReadPid"/>'s remarks record that
    /// happening once already, with a boot-time pid the old lower bound discarded; what this
    /// constant adds is a second, far more reachable way in, since the refusal now turns on two
    /// clocks agreeing and on Linux the figure is INFERRED. MEASURED, by the fourth drill at
    /// <see cref="TryOpen_RefusesAProcessOutsideTheWindowTheChildMustHaveStartedIn"/>: a guard
    /// tightened until it opens nothing reddens five rows and rows 1 and 5 are NOT among them.
    /// </para>
    /// <para>
    /// What still reddens is the systematic case, which is the one a wrong figure produces:
    /// <see cref="TheGuardsAnchors_StayInTheOrderARealChildProduces"/> fails on any platform
    /// where a real child's skew exceeds this, and it runs on the gating lane. What does NOT is
    /// the per-child case — one child on one run skewing past this while the rest do not — and
    /// closing that needs <see cref="TryOpen"/> to answer three ways rather than two, so
    /// <see cref="WaitForDeath"/> can tell a refusal from a death. That is a design change
    /// reaching the three helpers that read its answer — <see cref="IsAlive"/>,
    /// <see cref="WaitForDeath"/> and <see cref="KillTreeQuietly"/> — and every row that calls
    /// them, so it is tracked rather than done here.
    /// </para>
    /// <para>
    /// <strong>Too loose, and what the second bound actually buys.</strong> A recycled pid's
    /// process slips through if it started inside the accepted window, and that window is three
    /// different sizes depending on which threat is being weighed — worth stating all three
    /// rather than quoting the flattering one:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     RAW, both bounds, prompt write: <c>[startedUtc - 5s, writeTime + 2s]</c>, about 7.2s
    ///     where the lower bound alone left about 40s — roughly 5.6x, not the order of magnitude
    ///     an earlier draft of this paragraph claimed by counting the forward half only.
    ///   </description></item>
    ///   <item><description>
    ///     For RECYCLING specifically, the five seconds of backward slack are unreachable: a
    ///     stranger can only hold the pid after the child exited, and the child started after
    ///     <c>startedUtc</c>. The window open to a recycled pid is therefore about 2.2s against
    ///     about 35s — roughly 16x.
    ///   </description></item>
    ///   <item><description>
    ///     On the LATE-WRITE path <see cref="ReclaimPidForTeardownQuietlyAsync"/> exists for, the
    ///     second bound buys essentially NOTHING, and the reason is structural rather than a
    ///     matter of degree: the narrowing is whatever the guard's own moment exceeds
    ///     <c>write + PidStartTimeTolerance</c> by, and on that path the reclaim returns within
    ///     one <see cref="PollIntervalMs"/> of the write with the kill on the next line. The
    ///     anchor and the guard arrive together, so there is no gap for the bound to close.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Two structural changes matter more than any of those ratios, and both are properties of a
    /// PROMPT write — which is the common case, the child writing in its first few hundred
    /// milliseconds. The accepted window no longer
    /// SCALES with the budget: under the lower bound alone the window's upper edge is the moment
    /// the guard runs, which #524's escalation walks out from a 3s budget to a 24s one for free,
    /// and an anchor on an early write does not move when a budget does. And recycling after the
    /// RUNNER's own tree-kill is closed outright rather than
    /// narrowed, that kill landing up to a budget after an early write, so anything the freed pid
    /// is reused by starts well beyond <c>writeTime + 2s</c>. Both weaken on a LATE write for one
    /// reason: the anchor IS the write, so a write that arrives near the end of a budget carries
    /// the upper bound along with it, exactly as the old bound's did. The bullet above says the
    /// same thing from the other side.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan PidStartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>How often the pid file is polled.</summary>
    private const int PollIntervalMs = 100;

    /// <summary>
    /// The lifetime of a child that is supposed to outlast the row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounded rather than infinite so that a defect in the teardown below leaks a process which
    /// expires on its own within the minute instead of one that outlives the session — the same
    /// reasoning ChildProcessKillTreeTests records for its own child.
    /// </para>
    /// <para>
    /// It is also the CEILING every other window in this file has to stay under, which is why it is
    /// a number here and a string only where a command line needs one. A child that exits while a
    /// row is still waiting stops being the shape that row describes: rows 1 and 2 would see the
    /// pipes close and the call succeed, and would then fail asserting a timeout that could not
    /// happen.
    /// </para>
    /// <para>
    /// <strong>Two relationships depend on it, and each is asserted where it applies rather than
    /// described here.</strong> Rows 1 and 2 need the WHOLE of an attempt to fit — the largest
    /// budget, the unwind slack after it and the death poll after that — which is what
    /// <see cref="WithEscalatingBudget"/> checks; asserting only the budget would have left the
    /// other twelve seconds unpinned. Row 5 needs <see cref="UnracedPidCeiling"/> to fit, and
    /// asserts that itself. Neither is currently near its limit; the assertions exist so that the
    /// next edit to any of these numbers is caught by a red row rather than by a reader. THIRD
    /// and FOURTH consumers arrived with #541 and #529, and neither adds an assertion of its own:
    /// <see cref="PidFileWriters_PublishAPidTheReaderAccepts"/> and
    /// <see cref="TheGuardsAnchors_StayInTheOrderARealChildProduces"/> each spend the same
    /// <see cref="UnracedPidCeiling"/> plus <see cref="LateReadWindow"/> row 5 does, so row 5's
    /// guard already pins the sum all three depend on and a second copy would pin nothing new.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan ChildLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// <see cref="ChildLifetime"/> as the text a command line splices in.
    /// </summary>
    /// <remarks>
    /// Formatted once, invariantly, from the single source above rather than written out a second
    /// time: two spellings of one lifetime is exactly the pair that drifts, and the drift would be
    /// silent — the rows would keep passing until the day a budget outgrew the number nobody
    /// updated.
    /// </remarks>
    private static readonly string ChildLifetimeSeconds =
        ChildLifetime.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Row 1 — an unbounded read against a child that never exits must not wedge the caller.
    /// </summary>
    /// <remarks>
    /// This row covers the plain absence of any budget: without one, nothing in <c>Run</c> ever
    /// stops waiting. It is distinct from row 2, which fails for a different reason — see there.
    /// </remarks>
    [Fact]
    public async Task Run_WhenTheChildNeverExits_ThrowsWithinTheBudgetAndLeavesNoLiveChild()
    {
        await WithEscalatingBudget(
            "the never-exiting child never published a pid, at any of the budgets below",
            async budget =>
            {
                var startedUtc = DateTime.UtcNow;
                var directory = CreateScratchDirectory();
                var pidFile = Path.Combine(directory, PidFileName);
                var shape = NeverExitingChild();
                var runner = new SystemProcessRunner(budget);
                var grace = GraceFor(budget);

                var attemptClock = Stopwatch.StartNew();
                var work = Task.Run(() => runner.Run(shape.FileName, shape.Arguments, directory));
                int? pid = null;
                try
                {
                    pid = await Task.Run(
                        () => WaitForPid(pidFile, work, PidSettleWindow, grace + PidSettleWindow));

                    // ASSERTED BEFORE THE PREMISE IS JUDGED, AND THAT ORDER IS THE POINT. A missing
                    // pid is about to be handed back to WithEscalatingBudget as "too small a
                    // budget, try again" — so the things it could otherwise hide have to be ruled
                    // out here, while this attempt still owns the run. A run that has NOT returned
                    // is not a child that started too slowly; it is the wedge this row exists to
                    // catch, and retrying it three more times would bury the row's own finding
                    // under its setup machinery. Costs nothing when the run has already settled,
                    // which on the escalating path it always has.
                    //
                    // The elapsed figure is reported rather than the window, because the window is
                    // not what was waited: WaitForPid above may already have spent up to
                    // grace + PidSettleWindow, and this race then adds up to grace on top of it.
                    var finished = await Task.WhenAny(work, Task.Delay(grace)) == work;
                    Assert.True(
                        finished,
                        FormattableString.Invariant(
                            $"SystemProcessRunner.Run had still not returned {attemptClock.Elapsed.TotalSeconds:F0}s after it was launched against a child that never exits, although it was given a {budget.TotalSeconds:F0}s budget. Without a budget, StandardOutput.ReadToEnd() blocks until the child closes the pipe and WaitForExit() is unbounded besides."));

                    // HOISTED ABOVE THE PREMISE CHECK, and that is a correctness fix rather than
                    // tidying. Three shapes reach the `pid is null` return without any timeout
                    // having happened: Run throws ProcessLaunchException; Run throws
                    // ProcessCaptureException; or a re-broken runner kills its child at once and
                    // returns a perfectly ordinary ProcessResult — which is the "budget is
                    // enforced" property itself failing. In each of them the child dies before it
                    // writes, so the pid is missing for a reason that has nothing to do with
                    // scheduling, and DrainAsync's observer would swallow the real exception on the
                    // way out. Left below the return, the row would recycle a genuine runner defect
                    // four times and then blame the host — the #512 failure mode exactly.
                    //
                    // Always valid here, whatever the pid did: the runner sets `exceeded` from the
                    // reads still being pending when the budget expires, and throws on that alone.
                    // The pid file plays no part in it.
                    var timeout = await Assert.ThrowsAsync<ProcessTimeoutException>(() => work);
                    Assert.Equal(budget, timeout.Budget);

                    if (pid is null)
                    {
                        // Now narrowed to exactly one meaning, and it is NOT "the child is dead":
                        // the run refused correctly, and no pid had appeared by the time it did.
                        // Run issues the tree-kill without confirming it, so the survivor case is
                        // live (PR #532's review) — and the look that keeps such a survivor
                        // killable is in the `finally` below rather than here (#539). Nothing is
                        // reclaimed on this line any more: a look sited here runs only when the
                        // attempt ends by REACHING here, and the attempt's own assertions can end
                        // it sooner. The return is `false` either way, read off the ORIGINAL wait.
                        return false;
                    }

                    var dead = await Task.Run(() => WaitForDeath(pid, startedUtc, DeathWindow, pidFile));
                    Assert.True(
                        dead,
                        FormattableString.Invariant(
                            $"SystemProcessRunner.Run returned but child pid {pid} was still alive {DeathWindow.TotalSeconds:F0}s later. Abandoning the timed-out child is the leak #481 is about; the timeout path must tree-kill it. The window is there because the kill is asynchronous, not because a live child is tolerable."));
                    return true;
                }
                finally
                {
                    // TEARDOWN'S OWN LOOK, AND IT IS FIRST BECAUSE EVERYTHING BELOW IT NEEDS A
                    // TARGET (#539). Two paths reach a `finally` with `pid` still null, and only
                    // one of them is the no-pid return above. The other is any assertion in the
                    // body that threw while the pid was missing — above all
                    // `Assert.True(finished, …)`, the wedged-runner finding this row exists to
                    // make, which is precisely the path where a survivor is most likely and where
                    // a look sited on that return never ran. Siting it here covers both without
                    // asking which one happened.
                    //
                    // Quiet rather than loud, for the reason ReclaimPidForTeardownQuietlyAsync
                    // gives: a throw out of a `finally` replaces the finding being propagated.
                    // The pid it hands on is checked by TryOpen's two bounds and nothing else
                    // (#529), as it was from the return above; the placement moves the paths,
                    // not the guard.
                    if (pid is null)
                    {
                        pid = await ReclaimPidForTeardownQuietlyAsync(pidFile, work);
                    }

                    KillTreeQuietly(pid, startedUtc, pidFile);
                    await DrainAsync(work);
                    TryDeleteDirectory(directory);
                }
            });
    }

    /// <summary>
    /// Row 2 — the #392 shape: the child exits promptly, a grandchild keeps the inherited pipes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DELIBERATELY A SEPARATE ROW FROM row 1, because the two fail for different reasons and a
    /// fix that satisfies one can leave the other wedged. Row 1 fails because nothing bounds the
    /// wait. This row fails even if <c>WaitForExit</c> is given a budget: the direct child is
    /// already gone within a second or two, so a bounded <c>WaitForExit</c> would be satisfied
    /// immediately — and <c>Run</c> would still hang, because it never reaches
    /// <c>WaitForExit</c>. <c>StandardOutput.ReadToEnd()</c> runs first and does not see EOF
    /// until EVERY writer closes the pipe, and the orphaned grandchild inherited a copy of it.
    /// </para>
    /// <para>
    /// Unlike row 1 this row asserts nothing about the grandchild's fate, and that is the honest
    /// limit rather than an omission: by the time the budget expires the DIRECT child has already
    /// exited, so the runner's tree-kill reaches an empty tree. Nothing portable reclaims a
    /// process whose parent is gone, and pretending otherwise would be an assertion about the
    /// operating system rather than about <c>Run</c>. Teardown below kills it instead.
    /// </para>
    /// <para>
    /// So this row is the one that says the budget must cover the READS, not merely the exit.
    /// Deleting it as a duplicate of row 1 would delete the only cover for that distinction.
    /// </para>
    /// <para>
    /// The pid captured here is the GRANDCHILD's, not the child's: the child is expected to be
    /// dead by the time teardown runs, and a tree-kill of a dead parent reaches nothing.
    /// </para>
    /// <para>
    /// <strong>It escalates its budget for the same reason row 1 does, and has more to lose by not
    /// doing so.</strong> Its child has to start an interpreter AND launch a grandchild through it
    /// before it writes anything, so by construction it does strictly more work before its first
    /// write than row 1's child does. How much more has NOT been measured — the figures quoted for
    /// <see cref="StartingBudget"/> are row 1's shape, and this row's are simply unknown, which is
    /// itself an argument for a budget that adapts rather than one somebody picked. The one
    /// consequence worth naming: an attempt killed between spawning the grandchild and
    /// recording its pid leaves a grandchild nothing in this file can reach, and escalation can
    /// repeat that up to <see cref="BudgetAttempts"/> times. That is the header's stated gap rather
    /// than a new one, and the backstop is the same — the grandchild's own
    /// <see cref="ChildLifetime"/>, which is why that lifetime is finite.
    /// <see cref="ReclaimPidForTeardownQuietlyAsync"/>, in this row's <c>finally</c>, narrows it to
    /// a grandchild whose pid never lands at all, by giving a late write time to become one; it
    /// does not remove it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Run_WhenAGrandchildHoldsTheInheritedPipes_ThrowsWithinTheBudget()
    {
        await WithEscalatingBudget(
            "the pipe-holding grandchild was never recorded, at any of the budgets below",
            async budget =>
            {
                var startedUtc = DateTime.UtcNow;
                var directory = CreateScratchDirectory();
                var pidFile = Path.Combine(directory, GrandchildPidFileName);
                var shape = GrandchildHoldingPipesChild();
                var runner = new SystemProcessRunner(budget);
                var grace = GraceFor(budget);

                var attemptClock = Stopwatch.StartNew();
                var work = Task.Run(() => runner.Run(shape.FileName, shape.Arguments, directory));
                int? pid = null;
                try
                {
                    pid = await Task.Run(
                        () => WaitForPid(pidFile, work, PidSettleWindow, grace + PidSettleWindow));

                    // Before the premise is judged, exactly as in row 1 and for the same reason,
                    // including the elapsed figure rather than the window.
                    var finished = await Task.WhenAny(work, Task.Delay(grace)) == work;
                    Assert.True(
                        finished,
                        FormattableString.Invariant(
                            $"SystemProcessRunner.Run had still not returned {attemptClock.Elapsed.TotalSeconds:F0}s after it was launched, although its direct child exited almost immediately and it was given a {budget.TotalSeconds:F0}s budget. The orphaned grandchild still holds the inherited stdout/stderr handles, so ReadToEnd() never sees EOF — bounding WaitForExit alone does not fix this row (#392)."));

                    // Hoisted above the premise check for the reason row 1 sets out at length: a
                    // launch failure, a capture fault or a runner that stopped enforcing its budget
                    // all reach here with no pid, and all three are findings about
                    // SystemProcessRunner rather than about the host's scheduler.
                    var timeout = await Assert.ThrowsAsync<ProcessTimeoutException>(() => work);
                    Assert.Equal(budget, timeout.Budget);

                    // Every assertion this row makes has now run, so the pid decides only whether
                    // the grandchild was ever recorded — which is the premise, not a verdict.
                    // Row 1 keeps the longer form because it has a death assertion to place
                    // between the two.
                    if (pid is not null)
                    {
                        return true;
                    }

                    // No grandchild was recorded inside the original window, so the attempt is
                    // discarded. The look that gives teardown a target is in the `finally` below
                    // rather than on this line (#539): the premise is read off the ORIGINAL wait
                    // either way, and looking once the attempt is over also covers the paths where
                    // an assertion above threw instead of reaching this return.
                    return false;
                }
                finally
                {
                    // First, and quiet, for the reasons row 1's `finally` sets out at length: the
                    // attempt can also end at an assertion with no pid in hand, and a `finally`
                    // that throws replaces the finding it was supposed to be tidying up after.
                    // The pid it hands on is checked by TryOpen's two bounds and nothing else
                    // (#529), as before; the placement moves the paths, not the guard.
                    if (pid is null)
                    {
                        pid = await ReclaimPidForTeardownQuietlyAsync(pidFile, work);
                    }

                    KillTreeQuietly(pid, startedUtc, pidFile);
                    await DrainAsync(work);
                    TryDeleteDirectory(directory);
                }
            });
    }

    /// <summary>
    /// The budget row 5 injects: far above <see cref="RunUnwindSlack"/> on purpose.
    /// </summary>
    /// <remarks>
    /// If the row passes, it is the TOKEN that ended the call and nothing else — a budget anywhere
    /// near the window that row waits out would let a runner which ignores the token pass on the
    /// ceiling's timing, which is the one confusion this row exists to rule out.
    /// </remarks>
    private static readonly TimeSpan UnreachableBudget = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Row 5 — a signalled token ends the call promptly and leaves no live child.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The behaviour this pins is not "Run returns sooner", it is WHOSE cleanup runs. Before the
    /// token was threaded through, a Ctrl+C during <c>--changed-since</c> signalled a token that
    /// nothing on this path observed: System.CommandLine waited out its termination budget and
    /// then force-killed the CLI, at which point <c>Run</c>'s <c>finally</c> — and therefore the
    /// tree-kill — never ran and the git child was orphaned. That is the very leak #481 is about,
    /// on the path where an operator is most likely to intervene.
    /// </para>
    /// <para>
    /// FAIL-FAST LIKE ROWS 1 AND 2, and for the same reason: this assembly is a blocking CI gate
    /// with no per-test timeout, so <c>Run</c> is launched on a background task and the row
    /// asserts FIRST that it completed inside <see cref="RunUnwindSlack"/>. A runner that ignored
    /// the token would fail this row in ten seconds rather than sitting on
    /// <see cref="UnreachableBudget"/> for five minutes.
    /// </para>
    /// <para>
    /// <strong>It waits for the unwind alone, where rows 1 and 2 wait for a budget PLUS the
    /// unwind</strong> (<see cref="GraceFor"/>). Nothing is missing here: the token fires
    /// immediately, so there is no budget for this row to sit out, and adding
    /// <see cref="UnreachableBudget"/> to the window would let a runner that ignores the token pass
    /// on the ceiling's timing — the one confusion the unreachable budget exists to rule out.
    /// </para>
    /// <para>
    /// It does not escalate a budget either, and that is not an omission. The escalation on rows 1
    /// and 2 answers a race between the child's start-up and a kill scheduled by the budget; here
    /// the budget is five minutes and nothing is racing the child at all, so the only figure the
    /// row needs in order to JUDGE is <see cref="UnracedPidCeiling"/>. It is not the only figure it
    /// waits on — see the next paragraph, and the guard at the top of the row, which weighs both.
    /// </para>
    /// <para>
    /// <strong>It does take the late look, though, and that is teardown rather than escalation.</strong>
    /// The two are not the same thing: escalation retries a premise, and this row never retries
    /// anything — a missing pid fails it, once, for good. What the late look does is give the
    /// <c>finally</c> something to kill on exactly that failing path, where otherwise it would
    /// receive <see langword="null"/> and the orphan would be left to
    /// <see cref="ChildLifetime"/>. Milder than the same hole on rows 1 and 2, and deliberately
    /// described that way: there an orphan could hide behind a LATER attempt that passed, whereas a
    /// failure here is read by whoever ran the suite. Hygiene on a red row, not a leak test failing
    /// to fail. It is the header's original ×1 gap on the last row still carrying it — ×1 because
    /// this row runs once and never escalates, which #539 did not change.
    /// </para>
    /// <para>
    /// <strong>IT TAKES THAT LOOK IN TWO PLACES, AND THE SPLIT IS #539.</strong> The body keeps
    /// the look because it feeds the <c>childWasObserved</c> message, which is a job the escalating
    /// rows' looks never had. The <c>finally</c> carries a second, guarded one for the paths on
    /// which the body look does not run to completion — two that end the row in front of it, the
    /// <c>work.IsCompleted</c> block and a <see cref="WaitForPid"/> that threw, and the look
    /// itself throwing, since it uses the LOUD reader and an ACL fault escapes it. A
    /// <c>lateLookTaken</c> flag, set only once the body
    /// look has RUN TO COMPLETION, keeps the two from both firing on the paths where both would
    /// find something, so <see cref="LateReadWindow"/> is still paid at most once per row and the
    /// guard at the top of the row still weighs the whole of what the row spends looking.
    /// </para>
    /// <para>
    /// The child is killed by the row's own <c>finally</c> on every path, exactly as in rows 1
    /// and 2 — an assertion that fails must not leave a live child on the agent. Before #539 that
    /// sentence was truer of the intent than of the code: on the two paths above the
    /// <c>finally</c> ran with nothing to kill.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Run_WhenTheCallerCancels_ThrowsOperationCanceledAndLeavesNoLiveChild()
    {
        // Row 5's own window invariant, asserted rather than left in a doc-comment: a look that ran
        // to or past the child's lifetime would end against a child that had simply exited, and
        // everything this row says about a missing or a late pid would then be describing a corpse
        // — "a host that never started a shell" when the shell in fact finished, or "the shell did
        // start, and teardown has it" about a process that left on its own.
        //
        // THE WHOLE OF WHAT THIS ROW SPENDS LOOKING IS WEIGHED, NOT JUST THE FIRST WAIT, and that
        // is the reason this guard moved rather than being left at UnracedPidCeiling alone. On the
        // failing path the row looks for UnracedPidCeiling and then again for LateReadWindow, so it
        // is the SUM that has to stay under the lifetime. Today that is 35s of 60. Raise
        // LateReadWindow to 35s — a plausible edit, since nothing else in this file would complain
        // — and the total reaches 65s, past the point where the child exits of its own accord;
        // pinning only the first wait would leave the second free to drift there in silence. This
        // is the guard for THIS row's looking. The escalating rows' verdict-path guard lives in
        // WithEscalatingBudget and is a different sum for a different reason.
        var longestLook = UnracedPidCeiling + LateReadWindow;
        Assert.True(
            longestLook < ChildLifetime,
            FormattableString.Invariant(
                $"This row can spend {longestLook.TotalSeconds:F0}s looking for a pid ({UnracedPidCeiling.TotalSeconds:F0}s of UnracedPidCeiling, plus {LateReadWindow.TotalSeconds:F0}s of LateReadWindow on the failing path), which is not below the child's own lifetime ({ChildLifetime.TotalSeconds:F0}s). A look that outlives the child can end against one that simply exited, so a missing pid would no longer mean a host that could not start a shell, and a pid found late would no longer mean a process teardown can do anything about. Lower UnracedPidCeiling or LateReadWindow, or raise ChildLifetime."));

        var startedUtc = DateTime.UtcNow;
        var directory = CreateScratchDirectory();
        var pidFile = Path.Combine(directory, PidFileName);
        var shape = NeverExitingChild();
        var runner = new SystemProcessRunner(UnreachableBudget);
        using var cancellation = new CancellationTokenSource();

        var work = Task.Run(
            () => runner.Run(
                shape.FileName,
                shape.Arguments,
                directory,
                cancellationToken: cancellation.Token));
        int? pid = null;

        // Whether the body's late look below RAN TO COMPLETION, so the `finally` does not repeat it
        // (#539). It is the flag rather than `pid is null` that decides: a body look which found
        // nothing leaves the pid null too, and without this the `finally` would spend a second
        // LateReadWindow re-reading a file the row has just finished waiting on. With it, this row
        // pays that window at most once whichever path it takes, which is what keeps the
        // longestLook guard above weighing the whole of what the row spends looking.
        //
        // "RAN TO COMPLETION" rather than "was started", and the distinction is load-bearing: the
        // body look is the LOUD reader, so an ACL fault escapes it, and the assignment therefore
        // sits AFTER its await. A flag set before it would be true on a path where nothing had
        // looked at the child at all, and would suppress the `finally`'s look as well. That does
        // not, on today's code, cost a child — the quiet wrapper meets the same ACL fault and
        // answers null, so nothing is killed either way; what it costs is the flag's meaning and
        // a teardown that looks on its own account instead of resting on the two readers failing
        // alike. The argument is at the assignment; the ordering itself is pinned by
        // TheLateLookFlag_IsArmedOnlyAfterTheLookCompletes.
        var lateLookTaken = false;
        try
        {
            // Cancelled only once the child is known to be RUNNING. Cancelling earlier could be
            // answered by the pre-launch ThrowIfCancellationRequested, which would leave the row
            // asserting nothing about a live child.
            pid = await Task.Run(
                () => WaitForPid(pidFile, work, PidSettleWindow, UnracedPidCeiling));

            // CHECKED BEFORE THE PID IS JUDGED, because the pid wait now ends early when the RUN
            // settles — a bound this row did not have before #524. Nothing has been cancelled yet,
            // and the budget is five minutes against a child that never exits, so a run that has
            // already finished did so by failing: a launch failure or a capture fault, either of
            // which is a finding about SystemProcessRunner. Awaiting it here is what puts that
            // exception in front of xunit; left alone it would be swallowed by DrainAsync's
            // observer and the row would report a host that could not start a shell.
            if (work.IsCompleted)
            {
                var premature = await work;
                Assert.Fail(
                    FormattableString.Invariant(
                        $"SystemProcessRunner.Run returned exit code {premature.ExitCode} before this row cancelled anything, against a child that sleeps for {ChildLifetime.TotalSeconds:F0}s and a {UnreachableBudget.TotalMinutes:F0}-minute budget. Neither the token nor the budget can have ended that call, so the runner stopped waiting on its own."));
            }

            // The verdict is read off the ORIGINAL wait, BEFORE the late look below, so that look
            // cannot satisfy an assertion it was not asked to satisfy. Same discipline as row 2,
            // and here it also keeps UnracedPidCeiling meaning what it says: 30s remains the whole
            // of what this row will WAIT FOR a child, whatever teardown looks at afterwards.
            var childWasObserved = pid is not null;

            // HYGIENE, NOT A CORRECTNESS FIX, and the difference from rows 1 and 2 is the point.
            // There, a discarded attempt's orphan could be followed by a LATER attempt that passed,
            // so the row went green over a live child — a leak test failing to fail. Here the
            // assertion below is about to REDDEN and be read by whoever runs the suite; the orphan
            // rides along with a failure, it does not hide inside a pass. So this closes the
            // header's original ×1 gap on the one row still carrying it, not the escalation's ×4
            // one, and it is worth the five seconds only because a red row must still not leave a
            // live child on a CI agent.
            //
            // KEPT IN THE BODY WHERE ROWS 1 AND 2 MOVED THEIRS INTO A `finally` (#539), because
            // this one has a second job theirs never had: it precedes the childWasObserved
            // assertion and FEEDS ITS MESSAGE. `lateLook` below is the difference between telling
            // the reader "no shell ever started" and "a shell did start, later than this row is
            // willing to wait, and teardown has it" — a look that ran after the assertion could
            // not say either. Rows 1 and 2's look reported to nobody, so it lost nothing by moving.
            //
            // THE TWO PATHS THAT SKIP THIS LOOK ARE COVERED BY THE `finally` INSTEAD, which is the
            // whole of what #539 added to this row. The work.IsCompleted block above ends the row
            // on a runner defect, and WaitForPid can escape with an UnauthorizedAccessException;
            // either way the body look never runs, and before #539 teardown then received null and
            // left the child to ChildLifetime. That is the same class of hole the escalating rows
            // had, milder only in that it reddens loudly rather than hiding inside a later attempt
            // that passed — and "milder" was never a reason to leave a live child on a CI agent.
            //
            // IT COSTS AT MOST ONE LateReadWindow PER ROW, not two, because lateLookTaken is what
            // the `finally` tests rather than the pid — a body look that found nothing leaves the
            // pid null too, and a pid test alone would buy a second window to re-read a file this
            // row has just finished waiting on. The UnauthorizedAccessException path is the one
            // case where the `finally` DOES look again, deliberately: this reader is the loud one,
            // so that fault escapes before the flag is set and the `finally`'s quiet look is then
            // the only look this row has made. It costs nothing to let it run — the quiet wrapper
            // meets the same fault on its own first ReadPid and answers null there, and an ACL on
            // the scratch file does not heal in the moment between the two reads, so there is no
            // window to sit out.
            //
            // BRANCHED RATHER THAN `??=`, so that nothing describing the late look is composed on
            // the path that never takes it. With `??=` the green path fell through to a `lateLook`
            // string calling the ALREADY-OBSERVED pid a late arrival — dead today because the
            // assertion below passes and never reads it, but a sentence that is false the moment
            // anyone restructures the assertion. A branch cannot rot that way.
            string lateLook;
            if (childWasObserved)
            {
                lateLook = "was not taken — the wait above already had the pid";
            }
            else
            {
                // THE FLAG IS SET AFTER THE AWAIT, NOT BEFORE IT, and the order is a fix rather
                // than a style choice (Copilot, round 2). This is the LOUD reader —
                // ReclaimPidForTeardownAsync, not the quiet wrapper — so an ACL fault on the
                // scratch file escapes as an UnauthorizedAccessException and this look never
                // completes. Setting the flag first would leave the `finally` looking at
                // `pid is null && lateLookTaken` and skipping its own look on exactly the path
                // where nothing has yet looked at the child at all. Set afterwards, the flag means
                // what the `finally` reads it to mean: a body look that RAN TO COMPLETION.
                //
                // WHAT THAT BUYS IS THE FLAG'S MEANING, NOT A CHILD SAVED, and the distinction is
                // worth keeping straight because the obvious reading is the wrong one. On that
                // path the child is abandoned either way: the quiet wrapper meets the same ACL
                // fault on its own first ReadPid and answers null, so KillTreeQuietly is handed
                // nothing whether the `finally` looks or not. The gain is that the `finally`
                // looks ON ITS OWN ACCOUNT rather than the row resting on the coincidence that
                // the loud and quiet readers fail alike — true today, pinned by nothing.
                //
                // The double-spend the flag exists to prevent is not reopened either, for the
                // same reason the look finds nothing: an ACL does not heal in the moment between
                // the two reads, so the second answers at once. LateReadWindow is still paid at
                // most once per row.
                pid = await ReclaimPidForTeardownAsync(pidFile, work);
                lateLookTaken = true;
                lateLook = pid is null
                    ? "nothing either"
                    : FormattableString.Invariant(
                        $"pid {pid} — so a shell did start, later than this row is willing to wait, and teardown has been handed it");
            }

            Assert.True(
                childWasObserved,
                FormattableString.Invariant(
                    $"The never-exiting child did not write '{PidFileName}' into its working directory within {UnracedPidCeiling.TotalSeconds:F0}s, so this row could not establish that a child was ever running. The run had not finished and nothing was racing to kill that child — the injected budget is {UnreachableBudget.TotalMinutes:F0} minutes — so this is a host that did not start a shell inside {UnracedPidCeiling.TotalSeconds:F0}s. That is a defect in the row's environment, not in SystemProcessRunner. A further {LateReadWindow.TotalSeconds:F0}s look, for teardown's sake rather than for this verdict, found {lateLook}."));

            cancellation.Cancel();

            var finished = await Task.WhenAny(work, Task.Delay(RunUnwindSlack)) == work;
            Assert.True(
                finished,
                FormattableString.Invariant(
                    $"SystemProcessRunner.Run did not return within {RunUnwindSlack.TotalSeconds:F0}s of its cancellation token being signalled, against a child that never exits and a {UnreachableBudget.TotalMinutes:F0}-minute budget it cannot have reached. The token is not being raced against the budget, so a Ctrl+C reaches this runner's cleanup only after the process is force-killed — which is to say, never."));

            // ThrowsAny rather than Throws: TaskCanceledException derives from
            // OperationCanceledException, and the property under test is that cancellation
            // surfaces AS cancellation — never as ProcessTimeoutException, which GitChangeSet
            // maps to a usage error and would exit 2 for a Ctrl+C.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);

            var dead = await Task.Run(() => WaitForDeath(pid, startedUtc, DeathWindow, pidFile));
            Assert.True(
                dead,
                FormattableString.Invariant(
                    $"SystemProcessRunner.Run returned on cancellation but child pid {pid} was still alive {DeathWindow.TotalSeconds:F0}s later. Cancellation must reach the same tree-kill the timeout path does; a cancelled call that abandons its child is the orphan #481 exists to prevent. The window is there because the kill is asynchronous, not because a live child is tolerable."));
        }
        finally
        {
            // The same first-statement reclaim rows 1 and 2 carry (#539), conditioned on the flag
            // as well as the pid so the body's look is never repeated. Reached on the two paths
            // that end this row in front of that look — the work.IsCompleted block and a WaitForPid
            // that threw — and on neither of them has anything looked at the child yet, so without
            // this KillTreeQuietly would receive null over a child that may well be alive. The
            // pid it hands on is checked by TryOpen's two bounds and nothing else (#529), as the
            // body look's is; the placement moves the paths, not the guard.
            if (pid is null && !lateLookTaken)
            {
                pid = await ReclaimPidForTeardownQuietlyAsync(pidFile, work);
            }

            KillTreeQuietly(pid, startedUtc, pidFile);
            await DrainAsync(work);
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Row 3 — the normal path still captures both streams and the exit code. Green before AND
    /// after the fix; this is the regression guard the bounding change must not break.
    /// </summary>
    [Fact]
    public void Run_WhenTheChildPrintsAndExitsNonZero_CapturesBothStreamsAndTheExitCode()
    {
        var directory = CreateScratchDirectory();
        try
        {
            var shape = PrintsAndExitsChild();

            var result = SystemProcessRunner.Instance.Run(shape.FileName, shape.Arguments, directory);

            Assert.Equal(7, result.ExitCode);

            // Trimmed rather than matched exactly: the line terminator differs by shell, and
            // cmd.exe's `echo` is famous for carrying a trailing space through a redirection.
            // The property under test is "the stream reached the caller", not its whitespace.
            Assert.Equal("OUT", result.StandardOutput.Trim());
            Assert.Equal("ERR", result.StandardError.Trim());
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Row 4 — a missing executable is still a <see cref="ProcessLaunchException"/>, not a raw
    /// Win32Exception. Green before AND after the fix: GitChangeSet's usage-error mapping depends
    /// on it, so the try/finally the fix wraps the launch in must not swallow or retype it.
    /// </summary>
    [Fact]
    public void Run_WhenTheExecutableDoesNotExist_ThrowsProcessLaunchException()
    {
        var directory = CreateScratchDirectory();
        try
        {
            // Fully qualified and absent, not a bare name: a bare name is now refused by the
            // argument guard (row 6) and would never reach the launch this row is about.
            var missing = Path.Combine(
                directory,
                "vouchfx-no-such-executable-"
                    + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

            var exception = Assert.Throws<ProcessLaunchException>(
                () => SystemProcessRunner.Instance.Run(missing, Array.Empty<string>(), directory));

            Assert.Contains(missing, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Row 4b (#498, site 1) — the launch-failure message is the ENGINE's, not the BCL's quoted
    /// back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// .NET composes a failed start from <c>SR.ErrorStartingProcess</c> — "An error occurred trying
    /// to start process '{0}' with working directory '{1}'. {2}" — so splicing
    /// <c>ex.Message</c> into the engine's own sentence carried the absolute WORKING DIRECTORY into
    /// it, unasked. That is the #498 defect at this site, and all this row is about.
    /// </para>
    /// <para>
    /// <strong>The executable path deliberately REMAINS in this message, and row 4 above asserts
    /// that it does.</strong> That is not an oversight in the fix: this exception is the seam's
    /// internal diagnostic, <c>GitChangeSet.RunGit</c> discards its message entirely rather than
    /// printing it, and <c>GitChangeSetTests.LaunchFailure_DoesNotDiscloseTheResolvedPath</c> uses
    /// the raw message naming the resolved path as its CONTROL for the mapped one not naming it.
    /// Stripping it here would delete that control and leave a launch failure identifying nothing.
    /// </para>
    /// <para>
    /// The working directory is a SECOND scratch directory rather than the one holding the absent
    /// executable, so "the message does not name the working directory" is not satisfied trivially
    /// by the executable path being absent from it.
    /// </para>
    /// </remarks>
    [Fact]
    public void Run_WhenTheExecutableDoesNotExist_DoesNotQuoteTheFrameworkMessage()
    {
        var executableDirectory = CreateScratchDirectory();
        var workingDirectory = CreateScratchDirectory();
        try
        {
            var missing = Path.Combine(
                executableDirectory,
                "vouchfx-no-such-executable-"
                    + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

            var exception = Assert.Throws<ProcessLaunchException>(
                () => SystemProcessRunner.Instance.Run(
                    missing, Array.Empty<string>(), workingDirectory));

            // The premise, asserted rather than assumed: the BCL's own text really does name the
            // working directory, so the assertions below measure a removal rather than an absence
            // that was always there.
            Assert.NotNull(exception.InnerException);
            Assert.Contains(
                workingDirectory, exception.InnerException!.Message, StringComparison.Ordinal);

            Assert.DoesNotContain(workingDirectory, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "with working directory", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
            TryDeleteDirectory(executableDirectory);
        }
    }

    /// <summary>
    /// Row 7 (#500) — a caller-supplied environment is the child's WHOLE environment: an
    /// allow-listed name and a <c>GIT_</c>-prefixed name reach it, and nothing else does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The child prints its own environment block rather than one variable, so the row asserts the
    /// absence of the planted secret over everything the child can see rather than over one lookup
    /// that could be spelt wrong and pass.
    /// </para>
    /// <para>
    /// The planted names are process-wide for the duration of the row, which is safe in this
    /// assembly's parallel scheduling because nothing else reads them; they are removed with
    /// <see langword="null"/> rather than <c>""</c>, since an empty value DELETES the variable on
    /// net8 and would make the cleanup indistinguishable from a set-to-empty.
    /// </para>
    /// </remarks>
    [Fact]
    public void Run_WithAConfinedEnvironment_PassesTheAllowListAndTheGitPrefixAndNothingElse()
    {
        const string SecretName = "VOUCHFX_TEST_FAKE_SECRET";
        const string GitPrefixedName = "GIT_VOUCHFX_TEST_PROBE";

        var directory = CreateScratchDirectory();
        Environment.SetEnvironmentVariable(SecretName, "hunter2-not-for-the-child");
        Environment.SetEnvironmentVariable(GitPrefixedName, "probe-value");
        try
        {
            var confined = GitChangeSet.ConfinedGitEnvironment();
            var shape = PrintsItsEnvironmentChild();

            var result = SystemProcessRunner.Instance.Run(
                shape.FileName, shape.Arguments, directory, confined);

            Assert.Equal(0, result.ExitCode);

            var names = EnvironmentNamesIn(result.StandardOutput);

            // PATH is allow-listed, so it survives. Asserted as a NAME rather than as the substring
            // "PATH=", which a drill showed is satisfied by the allow-listed HOMEPATH and therefore
            // stayed green with PATH deleted from the allow-list.
            Assert.Contains("PATH", names, StringComparer.OrdinalIgnoreCase);

            // The GIT_ prefix passes through.
            Assert.Contains(GitPrefixedName, names, StringComparer.OrdinalIgnoreCase);

            // Everything else is gone -- this is the property #500 is about.
            Assert.DoesNotContain(SecretName, names, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretName, null);
            Environment.SetEnvironmentVariable(GitPrefixedName, null);
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Row 6 — an unqualified <c>fileName</c> is refused before anything is launched, which is the
    /// gate <see cref="IProcessRunner"/>'s contract lacked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This row exists because the requirement it pins shipped as prose.</strong> #499's fix
    /// resolves git to an absolute path in <c>GitChangeSet</c> and documents on this seam that every
    /// caller must do the same; nothing enforced it. The defect #499 closed was exactly a caller
    /// handing over the bare name <c>git</c>, so a doc-comment is demonstrably not cover: on Windows
    /// an unqualified name is resolved by the operating system's own search, which reaches the
    /// calling executable's directory and the calling process's current directory ahead of
    /// <c>PATH</c>. A future caller reintroducing that shape now fails here rather than silently
    /// reopening the hole.
    /// </para>
    /// <para>
    /// The three spellings are chosen for what each would do if it got through, not for variety.
    /// <c>git</c> is the original defect verbatim, and the M1 mutation drill confirmed it: with the
    /// guard removed, that call LAUNCHED — the operating system's search found a real git, which is
    /// the hole rather than a hypothetical one. <c>./git</c> is relative, and a relative name is
    /// resolved against the calling process's current directory — the term <c>GitChangeSet</c>'s
    /// header records as reachable, since <c>cd untrusted-repo &amp;&amp; vouchfx run .
    /// --changed-since main</c> hands the CLI that directory. <c>C:git</c> is the Windows
    /// drive-relative form, admitted by
    /// <see cref="Path.IsPathRooted(string)"/> and refused by
    /// <see cref="Path.IsPathFullyQualified(string)"/>; it is why the guard uses the latter, and it
    /// is asserted only on Windows because POSIX reads it as an ordinary relative file name with a
    /// colon in it — refused there too, but for a different reason, which would make the row assert
    /// a coincidence.
    /// </para>
    /// <para>
    /// <see cref="ArgumentException"/> rather than <see cref="ProcessLaunchException"/> is itself
    /// part of the contract and so is asserted by type: <c>GitChangeSet.RunGit</c> catches the
    /// runner's three failure types narrowly and maps them to a usage error (exit 2). A broken
    /// caller inside this assembly must escape that mapping rather than be reported to the user as
    /// their own mistake.
    /// </para>
    /// </remarks>
    [Fact]
    public void Run_WhenTheExecutableIsNotFullyQualified_ThrowsArgumentExceptionAndLaunchesNothing()
    {
        var directory = CreateScratchDirectory();
        try
        {
            var unqualified = new List<string> { "git", "./git" };
            if (OperatingSystem.IsWindows())
            {
                unqualified.Add("C:git");
            }

            foreach (var fileName in unqualified)
            {
                var exception = Assert.Throws<ArgumentException>(
                    () => SystemProcessRunner.Instance.Run(
                        fileName, Array.Empty<string>(), directory));

                Assert.Equal("fileName", exception.ParamName);
                Assert.Contains(fileName, exception.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// The pid file is read as a pid only when it holds one complete, newline-terminated digit
    /// line (#528).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pins <see cref="ReadPid"/>'s contract (#528) shape by shape: the SAME digits read as "not
    /// yet" unterminated and as a pid terminated, so a reader that filtered digits out of whatever
    /// it happened to catch fails here rather than in a teardown that kills a stranger. The newline
    /// is the delimiter because every child writer appends one — <c>Set-Content</c> and <c>echo</c>
    /// in <see cref="NeverExitingChild"/> and <see cref="GrandchildHoldingPipesChild"/> — so a file
    /// without one is a write still in flight. That those two writers DO append it is the other
    /// half of the same contract and is pinned separately, by
    /// <see cref="PidFileWriters_PublishAPidTheReaderAccepts"/> (#541), which runs them both for
    /// real; this row pins the READER alone and would stay green over a writer that stopped
    /// appending. <c>51234\r</c> is a torn CRLF: reachable by
    /// construction at a byte boundary inside the writer's seven-byte write, not something observed
    /// here. <c>51234\n999\n</c> pins the WHOLE-LINE half of the guard, which the four shapes above
    /// it leave free — none of them holds an interior non-digit, so a reader that went back to
    /// filtering digits out of a terminated file would keep all four green and read this one as
    /// <c>51234999</c>. <c>51234\n\n</c> and <c>51234\r\n\r\n</c> pin the count of terminators
    /// stripped, which no other shape reaches: the <c>TrimEnd('\r', '\n')</c> this change first
    /// carried removed the whole trailing run and handed both of them back as <c>51234</c>.
    /// </para>
    /// <para>
    /// <strong>BOTH ENDINGS ARE WRITTEN AS LITERALS RATHER THAN AS
    /// <see cref="Environment.NewLine"/>.</strong> That constant was this row's first draft and was
    /// rejected: it expands to CRLF on Windows and LF on Linux, so on the lane that gates merges it
    /// would have exercised the LF half only, and a reader that dropped <see cref="ReadPid"/>'s
    /// carriage-return strip would have passed CI while failing every pid read on Windows. The
    /// literals remove that blind spot rather than narrowing it:
    /// <see cref="File.WriteAllText(string,string)"/> writes these bytes verbatim and
    /// <see cref="File.ReadAllText(string)"/> translates none of them, so the CRLF member is a CRLF
    /// member on every platform. MEASURED on Windows with that strip deleted: eight rows red —
    /// this one, the three that read a pid through <see cref="SystemProcessRunner"/>, and the two
    /// parametrisations each of <see cref="PidFileWriters_PublishAPidTheReaderAccepts"/> (#541)
    /// and <see cref="TheGuardsAnchors_StayInTheOrderARealChildProduces"/> (#529). It was four
    /// before those rows existed and six between #541 and #529 — the same drill each time, not a
    /// stronger one; only the reddening population grew.
    /// </para>
    /// </remarks>
    [Fact]
    public void ReadPid_AcceptsOnlyACompleteNewlineTerminatedDigitLine()
    {
        var directory = CreateScratchDirectory();
        var pidFile = Path.Combine(directory, PidFileName);
        try
        {
            File.WriteAllText(pidFile, "51234");
            var unterminated = ReadPid(pidFile);

            File.WriteAllText(pidFile, "51234\r");
            var carriageReturnOnly = ReadPid(pidFile);

            File.WriteAllText(pidFile, "51234\r\n");
            var windowsEnding = ReadPid(pidFile);

            File.WriteAllText(pidFile, "51234\n");
            var posixEnding = ReadPid(pidFile);

            File.WriteAllText(pidFile, "51234\n999\n");
            var twoLines = ReadPid(pidFile);

            File.WriteAllText(pidFile, "51234\n\n");
            var extraPosixTerminator = ReadPid(pidFile);

            File.WriteAllText(pidFile, "51234\r\n\r\n");
            var extraWindowsTerminator = ReadPid(pidFile);

            // One assertion over all seven, so a failure names the shape that moved rather than
            // reporting the same "Expected: null" for whichever of them broke.
            Assert.Equal(
                ((int?)null,
                    (int?)null,
                    (int?)51234,
                    (int?)51234,
                    (int?)null,
                    (int?)null,
                    (int?)null),
                (unterminated,
                    carriageReturnOnly,
                    windowsEnding,
                    posixEnding,
                    twoLines,
                    extraPosixTerminator,
                    extraWindowsTerminator));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Both pid-file writers publish a line <see cref="ReadPid"/> accepts, and publish the pid
    /// teardown has to kill (#541).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>THE CONTRACT WAS ALREADY EXERCISED END TO END; WHAT IT LACKED WAS A LABEL.</strong>
    /// Rows 1, 2 and 5 run these same two writers through <see cref="SystemProcessRunner"/> and
    /// read them back with <see cref="ReadPid"/>, so a writer that stopped terminating its line
    /// already reddens them — that reader's own remarks set out why a broken writer contract costs
    /// a red row rather than a green one. It is a red row wearing the wrong label: those three
    /// fail blaming the host ("a host that did not start a shell inside 30s", "a host that still
    /// cannot start a shell inside the largest budget"), which is the #512 mislabel shape
    /// <see cref="PidFileName"/>'s remarks warn against, and a reader sent to look at the CI agent
    /// will not find the two-character edit that caused it.
    /// </para>
    /// <para>
    /// <strong>SO THIS ROW REMOVES THE OTHER EXPLANATIONS RATHER THAN ADDING AN
    /// ASSERTION.</strong> It launches the shape with
    /// <see cref="Process.Start(ProcessStartInfo)"/> directly instead of through the runner, so
    /// there is no budget and nothing racing to kill the child — and, the part that carries the
    /// message, a live handle in hand. A launch that returned is proof the shell started, which is
    /// exactly what rows 1, 2 and 5 cannot establish when their pid never arrives — and the same
    /// handle settles the question immediately behind it, because a shell that STARTED can still
    /// have DIED before writing. That is not hypothetical: <see cref="PidFileName"/>'s remarks
    /// measure it, a splice that leaves PowerShell exiting 1 on an unterminated string literal
    /// and writing nothing, wearing the label of a host that cannot start a shell. Refusing that
    /// mislabel is what this row is for, so the message reads the EXIT STATE first and ranks the
    /// rest behind it: the writer, then the reader, then a host that started a shell and did not
    /// schedule its first statement inside <see cref="UnracedPidCeiling"/>. The file's raw bytes
    /// are printed alongside, so a torn or unterminated write is read off the log rather than
    /// reproduced.
    /// </para>
    /// <para>
    /// <strong>THE EXIT STATE IS READ, NOT ACTED ON.</strong> The wait does not end early on
    /// <see cref="Process.HasExited"/>, because on <see cref="GrandchildHoldingPipesChild"/> the
    /// shell exits at once BY DESIGN (#392) and exiting is therefore no signal at all there. What
    /// carries the reading is the CODE: non-zero on either shape, and on
    /// <see cref="NeverExitingChild"/> any exit whatever, that shell being asleep for
    /// <see cref="ChildLifetime"/> — <c>Start-Sleep</c> on Windows, <c>sleep</c> on POSIX —
    /// across the whole of the ceiling. The remaining pair runs the ranking BACKWARDS: "still
    /// running" on <see cref="GrandchildHoldingPipesChild"/> means a shell that never reached its
    /// write, that write being its last act, so the host reading leads there and the contract
    /// readings are the distant ones. The message says so at the line rather than leaving it to
    /// be worked out. <see cref="ProcessExitState"/> is the guarded read —
    /// <see cref="Process.ExitCode"/> throws on a process that has not exited, and this argument
    /// is evaluated on the green path too.
    /// </para>
    /// <para>
    /// <strong>THE PID ITSELF IS THE SECOND ASSERTION, AND IT DIFFERS BY SHAPE.</strong>
    /// <see cref="NeverExitingChild"/> writes <c>$PID</c>/<c>$$</c> — the shell's own pid, which
    /// is the process this row started, so the pin is an equality against
    /// <see cref="Process.Id"/>. <see cref="GrandchildHoldingPipesChild"/> writes
    /// <c>(Start-Process -PassThru).Id</c>/<c>$!</c> — the GRANDCHILD, deliberately, because the
    /// shell exits at once and the grandchild is what keeps the inherited pipes open (#392); so
    /// the pin there is the inequality plus <see cref="IsAlive"/>, which is the same
    /// <see cref="TryOpen"/> guard row 2's teardown kills through. A writer that published the
    /// shell's pid into <see cref="GrandchildPidFileName"/> would satisfy the newline contract
    /// unchanged, leave row 2's teardown killing a process that had already exited, and leave the
    /// grandchild running for <see cref="ChildLifetime"/>.
    /// </para>
    /// <para>
    /// <strong>MEASURED on the maintainer's Windows 11 host, 2026-09-20, as three drills each
    /// reverted.</strong> Adding <c>-NoNewline</c> to both writers' <c>Set-Content</c> — the
    /// WRITER half — reddens both rows here, each on the labelled message, over a file holding a
    /// pid's digits and nothing after them; row 2 under that same mutation reddened too, with its
    /// host label word for word. Deleting <see cref="ReadPid"/>'s carriage-return strip — the
    /// READER half — was run over the whole class and reddens eight of its twenty-one rows: these
    /// two, rows 1, 2 and 5, the #528 fact, and both parametrisations of the #529 row that joined
    /// this file afterwards — six of fifteen when the figure was first taken here, before #529
    /// added six rows. Rows 1, 2 and 5 failed there with their host labels unchanged, word for
    /// word,
    /// which is the difference this row exists for. The two halves are told apart by the BYTES
    /// rather than by the count: the reader drill printed a complete line the reader refused,
    /// digits then <c>0D 0A</c>, where the writer drill printed the digits and stopped. The digits
    /// themselves are whichever pid the run was given and reproduce on no second run; what
    /// reproduces is the terminator's presence or absence. The exit state separates both of them
    /// from the shell: "still running" on the child rows, "exited 0" on the grandchild rows, on
    /// every drill above.
    /// </para>
    /// <para>
    /// <strong>The third drill is the exit state's own case.</strong> Dropping the closing quote
    /// from <see cref="NeverExitingChild"/>'s <c>-LiteralPath</c> — the #512 splice
    /// <see cref="PidFileName"/>'s remarks measure — reddens the child row ALONE, at the same
    /// full ceiling the other drills cost, reading "the file's first bytes are absent and the
    /// shell this row started is exited 1". Nothing about a newline is true of that failure, and
    /// nothing in the message claims it is. The grandchild row stayed green throughout, which is
    /// what makes the exit state a per-row reading rather than a run-wide one.
    /// </para>
    /// <para>
    /// <strong>TEARDOWN OWNS TWO PROCESSES AND NAMES THEM DIFFERENTLY, which is why the
    /// <c>finally</c> below carries both spellings of the kill.</strong> The shell is killed
    /// through the handle this row holds, from the inner <c>finally</c> of a <c>using</c> — the
    /// shape <see cref="ChildProcess"/>'s remarks prescribe, where the compiler puts the dispose
    /// in the enclosing <c>finally</c> and the dangerous order cannot be written. The grandchild
    /// has no handle here and is reachable only through the pid file, so it is killed through
    /// this file's own <see cref="KillTreeQuietly"/> under <see cref="TryOpen"/>'s guard, and the
    /// guarded <see cref="ReclaimPidForTeardownQuietlyAsync"/> ahead of it is the #539 late look:
    /// a grandchild whose write landed after this row gave up is otherwise unnameable. How long
    /// such a grandchild would outlive the row turns on when it STARTED, and the two cases this
    /// file can produce are most of a minute apart: one spawned late enough for its write to miss
    /// a 30s wait still has nearly the whole of <see cref="ChildLifetime"/> ahead of it when the
    /// row ends at 35s, while in the drills below — where it starts within a second and its write
    /// is simply never ACCEPTED — about 25 of the 60 seconds remain. That look is not conditioned
    /// on the shape.
    /// It buys the child shape nothing — that process is reachable through the handle whatever the
    /// file says — but it is paid only where the row has already failed, and a branch nothing pins
    /// is a worse thing to carry than one <see cref="LateReadWindow"/> on a red path.
    /// </para>
    /// <para>
    /// No runner, no budget, no Docker. The green path pays three things and one of them
    /// dominates: a shell start-up; up to one <see cref="PollIntervalMs"/> of pid-poll
    /// granularity, which is when the wait NOTICES a write rather than when the write happens;
    /// and the <see cref="DeathWindow"/> poll this row's <c>finally</c> takes after killing.
    /// MEASURED 2026-09-20, six green runs since that wait was added: 0.24s to 0.36s per row. The
    /// wait is not what varies in that — ten green samples per shape found the kill already
    /// landed at the FIRST liveness sample, 20 of 20, the whole wait costing 0-10ms, because
    /// <see cref="WaitForDeath"/> samples before it sleeps. A control run with the poll replaced
    /// by a single sample was indistinguishable.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(PidFileName)]
    [InlineData(GrandchildPidFileName)]
    public async Task PidFileWriters_PublishAPidTheReaderAccepts(string pidFileName)
    {
        var announcesTheShellItself =
            string.Equals(pidFileName, PidFileName, StringComparison.Ordinal);
        var shape = announcesTheShellItself ? NeverExitingChild() : GrandchildHoldingPipesChild();
        var startedUtc = DateTime.UtcNow;
        var directory = CreateScratchDirectory();
        var pidFile = Path.Combine(directory, pidFileName);

        // No redirection, and nothing here reads: that is what keeps this launch clear of the
        // pending-read machinery the runner rows exist for. The streams are the test host's, and
        // what reaches them differs by platform — on Windows the grandchild shape's `ping -n 60`
        // puts a reply line a second on them (they surface on the launching console), while the
        // POSIX `sleep 60 &` writes nothing. Neither can wedge this row the way #392 wedges a
        // runner, because no pipe of this row's is waiting to be drained.
        var startInfo = new ProcessStartInfo(shape.FileName)
        {
            UseShellExecute = false,
            WorkingDirectory = directory,
        };

        foreach (var argument in shape.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Process.Start returned no Process for a child shape this file launches "
                    + "directly, so there is nothing to wait for and nothing to kill.");

            using (process)
            {
                int? pid = null;
                try
                {
                    // WaitForPid rather than a second polling loop, for the reason
                    // ReclaimPidForTeardownAsync gives. `work` is Task.CompletedTask because there
                    // is no Run here at all: with both windows equal it is the CEILING that ends
                    // the wait — that method's remarks derive it — so the settle arm cannot
                    // shorten the window this row's message quotes.
                    var clock = Stopwatch.StartNew();
                    pid = await Task.Run(() => WaitForPid(
                        pidFile, Task.CompletedTask, UnracedPidCeiling, UnracedPidCeiling));
                    clock.Stop();

                    Assert.True(
                        pid is not null,
                        FormattableString.Invariant(
                            $"The writer of '{pidFileName}' published nothing ReadPid would accept in {clock.Elapsed.TotalSeconds:F1}s; the file's first bytes are {PidFileBytes(pidFile)} and the shell this row started is {ProcessExitState(process)}. READ THE EXIT STATE FIRST, because this row holds the handle that settles it. A non-zero code means the shell DIED rather than wrote — a quoting change, a renamed cmdlet, an absent ping.exe — which is the mislabel PidFileName's remarks measure (#512) and not a newline defect at all; so is ANY exit on '{PidFileName}', whose shell should still be sleeping out the {ChildLifetimeSeconds}s it was given (Start-Sleep on Windows, sleep on POSIX). 'exited 0' is expected on '{GrandchildPidFileName}' alone, whose shell exits the moment it has spawned the grandchild (#392) — and 'still running' THERE inverts this ranking rather than continuing it: that shell's write is its LAST act, so one still alive after {UnracedPidCeiling.TotalSeconds:F0}s never reached the write, which is neither a writer nor a reader breach but the fourth reading below, leading instead of distant. With the shell accounted for, what is left is the newline contract: ReadPid accepts ONLY a complete, newline-terminated ASCII digit line (#528) and every child writer in this file appends that terminator — Set-Content on Windows, echo on POSIX — so this red names a writer that stopped appending it, or a reader that stopped accepting it. It is NOT 'a host that cannot start a shell' — that is the label rows 1, 2 and 5 produce for this same breach, and they go on producing it; what #541 added is this red, which names the contract, landing in the same run as theirs. A host that started the shell and then did not schedule its first statement inside {UnracedPidCeiling.TotalSeconds:F0}s is the fourth reading — distant, except on the one pair named above that puts it first."));

                    if (announcesTheShellItself)
                    {
                        Assert.True(
                            pid == process.Id,
                            FormattableString.Invariant(
                                $"'{pidFileName}' holds pid {pid}, but the shell this row started is pid {process.Id}. NeverExitingChild publishes $PID on Windows and $$ on POSIX — the shell's OWN pid, which is the process Process.Start handed back — so either these are the same number or the writer has changed what it announces. Rows 1, 2 and 5 kill whatever this file holds, so a writer announcing anything else aims their teardown at a process they did not start."));
                    }
                    else
                    {
                        Assert.True(
                            pid != process.Id,
                            FormattableString.Invariant(
                                $"'{pidFileName}' holds pid {pid}, which is the shell this row started rather than its grandchild. GrandchildHoldingPipesChild announces (Start-Process -PassThru).Id on Windows and $! on POSIX deliberately: the shell exits at once, and the GRANDCHILD is what keeps the runner's inherited pipes open (#392). A writer that announced the shell instead would satisfy the newline contract unchanged, leave row 2's teardown killing a process that had already exited, and leave the grandchild running for the whole {ChildLifetime.TotalSeconds:F0}s."));

                        Assert.True(
                            IsAlive(pid, startedUtc, pidFile),
                            FormattableString.Invariant(
                                $"'{pidFileName}' holds pid {pid}, which TryOpen will not open as a live process started no earlier than this row. The grandchild is given {ChildLifetime.TotalSeconds:F0}s and this row spent {clock.Elapsed.TotalSeconds:F1}s of it waiting for the pid, so either it died early — in which case row 2's #392 shape no longer holds the pipes it is built on — or the number names something else, and that is the pid row 2's teardown tree-kills."));
                    }
                }
                finally
                {
                    // The same guarded late look rows 1, 2 and 5 carry (#539), and load-bearing
                    // here for the grandchild alone: it is named by this file only, so a write
                    // that landed after the wait above gave up leaves it unkillable for
                    // ChildLifetime. `work` is completed for the reason the body gives.
                    if (pid is null)
                    {
                        pid = await ReclaimPidForTeardownQuietlyAsync(pidFile, Task.CompletedTask);
                    }

                    // Pid first, then the handle: on the grandchild shape the pid IS the only
                    // name for the survivor, and on the child shape the two reach one process
                    // and the second call finds it already gone, or issues a kill the filter
                    // absorbs. Both under a guard — TryOpen's two bounds here (#529),
                    // ChildProcess's own exit race below.
                    KillTreeQuietly(pid, startedUtc, pidFile);
                    ChildProcess.KillTreeQuietly(process);

                    // THEN WAIT FOR THE KILL TO LAND, because the outer `finally` deletes the
                    // directory the killed process is standing in. Both kills only ISSUE a
                    // termination (see DeathWindow), and on Windows a process still holding that
                    // directory as its working directory makes Directory.Delete throw. Rows 1
                    // and 5 already interpose this wait; theirs ASSERTS on the answer, and this
                    // one discards it, because a `finally` that throws replaces the finding being
                    // propagated.
                    //
                    // That is why the answer is dropped. What makes the CALL safe here is a
                    // separate property, and it is the one the move from a body to a `finally`
                    // needs: WaitForDeath cannot throw. Every fault on the path is already
                    // filtered, and the list is THREE sources rather than two since #529 —
                    // TryOpen catches the GetProcessById and StartTime ones, PidFileWrittenUtc
                    // catches the GetLastWriteTimeUtc ones (IOException, UnauthorizedAccess,
                    // Argument, NotSupported; SecurityException is the only documented arrival
                    // outside that filter and .NET 8 does not raise it on this path), and IsAlive
                    // catches the HasExited one. The Win32Exception HasExited could raise in
                    // principle is unreachable from here, TryOpen having just read StartTime on
                    // the same handle, which needs the same access.
                    //
                    // It covers the paths where a kill HAD a target. Where no pid ever landed,
                    // `pid` is null, KillTreeQuietly killed nothing, the poll's first sample is
                    // not-alive and it returns at once — and on the grandchild shape the process
                    // still holding the directory is exactly the one nothing here can name. That
                    // residual is the header's, and this wait does not close it.
                    await Task.Run(() => WaitForDeath(pid, startedUtc, DeathWindow, pidFile));
                }
            }
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    // ── #529's guard, pinned comparison by comparison ────────────────────────────────────────

    /// <summary>
    /// <see cref="TryOpen"/> refuses a process that started before the attempt did, and one that
    /// started after the pid file announcing it was written (#529).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>IT LIVES HERE RATHER THAN IN <c>ProcessKillGuardParityTests</c>, AND THE REASON IS
    /// WHAT EACH FILE CAN SEE.</strong> That file pins a SOURCE-level property — the two
    /// <c>KillTreeQuietly</c> copies naming the same catch types — by parsing both files with
    /// Roslyn, which is the only way to compare code it cannot call. What #529 needs is the
    /// opposite: <see cref="TryOpen"/> is a private static of THIS class, and a behavioural pin
    /// has to invoke it with inputs it chooses. No other file can, short of reflection. The guard
    /// and its pin therefore sit together, and the parity file keeps the one job it can do.
    /// </para>
    /// <para>
    /// <strong>The live process is this test host, which is what makes the row instant and
    /// leak-free.</strong> <see cref="TryOpen"/> needs a pid that resolves to something alive; it
    /// does not need that something to be a child. Using the runner's own process means no
    /// launch, no teardown and no scratch child to strand — and the process it hands back is
    /// disposed rather than killed, which is the one thing this row must never do to its own
    /// host. The relationships under test are then SYNTHESISED around it: each case moves
    /// <c>startedUtc</c> or the pid file's write time to put the host's real start time on the
    /// wrong side of exactly one comparison.
    /// </para>
    /// <para>
    /// <strong>Nothing backstops that, and the census is the wrong shape to.</strong>
    /// <see cref="EveryFinallyThatKills_ReclaimsAPidFirst"/> counts killing <c>finally</c> blocks,
    /// so a kill added to THIS row's <c>finally</c> would redden it at five-not-six — but a kill
    /// added to its BODY is invisible to a rule about <c>finally</c> clauses. That matters more
    /// here than anywhere else in the file: the pid this row hands to
    /// <see cref="TryOpen"/> is the test runner's own, and
    /// <see cref="KillTreeQuietly"/> goes through <c>ChildProcess.KillTreeQuietly</c> with
    /// <c>entireProcessTree: true</c>, so one line would take the runner and everything beneath
    /// it. Open, read, dispose — never kill — and no gate will say so for you.
    /// </para>
    /// <para>
    /// <strong>Each case isolates ONE comparison, which is what makes the drill meaningful.</strong>
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>older-than-the-attempt</c> — an attempt that began an hour after this process did.
    ///     The lower bound refuses it; the upper bound would have accepted it, the write time
    ///     being later still. Deleting the lower bound reddens this row and no other.
    ///   </description></item>
    ///   <item><description>
    ///     <c>younger-than-the-announcement</c> — a pid file backdated to ten seconds before this
    ///     process started. The lower bound accepts, since the attempt is dated from the process
    ///     itself; the upper bound refuses, the start being later than the write by more than
    ///     <see cref="PidStartTimeTolerance"/>. Backdated rather than slept for: the relationship
    ///     is what is under test, not the clock.
    ///   </description></item>
    ///   <item><description>
    ///     <c>inside-both-bounds</c> — the positive control, and the direction that costs MORE
    ///     than a leak. A guard tightened until it refuses the real child kills nothing and
    ///     leaves it running for <see cref="ChildLifetime"/>; worse, the same refusal reaches
    ///     rows 1 and 5 as <see cref="IsAlive"/> answering <see langword="false"/>, so their
    ///     <c>dead</c> assertion goes GREEN over that running child. Those rows cannot notice it
    ///     — drill four below measured exactly that — which is why this case is carried here and
    ///     why <see cref="PidStartTimeTolerance"/>'s remarks set out what does and does not
    ///     redden.
    ///   </description></item>
    ///   <item><description>
    ///     <c>no-pid-file</c> — the fail-toward-today fallback. With no file to stat there is no
    ///     upper bound to apply, and the answer must be the one this guard gave before #529
    ///     rather than a refusal. Making the fallback fail-closed reddens this case alone.
    ///   </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>DRILLED, each comparison separately, on the maintainer's Windows host
    /// 2026-09-20 — 21 rows in the class, and the figure after each mutation is how many went
    /// red.</strong> Removing the lower bound: 1, <c>older-than-the-attempt</c>. Removing the
    /// upper bound: 1, <c>younger-than-the-announcement</c>. Making the missing-file fallback
    /// refuse instead of accept: 1, <c>no-pid-file</c>. Tightening the lower bound from minus
    /// five seconds to plus five: 5 — <c>inside-both-bounds</c> and <c>no-pid-file</c> here, both
    /// parametrisations of <see cref="TheGuardsAnchors_StayInTheOrderARealChildProduces"/>, and
    /// the grandchild parametrisation of
    /// <see cref="PidFileWriters_PublishAPidTheReaderAccepts"/>, whose liveness assertion runs
    /// through this guard. Each mutation was reverted, and the fourth was re-run after this row
    /// grew its guarded reader and explicit cases: the same five, unchanged.
    /// </para>
    /// <para>
    /// <strong>READ THAT FOURTH FIGURE FOR WHO IS MISSING FROM IT.</strong> Rows 1 and 5 are
    /// not among the five, under a guard that opened NOTHING — because a refusal reaches them as
    /// <see cref="IsAlive"/> answering <see langword="false"/>, which
    /// <see cref="WaitForDeath"/> reports as a death, which is what their <c>dead</c> assertion
    /// wants to hear. The #481 leak cover passes vacuously over a live child in exactly the case
    /// a mis-sized tolerance produces. That is the finding those five red rows are standing in
    /// for, and <see cref="TryOpen"/>'s remarks carry the mechanism.
    /// </para>
    /// <para>
    /// <strong>What it does NOT pin is the SIZE of the tolerance.</strong> Every case here is
    /// seconds clear of it, deliberately, so that a row about WHICH comparisons exist cannot
    /// redden over a figure being tuned. The figure is answerable only to a real child's skew,
    /// which is what <see cref="TheGuardsAnchors_StayInTheOrderARealChildProduces"/> measures.
    /// Nor does it pin identity: a pid recycled inside the window both comparisons accept passes
    /// this row exactly as it passes the guard, and no timestamp closes that.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("older-than-the-attempt")]
    [InlineData("younger-than-the-announcement")]
    [InlineData("inside-both-bounds")]
    [InlineData("no-pid-file")]
    public void TryOpen_RefusesAProcessOutsideTheWindowTheChildMustHaveStartedIn(string relation)
    {
        using var host = Process.GetCurrentProcess();
        var hostStartedUtc = host.StartTime.ToUniversalTime();
        var directory = CreateScratchDirectory();
        var pidFile = Path.Combine(directory, PidFileName);

        try
        {
            // Every case spelled out, and an unrecognised one THROWS rather than falling into
            // the last arm. A `default:` that set up one of the cases would turn a typo'd or
            // newly added InlineData into a silent duplicate of it — and `refusalExpected`
            // below, computed over the same strings, would agree with the duplicate, so the row
            // would pass for the wrong reason. Loud at the line is this file's posture for a
            // rule that must never go quiet.
            DateTime startedUtc;
            bool refusalExpected;
            switch (relation)
            {
                case "older-than-the-attempt":
                    startedUtc = hostStartedUtc.AddHours(1);
                    File.WriteAllText(pidFile, "1\n");
                    refusalExpected = true;
                    break;

                case "younger-than-the-announcement":
                    startedUtc = hostStartedUtc;
                    File.WriteAllText(pidFile, "1\n");
                    File.SetLastWriteTimeUtc(pidFile, hostStartedUtc.AddSeconds(-10));
                    refusalExpected = true;
                    break;

                case "inside-both-bounds":
                    startedUtc = hostStartedUtc;
                    File.WriteAllText(pidFile, "1\n");
                    refusalExpected = false;
                    break;

                case "no-pid-file":
                    startedUtc = hostStartedUtc;
                    refusalExpected = false;
                    break;

                default:
                    throw new InvalidOperationException(
                        FormattableString.Invariant(
                            $"'{relation}' is not a relation this row sets up. Each InlineData names one arrangement of a process start, an attempt date and a pid file's write time, and the arrangement is what decides whether TryOpen must refuse — so an unrecognised name has no expected answer and must not borrow another case's."));
            }

            var opened = TryOpen(host.Id, startedUtc, pidFile);
            using (opened)
            {
                Assert.True(
                    refusalExpected == (opened is null),
                    FormattableString.Invariant(
                        $"TryOpen {(opened is null ? "refused" : "accepted")} pid {host.Id} in the '{relation}' case, where it must {(refusalExpected ? "refuse" : "accept")} it. Signed against the process's own start, negative meaning earlier: the attempt is dated {(startedUtc - hostStartedUtc).TotalSeconds:F1}s from it, and the pid file was written {((PidFileWrittenUtc(pidFile) ?? hostStartedUtc) - hostStartedUtc).TotalSeconds:F1}s from it — no file at all reads as 0.0 here, and means the upper bound was skipped. A refusal that should have been an acceptance leaks whatever the pid named for {ChildLifetime.TotalSeconds:F0}s, because KillTreeQuietly then kills nothing; an acceptance that should have been a refusal tree-kills a stranger, DESCENDANTS INCLUDED. Both bounds and the missing-file fallback are separately drilled — see this row's remarks before re-aiming it."));
            }
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// A real child's start time precedes its own pid file's write time, by enough that
    /// <see cref="PidStartTimeTolerance"/> is never what admits it (#529).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A CANARY FOR THE TOLERANCE, NOT A PIN ON IT.</strong> It asserts the guard's upper
    /// comparison holds for a child this file actually launches, and reports the margin by which
    /// it holds. Shrinking <see cref="PidStartTimeTolerance"/> would not redden it on a host
    /// where the skew is negative — the measured Windows case, where it is negative by 25ms at
    /// its tightest — so this row cannot stand in for choosing the figure. What it catches is the
    /// case that figure exists for: a host where the two clocks disagree in the other direction
    /// far enough to make a real child look younger than its own announcement.
    /// </para>
    /// <para>
    /// <strong>THE LANE THAT GATES MERGES IS THE ONE NOBODY HAS MEASURED.</strong>
    /// <see cref="PidStartTimeTolerance"/>'s remarks record Windows figures and an INFERRED
    /// account of Linux, where .NET derives <see cref="Process.StartTime"/> from
    /// <c>/proc</c> and a boot-time estimate rather than from a per-process timestamp. This row
    /// turns that into data by writing the observed skew to the test output on every run, pass or
    /// fail — which is why this class takes an <see cref="ITestOutputHelper"/> at all, an
    /// assertion message saying nothing on the green run that carries the number.
    /// </para>
    /// <para>
    /// <strong>AND THE GREEN HALF OF THAT NEEDED A WORKFLOW CHANGE, WHICH IS WORTH KNOWING
    /// BEFORE TRUSTING IT.</strong> VSTest's console logger prints captured output for FAILED
    /// tests only, so on a lane running <c>dotnet test</c> with no logger the green line goes
    /// nowhere at all — written, captured, discarded. <c>build.yml</c>'s unit-test step therefore
    /// carries <c>--logger "trx;LogFilePrefix=unit"</c>, and the trx lands in
    /// <c>TestResults/</c>, inside the coverage artifact that step's neighbour already uploads
    /// with <c>if: always()</c>. A PREFIX rather than a fixed name because that step runs the
    /// whole solution and one test host per project writes one trx — a fixed name is a single
    /// path they overwrite in turn, MEASURED, and the reasoning is kept at the workflow. What
    /// this buys is precise and limited: the figure is retrievable from a build artefact by
    /// someone who goes looking, not visible in the job log. What IS visible without going
    /// looking is the RED path — a skew above <see cref="PidStartTimeTolerance"/> fails this row,
    /// and a failed test's output the console logger does print. Delete that flag and the green
    /// figure is silently lost again.
    /// </para>
    /// <para>
    /// <strong>Both shapes, because they bracket the quantity.</strong>
    /// <see cref="NeverExitingChild"/> announces its own pid after a whole interpreter start-up;
    /// <see cref="GrandchildHoldingPipesChild"/> announces a process spawned immediately before
    /// the write, which is the tighter of the two and the one that decides whether a tolerance is
    /// big enough. Launched directly, for the reasons
    /// <see cref="PidFileWriters_PublishAPidTheReaderAccepts"/> gives, and torn down the same
    /// way — the pid kill, the handle kill, then the death poll that lets the directory go.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(PidFileName)]
    [InlineData(GrandchildPidFileName)]
    public async Task TheGuardsAnchors_StayInTheOrderARealChildProduces(string pidFileName)
    {
        var announcesTheShellItself =
            string.Equals(pidFileName, PidFileName, StringComparison.Ordinal);
        var shape = announcesTheShellItself ? NeverExitingChild() : GrandchildHoldingPipesChild();
        var startedUtc = DateTime.UtcNow;
        var directory = CreateScratchDirectory();
        var pidFile = Path.Combine(directory, pidFileName);

        var startInfo = new ProcessStartInfo(shape.FileName)
        {
            UseShellExecute = false,
            WorkingDirectory = directory,
        };

        foreach (var argument in shape.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Process.Start returned no Process for a child shape this file launches "
                    + "directly, so there is nothing to wait for and nothing to kill.");

            using (process)
            {
                int? pid = null;
                try
                {
                    pid = await Task.Run(() => WaitForPid(
                        pidFile, Task.CompletedTask, UnracedPidCeiling, UnracedPidCeiling));

                    Assert.True(
                        pid is not null,
                        FormattableString.Invariant(
                            $"The writer of '{pidFileName}' published nothing ReadPid would accept inside {UnracedPidCeiling.TotalSeconds:F0}s, so there is no announced process to read a start time from and this row measured nothing. It is PidFileWriters_PublishAPidTheReaderAccepts that names why a pid line fails to arrive — read that row's red first; this one is downstream of it and says nothing new about it."));

                    var writtenUtc = PidFileWrittenUtc(pidFile);
                    Assert.True(
                        writtenUtc is not null,
                        FormattableString.Invariant(
                            $"'{pidFileName}' held a pid ReadPid accepted, but its last-write time could not be established — so TryOpen's upper bound would fall back to the lower bound alone on this host, silently. The fallback is deliberate (see PidStartTimeTolerance) and this row is where its cost is noticed: on a host that always answers this way, #529's upper bound is inert and the guard is the one it was before."));

                    // Through the guarded reader, not Process.GetProcessById + StartTime
                    // directly: both throw on an announced process that has just exited or
                    // refuses the query, and an escape here would report this row's finding as a
                    // framework message — the #512 shape, in the row written to close #529.
                    var announcedStartUtc = ProcessStartedUtc(pid!.Value);
                    Assert.True(
                        announcedStartUtc is not null,
                        FormattableString.Invariant(
                            $"'{pidFileName}' held pid {pid}, but no start time could be read for it — the process has exited since ReadPid accepted the line, or the query was refused. Either way this row has nothing to compare against the file's write time and measured nothing; it is not evidence about #529's bounds. A shape whose announced process does not outlive its own row is a defect in the shape, and {(announcesTheShellItself ? "NeverExitingChild sleeps for" : "GrandchildHoldingPipesChild pings for")} {ChildLifetime.TotalSeconds:F0}s precisely so that it does."));

                    var skew = announcedStartUtc!.Value - writtenUtc!.Value;

                    _output.WriteLine(FormattableString.Invariant(
                        $"#529 skew for '{pidFileName}' on {RuntimeInformation.OSDescription}: StartTime - LastWriteTimeUtc = {skew.TotalMilliseconds:F1}ms (tolerance {PidStartTimeTolerance.TotalMilliseconds:F0}ms). Negative is the order a real child produces."));

                    Assert.True(
                        skew <= PidStartTimeTolerance,
                        FormattableString.Invariant(
                            $"A child this file launched itself started {skew.TotalMilliseconds:F1}ms AFTER its own pid file was written, which is more than the {PidStartTimeTolerance.TotalMilliseconds:F0}ms TryOpen tolerates — so teardown would refuse the real child, kill nothing, and leave it running for {ChildLifetime.TotalSeconds:F0}s. The child cannot really have started after announcing itself, so this is the two clocks disagreeing: Process.StartTime comes from the OS process table and the write time from the filesystem, and on this platform they are further apart than PidStartTimeTolerance allows. Raise the tolerance against the figure this row printed rather than removing the bound."));

                    // End to end, and disposed rather than leaked: the guard that will be asked
                    // to open this pid in the `finally` below accepts it now.
                    using var openedForTeardown = TryOpen(pid, startedUtc, pidFile);
                    Assert.True(
                        openedForTeardown is not null,
                        FormattableString.Invariant(
                            $"TryOpen refused the pid '{pidFileName}' announced, although this row has just established that the process is alive and that its start time sits {skew.TotalMilliseconds:F1}ms before the file's write time. Both of #529's comparisons should therefore accept it, so a refusal here means a bound is reading something other than what this row measured — and in teardown it would mean killing nothing."));
                }
                finally
                {
                    if (pid is null)
                    {
                        pid = await ReclaimPidForTeardownQuietlyAsync(pidFile, Task.CompletedTask);
                    }

                    KillTreeQuietly(pid, startedUtc, pidFile);
                    ChildProcess.KillTreeQuietly(process);
                    await Task.Run(() => WaitForDeath(pid, startedUtc, DeathWindow, pidFile));
                }
            }
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    // ── #539's halves, pinned (see each row for what it does NOT pin) ────────────────────────

    /// <summary>
    /// The quiet wrapper keeps polling: a pid line left TORN and COMPLETED late inside
    /// <see cref="LateReadWindow"/> is reclaimed, and a file that never appears yields
    /// <see langword="null"/> after the whole window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>NOT A <c>Run</c> ROW.</strong> It launches no child and never calls
    /// <see cref="SystemProcessRunner"/>. What it pins is the SEMANTICS the five <c>finally</c>
    /// blocks depend on: that a write COMPLETING after the caller's original wait gave up is still
    /// picked up — which requires the reader to look again after refusing an incomplete one, the
    /// entire mechanism <see cref="ReclaimPidForTeardownAsync"/>'s first paragraph describes — and
    /// that a file which never appears costs the window and yields nothing rather than hanging or
    /// throwing.
    /// </para>
    /// <para>
    /// <strong>What it does NOT pin, stated here because the row's name invites the stronger
    /// reading.</strong> It says nothing about whether a real, live, late-writing CHILD is killed
    /// on the red path — that is the liveness half, and it has no portable harness (see
    /// <see cref="EveryFinallyThatKills_ReclaimsAPidFirst"/>'s remarks, which carry the argument
    /// for both rows). The pid written here is a literal; nothing opens it, and
    /// <see cref="KillTreeQuietly"/> is never called.
    /// </para>
    /// <para>
    /// <strong>The <c>work</c> handed over NEVER settles, and that is what makes both cases
    /// measure the window rather than a coincidence.</strong> <see cref="WaitForPid"/> ends on
    /// whichever of two arms fires first, and an unsettled task leaves only the CEILING —
    /// <see cref="LateReadWindow"/>, since <see cref="ReclaimPidForTeardownAsync"/> passes it as
    /// both arguments. Nothing awaits that task, so leaving it incomplete costs nothing: it
    /// carries no result and no exception, and is collected with the row.
    /// </para>
    /// <para>
    /// <strong>Case (a) starts from a TORN line, not from an absent file, and that is what makes
    /// it a test of POLLING.</strong> The pid file exists before the wrapper is called, holding
    /// the digits with NO terminator — the shape <see cref="ReadPid"/>'s #528 contract answers
    /// "not yet" to. It is completed (<c>digits</c> then <c>\n</c>) one second later. So every
    /// read the wrapper can possibly make before that moment is a read it MUST refuse, and the
    /// only way to return the pid is to look again afterwards: a one-shot reader reddens on the
    /// equality no matter when the thread pool got round to it. Against a file that was merely
    /// ABSENT at t=0 — the arrangement this case first carried — a one-shot that happened to be
    /// scheduled after the write would have passed, which is the objection this answers.
    /// MEASURED: with <see cref="WaitForPid"/> cut down to a single <see cref="ReadPid"/>, this
    /// case fails <c>Assert.Equal() Failure: Values differ / Expected: 51234 / Actual: null</c>.
    /// </para>
    /// <para>
    /// <strong>It moves one boundary, and that is what <see cref="WriteCompletingTheLine"/> pays
    /// for.</strong> A file present from t=0 is a file the poll OPENS, every
    /// <see cref="PollIntervalMs"/> milliseconds, under <see cref="FileShare.Read"/>; the absent
    /// file it replaced was never opened at all. So the completing write can now collide with a
    /// read and throw on the test thread — Windows only, and rare — which is a flake rather than a
    /// finding. That helper retries it briefly; its remarks carry the argument.
    /// </para>
    /// <para>
    /// <strong>The completion is a WHOLE line, and the terminator is an LF literal.</strong>
    /// <see cref="ReadPid"/> refuses anything else (#528), which is exactly why the torn form
    /// above works as a "not yet" and why the completed form must be spelled precisely. LF rather
    /// than <see cref="Environment.NewLine"/>, for the reason
    /// <see cref="ReadPid_AcceptsOnlyACompleteNewlineTerminatedDigitLine"/> gives at length: the
    /// constant is per-platform and would pin only the lane it ran in.
    /// </para>
    /// <para>
    /// <strong>The delay is longer than a poll interval and far shorter than the window.</strong>
    /// One second against <see cref="PollIntervalMs"/>'s hundred milliseconds leaves room for
    /// several refused reads before the completion, and against a five-second window it leaves
    /// four seconds of slack, so a loaded agent does not turn this into a flake. Total cost is
    /// about six seconds: one for the arriving case, five for the missing one, which is
    /// irreducible because the missing case IS the window.
    /// </para>
    /// <para>
    /// <strong>THE RESIDUAL IN CASE (a), STATED BECAUSE THE TORN LINE NARROWS IT RATHER THAN
    /// CLOSING IT.</strong> <see cref="ReclaimPidForTeardownAsync"/> dispatches through
    /// <c>Task.Run</c>, so a thread pool under enough load can leave the work item queued until
    /// after the completion lands. The wrapper's FIRST read then sees a whole line, the case
    /// reduces to a single read, and a one-shot implementation passes it. Nothing about the
    /// wrapper is observable from outside that would let this row tell the two apart — it has no
    /// seam reporting how many reads happened, and adding one would be production surface existing
    /// only for a test. What the torn line buys is that the pass is no longer AVAILABLE to a
    /// one-shot on an unloaded host, which is where the drill above was taken and where this lane
    /// runs; what it cannot buy is a guarantee under arbitrary load. Case (b)'s lower bound is
    /// untouched by any of this: it asserts that the whole window was spent, which no scheduling
    /// delay can shorten.
    /// </para>
    /// <para>
    /// <strong>Neither case asserts an elapsed UPPER bound, and BOTH race rather than await
    /// outright.</strong> No upper bound, because returning the pid already proves case (a)'s look
    /// stayed inside its ceiling, and an elapsed check would charge the wrapper for thread-pool
    /// scheduling that happened before its own clock started. A race in both, because that guards
    /// a different failure the pid cannot: a wrapper that kept polling PAST its ceiling never
    /// completes, and an unbounded <c>await</c> on it would WEDGE this blocking assembly rather
    /// than redden it — the trade the file header sets out for every hang row, and the reason
    /// three windows is slack rather than a latency target. Case (b) has the extra reason too: its
    /// expected answer is <see langword="null"/>, so a lost ceiling returns nothing to assert on
    /// at all. Case (b)'s LOWER bound stays — that is the half proving the window was actually
    /// spent, and it is the one assertion in this row no scheduling delay can weaken.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ReclaimPidForTeardownQuietly_TakesALateWholeLinePid_AndNullWhenNoneArrives()
    {
        const int ProbePid = 51234;
        var lateWriteDelay = TimeSpan.FromSeconds(1);

        // DateTime.UtcNow, which WaitForPid's ceiling is computed from, ticks at roughly 15ms on
        // Windows, while the Stopwatch below is high-resolution. The lower-bound assertion in case
        // (b) is therefore allowed to come in a shade under the window without that being a
        // finding about the wrapper.
        var clockTolerance = TimeSpan.FromMilliseconds(250);

        var directory = CreateScratchDirectory();
        var pidFile = Path.Combine(directory, PidFileName);
        var never = new TaskCompletionSource();
        try
        {
            // (a) A line COMPLETED inside the window is reclaimed.
            //
            // THE FILE EXISTS FROM t=0, HOLDING A TORN LINE, and that is what makes this case
            // about POLLING rather than about a delay. The digits are there with no terminator,
            // which ReadPid's #528 contract answers "not yet" to — so a reader that runs at any
            // moment before the rewrite below sees a file it must refuse, and only a reader that
            // LOOKS AGAIN afterwards can return the pid. A one-shot implementation reddens on the
            // equality whatever the thread pool did with its first read; against a file that
            // merely did not exist yet, a one-shot scheduled late would have passed.
            File.WriteAllText(pidFile, ProbePid.ToString(CultureInfo.InvariantCulture));

            // NO WALL-CLOCK UPPER BOUND ASSERTED, but the await IS bounded, and the two are not
            // the same thing. Returning the pid at all IS the in-window evidence — the wrapper's
            // ceiling is LateReadWindow, so a look that had outrun it would answer null and the
            // equality would redden — and an elapsed check would add nothing while charging the
            // wrapper for thread-pool scheduling that happened before its own clock started. What
            // the race guards is the other failure: a wrapper that kept polling PAST the ceiling
            // would never complete, and an unbounded await on it would wedge this blocking
            // assembly instead of failing it. Same trade, same three-window slack, as case (b).
            var arriving = ReclaimPidForTeardownQuietlyAsync(pidFile, never.Task);
            var arrivingClock = Stopwatch.StartNew();

            await Task.Delay(lateWriteDelay);
            WriteCompletingTheLine(
                pidFile, ProbePid.ToString(CultureInfo.InvariantCulture) + "\n");

            var arrivingSettled =
                await Task.WhenAny(arriving, Task.Delay(LateReadWindow * 3)) == arriving;
            Assert.True(
                arrivingSettled,
                FormattableString.Invariant(
                    $"ReclaimPidForTeardownQuietlyAsync had not returned {arrivingClock.Elapsed.TotalSeconds:F0}s after a whole pid line was completed {lateWriteDelay.TotalSeconds:F0}s into its own {LateReadWindow.TotalSeconds:F0}s window. A look that keeps polling past its ceiling does not delay a red row — it hangs the finally it is called from, and this assembly is a blocking gate with no per-test timeout."));

            Assert.Equal(ProbePid, await arriving);

            // (b) No write at all: null, and only after the whole window has been spent.
            //
            // RACED RATHER THAN AWAITED OUTRIGHT, in this file's own idiom: an unbounded await on
            // a ceiling that had regressed to "never" would WEDGE this blocking gate instead of
            // failing it, which is the trade the file header sets out for every hang row. Three
            // windows is slack against a loaded agent while still being finite.
            File.Delete(pidFile);
            var clock = Stopwatch.StartNew();

            var look = ReclaimPidForTeardownQuietlyAsync(pidFile, never.Task);
            var settled = await Task.WhenAny(look, Task.Delay(LateReadWindow * 3)) == look;
            Assert.True(
                settled,
                FormattableString.Invariant(
                    $"ReclaimPidForTeardownQuietlyAsync had not returned {clock.Elapsed.TotalSeconds:F0}s after it was called against a pid file that never appeared, although its own ceiling is {LateReadWindow.TotalSeconds:F0}s. A teardown look with no ceiling does not delay a red row — it hangs the finally it is called from, and this assembly is a blocking gate with no per-test timeout."));

            var missing = await look;
            var missingElapsed = clock.Elapsed;

            Assert.Null(missing);
            Assert.True(
                missingElapsed >= LateReadWindow - clockTolerance,
                FormattableString.Invariant(
                    $"ReclaimPidForTeardownQuietlyAsync gave up after {missingElapsed.TotalSeconds:F1}s against a pid file that never appeared, short of its own {LateReadWindow.TotalSeconds:F0}s window. The window is the whole of what this wrapper buys a late-writing survivor; a look that returns early buys less than the finally blocks are told it does."));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// The quiet wrapper answers <see langword="null"/> on the ACL fault its loud sibling throws,
    /// and answers it at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The swallow is <see cref="ReclaimPidForTeardownQuietlyAsync"/>'s ONLY non-trivial
    /// behaviour, and until this row nothing exercised it.</strong> Everything else that method
    /// does is delegate. Its <c>catch</c> exists because a <c>finally</c> that throws REPLACES the
    /// finding being propagated — the wedged runner, the undead child — with a permission detail,
    /// and the five teardown blocks that call it are all <c>finally</c> blocks. A deleted catch
    /// changes no other row in this file: rows 1, 2 and 5, and the two directly-launching rows
    /// that joined them as the fourth and fifth callers (#541, #529), never meet an ACL fault on
    /// a scratch directory they created themselves, so the suite would stay green over it.
    /// </para>
    /// <para>
    /// <strong>Three things are pinned, and the middle one is what makes the first mean
    /// something.</strong> The LOUD reader (<see cref="ReclaimPidForTeardownAsync"/>) throws
    /// <see cref="UnauthorizedAccessException"/> on this arrangement — that is
    /// <see cref="ReadPid"/>'s deliberate choice, recorded at its remarks, and asserting it here
    /// is what proves the quiet wrapper's <see langword="null"/> is a SWALLOW rather than an
    /// absence of anything to swallow. The QUIET wrapper answers <see langword="null"/>. And it
    /// answers PROMPTLY: the fault fires on the first <see cref="ReadPid"/>, so a wrapper that
    /// caught it and went on polling would still answer <see langword="null"/> but spend the whole
    /// of <see cref="LateReadWindow"/> doing it, which is the cost every "at most once per row"
    /// claim in this file is written against.
    /// </para>
    /// <para>
    /// <strong>THE ARRANGEMENT IS ASSERTED BEFORE IT IS USED, because a denial that did not take
    /// is a silent green.</strong> If the read were permitted, the loud reader would return a pid,
    /// the quiet one would return the same pid, and both of this row's real assertions would fail
    /// loudly — but only after a reader of the failure had been sent to look at the wrapper rather
    /// than at the host. So the row reads the file itself first and requires the fault, with a
    /// message naming the host condition that produces the other answer: running as <c>root</c>,
    /// or as a Windows identity whose access is evaluated against a different token than the one
    /// the ACE names.
    /// </para>
    /// <para>
    /// <strong>The platform split, and why the Windows side uses a DENY ace.</strong> On POSIX the
    /// denial is <see cref="UnixFileMode.None"/> — mode 000, which <c>root</c> ignores and nobody
    /// else does. On Windows it is an explicit DENY of <see cref="FileSystemRights.Read"/> for
    /// <see cref="WindowsIdentity"/>'s own user SID rather than the removal of an ALLOW, because
    /// deny entries are evaluated first: a developer box where the account also picks up access
    /// through Administrators still sees the fault. The file is created first and denied
    /// afterwards, so the arrangement does not depend on inheritance.
    /// </para>
    /// <para>
    /// <strong>The residual is a LOUD one, which is the safe direction.</strong> A host on which
    /// the denial is not enforced for the running identity fails this row at the arrangement
    /// assertion, naming that condition — it does not pass over an unexercised catch. Restoring
    /// access happens in a <c>finally</c> and BEFORE the directory delete, guarded, so a host that
    /// refuses the restore cannot turn teardown into the row's reported failure; see
    /// <see cref="TryRestoreRead"/>.
    /// </para>
    /// <para>
    /// <strong>REDDENING RATHER THAN SKIPPING IS A DIVERGENCE FROM THIS REPOSITORY'S OWN
    /// PRECEDENT, and it is deliberate.</strong> <c>GitChangeSetTests.HasRootsReach()</c> meets
    /// the same POSIX fact — <c>root</c> ignores file modes — and SKIPS its affected rows. This
    /// one does not, because the two have different things to lose. Those rows test whether a
    /// mode-0000 file can be launched, a property that is VACUOUS under root — root can launch it,
    /// so there is nothing left to diverge and a skip retires nothing. This row's property — that
    /// <see cref="ReclaimPidForTeardownQuietlyAsync"/>'s <c>catch</c> exists and swallows — holds
    /// under root just as well; it is only this ARRANGEMENT that root defeats, and this row is the
    /// catch's only cover, so a skip would retire it precisely on the lane that gates merges if
    /// that lane ever runs as root. A red row naming the host condition is recoverable in one
    /// reading; a green suite over an unexercised catch is not.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ReclaimPidForTeardownQuietly_AnswersNullOnAnAclFault_WhereTheLoudReaderThrows()
    {
        const int ProbePid = 51234;

        // HALF THE WINDOW. The discrimination this needs is "promptly" against "sat out the whole
        // of LateReadWindow", and 2.5s of 5s draws that line as cleanly as any smaller figure
        // while leaving 2.5x the margin against thread-pool scheduling — which this row's clock
        // starts before and therefore charges to the wrapper. A first-read fault costs
        // milliseconds; a swallow-then-keep-polling regression costs the full five seconds.
        var promptCeiling = LateReadWindow / 2;

        var directory = CreateScratchDirectory();
        var pidFile = Path.Combine(directory, PidFileName);
        var never = new TaskCompletionSource();

        // INSIDE THE TRY, both of them. Every other row in this file has its filesystem work
        // under the try that owns the cleanup; these two were the exception, and a throw from
        // either — a full disk on the write, a platform refusal on the deny — would have skipped
        // TryRestoreRead and TryDeleteDirectory and left a denied file behind. TryRestoreRead is
        // a guarded no-op against a file that was never denied or never created, so moving them
        // in costs nothing on the paths where only one of the two ran.
        try
        {
            File.WriteAllText(pidFile, ProbePid.ToString(CultureInfo.InvariantCulture) + "\n");
            DenyRead(pidFile);

            // THE ARRANGEMENT, first and on its own terms. The EXISTENCE check is part of it:
            // stat is authorised by the parent directory rather than by the file's own DACL, so
            // it holds under the denial today — but on a host where it did not, ReadPid would
            // answer null from its File.Exists guard rather than throwing, and the loud-reader
            // assertion below would fail while pointing at the wrong thing.
            var arrangement = Record.Exception(() => File.ReadAllText(pidFile));
            Assert.True(
                File.Exists(pidFile) && arrangement is UnauthorizedAccessException,
                FormattableString.Invariant(
                    $"this row denies itself read access to its own pid file and then requires the read to fault, but the file {(File.Exists(pidFile) ? "is present" : "cannot even be stat'd")} and File.ReadAllText answered with {(arrangement is null ? "no exception at all" : arrangement.GetType().Name)}. The denial did not take for the identity this process runs as — running as root on POSIX ignores mode 000, and on Windows an identity whose access is evaluated against a token the deny ACE does not name sees no fault. Nothing below this line would be testing the wrapper's catch on such a host, so the row stops here rather than passing over it."));

            // THE LOUD READER THROWS, which is what makes the null below a swallow.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => ReclaimPidForTeardownAsync(pidFile, never.Task));

            // THE QUIET WRAPPER ANSWERS NULL, AND PROMPTLY. Raced rather than awaited outright,
            // in this file's idiom: a wrapper that neither threw nor returned would wedge a
            // blocking gate instead of reddening it.
            var clock = Stopwatch.StartNew();
            var quiet = ReclaimPidForTeardownQuietlyAsync(pidFile, never.Task);
            var settled = await Task.WhenAny(quiet, Task.Delay(LateReadWindow * 3)) == quiet;
            Assert.True(
                settled,
                FormattableString.Invariant(
                    $"ReclaimPidForTeardownQuietlyAsync had not returned {clock.Elapsed.TotalSeconds:F0}s after being handed a pid file it cannot read, although the fault fires on its first read and its own ceiling is {LateReadWindow.TotalSeconds:F0}s. A teardown look that neither answers nor throws hangs the finally it is called from, and this assembly is a blocking gate with no per-test timeout."));

            Assert.Null(await quiet);

            var elapsed = clock.Elapsed;
            Assert.True(
                elapsed < promptCeiling,
                FormattableString.Invariant(
                    $"ReclaimPidForTeardownQuietlyAsync answered null after {elapsed.TotalSeconds:F1}s against a pid file whose very first read faults, which is not the prompt answer the five finally blocks are costed against. A catch that swallows the fault and then keeps polling spends the whole {LateReadWindow.TotalSeconds:F0}s window on a file that will never become readable, and every 'at most one LateReadWindow per row' claim in this file is written against the prompt answer."));
        }
        finally
        {
            // Access back BEFORE the delete, so the directory sweep is not the thing that fails.
            TryRestoreRead(pidFile);
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>Denies the current identity read access to one file, on either platform.</summary>
    private static void DenyRead(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var file = new FileInfo(path);
            var security = file.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                identity.User!,
                FileSystemRights.Read,
                AccessControlType.Deny));
            file.SetAccessControl(security);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.None);
    }

    /// <summary>
    /// Undoes <see cref="DenyRead"/>, best effort.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Guarded for the reason every teardown in this file is: it runs from a <c>finally</c>, where
    /// a throw would replace the row's own finding with a permission detail — the same rule the
    /// wrapper under test follows, and the reason the filter takes
    /// <see cref="InvalidOperationException"/> as well as the two I/O families.
    /// <c>RemoveAccessRuleAll</c> and <c>SetAccessControl</c> can raise it on a security
    /// descriptor the platform will not accept, and an exception kind left off this list is one
    /// that erases a finding. A host that refuses the restore leaves one unreadable file inside a
    /// Guid-named scratch directory; the recursive delete below it can still remove that
    /// directory, because deletion is authorised by the PARENT, which this row created.
    /// </para>
    /// <para>
    /// <strong>The Windows restore reads the DACL of a file it has just denied itself
    /// <see cref="FileSystemRights.Read"/> on, and that is not the contradiction it looks
    /// like.</strong> <c>Read</c> includes <see cref="FileSystemRights.ReadPermissions"/> —
    /// READ_CONTROL, the very right <c>GetAccessControl</c> needs — so the restore works only
    /// because the file's OWNER holds implicit READ_CONTROL and WRITE_DAC whatever the DACL says,
    /// and this row created the file. MEASURED on this host: the deny takes, the restore succeeds,
    /// and the scratch directory is gone afterwards. A host that defeats that — an OWNER RIGHTS
    /// ACE narrowing the owner's implicit grant, or a filtered token — makes this a no-op, which
    /// is the bounded consequence the paragraph above already describes rather than a new one.
    /// </para>
    /// </remarks>
    private static void TryRestoreRead(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var file = new FileInfo(path);
                var security = file.GetAccessControl();
                security.RemoveAccessRuleAll(new FileSystemAccessRule(
                    identity.User!,
                    FileSystemRights.Read,
                    AccessControlType.Deny));
                file.SetAccessControl(security);
                return;
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or PlatformNotSupportedException
                                       or InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Every <c>finally</c> in THIS file that tree-kills is preceded, in the same block, by a
    /// <c>pid is null</c>-guarded reclaim — and there are exactly five of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>WHY A CENSUS AND NOT A BEHAVIOURAL ROW — the honest version, because the reviewer's
    /// request was for the behavioural one.</strong> Copilot asked for a permanent test that forces
    /// the red path #539 added and verifies the late-published child is killed. That path is only
    /// taken when an attempt has no pid AND an assertion has already ended the body, so provoking
    /// it means a child that SURVIVES the runner's own tree-kill — WMI re-parenting on Windows,
    /// <c>setsid</c> on Linux — and then publishes a pid. A harness for that is a second,
    /// platform-forked child fixture whose whole purpose is to defeat the production kill, carried
    /// permanently in a blocking lane, to cover a teardown detail of a test file. The before/after
    /// for that path was measured once and recorded in #539's commit message
    /// (<c>ebddeca</c>): with the tree-kill defeated by re-parenting the child and the grace
    /// assertion forced red, the child was alive after the red row before the fix and dead after
    /// it, on the same assertion message both times. What is kept permanently instead is the set
    /// below it decomposes into, each of which is cheap and deterministic: the WRAPPER's semantics
    /// (<see cref="ReclaimPidForTeardownQuietly_TakesALateWholeLinePid_AndNullWhenNoneArrives"/>),
    /// this row's PLACEMENT, and the ARMING POINT of the flag this row's guard consults
    /// (<see cref="TheLateLookFlag_IsArmedOnlyAfterTheLookCompletes"/>). Between them, a reclaim
    /// deleted from any of the five <c>finally</c> blocks reddens here, a reclaim that stopped
    /// picking up late writes reddens in the first, and a flag armed too early reddens in the
    /// third — a defect THIS row structurally cannot see, since it changes no <c>finally</c>.
    /// </para>
    /// <para>
    /// <strong>What the set still does NOT establish.</strong> That the kill issued a line later
    /// actually reaches a live survivor. Nothing here opens a process. That is the liveness half,
    /// and it is exactly the half no portable child can reproduce; it is named rather than implied
    /// so that nobody quotes these rows as cover for it.
    /// </para>
    /// <para>
    /// <strong>PRECEDES is dominance within the block, not lexical position</strong> — the
    /// distinction <c>DcpArmingWindowCensusTests</c>'s rule 2 records as a real defect in its own
    /// first version. Each of the two invocations is reduced to its BLOCK-LEVEL ANCESTOR —
    /// <see cref="BlockLevelStatement"/>, the statement on the <c>finally</c> block's own
    /// statement list that contains it, whatever KIND that statement is — and the reclaim's must
    /// sit at a lower index than the kill's. Control entering a block runs its statements in
    /// order, so a reclaim whose block-level ancestor is at index <em>i</em> has executed by the
    /// time index <em>j &gt; i</em> is reached. The invocations themselves need not be direct
    /// children: today's reclaims sit inside an <c>if</c>, and one inside a loop or a nested
    /// <c>try</c> would be reduced to that loop or try and counted the same way. What does NOT
    /// count is an invocation outside this <c>finally</c> altogether — and that exclusion is done
    /// by scope, since <c>InvocationsNamed</c> enumerates only the clause's own descendants; the
    /// helper's <see langword="null"/> answer for a node with no block-level ancestor is a
    /// defensive branch nothing here reaches.
    /// </para>
    /// <para>
    /// That reduction is a deliberate over-approximation in one direction: a reclaim inside
    /// <c>if (false)</c>, or a loop that runs zero times, is credited as though it ran. The
    /// dominance argument is about statement ORDER, not reachability, and deciding the latter
    /// needs the control-flow analysis rule 1 of the sibling census records having no compilation
    /// for. It is aimed at a reclaim being deleted or moved, not at one being disabled in place.
    /// </para>
    /// <para>
    /// <strong>The <c>pid is null</c> guard is part of the rule, and not decoration.</strong> An
    /// unguarded reclaim would spend <see cref="LateReadWindow"/> on every green run of every row,
    /// re-reading a file the caller already has a pid from —
    /// <see cref="LateReadWindow"/>'s own remarks turn on the claim that no green path reaches it.
    /// </para>
    /// <para>
    /// <strong>SO IS ROW 5's SECOND CONJUNCT, <c>&amp;&amp; !lateLookTaken</c>, and it needed a
    /// rule of its own because the first one is blind to it.</strong> <c>pid is null</c> holds on
    /// the path where row 5's BODY look already ran and found nothing, so a guard testing only
    /// that repeats a look the row has just finished — two <see cref="LateReadWindow"/>s where the
    /// row is costed for one. Deleting the conjunct leaves the <c>pid is null</c> check intact and
    /// the arming-point rule
    /// (<see cref="TheLateLookFlag_IsArmedOnlyAfterTheLookCompletes"/>) intact, so both existing
    /// censuses stay green over it. What it breaks is arithmetic stated elsewhere: the
    /// <c>longestLook</c> assertion at the top of row 5 weighs
    /// <see cref="UnracedPidCeiling"/> plus ONE <see cref="LateReadWindow"/> against
    /// <see cref="ChildLifetime"/>, and a row spending two is no longer the row that guard
    /// describes.
    /// </para>
    /// <para>
    /// <strong>Applied only where it can apply, by looking for the flag's DECLARATION.</strong>
    /// Rows 1 and 2 and the two directly-launching rows take no body look, so they have nothing to
    /// repeat and carry no flag; requiring the conjunct of them would be requiring a test of a
    /// local that does not exist. The rule
    /// therefore asks which killing <c>finally</c> sits in a method declaring a
    /// <c>lateLookTaken</c> local, requires EXACTLY ONE to (row 5), and requires that one's guard
    /// to carry the negation. Both halves are counted: a second flagged method means another row
    /// grew a body look and needs the same conjunct, and none means row 5's flag has gone.
    /// </para>
    /// <para>
    /// Its limit is the one this file's other syntax rules share: the conjunct is PATTERN-matched,
    /// not reasoned about. <c>!lateLookTaken</c> satisfies it; <c>lateLookTaken is false</c> and
    /// <c>lateLookTaken == false</c> mean the same thing and would redden it. Loud and re-aimable
    /// at the line, which is the safe direction for a rule that must never go quiet.
    /// </para>
    /// <para>
    /// <strong>Exactly five, because a count is what turns this from a check into a
    /// census.</strong> Rows 1, 2 and 5 carry one each; so do
    /// <see cref="PidFileWriters_PublishAPidTheReaderAccepts"/> (#541) and
    /// <see cref="TheGuardsAnchors_StayInTheOrderARealChildProduces"/> (#529), the two rows that
    /// launch a child shape directly. Those last two hold TWO kills each rather than one: a pid —
    /// the grandchild on one parametrisation, the shell itself on the other — and then the shell
    /// through the handle they hold, spelled <c>ChildProcess.KillTreeQuietly</c>.
    /// Both are covered, because the match below is on the BARE NAME and a member access carries
    /// the same one. What the count adds is NOT that an unreclaimed newcomer would go unnoticed —
    /// the offenders loop walks every clause it finds, so a SIXTH killing <c>finally</c> without
    /// a reclaim reddens with or without it. It is what that loop structurally cannot see: a
    /// well-formed newcomer, and a teardown path that has GONE. Both are changes to the set this
    /// file's residual-child arithmetic is stated over, which is the "enumeration standing in for
    /// a property" failure this repository's census remarks warn about, and the count is what
    /// makes either one arrive at a line rather than in a reader's head.
    /// <see cref="KillTreeQuietly"/>'s own body is not one: its
    /// <c>ChildProcess.KillTreeQuietly</c> call sits inside a <c>using</c> statement, which is not
    /// a <c>finally</c> clause in syntax however the compiler lowers it.
    /// </para>
    /// <para>
    /// <strong>Roslyn over this file's own source, and the self-reference is safe by NODE
    /// KIND.</strong> The method names below are string constants, and this row's messages spell
    /// them too, but the scan switches on <see cref="InvocationExpressionSyntax"/> — a string
    /// literal is never one, however it is spelled. <c>descendIntoTrivia: false</c> keeps the
    /// file's very large comment and doc-comment blocks out on top of that. The house precedent is
    /// <c>Vouchfx.Engine.Orchestration.Tests.ChildProcessKillCallSiteCensusTests</c>, which reads
    /// trees it does not reference for the same reason: the file is parsed as text off disk, so
    /// nothing has to load.
    /// </para>
    /// <para>
    /// <strong>Its limits.</strong> Identifier matching, not symbols: a reclaim reached through a
    /// differently-named wrapper, or a kill spelled through an alias, is invisible — as is the
    /// question of whether the pid the guard tests is the pid the kill receives. The guard is
    /// matched by PATTERN and not by meaning, so <c>pid is null</c> satisfies it while
    /// <c>pid == null</c>, <c>pid.HasValue is false</c> and a <c>pid ??= …</c> that needs no
    /// <c>if</c> at all do not — each would redden this row despite being the same intent. That is
    /// the safe direction (loud, at the line it names, and fixed by spelling the pattern) and it
    /// is also why the rule is stated as the pattern rather than as "a guarded look". It is aimed
    /// at the accident (somebody tidies the <c>finally</c> and the reclaim goes with it), not at
    /// an adversary.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryFinallyThatKills_ReclaimsAPidFirst()
    {
        const string KillMethod = "KillTreeQuietly";
        const string ReclaimMethod = "ReclaimPidForTeardownQuietlyAsync";
        const string FlagName = "lateLookTaken";
        const int ExpectedKillingFinallies = 5;

        var killing = SelfSource()
            .DescendantNodes(descendIntoTrivia: false)
            .OfType<FinallyClauseSyntax>()
            .Where(clause => InvocationsNamed(clause, KillMethod).Any())
            .ToList();

        Assert.True(
            killing.Count == ExpectedKillingFinallies,
            FormattableString.Invariant(
                $"This file holds {killing.Count} `finally` block(s) calling `{KillMethod}`, not the {ExpectedKillingFinallies} this census covers (rows 1, 2 and 5, and the two rows that launch a shape directly — the writer-contract theory #541 added and the guard-anchor row #529 added). A new one needs the same guarded `{ReclaimMethod}` ahead of its kill — see #539 — and one that has gone means a teardown path was removed. Re-aim this count rather than deleting it."));

        var offenders = new List<string>();
        foreach (var clause in killing)
        {
            foreach (var kill in InvocationsNamed(clause, KillMethod))
            {
                if (!GuardedReclaimPrecedes(clause, kill, ReclaimMethod))
                {
                    offenders.Add(Describe(kill));
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            FormattableString.Invariant(
                $"{offenders.Count} tree-kill(s) in a `finally` are not preceded, in the same block and under a `pid is null` guard, by `{ReclaimMethod}`. Without that look the kill receives whatever the attempt happened to hold — which on the path an assertion ended is null — and a child whose first write was merely late is left to ChildLifetime (#539). Put the guarded reclaim first:\n  ")
            + string.Join("\n  ", offenders));

        // THE SECOND CONJUNCT, which the guard-shape check above cannot see. Only the `finally`
        // whose METHOD declares a `lateLookTaken` local is in a position to repeat a look its own
        // body already took, and only that one must therefore test the flag as well.
        var flagged = killing
            .Where(clause => DeclaresTheLateLookFlag(clause, FlagName))
            .ToList();

        Assert.True(
            flagged.Count == 1,
            FormattableString.Invariant(
                $"{flagged.Count} of the {ExpectedKillingFinallies} killing `finally` blocks sit in a method declaring a `{FlagName}` local, not the 1 this rule covers (row 5). Rows 1 and 2 and the two directly-launching rows have no body look to repeat, so they carry no flag; row 5 does. A second flagged method means another row grew a body look — it needs the same `&& !{FlagName}` conjunct — and none means row 5's has gone."));

        var unflagged = flagged
            .Where(clause => !GuardAlsoTestsTheFlag(clause, ReclaimMethod, FlagName))
            .Select(Describe)
            .ToList();

        Assert.True(
            unflagged.Count == 0,
            FormattableString.Invariant(
                $"row 5's teardown reclaim is not guarded by `&& !{FlagName}` as well as by `pid is null`. Without that conjunct the `finally` repeats the look the body has already taken, so the row spends TWO {LateReadWindow.TotalSeconds:F0}s windows rather than one — and the `longestLook` guard at the top of that row, which weighs UnracedPidCeiling plus a single LateReadWindow against ChildLifetime, is then measuring less than the row actually spends. Restore the conjunct:\n  ")
            + string.Join("\n  ", unflagged));
    }

    /// <summary>
    /// Whether the member enclosing <paramref name="clause"/> declares a local by this name.
    /// </summary>
    private static bool DeclaresTheLateLookFlag(FinallyClauseSyntax clause, string flagName) =>
        clause.Ancestors()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault()
            ?.DescendantNodes(descendIntoTrivia: false)
            .OfType<VariableDeclaratorSyntax>()
            .Any(declarator => declarator.Identifier.ValueText == flagName)
            ?? false;

    /// <summary>
    /// Whether the <c>if</c> guarding this clause's reclaim also tests <c>!flagName</c>.
    /// </summary>
    /// <remarks>
    /// Matched as a <see cref="PrefixUnaryExpressionSyntax"/> carrying <c>!</c> over the bare
    /// identifier, anywhere in the condition — so <c>pid is null &amp;&amp; !lateLookTaken</c>
    /// satisfies it and the operand order does not matter. Pattern-matched rather than reasoned
    /// about: <c>lateLookTaken is false</c> and <c>lateLookTaken == false</c> mean the same thing
    /// and would redden this, which is the same safe direction
    /// <see cref="GuardedOnANullPid"/>'s remarks argue for the other conjunct.
    /// </remarks>
    private static bool GuardAlsoTestsTheFlag(
        FinallyClauseSyntax clause, string reclaimMethod, string flagName) =>
        InvocationsNamed(clause, reclaimMethod)
            .SelectMany(reclaim => reclaim.Ancestors()
                .TakeWhile(ancestor => ancestor is not FinallyClauseSyntax)
                .OfType<IfStatementSyntax>())
            .Any(statement => statement.Condition
                .DescendantNodesAndSelf()
                .OfType<PrefixUnaryExpressionSyntax>()
                .Any(negation =>
                    negation.OperatorToken.IsKind(SyntaxKind.ExclamationToken)
                    && negation.Operand is IdentifierNameSyntax { Identifier.ValueText: { } name }
                    && name == flagName));

    /// <summary>
    /// Writes <paramref name="content"/> over the pid file, retrying briefly while a concurrent
    /// reader holds it open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This retry exists because the torn-line arrangement moved a boundary.</strong>
    /// While case (a)'s file merely did not exist yet, <see cref="ReadPid"/>'s
    /// <see cref="File.Exists(string)"/> answered <see langword="false"/> and no handle was ever
    /// opened. Now the file is there from t=0, so the wrapper's poll opens it every
    /// <see cref="PollIntervalMs"/> milliseconds — <see cref="File.ReadAllText(string)"/> takes
    /// <see cref="FileShare.Read"/>, which excludes writers — and the completing write can land
    /// inside one of those reads.
    /// </para>
    /// <para>
    /// The fault is an <see cref="IOException"/> on the TEST thread, which would redden the row
    /// for a reason that is not about the wrapper. It is Windows-only (Unix does not enforce
    /// share modes) and rare — a hundred-microsecond read against a hundred-millisecond poll
    /// period, order one run in a thousand — which is exactly the kind of flake that arrives
    /// months later attached to an unrelated change. Retrying is the whole fix: the reader's
    /// handle is open for microseconds, so the next attempt finds it gone.
    /// </para>
    /// <para>
    /// Bounded and then RETHROWN rather than swallowed. A write that cannot land after every
    /// attempt is not a sharing race, and case (a) would fail on the equality anyway with a far
    /// worse message; letting the <see cref="IOException"/> out names the real fault.
    /// </para>
    /// </remarks>
    private static void WriteCompletingTheLine(string pidFile, string content)
    {
        const int Attempts = 10;
        const int BackoffMs = 20;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.WriteAllText(pidFile, content);
                return;
            }
            catch (IOException) when (attempt < Attempts)
            {
                // The poll holds a FileShare.Read handle for the length of one read. Wait out
                // that read rather than the whole poll period.
                Thread.Sleep(BackoffMs);
            }
        }
    }

    /// <summary>
    /// Row 5's <c>lateLookTaken = true</c> sits AFTER the body look it records, not before it —
    /// and there is exactly one such pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A SEPARATE RULE FROM THE PLACEMENT CENSUS ABOVE, because it is about a different
    /// statement in a different block.</strong> That one reads <c>finally</c> blocks and checks
    /// the SHAPE of the guard it finds there (<c>pid is null</c>, reclaim before kill). This one
    /// reads row 5's BODY and checks WHERE the flag that guard consults is assigned. Neither can
    /// see the other's subject: a flag set at the wrong point leaves every <c>finally</c> in this
    /// file exactly as it was, and the placement census stays green over it. That is not
    /// hypothetical — it is how the defect below shipped and how it survived a review round.
    /// </para>
    /// <para>
    /// <strong>What it pins, and why the order is load-bearing rather than tidy.</strong> Row 5's
    /// body look calls <see cref="ReclaimPidForTeardownAsync"/>, the LOUD reader, which lets
    /// <see cref="ReadPid"/>'s <see cref="UnauthorizedAccessException"/> escape. Assign the flag
    /// BEFORE that await and an ACL fault on the scratch file leaves the row's <c>finally</c>
    /// seeing <c>pid is null &amp;&amp; lateLookTaken</c>, so it skips its own quiet look
    /// entirely. Assigned after, the flag means what the <c>finally</c> reads it to mean: a body
    /// look that RAN TO COMPLETION.
    /// </para>
    /// <para>
    /// <strong>THE BENEFIT IS THE FLAG'S MEANING, NOT A CHILD SAVED, and the difference has to be
    /// stated because the obvious reading is the wrong one.</strong> On today's code the child is
    /// abandoned on that path either way: the quiet wrapper meets the same ACL fault on its own
    /// first <see cref="ReadPid"/> and answers <see langword="null"/>, so
    /// <see cref="KillTreeQuietly"/> is handed nothing whether the <c>finally</c> looks or not.
    /// What the ordering buys is that the <c>finally</c> takes its look ON ITS OWN ACCOUNT rather
    /// than the row resting on a coincidence — that the loud and the quiet reader happen to fail
    /// alike. They do today; nothing pins that they always will, and the version that suppressed
    /// the look would go on suppressing it if they ever diverged. It is also what keeps the flag
    /// honest for anyone reading the <c>finally</c>'s guard, which says "a look has already
    /// happened" and must not be true when none has. The cost of letting the look run is nil for
    /// the same reason it finds nothing — the second read answers at once — so "at most one
    /// <see cref="LateReadWindow"/> per row" survives the fix.
    /// </para>
    /// <para>
    /// <strong>Keyed on the ASSIGNMENT, because that is the thing that can move.</strong> The
    /// method name appears twice in this file as an invocation — row 5's body call, and the one
    /// inside <see cref="ReclaimPidForTeardownQuietlyAsync"/> — so a rule keyed on the await would
    /// have to say which. Keyed on the flag there is exactly one, and its block is the block the
    /// await has to be found in. The count is part of the rule for the reason the sibling census
    /// gives: a SECOND arming assignment added elsewhere would otherwise not be looked at.
    /// </para>
    /// <para>
    /// <strong>Same dominance machinery as the sibling, inverted.</strong> Both nodes are reduced
    /// to their block-level ancestor by <see cref="BlockLevelStatement"/>, and the ASSIGNMENT's
    /// index must be the higher one. Control entering a block runs its statements in order, so an
    /// assignment at index <em>j</em> is reached only after the statement at <em>i &lt; j</em> has
    /// executed — which for an <c>await</c> means it completed rather than threw.
    /// </para>
    /// <para>
    /// <strong>Its limit is the same one, and it is worth naming because this rule leans on it
    /// harder.</strong> Statement ORDER is not reachability: an assignment after an await that is
    /// itself inside <c>if (false)</c> would satisfy this, and an <c>await</c> whose exception is
    /// swallowed by a <c>try</c> between the two would defeat the property while satisfying the
    /// rule. Deciding either needs the control-flow analysis this census has no compilation for.
    /// What it catches is the edit that actually happened — the two statements swapped — and it
    /// catches that deterministically.
    /// </para>
    /// <para>
    /// A <c>true</c> literal is required on the right-hand side so a future <c>lateLookTaken =
    /// false</c> reset could not stand in for the arming assignment. The declaration
    /// (<c>var lateLookTaken = false;</c>) is a declarator rather than an assignment expression
    /// and is invisible here, which is what keeps the count at one.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheLateLookFlag_IsArmedOnlyAfterTheLookCompletes()
    {
        const string FlagName = "lateLookTaken";
        const string LoudReclaimMethod = "ReclaimPidForTeardownAsync";

        var root = SelfSource();

        var armings = root
            .DescendantNodes(descendIntoTrivia: false)
            .OfType<AssignmentExpressionSyntax>()
            .Where(assignment =>
                assignment.Left is IdentifierNameSyntax { Identifier.ValueText: FlagName }
                && assignment.Right.IsKind(SyntaxKind.TrueLiteralExpression))
            .ToList();

        Assert.True(
            armings.Count == 1,
            FormattableString.Invariant(
                $"This file holds {armings.Count} assignment(s) of `{FlagName} = true`, not the 1 this rule covers (row 5's body look). A second one would be a second place the `finally`'s look can be suppressed from, and this rule would not be looking at it; one that has gone means the flag no longer records anything. Re-aim this count rather than deleting it."));

        var arming = armings[0];
        var block = arming.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
        Assert.True(
            block is not null,
            FormattableString.Invariant(
                $"`{FlagName} = true` is not inside a block, so this rule has no statement list to order it against. Re-aim it rather than deleting it."));

        var armingAnchor = BlockLevelStatement(block!, arming);
        var lookAnchor = InvocationsNamed(block!, LoudReclaimMethod)
            .Select(look => BlockLevelStatement(block!, look))
            .FirstOrDefault(anchor => anchor is not null);

        Assert.True(
            armingAnchor is not null && lookAnchor is not null,
            FormattableString.Invariant(
                $"`{FlagName} = true` and the `{LoudReclaimMethod}` call it records are not statements of one block, so this rule cannot order them. The body look moved — re-aim this rule rather than deleting it."));

        Assert.True(
            block!.Statements.IndexOf(armingAnchor!) > block.Statements.IndexOf(lookAnchor!),
            FormattableString.Invariant(
                $"`{FlagName} = true` at {Describe(arming)} does not sit after the `{LoudReclaimMethod}` call it is supposed to record. That call is the LOUD reader: an ACL fault on the pid file escapes it, and a flag armed beforehand then tells row 5's `finally` that a look has already happened when none has — so teardown skips its own look on the one path where nothing has looked at the child at all, and the row's teardown rests on the loud and quiet readers happening to fail alike rather than on a look of its own (#539, Copilot round 2). Move the assignment below the await."));
    }

    /// <summary>
    /// This file's own syntax tree, read off disk.
    /// </summary>
    /// <remarks>
    /// Shared by the two census rows above so the path and its diagnostic live in one place. The
    /// repository-relative tail is what the failure names, never the resolved path: the account
    /// name above the checkout belongs in nobody's public job log, and the tail is the whole of
    /// what a reader re-aims.
    /// </remarks>
    private static SyntaxNode SelfSource()
    {
        var path = Path.Combine(
            RepositoryRoot(), "tests", "Vouchfx.Cli.Tests", "SystemProcessRunnerTests.cs");

        Assert.True(
            File.Exists(path),
            "this census reads its own source at "
            + "'tests/Vouchfx.Cli.Tests/SystemProcessRunnerTests.cs' under the repository root, "
            + "which is not there. The file was renamed or moved — re-aim this row rather than "
            + "deleting it.");

        return CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
    }

    /// <summary>
    /// Whether a guarded reclaim is a block-level statement of <paramref name="clause"/> ahead of
    /// <paramref name="kill"/>'s own.
    /// </summary>
    private static bool GuardedReclaimPrecedes(
        FinallyClauseSyntax clause, InvocationExpressionSyntax kill, string reclaimMethod)
    {
        var block = clause.Block;
        var killAnchor = BlockLevelStatement(block, kill);
        if (killAnchor is null)
        {
            return false;
        }

        return InvocationsNamed(clause, reclaimMethod).Any(reclaim =>
        {
            var anchor = BlockLevelStatement(block, reclaim);
            return anchor is not null
                && block.Statements.IndexOf(anchor) < block.Statements.IndexOf(killAnchor)
                && GuardedOnANullPid(reclaim);
        });
    }

    /// <summary>
    /// The statement of <paramref name="block"/> that <paramref name="node"/> sits in — the node's
    /// own statement when it is a direct child, otherwise the enclosing <c>if</c>/loop/try that
    /// is. <see langword="null"/> when the node is not inside this block at all.
    /// </summary>
    private static StatementSyntax? BlockLevelStatement(BlockSyntax block, SyntaxNode node) =>
        node.AncestorsAndSelf()
            .OfType<StatementSyntax>()
            .FirstOrDefault(statement => ReferenceEquals(statement.Parent, block));

    /// <summary>
    /// Whether an enclosing <c>if</c> inside the same <c>finally</c> tests <c>pid is null</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The condition need only CONTAIN the pattern, so row 5's
    /// <c>pid is null &amp;&amp; !lateLookTaken</c> satisfies it: a narrower guard is still a
    /// guard.
    /// </para>
    /// <para>
    /// <strong>What it refuses is a look not guarded BY THAT SPELLING, which is narrower than "an
    /// unguarded look" and the difference is worth stating.</strong> This matches an
    /// <see cref="IsPatternExpressionSyntax"/>, so <c>pid == null</c>, <c>!pid.HasValue</c> and a
    /// <c>pid ??= …</c> with no <c>if</c> around it are all refused even though each guards the
    /// look perfectly well. The census reddens on them, loudly and at the line, and the fix is to
    /// spell the pattern; pinning one spelling is what keeps this decidable without a symbol
    /// table, and the call sites it governs are five lines in one file.
    /// </para>
    /// </remarks>
    private static bool GuardedOnANullPid(SyntaxNode node) =>
        node.Ancestors()
            .TakeWhile(ancestor => ancestor is not FinallyClauseSyntax)
            .OfType<IfStatementSyntax>()
            .Any(statement => statement.Condition
                .DescendantNodesAndSelf()
                .OfType<IsPatternExpressionSyntax>()
                .Any(pattern =>
                    pattern.Expression is IdentifierNameSyntax { Identifier.ValueText: "pid" }
                    && pattern.Pattern is ConstantPatternSyntax constant
                    && constant.Expression.IsKind(SyntaxKind.NullLiteralExpression)));

    /// <summary>Every invocation of a method with this bare name, at any depth.</summary>
    private static IEnumerable<InvocationExpressionSyntax> InvocationsNamed(
        SyntaxNode node, string methodName) =>
        node.DescendantNodes(descendIntoTrivia: false)
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Expression switch
            {
                MemberAccessExpressionSyntax access =>
                    access.Name.Identifier.ValueText == methodName,
                IdentifierNameSyntax name => name.Identifier.ValueText == methodName,
                _ => false,
            });

    /// <summary>
    /// A census offender as its failure message names it: line, then first line of text.
    /// </summary>
    private static string Describe(SyntaxNode node)
    {
        var line = node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
        return FormattableString.Invariant($"line {line}: ")
            + node.ToString().Split('\n')[0].Trim();
    }

    /// <summary>
    /// The repository root, found by walking up to the directory holding the solution.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "vouchfx.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    // ── budget machinery (#524) ──────────────────────────────────────────────────────────────

    /// <summary>
    /// How long <c>Run</c> is given to return once it has been handed <paramref name="budget"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The budget PLUS a flat unwind slack, not a multiple of the budget.</strong> A row
    /// that injects a budget is waiting for two different things end to end: the call sitting out
    /// the budget, which is proportional to it by definition, and then the tree-kill, the two
    /// stream disposals and the throw, which are the same handful of operations whatever the budget
    /// was. Multiplying would grant the second part a window that grows with the first for no
    /// reason, and the escalation in <see cref="WithEscalatingBudget"/> makes that compounding
    /// rather than merely untidy — at the largest budget a factor of three would be waiting over a
    /// minute to notice a hang.
    /// </para>
    /// <para>
    /// A genuine hang is unbounded, so it still fails this however the window is composed; what the
    /// composition decides is only how long a re-broken runner takes to say so.
    /// </para>
    /// </remarks>
    private static TimeSpan GraceFor(TimeSpan budget) => budget + RunUnwindSlack;

    /// <summary>
    /// Runs <paramref name="attempt"/> against a doubling budget until it establishes its premise.
    /// </summary>
    /// <param name="premise">
    /// What could not be established, phrased to complete a sentence about the exhausted attempts.
    /// </param>
    /// <param name="attempt">
    /// One whole attempt at one budget. <see langword="true"/> means it established its premise and
    /// its assertions have already run; <see langword="false"/> means the attempt judged nothing,
    /// because no pid arrived inside the windows its premise depends on. A pid that
    /// <see cref="ReclaimPidForTeardownQuietlyAsync"/> recovered in the attempt's <c>finally</c>
    /// does not change that answer — it was used to kill the child, not to establish anything.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>THIS IS NOT A RETRY OF A FAILED ASSERTION, AND THE DISTINCTION IS THE WHOLE
    /// LICENCE FOR IT.</strong> An attempt that returns <see langword="false"/> made no judgement
    /// about <see cref="SystemProcessRunner"/> at all: no pid ever arrived, so there was never a
    /// number to arm the death assertion with. WHY it did not arrive is a separate question that
    /// this fact does not settle — the usual answer is that the runner's tree-kill reached the child
    /// before the operating system scheduled its first write, but "usual" is not "always", and the
    /// case where the kill did NOT reach it is handled in the attempt's <c>finally</c>, by
    /// <see cref="ReclaimPidForTeardownQuietlyAsync"/>, rather than assumed away. An attempt that
    /// DID get a pid runs its assertions to completion, and a failure among them throws straight
    /// out through this method — assertions are never re-run, and there is no path here that sees
    /// one fail and tries again.
    /// </para>
    /// <para>
    /// <strong>Why doubling cannot buy a PASS.</strong> The worry with any retry near a leak test is
    /// that repetition buys one. It cannot here, because the escalation moves the row in the
    /// opposite direction: a larger budget gives the child longer to publish its pid, the pid is
    /// what arms the assertion that the child is dead, so every doubling makes the row MORE likely
    /// to judge the runner and never less. The only transition available is "could not establish a
    /// premise" to "established it and judged" — and if the premise is never established the row
    /// FAILS below rather than passing quietly.
    /// </para>
    /// <para>
    /// <strong>That is a narrower claim than the one this paragraph used to make, and the
    /// difference is the whole of PR #532's review.</strong> It said "cannot hide a leak", which is
    /// a statement about PROCESSES, and argued a statement about ASSERTIONS. Repetition genuinely
    /// can leave a live process behind — the attempt that is discarded had a child, and if the
    /// runner's kill failed while that child's first write was still pending, the attempt returned
    /// here holding no pid to kill it with. The discarded attempt closes that itself, in its
    /// <c>finally</c> at <see cref="ReclaimPidForTeardownQuietlyAsync"/>, before it returns;
    /// nothing in this method does, and this paragraph now claims only what it can carry.
    /// </para>
    /// <para>
    /// <strong>WHAT THE ESCALATION CANNOT DO EITHER WAY IS SAMPLE EVENLY, and this is the one cost
    /// of the fix above rather than another of its benefits.</strong> Rows 1 and 2 judge only
    /// attempts whose child announced itself inside the original window — which are exactly the
    /// attempts where the runner's tree-kill had an already-published, fully-formed target. A kill
    /// that fails SPECIFICALLY in the window before the child announces itself produces no pid on
    /// every attempt, so it is discarded unjudged every time; no budget is large enough to sample
    /// it, because enlarging the budget is what moves the announcement EARLIER and out of the shape.
    /// That bias predates PR #532.
    /// </para>
    /// <para>
    /// What PR #532 changed is the residue. Before it, such an attempt left a live orphan, which
    /// <see cref="ChildLifetime"/> expiry or a host sweep could eventually surface; after it, the
    /// late look finds that orphan and teardown kills it, so the shape leaves nothing behind at all.
    /// No verdict moves in either direction — but a fix whose purpose was to stop a leak hiding has,
    /// on this one shape, reduced its DETECTABILITY, and <see cref="ChildLifetime"/> no longer
    /// bounds a symptom here because there is no symptom left to bound. Written down because it is
    /// invisible from every other paragraph in this file: the ones above are about assertions, and
    /// <see cref="ReclaimPidForTeardownAsync"/>'s residuals are about processes this file cannot
    /// name. Neither describes a shape that is never sampled.
    /// </para>
    /// <para>
    /// Not a hypothetical mechanism. <strong>#501</strong> records that
    /// <c>Process.Kill(entireProcessTree: true)</c> is handle-pinned on Windows but on Linux — the
    /// lane that gates merges — enumerates and recurses into descendants by BARE PID, unpinned, at
    /// whatever moment the walk runs. Read that as establishing the mechanism only: the consequence
    /// #501 itself documents is a wrong kill (a recycled pid being signalled), NOT a missed
    /// descendant, and it proposes no action. What carries here is the timing property — a walk over
    /// descendants that exist WHEN IT RUNS is exactly the kind of kill whose success can depend on
    /// how early the child got going, which is the axis this row cannot sample along.
    /// </para>
    /// <para>
    /// <strong>Why the budget may be enlarged at all.</strong> Because the property under test does
    /// not name it: "the budget is enforced and the tree is killed" is the same statement at three
    /// seconds and at twenty-four, and each row asserts the timeout against the budget IT was given
    /// rather than against a constant. What the number has to do is outlast the child's start-up on
    /// the host of the day, and only the host of the day knows that figure — see
    /// <see cref="StartingBudget"/> for the spread: 154ms for the fastest idle sample against 13.6s
    /// for the slowest 128-burner one, a factor near ninety on one machine.
    /// </para>
    /// </remarks>
    private static async Task WithEscalatingBudget(string premise, Func<TimeSpan, Task<bool>> attempt)
    {
        // The relationship the whole escalation rests on, asserted rather than trusted to whoever
        // next edits any of these numbers. Every budget here ends in the runner killing a child
        // that is still running; a budget that outlived ChildLifetime would instead be answered by
        // the child EXITING, the pipes closing and Run SUCCEEDING, and the row would then fail
        // demanding a timeout that had become impossible — a confusing red for a healthy runner.
        //
        // THE WHOLE ATTEMPT IS WEIGHED, NOT JUST THE BUDGET, and the difference is not cosmetic:
        // after the budget expires an attempt still waits out the unwind slack and then polls for
        // the child's death, so it is the SUM that has to fit inside the child's lifetime. Checking
        // the budget alone would pin 24s of a 36s requirement and silently leave the remaining 12s
        // free to drift — which is exactly the shape of gap an assertion like this exists to close.
        //
        // "REACHES A VERDICT" IS A DELIBERATE NARROWING, AND ONE PATH IS EXCLUDED BY IT. If Run
        // does not settle at all, WaitForPid first spends its own ceiling (GraceFor(budget) +
        // PidSettleWindow = budget + 13s) and the race after it adds up to another GraceFor(budget)
        // (budget + 10s), so `Assert.True(finished, …)` is reached at two budgets plus twenty-three
        // seconds — 71s at the largest budget, already past ChildLifetime. Since #539 the `finally`
        // after it adds LateReadWindow (5s) for the reclaim and then DrainWindow (10s) for the
        // drain, so the WHOLE attempt runs to 2*budget + 38s, or 86s at the largest budget. The
        // drain was always there and was simply not counted in this paragraph before, so the
        // like-for-like figure #539 moved is 81s to 86s. Either way the sum sits where this
        // paragraph has always said it sits — outside the 60s lifetime — so the fix widened an
        // interval that was already outside rather than pushing one across.
        //
        // That is not a hole in this guard: it is a path that never reaches the death poll the
        // guard protects, because it fails first at `Assert.True(finished, …)`, which IS the row
        // correctly reporting a wedged runner. Nothing downstream of that assertion runs, so what
        // the child does at 60s cannot change the verdict — and the reclaim #539 put in the
        // `finally` runs strictly AFTER that assertion has thrown, so it cannot change one either.
        // It is there to give that red row's teardown something to kill, which on exactly this
        // path it previously had not.
        //
        // THE NO-PID PATH IS EXCLUDED BY THE SAME WORD, and since PR #532 it costs LateReadWindow
        // more than it did. It reaches no death poll either — it returns `false` and the loop below
        // discards it — so ChildLifetime is not the thing bounding it; BudgetAttempts is. What it
        // does cost is written down at LateReadWindow rather than folded in here, because it is a
        // wall-clock figure and not a correctness relationship. #539 changed WHERE that cost is
        // paid — the `finally` rather than the return — and therefore WHICH paths pay it: both of
        // the two above now do, and neither of them reaches the death poll.
        //
        // AND THE TWO CONDITIONS DO NOT MEET ANYWAY, which is a stronger statement than "fails
        // first" and is the one that actually closes the question. For ChildLifetime to matter at
        // all, the child must be ALIVE for the whole minute so that its own exit is what closes the
        // pipes — and a live child publishes its pid as its FIRST action, so WaitForPid returned
        // then and the race that follows expires at most grace later — and with a pid in hand the
        // `finally`'s reclaim is guarded out, so #539 adds nothing to this sum. Taking the slowest
        // start-up this file has ever measured (13.6s, at 128 burners) and the largest budget:
        // 13.6+34 = 47.6s, still short of 60. So whenever a child is alive to meet this ceiling,
        // the clocks that could reach it are short. There is no shape in which this ceiling decides
        // that path, and no need to re-derive that next time.
        //
        // THE CONVERSE USED TO BE ASSERTED HERE TOO — that WaitForPid running to its full ceiling
        // MEANT no child was going to write, "because it is dead or was never started". PR #532
        // deleted that inference rather than reworded it: it is the same assumption the header's
        // ×BudgetAttempts paragraph was corrected for, and drill 2 refuted it directly — the wait
        // gave up at ~6s with an empty directory and a genuinely live child wrote its pid at 8.31s.
        // The conclusion above never needed that leg and does not miss it.
        //
        // Folding that path in would not be free, which is why it is excluded rather than covered:
        // 2*budget+23 < 60 forces BudgetAttempts down to 3 and caps the escalation at a 12s budget
        // — below the worst start-up this file has measured (13.6s). The guard would then be
        // protecting an assertion nobody reaches at the cost of the escalation every loaded host
        // needs.
        var longestAttemptToAVerdict = GraceFor(LargestBudget) + DeathWindow;
        Assert.True(
            longestAttemptToAVerdict < ChildLifetime,
            FormattableString.Invariant(
                $"The longest attempt that reaches a verdict ({longestAttemptToAVerdict.TotalSeconds:F0}s = a {LargestBudget.TotalSeconds:F0}s largest budget, plus {RunUnwindSlack.TotalSeconds:F0}s of unwind slack, plus a {DeathWindow.TotalSeconds:F0}s death poll) is not below the child's own lifetime ({ChildLifetime.TotalSeconds:F0}s). The child would exit on its own inside that window, so rows 1 and 2 would be asserting a timeout that cannot occur and a death that proves nothing. Lower BudgetAttempts, or raise ChildLifetime."));

        var attempted = new List<string>(BudgetAttempts);
        var budget = StartingBudget;
        for (var i = 0; i < BudgetAttempts; i++)
        {
            if (await attempt(budget))
            {
                return;
            }

            attempted.Add(FormattableString.Invariant($"{budget.TotalSeconds:F0}s"));
            budget += budget;
        }

        Assert.Fail(
            FormattableString.Invariant(
                $"Across {BudgetAttempts} attempts at budgets of {string.Join(", ", attempted)}, {premise}. The child is started by SystemProcessRunner and tree-killed by it the moment the budget expires, so a child that publishes nothing is most likely one the kill reached before the operating system scheduled its first write — and each attempt also re-polled the pid file for a further {LateReadWindow.TotalSeconds:F0}s afterwards, so a survivor whose write landed inside THAT window was found and handed to teardown rather than left unreferenced. Doubling the budget is how a short budget is told apart from a defect in the runner, and a host that still cannot start a shell inside the largest budget is reporting a problem of its own. Nothing about SystemProcessRunner has been established either way by this failure."));
    }

    // ── child shapes ─────────────────────────────────────────────────────────────────────────

    /// <summary>An executable plus its argument vector.</summary>
    private sealed record ChildShape(string FileName, IReadOnlyList<string> Arguments);

    /// <summary>
    /// The file name, relative to the working directory, that every child announces itself into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>RELATIVE, AND THE ABSOLUTE PATH IS DELIBERATELY KEPT OFF THE COMMAND LINE.</strong>
    /// Both shapes below splice this into a shell command inside single quotes, and in BOTH shells a
    /// single quote is the one character that terminates such a literal — neither splice escapes it,
    /// and there is no portable escape that would serve both. The scratch directory sits under
    /// <see cref="Path.GetTempPath"/>, which on Windows contains the account name, and Windows
    /// permits an apostrophe in one: <c>C:\Users\O'Brien\…</c> is an ordinary developer host.
    /// </para>
    /// <para>
    /// MEASURED on such a path, with the absolute form spliced in as this file used to do: the
    /// PowerShell child exits 1 with "The string is missing the terminator: '." and writes no pid,
    /// and <c>/bin/sh</c> fails the same way with "unexpected EOF while looking for matching `''".
    /// That is not merely a broken row — it is a broken row wearing the wrong label, because a child
    /// that dies instantly looks exactly like a child that was never scheduled, so
    /// <see cref="WithEscalatingBudget"/> would spend all four attempts on it and then report a
    /// host that cannot start a shell. Confidently blaming the host for a defect is the failure
    /// mode #512 is about, and #524 must not reintroduce it one file over.
    /// </para>
    /// <para>
    /// <strong>THE OTHER HALF OF THAT MISLABEL — a writer whose LINE, rather than whose path,
    /// stops being what the reader accepts — has a pin of its own since #541.</strong>
    /// <see cref="PidFileWriters_PublishAPidTheReaderAccepts"/> launches both shapes directly,
    /// with no runner and nothing racing to kill them, so it can say what the escalating rows
    /// cannot: a process started, and what did not arrive is a terminated digit line. A breach of
    /// the writer contract reddens there naming the contract, and only incidentally reddens rows
    /// 1, 2 and 5 blaming the host.
    /// </para>
    /// <para>
    /// Passing a bare name removes the character class rather than escaping it: the runner already
    /// hands the child <c>directory</c> as its working directory, and a relative path resolves
    /// against it. Verified rather than assumed, precisely because PowerShell's current LOCATION is
    /// not always its process working directory: a child launched with
    /// <c>ProcessStartInfo.WorkingDirectory</c> set to a path containing an apostrophe wrote its pid
    /// to that directory, and the file contents matched the child's real pid.
    /// </para>
    /// </remarks>
    private const string PidFileName = "child.pid";

    /// <summary>
    /// The grandchild's announcement file, relative to the working directory.
    /// </summary>
    /// <remarks>Named apart from <see cref="PidFileName"/> only so a stray file is attributable.</remarks>
    private const string GrandchildPidFileName = "grandchild.pid";

    /// <summary>
    /// A child that writes its own pid and then holds the pipes open for its whole lifetime.
    /// </summary>
    private static ChildShape NeverExitingChild()
    {
        if (OperatingSystem.IsWindows())
        {
            // Single quotes throughout: the argument reaches CreateProcess quoted by the runtime,
            // and an embedded double quote would have to survive both that escaping and
            // powershell.exe's own command-line parsing. Nothing here needs one — and nothing
            // spliced in can contain a single quote either, which is why PidFileName is a constant
            // rather than a path.
            return new ChildShape(
                WindowsPowerShell,
                new[]
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    $"$PID | Set-Content -LiteralPath '{PidFileName}'; Start-Sleep -Seconds {ChildLifetimeSeconds}",
                });
        }

        return new ChildShape(
            "/bin/sh",
            new[] { "-c", $"echo $$ > '{PidFileName}'; sleep {ChildLifetimeSeconds}" });
    }

    /// <summary>
    /// A child that starts a grandchild, records the GRANDCHILD's pid, and exits — leaving the
    /// grandchild holding an inherited copy of the runner's stdout/stderr pipes (#392).
    /// </summary>
    /// <remarks>
    /// On Windows, <c>Start-Process -NoNewWindow</c> means UseShellExecute=false with no
    /// redirection, which hands the child the parent's std handles — here, the runner's pipes.
    /// On POSIX the background job inherits them through fork/exec, and <c>$!</c> is its pid.
    /// </remarks>
    private static ChildShape GrandchildHoldingPipesChild()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ChildShape(
                WindowsPowerShell,
                new[]
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    $"(Start-Process -FilePath 'ping.exe' -ArgumentList '-n','{ChildLifetimeSeconds}','127.0.0.1' -NoNewWindow -PassThru).Id | Set-Content -LiteralPath '{GrandchildPidFileName}'",
                });
        }

        return new ChildShape(
            "/bin/sh",
            new[] { "-c", $"sleep {ChildLifetimeSeconds} & echo $! > '{GrandchildPidFileName}'" });
    }

    /// <summary>A child that prints to both streams and exits 7.</summary>
    private static ChildShape PrintsAndExitsChild()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ChildShape(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                new[] { "/c", "echo OUT& echo ERR>&2& exit 7" });
        }

        return new ChildShape("/bin/sh", new[] { "-c", "echo OUT; echo ERR 1>&2; exit 7" });
    }

    /// <summary>A child that prints its own environment block and exits 0 (#500).</summary>
    /// <remarks>
    /// Both spellings are SHELL BUILTINS (<c>set</c> under cmd, <c>export -p</c> under POSIX sh)
    /// and neither is resolved through <c>PATH</c>. That matters because the row hands the child a
    /// confined environment: a shape that reached for <c>/usr/bin/env</c> through a <c>PATH</c>
    /// lookup would be testing the allow-list with the allow-list.
    /// </remarks>
    private static ChildShape PrintsItsEnvironmentChild()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ChildShape(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                new[] { "/c", "set" });
        }

        return new ChildShape("/bin/sh", new[] { "-c", "export -p" });
    }

    /// <summary>
    /// The variable NAMES in a <see cref="PrintsItsEnvironmentChild"/> capture.
    /// </summary>
    /// <remarks>
    /// Parsing to names rather than matching substrings is not tidiness: a drill deleting
    /// <c>PATH</c> from the allow-list left the row green, because the allow-listed
    /// <c>HOMEPATH=...</c> contains the substring <c>PATH=</c>. A name set cannot be satisfied by a
    /// suffix.
    /// </remarks>
    private static HashSet<string> EnvironmentNamesIn(string capture)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in capture.Split('\n'))
        {
            // POSIX `export -p` prefixes each line; cmd's `set` does not.
            var line = rawLine.Trim();
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..];
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                names.Add(line[..separator]);
            }
        }

        return names;
    }

    /// <summary>
    /// Windows PowerShell's fully qualified path, because the runner refuses an unqualified one.
    /// </summary>
    /// <remarks>
    /// These shapes named <c>powershell.exe</c> and <c>cmd.exe</c> bare until
    /// <see cref="SystemProcessRunner.Run"/> began enforcing the fully-qualified contract
    /// <see cref="IProcessRunner"/> documents. Rooting them is not test bookkeeping: a bare name
    /// here would have been resolved by the very operating-system search #499 exists to remove, so
    /// these rows were measuring the runner through the mechanism the change forbids. The POSIX
    /// branches already named <c>/bin/sh</c>, which is fully qualified, and are unchanged.
    /// </remarks>
    private static string WindowsPowerShell =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    // ── teardown machinery ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Polls <paramref name="pidFile"/> until it holds a pid, until <paramref name="work"/> has
    /// been settled for <paramref name="settle"/>, or until <paramref name="ceiling"/> expires —
    /// whichever comes first.
    /// </summary>
    /// <param name="pidFile">Where the child announces itself.</param>
    /// <param name="work">
    /// The <c>Run</c> whose child is being waited for, or
    /// <see cref="Task.CompletedTask"/> where there is no <c>Run</c> at all — the case
    /// <see cref="PidFileWriters_PublishAPidTheReaderAccepts"/> introduced by launching its shape
    /// itself, and <see cref="TheGuardsAnchors_StayInTheOrderARealChildProduces"/> repeats. A
    /// completed task arms the settle clock on the first pass, so those callers pass equal
    /// windows and let the ceiling decide, by the derivation at
    /// <see cref="ReclaimPidForTeardownAsync"/>.
    /// </param>
    /// <param name="settle">How long to keep looking after <paramref name="work"/> has settled.</param>
    /// <param name="ceiling">The backstop for a <paramref name="work"/> that will not settle.</param>
    /// <remarks>
    /// <para>
    /// <strong>THE RUN'S OWN COMPLETION IS THE REAL BOUND; <paramref name="ceiling"/> is the
    /// backstop (#524).</strong> A fixed wall-clock wait was wrong in both directions. Too short
    /// and a loaded host fails a row about SystemProcessRunner for a reason that is not about
    /// SystemProcessRunner; too long and the wait spends the difference polling a file nothing is
    /// alive to write — seventeen of twenty seconds at the production budget, which is a
    /// SUBTRACTION rather than a reading (the drill that measured the dead time directly used a
    /// 50ms budget and saw 20.02s of it) — because the runner tree-kills its
    /// child when the budget expires and a child killed before its first write never makes one.
    /// Ending the wait when the RUN has ended keys it to the only event that can still change the
    /// answer, so a failed premise is reported in about a budget instead of in a constant, which is
    /// what makes <see cref="WithEscalatingBudget"/> affordable.
    /// </para>
    /// <para>
    /// <paramref name="settle"/> rather than stopping the instant <paramref name="work"/>
    /// completes, because <c>Run</c> returns having only ISSUED the tree-kill: the child may still
    /// be executing, and a pid discarded here is a pid teardown cannot kill.
    /// </para>
    /// <para>
    /// Synchronous on purpose — callers wrap it in <c>Task.Run</c> so no blocking file read sits
    /// directly inside an async method. Reading <see cref="Task.IsCompleted"/> from that thread is
    /// safe and is the whole of the coupling: nothing here awaits, continues or faults
    /// <paramref name="work"/>, which stays the caller's to observe.
    /// </para>
    /// </remarks>
    private static int? WaitForPid(string pidFile, Task work, TimeSpan settle, TimeSpan ceiling)
    {
        var ceilingAt = DateTime.UtcNow + ceiling;
        DateTime? settledAt = null;

        while (true)
        {
            if (ReadPid(pidFile) is int pid)
            {
                return pid;
            }

            var now = DateTime.UtcNow;

            // Sampled AFTER the read above, never before it: the opposite order can see the run
            // complete, start the settle clock, and only then look at a file the child wrote in
            // between — which is the same answer, one poll later, but it makes the settle window
            // one poll shorter than it says it is.
            if (settledAt is null && work.IsCompleted)
            {
                settledAt = now;
            }

            if (now >= ceilingAt || (settledAt is { } observed && now - observed >= settle))
            {
                return null;
            }

            Thread.Sleep(PollIntervalMs);
        }
    }

    /// <summary>
    /// One last bounded look for a pid, so a child the caller never saw is still killable.
    /// </summary>
    /// <param name="pidFile">Where the child announces itself.</param>
    /// <param name="work">
    /// The <c>Run</c> whose child is being reclaimed. Never settled on row 5, the one caller that
    /// reaches this method directly. Rows 1 and 2 arrive through
    /// <see cref="ReclaimPidForTeardownQuietlyAsync"/> and may be either: settled when the attempt
    /// gave its premise up or when an assertion after the grace check threw, NOT settled when a
    /// wedged runner ended it. Two further indirect callers arrive the same way and are ALWAYS
    /// settled — the directly-launching rows #541 and #529 have no <c>Run</c> to name and pass
    /// <see cref="Task.CompletedTask"/>; the windows are equal on every caller here, so that
    /// changes which arm is armed and not which one ends the look.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>WHY THIS CLOSES THE MECHANISM IT IS FOR.</strong> A caller that ends without a pid
    /// has established at most that <c>Run</c> refused correctly — and on rows 1 and 2's wedged
    /// path, where <c>Assert.True(finished, …)</c> is what ended the attempt, not even that. What
    /// no such caller has established is that the child is DEAD: <c>Run</c> only ever ISSUES the
    /// tree-kill, and a caller that never saw a pid never looked at the child. If that kill was
    /// broken AND the child's first write was merely delayed past whatever window did the looking —
    /// <see cref="PidSettleWindow"/> after the run settles on rows 1 and 2,
    /// <see cref="UnracedPidCeiling"/> on row 5 — then the pid file is empty at the moment the
    /// caller looks and non-empty shortly afterwards. Looking a second time, later, is precisely
    /// what turns that child from unnameable into killable: the pid it finds is assigned to the
    /// caller's own <c>pid</c>, and the <c>finally</c> kills it through the ordinary
    /// <see cref="TryOpen"/> guard. There is no second kill site; this method only widens the
    /// window in which the existing one has something to aim at. Since #539 rows 1 and 2 do that
    /// widening from inside that same <c>finally</c>, which is what makes it unconditional rather
    /// than contingent on which of the attempt's endings occurred.
    /// </para>
    /// <para>
    /// <strong>WHAT IT DOES NOT CLOSE, STATED RATHER THAN IMPLIED.</strong> A child that publishes
    /// no pid AT ALL inside the widened window — because it died before the write for some reason
    /// other than the kill, because the write itself failed, or because it is slower still than
    /// <see cref="LateReadWindow"/> allows for — is exactly as unreachable as it was before, and so
    /// is row 2's grandchild when the kill left it behind before its pid was recorded. Nothing in
    /// this file can name a process that never named itself, and no length of wait changes that.
    /// That residual is bounded by <see cref="ChildLifetime"/> and by nothing else, which is why
    /// that lifetime is a minute rather than infinite.
    /// </para>
    /// <para>
    /// <strong>A SECOND RESIDUAL, OF THE OPPOSITE CLASS, AND THIS PATH WIDENS IT.</strong> A pid
    /// recovered here is handed to the <c>finally</c>'s tree-kill under <see cref="TryOpen"/>'s
    /// start-time bounds, which are not an identity check: a pid recycled onto a process that
    /// started inside the window they accept passes unchanged and that process is killed. #529
    /// narrowed that window from the whole attempt to
    /// <see cref="PidStartTimeTolerance"/> after the pid file was written; what is left of it is
    /// still open, and closing it needs a handle rather than a timestamp. It belongs here in ONE
    /// sub-case only, and the first
    /// paragraph above names the shape this method actually exists for. A SURVIVOR's pid is not
    /// recycled while it is alive: <see cref="TryOpen"/> runs in the same <c>finally</c> a beat
    /// later and opens the real child, so for a survivor the identity question is confined to
    /// that beat — one of #529's exposures, not an exception to it. The recycle needs the
    /// other shape — a child that DID write, WAS genuinely killed, and whose write the original
    /// wait missed, landing any time before this late look gives up — so that what lands here
    /// names a process which has since died and whose pid the operating system is free to
    /// reissue. Narrow, and still not closed; a paragraph promising to state what it does not
    /// close has to include the case it widens.
    /// </para>
    /// <para>
    /// <strong>A THIRD, RECORDED WHERE IT LIVES RATHER THAN RESTATED HERE.</strong>
    /// <see cref="ReadPid"/> lets an <see cref="UnauthorizedAccessException"/> escape; that remark
    /// sets out why it is left loud. It reaches the ONE call site that still enters this method
    /// directly — row 5's body look — and fails that row. The child is abandoned either way on
    /// that path — the <c>finally</c>'s quiet look meets the same ACL fault and answers
    /// <see langword="null"/> — but the throw now leaves <c>lateLookTaken</c> unset, so the row
    /// takes that look on its own account rather than resting on the two readers failing alike.
    /// Rows 1 and 2 go through
    /// <see cref="ReclaimPidForTeardownQuietlyAsync"/> since #539 and never see it, which is not a
    /// softening of that choice but a consequence of where they now call from: a <c>finally</c>
    /// that throws discards the row's own finding. Named here rather than written out, because this
    /// section promises completeness and a third residual left off the list would break it, while
    /// a second copy of the reasoning would be one more thing to keep in step.
    /// </para>
    /// <para>
    /// <strong>It never changes a verdict, and since #539 the two kinds of caller establish that
    /// differently.</strong> Rows 1 and 2 no longer rest on an arrangement at all: they call from a
    /// <c>finally</c>, which runs only once its attempt has already returned or thrown. The premise
    /// was decided on the ORIGINAL wait, and every assertion has either run or been pre-empted by
    /// an earlier throw — including the hoisted
    /// <c>Assert.ThrowsAsync&lt;ProcessTimeoutException&gt;</c> which keeps "the budget was too
    /// short" apart from a runner fault, which on the discarded path ran and on the wedged path was
    /// pre-empted by <c>Assert.True(finished, …)</c>, and in both cases before this. Row 5's BODY
    /// call is the one place an arrangement is still what carries it: that row captures
    /// <c>childWasObserved</c> before calling this and asserts on that. Its second, guarded call
    /// from its own <c>finally</c> (#539) rests on the structure like the others. Either way a pid
    /// recovered here arms teardown and nothing else — it cannot establish a premise the wait
    /// failed to establish, and <see cref="WithEscalatingBudget"/> still discards the attempt.
    /// </para>
    /// <para>
    /// <see cref="WaitForPid"/> with both its windows set to <see cref="LateReadWindow"/> rather
    /// than a second polling loop. <strong>The CEILING is what ends this wait, on every caller, and
    /// the settle arm is inert here by construction</strong> — worth deriving once rather than
    /// re-deriving later. <see cref="WaitForPid"/> computes <c>ceilingAt</c> from the clock on
    /// entry, but first samples <c>settledAt</c> only AFTER a <see cref="ReadPid"/>, so
    /// <c>settledAt</c> is never earlier than entry and <c>settledAt + settle</c> is therefore never
    /// earlier than <c>ceilingAt</c> whenever the two windows are equal. The ceiling wins outright
    /// when the clock has advanced at all, and on the tie it is still the ceiling that decides —
    /// it is the left operand of the <c>||</c>. An earlier draft of this paragraph claimed the
    /// settle arm ended the wait on rows
    /// 1 and 2; that was false, and the conclusion it was offered in support of — the bound is
    /// <see cref="LateReadWindow"/> for every caller — is true for this simpler reason.
    /// </para>
    /// <para>
    /// <strong>Equal rather than a smaller <c>settle</c>, deliberately.</strong> A smaller one would
    /// become live on rows 1 and 2 whenever <paramref name="work"/> HAS settled — which is their
    /// discarded path, the common one — and would end the look early on exactly the callers with the
    /// most to find: their survivor is alive because a kill failed, and the whole point is to give
    /// its pending first write the full window. Row 5 could not use a settle arm at all — its
    /// <c>Run</c> has not settled and the row has not cancelled yet — so a shorter figure would buy
    /// a divergence between callers and nothing else.
    /// </para>
    /// </remarks>
    private static Task<int?> ReclaimPidForTeardownAsync(string pidFile, Task work) =>
        Task.Run(() => WaitForPid(pidFile, work, LateReadWindow, LateReadWindow));

    /// <summary>
    /// <see cref="ReclaimPidForTeardownAsync"/> for a caller that must not throw the one ACL fault
    /// <see cref="ReadPid"/> lets escape — a <c>finally</c> (#539).
    /// </summary>
    /// <param name="pidFile">Where the child announces itself.</param>
    /// <param name="work">
    /// The <c>Run</c> whose child is being reclaimed. Either state, and which one says how the row
    /// ended: settled when an escalating attempt gave its premise up, when an assertion after its
    /// grace check threw, or when row 5's <c>work.IsCompleted</c> block fired; NOT settled when a
    /// wedged runner or a throwing <see cref="WaitForPid"/> is what ended it. Since #541 there is
    /// also a caller with no <c>Run</c> to name —
    /// <see cref="PidFileWriters_PublishAPidTheReaderAccepts"/> launches its shape itself and
    /// passes <see cref="Task.CompletedTask"/>; the windows are equal here, so that changes which
    /// arm is armed and not which one ends the look.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>THE ONLY DIFFERENCE IS THE <see cref="UnauthorizedAccessException"/> THAT
    /// <see cref="ReadPid"/> DELIBERATELY LETS ESCAPE</strong> — an ACL fault on the scratch file;
    /// that method's remarks set out why it is left loud, and loud was right at the body call sites
    /// this replaced, where the exception was itself a finding about a directory this file created.
    /// In a <c>finally</c> the same loudness is wrong, and not as a matter of taste: an exception
    /// thrown from a <c>finally</c> REPLACES the one being propagated, so a permission fault here
    /// would erase the assertion the row had just made — the wedged runner, the undead child — and
    /// report a teardown detail in its place. So this answers <see langword="null"/> instead, which
    /// is the same answer as "no pid arrived" and leads to the same no-op
    /// <see cref="KillTreeQuietly"/>. The swallow is confined to this wrapper; the direct method
    /// keeps its loud reader for row 5, which calls it from a body.
    /// </para>
    /// <para>
    /// <strong>ONE CATCH IS ENOUGH BECAUSE THE SURFACE WAS TRACED, not because a broader one looked
    /// risky.</strong> Everything under this call is <see cref="WaitForPid"/>'s loop, and the only
    /// member of it that can throw at all is <see cref="ReadPid"/>. There,
    /// <see cref="File.Exists(string)"/> answers <see langword="false"/> rather than throwing on a
    /// path or permission fault, <see cref="File.ReadAllText(string)"/> sits inside an
    /// <see cref="IOException"/> filter that takes the whole not-found family with it, and the
    /// parsing below that (<see cref="string.TrimStart(char)"/>, <see cref="char.IsAsciiDigit"/>,
    /// <see cref="int.TryParse(string,NumberStyles,IFormatProvider,out int)"/>, and the two range
    /// slices, each gated by the <c>EndsWith</c> test above it) answers rather than throws.
    /// <see cref="UnauthorizedAccessException"/> derives from <see cref="SystemException"/> and not
    /// from <see cref="IOException"/>, which is exactly why it slips that filter and is the single
    /// escape. A <c>catch (Exception)</c> here would therefore widen nothing that exists — it would
    /// only hide the next escape somebody adds, in the one place where letting it through also
    /// erases the row's finding.
    /// </para>
    /// <para>
    /// <strong>A PID RECOVERED HERE ARMS TEARDOWN AND NOTHING ELSE, and from a <c>finally</c> that
    /// is structural rather than arranged.</strong> A <c>finally</c> runs only once its attempt has
    /// already returned or thrown: the premise was decided on the original wait, every assertion
    /// has either run or been pre-empted by an earlier throw, and no assertion remains that a pid
    /// found here could satisfy. It cannot establish a premise, cannot rescue a discarded attempt
    /// and cannot convert a red row into a green one. Contrast
    /// <see cref="ReclaimPidForTeardownAsync"/>'s one remaining BODY caller, row 5's late look,
    /// where the same neutrality has to be arranged by capturing the verdict before the call.
    /// </para>
    /// <para>
    /// <strong>What a pid recovered here is NOT: proof that the process it names is still the
    /// child (#529).</strong> By the time a <c>finally</c> reads it, <c>Run</c> may have returned
    /// and its own tree-kill freed the number, so <see cref="KillTreeQuietly"/>'s
    /// <see cref="TryOpen"/> is the only thing between this pid and a stranger — and since #529 its
    /// start-time checks are TWO bounds, so what passes them is a process started between the
    /// attempt and <see cref="PidStartTimeTolerance"/> after the pid file was written rather than
    /// anything started after the attempt at all. That was
    /// equally true of the body-sited look this replaced on the discarded path; the maximum
    /// lateness between the read and the kill is the same at both sites, so #539 moved the paths
    /// that reach the guard and not the guard, and #529 moved the guard.
    /// </para>
    /// </remarks>
    private static async Task<int?> ReclaimPidForTeardownQuietlyAsync(string pidFile, Task work)
    {
        try
        {
            return await ReclaimPidForTeardownAsync(pidFile, work);
        }
        catch (UnauthorizedAccessException)
        {
            // Teardown must never replace the row's own finding with its own — see above.
            return null;
        }
    }

    /// <summary>
    /// The pid in <paramref name="pidFile"/>, or <see langword="null"/> while there is not yet a
    /// whole one to read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Absent and unreadable are the same answer — "not yet" — because the caller does the same
    /// thing with each of them: poll again. Distinguishing those two would only let the wait end on
    /// a race with the child's own write.
    /// </para>
    /// <para>
    /// <strong>A HALF-WRITTEN FILE IS IN THAT SET TOO, because publication is DELIMITED rather than
    /// inferred (#528).</strong> A pid comes back only from a file whose whole content is one line:
    /// ASCII digits, then ONE terminator — a newline, optionally preceded by a carriage return.
    /// Anything else is "not yet" and the caller polls again: a prefix of the digits, the whole
    /// number with no terminator, a trailing <c>\r</c> whose <c>\n</c> has not landed, a second line
    /// that the first one's newline would otherwise make look complete, a second terminator that a
    /// greedier trim would have swallowed. The newline is what makes the line observable as WHOLE,
    /// and every child writer in this file appends one — pinned writer by writer since #541 by
    /// <see cref="PidFileWriters_PublishAPidTheReaderAccepts"/>, which makes that claim a
    /// permanent row rather than a measurement somebody took once. What it pins is the
    /// ACCEPTANCE, not the byte reading below: this reader takes either terminator, so a writer
    /// that swapped one for the other would stay green there.
    /// MEASURED by running the shapes' own commands
    /// on the maintainer's host: <see cref="NeverExitingChild"/> left the digits then <c>0D 0A</c>
    /// under <c>Set-Content</c> (seven bytes for a five-digit pid, one byte per character, no mark)
    /// and the digits then <c>0A</c> under <c>/bin/sh</c>'s <c>echo</c>, which the shell
    /// specification requires; <see cref="GrandchildHoldingPipesChild"/> left
    /// <c>31 33 30 34 38 0D 0A</c> and <c>33 39 34 0A</c> respectively.
    /// </para>
    /// <para>
    /// <strong>THE <c>TrimStart</c> BELOW IS DEFENCE IN DEPTH, NOT PART OF THAT CONTRACT.</strong>
    /// <see cref="File.ReadAllText(string)"/> already removes a real byte-order mark while decoding,
    /// so the hand-trim fires only on a second, literal <c>U+FEFF</c> that survived it — and no
    /// writer here emits even the first (measured: no mark on either platform). It costs nothing and
    /// pins nothing; deleting it reddens no row.
    /// </para>
    /// <para>
    /// <strong>WHY A DELIMITER RATHER THAN THE FILTER THAT USED TO STAND HERE, in that reader's own
    /// measurements.</strong> It concatenated every ASCII digit it found, which cannot tell a
    /// partial number from a whole one: a torn read of <c>51234</c> handed back <c>5</c>,
    /// <c>51</c>, <c>512</c> or <c>5123</c>, each a syntactically valid pid. A SHORT prefix made the
    /// leak assertion PASS over a live child, with the recycle guard producing that pass rather than
    /// preventing it — <see cref="TryOpen"/> discarded a boot-time pid as recycled (pids 4 and 5
    /// resolved to processes started 2.1 days earlier; 8 and 100 threw
    /// <see cref="ArgumentException"/>), so <see cref="IsAlive"/> answered <see langword="false"/>,
    /// <see cref="WaitForDeath"/> answered <see langword="true"/> at its first sample, and the
    /// <c>dead</c> assertion — the whole point of the #481 leak cover — went green over a child
    /// that was still running. A LONG prefix is an ordinary live pid, so WHEN it resolved to a
    /// process that also cleared that same guard the row reddened naming a stranger and the
    /// <c>finally</c>'s <c>KillTreeQuietly</c> killed it — one that had already exited, or that
    /// started before <c>startedUtc</c> minus five seconds, was discarded by the guard exactly as
    /// above. The guard is what kept that kill uncommon rather than what made it common: 192 live
    /// processes held four-digit pids at the same moment, and three of them had started within the
    /// previous ten minutes — a window far wider than the guard admits, so the point is only that
    /// the young end of the range is not structurally empty: uncommon, but not zero. Both were
    /// reachable, which is the whole reason the reader now refuses to convert an incomplete file
    /// into a pid instead of filtering one out of it.
    /// </para>
    /// <para>
    /// <strong>ONE CATCH IS NARROWER THAN THE COMMENT BELOW IT SUGGESTS.</strong> The
    /// <see cref="IOException"/> filter covers the child's own mid-write, but an
    /// <see cref="UnauthorizedAccessException"/> — an ACL fault on the scratch file — escapes and
    /// faults the wait. Left as-is on purpose: a permission fault on a directory this file created
    /// is a real defect and is better loud than retried four times and then blamed on the host's
    /// scheduler. Recorded rather than fixed so the choice is visible.
    /// </para>
    /// <para>
    /// <strong>WHERE IT IS LOUD IS NO LONGER EVERYWHERE, AND THAT IS #539 RATHER THAN A RETREAT
    /// FROM THE CHOICE ABOVE.</strong> PR #532 made it reachable at three extra sites — rows 1 and
    /// 2's late looks and row 5's; #539 moved two of them into <c>finally</c> blocks and routed
    /// those through <see cref="ReclaimPidForTeardownQuietlyAsync"/>, which swallows it. The reason
    /// is about the CALLER rather than about the fault: an exception leaving a <c>finally</c>
    /// replaces the one being propagated, so a loud reader would erase the row's own finding — the
    /// opposite of what "better loud" argues for, since what makes loudness right everywhere else
    /// is that the fault is then the most informative thing the row can say. The sites where it is —
    /// rows 1 and 2's body wait, row 5's body wait and body late look, and the body wait each of
    /// the two directly-launching rows added (#541, #529) — remain loud. Row
    /// 5's
    /// third read, the guarded one #539 put in its <c>finally</c>, is quiet for the same reason
    /// rows 1 and 2's are: it is a <c>finally</c>, and so are those two rows'.
    /// </para>
    /// <para>
    /// <strong>THE STRICTNESS IS A CONTRACT WITH THE WRITERS, AND BREAKING IT REDDENS RATHER THAN
    /// GREENS.</strong> A writer that stopped terminating its line, or emitted UTF-16 without a
    /// mark — which <see cref="File.ReadAllText(string)"/> cannot sniff, so a <c>U+0000</c> lands
    /// beside every digit and the reader refuses the file whichever byte order it was — would make
    /// this method answer <see langword="null"/> forever. No row passes over that: rows 1 and 2
    /// exhaust every budget and fail at <see cref="WithEscalatingBudget"/>'s <c>Assert.Fail</c>,
    /// row 5 fails its <c>childWasObserved</c> assertion. That is why strictness is safe in a reader
    /// whose "not yet" is indistinguishable from "never" — the cost of a broken writer contract is a
    /// red row, not a green one. It is a red row wearing the wrong label, though: those failures
    /// blame the HOST ("a host that did not start a shell inside 30s", "a host that still cannot
    /// start a shell inside the largest budget") and name nothing about this contract — the same
    /// #512 mislabel shape <see cref="PidFileName"/>'s remarks warn against. Which is why the
    /// contract is pinned since #541 by
    /// <see cref="PidFileWriters_PublishAPidTheReaderAccepts"/>: it runs the same two writers
    /// against this same reader with the runner taken out of the way, so a breach reddens there
    /// FIRST and names the newline, the writers that append it and the reader that requires it.
    /// The three rows above still go red, still with the wrong label; what changes is that the
    /// red naming the cause is now in the same run.
    /// </para>
    /// </remarks>
    private static int? ReadPid(string pidFile)
    {
        if (!File.Exists(pidFile))
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(pidFile);
        }
        catch (IOException)
        {
            // The child is mid-write; the caller will try again.
            return null;
        }

        // Read only a complete pid line: writers append a newline when publication is complete.
        var candidate = text.TrimStart('\uFEFF');
        if (!candidate.EndsWith('\n'))
        {
            return null;
        }

        // EXACTLY ONE terminator, not every trailing one: strip the newline the gate above proved
        // is there, then the carriage return that may precede it. Trimming the whole run would let
        // a file whose extra lines are EMPTY in through its last line ending — the digit check
        // below is what refuses a second line, and it can only see one if this leaves it there.
        candidate = candidate[..^1];
        if (candidate.EndsWith('\r'))
        {
            candidate = candidate[..^1];
        }

        if (candidate.Length == 0 || candidate.Any(static c => !char.IsAsciiDigit(c)))
        {
            return null;
        }

        if (int.TryParse(candidate, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            return pid;
        }

        return null;
    }

    /// <summary>The most bytes of a pid file a failure message reads, and prints.</summary>
    /// <remarks>
    /// Sixteen is comfortably past the longest complete line these writers can produce — a
    /// seven-digit pid, a carriage return and a newline is nine bytes — so every shape
    /// <see cref="ReadPid"/> refuses is shown whole, while a file that has somehow become large
    /// is elided rather than pasted into a public job log. It bounds the READ as well as the
    /// print: <see cref="PidFileBytes"/> takes these bytes off a stream rather than slicing them
    /// out of a whole file, so a pid file that is not one cannot be materialised into an
    /// <see cref="OutOfMemoryException"/> that no filter below catches — and that argument has to
    /// hold on the GREEN path, the call being a positional message argument.
    /// </remarks>
    private const int PidFileBytePreview = 16;

    /// <summary>
    /// The leading bytes of <paramref name="pidFile"/>, as a failure message prints them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hex rather than text, because the whole class of breach this diagnoses is invisible as
    /// text: a missing terminator, a lone carriage return, a second line and a UTF-16 pid all
    /// render as digits or as nothing at all. Absent, empty and unreadable are reported apart
    /// from each other and from a byte string, since they are four different conditions.
    /// </para>
    /// <para>
    /// <strong>The cap is on the READ, not only on the print.</strong> The length is taken from
    /// the stream and the bytes come through a buffer of exactly
    /// <see cref="PidFileBytePreview"/>, so nothing allocated here is a function of the file's
    /// SIZE and one that has stopped being the small thing this name implies is elided rather
    /// than materialised. (The <see cref="FileStream"/>'s own internal buffer is the runtime's
    /// default and is likewise a constant, not the file.) <see cref="FileShare.Read"/> matches
    /// what <see cref="File.ReadAllBytes(string)"/> took before, so a concurrent writer still
    /// lands in the <c>unreadable</c> branch rather than being newly tolerated, and the filter
    /// still names the two types that branch existed for — <see cref="IOException"/> covers the
    /// file-disappeared, directory-gone and path-too-long arrivals. One branch is not bit-for-bit
    /// what it was: <c>length</c> is sampled before the read, so a file appended between the two
    /// calls can report <c>present and empty</c> over bytes the read did return — strictly more
    /// conservative than the old whole-file read, and unreachable against a writer that writes
    /// once.
    /// </para>
    /// <para>
    /// The path is never in the answer — only bytes, and the bare file name the caller already
    /// holds. The scratch directory sits under <see cref="Path.GetTempPath"/>, which on Windows
    /// contains the account name, and an absolute path in a new assertion message is the #498
    /// class this repository refuses.
    /// </para>
    /// </remarks>
    private static string PidFileBytes(string pidFile)
    {
        if (!File.Exists(pidFile))
        {
            return "absent";
        }

        var buffer = new byte[PidFileBytePreview];
        long length;
        int read;
        try
        {
            using var stream = new FileStream(
                pidFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            length = stream.Length;
            read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }

        if (length == 0 || read == 0)
        {
            return "present and empty";
        }

        var hex = string.Join(
            ' ',
            buffer.Take(read).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

        return length <= PidFileBytePreview
            ? hex
            : FormattableString.Invariant($"{hex} (first {PidFileBytePreview} of {length})");
    }

    /// <summary>
    /// The exit state of <paramref name="process"/>, as a failure message prints it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Guarded rather than conditioned on the caller, for the reason
    /// <see cref="PidFileBytes"/> is: it is a POSITIONAL argument to <c>Assert.True</c> and is
    /// therefore evaluated on the green path too.
    /// <see cref="Process.ExitCode"/> throws on a process that has not exited,
    /// <see cref="Process.HasExited"/> throws when no process is associated with the object, and
    /// on Windows a refused query arrives as
    /// <see cref="System.ComponentModel.Win32Exception"/> — the same three
    /// <see cref="TryOpen"/> already filters, for the same reason.
    /// </para>
    /// <para>
    /// A READING rather than a verdict: nothing here ends a wait early. On
    /// <see cref="GrandchildHoldingPipesChild"/> the shell exits the moment it has spawned the
    /// grandchild (#392), so "exited" alone is no signal at all there — a non-zero CODE is what
    /// names a shell that died instead of writing.
    /// </para>
    /// </remarks>
    private static string ProcessExitState(Process process)
    {
        try
        {
            return process.HasExited
                ? FormattableString.Invariant($"exited {process.ExitCode}")
                : "still running";
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            return "unreadable";
        }
    }

    /// <summary>
    /// Opens <paramref name="pid"/> if it is still a live process that this row could plausibly
    /// have started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="Process.StartTime"/> checks are a pid-reuse guard. The window between the
    /// child writing its pid and teardown reading it is seconds, but the consequence of losing
    /// that race is killing an unrelated process on a shared CI agent — and killing it with
    /// <c>entireProcessTree: true</c>, so the blast radius of a mis-hit is a stranger's whole
    /// TREE. That is worth the lines it takes to rule out.
    /// </para>
    /// <para>
    /// <strong>TWO BOUNDS SINCE #529, AND THE SECOND IS THE ONE THAT MAKES THIS MORE THAN A
    /// SANITY CHECK.</strong> The lower bound refuses a process older than the attempt. On its
    /// own it accepted ANY process started afterwards, so a pid recycled onto something the
    /// machine started mid-row was indistinguishable from the child. The upper bound refuses a
    /// process that started after the pid file was WRITTEN, which the true child cannot have
    /// done: it wrote that file itself, so its start precedes the write by construction.
    /// </para>
    /// <para>
    /// <strong>The anchor is the file's write time, not the moment the row read it.</strong>
    /// <see cref="ReadPid"/> refuses an incomplete line and the caller polls every
    /// <see cref="PollIntervalMs"/>, so the READ instant is up to one poll later than the write
    /// and varies with load; the file's own <see cref="File.GetLastWriteTimeUtc(string)"/> is
    /// not. Anchoring on the read would have to widen the tolerance by a poll to stay safe, and
    /// that poll is exactly the slack a stranger would be free to start in.
    /// </para>
    /// <para>
    /// <strong>A stat that fails falls back to the LOWER BOUND ALONE, which is fail-toward-today
    /// and deliberately not fail-closed.</strong> <see cref="File.GetLastWriteTimeUtc(string)"/>
    /// does not throw for a file that is not there — MEASURED: it answers
    /// <c>1601-01-01T00:00:00Z</c> for both a missing file and a missing directory — so the
    /// sentinel, not the <c>catch</c>, is the path that fallback normally takes. The direction is
    /// chosen rather than inherited: refusing to open a pid this row cannot corroborate would
    /// leak whatever it names for <see cref="ChildLifetime"/>, and a teardown that kills nothing
    /// is the leak #481 exists to prevent. Every caller therefore ends up no worse off than it
    /// was before #529, and better off whenever the file is there — which on every path in this
    /// file it is, all five teardown blocks and every body look running strictly before the
    /// <see cref="TryDeleteDirectory"/> that removes it.
    /// </para>
    /// <para>
    /// <strong>AND THE DIRECTION IS CHOSEN FOR A SHARPER REASON THAN "a leak is the cheaper
    /// mistake": A REFUSAL HERE IS INDISTINGUISHABLE FROM A DEATH UPSTREAM.</strong>
    /// <see cref="IsAlive"/> answers <see langword="false"/> on <see langword="null"/> whatever
    /// the reason, and <see cref="WaitForDeath"/> answers <see langword="true"/> on the first
    /// <see langword="false"/> — so a wrongly refused pid does not merely go unkilled, it is
    /// REPORTED DEAD, and rows 1 and 5's <c>dead</c> assertion passes over a child that is still
    /// running. Two returns carrying one meaning is the whole of it; the same collapse cost this
    /// file a vacuous pass once before, over a boot-time pid, as <see cref="ReadPid"/>'s remarks
    /// record. The upper bound widens the set of inputs that reach it, so the tolerance is sized
    /// to make a wrong refusal rare (see <see cref="PidStartTimeTolerance"/>) and the missing
    /// file is answered with the lower bound rather than with a refusal. Making
    /// <see cref="TryOpen"/> answer three ways — open, gone, refused — is what would close it,
    /// and that reaches every helper reading this answer (<see cref="IsAlive"/>,
    /// <see cref="WaitForDeath"/>, <see cref="KillTreeQuietly"/>) rather than a line here.
    /// </para>
    /// <para>
    /// <strong>THE LEDGER THIS PARAGRAPH USED TO KEEP.</strong> #524 multiplied the number of
    /// pids this file reads per run without changing the guard that decides which of them may be
    /// killed; PR #532 multiplied it again, by one reclaiming read per attempt that ends without
    /// a pid (<see cref="ReclaimPidForTeardownAsync"/>); #539 moved that read into the
    /// <c>finally</c>, leaving the count per attempt where it was and widening the set of
    /// attempts that take it; #541 multiplied it once more, by two body reads and up to two late
    /// looks, one of them naming a process the row holds no handle to. Through all of that the
    /// bound did not move, and this is the change that moves it. What is still NOT pinned is the
    /// identity itself: a pid recycled inside the window between the write and
    /// <see cref="PidStartTimeTolerance"/> after it still passes both comparisons, and closing
    /// that needs a handle rather than a timestamp.
    /// </para>
    /// <para>
    /// Both comparisons are pinned by
    /// <see cref="TryOpen_RefusesAProcessOutsideTheWindowTheChildMustHaveStartedIn"/>, each
    /// separately, and the skew the tolerance absorbs is recorded on every run by
    /// <see cref="TheGuardsAnchors_StayInTheOrderARealChildProduces"/>.
    /// </para>
    /// </remarks>
    private static Process? TryOpen(int? pid, DateTime startedUtc, string pidFile)
    {
        if (pid is not int id)
        {
            return null;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(id);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // No such process — it has already exited.
            return null;
        }

        DateTime startedByProcessUtc;
        try
        {
            startedByProcessUtc = process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            // StartTime unreadable (exited, access denied, remote) — treat it as not ours.
            process.Dispose();
            return null;
        }

        if (startedByProcessUtc < startedUtc.AddSeconds(-5))
        {
            // Older than this row: a recycled pid, not our child.
            process.Dispose();
            return null;
        }

        if (PidFileWrittenUtc(pidFile) is DateTime announcedUtc
            && startedByProcessUtc > announcedUtc + PidStartTimeTolerance)
        {
            // Younger than the announcement: whatever wrote that pid, this process is not it.
            process.Dispose();
            return null;
        }

        return process;
    }

    /// <summary>
    /// The UTC start time of <paramref name="pid"/>, or <see langword="null"/> when it cannot be
    /// read.
    /// </summary>
    /// <remarks>
    /// The raw reading <see cref="TryOpen"/>'s bounds are computed FROM, with neither applied —
    /// which is what makes it usable by a row measuring the bounds rather than relying on them.
    /// Guarded for the reason <see cref="ProcessExitState"/> is, and against the same two sets:
    /// <see cref="Process.GetProcessById(int)"/> throws for a process that has already exited,
    /// and <see cref="Process.StartTime"/> can be refused. A caller that let either escape would
    /// report its finding as a framework message instead of its own.
    /// </remarks>
    private static DateTime? ProcessStartedUtc(int pid)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }

        using (process)
        {
            try
            {
                return process.StartTime.ToUniversalTime();
            }
            catch (Exception ex) when (ex is InvalidOperationException
                                           or System.ComponentModel.Win32Exception
                                           or NotSupportedException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// When <paramref name="pidFile"/> was last written, or <see langword="null"/> when that
    /// cannot be established.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> is the fail-toward-today answer <see cref="TryOpen"/> reads as "no
    /// upper bound available", and the sentinel is the ordinary way it arrives: the framework
    /// answers <c>1601-01-01T00:00:00Z</c> rather than throwing when the file or its directory is
    /// gone. The <c>catch</c> covers the arrivals that do throw — a path the framework refuses,
    /// or one it cannot read — and both routes mean the same thing here.
    /// </remarks>
    private static DateTime? PidFileWrittenUtc(string pidFile)
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(pidFile);
            return written.Year <= 1601 ? null : written;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Whether the recorded pid is still a live process started by this row.</summary>
    /// <remarks>
    /// <paramref name="pidFile"/> is carried for <see cref="TryOpen"/>'s upper bound alone —
    /// nothing here reads it, and the file still being on disk at this point is a property of
    /// every call site rather than of this method.
    /// </remarks>
    private static bool IsAlive(int? pid, DateTime startedUtc, string pidFile)
    {
        var process = TryOpen(pid, startedUtc, pidFile);
        if (process is null)
        {
            return false;
        }

        using (process)
        {
            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Polls <see cref="IsAlive"/> until the recorded pid is gone, or <paramref name="window"/>
    /// expires; returns whether it went.
    /// </summary>
    /// <remarks>
    /// Synchronous on purpose, for the reason <see cref="WaitForPid"/> gives — callers wrap it in
    /// <c>Task.Run</c> rather than sleeping inside an async method.
    /// </remarks>
    private static bool WaitForDeath(int? pid, DateTime startedUtc, TimeSpan window, string pidFile)
    {
        var deadline = DateTime.UtcNow + window;
        while (true)
        {
            if (!IsAlive(pid, startedUtc, pidFile))
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(PollIntervalMs);
        }
    }

    /// <summary>
    /// Kills the recorded pid and everything beneath it. Called from every <c>finally</c> so that
    /// a red run leaves nothing behind.
    /// </summary>
    private static void KillTreeQuietly(int? pid, DateTime startedUtc, string pidFile)
    {
        var process = TryOpen(pid, startedUtc, pidFile);
        if (process is null)
        {
            return;
        }

        // Kill inside, dispose outside: `using` emits its Dispose in the enclosing finally, so the
        // dangerous dispose-then-kill order cannot be written here. See ChildProcess's remarks.
        using (process)
        {
            ChildProcess.KillTreeQuietly(process);
        }
    }

    /// <summary>
    /// Waits, boundedly, for an abandoned <c>Run</c> to unwind now that its child is dead, and
    /// observes whatever it eventually faults with.
    /// </summary>
    /// <remarks>
    /// The observer is a CONTINUATION rather than a read of <c>work.Exception</c> after the wait:
    /// a run that has not completed inside <see cref="DrainWindow"/> has a null <c>Exception</c>,
    /// so reading the property there observes nothing and a fault reached afterwards would still
    /// land on <see cref="TaskScheduler.UnobservedTaskException"/> — attributed to a finalizer
    /// thread and to no test in particular. The bound stays because teardown must not wedge on a
    /// runner that has.
    /// </remarks>
    private static async Task DrainAsync(Task<ProcessResult> work)
    {
        _ = work.ContinueWith(static settled => _ = settled.Exception, TaskScheduler.Default);
        await Task.WhenAny(work, Task.Delay(DrainWindow)).ConfigureAwait(false);
    }

    /// <summary>Creates a per-row scratch directory outside the repository.</summary>
    private static string CreateScratchDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "vouchfx-procrunner-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Best-effort scratch cleanup; a stuck child can still hold the pid file.</summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Teardown must never replace the real failure with its own.
        }
    }
}
