// S03-F-01 update of S02-C-02 regression — HttpRestProvider.Emit special-character safety.
//
// Verifies that model values containing double-quotes, backslashes, or other
// characters that would break a naïve C# string literal are emitted safely via
// JsonSerializer.Serialize and survive the full compile→run→Vars round-trip.
//
// Sprint 3 change: the emitter now calls HttpRest_Helpers.ExecuteAsync, which
// writes a StepOutcome under VarKeys.Outcome(sanitisedId).  For a path containing
// special characters the target endpoint will not exist, so the outcome is
// Verdict.EnvironmentError — the test asserts compilation succeeds and the outcome
// key is populated.
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.HttpRest;
using Xunit;

namespace Platform.Engine.Compilation.Tests;

/// <summary>
/// S02-C-02 regression (updated for Sprint 3): <c>HttpRestProvider.Emit</c> must
/// produce valid CSX even when model values contain characters that would break a
/// naïve C# string literal (double-quote, backslash, newline).
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
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// When <see cref="HttpRestModel.Path"/> and <see cref="HttpRestModel.Target"/>
    /// contain a double-quote and a backslash, <see cref="HttpRestProvider.Emit"/>
    /// must produce a <see cref="CsxFragment"/> whose spliced CSX compiles and runs
    /// without error.  Because the target service does not exist the outcome is
    /// <see cref="Verdict.EnvironmentError"/>, which confirms:
    /// <list type="bullet">
    ///   <item>The special-character values were escaped correctly (no compile error).</item>
    ///   <item>The outcome key is written to <c>Vars</c> (the step executed).</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Emit_SpecialCharactersInPath_CompilesAndRecordsEnvironmentError()
    {
        // ── Arrange ───────────────────────────────────────────────────────────

        // Values that would break a naïve `"{{model.Path}}"` interpolation:
        //   • double-quote terminates the string literal early
        //   • backslash introduces an invalid escape sequence
        const string dangerousPath = "/foo\"bar\\baz";
        const string dangerousTarget = "svc\"quote";
        const string stepId = "inject-step";

        var provider = new HttpRestProvider();
        var model = new HttpRestModel(
            Target: dangerousTarget,
            Method: "GET",
            Path: dangerousPath,
            Headers: null,
            Body: null,
            Expect: null);

        var ctx = new StubCompileContext(stepId);
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
        // The emitted helper references System.Net.Http and System.Text.Json
        // which are not in the default TPA subset, so supply them explicitly.
        var additionalRefs = new[]
        {
            typeof(System.Net.Http.HttpClient).Assembly.Location,
            typeof(System.Net.HttpStatusCode).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.Json.Nodes.JsonNode).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
            typeof(System.Uri).Assembly.Location,                  // System.Private.Uri (safe URI composition, M1)
            typeof(Json.Path.JsonPath).Assembly.Location,          // JsonPath.Net — capture logic (S04-B-02)
        };
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        // The target service key does not exist in Vars, so ExecuteAsync will
        // attempt to connect to "" + dangerousPath, which fails at the network
        // layer — that is correctly an EnvironmentError, not a test failure.
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        // ── Assert ────────────────────────────────────────────────────────────

        // The sanitised step-id "inject-step" → "inject_step".
        var safeId = CsxFragment.SanitiseId(stepId);
        var expectedOutcomeKey = VarKeys.Outcome(safeId);

        Assert.True(globals.Vars.ContainsKey(expectedOutcomeKey),
            $"Expected Vars to contain outcome key '{expectedOutcomeKey}'. " +
            $"Actual keys: [{string.Join(", ", globals.Vars.Keys)}]");

        var outcome = Assert.IsType<StepOutcome>(globals.Vars[expectedOutcomeKey]);

        // The step failed to connect (no service seeded in Vars) → EnvironmentError.
        // This simultaneously proves:
        //   1. The special chars were escaped correctly (no ScriptCompilationException).
        //   2. The emitted block executed and wrote a StepOutcome (not a raw exception).
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0,
            "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);
    }
}
