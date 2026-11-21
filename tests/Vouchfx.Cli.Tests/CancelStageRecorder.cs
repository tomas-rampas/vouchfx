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
/// Records a first-write-wins timestamp per <see cref="CancelStage"/>, thread-safely, and renders
/// them as a single diagnostic line.
/// </summary>
internal sealed class CancelStageRecorder
{
    private static readonly int StageCount = Enum.GetValues<CancelStage>().Length;

    // Stages whose absence is not evidence of a stall. ChildExited is stamped only when the test's
    // own poll sees the child gone, and ReadersDrained never arrives while something outside the
    // killed tree still holds a pipe, so a missing one reads "not observed" rather than
    // "NOT REACHED".
    private static readonly CancelStage[] Observational = { CancelStage.ChildExited, CancelStage.ReadersDrained };

    // 0 means "not reached". Stopwatch.GetTimestamp() is positive on every supported platform; a
    // zero reading is nudged to 1 so the sentinel stays unambiguous.
    private readonly long[] _timestamps = new long[StageCount];

    private string? _killSwallowed;
    private PoolSample? _atSignalled;
    private PoolSample? _atObserved;

    /// <summary>
    /// The hook handed to the runner: stamps <paramref name="stage"/> now, and keeps the
    /// <see cref="CancelStage.KillReturned"/> detail (a swallowed exception's type name).
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

        // The pool samples are what separate "the kill was slow" from "nothing was free to run the
        // continuation" from "the whole host stood still", so they are taken at the two stages
        // whose gap that distinction explains.
        switch (stage)
        {
            case CancelStage.KillReturned when detail is not null:
                Volatile.Write(ref _killSwallowed, detail);
                break;
            case CancelStage.Signalled:
                Volatile.Write(ref _atSignalled, PoolSample.Take());
                break;
            case CancelStage.Observed:
                Volatile.Write(ref _atObserved, PoolSample.Take());
                break;
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
    /// Renders every stage stamped, in chronological order with the gap since the previous one,
    /// then the stages not stamped, then the thread-pool samples, so a report taken while
    /// <c>Run</c> is still outstanding still says how far it got and why it might have stopped.
    /// </summary>
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

        var text = new StringBuilder("Cancel-path stage timings (ms; '+' is the gap since the previous stage, 't=' is since the token was signalled): ");
        var origin = TimestampOf(CancelStage.Signalled);
        long? previous = null;
        foreach (var (stage, at) in reached)
        {
            var gap = previous is { } p ? Stopwatch.GetElapsedTime(p, at).TotalMilliseconds : 0;
            var sinceSignal = origin is { } o ? Stopwatch.GetElapsedTime(o, at).TotalMilliseconds : double.NaN;
            text.Append(CultureInfo.InvariantCulture, $"{stage} +{gap:F1} (t={sinceSignal:F1}); ");
            previous = at;
        }

        var reportedAt = origin is { } o2 ? Stopwatch.GetElapsedTime(o2, report).TotalMilliseconds : double.NaN;
        if (notReached.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"NOT REACHED as of t={reportedAt:F1}: {string.Join(", ", notReached)}; ");
        }

        if (notObserved.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"not observed as of t={reportedAt:F1} (the test's death poll had not seen the child gone, or the reads had not settled; neither says the runner stalled): {string.Join(", ", notObserved)}; ");
        }

        text.Append(CultureInfo.InvariantCulture, $"kill swallowed: {KillSwallowedType ?? "nothing"}; ");
        text.Append(DescribePool());
        return text.ToString();
    }

    private string DescribePool()
    {
        var atSignalled = Volatile.Read(ref _atSignalled);
        if (atSignalled is null)
        {
            return "thread pool: not sampled (the token was never signalled).";
        }

        // When the runner never observed the cancellation, the second sample is taken now, so the
        // deltas still cover the span in which it failed to.
        var atObserved = Volatile.Read(ref _atObserved);
        var label = atObserved is null ? "at this report (Observed not reached)" : "at observe";
        var second = atObserved ?? PoolSample.Take();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"thread pool at signal: {atSignalled}; {label}: {second}; between them: {Stopwatch.GetElapsedTime(atSignalled.At, second.At).TotalMilliseconds:F1} ms of wall clock, {second.Completed - atSignalled.Completed} work items completed, {(second.CpuTime - atSignalled.CpuTime).TotalMilliseconds:F1} ms of process CPU time. Work items completing and CPU time advancing across a long gap mean the pool was busy (starvation); neither moving means the host itself stalled.");
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
