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
// Parse-failure rule (Ast == null): a scenario whose AST failed to build usually has NO metadata
// to match against. We INCLUDE it when no metadata filter (tag/owner) is active — so the run
// still reports it as Inconclusive (§12.1) and the author sees the broken file — and EXCLUDE
// it when a tag/owner filter is set AND nothing was recovered (it cannot satisfy a metadata
// constraint it has no data for). A document that PARSED and was refused only by AstBuilder is the
// exception: it bound its `metadata` block, discovery retains it (DiscoveredScenario.
// RecoveredMetadata, issue #411), and it is matched on that — see Matches. Path and change-set
// filters apply to parse-failures normally (path/identity, not metadata), so a tag-free
// `--path`/`--changed-since` selection still narrows them.
//
// The recovered-metadata half is OPT-OUTABLE for one caller — `--watch` — and the switch is
// `matchRecoveredMetadata`. See Apply's remarks for why.

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
    /// <param name="matchRecoveredMetadata">
    /// <see langword="true"/> (the default) to match a metadata filter against
    /// <see cref="DiscoveredScenario.RecoveredMetadata"/> for a document that parsed and was then
    /// refused by <c>AstBuilder</c>; <see langword="false"/> to read the built
    /// <see cref="DiscoveredScenario.Ast"/>'s metadata only, which is what every caller did before
    /// issue #411.
    /// </param>
    /// <returns>
    /// The matching scenarios, in their original order.  An empty result is valid (the CLI
    /// treats "nothing selected" as success — nothing to run is not a failure).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Matching is AND-across-dimensions, OR-within-a-dimension.  See the file header for the
    /// parse-failure rule.
    /// </para>
    /// <para>
    /// <strong><c>matchRecoveredMetadata: false</c> exists for <c>--watch</c>, and it keeps a
    /// behaviour rather than adding one.</strong> Selection runs in <c>RunCommand</c> BEFORE the
    /// watch branch, and <c>WatchRunner.RunAsync</c> refuses any selection whose count is not 1.
    /// Recovering metadata therefore made a broken sibling that carries the filter's own tag
    /// JOIN a filtered watch selection, taking it from 1 to 2 — measured on the built CLI, a
    /// directory pairing one good <c>smoke</c>-tagged file with one <c>smoke</c>-tagged file that
    /// parses and fails <c>AstBuilder</c> exited <strong>2</strong> under
    /// <c>run &lt;dir&gt; --tag smoke --watch</c>, where it previously watched the good file.
    /// Issue #411's carve-out never reached watch in the first place (that path returns before the
    /// split that builds any <c>UnbuiltDocument</c>), so there is nothing on this path for the
    /// recovery to serve and a regression is all it could contribute. The UNFILTERED case is
    /// untouched and still resolves to 2 and exits 2 — the recovery was never what included a
    /// parse-failure there. Issue #412 tracks watch's divergence from <c>run</c>.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<DiscoveredScenario> Apply(
        IReadOnlyList<DiscoveredScenario> all,
        SelectionCriteria criteria,
        IChangeSet changeSet,
        bool matchRecoveredMetadata = true)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(changeSet);

        var selected = new List<DiscoveredScenario>(all.Count);
        foreach (var scenario in all)
        {
            if (Matches(scenario, criteria, changeSet, matchRecoveredMetadata))
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
        IChangeSet changeSet,
        bool matchRecoveredMetadata)
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

        // Metadata dimensions (tag / owner).
        //
        // A PARSE-FAILURE THAT NONETHELESS BOUND ITS DOCUMENT IS MATCHED ON WHAT IT BOUND (issue
        // #411). `Ast` is null for every failure, so this used to read null for all of them and a
        // tag/owner filter excluded the lot. That is right for a file whose YAML never parsed —
        // there is genuinely nothing to match — and wrong for one that parsed and was refused only
        // by `AstBuilder`: its `metadata` block bound, in the same `Parse` call as its
        // `environment`, and discovery now retains both.
        //
        // THE COST OF THE OLD ANSWER WAS A SILENT SECURITY FALSE NEGATIVE, not a missing line of
        // output. Selection runs in `RunCommand` BEFORE the split that hands unbuilt documents to
        // the runner, so an excluded file contributes no declaration to the suite's assurance.
        // Measured on the built CLI: a secured unbuildable file beside a sibling tagged `smoke`
        // exited 4 under `vouchfx run <dir>` and 0 under the same command plus `--tag smoke`, with
        // the file's own parse error not even printed. Answering from the recovered metadata makes
        // the filter mean what the user asked; a document whose recovered tags genuinely do not
        // match is still excluded, which is also what the user asked.
        //
        // `matchRecoveredMetadata: false` opts one caller out — see Apply's remarks. It is the
        // recovery alone that is scoped out, not the parse-failure rule: `Ast?.Metadata` is null
        // for every failure either way, so a no-filter selection still includes them all.
        MetadataSpec? metadata = scenario.Ast?.Metadata
            ?? (matchRecoveredMetadata ? scenario.RecoveredMetadata : null);

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
