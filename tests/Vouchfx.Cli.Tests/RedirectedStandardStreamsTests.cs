using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Cli.Tests;

/// <summary>Actual entry-point byte tests, including Windows cp852 and hidden children.</summary>
public sealed class RedirectedStandardStreamsTests : IDisposable
{
    private const string Label = "Café — naïve §1";
    private static readonly JsonSerializerOptions IntentOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vouchfx-encoding-" + Guid.NewGuid().ToString("N"));

    public RedirectedStandardStreamsTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Scaffold_FileAndStdin_PreserveUnicodeInStdoutAndFile(bool createNoWindow, bool stdin)
    {
        var intent = JsonSerializer.Serialize(new
        {
            steps = new[] { new { id = "check", type = "script.csharp", label = Label } },
        }, IntentOptions);
        var intentPath = Path.Combine(_root, "intent.json");
        await File.WriteAllTextAsync(intentPath, intent, new UTF8Encoding(false));
        var arguments = new[] { "scaffold", "--intent", stdin ? "-" : intentPath };
        var input = stdin ? StrictUtf8.GetBytes(intent) : null;
        var stdout = await RunAsync(arguments, createNoWindow, input);
        Assert.Equal(0, stdout.ExitCode);
        Assert.Contains("# label: " + Label, Text(stdout.StdoutBase64), StringComparison.Ordinal);
        Assert.Equal(string.Empty, Text(stdout.StderrBase64));

        // Stdin->stdout alone can hide two inverse code-page conversions. A UTF-8 file
        // exposes the decoded author text and prevents that false positive.
        var outputPath = Path.Combine(_root, "scaffold.yaml");
        var file = await RunAsync(arguments.Concat(new[] { "--output", outputPath }).ToArray(), createNoWindow, input);
        Assert.Equal(0, file.ExitCode);
        Assert.Equal(string.Empty, Text(file.StdoutBase64));
        var bytes = await File.ReadAllBytesAsync(outputPath);
        AssertNoBom(bytes);
        Assert.Equal(Text(stdout.StdoutBase64), StrictUtf8.GetString(bytes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Schema_RedirectedBytes_AgreeExactlyWithUtf8File(bool createNoWindow)
    {
        var stdout = await RunAsync(new[] { "schema" }, createNoWindow);
        var outputPath = Path.Combine(_root, "schema.json");
        var file = await RunAsync(new[] { "schema", "--output", outputPath }, createNoWindow);
        Assert.Equal(0, stdout.ExitCode);
        Assert.Equal(0, file.ExitCode);
        var raw = await File.ReadAllBytesAsync(outputPath);
        AssertNoBom(raw);
        // WriteLine adds the platform newline, but the JSON text itself is byte-faithful.
        Assert.Equal(StrictUtf8.GetString(raw), Text(stdout.StdoutBase64).TrimEnd('\r', '\n'));
        using var document = JsonDocument.Parse(Text(stdout.StdoutBase64));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Parser_UnicodeArgument_IsPreservedOnStderr(bool createNoWindow)
    {
        var argument = "--" + Label.Replace(" ", string.Empty, StringComparison.Ordinal);
        var result = await RunAsync(new[] { argument }, createNoWindow);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains(argument, Text(result.StderrBase64), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "list")]
    [InlineData(true, "list")]
    [InlineData(false, "plan")]
    [InlineData(true, "plan")]
    public async Task Json_ExistingAsciiEscaping_IsPreserved(bool createNoWindow, string command)
    {
        string[] arguments = { "list", "--json" };
        if (command == "plan")
        {
            var suite = Path.Combine(_root, "unicode.e2e.yaml");
            await File.WriteAllTextAsync(suite,
                "metadata:\n  name: " + Label + "\nsteps:\n  - id: check\n    type: script.csharp\n    code: 'return;'\n");
            arguments = new[] { "plan", suite, "--json" };
        }
        var result = await RunAsync(arguments, createNoWindow);
        Assert.Equal(0, result.ExitCode);
        Assert.All(Convert.FromBase64String(result.StdoutBase64), value => Assert.True(value < 128));
        using var document = JsonDocument.Parse(Text(result.StdoutBase64));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        if (command == "plan")
            Assert.Equal(Label, document.RootElement.GetProperty("inventory").GetProperty("suites")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(false, "--help")]
    [InlineData(true, "--help")]
    [InlineData(false, "--version")]
    [InlineData(true, "--version")]
    public async Task EarlyCommands_UseInitializedRedirectedStreams(bool createNoWindow, string argument)
    {
        var result = await RunAsync(new[] { argument }, createNoWindow);
        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(Text(result.StdoutBase64));
        Assert.Equal(string.Empty, Text(result.StderrBase64));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MixedRedirection_InitializesOnlyTheRedirectedOutputHandle(bool createNoWindow, bool stderr)
    {
        if (stderr)
        {
            var argument = "--" + Label.Replace(" ", string.Empty, StringComparison.Ordinal);
            var result = await RunAsync(new[] { argument }, createNoWindow, redirectOutput: false);
            Assert.Equal(2, result.ExitCode);
            Assert.Contains(argument, Text(result.StderrBase64), StringComparison.Ordinal);
        }
        else
        {
            var result = await RunAsync(new[] { "schema" }, createNoWindow, redirectError: false);
            Assert.Equal(0, result.ExitCode);
            using var document = JsonDocument.Parse(Text(result.StdoutBase64));
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
    }

    private async Task<ProbeResult> RunAsync(string[] arguments, bool createNoWindow, byte[]? input = null, bool redirectOutput = true, bool redirectError = true)
    {
        // Execute the real CLI entry point with this test project's dependency/runtime
        // closure. No engine logic is called in-process and no Docker topology starts.
        var cliArguments = new[]
        {
            "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Vouchfx.Cli.Tests.runtimeconfig.json"),
            "--depsfile", Path.Combine(AppContext.BaseDirectory, "Vouchfx.Cli.Tests.deps.json"),
            typeof(InvocationScope).Assembly.Location,
        }.Concat(arguments).ToArray();
        var request = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json");
        var resultPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".result.json");
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new
        {
            Arguments = cliArguments,
            StdinBase64 = input is null ? null : Convert.ToBase64String(input),
            CreateNoWindow = createNoWindow,
            RedirectInput = true,
            RedirectOutput = redirectOutput,
            RedirectError = redirectError,
        }));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "EncodingProbe", "Vouchfx.Cli.Encoding.Probe.dll"));
        start.ArgumentList.Add(request);
        start.ArgumentList.Add(resultPath);
        using var probe = Process.Start(start) ?? throw new InvalidOperationException("Cannot launch private console probe.");
        try
        {
            probe.StandardInput.Close();
            var stdout = probe.StandardOutput.ReadToEndAsync();
            var stderr = probe.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await probe.WaitForExitAsync(timeout.Token);
            var probeOutput = await stdout;
            var error = await stderr;
            Assert.True(probe.ExitCode == 0, "Private probe exit " + probe.ExitCode + ": " + error + probeOutput);
            var result = JsonSerializer.Deserialize<ProbeResult>(await File.ReadAllTextAsync(resultPath))
                ?? throw new InvalidOperationException("Missing private probe result.");
            Assert.Equal(result.OutputPageBefore, result.OutputPageAfter);
            Assert.Equal(result.InputPageBefore, result.InputPageAfter);
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(852u, result.OutputPageBefore);
                Assert.Equal(852u, result.InputPageBefore);
            }
            _ = Text(result.StdoutBase64);
            _ = Text(result.StderrBase64);
            return result;
        }
        finally
        {
            ChildProcess.KillTreeQuietly(probe);
        }
    }

    private static string Text(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        AssertNoBom(bytes);
        return StrictUtf8.GetString(bytes);
    }

    private static void AssertNoBom(byte[] bytes) => Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed record ProbeResult(int ExitCode, string StdoutBase64, string StderrBase64,
        uint OutputPageBefore, uint InputPageBefore, uint OutputPageAfter, uint InputPageAfter);
}
