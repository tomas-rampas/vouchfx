// Vouchfx.Cli.Tests — CancelStageRecorder: stage timestamps for the cancel path of SystemProcessRunner.
//
// Diagnostic scaffolding for one intermittent failure (see Vouchfx.Cli.Selection.CancelStage): the
// cancellation row once took longer than its ten-second slack to see Run return, and nothing said
// which stage of the unwind took the time. The runner reports its own stages through the hook this
// class's Record method is passed as; the test stamps the stages only it can see (Signalled,
// SignalReturned, RunReturned, and ChildExited from its own process-death poll). It records instants,
// thread-pool samples and one exception TYPE name — never a command line, an argument, a path, an
// environment value or an exception message.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Vouchfx.Cli.Selection;

namespace Vouchfx.Cli.Tests;

/// <summary>
/// Records a first-write-wins timestamp and thread-pool sample per <see cref="CancelStage"/>,
/// thread-safely, and renders them as a single diagnostic line.
/// </summary>
internal sealed class CancelStageRecorder
{
    /// <summary>
    /// The gap between two stages from which <see cref="Describe"/> prints the thread-pool deltas
    /// across it; a shorter gap prints its timing alone, so a healthy timeline stays short.
    /// </summary>
    /// <remarks>
    /// MEASURED on macOS from the timeline the ordering row prints on every run: each gap of a
    /// healthy unwind under a millisecond except KillIssued to KillReturned, 32 to 36 ms. The
    /// failure being diagnosed is a gap of seconds. A quarter of a second sits about seven times
    /// above the slowest healthy gap seen and well under the failure; Windows' tree-kill, which
    /// enumerates processes, was not measured here and may sit closer.
    /// </remarks>
    internal static readonly TimeSpan LongGap = TimeSpan.FromMilliseconds(250);

    private static readonly int StageCount = Enum.GetValues<CancelStage>().Length;

    // Stages whose absence is not evidence of a stall. ChildExited is stamped only when the test's
    // own poll sees the child gone, and ReadersDrained never arrives while something outside the
    // killed tree still holds a pipe, so a missing one reads "not observed" rather than
    // "NOT REACHED".
    private static readonly CancelStage[] Observational = { CancelStage.ChildExited, CancelStage.ReadersDrained };

    // 0 means "not reached". Stopwatch.GetTimestamp() is positive on every supported platform; a
    // zero reading is nudged to 1 so the sentinel stays unambiguous.
    private readonly long[] _timestamps = new long[StageCount];

    // The pool sample taken by the call that won the stage's timestamp, so it is first-write-wins
    // too. Written just after the timestamp, so a report racing a stamp can find it missing.
    private readonly PoolSample?[] _samples = new PoolSample?[StageCount];

    // The earliest instant the child could have exited of its own accord, or null when not told.
    private readonly long? _childCannotExitBefore;

    private string? _killSwallowed;

    /// <summary>Initialises a recorder that knows nothing about the child's own lifetime.</summary>
    internal CancelStageRecorder()
        : this(childLifetime: null)
    {
    }

    /// <summary>
    /// Initialises a recorder for a child that is launched AFTER this call and does not exit of
    /// its own accord before <paramref name="childLifetime"/> has passed.
    /// </summary>
    /// <param name="childLifetime">
    /// The child's own lifetime, or <see langword="null"/> if unknown. The child starts after this
    /// constructor returns and lives at least this long, so a <see cref="CancelStage.ChildExited"/>
    /// stamped this long after construction or later may be the child expiring by itself, and
    /// <see cref="Describe"/> says so instead of presenting it as the kill landing.
    /// </param>
    internal CancelStageRecorder(TimeSpan? childLifetime)
    {
        if (childLifetime is { } lifetime)
        {
            _childCannotExitBefore = Stopwatch.GetTimestamp() + (long)(lifetime.TotalSeconds * Stopwatch.Frequency);
        }
    }

    /// <summary>
    /// The hook handed to the runner: stamps <paramref name="stage"/> now, samples the thread pool,
    /// and keeps the <see cref="CancelStage.KillReturned"/> detail (a swallowed exception's type
    /// name).
    /// </summary>
    internal void Record(CancelStage stage, string? detail)
    {
        var now = Stopwatch.GetTimestamp();
        if (now == 0)
        {
            now = 1;
        }

        if (Interlocked.CompareExchange(ref _timestamps[(int)stage], now, 0) != 0)
        {
            return;
        }

        // A sample costs four counter reads and one process-times call. It is what separates "this
        // stage's own work was slow" from "nothing was free to run it" from "the whole host stood
        // still", for whichever gap turns out to be the long one.
        Volatile.Write(ref _samples[(int)stage], PoolSample.Take());

        if (stage == CancelStage.KillReturned && detail is not null)
        {
            Volatile.Write(ref _killSwallowed, detail);
        }
    }

