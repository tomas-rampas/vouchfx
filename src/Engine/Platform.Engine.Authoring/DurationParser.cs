// Platform.Engine.Authoring — DurationParser (S03-B-02).
//
// Converts the raw duration string from the YAML `timeout` field into a
// TimeSpan.  Accepted formats: "<n>ms", "<n>s", "<n>m", and a bare integer
// (treated as seconds).  Uses CultureInfo.InvariantCulture throughout.

using System.Globalization;

namespace Platform.Engine.Authoring;

/// <summary>
/// Parses the raw duration strings used in the <c>.e2e.yaml</c> <c>timeout</c>
/// field into <see cref="TimeSpan"/> values.
/// </summary>
/// <remarks>
/// <para>
/// Accepted formats (case-sensitive suffix matching):
/// <list type="table">
///   <listheader><term>Format</term><description>Interpretation</description></listheader>
///   <item><term><c>&lt;n&gt;ms</c></term><description>Milliseconds, e.g. <c>500ms</c>.</description></item>
///   <item><term><c>&lt;n&gt;s</c></term><description>Seconds, e.g. <c>30s</c>.</description></item>
///   <item><term><c>&lt;n&gt;m</c></term><description>Minutes, e.g. <c>2m</c>.</description></item>
///   <item><term><c>&lt;n&gt;</c></term><description>Bare non-negative integer treated as seconds, e.g. <c>45</c>.</description></item>
/// </list>
/// All numeric parsing uses <see cref="CultureInfo.InvariantCulture"/>.
/// </para>
/// </remarks>
internal static class DurationParser
{
    /// <summary>
    /// Attempts to parse <paramref name="raw"/> into a <see cref="TimeSpan"/>.
    /// </summary>
    /// <param name="raw">
    /// The raw timeout string from the YAML step, e.g. <c>"30s"</c>,
    /// <c>"500ms"</c>, <c>"2m"</c>, or <c>"45"</c>.
    /// </param>
    /// <param name="result">
    /// When this method returns <see langword="true"/>, contains the parsed
    /// <see cref="TimeSpan"/>; otherwise <see cref="TimeSpan.Zero"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="raw"/> was successfully
    /// parsed; <see langword="false"/> otherwise.
    /// </returns>
    internal static bool TryParse(string raw, out TimeSpan result)
    {
        result = TimeSpan.Zero;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        // Milliseconds: e.g. "500ms"
        if (raw.EndsWith("ms", StringComparison.Ordinal))
        {
            var digits = raw[..^2];
            if (long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
            {
                result = TimeSpan.FromMilliseconds(ms);
                return true;
            }

            return false;
        }

        // Seconds: e.g. "30s"
        if (raw.EndsWith('s'))
        {
            var digits = raw[..^1];
            if (long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var sec))
            {
                result = TimeSpan.FromSeconds(sec);
                return true;
            }

            return false;
        }

        // Minutes: e.g. "2m"
        if (raw.EndsWith('m'))
        {
            var digits = raw[..^1];
            if (long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var min))
            {
                result = TimeSpan.FromMinutes(min);
                return true;
            }

            return false;
        }

        // Bare integer — treated as seconds.
        if (long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var bareSec))
        {
            result = TimeSpan.FromSeconds(bareSec);
            return true;
        }

        return false;
    }
}
