using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Json.Schema;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.Http.Soap;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.MetricsAssert.Prometheus;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>Raw HTTP path controls must agree at the provider and composed document gates.</summary>
public sealed class HttpPathControlParityTests
{
    private static readonly string[] s_kinds = { "http.rest", "http.soap", "metrics-assert.prometheus" };
    private static readonly StepKindRegistry s_registry = StepKindRegistry.BuildAndFreeze(new[]
    {
        typeof(HttpRestProvider).Assembly,
        typeof(HttpSoapProvider).Assembly,
        typeof(MetricsAssertPrometheusProvider).Assembly,
    });
    private static readonly IProjectContext s_context = new ProjectContext();
    private static readonly JsonSchema s_schema = SchemaComposer.ComposeSchema(s_registry);
    private static readonly string s_schemaJson = SchemaComposer.ComposeSchemaJson(s_registry);
    private static readonly EvaluationOptions s_options = new() { OutputFormat = OutputFormat.List };

    /// <summary>Every C0, DEL and C1 code point, inside and at the end of each provider's path.</summary>
    public static IEnumerable<object[]> RawControlCases()
    {
        foreach (var kind in s_kinds)
        {
            foreach (var codePoint in Enumerable.Range(0, 32).Concat(Enumerable.Range(0x7f, 33)))
            {
                yield return new object[] { kind, codePoint, false };
                yield return new object[] { kind, codePoint, true };
            }
        }
    }

    /// <summary>Valid boundaries, encoded control-like text and existing query/fragment/path policy.</summary>
    public static IEnumerable<object[]> ValidPathCases()
    {
        foreach (var kind in s_kinds)
        {
            foreach (var path in new[]
            {
                "/", "/orders/{id}?q={value}#fragment", "/nested//segments",
                "/a%0D%0Ab", "/a%7Fb", "/a%85b", "/a%00b", "/a%1Fb", "/a%9Fb",
                "/plain path", "/a~b", "/a\u00a0b", "/a\u2028b", "/a\u2029b", "/a#fragment?query",
            })
            {
                yield return new object[] { kind, path };
            }
        }
    }

    /// <summary>Existing invalid-category diagnostics retain precedence when a control is also present.</summary>
    public static IEnumerable<object[]> ExistingRefusalCases()
    {
        foreach (var kind in s_kinds)
        {
            yield return new object[] { kind, "relative\n", "rooted relative path" };
            yield return new object[] { kind, "//other.example/path\n", "rooted relative path" };
            yield return new object[] { kind, "/a\\b\n", "backslashes" };
            yield return new object[] { kind, "\n", "must not be empty" };
        }
    }

    [Theory]
    [MemberData(nameof(RawControlCases))]
    public void ProviderValidate_RefusesEveryRawControlWithoutEchoingPath(
        string kind, int codePoint, bool atEnd)
    {
        Assert.True(char.IsControl((char)codePoint));
        var result = ValidateProvider(kind, ControlPath(codePoint, atEnd));

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("'path'", error, StringComparison.Ordinal);
        Assert.Contains("raw control characters", error, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTHOR_PATH_CANARY", error, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RawControlCases))]
    public void ComposedJsonSchema_RefusesEveryRawControlAtPathPattern(
        string kind, int codePoint, bool atEnd)
    {
        var result = ValidateComposedSchema(kind, ControlPath(codePoint, atEnd));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.InstanceLocation == "/steps/0/path"
            && error.Message.Contains("[pattern]", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ValidPathCases))]
    public void ProviderAndComposedSchema_PreserveExistingValidPaths(string kind, string path)
    {
        var provider = ValidateProvider(kind, path);
        var schema = ValidateComposedSchema(kind, path);

        Assert.True(provider.IsValid, string.Join("; ", provider.Errors));
        Assert.True(schema.IsValid, string.Join("; ", schema.Errors.Select(error => error.Message)));
    }

    [Theory]
    [MemberData(nameof(ExistingRefusalCases))]
    public void ProviderAndComposedSchema_PreserveExistingRefusalPrecedence(
        string kind, string path, string message)
    {
        var provider = ValidateProvider(kind, path);
        var schema = DocumentValidator.Validate(Document(kind, path), s_registry);

        Assert.False(provider.IsValid);
        Assert.Contains(message, Assert.Single(provider.Errors), StringComparison.Ordinal);
        Assert.DoesNotContain("raw control characters", string.Join("; ", provider.Errors), StringComparison.Ordinal);
        Assert.False(schema.IsValid);
    }

    /// <summary>CR and LF also reach the production YAML document door without bridge ambiguity.</summary>
    public static IEnumerable<object[]> YamlControlCases()
    {
        foreach (var kind in s_kinds)
        {
            foreach (var codePoint in new[] { 0x0a, 0x0d })
            {
                yield return new object[] { kind, codePoint, false };
                yield return new object[] { kind, codePoint, true };
            }
        }
    }

    [Theory]
    [MemberData(nameof(YamlControlCases))]
    public void YamlDocument_RefusesRepresentativeRawControlsAtPathPattern(
        string kind, int codePoint, bool atEnd)
    {
        var result = DocumentValidator.Validate(Document(kind, ControlPath(codePoint, atEnd)), s_registry);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.InstanceLocation == "/steps/0/path"
            && error.Message.Contains("[pattern]", StringComparison.Ordinal));
    }

