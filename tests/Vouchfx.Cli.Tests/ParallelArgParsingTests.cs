// Vouchfx.Cli.Tests — --parallel option parsing tests (S08-T1). No Docker.
//
// Asserts that the `run` command's --parallel option parses into the right int? value
// (--parallel 3 → 3; absent → null), and that the >= 1 contract is enforced as a usage
// error (exit 2) rather than crashing or running.  The full run path needs Docker and is
// NOT invoked here — only the parse + the ExecuteAsync usage-error short-circuit are tested.

using System.CommandLine;
using Vouchfx.Cli;
using Vouchfx.Cli.Selection;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ParallelArgParsingTests
{
    // Mirrors RunCommand.Build's option wiring so the --parallel parse can be exercised alone.
    private static int? ParseParallel(params string[] args)
    {
        var parallelOption = RunCommand.BuildParallelOption();

        var command = new Command("run");
        command.Add(parallelOption);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(parallelOption);
    }

    [Fact]
    public void ParallelThree_ParsesToThree()
    {
        Assert.Equal(3, ParseParallel("--parallel", "3"));
    }

    [Fact]
    public void ParallelOne_ParsesToOne()
    {
        Assert.Equal(1, ParseParallel("--parallel", "1"));
    }

    [Fact]
    public void ParallelAbsent_ParsesToNull()
    {
        Assert.Null(ParseParallel());
    }

    [Fact]
    public void ParallelZero_BindsToZero_ButExecuteRejectsAsUsageError()
    {
        // System.CommandLine binds 0 fine (it is a valid int); the >= 1 contract is the
        // engine's, enforced by ExecuteAsync.  The parse itself produces 0…
        Assert.Equal(0, ParseParallel("--parallel", "0"));
    }

    [Fact]
    public async Task ExecuteAsync_ParallelZero_ReturnsUsageError_AndWritesMessage()
    {
        // …and ExecuteAsync short-circuits a < 1 value to UsageError (exit 2) BEFORE discovering
        // or running anything — so no Docker/topology is touched.  The discovery root is
        // irrelevant because the --parallel validation runs first.
        var sw = new StringWriter();

        var exitCode = await RunCommand.ExecuteAsync(
            path: ".",
            criteria: SelectionCriteria.None,
            parallel: 0,
            output: sw,
            cancellationToken: default);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains("--parallel must be 1 or greater.", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FullRunCommand_AcceptsParallelAlongsidePathAndSelectionFlags()
    {
        // The built `run` command must accept --parallel alongside the positional <path> and flags.
        var command = RunCommand.Build();
        var result = command.Parse(new[]
        {
            "scenarios", "--parallel", "2", "--tag", "smoke",
        });

        Assert.Empty(result.Errors);
    }
}
