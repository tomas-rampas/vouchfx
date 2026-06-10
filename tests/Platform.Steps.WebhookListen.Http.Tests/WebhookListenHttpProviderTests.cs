// Tests for WebhookListenHttpProvider — bind / validate / host-resource / schema / registry.
//
// Covers (all non-docker; no topology, no real listener):
//   1. Bind: full YAML step (listener + match with method/path/headers/bodyContains) → model.
//   2. Bind: non-mapping node → safe empty-stringed model with an all-null match (defensive).
//   3. Bind: each single criterion binds (method-only, path-only, headers-only, body-only).
//   4. Validate: valid model (listener + ≥1 criterion) → IsValid.
//   5. Validate: empty listener → invalid with clear message.
//   6. Validate: empty match (no criteria) → invalid with clear message.
//   7. Validate: present-but-empty headers map alone → invalid (imposes no constraint).
//   8. IHostResourceContributor: HostResources yields ONE ("webhook-listener", listener).
//   9. Registry: provider discoverable at key "webhook-listen.http".
//  10. Registry: SchemaFragment references "listener" and "match".
//
// No dependency reconciliation is expected — the listener is a host resource the provider
// itself contributes, not an environment.dependencies entry.
using Platform.Sdk;
using Platform.Steps.WebhookListen.Http;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Platform.Steps.WebhookListen.Http.Tests;

// ── Stub context implementations ─────────────────────────────────────────────

