// Integration-test fixture for the WORKED-EXAMPLE provider echo.text (S10-F-01).
//
// This is the acceptance proof that an OUTSIDE CONTRIBUTOR can build a NON-Core provider
// against the frozen v1 Platform.Sdk contract and exercise it end to end, WITHOUT Docker,
// using ONLY the published Platform.Sdk.Testing harness:
//
//   1. The reflective StepKindRegistry discovers the example provider purely from its
//      [StepProvider] attribute (no hand-registration) — the contributor only has to
//      (a) add a project, (b) implement the v1 interfaces, (c) mark [StepProvider].
//   2. The provider's JsonSchemaFragment composes into the unified language schema, so a
//      .e2e.yaml using `echo.text` passes (or fails) schema validation as expected.
//   3. ProviderTestHarness.RunSingleStepAsync drives Bind -> Validate -> Emit ->
//      assemble -> compile-once -> run-isolated (the §5 memory model: compile-once,
//      isolate, unload — never CSharpScript.EvaluateAsync) and returns the verdict as
//      DATA (Pass / Fail / null-with-errors), never as an exception.
//
// All tests are non-docker: `echo.text` is a dependency-free step (no service, no managed
// dependency), so the harness drives the compile-and-run pipeline directly.
//
// BDD note: this fixture was written FIRST (red — EchoTextProvider did not yet exist),
// then the provider was implemented to make it pass (green).
using System;
using System.Threading;
using System.Threading.Tasks;
using Example.Steps.Echo;
using Platform.Engine.Abstractions;
using Platform.Sdk.Testing;
using Xunit;

namespace Example.Steps.Echo.Tests;

/// <summary>
/// End-to-end, non-docker integration fixture for the worked-example
/// <see cref="EchoTextProvider"/> (<c>echo.text</c>), driven entirely through the
/// published <see cref="ProviderTestHarness"/>.
/// </summary>
public sealed class EchoTextFixtureTests
{
    // The example provider assembly — the ONLY input the reflective registry needs.
    private static readonly System.Reflection.Assembly s_providerAssembly =
        typeof(EchoTextProvider).Assembly;

    // Every harness call is bounded by a caller-owned timeout, exactly as the published
    // README advises: a CPU-bound infinite loop in emitted code cannot be cooperatively
    // cancelled, so the caller guards against a provider hang.
    private static CancellationToken Bounded() =>
        new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    // ── PASS: text == expect ─────────────────────────────────────────────────────────

    /// <summary>
    /// The happy path: a single <c>echo.text</c> step whose <c>text</c> equals its
    /// <c>expect</c> runs end to end and yields <see cref="Verdict.Pass"/>, with the
    /// echoed text present in the observation and a non-negative duration.
    /// </summary>
    [Fact]
    public async Task EchoTextStep_TextEqualsExpect_RunsEndToEnd_Pass()
    {
        const string yaml = """
            metadata:
              name: echo-text-pass
              description: Worked-example provider — echoes text and asserts it equals a constant.
            steps:
              - id: say-echo
                type: echo.text
                text: "hello, contributor"
                expect: "hello, contributor"
            """;

        var result = await ProviderTestHarness.RunSingleStepAsync(
            yaml, s_providerAssembly, stepId: "say-echo", cancellationToken: Bounded());

        Assert.True(result.IsPass,
            $"Expected a Pass; got Verdict={result.Verdict?.ToString() ?? "null"}. " +
            $"Schema errors: [{string.Join("; ", result.SchemaErrors)}]. " +
            $"Validation errors: [{string.Join("; ", result.ValidationErrors)}].");
        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.Contains("hello, contributor", result.Observation ?? string.Empty, StringComparison.Ordinal);
        Assert.True(result.DurationMs >= 0);
        Assert.Empty(result.SchemaErrors);
        Assert.Empty(result.ValidationErrors);
    }

    // ── FAIL: text != expect ─────────────────────────────────────────────────────────

