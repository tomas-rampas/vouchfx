// Vouchfx.Cli.Tests — --events / --json option parsing tests (S10). No Docker.
//
// Asserts that the `run` command's --events option (and its --json alias) parse into the
// right string? value (--events r.jsonl → "r.jsonl"; --json r.jsonl → "r.jsonl"; absent →
// null), and that it is accepted alongside the existing flags (path / selection / parallel /
// report). --events and --json are ONE option with two names, so both must bind to the SAME
// option instance and therefore the same parsed value.  The full run path needs Docker and is
// NOT invoked here — only the parse is tested.  Mirrors ReportArgParsingTests.

using System.CommandLine;
using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class EventsArgParsingTests
{
    // Mirrors RunCommand.Build's option wiring so the --events / --json parse can be exercised
    // alone.  ONE option, two names: parsing either name yields the same option value.
    private static string? ParseEvents(params string[] args)
    {
        var eventsOption = RunCommand.BuildEventsOption();

        var command = new Command("run");
        command.Add(eventsOption);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(eventsOption);
    }

    [Fact]
    public void EventsPath_ParsesToPath()
    {
        Assert.Equal("r.jsonl", ParseEvents("--events", "r.jsonl"));
    }

    [Fact]
    public void JsonAlias_ParsesToSamePath()
    {
        // --json is an ALIAS of --events (one option, two names): it must bind the same value.
        Assert.Equal("r.jsonl", ParseEvents("--json", "r.jsonl"));
    }

    [Fact]
    public void EventsAbsent_ParsesToNull()
    {
        Assert.Null(ParseEvents());
    }

    [Fact]
    public void FullRunCommand_AcceptsEventsAlongsideOtherFlags()
    {
        // The built `run` command must accept --events alongside the positional <path> and the
        // existing selection / parallel / report flags.
        var command = RunCommand.Build();
        var result = command.Parse(new[]
        {
            "scenarios",
            "--events", "out/events.jsonl",
            "--html", "out/report.html",
            "--junit", "out/report.xml",
            "--parallel", "2",
            "--tag", "smoke",
        });

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void FullRunCommand_AcceptsJsonAlias()
    {
        // The --json alias must be accepted by the fully-built `run` command too.
        var command = RunCommand.Build();
        var result = command.Parse(new[]
        {
            "scenarios",
            "--json", "out/events.jsonl",
        });

        Assert.Empty(result.Errors);
    }
}
