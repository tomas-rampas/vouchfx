// Vouchfx.Cli.Tests — `run <dir>` entirely-parse-failure exit code (issue #278). No Docker.
//
// Bug: `vouchfx run <dir>` where EVERY discovered *.e2e.yaml scenario fails to parse
// (malformed YAML / unknown step type across the board) returned exit 0 (Success), while
// `vouchfx validate <dir>` on the identical input returns exit 4 (Inconclusive). A CI pipeline
// keying on `run`'s exit code treated a fully-unparseable suite as passing. Per the verdict
// taxonomy (§12.1), an all-unparseable suite is Inconclusive (exit 4), never a clean Pass.
//
// This exercises the DIRECTORY case end-to-end against the real RunCommand.ExecuteAsync: with
// zero scenarios parsed (parsedCount == 0), ScenarioRunner.RunSuiteAsync — the ONLY Docker/
// Aspire-touching call in ExecuteAsync — is never reached, so this file needs no containers.
// The single-file variant of the SAME bug is covered by RunPathRootExecuteTests (a single bad
// file is, by construction, also an entirely-parse-failure set); the underlying fix and decision
// point (RunCommand.ComputeExitCode) are identical for both shapes.

using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class RunAllParseFailureExitCodeTests : IDisposable
{
    private readonly string _root;

    public RunAllParseFailureExitCodeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vouchfx-cli-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; a locked or read-only file must not fail the test.
            // UnauthorizedAccessException (not just IOException) is possible here too — e.g.
            // a read-only file on some platforms — so both are swallowed (Copilot review
            // finding, PR #284).
        }
    }

    private const string MalformedUnknownType =
        "steps:\n  - id: x\n    type: not-a-real-provider\n";

    private static Task<int> ExecuteAsync(string path, TextWriter output, bool failOnInconclusive = false)
        => RunCommand.ExecuteAsync(
            path: path,
            criteria: Vouchfx.Cli.Selection.SelectionCriteria.None,
            parallel: null,
            watch: false,
            failOnEnvironmentError: false,
            failOnInconclusive: failOnInconclusive,
            htmlReportPath: null,
            junitReportPath: null,
            eventsReportPath: null,
            eventsStreamPath: null,
            decorate: false,
            output: output,
            telemetryHook: null,
            cancellationToken: default);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DirectoryRoot_EveryScenarioUnparseable_ReturnsInconclusive_RegardlessOfFailOnInconclusiveFlag(
        bool failOnInconclusive)
    {
        File.WriteAllText(Path.Combine(_root, "broken-one.e2e.yaml"), MalformedUnknownType);
        File.WriteAllText(Path.Combine(_root, "broken-two.e2e.yaml"), "not: [valid, yaml, at, all: :\n");

        var sw = new StringWriter();
        var exitCode = await ExecuteAsync(_root, sw, failOnInconclusive);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);

        var rendered = sw.ToString();
        Assert.Contains("broken-one.e2e.yaml", rendered, StringComparison.Ordinal);
        Assert.Contains("broken-two.e2e.yaml", rendered, StringComparison.Ordinal);
        Assert.Contains("(Inconclusive)", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_DirectoryRoot_SingleUnparseableScenario_ReturnsInconclusive()
    {
        File.WriteAllText(Path.Combine(_root, "broken.e2e.yaml"), MalformedUnknownType);

        var sw = new StringWriter();
        var exitCode = await ExecuteAsync(_root, sw);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    // Empty-directory (no scenarios discovered at all) behaviour is DELIBERATELY left as it
    // stands today — the issue is specifically about the ALL-parse-failure case, and `run`
    // already agrees with `validate` here: both return Success (0, "nothing ran, nothing
    // failed" / "vacuously valid") for zero discovered scenarios. Pinned here so a future
    // change to this path is a deliberate choice, not an accidental regression.
    [Fact]
    public async Task ExecuteAsync_DirectoryRoot_NoScenariosDiscovered_StaysSuccess_UnchangedFromToday()
    {
        var sw = new StringWriter();
        var exitCode = await ExecuteAsync(_root, sw);

        Assert.Equal(ExitCodes.Success, exitCode);
    }
}
