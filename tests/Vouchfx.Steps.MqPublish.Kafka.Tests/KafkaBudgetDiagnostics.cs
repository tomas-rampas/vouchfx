using System.Diagnostics;
using System.Globalization;

namespace Vouchfx.Steps.MqPublish.Kafka.Tests;

// Test-only observer: it never changes the emitted script, its token, outcome or teardown.
internal sealed class KafkaBudgetDiagnostics : IDisposable
{
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly Stopwatch _phase = new();
    private readonly CancellationTokenSource _sampleTimer = new();
    private CancellationTokenRegistration _sampleRegistration;
    private long _sampleDeliveredTicks;
    private long _sampleStartedTicks;
    private long _compileMs;
    private long _invokeMs;
    private long _pendingBefore;
    private long _pendingAfter;
    private int _availableBefore;
    private int _availableAfter;

    internal void BeginCompile() => _phase.Restart();
    internal void EndCompile() => _compileMs = _phase.ElapsedMilliseconds;

    internal void BeginInvocation(long budgetMs)
    {
        _pendingBefore = ThreadPool.PendingWorkItemCount;
        ThreadPool.GetAvailableThreads(out _availableBefore, out _);
        _sampleStartedTicks = Stopwatch.GetTimestamp();
        _sampleRegistration = _sampleTimer.Token.Register(() =>
            Interlocked.Exchange(ref _sampleDeliveredTicks, Stopwatch.GetTimestamp()));
        // An independent timer samples the same window. It is not the production step token.
        _sampleTimer.CancelAfter(TimeSpan.FromMilliseconds(budgetMs));
        _phase.Restart();
    }

    internal void EndInvocation()
    {
        // RunIsolatedAsync has returned, including its awaited script completion/unload route.
        _invokeMs = _phase.ElapsedMilliseconds;
        _pendingAfter = ThreadPool.PendingWorkItemCount;
        ThreadPool.GetAvailableThreads(out _availableAfter, out _);
        Dispose();
    }

    internal string Describe(long? actualStepMs = null)
    {
        var delivered = Interlocked.Read(ref _sampleDeliveredTicks);
        var sampleMs = delivered == 0 ? (double?)null
            : (delivered - _sampleStartedTicks) * 1000.0 / Stopwatch.Frequency;
        return Format(_compileMs, _invokeMs, _total.ElapsedMilliseconds, actualStepMs, sampleMs,
            _pendingBefore, _pendingAfter, _availableBefore, _availableAfter);
    }

    internal static string Format(long compileMs, long invokeMs, long totalMs, long? actualStepMs,
        double? sampleTimerMs, long pendingBefore, long pendingAfter, int availableBefore, int availableAfter)
    {
        static string Number<T>(T? value) where T : struct, IFormattable =>
            value.HasValue ? value.Value.ToString(null, CultureInfo.InvariantCulture) : "unobserved";
        return $"compileMs={compileMs}; invokeAndReleaseMs={invokeMs}; testElapsedMs={totalMs}; "
            + $"actualStepMs={Number(actualStepMs)}; independentTimerDeliveryMs={Number(sampleTimerMs)}; "
            + $"pendingWorkItems={pendingBefore}->{pendingAfter}; availableWorkers={availableBefore}->{availableAfter}. "
            + "The independent timer is a scheduling sample; producer-internal phases and actual step-token delivery are unmeasured.";
    }

    public void Dispose()
    {
        // No sampling callback can outlive the test or retain the collectible script context.
        _sampleRegistration.Dispose();
        _sampleTimer.Dispose();
    }
}
