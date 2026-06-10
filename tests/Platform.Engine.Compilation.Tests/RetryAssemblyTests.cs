// S06 — RETRY assembly tests (verifyMode: RETRY, §7, §12.1, §14).
//
// Validates the RETRY-wrapping path of CsxAssembler: a StepCompilePlan with
// Retry == true must wrap the provider's StatementBlock in a local async attempt
// function and an `await RetryRunner.PollAsync(...)` call, so the engine owns the
// polling/backoff timeline (authors never write Thread.Sleep, §7).
//
// Covers:
//   • Wrap shape — the assembled source contains the RetryRunner.PollAsync call,
//     a per-step attempt local function, the attempts Vars key, and NO "using var".
//   • Compiles — the wrapped source compiles once via RoslynScriptCompiler.CompileOnce.
//   • Runs / passes on the Nth attempt — the loop re-invokes the block until it
//     reports Pass; the final outcome is Pass and the attempt timeline has 3 records.
//   • Never satisfied → Inconclusive — a block that always Fails resolves to
//     Inconclusive (timeout), never Fail (§12.1).
//   • Back-compat — the legacy tuple overload still produces an UNWRAPPED block.
using System.Collections.Generic;
using System.Threading.Tasks;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Retry;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Xunit;

namespace Platform.Engine.Compilation.Tests;

/// <summary>
/// S06: unit + end-to-end tests for the RETRY-wrapping path of
/// <see cref="CsxAssembler.Assemble(IReadOnlyList{StepCompilePlan})"/>.
/// </summary>
public sealed class RetryAssemblyTests
{
    // Hoisted to a static readonly field to satisfy CA1861 (no constant array
    // arguments in repeatedly-callable assertion helpers).
    private static readonly string[] PollStepIdsExpected = new[] { "poll-step" };

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a stub <see cref="CsxFragment"/> whose <see cref="CsxFragment.StatementBlock"/>
    /// is <paramref name="block"/> verbatim.  The block must be a single brace-enclosed
    /// statement block (it is spliced into the RETRY wrapper as-is).
    /// </summary>
    private static CsxFragment StubFragment(string block) =>
        new(
            RequiredUsings: System.Array.Empty<string>(),
            RequiredHelpers: System.Array.Empty<string>(),
            StatementBlock: block);

    // ── 1. Wrap shape ──────────────────────────────────────────────────────────

    /// <summary>
    /// A single <see cref="StepCompilePlan"/> with <c>Retry == true</c> must produce a
    /// source that calls <c>RetryRunner.PollAsync</c>, declares the per-step attempt
    /// local function <c>__attempt_poll_step</c>, references the attempts Vars key
    /// <c>__attempts::poll_step</c>, and contains no <c>using var</c>.
    /// </summary>
    [Fact]
    public void Assemble_RetryPlan_WrapShape_ContainsPollAsyncAndNoUsingVar()
    {
        var fragment = StubFragment("""
            {
                Vars["__outcome::poll_step"] = new Platform.Engine.Abstractions.StepOutcome(
                    Platform.Engine.Abstractions.Verdict.Pass, 1L, null);
            }
            """);

        var plans = new[]
        {
            new StepCompilePlan("poll-step", fragment, Retry: true, TimeoutMs: 5000, PollIntervalMs: 10),
        };

        var assembled = CsxAssembler.Assemble(plans);

        Assert.Contains("RetryRunner.PollAsync", assembled.CsxSource, System.StringComparison.Ordinal);
        Assert.Contains("__attempt_poll_step", assembled.CsxSource, System.StringComparison.Ordinal);
        Assert.Contains("__attempts::poll_step", assembled.CsxSource, System.StringComparison.Ordinal);
        Assert.DoesNotContain("using var", assembled.CsxSource, System.StringComparison.Ordinal);

        // StepIds round-trip (raw, un-sanitised, in order).
        Assert.Equal(PollStepIdsExpected, assembled.StepIds);
    }

    // ── 2. Compiles ────────────────────────────────────────────────────────────

