// Drill-lane process hygiene — issue #378.
//
// Two symptoms, one family. A drill row launches the built CLI as a child process
// (`dotnet <repo>/src/Cli/Vouchfx.Cli/bin/<cfg>/net8.0/vouchfx.dll run <suite>`), and that
// child is itself a parent: DCP runs beneath it, holding containers and an
// aspire-session-network-*. A row that ends any way other than "the child exited" leaves that
// host running, and the host keeps the CLI's own DLLs mapped. The developer sees nothing until
// the NEXT build fails with a file lock on a path that names no test.
//
// This file carries the two halves of the answer:
//
//   * ChildProcess.KillTreeQuietly  - the guarded tree-kill every child-process launch site in
//     this assembly calls from its `finally`. Sites, and what each was before:
//
//       ThreeRequirementsSuiteDockerTests.RunCliAsync            guarded already
//       KafkaAuthorisationDrillDockerTests.RunCliAsync           guarded already
//       KafkaSecurityConfirmationDrillDockerTests.RunCliAsync    guarded already
//       ExamplesCompileTests.RunSetupScriptAsync                 guarded on the timeout path only
//       Sprint11ReferenceCapstoneTests (the real CLI row)        NO kill on any path
//       ThreeRequirementsSuiteDockerTests.DockerAsync            NO kill on any path
//       KafkaAuthorisationDrillDockerTests.DockerAsync           NO kill on any path
//       KafkaSecurityConfirmationDrillDockerTests.DockerAsync    NO kill on any path
//
//     Four functionally identical copies of the same race guard collapsed into this one; the
//     other four sites had no unconditional kill at all. The last three swallow cancellation and
//     RETURN while their child runs, and one of them (DockerExecAsync) runs an arbitrary command
//     inside a container for an arbitrary time.
//
//   * DrillHostSweep + DrillHostSweepFixture - the sweep that runs before the drill lane and
//     again after it, clearing what an earlier session left behind and what this one leaks. It
//     mirrors the philosophy of the container leak check in
//     Vouchfx.Engine.Orchestration.Tests/TopologyTeardownLeakTests.cs: clean up always, and be
//     loud about a finding that the cleanup could not resolve.
//
// WHY THE DRILL LANE IS ALWAYS RUN WITH --blame-crash  (the canonical statement; the drill
// classes' own "Run with:" headers point here rather than restating it)
// ─────────────────────────────────────────────────────────────────────
// A spec-compliance review ran the docker drill classes four times. One of those four runs
// aborted with "Test host process crashed", after two rows had passed and before the next class
// started; the other three passed, 3/3 and 5/5 and 5/5. Twelve row-executions, zero test
// failures, one host crash.
//
// The number is a property of THE LANE, once in four runs of it — NOT of any one class, and not
// once per class. The crash was never attributed to a row, and the run it happened in was the
// only one without a dump utility attached, so no dump exists. That is the whole reason the
// flags are not optional: the next occurrence has to leave evidence, and the cost of forgetting
// is measured in multiples of a three-minute run.
//
// The scoping rule is absolute and is the reason the sweep is allowed to kill anything at all:
// a process is a candidate only if it has an image mapped underneath THIS repository's
// src/Cli/Vouchfx.Cli/bin. Nothing else in the tree is in scope, and in particular the sweep is
// deliberately NOT widened to the whole repository's bin output - the running test host has
// tests/**/bin modules mapped, so a repo-wide root would name the sweeper itself.
using System.Diagnostics;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>The guarded tree-kill shared by every child-process launch site in this assembly.</summary>
internal static class ChildProcess
{
    /// <summary>
    /// Kills <paramref name="process"/> and everything beneath it, swallowing the races and
    /// refusals that a kill can legitimately lose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>entireProcessTree: true</c> because a CLI child is itself a parent: DCP runs beneath it,
    /// and killing only the CLI would leave the orchestrator holding containers with nothing left
    /// to tear them down.
    /// </para>
    /// <para>
    /// The <c>HasExited</c> test cannot be made atomic with the kill, so the catch is the real
    /// guard rather than a courtesy. Callers invoke this from a <c>finally</c>, where ANY throw
    /// replaces the real failure with a teardown one - which is the misattribution issue #378 is
    /// about, arriving through the fix for it. The filter therefore covers every exception
    /// <c>Kill(bool)</c> documents, read from the .NET 8 reference XML rather than assumed:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="System.ComponentModel.Win32Exception"/> - "could not be terminated -or- the
    ///     process is terminating". This, NOT <c>InvalidOperationException</c>, is the
    ///     ended-between-the-check-and-the-kill case on this runtime.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="AggregateException"/> - "not all processes in the descendant tree could be
    ///     terminated". Reachable only through <c>entireProcessTree: true</c>, which is exactly how
    ///     every caller here invokes it, and a partial tree kill is not worth a thrown teardown.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="NotSupportedException"/> - a remote process.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="InvalidOperationException"/> - documented for .NET Framework and .NET Core
    ///     3.0 and earlier only, so it is NOT the exit race here. Kept because <c>HasExited</c>
    ///     itself raises it when no process is associated with the object.
    ///   </description></item>
    /// </list>
    /// </remarks>
    internal static void KillTreeQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException
                                       or AggregateException)
        {
            // Terminating, partially killed, remote, or no longer associated - see the remarks.
        }
    }
}

