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
//  13. 'file' field: Bind reads it, Validate enforces exclusivity/existence/size (via
//      FileInfo.Length — content is never read at Validate time), Emit reads the
//      referenced .csx file's content and splices it verbatim, exactly like an inline
//      'code' body.
//  14. Size bound (plain resource limit, NOT a crash-closer): 'code'/'file' body size
//      is capped at 64 KiB.
//  15. Path disclosure (#488): when Emit's read of the 'file' reference fails, the
//      diagnostic that escapes names the DECLARED path and never the resolved absolute
//      host path — asserted through the shared HostPathDisclosure predicate, and only
//      after the raw BCL failure has been pinned as containing that resolved path.
using System.IO;
using System.Linq;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Vouchfx.TestSupport;
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
    public IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices { get; }
        = new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal);

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
        var compiled = CompileFragment(fragment, stepId);

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
        var compiled = CompileFragment(fragment, stepId);

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

        // THIS ROW USED TO REQUIRE `_root` IN THE MESSAGE — the resolved absolute host path —
        // and that is the disclosure #357 removed. It is now asserted absent. The declared path
        // stays, because it is the actionable half and the resolved form never was: the same
        // change, with the same measured outcome, that slice D made to SecurityMaterialException.
        //
        // It matters because the audience is wider than whoever runs the suite (a compile-time
        // ValidationResult.Failure ships to CI artefacts and dashboards) and because it cannot be
        // redacted downstream: ScenarioRunner.ScrubDiagnostic is ResolvedSecrets.Scrub, a targeted
        // net over values the run's SecretAccessor actually revealed, which never covers a path.
        Assert.Contains(result.Errors, e =>
            e.Contains("does-not-exist.csx", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, e =>
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
            var compiled = CompileFragment(fragment, stepId);
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

        // Assemble both steps into one CSX via CsxAssembler (not a manual join) — it
        // declares each step's own __stepCt_<safeId> local, referenced by the emitted
        // rethrow filter (§4 common step fields, issue #232).
        var assembled = CsxAssembler.Assemble(new[] { (stepId1, fragment1), (stepId2, fragment2) });
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource);

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
        var compiled = CompileFragment(fragment, stepId);

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
    /// Scanning the provider assembly via <see cref="StepKindRegistry.BuildAndFreeze(System.Collections.Generic.IEnumerable{System.Reflection.Assembly})"/>
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

    // ── 13c-bis. Validate: an unresolvable 'file' fails cleanly, never throws ──

    /// <summary>
    /// <c>Validate</c>'s contract is to NEVER throw. A declared <c>file</c> path that
    /// <c>Path.GetFullPath</c> cannot resolve must therefore come back as a
    /// <see cref="ValidationResult"/> failure naming the declared path, not as an exception.
    /// </summary>
    /// <remarks>
    /// Found in peer review of #488: the resolve sat outside every <c>try</c>, immediately above
    /// the sibling <c>FileInfo.Length</c> stat whose own comment states the never-throw rule.
    /// The trigger is an embedded NUL — measured on net8.0 as
    /// <c>ArgumentException: Null character in path.</c> — which is also the one route whose BCL
    /// message happens to carry no path, so the disclosure assertion here is a guard against a
    /// future message rather than a present leak.
    /// </remarks>
    [Fact]
    public void Validate_FilePathUnresolvable_ReturnsFailure_DoesNotThrow()
    {
        var declaredPath = "fixtures/bad\0name.csx";
        var model = new ScriptCsharpModel(Code: null, File: declaredPath);
        var ctx = new StubProjectContext(_root);

        // The premise: the resolve this guard wraps really does throw for this input.
        Assert.ThrowsAny<ArgumentException>(
            () => Path.GetFullPath(Path.Combine(_root, declaredPath)));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("script.csharp", error, StringComparison.Ordinal);
        Assert.Contains("ArgumentException", error, StringComparison.Ordinal);
        HostPathDisclosure.AssertNoAbsoluteHostPath(
            "the script.csharp Validate resolve diagnostic", error, _root);
    }

    // ── 13d. Emit: an unreadable 'file' discloses no resolved host path (#488) ──

    /// <summary>
    /// When <c>Emit</c>'s read of the referenced <c>.csx</c> file fails, the diagnostic that
    /// escapes must name the <strong>declared</strong> path the author wrote and never the
    /// resolved absolute host path — #357's rule, in the shape #473 applied to
    /// <c>SeedFixtures</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THE ESCAPE MATTERS, stated with its actual condition rather than unconditionally.
    /// <c>ProviderPipeline</c> catches an <c>Emit</c> throw and folds it into a compile refusal
    /// whose text <c>DescribeProviderFault</c> composes — <c>"step '&lt;id&gt;': the
    /// '&lt;type&gt;' provider's Emit threw &lt;Type&gt;: &lt;chain&gt;  &lt;attribution&gt;"</c>
    /// — and that reaches <c>--events</c>, the JUnit XML and the HTML report. Between the throw
    /// and the artefact sits <c>ScrubSuiteDirectory</c>, which substitutes the literal text
    /// "the suite directory" for the resolved suite directory. <strong>So an in-suite
    /// <c>file:</c> was already partly covered, and the guard is load-bearing for a path that
    /// resolves OUTSIDE the suite directory</strong> — which nothing refuses: <c>file</c>
    /// carries <c>minLength: 1</c> and no <c>pattern</c> in the schema, and <c>Validate</c> runs
    /// an existence check and a size check with no containment check. Both cases are driven
    /// below, and the escaping row asserts that it really does escape.
    /// </para>
    /// <para>
    /// NO PROVIDER-SIDE ALTERNATIVE EXISTS. No provider assembly can reach
    /// <c>SecurityPathDisclosureLedger</c> (a provider references only <c>Vouchfx.Sdk</c> and
    /// <c>Vouchfx.Engine.Abstractions</c>), and the declared/resolved pair was never recorded
    /// into one, so omitting the resolved path AT THE SOURCE is the only guard available —
    /// exactly the reasoning <c>Validate</c>'s not-found and stat guards already carry.
    /// </para>
    /// <para>
    /// THE PREMISE IS PINNED IN THE TEST, not assumed, because the whole case rests on it: the
    /// raw BCL failure is asserted to contain the resolved path BEFORE the provider's own
    /// diagnostic is asserted not to. Without that first assertion a future BCL that stopped
    /// quoting the path would turn this into a test that passes while proving nothing.
    /// </para>
    /// <para>
    /// <strong>THE WHOLE INNER CHAIN IS ASSERTED, NOT JUST <c>Message</c>, and that is the point
    /// of this test rather than a flourish.</strong> The guard's design depends on attaching NO
    /// inner exception, because <c>ProviderPipeline.DescribeCauseChain</c> walks up to four
    /// links and appends each one's message. <see cref="Exception.Message"/> does not include
    /// inner messages, so a maintainer adding <c>innerException: ex</c> would reinstate the full
    /// disclosure while a <c>Message</c>-only assertion stayed green. The rendered chain is
    /// therefore reproduced here in <c>DescribeCauseChain</c>'s own shape and asserted over,
    /// alongside an explicit <see langword="null"/> check on the inner exception for a legible
    /// failure.
    /// </para>
    /// <para>
    /// <strong>THE TRIGGER IS SYNTHETIC AND SAYING SO MATTERS.</strong> A directory in the
    /// file's place cannot occur in production: <c>File.Exists</c> returns
    /// <see langword="false"/> for a directory — asserted below rather than claimed — so
    /// <c>Validate</c> refuses the step first and <c>Emit</c> is never reached. It is used
    /// because it is the one trigger that is deterministic on both platforms (CI runs on
    /// ubuntu-latest, this suite is authored on Windows) and needs no <c>FileShare</c>
    /// emulation, whose Unix behaviour is advisory. <strong>The production shapes are the two
    /// <c>Validate</c> genuinely cannot pre-empt</strong> — measured on net8.0, both
    /// <c>File.Exists</c> and <c>FileInfo.Length</c> SUCCEED on an exclusively-locked file and
    /// on an ACL-denied one that <c>File.ReadAllText</c> then refuses with
    /// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> naming the resolved
    /// path. All three reach the same guarded read, so the synthetic trigger exercises the same
    /// code path the real ones take.
    /// </para>
    /// </remarks>
    /// <param name="escapesSuiteDirectory">
    /// <see langword="false"/> drives an ordinary in-suite relative path;
    /// <see langword="true"/> drives one that resolves outside the suite directory — the case
    /// <c>ScrubSuiteDirectory</c> cannot cover and the only one where this guard is the sole
    /// protection.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Emit_FileUnreadable_DiagnosticNamesDeclaredPathNeverResolvedPath(
        bool escapesSuiteDirectory)
    {
        // `outsideRoot` is a sibling of the suite directory, not a child, so a declared path
        // reaching it genuinely leaves the tree ScrubSuiteDirectory knows about.
        var outsideRoot = Path.Combine(
            Path.GetTempPath(), "vouchfx-script-outside-" + Guid.NewGuid().ToString("n"));

        try
        {
            string declaredPath;
            string hostDirectory;

            if (escapesSuiteDirectory)
            {
                Directory.CreateDirectory(outsideRoot);

                // The author writes a traversal; nothing in the schema or in Validate refuses
                // one. The host directory at risk is the one it lands in, not the suite's.
                declaredPath = Path.GetRelativePath(
                    _root, Path.Combine(outsideRoot, "escaped.csx"));
                hostDirectory = outsideRoot;
            }
            else
            {
                declaredPath = "fixtures/unreadable-" + Guid.NewGuid().ToString("n") + ".csx";
                hostDirectory = _root;
            }

            var resolvedPath = Path.GetFullPath(Path.Combine(_root, declaredPath));

            // THE ROW'S OWN PRECONDITION: the escaping row must actually escape, or it silently
            // degrades into a duplicate of the in-suite row.
            Assert.Equal(
                escapesSuiteDirectory,
                !resolvedPath.StartsWith(
                    _root + Path.DirectorySeparatorChar, StringComparison.Ordinal));

            // A directory where the file should be. Asserting File.Exists is false records, as
            // an executable fact, why this trigger is synthetic: Validate would refuse first.
            Directory.CreateDirectory(resolvedPath);
            Assert.False(
                File.Exists(resolvedPath),
                "File.Exists must be false for a directory — this is why the trigger is "
                + "synthetic and Validate would refuse such a step before Emit ran.");

            // ── Premise, asserted rather than assumed ────────────────────────
            var raw = Assert.ThrowsAny<Exception>(() => File.ReadAllText(resolvedPath));
            Assert.Contains(hostDirectory, raw.Message, StringComparison.Ordinal);

            // ── The property under test ─────────────────────────────────────
            var model = new ScriptCsharpModel(Code: null, File: declaredPath);
            var ctx = new StubCompileContext("unreadable-step", _root);

            var thrown = Assert.ThrowsAny<Exception>(() => _provider.Emit(model, ctx));

            const string Channel = "the script.csharp Emit file-read diagnostic";

            // (1) The message itself.
            HostPathDisclosure.AssertNoAbsoluteHostPath(Channel, thrown.Message, hostDirectory);

            // (2) THE GATE ON THE DESIGN: no inner exception to walk. Stated directly so the
            // failure names the edit that caused it.
            Assert.Null(thrown.InnerException);

            // (3) And the property over the chain AS THE PIPELINE RENDERS IT, so the guarantee
            // does not depend on (2) being the check a future maintainer happens to read. This
            // mirrors ProviderPipeline.DescribeCauseChain's shape and its four-link bound.
            var rendered = thrown.Message;
            var link = thrown.InnerException;
            for (var depth = 1; link is not null && depth < 4; depth++)
            {
                rendered += $" -> {link.GetType().Name}: {link.Message}";
                link = link.InnerException;
            }

            HostPathDisclosure.AssertNoAbsoluteHostPath(
                Channel + " (message + rendered inner-exception chain)", rendered, hostDirectory);

            // The declared path is the actionable half and must survive: a diagnostic that
            // disclosed nothing AND identified nothing would satisfy the rule while being
            // useless.
            Assert.Contains(declaredPath, thrown.Message, StringComparison.Ordinal);
            Assert.Contains("script.csharp", thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
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
        var compiled = CompileFragment(fragment, stepId);

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

    // ── 14. Size bound (NOT a crash-closer — see class/file remarks) ─────────
    //
    // A plain 64 KiB resource bound on the script.csharp body: the inline 'code' text
    // (characters) and the 'file' reference's on-disk size (bytes, via FileInfo.Length —
    // content is never read for this check). Each is tested at its exact boundary and
    // one-over. A bracket-nesting-depth companion check was tried and removed (it could
    // not, even in principle, catch the non-bracket constructs that actually recurse the
    // compiler) — there is deliberately no nesting-depth test here any more.

    [Fact]
    public void Validate_CodeSizeExceeds64KiB_IsInvalid()
    {
        // Exactly 65537 chars: one over the 64 KiB / 65536-char limit.
        var model = new ScriptCsharpModel(Code: new string('a', 65537), File: null);

        var result = _provider.Validate(model, s_projectCtx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("size 65537 characters", StringComparison.Ordinal) &&
            // Copilot review (#277, grammar): the hyphenated adjective before "limit" is
            // SINGULAR ("65536-character limit") even though the measured count just before
            // it is plural ("65537 characters") - the trailing " limit" here is load-bearing:
            // it is what distinguishes the correct "65536-character limit" from the buggy
            // "65536-characters limit" this assertion would otherwise also match, since
            // "65536-character" alone is a substring of both.
            e.Contains("65536-character limit", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_CodeSizeAt64KiB_IsValid()
    {
        // Exactly 65536 chars: AT the limit, not exceeding it.
        var model = new ScriptCsharpModel(Code: new string('a', 65536), File: null);

        var result = _provider.Validate(model, s_projectCtx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // Copilot review (#277): Validate's File.Exists(resolvedPath) check is immediately
    // followed by a new FileInfo(resolvedPath).Length stat, now wrapped in a try/catch for
    // IOException/UnauthorizedAccessException/SecurityException (see ScriptCsharpProvider,
    // just below the size-cap comment) so a permissions problem or a racey delete between
    // the two calls returns a clean ValidationResult.Failure instead of an unhandled
    // exception. NO test exercises the catch through Validate itself: empirical probing
    // confirmed File.Exists(path) returns FALSE for a directory (so a directory 'file:'
    // target is rejected by the EARLIER "not found" branch above, never reaching the
    // stat), and the one case that DOES make FileInfo.Length throw after File.Exists saw
    // the file - deleting it between the two calls - can only be reproduced by racing a
    // concurrent deletion against Validate's own two sequential, back-to-back calls (no
    // seam exists to inject a delay between them without an invasive refactor of a small,
    // targeted fix). That is an inherently flaky trigger, not a deterministic one, so per
    // this fix's brief it is intentionally left untested rather than added as a racy test.

    /// <summary>
    /// The 'file' size check reads ONLY <see cref="FileInfo.Length"/> — never the file's
    /// content — so a file this large costs one filesystem stat, not a 65 KB read (and,
    /// by the same code path, would cost a stat rather than a multi-GB read for a
    /// pathologically large file).
    /// </summary>
    [Fact]
    public void Validate_FileSizeExceeds64KiB_IsInvalid()
    {
        var fileName = "big-" + Guid.NewGuid().ToString("n") + ".csx";
        // Exactly 65537 bytes: one over the 64 KiB / 65536-byte limit. File.WriteAllText
        // defaults to UTF-8 WITHOUT a byte-order mark, so the on-disk byte count exactly
        // matches the character count written here (all-ASCII content).
        File.WriteAllText(Path.Combine(_root, fileName), new string('a', 65537));

        var model = new ScriptCsharpModel(Code: null, File: fileName);
        var ctx = new StubProjectContext(_root);

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("size 65537 bytes", StringComparison.Ordinal) &&
            // Copilot review (#277, grammar): see the analogous comment on
            // Validate_CodeSizeExceeds64KiB_IsInvalid above - "65536-byte limit" (singular)
            // is the correct wording; the trailing " limit" distinguishes it from the buggy
            // plural "65536-bytes limit".
            e.Contains("65536-byte limit", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_FileSizeAt64KiB_IsValid()
    {
        var fileName = "big-ok-" + Guid.NewGuid().ToString("n") + ".csx";
        File.WriteAllText(Path.Combine(_root, fileName), new string('a', 65536));

        var model = new ScriptCsharpModel(Code: null, File: fileName);
        var ctx = new StubProjectContext(_root);

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
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
    /// Assembles a <see cref="CsxFragment"/> into a single CSX source string via
    /// <see cref="CsxAssembler.Assemble(IReadOnlyList{ValueTuple{string, CsxFragment}})"/>
    /// — not a manual join — and compiles it with
    /// <see cref="RoslynScriptCompiler.CompileOnce"/>.  <c>CsxAssembler</c> declares the
    /// per-step <c>__stepCt_&lt;safeId&gt;</c> local the emitted StatementBlock's rethrow
    /// filter now references (§4 common step fields, issue #232).
    /// </summary>
    private static CompiledScript CompileFragment(CsxFragment fragment, string stepId)
    {
        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
        return RoslynScriptCompiler.CompileOnce(assembled.CsxSource);
    }

    /// <summary>
    /// #357: the file-not-found diagnostic must not disclose the resolved absolute host path.
    /// </summary>
    /// <remarks>
    /// It cannot be redacted downstream: <c>ScenarioRunner.ScrubDiagnostic</c> is
    /// <c>ResolvedSecrets.Scrub</c>, a targeted net over values the run's <c>SecretAccessor</c>
    /// actually revealed, so a filesystem path is never covered by it. And the audience is wider
    /// than whoever runs the suite — a compile-time <c>ValidationResult.Failure</c> lands in a
    /// scenario diagnostic that ships to CI artefacts and dashboards.
    /// <para>
    /// The DECLARED path is retained, because it is the actionable half; the resolved form never
    /// was. Same fix, same reasoning, as slice D applied to <c>SecurityMaterialException</c>'s
    /// <c>clientCert</c>/<c>clientKey</c>/<c>caCert</c> messages.
    /// </para>
    /// </remarks>
    [Fact]
    public void Validate_MissingFile_NamesTheDeclaredPathButNotTheResolvedOne()
    {
        var suiteDirectory = Directory.CreateTempSubdirectory("vouchfx-357-").FullName;
        try
        {
            var model = new ScriptCsharpModel(Code: null, File: "missing/helper.csx");

            var result = _provider.Validate(model, new StubProjectContext(suiteDirectory));

            Assert.False(result.IsValid);
            var message = string.Join(" ", result.Errors);

            // The declared path survives — the diagnostic stays actionable.
            Assert.Contains("missing/helper.csx", message, StringComparison.Ordinal);

            // The resolved absolute path does not appear, in whole or in part. Asserting on the
            // SUITE DIRECTORY rather than on the literal "resolved to" wording is deliberate:
            // a reworded message that still interpolated the absolute path would pass a
            // wording assertion and fail this one, which is the property that matters.
            Assert.DoesNotContain(suiteDirectory, message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(suiteDirectory, recursive: true);
        }
    }
}
