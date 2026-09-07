// S02-C-02 — Schema composition and http.rest provider tests (§8.2, §13.6).
//
// Test strategy:
//   1.  ComposedSchema_ValidatesValidHttpRestStep
//       Registry with HttpRestProvider → valid http.rest step → IsValid.
//
//   2.  ComposedSchema_RejectsHttpRestStepWithBadMethod
//       Registry with HttpRestProvider → step with method: TELEPORT → not valid
//       (enum constraint from provider fragment fires).
//
//   3.  ComposedSchema_FragmentOriginatesFromProvider
//       EMPTY registry → same bad-method step → REJECTED, because 'target'/
//       'method'/'path' aren't even recognised as step properties at all (no
//       fragment to mark them evaluated — $defs/step's unevaluatedProperties,
//       the typo-closing change) — proving http.rest's entire vocabulary,
//       not merely its method enum, came from the provider, not a central file.
//
//   4.  HttpRestProvider_IsDiscoverableAndExposesSchemaFragment
//       Assembly-level discovery finds http.rest; SchemaFragment is non-null and
//       contains the method enum.
//
//   5.  HttpRestProvider_Emit_ObeysCsxFragmentRules
//       StatementBlock starts/ends with brace, no 'using var', helper prefixed
//       HttpRest_, sanitised step id.
//
//   6.  HttpRestProvider_Bind_RoundTripsYamlMapping
//       Bind parses a full YAML mapping into the expected HttpRestModel fields.
//
//   7.  HttpRestProvider_Validate_RejectsBadMethod
//       Validate returns Failure when method is not an allowed HTTP verb.
using System.Linq;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// S02-C-02: schema-composition mechanism and <c>http.rest</c> provider tests.
/// </summary>
public sealed class SchemaCompositionTests
{
    // ── Schema composition tests ───────────────────────────────────────────────

