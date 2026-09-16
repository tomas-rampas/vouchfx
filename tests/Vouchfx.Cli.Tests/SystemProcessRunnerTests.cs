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
// EVERY ROW THAT CAN REACH THAT GAP NOW TAKES ONE LATE LOOK BEFORE GIVING UP — rows 1 and 2 on
// their no-pid return, row 5 before its pid assertion — so the slice of the gap where the write was
// merely LATE is closed, because a late write becomes observable and therefore killable. See
// ReclaimPidForTeardownAsync. What remains of the gap is a child that never writes a pid AT ALL,
// and ChildLifetimeSeconds is still its only backstop.
//
// SINCE #524 THAT GAP IS WIDER BY A FACTOR OF BudgetAttempts, and both escalating rows sit in it,
// not just the one whose remarks mention it. An attempt that ends without a pid is retried, so rows
// 1 and 2 can each leave up to BudgetAttempts unannounced children behind — worst case four per
// row, eight per run of this file.
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
// SO THE NO-PID PATH LOOKS ONCE MORE BEFORE IT GIVES UP. ReclaimPidForTeardownAsync re-polls the
// pid file for LateReadWindow and hands whatever lands to the same `finally` that would have
// received it normally, which is exactly the mechanism above: a late write becomes observable and
// therefore killable. What survives is narrower and is stated at that method — a child that
// publishes NO pid inside the widened window and whose kill also failed. Nothing in this file can
// name a process that never named itself, so ChildLifetime remains the ceiling on that residual,
// which is why that lifetime is finite rather than infinite.
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
// leave a live PROCESS behind even though it cannot leave a live FINDING behind. Most of that is
// now closed by the late look above — a discarded attempt reclaims and kills a late-writing
// survivor before it returns. What is NOT closed, and what the old heading therefore could not have
// carried even post-fix, is a child that publishes no pid at all inside budget+8s and whose kill
// also failed: escalation still discards it unseen, ChildLifetime is still its only backstop, and
// no wording in this paragraph changes that.
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
// late look, and only once the attempt has already given up on its premise. Row 5's Run does not
// settle on its own — its budget is unreachable and it is the row that cancels — so that row alone
// still carries an absolute ceiling; UnracedPidCeiling records why an absolute figure is defensible
// there and was not here.
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
using Vouchfx.Cli.Selection;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Cli.Tests;

