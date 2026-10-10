using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Vouchfx.Engine.Abstractions.Events;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests.Events;

public sealed partial class EventContractFreezeTests
{
    [Fact]
    public void EventStreamJsonRuntime_MatchesActualCliDependencyAndBinary()
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
        Assert.Equal(cliHash, loadedHash);
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
