using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests.Events;

public sealed partial class EventContractFreezeTests
{
    [Fact]
    public async Task EventStreamJsonRuntime_MatchesActualCliDependencyAndBinary()
    {
        var cliDirectory = Path.Combine(AppContext.BaseDirectory, "EventContractCliRuntime");
        var cliBinary = Path.Combine(cliDirectory, "System.Text.Json.dll");
        var loaded = typeof(JsonSerializer).Assembly;
        var cliIdentity = AssemblyName.GetAssemblyName(cliBinary);
        var cliPackage = ReadSystemTextJsonPackage(Path.Combine(cliDirectory, "vouchfx.deps.json"));
        var testPackage = ReadSystemTextJsonPackage(Path.Combine(
            AppContext.BaseDirectory, "Vouchfx.Engine.Abstractions.Tests.deps.json"));
        var loadedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(loaded.Location)));
        var cliHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cliBinary)));
        _output.WriteLine($"Loaded STJ: {loaded.FullName}; {loaded.Location}; SHA256 {loadedHash}");
        _output.WriteLine($"CLI STJ: {cliIdentity.FullName}; {cliBinary}; SHA256 {cliHash}");
        _output.WriteLine($"Resolved deps packages: CLI={cliPackage ?? "<in-box>"}; test={testPackage ?? "<in-box>"}");
        Assert.NotNull(cliPackage);
        Assert.Equal(cliPackage, testPackage);
        Assert.Equal(cliIdentity.FullName, loaded.FullName);
        // A newer runtime may service the package with its in-box assembly. Compare
        // the CLI's actual loaded serializer, rather than assuming its bundled file wins.
        var cliOutput = await ProbeCliSerializerAsync();
        Assert.Contains($"VOUCHFX_STJ={loaded.FullName}|{loadedHash}", cliOutput, StringComparison.Ordinal);
    }

    private async Task<string> ProbeCliSerializerAsync()
    {
        var assemblyDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var framework = Path.GetFileName(assemblyDirectory);
        var configuration = Directory.GetParent(assemblyDirectory)!.Name;
        var cli = Path.Combine(RepoRoot.Resolve(), "src", "Cli", "Vouchfx.Cli", "bin",
            configuration, framework, "vouchfx.dll");
        Assert.True(File.Exists(cli), "Build the solution before checking the CLI serializer.");

        var scratch = Directory.CreateTempSubdirectory("vouchfx-event-runtime-");
        try
        {
            var suite = Path.Combine(scratch.FullName, "serializer.e2e.yaml");
            await File.WriteAllTextAsync(suite, """
                steps:
                  - id: serializer-proof
                    type: script.csharp
                    code: |
                      var serializerAssembly = typeof(System.Text.Json.JsonSerializer).Assembly;
                      var serializerHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(serializerAssembly.Location)));
                      Console.WriteLine("VOUCHFX_STJ=" + serializerAssembly.FullName + "|" + serializerHash);
                """);
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // Match this test host even when several runtime majors are installed.
            foreach (var argument in new[] { "exec", "--fx-version", Environment.Version.ToString(),
                cli, "run", suite, "--fail-on-env-error", "--fail-on-inconclusive", "--no-telemetry",
                "--no-decorations", "--no-default-providers" })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the CLI serializer probe.");
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                var output = await stdout;
                var error = await stderr;
                _output.WriteLine(output);
                _output.WriteLine(error);
                Assert.Equal(0, process.ExitCode);
                return output;
            }
            finally
            {
                ChildProcess.KillTreeQuietly(process);
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    [Fact]
    public void EventStreamJsonRuntime_CliAndTestHostHaveNoFeatureSwitchOverrides()
    {
        // Check evaluated build output, so switches introduced by any imported props,
        // targets or runtimeconfig.template.json cannot evade a project-text scan.
        var paths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "EventContractCliRuntime", "vouchfx.runtimeconfig.json"),
            Path.Combine(AppContext.BaseDirectory, "Vouchfx.Engine.Abstractions.Tests.runtimeconfig.json"),
        };
        foreach (var path in paths)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var overrides = document.RootElement.GetProperty("runtimeOptions")
                .GetProperty("configProperties").EnumerateObject()
                .Where(property => property.Name.StartsWith("System.Text.Json.", StringComparison.Ordinal))
                .Select(property => $"{property.Name}={property.Value.GetRawText()}").ToArray();
            _output.WriteLine($"STJ runtimeconfig overrides at {path}: {string.Join(", ", overrides)}");
            Assert.True(overrides.Length == 0,
                $"{path} overrides System.Text.Json feature switches: {string.Join(", ", overrides)}. "
                + "The event-wire host must exercise the CLI's effective serializer defaults.");
        }
    }

    private static string? ReadSystemTextJsonPackage(string depsPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(depsPath));
        var libraries = document.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(property => property.Name.StartsWith("System.Text.Json/", StringComparison.Ordinal))
            .Select(property => property.Name).ToArray();
        return libraries.Length == 0 ? null : Assert.Single(libraries);
    }
}
