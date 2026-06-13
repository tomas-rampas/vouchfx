// Vouchfx.Cli.Tests — --fail-on-env-error / --fail-on-inconclusive parsing tests (S09-C-03).
// No Docker.
//
// Asserts that the `run` command's two opt-in CI-gating flags parse (--fail-on-env-error /
// --fail-on-inconclusive → true; absent → false), and that they compose with the positional
// <path> and the existing selection / parallel / watch flags.  These flags do NOT change the
// default — only Fail breaks CI unless one is set, at which point an EnvironmentError verdict
// exits 3 (its flag) / an Inconclusive verdict exits 4 (its flag).
//
// Mirrors WatchArgParsingTests / ParallelArgParsingTests: parse each option in isolation via
// its BuildXxxOption() helper, then assert the full `run` command accepts the flags together.

using System.CommandLine;
using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class FailOnArgParsingTests
{
    private static bool ParseFailOnEnvError(params string[] args)
    {
        var option = RunCommand.BuildFailOnEnvironmentErrorOption();

        var command = new Command("run");
        command.Add(option);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(option);
    }

    private static bool ParseFailOnInconclusive(params string[] args)
    {
        var option = RunCommand.BuildFailOnInconclusiveOption();

        var command = new Command("run");
        command.Add(option);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(option);
    }

    [Fact]
    public void FailOnEnvError_Present_ParsesToTrue()
    {
        Assert.True(ParseFailOnEnvError("--fail-on-env-error"));
    }

    [Fact]
    public void FailOnEnvError_Absent_ParsesToFalse()
    {
        Assert.False(ParseFailOnEnvError());
    }

    [Fact]
    public void FailOnInconclusive_Present_ParsesToTrue()
    {
        Assert.True(ParseFailOnInconclusive("--fail-on-inconclusive"));
    }

    [Fact]
    public void FailOnInconclusive_Absent_ParsesToFalse()
    {
        Assert.False(ParseFailOnInconclusive());
    }

    [Fact]
    public void FullRunCommand_AcceptsBothFailOnFlagsAlongsidePathAndOtherFlags()
    {
        // The built `run` command must accept both opt-in CI-gating flags alongside the
        // positional <path>, a selection flag, and --parallel without a parse error.
        var command = RunCommand.Build();
        var result = command.Parse(new[]
        {
            "scenarios",
            "--fail-on-env-error",
            "--fail-on-inconclusive",
            "--tag", "smoke",
            "--parallel", "2",
        });

        Assert.Empty(result.Errors);
    }
}