/// <summary>One process the sweep considered, reduced to the facts selection needs.</summary>
/// <param name="Pid">The process id.</param>
/// <param name="ProcessName">The process name, for the report only - never for selection.</param>
/// <param name="ImagePaths">
/// Every image this process has mapped that the sweep could read: the main module first, then
/// the loaded modules. Selection looks at these paths and at nothing else.
/// </param>
/// <param name="InspectionFailure">
/// Why the paths could not be read, or <see langword="null"/> when they could. A process whose
/// images are unreadable is SKIPPED and said so - never guessed at, and never killed.
/// </param>
/// <param name="StartTime">
/// When the process started, or <see langword="null"/> when that could not be read.
/// </param>
/// <remarks>
/// <para>
/// <strong><paramref name="StartTime"/> is half of the process's identity, not a diagnostic.</strong>
/// A pid alone is not unique over time: the operating system reuses it once the process is gone,
/// and this sweep decides on a snapshot taken before the kill. Between the two, the orphan can
/// exit and an unrelated process inherit its number - so the kill would land on whatever now holds
/// it. The canonical identity is the pair (pid, start time), which is why it is captured here and
/// re-compared immediately before the kill.
/// </para>
/// </remarks>
internal sealed record HostCandidate(
    int Pid,
    string ProcessName,
    IReadOnlyList<string> ImagePaths,
    string? InspectionFailure,
    DateTime? StartTime = null);

/// <summary>How an attempted kill ended.</summary>
internal enum KillResult
{
    /// <summary>The process is gone, confirmed.</summary>
    Confirmed,

    /// <summary>It is still running, or the kill was refused. The loud case.</summary>
    Failed,

    /// <summary>
    /// The pid no longer denotes the inspected process, so nothing was killed. Not a failure: the
    /// orphan is gone, which is what the sweep wanted.
    /// </summary>
    NotTheSameProcess,
}

/// <summary>The outcome of one kill, and the sentence explaining it.</summary>
/// <param name="Result">Which of the three things happened.</param>
/// <param name="Detail">
/// Why. <see langword="null"/> for an unqualified <see cref="KillResult.Confirmed"/>; a caveat
/// worth recording for a confirmed kill that was not clean - see
/// <see cref="ConfirmedWithSurvivingDescendants"/>.
/// </param>
internal sealed record KillOutcome(KillResult Result, string? Detail)
{
    /// <summary>The confirmed-dead outcome, with nothing left behind.</summary>
    internal static KillOutcome Confirmed { get; } = new(KillResult.Confirmed, Detail: null);

    /// <summary>
    /// The root is confirmed dead but the tree kill reported that not every descendant could be
    /// terminated.
    /// </summary>
    /// <remarks>
    /// Still a <see cref="KillResult.Confirmed"/>, because the question the sweep asks is whether
    /// this repository's build output has been released, and the root holding it is gone. But it
    /// is NOT the same event as a clean kill and must not be recorded as one: the survivor of a
    /// CLI host's tree is typically DCP, which holds containers and an aspire-session-network-*
    /// that now have nothing left to tear them down. A plain "killed" line would leave that
    /// invisible - the exact shape of silence issue #378 is about.
    /// </remarks>
    internal static KillOutcome ConfirmedWithSurvivingDescendants { get; } = new(
        KillResult.Confirmed,
        "root killed, but the tree kill reported that some descendants could not be terminated - "
        + "check for orphaned containers and networks");

    /// <summary>The process survived, or the kill was refused.</summary>
    internal static KillOutcome Failed(string detail) => new(KillResult.Failed, detail);

    /// <summary>The pid was reused between the inspection and the kill.</summary>
    internal static KillOutcome NotTheSameProcess(string detail) =>
        new(KillResult.NotTheSameProcess, detail);
}

/// <summary>What the sweep did, in the three shapes a caller has to tell apart.</summary>
/// <param name="Killed">
/// Orphans found and confirmed gone. A line here may carry a caveat when the tree kill left a
/// descendant behind - see <see cref="KillOutcome.ConfirmedWithSurvivingDescendants"/>.
/// </param>
/// <param name="Unkillable">Orphans found that survived the kill - the loud case.</param>
/// <param name="Skipped">
/// Everything the sweep saw but did not kill, with the reason. Three kinds land here, and only the
/// first is a candidate it could not judge: a process whose images were unreadable; a process
/// whose images were readable but empty; and an orphan it DID judge whose process id was reused
/// before the kill, so the process it would have killed was no longer the one it inspected. The
/// disabled-sweep note goes here too. None of these fails the lane.
/// </param>
internal sealed record SweepReport(
    IReadOnlyList<string> Killed,
    IReadOnlyList<string> Unkillable,
    IReadOnlyList<string> Skipped)
{
    /// <summary>Every line worth printing, in report order.</summary>
    internal IEnumerable<string> Lines => Killed.Concat(Unkillable).Concat(Skipped);
}

