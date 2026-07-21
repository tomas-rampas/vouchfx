// Vouchfx.Cli.Tests — `list` argument parsing tests (#260). No Docker.
//
// Asserts that the `list` command's --step-types and --json flags parse to the right
// values, and that the fully built command accepts them together (and alone, and with
// neither — --step-types is the default mode when omitted). Mirrors
// NoDecorationsArgParsingTests / EventsArgParsingTests. The Docker-free Execute
// orchestration and its sealed-registry output are tested in ListCommandTests.

using System.CommandLine;
using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ListArgParsingTests
{
    private static bool ParseStepTypes(params string[] args)
    {
        var option = ListCommand.BuildStepTypesOption();

        var command = new Command("list");
        command.Add(option);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(option);
    }

    private static bool ParseJson(params string[] args)
    {
        var option = ListCommand.BuildJsonOption();

        var command = new Command("list");
        command.Add(option);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(option);
    }

    [Fact]
    public void StepTypesFlag_Present_ParsesToTrue()
    {
        Assert.True(ParseStepTypes("--step-types"));
    }

    [Fact]
    public void StepTypesFlag_Absent_ParsesToFalse()
    {
        Assert.False(ParseStepTypes());
    }

    [Fact]
    public void JsonFlag_Present_ParsesToTrue()
    {
        Assert.True(ParseJson("--json"));
    }

    [Fact]
    public void JsonFlag_Absent_ParsesToFalse()
    {
        Assert.False(ParseJson());
    }

    [Fact]
    public void FullListCommand_AcceptsStepTypesAndJsonTogether()
    {
        var command = ListCommand.Build();
        var result = command.Parse(new[] { "--step-types", "--json" });

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void FullListCommand_AcceptsNoFlagsAtAll()
    {
        // --step-types is the default mode when omitted: a bare `list` must still parse.
        var command = ListCommand.Build();
        var result = command.Parse(Array.Empty<string>());

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void FullListCommand_AcceptsJsonAlone()
    {
        var command = ListCommand.Build();
        var result = command.Parse(new[] { "--json" });

        Assert.Empty(result.Errors);
    }
}
