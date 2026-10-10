namespace Vouchfx.TestSupport;

/// <summary>
/// Preserves explicit local golden regeneration while refusing it in CI before any write.
/// </summary>
public static class GoldenRegeneration
{
    /// <summary>
    /// Returns whether a switch is active (1 or case-insensitive true), throwing when
    /// an active request is accompanied by a nonempty CI or GITHUB_ACTIONS marker.
    /// </summary>
    public static bool IsRequested(string switchName)
    {
        var value = Environment.GetEnvironmentVariable(switchName);
        if (value != "1" && !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var marker in new[] { "CI", "GITHUB_ACTIONS" })
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(marker)))
            {
                throw new InvalidOperationException(
                    $"Golden regeneration via {switchName} is refused while {marker} is set. "
                    + "Regenerate locally with CI markers unset.");
            }
        }

        return true;
    }
}
