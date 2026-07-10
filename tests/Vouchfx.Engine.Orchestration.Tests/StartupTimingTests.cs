using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Fast, non-docker unit tests for <see cref="StartupTiming"/>.
/// These run in the standard unit-CI job (no docker trait) and guarantee the record type
/// is exercised by the fast pipeline gate.
/// </summary>
/// <remarks>
/// Genuine timeout-to-Environment-error behaviour requires live containers and is covered
/// by <see cref="HealthGateReliabilityTests"/> (docker-gated) and the S01-A-02/A-03 suite.
/// </remarks>
public sealed class StartupTimingTests
{
    /// <summary>
    /// Constructs a <see cref="StartupTiming"/> instance and verifies that all three properties
    /// round-trip correctly.
    /// </summary>
    [Fact]
    public void StartupTiming_Properties_RoundTrip()
    {
        // Arrange
        var total = TimeSpan.FromSeconds(45.3);
        var databaseReady = TimeSpan.FromSeconds(30.1);
        var serviceReady = TimeSpan.FromSeconds(15.2);

        // Act
        var timing = new StartupTiming(total, databaseReady, serviceReady);

        // Assert
        Assert.Equal(total, timing.Total);
        Assert.Equal(databaseReady, timing.DatabaseReady);
        Assert.Equal(serviceReady, timing.ServiceReady);
    }

    /// <summary>
    /// Verifies that two <see cref="StartupTiming"/> records with identical values are equal,
    /// exercising the record's compiler-generated value-equality semantics.
    /// </summary>
    [Fact]
    public void StartupTiming_ValueEquality_EqualWhenPropertiesMatch()
    {
        // Arrange
        var a = new StartupTiming(
            Total: TimeSpan.FromSeconds(60),
            DatabaseReady: TimeSpan.FromSeconds(40),
            ServiceReady: TimeSpan.FromSeconds(20));

        var b = new StartupTiming(
            Total: TimeSpan.FromSeconds(60),
            DatabaseReady: TimeSpan.FromSeconds(40),
            ServiceReady: TimeSpan.FromSeconds(20));

        // Assert
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    /// <summary>
    /// Verifies that two <see cref="StartupTiming"/> records with differing values are not equal.
    /// </summary>
    [Fact]
    public void StartupTiming_ValueEquality_NotEqualWhenPropertiesDiffer()
    {
        // Arrange
        var a = new StartupTiming(
            Total: TimeSpan.FromSeconds(60),
            DatabaseReady: TimeSpan.FromSeconds(40),
            ServiceReady: TimeSpan.FromSeconds(20));

        var b = new StartupTiming(
            Total: TimeSpan.FromSeconds(90),
            DatabaseReady: TimeSpan.FromSeconds(60),
            ServiceReady: TimeSpan.FromSeconds(30));

        // Assert
        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    /// <summary>
    /// Verifies that a <see cref="StartupTiming"/> record supports non-destructive mutation
    /// via <c>with</c> expressions, and that the original instance is unmodified.
    /// </summary>
    [Fact]
    public void StartupTiming_WithExpression_ProducesNewInstanceWithUpdatedProperty()
    {
        // Arrange
        var original = new StartupTiming(
            Total: TimeSpan.FromSeconds(60),
            DatabaseReady: TimeSpan.FromSeconds(40),
            ServiceReady: TimeSpan.FromSeconds(20));

        // Act — non-destructive mutation
        var modified = original with { Total = TimeSpan.FromSeconds(75) };

        // Assert — modified has updated Total; others unchanged
        Assert.Equal(TimeSpan.FromSeconds(75), modified.Total);
        Assert.Equal(TimeSpan.FromSeconds(40), modified.DatabaseReady);
        Assert.Equal(TimeSpan.FromSeconds(20), modified.ServiceReady);

        // Original is unmodified (immutability)
        Assert.Equal(TimeSpan.FromSeconds(60), original.Total);
    }

    /// <summary>
    /// Verifies that zero-value timespans are accepted (e.g. a topology that starts instantaneously
    /// in a test double scenario where times are stubbed to zero).
    /// </summary>
    [Fact]
    public void StartupTiming_ZeroValues_Accepted()
    {
        // Arrange & Act
        var timing = new StartupTiming(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

        // Assert
        Assert.Equal(TimeSpan.Zero, timing.Total);
        Assert.Equal(TimeSpan.Zero, timing.DatabaseReady);
        Assert.Equal(TimeSpan.Zero, timing.ServiceReady);
    }
}
