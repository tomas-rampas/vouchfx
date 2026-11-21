using System.Text.Json;
using Json.Schema;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration.Environments;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>Adapter configuration and schema agreement; no Docker, network or credentials are used.</summary>
public sealed class ComposeTcpBindingTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("vouchfx-compose-tcp-bind-").FullName;
    private static readonly JsonSchema s_schema = JsonSchema.FromText(
        ComposeEnvironmentDriver.ExportConfigurationSchema(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });

    [Theory]
    [InlineData("tcp")]
    [InlineData("healthcheck")]
    public void TcpOnlyConfiguration_BindsAsServiceWithExplicitReadiness(string readiness)
    {
        var path = WriteConfiguration("""
            {"broker":{"type":"tcp","service":"kafka","port":9092,"readiness":{"type":"READY"}}}
            """.Replace("READY", readiness, StringComparison.Ordinal));
        Assert.True(SchemaValid(path));
        var configuration = ComposeConfiguration.Load(path);
        var declarations = configuration.Declarations();
        Assert.Equal("tcp", Assert.Single(declarations.Services["broker"].EndpointNames));
        Assert.Empty(declarations.Dependencies);
        var plan = new ComposeEnvironmentDriver().Bind(new EnvironmentSourceSpec("compose", path), _directory);
        Assert.Equal("tcp", Assert.Single(plan.Declarations.Services["broker"].EndpointNames));
        Assert.Contains(path, plan.InputFiles);
    }

    // An alias to a collection inside a mapping key makes YamlStream.Load hash the key without
    // bound (stack overflow, or exponential time). The pre-scan must refuse every shape before
    // Load, with the line and column, so none of these can crash the test host.
    [Theory]
    [InlineData("{ ? &k [ *k ] : v }", 1, 10)]
    [InlineData("a: &k [ *k ]\nb: { ? *k : v }", 2, 8)]
    [InlineData("a: &k [ x ]\nb: { ? *k : v }", 2, 8)]
    [InlineData("a: &k [ *k ]\nb: { *k : v }", 2, 6)]
    [InlineData("a: &k [ *k ]\n*k : v", 2, 1)]
    [InlineData("a: &k [ *k ]\n? [ *k ] : v", 2, 5)]
    [InlineData("a: &k !!seq [ *k ]\n? !!seq [ *k ] : v", 2, 11)]
    [InlineData("a: &x [ &y [ *x ] ]\nb: { ? *y : v }", 2, 8)]
    public void CollectionAliasInMappingKey_IsRefusedAsInvalidYaml_NotAStackOverflow(string text, int line, int column)
    {
        var path = Path.Combine(_directory, "key-alias.compose.yaml");
        File.WriteAllText(path, text);
        var ex = Assert.Throws<ArgumentException>(() => ComposeConfiguration.Load(path));
        Assert.StartsWith(
            $"Compose configuration: invalid YAML at line {line}, column {column}: alias '", ex.Message, StringComparison.Ordinal);
        Assert.Contains("a key may refer by alias only to a scalar", ex.Message, StringComparison.Ordinal);
    }

    // The configuration is clean; the manifest it references holds the refused key, so the
    // second load (the manifest scan) is the one that must refuse it.
    [Theory]
    [InlineData("services: { web: { image: app } }\nx: &k [ *k ]\ny: { ? *k : v }", 3, 8)]
    [InlineData("x: &k [ one ]\ny: { ? *k : v }", 2, 8)]
    public void CollectionAliasInManifestKey_IsRefusedAsInvalidManifestYaml(string manifest, int line, int column)
    {
        var path = WriteConfiguration("{\"web\":{\"service\":\"web\",\"port\":8080,\"readiness\":{\"path\":\"/health\",\"status\":200}}}");
        File.WriteAllText(Path.Combine(_directory, "compose.yaml"), manifest);
        var ex = Assert.Throws<ArgumentException>(() => ComposeConfiguration.Load(path));
        Assert.StartsWith(
            $"Compose manifest: invalid YAML; at line {line}, column {column}: alias 'k'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"service\":\"web\",\"port\":8080,\"readiness\":{\"path\":\"/health\",\"status\":200}}")]
    [InlineData("{\"type\":\"http\",\"service\":\"web\",\"port\":8080,\"readiness\":{\"type\":\"http\",\"path\":\"/health\"}}")]
    public void ExistingAndExplicitHttpForms_KeepHttpDeclarations(string binding)
    {
        var path = WriteConfiguration("{\"api\":" + binding + "}");
        Assert.True(SchemaValid(path));
        Assert.Equal("http", Assert.Single(ComposeConfiguration.Load(path).Declarations().Services["api"].EndpointNames));
    }

    [Fact]
    public void ExistingPostgresForm_RemainsDependencyAndDoesNotResolvePassword()
    {
        var path = WriteConfiguration("{}", """
            {"db":{"type":"postgres","service":"db","port":5432,"database":"app","username":"app","password":"${secret:env/VOUCHFX_MISSING_TCP_TEST_PASSWORD}"}}
            """);
        Assert.True(SchemaValid(path));
        var plan = new ComposeEnvironmentDriver().Bind(new EnvironmentSourceSpec("compose", path), _directory);
        Assert.Empty(plan.Declarations.Services);
        Assert.Equal("postgres", plan.Declarations.Dependencies["db"]);
    }

    [Theory]
    [InlineData("{\"type\":\"udp\",\"service\":\"kafka\",\"port\":9092,\"readiness\":{\"type\":\"tcp\"}}")]
    [InlineData("{\"type\":\"TCP\",\"service\":\"kafka\",\"port\":9092,\"readiness\":{\"type\":\"tcp\"}}")]
    [InlineData("{\"type\":\"tcp\",\"service\":\"kafka\",\"port\":9092}")]
    [InlineData("{\"type\":\"tcp\",\"service\":\"kafka\",\"port\":9092,\"readiness\":{}}")]
    [InlineData("{\"type\":\"tcp\",\"service\":\"kafka\",\"port\":9092,\"readiness\":{\"path\":\"/health\"}}")]
    [InlineData("{\"type\":\"tcp\",\"service\":\"kafka\",\"port\":9092,\"readiness\":{\"type\":\"http\",\"path\":\"/health\"}}")]
    [InlineData("{\"type\":\"tcp\",\"service\":\"kafka\",\"port\":9092,\"readiness\":{\"type\":\"tcp\",\"status\":200}}")]
    [InlineData("{\"type\":\"tcp\",\"service\":\"kafka\",\"port\":9092,\"readiness\":{\"type\":\"healthcheck\",\"path\":\"/health\"}}")]
    [InlineData("{\"type\":\"tcp\",\"service\":\"kafka\",\"port\":9092,\"readiness\":{\"type\":\"image\"}}")]
    [InlineData("{\"type\":\"tcp\",\"service\":\"kafka\",\"port\":9092,\"readiness\":{\"type\":\"tcp\",\"skip\":true}}")]
    [InlineData("{\"service\":\"web\",\"port\":8080,\"readiness\":{\"type\":\"tcp\"}}")]
    [InlineData("{\"type\":\"http\",\"service\":\"web\",\"port\":8080,\"readiness\":{\"type\":\"healthcheck\"}}")]
    [InlineData("{\"type\":\"tcp\",\"service\":\"kafka\",\"port\":0,\"readiness\":{\"type\":\"tcp\"}}")]
    [InlineData("{\"type\":\"tcp\",\"service\":\"kafka\",\"port\":65536,\"readiness\":{\"type\":\"tcp\"}}")]
    public void WrongProtocolOrReadinessShape_SchemaAndPureBindingRefuse(string binding)
    {
        var path = WriteConfiguration("{\"broker\":" + binding + "}");
        Assert.False(SchemaValid(path));
        var error = Assert.Throws<EnvironmentBindingException>(() => new ComposeEnvironmentDriver()
            .Bind(new EnvironmentSourceSpec("compose", path), _directory));
        Assert.Contains("services.broker", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadinessEdit_ChangesIdentityAndRetainsImmutableDeclaredEndpoint()
    {
        var path = WriteConfiguration("""
            {"broker":{"type":"tcp","service":"kafka","port":9092,"readiness":{"type":"tcp"}}}
            """);
        var first = new ComposeEnvironmentDriver().Bind(new EnvironmentSourceSpec("compose", path), _directory);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"type\":\"tcp\"}}", "\"type\":\"healthcheck\"}}", StringComparison.Ordinal));
        var second = new ComposeEnvironmentDriver().Bind(new EnvironmentSourceSpec("compose", path), _directory);
        Assert.NotEqual(first.SourceIdentity, second.SourceIdentity);
        Assert.Equal("tcp", Assert.Single(first.Declarations.Services["broker"].EndpointNames));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)first.Declarations.Services["broker"].EndpointNames)[0] = "changed");
    }

    private string WriteConfiguration(string services, string dependencies = "{}")
    {
        File.WriteAllText(Path.Combine(_directory, "compose.yaml"), "services: { kafka: { image: broker }, web: { image: app }, db: { image: postgres } }");
        var path = Path.Combine(_directory, "environment.compose.yaml");
        File.WriteAllText(path, "{\"version\":1,\"files\":[\"./compose.yaml\"],\"services\":" + services + ",\"dependencies\":" + dependencies + "}");
        return path;
    }

    private static bool SchemaValid(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return s_schema.Evaluate(document.RootElement).IsValid;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
