using Vouchfx.Engine.Orchestration.Environments;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

public sealed class ComposeCyclicYamlTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("vouchfx-compose-cycle-").FullName;

    [Fact]
    public void Load_SelfContainingSequenceKeyInConfiguration_ThrowsArgumentException()
    {
        var path = Path.Combine(_directory, "environment.compose.yaml");
        File.WriteAllText(path, "services: { ? &k [ *k ] : v }\n");

        var ex = Assert.Throws<ArgumentException>(() => ComposeConfiguration.Load(path));
        Assert.Contains("*k", ex.Message, StringComparison.Ordinal);
        Assert.Contains("overflows the stack", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_SelfContainingSequenceKeyInManifest_ThrowsArgumentException()
    {
        File.WriteAllText(Path.Combine(_directory, "compose.yaml"), "services: { ? &k [ *k ] : v }\n");
        var path = Path.Combine(_directory, "environment.compose.yaml");
        File.WriteAllText(path, """
            version: 1
            files:
              - ./compose.yaml
            services:
              web:
                service: web
                port: 8080
                readiness:
                  type: http
                  path: /health
            """);

        var ex = Assert.Throws<ArgumentException>(() => ComposeConfiguration.Load(path));
        Assert.Contains("*k", ex.Message, StringComparison.Ordinal);
        Assert.Contains("overflows the stack", ex.Message, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
