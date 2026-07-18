// #232 — IMMEDIATE step-timeout enforcement tests.
//
// The DSL documents `timeout` as an upper bound on the step; before #232 the
// assembler wired it only as the RETRY polling window.  These tests cover the
// WrapForImmediate cancellation scope:
//   • no declared timeout → token local aliases CancellationToken.None; the
//     provider block's outcome is untouched (behavioural parity)
//   • non-positive timeout → treated exactly like no timeout
//   • fast body under budget → outcome preserved verbatim
//   • body that ignores the token but completes past the budget → outcome
//     superseded by Inconclusive {"reason":"step-timeout",…,"supersededVerdict"}
//   • body that observes __stepCt_<safeId> → early cooperative cut classified
//     Inconclusive {"reason":"step-timeout"} well before the body's own duration,
//     and the NEXT step still executes (per-step isolation)
//   • RETRY wrapper exposes the same __stepCt_<safeId> alias inside the attempt
using System.Diagnostics;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// #232: unit tests for the IMMEDIATE per-step cancellation scope emitted by
/// <see cref="CsxAssembler"/> (step <c>timeout</c> as an enforced upper bound).
/// </summary>
public sealed class ImmediateTimeoutTests
{
    private static readonly string[] NoUsings = Array.Empty<string>();
    private static readonly string[] NoHelpers = Array.Empty<string>();

    /// <summary>
    /// Builds a fragment whose block writes a <see cref="StepOutcome"/> with
    /// <paramref name="verdict"/> after awaiting <paramref name="delayMs"/> without
    /// observing any token — the shape of an unwired provider body.
    /// </summary>
    private static CsxFragment TokenIgnorantFragment(string safeId, int delayMs, string verdict)
        => new(
            RequiredUsings: NoUsings,
            RequiredHelpers: NoHelpers,
            StatementBlock:
                "{\n" +
                $"    await System.Threading.Tasks.Task.Delay({delayMs});\n" +
                $"    Vars[\"{VarKeys.Outcome(safeId)}\"] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
                $"        Vouchfx.Engine.Abstractions.Verdict.{verdict}, {delayMs}L, null);\n" +
                "}");

    /// <summary>
    /// Builds a fragment whose block awaits a long delay THROUGH the step token —
    /// the shape of a provider wired to the #232 convention.
    /// </summary>
    private static CsxFragment TokenObservingFragment(string safeId, int delayMs)
        => new(
            RequiredUsings: NoUsings,
            RequiredHelpers: NoHelpers,
            StatementBlock:
                "{\n" +
                $"    await System.Threading.Tasks.Task.Delay({delayMs}, __stepCt_{safeId});\n" +
                $"    Vars[\"{VarKeys.Outcome(safeId)}\"] = new Vouchfx.Engine.Abstractions.StepOutcome(\n" +
                "        Vouchfx.Engine.Abstractions.Verdict.Pass, 0L, null);\n" +
                "}");

