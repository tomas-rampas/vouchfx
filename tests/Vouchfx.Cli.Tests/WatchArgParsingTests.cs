// Vouchfx.Cli.Tests — --watch option parsing tests (S08-C-01). No Docker.
//
// Asserts that the `run` command's --watch flag parses (--watch → true; absent → false),
// that it composes with the positional <file> and selection flags, and that the
// --watch + --parallel combination is rejected as a usage error (exit 2) BEFORE anything
// runs — they are mutually exclusive (one watches a single file and keeps one topology
// alive; the other fans many scenarios across many topologies).

using System.CommandLine;
using Vouchfx.Cli;
using Vouchfx.Cli.Selection;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class WatchArgParsingTests
{
    private static bool ParseWatch(params string[] args)
    {
        var watchOption = RunCommand.BuildWatchOption();

        var command = new Command("run");
        command.Add(watchOption);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(watchOption);
    }

    [Fact]
    public void WatchFlag_Present_ParsesToTrue()
    {
        Assert.True(ParseWatch("--watch"));
    }

    [Fact]
    public void WatchFlag_Absent_ParsesToFalse()
    {
        Assert.False(ParseWatch());
    }

    [Fact]
    public void FullRunCommand_AcceptsWatchAlongsidePathAndSelectionFlags()
    {
        var command = RunCommand.Build();
        var result = command.Parse(new[]
        {
            "scenarios/login.e2e.yaml", "--watch", "--tag", "smoke",
        });

        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ExecuteAsync_WatchWithParallel_ReturnsUsageError_AndWritesMessage()
    {
        // --watch + --parallel are mutually exclusive: ExecuteAsync rejects the combo as a
        // usage error (exit 2) BEFORE discovering or running anything — no Docker is touched.
        var sw = new StringWriter();

        var exitCode = await RunCommand.ExecuteAsync(
            path: ".",
            criteria: SelectionCriteria.None,
            parallel: 2,
            watch: true,
            failOnEnvironmentError: false,
            failOnInconclusive: false,
            htmlReportPath: null,
            junitReportPath: null,
            eventsReportPath: null,
            eventsStreamPath: null,
            decorate: false,
            output: sw,
            telemetryHook: null,
            cancellationToken: default);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains(
            "--watch cannot be combined with --parallel",
            sw.ToString(),
            StringComparison.Ordinal);
    }
}
