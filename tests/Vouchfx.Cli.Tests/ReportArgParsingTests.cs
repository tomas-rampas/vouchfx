// Vouchfx.Cli.Tests — --html / --junit option parsing tests (S09-T3). No Docker.
//
// Asserts that the `run` command's --html and --junit options parse into the right
// string? value (--html r.html → "r.html"; --junit r.xml → "r.xml"; absent → null),
// and that they are accepted alongside the existing flags (path / selection / parallel).
// The full run path needs Docker and is NOT invoked here — only the parse is tested.
// Mirrors ParallelArgParsingTests / FailOnArgParsingTests.

using System.CommandLine;
using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ReportArgParsingTests
{
    // Mirrors RunCommand.Build's option wiring so the --html parse can be exercised alone.
    private static string? ParseHtml(params string[] args)
    {
        var htmlOption = RunCommand.BuildHtmlReportOption();

        var command = new Command("run");
        command.Add(htmlOption);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(htmlOption);
    }

    private static string? ParseJunit(params string[] args)
    {
        var junitOption = RunCommand.BuildJunitReportOption();

        var command = new Command("run");
        command.Add(junitOption);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(junitOption);
    }

    [Fact]
    public void HtmlPath_ParsesToPath()
    {
        Assert.Equal("r.html", ParseHtml("--html", "r.html"));
    }

    [Fact]
    public void HtmlAbsent_ParsesToNull()
    {
        Assert.Null(ParseHtml());
    }

    [Fact]
    public void JunitPath_ParsesToPath()
    {
        Assert.Equal("r.xml", ParseJunit("--junit", "r.xml"));
    }

    [Fact]
    public void JunitAbsent_ParsesToNull()
    {
        Assert.Null(ParseJunit());
    }

    [Fact]
    public void FullRunCommand_AcceptsHtmlAndJunitAlongsideOtherFlags()
    {
        // The built `run` command must accept --html / --junit alongside the positional
        // <path> and the existing selection / parallel flags.
        var command = RunCommand.Build();
        var result = command.Parse(new[]
        {
            "scenarios",
            "--html", "out/report.html",
            "--junit", "out/report.xml",
            "--parallel", "2",
            "--tag", "smoke",
        });

        Assert.Empty(result.Errors);
    }
}
