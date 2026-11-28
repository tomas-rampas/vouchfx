// The YAML nesting limit through the real CLI entry point, as a separate process: a suite of
// 200,000 nested '[' overflowed the stack (exit 134, uncatchable) in YamlStream.Load before the
// limit existed. Each test starts the built CLI with this test project's dependency closure,
// the way RedirectedStandardStreamsTests does, but without the console probe: only the exit
// code and the two streams are examined. No Docker topology starts, because the refusal comes
// from suite parsing, before any environment is built. A child that does not exit within the
// timeout is killed and fails the test rather than hanging it.
using System.Diagnostics;
using System.Text;
using Vouchfx.Tests;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class YamlDepthLimitCliTests : IDisposable
{
    private const string DepthText = "nested more than 64 levels deep";
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vouchfx-depth-" + Guid.NewGuid().ToString("N"));

    public YamlDepthLimitCliTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteSuite(string name, string text)
    {
        var path = Path.Combine(_root, name + ".e2e.yaml");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("run")]
    public async Task DeeplyNestedSuite_IsRefusedWithTheExitCodeOfTheSelfReferentialKeyRefusal(string command)
    {
        var deep = WriteSuite("deep", DeepYamlShapes.UnterminatedFlowSequences(200_000));
        var selfReferential = WriteSuite("self", "? &k { *k : v }\n: x\n");

        var refused = await RunCliAsync(command, deep);
        var reference = await RunCliAsync(command, selfReferential);

        // The reference is the existing refusal: it must be a plain, non-crash failure itself.
        Assert.NotEqual(134, reference.ExitCode);
        Assert.NotEqual(0, reference.ExitCode);
        Assert.Contains("alias 'k' inside a mapping key", reference.Output, StringComparison.Ordinal);

        Assert.Equal(reference.ExitCode, refused.ExitCode);
        Assert.NotEqual(134, refused.ExitCode);
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains(DepthText, refused.Output, StringComparison.Ordinal);
        Assert.Contains("64", refused.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Stack overflow", refused.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_ShallowWrittenNestingButAliasChainOf100000_DoesNotCrash()
    {
        // Each anchored sequence holds an alias to the previous one, so the effective depth is
        // 100,000 while the written nesting is 3 (counting the root mapping). Two alternating
        // anchor names let the chain fit under the 1 MiB discovery limit (about 800 KB).
        var text = new StringBuilder("metadata: { name: chain }\nchain: [&b [x]");
        for (var index = 0; index < 100_000; index++)
        {
            var (name, previous) = index % 2 == 0 ? ('a', 'b') : ('b', 'a');
            text.Append(",&").Append(name).Append(" [*").Append(previous).Append(']');
        }

        text.Append("]\n");
        Assert.True(text.Length < 1_048_576, "the chain file must stay under the 1 MiB discovery limit");
        var suite = WriteSuite("alias-chain", text.ToString());

        var result = await RunCliAsync("validate", suite);

        Assert.NotEqual(134, result.ExitCode);
        Assert.DoesNotContain("Stack overflow", result.Stderr, StringComparison.OrdinalIgnoreCase);
        // The suite declares no steps, so it can never validate: a crash is the only way to
        // land anywhere but a plain failure.
        Assert.NotEqual(0, result.ExitCode);
    }

    private async Task<CliResult> RunCliAsync(string command, string suite)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Vouchfx.Cli.Tests.runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Vouchfx.Cli.Tests.deps.json"));
        start.ArgumentList.Add(typeof(InvocationScope).Assembly.Location);
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(suite);
        // Keep the child away from the developer's real profile and from telemetry state: HOME
        // alone does not move %APPDATA% on Windows, and XDG_CONFIG_HOME wins over HOME on Linux.
        start.Environment["DOTNET_CLI_HOME"] = _root;
        start.Environment["HOME"] = _root;
        start.Environment["APPDATA"] = _root;
        start.Environment["LOCALAPPDATA"] = _root;
        start.Environment["XDG_CONFIG_HOME"] = _root;
        start.Environment["VOUCHFX_NO_TELEMETRY"] = "1";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot launch the CLI.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(s_timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"'vouchfx {command}' did not exit within {s_timeout.TotalSeconds:0} seconds.");
            }

            return new CliResult(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            // A child that outlives the wait (a regression that hangs) must not outlive the test.
            ChildProcess.KillTreeQuietly(process);
        }
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr)
    {
        internal string Output => Stdout + Stderr;
    }
}
