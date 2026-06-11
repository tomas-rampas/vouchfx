// Vouchfx.Cli — GlobMatcher (S07-C-02).
//
// A tiny, dependency-free glob → regex translator for the `--path` selector. It deliberately
// avoids pulling in Microsoft.Extensions.FileSystemGlobbing (a new dependency for one option)
// and gives precise control over the three wildcards the spec calls for:
//
//   *   — matches any run of characters EXCEPT the path separator '/'  (one path segment)
//   **  — matches any run of characters INCLUDING '/'                  (spans segments)
//   ?   — matches exactly one character except '/'
//
// Matching is performed against the scenario's FORWARD-SLASH-normalised absolute path
// (ScenarioSelector.NormalisePath), case-insensitively (paths on Windows are case-tolerant,
// and tests author globs without worrying about drive-letter casing).
//
// Convenience: a glob with no wildcard at all is treated as a SUBSTRING match (anchored
// nowhere), so `--path orders` selects every scenario whose path contains "orders" — the
// documented MVP substring behaviour. Any glob containing a wildcard is matched as a full
// (anchored) pattern so `orders/**` means "under an orders/ directory" rather than a literal.

using System.Text;
using System.Text.RegularExpressions;

namespace Vouchfx.Cli.Selection;

/// <summary>
/// Translates a simple glob (<c>*</c>, <c>**</c>, <c>?</c>) into a regex and matches it
/// against a forward-slash-normalised path.
/// </summary>
internal static class GlobMatcher
{
    /// <summary>
    /// Returns whether <paramref name="normalisedPath"/> matches <paramref name="glob"/>.
    /// </summary>
    /// <param name="glob">
    /// The glob pattern.  When it contains no wildcard it is matched as a case-insensitive
    /// substring; otherwise it is anchored and matched in full.
    /// </param>
    /// <param name="normalisedPath">
    /// The candidate path, already normalised to <c>/</c> separators
    /// (see <see cref="ScenarioSelector.NormalisePath"/>).
    /// </param>
    /// <returns><see langword="true"/> on a match.</returns>
    public static bool IsMatch(string glob, string normalisedPath)
    {
        ArgumentNullException.ThrowIfNull(glob);
        ArgumentNullException.ThrowIfNull(normalisedPath);

        // No wildcard ⇒ substring match (documented MVP convenience).
        if (!ContainsWildcard(glob))
        {
            return normalisedPath.Contains(
                glob.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }

        var regex = ToRegex(glob.Replace('\\', '/'));
        return regex.IsMatch(normalisedPath);
    }

    private static bool ContainsWildcard(string glob) =>
        glob.Contains('*', StringComparison.Ordinal)
        || glob.Contains('?', StringComparison.Ordinal);

    /// <summary>
    /// Compiles a glob into an anchored, case-insensitive <see cref="Regex"/>.
    /// </summary>
    /// <remarks>
    /// A bare glob (no <c>/</c>) is allowed to match anywhere in the path by prefixing an
    /// implicit <c>**/</c>, so <c>*.e2e.yaml</c> matches a file at any depth.  A glob that
    /// already contains a <c>/</c> is anchored at the start so <c>orders/**</c> means "the
    /// orders segment onward", but is still allowed to begin partway through the absolute
    /// path (it is not forced to match the drive root) by allowing a leading <c>**/</c>.
    /// </remarks>
    private static Regex ToRegex(string glob)
    {
        var pattern = new StringBuilder();

        // Allow the glob to match a suffix of the absolute path (it need not include the
        // drive/root prefix). Anchoring with an optional leading "any path" makes
        // `orders/**` match `/abs/repo/orders/x.e2e.yaml`.
        pattern.Append("^(?:.*/)?");

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        // '**' — match across separators.
                        pattern.Append(".*");
                        i++;

                        // Swallow a separator immediately following '**' so that
                        // `orders/**` also matches `orders` itself (zero trailing segments).
                        if (i + 1 < glob.Length && glob[i + 1] == '/')
                        {
                            pattern.Append("(?:/)?");
                            i++;
                        }
                    }
                    else
                    {
                        // '*' — match within a single segment.
                        pattern.Append("[^/]*");
                    }

                    break;

                case '?':
                    pattern.Append("[^/]");
                    break;

                default:
                    pattern.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        pattern.Append('$');

        return new Regex(
            pattern.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
