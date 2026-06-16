// Platform.Engine.Telemetry.Tests — OutboxDrainState (S12-G-01, Phase A).
//
// Proves the cross-run back-off is DETERMINISTIC under an injected clock + jitter, and
// fail-open under a missing/corrupt state file:
//   • a missing/corrupt file reads as the permissive default (attempt allowed);
//   • AfterFailure pushes next-attempt to now + min(base*2^failures, cap) with bounded
//     jitter — exactly computable with jitter=0;
//   • the interval grows geometrically and saturates at the cap;
//   • Reset() clears the back-off (immediate next attempt);
//   • the round-trip (Write→Read) preserves the state atomically.

using Xunit;

namespace Platform.Engine.Telemetry.Tests;

public sealed class OutboxDrainStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Read_MissingFile_ReturnsPermissiveDefault_NeverThrows()
    {
        using var paths = new TempPaths();

        var state = OutboxDrainState.Read(paths.DrainStatePath);

        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.True(state.MayAttempt(Now));
    }

    [Fact]
    public void Read_CorruptFile_ReturnsPermissiveDefault_NeverThrows()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DrainStatePath)!);
        File.WriteAllText(paths.DrainStatePath, "{ not valid json ]]]");

        var state = OutboxDrainState.Read(paths.DrainStatePath);

        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.True(state.MayAttempt(Now));
    }

    [Fact]
    public void AfterFailure_NoJitter_PushesNextAttemptByBaseTimesTwoToTheFailures()
    {
        var s0 = new OutboxDrainState();

        // First failure: interval = base * 2^0 = base (1 min).
        var s1 = s0.AfterFailure(Now, jitter: 0.0);
        Assert.Equal(1, s1.ConsecutiveFailures);
        Assert.Equal(Now + OutboxDrainState.DefaultBaseBackoff, s1.NextAttemptUtc);

        // Second failure: interval = base * 2^1 = 2 min.
        var s2 = s1.AfterFailure(Now, jitter: 0.0);
        Assert.Equal(2, s2.ConsecutiveFailures);
        Assert.Equal(Now + TimeSpan.FromTicks(OutboxDrainState.DefaultBaseBackoff.Ticks * 2),
            s2.NextAttemptUtc);

        // Third failure: interval = base * 2^2 = 4 min.
        var s3 = s2.AfterFailure(Now, jitter: 0.0);
        Assert.Equal(3, s3.ConsecutiveFailures);
        Assert.Equal(Now + TimeSpan.FromTicks(OutboxDrainState.DefaultBaseBackoff.Ticks * 4),
            s3.NextAttemptUtc);
    }

    [Fact]
    public void AfterFailure_SaturatesAtTheCap()
    {
        // A large failure count must clamp the interval at the cap, never overflow.
        var state = new OutboxDrainState { ConsecutiveFailures = 40 };

        var next = state.AfterFailure(Now, jitter: 0.0);

        // base * 2^40 hugely exceeds the 6h cap, so the interval is exactly the cap.
        Assert.Equal(Now + OutboxDrainState.DefaultMaxBackoff, next.NextAttemptUtc);
    }

    [Fact]
    public void AfterFailure_Jitter_AddsUpToTwentyPercent_Deterministically()
    {
        var s0 = new OutboxDrainState();

        // jitter == 1.0 ⇒ the full +20% of the base interval.
        var full = s0.AfterFailure(Now, jitter: 1.0);
        var expectedFull = Now + TimeSpan.FromTicks(
            (long)(OutboxDrainState.DefaultBaseBackoff.Ticks
                   + OutboxDrainState.DefaultBaseBackoff.Ticks * 0.2));
        Assert.Equal(expectedFull, full.NextAttemptUtc);

        // jitter == 0.5 ⇒ +10%.
        var half = s0.AfterFailure(Now, jitter: 0.5);
        var expectedHalf = Now + TimeSpan.FromTicks(
            (long)(OutboxDrainState.DefaultBaseBackoff.Ticks
                   + OutboxDrainState.DefaultBaseBackoff.Ticks * 0.2 * 0.5));
        Assert.Equal(expectedHalf, half.NextAttemptUtc);
    }

    [Fact]
    public void Reset_AllowsImmediateAttempt()
    {
        var reset = OutboxDrainState.Reset();

        Assert.Equal(0, reset.ConsecutiveFailures);
        Assert.True(reset.MayAttempt(Now));
        Assert.True(reset.MayAttempt(DateTimeOffset.MinValue));
    }

    [Fact]
    public void MayAttempt_RespectsTheBackoffWindow()
    {
        var state = new OutboxDrainState { NextAttemptUtc = Now.AddHours(1) };

        Assert.False(state.MayAttempt(Now));
        Assert.False(state.MayAttempt(Now.AddMinutes(59)));
        Assert.True(state.MayAttempt(Now.AddHours(1)));
        Assert.True(state.MayAttempt(Now.AddHours(2)));
    }

    [Fact]
    public void WriteThenRead_RoundTripsTheState()
    {
        using var paths = new TempPaths();
        var original = new OutboxDrainState
        {
            ConsecutiveFailures = 3,
            NextAttemptUtc = Now.AddMinutes(8),
        };

        Assert.True(original.Write(paths.DrainStatePath));

        var read = OutboxDrainState.Read(paths.DrainStatePath);
        Assert.Equal(3, read.ConsecutiveFailures);
        Assert.Equal(original.NextAttemptUtc, read.NextAttemptUtc);
    }
}
