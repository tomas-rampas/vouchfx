// Vouchfx.Cli.Tests — InvocationScope tests (vouchfx-mcp#17; peer-review MINOR-1 fix). No
// Docker, cross-platform (pure string-array predicates, no Console/process interaction).
//
// Program.cs's ProcessTerminationTimeout gate widens the teardown budget ONLY for the `run`
// subcommand (the only one that stands up a container topology); `validate` / `list` /
// `telemetry` (and a bare --help / --version) must be left at System.CommandLine's own ~2s
// default. These tests pin IsRunInvocation / IsWatchInvocation directly — Program.cs's own
// wiring (a top-level-statement script, not a class) is not itself unit-testable, so this is the
// seam the peer review asked to cover.
//
// Theory cases pass a single space-separated string rather than a string[] InlineData argument:
// xUnit's InlineData(params object[]) flattens a string[]-typed argument (CS0182) rather than
// treating it as one array value, so a space-separated line + a Split helper sidesteps that
// entirely while staying just as readable.

using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class InvocationScopeTests
{
    private static string[] Split(string argsLine) =>
        argsLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Theory]
    [InlineData("run")]
    [InlineData("run scenarios")]
    [InlineData("run scenarios --tag smoke")]
    [InlineData("run --watch")]
    [InlineData("run scenario.e2e.yaml --watch")]
    public void IsRunInvocation_RunSubcommandPresent_ReturnsTrue(string argsLine)
    {
        Assert.True(InvocationScope.IsRunInvocation(Split(argsLine)));
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("validate .")]
    [InlineData("validate . --json")]
    [InlineData("list")]
    [InlineData("list --step-types")]
    [InlineData("telemetry")]
    [InlineData("telemetry status")]
    [InlineData("telemetry enable")]
    [InlineData("")]
    [InlineData("--help")]
    [InlineData("--version")]
    public void IsRunInvocation_NonRunInvocation_ReturnsFalse(string argsLine)
    {
        Assert.False(InvocationScope.IsRunInvocation(Split(argsLine)));
    }

    [Theory]
    [InlineData("run --watch")]
    [InlineData("run scenario.e2e.yaml --watch")]
    [InlineData("run --watch --tag smoke")]
    public void IsWatchInvocation_WatchFlagPresent_ReturnsTrue(string argsLine)
    {
        Assert.True(InvocationScope.IsWatchInvocation(Split(argsLine)));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("run scenarios")]
    [InlineData("validate")]
    [InlineData("list")]
    [InlineData("telemetry status")]
    [InlineData("")]
    public void IsWatchInvocation_WatchFlagAbsent_ReturnsFalse(string argsLine)
    {
        Assert.False(InvocationScope.IsWatchInvocation(Split(argsLine)));
    }

    // ── The two scenarios Program.cs's gate must reproduce EXACTLY as before this fix ──────────

    [Fact]
    public void PlainRun_IsRunInvocation_AndNotWatch_SoGetsTheWidenedFiniteBudget()
    {
        // `vouchfx run` (no --watch): Program.cs sets ProcessTerminationTimeout to the widened,
        // FINITE budget (RunCommand.TeardownBudgetSeconds) — unchanged by the run-scoping fix.
        var args = new[] { "run" };

        Assert.True(InvocationScope.IsRunInvocation(args));
        Assert.False(InvocationScope.IsWatchInvocation(args));
    }

    [Fact]
    public void RunWithWatch_IsRunInvocation_AndWatch_SoGetsTheDisabledBudget()
    {
        // `vouchfx run --watch`: Program.cs sets ProcessTerminationTimeout to null (disabled) —
        // unchanged by the run-scoping fix.
        var args = new[] { "run", "--watch" };

        Assert.True(InvocationScope.IsRunInvocation(args));
        Assert.True(InvocationScope.IsWatchInvocation(args));
    }

    [Fact]
    public void ValidateInvocation_IsNotRunInvocation_SoProcessTerminationTimeoutIsLeftAtDefault()
    {
        // `vouchfx validate .`: NOT a run invocation, so Program.cs's gate never touches
        // ProcessTerminationTimeout at all — System.CommandLine's own ~2s default applies. This
        // is the exact regression the peer review caught: an earlier gate (checking only
        // IsWatchInvocation) widened this command's budget by accident.
        var args = new[] { "validate", "." };

        Assert.False(InvocationScope.IsRunInvocation(args));
    }
}