/// <summary>
/// Finds and clears CLI hosts left behind by an earlier drill session.
/// </summary>
internal static class DrillHostSweep
{
    /// <summary>Process names that can host the CLI - the only ones ever inspected.</summary>
    /// <remarks>
    /// A framework-dependent run is <c>dotnet vouchfx.dll</c> (process name "dotnet" on every
    /// platform); a published apphost run is <c>vouchfx</c>. Restricting enumeration to these two
    /// keeps the sweep cheap and keeps it from opening handles to unrelated processes. It is a
    /// narrowing only: the path containment test below is what makes a kill safe.
    /// </remarks>
    private static readonly string[] s_hostProcessNames = { "dotnet", "vouchfx" };

    /// <summary>How long a killed orphan is given to actually go away.</summary>
    private static readonly TimeSpan s_killGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The one directory a process may hold an image under and still be considered an orphan:
    /// this repository's CLI build output, both configurations.
    /// </summary>
    internal static string ResolveCliBinRoot()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(DrillHostSweep).Assembly.Location)!;
        var repoRoot = Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "..", "..", "..", ".."));
        return Path.GetFullPath(Path.Combine(repoRoot, "src", "Cli", "Vouchfx.Cli", "bin"));
    }

    /// <summary>
    /// Decides which candidates are orphans, kills them through <paramref name="kill"/>, and
    /// returns what happened.
    /// </summary>
    /// <param name="candidates">Everything enumerated.</param>
    /// <param name="cliBinRoot">The only in-scope directory - see <see cref="ResolveCliBinRoot"/>.</param>
    /// <param name="selfPid">This process, which is never a candidate for its own sweep.</param>
    /// <param name="kill">
    /// Attempts to end the candidate and reports which of three things happened - see
    /// <see cref="KillOutcome"/>. It is handed the whole <see cref="HostCandidate"/> rather than
    /// its pid because the pid alone is not an identity: the implementation has to re-compare the
    /// start time it was judged on before acting. It never returns <see langword="null"/>, and the
    /// distinction it draws between a failure and a pid that was reused is what keeps a vanished
    /// orphan out of the Unkillable list. Injected so the decision logic below is testable without
    /// starting or ending a process.
    /// </param>
    /// <remarks>
    /// Pure with respect to everything except <paramref name="kill"/>: which candidates are
    /// selected is a function of <paramref name="candidates"/>, <paramref name="cliBinRoot"/> and
    /// <paramref name="selfPid"/> alone, and nothing here reads the process table or the clock.
    /// That is what lets the guard be drilled rather than trusted.
    /// </remarks>
    internal static SweepReport Sweep(
        IReadOnlyList<HostCandidate> candidates,
        string cliBinRoot,
        int selfPid,
        Func<HostCandidate, KillOutcome> kill)
    {
        var killed = new List<string>();
        var unkillable = new List<string>();
        var skipped = new List<string>();
        var root = Path.GetFullPath(cliBinRoot);

        foreach (var candidate in candidates)
        {
            if (candidate.Pid == selfPid)
            {
                continue;
            }

            if (candidate.InspectionFailure is not null)
            {
                // Access denied to another user's process, or the process exited mid-inspection.
                // Either way the sweep does not know what it holds, so it says so and moves on.
                skipped.Add(
                    $"skipped pid {candidate.Pid} ({candidate.ProcessName}): "
                    + $"could not read its loaded images: {candidate.InspectionFailure}");
                continue;
            }

            if (candidate.ImagePaths.Count == 0)
            {
                // Inspection SUCCEEDED and returned nothing. Distinct from both neighbours, and
                // worth its own line: "no image path was readable" is a process the sweep could
                // not judge, whereas "none of its images matched" is a judgement. Reporting the
                // first as the second would let a platform that silently yields no module paths
                // read as a clean sweep for ever.
                skipped.Add(
                    $"skipped pid {candidate.Pid} ({candidate.ProcessName}): inspection returned "
                    + "no image path at all, so it could not be judged either way");
                continue;
            }

            var held = candidate.ImagePaths.FirstOrDefault(path => IsUnder(path, root));
            if (held is null)
            {
                // Inspected, judged, and not ours. The quiet case, and the overwhelmingly common
                // one - every unrelated `dotnet` on the machine lands here.
                continue;
            }

            var outcome = kill(candidate);
            switch (outcome.Result)
            {
                case KillResult.Confirmed:
                    // The caveat clause is appended rather than dropped: a confirmed kill whose
                    // tree was only partly terminated is a different event from a clean one, and
                    // the survivor is usually DCP holding containers.
                    killed.Add(
                        $"killed orphaned CLI host pid {candidate.Pid} ({candidate.ProcessName}) "
                        + $"holding {held}"
                        + (outcome.Detail is null ? string.Empty : $" ({outcome.Detail})"));
                    break;

                case KillResult.NotTheSameProcess:
                    // The orphan exited on its own between the inspection and the kill. Nothing
                    // was killed and nothing is wrong; it is reported so the line is not silence.
                    skipped.Add(
                        $"skipped pid {candidate.Pid} ({candidate.ProcessName}), which held "
                        + $"{held} when inspected: {outcome.Detail}");
                    break;

                default:
                    // "was NOT removed", not "could not be killed". Two different events land
                    // here: a kill that was attempted and failed, and a kill the sweep DECLINED to
                    // attempt because it could not verify the pid still denoted the process it had
                    // inspected. The detail says which; the prefix must not assert the first, or a
                    // declined kill reads as a stubborn process that does not exist.
                    unkillable.Add(
                        $"orphaned CLI host pid {candidate.Pid} ({candidate.ProcessName}) holding "
                        + $"{held} was NOT removed: {outcome.Detail}");
                    break;
            }
        }

        return new SweepReport(killed, unkillable, skipped);
    }

    /// <summary>
    /// Whether <paramref name="path"/> sits inside <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// The trailing separator is load-bearing rather than cosmetic. A bare
    /// <c>StartsWith(root)</c> would put <c>...\Vouchfx.Cli\bin-scratch\x.dll</c> inside
    /// <c>...\Vouchfx.Cli\bin</c>, and this predicate is the whole of what stops the sweep
    /// killing a process it has no business touching.
    /// </remarks>
    internal static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unparseable path is not evidence of anything; it is certainly not consent to kill.
            return false;
        }

        var normalisedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return full.StartsWith(normalisedRoot, comparison);
    }

    /// <summary>Runs the real sweep against the live process table.</summary>
    /// <remarks>
    /// Enumeration needs no elevation: <see cref="Process.MainModule"/> and
    /// <see cref="Process.Modules"/> are readable for the caller's own processes, which is where a
    /// leaked drill child always is. A process owned by somebody else raises and is recorded as a
    /// skip rather than as an absence.
    /// </remarks>
    internal static SweepReport SweepLiveProcesses()
    {
        var live = EnumerateLive();
        try
        {
            // TryAdd rather than ToDictionary: an inspection failure yields the placeholder pid -1
            // (see Describe), and two of those would make ToDictionary throw before the sweep ever
            // ran. Such a candidate is skipped by Sweep and so never reaches the lookup anyway.
            var byPid = new Dictionary<int, Process>();
            foreach (var pair in live)
            {
                byPid.TryAdd(pair.Candidate.Pid, pair.Process);
            }

            return Sweep(
                live.Select(pair => pair.Candidate).ToList(),
                ResolveCliBinRoot(),
                Environment.ProcessId,
                candidate => byPid.TryGetValue(candidate.Pid, out var process)
                    ? KillIfStillTheSameProcess(candidate, process)
                    : KillOutcome.Failed(
                        $"pid {candidate.Pid} was no longer in the enumerated set"));
        }
        finally
        {
            foreach (var pair in live)
            {
                pair.Process.Dispose();
            }
        }
    }

    /// <summary>
    /// Re-checks that the pid still denotes the process the sweep judged, then kills it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gap between deciding and killing is small but not zero, and a pid is not unique across
    /// it: the orphan can exit and the operating system hand its number to something new, which
    /// the kill would then land on. Nothing about the earlier judgement transfers to that process
    /// - it was never inspected, and its images were never shown to be under this repository's
    /// build output. A kill on it would be exactly the unscoped kill this guard promises never to
    /// make.
    /// </para>
    /// <para>
    /// <see cref="Process.StartTime"/> closes it: (pid, start time) is unique, because the reused
    /// pid necessarily belongs to a process that started later. A mismatch is reported as a skip
    /// rather than as a failure - the orphan is gone, which is the outcome the sweep wanted.
    /// </para>
    /// <para>
    /// A start time that cannot be re-read is also declined. The sweep kills on positive evidence
    /// of identity, never on the absence of evidence against it.
    /// </para>
    /// </remarks>
    private static KillOutcome KillIfStillTheSameProcess(HostCandidate candidate, Process process)
    {
        if (candidate.StartTime is not { } expected)
        {
            return KillOutcome.Failed(
                "its start time could not be read when it was inspected, so the pid could not be "
                + "confirmed to still denote the same process and it was NOT killed");
        }

        DateTime actual;
        try
        {
            actual = process.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            // Typically because it has already exited, which is the outcome the sweep wanted.
            return KillOutcome.NotTheSameProcess(
                $"its start time can no longer be read ({ex.GetType().Name}), which is what an "
                + "already-exited process looks like");
        }

        if (actual != expected)
        {
            return KillOutcome.NotTheSameProcess(
                $"the pid now denotes a DIFFERENT process (started {actual:O}, the inspected one "
                + $"started {expected:O}), so it was left alone");
        }

        return KillAndConfirm(process);
    }

    /// <summary>
    /// Kills a tree and CONFIRMS the root went, reporting which of <see cref="KillResult"/>'s
    /// outcomes occurred and the sentence that explains it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The confirmation is the point. An unconfirmed kill reported as a success is exactly the
    /// shape of failure this guard exists to remove: the build still breaks, and now a passing
    /// sweep line says it should not have.
    /// </para>
    /// <para>
    /// <strong>No path out of this method throws for any exception the calls inside it document as
    /// reachable here</strong>, and that is a contract rather than tidiness: its return value feeds
    /// the fixture's Unkillable list, so an escaping exception would error every row in the
    /// collection instead of reporting one process. That is why <see cref="AggregateException"/> -
    /// which <c>Kill(entireProcessTree: true)</c> documents for "not all processes in the
    /// descendant tree could be terminated" - is caught rather than left to propagate, and why it
    /// does NOT short-circuit to a failure: a partially-killed tree whose ROOT died has released
    /// this repository's build output, which is the whole question. The verdict is taken from the
    /// confirmation below in every case.
    /// </para>
    /// <para>
    /// <strong>The claim is scoped rather than absolute, and the gap is named.</strong>
    /// <c>WaitForExit(int)</c> documents a RAW <see cref="SystemException"/>, which the outer
    /// filter does not admit - it admits <see cref="InvalidOperationException"/>, a subclass, not
    /// the base. All three of its documented triggers are unreachable for the processes this class
    /// waits on: every one comes from <c>Process.GetProcessesByName</c>, so it always has a pid
    /// set, is always associated with a process, and is always local (the no-machine-name overload
    /// returns local processes only). It also documents
    /// <see cref="ArgumentOutOfRangeException"/> for a negative timeout, and the timeout here is a
    /// positive constant.
    /// </para>
    /// <para>
    /// The outer filter is deliberately NOT widened to <see cref="SystemException"/> to close that
    /// gap on paper, and the asymmetry with the nested catch below is the considered choice rather
    /// than an oversight. <see cref="SystemException"/> is the base of
    /// <see cref="NullReferenceException"/>, <see cref="InvalidCastException"/> and most other
    /// ordinary programming errors, so admitting it around this whole block would silently swallow
    /// a real defect anywhere in it and report the process as confirmed dead. The nested catch can
    /// afford the breadth because it wraps a SINGLE call and already sits on a failure path. A
    /// guard that lies about a kill is worse than one whose contract needs a sentence.
    /// </para>
    /// </remarks>
    private static KillOutcome KillAndConfirm(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return KillOutcome.Confirmed;
            }

            var descendantSurvived = false;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (AggregateException)
            {
                // Some descendant survived. Whether the ROOT died is decided by the wait below;
                // this flag only makes sure the record says the kill was not clean.
                descendantSurvived = true;
            }

            if (!process.WaitForExit((int)s_killGrace.TotalMilliseconds))
            {
                return KillOutcome.Failed($"still running {s_killGrace.TotalSeconds:F0}s after the kill");
            }

            return descendantSurvived
                ? KillOutcome.ConfirmedWithSurvivingDescendants
                : KillOutcome.Confirmed;
        }
        catch (InvalidOperationException)
        {
            // No process associated with the object any more - the outcome the caller wanted.
            // (On .NET 8 the exit race itself surfaces as Win32Exception, handled below.)
            return KillOutcome.Confirmed;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // "The process is terminating" arrives here too, so confirm rather than trust the
            // message: a process that is on its way out is a success, not a survivor.
            try
            {
                if (process.WaitForExit((int)s_killGrace.TotalMilliseconds))
                {
                    return KillOutcome.Confirmed;
                }
            }
            catch (Exception nested) when (nested is InvalidOperationException
                                               or System.ComponentModel.Win32Exception
                                               or SystemException)
            {
                // Fall through to the message below.
            }

            return KillOutcome.Failed(ex.Message);
        }
    }

    private static List<(Process Process, HostCandidate Candidate)> EnumerateLive()
    {
        var live = new List<(Process, HostCandidate)>();

        foreach (var name in s_hostProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                           or System.ComponentModel.Win32Exception)
            {
                continue;
            }

            foreach (var process in processes)
            {
                live.Add((process, Describe(process)));
            }
        }

        return live;
    }

    /// <summary>Reads one process's mapped images, or records why it could not.</summary>
    private static HostCandidate Describe(Process process)
    {
        int pid;
        string processName;
        try
        {
            pid = process.Id;
            processName = process.ProcessName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return new HostCandidate(-1, "unknown", Array.Empty<string>(), ex.Message);
        }

        // The other half of the process's identity, captured with the images it is being judged on
        // so the two can be re-compared before the kill. Its absence is not fatal here - it is
        // carried as null and refused at the kill, where declining is cheap and correct.
        DateTime? startTime;
        try
        {
            startTime = process.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            startTime = null;
        }

        var paths = new List<string>();
        try
        {
            var main = process.MainModule?.FileName;
            if (!string.IsNullOrEmpty(main))
            {
                paths.Add(main);
            }

            foreach (ProcessModule module in process.Modules)
            {
                var fileName = module.FileName;
                if (!string.IsNullOrEmpty(fileName))
                {
                    paths.Add(fileName);
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       or InvalidOperationException
                                       or NotSupportedException)
        {
            // Access denied (another user's process), a bitness mismatch, or an exit mid-read.
            return new HostCandidate(
                pid, processName, Array.Empty<string>(), ex.Message, startTime);
        }

        return new HostCandidate(pid, processName, paths, InspectionFailure: null, startTime);
    }
}

/// <summary>
/// Sweeps orphaned CLI hosts before the first row of the drill lane runs, and again after the
/// last one.
/// </summary>
/// <remarks>
/// <para>
/// A collection fixture rather than a per-class one: xUnit builds it exactly once, immediately
/// before the collection's first test, which is the only hook inside <c>dotnet test</c> that
/// precedes the lane as a whole - and disposes it after the last, which is the only one that
/// follows it.
/// </para>
/// <para>
/// <strong>Both ends, because the entry sweep alone leaves the session that leaks paying nothing
/// and the next one paying everything.</strong> A lane that wedges a CLI child would otherwise
/// break its OWN next build - the developer's next <c>dotnet build</c>, minutes later - and the
/// entry sweep would only clear it on the next DRILL run, which may be days away and on a
/// different branch. Sweeping on the way out closes that window, and it is where the unconditional
/// tree-kills in the launch sites should already have left nothing to find: a non-empty exit sweep
/// means one of them was bypassed, which is worth knowing.
/// </para>
/// <para>
/// Disposal is best-effort and never throws. xUnit reports a fixture disposal failure against the
/// collection, so an exit sweep that reddened the lane would attribute a teardown observation to
/// whichever tests happened to be in it - the misattribution this whole guard exists to end. The
/// exit sweep therefore records and stays quiet; the ENTRY sweep is the one that can be loud,
/// because there the finding genuinely precedes and endangers the work about to run.
/// </para>
/// <para>
/// <strong>It kills rather than fails, and that asymmetry with the container leak check is
/// deliberate.</strong> That check fails because the leak it finds is the defect under test - the
/// run that leaked is the run being judged. An orphan found HERE belongs to an earlier session,
/// so failing today's lane for it would repeat the very misattribution issue #378 is about. What
/// the two share is the part that matters: clean up unconditionally, name what was cleaned, and
/// be loud when the cleanup did not work. An orphan that survives the kill WILL break the next
/// build, so that case throws - every row in the collection then errors carrying the pid and the
/// path, which is the only signal strong enough to be acted on.
/// </para>
/// <para>
/// Concurrent drill sessions are out of scope by construction: this assembly disables test
/// parallelism, <c>dotnet test</c> runs one host per project, and two simultaneous drill lanes
/// would collide on container names and host ports long before this sweep mattered.
/// </para>
/// </remarks>
public sealed class DrillHostSweepFixture : IDisposable
{
    /// <summary>The environment variable that turns the sweep off.</summary>
    /// <remarks>
    /// The sweep kills processes, so it has to be possible to decline it without editing code -
    /// for a developer deliberately holding a CLI host under a debugger, say. Default ON: the
    /// failure it prevents is invisible until the next build, so opting IN would leave the trap
    /// armed for everyone who has not read about it.
    /// </remarks>
    internal const string OptOutVariable = "VOUCHFX_DRILL_SWEEP";

    /// <summary>Runs the sweep against the live process table, unless it has been turned off.</summary>
    /// <remarks>
    /// The only constructor xUnit ever calls, and the only one that reaches the live process table.
    /// Both of its arguments to the seam below are the production defaults.
    /// </remarks>
    public DrillHostSweepFixture()
        : this(SweepUnlessDisabled(), recordPath: null, sweep: SweepUnlessDisabled)
    {
    }

    /// <summary>
    /// Where a sweep that found something writes its record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file rather than the console, and that is a MEASURED choice rather than a preference.
    /// A fixture constructor runs outside any test, so its <c>Console</c> output has no
    /// <c>ITestOutputHelper</c> to attach to; measured on this repository, a sweep line written to
    /// <c>Console.Out</c> from here does NOT appear in <c>dotnet test</c>'s output at default
    /// verbosity. It is still written - it costs nothing and shows up under a detailed console
    /// logger - but the file is what makes the record dependable.
    /// </para>
    /// <para>
    /// <strong>Under the per-user local application data directory, not the temp directory.</strong>
    /// The record names process ids and absolute paths from this repository. On a shared host the
    /// temp directory is world-readable and world-writable, so a filename there can be pre-created
    /// by another user - as a symlink, or simply to be read afterwards. The per-user directory is
    /// neither. It is also outside the repository, which the guard's own tests assert: a sweep
    /// artefact is not a file the change intends to ship, and this repository's .gitignore would
    /// hide it rather than flag it.
    /// </para>
    /// </remarks>
    internal static string ReportPath { get; } = ResolveReportPath();

    /// <summary>
    /// The record's absolute path: the per-user directory, or the temp directory if the platform
    /// has no per-user one.
    /// </summary>
    /// <remarks>
    /// <strong>The fallback is not defensive padding.</strong>
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> returns the EMPTY STRING
    /// rather than throwing when a folder is not defined for the platform - and
    /// <see cref="Path.Combine(string, string, string)"/> on an empty first segment yields the
    /// RELATIVE path <c>vouchfx/drill-host-sweep.log</c>, which resolves against the current
    /// directory. Under <c>dotnet test</c> that is inside this repository, so the guard would
    /// silently write its record into the tree it exists to keep clean, where .gitignore's
    /// blacklist would hide it rather than flag it. An absolute path is asserted by the drills;
    /// this is what makes the assertion hold on every platform rather than on this one.
    /// </remarks>
    private static string ResolveReportPath()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        var root = string.IsNullOrEmpty(localAppData) ? Path.GetTempPath() : localAppData;
        return Path.GetFullPath(Path.Combine(root, "vouchfx", "drill-host-sweep.log"));
    }

    /// <summary>
    /// The production sweep: the live process table, unless the opt-out turned it off - in which
    /// case no process is inspected or killed and a line says so.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so one drill can pin it as the default of the seam below. That
    /// assertion is what stops the seam quietly becoming a permanent no-op, which would disarm the
    /// guard in production while every test stayed green.
    /// </remarks>
    internal static SweepReport SweepUnlessDisabled()
    {
        var setting = Environment.GetEnvironmentVariable(OptOutVariable);
        if (!IsDisabledBy(setting))
        {
            return DrillHostSweep.SweepLiveProcesses();
        }

        return new SweepReport(
            Killed: Array.Empty<string>(),
            Unkillable: Array.Empty<string>(),
            Skipped: new[]
            {
                $"sweep DISABLED by {OptOutVariable}={setting}: no process was inspected or killed. "
                + "A CLI host left by an earlier drill session will still hold this repository's "
                + "build output and break the next build.",
            });
    }

    /// <summary>
    /// Whether <paramref name="value"/> turns the sweep off. Only the exact string <c>0</c> does.
    /// </summary>
    /// <remarks>
    /// Deliberately not a general truthiness test. A guard that kills processes should be switched
    /// off by an unmistakable value and by nothing else - an unset variable, an empty one, or a
    /// typo all leave it armed.
    /// </remarks>
    internal static bool IsDisabledBy(string? value) =>
        string.Equals(value, "0", StringComparison.Ordinal);

    /// <summary>Seam for the guard's own tests - takes a report instead of producing one.</summary>
    /// <param name="report">What the entry sweep found.</param>
    /// <param name="recordPath">
    /// Where to append. The guard's own tests pass a scratch path of their own: a drill must never
    /// write into <see cref="ReportPath"/>, whose entire value is that a line in it is a real
    /// finding rather than a fabricated one.
    /// </param>
    /// <param name="sweep">
    /// What <see cref="Dispose"/> runs. Defaults to <see cref="SweepUnlessDisabled"/>, so the
    /// production path is unchanged.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong><paramref name="sweep"/> exists because its absence was a measured defect, and the
    /// shape of that defect is worth stating.</strong> The entry sweep was already injectable - a
    /// drill hands in a fabricated <paramref name="report"/> and no process is touched. Disposal
    /// was not: it called the live sweep directly, so the guard's OWN drills, which are untraited
    /// and therefore run in the fast <c>requires!=docker</c> lane, killed real processes there.
    /// </para>
    /// <para>
    /// That is precisely the outcome the drill classes were split apart to prevent (see
    /// KafkaSecurityConfirmationPreflightTests), reached a second time through a different door.
    /// A guard that kills has to be un-runnable by accident on EVERY path, not on the paths its
    /// author was thinking about - which is why the standing census in the guard's own drills now
    /// pins the call sites of <c>SweepLiveProcesses</c> rather than trusting this comment.
    /// </para>
    /// </remarks>
    internal DrillHostSweepFixture(
        SweepReport report, string? recordPath, Func<SweepReport>? sweep = null)
    {
        Report = report;
        _recordPath = recordPath;
        _sweep = sweep ?? SweepUnlessDisabled;
        Announce(report, recordPath, "entry");

        if (report.Unkillable.Count > 0)
        {
            // "was not removed" rather than "could not be killed": NOT every entry here is a
            // failed kill. One of them is a kill the sweep DECLINED to attempt, because it could
            // not read the process's start time and so could not confirm the pid still denoted the
            // process it had inspected. Naming that as a failed kill would send the reader looking
            // for a stubborn process when the truth is an unverifiable one. The per-line reason
            // below says which happened; this sentence must not contradict it.
            throw new InvalidOperationException(
                "A CLI host left behind by an earlier drill session is still running and was not "
                + "removed - either the kill failed, or the sweep declined to attempt it because "
                + "the process's identity could not be verified. It holds this repository's CLI "
                + "build output open, so the next build will fail with a file lock naming no test. "
                + "End the process listed below by hand (its whole tree - a CLI host has DCP "
                + "beneath it) and re-run:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, report.Unkillable));
        }
    }

    /// <summary>What the entry sweep found, for a caller that wants to assert on it.</summary>
    internal SweepReport Report { get; }

    /// <summary>Where this instance records, carried so disposal writes to the same place.</summary>
    private readonly string? _recordPath;

    /// <summary>What <see cref="Dispose"/> runs - the seam that keeps the fast lane safe.</summary>
    private readonly Func<SweepReport> _sweep;

    /// <summary>
    /// The delegate disposal will run, exposed so one drill can pin its production default.
    /// </summary>
    internal Func<SweepReport> SweepDelegate => _sweep;

    /// <summary>What the exit sweep found. Null until this fixture has been disposed.</summary>
    internal SweepReport? ExitReport { get; private set; }

    /// <summary>
    /// Sweeps once more, after the lane's last row, and records whatever it finds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never throws - see the class remarks. A finding here is a real defect (a launch site that
    /// bypassed its <c>finally</c>), but it is a defect in the code under test's HARNESS, observed
    /// after every verdict is already decided. Turning it into a collection-level error would
    /// repaint a set of passing rows as failures without changing what they measured.
    /// </para>
    /// <para>
    /// The opt-out is honoured inside <see cref="SweepUnlessDisabled"/>, so it means what it says
    /// on both ends of the lane.
    /// </para>
    /// <para>
    /// <strong>It runs the injected delegate, never the live sweep directly.</strong> That is not
    /// indirection for its own sake: a hard call here made the guard's own untraited drills kill
    /// real processes in the fast lane, which is the defect this seam exists to make unreachable.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (ExitReport is not null)
        {
            return;
        }

        try
        {
            ExitReport = _sweep();
            Announce(ExitReport, _recordPath, "exit");
        }
        // DELIBERATELY TOTAL, and the contrast with KillAndConfirm's narrow filter is the point.
        //
        // There the filter stays narrow because its result feeds a THROW decision taken before any
        // test runs: a masked defect would become a false "confirmed dead", and the lane would then
        // proceed on a lie. Here every verdict is already decided. An exception escaping disposal
        // cannot inform anything - xUnit attributes a fixture disposal failure to the COLLECTION,
        // so its only possible effect is to repaint a set of passing rows as failures, which is the
        // misattribution this whole change exists to end.
        //
        // Nothing is lost by catching everything, because nothing is swallowed: the exception's
        // message becomes a line in the exit report and in the record. A guard defect is demoted
        // from "reddens unrelated rows" to "is written down", which is the right trade at teardown
        // and the wrong one before the run.
        //
        // It also makes the never-throws contract absolute rather than conditional on the injected
        // delegate's exception types - a stub that throws ArgumentException is now covered.
        catch (Exception ex)
        {
            ExitReport = new SweepReport(
                Array.Empty<string>(),
                Array.Empty<string>(),
                new[] { "exit sweep could not run: " + ex.Message });
        }
    }

    /// <summary>Writes a report to both channels, tagged with which end of the lane produced it.</summary>
    private static void Announce(SweepReport report, string? recordPath, string phase)
    {
        foreach (var line in report.Lines)
        {
            Console.Out.WriteLine($"[drill-host-sweep {phase}] " + line);
        }

        Record(report, recordPath, phase);
    }

    /// <summary>
    /// Appends a sweep that found something to <see cref="ReportPath"/>, and never throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sweep that observed nothing writes nothing: an append-only record of pure non-events would
    /// bury the lines anybody ever goes looking for.
    /// </para>
    /// <para>
    /// <strong>A quiet run is not necessarily an empty one.</strong> A skip is an observation, so
    /// it is recorded, and how often one occurs is a property of the HOST rather than of the sweep:
    /// measured at zero across two live sweeps on one machine where all twenty <c>dotnet</c>
    /// processes were inspectable, and at one per sweep on another. A host running <c>dotnet</c>
    /// under another account produces them; a single-user one may never. Neither is a fault. The
    /// lines worth searching for are the two that name a finding, <c>killed</c> and <c>was NOT
    /// removed</c>; grep for those, not for the file being short.
    /// </para>
    /// <para>
    /// Recording is best-effort by design - failing the drill lane because a log file was
    /// unwritable would be a worse outcome than the missing line, and the unkillable case carries
    /// its own signal through an exception rather than through this file.
    /// </para>
    /// </remarks>
    /// <param name="report">What the sweep found.</param>
    /// <param name="path">
    /// Where to append. Defaults to <see cref="ReportPath"/>; the guard's own tests pass a path of
    /// their own so a drill never writes into the record a developer reads.
    /// </param>
    /// <param name="phase">
    /// Which end of the lane produced this - <c>entry</c> or <c>exit</c>. Recorded because the two
    /// mean different things: an entry finding is an earlier session's residue, an exit finding is
    /// a launch site in THIS run that did not kill its child.
    /// </param>
    internal static void Record(SweepReport report, string? path = null, string phase = "entry")
    {
        var lines = report.Lines.ToList();
        if (lines.Count == 0)
        {
            return;
        }

        try
        {
            var target = path ?? ReportPath;
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stamp = DateTime.UtcNow.ToString(
                "yyyy-MM-dd HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
            File.AppendAllLines(
                target,
                lines.Select(line => $"{stamp} pid {Environment.ProcessId} [{phase}] {line}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException
                                       or NotSupportedException
                                       or ArgumentException)
        {
            // Best-effort - see the remarks.
        }
    }
}

/// <summary>The collection every drill class that launches the CLI belongs to.</summary>
[CollectionDefinition(DrillHostSweepCollectionDefinition.Name)]
public sealed class DrillHostSweepCollectionDefinition : ICollectionFixture<DrillHostSweepFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "vouchfx-cli-drill";
}