/// <summary>
/// Stub <see cref="IProjectContext"/> exposing an (unused) empty declared-dependency
/// map — webhook-listen.http performs no dependency reconciliation.
/// </summary>
file sealed class StubProjectContext : IProjectContext
{
    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> DeclaredDependencies { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Stub <see cref="IBindingContext"/> for tests that do not require binding-stage services.
/// </summary>
internal sealed class StubBindingContext : IBindingContext { }

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Non-docker unit tests for <see cref="WebhookListenHttpProvider"/>
/// (bind, validate, host-resource contribution, schema, registry discoverability).
/// </summary>
public sealed class WebhookListenHttpProviderTests
{
    private readonly WebhookListenHttpProvider _provider = new();
    private static readonly StubBindingContext s_bindCtx = new();

    // ── 1. Bind: full YAML step ────────────────────────────────────────────────

    [Fact]
    public void Bind_FullYamlMapping_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "listener", new YamlScalarNode("orders-callback") },
            {
                "match", new YamlMappingNode
                {
                    { "method",       new YamlScalarNode("POST") },
                    { "path",         new YamlScalarNode("/callbacks/order") },
                    { "bodyContains", new YamlScalarNode("\"status\":\"NEW\"") },
                    {
                        "headers", new YamlMappingNode
                        {
                            { "content-type",  new YamlScalarNode("application/json") },
                            { "x-correlation", new YamlScalarNode("abc-123") },
                        }
                    },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("orders-callback", model.Listener);
        Assert.Equal("POST", model.Match.Method);
        Assert.Equal("/callbacks/order", model.Match.Path);
        Assert.Equal("\"status\":\"NEW\"", model.Match.BodyContains);

        Assert.NotNull(model.Match.Headers);
        Assert.Equal(2, model.Match.Headers!.Count);
        Assert.Equal("application/json", model.Match.Headers["content-type"]);
        Assert.Equal("abc-123", model.Match.Headers["x-correlation"]);
    }

    // ── 2. Bind: non-mapping node ──────────────────────────────────────────────

    [Fact]
    public void Bind_NonMappingNode_ReturnsEmptyModel()
    {
        var model = _provider.Bind(new YamlScalarNode("bad"), s_bindCtx);

        Assert.Equal(string.Empty, model.Listener);
        Assert.Null(model.Match.Method);
        Assert.Null(model.Match.Path);
        Assert.Null(model.Match.Headers);
        Assert.Null(model.Match.BodyContains);
    }

    // ── 3. Bind: each single criterion binds ───────────────────────────────────

    [Fact]
    public void Bind_MethodOnly_OtherCriteriaNull()
    {
        var model = _provider.Bind(MakeYaml(method: "GET"), s_bindCtx);

        Assert.Equal("GET", model.Match.Method);
        Assert.Null(model.Match.Path);
        Assert.Null(model.Match.Headers);
        Assert.Null(model.Match.BodyContains);
    }

    [Fact]
    public void Bind_PathOnly_OtherCriteriaNull()
    {
        var model = _provider.Bind(MakeYaml(path: "/ping"), s_bindCtx);

        Assert.Equal("/ping", model.Match.Path);
        Assert.Null(model.Match.Method);
        Assert.Null(model.Match.Headers);
        Assert.Null(model.Match.BodyContains);
    }

    [Fact]
    public void Bind_BodyContainsOnly_OtherCriteriaNull()
    {
        var model = _provider.Bind(MakeYaml(bodyContains: "hello"), s_bindCtx);

        Assert.Equal("hello", model.Match.BodyContains);
        Assert.Null(model.Match.Method);
        Assert.Null(model.Match.Path);
        Assert.Null(model.Match.Headers);
    }

    [Fact]
    public void Bind_HeadersOnly_OtherCriteriaNull()
    {
        var yaml = new YamlMappingNode
        {
            { "listener", new YamlScalarNode("cb") },
            {
                "match", new YamlMappingNode
                {
                    {
                        "headers", new YamlMappingNode
                        {
                            { "x-token", new YamlScalarNode("v") },
                        }
                    },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.NotNull(model.Match.Headers);
        Assert.Equal("v", model.Match.Headers!["x-token"]);
        Assert.Null(model.Match.Method);
        Assert.Null(model.Match.Path);
        Assert.Null(model.Match.BodyContains);
    }

    // ── 4. Validate: valid model ───────────────────────────────────────────────

    [Fact]
    public void Validate_ValidModel_IsValid()
    {
        var ctx = new StubProjectContext();
        var model = new WebhookListenHttpModel(
            Listener: "orders-callback",
            Match: new WebhookMatch(Method: "POST", Path: null, Headers: null, BodyContains: null));

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
    }

    // ── 5. Validate: empty listener ────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyListener_IsInvalid()
    {
        var ctx = new StubProjectContext();
        var model = new WebhookListenHttpModel(
            Listener: string.Empty,
            Match: new WebhookMatch(Method: "POST", Path: null, Headers: null, BodyContains: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'listener'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 6. Validate: empty match (no criteria) ─────────────────────────────────

    [Fact]
    public void Validate_EmptyMatch_IsInvalid()
    {
        var ctx = new StubProjectContext();
        var model = new WebhookListenHttpModel(
            Listener: "cb",
            Match: new WebhookMatch(Method: null, Path: null, Headers: null, BodyContains: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'match'", StringComparison.Ordinal) &&
            e.Contains("at least one criterion", StringComparison.Ordinal));
    }

    // ── 7. Validate: present-but-empty headers map alone ───────────────────────

    [Fact]
    public void Validate_MatchWithEmptyHeadersMapOnly_IsInvalid()
    {
        var ctx = new StubProjectContext();
        var model = new WebhookListenHttpModel(
            Listener: "cb",
            Match: new WebhookMatch(
                Method: null,
                Path: null,
                Headers: new Dictionary<string, string>(StringComparer.Ordinal),
                BodyContains: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("at least one criterion", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_HeadersOnly_NonEmpty_IsValid()
    {
        var ctx = new StubProjectContext();
        var model = new WebhookListenHttpModel(
            Listener: "cb",
            Match: new WebhookMatch(
                Method: null,
                Path: null,
                Headers: new Dictionary<string, string>(StringComparer.Ordinal) { ["x"] = "1" },
                BodyContains: null));

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 8. IHostResourceContributor ────────────────────────────────────────────

    [Fact]
    public void HostResources_YieldsSingleWebhookListenerRequirement()
    {
        var model = new WebhookListenHttpModel(
            Listener: "orders-callback",
            Match: new WebhookMatch(Method: "POST", Path: null, Headers: null, BodyContains: null));

        var requirements = _provider.HostResources(model).ToList();

        Assert.Single(requirements);
        Assert.Equal("webhook-listener", requirements[0].Kind, StringComparer.Ordinal);
        Assert.Equal("orders-callback", requirements[0].VarName, StringComparer.Ordinal);
    }

    // ── 9. Registry: provider discoverable ─────────────────────────────────────

    [Fact]
    public void Provider_IsDiscoverableViaStepKindRegistry()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(WebhookListenHttpProvider).Assembly });

        var found = registry.TryGet("webhook-listen.http", out var registered);

        Assert.True(found, "Expected 'webhook-listen.http' to be registered.");
        Assert.NotNull(registered);
        Assert.Equal("webhook-listen", registered!.Kind.Family);
        Assert.Equal("http", registered.Kind.Provider);
        Assert.IsType<WebhookListenHttpProvider>(registered.Instance);
    }

    // ── 10. Registry: SchemaFragment references listener + match ────────────────

    [Fact]
    public void Provider_SchemaFragment_ContainsListenerAndMatch()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(WebhookListenHttpProvider).Assembly });

        registry.TryGet("webhook-listen.http", out var registered);

        Assert.NotNull(registered!.SchemaFragment);
        Assert.Contains("listener", registered.SchemaFragment!.Json, StringComparison.Ordinal);
        Assert.Contains("match", registered.SchemaFragment.Json, StringComparison.Ordinal);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static YamlMappingNode MakeYaml(
        string? method = null,
        string? path = null,
        string? bodyContains = null)
    {
        var match = new YamlMappingNode();
        if (method is not null) match.Add("method", new YamlScalarNode(method));
        if (path is not null) match.Add("path", new YamlScalarNode(path));
        if (bodyContains is not null) match.Add("bodyContains", new YamlScalarNode(bodyContains));

        return new YamlMappingNode
        {
            { "listener", new YamlScalarNode("cb") },
            { "match", match },
        };
    }
}
