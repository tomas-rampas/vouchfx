// Vouchfx.Engine.Telemetry.Tests — suppression predicate (S10-G-04).
//
// Proves the opt-in semantics: NO collection while Undecided or Disabled, and even when
// Enabled the --no-telemetry flag OR the VOUCHFX_NO_TELEMETRY env var each suppress
// emission.  These are pure-function assertions over TelemetrySuppression.

using Xunit;

namespace Vouchfx.Engine.Telemetry.Tests;

public sealed class TelemetrySuppressionTests
{
    [Theory]
    [InlineData(TelemetryConsent.Undecided)]
    [InlineData(TelemetryConsent.Disabled)]
    public void ShouldEmit_False_WhenConsentNotEnabled(TelemetryConsent consent)
    {
        // No opt-out signals at all — still must not emit unless explicitly Enabled.
        Assert.False(TelemetrySuppression.ShouldEmit(consent, noTelemetryFlag: false, optOutEnvValue: null));
    }

    [Fact]
    public void ShouldEmit_True_WhenEnabled_AndNoOptOut()
    {
        Assert.True(TelemetrySuppression.ShouldEmit(
            TelemetryConsent.Enabled, noTelemetryFlag: false, optOutEnvValue: null));
    }

    [Fact]
    public void ShouldEmit_False_WhenEnabled_ButNoTelemetryFlagSet()
    {
        Assert.False(TelemetrySuppression.ShouldEmit(
            TelemetryConsent.Enabled, noTelemetryFlag: true, optOutEnvValue: null));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("anything-nonempty")]
    public void ShouldEmit_False_WhenEnabled_ButEnvVarSet(string envValue)
    {
        Assert.False(TelemetrySuppression.ShouldEmit(
            TelemetryConsent.Enabled, noTelemetryFlag: false, optOutEnvValue: envValue));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EnvOptOut_TreatsNullOrEmptyOrWhitespaceAsNotSet(string? envValue)
    {
        Assert.False(TelemetrySuppression.IsEnvOptOut(envValue));
        // And therefore an Enabled run with no other opt-out DOES emit.
        Assert.True(TelemetrySuppression.ShouldEmit(
            TelemetryConsent.Enabled, noTelemetryFlag: false, optOutEnvValue: envValue));
    }

    [Fact]
    public void EnvOptOut_TreatsAnyNonEmptyValueAsOptOut()
    {
        Assert.True(TelemetrySuppression.IsEnvOptOut("0")); // presence, not truthiness, opts out.
        Assert.True(TelemetrySuppression.IsEnvOptOut("false"));
        Assert.True(TelemetrySuppression.IsEnvOptOut("x"));
    }
}
