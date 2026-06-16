// Platform.Engine.Telemetry — TelemetryVersions (S10-G-04).
//
// Resolves the three NON-IDENTIFYING version strings the allowlist carries: the tool
// (CLI) version, the engine version, and the .NET runtime version.  These are facts
// about the vouchfx build and the host runtime — never anything about the customer's
// tests — so they are allowlisted.

using System.Reflection;
using System.Runtime.InteropServices;

namespace Platform.Engine.Telemetry;

/// <summary>
/// Resolves the version facts carried on a <see cref="TelemetryEvent"/> (S10-G-04).
/// </summary>
/// <remarks>
/// These are build/runtime facts (tool / engine / .NET versions), not run data.  Each
/// resolver is null-safe: a missing informational version falls back to a stable
/// <c>"unknown"</c> sentinel rather than throwing.
/// </remarks>
public static class TelemetryVersions
{
    private const string Unknown = "unknown";

    /// <summary>
    /// The informational version of the supplied tool assembly (the CLI), e.g.
    /// <c>"1.0.0"</c>.  Falls back to the assembly version, then to <c>"unknown"</c>.
    /// </summary>
    /// <param name="toolAssembly">
    /// The CLI assembly (pass <c>Assembly.GetExecutingAssembly()</c> from the CLI).
    /// </param>
    /// <returns>The resolved tool version string; never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="toolAssembly"/> is null.</exception>
    public static string ToolVersion(Assembly toolAssembly)
    {
        ArgumentNullException.ThrowIfNull(toolAssembly);
        return InformationalVersion(toolAssembly);
    }

    /// <summary>
    /// The informational version of the engine (the
    /// <c>Platform.Engine.Telemetry</c> assembly is part of the engine and versions
    /// in lockstep with it), e.g. <c>"1.0.0"</c>.
    /// </summary>
    /// <returns>The resolved engine version string; never <see langword="null"/>.</returns>
    public static string EngineVersion() =>
        InformationalVersion(typeof(TelemetryVersions).Assembly);

    /// <summary>
    /// The .NET runtime description the tool is executing on, e.g. <c>".NET 8.0.7"</c>
    /// (from <see cref="RuntimeInformation.FrameworkDescription"/>).
    /// </summary>
    /// <returns>The runtime description string; never <see langword="null"/>.</returns>
    public static string DotnetVersion() => RuntimeInformation.FrameworkDescription;

    /// <summary>
    /// Reads an assembly's <see cref="AssemblyInformationalVersionAttribute"/>,
    /// stripping any SourceLink <c>+commithash</c> suffix; falls back to the assembly
    /// version, then to <c>"unknown"</c>.
    /// </summary>
    private static string InformationalVersion(Assembly assembly)
    {
        var info = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(info))
        {
            // Deterministic builds append "+<commit>" to the informational version;
            // drop it so the version is a clean, low-cardinality string.
            var plus = info.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? info[..plus] : info;
        }

        var asmVersion = assembly.GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(asmVersion) ? Unknown : asmVersion;
    }
}
