// Tests for S04-A-02: RunSuiteAsync, SuiteResult, verdict-aggregation helper.
//
// Non-docker tests cover:
//   • Verdict-aggregation precedence (EnvironmentError > Fail > Inconclusive > Pass).
//   • Empty-scenario suite returns Pass immediately.
//   • RunAsync still behaves correctly for schema-invalid input (regression: delegates
//     to the same early-exit path as before the S04-A-02 refactor).
//
// Docker-gated tests for the full Respawn reset-proof scenario are in
// Vouchfx.Engine.Orchestration.Tests/RespawnResetProofTests.cs.

using System.IO;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Non-docker unit tests for <see cref="ScenarioRunner.RunSuiteAsync"/> and the
/// verdict-aggregation helper (S04-A-02).
/// </summary>
public sealed class RunSuiteAsyncTests
{
    private static readonly System.Reflection.Assembly[] ProviderAssemblies =
        new[] { typeof(HttpRestProvider).Assembly };

    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";

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
            scenarios: Array.Empty<Vouchfx.Engine.Authoring.Ast.ScenarioAst>(),
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
        var doc = Vouchfx.Engine.Authoring.YamlDocumentParser.Parse("steps:\n  - id: s1\n    type: http.rest\n    target: x\n    method: GET\n    path: /\n    expect:\n      status: 200\n");
        var registry = Vouchfx.Sdk.StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
        var ast = Vouchfx.Engine.Authoring.AstBuilder.Build(doc, registry);

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

    /// <summary>
    /// <see cref="ScenarioRunner.RunSuiteAsync"/> throws <see cref="ArgumentException"/>
    /// when the optional <c>scenarioBaseDirectories</c> list (issue #268) is supplied but its
    /// length does not match <c>scenarios</c>. This guard fires immediately after the
    /// empty-list short-circuit and BEFORE the provider registry is built (let alone any
    /// topology) — see <c>RunSuiteAsync</c>'s arg-validation block — so it is reachable from a
    /// plain unit test with no Docker involved.
    /// </summary>
    [Fact]
    public async Task RunSuiteAsync_MismatchedScenarioBaseDirectoriesLength_ThrowsArgumentException()
    {
        var doc = Vouchfx.Engine.Authoring.YamlDocumentParser.Parse("steps:\n  - id: s1\n    type: http.rest\n    target: x\n    method: GET\n    path: /\n    expect:\n      status: 200\n");
        var registry = Vouchfx.Sdk.StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
        var ast = Vouchfx.Engine.Authoring.AstBuilder.Build(doc, registry);

        var sw = new StringWriter();

        // Local arrays avoid CA1861 (constant-element array arguments in repeated call-sites).
        var oneScenario = new[] { ast };
        var oneName = new[] { "s0" };
        var oneYaml = new[] { "yaml" };
        var twoBaseDirectories = new string?[] { "dir-a", "dir-b" }; // mismatch: 2 dirs for 1 scenario

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ScenarioRunner.RunSuiteAsync(
                scenarios: oneScenario,
                scenarioNames: oneName,
                yamlTexts: oneYaml,
                providerAssemblies: ProviderAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: sw,
                scenarioBaseDirectories: twoBaseDirectories));
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

    // ── RunSuiteAsync regression — env: authoring error classification (M2) ──────

    /// <summary>
    /// A service's <c>env:</c> reference to an unknown dependency (<c>${conn:typo}</c>) must be
    /// classified as <see cref="Verdict.Inconclusive"/> through the sequential
    /// <see cref="ScenarioRunner.RunSuiteAsync"/> path — NOT an unhandled exception, and NOT
    /// <see cref="Verdict.EnvironmentError"/>. No Docker is required: the shared topology's
    /// <c>EnvironmentMapper.Map</c> call throws before <c>HeadlessTopology.StartAsync</c> (and
    /// therefore DCP) is ever reached.
    /// </summary>
    /// <remarks>
    /// Regression guard (M2, MAJOR, code-review-gatekeeper): before this fix,
    /// <see cref="ScenarioRunner.RunSuiteAsync"/> caught only <c>OrchestrationException</c>
    /// around its shared-topology-build <c>SuiteTopology.StartAsync</c> call, so a Map()-time
    /// <see cref="System.ArgumentException"/> propagated as a raw, unhandled exception instead
    /// of a clean §12.1 verdict for every scenario in the suite.
    /// </remarks>
    [Fact]
    public async Task RunSuiteAsync_EnvConfigReferencesUnknownDependency_ReturnsInconclusive_NoTopology()
    {
        const string yaml = """
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  env:
                    FOO: "${conn:typo}"
            steps:
              - id: get-noop
                type: http.rest
                target: api
                method: GET
                path: /
                expect:
                  status: 200
            """;

        var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, registry);

        var sw = new StringWriter();

        // Local arrays avoid CA1861 (constant-element array arguments in repeated call-sites).
        var oneScenario = new[] { ast };
        var oneName = new[] { "env-config-typo" };
        var oneYaml = new[] { yaml };

        var result = await ScenarioRunner.RunSuiteAsync(
            scenarios: oneScenario,
            scenarioNames: oneName,
            yamlTexts: oneYaml,
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw);

        Assert.Equal(Verdict.Inconclusive, result.Verdict);
        Assert.NotEqual(Verdict.EnvironmentError, result.Verdict);
        var scenarioVerdict = Assert.Single(result.ScenarioVerdicts);
        Assert.Equal("env-config-typo", scenarioVerdict.ScenarioName);
        Assert.Equal(Verdict.Inconclusive, scenarioVerdict.Verdict);

        var rendered = sw.ToString();
        Assert.Contains("typo", rendered, StringComparison.Ordinal);
    }
}
