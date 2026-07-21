// Vouchfx.Cli.Tests — --events-stream option parsing tests (issue #258). No Docker.
//
// Asserts that the `run` command's --events-stream option parses into the right string?
// value (--events-stream s.jsonl → "s.jsonl"; absent → null), and that it is accepted
// alongside the existing flags (path / selection / parallel / report / --events). The full
// run path needs Docker and is NOT invoked here — only the parse is tested. Mirrors
// EventsArgParsingTests (the sibling --events / --json option this one is deliberately
// SEPARATE from — the two are independent options with independent files).

using System.CommandLine;
using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class EventsStreamArgParsingTests
{
    // Mirrors RunCommand.Build's option wiring so the --events-stream parse can be exercised
    // alone.
    private static string? ParseEventsStream(params string[] args)
    {
        var eventsStreamOption = RunCommand.BuildEventsStreamOption();

        var command = new Command("run");
        command.Add(eventsStreamOption);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(eventsStreamOption);
    }

    [Fact]
    public void EventsStreamPath_ParsesToPath()
    {
        Assert.Equal("s.jsonl", ParseEventsStream("--events-stream", "s.jsonl"));
    }

    [Fact]
    public void EventsStreamAbsent_ParsesToNull()
    {
        Assert.Null(ParseEventsStream());
    }

    [Fact]
    public void FullRunCommand_AcceptsEventsStreamAlongsideOtherFlags()
    {
        // The built `run` command must accept --events-stream alongside the positional <path>
        // and the existing selection / parallel / report flags.
        var command = RunCommand.Build();
        var result = command.Parse(new[]
        {
            "scenarios",
            "--events-stream", "out/events-stream.jsonl",
            "--html", "out/report.html",
            "--junit", "out/report.xml",
            "--parallel", "2",
            "--tag", "smoke",
        });

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void FullRunCommand_AcceptsEventsStreamTogetherWithEvents()
    {
        // --events-stream and --events / --json are two INDEPENDENT options — both must be
        // accepted together, each binding to its own distinct path.
        var command = RunCommand.Build();
        var result = command.Parse(new[]
        {
            "scenarios",
            "--events", "out/events.jsonl",
            "--events-stream", "out/events-stream.jsonl",
        });

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void EventsStreamOption_IsDistinctFromEventsOption()
    {
        // --events-stream must NOT be an alias of --events / --json: parsing --events must
        // leave --events-stream at its default (null), and vice versa.
        var eventsOption = RunCommand.BuildEventsOption();
        var eventsStreamOption = RunCommand.BuildEventsStreamOption();

        var command = new Command("run");
        command.Add(eventsOption);
        command.Add(eventsStreamOption);

        var result = command.Parse(new[] { "--events", "only-events.jsonl" });
        Assert.Empty(result.Errors);

        Assert.Equal("only-events.jsonl", result.GetValue(eventsOption));
        Assert.Null(result.GetValue(eventsStreamOption));
    }
}
