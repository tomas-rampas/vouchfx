// Tests for MetricsAssertPrometheusProvider — bind/validate/schema/registry.
//
// Covers:
//   1.  Bind: full YAML step (target/path/metric/labels/expect) → correct model.
//   2.  Bind: non-mapping node → safe empty model (defensive; path defaults to /metrics).
//   3.  Bind: path absent → defaults to "/metrics".
//   4.  Bind: labels absent → null.
//   5.  Validate: valid model (expect.value only) → IsValid.
//   6.  Validate: valid model (expect.min + expect.max only) → IsValid.
//   7.  Validate: empty target → invalid.
//   8.  Validate: empty metric → invalid.
//   9.  Validate: expect with no member → invalid.
//   10. Validate: min > max (both static literals) → invalid.
//   11. Validate: min > max but one side is a placeholder → skipped (still valid).
//   12. Registry: provider discoverable via StepKindRegistry with key "metrics-assert.prometheus".
//   13. Registry: SchemaFragment contains "metric".
//   14. Resources: yields an http ResourceRequirement whose Name equals model.Target.
//   15. CompileReferenceAssemblies: contains HttpClient + JsonPath.Net assemblies.
//   16. Validate: 'path' SSRF guard (mirrors HttpRestProvider.Validate's rooted-path
//       checks exactly) — an absolute URL, a protocol-relative path, and a backslash
//       path are all rejected; a rooted relative path is valid.
//
// All tests are non-docker.  No topology and no HTTP server are started.
using Vouchfx.Sdk;
using Vouchfx.Steps.MetricsAssert.Prometheus;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MetricsAssert.Prometheus.Tests;

// ── Stub contexts ─────────────────────────────────────────────────────────────

