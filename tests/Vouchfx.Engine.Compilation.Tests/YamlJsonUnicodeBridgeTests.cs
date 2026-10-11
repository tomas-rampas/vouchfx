using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.Http.Soap;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.MetricsAssert.Prometheus;
using Xunit;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class YamlJsonUnicodeBridgeTests
{
    private static readonly char[] s_escapePrefix = { '\\' };
    private static readonly StepKindRegistry s_registry = StepKindRegistry.BuildAndFreeze(new[]
    {
        typeof(HttpRestProvider).Assembly,
        typeof(HttpSoapProvider).Assembly,
        typeof(MetricsAssertPrometheusProvider).Assembly,
    });

    [Theory]
    [InlineData("http.rest", "/a\u2028b")]
    [InlineData("http.rest", "/a\u2029b")]
    [InlineData("http.soap", "/a\u2028b")]
    [InlineData("http.soap", "/a\u2029b")]
    [InlineData("metrics-assert.prometheus", "/a\u2028b")]
    [InlineData("metrics-assert.prometheus", "/a\u2029b")]
    public void DocumentValidator_ValidUnicodePath_ReachesComposedSchema(string kind, string path)
    {
        var yaml = Document(kind, path);
        using var json = SchemaResources.ConvertYamlToJsonDocument(yaml);
        Assert.Equal(path, json.RootElement.GetProperty("steps")[0].GetProperty("path").GetString());
        var result = DocumentValidator.Validate(yaml, s_registry);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    public static IEnumerable<object[]> StringValues()
    {
        // JSON escapes in the YAML input are valid YAML double-quoted escapes.
        // The output must contain the same scalar, regardless of YAML's own emitter escapes.
        for (var codePoint = 0; codePoint <= 0x9f; codePoint++)
            yield return new object[] { $"U+{codePoint:X4}", ((char)codePoint).ToString() };
        foreach (var value in new[]
        {
            "\u00a0", "\u2028", "\u2029", "Příliš žluťoučký kůň", "العربية", "😀🚀",
            "quote\"slash\\tail", "\\L", "\\P", "\\N", "\\_", "\\x7f", "\\u2028",
            "\\\\L", "\\\\P", "\\\"\\L", "\\\\\"\\P", "prefix\u2028\\L\"\u2029tail",
        })
            yield return new object[] { "text:" + JsonSerializer.Serialize(value), value };
    }

    [Theory]
    [MemberData(nameof(StringValues))]
    public void Bridge_QuotedString_RoundTripsWithoutYamlOnlyEscapes(string label, string value)
    {
        Assert.NotEmpty(label);
        using var json = SchemaResources.ConvertYamlToJsonDocument("value: " + JsonSerializer.Serialize(value));
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("value").ValueKind);
        Assert.Equal(value, json.RootElement.GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    [InlineData("😀\\L\"\nPříliš")]
    public void DocumentValidator_UnicodeMetadataAndVariables_ArePreserved(string value)
    {
        var yaml = "metadata:\n  description: " + JsonSerializer.Serialize(value)
            + "\nvariables:\n  text: " + JsonSerializer.Serialize(value)
            + "\nsteps:\n  - id: request\n    type: http.rest\n    target: api\n    method: GET\n    path: /\n";
        using var json = SchemaResources.ConvertYamlToJsonDocument(yaml);
        Assert.Equal(value, json.RootElement.GetProperty("metadata").GetProperty("description").GetString());
        Assert.Equal(value, json.RootElement.GetProperty("variables").GetProperty("text").GetString());
        var result = DocumentValidator.Validate(yaml, s_registry);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void Bridge_QuotedMappingKeysAndUnicodeAliases_PreserveShapeAndValues()
    {
        const string yaml = """
            "a\\b": &shared
              "quote\"slash\\key": "value\u2029end"
            alias: *shared
            sequence: ["\u2028", "\u2029", true, null, 0123, 1.25]
            """;
        using var json = SchemaResources.ConvertYamlToJsonDocument(yaml);
        var root = json.RootElement;
        Assert.Equal("value\u2029end", root.GetProperty("a\\b").GetProperty("quote\"slash\\key").GetString());
        Assert.Equal(root.GetProperty("a\\b").GetRawText(), root.GetProperty("alias").GetRawText());
        var sequence = root.GetProperty("sequence");
        Assert.Equal(6, sequence.GetArrayLength());
        Assert.Equal("\u2028", sequence[0].GetString());
        Assert.Equal("\u2029", sequence[1].GetString());
        Assert.True(sequence[2].GetBoolean());
        Assert.Equal(JsonValueKind.Null, sequence[3].ValueKind);
        Assert.Equal(123, sequence[4].GetInt64());
        Assert.Equal(1.25, sequence[5].GetDouble());
    }

    [Theory]
    [InlineData("steps: [", "")]
    [InlineData("steps: false", "/steps")]
    [InlineData("steps:\n  - id: request\n    type: http.rest\n    target: api\n    method: GET\n    path: true", "/steps/0/path")]
    [InlineData("steps:\n  - id: request\n    type: unknown.provider", "/steps/0/type")]
    public void DocumentValidator_InvalidSyntaxShapeAndTypes_RemainRefused(string yaml, string location)
    {
        var result = DocumentValidator.Validate(yaml, s_registry);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.InstanceLocation == location);
    }

    [Fact]
    public void AuthoringParser_DuplicateKeys_RemainRefused()
    {
        const string yaml = "steps:\n  - id: request\n    type: http.rest\n    target: api\n    method: GET\n    path: /first\n    path: /second\n";
        Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
    }

    [Theory]
    [InlineData("\u2028", "object")]
    [InlineData("\u2028", "nested")]
    [InlineData("\u2028", "array")]
    [InlineData("\u2029", "object")]
    [InlineData("\u2029", "nested")]
    [InlineData("\u2029", "array")]
    public void DocumentValidator_StructuredBodyWithSeparatorKey_IsValid(string separator, string shape)
    {
        var result = DocumentValidator.Validate(BodyDocument("a" + separator + "b", shape), s_registry);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    public static IEnumerable<object[]> BodyKeyCases()
    {
        foreach (var key in new[] { "normal", "kůň😀", "a\u2028b", "a\u2029b" })
            foreach (var shape in new[] { "object", "nested", "array" })
                yield return new object[] { key, shape };
    }

    [Theory]
    [MemberData(nameof(BodyKeyCases))]
    public void BodyKeys_ComposedJsonAndPublicProvider_AgreeWithDocumentValidation(string key, string shape)
    {
        var yaml = BodyDocument(key, shape);
        var expectedBody = BodyValue(key, shape);
        var expected = new JsonObject
        {
            ["environment"] = new JsonObject
            {
                ["services"] = new JsonObject { ["api"] = new JsonObject { ["image"] = "example/app:1.0" } },
            },
            ["steps"] = new JsonArray(new JsonObject
            {
                ["id"] = "request",
                ["type"] = "http.rest",
                ["target"] = "api",
                ["method"] = "POST",
                ["path"] = "/",
                ["body"] = expectedBody.DeepClone(),
            }),
        };
        using var expectedJson = JsonDocument.Parse(expected.ToJsonString());
        var schema = SchemaComposer.ComposeSchema(s_registry);
        Assert.True(schema.Evaluate(expectedJson.RootElement).IsValid);

        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        var steps = (YamlSequenceNode)root.Children[new YamlScalarNode("steps")];
        var provider = new HttpRestProvider();
        var bound = provider.Bind(steps.Children[0], new BindingContext());
        Assert.True(provider.Validate(bound, new ProjectContext()).IsValid);
        Assert.True(JsonNode.DeepEquals(expectedBody, JsonNode.Parse(bound.Body!)));

        var validation = DocumentValidator.Validate(yaml, s_registry);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(e => e.Message)));
        using var bridged = SchemaResources.ConvertYamlToJsonDocument(yaml);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(bridged.RootElement.GetRawText())));
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("0123", "123")]
    [InlineData("1.25", "1.25")]
    [InlineData("\"true\"", "\"true\"")]
    [InlineData("\"null\"", "\"null\"")]
    [InlineData("[true, false, null, 0123, \"0123\"]", "[true,false,null,123,\"0123\"]")]
    [InlineData("{value: null, empty: {}, items: []}", "{\"value\":null,\"empty\":{},\"items\":[]}")]
    public void Bridge_RootScalarAndCollectionShapes_PreserveTypes(string yaml, string expectedJson)
    {
        using var actual = SchemaResources.ConvertYamlToJsonDocument(yaml);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedJson), JsonNode.Parse(actual.RootElement.GetRawText())));
    }

    [Theory]
    [InlineData("{1: value}")]
    [InlineData("{true: value}")]
    [InlineData("{[a, b]: value}")]
    public void Bridge_NonStringAndStructuredKeys_RetainExistingRefusal(string yaml)
    {
        Assert.ThrowsAny<JsonException>(() => SchemaResources.ConvertYamlToJsonDocument(yaml));
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    [InlineData("\\uD800x")]
    public void Bridge_UnpairedSurrogate_IsRefused(string escape)
    {
        Assert.ThrowsAny<Exception>(() => SchemaResources.ConvertYamlToJsonDocument($"value: \"{escape}\""));
    }

    [Fact]
    public void Bridge_ValidSurrogatePair_IsPreserved()
    {
        using var actual = SchemaResources.ConvertYamlToJsonDocument("value: \"\\uD83D\\uDE00\"");
        Assert.Equal("😀", actual.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public void ConversionWriter_ExpandedEscape_ExactlyFitsTheOutputBudget()
    {
        using var writer = CreateConversionWriter();
        writer.Write('"');
        writer.Write(new string('a', SchemaResources.MaxJsonChars - 8));
        writer.Write("\\L");
        writer.Write('"');
        var emitted = writer.ToString()!;
        Assert.Equal(SchemaResources.MaxJsonChars, emitted.Length);
        Assert.EndsWith("\\u2028\"", emitted, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionWriter_ExpandedEscape_IsRefusedBeforeExceedingTheBudget()
    {
        using var writer = CreateConversionWriter();
        writer.Write('"');
        writer.Write(new string('a', SchemaResources.MaxJsonChars - 6));
        writer.Write(s_escapePrefix, 0, s_escapePrefix.Length);
        var error = Assert.Throws<InvalidOperationException>(() => writer.Write('P'));
        Assert.Contains("of JSON during schema conversion", error.Message, StringComparison.Ordinal);
        Assert.True(writer.ToString()!.Length <= SchemaResources.MaxJsonChars);
    }

    [Theory]
    [InlineData("missing-value")]
    [InlineData("unclosed-array")]
    [InlineData("wrong-end")]
    [InlineData("second-root")]
    public void JsonTokenEmitter_IncompleteTokenStructures_AreRefused(string scenario)
    {
        using var writer = CreateConversionWriter();
        var emitter = CreateTokenEmitter(writer);
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart());
        switch (scenario)
        {
            case "missing-value":
                emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, MappingStyle.Flow));
                emitter.Emit(StringScalar("key"));
                Assert.Throws<JsonException>(() => emitter.Emit(new MappingEnd()));
                break;
            case "unclosed-array":
                emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Flow));
                Assert.Throws<JsonException>(() => emitter.Emit(new DocumentEnd(true)));
                break;
            case "wrong-end":
                emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Flow));
                Assert.Throws<JsonException>(() => emitter.Emit(new MappingEnd()));
                break;
            default:
                emitter.Emit(StringScalar("first"));
                Assert.Throws<JsonException>(() => emitter.Emit(StringScalar("second")));
                break;
        }
    }

    [Theory]
    [InlineData(0xD800, false)]
    [InlineData(0xDC00, false)]
    [InlineData(0xD800, true)]
    public void JsonTokenEmitter_UnpairedSurrogate_IsRefused(int codeUnit, bool trailingCharacter)
    {
        // xUnit serializes theory strings as UTF-8; construct invalid UTF-16 in the test.
        var value = ((char)codeUnit).ToString() + (trailingCharacter ? "x" : "");
        using var writer = CreateConversionWriter();
        var emitter = CreateTokenEmitter(writer);
        Assert.Throws<JsonException>(() => emitter.Emit(StringScalar(value)));
    }

    [Fact]
    public void JsonTokenEmitter_ExpandedString_ExactlyFitsFinalCharacterBudget()
    {
        using var writer = CreateConversionWriter();
        var emitter = CreateTokenEmitter(writer);
        emitter.Emit(StringScalar(new string('a', SchemaResources.MaxJsonChars - 8) + "\u2028"));
        Assert.Equal(SchemaResources.MaxJsonChars, writer.ToString()!.Length);
        Assert.EndsWith("\\u2028\"", writer.ToString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonTokenEmitter_ExpandedString_IsRefusedBeforeExceedingBudget()
    {
        using var writer = CreateConversionWriter();
        var emitter = CreateTokenEmitter(writer);
        var value = new string('a', SchemaResources.MaxJsonChars - 6) + "\u2029";
        Assert.Throws<InvalidOperationException>(() => emitter.Emit(StringScalar(value)));
        Assert.True(writer.ToString()!.Length <= SchemaResources.MaxJsonChars);
    }

    private static Scalar StringScalar(string value) =>
        new(AnchorName.Empty, TagName.Empty, value, ScalarStyle.DoubleQuoted, true, false);

    private static IEmitter CreateTokenEmitter(TextWriter writer)
    {
        var type = typeof(SchemaResources).GetNestedType("JsonTokenEmitter",
            System.Reflection.BindingFlags.NonPublic)!;
        return (IEmitter)Activator.CreateInstance(type, writer)!;
    }

    private static TextWriter CreateConversionWriter()
    {
        // Exercise the conversion's actual writer and budget without asking YAML to
        // allocate a 16 Mi-character input or widening the writer's visibility.
        var type = typeof(SchemaResources).GetNestedType("BudgetedJsonWriter",
            System.Reflection.BindingFlags.NonPublic)!;
        return (TextWriter)Activator.CreateInstance(type, nonPublic: true)!;
    }

    private static string BodyDocument(string key, string shape)
    {
        var body = shape switch
        {
            "object" => $"    body:\n      {JsonSerializer.Serialize(key)}: value\n",
            "nested" => $"    body:\n      outer:\n        {JsonSerializer.Serialize(key)}: value\n",
            _ => $"    body:\n      - {JsonSerializer.Serialize(key)}: value\n",
        };
        return "environment:\n  services:\n    api:\n      image: example/app:1.0\nsteps:\n"
            + "  - id: request\n    type: http.rest\n    target: api\n    method: POST\n    path: /\n" + body;
    }

    private static JsonNode BodyValue(string key, string shape)
    {
        var value = new JsonObject { [key] = "value" };
        return shape switch
        {
            "object" => value,
            "nested" => new JsonObject { ["outer"] = value },
            _ => new JsonArray(value),
        };
    }

    private sealed class BindingContext : IBindingContext { }

    private sealed class ProjectContext : IProjectContext
    {
        private static readonly string[] s_httpEndpoints = { "http" };
        public string SuiteDirectory => Directory.GetCurrentDirectory();
        public IReadOnlyDictionary<string, string> DeclaredDependencies { get; } = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices { get; } =
            new Dictionary<string, DeclaredServiceInfo> { ["api"] = new(s_httpEndpoints) };
    }

    private static string Document(string kind, string path)
    {
        var fields = kind switch
        {
            "http.rest" => "method: GET",
            "http.soap" => "envelope: '<a/>'",
            _ => "metric: orders_total\n    expect: { min: 0 }",
        };
        return "environment:\n  services:\n    api:\n      image: example/app:1.0\nsteps:\n"
            + $"  - id: request\n    type: {kind}\n    target: api\n    path: {JsonSerializer.Serialize(path)}\n"
            + $"    {fields}\n";
    }
}
