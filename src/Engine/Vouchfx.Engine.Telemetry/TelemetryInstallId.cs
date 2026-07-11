// Vouchfx.Engine.Telemetry — TelemetryInstallId.
//
// The single testable parser for the VOUCHFX_TELEMETRY_INSTALL_ID environment
// variable — the third leg of ENVIRONMENT-CONFIGURED telemetry.  When all three
// VOUCHFX_TELEMETRY_* variables are set (endpoint + token via
// TelemetryTransportOptions, plus this install id), setting them constitutes the
// opt-in for that environment and the emitted event carries the supplied id.  This
// gives CI a STABLE per-repository install identity: ephemeral runners no longer
// mint a fresh random id per job, so runs aggregate under one install instead of
// inflating install counts.
//
// The variable supplies only the VALUE of the existing allowlisted `installId`
// field — no new TelemetryEvent property, no wire-contract change.

namespace Vouchfx.Engine.Telemetry;

/// <summary>
/// Parses the <see cref="EnvVar"/> environment variable that supplies a stable,
/// caller-chosen install id for environment-configured telemetry (CI).
/// </summary>
/// <remarks>
/// Pure and side-effect-free: the environment read happens at the CLI edge (matching
/// how <see cref="TelemetrySuppression.OptOutEnvVar"/> is consumed), so tests drive
/// this parser without mutating process state.
/// </remarks>
public static class TelemetryInstallId
{
    /// <summary>
    /// The environment variable that supplies the install id for environment-configured
    /// telemetry.  Set it to any GUID chosen once per repository/project (e.g. from
    /// <c>uuidgen</c>) alongside <see cref="TelemetryTransportOptions.EndpointEnvVar"/>
    /// and <see cref="TelemetryTransportOptions.TokenEnvVar"/>; all three present is the
    /// opt-in for that environment.
    /// </summary>
    public const string EnvVar = "VOUCHFX_TELEMETRY_INSTALL_ID";

    /// <summary>
    /// Parses a raw <see cref="EnvVar"/> value.  Tri-state result:
    /// <list type="bullet">
    /// <item><see langword="true"/> with <paramref name="id"/> = <see langword="null"/>
    /// when the variable is unset / empty / whitespace ("not supplied" — valid).</item>
    /// <item><see langword="true"/> with a value when the input parses as a GUID in any
    /// standard format.</item>
    /// <item><see langword="false"/> for anything else, including <see cref="Guid.Empty"/>
    /// (a degenerate all-zero id is treated as invalid, never as an identity).</item>
    /// </list>
    /// </summary>
    /// <param name="raw">
    /// The raw environment-variable value (pass
    /// <c>Environment.GetEnvironmentVariable(EnvVar)</c>).
    /// </param>
    /// <param name="id">The parsed install id, or <see langword="null"/> when not supplied.</param>
    /// <returns><see langword="true"/> when the value is absent or a usable GUID.</returns>
    public static bool TryParse(string? raw, out Guid? id)
    {
        id = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (Guid.TryParse(raw.Trim(), out var parsed) && parsed != Guid.Empty)
        {
            id = parsed;
            return true;
        }

        return false;
    }
}
