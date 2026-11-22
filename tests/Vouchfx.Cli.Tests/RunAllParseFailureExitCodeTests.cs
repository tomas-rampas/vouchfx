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

    // The run path: a key alias that YamlStream.Load cannot hash in bounded time (an alias to a
    // collection, or one before its anchor) is a parse failure, never a crash and never exit 0.
    // With every scenario unparseable the Docker-touching call is not reached. Every test that
    // could crash the host if the pre-scan were unwired first runs an acyclic document that Load
    // survives, so a wiring regression fails that assertion cleanly. The assertion names the
    // layer: ScenarioDiscovery writes "Parse / AST error: " before the message of a parse or
    // AST-build failure, while DocumentValidator's own scan, which would still refuse the
    // document with the same located text, reaches the output without that prefix. So the
    // canary fails if YamlDocumentParser stops calling the scan.
    // The canary proves only that the scan is wired in: a regression in the scan itself that
    // stopped refusing one shape would still crash the host on that shape's row.
    private async Task AssertRunRefusesKeyAlias(string yaml, string located)
    {
        var file = Path.Combine(_root, "key-alias.e2e.yaml");
        File.WriteAllText(file, yaml);

        var sw = new StringWriter();
        var exitCode = await ExecuteAsync(file, sw);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        Assert.Contains("Parse / AST error: YAML parse error at " + located, sw.ToString(), StringComparison.Ordinal);
    }

    private Task AssertRunKeyAliasGuardIsWired() =>
        AssertRunRefusesKeyAlias(
            "steps: []\na: &k [ 1 ]\nb: { ? *k : v }\n",
            "line 3, column 8: alias 'k' inside a mapping key refers to a mapping or sequence");

    [Fact]
    public async Task ExecuteAsync_AcyclicCollectionAliasInMappingKey_ReturnsInconclusive()
    {
        await AssertRunKeyAliasGuardIsWired();
    }

    [Theory]
    [InlineData("steps: []\na: &k [ *k ]\nb: { ? *k : v }\n", "line 3, column 8: alias 'k' inside a mapping key refers to a mapping or sequence")]
    [InlineData("name: fwd\n? *k : v\nsteps: &k [ *k ]\n", "line 2, column 3: alias 'k' inside a mapping key refers to an anchor that is not defined before it")]
    [InlineData("steps: []\n? *k : v\nb: &k x\n", "line 2, column 3: alias 'k' inside a mapping key refers to an anchor that is not defined before it")]
    [InlineData("steps: []\n? { ? *k : 1 } : 2\nb: &k [ *k ]\n", "line 2, column 7: alias 'k' inside a mapping key refers to an anchor that is not defined before it")]
    [InlineData("steps: []\na: [ *k : v ]\nb: &k [ *k ]\n", "line 2, column 6: alias 'k' inside a mapping key refers to an anchor that is not defined before it")]
    [InlineData("steps: []\n? [ *k ] : *k\nb: &k [ *k ]\n", "line 2, column 5: alias 'k' inside a mapping key refers to an anchor that is not defined before it")]
    [InlineData("steps: []\nb: { ? *nowhere : v }\n", "line 2, column 8: alias 'nowhere' inside a mapping key refers to an anchor that is not defined before it")]
    public async Task ExecuteAsync_RefusedAliasInMappingKey_ReturnsInconclusive_WithoutCrashing(string yaml, string located)
    {
        await AssertRunKeyAliasGuardIsWired();
        await AssertRunRefusesKeyAlias(yaml, located);
    }

    [Fact]
    public async Task ExecuteAsync_ForwardBillionLaughsKey_ReturnsInconclusivePromptly()
    {
        await AssertRunKeyAliasGuardIsWired();

        var yaml = new System.Text.StringBuilder("variables:\n  ? [ *a9 ] : *a9\n  a0: &a0 [x, y]\n");
        for (var level = 1; level <= 9; level++)
        {
            yaml.Append("  a").Append(level).Append(": &a").Append(level).Append(" [")
                .Append(string.Join(", ", Enumerable.Repeat("*a" + (level - 1), 10))).Append("]\n");
        }

        yaml.Append("steps: []\n");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await AssertRunRefusesKeyAlias(
            yaml.ToString(), "line 2, column 7: alias 'a9' inside a mapping key refers to an anchor that is not defined before it");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"run took {watch.Elapsed}");
    }

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
