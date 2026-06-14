// Vouchfx.Cli.Tests — --no-decorations option parsing tests (S10-G-03a). No Docker.
//
// Asserts that the `run` command's --no-decorations flag parses into the right bool value
// (--no-decorations → true; absent → false), and that it is accepted alongside the existing
// flags (path / selection / parallel / report / events). The flag opts OUT of the optional
// terminal decorations (ANSI colour + per-verdict shape glyph), giving a plain, screen-reader
// / CI-friendly output; the verdict TEXT tokens (the WCAG-1.4.1 guarantee) are unconditional
// and unaffected by it. The full run path needs Docker and is NOT invoked here — only the
// parse is tested. Mirrors EventsArgParsingTests / FailOnArgParsingTests.

using System.CommandLine;
using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class NoDecorationsArgParsingTests
{
    // Mirrors RunCommand.Build's option wiring so the --no-decorations parse can be exercised
    // alone.
    private static bool ParseNoDecorations(params string[] args)
    {
        var option = RunCommand.BuildNoDecorationsOption();

        var command = new Command("run");
        command.Add(option);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(option);
    }

    [Fact]
    public void NoDecorationsFlag_Present_ParsesToTrue()
    {
        Assert.True(ParseNoDecorations("--no-decorations"));
    }

    [Fact]
    public void NoDecorationsFlag_Absent_ParsesToFalse()
    {
        Assert.False(ParseNoDecorations());
    }

    [Fact]
    public void FullRunCommand_AcceptsNoDecorationsAlongsideOtherFlags()
    {
        // The built `run` command must accept --no-decorations alongside the positional <path>
        // and the existing selection / parallel / report / events flags.
        var command = RunCommand.Build();
        var result = command.Parse(new[]
        {
            "scenarios",
            "--no-decorations",
            "--events", "out/events.jsonl",
            "--html", "out/report.html",
            "--junit", "out/report.xml",
            "--parallel", "2",
            "--tag", "smoke",
        });

        Assert.Empty(result.Errors);
    }
}