    /// <summary>Stamps a stage only the caller can see; a second stamp of the same stage is ignored.</summary>
    internal void Mark(CancelStage stage) => Record(stage, detail: null);

    /// <summary>The type name of the exception the tree-kill guard swallowed, if it swallowed one.</summary>
    internal string? KillSwallowedType => Volatile.Read(ref _killSwallowed);

    /// <summary>The raw timestamp of <paramref name="stage"/>, or <see langword="null"/> if not reached.</summary>
    internal long? TimestampOf(CancelStage stage)
    {
        var value = Volatile.Read(ref _timestamps[(int)stage]);
        return value == 0 ? null : value;
    }

    /// <summary>Whether <paramref name="stage"/> has been stamped.</summary>
    internal bool Reached(CancelStage stage) => TimestampOf(stage) is not null;

    /// <summary>
    /// Renders every stage stamped, in chronological order with the gap since the previous one
    /// and, for a gap of <see cref="LongGap"/> or more, the thread-pool deltas across it; then the
    /// stages not stamped, the pool at the signal, and how to read the deltas. A report taken
    /// while <c>Run</c> is still outstanding also covers the span since the last stage.
    /// </summary>
    /// <remarks>
    /// One line, because it is spliced into an assertion message. Bounded in length by
    /// construction: one entry per <see cref="CancelStage"/> member, fixed-format numbers, and the
    /// reading guide at most once.
    /// </remarks>
    internal string Describe()
    {
        var report = Stopwatch.GetTimestamp();
        var reached = new List<(CancelStage Stage, long At)>();
        var notReached = new List<CancelStage>();
        var notObserved = new List<CancelStage>();
        foreach (var stage in Enum.GetValues<CancelStage>())
        {
            if (TimestampOf(stage) is { } at)
            {
                reached.Add((stage, at));
            }
            else if (Observational.Contains(stage))
            {
                notObserved.Add(stage);
            }
            else
            {
                notReached.Add(stage);
            }
        }

        reached.Sort(static (left, right) => left.At.CompareTo(right.At));

        var text = new StringBuilder("Cancel-path stage timings (ms; '+' is the gap since the previous stage, 't=' is since the token was signalled; a gap of ");
        text.Append(CultureInfo.InvariantCulture, $"{LongGap.TotalMilliseconds:F0} ms or more carries the thread-pool change across it in brackets): ");
        var origin = TimestampOf(CancelStage.Signalled);
        (CancelStage Stage, long At)? previous = null;
        var anyDeltas = false;
        foreach (var (stage, at) in reached)
        {
            var gap = previous is { } p ? Stopwatch.GetElapsedTime(p.At, at) : TimeSpan.Zero;
            var sinceSignal = origin is { } o ? Stopwatch.GetElapsedTime(o, at).TotalMilliseconds : double.NaN;
            text.Append(CultureInfo.InvariantCulture, $"{stage} +{gap.TotalMilliseconds:F1} (t={sinceSignal:F1})");
            if (previous is { } from && gap >= LongGap)
            {
                text.Append(' ').Append(Deltas(SampleOf(from.Stage), SampleOf(stage)));
                anyDeltas = true;
            }

            if (stage == CancelStage.ChildExited && _childCannotExitBefore is { } floor && at >= floor)
            {
                text.Append(" [observed after the child's own lifetime could have ended, so it does not show that the kill landed]");
            }

            text.Append("; ");
            previous = (stage, at);
        }

        var reportedAt = origin is { } o2 ? Stopwatch.GetElapsedTime(o2, report).TotalMilliseconds : double.NaN;
        if (notReached.Count > 0)
        {
            // The open span: whatever the runner was doing, or waiting for, when this was taken.
            if (previous is { } last && Stopwatch.GetElapsedTime(last.At, report) >= LongGap)
            {
                text.Append(CultureInfo.InvariantCulture, $"still waiting {Stopwatch.GetElapsedTime(last.At, report).TotalMilliseconds:F1} after {last.Stage} at this report ");
                text.Append(Deltas(SampleOf(last.Stage), PoolSample.Take())).Append("; ");
                anyDeltas = true;
            }

            text.Append(CultureInfo.InvariantCulture, $"NOT REACHED as of t={reportedAt:F1}: {string.Join(", ", notReached)}; ");
        }

        if (notObserved.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"not observed as of t={reportedAt:F1} (the test's death poll had not seen the child gone, or the reads had not settled; neither says the runner stalled): {string.Join(", ", notObserved)}; ");
        }

