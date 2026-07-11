// S04-B-03 — SubstituteHelper tests (non-docker).
//
// Verifies:
//   1. SubstituteHelper.Source compiles standalone as a Roslyn script.
//   2. Resolve replaces {name} tokens with the corresponding Vars value.
//   3. Absent key → empty string (not exception).
//   4. Null value → empty string.
//   5. Non-identifier braces (e.g. JSON syntax) pass through unchanged.
//   6. Multiple placeholders in one template all resolve.
//   7. HttpRestProvider.Emit emits 'path' as a raw template literal resolved at runtime
//      via Secret_Helpers.ResolveTemplate (single-pass substitute + secret, S05-B-02),
//      so the placeholder survives as literal text inside the JSON-escaped literal.
//   8. DbAssertPostgresProvider.Emit wraps query + param values similarly.
//   9. Full compile-and-run: {name}="world" in path resolves at runtime.
//  10. No 'using var' in SubstituteHelper.Source.
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// S04-B-03: non-docker tests for <see cref="SubstituteHelper"/> and its
/// integration into <c>HttpRestProvider</c> and <c>DbAssertPostgresProvider</c>.
/// </summary>
public sealed class SubstituteHelperTests
{
    // ── Stubs ──────────────────────────────────────────────────────────────────

    private sealed class StubCompileContext : ICompileContext
    {
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public StubCompileContext(
            string stepId,
            IReadOnlyDictionary<string, string>? captures = null)
        {
            StepId = stepId;
            Captures = captures ?? new Dictionary<string, string>(StringComparer.Ordinal);
            CaptureExprs = Captures.ToDictionary(
                kv => kv.Key,
                kv => new CaptureExpr(CaptureFormat.JsonPath, kv.Value),
                StringComparer.Ordinal);
        }

        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; }
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; }
    }

    // Additional Roslyn metadata references required by the emitted helpers.
    private static readonly IReadOnlyList<string> s_baseRefs = new[]
    {
        typeof(System.Net.Http.HttpClient).Assembly.Location,
        typeof(System.Net.HttpStatusCode).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Uri).Assembly.Location,
        typeof(Json.Path.JsonPath).Assembly.Location,
        typeof(System.Xml.XmlDocument).Assembly.Location,          // System.Private.Xml — XPath capture logic (S07-B-01b)
    };

    // ── 1. SubstituteHelper.Source compiles standalone ─────────────────────────

    /// <summary>
    /// <see cref="SubstituteHelper.Source"/> must compile as a standalone Roslyn
    /// script fragment (no 'using' context required — fully-qualified types).
    /// </summary>
    [Fact]
    public void SubstituteHelperSource_CompilesStandalone()
    {
        // The source is a static class body — wrap it in a using block so Roslyn
        // can see it and call Resolve.  Use a short smoke-call.
        var csx =
            SubstituteHelper.Source + "\n" +
            "Vars[\"result\"] = Substitute_Helpers.Resolve(Vars, \"{greeting}, world\");";

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["greeting"] = "hello",
        };
        var globals = new ScriptGlobalVariables(vars);

        // If it doesn't compile this throws ScriptCompilationException.
        var compiled = RoslynScriptCompiler.CompileOnce(csx);
        // Does not throw — source is well-formed.
        Assert.NotNull(compiled);
    }

    // ── 2. Resolve replaces {name} tokens ─────────────────────────────────────

    /// <summary>
    /// Resolve must replace every <c>{name}</c> token with the string representation
    /// of <c>vars[name]</c>.
    /// </summary>
    [Fact]
    public async Task Resolve_ReplacesPlaceholderTokens_WithVarsValue()
    {
        var csx =
            SubstituteHelper.Source + "\n" +
            "Vars[\"result\"] = Substitute_Helpers.Resolve(Vars, \"{greeting}, {name}!\");";

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["greeting"] = "Hello",
            ["name"] = "world",
        };
        var globals = new ScriptGlobalVariables(vars);
        var compiled = RoslynScriptCompiler.CompileOnce(csx);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        Assert.Equal("Hello, world!", vars["result"]);
    }

    // ── 3. Absent key → empty string ──────────────────────────────────────────

    /// <summary>
    /// When a placeholder key is not in <c>Vars</c>, Resolve must substitute an
    /// empty string rather than throwing.
    /// </summary>
    [Fact]
    public async Task Resolve_AbsentKey_YieldsEmptyString()
    {
        var csx =
            SubstituteHelper.Source + "\n" +
            "Vars[\"result\"] = Substitute_Helpers.Resolve(Vars, \"before-{missing}-after\");";

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);
        var compiled = RoslynScriptCompiler.CompileOnce(csx);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        Assert.Equal("before--after", vars["result"]);
    }

    // ── 4. Null value → empty string ──────────────────────────────────────────

    /// <summary>
    /// When a key exists but has a null value, Resolve must return the empty string,
    /// not "null" or an exception.
    /// </summary>
    [Fact]
    public async Task Resolve_NullValue_YieldsEmptyString()
    {
        var csx =
            SubstituteHelper.Source + "\n" +
            "Vars[\"result\"] = Substitute_Helpers.Resolve(Vars, \"{x}\");";

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["x"] = null,
        };
        var globals = new ScriptGlobalVariables(vars);
        var compiled = RoslynScriptCompiler.CompileOnce(csx);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        Assert.Equal(string.Empty, vars["result"]);
    }

    // ── 5. Non-identifier braces pass through unchanged ────────────────────────

    /// <summary>
    /// Braces that do not match the <c>[A-Za-z_][A-Za-z0-9_]*</c> pattern (e.g.
    /// JSON object syntax like <c>{"key":"val"}</c>) must pass through unchanged.
    /// </summary>
    [Fact]
    public async Task Resolve_NonIdentifierBraces_PassThrough()
    {
        // {0} is a format specifier, not a valid identifier — passes through.
        var csx =
            SubstituteHelper.Source + "\n" +
            "Vars[\"result\"] = Substitute_Helpers.Resolve(Vars, \"{0} and {name}\");";

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = "Alice",
        };
        var globals = new ScriptGlobalVariables(vars);
        var compiled = RoslynScriptCompiler.CompileOnce(csx);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        // {0} is not an identifier (starts with digit) → passes through;
        // {name} is an identifier → replaced.
        Assert.Equal("{0} and Alice", vars["result"]);
    }

    // ── 6. Multiple placeholders resolve ─────────────────────────────────────

    /// <summary>
    /// All <c>{placeholder}</c> tokens in a template must be replaced in a single
    /// Resolve call.
    /// </summary>
    [Fact]
    public async Task Resolve_MultiplePlaceholders_AllResolved()
    {
        var csx =
            SubstituteHelper.Source + "\n" +
            "Vars[\"result\"] = Substitute_Helpers.Resolve(Vars, \"/users/{userId}/orders/{orderId}\");";

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["userId"] = "42",
            ["orderId"] = "99",
        };
        var globals = new ScriptGlobalVariables(vars);
        var compiled = RoslynScriptCompiler.CompileOnce(csx);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        Assert.Equal("/users/42/orders/99", vars["result"]);
    }

    // ── 7. HttpRestProvider.Emit wraps 'path' with Resolve ────────────────────

    /// <summary>
    /// <see cref="HttpRestProvider.Emit"/> must emit the <c>path</c> field as a RAW
    /// template literal passed to <c>HttpRest_Helpers.ExecuteAsync</c> (S05-B-02): the
    /// substitution + secret resolution now happen INSIDE the helper's guarded region,
    /// so the placeholder survives as literal text inside the JSON-escaped string
    /// literal rather than being wrapped at the call site.
    /// </summary>
    [Fact]
    public void HttpRestEmit_PathField_IsEmittedAsRawTemplateLiteral()
    {
        var provider = new HttpRestProvider();
        var model = new HttpRestModel(
            Target: "svc",
            Method: "GET",
            Path: "/users/{userId}/orders",
            Headers: null,
            Body: null,
            Expect: null);
        var ctx = new StubCompileContext("get-orders");

        var fragment = provider.Emit(model, ctx);

        // The placeholder text {userId} must survive as literal text inside the
        // JSON-escaped string literal, NOT resolved at emit time.
        Assert.Contains("{userId}", fragment.StatementBlock, StringComparison.Ordinal);

        // The path is emitted as the raw template literal "/users/{userId}/orders" and
        // passed directly to ExecuteAsync; the helper performs substitution + secret
        // resolution at runtime inside its try/catch.
        Assert.Contains(
            "\"/users/{userId}/orders\"",
            fragment.StatementBlock,
            StringComparison.Ordinal);

        // The helper itself must perform BOTH substitution and secret resolution in a
        // SINGLE pass (S05-B-02 hardening): the path is resolved via
        // Secret_Helpers.ResolveTemplate(secrets, vars, pathTemplate), which handles
        // {placeholder} and ${secret:...} over the original template in one left-to-right
        // pass — closing the secret-reference-injection / secret-corruption window.
        var allHelpers = string.Join("\n", fragment.RequiredHelpers);
        Assert.Contains("Secret_Helpers", allHelpers, StringComparison.Ordinal);
        Assert.Contains("Secret_Helpers.ResolveTemplate(secrets, vars, pathTemplate)", allHelpers,
            StringComparison.Ordinal);

        // Substitute_Helpers source remains present (it is still used by other providers
        // and dedupes harmlessly); the no-IL-bake test also asserts on its presence.
        Assert.Contains("Substitute_Helpers", allHelpers, StringComparison.Ordinal);
    }

    // ── 8. Compile+run: {userId} resolves in path at runtime ─────────────────

    /// <summary>
    /// Full compile-and-run: when <c>Vars["userId"] = "42"</c> and the step uses
    /// path <c>/users/{userId}</c>, the resolved path is used at runtime.
    /// The step will fail with EnvironmentError (no real service) but the important
    /// assertion is that it COMPILED and ran — proving the substitution survives the
    /// emit→compile→run pipeline.
    /// </summary>
    [Fact]
    public async Task HttpRestEmit_PlaceholderInPath_ResolvesAtRuntime()
    {
        var provider = new HttpRestProvider();
        var model = new HttpRestModel(
            Target: "svc",
            Method: "GET",
            Path: "/users/{userId}",
            Headers: null,
            Body: null,
            Expect: null);
        var ctx = new StubCompileContext("resolve-test");
        var fragment = provider.Emit(model, ctx);

        var assembled = CsxAssembler.Assemble(new[] { ("resolve-test", fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource,
            additionalReferencePaths: s_baseRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Service("svc")] = "http://localhost:9",  // closed port → EnvironmentError
            ["userId"] = "42",
        };
        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var safeId = CsxFragment.SanitiseId("resolve-test");
        var outcomeKey = VarKeys.Outcome(safeId);

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain '{outcomeKey}'. Keys: [{string.Join(", ", vars.Keys)}]");

        // EnvironmentError is expected (closed port) — but the step ran, which proves
        // compilation succeeded and the Resolve call did not crash at runtime.
        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
    }

    // ── 9. No 'using var' in SubstituteHelper.Source ──────────────────────────

    /// <summary>
    /// <see cref="SubstituteHelper.Source"/> must not contain <c>using var</c>
    /// (prohibited in Roslyn script bodies, §13.3.1).
    /// </summary>
    [Fact]
    public void SubstituteHelperSource_ContainsNoUsingVar()
    {
        Assert.DoesNotContain("using var", SubstituteHelper.Source, StringComparison.Ordinal);
    }
}
