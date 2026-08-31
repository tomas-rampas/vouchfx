// The security-confirmation drills that need NO container, split out of
// KafkaSecurityConfirmationDrillDockerTests.
//
// WHY THEY LIVE IN THEIR OWN CLASS (issue #378)
// ─────────────────────────────────────────────
// The drill class joined the "vouchfx-cli-drill" collection so that its orphan-host sweep runs
// before the lane. A collection fixture is built before the collection's first test on WHATEVER
// lane runs it — so a single untraited row in a collection member drags the sweep, and its kills,
// into the fast `requires!=docker` job that the blocking CI build runs. These three rows carry no
// requires=docker trait, by design: a correct engine never reaches Docker for any of them.
//
// Stamping them requires=docker to keep them beside their siblings would have been the wrong fix
// twice over — it would move container-free coverage out of the fast lane, and it would assert a
// dependency they do not have. Splitting the CLASS is what actually matches the truth: these rows
// need the drill's fixture, not its collection.
//
// They are otherwise unchanged. Everything they use is reached through the `internal` members of
// the drill class, aliased below as `Drill`, so there is exactly one definition of the suite
// material and a fixture change still reaches these rows and the docker ones together.
//
// Run with: dotnet test --filter "requires!=docker&FullyQualifiedName~KafkaSecurityConfirmationPreflight"
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Runtime;
using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;
using Drill = Vouchfx.Engine.Runtime.Tests.KafkaSecurityConfirmationDrillDockerTests;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// The container-free half of the security-confirmation drills: the two absent-client-certificate
/// rows, and the fixture-premise guard the docker rows rest on.
/// </summary>
/// <remarks>
/// Deliberately NOT a member of the drill collection — see this file's header. It launches the CLI
/// itself (through the drill class's shared runner, which tree-kills in a <c>finally</c>), but it
/// never starts a topology, so it has no orphan for the sweep to find.
/// </remarks>
public sealed class KafkaSecurityConfirmationPreflightTests
{
    private readonly ITestOutputHelper _output;

    public KafkaSecurityConfirmationPreflightTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The declared <c>clientCert</c> file is deleted. The suite must be refused before any
    /// topology work, and no container may be created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Driven through <c>RunSuiteAsync</c>, not the per-scenario core the docker rows
    /// use</strong> — because that is the seam the CLI's own <c>run</c> takes, and this variant is
    /// entirely about what happens BEFORE a topology exists. It is also the seam carrying the
    /// all-early-verdict guard: when every scenario has a pre-topology verdict, the suite completes
    /// without the topology being built at all.
    /// </para>
    /// <para>
    /// <strong>NOT docker-gated, and that is the claim.</strong> This row is in the unit suite
    /// because a correct engine never reaches Docker here. A regression that started a topology is
    /// caught by the VERDICT assertion, not by the container snapshot: THIS suite is a single
    /// scenario whose only verdict is a pre-topology one, so it takes <c>RunSuiteAsync</c>'s
    /// all-early-verdict guard into <c>CompleteWithoutTopologyAsync</c>, and an engine that built a
    /// topology instead would have to reach the probe, whose failures are <c>EnvironmentError</c>
    /// rather than the <c>Inconclusive</c> asserted below. The snapshot cannot catch it — teardown
    /// removes the container before the run returns (measured, and recorded at the container
    /// watcher), so a regressed engine that built, probed, failed and tore down leaves before and
    /// after equal.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AbsentClientCertificate_IsRefusedByThePreflightWithNoTopologyWork()
    {
        var suiteDirectory = Drill.MaterialiseSuiteDirectory(
            "absent-client-cert", securedEndpoint: "9093", keystoreTarget: Drill.CheckedKeystorePath);

        // The ONE difference from the passing suite: its declared client certificate is gone.
        File.Delete(Path.Combine(suiteDirectory, TestCertificateAuthority.ClientCertFileName));

        var yaml = File.ReadAllText(Path.Combine(suiteDirectory, "drill.e2e.yaml"));
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), Drill.s_registry);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var containersBefore = await Drill.ListBrokerContainersAsync(cts.Token);

        var scenarioNames = new List<string> { "absent-client-cert" };
        var diagnostics = new StringWriter();
        var result = await ScenarioRunner.RunSuiteAsync(
            new[] { ast },
            scenarioNames,
            new[] { yaml },
            Drill.s_providerAssemblies,
            Drill.AppHostAssemblyName,
            diagnostics,
            seedBaseDirectory: suiteDirectory,
            scenarioBaseDirectories: new string?[] { suiteDirectory },
            cancellationToken: cts.Token);

        var containersAfter = await Drill.ListBrokerContainersAsync(cts.Token);
        var output = diagnostics.ToString();
        _output.WriteLine($"verdict={result.Verdict} securityUnconfirmed={result.Assurance.Unconfirmed} refusal={result.Assurance.Refusal}");
        _output.WriteLine("── diagnostics ──\n" + output);

        // ── The classification: a pre-topology security rejection ─────────────────────────────
        // Inconclusive, NOT EnvironmentError: the scenario never ran, and an authoring error is not
        // an infrastructure fault. The flag is what lifts it off exit 0 all the same.
        Assert.Equal(Verdict.Inconclusive, result.Verdict);
        Assert.True(
            result.Assurance.Unconfirmed,
            "a declared-but-missing client certificate is a security rejection, so the flagless "
            + "run must not exit 0 — see this test's exit-code derivation below.");

        // ── NO TOPOLOGY WORK, argued structurally and then corroborated ───────────────────────
        // The structural half is the stronger one, and it is scoped to THIS suite's shape rather
        // than asserted as an engine invariant. "Every Inconclusive-with-the-flag result is
        // pre-topology" is FALSE in general: a multi-scenario suite that builds a topology and
        // completes normally still carries the flag forward from a scenario that failed preflight,
        // and the parallel runner aggregates it the same way. What holds here is narrower and
        // sufficient: this is a SINGLE scenario whose one verdict is pre-topology, so RunSuiteAsync
        // takes its all-early-verdict guard and returns through CompleteWithoutTopologyAsync
        // without building anything — and the only door that reaches a running topology, the
        // confirmation probe, yields EnvironmentError rather than the Inconclusive asserted above.
        // The message below says which pre-topology door it came through.
        //
        // The general form of that claim has now been wrong three times on this branch, in this
        // file and in the engine's own comments; it is written scoped here for that reason.
        Assert.Contains("clientCert", output, StringComparison.Ordinal);
        Assert.Contains(
            $"file '{TestCertificateAuthority.ClientCertFileName}' not found",
            output,
            StringComparison.Ordinal);

        // The corroborating half, and it is WEAKER than it looks in three ways worth naming: on a
        // host with no Docker at all both snapshots are empty and this compares nothing; even with
        // Docker it cannot see a topology built and torn down inside the call (see this test's own
        // remarks); and it reads GLOBAL docker state, which this row does not own.
        //
        // So it asserts only that no broker container APPEARED — never that the two lists are
        // equal. MEASURED: equality fails when a PREVIOUS row's teardown completes during this
        // one, which removes a container between the two snapshots and is not this row's doing at
        // all. A row that fails because a sibling finished tidying up is order-coupled by
        // construction, and a subset check is the assertion that actually matches the claim.
        Drill.AssertNoBrokerContainerAppeared(containersBefore, containersAfter);
    }

    /// <summary>
    /// The same absent-material suite as a plain <c>vouchfx run</c> with no gating flags, and as
    /// <c>vouchfx validate</c> — the two invocations an author actually makes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The expected code is DERIVED, not assumed, and the derivation is a committed
    /// measurement rather than an argument made here.</strong> This door produces
    /// <c>Verdict.Inconclusive</c> with the security signal set (asserted by the row above), and
    /// <c>Vouchfx.Cli.Tests.SecurityConfirmationExitCodeTests.
    /// FromVerdict_SecurityPreflightRejection_ExitsInconclusiveWithoutTheFlag</c> pins
    /// <c>ExitCodes.FromVerdict(Verdict.Inconclusive, failOnEnvironmentError: false,
    /// failOnInconclusive: false, an UNCONFIRMED SecurityAssurance) == ExitCodes.Inconclusive</c>.
    /// So the code is <b>4</b>, and it is NOT the 3 the two probe-failure drills in this file
    /// expect: those abort with <c>EnvironmentError</c> from a running topology, this one never
    /// builds a topology at all. Two security rejections, two different codes, each keeping the
    /// code its own verdict names — which is exactly the property the carve-out was written to
    /// preserve, and the reason assuming 3 here would have been wrong.
    /// </para>
    /// <para>
    /// Not docker-gated, for the same reason as the row above.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AbsentClientCertificate_PlainVouchfxRunAndValidate_BothRefuseWithTheInconclusiveCode()
    {
        var cli = Drill.ResolveCliAssembly();
        var suiteDirectory = Drill.MaterialiseSuiteDirectory(
            "absent-client-cert-cli", securedEndpoint: "9093", keystoreTarget: Drill.CheckedKeystorePath);
        File.Delete(Path.Combine(suiteDirectory, TestCertificateAuthority.ClientCertFileName));

        var suite = Path.Combine(suiteDirectory, "drill.e2e.yaml");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var containersBefore = await Drill.ListBrokerContainersAsync(cts.Token);

        var (runExit, runOutput) = await Drill.RunCliAsync(cli, "run", suite, cts.Token);
        _output.WriteLine($"`run` exit code: {runExit}");
        _output.WriteLine("── run output ──\n" + runOutput);

        Assert.Equal(4, runExit);
        Assert.Contains("clientCert", runOutput, StringComparison.Ordinal);
        Assert.Contains(
            $"file '{TestCertificateAuthority.ClientCertFileName}' not found",
            runOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain($"step '{Drill.StepId}'", runOutput, StringComparison.Ordinal);

        // The author-facing half: the same fault is reachable without running anything, which is
        // what makes it a VALIDATION-time check rather than a run-time one.
        var (validateExit, validateOutput) = await Drill.RunCliAsync(cli, "validate", suite, cts.Token);
        _output.WriteLine($"`validate` exit code: {validateExit}");
        _output.WriteLine("── validate output ──\n" + validateOutput);

        // 4, asserted exactly: `validate`'s code for an invalid document is deterministic, and this
        // test's own name promises a number. It is NOT the carve-out's doing — `validate` never
        // reaches a verdict to carve out of; the two invocations agreeing on 4 here is the
        // taxonomy's Inconclusive code being reached by two different routes.
        Assert.Equal(4, validateExit);
        Assert.Contains(
            $"file '{TestCertificateAuthority.ClientCertFileName}' not found",
            validateOutput,
            StringComparison.Ordinal);

        var containersAfter = await Drill.ListBrokerContainersAsync(cts.Token);
        Drill.AssertNoBrokerContainerAppeared(containersBefore, containersAfter);
    }

    /// <summary>
    /// <strong>The bed, not the carve-out.</strong> Every premise the three rows above rest on,
    /// asserted without a container: each sibling is schema-REJECTED, rejected OUTSIDE its
    /// <c>security</c> node, still builds an AST (so it becomes a scenario rather than an unbuilt
    /// document), and diverges in environment and in declared identity exactly as its row claims.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A fixture that quietly stopped satisfying any of these would leave all three Docker rows
    /// GREEN while measuring nothing — the identical shape would pass Row 1 for the wrong reason,
    /// and a sibling that became AST-unbuildable would take Row 1's exit code non-zero through
    /// #415's whole-value rule instead of through anything this issue is about. Cheap to state, and
    /// it runs on every machine rather than only where Docker is healthy.
    /// </para>
    /// <para>
    /// It deliberately does NOT assert an exit code or an assurance: those need a confirmed probe,
    /// which needs a container. This checks only that the documents are what the rows say they are.
    /// </para>
    /// <para>
    /// <strong>Why this file's Kafka-only registry gives the same verdicts the CLI's 25-provider
    /// registry will.</strong> The two registries can disagree about a document — an unknown step
    /// type is a schema error AND an <c>AstBuilder.Build</c> refusal to the registry that lacks the
    /// provider, and neither to the one that has it. These fixtures are built so that no such
    /// disagreement is possible: every step names <c>mq-publish.kafka</c>, which BOTH registries
    /// resolve, and the fault is a MISSING REQUIRED FIELD on it — a property of that provider's own
    /// contributed schema, identical in both. So the composed schema each registry builds rejects
    /// this document at the same pointer for the same reason, and each builds the same AST. That
    /// equivalence is the reason the earlier <c>http.rest</c> draft was abandoned rather than
    /// patched: it held only for a registry that carried the provider.
    /// </para>
    /// </remarks>
    [Fact]
    public void SiblingPremises_AreWhatEachRowClaims()
    {
        // The SAME baseline the rows materialise — one helper, so a fixture change reaches this
        // guard and the three rows together or not at all.
        var drillYaml = Drill.Issue410BaselineSuiteYaml();
        var drillAst = AstBuilder.Build(YamlDocumentParser.Parse(drillYaml), Drill.s_registry);
        var drillIdentities = SecuredTargets.Enumerate(drillAst.Environment)
            .Select(SecuredTargets.IdentityOf).ToArray();
        Assert.NotEmpty(drillIdentities);
        Assert.True(
            DocumentValidator.Validate(drillYaml, Drill.s_registry).IsValid,
            "the drill itself must be schema-VALID — it is the baseline every row's topology is "
            + "built from.");

        foreach (var shape in Enum.GetValues<Drill.SiblingShape>())
        {
            var siblingYaml = Drill.SchemaRejectedSiblingYaml(drillYaml, shape);

            // (1) Schema-REJECTED — otherwise it is an ordinary sibling and no row's door opens.
            var validation = DocumentValidator.Validate(siblingYaml, Drill.s_registry);
            Assert.False(
                validation.IsValid,
                $"the {shape} sibling is schema-VALID, so no row's schema door opens for it.");
            // (1b) The DIAGNOSTIC TEXT the rows key on, pinned here so it is checked on every
            //      machine rather than only where Docker is healthy. Row 1 asserts this fragment in
            //      the CLI's output to prove the sibling was refused for the reason this fixture
            //      built it to be; if the schema library's wording moves, this guard reddens first
            //      and says so, instead of Row 1 failing on a container host with no explanation.
            Assert.Contains(
                Drill.Issue410SiblingSchemaDiagnostic,
                string.Join(" ", validation.Errors.Select(e => e.Message)),
                StringComparison.Ordinal);

            // (2) Rejected OUTSIDE the security node, asserted through the engine's OWN predicate
            //     rather than a substring of the pointer. `SecurityDeclarationRejected` raises
            //     unconditionally, which would take Row 1 non-zero and make Rows 2 and 3 pass
            //     without the union or the fold ever being consulted.
            Assert.All(
                validation.Errors,
                error => Assert.False(
                    ScenarioRunner.LocatesADeclaredSecurityBlock(error.InstanceLocation),
                    $"the {shape} sibling's schema error at '{error.InstanceLocation}' is located "
                    + "IN its security block, so the door records SecurityDeclarationRejected and "
                    + "every row here stops measuring what it names."));

            // (3) Still AST-buildable, so it becomes a SCENARIO. An unbuilt document is #415's
            //     rule, not this issue's, and raises on its own whatever the probe confirmed.
            var siblingAst = AstBuilder.Build(YamlDocumentParser.Parse(siblingYaml), Drill.s_registry);

            // (4) The environment knob did what its row's name says — asserted through
            //     `FoldRejectedDivergent` itself, which IS the gate the rows turn on, rather than
            //     through a second serialisation this test would have to keep in step with
            //     `SerialiseEnvironment`. A folded value of `None` is the equal-environment cell.
            var folded = ScenarioRunner.FoldRejectedDivergent(
                new[] { drillAst, siblingAst },
                Drill.s_baselineThenRejected,
                Drill.s_noKindThenAuthoringFault,
                baselineIndex: 0);
            Assert.Equal(shape is not Drill.SiblingShape.IdenticalEnvironment, folded.Unconfirmed);

            // (5) The declaration knob did too — and this is the pair that separates Rows 2 and 3.
            var siblingIdentities = SecuredTargets.Enumerate(siblingAst.Environment)
                .Select(SecuredTargets.IdentityOf).ToArray();
            Assert.NotEmpty(siblingIdentities);
            Assert.Equal(
                shape is not Drill.SiblingShape.DivergentEnvironmentAndDeclaration,
                drillIdentities.SequenceEqual(siblingIdentities));
        }
    }
}