/// <summary>
/// Lifetime and bounding cover for <see cref="SystemProcessRunner"/> (#481).
/// </summary>
public sealed class SystemProcessRunnerTests
{
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
    /// How much longer <see cref="ReclaimPidForTeardownAsync"/> looks after an attempt has given up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Paid only on a path that has ALREADY failed to observe its child, so a healthy run
    /// never reaches it.</strong> On rows 1 and 2 that is an attempt about to hand its budget back
    /// to be doubled, at most <see cref="BudgetAttempts"/> times — twenty seconds added to a row
    /// already spending forty-five on budgets alone. On row 5 it is paid at most once, immediately
    /// before an assertion that is going to fail anyway, so it delays a red row by five seconds and
    /// costs a green one nothing.
    /// </para>
    /// <para>
    /// It is on no assertion's critical path within its own attempt: it runs after that attempt's
    /// last assertion (rows 1 and 2) or in front of one already determined to fail (row 5). It does
    /// sit ahead of the NEXT attempt, so the wall clock it adds is real; what it cannot do is delay
    /// or alter a finding that has already been made.
    /// </para>
    /// <para>
    /// <strong>Five rather than reusing <see cref="PidSettleWindow"/>'s three, because the two are
    /// sized against different children.</strong> <see cref="PidSettleWindow"/> is on the hot path
    /// of every attempt and is sized for a write already in flight from a child that has just been
    /// killed. This one is sized for a child that is still ALIVE because the tree-kill did not reach
    /// it, and whose first write is therefore still ahead of it. The figure that matters is the
    /// TOTAL observation window — the budget, plus <see cref="PidSettleWindow"/>, plus this — which
    /// comes to 11s on the first attempt, 14s on the second, 20s on the third and 32s on the last.
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
    /// Row 5 only. Its runner carries <see cref="UnreachableBudget"/> and the row is what ends the
    /// call, so "wait until Run settles" would wait for something the row has not done yet, and the
    /// wait needs a figure.
    /// </para>
    /// <para>
    /// <strong>An absolute figure is defensible HERE for the reason it was not defensible on rows 1
    /// and 2: nothing is racing to destroy the child.</strong> On those rows the constant had to
    /// beat a competing deadline that was already killing the thing being waited for, so any host
    /// slow enough to lose the race produced an empty directory and a red row. Row 5's child has
    /// five minutes of budget and no killer; this ceiling has only to exceed a shell start-up, and a
    /// host that cannot manage one in thirty seconds — against a worst case of 13.6s across every
    /// load measured in <see cref="StartingBudget"/>'s figures — has a problem this row should
    /// report rather than absorb. It must stay below <see cref="ChildLifetime"/> so that a wait
    /// which ran to the ceiling is known to have been waiting on a live child rather than on one
    /// that had already exited — asserted at the top of row 5 itself, because that is the only row
    /// this ceiling governs and a doc-comment does not fail a build. That guard weighs this ceiling
    /// PLUS <see cref="LateReadWindow"/>, because the failing path spends both looking; the bound on
    /// this constant alone follows from the sum and is not separately asserted. Stated rather than
    /// left to the reader, since the sentence before it is an argument that the build enforces this
    /// and a pointer to a guard over a different quantity would quietly stop being one.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan UnracedPidCeiling = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long teardown waits for the abandoned <c>Run</c> to unwind once its child is dead.
    /// </summary>
    private static readonly TimeSpan DrainWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long row 1 waits for the killed child to actually disappear before failing.
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
    /// Costs nothing on the green path: the poll ends at the first dead sample. Two seconds is the
    /// budget for the failure to be believed, not a latency the healthy case pays.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan DeathWindow = TimeSpan.FromSeconds(2);

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
    /// next edit to any of these numbers is caught by a red row rather than by a reader.
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
                        // live (PR #532's review) and one more bounded look is what keeps a
                        // late-writing survivor killable by the `finally` below. It cannot rescue
                        // the attempt — the return is `false` whatever it finds.
                        pid = await ReclaimPidForTeardownAsync(pidFile, work);
                        return false;
                    }

                    var dead = await Task.Run(() => WaitForDeath(pid, startedUtc, DeathWindow));
                    Assert.True(
                        dead,
                        FormattableString.Invariant(
                            $"SystemProcessRunner.Run returned but child pid {pid} was still alive {DeathWindow.TotalSeconds:F0}s later. Abandoning the timed-out child is the leak #481 is about; the timeout path must tree-kill it. The window is there because the kill is asynchronous, not because a live child is tolerable."));
                    return true;
                }
                finally
                {
                    KillTreeQuietly(pid, startedUtc);
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
    /// <see cref="ReclaimPidForTeardownAsync"/> narrows it to a grandchild whose pid never lands at
    /// all, by giving a late write time to become one; it does not remove it.
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

                    // The premise is read off the ORIGINAL wait above, before this runs, and
                    // deliberately: a pid the late look recovers is a target for teardown, not
                    // evidence that this attempt observed a grandchild. Assigning it after the
                    // premise has been decided is what keeps the two apart.
                    pid = await ReclaimPidForTeardownAsync(pidFile, work);
                    return false;
                }
                finally
                {
                    KillTreeQuietly(pid, startedUtc);
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
    /// to fail. It is the header's original ×1 gap on the last row still carrying it.
    /// </para>
    /// <para>
    /// The child is killed by the row's own <c>finally</c> on every path, exactly as in rows 1
    /// and 2 — an assertion that fails must not leave a live child on the agent.
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
                pid = await ReclaimPidForTeardownAsync(pidFile, work);
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

            var dead = await Task.Run(() => WaitForDeath(pid, startedUtc, DeathWindow));
            Assert.True(
                dead,
                FormattableString.Invariant(
                    $"SystemProcessRunner.Run returned on cancellation but child pid {pid} was still alive {DeathWindow.TotalSeconds:F0}s later. Cancellation must reach the same tree-kill the timeout path does; a cancelled call that abandons its child is the orphan #481 exists to prevent. The window is there because the kill is asynchronous, not because a live child is tolerable."));
        }
        finally
        {
            KillTreeQuietly(pid, startedUtc);
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
    /// without one is a write still in flight. <c>51234\r</c> is a torn CRLF: reachable by
    /// construction at a byte boundary inside the writer's seven-byte write, not something observed
    /// here. <c>51234\n999\n</c> pins the WHOLE-LINE half of the guard, which the four single-line
    /// shapes leave free — none of them holds an interior non-digit, so a reader that went back to
    /// filtering digits out of a terminated file would keep all four green and read this one as
    /// <c>51234999</c>.
    /// </para>
    /// <para>
    /// <strong>BOTH ENDINGS ARE WRITTEN AS LITERALS RATHER THAN AS
    /// <see cref="Environment.NewLine"/>.</strong> That constant was this row's first draft and was
    /// rejected: it expands to CRLF on Windows and LF on Linux, so on the lane that gates merges it
    /// would have exercised the LF half only, and a reader that dropped the <c>'\r'</c> from
    /// <see cref="ReadPid"/>'s <c>TrimEnd</c> would have passed CI while failing every pid read on
    /// Windows. The literals remove that blind spot rather than narrowing it:
    /// <see cref="File.WriteAllText(string,string)"/> writes these bytes verbatim and
    /// <see cref="File.ReadAllText(string)"/> translates none of them, so the CRLF member is a CRLF
    /// member on every platform. MEASURED on Windows with that character deleted: four rows red —
    /// this one and the three that read a pid.
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

            // One assertion over all five, so a failure names the shape that moved rather than
            // reporting the same "Expected: null" for whichever of them broke.
            Assert.Equal(
                ((int?)null, (int?)null, (int?)51234, (int?)51234, (int?)null),
                (unterminated, carriageReturnOnly, windowsEnding, posixEnding, twoLines));
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
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
    /// <see cref="ReclaimPidForTeardownAsync"/> recovered afterwards does not change that answer —
    /// it was used to kill the child, not to establish anything.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>THIS IS NOT A RETRY OF A FAILED ASSERTION, AND THE DISTINCTION IS THE WHOLE
    /// LICENCE FOR IT.</strong> An attempt that returns <see langword="false"/> made no judgement
    /// about <see cref="SystemProcessRunner"/> at all: no pid ever arrived, so there was never a
    /// number to arm the death assertion with. WHY it did not arrive is a separate question that
    /// this fact does not settle — the usual answer is that the runner's tree-kill reached the child
    /// before the operating system scheduled its first write, but "usual" is not "always", and the
    /// case where the kill did NOT reach it is handled by <see cref="ReclaimPidForTeardownAsync"/>
    /// rather than assumed away. An attempt that DID get a pid runs its assertions to completion,
    /// and a failure among them throws straight out through this method — assertions
    /// are never re-run, and there is no path here that sees one fail and tries again.
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
    /// here holding no pid to kill it with. The discarded attempt closes that itself, at
    /// <see cref="ReclaimPidForTeardownAsync"/>, before it returns; nothing in this method does, and
    /// this paragraph now claims only what it can carry.
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
        // does not settle at all, WaitForPid first spends its own ceiling (grace + PidSettleWindow)
        // and the race after it adds up to another grace, so an attempt can run to roughly two
        // budgets plus twenty-three seconds — about 71s at the largest budget, past ChildLifetime.
        // That is not a hole in this guard: it is a path that never reaches the death poll the
        // guard protects, because it fails first at `Assert.True(finished, …)`, which IS the row
        // correctly reporting a wedged runner. Nothing downstream of that assertion runs, so what
        // the child does at 60s cannot change the verdict.
        //
        // THE NO-PID PATH IS EXCLUDED BY THE SAME WORD, and since PR #532 it costs LateReadWindow
        // more than it did. It reaches no death poll either — it returns `false` and the loop below
        // discards it — so ChildLifetime is not the thing bounding it; BudgetAttempts is. What it
        // does cost is written down at LateReadWindow rather than folded in here, because it is a
        // wall-clock figure and not a correctness relationship.
        //
        // AND THE TWO CONDITIONS DO NOT MEET ANYWAY, which is a stronger statement than "fails
        // first" and is the one that actually closes the question. For ChildLifetime to matter at
        // all, the child must be ALIVE for the whole minute so that its own exit is what closes the
        // pipes — and a live child publishes its pid as its FIRST action, so WaitForPid returned
        // then and the race that follows expires at most grace later. Taking the slowest start-up
        // this file has ever measured (13.6s, at 128 burners) and the largest budget: 13.6+34 =
        // 47.6s, still short of 60. So whenever a child is alive to meet this ceiling, the clocks
        // that could reach it are short. There is no shape in which this ceiling decides that path,
        // and no need to re-derive that next time.
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
    /// <param name="work">The <c>Run</c> whose child is being waited for.</param>
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
    /// The <c>Run</c> whose child is being reclaimed. Settled on rows 1 and 2, NOT settled on row 5.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>WHY THIS CLOSES THE MECHANISM IT IS FOR.</strong> A caller that reaches its no-pid
    /// path has established at most that <c>Run</c> refused correctly. It has NOT established that
    /// the child is dead: <c>Run</c> only ever ISSUES the tree-kill, and a caller that never saw a
    /// pid never looked at the child. If that kill was broken AND the child's first write was merely
    /// delayed past whatever window did the looking — <see cref="PidSettleWindow"/> after the run
    /// settles on rows 1 and 2, <see cref="UnracedPidCeiling"/> on row 5 — then the pid file is
    /// empty at the moment the caller looks and non-empty shortly afterwards. Looking a second time,
    /// later, is precisely what turns that child from unnameable into killable: the pid it finds is
    /// assigned to the caller's own <c>pid</c>, and the ordinary <c>finally</c> kills it through the
    /// ordinary <see cref="TryOpen"/> guard. There is no second kill site; this method only widens
    /// the window in which the existing one has something to aim at.
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
    /// start-time LOWER BOUND, which is not an identity check: a pid recycled onto a process that
    /// started after this row did passes it unchanged and that process is killed. Tracked as
    /// <strong>#529</strong>, and open. It belongs here in ONE sub-case only, and the first
    /// paragraph above names the shape this method actually exists for. A SURVIVOR's pid is not
    /// recycled while it is alive: <see cref="TryOpen"/> runs in the same <c>finally</c> a beat
    /// later and opens the real child, so no question of identity arises. The recycle needs the
    /// other shape — a child that DID write, WAS genuinely killed, and whose write the original
    /// wait missed, landing any time before this late look gives up — so that what lands here
    /// names a process which has since died and whose pid the operating system is free to
    /// reissue. Narrow, and still not closed; a paragraph promising to state what it does not
    /// close has to include the case it widens.
    /// </para>
    /// <para>
    /// <strong>A THIRD, RECORDED WHERE IT LIVES RATHER THAN RESTATED HERE.</strong>
    /// <see cref="ReadPid"/> lets an <see cref="UnauthorizedAccessException"/> escape, which at this
    /// method's call sites turns "retry with a larger budget" into a hard row failure; that remark
    /// sets out why it is left loud. Named here rather than written out, because this section
    /// promises completeness and a third residual left off the list would break that promise, while
    /// a second copy of the reasoning would be one more thing to keep in step.
    /// </para>
    /// <para>
    /// <strong>It never changes a verdict, and every call site is arranged so that it cannot.</strong>
    /// Rows 1 and 2 return <see langword="false"/> whatever it finds; row 5 captures
    /// <c>childWasObserved</c> before calling it and asserts on that. So the verdict is always read
    /// off the ORIGINAL wait and a pid recovered here arms teardown and nothing else — it cannot
    /// establish a premise the wait failed to establish, and <see cref="WithEscalatingBudget"/>
    /// still discards the attempt. On rows 1 and 2 the hoisted
    /// <c>Assert.ThrowsAsync&lt;ProcessTimeoutException&gt;</c> above the call site is what keeps
    /// "the budget was too short" apart from a runner fault, and this runs strictly after it.
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
    /// become live on rows 1 and 2 (where <paramref name="work"/> HAS settled) and would end the
    /// look early on exactly the callers with the most to find: their survivor is alive because a
    /// kill failed, and the whole point is to give its pending first write the full window. Row 5
    /// could not use a settle arm at all — its <c>Run</c> has not settled and the row has not
    /// cancelled yet — so a shorter figure would buy a divergence between callers and nothing else.
    /// </para>
    /// </remarks>
    private static Task<int?> ReclaimPidForTeardownAsync(string pidFile, Task work) =>
        Task.Run(() => WaitForPid(pidFile, work, LateReadWindow, LateReadWindow));

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
    /// ASCII digits, then a newline. Anything short of that is "not yet" and the caller polls again
    /// — a prefix of the digits, the whole number with no terminator, a trailing <c>\r</c> whose
    /// <c>\n</c> has not landed, a second line that the first one's newline would otherwise make
    /// look complete. The newline is what makes the line observable as WHOLE, and every child writer
    /// in this file appends one. MEASURED by running the shapes' own commands on the maintainer's
    /// host: <see cref="NeverExitingChild"/> left the digits then <c>0D 0A</c> under
    /// <c>Set-Content</c> (seven bytes for a five-digit pid, one byte per character, no mark) and
    /// the digits then <c>0A</c> under <c>/bin/sh</c>'s <c>echo</c>, which the shell specification
    /// requires; <see cref="GrandchildHoldingPipesChild"/> left <c>31 33 30 34 38 0D 0A</c> and
    /// <c>33 39 34 0A</c> respectively.
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
    /// that was still running. A LONG prefix is an ordinary live pid, so the row reddened naming a
    /// stranger and the <c>finally</c>'s <c>KillTreeQuietly</c> killed it: 192 live processes held
    /// four-digit pids at the same moment, three of them started within the previous ten minutes.
    /// Both were reachable, which is the whole reason the reader now refuses to convert an
    /// incomplete file into a pid instead of filtering one out of it.
    /// </para>
    /// <para>
    /// <strong>ONE CATCH IS NARROWER THAN THE COMMENT BELOW IT SUGGESTS.</strong> The
    /// <see cref="IOException"/> filter covers the child's own mid-write, but an
    /// <see cref="UnauthorizedAccessException"/> — an ACL fault on the scratch file — escapes and
    /// faults the wait. PR #532 made that reachable at two more sites
    /// (<see cref="ReclaimPidForTeardownAsync"/>'s callers), where it converts "retry with a larger
    /// budget" into a hard row failure. Left as-is on purpose: a permission fault on a directory
    /// this file created is a real defect and is better loud than retried four times and then
    /// blamed on the host's scheduler. Recorded rather than fixed so the choice is visible.
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
    /// #512 mislabel shape <see cref="PidFileName"/>'s remarks warn against.
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

        candidate = candidate.TrimEnd('\r', '\n');
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

    /// <summary>
    /// Opens <paramref name="pid"/> if it is still a live process that this row could plausibly
    /// have started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="Process.StartTime"/> check is a pid-reuse guard. The window between the
    /// child writing its pid and teardown reading it is seconds, but the consequence of losing
    /// that race is killing an unrelated process on a shared CI agent, which is worth three lines
    /// to rule out.
    /// </para>
    /// <para>
    /// It is a LOWER BOUND, not an identity check, and no test pins it: a pid recycled onto a
    /// process that started after this row did passes the comparison unchanged. Tracked as
    /// <strong>#529</strong>. Named here because #524 multiplied the number of pids this file reads
    /// per run without changing the guard that decides which of them may be killed — and PR #532
    /// multiplied it again, by one read per no-pid path per attempt
    /// (<see cref="ReclaimPidForTeardownAsync"/>), still without changing this guard.
    /// </para>
    /// </remarks>
    private static Process? TryOpen(int? pid, DateTime startedUtc)
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

        try
        {
            if (process.StartTime.ToUniversalTime() < startedUtc.AddSeconds(-5))
            {
                // Older than this row: a recycled pid, not our child.
                process.Dispose();
                return null;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            // StartTime unreadable (exited, access denied, remote) — treat it as not ours.
            process.Dispose();
            return null;
        }

        return process;
    }

    /// <summary>Whether the recorded pid is still a live process started by this row.</summary>
    private static bool IsAlive(int? pid, DateTime startedUtc)
    {
        var process = TryOpen(pid, startedUtc);
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
    private static bool WaitForDeath(int? pid, DateTime startedUtc, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (true)
        {
            if (!IsAlive(pid, startedUtc))
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
    private static void KillTreeQuietly(int? pid, DateTime startedUtc)
    {
        var process = TryOpen(pid, startedUtc);
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
