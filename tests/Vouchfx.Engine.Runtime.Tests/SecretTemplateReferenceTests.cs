using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;
using YamlDotNet.RepresentationModel;
using static Vouchfx.Engine.Runtime.Tests.SecretTemplateEmissionCensusTests;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class SecretTemplateReferenceTests
{
    [Theory]
    [InlineData("${secret:unknownsource/IGNORED}")]
    [InlineData("${secret:env/}")]
    [InlineData("${secret:env/IGNORED}")]
    public void KafkaAvro_IgnoredPayload_DoesNotAddValidationProvenanceOrReferenceHashes(string payload)
    {
        var yaml = NormalYaml("mq-publish.kafka", avro: true, unknownMarker: null);
        var registry = Discover();
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry);
        var baseline = ast.Steps[0];
        baseline.RawNode.Children[new YamlScalarNode("payload")] = new YamlScalarNode("unused-literal");
        var expectedFields = StepTemplateCensus.Collect(baseline).Fields;
        var expectedProvenance = JsonSerializer.Serialize(ScenarioRunner.DeriveSubstitutionProvenance(baseline, new Dictionary<string, string>()));
        var expectedHashes = JsonSerializer.Serialize(ScenarioRunner.BuildReproducibilityEnvelope(ast, null).SecretReferences);
        baseline.RawNode.Children[new YamlScalarNode("payload")] = new YamlScalarNode(payload);
        Assert.False(ScenarioRunner.TryValidateSecretReferences(ast, out var error, out _), error);
        Assert.Equal(expectedFields, StepTemplateCensus.Collect(baseline).Fields);
        Assert.DoesNotContain(StepTemplateCensus.Collect(baseline).Fields, field => field.Location == "payload");
        Assert.Equal(expectedProvenance, JsonSerializer.Serialize(ScenarioRunner.DeriveSubstitutionProvenance(baseline, new Dictionary<string, string>())));
        Assert.Equal(expectedHashes, JsonSerializer.Serialize(ScenarioRunner.BuildReproducibilityEnvelope(ast, null).SecretReferences));
        var stream = new YamlStream(new YamlDocument(new YamlMappingNode
        {
            { "environment", new YamlMappingNode { { "dependencies", new YamlMappingNode
            {
                { "fixture", new YamlMappingNode { { "type", "kafka" } } },
                { "fixtureName", new YamlMappingNode { { "type", "kafka" } } },
            } } } },
            { "steps", new YamlSequenceNode(baseline.RawNode) },
        }));
        var writer = new StringWriter(); stream.Save(writer, assignAnchors: false);
        var validated = ScenarioValidator.ValidateScenario(writer.ToString(), "avro.e2e.yaml", registry, Path.GetTempPath());
        Assert.True(validated.IsValid, string.Join('\n', validated.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    private static readonly string[] s_refusedScenarioNames = { "refused" };
    public static IEnumerable<object[]> SecretFields() => Fields().Where(row => (bool)row[3] && (string)row[0] != "db-assert.postgres");

    [Theory]
    [MemberData(nameof(SecretFields))]
    public void StandaloneValidator_EveryResolvedFieldRefusesAtPipelineDoor(
        string kind, bool avro, string marker, bool collected, string location)
    {
        Assert.True(collected);
        foreach (var malformed in new[] { false, true })
        {
            var yaml = NormalYaml(kind, avro, marker);
            if (malformed) yaml = yaml.Replace("unknownsource/" + marker, "env/", StringComparison.Ordinal);
            var result = ScenarioValidator.ValidateScenario(yaml, "census.e2e.yaml", Discover(), Path.GetTempPath());
            Assert.False(result.IsValid);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Stage == ValidationStage.Pipeline
                && diagnostic.Message.Contains(malformed ? "malformed" : "names an unknown source", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Stage == ValidationStage.Schema);
        }
        Assert.NotEmpty(location);
    }

    [Theory]
    [MemberData(nameof(SecretFields))]
    public async Task NormalRun_EveryResolvedFieldRefusesBeforeRegisteredDriverAcquisition(
        string kind, bool avro, string marker, bool collected, string location)
    {
        Assert.True(collected);
        foreach (var malformed in new[] { false, true })
        {
            var yaml = NormalYaml(kind, avro, marker);
            if (malformed) yaml = yaml.Replace("unknownsource/" + marker, "env/", StringComparison.Ordinal);
            var registry = Discover();
            var declarations = AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry).Environment;
            var driver = new RefusalDriver(new EnvironmentDeclarations(
                declarations?.Services?.ToDictionary(pair => pair.Key, _ => new DeclaredServiceInfo(Array.Empty<string>()))
                    ?? new Dictionary<string, DeclaredServiceInfo>(),
                declarations?.Dependencies?.ToDictionary(pair => pair.Key, pair => pair.Value.Type)
                    ?? new Dictionary<string, string>()));
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            var root = (YamlMappingNode)stream.Documents[0].RootNode;
            root.Children[new YamlScalarNode("environment")] = new YamlMappingNode
            {
                { "source", new YamlMappingNode { { "driver", driver.Id }, { "config", "./fixture.yaml" } } },
            };
            var writer = new StringWriter(); stream.Save(writer, assignAnchors: false); yaml = writer.ToString();
            var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry);
            var output = new StringWriter();
            var result = await ScenarioRunner.RunSuiteAsync(new[] { ast }, s_refusedScenarioNames, new[] { yaml },
                registry.All.Select(provider => provider.Instance.GetType().Assembly).Distinct(),
                "census-no-DCP", output, environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
            Assert.Equal(0, driver.Starts);
            Assert.Equal(Verdict.Inconclusive, result.Verdict);
            Assert.True(result.AuthoringRefusalObserved);
            Assert.Contains(malformed ? "malformed" : "names an unknown source", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        Assert.NotEmpty(location);
    }

    public static IEnumerable<object[]> SecretProviders() => Providers().Where(row => Slots((string)row[0]).Any(slot => slot.Mode == Mode.Secret));

    [Theory]
    [MemberData(nameof(SecretProviders))]
    public void StandaloneValidator_ValidReferencesCompileWithoutReadingSecretValues(string kind)
    {
        foreach (var avro in kind is "mq-publish.kafka" or "mq-expect.kafka" ? new[] { false, true } : new[] { false })
        {
            var result = ScenarioValidator.ValidateScenario(NormalYaml(kind, avro, null), "census.e2e.yaml", Discover(), Path.GetTempPath());
            Assert.True(result.IsValid, string.Join('\n', result.Diagnostics.Select(diagnostic => diagnostic.Stage + ": " + diagnostic.Message)));
        }
    }

    [Theory]
    [MemberData(nameof(SecretProviders))]
    public void ActualCompiledSourceAndImage_NeverContainResolvedSyntheticCanary(string kind)
    {
        const string canary = "synthetic-value-8bda72d5-never-baked-into-csx";
        foreach (var avro in kind is "mq-publish.kafka" or "mq-expect.kafka" ? new[] { false, true } : new[] { false })
        {
            var fixture = CreateFixture(kind, avro);
            var saved = fixture.Markers.Keys.ToDictionary(marker => marker, Environment.GetEnvironmentVariable);
            try
            {
                foreach (var marker in fixture.Markers.Keys) Environment.SetEnvironmentVariable(marker, canary);
                var registry = Discover();
                var ast = AstBuilder.Build(YamlDocumentParser.Parse(NormalYaml(kind, avro, null)), registry);
                var pipeline = ProviderPipeline.Compile(ast, registry, "CensusCanary");
                Assert.Null(pipeline.Failure);
                Assert.NotNull(pipeline.Assembled);
                var source = pipeline.Assembled!.CsxSource;
                Assert.DoesNotContain(canary, source, StringComparison.Ordinal);
                using var budget = new CancellationTokenSource(RoslynScriptCompiler.DefaultCompileBudget);
                var compiled = RoslynScriptCompiler.CompileOnce(source,
                    additionalReferencePaths: ScenarioRunner.BclReferencePaths().Concat(pipeline.CompileReferencePaths).Distinct(StringComparer.Ordinal).ToArray(),
                    cancellationToken: budget.Token);
                Assert.True(compiled.Image.Length > 0);
                Assert.Equal(-1, compiled.Image.AsSpan().IndexOf(Encoding.UTF8.GetBytes(canary)));
                Assert.Equal(-1, compiled.Image.AsSpan().IndexOf(Encoding.Unicode.GetBytes(canary)));
            }
            finally
            {
                foreach (var (marker, original) in saved) Environment.SetEnvironmentVariable(marker, original);
            }
        }
    }

    public static IEnumerable<object[]> Fields()
    {
        foreach (var provider in Discover().All)
        {
            var kind = provider.Kind.Family + "." + provider.Kind.Provider;
            foreach (var avro in kind is "mq-publish.kafka" or "mq-expect.kafka" ? new[] { false, true } : new[] { false })
                foreach (var (marker, slot) in CreateFixture(kind, avro).Markers)
                    yield return new object[] { kind, avro, marker, slot.Mode == Mode.Secret || (kind == "db-assert.postgres" && slot.Mode == Mode.Placeholder), slot.Path };
        }
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public void UnknownReference_RefusedOnlyInActualTemplateOrLegacyPostgresField(
        string kind, bool avro, string marker, bool collected, string location)
    {
        var node = CreateFixture(kind, avro).Node;
        node = node with { RawNode = (YamlMappingNode)Rewrite(node.RawNode, "env/" + marker, "unknownsource/" + marker) };
        var refused = ScenarioRunner.TryValidateSecretReferences(Ast(node), out var diagnostic, out _);
        Assert.Equal(collected, refused);
        if (collected)
        {
            Assert.Contains("step 'census'", diagnostic, StringComparison.Ordinal);
            Assert.Contains("unknownsource", diagnostic, StringComparison.Ordinal);
            if (kind is not "http.rest" and not "db-assert.postgres") Assert.Contains("field '", diagnostic, StringComparison.Ordinal);
        }
        else Assert.Null(diagnostic);
        Assert.NotEmpty(location);
    }

    [Theory]
    [MemberData(nameof(Providers), MemberType = typeof(SecretTemplateEmissionCensusTests))]
    public void ValidReferences_ProvenanceAndHashUseLabelsNeverEnvironmentValues(string kind)
    {
        var fixture = CreateFixture(kind);
        var labels = fixture.Markers.Where(pair => pair.Value.Mode == Mode.Secret
                || (kind == "db-assert.postgres" && pair.Value.Mode == Mode.Placeholder))
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        const string canary = "synthetic-census-value-7d821cc-never-persist";
        var saved = fixture.Markers.Keys.ToDictionary(marker => marker, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var marker in fixture.Markers.Keys) Environment.SetEnvironmentVariable(marker, canary);
            var ast = Ast(fixture.Node);
            Assert.False(ScenarioRunner.TryValidateSecretReferences(ast, out var diagnostic, out _), diagnostic);
            var provenance = ScenarioRunner.DeriveSubstitutionProvenance(fixture.Node, new Dictionary<string, string>());
            Assert.Equal(labels.OrderBy(label => label), (provenance ?? Array.Empty<Vouchfx.Engine.Abstractions.Events.SubstitutionRef>())
                .Where(reference => reference.SecretDerived).Select(reference => reference.Placeholder[4..]).OrderBy(label => label));
            var envelope = ScenarioRunner.BuildReproducibilityEnvelope(ast, null);
            Assert.Equal(labels.Count, envelope.SecretReferences.Count);
            var serialized = JsonSerializer.Serialize(envelope);
            Assert.DoesNotContain(canary, serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, JsonSerializer.Serialize(provenance), StringComparison.Ordinal);
            Assert.DoesNotContain(canary, string.Join('\n', StepTemplateCensus.Collect(fixture.Node).Fields.Select(field => field.Text)), StringComparison.Ordinal);
            foreach (var marker in labels)
            {
                var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("${secret:env/" + marker + "}"))).ToLowerInvariant();
                Assert.Contains(expected, serialized, StringComparison.Ordinal);
            }
            Assert.True(Discover().TryGet(kind, out var provider));
            var (_, fragment) = BindAndEmit(provider!.Instance, fixture.Node);
            Assert.DoesNotContain(canary, fragment.StatementBlock, StringComparison.Ordinal);
            Assert.All(fragment.RequiredHelpers, helper => Assert.DoesNotContain(canary, helper, StringComparison.Ordinal));
        }
        finally
        {
            foreach (var (marker, original) in saved) Environment.SetEnvironmentVariable(marker, original);
        }
    }

    [Theory]
    [InlineData("body: { integer: 42, float: 1.25, boolean: true, nil: null, quoted: '42', sequence: [false, null, 'a\\b'] }")]
    [InlineData("body: { '${secret:env/BODY_KEY}': { nested: '${secret:env/BODY_VALUE}' }, unicode: 'é😀', empty: '' }")]
    [InlineData("body: &shared { a: [1, 2] }\nheaders: { copied: ignored }")]
    [InlineData("body: 'scalar ${secret:env/BODY_VALUE}'")]
    public void RestBody_CollectedTextExactlyEqualsOnceBoundBody(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var raw = (YamlMappingNode)stream.Documents[0].RootNode;
        raw.Add("target", "fixture"); raw.Add("method", "POST"); raw.Add("path", "/");
        var node = CreateFixture("http.rest").Node with { RawNode = raw };
        Assert.True(Discover().TryGet("http.rest", out var provider));
        var (model, _) = BindAndEmit(provider!.Instance, node);
        var collection = StepTemplateCensus.Collect(node);
        Assert.Null(collection.Failure);
        Assert.Equal(((HttpRestModel)model).Body, Assert.Single(collection.Fields, field => field.Location == "body").Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestBody_OverDepthOrNodeBudget_IsBoundedSafeRefusal(bool depth)
    {
        const string canary = "synthetic-sensitive-body-value";
        YamlNode body = new YamlScalarNode(canary);
        if (depth)
            for (var index = 0; index < 65; index++) body = new YamlSequenceNode(body);
        else body = new YamlSequenceNode(Enumerable.Range(0, 50_001).Select(_ => new YamlScalarNode(canary)));
        var node = CreateFixture("http.rest").Node;
        node.RawNode.Children[new YamlScalarNode("body")] = body;
        Assert.True(ScenarioRunner.TryValidateSecretReferences(Ast(node), out var diagnostic, out _));
        Assert.Contains("maximum depth 64 and 50000 nodes", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, diagnostic, StringComparison.Ordinal);
        Assert.True(Discover().TryGet("http.rest", out var provider));
        var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => BindAndEmit(provider!.Instance, node));
        Assert.NotNull(error.InnerException);
    }

    private static ScenarioAst Ast(StepNode node) => new(null, null, new Dictionary<string, string>(), new[] { node });

    private sealed class RefusalDriver : IEnvironmentDriver
    {
        private readonly EnvironmentDeclarations _declarations;
        internal RefusalDriver(EnvironmentDeclarations declarations) => _declarations = declarations;
        public string Id => "census-refusal";
        internal int Starts;
        public EnvironmentPlan Bind(EnvironmentSourceSpec source, string suiteDirectory) => new(
            _declarations, "census-refusal-v1", EnvironmentStatePolicy.FreshOwned, (_, _) =>
            {
                Interlocked.Increment(ref Starts);
                throw new InvalidOperationException("The authoring refusal must precede acquisition.");
            });
    }

    private static string NormalYaml(string kind, bool avro, string? unknownMarker)
    {
        var fixture = CreateFixture(kind, avro);
        var raw = fixture.Node.RawNode;
        foreach (var name in new[] { "target", "listener", "receiver", "method", "operation" })
        {
            var keep = name switch
            {
                "target" => kind is not "webhook-listen.http" and not "trace-expect.otlp",
                "listener" => kind == "webhook-listen.http",
                "receiver" => kind == "trace-expect.otlp",
                "method" => kind == "http.rest",
                _ => false,
            };
            if (!keep) raw.Children.Remove(new YamlScalarNode(name));
        }
        foreach (var (marker, slot) in fixture.Markers)
            if (slot.Mode == Mode.Literal)
            {
                var value = slot.Path switch
                {
                    "expect.xpath[].path" => "/Envelope/value",
                    "avro.schema" => "{\"type\":\"record\",\"name\":\"Fixture\",\"fields\":[{\"name\":\"fixtureName\",\"type\":\"string\"}]}",
                    _ when slot.Path.EndsWith("json.#", StringComparison.Ordinal) => "$.value",
                    _ => "fixtureName",
                };
                raw = (YamlMappingNode)Rewrite(raw, "${secret:env/" + marker + "}", value);
            }
        if (kind is "http.rest" or "http.soap" or "metrics-assert.prometheus")
        {
            var path = (YamlScalarNode)raw.Children[new YamlScalarNode("path")];
            path.Value = "/" + path.Value;
        }
        if (kind == "http.soap")
        {
            var envelope = (YamlScalarNode)raw.Children[new YamlScalarNode("envelope")];
            envelope.Value = "<Envelope>" + envelope.Value + "</Envelope>";
        }
        if (kind == "storage-assert.s3")
        {
            var expect = (YamlMappingNode)raw.Children[new YamlScalarNode("expect")];
            var minSize = unknownMarker is not null && fixture.Markers[unknownMarker].Path == "expect.minSize";
            expect.Children.Remove(new YamlScalarNode(minSize ? "size" : "minSize"));
        }
        if (kind.EndsWith(".azureservicebus", StringComparison.Ordinal))
        {
            raw.Children.Remove(new YamlScalarNode("topic"));
            raw.Children.Remove(new YamlScalarNode("subscription"));
        }
        if (kind == "cache-assert.elasticsearch") raw.Children[new YamlScalarNode("query")] = new YamlScalarNode("{}");
        if (unknownMarker is not null) raw = (YamlMappingNode)Rewrite(raw, "env/" + unknownMarker, "unknownsource/" + unknownMarker);
        var root = new YamlMappingNode { { "steps", new YamlSequenceNode(raw) } };
        var dependency = kind switch
        {
            "mail-expect.smtp" => "mailpit",
            "storage-assert.s3" => "s3",
            "cache-assert.elasticsearch" => "elasticsearch",
            _ when kind.StartsWith("mq-", StringComparison.Ordinal) => kind.Split('.')[1],
            _ => null,
        };
        if (dependency is not null)
            root.Add("environment", new YamlMappingNode { { "dependencies", new YamlMappingNode { { "fixture", new YamlMappingNode { { "type", dependency } } } } } });
        else if (kind is "http.rest" or "http.soap" or "metrics-assert.prometheus")
            root.Add("environment", new YamlMappingNode { { "services", new YamlMappingNode { { "fixture", new YamlMappingNode { { "image", "app" } } } } } });
        if (avro)
        {
            var environment = (YamlMappingNode)root.Children[new YamlScalarNode("environment")];
            var dependencies = (YamlMappingNode)environment.Children[new YamlScalarNode("dependencies")];
            dependencies.Add("fixtureName", new YamlMappingNode { { "type", "kafka" } });
        }
        var stream = new YamlStream(new YamlDocument(root));
        var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static YamlNode Rewrite(YamlNode node, string from, string to) => node switch
    {
        YamlScalarNode scalar => new YamlScalarNode(scalar.Value?.Replace(from, to, StringComparison.Ordinal)) { Style = scalar.Style },
        YamlSequenceNode sequence => new YamlSequenceNode(sequence.Children.Select(child => Rewrite(child, from, to))),
        YamlMappingNode map => new YamlMappingNode(map.Children.Select(pair => new KeyValuePair<YamlNode, YamlNode>(Rewrite(pair.Key, from, to), Rewrite(pair.Value, from, to)))),
        _ => throw new InvalidOperationException("Fixture contains an unsupported YAML node."),
    };
}
