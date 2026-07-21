// Vouchfx.Cli.Tests — `validate` argument parsing tests (#260). No Docker.
//
// Asserts that the `validate` command's <path> argument (default ".") and --json flag
// parse to the right values, and that the fully built command accepts them together.
// Mirrors EventsArgParsingTests / NoDecorationsArgParsingTests: parse each piece in
// isolation via its BuildXxx helper, then assert the full command accepts them together.
// The Docker-free Execute orchestration itself is tested in ValidateCommandTests.

using System.CommandLine;
using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ValidateArgParsingTests
{
    private static string? ParsePath(params string[] args)
    {
        var pathArgument = ValidateCommand.BuildPathArgument();

        var command = new Command("validate");
        command.Add(pathArgument);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(pathArgument);
    }

    private static bool ParseJson(params string[] args)
    {
        var jsonOption = ValidateCommand.BuildJsonOption();

        var command = new Command("validate");
        command.Add(jsonOption);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(jsonOption);
    }

    [Fact]
    public void Path_Absent_DefaultsToDot()
    {
        Assert.Equal(".", ParsePath());
    }

    [Fact]
    public void Path_Present_ParsesToGivenValue()
    {
        Assert.Equal("scenarios", ParsePath("scenarios"));
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
    public void FullValidateCommand_AcceptsPathAndJsonTogether()
    {
        var command = ValidateCommand.Build();
        var result = command.Parse(new[] { "scenarios", "--json" });

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void FullValidateCommand_AcceptsBarePathOnly()
    {
        var command = ValidateCommand.Build();
        var result = command.Parse(new[] { "scenarios" });

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void FullValidateCommand_AcceptsNoArgumentsAtAll()
    {
        var command = ValidateCommand.Build();
        var result = command.Parse(Array.Empty<string>());

        Assert.Empty(result.Errors);
    }
}
