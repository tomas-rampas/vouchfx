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
using System.Net;
using System.Net.Sockets;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Model;
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

        // No documents were supplied, so this arm learns nothing and must SAY nothing: the
        // registry is never built and the assurance is the identity. This half is what keeps the
        // arm's answer to a document-carrying caller (below) from costing the ordinary caller
        // anything.
        Assert.Empty(result.Assurance.Declared);
        Assert.Null(result.Assurance.Refusal);
        Assert.False(result.Assurance.Unconfirmed);
    }

    /// <summary>
    /// <strong>The empty-scenario arm answers from the documents it was handed rather than
    /// discarding them</strong> (Copilot, PR #416). A secured unbuilt document beside NO scenario
    /// is exactly what it is beside one: a declaration that nothing ever confirmed.
    /// </summary>
    /// <remarks>
    /// Measured before the fix, on this same input: <c>Declared=[] Refusal=&lt;null&gt;
    /// Unconfirmed=False</c>, while <c>UnbuiltDocument.Assure</c> on the very same document
    /// reported <c>Declared=[legacy] Refusal=AuthoringFault Unconfirmed=True</c> — a public method
    /// silently dropping a parameter, which <c>ExitCodes.FromVerdict</c> would have mapped to 0.
    /// The verdict is deliberately unchanged: an unbuilt document contributes to the assurance and
    /// to nothing else on the populated path either.
    /// </remarks>
    [Fact]
    public async Task RunSuiteAsync_NoScenariosBesideASecuredUnbuiltDocument_AnswersFromTheDocument()
    {
        var sw = new StringWriter();
        var unbuilt = new[] { UnbuiltDocumentDeclaring(secured: true) };

        var result = await ScenarioRunner.RunSuiteAsync(
            scenarios: Array.Empty<Vouchfx.Engine.Authoring.Ast.ScenarioAst>(),
            scenarioNames: Array.Empty<string>(),
            yamlTexts: Array.Empty<string>(),
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw,
            unbuiltDocuments: unbuilt);

        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.Empty(result.ScenarioVerdicts);

        // A local rather than an inline array literal: CA1861 on a repeated constant argument.
        // Issue #415 retyped `Declared` from names to identities, so the same claim — this run
        // declared exactly `legacy` and nothing else — is now asserted through the Name projection.
        // Unchanged in strength: still an ordered equality against a one-element expectation.
        var expectedDeclared = new[] { "legacy" };
        Assert.Equal(expectedDeclared, result.Assurance.Declared.Select(identity => identity.Name));
        Assert.Equal(SecurityAbortKind.AuthoringFault, result.Assurance.Refusal);
        Assert.True(result.Assurance.Unconfirmed);
    }

    /// <summary>
    /// The control for the row above, and the one that stops the fix from becoming a second defect:
    /// an unbuilt document declaring NO <c>security</c> block still contributes nothing, so this
    /// arm cannot manufacture a refusal for a caller whose suite asserts nothing about security.
    /// </summary>
    [Fact]
    public async Task RunSuiteAsync_NoScenariosBesideAnUnsecuredUnbuiltDocument_ContributesNothing()
    {
        var sw = new StringWriter();
        var unbuilt = new[] { UnbuiltDocumentDeclaring(secured: false) };

        var result = await ScenarioRunner.RunSuiteAsync(
            scenarios: Array.Empty<Vouchfx.Engine.Authoring.Ast.ScenarioAst>(),
            scenarioNames: Array.Empty<string>(),
            yamlTexts: Array.Empty<string>(),
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw,
            unbuiltDocuments: unbuilt);

        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.Empty(result.Assurance.Declared);
        Assert.Null(result.Assurance.Refusal);
        Assert.False(result.Assurance.Unconfirmed);
    }

    /// <summary>
    /// <strong>EDGE-002: the no-scenarios arm pairs PER DOCUMENT rather than unioning.</strong> One
    /// unsecured unbuildable document beside one secured one: the arm raises, and the assurance it
    /// reports is the secured document's own — its declaration beside its own refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This arm used to build one <see cref="SecurityAssurance"/> whose <c>Declared</c> was a union
    /// over every document and then stamp each document's refusal onto it, so a refusal from one
    /// document sat beside a declaration from another. It answered correctly only because
    /// <c>UnbuiltDocument.Assure</c> contributes NO refusal for a document that declared nothing —
    /// a property of a different method, which the fold no longer has to rely on
    /// (declaration-confirmation-matching, REQ-001).
    /// </para>
    /// <para>
    /// Asserted in both document orders, because order-independence in <c>Unconfirmed</c> and
    /// <c>Refusal</c> is the invariant <c>SecurityAssurance.Worse</c>'s own remarks state.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunSuiteAsync_NoScenariosBesideTwoUnbuiltDocuments_PairsPerDocument(
        bool securedIsFirst)
    {
        var sw = new StringWriter();
        var unbuilt = securedIsFirst
            ? new[] { UnbuiltDocumentDeclaring(secured: true), UnbuiltDocumentDeclaring(secured: false) }
            : new[] { UnbuiltDocumentDeclaring(secured: false), UnbuiltDocumentDeclaring(secured: true) };

        var result = await ScenarioRunner.RunSuiteAsync(
            scenarios: Array.Empty<Vouchfx.Engine.Authoring.Ast.ScenarioAst>(),
            scenarioNames: Array.Empty<string>(),
            yamlTexts: Array.Empty<string>(),
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw,
            unbuiltDocuments: unbuilt);

        Assert.Equal(Verdict.Pass, result.Verdict);

        // A local rather than an inline array literal: CA1861 on a repeated constant argument.
        // Exactly one name, whichever order the documents arrived in: the unsecured document
        // declared nothing and contributed nothing, and the secured one's declaration is not merged
        // with anybody else's.
        var expectedDeclared = new[] { "legacy" };
        Assert.Equal(expectedDeclared, result.Assurance.Declared.Select(identity => identity.Name));
        Assert.Equal(SecurityAbortKind.AuthoringFault, result.Assurance.Refusal);
        Assert.True(result.Assurance.Unconfirmed);
    }

    /// <summary>
    /// <strong>The anchoring property behind a proposed optimisation, measured and FALSE</strong>
    /// (Copilot, PR #416): <c>UnbuiltDocument.Assure</c>'s <c>DocumentValidator.Validate</c> call
    /// may NOT be skipped for a document whose bound <c>Environment</c> is <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The ORIGINAL anchoring argument is now obsolete, and the guarantee is not.</strong>
    /// This row was written because the two halves of <c>Assure</c> read the document through
    /// DIFFERENT parsers whose key lookups disagreed: an explicitly tagged root key
    /// (<c>!!str environment:</c>) bound no environment for the walk while the schema still
    /// reported an error AT the declared block, so skipping validation on a null environment would
    /// have answered <c>SecurityAssurance.None</c> — exit 0 on a rejected security declaration,
    /// the hole issue #411 closed. #417 removed that disagreement at its source, so this input no
    /// longer demonstrates it.
    /// </para>
    /// <para>
    /// <strong>It still demonstrates the contract, for a reason that was always the stronger
    /// one:</strong> the walk seeing an <c>environment</c> block does not mean the walk can
    /// adjudicate the <c>security</c> node inside it. The schema door is the engine's only
    /// spelling of "this declaration is rejected", and <c>Assure</c> must therefore call it
    /// unconditionally — which is exactly what the two assertions at the foot of this test pin.
    /// Do not re-derive the skip from the walk's result under any phrasing.
    /// </para>
    /// <para>
    /// Retained rather than deleted BECAUSE its premise moved: a row whose setup no longer
    /// reproduces the original hazard, but whose guarantee is unchanged, is the row most likely to
    /// be quietly dropped in a later cleanup. The historical account above is why it stays.
    /// </para>
    /// <para>
    /// Superseded detail, kept for the record: a RepresentationModel key lookup compared a
    /// scalar's TAG as well as its value. An
    /// explicitly tagged root key (<c>!!str environment:</c>) therefore binds no environment for
    /// the walk while the schema still reports an error AT the declared block. Skipping the
    /// validation on a null environment would answer <c>SecurityAssurance.None</c> for this
    /// document: exit 0 on a rejected security declaration, the hole issue #411 closed.
    /// </para>
    /// <para>
    /// Reachability is asserted, not assumed: the document PARSES (so it is not one of the three
    /// classes that bind nothing) and <c>AstBuilder.Build</c> then refuses it, which is precisely
    /// the class the CLI hands to the runner as an <c>UnbuiltDocument</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void Assure_TaggedEnvironmentKeyBindingNoEnvironment_StillRecordsTheSchemaRefusal()
    {
        const string yaml =
            "!!str environment:\n"
            + "  services:\n"
            + "    api:\n"
            + "      image: myorg/api:1.0\n"
            + "      security:\n"
            + "        profile: mtls\n"
            + "steps:\n  - id: x\n    type: not-a-real-provider\n";

        var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
        var document = YamlDocumentParser.Parse(yaml);

        // THE WALK NOW SEES THE BLOCK, and that is #417's fix, not a regression in this row.
        // This assertion used to be `Assert.Null(document.Environment)` — it pinned the DIVERGENCE
        // as its premise: the RepresentationModel lookup compared a scalar's tag as well as its
        // value, so an explicitly tagged root key bound nothing for the walk while the schema's own
        // front-end saw the block. YamlDocumentParser.TryGetNode now compares keys by value, which
        // is what YAML means, so both front-ends agree and the premise is simply false.
        Assert.NotNull(document.Environment);

        // …and it is genuinely an unbuilt document — parsed, then refused by AstBuilder.
        Assert.ThrowsAny<Exception>(() => AstBuilder.Build(document, registry));

        var assurance = new UnbuiltDocument(yaml, document).Assure(registry);

        Assert.Equal(SecurityAbortKind.SecurityDeclarationRejected, assurance.Refusal);
        Assert.True(assurance.Unconfirmed);
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

    private static readonly string[] s_sharedEnvironmentScenarioNames = { "env-a", "env-b" };

    /// <summary>
    /// The THIRD instance of the same artefact gap, at the shared-<c>environment</c> divergence
    /// guard (peer-review MAJOR-1, fix round ten) — the seam the two fixes above left behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED RED FIRST on the branch as it stood: this shape exited 3 with
    /// <c>junit exists = False, html exists = False, events exists = False</c>. It is the worst
    /// spelling of the defect the sibling test above describes — a NON-ZERO exit beside an empty
    /// results directory — and it is the one seam of the three that produces
    /// <see cref="Verdict.EnvironmentError"/> rather than Inconclusive, so its scenarios map to
    /// JUnit's <c>&lt;error&gt;</c> primitive and its counts to <c>envError</c>.
    /// </para>
    /// <para>
    /// The guard runs ABOVE the per-scenario compilation loop, so the completion path is handed a
    /// scenario list synthesised from the parameters rather than one stamped onto compilations.
    /// Moving the guard below that loop would have shared the stamp — and changed which diagnostic
    /// an author sees first, which is a behaviour change this fix deliberately did not make.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RunSuiteAsync_SecuredSuiteWithDivergingEnvironments_WritesEveryRequestedReport()
    {
        const string first = """
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

        // Byte-identical but for the image tag — so the divergence is never the security block.
        var second = first.Replace("myorg/api:1.0", "myorg/api:2.0", StringComparison.Ordinal);

        var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
        var firstAst = AstBuilder.Build(YamlDocumentParser.Parse(first), registry);
        var secondAst = AstBuilder.Build(YamlDocumentParser.Parse(second), registry);

        var directory = Directory.CreateTempSubdirectory("vouchfx-env-divergence-reports-");
        try
        {
            var junitPath = Path.Combine(directory.FullName, "results.xml");
            var htmlPath = Path.Combine(directory.FullName, "report.html");
            var eventsPath = Path.Combine(directory.FullName, "events.jsonl");

            var sw = new StringWriter();
            var result = await ScenarioRunner.RunSuiteAsync(
                scenarios: new[] { firstAst, secondAst },
                scenarioNames: s_sharedEnvironmentScenarioNames,
                yamlTexts: new[] { first, second },
                providerAssemblies: ProviderAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: sw,
                htmlReportPath: htmlPath,
                junitReportPath: junitPath,
                eventsReportPath: eventsPath);

            Assert.True(
                File.Exists(junitPath),
                "The shared-environment divergence guard must write the requested JUnit report — "
                + "this seam exits non-zero for a secured suite.");
            Assert.True(File.Exists(htmlPath), "…and the requested HTML report.");
            Assert.True(File.Exists(eventsPath), "…and the requested events stream.");

            // Both scenarios present, both EnvironmentError — JUnit's <error> primitive, not
            // <skipped>, which is what makes CountsFor's non-Inconclusive arm a live path.
            var xml = File.ReadAllText(junitPath);
            Assert.Contains("tests=\"2\"", xml, StringComparison.Ordinal);
            Assert.Contains("errors=\"2\"", xml, StringComparison.Ordinal);
            Assert.Contains("<testcase name=\"env-a\"", xml, StringComparison.Ordinal);
            Assert.Contains("<testcase name=\"env-b\"", xml, StringComparison.Ordinal);
            Assert.Equal(
                2,
                CountOccurrences(xml, "<property name=\"vouchfx.verdict\" value=\"ENV_ERROR\"/>"));

            Assert.Equal(4, File.ReadAllLines(eventsPath).Length);

            // Verdict and assurance are UNCHANGED by the reroute — only the artefacts are new.
            Assert.Equal(Verdict.EnvironmentError, result.Verdict);
            Assert.True(result.Assurance.Unconfirmed);
            Assert.Equal(2, result.ScenarioVerdicts.Count);
            Assert.All(result.ScenarioVerdicts, entry => Assert.Equal(Verdict.EnvironmentError, entry.Verdict));

            // One print, not two: the divergence is one suite-level fact, and it is also stamped as
            // each scenario's own cause.
            var rendered = sw.ToString();
            var diagnostic = rendered
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .FirstOrDefault(line => line.StartsWith("RunSuiteAsync: scenario ", StringComparison.Ordinal))
                ?? string.Empty;
            Assert.NotEmpty(diagnostic);
            Assert.Equal(1, CountOccurrences(rendered, diagnostic));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static readonly string[] s_topologyFailureScenarioNames = { "secured-suite" };

    /// <summary>
    /// The FOURTH and last instance of the artefact gap (#407), at the
    /// <c>OrchestrationException</c> catch — the one seam the three fixes above left behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED RED FIRST: a suite whose topology fails to start printed the topology marker and
    /// returned a bare <see cref="SuiteResult"/>, so no <c>ScenarioStarted</c>/<c>Completed</c>
    /// events reached the stream and none of <c>--junit</c>/<c>--html</c>/<c>--events</c> was
    /// written. It mattered more here than at the other three seams once a secured suite began
    /// exiting 3 on this path: a red build beside an empty results directory reads as a broken
    /// runner rather than a real refusal, so the failure was correct but unattributable.
    /// </para>
    /// <para>
    /// Reaches a failed topology WITHOUT Docker by the same means as the rows below: the suite
    /// pins a host port this test process is holding, so EDGE-012's bind pre-flight throws an
    /// <c>OrchestrationException</c> of kind <c>Provision</c> inside <c>StartAsync</c> — after Map,
    /// before Aspire or DCP. The listener binds port 0, so the port is allocated rather than
    /// hard-coded and this row cannot collide with an ephemeral allocation (#377/#431).
    /// </para>
    /// <para>
    /// Verdict and exit code are deliberately NOT asserted as changed: the aggregate over N
    /// EnvironmentErrors is EnvironmentError, which is what the bare return already said. This
    /// pins the artefacts, and that the cause reaches them — #407's acceptance is explicit that a
    /// test asserting only the exit code would not cover it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RunSuiteAsync_TopologyFailsToStart_WritesEveryRequestedReport()
    {
        var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        var heldPort = ((IPEndPoint)squatter.LocalEndpoint).Port;

        var suiteDirectory = Directory.CreateTempSubdirectory("vouchfx-topology-failure-reports-");
        try
        {
            File.WriteAllText(Path.Combine(suiteDirectory.FullName, "client.pem"), "placeholder");
            File.WriteAllText(Path.Combine(suiteDirectory.FullName, "client.key"), "placeholder");

            var junitPath = Path.Combine(suiteDirectory.FullName, "results.xml");
            var htmlPath = Path.Combine(suiteDirectory.FullName, "report.html");
            var eventsPath = Path.Combine(suiteDirectory.FullName, "events.jsonl");

            var yaml = SecuredSuitePinning(heldPort);
            var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
            var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry);
            var scenarios = new[] { ast };
            var yamls = new[] { yaml };

            var sw = new StringWriter();
            var result = await ScenarioRunner.RunSuiteAsync(
                scenarios: scenarios,
                scenarioNames: s_topologyFailureScenarioNames,
                yamlTexts: yamls,
                providerAssemblies: ProviderAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: sw,
                htmlReportPath: htmlPath,
                junitReportPath: junitPath,
                eventsReportPath: eventsPath,
                seedBaseDirectory: suiteDirectory.FullName);

            Assert.True(
                File.Exists(junitPath),
                "A topology that fails to start must still write the requested JUnit report — "
                + "this seam exits non-zero for a secured suite.");
            Assert.True(File.Exists(htmlPath), "…and the requested HTML report.");
            Assert.True(File.Exists(eventsPath), "…and the requested events stream.");

            // The scenario is present and carries EnvironmentError — JUnit's <error> primitive.
            var xml = File.ReadAllText(junitPath);
            Assert.Contains("tests=\"1\"", xml, StringComparison.Ordinal);
            Assert.Contains("errors=\"1\"", xml, StringComparison.Ordinal);
            Assert.Contains("<testcase name=\"secured-suite\"", xml, StringComparison.Ordinal);

            // #407's acceptance, BOTH halves. The artefacts exist (above) and they NAME the
            // failure (here) — the second half arriving with #372, which added the optional
            // `message` field to ScenarioCompletedEvent and taught both renderers to read it.
            // Before that, no artefact channel carried a scenario-level message for ANY of the
            // four seams, so this row could only assert the terminal.
            var eventLines = File.ReadAllLines(eventsPath);
            Assert.Contains(eventLines, line => line.Contains("scenario-started", StringComparison.Ordinal));
            Assert.Contains(eventLines, line => line.Contains("scenario-completed", StringComparison.Ordinal));

            // The cause reaches the WRITTEN stream, which is the artefact a CI job archives.
            Assert.Contains(
                eventLines,
                line => line.Contains("scenario-completed", StringComparison.Ordinal)
                        && line.Contains(TopologyFailureMarker, StringComparison.Ordinal));

            // …and the JUnit report, which is what a publisher UI shows a maintainer. Parsed
            // rather than substring-matched: the renderer XML-escapes, so the document holds
            // `&apos;` where the marker has an apostrophe (this marker has none, but the
            // surrounding message can) and a raw substring assertion would be fragile.
            var errorMessage = System.Xml.Linq.XDocument.Load(junitPath)
                .Descendants("error")
                .Single()
                .Attribute("message")!
                .Value;
            Assert.Contains(TopologyFailureMarker, errorMessage, StringComparison.Ordinal);

            // Still on the terminal too, exactly once.
            Assert.Contains(TopologyFailureMarker, sw.ToString(), StringComparison.Ordinal);

            // Verdict and assurance are UNCHANGED by the reroute — only the artefacts are new.
            Assert.Equal(Verdict.EnvironmentError, result.Verdict);
            Assert.Single(result.ScenarioVerdicts);

            // One print, not two: the completion path is told the marker already reached the
            // terminal, so it emits no duplicate.
            var rendered = sw.ToString();
            Assert.Equal(1, CountOccurrences(rendered, TopologyFailureMarker));
        }
        finally
        {
            squatter.Stop();
            suiteDirectory.Delete(recursive: true);
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

    // ── The unbuilt-document contribution (issue #411), against a FAILED TOPOLOGY ─────────
    //
    // WHAT THESE TWO ROWS ARE FOR, AND WHY THEY LIVE HERE RATHER THAN AT THE CLI TIER.
    // `RunSuiteAsync` holds ONE suite-wide assurance whose `Declared` is a union over every
    // scenario AND every unbuilt document, so the refusal an unbuilt document contributes is
    // paired with a declaration that may belong to a DIFFERENT file. The only shape that can
    // measure whether that pairing is guarded is one where the sibling reaches a topology and
    // records no authoring fault of its own — every pre-topology door records `AuthoringFault`,
    // which would supply the refusal whatever these rows did. That is why
    // `SecurityAssuranceMatrixTests`' Row 09b cannot see it: its sibling's own step-secret fault
    // supplies the refusal, so its SEQUENTIAL arm passes with the contribution removed entirely.
    //
    // HOW THEY REACH A FAILED TOPOLOGY WITHOUT DOCKER. The suite pins a host port the test process
    // is holding, so EDGE-012's bind pre-flight (`SuiteTopology.EnsurePinnedHostPortsAreFree`)
    // throws an `OrchestrationException` of kind `Provision` — inside `StartAsync`, after Map, and
    // before Aspire or DCP is reached. `RunSuiteAsync` catches it, prints the topology marker below
    // and records `TopologyUnavailable`, which is the #390 shape exactly: on its own it raises
    // nothing and exits 0.
    //
    // The pair is a conjunction test, and both halves are needed: the unsecured row fails if the
    // contribution is unguarded (it was — measured 3 on this path against 0 under `--parallel 1`,
    // a divergence AND a silent override of #390), and the secured row fails if the contribution is
    // dropped (it would then leave `TopologyUnavailable`, which never raises).

    private const string TopologyFailureMarker = "RunSuiteAsync: topology failed to start";

    /// <summary>A suite whose one service is SECURED and pins <paramref name="hostPort"/>.</summary>
    private static string SecuredSuitePinning(int hostPort) => $$"""
        environment:
          services:
            api:
              image: myorg/api:1.0
              ports: ["{{hostPort}}:8443"]
              security:
                profile: mtls
                endpoint: 8443
                clientCert: ./client.pem
                clientKey: ./client.key
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
    /// A document that parsed and was then refused by <c>AstBuilder</c> — what
    /// <c>ScenarioDiscovery.RecoveredDocument</c> hands the runner, text and bound document
    /// together. The document is PARSED from that same text rather than constructed, so the pair is
    /// exactly what production supplies; and since <c>UnbuiltDocument</c> now projects its
    /// environment off that document rather than taking it as a second parameter, they cannot drift
    /// apart in ANY caller, not merely in this fixture.
    /// </summary>
    private static UnbuiltDocument UnbuiltDocumentDeclaring(bool secured)
    {
        var yaml =
            "environment:\n"
            + "  services:\n"
            + "    legacy:\n"
            + "      image: myorg/legacy:1.0\n"
            + (secured
                ? "      security:\n"
                    + "        profile: mtls\n"
                    + "        endpoint: 8443\n"
                    + "        clientCert: ./client.pem\n"
                    + "        clientKey: ./client.key\n"
                : string.Empty)
            + "steps:\n  - id: x\n    type: not-a-real-provider\n";

        return new UnbuiltDocument(yaml, YamlDocumentParser.Parse(yaml));
    }

    /// <summary>
    /// A document whose <c>security</c> node the SCHEMA rejects — the profile name written where the
    /// block belongs — so it binds no <c>SecuritySpec</c> and
    /// <see cref="SecurityAbortKind.SecurityDeclarationRejected"/> is what carries it. Its
    /// precedence outranks <see cref="SecurityAbortKind.AuthoringFault"/>, which is what makes it
    /// usable as the highest-precedence member of a mixed set of unbuilt documents.
    /// </summary>
    private static UnbuiltDocument UnbuiltDocumentWhoseSecurityNodeIsRejected()
    {
        const string yaml =
            "environment:\n"
            + "  services:\n"
            + "    broken:\n"
            + "      image: myorg/broken:1.0\n"
            + "      security: mtls\n"
            + "steps:\n  - id: x\n    type: not-a-real-provider\n";

        return new UnbuiltDocument(yaml, YamlDocumentParser.Parse(yaml));
    }

    /// <summary>
    /// Runs the secured, port-pinning suite with the supplied unbuilt documents beside it, holding
    /// that host port for the duration so the topology cannot start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes the documents themselves rather than a <c>bool</c> (declaration-confirmation-matching,
    /// EDGE-003): the mixed-document rows need two and three of them, and the per-document pairing
    /// is only observable with more than one in the list.
    /// </para>
    /// <para>
    /// <strong>THE TRAP <c>params</c> OPENS, named rather than fixed:</strong> a zero-argument call
    /// now compiles and means "no unbuilt documents", where the <c>bool</c> this replaced forced
    /// every call to state the case. Every caller today passes at least one — a call site that
    /// passes none is asserting something about the suite ALONE and almost certainly wants a
    /// different fixture.
    /// </para>
    /// </remarks>
    private static async Task<(SuiteResult Result, string Rendered)> RunAgainstAHeldPortAsync(
        params UnbuiltDocument[] unbuiltDocuments)
    {
        var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        var heldPort = ((IPEndPoint)squatter.LocalEndpoint).Port;

        var suiteDirectory = Directory.CreateTempSubdirectory("vouchfx-unbuilt-assurance").FullName;
        try
        {
            // The declared client material must EXIST for the security preflight to pass — this
            // row is about what happens at the topology, not at the preflight — but nothing reads
            // its contents on this path, because the run never gets past the bind check.
            File.WriteAllText(Path.Combine(suiteDirectory, "client.pem"), "placeholder");
            File.WriteAllText(Path.Combine(suiteDirectory, "client.key"), "placeholder");

            var yaml = SecuredSuitePinning(heldPort);
            var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
            var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), registry);
            var sw = new StringWriter();

            // Locals rather than inline arrays: CA1861 on a repeated constant-element argument.
            var scenarios = new[] { ast };
            var names = new[] { "secured-suite" };
            var yamls = new[] { yaml };

            var result = await ScenarioRunner.RunSuiteAsync(
                scenarios: scenarios,
                scenarioNames: names,
                yamlTexts: yamls,
                providerAssemblies: ProviderAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: sw,
                seedBaseDirectory: suiteDirectory,
                unbuiltDocuments: unbuiltDocuments);

            return (result, sw.ToString());
        }
        finally
        {
            squatter.Stop();
            Directory.Delete(suiteDirectory, recursive: true);
        }
    }

    /// <summary>
    /// <strong>The unsecured control, ACROSS documents — the row whose absence let a #390 override
    /// through.</strong> An unbuilt document that declares no <c>security</c> block contributes no
    /// refusal, so a secured sibling whose only fault is a topology that would not start still
    /// exits 0: <see cref="SecurityAbortKind.TopologyUnavailable"/> survives, and it never raises.
    /// </summary>
    /// <remarks>
    /// Unguarded, the contribution stamped <see cref="SecurityAbortKind.AuthoringFault"/> whatever
    /// the unbuilt document declared; that outranks <see cref="SecurityAbortKind.TopologyUnavailable"/>
    /// and pairs with the SIBLING's declaration, so this suite reported a security failure caused by
    /// a file that asserted nothing. Measured on the built CLI at the time: exit 3 under <c>run</c>
    /// against exit 0 under <c>run --parallel 1</c>, whose per-document fold cannot make that
    /// pairing.
    /// </remarks>
    [Fact]
    public async Task RunSuiteAsync_UnsecuredUnbuiltDocument_LeavesAFailedTopologyExitingZero()
    {
        var (result, rendered) = await RunAgainstAHeldPortAsync(
            UnbuiltDocumentDeclaring(secured: false));

        // The run reached the topology and failed there — not at a pre-topology door, which would
        // record an authoring fault of its own and make this row prove nothing.
        Assert.Contains(TopologyFailureMarker, rendered, StringComparison.Ordinal);
        Assert.Equal(Verdict.EnvironmentError, result.Verdict);

        // The sibling's declaration is in `Declared`; the unbuilt document's is not…
        // Issue #415 retyped `Declared` from names to identities, so both membership claims are now
        // made over the Name projection. The claim is unchanged — which NAMES this run declared —
        // and the ordinal comparer is retained on the projected sequence.
        var declaredNames = result.Assurance.Declared.Select(identity => identity.Name).ToArray();
        Assert.Contains("api", declaredNames, StringComparer.Ordinal);
        Assert.DoesNotContain("legacy", declaredNames, StringComparer.Ordinal);

        // …and #390's fence holds: the only refusal recorded is the topology's, which never raises.
        Assert.Equal(SecurityAbortKind.TopologyUnavailable, result.Assurance.Refusal);
        Assert.False(result.Assurance.Unconfirmed);
    }

    /// <summary>
    /// <strong>The other half: the contribution itself, pinned where nothing else supplies it.</strong>
    /// The same suite with a SECURED unbuilt document beside it records
    /// <see cref="SecurityAbortKind.AuthoringFault"/> — outranking the topology's own refusal — and
    /// raises, because that document's declaration was never confirmed by anything.
    /// </summary>
    /// <remarks>
    /// This is the row that fails if the unbuilt refusal is removed: with only
    /// <see cref="SecurityAbortKind.TopologyUnavailable"/> left, <c>Unconfirmed</c> goes false and
    /// the suite exits 0 with an unexercised <c>mtls</c> declaration in it. Row 09b of
    /// <c>SecurityAssuranceMatrixTests</c> looks like it covers this and does not: its sequential
    /// arm survives that removal on its sibling's own authoring fault.
    /// </remarks>
    [Fact]
    public async Task RunSuiteAsync_SecuredUnbuiltDocument_RaisesEvenWhenOnlyTheTopologyFailed()
    {
        var (result, rendered) = await RunAgainstAHeldPortAsync(
            UnbuiltDocumentDeclaring(secured: true));

        Assert.Contains(TopologyFailureMarker, rendered, StringComparison.Ordinal);
        Assert.Equal(Verdict.EnvironmentError, result.Verdict);

        // The unbuilt document's own declaration — and the ATTRIBUTION is corrected here rather than
        // left to read the same and mean something else (declaration-confirmation-matching, REQ-006).
        // This used to say "folded into the one canonical walk", i.e. that `Declared` was a UNION
        // across the scenarios and the unbuilt documents and `legacy` was one member of it. REQ-001
        // removed that union: each unbuilt document now contributes one WHOLE assurance folded by
        // `SecurityAssurance.Worse`, which selects the raising one entire, so the assurance this
        // suite reports IS the unbuilt document's own and `legacy` is its whole declaration. The
        // assertion is unchanged and still passes; only the reason it passes has moved.
        //
        // Issue #415 retyped `Declared` from names to identities; the claim here is about WHICH
        // target the unbuilt document contributed, so it is asserted over the Name projection with
        // the ordinal comparer retained.
        Assert.Contains(
            "legacy",
            result.Assurance.Declared.Select(identity => identity.Name),
            StringComparer.Ordinal);

        // …and the refusal it carries outranks the topology's, so the pair raises.
        Assert.Equal(SecurityAbortKind.AuthoringFault, result.Assurance.Refusal);
        Assert.True(result.Assurance.Unconfirmed);
    }

    /// <summary>
    /// <strong>EDGE-003: several unbuilt documents, mixed, on the sequential path — each declaration
    /// paired with its OWN refusal.</strong> An unsecured unbuildable file beside a secured one,
    /// beside a suite whose topology failed: the suite raises on the SECURED document's pairing, and
    /// the assurance it reports is that document's own value rather than a union.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exact-equality assertion on <c>Declared</c> is what makes this a per-document test rather
    /// than a "does it raise" test. Under the union this replaced, <c>Declared</c> held the
    /// SCENARIO's <c>api</c> as well, and the unsecured document's declaration (there is none) and
    /// the secured document's refusal were free to meet in one record. One name, exactly, is the
    /// mechanical form of "the pairing is per document".
    /// </para>
    /// <para>
    /// The unsecured document contributes <see cref="SecurityAssurance.None"/> — no declaration AND
    /// no refusal — so the non-pairing it is here to demonstrate is structural rather than a check
    /// that happens to pass: it has no refusal for a sibling's declaration to be paired with.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RunSuiteAsync_SeveralUnbuiltDocuments_PairEachDeclarationWithItsOwnRefusal()
    {
        var (result, rendered) = await RunAgainstAHeldPortAsync(
            UnbuiltDocumentDeclaring(secured: false),
            UnbuiltDocumentDeclaring(secured: true));

        Assert.Contains(TopologyFailureMarker, rendered, StringComparison.Ordinal);

        // A local rather than an inline array literal: CA1861 on a repeated constant argument.
        var expectedDeclared = new[] { "legacy" };
        Assert.Equal(expectedDeclared, result.Assurance.Declared.Select(identity => identity.Name));
        Assert.Equal(SecurityAbortKind.AuthoringFault, result.Assurance.Refusal);
        Assert.True(result.Assurance.Unconfirmed);
    }

    /// <summary>
    /// <strong>EDGE-003, the precedence half:</strong> with three unbuilt documents refused at three
    /// different doors, the one the suite records is the HIGHEST-precedence refusal —
    /// <see cref="SecurityAbortKind.SecurityDeclarationRejected"/> over
    /// <see cref="SecurityAbortKind.AuthoringFault"/> over the topology's own — and it is recorded
    /// whichever order the documents arrive in.
    /// </summary>
    /// <remarks>
    /// The rejected document binds no <c>SecuritySpec</c>, so its own <c>Declared</c> is empty and it
    /// raises unconditionally. That is why <c>Declared</c> is asserted EMPTY here: the winning
    /// document's whole assurance is what survives the fold, and a union would have left the secured
    /// sibling's <c>legacy</c> (and the scenario's <c>api</c>) sitting beside a refusal neither of
    /// them earned.
    /// </remarks>
    [Fact]
    public async Task RunSuiteAsync_SeveralUnbuiltDocuments_RecordTheHighestPrecedenceRefusal()
    {
        var (forward, _) = await RunAgainstAHeldPortAsync(
            UnbuiltDocumentDeclaring(secured: false),
            UnbuiltDocumentDeclaring(secured: true),
            UnbuiltDocumentWhoseSecurityNodeIsRejected());

        var (reversed, _) = await RunAgainstAHeldPortAsync(
            UnbuiltDocumentWhoseSecurityNodeIsRejected(),
            UnbuiltDocumentDeclaring(secured: true),
            UnbuiltDocumentDeclaring(secured: false));

        Assert.Equal(SecurityAbortKind.SecurityDeclarationRejected, forward.Assurance.Refusal);
        Assert.Equal(SecurityAbortKind.SecurityDeclarationRejected, reversed.Assurance.Refusal);
        Assert.True(forward.Assurance.Unconfirmed);
        Assert.True(reversed.Assurance.Unconfirmed);

        Assert.Empty(forward.Assurance.Declared);
        Assert.Empty(reversed.Assurance.Declared);
    }

    // ── The pre-topology doors that had no pin at all (T2 review, MAJOR) ─────────────────────
    //
    // `RunSuiteAsync` applies the unbuilt-document fold at EIGHT `SuiteResult` return sites. Five
    // were already pinned or need no pin:
    //
    //   • empty `scenarios`            — `RunSuiteAsync_NoScenariosBeside*` above.
    //   • every scenario early-verdict — `SecurityAssuranceMatrixTests.Row09b_*`, at the CLI tier.
    //   • `catch (OrchestrationException)` — the `RunAgainstAHeldPortAsync` rows above.
    //   • security base-directory divergence — NO pin needed, and deliberately none added: that
    //     guard fires only for a suite that ALREADY declares security, so the scenarios' own
    //     assurance raises there whatever the documents contribute, and the fold cannot change
    //     `Unconfirmed`.
    //   • normal completion — the ONE remaining gap. It needs a running topology, so it is Docker
    //     -gated work rather than something these rows can reach; stated here so a reader can tell
    //     the pins from the hole.
    //
    // The three below had NOTHING. Each is reached with UNSECURED scenarios, which is what makes
    // the row measure the wrap rather than the scenarios: with an empty `Declared`, the scenarios'
    // own assurance cannot raise at any of these doors, so if the `WithUnbuiltDocuments(...)` wrap
    // were dropped the suite would return `Unconfirmed = false` — exit 0 — while carrying a broken
    // secured file. That is precisely the false negative this series exists to close, and a suite
    // that declares security of its own cannot detect it.

    /// <summary>Which pre-topology door a row of the theory below drives.</summary>
    public enum PreTopologyDoor
    {
        /// <summary>Two scenarios whose <c>environment</c> blocks differ — the cheapest door.</summary>
        SharedEnvironmentDivergence,

        /// <summary>Two runnable scenarios splitting the HTTP and Kafka families over one target.</summary>
        ProtocolConflict,

        /// <summary><c>EnvironmentMapper.Map</c>'s eager <c>ArgumentException</c> (<c>${conn:typo}</c>).</summary>
        EnvironmentMapperArgumentFault,
    }

    /// <summary>
    /// The `mq-publish.kafka` provider is needed for the protocol-conflict row and harms no other:
    /// a registry is a set of step kinds, and no row below declares a Kafka step it does not mean.
    /// </summary>
    private static readonly System.Reflection.Assembly[] PreTopologyDoorProviderAssemblies =
        new[]
        {
            typeof(HttpRestProvider).Assembly,
            typeof(Vouchfx.Steps.MqPublish.Kafka.MqPublishKafkaProvider).Assembly,
        };

    /// <summary>The first of two scenarios whose <c>environment</c> blocks differ by one character.</summary>
    private const string DivergentEnvironmentFirst = """
        environment:
          services:
            api:
              image: myorg/api:1.0
        steps:
          - id: get-noop
            type: http.rest
            target: api
            method: GET
            path: /
            expect:
              status: 200
        """;

    /// <summary>The second — same steps, a different image tag, so the divergence guard fires.</summary>
    private const string DivergentEnvironmentSecond = """
        environment:
          services:
            api:
              image: myorg/api:2.0
        steps:
          - id: get-noop
            type: http.rest
            target: api
            method: GET
            path: /
            expect:
              status: 200
        """;

    // The environment the two protocol-conflict halves share BYTE-IDENTICALLY, as a suite requires
    // — otherwise the divergence door above fires first and this row would silently measure that
    // one instead. The `${conn:typo}` is belt and braces: if the conflict guard ever stopped
    // firing, the run would fail fast at `Map` rather than reaching Docker, and the
    // conflict-diagnostic assertion below would still fail the row.
    private const string ProtocolConflictEnvironment = """
        environment:
          services:
            broker:
              image: acme/broker:1
              ports: [9093]
              env:
                FOO: "${conn:typo}"
        """;

    private const string ProtocolConflictHttpHalf = ProtocolConflictEnvironment + """

        steps:
          - id: get
            type: http.rest
            target: broker
            method: GET
            path: /
            expect:
              status: 200
        """;

    private const string ProtocolConflictKafkaHalf = ProtocolConflictEnvironment + """

        steps:
          - id: publish
            type: mq-publish.kafka
            target: broker
            topic: orders
            payload: "{}"
        """;

    private const string DivergentEnvironmentMarker = "declares a different environment block";
    private const string ProtocolConflictMarker = "one endpoint value per target";

    /// <summary>
    /// <strong>Each of the three previously-unpinned pre-topology doors, driven with UNSECURED
    /// scenarios beside a SECURED unbuilt document, must raise.</strong> The suite's own scenarios
    /// declare nothing, so the only thing that can make <c>Unconfirmed</c> true is the unbuilt
    /// document's whole assurance arriving through <c>WithUnbuiltDocuments</c> at that door's
    /// return site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Row-to-site map, so a later reader can tell a pin from the gap:
    /// <see cref="PreTopologyDoor.SharedEnvironmentDivergence"/> pins the shared-<c>environment</c>
    /// divergence return; <see cref="PreTopologyDoor.ProtocolConflict"/> pins the
    /// protocol-conflict return; <see cref="PreTopologyDoor.EnvironmentMapperArgumentFault"/> pins
    /// the <c>catch (ArgumentException)</c> return. The success return remains unpinned and needs a
    /// container — see the block comment above.
    /// </para>
    /// <para>
    /// <strong>MEASURED RED, ONE SITE AT A TIME.</strong> Each site's
    /// <c>WithUnbuiltDocuments(...)</c> was removed in turn (the assurance passed bare), rebuilt,
    /// and the theory re-run. Every time, EXACTLY ONE row failed — the row named above for that
    /// site — with <c>Assert.Equal() Failure: Collections differ … Expected: ["legacy"] Actual:
    /// []</c>: the document's declaration gone, and with it the raise. The other two rows stayed
    /// green in each of the three runs, which is what makes each row a pin on ITS site rather than
    /// on the fold they share.
    /// </para>
    /// <para>
    /// Each row also asserts its OWN door's diagnostic, because three doors that all record
    /// <see cref="SecurityAbortKind.AuthoringFault"/> are indistinguishable from the assurance
    /// alone: without that, a row could drift onto a neighbouring door and still pass, pinning the
    /// same site three times.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(PreTopologyDoor.SharedEnvironmentDivergence, Verdict.EnvironmentError)]
    [InlineData(PreTopologyDoor.ProtocolConflict, Verdict.Inconclusive)]
    [InlineData(PreTopologyDoor.EnvironmentMapperArgumentFault, Verdict.Inconclusive)]
    public async Task RunSuiteAsync_UnsecuredScenariosBesideASecuredUnbuiltDocument_RaisesAtEveryPreTopologyDoor(
        PreTopologyDoor door, Verdict expectedVerdict)
    {
        var (yamls, names, doorMarker) = door switch
        {
            PreTopologyDoor.SharedEnvironmentDivergence => (
                new[] { DivergentEnvironmentFirst, DivergentEnvironmentSecond },
                new[] { "first", "second" },
                DivergentEnvironmentMarker),
            PreTopologyDoor.ProtocolConflict => (
                new[] { ProtocolConflictHttpHalf, ProtocolConflictKafkaHalf },
                new[] { "http-half", "kafka-half" },
                ProtocolConflictMarker),
            _ => (
                new[] { ValidScenario },
                new[] { "valid" },
                PreTopologyMarker),
        };

        var registry = StepKindRegistry.BuildAndFreeze(PreTopologyDoorProviderAssemblies);
        var scenarios = yamls
            .Select(y => AstBuilder.Build(YamlDocumentParser.Parse(y), registry))
            .ToArray();
        var sw = new StringWriter();
        var unbuilt = new[] { UnbuiltDocumentDeclaring(secured: true) };

        var result = await ScenarioRunner.RunSuiteAsync(
            scenarios: scenarios,
            scenarioNames: names,
            yamlTexts: yamls,
            providerAssemblies: PreTopologyDoorProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: sw,
            unbuiltDocuments: unbuilt);

        var rendered = sw.ToString();

        // This row reached ITS door: the diagnostic only that door prints is present…
        Assert.Contains(doorMarker, rendered, StringComparison.Ordinal);
        Assert.Equal(expectedVerdict, result.Verdict);

        // …and the two doors that return BEFORE the topology build did not reach it, which is the
        // other half of "not a neighbour's door" (the Map row's own marker IS the topology marker,
        // so for that row the assertion above already made the claim).
        if (door != PreTopologyDoor.EnvironmentMapperArgumentFault)
        {
            Assert.DoesNotContain(PreTopologyMarker, rendered, StringComparison.Ordinal);
        }

        // The whole point of the fixture: `legacy` is the UNBUILT document's declaration, and the
        // scenarios contributed none of their own — so an exact equality here says the value that
        // survived the fold is the document's own, arriving through this door's wrap.
        // A local rather than an inline array literal: CA1861 on a repeated constant argument.
        var expectedDeclared = new[] { "legacy" };
        Assert.Equal(expectedDeclared, result.Assurance.Declared.Select(identity => identity.Name));
        Assert.Equal(SecurityAbortKind.AuthoringFault, result.Assurance.Refusal);
        Assert.True(result.Assurance.Unconfirmed);
    }
}
