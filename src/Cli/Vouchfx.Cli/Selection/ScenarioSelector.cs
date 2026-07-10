// Vouchfx.Cli — ScenarioSelector (S07-C-02).
//
// Applies a SelectionCriteria to the set of discovered scenarios BEFORE they reach the
// runner. This is the runner's test-selection language (BP §16): `metadata` (tag/owner) has
// no execution effect — it only narrows the set. Selection deliberately happens at the CLI
// layer, between ScenarioDiscovery and ScenarioRunner.RunSuiteAsync, so the runner only ever
// sees the chosen scenarios.
//
// Semantics — AND across dimensions, OR within a dimension. A scenario passes when ALL hold:
//   • Tags empty   OR scenario carries ANY of Tags          (case-insensitive)
//   • Owners empty OR scenario.Owner is one of Owners        (case-insensitive)
//   • PathGlob null OR the normalised path matches the glob
//   • ChangedSinceRef null OR changeSet.IsChanged(path)
//
// Parse-failure rule (Ast == null): a scenario whose AST failed to build has NO metadata to
// match against. We INCLUDE it when no metadata filter (tag/owner) is active — so the run
// still reports it as Inconclusive (§12.1) and the author sees the broken file — and EXCLUDE
// it when a tag/owner filter is set (it cannot satisfy a metadata constraint it has no data
// for). Path and change-set filters apply to parse-failures normally (path/identity, not
// metadata), so a tag-free `--path`/`--changed-since` selection still narrows them.

using Vouchfx.Engine.Authoring.Model;

namespace Vouchfx.Cli.Selection;

/// <summary>
/// Filters discovered scenarios down to those matching a <see cref="SelectionCriteria"/>.
/// </summary>
internal static class ScenarioSelector
{
    /// <summary>
    /// Returns the subset of <paramref name="all"/> that satisfies <paramref name="criteria"/>.
    /// </summary>
    /// <param name="all">The discovered scenarios (parsed and parse-failures alike).</param>
    /// <param name="criteria">The composable filter (AND across, OR within — see remarks).</param>
    /// <param name="changeSet">
    /// The change-set consulted when <see cref="SelectionCriteria.ChangedSinceRef"/> is set;
    /// pass <see cref="NullChangeSet.Instance"/> when no change-set filter is active.
    /// </param>
    /// <returns>
    /// The matching scenarios, in their original order.  An empty result is valid (the CLI
    /// treats "nothing selected" as success — nothing to run is not a failure).
    /// </returns>
    /// <remarks>
    /// Matching is AND-across-dimensions, OR-within-a-dimension.  See the file header for the
    /// parse-failure rule.
    /// </remarks>
    public static IReadOnlyList<DiscoveredScenario> Apply(
        IReadOnlyList<DiscoveredScenario> all,
        SelectionCriteria criteria,
        IChangeSet changeSet)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(changeSet);

        var selected = new List<DiscoveredScenario>(all.Count);
        foreach (var scenario in all)
        {
            if (Matches(scenario, criteria, changeSet))
            {
                selected.Add(scenario);
            }
        }

        return selected;
    }

    /// <summary>
    /// Evaluates the four selection dimensions (AND across them) for a single scenario.
    /// </summary>
    private static bool Matches(
        DiscoveredScenario scenario,
        SelectionCriteria criteria,
        IChangeSet changeSet)
    {
        // Path / change-set are identity-based and apply to parse-failures too.
        var normalisedPath = NormalisePath(scenario.AbsolutePath);

        if (criteria.PathGlob is { } glob && !GlobMatcher.IsMatch(glob, normalisedPath))
        {
            return false;
        }

        if (criteria.ChangedSinceRef is not null && !changeSet.IsChanged(scenario.AbsolutePath))
        {
            return false;
        }

        // Metadata dimensions (tag / owner). A parse-failure has no metadata to match.
        MetadataSpec? metadata = scenario.Ast?.Metadata;

        if (criteria.Tags.Count > 0 && !MatchesAnyTag(metadata, criteria.Tags))
        {
            return false;
        }

        if (criteria.Owners.Count > 0 && !MatchesAnyOwner(metadata, criteria.Owners))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// <see langword="true"/> when the scenario carries at least one of the filter tags
    /// (case-insensitive).  A scenario with no metadata / no tags never matches.
    /// </summary>
    private static bool MatchesAnyTag(MetadataSpec? metadata, IReadOnlyList<string> filterTags)
    {
        var tags = metadata?.Tags;
        if (tags is null || tags.Count == 0)
        {
            return false;
        }

        foreach (var filterTag in filterTags)
        {
            foreach (var tag in tags)
            {
                if (string.Equals(tag, filterTag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// <see langword="true"/> when the scenario's owner is one of the filter owners
    /// (case-insensitive).  A scenario with no owner never matches.
    /// </summary>
    private static bool MatchesAnyOwner(MetadataSpec? metadata, IReadOnlyList<string> filterOwners)
    {
        var owner = metadata?.Owner;
        if (string.IsNullOrWhiteSpace(owner))
        {
            return false;
        }

        foreach (var filterOwner in filterOwners)
        {
            if (string.Equals(owner, filterOwner, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Normalises a scenario path for glob/substring matching: forward slashes throughout so
    /// a Windows <c>\</c>-path can be matched by a <c>/</c>-glob.
    /// </summary>
    internal static string NormalisePath(string path) => path.Replace('\\', '/');
}
