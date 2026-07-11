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
//   9. SchemaFragment: contains "code" and "file" fields.
//  10. M3 fix: return; in author body does NOT abort downstream steps (local-function containment).
//  11. M3 fix: author body using await compiles and runs correctly.
//  12. M3 fix: brace-injection into local function still yields compile error.
//  13. 'file' field: Bind reads it, Validate enforces exclusivity/existence, Emit
//      reads the referenced .csx file's content and splices it verbatim, exactly
//      like an inline 'code' body.
using System.IO;
using System.Linq;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.Script.Csharp.Tests;

// ── Stubs ─────────────────────────────────────────────────────────────────────

/// <summary>Minimal <see cref="IBindingContext"/> for bind tests.</summary>
file sealed class StubBindingContext : IBindingContext { }

/// <summary>Minimal <see cref="ICompileContext"/> for emit tests.</summary>
file sealed class StubCompileContext : ICompileContext
{
    internal StubCompileContext(string stepId, string? suiteDirectory = null)
    {
        StepId = stepId;
        SuiteDirectory = suiteDirectory ?? Directory.GetCurrentDirectory();
    }

    /// <inheritdoc />
    public string StepId { get; }

    /// <inheritdoc />
    public string SuiteNamespace => "Generated";

    /// <inheritdoc />
    public string SuiteDirectory { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Captures { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
        new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
}

/// <summary>Minimal <see cref="IProjectContext"/> for validator tests.</summary>
file sealed class StubProjectContext : IProjectContext
{
    internal StubProjectContext(string? suiteDirectory = null)
    {
        SuiteDirectory = suiteDirectory ?? Directory.GetCurrentDirectory();
    }

    public IReadOnlyDictionary<string, string> DeclaredDependencies { get; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public string SuiteDirectory { get; }
}

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Non-docker unit and integration tests for <see cref="ScriptCsharpProvider"/>.
/// </summary>
public sealed class ScriptCsharpProviderTests : IDisposable
{
    private readonly ScriptCsharpProvider _provider = new();
    private static readonly IProjectContext s_projectCtx = new StubProjectContext();

    // A fresh temp directory per test, used by the 'file' field tests to stand in
    // for the scenario's own directory (SuiteDirectory).  Deleted in Dispose.
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vouchfx-script-csharp-tests-" + Guid.NewGuid().ToString("n"));

    public ScriptCsharpProviderTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the test.
        }
    }

    // ── 1. Emit lint ─────────────────────────────────────────────────────────

    /// <summary>
    /// The emitted <see cref="CsxFragment.StatementBlock"/> must begin with '{'
    /// and end with '}' (§13.3.1 brace rule).
    /// </summary>
    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var model = new ScriptCsharpModel(Code: "Vars[\"x\"] = 1;", File: null);
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
        var model = new ScriptCsharpModel(Code: authorCode, File: null);
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
        var model = new ScriptCsharpModel(Code: "var x = 1;", File: null);
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
        var model = new ScriptCsharpModel(Code: "// author comment", File: null);
        var ctx = new StubCompileContext(rawId);

        var fragment = _provider.Emit(model, ctx);

        // The sanitised outcome key must appear in the block.
        var expectedKey = VarKeys.Outcome(safeId);
        Assert.Contains(expectedKey, fragment.StatementBlock, StringComparison.Ordinal);

        // Engine-introduced local names carry the safe id.
        Assert.Contains("__sw_" + safeId, fragment.StatementBlock, StringComparison.Ordinal);
        Assert.Contains("__v_" + safeId, fragment.StatementBlock, StringComparison.Ordinal);
        Assert.Contains("__obs_" + safeId, fragment.StatementBlock, StringComparison.Ordinal);
        Assert.Contains("__body_" + safeId, fragment.StatementBlock, StringComparison.Ordinal);

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
        var model = new ScriptCsharpModel(Code: "// no-op", File: null);
        var ctx = new StubCompileContext(stepId);

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(expectedKeyLiteral, fragment.StatementBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="CsxFragment.RequiredUsings"/> must contain at least
    /// <c>System</c>, <c>System.Diagnostics</c>, <c>System.Threading.Tasks</c>, and
    /// <c>Vouchfx.Engine.Abstractions</c>.
    /// </summary>
    [Fact]
    public void Emit_RequiredUsings_ContainsExpectedNamespaces()
    {
        var model = new ScriptCsharpModel(Code: "// no-op", File: null);
        var ctx = new StubCompileContext("u");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("System", fragment.RequiredUsings, StringComparer.Ordinal);
        Assert.Contains("System.Diagnostics", fragment.RequiredUsings, StringComparer.Ordinal);
        Assert.Contains("System.Threading.Tasks", fragment.RequiredUsings, StringComparer.Ordinal);
        Assert.Contains("Vouchfx.Engine.Abstractions", fragment.RequiredUsings, StringComparer.Ordinal);
    }

    // ── 2. Hyphened step id with sanitised locals ─────────────────────────────

    /// <summary>
    /// When the step id contains hyphens, the engine-introduced locals in the
    /// emitted block use the sanitised id (underscores) for all names
    /// (__sw_, __v_, __obs_, __body_, __ex_).
    /// </summary>
    [Fact]
    public void Emit_HyphenatedId_AllEngineLocalsAreSanitised()
    {
        const string rawId = "a-b-c";
        var safeId = CsxFragment.SanitiseId(rawId); // "a_b_c"
        var model = new ScriptCsharpModel(Code: "Vars[\"k\"] = 0;", File: null);
        var ctx = new StubCompileContext(rawId);

        var fragment = _provider.Emit(model, ctx);
        var block = fragment.StatementBlock;

        // All engine locals must use the safe id.
        Assert.Contains("__sw_" + safeId, block, StringComparison.Ordinal);
        Assert.Contains("__v_" + safeId, block, StringComparison.Ordinal);
        Assert.Contains("__obs_" + safeId, block, StringComparison.Ordinal);
        Assert.Contains("__body_" + safeId, block, StringComparison.Ordinal);
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
        var model = new ScriptCsharpModel(Code: authorCode, File: null);
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
        var model = new ScriptCsharpModel(Code: authorCode, File: null);
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
        var model = new ScriptCsharpModel(Code: authorCode, File: null);
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
        var model = new ScriptCsharpModel(Code: string.Empty, File: null);

        var result = _provider.Validate(model, s_projectCtx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("exactly one of", StringComparison.Ordinal));
    }

    /// <summary>
    /// A whitespace-only Code string must be rejected with a clear validation error.
    /// </summary>
    [Fact]
    public void Validate_WhitespaceCode_IsInvalid()
    {
        var model = new ScriptCsharpModel(Code: "   \t\n  ", File: null);

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
        var model = new ScriptCsharpModel(Code: "var x = 1;", File: null);

        var result = _provider.Validate(model, s_projectCtx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
    }

    // ── 13a. Validator: 'code'/'file' exclusivity ─────────────────────────────

    /// <summary>
    /// Setting both 'code' and 'file' must be rejected as mutually exclusive.
    /// </summary>
    [Fact]
    public void Validate_BothCodeAndFileSet_IsInvalid()
    {
        var model = new ScriptCsharpModel(Code: "var x = 1;", File: "script.csx");

        var result = _provider.Validate(model, s_projectCtx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("mutually exclusive", StringComparison.Ordinal));
    }

    /// <summary>
    /// Setting neither 'code' nor 'file' must be rejected.
    /// </summary>
    [Fact]
    public void Validate_NeitherCodeNorFileSet_IsInvalid()
    {
        var model = new ScriptCsharpModel(Code: null, File: null);

        var result = _provider.Validate(model, s_projectCtx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("exactly one of", StringComparison.Ordinal));
    }

    /// <summary>
    /// A 'file' reference that does not exist relative to
    /// <see cref="IProjectContext.SuiteDirectory"/> must be rejected with a clear
    /// error naming both the declared path and the resolved path — this keeps a
    /// bad/typo'd path a clean Inconclusive verdict (via ValidationFailure) rather
    /// than an unhandled exception from Emit.
    /// </summary>
    [Fact]
    public void Validate_FileSet_MissingFile_IsInvalid()
    {
        var model = new ScriptCsharpModel(Code: null, File: "does-not-exist.csx");
        var ctx = new StubProjectContext(_root);

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("does-not-exist.csx", StringComparison.Ordinal) &&
            e.Contains(_root, StringComparison.Ordinal));
    }

    /// <summary>
    /// A 'file' reference that exists relative to
    /// <see cref="IProjectContext.SuiteDirectory"/> passes validation.
    /// </summary>
    [Fact]
    public void Validate_FileSet_ExistingFile_IsValid()
    {
        var fileName = "existing-" + Guid.NewGuid().ToString("n") + ".csx";
        File.WriteAllText(Path.Combine(_root, fileName), "Vars[\"x\"] = 1;");

        var model = new ScriptCsharpModel(Code: null, File: fileName);
        var ctx = new StubProjectContext(_root);

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 7. Brace-injection attempt ────────────────────────────────────────────

    /// <summary>
    /// An author body that attempts structural injection by closing the local
    /// function early and orphaning a <c>catch</c> clause must cause a compile error,
    /// NOT a silent clobber of the outcome write or another step's state.
    ///
    /// Security reasoning (M3 fix): the author body is spliced verbatim inside
    /// an <c>async</c> local function <c>__body_&lt;id&gt;()</c>.  If the body
    /// closes that function early and injects an orphaned <c>catch</c> or other
    /// mismatched keyword, the assembled CSX text is syntactically invalid; Roslyn
    /// refuses to emit it and <see cref="ScriptCompilationException"/> is thrown.
    /// That is an Inconclusive verdict — not a silent clobber (§13.3.1, §security M1).
    /// </summary>
    [Fact]
    public async Task Emit_BraceInjectionAttempt_CausesCompileError_NotSilentClobber()
    {
        // This body closes the local async function early, then injects an orphaned
        // catch clause which is syntactically invalid outside a try block.
        const string maliciousCode = "} catch (System.Exception) { Vars[\"evil\"] = 1; } async System.Threading.Tasks.Task __dummy() {";
        const string stepId = "injection-attempt";
        var model = new ScriptCsharpModel(Code: maliciousCode, File: null);
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

    // ── M3 new tests: return-containment, author-await, brace-injection ──────

    /// <summary>
    /// M3 fix: a <c>return;</c> in the first script.csharp step must NOT abort the
    /// Roslyn submission delegate — the second step's Vars write must still execute.
    /// This proves <c>return;</c> now returns from <c>__body_&lt;safeId&gt;()</c>
    /// only, not from the entire submission.
    /// </summary>
    [Fact]
    public async Task Emit_ReturnInAuthorBody_DoesNotAbortDownstreamSteps()
    {
        // Step 1: author body is a bare 'return;'
        const string stepId1 = "early-return";
        var model1 = new ScriptCsharpModel(Code: "return;", File: null);
        var ctx1 = new StubCompileContext(stepId1);
        var fragment1 = _provider.Emit(model1, ctx1);

        // Step 2: author body writes a marker var
        const string stepId2 = "after-return";
        var model2 = new ScriptCsharpModel(Code: "Vars[\"after\"] = \"ok\";", File: null);
        var ctx2 = new StubCompileContext(stepId2);
        var fragment2 = _provider.Emit(model2, ctx2);

        // Assemble both steps into one CSX (mirrors CsxAssembler).
        var allUsings = fragment1.RequiredUsings
            .Concat(fragment2.RequiredUsings)
            .Distinct(StringComparer.Ordinal);
        var usings = string.Join("\n", allUsings.Select(u => $"using {u};"));
        var csx = $"{usings}\n{fragment1.StatementBlock}\n{fragment2.StatementBlock}";
        var compiled = RoslynScriptCompiler.CompileOnce(csx);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        // Step 2's marker must be present (downstream step ran).
        Assert.True(vars.ContainsKey("after"),
            $"Expected Vars[\"after\"] to be set by the downstream step. Keys: [{string.Join(", ", vars.Keys)}]");
        Assert.Equal("ok", vars["after"]);

        // Step 1 must still have a Pass outcome (return; exits cleanly).
        var safeId1 = CsxFragment.SanitiseId(stepId1);
        var outcomeKey1 = VarKeys.Outcome(safeId1);
        Assert.True(vars.ContainsKey(outcomeKey1),
            $"Expected step 1 outcome key '{outcomeKey1}'. Keys: [{string.Join(", ", vars.Keys)}]");
        var outcome1 = Assert.IsType<StepOutcome>(vars[outcomeKey1]);
        Assert.Equal(Verdict.Pass, outcome1.Verdict);
    }

    /// <summary>
    /// M3 fix: an author body that uses <c>await</c> must compile and run correctly
    /// because the local function is declared <c>async</c>.
    /// </summary>
    [Fact]
    public async Task Emit_AuthorBodyWithAwait_CompilesAndRunsCorrectly()
    {
        const string stepId = "await-step";
        // Author body that awaits a Task and then writes a Vars entry.
        const string authorCode =
            "await System.Threading.Tasks.Task.Delay(1);\n" +
            "Vars[\"awaited\"] = \"yes\";";
        var model = new ScriptCsharpModel(Code: authorCode, File: null);
        var ctx = new StubCompileContext(stepId);

        var fragment = _provider.Emit(model, ctx);
        var compiled = CompileFragment(fragment);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        // The author var must be set (await completed correctly).
        Assert.True(vars.ContainsKey("awaited"),
            $"Expected Vars[\"awaited\"] to be set. Keys: [{string.Join(", ", vars.Keys)}]");
        Assert.Equal("yes", vars["awaited"]);

        // Step must be Pass.
        var safeId = CsxFragment.SanitiseId(stepId);
        var outcomeKey = VarKeys.Outcome(safeId);
        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.Pass, outcome.Verdict);
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

    // ── 9. SchemaFragment contains "code" and "file" ──────────────────────────

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

    /// <summary>
    /// The provider's <see cref="JsonSchemaFragment"/> must also reference the
    /// <c>file</c> field, and require exactly one of <c>code</c>/<c>file</c>.
    /// </summary>
    [Fact]
    public void Provider_SchemaFragment_ContainsFileField()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(ScriptCsharpProvider).Assembly });

        registry.TryGet("script.csharp", out var registered);

        Assert.NotNull(registered!.SchemaFragment);
        Assert.Contains("file", registered.SchemaFragment!.Json, StringComparison.Ordinal);
        Assert.Contains("oneOf", registered.SchemaFragment.Json, StringComparison.Ordinal);
    }

    // ── 13b. Bind: 'file' vs 'code' ────────────────────────────────────────────

    /// <summary>
    /// Binding a step mapping with a <c>file</c> key populates
    /// <see cref="ScriptCsharpModel.File"/> and leaves <see cref="ScriptCsharpModel.Code"/> null.
    /// </summary>
    [Fact]
    public void Bind_FileKey_PopulatesFileNotCode()
    {
        var model = BindYaml("id: s1\ntype: script.csharp\nfile: scripts/check.csx\n");

        Assert.Equal("scripts/check.csx", model.File);
        Assert.Null(model.Code);
    }

    /// <summary>
    /// Binding a step mapping with a <c>code</c> key populates
    /// <see cref="ScriptCsharpModel.Code"/> and leaves <see cref="ScriptCsharpModel.File"/> null.
    /// </summary>
    [Fact]
    public void Bind_CodeKey_PopulatesCodeNotFile()
    {
        var model = BindYaml("id: s1\ntype: script.csharp\ncode: 'Vars[\"x\"] = 1;'\n");

        Assert.Equal("Vars[\"x\"] = 1;", model.Code);
        Assert.Null(model.File);
    }

    /// <summary>
    /// Binding a step mapping with neither key leaves both null (Validate is the
    /// stage that rejects this, not Bind — Bind is a pure, non-throwing projection).
    /// </summary>
    [Fact]
    public void Bind_MappingWithNeitherKey_BothNull()
    {
        var model = BindYaml("id: s1\ntype: script.csharp\n");

        Assert.Null(model.Code);
        Assert.Null(model.File);
    }

    // ── 13c. Emit: 'file' reads and splices external content ─────────────────

    /// <summary>
    /// When <see cref="ScriptCsharpModel.File"/> is set, <c>Emit</c> reads the
    /// referenced file's content (resolved against
    /// <see cref="ICompileContext.SuiteDirectory"/>) and splices it verbatim —
    /// identically to how an inline <c>code</c> body is spliced.
    /// </summary>
    [Fact]
    public void Emit_FileSet_ReadsExternalFileContent_SplicesVerbatim()
    {
        const string fileContent = "Vars[\"fromFile\"] = \"yes\";";
        var fileName = "check-" + Guid.NewGuid().ToString("n") + ".csx";
        File.WriteAllText(Path.Combine(_root, fileName), fileContent);

        var model = new ScriptCsharpModel(Code: null, File: fileName);
        var ctx = new StubCompileContext("file-step", _root);

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(fileContent, fragment.StatementBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// A step whose 'file' reference sets a Vars entry compiles, runs, and
    /// produces <see cref="Verdict.Pass"/> with the value visible in Vars
    /// afterwards — the full round trip via an external file, not just Emit.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_FileBasedScript_PassVerdict_ValueAppearsInVars()
    {
        const string stepId = "file-greet-step";
        var fileName = "greet-" + Guid.NewGuid().ToString("n") + ".csx";
        File.WriteAllText(Path.Combine(_root, fileName), "Vars[\"greeting\"] = \"hi from file\";");

        var model = new ScriptCsharpModel(Code: null, File: fileName);
        var ctx = new StubCompileContext(stepId, _root);

        var fragment = _provider.Emit(model, ctx);
        var compiled = CompileFragment(fragment);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var safeId = CsxFragment.SanitiseId(stepId);
        var outcomeKey = VarKeys.Outcome(safeId);

        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.Pass, outcome.Verdict);

        Assert.True(vars.ContainsKey("greeting"),
            "Expected Vars[\"greeting\"] to be set by the external file's code.");
        Assert.Equal("hi from file", vars["greeting"]);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses a single-step YAML mapping and binds it through the provider.
    /// </summary>
    private ScriptCsharpModel BindYaml(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = (YamlMappingNode)stream.Documents[0].RootNode;
        return _provider.Bind(root, new StubBindingContext());
    }

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
