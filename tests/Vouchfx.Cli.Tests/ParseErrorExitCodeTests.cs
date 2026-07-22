// Vouchfx.Cli.Tests — parse-error exit-code remap tests (issue #269). No Docker.
//
// System.CommandLine's own InvokeAsync default for a parse error (an unrecognised option or
// extra token) is exit code 1 — the SAME code the app's own taxonomy reserves for a genuine
// Verdict.Fail (ExitCodes.TestFailure). Left unremapped, a CI pipeline keying on the exit code
// cannot tell "the suite genuinely failed" apart from "the invocation itself was malformed"
// (e.g. `vouchfx validate <suite> --bogus`). ExitCodes' own doc comment already reserved 2 for
// exactly this case; this file pins the fix that makes the reservation real.
//
// Program.cs (a top-level-statement script, not a class) is not itself unit-testable — see
// InvocationScope's header comment — so the remap decision is factored into the small, pure
// InvocationScope.ResolveExitCode(parseErrorCount, invokeResult), the seam this file pins:
//   • parseErrorCount > 0  → ExitCodes.UsageError (2), REGARDLESS of invokeResult. System.
//     CommandLine still runs its own InvokeAsync to completion first (it prints its errors
//     to stderr and help to stdout exactly as before) — only the PROCESS EXIT CODE Program.cs
//     returns changes.
//   • parseErrorCount == 0 → invokeResult is passed through UNCHANGED (0 / 1 / 2 / 3 / 4,
//     whatever the subcommand action itself decided). A genuine Verdict.Fail (1) is NEVER
//     remapped — only a parse error is.
//
// ParseErrorPreconditionTests exercises the real System.CommandLine wiring — a RootCommand
// built from RunCommand + TelemetryCommand + ValidateCommand + ListCommand exactly as
// Program.cs assembles it — to confirm the helper's precondition is real: an unrecognised
// option on every subcommand genuinely produces a non-empty ParseResult.Errors, while --help
// and --version produce none (System.CommandLine resolves those itself, exit 0 — never
// remapped, because there are no parse errors to trigger the remap).

using System.CommandLine;
using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ResolveExitCodeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ParseErrorsPresent_AlwaysReturnsUsageError_RegardlessOfInvokeResult(int invokeResult)
    {
        Assert.Equal(
            ExitCodes.UsageError,
            InvocationScope.ResolveExitCode(parseErrorCount: 1, invokeResult));
    }

    [Fact]
    public void ManyParseErrors_StillJustTheOneUsageErrorCode()
    {
        Assert.Equal(
            ExitCodes.UsageError,
            InvocationScope.ResolveExitCode(parseErrorCount: 3, invokeResult: 1));
    }

    [Theory]
    [InlineData(ExitCodes.Success)]
    [InlineData(ExitCodes.TestFailure)]
    [InlineData(ExitCodes.UsageError)]
    [InlineData(ExitCodes.EnvironmentError)]
    [InlineData(ExitCodes.Inconclusive)]
    public void NoParseErrors_PassesInvokeResultThroughUnchanged(int invokeResult)
    {
        Assert.Equal(
            invokeResult,
            InvocationScope.ResolveExitCode(parseErrorCount: 0, invokeResult));
    }
}

public sealed class ParseErrorPreconditionTests
{
    // Mirrors Program.cs's own root wiring exactly (see Program.cs) so the precondition the
    // helper above relies on — "a bad option really does produce ParseResult.Errors" — is
    // pinned against the SAME command tree the shipped CLI builds, not a stand-in.
    private static RootCommand BuildFullRoot()
    {
        var root = new RootCommand("test root");
        root.Add(RunCommand.Build());
        root.Add(TelemetryCommand.Build());
        root.Add(ValidateCommand.Build());
        root.Add(ListCommand.Build());
        return root;
    }

