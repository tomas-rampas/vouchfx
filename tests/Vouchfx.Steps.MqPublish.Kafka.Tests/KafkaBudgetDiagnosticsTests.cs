using Xunit;

namespace Vouchfx.Steps.MqPublish.Kafka.Tests;

public sealed class KafkaBudgetDiagnosticsTests
{
    [Fact]
    public void Format_PreservesActualStepDurationSeparatelyFromCompileAndRelease()
    {
        var text = KafkaBudgetDiagnostics.Format(123, 9876, 10000, 7702, 5689.5, 5, 2, 10, 12);
        Assert.Contains("compileMs=123; invokeAndReleaseMs=9876; testElapsedMs=10000; actualStepMs=7702", text, StringComparison.Ordinal);
        Assert.Contains("independentTimerDeliveryMs=5689.5", text, StringComparison.Ordinal);
        Assert.Contains("pendingWorkItems=5->2; availableWorkers=10->12", text, StringComparison.Ordinal);
        Assert.Contains("producer-internal phases and actual step-token delivery are unmeasured", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_AbsentTimerAndOutcomeAreUnobservedRatherThanZero()
    {
        var text = KafkaBudgetDiagnostics.Format(1, 2, 3, null, null, 0, 0, 4, 4);
        Assert.Contains("actualStepMs=unobserved; independentTimerDeliveryMs=unobserved", text, StringComparison.Ordinal);
        Assert.DoesNotContain("actualStepMs=0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("independentTimerDeliveryMs=0", text, StringComparison.Ordinal);
    }
}
