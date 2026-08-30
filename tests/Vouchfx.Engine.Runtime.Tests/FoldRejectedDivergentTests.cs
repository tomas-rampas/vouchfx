// Vouchfx.Engine.Runtime.Tests — the DECISION `ScenarioRunner.FoldRejectedDivergent` makes,
// tabled. No Docker.
//
// WHY THIS EXISTS AS ITS OWN FILE. The logic was inline in `RunSuiteAsync` and only its FOLDED
// VALUE was reachable from an end-to-end test, and only on the paths where the topology never came
// up — where everything raises anyway. So the gate that decides WHICH scenarios are folded was
// untested: inverting the equality condition left the whole suite green (peer-review MAJOR-1).
// Extracting the decision makes all four cells addressable without a container.
//
// THE FOUR CELLS, and what each is load-bearing for:
//   • rejected + divergent + declares security  → raises, carrying THAT SCENARIO'S OWN kind.
//     This is the fail-open the fold closes: the identity digest hashes the `endpoint:` selector's
//     TEXT and not its resolution, so a rejected scenario declaring the same block against a
//     different `httpPort:` shares the running sibling's identity and would be vouched for by a
//     probe that never tested it.
//   • rejected + EQUAL environment              → None. This is the cell that keeps #451's
//     improvement: the ordinary typo case must stay in the canonical union, where the suite's own
//     probe covers it, or the fix trades one wrong answer for another.
//   • rejected + divergent + declares nothing   → None. `SecurityAssurance.None` must stay the
//     fold's identity element rather than becoming a refusal looking for a declaration.
//   • schema-VALID                              → None, whatever its environment says: the
//     divergence guard already holds those against the baseline.

