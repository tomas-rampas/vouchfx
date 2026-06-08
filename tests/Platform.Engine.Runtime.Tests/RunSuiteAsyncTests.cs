// Tests for S04-A-02: RunSuiteAsync, SuiteResult, verdict-aggregation helper.
//
// Non-docker tests cover:
//   • Verdict-aggregation precedence (EnvironmentError > Fail > Inconclusive > Pass).
//   • Empty-scenario suite returns Pass immediately.
//   • RunAsync still behaves correctly for schema-invalid input (regression: delegates
//     to the same early-exit path as before the S04-A-02 refactor).
//
// Docker-gated tests for the full Respawn reset-proof scenario are in
// Platform.Engine.Orchestration.Tests/RespawnResetProofTests.cs.

using System.IO;
using Platform.Engine.Abstractions;
using Platform.Engine.Runtime;
using Platform.Steps.HttpRest;
using Xunit;

namespace Platform.Engine.Runtime.Tests;

/// <summary>
/// Non-docker unit tests for <see cref="ScenarioRunner.RunSuiteAsync"/> and the
/// verdict-aggregation helper (S04-A-02).
/// </summary>
public sealed class RunSuiteAsyncTests
{
    private static readonly System.Reflection.Assembly[] ProviderAssemblies =
        new[] { typeof(HttpRestProvider).Assembly };

    private const string AppHostAssemblyName = "Platform.Engine.Runtime.Tests";

    // ── Verdict-aggregation precedence ────────────────────────────────────────

    /// <summary>
    /// The internal <c>Elevate</c> helper must implement the canonical precedence
    /// rule: <c>EnvironmentError &gt; Fail &gt; Inconclusive &gt; Pass</c>.
    /// </summary>
    [Theory]
    [InlineData(Verdict.Pass, Verdict.Pass, Verdict.Pass)]
    [InlineData(Verdict.Pass, Verdict.Inconclusive, Verdict.Inconclusive)]
    [InlineData(Verdict.Pass, Verdict.Fail, Verdict.Fail)]
    [InlineData(Verdict.Pass, Verdict.EnvironmentError, Verdict.EnvironmentError)]
    [InlineData(Verdict.Inconclusive, Verdict.Pass, Verdict.Inconclusive)]
    [InlineData(Verdict.Inconclusive, Verdict.Fail, Verdict.Fail)]
    [InlineData(Verdict.Inconclusive, Verdict.EnvironmentError, Verdict.EnvironmentError)]
    [InlineData(Verdict.Fail, Verdict.Inconclusive, Verdict.Fail)]
    [InlineData(Verdict.Fail, Verdict.EnvironmentError, Verdict.EnvironmentError)]
    [InlineData(Verdict.EnvironmentError, Verdict.Pass, Verdict.EnvironmentError)]
    [InlineData(Verdict.EnvironmentError, Verdict.Fail, Verdict.EnvironmentError)]
    [InlineData(Verdict.EnvironmentError, Verdict.Inconclusive, Verdict.EnvironmentError)]
    public void Elevate_ObeysPrecedenceRule(
        Verdict current,
        Verdict next,
        Verdict expected)
    {
        var result = ScenarioRunner.Elevate(current, next);

        Assert.Equal(expected, result);
    }

    /// <summary>
    /// Aggregating a list of fabricated per-scenario verdicts via repeated
    /// <c>Elevate</c> calls must produce the highest-precedence verdict from
    /// the list.
    /// </summary>
    [Fact]
    public void Elevate_AggregatingList_ReturnsHighestPrecedence()
    {
        // Simulate a suite with: Pass, Fail, Inconclusive, Pass
        // Expected aggregate: Fail (higher than Inconclusive, lower than EnvironmentError).
        var list = new[] { Verdict.Pass, Verdict.Fail, Verdict.Inconclusive, Verdict.Pass };
        var aggregate = Verdict.Pass;

        foreach (var v in list)
        {
            aggregate = ScenarioRunner.Elevate(aggregate, v);
        }

        Assert.Equal(Verdict.Fail, aggregate);
    }

    /// <summary>
    /// Aggregating a list that contains an EnvironmentError must produce
    /// EnvironmentError regardless of ordering.
    /// </summary>
    [Fact]
    public void Elevate_AggregatingList_EnvironmentErrorDominates()
    {
        var list = new[]
        {
            Verdict.Pass,
            Verdict.Fail,
            Verdict.EnvironmentError,
            Verdict.Inconclusive,
        };
        var aggregate = Verdict.Pass;

        foreach (var v in list)
        {
            aggregate = ScenarioRunner.Elevate(aggregate, v);
        }

        Assert.Equal(Verdict.EnvironmentError, aggregate);
    }

