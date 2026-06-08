// Tests for ScenarioRunner — Sprint 3 integration spine.
//
// Test categories:
//   • Non-docker: validation early-return path (no topology started).
//   • Docker-gated: end-to-end capstone tests proving the full pipeline.
//
// Non-docker tests are distinguished by the absence of [Trait("requires","docker")].
// Docker tests carry [Trait("requires","docker")] and are excluded from the
// non-docker CI job: dotnet test --filter "requires!=docker"
//
// Run docker tests:
//   dotnet test tests/Platform.Engine.Runtime.Tests -c Release --filter "requires=docker"

using System.IO;
using Platform.Engine.Abstractions;
using Platform.Engine.Runtime;
using Platform.Steps.HttpRest;
using Xunit;
using Xunit.Abstractions;

namespace Platform.Engine.Runtime.Tests;

/// <summary>
/// Integration tests for <see cref="ScenarioRunner"/>.
/// </summary>
public sealed class ScenarioRunnerTests
{
    private readonly ITestOutputHelper _output;

    public ScenarioRunnerTests(ITestOutputHelper output) => _output = output;

    // The short name of this test assembly — the one whose AssemblyInfo carries dcpclipath.
    private const string AppHostAssemblyName = "Platform.Engine.Runtime.Tests";

    // Provider assemblies: the http.rest Core provider.
    private static readonly System.Reflection.Assembly[] ProviderAssemblies =
        new[] { typeof(HttpRestProvider).Assembly };

    // ── Non-docker: schema-validation early-return ────────────────────────────

    /// <summary>
    /// A document whose step is missing the required <c>id</c> field should
    /// fail schema validation before any topology is started, returning
    /// <see cref="Verdict.Inconclusive"/> and writing a located error message
    /// to the output.  No Docker is required.
    /// </summary>
    [Fact]
    public async Task RunAsync_SchemaInvalidDocument_ReturnsInconclusive_NoTopology()
    {
        // Arrange — steps section is present but the step is missing an 'id' field.
        const string yaml = """
            steps:
              - type: http.rest
                method: GET
                path: /
                target: whoami
            """;

        var sw = new StringWriter();

        // Act
        var verdict = await ScenarioRunner.RunAsync(
            yamlText: yaml,
            scenarioName: "validation-test",
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw);

        // Assert
        Assert.Equal(Verdict.Inconclusive, verdict);
        var rendered = sw.ToString();
        // The output must contain a located error message (line number or error text).
        Assert.False(string.IsNullOrWhiteSpace(rendered),
            "Expected non-empty output containing the validation error message.");
    }

    // ── Non-docker: RETRY rejection (M-2) ────────────────────────────────────

    /// <summary>
    /// A document that contains a step with <c>verifyMode: RETRY</c> must be
    /// rejected before the topology is started, returning
    /// <see cref="Verdict.Inconclusive"/> and a clear message explaining that
    /// RETRY is not yet supported.  No Docker is required.
    /// </summary>
    [Fact]
    public async Task RunAsync_RetryStep_ReturnsInconclusive_WithMessage_NoTopology()
    {
        const string yaml = """
            steps:
              - id: poll-health
                type: http.rest
                target: some-api
                method: GET
                path: /health
                verifyMode: RETRY
                expect:
                  status: 200
            """;

        var sw = new StringWriter();

        var verdict = await ScenarioRunner.RunAsync(
            yamlText: yaml,
            scenarioName: "retry-rejection-test",
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw);

        Assert.Equal(Verdict.Inconclusive, verdict);

        var rendered = sw.ToString();
        Assert.Contains("RETRY", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not yet supported", rendered, StringComparison.OrdinalIgnoreCase);
    }

    // ── Docker-gated capstone tests ───────────────────────────────────────────

    /// <summary>
    /// End-to-end capstone: one http.rest GET to traefik/whoami expecting HTTP 200
    /// → the runner should return <see cref="Verdict.Pass"/> and the rendered output
    /// should contain the step id and <c>PASS</c>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Capstone_HttpRestGetWhoami_Pass()
    {
        const string yaml = """
            environment:
              services:
                whoami:
                  image: traefik/whoami
            steps:
              - id: get-root
                type: http.rest
                target: whoami
                method: GET
                path: /
                expect:
                  status: 200
            """;

        var sw = new StringWriter();

        var verdict = await ScenarioRunner.RunAsync(
            yamlText: yaml,
            scenarioName: "whoami-pass",
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw,
            cancellationToken: new System.Threading.CancellationTokenSource(
                TimeSpan.FromMinutes(3)).Token);

        var rendered = sw.ToString();
        _output.WriteLine($"Verdict: {verdict}");
        _output.WriteLine($"Output:\n{rendered}");
        Assert.Equal(Verdict.Pass, verdict);
        Assert.Contains("get-root", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PASS", rendered, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// End-to-end capstone: one http.rest GET to traefik/whoami expecting HTTP 418
    /// (the server returns 200) → the runner should return <see cref="Verdict.Fail"/>
    /// and the rendered output should contain <c>FAIL</c>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Capstone_HttpRestGetWhoami_StatusMismatch_Fail()
    {
        const string yaml = """
            environment:
              services:
                whoami:
                  image: traefik/whoami
            steps:
              - id: get-root-418
                type: http.rest
                target: whoami
                method: GET
                path: /
                expect:
                  status: 418
            """;

        var sw = new StringWriter();

        var verdict = await ScenarioRunner.RunAsync(
            yamlText: yaml,
            scenarioName: "whoami-fail",
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw,
            cancellationToken: new System.Threading.CancellationTokenSource(
                TimeSpan.FromMinutes(3)).Token);

        var rendered = sw.ToString();
        _output.WriteLine($"Verdict: {verdict}");
        _output.WriteLine($"Output:\n{rendered}");
        Assert.Equal(Verdict.Fail, verdict);
        Assert.Contains("FAIL", rendered, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// End-to-end capstone: a service with a non-existent image → topology fails →
    /// the runner returns <see cref="Verdict.EnvironmentError"/>; the output contains
    /// an environment-error indication; the verdict is never <c>Fail</c>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Capstone_BadImage_EnvironmentError()
    {
        const string yaml = """
            environment:
              services:
                nope:
                  image: nonexistent.invalid/nope:latest
            steps:
              - id: call-nope
                type: http.rest
                target: nope
                method: GET
                path: /
                expect:
                  status: 200
            """;

        var sw = new StringWriter();

        var verdict = await ScenarioRunner.RunAsync(
            yamlText: yaml,
            scenarioName: "bad-image",
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw,
            cancellationToken: new System.Threading.CancellationTokenSource(
                TimeSpan.FromSeconds(90)).Token);

        var rendered = sw.ToString();
        _output.WriteLine($"Verdict: {verdict}");
        _output.WriteLine($"Output:\n{rendered}");
        Assert.Equal(Verdict.EnvironmentError, verdict);
        Assert.NotEqual(Verdict.Fail, verdict);
        // Output must contain some env-error indication — either from the
        // TerminalRenderer's "Environment error on" line or the ENV_ERROR token.
        var hasEnvError =
            rendered.Contains("Environment error", StringComparison.OrdinalIgnoreCase) ||
            rendered.Contains("ENV_ERROR", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasEnvError,
            $"Expected output to contain 'Environment error' or 'ENV_ERROR'. Actual output:\n{rendered}");
    }
}