    private static async Task<Dictionary<string, object?>> AssembleAndRunAsync(
        params StepCompilePlan[] plans)
    {
        var assembled = CsxAssembler.Assemble(plans);
        Assert.DoesNotContain("using var", assembled.CsxSource, StringComparison.Ordinal);

        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource);
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, new ScriptGlobalVariables(vars));
        return vars;
    }

    // ── no timeout: parity ────────────────────────────────────────────────────

    [Fact]
    public async Task NoTimeout_EmitsNoneAliasAndPreservesOutcome()
    {
        var plan = new StepCompilePlan(
            "plain-step", TokenIgnorantFragment("plain_step", 10, "Pass"),
            Retry: false, TimeoutMs: null, PollIntervalMs: null);

        var assembled = CsxAssembler.Assemble(new[] { plan });

        // Token convention exists even without a timeout…
        Assert.Contains(
            "System.Threading.CancellationToken __stepCt_plain_step = System.Threading.CancellationToken.None;",
            assembled.CsxSource, StringComparison.Ordinal);
        // …the budget-governed convention local is false…
        Assert.Contains(
            "bool __stepBudgetGoverned_plain_step = false;",
            assembled.CsxSource, StringComparison.Ordinal);
        // …and no enforcement scaffolding is emitted.
        Assert.DoesNotContain("__stepCts_plain_step", assembled.CsxSource, StringComparison.Ordinal);
        Assert.DoesNotContain("step-timeout", assembled.CsxSource, StringComparison.Ordinal);

        var vars = await AssembleAndRunAsync(plan);
        var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome("plain_step")]);
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    [Fact]
    public async Task NonPositiveTimeout_TreatedAsNoTimeout()
    {
        var plan = new StepCompilePlan(
            "zero-step", TokenIgnorantFragment("zero_step", 10, "Pass"),
            Retry: false, TimeoutMs: 0, PollIntervalMs: null);

        var assembled = CsxAssembler.Assemble(new[] { plan });
        Assert.DoesNotContain("__stepCts_zero_step", assembled.CsxSource, StringComparison.Ordinal);

        var vars = await AssembleAndRunAsync(plan);
        var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome("zero_step")]);
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    // ── under budget: preserved ───────────────────────────────────────────────

    [Fact]
    public async Task FastBodyUnderBudget_OutcomePreserved()
    {
        var plan = new StepCompilePlan(
            "fast-step", TokenIgnorantFragment("fast_step", 10, "Fail"),
            Retry: false, TimeoutMs: 30_000, PollIntervalMs: null);

        var vars = await AssembleAndRunAsync(plan);

        var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome("fast_step")]);
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Null(outcome.Observation);
    }

    // ── late completion: superseded ───────────────────────────────────────────

    [Fact]
    public async Task LateCompletion_SupersededByStepTimeout()
    {
        var plan = new StepCompilePlan(
            "late-step", TokenIgnorantFragment("late_step", 400, "Pass"),
            Retry: false, TimeoutMs: 100, PollIntervalMs: null);

        var vars = await AssembleAndRunAsync(plan);

        var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome("late_step")]);
        Assert.Equal(Verdict.Inconclusive, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("\"reason\":\"step-timeout\"", outcome.Observation, StringComparison.Ordinal);
        Assert.Contains("\"timeoutMs\":100", outcome.Observation, StringComparison.Ordinal);
        Assert.Contains("\"supersededVerdict\":\"Pass\"", outcome.Observation, StringComparison.Ordinal);
    }

    // ── token observed: early cooperative cut ─────────────────────────────────

    [Fact]
    public async Task TokenObservingBody_EarlyCut_AndNextStepStillRuns()
    {
        var cut = new StepCompilePlan(
            "cut-step", TokenObservingFragment("cut_step", 30_000),
            Retry: false, TimeoutMs: 150, PollIntervalMs: null);
        var after = new StepCompilePlan(
            "after-step", TokenIgnorantFragment("after_step", 1, "Pass"),
            Retry: false, TimeoutMs: null, PollIntervalMs: null);

        var overall = Stopwatch.StartNew();
        var vars = await AssembleAndRunAsync(cut, after);
        overall.Stop();

        // The 30-second delay must have been cut early by the 150 ms budget.
        Assert.True(overall.ElapsedMilliseconds < 15_000,
            $"Expected an early cooperative cut; the whole run took {overall.ElapsedMilliseconds} ms.");

        var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome("cut_step")]);
        Assert.Equal(Verdict.Inconclusive, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("\"reason\":\"step-timeout\"", outcome.Observation, StringComparison.Ordinal);
        Assert.DoesNotContain("supersededVerdict", outcome.Observation, StringComparison.Ordinal);

        // Per-step isolation: the timeout of one step never blocks the next.
        var next = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome("after_step")]);
        Assert.Equal(Verdict.Pass, next.Verdict);
    }

    // ── RETRY: uniform token alias ────────────────────────────────────────────

    [Fact]
    public void RetryWrapper_ExposesStepTokenAlias()
    {
        var plan = new StepCompilePlan(
            "poll-step", TokenIgnorantFragment("poll_step", 1, "Pass"),
            Retry: true, TimeoutMs: 500, PollIntervalMs: 50);

        var assembled = CsxAssembler.Assemble(new[] { plan });

        Assert.Contains(
            "System.Threading.CancellationToken __stepCt_poll_step = __ct_poll_step;",
            assembled.CsxSource, StringComparison.Ordinal);
        // RETRY attempts keep the provider transport convention as the per-attempt bound.
        Assert.Contains(
            "bool __stepBudgetGoverned_poll_step = false;",
            assembled.CsxSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DeclaredTimeout_EmitsBudgetGovernedTrue()
    {
        var plan = new StepCompilePlan(
            "gov-step", TokenIgnorantFragment("gov_step", 1, "Pass"),
            Retry: false, TimeoutMs: 5_000, PollIntervalMs: null);

        var assembled = CsxAssembler.Assemble(new[] { plan });

        Assert.Contains(
            "bool __stepBudgetGoverned_gov_step = true;",
            assembled.CsxSource, StringComparison.Ordinal);
    }
}
