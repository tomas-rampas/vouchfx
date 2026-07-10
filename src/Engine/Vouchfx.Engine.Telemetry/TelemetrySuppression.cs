// Vouchfx.Engine.Telemetry — TelemetrySuppression (S10-G-04).
//
// The single testable predicate that decides whether a run may emit a telemetry
// event.  Emission requires ALL of:
//   • consent == Enabled (opt-in — never Undecided, never Disabled), AND
//   • the run was NOT opted out via the --no-telemetry flag, AND
//   • the run was NOT opted out via the VOUCHFX_NO_TELEMETRY environment variable.
//
// The env var doubles as the PRODUCTION-RUN EXCLUSION: CI/automation that cannot
// pass a flag sets VOUCHFX_NO_TELEMETRY=1 to guarantee no emission.

namespace Vouchfx.Engine.Telemetry;

/// <summary>
/// Decides whether a run is permitted to emit a telemetry event (S10-G-04).
/// </summary>
/// <remarks>
/// Pure, side-effect-free, and fully unit-testable.  Emission is the conjunction of
/// opt-in consent AND the absence of any opt-out signal, so any single opt-out
/// (consent not Enabled, the <c>--no-telemetry</c> flag, or the
/// <c>VOUCHFX_NO_TELEMETRY</c> environment variable) suppresses emission.
/// </remarks>
public static class TelemetrySuppression
{
    /// <summary>
    /// The environment variable that opts a run out of telemetry.  Set to any
    /// non-empty value (e.g. <c>1</c>, <c>true</c>) to suppress emission — it doubles
    /// as the production-run exclusion for CI / automation that cannot pass a CLI flag.
    /// </summary>
    public const string OptOutEnvVar = "VOUCHFX_NO_TELEMETRY";

    /// <summary>
    /// Returns <see langword="true"/> when this run MAY emit telemetry: consent is
    /// <see cref="TelemetryConsent.Enabled"/> AND no opt-out signal is present.
    /// </summary>
    /// <param name="consent">The persisted consent decision.</param>
    /// <param name="noTelemetryFlag">
    /// The <c>--no-telemetry</c> run flag: when <see langword="true"/>, this run is
    /// opted out regardless of consent.
    /// </param>
    /// <param name="optOutEnvValue">
    /// The raw value of the <see cref="OptOutEnvVar"/> environment variable (pass
    /// <c>Environment.GetEnvironmentVariable(OptOutEnvVar)</c>).  A non-empty value
    /// opts the run out.  <see langword="null"/> / empty / whitespace means "not set".
    /// </param>
    /// <returns>
    /// <see langword="true"/> only when telemetry is permitted; <see langword="false"/>
    /// when any opt-out applies.
    /// </returns>
    public static bool ShouldEmit(
        TelemetryConsent consent,
        bool noTelemetryFlag,
        string? optOutEnvValue)
    {
        if (consent != TelemetryConsent.Enabled)
        {
            return false;
        }

        if (noTelemetryFlag)
        {
            return false;
        }

        if (IsEnvOptOut(optOutEnvValue))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether the supplied <see cref="OptOutEnvVar"/> value counts as an opt-out: any
    /// non-empty, non-whitespace value (truthy-by-presence).  <see langword="null"/> /
    /// empty / whitespace means the variable is effectively unset.
    /// </summary>
    /// <param name="optOutEnvValue">The raw environment-variable value.</param>
    /// <returns><see langword="true"/> when the value opts the run out.</returns>
    public static bool IsEnvOptOut(string? optOutEnvValue) =>
        !string.IsNullOrWhiteSpace(optOutEnvValue);
}
