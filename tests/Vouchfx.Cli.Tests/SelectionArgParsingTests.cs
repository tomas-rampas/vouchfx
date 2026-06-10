// Vouchfx.Cli.Tests — selection-option parsing tests (S07-C-02). No Docker.
//
// Asserts that the `run` command's --tag / --owner / --path / --changed-since options parse
// into the right SelectionCriteria (repeatable tag/owner accumulate; path/changed-since are
// single-valued), and that a bare `run` yields the empty (select-all) criteria.

using System.CommandLine;
using System.Linq;
using Vouchfx.Cli;
using Vouchfx.Cli.Selection;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class SelectionArgParsingTests
{
    // Mirrors RunCommand.Build's option wiring so the parse can be exercised in isolation.
    private static SelectionCriteria ParseCriteria(params string[] args)
    {
        var tagOption = RunCommand.BuildTagOption();
        var ownerOption = RunCommand.BuildOwnerOption();
        var pathOption = RunCommand.BuildPathOption();
        var changedSinceOption = RunCommand.BuildChangedSinceOption();

        var command = new Command("run");
        command.Add(tagOption);
        command.Add(ownerOption);
        command.Add(pathOption);
        command.Add(changedSinceOption);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return RunCommand.BuildCriteria(result, tagOption, ownerOption, pathOption, changedSinceOption);
    }

    [Fact]
    public void AllOptions_ParseIntoCriteria()
    {
        var criteria = ParseCriteria(
            "--tag", "a", "--tag", "b",
            "--owner", "x",
            "--path", "orders/**",
            "--changed-since", "main");

        Assert.Equal(new[] { "a", "b" }, criteria.Tags);
        Assert.Equal(new[] { "x" }, criteria.Owners);
        Assert.Equal("orders/**", criteria.PathGlob);
        Assert.Equal("main", criteria.ChangedSinceRef);
    }

    [Fact]
    public void RepeatedOwner_Accumulates()
    {
        var criteria = ParseCriteria("--owner", "alice", "--owner", "bob");
        Assert.Equal(new[] { "alice", "bob" }, criteria.Owners);
    }

    [Fact]
    public void BareRun_YieldsEmptyCriteria_SelectingAll()
    {
        var criteria = ParseCriteria();

        Assert.Empty(criteria.Tags);
        Assert.Empty(criteria.Owners);
        Assert.Null(criteria.PathGlob);
        Assert.Null(criteria.ChangedSinceRef);
        Assert.True(criteria.IsEmpty);
    }

    [Fact]
    public void EmptyPath_NormalisesToNull()
    {
        // A whitespace path is treated as "not supplied" (no path constraint).
        var criteria = ParseCriteria("--path", "   ");
        Assert.Null(criteria.PathGlob);
    }

    [Fact]
    public void FullRunCommand_AcceptsPathArgumentAndSelectionFlags_Together()
    {
        // The built `run` command must accept the positional <path> alongside the flags.
        var command = RunCommand.Build();
        var result = command.Parse(new[]
        {
            "scenarios", "--tag", "smoke", "--changed-since", "main",
        });

        Assert.Empty(result.Errors);
    }
}
