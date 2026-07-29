// Vouchfx.Cli.Tests — `plan` argument parsing tests (M3 Planner / planner-coverage-and-gap-report,
// REQ-002, REQ-006, EDGE-008/EDGE-009-adjacent usage). No Docker.
//
// Asserts that every `plan` option/argument parses to the right value (and default), that
// the threshold defaults are exactly 30/2/2/2 (PlanThresholds.Defaults — never re-declared
// here as separate literals), that `--help` documents every argument (including the
// direction-explicit `--events` wording, F-7), and that an unrecognised option is a genuine
// System.CommandLine parse error resolving to exit 2 via InvocationScope.ResolveExitCode.
// Mirrors ListArgParsingTests / ValidateArgParsingTests. The Docker-free Execute
// orchestration and the REQ-010 exit-code matrix are tested in PlanCommandTests.

using System.CommandLine;
using System.Text.RegularExpressions;
using Vouchfx.Cli;
using Vouchfx.Engine.Planning;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class PlanArgParsingTests
{
    // ── Individual option/argument parsing, in isolation ──────────────────────────

    private static string? ParseSuitePath(params string[] args)
    {
        var argument = PlanCommand.BuildSuitePathArgument();

        var command = new Command("plan");
        command.Add(argument);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(argument);
    }

    private static string? ParseEvents(params string[] args)
    {
        var option = PlanCommand.BuildEventsOption();

        var command = new Command("plan");
        command.Add(option);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(option);
    }

    private static bool ParseJson(params string[] args)
    {
        var option = PlanCommand.BuildJsonOption();

        var command = new Command("plan");
        command.Add(option);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(option);
    }

    private static string? ParseOutput(params string[] args)
    {
        var option = PlanCommand.BuildOutputOption();

        var command = new Command("plan");
        command.Add(option);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(option);
    }

    private static bool ParseFailOnGap(params string[] args)
    {
        var option = PlanCommand.BuildFailOnGapOption();

        var command = new Command("plan");
        command.Add(option);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(option);
    }

    private static int ParseStaleDays(params string[] args)
    {
        var option = PlanCommand.BuildStaleDaysOption();

        var command = new Command("plan");
        command.Add(option);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(option);
    }

    private static int ParseFlakyMinRuns(params string[] args)
    {
        var option = PlanCommand.BuildFlakyMinRunsOption();

        var command = new Command("plan");
        command.Add(option);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(option);
    }

    private static int ParseFragileMinEnvErrors(params string[] args)
    {
        var option = PlanCommand.BuildFragileMinEnvErrorsOption();

        var command = new Command("plan");
        command.Add(option);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(option);
    }

    private static int ParseInconclusiveMin(params string[] args)
    {
        var option = PlanCommand.BuildInconclusiveMinOption();

        var command = new Command("plan");
        command.Add(option);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(option);
    }

    [Fact]
    public void SuitePath_Absent_DefaultsToDot()
    {
        Assert.Equal(".", ParseSuitePath());
    }

    [Fact]
    public void SuitePath_Present_ParsesToGivenValue()
    {
        Assert.Equal("suites", ParseSuitePath("suites"));
    }

    [Fact]
    public void Events_Absent_ParsesToNull()
    {
        Assert.Null(ParseEvents());
    }

    [Fact]
    public void Events_Present_ParsesToGivenPath()
    {
        Assert.Equal("history.jsonl", ParseEvents("--events", "history.jsonl"));
    }

    [Fact]
    public void JsonFlag_Absent_ParsesToFalse()
    {
        Assert.False(ParseJson());
    }

    [Fact]
    public void JsonFlag_Present_ParsesToTrue()
    {
        Assert.True(ParseJson("--json"));
    }

    [Fact]
    public void Output_Absent_ParsesToNull()
    {
        Assert.Null(ParseOutput());
    }

    [Fact]
    public void Output_Present_ParsesToGivenPath()
    {
        Assert.Equal("report.json", ParseOutput("--output", "report.json"));
    }

    [Fact]
    public void FailOnGap_Absent_ParsesToFalse()
    {
        Assert.False(ParseFailOnGap());
    }

    [Fact]
    public void FailOnGap_Present_ParsesToTrue()
    {
        Assert.True(ParseFailOnGap("--fail-on-gap"));
    }

    // ── Threshold defaults: exactly PlanThresholds.Defaults (30/2/2/2) — never a
    // separately re-declared literal in the CLI layer. ────────────────────────────

    [Fact]
    public void StaleDays_Absent_DefaultsToPlanThresholdsDefaults()
    {
        Assert.Equal(30, PlanThresholds.Defaults.StaleDays);
        Assert.Equal(PlanThresholds.Defaults.StaleDays, ParseStaleDays());
    }

    [Fact]
    public void StaleDays_Present_ParsesToGivenValue()
    {
        Assert.Equal(45, ParseStaleDays("--stale-days", "45"));
    }

    [Fact]
    public void FlakyMinRuns_Absent_DefaultsToPlanThresholdsDefaults()
    {
        Assert.Equal(2, PlanThresholds.Defaults.FlakyMinRuns);
        Assert.Equal(PlanThresholds.Defaults.FlakyMinRuns, ParseFlakyMinRuns());
    }

    [Fact]
    public void FlakyMinRuns_Present_ParsesToGivenValue()
    {
        Assert.Equal(3, ParseFlakyMinRuns("--flaky-min-runs", "3"));
    }

    [Fact]
    public void FragileMinEnvErrors_Absent_DefaultsToPlanThresholdsDefaults()
    {
        Assert.Equal(2, PlanThresholds.Defaults.FragileMinEnvErrors);
        Assert.Equal(PlanThresholds.Defaults.FragileMinEnvErrors, ParseFragileMinEnvErrors());
    }

    [Fact]
    public void FragileMinEnvErrors_Present_ParsesToGivenValue()
    {
        Assert.Equal(5, ParseFragileMinEnvErrors("--fragile-min-env-errors", "5"));
    }

    [Fact]
    public void InconclusiveMin_Absent_DefaultsToPlanThresholdsDefaults()
    {
        Assert.Equal(2, PlanThresholds.Defaults.InconclusiveMin);
        Assert.Equal(PlanThresholds.Defaults.InconclusiveMin, ParseInconclusiveMin());
    }

    [Fact]
    public void InconclusiveMin_Present_ParsesToGivenValue()
    {
        Assert.Equal(7, ParseInconclusiveMin("--inconclusive-min", "7"));
    }

    // ── The direction-explicit --events help text (F-7): `plan`'s --events is an INPUT,
    // the OPPOSITE of `run`'s OUTPUT --events, and the description must say so. Asserted
    // directly against the Option's own Description (pre-render, so immune to any help-text
    // line-wrapping) AND against the real rendered --help text below. ───────────────────────

    [Fact]
    public void EventsOption_DescriptionStatesInputDirectionExplicitly()
    {
        var description = PlanCommand.BuildEventsOption().Description;

        Assert.Contains("vouchfx run --events", description, StringComparison.Ordinal);
        Assert.Contains("writes", description, StringComparison.Ordinal);
    }

    // ── The fully built command ────────────────────────────────────────────────────

    [Fact]
    public void Build_HasPlanName()
    {
        var command = PlanCommand.Build();
        Assert.Equal("plan", command.Name);
    }

    [Fact]
    public void Build_ExposesEveryDocumentedOption()
    {
        var command = PlanCommand.Build();

        Assert.Contains(command.Arguments, a => a.Name == "path");
        Assert.Contains(command.Options, o => o.Name == "--events");
        Assert.Contains(command.Options, o => o.Name == "--json");
        Assert.Contains(command.Options, o => o.Name == "--output");
        Assert.Contains(command.Options, o => o.Name == "--fail-on-gap");
        Assert.Contains(command.Options, o => o.Name == "--stale-days");
        Assert.Contains(command.Options, o => o.Name == "--flaky-min-runs");
        Assert.Contains(command.Options, o => o.Name == "--fragile-min-env-errors");
        Assert.Contains(command.Options, o => o.Name == "--inconclusive-min");
    }

    [Fact]
    public void Build_EveryOptionAndArgumentHasANonEmptyDescription()
    {
        var command = PlanCommand.Build();

        Assert.All(command.Arguments, a => Assert.False(string.IsNullOrWhiteSpace(a.Description)));
        Assert.All(command.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Description)));
    }

    [Fact]
    public void FullPlanCommand_AcceptsEveryOptionTogether()
    {
        var command = PlanCommand.Build();
        var result = command.Parse(new[]
        {
            "suites",
            "--events", "history.jsonl",
            "--json",
            "--output", "report.json",
            "--fail-on-gap",
            "--stale-days", "10",
            "--flaky-min-runs", "1",
            "--fragile-min-env-errors", "1",
            "--inconclusive-min", "1",
        });

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void FullPlanCommand_AcceptsNoArgumentsAtAll()
    {
        var command = PlanCommand.Build();
        var result = command.Parse(Array.Empty<string>());

        Assert.Empty(result.Errors);
    }

    // ── `--help` documents the arguments (REQ-002 acceptance), including the
    // direction-explicit --events wording, verified against the REAL rendered help text
    // (not just the Option's own Description) so a future HelpBuilder change cannot silently
    // drop it. Whitespace (including any line-wrap the renderer inserts) is normalised to a
    // single space before the substring check, so this is robust to console-width wrapping. ──

    private static async Task<string> RenderHelpAsync(Command command)
    {
        var root = new RootCommand("test root");
        root.Add(command);

        var parseResult = root.Parse(new[] { "plan", "--help" });
        var stdout = new StringWriter();
        var config = new InvocationConfiguration { Output = stdout };

        await parseResult.InvokeAsync(config);

        return stdout.ToString();
    }

    private static string NormaliseWhitespace(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim();

    [Fact]
    public async Task Help_DocumentsSuitePathEventsAndFailOnGap()
    {
        var helpText = NormaliseWhitespace(await RenderHelpAsync(PlanCommand.Build()));

        Assert.Contains("--events", helpText, StringComparison.Ordinal);
        Assert.Contains("--json", helpText, StringComparison.Ordinal);
        Assert.Contains("--output", helpText, StringComparison.Ordinal);
        Assert.Contains("--fail-on-gap", helpText, StringComparison.Ordinal);
        Assert.Contains("--stale-days", helpText, StringComparison.Ordinal);
        Assert.Contains("--flaky-min-runs", helpText, StringComparison.Ordinal);
        Assert.Contains("--fragile-min-env-errors", helpText, StringComparison.Ordinal);
        Assert.Contains("--inconclusive-min", helpText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_EventsSectionStatesInputDirectionExplicitly()
    {
        // F-7: `--events` means the OPPOSITE thing on `run` (an OUTPUT artefact) to what it
        // means here (an INPUT). The rendered help text must state that explicitly, not just
        // the Option's Description field in isolation (EventsOption_DescriptionStates...
        // above already pins that; this pins it survives all the way to the terminal).
        var helpText = NormaliseWhitespace(await RenderHelpAsync(PlanCommand.Build()));

        Assert.Contains("vouchfx run --events", helpText, StringComparison.Ordinal);
    }

    // ── Unknown option: a genuine System.CommandLine parse error, resolving to exit 2
    // (mirrors ListArgParsingTests / ValidateArgParsingTests' documented pattern: a real
    // path value must fill the positional slot first, or the bad token is silently
    // swallowed into it instead of being rejected). ───────────────────────────────────

    private static RootCommand BuildRootWithPlan()
    {
        var root = new RootCommand("test root");
        root.Add(PlanCommand.Build());
        return root;
    }

    [Fact]
    public void Plan_UnrecognisedOption_ParseResultHasErrors()
    {
        var root = BuildRootWithPlan();
        var result = root.Parse(new[] { "plan", "suites", "--this-flag-does-not-exist" });

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Plan_UnrecognisedOption_ResolvesToUsageErrorViaInvocationScope()
    {
        var root = BuildRootWithPlan();
        var parseResult = root.Parse(new[] { "plan", "suites", "--this-flag-does-not-exist" });

        var invokeResult = await parseResult.InvokeAsync(new InvocationConfiguration());
        var exitCode = InvocationScope.ResolveExitCode(parseResult.Errors.Count, invokeResult);

        Assert.Equal(ExitCodes.UsageError, exitCode);
    }
}