    // ── RunSuiteAsync — parameter validation ─────────────────────────────────

    /// <summary>
    /// <see cref="ScenarioRunner.RunSuiteAsync"/> with an empty scenario list
    /// returns a <see cref="SuiteResult"/> with <see cref="Verdict.Pass"/> and
    /// an empty per-scenario breakdown without starting any topology.
    /// </summary>
    [Fact]
    public async Task RunSuiteAsync_EmptyScenarioList_ReturnsPassImmediately()
    {
        var sw = new StringWriter();

        var result = await ScenarioRunner.RunSuiteAsync(
            scenarios: Array.Empty<Platform.Engine.Authoring.Ast.ScenarioAst>(),
            scenarioNames: Array.Empty<string>(),
            yamlTexts: Array.Empty<string>(),
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw);

        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.Empty(result.ScenarioVerdicts);
    }

    /// <summary>
    /// <see cref="ScenarioRunner.RunSuiteAsync"/> throws
    /// <see cref="ArgumentException"/> when the lengths of the three parallel
    /// lists differ.
    /// </summary>
    [Fact]
    public async Task RunSuiteAsync_MismatchedListLengths_ThrowsArgumentException()
    {
        var doc = Platform.Engine.Authoring.YamlDocumentParser.Parse("steps:\n  - id: s1\n    type: http.rest\n    target: x\n    method: GET\n    path: /\n    expect:\n      status: 200\n");
        var registry = Platform.Sdk.StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
        var ast = Platform.Engine.Authoring.AstBuilder.Build(doc, registry);

        var sw = new StringWriter();

        // Local arrays avoid CA1861 (constant-element array arguments in repeated call-sites).
        var oneScenario = new[] { ast };
        var oneYaml = new[] { "yaml" };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ScenarioRunner.RunSuiteAsync(
                scenarios: oneScenario,
                scenarioNames: Array.Empty<string>(), // mismatch: 0 names for 1 scenario
                yamlTexts: oneYaml,
                providerAssemblies: ProviderAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: sw));
    }

    // ── RunAsync regression — schema-invalid input ────────────────────────────

    /// <summary>
    /// <see cref="ScenarioRunner.RunAsync"/> still returns
    /// <see cref="Verdict.Inconclusive"/> for schema-invalid YAML after the
    /// S04-A-02 refactor that extracted <c>RunScenarioAgainstTopologyAsync</c>.
    /// This is a regression guard — the early-exit path must be unchanged.
    /// </summary>
    [Fact]
    public async Task RunAsync_AfterRefactor_SchemaInvalidDocument_StillReturnsInconclusive()
    {
        const string yaml = """
            steps:
              - type: http.rest
                method: GET
                path: /
                target: whoami
            """;

        var sw = new StringWriter();

        var verdict = await ScenarioRunner.RunAsync(
            yamlText: yaml,
            scenarioName: "regression-schema-invalid",
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw);

        Assert.Equal(Verdict.Inconclusive, verdict);
        Assert.False(string.IsNullOrWhiteSpace(sw.ToString()),
            "Expected non-empty output containing the validation error.");
    }

    /// <summary>
    /// <see cref="ScenarioRunner.RunAsync"/> still returns
    /// <see cref="Verdict.Inconclusive"/> when the YAML contains a RETRY step,
    /// proving the RETRY-rejection path survived the S04-A-02 refactor.
    /// </summary>
    [Fact]
    public async Task RunAsync_AfterRefactor_RetryStep_StillReturnsInconclusive()
    {
        const string yaml = """
            steps:
              - id: poll
                type: http.rest
                target: api
                method: GET
                path: /health
                verifyMode: RETRY
                expect:
                  status: 200
            """;

        var sw = new StringWriter();

        var verdict = await ScenarioRunner.RunAsync(
            yamlText: yaml,
            scenarioName: "regression-retry-rejection",
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw);

        Assert.Equal(Verdict.Inconclusive, verdict);
        var rendered = sw.ToString();
        Assert.Contains("RETRY", rendered, StringComparison.OrdinalIgnoreCase);
    }
}
