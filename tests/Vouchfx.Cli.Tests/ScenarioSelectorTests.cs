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
        IChangeSet? changeSet = null) =>
        ScenarioSelector
            .Apply(all, criteria, changeSet ?? NullChangeSet.Instance)
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