    [Fact]
    public void Run_UnrecognisedOption_ProducesParseErrors()
    {
        // `run` has an optional <path> positional (like `validate`) that would otherwise
        // swallow a single bare unrecognised token — supplying a real path value first fills
        // that slot, so the trailing bad option has nowhere to go and is genuinely rejected
        // (mirrors ValidateArgParsingTests' documented swallowing gotcha).
        var root = BuildFullRoot();
        var result = root.Parse(new[] { "run", "scenarios", "--this-flag-does-not-exist" });

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Validate_UnrecognisedOption_ProducesParseErrors()
    {
        var root = BuildFullRoot();
        var result = root.Parse(new[] { "validate", "scenarios", "--this-flag-does-not-exist" });

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void List_UnrecognisedOption_ProducesParseErrors()
    {
        // `list` has no positional argument to swallow a bare unrecognised token, so a single
        // bad option is enough here (mirrors ListArgParsingTests).
        var root = BuildFullRoot();
        var result = root.Parse(new[] { "list", "--this-flag-does-not-exist" });

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Telemetry_UnrecognisedOption_ProducesParseErrors()
    {
        // `telemetry` has only subcommands (no positional argument), so a bad option on the
        // group itself is rejected the same way `list`'s is.
        var root = BuildFullRoot();
        var result = root.Parse(new[] { "telemetry", "--this-flag-does-not-exist" });

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void EmptyArgs_ProducesParseErrors()
    {
        // A bare `vouchfx` (no subcommand at all): System.CommandLine itself raises "required
        // command was not provided" as a genuine parse error, so this ALSO gets remapped to
        // ExitCodes.UsageError (2) by Program.cs — a useful behaviour confirmed against the
        // real binary but otherwise uncovered by a unit test.
        var root = BuildFullRoot();
        var result = root.Parse(Array.Empty<string>());

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void UnknownSubcommand_ProducesParseErrors()
    {
        // A subcommand token System.CommandLine does not recognise at all (as opposed to a
        // recognised subcommand with a bad option) is likewise a genuine parse error, so
        // `vouchfx badsubcommand` also exits 2, not 1.
        var root = BuildFullRoot();
        var result = root.Parse(new[] { "badsubcommand" });

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Help_ProducesNoParseErrors()
    {
        var root = BuildFullRoot();
        var result = root.Parse(new[] { "--help" });

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Version_ProducesNoParseErrors()
    {
        var root = BuildFullRoot();
        var result = root.Parse(new[] { "--version" });

        Assert.Empty(result.Errors);
    }

    // ── End-to-end seam: Parse → InvokeAsync (SCL's own errors+help side effect, unchanged) →
    // ResolveExitCode (the remap Program.cs applies to what it returns to the OS). ───────────

    [Fact]
    public async Task UnrecognisedOption_EndToEnd_RemapsToUsageError_ViaResolveExitCode()
    {
        var root = BuildFullRoot();
        var parseResult = root.Parse(new[] { "validate", "scenarios", "--this-flag-does-not-exist" });

        var invokeResult = await parseResult.InvokeAsync(new InvocationConfiguration());
        var exitCode = InvocationScope.ResolveExitCode(parseResult.Errors.Count, invokeResult);

        // System.CommandLine's own raw default is unchanged by this fix...
        Assert.Equal(1, invokeResult);
        // ...but this is what Program.cs now actually returns to the OS.
        Assert.Equal(ExitCodes.UsageError, exitCode);
    }

    [Fact]
    public async Task Help_EndToEnd_StaysSuccessExitCode_NeverRemapped()
    {
        var root = BuildFullRoot();
        var parseResult = root.Parse(new[] { "--help" });

        var invokeResult = await parseResult.InvokeAsync(new InvocationConfiguration());
        var exitCode = InvocationScope.ResolveExitCode(parseResult.Errors.Count, invokeResult);

        Assert.Equal(ExitCodes.Success, invokeResult);
        Assert.Equal(ExitCodes.Success, exitCode);
    }
}
