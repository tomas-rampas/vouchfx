// Vouchfx.Cli.Tests — ScenarioSelector unit tests (S07-C-02). No Docker.
//
// Exercises the test-selection language: AND across dimensions (tag / owner / path /
// change-set), OR within a dimension. Scenarios are hand-built with synthetic metadata and
// paths so the selector is tested in isolation — no discovery, no parsing, no git.

using System.Collections.Generic;
using System.Linq;
using Vouchfx.Cli;
using Vouchfx.Cli.Selection;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ScenarioSelectorTests
{
    // ---- Builders ---------------------------------------------------------------------

    private static DiscoveredScenario Scenario(
        string absolutePath,
        string? owner = null,
        params string[] tags)
    {
        var metadata = new MetadataSpec(
            Name: null,
            Owner: owner,
            Tags: tags.Length == 0 ? null : tags.ToList(),
            Description: null,
            SchemaVersion: null);

        var ast = new ScenarioAst(
            Metadata: metadata,
            Environment: null,
            Variables: new Dictionary<string, string>(),
            Steps: new List<StepNode>());

        return new DiscoveredScenario(absolutePath, YamlText: "steps: []", ast, ParseError: null);
    }

    private static DiscoveredScenario ParseFailure(string absolutePath) =>
        new(absolutePath, YamlText: "broken", Ast: null, ParseError: "boom");

    private static SelectionCriteria Criteria(
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? owners = null,
        string? pathGlob = null,
        string? changedSinceRef = null) =>
        new(
            tags ?? System.Array.Empty<string>(),
            owners ?? System.Array.Empty<string>(),
            pathGlob,
            changedSinceRef);

    private static IReadOnlyList<string> Apply(
        IReadOnlyList<DiscoveredScenario> all,
        SelectionCriteria criteria,
        IChangeSet? changeSet = null,
        bool matchRecoveredMetadata = true) =>
        ScenarioSelector
            .Apply(all, criteria, changeSet ?? NullChangeSet.Instance, matchRecoveredMetadata)
            .Select(s => s.AbsolutePath)
            .ToList();

    // A small fake change-set keyed by exact absolute path.
    private sealed class FakeChangeSet : IChangeSet
    {
        private readonly HashSet<string> _changed;

        public FakeChangeSet(params string[] changed) =>
            _changed = new HashSet<string>(changed, System.StringComparer.OrdinalIgnoreCase);

        public bool IsChanged(string absolutePath) => _changed.Contains(absolutePath);
    }

    // ---- Empty criteria selects all ---------------------------------------------------

    [Fact]
    public void EmptyCriteria_SelectsEverything()
    {
        var all = new[]
        {
            Scenario("/r/a.e2e.yaml", owner: "alice", "smoke"),
            Scenario("/r/b.e2e.yaml", owner: "bob", "billing"),
            ParseFailure("/r/broken.e2e.yaml"),
        };

        var selected = Apply(all, SelectionCriteria.None);

        Assert.Equal(
            new[] { "/r/a.e2e.yaml", "/r/b.e2e.yaml", "/r/broken.e2e.yaml" },
            selected);
    }

    [Fact]
    public void None_IsEmpty_AndHasNoMetadataFilter()
    {
        Assert.True(SelectionCriteria.None.IsEmpty);
        Assert.False(SelectionCriteria.None.HasMetadataFilter);
        Assert.True(Criteria(tags: new[] { "smoke" }).HasMetadataFilter);
        Assert.True(Criteria(owners: new[] { "alice" }).HasMetadataFilter);
        Assert.False(Criteria(pathGlob: "x").HasMetadataFilter);
    }

    // ---- Tag dimension (OR within) ----------------------------------------------------

    [Fact]
    public void TagFilter_MatchesAnyOfTheTags_OrWithin()
    {
        var all = new[]
        {
            Scenario("/r/smoke.e2e.yaml", tags: "smoke"),
            Scenario("/r/billing.e2e.yaml", tags: "billing"),
            Scenario("/r/other.e2e.yaml", tags: "nightly"),
            Scenario("/r/multi.e2e.yaml", tags: new[] { "regression", "billing" }),
        };

        var selected = Apply(all, Criteria(tags: new[] { "smoke", "billing" }));

        Assert.Equal(
            new[] { "/r/smoke.e2e.yaml", "/r/billing.e2e.yaml", "/r/multi.e2e.yaml" },
            selected);
    }

    [Fact]
    public void TagFilter_IsCaseInsensitive()
    {
        var all = new[] { Scenario("/r/a.e2e.yaml", tags: "Smoke") };
        Assert.Single(Apply(all, Criteria(tags: new[] { "smoke" })));
    }

    [Fact]
    public void TagFilter_ExcludesScenarioWithNoTags()
    {
        var all = new[]
        {
            Scenario("/r/tagged.e2e.yaml", tags: "smoke"),
            Scenario("/r/untagged.e2e.yaml"), // no tags
        };

        var selected = Apply(all, Criteria(tags: new[] { "smoke" }));
        Assert.Equal(new[] { "/r/tagged.e2e.yaml" }, selected);
    }

    // ---- Owner dimension (OR within) --------------------------------------------------

    [Fact]
    public void OwnerFilter_MatchesAnyOfTheOwners_OrWithin()
    {
        var all = new[]
        {
            Scenario("/r/a.e2e.yaml", owner: "alice"),
            Scenario("/r/b.e2e.yaml", owner: "bob"),
            Scenario("/r/c.e2e.yaml", owner: "carol"),
        };

        var selected = Apply(all, Criteria(owners: new[] { "alice", "carol" }));
        Assert.Equal(new[] { "/r/a.e2e.yaml", "/r/c.e2e.yaml" }, selected);
    }

    [Fact]
    public void OwnerFilter_IsCaseInsensitive_AndExcludesOwnerless()
    {
        var all = new[]
        {
            Scenario("/r/a.e2e.yaml", owner: "Alice"),
            Scenario("/r/none.e2e.yaml"), // no owner
        };

        var selected = Apply(all, Criteria(owners: new[] { "alice" }));
        Assert.Equal(new[] { "/r/a.e2e.yaml" }, selected);
    }

    // ---- AND across dimensions: tag + owner -------------------------------------------

    [Fact]
    public void TagAndOwner_BothMustHold_AndAcross()
    {
        var all = new[]
        {
            Scenario("/r/a.e2e.yaml", owner: "alice", "smoke"),   // tag+owner both match
            Scenario("/r/b.e2e.yaml", owner: "bob", "smoke"),     // tag matches, owner does not
            Scenario("/r/c.e2e.yaml", owner: "alice", "nightly"), // owner matches, tag does not
        };

        var selected = Apply(all, Criteria(tags: new[] { "smoke" }, owners: new[] { "alice" }));
        Assert.Equal(new[] { "/r/a.e2e.yaml" }, selected);
    }

    // ---- Path dimension ---------------------------------------------------------------

    [Fact]
    public void PathFilter_SubstringMatch_WhenNoWildcard()
    {
        var all = new[]
        {
            Scenario("/repo/orders/place.e2e.yaml"),
            Scenario("/repo/billing/charge.e2e.yaml"),
        };

        var selected = Apply(all, Criteria(pathGlob: "orders"));
        Assert.Equal(new[] { "/repo/orders/place.e2e.yaml" }, selected);
    }

    [Fact]
    public void PathFilter_GlobStarStar_MatchesUnderDirectory()
    {
        var all = new[]
        {
            Scenario("/repo/orders/place.e2e.yaml"),
            Scenario("/repo/orders/nested/deep/x.e2e.yaml"),
            Scenario("/repo/billing/charge.e2e.yaml"),
        };

        var selected = Apply(all, Criteria(pathGlob: "orders/**"));
        Assert.Equal(
            new[] { "/repo/orders/place.e2e.yaml", "/repo/orders/nested/deep/x.e2e.yaml" },
            selected);
    }

    [Fact]
    public void PathFilter_SingleStar_DoesNotCrossSeparators()
    {
        var all = new[]
        {
            Scenario("/repo/orders/place.e2e.yaml"),
            Scenario("/repo/orders/nested/deep.e2e.yaml"),
        };

        // 'orders/*.e2e.yaml' matches a file directly in orders/, not a nested one.
        var selected = Apply(all, Criteria(pathGlob: "orders/*.e2e.yaml"));
        Assert.Equal(new[] { "/repo/orders/place.e2e.yaml" }, selected);
    }

    [Fact]
    public void PathFilter_QuestionMark_MatchesSingleChar()
    {
        var all = new[]
        {
            Scenario("/repo/t1.e2e.yaml"),
            Scenario("/repo/t2.e2e.yaml"),
            Scenario("/repo/t10.e2e.yaml"),
        };

        var selected = Apply(all, Criteria(pathGlob: "t?.e2e.yaml"));
        Assert.Equal(new[] { "/repo/t1.e2e.yaml", "/repo/t2.e2e.yaml" }, selected);
    }

    [Fact]
    public void PathFilter_NormalisesWindowsBackslashes_AgainstForwardSlashGlob()
    {
        // A Windows-style absolute path with backslashes must match a '/'-glob.
        var all = new[] { Scenario(@"C:\repo\orders\place.e2e.yaml") };

        var selected = Apply(all, Criteria(pathGlob: "orders/**"));
        Assert.Single(selected);
    }

    // ---- AND across: tag + path -------------------------------------------------------

    [Fact]
    public void TagAndPath_BothMustHold()
    {
        var all = new[]
        {
            Scenario("/repo/orders/a.e2e.yaml", tags: "smoke"),
            Scenario("/repo/orders/b.e2e.yaml", tags: "nightly"), // wrong tag
            Scenario("/repo/billing/c.e2e.yaml", tags: "smoke"),  // wrong path
        };

        var selected = Apply(all, Criteria(tags: new[] { "smoke" }, pathGlob: "orders/**"));
        Assert.Equal(new[] { "/repo/orders/a.e2e.yaml" }, selected);
    }

    // ---- Change-set dimension ---------------------------------------------------------

    [Fact]
    public void ChangedSince_OnlySelectsChangedFiles()
    {
        var all = new[]
        {
            Scenario("/repo/a.e2e.yaml"),
            Scenario("/repo/b.e2e.yaml"),
        };

        var changeSet = new FakeChangeSet("/repo/a.e2e.yaml");
        var selected = Apply(all, Criteria(changedSinceRef: "main"), changeSet);
        Assert.Equal(new[] { "/repo/a.e2e.yaml" }, selected);
    }

    [Fact]
    public void ChangedSince_Null_IgnoresChangeSetEntirely()
    {
        var all = new[] { Scenario("/repo/a.e2e.yaml"), Scenario("/repo/b.e2e.yaml") };

        // Even with a change-set that reports nothing changed, a null ref means "no filter".
        var selected = Apply(all, SelectionCriteria.None, new FakeChangeSet());
        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void ChangedSinceAndTag_BothMustHold_AndAcross()
    {
        var all = new[]
        {
            Scenario("/repo/a.e2e.yaml", tags: "smoke"),   // changed + tagged
            Scenario("/repo/b.e2e.yaml", tags: "smoke"),   // tagged but not changed
            Scenario("/repo/c.e2e.yaml", tags: "nightly"), // changed but wrong tag
        };

        var changeSet = new FakeChangeSet("/repo/a.e2e.yaml", "/repo/c.e2e.yaml");
        var selected = Apply(
            all,
            Criteria(tags: new[] { "smoke" }, changedSinceRef: "main"),
            changeSet);

        Assert.Equal(new[] { "/repo/a.e2e.yaml" }, selected);
    }

    // ---- Parse-failure rule -----------------------------------------------------------

    [Fact]
    public void ParseFailure_IncludedWhenNoMetadataFilter()
    {
        var all = new[]
        {
            Scenario("/repo/good.e2e.yaml", tags: "smoke"),
            ParseFailure("/repo/broken.e2e.yaml"),
        };

        // No tag/owner filter ⇒ the parse-failure is still selectable (reported Inconclusive).
        var selected = Apply(all, SelectionCriteria.None);
        Assert.Contains("/repo/broken.e2e.yaml", selected);
    }

    [Fact]
    public void ParseFailure_ExcludedByTagFilter()
    {
        var all = new[]
        {
            Scenario("/repo/good.e2e.yaml", tags: "smoke"),
            ParseFailure("/repo/broken.e2e.yaml"),
        };

        // A metadata filter is active; the parse-failure has no metadata to match ⇒ excluded.
        var selected = Apply(all, Criteria(tags: new[] { "smoke" }));
        Assert.Equal(new[] { "/repo/good.e2e.yaml" }, selected);
    }

    [Fact]
    public void ParseFailure_ExcludedByOwnerFilter()
    {
        var all = new[]
        {
            Scenario("/repo/good.e2e.yaml", owner: "alice"),
            ParseFailure("/repo/broken.e2e.yaml"),
        };

        var selected = Apply(all, Criteria(owners: new[] { "alice" }));
        Assert.Equal(new[] { "/repo/good.e2e.yaml" }, selected);
    }

    // ---- The RECOVERED-metadata rule (issue #411) -------------------------------------
    //
    // A parse-failure that nonetheless BOUND its document is matched on what it bound. The four
    // rows above stay exactly as they were — they use a failure that recovered nothing, which is
    // still every failure whose YAML did not parse — and these three cover the one class that did.
    //
    // Why it matters here rather than only in the CLI's own rows: selection runs BEFORE the split
    // that hands unbuilt documents to the runner, so a document this method excludes contributes no
    // `security` declaration to the suite's assurance. Excluding it was therefore a silent security
    // false negative, not a missing line of output.

    /// <summary>
    /// A parse-failure of the class that BOUND its document: no <c>Ast</c>, and a recovered
    /// document carrying the metadata the selector matches on.
    /// </summary>
    private static DiscoveredScenario UnbuiltFailure(
        string absolutePath, string? owner = null, params string[] tags) =>
        new(absolutePath, YamlText: "steps: []", Ast: null, ParseError: "boom")
        {
            RecoveredDocument = new E2eDocument(
                Metadata: new MetadataSpec(
                    Name: null,
                    Owner: owner,
                    Tags: tags.Length == 0 ? null : tags.ToList(),
                    Description: null,
                    SchemaVersion: null),
                Environment: null,
                Variables: null,
                Steps: new List<StepSpec>()),
        };

    [Fact]
    public void UnbuiltFailure_SelectedByATagItsRecoveredMetadataCarries()
    {
        var all = new[]
        {
            Scenario("/repo/good.e2e.yaml", tags: "smoke"),
            UnbuiltFailure("/repo/broken.e2e.yaml", owner: null, "smoke"),
        };

        // Before #411 this answered `Ast?.Metadata`, which is null for EVERY failure, so the
        // broken file was excluded however it was tagged.
        var selected = Apply(all, Criteria(tags: new[] { "smoke" }));
        Assert.Equal(new[] { "/repo/good.e2e.yaml", "/repo/broken.e2e.yaml" }, selected);
    }

    [Fact]
    public void UnbuiltFailure_SelectedByAnOwnerItsRecoveredMetadataCarries()
    {
        var all = new[]
        {
            Scenario("/repo/good.e2e.yaml", owner: "alice"),
            UnbuiltFailure("/repo/broken.e2e.yaml", owner: "alice"),
        };

        var selected = Apply(all, Criteria(owners: new[] { "alice" }));
        Assert.Equal(new[] { "/repo/good.e2e.yaml", "/repo/broken.e2e.yaml" }, selected);
    }

    /// <summary>
    /// The control, and it is what makes the two rows above a fix rather than an exemption: a
    /// recovered document whose metadata does NOT satisfy the filter is still excluded. Recovery
    /// makes the selector able to answer; it does not make it answer yes.
    /// </summary>
    [Fact]
    public void UnbuiltFailure_ExcludedWhenItsRecoveredMetadataDoesNotMatch()
    {
        var all = new[]
        {
            Scenario("/repo/good.e2e.yaml", tags: "smoke"),
            UnbuiltFailure("/repo/broken.e2e.yaml", owner: null, "billing"),
            UnbuiltFailure("/repo/untagged.e2e.yaml"),
        };

        var selected = Apply(all, Criteria(tags: new[] { "smoke" }));
        Assert.Equal(new[] { "/repo/good.e2e.yaml" }, selected);
    }

    /// <summary>
    /// <c>matchRecoveredMetadata: false</c> — the opt-out <c>--watch</c> passes. The recovered
    /// metadata is not read, so the SAME inputs that select the broken file above exclude it here,
    /// which is the pre-#411 answer and the one the watch path's single-file rule was written
    /// against.
    /// </summary>
    [Fact]
    public void UnbuiltFailure_ExcludedByAMetadataFilter_WhenRecoveredMetadataIsNotMatched()
    {
        var all = new[]
        {
            Scenario("/repo/good.e2e.yaml", tags: "smoke"),
            UnbuiltFailure("/repo/broken.e2e.yaml", owner: null, "smoke"),
        };

        var selected = Apply(
            all, Criteria(tags: new[] { "smoke" }), matchRecoveredMetadata: false);
        Assert.Equal(new[] { "/repo/good.e2e.yaml" }, selected);
    }

    /// <summary>
    /// The opt-out scopes out the RECOVERY, not the parse-failure rule: with no metadata filter
    /// active every failure is still included, exactly as it is with the recovery on. This is why
    /// an UNFILTERED <c>--watch</c> over two files still resolves to 2.
    /// </summary>
    [Fact]
    public void UnbuiltFailure_StillIncluded_WhenNoMetadataFilterAndRecoveryIsNotMatched()
    {
        var all = new[]
        {
            Scenario("/repo/good.e2e.yaml", tags: "smoke"),
            UnbuiltFailure("/repo/broken.e2e.yaml", owner: null, "smoke"),
        };

        var selected = Apply(all, Criteria(), matchRecoveredMetadata: false);
        Assert.Equal(new[] { "/repo/good.e2e.yaml", "/repo/broken.e2e.yaml" }, selected);
    }

    [Fact]
    public void ParseFailure_StillFilteredByPath_WhenNoMetadataFilter()
    {
        // Path is identity-based, so it applies to parse-failures too (no metadata needed).
        var all = new[]
        {
            ParseFailure("/repo/orders/broken.e2e.yaml"),
            ParseFailure("/repo/billing/broken.e2e.yaml"),
        };

        var selected = Apply(all, Criteria(pathGlob: "orders/**"));
        Assert.Equal(new[] { "/repo/orders/broken.e2e.yaml" }, selected);
    }

    [Fact]
    public void NothingMatches_ReturnsEmpty_NotNull()
    {
        var all = new[] { Scenario("/repo/a.e2e.yaml", tags: "smoke") };
        var selected = ScenarioSelector.Apply(
            all,
            Criteria(tags: new[] { "nonexistent" }),
            NullChangeSet.Instance);

        Assert.NotNull(selected);
        Assert.Empty(selected);
    }
}
