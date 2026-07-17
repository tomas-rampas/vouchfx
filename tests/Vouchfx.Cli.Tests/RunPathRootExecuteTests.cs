// Vouchfx.Cli.Tests — ExecuteAsync path-root behaviour (single-file discovery root). No Docker.
//
// Exercises RunCommand.ExecuteAsync's Docker-free early paths for the <path> positional:
// a missing root and a wrong-extension file root map to a usage error (exit 2) with the
// discovery message written verbatim; a valid single-file root flows through selection
// ("of 1 discovered") and the parse-failure fold (Inconclusive → exit 0 by default,
// exit 4 under --fail-on-inconclusive).  Every case returns before the runner block, so
// no topology is started.

using Vouchfx.Cli;
using Vouchfx.Cli.Selection;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class RunPathRootExecuteTests : IDisposable
{
    private readonly string _root;

    public RunPathRootExecuteTests()
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
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the test.
        }
    }

    private const string MinimalValidScenario =
        "metadata:\n" +
        "  name: minimal\n" +
        "  tags: [smoke]\n" +
        "steps:\n" +
        "  - id: call-api\n" +
        "    type: http.rest\n";

    private static Task<int> ExecuteAsync(
        string path,
        SelectionCriteria? criteria,
        TextWriter output,
        bool failOnInconclusive = false)
        => RunCommand.ExecuteAsync(
            path: path,
            criteria: criteria ?? SelectionCriteria.None,
            parallel: null,
            watch: false,
            failOnEnvironmentError: false,
            failOnInconclusive: failOnInconclusive,
            htmlReportPath: null,
            junitReportPath: null,
            eventsReportPath: null,
            decorate: false,
            output: output,
            telemetryHook: null,
            cancellationToken: default);

    [Fact]
    public async Task ExecuteAsync_MissingRoot_ReturnsUsageError_AndWritesNotFoundMessage()
    {
        var sw = new StringWriter();
        var missing = Path.Combine(_root, "does-not-exist");

        var exitCode = await ExecuteAsync(missing, criteria: null, sw);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains("does not exist", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WrongExtensionFileRoot_ReturnsUsageError_AndWritesSuffixMessage()
    {
        var sw = new StringWriter();
        var wrong = Path.Combine(_root, "scenario.txt");
        File.WriteAllText(wrong, MinimalValidScenario);

        var exitCode = await ExecuteAsync(wrong, criteria: null, sw);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains(".e2e.yaml", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_FileRoot_TagFilterMatchesNothing_ReturnsSuccessWithMessage()
    {
        var sw = new StringWriter();
        var file = Path.Combine(_root, "one.e2e.yaml");
        File.WriteAllText(file, MinimalValidScenario);

        var criteria = SelectionCriteria.None with { Tags = new[] { "no-such-tag" } };
        var exitCode = await ExecuteAsync(file, criteria, sw);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("of 1 discovered", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_FileRoot_ParseFailure_ReportsInconclusive_DefaultExitZero()
    {
        var sw = new StringWriter();
        var bad = Path.Combine(_root, "broken.e2e.yaml");
        File.WriteAllText(bad, "steps:\n  - id: x\n    type: not-a-real-provider\n");

        var exitCode = await ExecuteAsync(bad, criteria: null, sw);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("(Inconclusive)", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_FileRoot_ParseFailure_WithFailOnInconclusive_ReturnsExit4()
    {
        var sw = new StringWriter();
        var bad = Path.Combine(_root, "broken.e2e.yaml");
        File.WriteAllText(bad, "steps:\n  - id: x\n    type: not-a-real-provider\n");

        var exitCode = await ExecuteAsync(bad, criteria: null, sw, failOnInconclusive: true);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }
}
