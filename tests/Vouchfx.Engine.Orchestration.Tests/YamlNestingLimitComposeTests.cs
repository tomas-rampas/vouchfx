using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Engine.YamlSafety;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Both Compose YAML loads — the adapter configuration and each referenced manifest —
/// go through one loader. A deeply nested file must be an argument error, not a crash.
/// </summary>
public sealed class YamlNestingLimitComposeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("vouchfx-compose-nesting-").FullName;

    [Fact]
    public void Configuration_NestingPastTheLimit_IsInvalidYaml()
    {
        var path = Path.Combine(_directory, "environment.compose.yaml");
        File.WriteAllText(path, Nested(YamlNestingLimit.MaxDepth));

        var error = Assert.Throws<ArgumentException>(() => ComposeConfiguration.Load(path));

        Assert.Contains("Compose configuration: the document nests more than 64 collections", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_NestingPastTheLimit_IsInvalidYaml()
    {
        File.WriteAllText(Path.Combine(_directory, "compose.yaml"), Nested(YamlNestingLimit.MaxDepth));
        var path = Path.Combine(_directory, "environment.compose.yaml");
        File.WriteAllText(path, """
            {"version":1,"files":["./compose.yaml"],"services":{"api":{"service":"web","port":8080,"readiness":{"path":"/health"}}}}
            """);

        var error = Assert.Throws<ArgumentException>(() => ComposeConfiguration.Load(path));

        Assert.Contains("Compose manifest: the document nests more than 64 collections", error.Message, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Nested(int sequences) =>
        "k: " + new string('[', sequences) + "1" + new string(']', sequences) + "\n";
}
