// Tests for S04-F-03: ScriptCsharpProvider — emit lint, compile/run round-trip,
// validator, and brace-injection safety.
//
// All tests are non-docker.  They exercise:
//   1. Emit lint: StatementBlock is one brace-balanced block; contains the author
//      code verbatim; no 'using var'; locals carry the sanitised id; outcome key
//      is JSON-escaped.
//   2. Author code with hyphened step id: engine locals use sanitised id.
//   3. Author code with braces survives: proves StringBuilder splice, not $$""" hole.
//   4. Compile + run round-trip: Pass verdict, value written to Vars.
//   5. Compile + run round-trip: throw → Fail verdict with correct observation,
//      outcome still written (proves try/finally).
//   6. Validator: empty/whitespace Code rejected.
//   7. Brace-injection attempt: unbalanced author body → compile error (not silent clobber).
//   8. Registry: provider discoverable via StepKindRegistry with key "script.csharp".
//   9. SchemaFragment: contains "code" field.
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.Script.Csharp;
using Xunit;

namespace Platform.Steps.Script.Csharp.Tests;

// ── Stubs ─────────────────────────────────────────────────────────────────────

/// <summary>Minimal <see cref="ICompileContext"/> for emit tests.</summary>
file sealed class StubCompileContext : ICompileContext
{
    internal StubCompileContext(string stepId) => StepId = stepId;

    /// <inheritdoc />
    public string StepId { get; }

    /// <inheritdoc />
    public string SuiteNamespace => "Generated";
}

