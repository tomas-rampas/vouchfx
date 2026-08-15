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

    // ── RunSuiteAsync — the pre-topology abort (REQ-004 acceptance, EDGE-010(a)) ──────────
    //
    // HOW THESE TWO TESTS MEASURE "was the topology build attempted", without Docker. Both suites
    // below declare `env: { FOO: "${conn:typo}" }` — a reference to a dependency that does not
    // exist. `EnvironmentMapper.Map` rejects it EAGERLY, inside SuiteTopology.StartAsync and long
    // before DCP or any container, and RunSuiteAsync catches that as the distinctive line
    // "RunSuiteAsync: environment configuration error". So that string is a Docker-free marker for
    // "control reached the topology build":
    //
    //   present  → the build was attempted
    //   absent   → the run returned before it
    //
    // Nothing else in either suite can emit it, and neither test can pass by accident: the
    // all-early suite asserts the marker's ABSENCE and the mixed suite asserts its PRESENCE, so a
    // guard that fired always, or never, fails one of them.

    private const string PreTopologyMarker = "RunSuiteAsync: environment configuration error";

    /// <summary>
    /// A suite in which EVERY scenario fails the security preflight must not build a topology:
    /// REQ-004's acceptance names "the pre-topology stage of <c>vouchfx run</c>" and EDGE-010(a)
    /// says the suite "never reaches topology build".
    /// </summary>
    /// <remarks>
    /// The defect this guards: the compilation loop recorded each failure and continued, and the
    /// topology was then built unconditionally. Measured before the fix, a suite with a missing
    /// <c>clientCert</c> started a container and reported a health-gate timeout 128 s later —
    /// burying the preflight message the engine had already computed, and turning the exit 4 that
    /// <c>vouchfx validate</c> produces in under 2 s into an exit 3.
    /// </remarks>
    [Fact]
    public async Task RunSuiteAsync_EverySecurityPreflightRejected_ReturnsBeforeTheTopologyBuild()
    {
        const string yaml = """
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  env:
                    FOO: "${conn:typo}"
                  security:
                    profile: mtls
                    endpoint: 8443
                    clientCert: ./certs/client.pem
                    clientKey: ./certs/client-key.pem
            steps:
              - id: get-noop
                type: http.rest
                target: api
                method: GET
                path: /
                expect:
                  status: 200
            """;

        var suiteDirectory = Directory.CreateTempSubdirectory("vouchfx-preflight-abort").FullName;
        try
        {
            var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
            var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry);
            var sw = new StringWriter();

            var scenarios = new[] { ast };
            var names = new[] { "missing-client-cert" };
            var yamls = new[] { yaml };

            var result = await ScenarioRunner.RunSuiteAsync(
                scenarios: scenarios,
                scenarioNames: names,
                yamlTexts: yamls,
                providerAssemblies: ProviderAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: sw,
                seedBaseDirectory: suiteDirectory);

            var rendered = sw.ToString();

            // The preflight fired and named the field and the resolved path (REQ-004).
            Assert.Contains("clientCert", rendered, StringComparison.Ordinal);
            Assert.Contains(suiteDirectory, rendered, StringComparison.Ordinal);

            // …and the topology build was never reached.
            Assert.DoesNotContain(PreTopologyMarker, rendered, StringComparison.Ordinal);

            Assert.Equal(Verdict.Inconclusive, result.Verdict);

            // REQ-018: Inconclusive + this flag is exit 4, which is what the documentation claims
            // and what `vouchfx validate` already produced for the same suite.
            Assert.True(result.Assurance.Unconfirmed);
        }
        finally
        {
            Directory.Delete(suiteDirectory, recursive: true);
        }
    }

    private const string MixedSuiteEnvironment = """
        environment:
          services:
            api:
              image: myorg/api:1.0
              env:
                FOO: "${conn:typo}"
        """;

    private const string BadSecretScenario = MixedSuiteEnvironment + """

        steps:
          - id: get-noop
            type: http.rest
            target: api
            method: GET
            path: /
            headers:
              Authorization: "Bearer ${secret:nosuchsource/token}"
            expect:
              status: 200
        """;

    private const string ValidScenario = MixedSuiteEnvironment + """

        steps:
          - id: get-noop
            type: http.rest
            target: api
            method: GET
            path: /
            expect:
              status: 200
        """;

    /// <summary>
    /// The guard's differential, both halves against the SAME scenarios: alone, a scenario with an
    /// early verdict stops the run before the topology build; paired with a valid scenario, the
    /// identical scenario does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mixed case is the one the prescribed fix was flagged as possibly breaking, and it is why
    /// the guard's condition is "EVERY scenario has an early verdict" rather than "any". Written as
    /// one theory over one pair of inputs so the two halves cannot drift apart: the only difference
    /// between the rows is whether a valid scenario is present.
    /// </para>
    /// <para>
    /// Note what the mixed row deliberately does NOT assert — that the early scenario's own message
    /// was printed. It is not, and that is pre-existing and unrelated: the per-scenario loop that
    /// prints early messages runs AFTER the topology build, so a suite whose build then fails
    /// returns from the catch before reaching it. Asserting it here would be asserting a behaviour
    /// this change neither has nor claims.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task RunSuiteAsync_TopologyBuildIsReachedOnlyWhenSomeScenarioCanRun(
        bool includeValidScenario, bool expectTopologyBuildAttempted)
    {
        var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
        var sw = new StringWriter();

        var yamls = includeValidScenario
            ? new[] { BadSecretScenario, ValidScenario }
            : new[] { BadSecretScenario };
        var names = includeValidScenario
            ? new[] { "bad-secret-reference", "valid" }
            : new[] { "bad-secret-reference" };
        var scenarios = yamls
            .Select(y => AstBuilder.Build(YamlDocumentParser.Parse(y), registry))
            .ToArray();

        var result = await ScenarioRunner.RunSuiteAsync(
            scenarios: scenarios,
            scenarioNames: names,
            yamlTexts: yamls,
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw);

        var rendered = sw.ToString();

        Assert.Equal(
            expectTopologyBuildAttempted,
            rendered.Contains(PreTopologyMarker, StringComparison.Ordinal));

        Assert.Equal(Verdict.Inconclusive, result.Verdict);
        Assert.Equal(yamls.Length, result.ScenarioVerdicts.Count);

        // No security is declared anywhere here, so REQ-018's carve-out must stay off on both rows.
        Assert.False(result.Assurance.Unconfirmed);
    }

    /// <summary>
    /// MAJOR-5, which shares MAJOR-4's root cause: a <c>profile</c> the language forbids on a
    /// target kind (REQ-021/REQ-022 — <c>mtls</c> on <c>redis</c>) must reach the author as THAT
    /// message under <c>vouchfx run</c>, not as REQ-005's probe complaining that the topology
    /// staged no reachable address — which pointed at endpoint publication for a declaration the
    /// language rejects outright.
    /// </summary>
    [Fact]
    public async Task RunSuiteAsync_ProfileNotWiredForTheTargetKind_ReportsTheRealCauseNotTheProbe()
    {
        // The step targets the (unsecured) service rather than the redis dependency: what is under
        // test is where the ILLEGAL DECLARATION surfaces, and this test project registers only the
        // http.rest provider.
        const string yaml = """
            environment:
              services:
                api:
                  image: myorg/api:1.0
              dependencies:
                cache:
                  type: redis
                  security:
                    profile: mtls
                    endpoint: 6380
                    clientCert: ./certs/client.pem
                    clientKey: ./certs/client-key.pem
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
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry);
        var sw = new StringWriter();

        var scenarios = new[] { ast };
        var names = new[] { "mtls-on-redis" };
        var yamls = new[] { yaml };

        var result = await ScenarioRunner.RunSuiteAsync(
            scenarios: scenarios,
            scenarioNames: names,
            yamlTexts: yamls,
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw);

        var rendered = sw.ToString();

        // The real cause reaches the author…
        Assert.Contains("redis", rendered, StringComparison.Ordinal);

        // …the probe is never reached, so its misleading endpoint-publication message cannot
        // appear, and no topology was built to reach it.
        Assert.DoesNotContain("staged no reachable address", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(PreTopologyMarker, rendered, StringComparison.Ordinal);

        Assert.Equal(Verdict.Inconclusive, result.Verdict);
        Assert.True(result.Assurance.Unconfirmed);
    }

    /// <summary>
    /// m5: a secured suite whose scenarios resolve their declared security paths against DIFFERENT
    /// directories is refused before the topology build, rather than silently probing with one
    /// scenario's material on behalf of another's steps.
    /// </summary>
    [Fact]
    public async Task RunSuiteAsync_SecuredSuiteWithDivergingScenarioDirectories_IsRefused()
    {
        const string yaml = """
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  security:
                    profile: tls
                    endpoint: 8443
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
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry);
        var sw = new StringWriter();

        var scenarios = new[] { ast, ast };
        var names = new[] { "in-dir-a", "in-dir-b" };
        var yamls = new[] { yaml, yaml };
        var directories = new string?[] { "dir-a", "dir-b" };

        var result = await ScenarioRunner.RunSuiteAsync(
            scenarios: scenarios,
            scenarioNames: names,
            yamlTexts: yamls,
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw,
            scenarioBaseDirectories: directories);

        var rendered = sw.ToString();
        Assert.Contains("resolves its declared security paths against a different directory", rendered, StringComparison.Ordinal);
        Assert.Equal(Verdict.Inconclusive, result.Verdict);
        Assert.True(result.Assurance.Unconfirmed);
    }

    // Hoisted to fields: CA1861 fires on an array of literal constants in an argument position,
    // regardless of how often the method is actually called — RunSuiteAsync is called ONCE below.
    // The rule's own message names a "repeatedly called" method and an earlier form of this comment
    // repeated that as if it described this test, which it does not (m6, gatekeeper, fix round
    // seven). MEASURED, same round: inlining these two back produced CA1861 at both argument
    // positions and, under TreatWarningsAsErrors, failed the build. The sibling
    // `new[] { ast, ast }` and `new[] { yaml, yaml }` arguments stay inline because neither trips
    // the rule in that same build — `ast` is a runtime-built local, and `yaml`, though a
    // `const string`, reaches the array as a REFERENCE rather than as a literal.
    private static readonly string[] s_divergingScenarioNames = { "in-dir-a", "in-dir-b" };
    private static readonly string?[] s_divergingScenarioDirectories = { "dir-a", "dir-b" };

    /// <summary>
    /// The ARTEFACT half of the divergence refusal (gatekeeper MAJOR-1 + security MINOR-1, fix
    /// round six): a CI job that asked for reports must get them from this seam too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this is not cosmetic, and why it is worse here than at the sibling seam.</strong>
    /// This guard records an authoring refusal on a suite that declares security, so the assurance reads UNCONFIRMED and the run exits NON-ZERO (REQ-018). Before
    /// this fix it returned a bare <see cref="SuiteResult"/>: no scenario events, no live pump, no
    /// terminal render and — the one that reaches CI — no
    /// <c>FileReportWriter.WriteFileReports</c>. A pipeline running
    /// <c>vouchfx run --junit results.xml --html report.html --events events.jsonl tests/</c>
    /// therefore went red beside an EMPTY results directory, with the JUnit publisher reporting
    /// "no test results" — the failure looks like a broken runner rather than a rejected suite.
    /// </para>
    /// <para>
    /// MEASURED RED FIRST: with the bare return in place, all three
    /// <see cref="File.Exists(string)"/> assertions below were <see langword="false"/>.
    /// </para>
    /// <para>
    /// The single-print assertion is the other half: routing this seam through the shared
    /// completion path must not make the divergence diagnostic appear twice, now that the same
    /// text is also stamped as each unjudged scenario's own cause.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RunSuiteAsync_SecuredSuiteWithDivergingScenarioDirectories_WritesEveryRequestedReport()
    {
        const string yaml = """
            environment:
              services:
                api:
                  image: myorg/api:1.0
                  security:
                    profile: tls
                    endpoint: 8443
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
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry);

        var directory = Directory.CreateTempSubdirectory("vouchfx-divergence-reports-");
        try
        {
            var junitPath = Path.Combine(directory.FullName, "results.xml");
            var htmlPath = Path.Combine(directory.FullName, "report.html");
            var eventsPath = Path.Combine(directory.FullName, "events.jsonl");

            var sw = new StringWriter();
            var result = await ScenarioRunner.RunSuiteAsync(
                scenarios: new[] { ast, ast },
                scenarioNames: s_divergingScenarioNames,
                yamlTexts: new[] { yaml, yaml },
                providerAssemblies: ProviderAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: sw,
                scenarioBaseDirectories: s_divergingScenarioDirectories,
                htmlReportPath: htmlPath,
                junitReportPath: junitPath,
                eventsReportPath: eventsPath);

            Assert.True(
                File.Exists(junitPath),
                "The divergence guard must write the requested JUnit report — the run exits "
                + "non-zero, so an empty results directory beside a red build is the worst shape.");
            Assert.True(File.Exists(htmlPath), "The divergence guard must write the requested HTML report.");
            Assert.True(File.Exists(eventsPath), "The divergence guard must write the requested events stream.");

            // Both scenarios present, both Inconclusive, tallies agreeing with the scenario count.
            var xml = File.ReadAllText(junitPath);
            Assert.Contains("tests=\"2\"", xml, StringComparison.Ordinal);
            Assert.Contains("skipped=\"2\"", xml, StringComparison.Ordinal);
            Assert.Contains("<testcase name=\"in-dir-a\"", xml, StringComparison.Ordinal);
            Assert.Contains("<testcase name=\"in-dir-b\"", xml, StringComparison.Ordinal);

            // The events file carries the started/completed pair for each scenario.
            var eventLines = File.ReadAllLines(eventsPath);
            Assert.Equal(4, eventLines.Length);

            // Classification unchanged by the reroute: still Inconclusive, still a security
            // -confirmation failure (this guard IS about security material).
            Assert.Equal(Verdict.Inconclusive, result.Verdict);
            Assert.True(result.Assurance.Unconfirmed);
            Assert.Equal(2, result.ScenarioVerdicts.Count);

            // One print, not two — the diagnostic is one suite-level fact.
            var rendered = sw.ToString();
            var diagnostic = rendered
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .FirstOrDefault(line => line.StartsWith("RunSuiteAsync: this suite declares", StringComparison.Ordinal))
                ?? string.Empty;
            Assert.NotEmpty(diagnostic);
            Assert.Equal(1, CountOccurrences(rendered, diagnostic));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Counts non-overlapping occurrences of <paramref name="needle"/>.</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        Assert.NotEmpty(needle);

        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    // ── REQ-018's schema door: which instance locations ARE a security declaration ─────────
    //
    // The classifier that decides this ran as `InstanceLocation.Contains("/security")` for one fix
    // round, and every test that existed at the time passed under BOTH that form and the correct
    // one — the security-shaped rows all used pointers like `/environment/dependencies/cache/
    // security`, which a substring test and a segment test agree on. Nothing pinned the NARROWNESS,
    // so the over-match shipped: a service merely NAMED `security-gateway`, declaring no security
    // at all, took REQ-018's carve-out and exited 4.
    //
    // The two tests below are that missing pin, at the two levels the defect had:
    //   • the pointer predicate itself, over every shape it must classify (including the ones that
    //     separate the two implementations — a row that passes under either is not evidence);
    //   • the end-to-end differential through RunSuiteAsync, one theory over a pair of suites
    //     differing ONLY in a service name, proving the carve-out stays off for both.

    /// <summary>
    /// The classification table for <c>LocatesADeclaredSecurityBlock</c>: every pointer shape the
    /// language can put in front of it, with the expected verdict and where the input came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first eleven rows are the <c>expected-instance-location</c> headers of all eleven
    /// <c>Corpus/Rejected/security-*.e2e.yaml</c> fixtures, quoted verbatim. Those headers are not
    /// aspirational: <c>SchemaRejectedCorpusTests.RejectedDocument_IsInvalidAtExpectedLocation</c>
    /// asserts each one equals an <c>InstanceLocation</c> the validator actually produces, so this
    /// table is measured input rather than an invented shape. Two header values recur across two
    /// fixtures each — kept as separate rows, with the source fixture as the first argument, so the
    /// table reads as a census of the corpus rather than a deduplicated set.
    /// </para>
    /// <para>
    /// The remaining rows are the ones that DISCRIMINATE, and each is named for what it pins. Rows
    /// marked "(substring form: true)" are the exact inputs the old implementation got wrong; a
    /// table without them cannot fail when the narrowness regresses.
    /// </para>
    /// </remarks>
    [Theory]
    // ── The eleven pinned corpus locations: all INSIDE a declared security block ──
    [InlineData("security-clientcert-under-tls", "/environment/services/app/security/clientCert", true)]
    [InlineData("security-missing-endpoint", "/environment/dependencies/events-kafka/security", true)]
    [InlineData("security-mtls-missing-clientcert", "/environment/dependencies/orders-mq/security", true)]
    [InlineData("security-mtls-missing-clientkey", "/environment/dependencies/events-kafka/security", true)]
    [InlineData("security-mtls-non-kafka-dependency", "/environment/dependencies/cache/security", true)]
    [InlineData("security-profile-wrong-case", "/environment/services/app/security/profile", true)]
    [InlineData("security-serverartifacts-contents-instead-of-source", "/environment/dependencies/events-kafka/security/serverArtifacts/0", true)]
    [InlineData("security-serverartifacts-target-not-absolute", "/environment/dependencies/events-kafka/security/serverArtifacts/0/target", true)]
    [InlineData("security-serverartifacts-unknown-key", "/environment/dependencies/events-kafka/security/serverArtifacts/0/bogus", true)]
    [InlineData("security-tls-non-kafka-dependency", "/environment/dependencies/cache/security", true)]
    [InlineData("security-unknown-key", "/environment/dependencies/events-kafka/security/bogus", true)]
    // ── The reported defect: a NAME that merely starts with "security" (substring form: true) ──
    [InlineData("name-prefixed-security-gateway", "/environment/services/security-gateway/bogus", false)]
    // ── A service named EXACTLY `security`: the name alone is not a declaration… ──
    [InlineData("name-exactly-security", "/environment/services/security/bogus", false)]
    // ── …but the same service's own security block still is (substring form: true, correctly) ──
    [InlineData("name-exactly-security-with-a-block", "/environment/services/security/security/profile", true)]
    // ── An env var named `security` is not a security declaration (substring form: true) ──
    [InlineData("env-var-named-security", "/environment/services/app/env/security", false)]
    // ── An EMPTY owner name. `environment.services` is an open object with no `propertyNames`
    //    constraint, so `"": { … }` is schema-legal; measured, a service named `""` with an
    //    unknown key in its security block reports at EXACTLY this pointer. Dropping empty
    //    segments (as TryGetStepScope's RemoveEmptyEntries would) shifts every index left and
    //    misses a REAL block — a false negative, the direction REQ-018 forbids ──
    [InlineData("owner-named-empty-string", "/environment/services//security/bogus", true)]
    // ── An owner name containing '/', which arrives RFC 6901-escaped as `~1` — the escaping
    //    slice C's DocumentValidator fix turned on. Measured: a service named `a/b` reports at
    //    `/environment/services/a~1b/security/bogus`, one segment for the name, and a service
    //    named `c~d` at `c~0d`. So splitting the RAW pointer is correct and decoding first
    //    would not be ──
    [InlineData("owner-name-containing-a-slash", "/environment/services/a~1b/security", true)]
    // ── Shallower / unrelated surfaces ──
    [InlineData("root-pointer", "", false)]
    [InlineData("owner-itself-not-its-security", "/environment/services/app", false)]
    [InlineData("a-step-not-the-environment", "/steps/0/security", false)]
    public void LocatesADeclaredSecurityBlock_ClassifiesEveryPointerShape(
        string source, string instanceLocation, bool expected)
    {
        Assert.Equal(
            expected,
            ScenarioRunner.LocatesADeclaredSecurityBlock(instanceLocation));

        // `source` is carried purely so each row is traceable to the fixture or defect it came
        // from (and so two fixtures sharing one pinned location stay two distinct test cases).
        Assert.NotEmpty(source);
    }

    /// <summary>
    /// The end-to-end mirror: a schema-invalid suite that declares NO security anywhere must leave
    /// REQ-018's carve-out off — whatever its services happen to be called.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One theory over one pair of inputs differing ONLY in the service name, so the two halves
    /// cannot drift apart. Both rows carry the identical ordinary <c>additionalProperties</c>
    /// violation (an unknown <c>bogus</c> key on the service), which is an authoring error like any
    /// other: Inconclusive, and exit 0 by default. Under the substring form the
    /// <c>security-gateway</c> row set the flag and the <c>gateway</c> row did not — the same
    /// document, the same defect, a different exit code decided by an unrelated naming choice.
    /// </para>
    /// <para>
    /// The rendered assertion is load-bearing: without it the test would still pass if the suite
    /// failed for some unrelated reason before ever reaching the schema door, which is precisely
    /// how a mirror stops mirroring anything.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("security-gateway")]
    [InlineData("gateway")]
    public async Task RunSuiteAsync_SchemaInvalidSuiteDeclaringNoSecurity_LeavesTheCarveOutOff(
        string serviceName)
    {
        var yaml = $"""
            environment:
              services:
                {serviceName}:
                  image: myorg/api:1.0
                  bogus: nope
            steps:
              - id: get-noop
                type: http.rest
                target: {serviceName}
                method: GET
                path: /
                expect:
                  status: 200
            """;

        var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry);
        var sw = new StringWriter();

        var scenarios = new[] { ast };
        var names = new[] { "no-security-declared" };
        var yamls = new[] { yaml };

        var result = await ScenarioRunner.RunSuiteAsync(
            scenarios: scenarios,
            scenarioNames: names,
            yamlTexts: yamls,
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw);

        // The schema door is the one that opened: the unknown key was reported on THIS service.
        var rendered = sw.ToString();
        Assert.Contains("bogus", rendered, StringComparison.Ordinal);
        Assert.Contains(serviceName, rendered, StringComparison.Ordinal);

        Assert.Equal(Verdict.Inconclusive, result.Verdict);

        // REQ-018's mechanism clause: an ordinary authoring error keeps the ordinary mapping.
        Assert.False(result.Assurance.Unconfirmed);
    }
}
