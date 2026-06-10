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
using Platform.Steps.DbAssert.Postgres;
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

    // Provider assemblies including db-assert.postgres — used by the secret-validation
    // test that places a ${secret:…} reference in a db-assert expect.row value (the
    // registry must know the db-assert.postgres kind for the step to pass schema and
    // provider validation, so the secret-validation pass is what rejects it).
    private static readonly System.Reflection.Assembly[] HttpAndDbProviderAssemblies =
        new[]
        {
            typeof(HttpRestProvider).Assembly,
            typeof(DbAssertPostgresProvider).Assembly,
        };

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

    // ── Non-docker: secret-reference validation (S05-B-01) ──────────────────

    /// <summary>
    /// A step whose substitutable field references an unknown secret source must
    /// be rejected by the central secret-validation pass before the topology is
    /// started, returning <see cref="Verdict.Inconclusive"/> (the test never ran)
    /// with a message naming the unknown source.  No Docker is required.
    /// </summary>
    [Fact]
    public async Task RunAsync_UnknownSecretSource_ReturnsInconclusive_NoTopology()
    {
        const string yaml = """
            environment:
              services:
                whoami:
                  image: traefik/whoami
            steps:
              - id: get-with-secret
                type: http.rest
                target: whoami
                method: GET
                path: /search?key=${secret:vault/api-key}
                expect:
                  status: 200
            """;

        var sw = new StringWriter();

        var verdict = await ScenarioRunner.RunAsync(
            yamlText: yaml,
            scenarioName: "unknown-secret-source",
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw);

        Assert.Equal(Verdict.Inconclusive, verdict);

        var rendered = sw.ToString();
        Assert.Contains("get-with-secret", rendered, StringComparison.Ordinal);
        Assert.Contains("vault", rendered, StringComparison.Ordinal);
        // It must never be reported as a product defect.
        Assert.NotEqual(Verdict.Fail, verdict);
    }

    /// <summary>
    /// A step whose substitutable field contains a malformed secret sigil (missing
    /// the <c>/path</c> segment) must be rejected before the topology is started,
    /// returning <see cref="Verdict.Inconclusive"/>.  No Docker is required.
    /// </summary>
    [Fact]
    public async Task RunAsync_MalformedSecretReference_ReturnsInconclusive_NoTopology()
    {
        const string yaml = """
            environment:
              services:
                whoami:
                  image: traefik/whoami
            steps:
              - id: get-malformed-secret
                type: http.rest
                target: whoami
                method: GET
                path: /search?key=${secret:env}
                expect:
                  status: 200
            """;

        var sw = new StringWriter();

        var verdict = await ScenarioRunner.RunAsync(
            yamlText: yaml,
            scenarioName: "malformed-secret",
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw);

        Assert.Equal(Verdict.Inconclusive, verdict);

        var rendered = sw.ToString();
        Assert.Contains("get-malformed-secret", rendered, StringComparison.Ordinal);
        Assert.Contains("malformed", rendered, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A <c>db-assert.postgres</c> step whose <c>expect.row</c> value references an
    /// unknown secret source must be rejected by the central secret-validation pass
    /// before the topology is started, returning <see cref="Verdict.Inconclusive"/>.
    /// </summary>
    /// <remarks>
    /// Regression guard for the S05-B-01 SHOULD-FIX: <c>db-assert.postgres</c> wraps
    /// its <c>expect.row</c> values in <c>Substitute_Helpers.Resolve</c> at runtime, so
    /// a <c>${secret:…}</c> there is substituted at execution time.  Before this fix,
    /// <c>CollectSubstitutableTexts</c> under-collected (query + parameters only) and an
    /// unknown-source secret in an expect-row value reached execution un-caught.  The
    /// scenario is otherwise fully valid (declared <c>capdb</c> postgres dependency,
    /// non-empty query, a row assertion), so the secret-validation pass — not schema or
    /// provider validation — is what produces the <see cref="Verdict.Inconclusive"/>.
    /// No Docker is required.
    /// </remarks>
    [Fact]
    public async Task RunAsync_UnknownSecretSource_InDbAssertExpectRow_ReturnsInconclusive_NoTopology()
    {
        const string yaml = """
            environment:
              dependencies:
                capdb:
                  type: postgres
            steps:
              - id: assert-secret-row
                type: db-assert.postgres
                target: capdb
                query: "SELECT status FROM orders WHERE id = 1"
                expect:
                  rowCount: 1
                  row:
                    status: ${secret:vault/expected-status}
            """;

        var sw = new StringWriter();

        var verdict = await ScenarioRunner.RunAsync(
            yamlText: yaml,
            scenarioName: "unknown-secret-source-db-assert-expect",
            providerAssemblies: HttpAndDbProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw);

        Assert.Equal(Verdict.Inconclusive, verdict);

        var rendered = sw.ToString();
        Assert.Contains("assert-secret-row", rendered, StringComparison.Ordinal);
        Assert.Contains("vault", rendered, StringComparison.Ordinal);
        // It must never be reported as a product defect, and must be caught
        // pre-topology (no EnvironmentError from a failed container start).
        Assert.NotEqual(Verdict.Fail, verdict);
        Assert.NotEqual(Verdict.EnvironmentError, verdict);
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