/// <summary>Minimal <see cref="IProjectContext"/> for validator tests.</summary>
file sealed class StubProjectContext : IProjectContext
{
    public IReadOnlyDictionary<string, string> DeclaredDependencies { get; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Non-docker unit and integration tests for <see cref="ScriptCsharpProvider"/>.
/// </summary>
public sealed class ScriptCsharpProviderTests
{
    private readonly ScriptCsharpProvider _provider = new();
    private static readonly IProjectContext s_projectCtx = new StubProjectContext();

    // ── 1. Emit lint ─────────────────────────────────────────────────────────

    /// <summary>
    /// The emitted <see cref="CsxFragment.StatementBlock"/> must begin with '{'
    /// and end with '}' (§13.3.1 brace rule).
    /// </summary>
    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var model = new ScriptCsharpModel(Code: "Vars[\"x\"] = 1;");
        var ctx = new StubCompileContext("my-step");

        var fragment = _provider.Emit(model, ctx);
        var block = fragment.StatementBlock.Trim();

        Assert.True(block.StartsWith('{'),
            $"StatementBlock must start with '{{'; actual start: '{block[..Math.Min(20, block.Length)]}'");
        Assert.True(block.EndsWith('}'),
            $"StatementBlock must end with '}}'; actual end: '{block[Math.Max(0, block.Length - 20)..]}'");
    }

    /// <summary>
    /// The author code must appear verbatim as a substring of the
    /// <see cref="CsxFragment.StatementBlock"/>.
    /// </summary>
    [Fact]
    public void Emit_StatementBlock_ContainsAuthorCodeVerbatim()
    {
        const string authorCode = "Vars[\"greeting\"] = \"hello\";";
        var model = new ScriptCsharpModel(Code: authorCode);
        var ctx = new StubCompileContext("greet");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(authorCode, fragment.StatementBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <see cref="CsxFragment.StatementBlock"/> must not contain 'using var'
    /// anywhere (Roslyn script parse error, §13.3.1).
    /// </summary>
    [Fact]
    public void Emit_StatementBlock_ContainsNoUsingVar()
    {
        var model = new ScriptCsharpModel(Code: "var x = 1;");
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        Assert.DoesNotContain("using var", fragment.StatementBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// Engine-introduced locals must use the sanitised step id (hyphens → underscores).
    /// The raw hyphenated id must not appear anywhere in the StatementBlock.
    /// </summary>
    [Fact]
    public void Emit_StepIdWithHyphens_EngineLocalsUseSanitisedId()
    {
        const string rawId = "inline-script-step";
        var safeId = CsxFragment.SanitiseId(rawId); // "inline_script_step"
        var model = new ScriptCsharpModel(Code: "// author comment");
        var ctx = new StubCompileContext(rawId);

        var fragment = _provider.Emit(model, ctx);

        // The sanitised outcome key must appear in the block.
        var expectedKey = VarKeys.Outcome(safeId);
        Assert.Contains(expectedKey, fragment.StatementBlock, StringComparison.Ordinal);

        // Engine-introduced local names carry the safe id.
        Assert.Contains("__sw_" + safeId, fragment.StatementBlock, StringComparison.Ordinal);
        Assert.Contains("__v_" + safeId, fragment.StatementBlock, StringComparison.Ordinal);
        Assert.Contains("__obs_" + safeId, fragment.StatementBlock, StringComparison.Ordinal);

        // The raw hyphenated id must NOT appear (invalid C# identifier).
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// The outcome key in the StatementBlock must be the JSON-serialised form of
    /// <c>VarKeys.Outcome(safeId)</c> — i.e. wrapped in double-quotes and safely escaped.
    /// </summary>
    [Fact]
    public void Emit_OutcomeKey_IsJsonEscaped()
    {
        const string stepId = "check";
        var safeId = CsxFragment.SanitiseId(stepId);
        var expectedKeyLiteral = System.Text.Json.JsonSerializer.Serialize(VarKeys.Outcome(safeId));
        var model = new ScriptCsharpModel(Code: "// no-op");
        var ctx = new StubCompileContext(stepId);

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(expectedKeyLiteral, fragment.StatementBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="CsxFragment.RequiredUsings"/> must contain at least
    /// <c>System</c>, <c>System.Diagnostics</c>, and
    /// <c>Platform.Engine.Abstractions</c>.
    /// </summary>
    [Fact]
    public void Emit_RequiredUsings_ContainsExpectedNamespaces()
    {
        var model = new ScriptCsharpModel(Code: "// no-op");
        var ctx = new StubCompileContext("u");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("System", fragment.RequiredUsings, StringComparer.Ordinal);
        Assert.Contains("System.Diagnostics", fragment.RequiredUsings, StringComparer.Ordinal);
        Assert.Contains("Platform.Engine.Abstractions", fragment.RequiredUsings, StringComparer.Ordinal);
    }

    // ── 2. Hyphened step id with sanitised locals ─────────────────────────────

    /// <summary>
    /// When the step id contains hyphens, the engine-introduced locals in the
    /// emitted block use the sanitised id (underscores) for all three names
    /// (__sw_, __v_, __obs_).
    /// </summary>
    [Fact]
    public void Emit_HyphenatedId_AllEngineLocalsAreSanitised()
    {
        const string rawId = "a-b-c";
        var safeId = CsxFragment.SanitiseId(rawId); // "a_b_c"
        var model = new ScriptCsharpModel(Code: "Vars[\"k\"] = 0;");
        var ctx = new StubCompileContext(rawId);

        var fragment = _provider.Emit(model, ctx);
        var block = fragment.StatementBlock;

        // All three engine locals must use the safe id.
        Assert.Contains("__sw_" + safeId, block, StringComparison.Ordinal);
        Assert.Contains("__v_" + safeId, block, StringComparison.Ordinal);
        Assert.Contains("__obs_" + safeId, block, StringComparison.Ordinal);
        Assert.Contains("__ex_" + safeId, block, StringComparison.Ordinal);
    }

    // ── 3. Author code with braces survives verbatim ─────────────────────────

    /// <summary>
    /// Author body containing braces, string interpolations and inner blocks must
    /// appear intact in the StatementBlock — proving the StringBuilder splice and
    /// the absence of $$"""…""" interpolation (which would corrupt brace semantics).
    /// </summary>
    [Fact]
    public void Emit_AuthorCodeWithBraces_SurvivesVerbatim()
    {
        const string authorCode =
            "if (true) { Vars[\"n\"] = 1; } else { Vars[\"n\"] = 2; }";
        var model = new ScriptCsharpModel(Code: authorCode);
        var ctx = new StubCompileContext("branching");

        var fragment = _provider.Emit(model, ctx);

        // The entire author body including both brace-pairs must appear intact.
        Assert.Contains(authorCode, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 4. Compile + run round-trip: Pass ────────────────────────────────────

    /// <summary>
    /// A step whose Code sets a Vars entry compiles, runs, and produces
    /// <see cref="Verdict.Pass"/> with the value visible in Vars afterwards.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_PassVerdict_ValueAppearsInVars()
    {
        const string stepId = "greet-step";
        const string authorCode = "Vars[\"greeting\"] = \"hi\";";
        var model = new ScriptCsharpModel(Code: authorCode);
        var ctx = new StubCompileContext(stepId);

        var fragment = _provider.Emit(model, ctx);
        var compiled = CompileFragment(fragment);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var safeId = CsxFragment.SanitiseId(stepId);
        var outcomeKey = VarKeys.Outcome(safeId);

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain outcome key '{outcomeKey}'. Keys: [{string.Join(", ", vars.Keys)}]");

        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0);

        // The author-written value must also be in Vars.
        Assert.True(vars.ContainsKey("greeting"),
            "Expected Vars[\"greeting\"] to be set by the author code.");
        Assert.Equal("hi", vars["greeting"]);
    }

    // ── 5. Compile + run round-trip: throw → Fail ────────────────────────────

    /// <summary>
    /// A step whose Code throws must produce <see cref="Verdict.Fail"/> with
    /// the exception message as the observation.  The engine must still write
    /// the outcome (proves the try/finally wrapper is brace-balanced and correct).
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_ThrowingCode_FailVerdictWithObservation()
    {
        const string stepId = "boom-step";
        const string authorCode = "throw new System.Exception(\"boom\");";
        var model = new ScriptCsharpModel(Code: authorCode);
        var ctx = new StubCompileContext(stepId);

        var fragment = _provider.Emit(model, ctx);
        var compiled = CompileFragment(fragment);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        // Must not propagate an unhandled exception — the wrapper catches it.
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var safeId = CsxFragment.SanitiseId(stepId);
        var outcomeKey = VarKeys.Outcome(safeId);

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain outcome key '{outcomeKey}'.  Keys: [{string.Join(", ", vars.Keys)}]");

        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Equal("boom", outcome.Observation);
        Assert.True(outcome.DurationMs >= 0);
    }

    // ── 6. Validator: empty/whitespace Code rejected ──────────────────────────

    /// <summary>
    /// An empty Code string must be rejected with a clear validation error.
    /// </summary>
    [Fact]
    public void Validate_EmptyCode_IsInvalid()
    {
        var model = new ScriptCsharpModel(Code: string.Empty);

        var result = _provider.Validate(model, s_projectCtx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'code'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    /// <summary>
    /// A whitespace-only Code string must be rejected with a clear validation error.
    /// </summary>
    [Fact]
    public void Validate_WhitespaceCode_IsInvalid()
    {
        var model = new ScriptCsharpModel(Code: "   \t\n  ");

        var result = _provider.Validate(model, s_projectCtx);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    /// <summary>
    /// A non-empty Code string passes validation.
    /// </summary>
    [Fact]
    public void Validate_NonEmptyCode_IsValid()
    {
        var model = new ScriptCsharpModel(Code: "var x = 1;");

        var result = _provider.Validate(model, s_projectCtx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
    }

    // ── 7. Brace-injection attempt ────────────────────────────────────────────

    /// <summary>
    /// An author body that attempts to break out of the try block by injecting
    /// unbalanced braces (<c>} Vars["evil"]=1; {</c>) must cause a compile error,
    /// NOT a silent clobber of the outcome write or another step's state.
    ///
    /// Security reasoning: the author body is spliced verbatim inside
    /// <c>try { … }</c>.  If the body introduces unbalanced braces the assembled
    /// CSX text becomes syntactically invalid; Roslyn refuses to emit it and
    /// <see cref="ScriptCompilationException"/> is thrown.  That is an Inconclusive
    /// verdict — not a silent clobber (§13.3.1, §security M1).
    /// </summary>
    [Fact]
    public async Task Emit_BraceInjectionAttempt_CausesCompileError_NotSilentClobber()
    {
        // This body tries to close the try-block early and inject a statement outside it.
        const string maliciousCode = "} Vars[\"evil\"] = 1; {";
        const string stepId = "injection-attempt";
        var model = new ScriptCsharpModel(Code: maliciousCode);
        var ctx = new StubCompileContext(stepId);

        var fragment = _provider.Emit(model, ctx);

        // The compile must fail — assert that ScriptCompilationException is thrown.
        await Assert.ThrowsAsync<ScriptCompilationException>(async () =>
        {
            var compiled = CompileFragment(fragment);
            // If (unexpectedly) compilation succeeds, run to see if the clobber happened.
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, new ScriptGlobalVariables(vars));
            // If we reach here the injection attempt silently succeeded — fail the test.
            Assert.Fail("Brace-injection author body compiled and ran without error; " +
                        "the outcome-write clobber guard is broken.");
        });
    }

    // ── 8. Registry: provider discoverable ───────────────────────────────────

    /// <summary>
    /// Scanning the provider assembly via <see cref="StepKindRegistry.BuildAndFreeze"/>
    /// discovers <see cref="ScriptCsharpProvider"/> at key <c>"script.csharp"</c>.
    /// </summary>
    [Fact]
    public void Provider_IsDiscoverableViaStepKindRegistry()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(ScriptCsharpProvider).Assembly });

        var found = registry.TryGet("script.csharp", out var registered);

        Assert.True(found, "Expected 'script.csharp' to be registered.");
        Assert.NotNull(registered);
        Assert.Equal("script", registered!.Kind.Family);
        Assert.Equal("csharp", registered.Kind.Provider);
        Assert.IsType<ScriptCsharpProvider>(registered.Instance);
    }

    // ── 9. SchemaFragment contains "code" ─────────────────────────────────────

    /// <summary>
    /// The provider's <see cref="JsonSchemaFragment"/> must reference the <c>code</c> field.
    /// </summary>
    [Fact]
    public void Provider_SchemaFragment_ContainsCodeField()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(ScriptCsharpProvider).Assembly });

        registry.TryGet("script.csharp", out var registered);

        Assert.NotNull(registered!.SchemaFragment);
        Assert.Contains("code", registered.SchemaFragment!.Json, StringComparison.Ordinal);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Assembles a <see cref="CsxFragment"/> into a single CSX source string and
    /// compiles it with <see cref="RoslynScriptCompiler.CompileOnce"/>.
    /// Mirrors what <c>CsxAssembler.Assemble</c> does for a single step.
    /// </summary>
    private static CompiledScript CompileFragment(CsxFragment fragment)
    {
        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";
        return RoslynScriptCompiler.CompileOnce(csx);
    }
}
