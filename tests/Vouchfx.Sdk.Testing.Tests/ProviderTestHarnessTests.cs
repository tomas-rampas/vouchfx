// Vouchfx.Sdk.Testing.Tests — acceptance tests for ProviderTestHarness.
//
// These prove that an out-of-repo provider author can run a single-step .e2e.yaml END TO
// END through the published harness, driving an arbitrary provider (echo.console) whose
// model type the harness does not know at compile time.  Written RED first (the harness
// did not exist), then implemented to GREEN.
using System.Linq;
using System.Reflection;
using Harness.Steps.Echo;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk.Testing;
using Xunit;

namespace Vouchfx.Sdk.Testing.Tests;

/// <summary>
/// End-to-end acceptance tests for <see cref="ProviderTestHarness"/> driving the in-test
/// <c>echo.console</c> provider.
/// </summary>
public sealed class ProviderTestHarnessTests
{
    // The assembly that contains the echo.console provider — the only input the
    // reflective registry needs.  It is THIS test assembly.
    private static readonly Assembly s_providerAssembly =
        typeof(EchoConsoleProvider).Assembly;

    /// <summary>
    /// Pass path: a single <c>echo.console</c> step whose <c>message</c> equals its
    /// <c>expect</c> runs end to end and yields <see cref="Verdict.Pass"/>, with the
    /// emitted message present in the observation and a non-negative duration.
    /// </summary>
    [Fact]
    public async Task SingleStep_MessageEqualsExpect_RunsEndToEnd_Pass()
    {
        const string yaml = """
            metadata:
              name: echo-pass
            steps:
              - id: say-echo
                type: echo.console
                message: "hello, harness"
                expect: "hello, harness"
            """;

        var result = await ProviderTestHarness.RunSingleStepAsync(
            yaml, s_providerAssembly, stepId: "say-echo");

        Assert.True(result.IsPass,
            $"Expected a Pass; got Verdict={result.Verdict?.ToString() ?? "null"}. " +
            $"Schema errors: [{string.Join("; ", result.SchemaErrors)}]. " +
            $"Validation errors: [{string.Join("; ", result.ValidationErrors)}].");
        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.Contains("hello, harness", result.Observation ?? string.Empty, StringComparison.Ordinal);
        Assert.True(result.DurationMs >= 0);
        Assert.Empty(result.SchemaErrors);
        Assert.Empty(result.ValidationErrors);
    }

    /// <summary>
    /// Fail path: when <c>message</c> does not equal <c>expect</c> the step yields
    /// <see cref="Verdict.Fail"/> — NOT an exception and NOT
    /// <see cref="Verdict.EnvironmentError"/> — proving the assertion is real.
    /// </summary>
    [Fact]
    public async Task SingleStep_MessageNotEqualExpect_RunsEndToEnd_Fail()
    {
        const string yaml = """
            steps:
              - id: say-echo
                type: echo.console
                message: "hello, harness"
                expect: "goodbye, harness"
            """;

        var result = await ProviderTestHarness.RunSingleStepAsync(
            yaml, s_providerAssembly, stepId: "say-echo");

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.NotEqual(Verdict.EnvironmentError, result.Verdict);
        Assert.False(result.IsPass);
    }

    /// <summary>
    /// RETRY pass path: a <c>verifyMode: RETRY</c> step whose assertion is satisfied on the
    /// first attempt resolves to <see cref="Verdict.Pass"/> quickly — proving the harness
    /// routes RETRY steps through the engine-owned polling loop and that the loop short-
    /// circuits on a Pass rather than waiting out the window.
    /// </summary>
    [Fact]
    public async Task RetryStep_AssertionSatisfied_RunsThroughPollingLoop_Pass()
    {
        const string yaml = """
            steps:
              - id: say-echo
                type: echo.console
                message: "hello, harness"
                expect: "hello, harness"
                verifyMode: RETRY
                timeout: 2s
            """;

        var result = await ProviderTestHarness.RunSingleStepAsync(
            yaml, s_providerAssembly, stepId: "say-echo");

        Assert.True(result.IsPass,
            $"Expected a Pass; got Verdict={result.Verdict?.ToString() ?? "null"}. " +
            $"Schema errors: [{string.Join("; ", result.SchemaErrors)}]. " +
            $"Validation errors: [{string.Join("; ", result.ValidationErrors)}].");
        Assert.Equal(Verdict.Pass, result.Verdict);
    }

    /// <summary>
    /// RETRY timeout path (the RED→GREEN guard against the silent RETRY downgrade): a
    /// <c>verifyMode: RETRY</c> step whose assertion is NEVER satisfied polls until its
    /// short <c>timeout</c> elapses and then resolves to <see cref="Verdict.Inconclusive"/>
    /// — the engine's sustained-Fail → Inconclusive-on-timeout behaviour (§12.1), NOT
    /// <see cref="Verdict.Fail"/>.
    /// </summary>
    /// <remarks>
    /// Under the previous harness — which assembled via the legacy tuple overload that
    /// hard-codes <c>Retry: false</c> — this same YAML was run IMMEDIATE and returned
    /// <see cref="Verdict.Fail"/> on the single attempt.  Asserting
    /// <see cref="Verdict.Inconclusive"/> here is the proof that the silent downgrade is
    /// gone and that the engine's RETRY polling path is exercised end to end through the
    /// harness's compile/reference closure.
    /// </remarks>
    [Fact]
    public async Task RetryStep_AssertionNeverSatisfied_PollsThenTimesOut_Inconclusive()
    {
        const string yaml = """
            steps:
              - id: say-echo
                type: echo.console
                message: "hello, harness"
                expect: "goodbye, harness"
                verifyMode: RETRY
                timeout: 150ms
            """;

        var result = await ProviderTestHarness.RunSingleStepAsync(
            yaml, s_providerAssembly, stepId: "say-echo");

        Assert.Equal(Verdict.Inconclusive, result.Verdict);
        Assert.NotEqual(Verdict.Fail, result.Verdict);
        Assert.False(result.IsPass);
    }

    /// <summary>
    /// Schema-reject path: omitting the provider's required <c>message</c> field fails
    /// schema validation, so the harness halts before running and returns
    /// <see cref="StepRunResult.Verdict"/> <see langword="null"/> with a populated
    /// <see cref="StepRunResult.SchemaErrors"/> — never an exception.
    /// </summary>
    [Fact]
    public async Task SingleStep_MissingRequiredField_SchemaRejected_VerdictNull()
    {
        const string yaml = """
            steps:
              - id: say-echo
                type: echo.console
                expect: "hello, harness"
            """;

        var result = await ProviderTestHarness.RunSingleStepAsync(
            yaml, s_providerAssembly, stepId: "say-echo");

        Assert.Null(result.Verdict);
        Assert.NotEmpty(result.SchemaErrors);
        Assert.Empty(result.ValidationErrors);
        Assert.False(result.IsPass);
    }
}
