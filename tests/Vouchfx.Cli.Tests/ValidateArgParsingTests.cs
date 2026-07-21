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

    // Peer-review QUESTION-1: an unrecognised flag must be a genuine System.CommandLine
    // parse error. Two things had to be got right to exercise this honestly, and the
    // second one surfaced a PRE-EXISTING inaccuracy worth recording:
    //   • A bare unrecognised token must NOT be swallowed by `validate`'s own optional
    //     positional <path> argument (System.CommandLine's permissive fallback binds an
    //     unmatched option-looking token to an open positional slot instead of erroring —
    //     confirmed by inspection: `validate --this-flag-does-not-exist` alone parses
    //     CLEANLY with <path> bound to the literal string "--this-flag-does-not-exist",
    //     which is NOT what this test wants to exercise). Supplying a real path value
    //     first fills that one positional slot, so a SECOND unrecognised token has
    //     nowhere left to go and is genuinely rejected.
    //   • Program.cs's own header comment claims a parse error exits 2
    //     ("System.CommandLine resolves ... parse errors itself (exit code 2 on a parse
    //     error)"). Empirically (confirmed here, both via a standalone Command AND via a
    //     RootCommand shaped exactly like Program.cs's own) a genuine parse error exits
    //     **1**, matching System.CommandLine's own documented default ("prints errors to
    //     stderr, prints help to stdout, returns 1") — NOT 2. ExitCodes.UsageError (2) is
    //     produced ONLY by the application's OWN detected usage errors (a bad/missing
    //     <path>, a bad --parallel value, …), never by System.CommandLine's parse-error
    //     path itself. This is a pre-existing discrepancy in Program.cs's comment, out of
    //     scope for this fix (not touched here) — flagged for the maintainer.
    private static RootCommand BuildRootWithValidate()
    {
        var root = new RootCommand("test root");
        root.Add(ValidateCommand.Build());
        return root;
    }

    [Fact]
    public void Validate_UnrecognisedExtraToken_ParseResultHasErrors()
    {
        var root = BuildRootWithValidate();
        var result = root.Parse(new[] { "validate", "scenarios", "--this-flag-does-not-exist" });

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Validate_UnrecognisedExtraToken_InvokeReturnsSystemCommandLinesOwnParseErrorExitCode()
    {
        var root = BuildRootWithValidate();
        var parseResult = root.Parse(new[] { "validate", "scenarios", "--this-flag-does-not-exist" });

        var exitCode = await parseResult.InvokeAsync(new InvocationConfiguration());

        // 1, NOT ExitCodes.UsageError (2) — System.CommandLine's own documented default
        // for a parse error, verified empirically (see the remarks above).
        Assert.Equal(1, exitCode);
    }
}