    /// <summary>
    /// The fail path: when the echoed text does not match the expectation the step yields
    /// <see cref="Verdict.Fail"/> (NOT an exception, NOT <see cref="Verdict.EnvironmentError"/>) —
    /// proving the assertion is real, so the Pass above is meaningful.
    /// </summary>
    [Fact]
    public async Task EchoTextStep_TextNotEqualExpect_RunsEndToEnd_Fail()
    {
        const string yaml = """
            steps:
              - id: say-echo
                type: echo.text
                text: "hello, contributor"
                expect: "goodbye, contributor"
            """;

        var result = await ProviderTestHarness.RunSingleStepAsync(
            yaml, s_providerAssembly, stepId: "say-echo", cancellationToken: Bounded());

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.NotEqual(Verdict.EnvironmentError, result.Verdict);
        Assert.False(result.IsPass);
    }

    // ── SCHEMA reject: a required field is missing ───────────────────────────────────

    /// <summary>
    /// A <c>.e2e.yaml</c> whose <c>echo.text</c> step omits the required <c>expect</c>
    /// field must fail schema validation against the composed schema — proving the
    /// provider's <c>SchemaFragment</c> is actually composed in and enforced.  The harness
    /// halts before running and returns <see cref="StepRunResult.Verdict"/>
    /// <see langword="null"/> with a populated <see cref="StepRunResult.SchemaErrors"/> —
    /// never an exception.
    /// </summary>
    [Fact]
    public async Task EchoTextStep_MissingRequiredField_SchemaRejected_VerdictNull()
    {
        const string yaml = """
            steps:
              - id: say-echo
                type: echo.text
                text: "hello, contributor"
            """;

        var result = await ProviderTestHarness.RunSingleStepAsync(
            yaml, s_providerAssembly, stepId: "say-echo", cancellationToken: Bounded());

        Assert.Null(result.Verdict);
        Assert.NotEmpty(result.SchemaErrors);
        Assert.Empty(result.ValidationErrors);
        Assert.False(result.IsPass);
    }

    // ── {placeholder} substitution (cross-step state via Vars) ───────────────────────

    /// <summary>
    /// Demonstrates that the <c>echo.text</c> provider wires <c>{placeholder}</c>
    /// substitution into its emitted block: the <c>text</c> field is resolved at runtime
    /// against the <c>Vars</c> global via the shared <c>Substitute_Helpers.Resolve</c>
    /// helper (the same mechanism the Core <c>http.rest</c> provider uses for its
    /// <c>path</c>), exactly as a real cross-step value would be threaded forward.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In the engine's production <c>ScenarioRunner</c> a <c>variables:</c> block entry is
    /// seeded into <c>Vars</c> before step 1, so <c>text: "{greeting}"</c> with
    /// <c>variables: {greeting: "hello, world"}</c> would resolve to <c>hello, world</c>.
    /// </para>
    /// <para>
    /// The published <see cref="ProviderTestHarness"/> single-step path, however, starts
    /// with an EMPTY <c>Vars</c> and does NOT seed the scenario's <c>variables:</c> block
    /// (see the FRICTION LOG, finding F1).  Under the substitution contract an absent
    /// placeholder key resolves to the EMPTY STRING — so here <c>{greeting}</c> resolves to
    /// <c>""</c>, and the step Passes against an empty <c>expect</c>.  This still proves the
    /// substitution helper is composed in and executes end to end through the harness; the
    /// non-empty-value path is exercised by the in-repo Docker fixtures where the engine
    /// seeds the variables block.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EchoTextStep_PlaceholderText_ResolvesAgainstVars_RunsEndToEnd()
    {
        // `text` carries a {greeting} placeholder.  The harness Vars start empty and the
        // variables: block is not seeded, so {greeting} resolves to the empty string — and
        // the step asserts the resolved text equals the (also-empty) expectation, Passing.
        const string yaml = """
            variables:
              greeting: "hello, world"
            steps:
              - id: say-echo
                type: echo.text
                text: "{greeting}"
                expect: ""
            """;

        var result = await ProviderTestHarness.RunSingleStepAsync(
            yaml, s_providerAssembly, stepId: "say-echo", cancellationToken: Bounded());

        Assert.True(result.IsPass,
            $"Expected a Pass (the {{greeting}} placeholder resolves to empty under the " +
            $"single-step harness, matching the empty expect); got " +
            $"Verdict={result.Verdict?.ToString() ?? "null"}. " +
            $"Schema errors: [{string.Join("; ", result.SchemaErrors)}]. " +
            $"Validation errors: [{string.Join("; ", result.ValidationErrors)}].");
        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.Empty(result.SchemaErrors);
        Assert.Empty(result.ValidationErrors);
    }
}