    /// <summary>
    /// The RETRY-wrapped source must compile cleanly via
    /// <see cref="RoslynScriptCompiler.CompileOnce"/> — the wrapper's references to
    /// <c>RetryRunner</c>, <c>StepOutcome</c>, and <c>Verdict</c> all resolve from the
    /// already-referenced <c>Platform.Engine.Abstractions</c> assembly.  No Docker.
    /// </summary>
    [Fact]
    public void Assemble_RetryPlan_Compiles()
    {
        var fragment = StubFragment("""
            {
                Vars["__outcome::poll_step"] = new Platform.Engine.Abstractions.StepOutcome(
                    Platform.Engine.Abstractions.Verdict.Pass, 1L, null);
            }
            """);

        var plans = new[]
        {
            new StepCompilePlan("poll-step", fragment, Retry: true, TimeoutMs: 5000, PollIntervalMs: 10),
        };

        var assembled = CsxAssembler.Assemble(plans);

        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource);
        Assert.NotEmpty(compiled.Image);
    }

    // ── 2b. Compiles — provider block contains a raw string + nested braces ─────

    /// <summary>
    /// Regression for the verbatim-splice in <c>CsxAssembler.WrapForRetry</c>: a provider
    /// <see cref="CsxFragment.StatementBlock"/> that itself contains a C# 11 double-dollar
    /// raw string (<c>$$"""…"""</c> with a <c>{{…}}</c> interpolation hole and literal
    /// <c>{ }</c> braces) plus an inner <c>{ }</c> nested scope must survive the RETRY wrap
    /// unmangled and still compile via <see cref="RoslynScriptCompiler.CompileOnce"/>.  The
    /// wrapper is built by pure <see cref="System.Text.StringBuilder"/> concatenation rather
    /// than an outer interpolated raw string precisely so this stays brace-balanced.  No Docker.
    /// </summary>
    [Fact]
    public async Task Assemble_RetryPlan_WithRawStringAndNestedBraces_Compiles()
    {
        // The block embeds a $$"""…""" raw string (literal { } braces + a {{…}} hole) and an
        // inner { } scope, then writes a Pass outcome.  Authored inside a FOUR-quote """"…""""
        // test literal (so the inner three-quote $$"""…""" does not terminate it early); every
        // brace below is literal text spliced verbatim into the CSX.
        var fragment = StubFragment(""""
            {
                var __sw_poll_step = 1;
                { var inner = __sw_poll_step + 1; }
                var j = $$"""{ "k": {{__sw_poll_step}} }""";
                Vars["__outcome::poll_step"] = new Platform.Engine.Abstractions.StepOutcome(
                    Platform.Engine.Abstractions.Verdict.Pass, 0L, j);
            }
            """");

        var plans = new[]
        {
            new StepCompilePlan("poll-step", fragment, Retry: true, TimeoutMs: 5000, PollIntervalMs: 10),
        };

        var assembled = CsxAssembler.Assemble(plans);

        // It compiles — the spliced raw string + nested braces stayed brace-balanced.
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource);
        Assert.NotEmpty(compiled.Image);

        // And it runs to a Pass on the first attempt (the block always reports Pass).
        var vars = new Dictionary<string, object?>(System.StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome("poll_step");
        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    // ── 3. Runs / passes on the Nth attempt ────────────────────────────────────

    /// <summary>
    /// A block that fails until its third invocation must drive the RETRY loop to a
    /// final <see cref="Verdict.Pass"/>, accumulate three <see cref="AttemptRecord"/>s
    /// under <c>VarKeys.Attempts</c>, and leave the counter at three.
    /// </summary>
    [Fact]
    public async Task Assemble_RetryPlan_PassesOnThirdAttempt_RecordsThreeAttempts()
    {
        // The block reads/increments a counter and reports Pass only on attempt 3.
        var fragment = StubFragment("""
            {
                var __n = Vars.TryGetValue("n", out var __rv) ? (int)__rv! : 0;
                __n++;
                Vars["n"] = __n;
                Vars["__outcome::poll_step"] = new Platform.Engine.Abstractions.StepOutcome(
                    __n >= 3
                        ? Platform.Engine.Abstractions.Verdict.Pass
                        : Platform.Engine.Abstractions.Verdict.Fail,
                    0L,
                    null);
            }
            """);

        var plans = new[]
        {
            new StepCompilePlan("poll-step", fragment, Retry: true, TimeoutMs: 30_000, PollIntervalMs: 1),
        };

        var assembled = CsxAssembler.Assemble(plans);
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource);

        var vars = new Dictionary<string, object?>(System.StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        // Final outcome must be Pass.
        var outcomeKey = VarKeys.Outcome("poll_step");
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected outcome key '{outcomeKey}'. Actual keys: [{string.Join(", ", vars.Keys)}]");
        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.Pass, outcome.Verdict);

        // Attempt timeline must hold exactly three records.
        var attemptsKey = VarKeys.Attempts("poll_step");
        Assert.True(vars.ContainsKey(attemptsKey),
            $"Expected attempts key '{attemptsKey}'. Actual keys: [{string.Join(", ", vars.Keys)}]");
        var attempts = Assert.IsType<List<AttemptRecord>>(vars[attemptsKey]);
        Assert.Equal(3, attempts.Count);

        // The counter ended at three.
        Assert.Equal(3, vars["n"]);
    }

    // ── 4. Never satisfied → Inconclusive ──────────────────────────────────────

    /// <summary>
    /// A block that always reports <see cref="Verdict.Fail"/> must resolve, when the
    /// polling window elapses, to <see cref="Verdict.Inconclusive"/> — never
    /// <see cref="Verdict.Fail"/> (§12.1).
    /// </summary>
    [Fact]
    public async Task Assemble_RetryPlan_NeverSatisfied_ResolvesToInconclusive()
    {
        var fragment = StubFragment("""
            {
                Vars["__outcome::poll_step"] = new Platform.Engine.Abstractions.StepOutcome(
                    Platform.Engine.Abstractions.Verdict.Fail, 0L, null);
            }
            """);

        var plans = new[]
        {
            new StepCompilePlan("poll-step", fragment, Retry: true, TimeoutMs: 200, PollIntervalMs: 10),
        };

        var assembled = CsxAssembler.Assemble(plans);
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource);

        var vars = new Dictionary<string, object?>(System.StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome("poll_step");
        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.Inconclusive, outcome.Verdict);
    }

    // ── 5. Back-compat — tuple overload stays UNWRAPPED ─────────────────────────

    /// <summary>
    /// The legacy tuple overload must continue to emit an IMMEDIATE (unwrapped) block:
    /// the assembled source must NOT contain <c>RetryRunner</c>, and the block must be
    /// the bare StatementBlock — byte-identical to what a non-retry
    /// <see cref="StepCompilePlan"/> would produce.
    /// </summary>
    [Fact]
    public void Assemble_TupleOverload_ProducesUnwrappedImmediateBlock()
    {
        var fragment = StubFragment("{ Vars[\"immediate\"] = 1; }");

        var tupleAssembled = CsxAssembler.Assemble(
            new[] { ("poll-step", fragment) });

        // No RETRY scaffolding in the IMMEDIATE path.
        Assert.DoesNotContain("RetryRunner", tupleAssembled.CsxSource, System.StringComparison.Ordinal);

        // The tuple overload must delegate to a non-retry plan, so it equals the
        // explicit non-retry StepCompilePlan output exactly (shim equivalence).
        var planAssembled = CsxAssembler.Assemble(new[]
        {
            new StepCompilePlan("poll-step", fragment, Retry: false, TimeoutMs: null, PollIntervalMs: null),
        });

        Assert.Equal(planAssembled.CsxSource, tupleAssembled.CsxSource);

        // And the bare StatementBlock is present verbatim.
        Assert.Contains("{ Vars[\"immediate\"] = 1; }", tupleAssembled.CsxSource, System.StringComparison.Ordinal);
    }
}
