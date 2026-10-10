using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class EnvironmentKeyValidationTests
{
    private static readonly StepKindRegistry s_registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(ScriptCsharpProvider).Assembly });
    private static readonly string s_schemaJson = SchemaComposer.ComposeSchemaJson(s_registry);
    private static readonly JsonSchema s_schema = JsonSchema.FromText(s_schemaJson);
    private const string Canary = "credential_canary_607";

    public static TheoryData<string, string> ReservedSuffixes => new()
    {
        { "elasticsearch", "discovery.type" }, { "elasticsearch", "xpack.security.enabled" },
        { "elasticsearch", "ES_JAVA_OPTS" }, { "elasticsearch", "cluster.routing.allocation.disk.threshold_enabled" },
        { "minio", "MINIO_ROOT_USER" }, { "minio", "MINIO_ROOT_PASSWORD" },
        { "s3", "RUSTFS_ACCESS_KEY" }, { "s3", "RUSTFS_SECRET_KEY" },
        { "azureservicebus", "ACCEPT_EULA" }, { "azureservicebus", "MSSQL_SA_PASSWORD" }, { "azureservicebus", "SQL_SERVER" },
        { "postgres", "UNRESERVED" }, { "image", "UNRESERVED" }, { "project", "UNRESERVED" },
    };

    [Theory]
    [MemberData(nameof(ReservedSuffixes))]
    public void ReservedSuffix_SyntheticJson_IsRefusedByActualSchema(string kind, string prefix)
        => AssertSafeJson(kind, prefix + "=" + Canary, JsonValue.Create("value_canary_607"));

    [Theory]
    [MemberData(nameof(ReservedSuffixes))]
    public void ReservedSuffix_NormalYamlValidator_IsRefused(string kind, string prefix)
        => AssertSafeYaml(Instance(kind, prefix + "=" + Canary, JsonValue.Create("value_canary_607"), "owner/~escaped"), kind);

    public static TheoryData<string, string, int> InvalidNames
    {
        get
        {
            var cases = new TheoryData<string, string, int>();
            foreach (var kind in new[] { "image", "postgres" })
                foreach (var key in new[] { "", "FOO\0", "FOO\n", "FOO\r\n", "BAD/name", "BAD~name", "FOO=" + Canary, "9FOO", ".FOO", "FÖO", "FOO-BAR" })
                    foreach (var valueKind in new[] { 0, 1, 2 }) cases.Add(kind, key, valueKind);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void InvalidName_AllSchemaErrorsUseSafeMapLocation(string kind, string key, int valueKind)
        => AssertSafeJson(kind, key, valueKind switch { 1 => new JsonArray("value_canary_607"), 2 => null, _ => JsonValue.Create("value_canary_607") }, "owner/~escaped");

    private static void AssertSafeJson(string kind, string key, JsonNode? value, string owner = "owner")
    {
        var instance = Instance(kind, key, value, owner);
        using var doc = JsonDocument.Parse(instance.ToJsonString());
        var result = s_schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.False(result.IsValid);
        using var schemaDoc = JsonDocument.Parse(s_schemaJson);
        var errors = SchemaErrorCollector.CollectErrors(result, doc.RootElement, schemaDoc.RootElement);
        AssertSafeErrors(errors.Select(e => (e.InstanceLocation, e.Message)).ToArray(), MapPath(kind, owner));
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void NormalYamlValidator_RefusesWithoutMalformedNameOrValueLeak(string kind, string key, int valueKind)
    {
        var instance = Instance(kind, key, valueKind switch { 1 => new JsonArray("value_canary_607"), 2 => null, _ => JsonValue.Create("value_canary_607") }, "owner/~escaped");
        AssertSafeYaml(instance, kind);
    }

    private static void AssertSafeYaml(JsonNode instance, string kind)
    {
        // JSON is a supported YAML spelling; control characters stay escaped through input parsing.
        var yaml = instance.ToJsonString();
        var composed = DocumentValidator.Validate(yaml, s_registry);
        Assert.False(composed.IsValid);
        AssertSafeErrors(composed.Errors.Select(e => (e.InstanceLocation, e.Message)).ToArray(), MapPath(kind, "owner/~escaped"));
        var rootOnly = YamlSchemaValidator.Validate(yaml.Replace(",\"code\":\"return;\"", "", StringComparison.Ordinal));
        Assert.False(rootOnly.IsValid);
        AssertSafeErrors(rootOnly.Errors.Select(e => (e.InstanceLocation, e.Message)).ToArray(), MapPath(kind, "owner/~escaped"));
    }

    [Theory]
    [InlineData("image")]
    [InlineData("postgres")]
    public void Parser_InvalidNameWithSequenceValue_DoesNotLeakName(string kind)
    {
        var yaml = Instance(kind, "FOO=" + Canary, new JsonArray("value_canary_607")).ToJsonString();
        var error = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("Environment variable names", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("value_canary_607", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("project")]
    [InlineData("postgres")]
    public void DottedCaseSensitiveName_IsPreserved(string kind)
    {
        var yaml = Instance(kind, "VOUCHFX_PROBE.key_1", JsonValue.Create("ExactCaseValue")).ToJsonString();
        Assert.True(DocumentValidator.Validate(yaml, s_registry).IsValid);
        var doc = YamlDocumentParser.Parse(yaml);
        var env = kind is "image" or "project" ? doc.Environment!.Services!["owner"].Env! : doc.Environment!.Dependencies!["owner"].Env!;
        Assert.Equal("ExactCaseValue", env["VOUCHFX_PROBE.key_1"]);
        Assert.Single(env);
    }

    [Fact]
    public void Collector_DeduplicatesBadNamesWithoutChangingValidNameValueErrors()
    {
        var instance = Instance("image", "FOO=" + Canary, new JsonArray("value_canary_607"));
        var names = instance["environment"]!["services"]!["owner"]!["env"]!.AsObject();
        names["BAD/name"] = null;
        names["VALID.key"] = new JsonArray("valid_value_canary_607");
        using var doc = JsonDocument.Parse(instance.ToJsonString());
        var evaluated = s_schema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.False(evaluated.IsValid);
        using var schemaDoc = JsonDocument.Parse(s_schemaJson);
        var errors = SchemaErrorCollector.CollectErrors(evaluated, doc.RootElement, schemaDoc.RootElement);
        Assert.Equal(2, errors.Count);
        Assert.Single(errors, e => e.InstanceLocation == "/environment/services/owner/env"
            && e.Message.Contains("Environment variable names", StringComparison.Ordinal));
        Assert.Single(errors, e => e.InstanceLocation == "/environment/services/owner/env/VALID.key"
            && e.Message.Contains("[type]", StringComparison.Ordinal));
        Assert.All(errors, e => Assert.DoesNotContain(Canary, e.InstanceLocation + e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void Conversion_OnlyRecognizedRootEnvironmentNamesAreInspected()
    {
        const string yaml = """
            variables:
              arbitrary:
                env: { 'FOO=credential_canary_607': value }
            environment:
              services:
                app:
                  image: app
                  env: &env { VOUCHFX_PROBE.key_1: ExactCaseValue }
                sibling: { image: app, env: *env }
            steps: [{ id: probe, type: script.csharp, code: 'return;' }]
            """;
        using var doc = SchemaResources.ConvertYamlToJsonDocument(yaml);
        Assert.Equal("value", doc.RootElement.GetProperty("variables").GetProperty("arbitrary").GetProperty("env").GetProperty("FOO=credential_canary_607").GetString());
        Assert.Equal("ExactCaseValue", doc.RootElement.GetProperty("environment").GetProperty("services").GetProperty("sibling").GetProperty("env").GetProperty("VOUCHFX_PROBE.key_1").GetString());
    }

    private static void AssertSafeErrors((string Path, string Message)[] errors, string path)
    {
        var error = Assert.Single(errors);
        Assert.Equal(path, error.Path);
        Assert.Contains("Environment variable names", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bookkeeping", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Canary, error.Path + error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("value_canary_607", error.Path + error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\0', error.Path + error.Message);
        Assert.DoesNotContain('\r', error.Path + error.Message);
        Assert.DoesNotContain('\n', error.Path + error.Message);
    }

    private static string MapPath(string kind, string owner) => "/environment/" + (kind is "image" or "project" ? "services/" : "dependencies/") + owner.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal) + "/env";

    private static JsonObject Instance(string kind, string key, JsonNode? value, string owner = "owner")
    {
        var entity = new JsonObject { [kind is "image" or "project" ? kind : "type"] = kind is "image" ? "app" : kind is "project" ? "./app.csproj" : kind, ["env"] = new JsonObject { [key] = value } };
        return new JsonObject { ["environment"] = new JsonObject { [kind is "image" or "project" ? "services" : "dependencies"] = new JsonObject { [owner] = entity } }, ["steps"] = new JsonArray(new JsonObject { ["id"] = "probe", ["type"] = "script.csharp", ["code"] = "return;" }) };
    }
}