file sealed class StubProjectContext : IProjectContext
{
    /// <inheritdoc />
    public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

    internal StubProjectContext(
        IReadOnlyDictionary<string, string>? deps = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? services = null)
    {
        DeclaredDependencies = deps
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        DeclaredServices = services
            ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, string> DeclaredDependencies { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DeclaredServices { get; }
}

internal sealed class StubBindingContext : IBindingContext { }

// ── Test class ────────────────────────────────────────────────────────────────

public sealed class MetricsAssertPrometheusProviderTests
{
    private readonly MetricsAssertPrometheusProvider _provider = new();
    private static readonly StubBindingContext s_bindCtx = new();

    /// <summary>
    /// A <see cref="StubProjectContext"/> declaring service <c>"sut"</c> — the target every
    /// Validate test in this file uses (REQ-012: target reconciliation needs it declared as
    /// a service or dependency to pass; these tests are about metric/path/expect shape, not
    /// target reconciliation).
    /// </summary>
    private static readonly IProjectContext s_sutDeclaredContext = new StubProjectContext(
        services: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["sut"] = new List<string> { "http" },
        });

    // ── 1. Bind: full YAML step ────────────────────────────────────────────────

    [Fact]
    public void Bind_FullYamlMapping_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("sut") },
            { "path", new YamlScalarNode("/actuator/metrics") },
            { "metric", new YamlScalarNode("orders_total") },
            {
                "labels", new YamlMappingNode
                {
                    { "status", new YamlScalarNode("ok") },
                }
            },
            {
                "expect", new YamlMappingNode
                {
                    { "value", new YamlScalarNode("1") },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("sut", model.Target);
        Assert.Equal("/actuator/metrics", model.Path);
        Assert.Equal("orders_total", model.Metric);
        Assert.NotNull(model.Labels);
        Assert.Equal("ok", model.Labels!["status"]);
        Assert.Equal("1", model.Expect.Value);
    }

    // ── 2. Bind: non-mapping node ──────────────────────────────────────────────

    [Fact]
    public void Bind_NonMappingNode_ReturnsSafeEmptyModel()
    {
        var model = _provider.Bind(new YamlScalarNode("not-a-mapping"), s_bindCtx);

        Assert.Equal(string.Empty, model.Target);
        Assert.Equal("/metrics", model.Path);
        Assert.Equal(string.Empty, model.Metric);
        Assert.Null(model.Labels);
        Assert.Null(model.Expect.Value);
        Assert.Null(model.Expect.Min);
        Assert.Null(model.Expect.Max);
    }

    // ── 3. Bind: path absent → defaults to /metrics ───────────────────────────

    [Fact]
    public void Bind_PathAbsent_DefaultsToMetrics()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("sut") },
            { "metric", new YamlScalarNode("orders_total") },
            { "expect", new YamlMappingNode { { "value", new YamlScalarNode("1") } } },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("/metrics", model.Path);
    }

    // ── 4. Bind: labels absent → null ─────────────────────────────────────────

    [Fact]
    public void Bind_LabelsAbsent_IsNull()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("sut") },
            { "metric", new YamlScalarNode("orders_total") },
            { "expect", new YamlMappingNode { { "value", new YamlScalarNode("1") } } },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.Labels);
    }

    // ── 5. Validate: valid model (value only) ─────────────────────────────────

    [Fact]
    public void Validate_ValidModel_ValueOnly_IsValid()
    {
        var model = new MetricsAssertPrometheusModel(
            "sut", "/metrics", "orders_total", null,
            new MetricsExpectation(Value: "1", Min: null, Max: null));

        var result = _provider.Validate(model, s_sutDeclaredContext);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 6. Validate: valid model (min + max only) ─────────────────────────────

    [Fact]
    public void Validate_ValidModel_MinMaxOnly_IsValid()
    {
        var model = new MetricsAssertPrometheusModel(
            "sut", "/metrics", "orders_total", null,
            new MetricsExpectation(Value: null, Min: "1", Max: "10"));

        var result = _provider.Validate(model, s_sutDeclaredContext);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 7. Validate: empty target ─────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyTarget_IsInvalid()
    {
        var model = new MetricsAssertPrometheusModel(
            "", "/metrics", "orders_total", null,
            new MetricsExpectation(Value: "1", Min: null, Max: null));

        var result = _provider.Validate(model, s_sutDeclaredContext);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("target"));
    }

    // ── 8. Validate: empty metric ─────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyMetric_IsInvalid()
    {
        var model = new MetricsAssertPrometheusModel(
            "sut", "/metrics", "", null,
            new MetricsExpectation(Value: "1", Min: null, Max: null));

        var result = _provider.Validate(model, s_sutDeclaredContext);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("metric"));
    }

    // ── 9. Validate: expect with no member ────────────────────────────────────

    [Fact]
    public void Validate_ExpectWithNoMember_IsInvalid()
    {
        var model = new MetricsAssertPrometheusModel(
            "sut", "/metrics", "orders_total", null,
            new MetricsExpectation(Value: null, Min: null, Max: null));

        var result = _provider.Validate(model, s_sutDeclaredContext);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("expect"));
    }

    // ── 10. Validate: min > max (static literals) ─────────────────────────────

    [Fact]
    public void Validate_MinGreaterThanMax_StaticLiterals_IsInvalid()
    {
        var model = new MetricsAssertPrometheusModel(
            "sut", "/metrics", "orders_total", null,
            new MetricsExpectation(Value: null, Min: "10", Max: "1"));

        var result = _provider.Validate(model, s_sutDeclaredContext);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("min") && e.Contains("max"));
    }

    // ── 11. Validate: min > max but one side is a placeholder → skipped ──────

    [Fact]
    public void Validate_MinGreaterThanMax_WithPlaceholder_IsSkippedAndValid()
    {
        var model = new MetricsAssertPrometheusModel(
            "sut", "/metrics", "orders_total", null,
            new MetricsExpectation(Value: null, Min: "{expectedMin}", Max: "1"));

        var result = _provider.Validate(model, s_sutDeclaredContext);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── Target reconciliation (services-generalisation spec, REQ-012/EDGE-009) ──

    /// <summary>
    /// REQ-012/EDGE-009: a <c>target</c> naming neither a declared service nor a declared
    /// dependency fails validation, naming the unknown target and listing what IS declared.
    /// </summary>
    [Fact]
    public void Validate_TargetNamesNeitherServiceNorDependency_IsInvalid_ListsDeclaredSurfaces()
    {
        var model = new MetricsAssertPrometheusModel(
            "does-not-exist", "/metrics", "orders_total", null,
            new MetricsExpectation(Value: "1", Min: null, Max: null));

        var result = _provider.Validate(model, s_sutDeclaredContext);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("does-not-exist", StringComparison.Ordinal) &&
            e.Contains("sut", StringComparison.Ordinal));
    }

    /// <summary>
    /// M1 fix (fix round 2, PR #349 follow-up): a <c>target</c> naming a declared
    /// DEPENDENCY (never a service) must be rejected — mirrors HttpRestProvider's own
    /// identical fix and rationale. The message must name the dependency and list the
    /// services it CAN reach.
    /// </summary>
    [Fact]
    public void Validate_TargetNamesDeclaredDependency_IsInvalid_NamesDependencyAndListsServices()
    {
        var ctx = new StubProjectContext(
            deps: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["orders-db"] = "postgres",
            },
            services: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["sut"] = new List<string> { "http" },
            });
        var model = new MetricsAssertPrometheusModel(
            "orders-db", "/metrics", "orders_total", null,
            new MetricsExpectation(Value: "1", Min: null, Max: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("orders-db", StringComparison.Ordinal) &&
            e.Contains("dependency", StringComparison.OrdinalIgnoreCase) &&
            e.Contains("sut", StringComparison.Ordinal));
    }

    // ── 12. Registry: provider discoverable ──────────────────────────────────

    [Fact]
    public void Registry_ProviderIsDiscoverable_ByKey()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(MetricsAssertPrometheusProvider).Assembly });

        Assert.True(
            registry.TryGet("metrics-assert.prometheus", out var provider) && provider is not null,
            "Provider for 'metrics-assert.prometheus' must be discoverable via StepKindRegistry.");
    }

    // ── 13. Registry: SchemaFragment contains 'metric' ───────────────────────

    [Fact]
    public void SchemaFragment_ContainsMetric()
    {
        Assert.Contains("\"metric\"", _provider.SchemaFragment.Json, StringComparison.Ordinal);
    }

    // ── 14. Resources: yields http ResourceRequirement ───────────────────────

    [Fact]
    public void Resources_YieldsHttpRequirementWithCorrectName()
    {
        var model = new MetricsAssertPrometheusModel(
            "sut", "/metrics", "orders_total", null,
            new MetricsExpectation(Value: "1", Min: null, Max: null));
        var resources = _provider.Resources(model).ToList();

        Assert.Single(resources);
        Assert.Equal("http", resources[0].Family);
        Assert.Equal("sut", resources[0].Name);
    }

    // ── 15. CompileReferenceAssemblies: contains HttpClient + JsonPath.Net ───

    [Fact]
    public void CompileReferenceAssemblies_ContainsHttpClientAndJsonPathNetAssemblies()
    {
        var refs = _provider.CompileReferenceAssemblies.ToList();

        Assert.Contains(refs, a => a.GetName().Name == "System.Net.Http");
        Assert.Contains(refs, a => a.GetName().Name == "JsonPath.Net");
    }

    // ── 16. Validate: 'path' SSRF guard (mirrors HttpRestProvider.Validate) ──

    [Fact]
    public void Validate_PathIsAbsoluteUrl_IsInvalid()
    {
        var model = new MetricsAssertPrometheusModel(
            "sut", "http://evil.example/metrics", "orders_total", null,
            new MetricsExpectation(Value: "1", Min: null, Max: null));

        var result = _provider.Validate(model, s_sutDeclaredContext);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("rooted relative path"));
    }

    [Fact]
    public void Validate_PathIsBareRelative_IsInvalid()
    {
        var model = new MetricsAssertPrometheusModel(
            "sut", "metrics", "orders_total", null,
            new MetricsExpectation(Value: "1", Min: null, Max: null));

        var result = _provider.Validate(model, s_sutDeclaredContext);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("rooted relative path"));
    }

    [Fact]
    public void Validate_PathIsProtocolRelative_IsInvalid()
    {
        var model = new MetricsAssertPrometheusModel(
            "sut", "//evil.example/metrics", "orders_total", null,
            new MetricsExpectation(Value: "1", Min: null, Max: null));

        var result = _provider.Validate(model, s_sutDeclaredContext);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("rooted relative path"));
    }

    [Fact]
    public void Validate_PathContainsBackslash_IsInvalid()
    {
        var model = new MetricsAssertPrometheusModel(
            "sut", "/metrics\\..\\admin", "orders_total", null,
            new MetricsExpectation(Value: "1", Min: null, Max: null));

        var result = _provider.Validate(model, s_sutDeclaredContext);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("backslash"));
    }

    [Fact]
    public void Validate_PathIsRootedRelative_IsValid()
    {
        var model = new MetricsAssertPrometheusModel(
            "sut", "/metrics", "orders_total", null,
            new MetricsExpectation(Value: "1", Min: null, Max: null));

        var result = _provider.Validate(model, s_sutDeclaredContext);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }
}
