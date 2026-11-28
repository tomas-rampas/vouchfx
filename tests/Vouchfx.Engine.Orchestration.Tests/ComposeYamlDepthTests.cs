// The nesting limit at the two Compose entry points that load author-supplied YAML: the
// configuration file and the manifest it lists. Nesting this deep overflows the stack inside
// YamlStream.Load, which no catch can stop, so each refused input is refused before the load;
// if the check were unwired the 200,000-level rows would crash the test host instead of
// failing. No Docker executable is invoked: Load only reads and binds the files.
using System.Diagnostics;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Tests;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

public sealed class ComposeYamlDepthTests : IDisposable
{
    private const string DepthText = "nested more than 64 levels deep";
    private static readonly TimeSpan s_bound = TimeSpan.FromSeconds(5);

    private const string ValidConfig = """
        version: 1
        files: [./compose.yaml]
        services:
          api:
            service: api
            port: 8080
            readiness: { path: /health, status: 200 }
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory("vouchfx-compose-depth-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Config => Path.Combine(_directory, "environment.compose.yaml");

    private string Manifest => Path.Combine(_directory, "compose.yaml");

    private ComposeConfiguration LoadWith(string config, string manifest)
    {
        File.WriteAllText(Config, config);
        File.WriteAllText(Manifest, manifest);
        return ComposeConfiguration.Load(Config);
    }

    private ArgumentException RefusedConfig(string config)
    {
        var ex = Assert.Throws<ArgumentException>(() => LoadWith(config, "services: { api: { image: app } }"));
        Assert.StartsWith("Compose configuration: invalid YAML at line ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("64", ex.Message, StringComparison.Ordinal);
        Assert.Contains(DepthText, ex.Message, StringComparison.Ordinal);
        return ex;
    }

    private ArgumentException RefusedManifest(string manifest)
    {
        var ex = Assert.Throws<ArgumentException>(() => LoadWith(ValidConfig, manifest));
        Assert.StartsWith("Compose manifest: invalid YAML at line ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("64", ex.Message, StringComparison.Ordinal);
        Assert.Contains(DepthText, ex.Message, StringComparison.Ordinal);
        return ex;
    }

    [Fact]
    public void Config_65NestedFlowSequences_IsRefusedWithLimitLineAndColumn()
        => Assert.Contains("line 1, column 65",
            RefusedConfig(DeepYamlShapes.ClosedFlowSequences(DeepYamlShapes.Limit + 1)).Message, StringComparison.Ordinal);

    [Fact]
    public void Config_65NestedBlockMappings_IsRefusedWithLimitLineAndColumn()
        => Assert.Contains("line 65, column 65",
            RefusedConfig(DeepYamlShapes.BlockMappings(DeepYamlShapes.Limit + 1)).Message, StringComparison.Ordinal);

    [Fact]
    public void Config_64NestedFlowSequences_IsNotRefusedByTheDepthCheck()
    {
        var ex = Assert.Throws<ArgumentException>(() => LoadWith(
            DeepYamlShapes.ClosedFlowSequences(DeepYamlShapes.Limit), "services: { api: { image: app } }"));
        Assert.DoesNotContain(DepthText, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_65NestedFlowSequences_IsRefusedWithLimitLineAndColumn()
        => Assert.Contains("line 1, column 65",
            RefusedManifest(DeepYamlShapes.ClosedFlowSequences(DeepYamlShapes.Limit + 1)).Message, StringComparison.Ordinal);

    [Fact]
    public void Manifest_65NestedBlockMappings_IsRefusedWithLimitLineAndColumn()
        => Assert.Contains("line 65, column 65",
            RefusedManifest(DeepYamlShapes.BlockMappings(DeepYamlShapes.Limit + 1)).Message, StringComparison.Ordinal);

    [Fact]
    public void Manifest_64NestedFlowSequences_IsNotRefusedByTheDepthCheck()
    {
        // A manifest whose root is not a mapping is skipped, so the load succeeds: the depth
        // check did not refuse a document nested exactly 64 deep.
        var configuration = LoadWith(ValidConfig, DeepYamlShapes.ClosedFlowSequences(DeepYamlShapes.Limit));
        Assert.NotNull(configuration);
    }

    [Fact]
    public void Manifest_64NestedBlockMappings_IsNotRefusedByTheDepthCheck()
    {
        var ex = Record.Exception(() => LoadWith(ValidConfig, DeepYamlShapes.BlockMappings(DeepYamlShapes.Limit)));
        Assert.DoesNotContain(DepthText, ex?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("flow", 200_000)]
    [InlineData("flow", DeepYamlShapes.OneMiB)]
    [InlineData("flow", DeepYamlShapes.FourMiB)]
    [InlineData("block", 200_000)]
    [InlineData("block", DeepYamlShapes.OneMiB)]
    [InlineData("block", DeepYamlShapes.FourMiB)]
    public void Config_VeryDeepInput_IsRefusedInTimeBoundedByTheLimit(string shape, int characters)
    {
        var yaml = DeepYamlShapes.Large(shape, characters);
        File.WriteAllText(Config, yaml);
        File.WriteAllText(Manifest, "services: { api: { image: app } }");
        var clock = Stopwatch.StartNew();
        var ex = Assert.Throws<ArgumentException>(() => ComposeConfiguration.Load(Config));
        clock.Stop();
        Assert.Contains(DepthText, ex.Message, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < s_bound, $"refusal took {clock.Elapsed}");
    }

    [Theory]
    [InlineData("flow", 200_000)]
    [InlineData("flow", DeepYamlShapes.OneMiB)]
    [InlineData("flow", DeepYamlShapes.FourMiB)]
    [InlineData("block", 200_000)]
    [InlineData("block", DeepYamlShapes.OneMiB)]
    [InlineData("block", DeepYamlShapes.FourMiB)]
    public void Manifest_VeryDeepInput_IsRefusedInTimeBoundedByTheLimit(string shape, int characters)
    {
        var yaml = DeepYamlShapes.Large(shape, characters);
        File.WriteAllText(Config, ValidConfig);
        File.WriteAllText(Manifest, yaml);
        var clock = Stopwatch.StartNew();
        var ex = Assert.Throws<ArgumentException>(() => ComposeConfiguration.Load(Config));
        clock.Stop();
        Assert.Contains(DepthText, ex.Message, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < s_bound, $"refusal took {clock.Elapsed}");
    }
}
