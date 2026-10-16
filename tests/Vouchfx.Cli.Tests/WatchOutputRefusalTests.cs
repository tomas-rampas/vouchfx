using System.CommandLine;
using Vouchfx.Cli;
using Vouchfx.Cli.Selection;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class WatchOutputRefusalTests : IDisposable
{
    private const string Refusal =
        "--watch cannot be combined with --html, --junit, --events/--json or --events-stream. "
        + "Remove the output options for terminal-only watch, or remove --watch to write reports.";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vouchfx-watch-output-" + Guid.NewGuid().ToString("n"));

    public WatchOutputRefusalTests() => Directory.CreateDirectory(_root);

    public static IEnumerable<object[]> OutputCombinations()
    {
        for (var mask = 1; mask < 16; mask++)
        {
            yield return new object[] { mask };
        }
    }

    [Theory]
    [MemberData(nameof(OutputCombinations))]
    public async Task WatchWithOutput_RefusesBeforeDiscovery_AndLeavesOutputsUntouched(int mask)
    {
        var suite = Path.Combine(_root, "private-suite.e2e.yaml");
        File.WriteAllText(suite, "steps: [broken");
        var existing = Path.Combine(_root, "existing-report");
        File.WriteAllText(existing, "keep this report");
        var newDirectory = Path.Combine(_root, "reports");
        var flags = new[] { "--html", "--junit", "--events", "--events-stream" };
        var args = new List<string> { suite, "--watch" };
        for (var i = 0; i < flags.Length; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                args.Add(flags[i]);
                args.Add(i == 0 ? existing : Path.Combine(newDirectory, $"report-{i}"));
            }
        }

        var (exitCode, output) = await ExecuteParsedAsync(args.ToArray());

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Equal(Refusal + Environment.NewLine, output);
        Assert.Equal("keep this report", File.ReadAllText(existing));
        Assert.False(Directory.Exists(newDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WatchWithJsonAlias_RefusesBeforeMissingPathDiagnostic(bool shutdownOnStdinEof)
    {
        var (exitCode, output) = await ExecuteParsedAsync(
            Path.Combine(_root, "private-missing-suite"), "--watch", "--json", "report.jsonl",
            shutdownOnStdinEof ? "--shutdown-on-stdin-eof" : "--no-telemetry");
        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Equal(Refusal + Environment.NewLine, output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SupportedModes_ContinueToDiscovery(bool watch)
    {
        var args = watch
            ? new[] { _root, "--watch" }
            : new[] { _root, "--html", "report.html", "--junit", "report.xml",
                "--events", "events.jsonl", "--events-stream", "stream.jsonl" };
        var (exitCode, output) = await ExecuteParsedAsync(args);
        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("scenarios found", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryRunWithAuthoringRefusal_StillWritesAllRequestedReports()
    {
        var suite = Path.Combine(_root, "missing-script.e2e.yaml");
        File.WriteAllText(suite,
            "steps:\n  - id: refused\n    type: script.csharp\n    file: ./missing.csx\n");
        var paths = new[] { "report.html", "report.xml", "events.jsonl", "stream.jsonl" }
            .Select(name => Path.Combine(_root, name)).ToArray();
        var (exitCode, output) = await ExecuteParsedAsync(
            suite, "--html", paths[0], "--junit", paths[1],
            "--events", paths[2], "--events-stream", paths[3]);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        Assert.DoesNotContain(Refusal, output, StringComparison.Ordinal);
        Assert.All(paths, path => Assert.True(new FileInfo(path).Length > 0));
    }

    [Fact]
    public async Task WatchParallelWithOutput_PreservesParallelRefusalPrecedence()
    {
        var (exitCode, output) = await ExecuteParsedAsync(
            _root, "--watch", "--parallel", "2", "--html", "report.html");
        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.StartsWith("--watch cannot be combined with --parallel", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WatchWithCollidingOutputs_PreservesCollisionRefusalPrecedence()
    {
        var (exitCode, output) = await ExecuteParsedAsync(
            _root, "--watch", "--events", "same.jsonl", "--events-stream", "same.jsonl");
        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Equal("--events-stream must not point at the same file as --events/--html/--junit."
            + Environment.NewLine, output);
    }

    private static async Task<(int ExitCode, string Output)> ExecuteParsedAsync(params string[] args)
    {
        var command = RunCommand.Build();
        var parsed = command.Parse(args);
        Assert.Empty(parsed.Errors);
        T Value<T>(string name) => parsed.GetValue((Option<T>)command.Options.Single(o => o.Name == name))!;
        using var output = new StringWriter();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var exitCode = await RunCommand.ExecuteAsync(
            path: parsed.GetValue((Argument<string>)command.Arguments.Single())!,
            criteria: SelectionCriteria.None,
            parallel: Value<int?>("--parallel"),
            watch: Value<bool>("--watch"),
            failOnEnvironmentError: false,
            failOnInconclusive: false,
            htmlReportPath: Value<string?>("--html"),
            junitReportPath: Value<string?>("--junit"),
            eventsReportPath: Value<string?>("--events"),
            eventsStreamPath: Value<string?>("--events-stream"),
            decorate: false,
            output: output,
            telemetryHook: null,
            cancellationToken: cancellation.Token,
            shutdownOnStdinEof: Value<bool>("--shutdown-on-stdin-eof")).WaitAsync(TimeSpan.FromSeconds(15));
        return (exitCode, output.ToString());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