        text.Append(CultureInfo.InvariantCulture, $"kill swallowed: {KillSwallowedType ?? "nothing"}; ");
        text.Append(SampleOf(CancelStage.Signalled) is { } atSignal
            ? string.Create(CultureInfo.InvariantCulture, $"thread pool at signal: {atSignal}.")
            : "thread pool: not sampled at a signal (the token was never signalled).");

        if (anyDeltas)
        {
            text.Append(' ').Append(ReadingGuide);
        }

        return text.ToString();
    }

    // MEASURED, 2026-11-21, .NET 10 on an 18-CPU macOS host (ThreadPool minimum 18). A standalone
    // probe, one work item queued behind each load and timed from a sample taken just before it:
    //   busy    — 72 CPU-bound items of 400 ms: 1.6 s gap, +57 completed, +26.4 s CPU (about 92%
    //             of 18 CPUs), +0 threads;
    //   blocked — 40 / 60 items in synchronous pipe reads: 12.6 s / 25.2 s gap, +0 completed,
    //             +27 / +38 ms CPU (under 0.1%), +24 / +44 threads (1.90 / 1.75 a second);
    //   stalled — the process stopped by SIGSTOP for 5 s, pool idle: 5.0 s gap, +0 completed,
    //             +19 ms CPU, +0 threads.
    // And row 5 itself in a test host whose pool was held 10 items deep in 20 s sleeps: Signalled to
    // Observed 6.1 s, +10 threads (1.64 a second), +14 completed, CPU about 2% of 18 CPUs (the load
    // harness polled a directory every 5 ms throughout, which is CPU of its own).
    // The injection rate is the runtime's starvation heuristic at work, not a constant of it, so the
    // guide quotes it as this host's figure.
    private const string ReadingGuide =
        "Reading the brackets: work items completing mean the pool was running other work ahead of whatever waited, a busy pool, and the CPU share says whether that work computed (near 100%) or mostly waited (a few percent). "
        + "Threads growing while few work items complete and the CPU share stays low mean pool threads blocked in waits, which is starvation too: "
        + "above its minimum the pool adds threads slowly (measured at 1.6 to 1.9 a second on an 18-CPU macOS host with .NET 10, which is the runtime's thread-injection behaviour on that host, not a constant). "
        + "Threads, work items and CPU all standing still mean the host itself stalled, because the pool's own thread injector cannot run either. "
        + "A non-zero pending count is work queued with no pool thread free to take it, so anything queued at that moment, the runner's continuation included, waited behind it. "
        + "CPU time and work items are process-wide and include other tests running alongside.";

    private PoolSample? SampleOf(CancelStage stage) => Volatile.Read(ref _samples[(int)stage]);

    private static string Deltas(PoolSample? from, PoolSample? to)
    {
        if (from is null || to is null)
        {
            return "[no pool sample at one end]";
        }

        // The CPU time as a share of what every CPU could have spent across the same span, so a
        // pool computing flat out reads near 100% and one whose threads wait reads near 0%.
        var cpu = (to.CpuTime - from.CpuTime).TotalMilliseconds;
        var capacity = Stopwatch.GetElapsedTime(from.At, to.At).TotalMilliseconds * Environment.ProcessorCount;
        var share = capacity > 0 ? 100 * cpu / capacity : double.NaN;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[CPU +{cpu:F1} ms ({share:F1}% of {Environment.ProcessorCount} CPUs), work items completed +{to.Completed - from.Completed}, threads {to.Threads - from.Threads:+0;-0;+0} ({from.Threads} to {to.Threads}), pending {from.Pending} to {to.Pending}]");
    }

    private sealed record PoolSample(long At, int Threads, long Pending, long Completed, TimeSpan CpuTime)
    {
        internal static PoolSample Take() =>
            new(
                Stopwatch.GetTimestamp(),
                ThreadPool.ThreadCount,
                ThreadPool.PendingWorkItemCount,
                ThreadPool.CompletedWorkItemCount,
                Environment.CpuUsage.TotalTime);

        public override string ToString() =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"threads={Threads} pending={Pending} completed={Completed} cpu={CpuTime.TotalMilliseconds:F1}ms cpus={Environment.ProcessorCount}");
    }
}
