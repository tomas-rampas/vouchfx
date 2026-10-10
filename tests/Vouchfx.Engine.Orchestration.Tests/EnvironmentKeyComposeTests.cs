using System.Text.Json;
using Json.Schema;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration.Environments;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

public sealed class EnvironmentKeyComposeTests
{
    private static readonly string[] s_files = { "./compose.yaml" };
    private static readonly JsonSchema s_schema = ReadSchema();
    [Theory]
    [InlineData("")]
    [InlineData("FOO=credential_canary_607")]
    [InlineData("FOO\0")]
    [InlineData("FOO\n")]
    [InlineData("FOO\r\n")]
    [InlineData("BAD/name")]
    [InlineData("BAD~name")]
    public void Bind_InvalidEnvironmentName_UsesSafeFieldDiagnostic(string key)
    {
        var path = Configuration(key);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(s_schema.Evaluate(doc.RootElement).IsValid);
        var error = Assert.Throws<EnvironmentBindingException>(() => new ComposeEnvironmentDriver().Bind(new EnvironmentSourceSpec("compose", path), Path.GetDirectoryName(path)!));
        Assert.Contains("Environment variable names", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("credential_canary_607", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("value_canary_607", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\0', error.Message);
        Assert.DoesNotContain('\r', error.Message);
        Assert.DoesNotContain('\n', error.Message);
        Assert.Contains("environment", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("COMPOSE_PROJECT_NAME")]
    [InlineData("DOCKER_HOST")]
    public void Bind_ValidReservedName_RetainsOwnershipReason(string key)
    {
        var path = Configuration(key);
        var error = Assert.Throws<EnvironmentBindingException>(() => new ComposeEnvironmentDriver().Bind(new EnvironmentSourceSpec("compose", path), Path.GetDirectoryName(path)!));
        Assert.Contains("cannot override Compose ownership selections or Docker daemon configuration", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DottedCaseName_ConfigAndActualSchemaAgree()
    {
        var path = Configuration("VOUCHFX_PROBE.key_1");
        var configuration = ComposeConfiguration.Load(path);
        Assert.Equal("value_canary_607", configuration.Environment["VOUCHFX_PROBE.key_1"]);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(s_schema.Evaluate(doc.RootElement).IsValid);
    }

    private static JsonSchema ReadSchema()
    {
        var assembly = typeof(ComposeEnvironmentDriver).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith("compose-environment-schema.json", StringComparison.Ordinal)))!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private static string Configuration(string key)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-env607-compose-").FullName;
        File.WriteAllText(Path.Combine(directory, "compose.yaml"), "services: { app: { image: app } }");
        var path = Path.Combine(directory, "environment.compose.yaml");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            version = 1,
            files = s_files,
            environment = new Dictionary<string, string> { [key] = "value_canary_607" },
            services = new { api = new { service = "app", port = 8080, readiness = new { path = "/health", status = 200 } } },
        }));
        return path;
    }
}
