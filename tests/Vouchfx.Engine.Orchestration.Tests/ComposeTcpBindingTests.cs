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

    // An alias inside a mapping key that names a collection, or an anchor not defined before it,
    // makes YamlStream.Load hash or compare the key without bound (stack overflow, or exponential
    // time). The pre-scan must refuse every shape before Load, with the line and column. Most rows
    // would crash the test host if the scan were unwired, so each crash-capable test first loads an
    // acyclic document that Load survives: a wiring regression then fails that assertion cleanly.
    // The canary proves only that the scan is wired in: a regression in the scan itself that
    // stopped refusing one shape would still crash the host on that shape's row.
    private const string ConfigurationCanary = "a: &k [ x ]\nb: { ? *k : v }";
    private const string ManifestCanary = "x: &k [ one ]\ny: { ? *k : v }";

    [Fact]
    public void AcyclicCollectionAliasInMappingKey_IsRefusedAsInvalidYaml()
    {
        AssertConfigurationRefuses(ConfigurationCanary, 2, 8, CollectionCause);
    }

    [Theory]
    [InlineData("{ ? &k [ *k ] : v }", 1, 10)]
    [InlineData("a: &k [ *k ]\nb: { ? *k : v }", 2, 8)]
    [InlineData("a: &k [ *k ]\nb: { *k : v }", 2, 6)]
    [InlineData("a: &k [ *k ]\n*k : v", 2, 1)]
    [InlineData("a: &k [ *k ]\n? [ *k ] : v", 2, 5)]
    [InlineData("a: &k !!seq [ *k ]\n? !!seq [ *k ] : v", 2, 11)]
    [InlineData("a: &x [ &y [ *x ] ]\nb: { ? *y : v }", 2, 8)]
    public void CollectionAliasInMappingKey_IsRefusedAsInvalidYaml_NotAStackOverflow(string text, int line, int column)
    {
        AssertConfigurationRefuses(ConfigurationCanary, 2, 8, CollectionCause);
        AssertConfigurationRefuses(text, line, column, CollectionCause);
    }

    [Theory]
    [InlineData("? *k : v\nb: &k x", 1, 3)]
    [InlineData("? *k : v\nb: &k [ *k ]", 1, 3)]
    [InlineData("a: { ? *k : v }\nb: &k [ *k ]", 1, 8)]
    [InlineData("a: { *k : v }\nb: &k [ *k ]", 1, 6)]
    [InlineData("? { ? *k : 1 } : 2\nb: &k [ *k ]", 1, 7)]
    [InlineData("a: [ ? *k : v ]\nb: &k [ *k ]", 1, 8)]
    [InlineData("a: [ *k : v ]\nb: &k [ *k ]", 1, 6)]
    [InlineData("? [ *k ] : *k\nb: &k [ *k ]", 1, 5)]
    [InlineData("b: { ? *nowhere : v }", 1, 8)]
    public void AliasInMappingKeyBeforeItsAnchor_IsRefusedAsInvalidYaml_NotAStackOverflow(string text, int line, int column)
    {
        AssertConfigurationRefuses(ConfigurationCanary, 2, 8, CollectionCause);
        AssertConfigurationRefuses(text, line, column, ForwardCause);
    }

    [Fact]
    public void ForwardBillionLaughsKey_IsRefusedPromptly()
    {
        AssertConfigurationRefuses(ConfigurationCanary, 2, 8, CollectionCause);

        var text = new System.Text.StringBuilder("? [ *a9 ] : *a9\na0: &a0 [x, y]\n");
        for (var level = 1; level <= 9; level++)
        {
            text.Append('a').Append(level).Append(": &a").Append(level).Append(" [")
                .Append(string.Join(", ", Enumerable.Repeat("*a" + (level - 1), 10))).Append("]\n");
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        AssertConfigurationRefuses(text.ToString(), 1, 5, ForwardCause);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"refusal took {watch.Elapsed}");
    }

    // The configuration is clean; the manifest it references holds the refused key, so the
    // second load (the manifest scan) is the one that must refuse it.
    [Fact]
    public void AcyclicCollectionAliasInManifestKey_IsRefusedAsInvalidManifestYaml()
    {
        AssertManifestRefuses(ManifestCanary, 2, 8, CollectionCause);
    }

    [Theory]
    [InlineData("services: { web: { image: app } }\nx: &k [ *k ]\ny: { ? *k : v }", 3, 8, false)]
    [InlineData("services: { web: { image: app } }\n? *k : v\nx: &k x", 2, 3, true)]
    [InlineData("services: { web: { image: app } }\ny: { ? { ? *k : 1 } : 2 }\nx: &k [ *k ]", 2, 12, true)]
    [InlineData("services: { web: { image: app } }\ny: [ *k : v ]\nx: &k [ *k ]", 2, 6, true)]
    [InlineData("services: { web: { image: app } }\n? [ *k ] : *k\nx: &k [ *k ]", 2, 5, true)]
    [InlineData("services: { web: { image: app } }\ny: { ? *k : v }", 2, 8, true)]
    public void RefusedAliasInManifestKey_IsRefusedAsInvalidManifestYaml(string manifest, int line, int column, bool forward)
    {
        AssertManifestRefuses(ManifestCanary, 2, 8, CollectionCause);
        AssertManifestRefuses(manifest, line, column, forward ? ForwardCause : CollectionCause);
    }

    // The cap on the scalar text that aliases bring into keys, tags included: line 1 anchors a
    // 100,000-character value, or the value `x` under a 100,000-character tag; each key that
    // uses it is charged 100,001 or 100,002, so the eleventh key (line 12) passes the cap.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScalarTextRepeatedInKeysPastTheCap_IsRefusedAsInvalidYaml(bool tagged)
    {
        var text = new System.Text.StringBuilder("a: &a ");
        if (tagged)
            text.Append('!').Append('t', 99_999).Append(" x\n");
        else
            text.Append('x', 100_000).Append('\n');
        for (var i = 0; i < 11; i++)
            text.Append("[*a, ").Append(i).Append("]: v\n");

        AssertConfigurationRefuses(text.ToString(), 12, 2, BudgetCause);
    }

    [Fact]
    public void ScalarTextRepeatedInManifestKeysPastTheCap_IsRefusedAsInvalidManifestYaml()
    {
        var manifest = new System.Text.StringBuilder("x: &a !").Append('t', 99_999).Append(" x\n");
        for (var i = 0; i < 11; i++)
            manifest.Append("[*a, ").Append(i).Append("]: v\n");
        manifest.Append("services: { web: { image: app } }\n");

        AssertManifestRefuses(manifest.ToString(), 12, 2, BudgetCause);
    }

    private const string BudgetCause =
        "brings the scalar text repeated through aliases in keys, tags included, past 1048576 characters " +
        "(a key nested in another key counts again); shorten the aliased scalars or their tags, or write fewer such keys.";

    private const string CollectionCause = "refers to a mapping or sequence; a key may refer by alias only to a scalar.";
    private const string ForwardCause =
        "refers to an anchor that is not defined before it; a key may refer by alias only to a scalar anchor defined earlier in the same document.";

    private void AssertConfigurationRefuses(string text, int line, int column, string cause)
    {
        var path = Path.Combine(_directory, "key-alias.compose.yaml");
        File.WriteAllText(path, text);
        var ex = Assert.Throws<ArgumentException>(() => ComposeConfiguration.Load(path));
        Assert.StartsWith(
            $"Compose configuration: invalid YAML at line {line}, column {column}: alias '", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(cause, ex.Message, StringComparison.Ordinal);
    }

    private void AssertManifestRefuses(string manifest, int line, int column, string cause)
    {
        var path = WriteConfiguration("{\"web\":{\"service\":\"web\",\"port\":8080,\"readiness\":{\"path\":\"/health\",\"status\":200}}}");
        File.WriteAllText(Path.Combine(_directory, "compose.yaml"), manifest);
        var ex = Assert.Throws<ArgumentException>(() => ComposeConfiguration.Load(path));
        Assert.StartsWith(
            $"Compose manifest: invalid YAML at line {line}, column {column}: alias '", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(cause, ex.Message, StringComparison.Ordinal);
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
