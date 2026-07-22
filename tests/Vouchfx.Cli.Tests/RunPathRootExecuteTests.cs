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
            eventsStreamPath: null,
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

    // Copilot review finding #1 parity (#260 follow-up): RunCommand.ExecuteAsync's discovery
    // catch was broadened in lock-step with ValidateCommand.Execute's (see
    // ValidateCommandTests.Execute_PathContainsNullCharacter_* for the full rationale). A NUL
    // character is the deterministic, cross-platform trigger: Path.GetFullPath unconditionally
    // rejects it (ArgumentException) on every .NET platform/OS.
    [Fact]
    public async Task ExecuteAsync_PathContainsNullCharacter_ReturnsUsageError_WithoutUnhandledException()
    {
        var sw = new StringWriter();
        var badPath = "bad\0path";

        var exitCode = await ExecuteAsync(badPath, criteria: null, sw);

        Assert.Equal(ExitCodes.UsageError, exitCode);
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

    /// <summary>
    /// Issue #266, Item 4: <c>AstBuilder</c>'s unknown-step-type message splices the
    /// declared, dotted step <c>type:</c> straight from the document verbatim
    /// (<c>"unknown step type '{raw}' — no registered provider"</c>), and this is the MOST
    /// reachable human-terminal surface for hostile suite content — a plain <c>vouchfx run</c>
    /// on a malformed suite reaches <see cref="RunCommand"/>'s parse-failure loop with no
    /// flag needed. The file on disk stays plain ASCII (a YAML double-quoted scalar's
    /// <c>\x1B</c> escape is the spec-compliant way to encode a C0 control character — an
    /// UNESCAPED control byte is not valid YAML content and would fail at the YAML-syntax
    /// stage before ever reaching AstBuilder); YamlDotNet decodes the escape back to the raw
    /// ESC character during parsing, which is exactly the point at which the hostile text
    /// must already be inert by the time it reaches the terminal.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_FileRoot_ParseFailure_HostileTypeWithAnsiSequence_RendersInert()
    {
        var sw = new StringWriter();
        var bad = Path.Combine(_root, "hostile-type.e2e.yaml");
        File.WriteAllText(
            bad,
            "steps:\n"
            + "  - id: call-api\n"
            + "    type: \"hostile.provider\\x1B[31mHACKED\\x1B[0m\"\n");

        var exitCode = await ExecuteAsync(bad, criteria: null, sw);

        Assert.Equal(ExitCodes.Success, exitCode);

        var rendered = sw.ToString();
        Assert.Contains("(Inconclusive)", rendered, StringComparison.Ordinal);
        // The surrounding diagnostic text survives sanitisation intact...
        Assert.Contains("HACKED", rendered, StringComparison.Ordinal);
        Assert.Contains("no registered provider", rendered, StringComparison.Ordinal);
        // ...but no raw ESC byte reaches the terminal.
        Assert.DoesNotContain((char)0x1B, rendered);
    }
}