using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class FoldRejectedDivergentTests
{
    private static readonly System.Reflection.Assembly[] ProviderAssemblies =
        new[] { typeof(Vouchfx.Steps.HttpRest.HttpRestProvider).Assembly };

    /// <summary>The baseline: one secured service, and the environment the topology starts from.</summary>
    private const string BaselineSuite = """
        environment:
          services:
            api:
              image: myorg/api:1.0
              httpPort: 8080
              security:
                profile: tls
                endpoint: http
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
    /// <strong>The fail-open shape.</strong> A byte-identical <c>security:</c> block — so the same
    /// declared IDENTITY, since the digest hashes the selector text <c>http</c> and not the port it
    /// resolves to — on a service whose <c>httpPort:</c> differs. The environments are therefore
    /// NOT equal while the identities are, which is precisely the combination that let a sibling's
    /// probe vouch for mutual TLS on a port nothing tested.
    /// </summary>
    private static readonly string DivergentSecuredSuite =
        BaselineSuite.Replace("httpPort: 8080", "httpPort: 9999", StringComparison.Ordinal);

    /// <summary>The same divergence with no <c>security</c> block at all — the unsecured control.</summary>
    private const string DivergentUnsecuredSuite = """
        environment:
          services:
            api:
              image: myorg/api:1.0
              httpPort: 9999
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
    /// The four cells. <paramref name="secondSuite"/> is the scenario under test; the first
    /// scenario is always the schema-valid baseline at index 0.
    /// </summary>
    [Theory]

    // Cell 1 — rejected, divergent, declares security: raises, with its OWN recorded kind.
    [InlineData(false, true, SecurityAbortKind.AuthoringFault, true, SecurityAbortKind.AuthoringFault)]
    [InlineData(false, true, SecurityAbortKind.SecurityDeclarationRejected, true, SecurityAbortKind.SecurityDeclarationRejected)]

    // Cell 2 — rejected but its environment EQUALS the baseline's: nothing folded (#451's improvement).
    [InlineData(false, false, SecurityAbortKind.AuthoringFault, false, null)]

    // Cell 4 — schema-VALID: never folded, however divergent it is (the guard owns those).
    [InlineData(true, true, null, false, null)]
    public void FoldRejectedDivergent_TablesTheDecision(
        bool secondIsSchemaValid,
        bool secondDiverges,
        SecurityAbortKind? recordedKind,
        bool expectedRaises,
        SecurityAbortKind? expectedRefusal)
    {
        var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
        var secondYaml = secondDiverges ? DivergentSecuredSuite : BaselineSuite;

        var scenarios = new[]
        {
            AstBuilder.Build(YamlDocumentParser.Parse(BaselineSuite), registry),
            AstBuilder.Build(YamlDocumentParser.Parse(secondYaml), registry),
        };

        var folded = ScenarioRunner.FoldRejectedDivergent(
            scenarios,
            new[] { true, secondIsSchemaValid },
            new SecurityAbortKind?[] { null, recordedKind },
            baselineIndex: 0);

        Assert.Equal(expectedRaises, folded.Unconfirmed);
        Assert.Equal(expectedRefusal, folded.Refusal);

        if (!expectedRaises)
        {
            Assert.Empty(folded.Declared);
        }
        else
        {
            // The declaration folded is the REJECTED scenario's own, which is what pairs with its
            // own refusal — the whole point of folding a value rather than unioning a declaration.
            Assert.NotEmpty(folded.Declared);
        }
    }

    // Locals rather than inline array literals at the call sites below: CA1861 on a repeated
    // constant-element argument.
    private static readonly bool[] s_validThenRejected = { true, false };
    private static readonly SecurityAbortKind?[] s_noneThenAuthoringFault =
        { null, SecurityAbortKind.AuthoringFault };
    private static readonly bool[] s_onlyRejected = { false };
    private static readonly SecurityAbortKind?[] s_onlyAuthoringFault =
        { SecurityAbortKind.AuthoringFault };

    /// <summary>
    /// Cell 3 — rejected and divergent, but declaring NO <c>security</c> block: contributes
    /// nothing, so it cannot displace a sibling's evidence in the fold.
    /// </summary>
    /// <remarks>
    /// Its own row rather than a table cell because it needs a different second document (one with
    /// no <c>security</c> node at all), which the theory's two-fixture shape cannot express.
    /// </remarks>
    [Fact]
    public void FoldRejectedDivergent_RejectedDivergentButUnsecured_ContributesNothing()
    {
        var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);

        var scenarios = new[]
        {
            AstBuilder.Build(YamlDocumentParser.Parse(BaselineSuite), registry),
            AstBuilder.Build(YamlDocumentParser.Parse(DivergentUnsecuredSuite), registry),
        };

        var folded = ScenarioRunner.FoldRejectedDivergent(
            scenarios,
            s_validThenRejected,
            s_noneThenAuthoringFault,
            baselineIndex: 0);

        Assert.Same(SecurityAssurance.None, folded);
        Assert.False(folded.Unconfirmed);
    }

    /// <summary>
    /// No schema-valid scenario at all: there is no baseline and no topology, so the fold
    /// contributes nothing and the canonical union does the raising.
    /// </summary>
    [Fact]
    public void FoldRejectedDivergent_NoBaseline_ContributesNothing()
    {
        var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);

        var scenarios = new[]
        {
            AstBuilder.Build(YamlDocumentParser.Parse(BaselineSuite), registry),
        };

        var folded = ScenarioRunner.FoldRejectedDivergent(
            scenarios,
            s_onlyRejected,
            s_onlyAuthoringFault,
            baselineIndex: -1);

        Assert.Same(SecurityAssurance.None, folded);
    }

    /// <summary>
    /// <strong>The premise the fail-open rests on, asserted rather than assumed:</strong> the two
    /// documents' environments are NOT equal while the identities they declare ARE.
    /// </summary>
    /// <remarks>
    /// Without this the cell-1 rows would prove only that something was folded, not that the fold
    /// was NECESSARY — a digest that already distinguished the two would make the fold dead code
    /// and this whole file vacuous.
    /// </remarks>
    [Fact]
    public void TheDivergentSecuredSuite_SharesTheBaselinesIdentityDespiteADifferentPort()
    {
        var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
        var baseline = AstBuilder.Build(YamlDocumentParser.Parse(BaselineSuite), registry);
        var divergent = AstBuilder.Build(YamlDocumentParser.Parse(DivergentSecuredSuite), registry);

        var baselineIdentities = Vouchfx.Engine.Authoring.Model.SecuredTargets
            .Enumerate(baseline.Environment)
            .Select(Vouchfx.Engine.Authoring.Model.SecuredTargets.IdentityOf)
            .ToArray();
        var divergentIdentities = Vouchfx.Engine.Authoring.Model.SecuredTargets
            .Enumerate(divergent.Environment)
            .Select(Vouchfx.Engine.Authoring.Model.SecuredTargets.IdentityOf)
            .ToArray();

        Assert.NotEmpty(baselineIdentities);
        Assert.Equal(baselineIdentities, divergentIdentities);
    }
}
