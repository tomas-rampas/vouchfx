// S02-C-02 regression — Fix 3: HttpRestProvider.Emit escapes string literals.
//
// Verifies that model values containing double-quotes, backslashes, or other
// characters that would break a naïve C# string literal are emitted safely via
// JsonSerializer.Serialize and survive the full compile→run→Vars round-trip.
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.HttpRest;
using Xunit;

namespace Platform.Engine.Compilation.Tests;

/// <summary>
/// S02-C-02 regression: <c>HttpRestProvider.Emit</c> must produce valid CSX
/// even when model values contain characters that would break a naïve C# string
/// literal (double-quote, backslash, newline).
/// </summary>
public sealed class HttpRestEmitTests
{
    // ── ICompileContext stub ───────────────────────────────────────────────────

    /// <summary>Minimal <see cref="ICompileContext"/> for emit tests.</summary>
    private sealed class StubCompileContext : ICompileContext
    {
        public StubCompileContext(string stepId) => StepId = stepId;
        public string StepId { get; }
        public string SuiteNamespace => "Generated";
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// When <see cref="HttpRestModel.Path"/> and <see cref="HttpRestModel.Target"/>
    /// contain a double-quote and a backslash, <see cref="HttpRestProvider.Emit"/>
    /// must produce a <see cref="CsxFragment"/> whose spliced CSX compiles and
    /// runs without error, and the path value must round-trip intact through
    /// <see cref="ScriptGlobalVariables.Vars"/>.
    /// </summary>
    [Fact]
    public async Task Emit_SpecialCharactersInPath_RoundTripsIntactThroughCompiledScript()
    {
        // ── Arrange ───────────────────────────────────────────────────────────

        // Values that would break a naïve `"{{model.Path}}"` interpolation:
        //   • double-quote terminates the string literal early
        //   • backslash introduces an invalid escape sequence
        const string dangerousPath = "/foo\"bar\\baz";
        const string dangerousTarget = "svc\"quote";

        var provider = new HttpRestProvider();
        var model = new HttpRestModel(
            Target: dangerousTarget,
            Method: "GET",
            Path: dangerousPath,
            Headers: null,
            Body: null,
            Expect: null);

        var ctx = new StubCompileContext("inject-step");
        var frag = provider.Emit(model, ctx);

        // Splice the fragment the same way the engine does at suite compile time:
        //   1. using directives
        //   2. helper class definitions (no 'using' keyword, no ';')
        //   3. statement block
        var usings = string.Join("\n", frag.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", frag.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{frag.StatementBlock}";

        // Guard: §13.3.1 invariant must still hold even with special chars.
        Assert.DoesNotContain("using var", csx, StringComparison.Ordinal);

        // ── Act ───────────────────────────────────────────────────────────────

        // CompileOnce must not throw — previously it would because the unescaped
        // double-quote in Path terminated the emitted string literal early.
        var compiled = RoslynScriptCompiler.CompileOnce(csx);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        // ── Assert ────────────────────────────────────────────────────────────

        // The sanitised step-id "inject-step" → "inject_step".
        const string expectedPathKey = "http_rest_inject_step_path";
        Assert.True(globals.Vars.ContainsKey(expectedPathKey),
            $"Expected Vars to contain key '{expectedPathKey}'. " +
            $"Actual keys: [{string.Join(", ", globals.Vars.Keys)}]");

        // The value must be the original, un-mangled string — not a truncated or
        // partially-escaped version of it.
        Assert.Equal(dangerousPath, globals.Vars[expectedPathKey]);

        // Target must also round-trip.
        const string expectedTargetKey = "http_rest_inject_step_target";
        Assert.True(globals.Vars.ContainsKey(expectedTargetKey),
            $"Expected Vars to contain key '{expectedTargetKey}'.");
        Assert.Equal(dangerousTarget, globals.Vars[expectedTargetKey]);
    }
}