    private static string ControlPath(int codePoint, bool atEnd) =>
        "/AUTHOR_PATH_CANARY" + (char)codePoint + (atEnd ? string.Empty : "/tail");

    private static ValidationResult ValidateProvider(string kind, string path) => kind switch
    {
        "http.rest" => new HttpRestProvider().Validate(
            new HttpRestModel("api", "GET", path, null, null, null), s_context),
        "http.soap" => new HttpSoapProvider().Validate(
            new HttpSoapModel("api", path, null, "<a/>", null), s_context),
        _ => new MetricsAssertPrometheusProvider().Validate(
            new MetricsAssertPrometheusModel("api", path, "orders_total", null,
                new MetricsExpectation(null, "0", null)), s_context),
    };

    private static SchemaValidationResult ValidateComposedSchema(string kind, string path)
    {
        var step = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["id"] = "request",
            ["type"] = kind,
            ["target"] = "api",
            ["path"] = path,
        };
        switch (kind)
        {
            case "http.rest":
                step["method"] = "GET";
                break;
            case "http.soap":
                step["envelope"] = "<a/>";
                break;
            default:
                step["metric"] = "orders_total";
                step["expect"] = new { min = 0 };
                break;
        }

        // Exercise the actual composed schema on JSON so every Unicode boundary reaches
        // its pattern independently of YAML-to-JSON conversion.
        using var instance = JsonDocument.Parse(JsonSerializer.Serialize(new { steps = new[] { step } }));
        var results = s_schema.Evaluate(instance.RootElement, s_options);
        if (results.IsValid)
            return SchemaValidationResult.Valid;

        using var schema = JsonDocument.Parse(s_schemaJson);
        return new SchemaValidationResult(false,
            SchemaErrorCollector.CollectErrors(results, instance.RootElement, schema.RootElement));
    }

    private static string Document(string kind, string path)
    {
        var fields = kind switch
        {
            "http.rest" => "method: GET",
            "http.soap" => "envelope: '<a/>'",
            _ => "metric: orders_total\n    expect: { min: 0 }",
        };
        // CR/LF JSON escapes are valid YAML double-quoted escapes for this document-door cohort.
        return "environment:\n  services:\n    api:\n      image: example/app:1.0\nsteps:\n"
            + $"  - id: request\n    type: {kind}\n    target: api\n    path: {JsonSerializer.Serialize(path)}\n"
            + $"    {fields}\n";
    }

    private sealed class ProjectContext : IProjectContext
    {
        private static readonly string[] s_httpEndpoints = { "http" };
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();
        public IReadOnlyDictionary<string, string> DeclaredDependencies { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices { get; } =
            new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal)
            {
                ["api"] = new DeclaredServiceInfo(s_httpEndpoints),
            };
    }
}
