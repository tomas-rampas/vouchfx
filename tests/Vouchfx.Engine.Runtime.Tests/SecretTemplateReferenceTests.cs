using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
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

    // A '+' in a secret path is legal (the path runs to the closing brace). The structured
    // body is JSON, and the default encoder would rewrite '+' as \u002B before the census
    // and the resolver ever see the token, so the run would request a different path.
    [Fact]
    public void RestBody_PlusInSecretPath_ReachesTheCensusUnescaped()
    {
        var node = RestNode("body: { token: '${secret:env/a+b}' }");
        var text = Assert.Single(StepTemplateCensus.Collect(node).Fields, field => field.Location == "body").Text;
        Assert.Contains("${secret:env/a+b}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u002B", text, StringComparison.Ordinal);
        var reference = Assert.Single(SecretReference.FindAll(text));
        Assert.Equal("env", reference.Source);
        Assert.Equal("a+b", reference.Path);
    }

    // The census reads a structured body through the binder's own conversion (ZEB-67 review:
    // HttpRestBodyConversion is one source file compiled into both assemblies). One row per
    // rule the conversion applies — merge with an override, block scalar, plain empty value,
    // !!str and the other core tags, non-finite number text — beside the original rows.
    [Theory]
    [InlineData("body: { integer: 42, float: 1.25, boolean: true, nil: null, quoted: '42', sequence: [false, null, 'a\\b'] }")]
    [InlineData("body: { '${secret:env/BODY_KEY}': { nested: '${secret:env/BODY_VALUE}' }, unicode: 'é😀', empty: '' }")]
    [InlineData("body: &shared { a: [1, 2] }\nheaders: { copied: ignored }")]
    [InlineData("body: 'scalar ${secret:env/BODY_VALUE}'")]
    [InlineData("body: { <<: { a: 1, t: '${secret:env/OVERRIDDEN}' }, t: '${secret:env/SENT}' }")]
    [InlineData("base: &base { a: 1, t: '${secret:env/MERGED}' }\nbody: { <<: [*base, { z: 2 }], b: 3 }")]
    [InlineData("body:\n  n: |\n    123\n  t: >-\n    token\n    ${secret:env/BLOCK}\n  k: |+\n    true\n\n")]
    [InlineData("body:\n  note:\n  quoted: ''\n  count: 1")]
    [InlineData("body: { count: !!str 123, n: !!int '5', f: !!float '1.5', b: !!bool 'true', z: !!null '', ts: !!timestamp 2026-11-17 }")]
    [InlineData("body: { nan: NaN, inf: Infinity, neg: -Infinity, big: 1e400, fin: 1e308 }")]
    public void RestBody_CollectedTextExactlyEqualsOnceBoundBody(string yaml)
    {
        var node = RestNode(yaml);
        Assert.True(Discover().TryGet("http.rest", out var provider));
        var (model, _) = BindAndEmit(provider!.Instance, node);
        var bound = (HttpRestModel)model;
        Assert.Null(bound.BodyRefusal);
        var collection = StepTemplateCensus.Collect(node);
        Assert.Equal(bound.Body, Assert.Single(collection.Fields, field => field.Location == "body").Text);
    }

    // The two invariants the reviewer named, end to end through the census's three consumers:
    // a reference in a member the explicit key overrides is never sent, so the check before
    // acquisition must not refuse it, and provenance and the reproducibility envelope must
    // not list it; a reference that IS sent through a merge must be found by all three.
    [Fact]
    public void RestBody_MergeOverriddenUnknownSource_IsAcceptedAndNotListed()
    {
        var node = RestNode("body: { <<: { t: '${secret:nosuchsource/X}' }, t: fixed }");
        Assert.False(ScenarioRunner.TryValidateSecretReferences(Ast(node), out var diagnostic, out _), diagnostic);
        Assert.Null(ScenarioRunner.DeriveSubstitutionProvenance(node, new Dictionary<string, string>()));
        Assert.Empty(ScenarioRunner.BuildReproducibilityEnvelope(Ast(node), null).SecretReferences);

        var result = ScenarioValidator.ValidateScenario(
            RestSuite("body: { <<: { t: '${secret:nosuchsource/X}' }, t: fixed }"),
            "merge.e2e.yaml", Discover(), Path.GetTempPath());
        Assert.True(result.IsValid, string.Join('\n', result.Diagnostics.Select(d => d.Stage + ": " + d.Message)));
    }

    private static readonly string[] s_sentMergeLabels = { "env/MERGED", "env/SENT" };

    [Fact]
    public void RestBody_MergedAndOverridden_OnlySentReferencesReachProvenanceAndEnvelope()
    {
        var node = RestNode("body: { <<: { m: '${secret:env/MERGED}', t: '${secret:env/OVERRIDDEN}' }, t: '${secret:env/SENT}' }");
        Assert.False(ScenarioRunner.TryValidateSecretReferences(Ast(node), out var diagnostic, out _), diagnostic);

        var provenance = ScenarioRunner.DeriveSubstitutionProvenance(node, new Dictionary<string, string>())!;
        Assert.Equal(s_sentMergeLabels,
            provenance.Where(reference => reference.SecretDerived).Select(reference => reference.Placeholder).OrderBy(label => label, StringComparer.Ordinal));

        var hashes = ScenarioRunner.BuildReproducibilityEnvelope(Ast(node), null).SecretReferences;
        var serialized = JsonSerializer.Serialize(hashes);
        Assert.Equal(2, hashes.Count);
        Assert.Contains(ReferenceHash("${secret:env/MERGED}"), serialized, StringComparison.Ordinal);
        Assert.Contains(ReferenceHash("${secret:env/SENT}"), serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(ReferenceHash("${secret:env/OVERRIDDEN}"), serialized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("body: { <<: { t: '${secret:nosuchsource/MERGED}' }, a: 1 }", "nosuchsource")]
    [InlineData("body:\n  note: |\n    token ${secret:nosuchsource/BLOCK}\n", "nosuchsource")]
    [InlineData("body:\n  note: >-\n    ${secret:env/}\n", "malformed")]
    public void RestBody_SentReferenceInMergeOrBlockScalar_IsStillChecked(string yaml, string expected)
    {
        var node = RestNode(yaml);
        Assert.True(ScenarioRunner.TryValidateSecretReferences(Ast(node), out var diagnostic, out _));
        Assert.Contains("step 'census'", diagnostic, StringComparison.Ordinal);
        Assert.Contains(expected, diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(field 'body')", diagnostic, StringComparison.Ordinal);
    }

    // ZEB-67 review: a body the conversion refuses is an AUTHORING refusal. Through the full
    // engine path the author sees the provider's own message once, on the pipeline stage,
    // with no provider-fault attribution, and the secret census adds nothing of its own (it
    // used to add a "maximum depth 64 and 50000 nodes" line for an over-limit body). Each row
    // passes schema validation, so it reaches the binder on the real path.
    public static IEnumerable<object[]> RefusedBodies() => new[]
    {
        new object[] { "body:\n  v: !!bool yes\n", "tags the value at line 12, column 10 as !!bool, but the value is not one of true" },
        new object[] { "body:\n  <<: oops\n", "has a merge key '<<' at line 12, column 7 that cannot be applied: its value is a scalar" },
        new object[] { "body:\n  v: !money 5\n", "uses the YAML tag '!money' on a scalar at line 12, column 10" },
        new object[] { "body:\n  a: &a [\"x\",\"x\",\"x\",\"x\",\"x\",\"x\",\"x\",\"x\",\"x\",\"x\"]\n  b: &b [*a, *a, *a, *a, *a, *a, *a, *a, *a, *a]\n  c: &c [*b, *b, *b, *b, *b, *b, *b, *b, *b, *b]\n  d: &d [*c, *c, *c, *c, *c, *c, *c, *c, *c, *c]\n  e: [*d, *d, *d, *d, *d, *d, *d, *d, *d, *d]\n", "expands to more than 50000 JSON nodes" },
    };

    [Theory]
    [MemberData(nameof(RefusedBodies))]
    public void RestBody_ConversionRefusal_IsOneAuthoringRefusalNeverAProviderFault(string bodyYaml, string expected)
    {
        var yaml = RestSuite(bodyYaml);
        var registry = Discover();

        var result = ScenarioValidator.ValidateScenario(yaml, "refused.e2e.yaml", registry, Path.GetTempPath());
        Assert.False(result.IsValid);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ValidationStage.Pipeline, diagnostic.Stage);
        Assert.StartsWith("Step 'census' model validation failed: http.rest: the 'body' of step 'census' ", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(expected, diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("This is an authoring fault, not a fault in the service under test", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("defect in the provider", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("maximum depth 64 and 50000 nodes", diagnostic.Message, StringComparison.Ordinal);
        Assert.All(diagnostic.Message, ch => Assert.True(ch is >= ' ' and <= '~', $"non-ASCII U+{(int)ch:X4}"));

        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry);
        var failure = ProviderPipeline.Compile(ast, registry, "CensusRefusal").Failure;
        Assert.NotNull(failure);
        Assert.False(failure!.IsProviderOrEngineFault);
        Assert.Equal(diagnostic.Message, failure.Message);
        Assert.Empty(ScenarioRunner.CollectSecretReferenceFaults(ast));
    }

    // The depth bound cannot be reached through schema validation (the bridge refuses 50
    // levels), so the over-depth row binds a body built in code and drives the pipeline
    // directly. Both bounds: no census refusal, one authoring refusal naming the step and the
    // limit, never the body's own value, never a provider fault.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestBody_OverDepthOrNodeBudget_IsOneBoundedAuthoringRefusal(bool depth)
    {
        const string canary = "synthetic-sensitive-body-value";
        YamlNode body = new YamlScalarNode(canary);
        if (depth)
            for (var index = 0; index < 65; index++) body = new YamlSequenceNode(body);
        else body = new YamlSequenceNode(Enumerable.Range(0, 50_001).Select(_ => new YamlScalarNode(canary)));
        var node = CreateFixture("http.rest").Node;
        node.RawNode.Children[new YamlScalarNode("body")] = body;

        Assert.False(ScenarioRunner.TryValidateSecretReferences(Ast(node), out var diagnostic, out _), diagnostic);
        Assert.DoesNotContain(StepTemplateCensus.Collect(node).Fields, field => field.Location == "body");

        var failure = ProviderPipeline.Compile(Ast(node), Discover(), "CensusBounds").Failure;
        Assert.NotNull(failure);
        Assert.False(failure!.IsProviderOrEngineFault);
        Assert.Contains("the 'body' of step 'census'", failure.Message, StringComparison.Ordinal);
        Assert.Contains(depth ? "nests more than 64 levels deep" : "expands to more than 50000 JSON nodes", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("defect in the provider", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>An http.rest census node whose raw mapping is <paramref name="yaml"/> plus a fixed target, method and path.</summary>
    private static StepNode RestNode(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var raw = (YamlMappingNode)stream.Documents[0].RootNode;
        raw.Add("target", "fixture"); raw.Add("method", "POST"); raw.Add("path", "/");
        return CreateFixture("http.rest").Node with { RawNode = raw };
    }

    /// <summary>
    /// A whole one-step suite whose step 'census' ends with <paramref name="bodyYaml"/>
    /// (block lines at step indentation); its first body line is line 11.
    /// </summary>
    private static string RestSuite(string bodyYaml) =>
        "environment:\n  services:\n    fixture:\n      image: app:1.0\n"
        + "steps:\n  - id: census\n    type: http.rest\n    target: fixture\n    method: POST\n    path: /\n"
        + string.Join('\n', bodyYaml.Split('\n').Select(line => line.Length == 0 ? line : "    " + line));

    private static string ReferenceHash(string reference) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference))).ToLowerInvariant();

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
            root.Add("environment", new YamlMappingNode { { "services", new YamlMappingNode { { "fixture", new YamlMappingNode { { "image", "app:1.0" } } } } } });
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
