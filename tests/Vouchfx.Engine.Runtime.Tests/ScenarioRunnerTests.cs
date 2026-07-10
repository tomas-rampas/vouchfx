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
//   dotnet test tests/Vouchfx.Engine.Runtime.Tests -c Release --filter "requires=docker"

using System.IO;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Runtime;
using Vouchfx.Steps.DbAssert.Postgres;
using Vouchfx.Steps.HttpRest;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Integration tests for <see cref="ScenarioRunner"/>.
/// </summary>
public sealed class ScenarioRunnerTests
{
    private readonly ITestOutputHelper _output;

    public ScenarioRunnerTests(ITestOutputHelper output) => _output = output;

    // The short name of this test assembly — the one whose AssemblyInfo carries dcpclipath.
    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";

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

    // ── Non-docker: no-render core seam (S07-C-03 foundation) ────────────────

    /// <summary>
    /// The no-render core <see cref="ScenarioRunner.RunScenarioOwningTopologyAsync"/>
    /// must, for a schema-invalid scenario, return the verdict together with the
    /// fully-populated event buffer <strong>without rendering it</strong> — proving the
    /// seam that a future Sprint 8 parallel runner relies on: each scenario produces its
    /// own complete buffer, and the caller owns the single render.
    /// </summary>
    /// <remarks>
    /// A schema-invalid input short-circuits before any topology is started, so this
    /// exercises the core's early-exit path without Docker.  The returned buffer must be
    /// non-empty and contain the <c>scenario-completed</c> event line; the verdict is
    /// <see cref="Verdict.Inconclusive"/> (the scenario never ran).
    /// </remarks>
    [Fact]
    public async Task RunScenarioOwningTopologyAsync_SchemaInvalid_ReturnsBufferWithoutRendering()
    {
        // Arrange — step missing the required 'id' field (same short-circuit input as
        // the RunAsync schema-invalid test), so the core never reaches the topology.
        const string yaml = """
            steps:
              - type: http.rest
                method: GET
                path: /
                target: whoami
            """;

        var registry = Vouchfx.Sdk.StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
        var sw = new StringWriter();

        // Act — call the no-render core directly.
        var (verdict, buffer) = await ScenarioRunner.RunScenarioOwningTopologyAsync(
            registry: registry,
            yamlText: yaml,
            scenarioName: "core-seam-schema-invalid",
            appHostAssemblyName: AppHostAssemblyName,
            output: sw,
            seedBaseDirectory: null,
            cancellationToken: default);

        // Assert — the core returns the verdict + the complete event buffer.
        Assert.Equal(Verdict.Inconclusive, verdict);
        Assert.NotEmpty(buffer);

        // The buffer carries the structured event stream; the scenario-completed event
        // proves the core populated the buffer up to the terminal event.
        Assert.Contains(
            buffer,
            line => line.Contains("scenario-completed", StringComparison.Ordinal));

        // The buffer is the UN-rendered event stream: its lines are raw JSON, not the
        // human-readable text the TerminalRenderer would produce ("Scenario '…' started").
        // Confirming the core did not render: no buffer line is a rendered scenario line.
        Assert.DoesNotContain(
            buffer,
            line => line.Contains("Scenario 'core-seam-schema-invalid' started", StringComparison.Ordinal));
    }

    // ── Non-docker: secret-reference validation (S05-B-01) ──────────────────

    /// <summary>
    /// A step whose substitutable field references an unknown secret source must
    /// be rejected by the central secret-validation pass before the topology is
    /// started, returning <see cref="Verdict.Inconclusive"/> (the test never ran)
    /// with a message naming the unknown source.  No Docker is required.
    /// </summary>
    /// <remarks>
    /// Uses <c>awssecrets</c> — a source the MVP engine does NOT know — to exercise the
    /// rejection path.  The MVP sources are <c>env</c> (S05) and <c>vault</c> (S08), so a
    /// reference to either of those would now validate and proceed; this test must name a
    /// genuinely-unknown source to assert the pre-topology rejection.
    /// </remarks>
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
                path: /search?key=${secret:awssecrets/api-key}
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
        Assert.Contains("awssecrets", rendered, StringComparison.Ordinal);
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
    /// A service's <c>env:</c> reference to an unknown dependency (<c>${conn:typo}</c>) must be
    /// classified as <see cref="Verdict.Inconclusive"/> through the sequential/single-scenario
    /// <see cref="ScenarioRunner.RunAsync"/> path — NOT an unhandled exception escaping to the
    /// caller, and NOT <see cref="Verdict.EnvironmentError"/> (an authoring typo is not an
    /// infrastructure fault; conflating the two could drive infra-retry/alerting for a mistake
    /// that will never self-heal). No Docker is required: <c>EnvironmentMapper.Map</c> throws
    /// before <c>HeadlessTopology.StartAsync</c> (and therefore DCP) is ever reached.
    /// </summary>
    /// <remarks>
    /// Regression guard (M2, MAJOR, code-review-gatekeeper): before this fix,
    /// <c>SuiteTopology.StartAsync</c> called <c>EnvironmentMapper.Map</c> outside any
    /// try/catch, and <see cref="ScenarioRunner.RunAsync"/> caught only
    /// <c>OrchestrationException</c> around the topology-start call — so a Map()-time
    /// <see cref="ArgumentException"/> (malformed <c>env:</c> reference, unknown dependency,
    /// secret-in-env, unsupported <c>.part</c>, etc.) propagated as a raw, unhandled exception
    /// instead of a clean §12.1 verdict. The fix classifies it the SAME way as the
    /// schema-invalid / parse-AST / pipeline-compile / secret-reference failures already
    /// handled above in this file: Inconclusive, with the helpful message text preserved in
    /// the rendered output.
    /// </remarks>
    [Fact]
    public async Task RunAsync_EnvConfigReferencesUnknownDependency_ReturnsInconclusive_NoTopology()
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

        var sw = new StringWriter();

        var verdict = await ScenarioRunner.RunAsync(
            yamlText: yaml,
            scenarioName: "env-config-unknown-dependency",
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw);

        Assert.Equal(Verdict.Inconclusive, verdict);
        Assert.NotEqual(Verdict.EnvironmentError, verdict);
        Assert.NotEqual(Verdict.Fail, verdict);

        var rendered = sw.ToString();
        Assert.Contains("typo", rendered, StringComparison.Ordinal);
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
    /// <para>
    /// Uses <c>awssecrets</c> — a source the MVP engine does NOT know (the MVP sources are
    /// <c>env</c> and <c>vault</c>) — so the rejection is driven by the unknown source, not
    /// by any since-added source becoming known.
    /// </para>
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
                    status: ${secret:awssecrets/expected-status}
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
        Assert.Contains("awssecrets", rendered, StringComparison.Ordinal);
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