    /// <summary>
    /// A registry containing the <c>http.rest</c> provider must produce a
    /// composed schema that accepts a structurally valid <c>http.rest</c> step.
    /// </summary>
    [Fact]
    public void ComposedSchema_ValidatesValidHttpRestStep()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(HttpRestProvider).Assembly });

        const string yaml = """
            steps:
              - id: call-api
                type: http.rest
                target: orders-api
                method: GET
                path: /health
            """;

        var result = SchemaComposer.Validate(registry, yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {FormatErrors(result)}");
        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// When the <c>http.rest</c> provider is registered, the composed schema must
    /// reject a step with an invalid HTTP verb (<c>TELEPORT</c>), because the
    /// provider's fragment constrains <c>method</c> to a closed enum.
    /// </summary>
    [Fact]
    public void ComposedSchema_RejectsHttpRestStepWithBadMethod()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(HttpRestProvider).Assembly });

        const string yaml = """
            steps:
              - id: bad-step
                type: http.rest
                target: orders-api
                method: TELEPORT
                path: /warp
            """;

        var result = SchemaComposer.Validate(registry, yaml);

        Assert.False(result.IsValid,
            "Expected validation to fail when method is TELEPORT (not in enum).");
        Assert.NotEmpty(result.Errors);
    }

    /// <summary>
    /// When the registry is EMPTY (no providers registered), the same bad-method
    /// step must be REJECTED — not because of the bad method value (no provider
    /// contributed that enum constraint), but because <c>target</c>/<c>method</c>/
    /// <c>path</c> are not even recognised as step properties AT ALL. With no
    /// provider fragment, the composer injects no <c>allOf</c> clause at all
    /// (<c>SchemaComposer.BuildIfThenClauses</c> returns empty), so
    /// $defs/step's own <c>unevaluatedProperties: false</c> (the typo-closing
    /// change) has nothing marking those three properties as evaluated. This
    /// proves the SAME underlying fact the original (additionalProperties-era)
    /// version of this test proved — that http.rest's vocabulary and
    /// constraints originate entirely from the provider fragment, never from
    /// any central, hand-maintained file — now via a stronger signal: the
    /// WHOLE vocabulary disappears with the provider, not merely its method
    /// enum.
    /// </summary>
    [Fact]
    public void ComposedSchema_FragmentOriginatesFromProvider()
    {
        // Empty registry — no provider fragments at all.
        var emptyRegistry = StepKindRegistry.BuildAndFreeze(
            System.Array.Empty<IStepProvider>());

        const string yaml = """
            steps:
              - id: bad-step
                type: http.rest
                target: orders-api
                method: TELEPORT
                path: /warp
            """;

        var result = SchemaComposer.Validate(emptyRegistry, yaml);

        Assert.False(result.IsValid,
            "Expected invalid: with no http.rest fragment registered, 'target'/'method'/'path' " +
            "are unrecognised (unevaluated) step properties, not merely unconstrained ones.");

        // Every one of the three provider-specific fields must be named as
        // unevaluated — not just one — confirming the WHOLE vocabulary is
        // provider-sourced, not only the method enum.
        foreach (var property in new[] { "target", "method", "path" })
        {
            Assert.Contains(result.Errors, e =>
                e.InstanceLocation == $"/steps/0/{property}" &&
                e.Message.Contains(property, System.StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// <see cref="StepKindRegistry.BuildAndFreeze(System.Collections.Generic.IEnumerable{System.Reflection.Assembly})"/>
    /// must discover the <c>http.rest</c> provider from its assembly and the
    /// resulting <see cref="RegisteredProvider.SchemaFragment"/> must be non-null
    /// and contain the <c>method</c> enum.
    /// </summary>
    [Fact]
    public void HttpRestProvider_IsDiscoverableAndExposesSchemaFragment()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(HttpRestProvider).Assembly });

        var found = registry.TryGet("http.rest", out var registered);

        Assert.True(found, "Expected http.rest to be discoverable.");
        Assert.NotNull(registered);
        Assert.NotNull(registered!.SchemaFragment);

        // The fragment must contain the method enum so the IDE / validator
        // can constrain the HTTP verb before any container starts.
        var fragmentJson = registered.SchemaFragment!.Json;
        Assert.Contains("\"enum\"", fragmentJson, System.StringComparison.Ordinal);
        Assert.Contains("\"GET\"", fragmentJson, System.StringComparison.Ordinal);
        Assert.Contains("\"POST\"", fragmentJson, System.StringComparison.Ordinal);
    }

    // ── Provider unit tests ────────────────────────────────────────────────────

    /// <summary>
    /// The <see cref="CsxFragment"/> emitted by <c>HttpRestProvider</c> must
    /// satisfy all CsxFragment composition rules (§13.3.1):
    /// <list type="bullet">
    ///   <item>StatementBlock starts with <c>{</c> and ends with <c>}</c>.</item>
    ///   <item>StatementBlock does not contain <c>using var</c>.</item>
    ///   <item>RequiredHelpers contains a class named <c>HttpRest_Helpers</c>.</item>
    ///   <item>The step id is sanitised (hyphens → underscores).</item>
    /// </list>
    /// </summary>
    [Fact]
    public void HttpRestProvider_Emit_ObeysCsxFragmentRules()
    {
        var provider = new HttpRestProvider();
        var model = new HttpRestModel(
            Target: "orders-api",
            Method: "POST",
            Path: "/users",
            Headers: null,
            Body: null,
            Expect: null);

        var ctx = new TestCompileContext("my-step-id");
        var fragment = provider.Emit(model, ctx);

        // StatementBlock must be exactly one brace-enclosed block.
        var block = fragment.StatementBlock.Trim();
        Assert.True(block.StartsWith('{'),
            "StatementBlock must start with '{'.");
        Assert.True(block.EndsWith('}'),
            "StatementBlock must end with '}'.");

        // 'using var' is illegal in a Roslyn script body (§13.3.1).
        Assert.DoesNotContain("using var", fragment.StatementBlock, System.StringComparison.Ordinal);

        // RequiredHelpers must contain the provider-id-prefixed class definition.
        Assert.True(
            fragment.RequiredHelpers.Any(h => h.Contains("HttpRest_Helpers", System.StringComparison.Ordinal)),
            "RequiredHelpers must contain the 'HttpRest_Helpers' class definition.");

        // RequiredUsings must be bare namespace strings (no 'using' keyword, no ';').
        foreach (var ns in fragment.RequiredUsings)
        {
            Assert.DoesNotContain("using ", ns, System.StringComparison.Ordinal);
            Assert.DoesNotContain(";", ns, System.StringComparison.Ordinal);
        }

        // The step id must appear sanitised (hyphen → underscore) in the block.
        var sanitised = CsxFragment.SanitiseId("my-step-id"); // "my_step_id"
        Assert.Contains(sanitised, fragment.StatementBlock, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="HttpRestProvider.Bind"/> must round-trip a full YAML mapping
    /// into the expected <see cref="HttpRestModel"/> fields.
    /// </summary>
    [Fact]
    public void HttpRestProvider_Bind_RoundTripsYamlMapping()
    {
        var provider = new HttpRestProvider();
        var ctx = new TestBindingContext();

        // Build a YamlMappingNode that mirrors a real .e2e.yaml step body.
        var mapping = new YamlMappingNode
        {
            { new YamlScalarNode("target"), new YamlScalarNode("orders-api") },
            { new YamlScalarNode("method"), new YamlScalarNode("POST") },
            { new YamlScalarNode("path"),   new YamlScalarNode("/users") },
            {
                new YamlScalarNode("headers"),
                new YamlMappingNode
                {
                    { new YamlScalarNode("Authorization"), new YamlScalarNode("Bearer token123") },
                }
            },
            {
                new YamlScalarNode("expect"),
                new YamlMappingNode
                {
                    { new YamlScalarNode("status"), new YamlScalarNode("201") },
                }
            },
        };

        var model = provider.Bind(mapping, ctx);

        Assert.Equal("orders-api", model.Target);
        Assert.Equal("POST", model.Method);
        Assert.Equal("/users", model.Path);
        Assert.NotNull(model.Headers);
        Assert.True(model.Headers!.ContainsKey("Authorization"));
        Assert.Equal("Bearer token123", model.Headers["Authorization"]);
        Assert.NotNull(model.Expect);
        Assert.Equal(201, model.Expect!.Status);
    }

    /// <summary>
    /// <see cref="HttpRestProvider.Validate"/> must return a failed
    /// <see cref="ValidationResult"/> when the method is not a recognised
    /// HTTP verb.
    /// </summary>
    [Fact]
    public void HttpRestProvider_Validate_RejectsBadMethod()
    {
        var provider = new HttpRestProvider();
        var ctx = new TestProjectContext();

        var model = new HttpRestModel(
            Target: "orders-api",
            Method: "TELEPORT",
            Path: "/warp",
            Headers: null,
            Body: null,
            Expect: null);

        var result = provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.True(
            result.Errors.Any(e => e.Contains("method", System.StringComparison.OrdinalIgnoreCase)),
            "At least one error must mention 'method'.");
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static string FormatErrors(SchemaValidationResult result) =>
        string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"));

    /// <summary>Minimal test implementation of <see cref="ICompileContext"/>.</summary>
    private sealed class TestCompileContext : ICompileContext
    {
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public TestCompileContext(string stepId) => StepId = stepId;
        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    /// <summary>Minimal test implementation of <see cref="IBindingContext"/>.</summary>
    private sealed class TestBindingContext : IBindingContext { }

    /// <summary>Minimal test implementation of <see cref="IProjectContext"/>.</summary>
    private sealed class TestProjectContext : IProjectContext
    {
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        /// <inheritdoc />
        public IReadOnlyDictionary<string, string> DeclaredDependencies { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <inheritdoc />
        public IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices { get; } =
            new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal);
    }
}
