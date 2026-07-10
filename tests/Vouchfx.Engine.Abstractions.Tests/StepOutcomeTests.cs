// Tests for S03-T0: StepOutcome record — property round-trip, value equality, null Observation.
// Written RED-first before the implementation exists.

using Vouchfx.Engine.Abstractions;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests;

/// <summary>
/// Verifies the <see cref="StepOutcome"/> positional record: property round-trip,
/// C# record value-equality semantics, and the nullable <c>Observation</c> contract.
/// </summary>
public sealed class StepOutcomeTests
{
    // -------------------------------------------------------------------------
    // Property round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void StepOutcome_Properties_RoundTrip_Correctly()
    {
        // Arrange
        const string observation = """{"status":500,"expected":200}""";

        // Act
        var outcome = new StepOutcome(Verdict.Fail, 1530L, observation);

        // Assert
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Equal(1530L, outcome.DurationMs);
        Assert.Equal(observation, outcome.Observation);
    }

    // -------------------------------------------------------------------------
    // Record value-equality
    // -------------------------------------------------------------------------

    [Fact]
    public void StepOutcome_ValueEquality_TwoEqualInstances_AreEqual()
    {
        // Two independently constructed instances with the same values must satisfy
        // C# record structural equality.
        const string observation = """{"status":500}""";

        var a = new StepOutcome(Verdict.Fail, 1530L, observation);
        var b = new StepOutcome(Verdict.Fail, 1530L, observation);

        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void StepOutcome_ValueEquality_DifferentInstances_AreNotEqual()
    {
        var a = new StepOutcome(Verdict.Pass, 120L, null);
        var b = new StepOutcome(Verdict.Fail, 120L, null);

        Assert.NotEqual(a, b);
        Assert.False(a == b);
    }

    // -------------------------------------------------------------------------
    // Null Observation is allowed
    // -------------------------------------------------------------------------

    [Fact]
    public void StepOutcome_NullObservation_IsAllowed()
    {
        // Observation is optional — a null value must be accepted without error.
        var outcome = new StepOutcome(Verdict.Pass, 42L, null);

        Assert.Null(outcome.Observation);
    }

    // -------------------------------------------------------------------------
    // All Verdict members are representable
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(Verdict.Pass)]
    [InlineData(Verdict.Fail)]
    [InlineData(Verdict.EnvironmentError)]
    [InlineData(Verdict.Inconclusive)]
    public void StepOutcome_AcceptsAllVerdictValues(Verdict verdict)
    {
        var outcome = new StepOutcome(verdict, 0L, null);

        Assert.Equal(verdict, outcome.Verdict);
    }
}
