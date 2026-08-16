// Vouchfx.Cli.Tests — REQ-005's matrix (security-assurance-derivation).
//
// ONE table, both run paths, asserted on the MESSAGE as well as the exit code. It is the
// executable form of the spec's own table: `run` (the CLI default, ScenarioRunner.RunSuiteAsync)
// and `run --parallel 1` (ScenarioRunner.RunScenarioOwningTopologyAsync via ParallelSuiteRunner)
// must give the SAME answer for the SAME document, and that answer must be a property of the
// document rather than of which door happened to fire first.
//
// WHY THE MESSAGE IS ALWAYS ASSERTED. At least four doors on this surface produce exit 4, and a
// malformed fixture produces 4 through issue #278's all-parse-failure rule, so an exit code alone
// never proves which door ran. Two probe rounds in the preceding series were void for exactly
// that and read as "fixed".
//
// WHY EVERY ROW RUNS WITHOUT DOCKER. That is the coverage hole #401 names. Every row here aborts
// before any container starts: the pre-topology doors return before SuiteTopology.StartAsync, and
// the one row that does reach StartAsync (${conn:typo}) is refused by EnvironmentMapper.Map's
// eager, pre-Configure validation — Step 1 of StartAsync, before HeadlessTopology/DCP is reached.
// The post-topology rows (health gate, probe, step outcomes) are the record-level tier in
// SecurityConfirmationExitCodeTests, which is what carrying an assurance RECORD rather than a
// boolean makes reachable at all.
//
// EVERY ROW THAT HAS AN UNSECURED CONTROL CARRIES ONE. A change that reddens an unsecured
// document is wrong, and four of these rows redden a secured document that was green.
using Vouchfx.Cli;
using Vouchfx.Cli.Selection;
using Xunit;

namespace Vouchfx.Cli.Tests;

/// <summary>
/// REQ-005: {fault} × {secured/unsecured} × {both run paths}, with the expected exit code and the
/// expected reported fault stated by the spec rather than surveyed from the code.
/// </summary>
public sealed class SecurityAssuranceMatrixTests : IDisposable
{
    private readonly string _root;

    public SecurityAssuranceMatrixTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vouchfx-assurance-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the test.
        }
    }

    // ── Fixture construction ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A declared <c>security</c> block. Every path-valued field names a file this fixture writes,
    /// unless the row's OWN fault is one of them.
    /// </summary>
    private static string SecurityBlock(
        string clientCert = "./client.pem",
        string clientKey = "./client.key",
        string? clientKeyPassword = null) =>
        "      security:\n" +
        "        profile: mtls\n" +
        "        endpoint: 8443\n" +
        $"        clientCert: {clientCert}\n" +
        $"        clientKey: {clientKey}\n" +
        (clientKeyPassword is null
            ? string.Empty
            : $"        clientKeyPassword: \"{clientKeyPassword}\"\n");

    /// <summary>
    /// One suite over a single service <c>api</c>. <paramref name="securityBlock"/> is
    /// <see langword="null"/> for the UNSECURED control of a row — the two documents then differ in
    /// exactly one thing, which is what makes the control evidence.
    /// </summary>
    private static string Suite(string? securityBlock, string serviceLines, string steps) =>
        "environment:\n" +
        "  services:\n" +
        "    api:\n" +
        "      image: myorg/api:1.0\n" +
        serviceLines +
        (securityBlock ?? string.Empty) +
        steps;

    private const string CleanStep =
        "steps:\n" +
        "  - id: call\n" +
        "    type: http.rest\n" +
        "    target: api\n" +
        "    method: GET\n" +
        "    path: /health\n";

    /// <summary>The same step with the REQUIRED <c>method</c> omitted — a schema error at
    /// <c>/steps/0</c>, deliberately OUTSIDE any <c>security</c> block.</summary>
    private const string SchemaErrorStep =
        "steps:\n" +
        "  - id: call\n" +
        "    type: http.rest\n" +
        "    target: api\n" +
        "    path: /health\n";

    /// <summary>An ordinary authoring error in a STEP: an unknown secret source in a header.</summary>
    private const string StepSecretFaultStep =
        CleanStep +
        "    headers:\n" +
        "      Authorization: \"Bearer ${secret:nosuchsource/STEP_TOKEN}\"\n";

    /// <summary>An unresolvable <c>script.csharp</c> <c>file:</c> — a ProviderPipeline refusal
    /// carrying no security signal of its own.</summary>
    private const string MissingScriptStep =
        "steps:\n" +
        "  - id: helper\n" +
        "    type: script.csharp\n" +
        "    file: no-such-helper.csx\n";

    /// <summary>One target addressed by BOTH protocol families — REQ-023's per-scenario refusal.</summary>
    private const string ProtocolConflictSteps =
        "steps:\n" +
        "  - id: call\n" +
        "    type: http.rest\n" +
        "    target: api\n" +
        "    method: GET\n" +
        "    path: /health\n" +
        "  - id: publish\n" +
        "    type: mq-publish.kafka\n" +
        "    target: api\n" +
        "    topic: orders\n" +
        "    payload: \"{}\"\n";

    /// <summary>
    /// A per-row directory-name suffix naming the run path, so the two arms of a theory never
    /// share a fixture directory.
    /// </summary>
    private static string Tag(int? parallel) => parallel is null ? "seq" : "par";

    /// <summary>
    /// Writes one suite into its OWN directory (so the discovery root is that one file) together
    /// with the placeholder certificate material every secured fixture declares.
    /// </summary>
    private string WriteSuite(string caseName, string yaml)
    {
        var dir = Path.Combine(_root, caseName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "client.pem"), "placeholder");
        File.WriteAllText(Path.Combine(dir, "client.key"), "placeholder");
        var file = Path.Combine(dir, "suite.e2e.yaml");
        File.WriteAllText(file, yaml);
        return file;
    }

    /// <summary>
    /// Drives <see cref="RunCommand.ExecuteAsync"/> at a chosen <c>--parallel</c> value with BOTH
    /// gating flags off. The flags stay off on purpose: REQ-018's property is that a secured suite
    /// which cannot be confirmed exits non-zero <em>regardless of gating flags</em>, so a row that
    /// needed one would not be testing it.
    /// </summary>
    private static Task<int> RunAsync(string path, int? parallel, TextWriter output) =>
        RunCommand.ExecuteAsync(
            path: path,
            criteria: SelectionCriteria.None,
            parallel: parallel,
            watch: false,
            failOnEnvironmentError: false,
            failOnInconclusive: false,
            htmlReportPath: null,
            junitReportPath: null,
            eventsReportPath: null,
            eventsStreamPath: null,
            decorate: false,
            output: output,
            telemetryHook: null,
            cancellationToken: default);

    // ── Row 1: a schema error anywhere ────────────────────────────────────────────────────
    //   secured 4 → 4    unsecured 0, unchanged

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row01_SchemaErrorAnywhere_Secured_ExitsInconclusive(int? parallel)
    {
        var file = WriteSuite(
            $"r01-secured-{Tag(parallel)}",
            Suite(SecurityBlock(), string.Empty, SchemaErrorStep));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);

        Assert.Contains("method", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row01_SchemaErrorAnywhere_Unsecured_ExitsSuccess(int? parallel)
    {
        var file = WriteSuite(
            $"r01-plain-{Tag(parallel)}",
            Suite(securityBlock: null, string.Empty, SchemaErrorStep));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);

        Assert.Contains("method", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

    // ── Row 2: a security preflight fault ─────────────────────────────────────────────────
    //   secured 4 → 4    (no unsecured control exists: the fault IS the security declaration)

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row02_SecurityPreflightFault_ExitsInconclusive(int? parallel)
    {
        var file = WriteSuite(
            $"r02-{Tag(parallel)}",
            Suite(SecurityBlock(clientCert: "./no-such-cert.pem"), string.Empty, CleanStep));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);

        Assert.Contains("no-such-cert.pem", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    // ── Row 3: a secret-reference fault INSIDE the security block ─────────────────────────
    //   secured 4 → 4

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row03_SecretFaultInsideTheSecurityBlock_ExitsInconclusive(int? parallel)
    {
        var file = WriteSuite(
            $"r03-{Tag(parallel)}",
            Suite(
                SecurityBlock(clientKeyPassword: "${secret:nosuchsource/PASS}"),
                string.Empty,
                CleanStep));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);

        Assert.Contains(
            "environment.services.api.security.clientKeyPassword:",
            sw.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    // ── Row 4: a preflight fault PLUS a step secret fault (#399) ──────────────────────────
    //   ○ / 4 by path → 4 on BOTH, and the preflight refusal reported on both.
    //
    // This is the row where door ORDER used to decide what was reported: `run` reached the step
    // secret pass before ProviderPipeline.Compile and returned, so the preflight refusal was never
    // computed; `--parallel` compiled first and refused, so the step fault was never computed. Each
    // path named a different one of the document's two faults, and the exit code — 4 from at least
    // four different doors on this surface — could not distinguish them.
    //
    // THE EXIT CODE ALONE IS NOT THE ASSERTION, and the row's own header is why: it claims the
    // preflight refusal is "reported on both". Asserting only the code let the two paths keep
    // reporting different faults while the row read as passing. The document genuinely has TWO
    // faults, so both are asserted, on both paths — which is what makes the reported diagnosis a
    // property of the document rather than of which pre-topology pass ran first.

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row04_PreflightFaultPlusStepSecretFault_ExitsInconclusiveOnBothPaths(int? parallel)
    {
        var file = WriteSuite(
            $"r04-{Tag(parallel)}",
            Suite(
                SecurityBlock(clientCert: "./no-such-cert.pem"),
                string.Empty,
                StepSecretFaultStep));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);
        var rendered = sw.ToString();

        // The preflight refusal — the fault `run` used to skip past.
        Assert.Contains("no-such-cert.pem", rendered, StringComparison.Ordinal);

        // …and the step secret fault — the one `--parallel` used to skip past.
        Assert.Contains("step 'call'", rendered, StringComparison.Ordinal);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    // ── Row 5: a step-level secret fault ALONE ────────────────────────────────────────────
    //   secured ○ → 4    unsecured 0, unchanged

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row05_StepSecretFaultAlone_Secured_ExitsInconclusive(int? parallel)
    {
        var file = WriteSuite(
            $"r05-secured-{Tag(parallel)}",
            Suite(SecurityBlock(), string.Empty, StepSecretFaultStep));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);

        Assert.Contains("step 'call'", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row05_StepSecretFaultAlone_Unsecured_ExitsSuccess(int? parallel)
    {
        var file = WriteSuite(
            $"r05-plain-{Tag(parallel)}",
            Suite(securityBlock: null, string.Empty, StepSecretFaultStep));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);

        Assert.Contains("step 'call'", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

    // ── Row 6: an unresolvable `script.csharp file:` ALONE ────────────────────────────────
    //   secured ○ → 4    unsecured 0, unchanged
    //
    // ── RETRACTION: THE RULE THIS ROW OVERTURNED ──────────────────────────────────────────
    //
    // `RunPathRootExecuteTests.ExecuteAsync_SecuredSuiteWithNoSecretFault_IsNotCarvedOut` asserted
    // the OPPOSITE of the secured row below, on both run paths, over this same fixture shape: a
    // secured document whose only fault is an unresolvable `script.csharp file:` exits 0, because
    // REQ-018's carve-out did not reach that door.
    //
    // THE OLD RULE WAS DELIBERATELY OVERTURNED, not found defective on its own terms. It was true
    // and load-bearing while the carve-out was NARROW: the flag was raised by named doors, this was
    // not one of them, and the test's job was to prove the narrowness was a decision rather than an
    // accident — so that the security rows either side of it could not be satisfied by an
    // implementation that simply reddened every secured suite.
    //
    // WHY IT NO LONGER HOLDS. The derived rule is WIDE by decision (spec REQ-003, stated as a
    // user-visible consequence in REQ-004): an authoring refusal that leaves a declared target
    // unconfirmed raises, because nothing downstream of the refusal ever validates the
    // declaration — and which door refused is not consulted. (Not "whatever refused it": Row 09's
    // unparseable secured document is refused before any container starts and does NOT raise, its
    // own row asserting the notice is absent. The identity of the door is irrelevant; the
    // predicate is not.) An unresolvable `script.csharp file:` is one such refusal. The
    // row therefore flips from 0 to 4 — and that flip IS the requirement, so a test asserting the
    // 0 was, from that decision onward, a test asserting the defect.
    //
    // WHY DELETED RATHER THAN REWRITTEN IN PLACE. Rewritten, it would have become this row: same
    // document shape, same two arms, same assertion — one rule asserted twice in two files, which
    // is the drift this whole spec exists to close. The surviving spelling is the one that also
    // carries the unsecured control.
    //
    // WHAT THE DELETED TEST'S JOB IS NOW — AND THE COVERAGE THAT WENT WITH IT. Its guard did not
    // move file; it moved TIER, and that is worth stating plainly rather than leaving to be
    // inferred. "A secured suite does not simply always exit non-zero" is still proved HERE for
    // UNSECURED documents by the control below, but no Docker-free test at THIS tier drives a
    // SECURED document to exit 0 any more, and the tier has nowhere to put one. The guard
    // therefore lives at the record tier, in SecurityConfirmationExitCodeTests, where a declared
    // `security` block still exits 0:
    // `FromVerdict_SecuredSuiteWhoseHealthGateFailed_StillExitsSuccess`,
    // `FromVerdict_TopologyUpAndProbeConfirmed_MapsPerTaxonomy` and
    // `Unconfirmed_AuthoringRefusalBesideAFullyConfirmedProbe_DoesNotRaise`; and end-to-end in
    // `KafkaSecurityConfirmationDrillDockerTests`, which needs Docker. The residual risk is
    // named rather than hidden: a regression confined to CLI WIRING that reddened a secured suite
    // which ought to exit 0 would leave all three record-tier guards green.
    //
    // THE SCAR IT CARRIED, kept here because #399's row in RunPathRootExecuteTests cites it. An
    // earlier form of the deleted test drove this case with a tag filter matching nothing and was
    // VACUOUS: selection short-circuits before compilation, so no door ran at all — the same filter
    // over a document with a genuine security fault also exits 0. It was re-driven with the
    // `script.csharp file:` fixture this row now uses.

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row06_UnresolvableScriptFile_Secured_ExitsInconclusive(int? parallel)
    {
        var file = WriteSuite(
            $"r06-secured-{Tag(parallel)}",
            Suite(SecurityBlock(), string.Empty, MissingScriptStep));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);

        Assert.Contains("no-such-helper.csx", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row06_UnresolvableScriptFile_Unsecured_ExitsSuccess(int? parallel)
    {
        var file = WriteSuite(
            $"r06-plain-{Tag(parallel)}",
            Suite(securityBlock: null, string.Empty, MissingScriptStep));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);

        Assert.Contains("no-such-helper.csx", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

    // ── Row 7: `${conn:typo}` — EnvironmentMapper.Map's ArgumentException ─────────────────
    //   secured ○ → 4    unsecured 0, unchanged
    //
    // The boundary case the spec settles explicitly: this fault arrives INSIDE StartAsync but
    // starts NO container (Map is eager and pure, ahead of DCP), so "before any container started"
    // — not "before the StartAsync call" — is what raises.

    private const string UnknownConnReferenceLines =
        "      env:\n" +
        "        DB: \"${conn:nosuchdependency}\"\n";

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row07_UnknownConnReference_Secured_ExitsInconclusive(int? parallel)
    {
        var file = WriteSuite(
            $"r07-secured-{Tag(parallel)}",
            Suite(SecurityBlock(), UnknownConnReferenceLines, CleanStep));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);

        Assert.Contains("unknown dependency", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row07_UnknownConnReference_Unsecured_ExitsSuccess(int? parallel)
    {
        var file = WriteSuite(
            $"r07-plain-{Tag(parallel)}",
            Suite(securityBlock: null, UnknownConnReferenceLines, CleanStep));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);

        Assert.Contains("unknown dependency", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

    // ── Row 8a: a protocol conflict inside ONE scenario ───────────────────────────────────
    //   secured ○ → 4    unsecured 0, unchanged
    //
    // The per-scenario half of REQ-023's refusal (ProviderPipeline.Compile), reachable on BOTH
    // paths. The suite-level half is Row 8b, which the `--parallel` path has no equivalent of.

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row08a_ProtocolConflictInOneScenario_Secured_ExitsInconclusive(int? parallel)
    {
        var file = WriteSuite(
            $"r08a-secured-{Tag(parallel)}",
            Suite(SecurityBlock(), "      ports: [9093]\n", ProtocolConflictSteps));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);

        Assert.Contains("one endpoint value per target", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row08a_ProtocolConflictInOneScenario_Unsecured_ExitsSuccess(int? parallel)
    {
        var file = WriteSuite(
            $"r08a-plain-{Tag(parallel)}",
            Suite(securityBlock: null, "      ports: [9093]\n", ProtocolConflictSteps));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);

        Assert.Contains("one endpoint value per target", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

    // ── Row 8b: the SUITE-LEVEL protocol conflict ─────────────────────────────────────────
    //   secured ○ → 4    unsecured 0, unchanged
    //
    // The door whose own written rationale this change overturns: "a protocol conflict is an
    // authoring error, not a failure to confirm a security assertion". Under the derived rule the
    // door's own classification of its fault is not consulted: an authoring refusal that leaves a
    // declared target unconfirmed raises whichever door recorded it.
    //
    // `run` ONLY, and the absence is structural rather than an omission: the suite-level guard
    // exists because ONE shared topology stages one value per target for every scenario. Under
    // `--parallel` each scenario owns its own topology, so there is no shared staging to conflict
    // and no such guard to reach.

    /// <summary>
    /// Writes a two-scenario suite into ONE directory (so the base-directory divergence guard,
    /// which runs first, does not fire), splitting the two protocol families across the scenarios.
    /// </summary>
    private string WriteSplitFamilySuite(string caseName, string? securityBlock)
    {
        var dir = Path.Combine(_root, caseName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "client.pem"), "placeholder");
        File.WriteAllText(Path.Combine(dir, "client.key"), "placeholder");

        // Byte-identical environment blocks — required of every scenario in a shared-topology
        // suite, and the reason the conflict is invisible to either scenario's own compilation.
        var environment =
            "environment:\n" +
            "  services:\n" +
            "    api:\n" +
            "      image: myorg/api:1.0\n" +
            (securityBlock ?? string.Empty) +
            "    broker:\n" +
            "      image: myorg/broker:1.0\n" +
            "      ports: [9093]\n";

        File.WriteAllText(
            Path.Combine(dir, "a.e2e.yaml"),
            environment +
            "steps:\n" +
            "  - id: call\n" +
            "    type: http.rest\n" +
            "    target: broker\n" +
            "    method: GET\n" +
            "    path: /health\n");

        File.WriteAllText(
            Path.Combine(dir, "b.e2e.yaml"),
            environment +
            "steps:\n" +
            "  - id: publish\n" +
            "    type: mq-publish.kafka\n" +
            "    target: broker\n" +
            "    topic: orders\n" +
            "    payload: \"{}\"\n");

        return dir;
    }

    [Fact]
    public async Task Row08b_SuiteLevelProtocolConflict_Secured_ExitsInconclusive()
    {
        var dir = WriteSplitFamilySuite("r08b-secured", SecurityBlock());

        var sw = new StringWriter();
        var exitCode = await RunAsync(dir, parallel: null, sw);

        Assert.Contains("one endpoint value per target", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    [Fact]
    public async Task Row08b_SuiteLevelProtocolConflict_Unsecured_ExitsSuccess()
    {
        var dir = WriteSplitFamilySuite("r08b-plain", securityBlock: null);

        var sw = new StringWriter();
        var exitCode = await RunAsync(dir, parallel: null, sw);

        Assert.Contains("one endpoint value per target", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

    // ── Row 9: an unparseable document ────────────────────────────────────────────────────
    //   4 → 4, on BOTH the secured and the unsecured spelling — and via issue #278's
    //   all-parse-failure rule, NOT via this signal. A document that cannot be parsed cannot be
    //   shown to declare anything, so the assurance's `Declared` is empty and nothing raises.
    //
    // Both spellings are WRITTEN, not merely claimed: the sentence above asserted a pair and only
    // the secured half existed, which is the shape of claim this file's own header rejects
    // everywhere else.
    //
    // AND THE RESCUE IS WHAT MAKES THIS ROW GREEN — put the same secured unparseable file beside a
    // parseable sibling and #278's rule no longer applies, so the suite exits 0 with no security
    // notice. Row 09b below pins that, deliberately asserting today's wrong answer (issue #411).

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(null, false)]
    [InlineData(1, false)]
    public async Task Row09_UnparseableDocument_ExitsInconclusiveViaTheParseFailureRule(
        int? parallel, bool secured)
    {
        var file = WriteSuite(
            $"r09-{(secured ? "secured" : "plain")}-{Tag(parallel)}",
            Suite(
                secured ? SecurityBlock() : null,
                string.Empty,
                "steps:\n  - id: x\n    type: not-a-real-provider\n"));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);
        var rendered = sw.ToString();

        Assert.Contains("no registered provider", rendered, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);

        // …and NOT through the security signal: the parse never produced an environment, so the
        // engine cannot know the document declared anything.
        Assert.DoesNotContain("declares a 'security' block", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>Row 09's mixed spelling, and it asserts the CURRENT WRONG behaviour on purpose.</strong>
    /// The single-file row above exits 4 through issue #278's all-parse-failure rule, which rescues
    /// it. Put the same secured unparseable file BESIDE a parseable sibling and that rescue does not
    /// apply: <c>RunCommand</c> drops unparseable files into its <c>failures</c> list before the
    /// runner is called, so the declaration never reaches <c>SecuredTargets.Enumerate</c>,
    /// <c>Declared</c> is empty, nothing raises — and the suite exits 0 with no security notice
    /// while a file in it plainly asserts <c>mtls</c>.
    /// <para>
    /// That is the documented hole [issue #411](https://github.com/tomas-rampas/vouchfx/issues/411),
    /// and it is pinned rather than described because a measured row is worth more than prose: when
    /// #411 closes, THIS TEST GOES RED and is inverted to assert the non-zero exit and the notice.
    /// The fix is a behaviour change in <c>RunCommand</c>'s parse handling and is deliberately not
    /// part of this branch.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Row09b_SecuredUnparseableBesideAParseableSibling_ExitsSuccessWithNoNotice()
    {
        var dir = Path.Combine(_root, "r09b");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "client.pem"), "placeholder");
        File.WriteAllText(Path.Combine(dir, "client.key"), "placeholder");

        // The SECURED file, unparseable: it declares a full mtls block AND an unknown step type.
        File.WriteAllText(
            Path.Combine(dir, "a.e2e.yaml"),
            Suite(
                SecurityBlock(),
                string.Empty,
                "steps:\n  - id: x\n    type: not-a-real-provider\n"));

        // …beside an UNSECURED sibling that parses and is then refused on an ordinary authoring
        // fault, so the run never needs a container and the exit code is the suite's own.
        File.WriteAllText(
            Path.Combine(dir, "b.e2e.yaml"),
            Suite(securityBlock: null, string.Empty, StepSecretFaultStep));

        var sw = new StringWriter();
        var exitCode = await RunAsync(dir, parallel: null, sw);
        var rendered = sw.ToString();

        // Both faults are reported — the run saw both files.
        Assert.Contains("no registered provider", rendered, StringComparison.Ordinal);
        Assert.Contains("step 'call'", rendered, StringComparison.Ordinal);

        // …and yet: no security notice, and a green build.
        Assert.DoesNotContain("declares a 'security' block", rendered, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

    // ── Row 10: base-directory divergence ─────────────────────────────────────────────────
    //   4 → 4. `run` only: the guard is a property of the ONE shared topology's single root.

    [Fact]
    public async Task Row10_SecurityBaseDirectoryDivergence_ExitsInconclusive()
    {
        var dir = Path.Combine(_root, "r10");
        var left = Path.Combine(dir, "left");
        var right = Path.Combine(dir, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);

        foreach (var scenarioDirectory in new[] { left, right })
        {
            File.WriteAllText(Path.Combine(scenarioDirectory, "client.pem"), "placeholder");
            File.WriteAllText(Path.Combine(scenarioDirectory, "client.key"), "placeholder");
            File.WriteAllText(
                Path.Combine(scenarioDirectory, "s.e2e.yaml"),
                Suite(SecurityBlock(), string.Empty, CleanStep));
        }

        var sw = new StringWriter();
        var exitCode = await RunAsync(dir, parallel: null, sw);

        Assert.Contains(
            "resolves its declared security paths against a different directory",
            sw.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    // ── Row 11: shared-`environment` divergence across scenarios ──────────────────────────
    //   secured ○ → 3    unsecured 0, unchanged
    //
    // `run` only, and structurally so: the gate exists because ONE shared topology is built from
    // ONE environment block. Under `--parallel` each scenario owns its own topology and there is
    // nothing to share, so there is no such gate to reach.
    //
    // The verdict is EnvironmentError rather than Inconclusive — the suite aborted, it did not
    // report an authoring verdict per scenario — so the secured spelling exits 3, not 4. That is
    // the row's own line in the spec's table, and it was the one row of the table with no test.

    /// <summary>
    /// Two scenarios in ONE directory (so the base-directory guard, which runs later, is not what
    /// fires) whose <c>environment</c> blocks differ in the service image — plus, per arm, whether
    /// each declares <c>security</c>.
    /// </summary>
    private string WriteDivergentEnvironmentSuite(
        string caseName,
        string? firstSecurity,
        string? secondSecurity)
    {
        var dir = Path.Combine(_root, caseName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "client.pem"), "placeholder");
        File.WriteAllText(Path.Combine(dir, "client.key"), "placeholder");

        // The images differ, so the environments diverge whatever the two security blocks say —
        // the divergence is never itself the security declaration.
        File.WriteAllText(
            Path.Combine(dir, "a.e2e.yaml"),
            "environment:\n  services:\n    api:\n      image: myorg/api:1.0\n"
            + (firstSecurity ?? string.Empty)
            + CleanStep);

        File.WriteAllText(
            Path.Combine(dir, "b.e2e.yaml"),
            "environment:\n  services:\n    api:\n      image: myorg/api:2.0\n"
            + (secondSecurity ?? string.Empty)
            + CleanStep);

        return dir;
    }

    [Fact]
    public async Task Row11_SharedEnvironmentDivergence_Secured_ExitsEnvironmentError()
    {
        var dir = WriteDivergentEnvironmentSuite(
            "r11-secured", SecurityBlock(), SecurityBlock());

        var sw = new StringWriter();
        var exitCode = await RunAsync(dir, parallel: null, sw);

        Assert.Contains("must share one topology", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.EnvironmentError, exitCode);
    }

    [Fact]
    public async Task Row11_SharedEnvironmentDivergence_Unsecured_ExitsSuccess()
    {
        var dir = WriteDivergentEnvironmentSuite(
            "r11-plain", firstSecurity: null, secondSecurity: null);

        var sw = new StringWriter();
        var exitCode = await RunAsync(dir, parallel: null, sw);

        Assert.Contains("must share one topology", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

    /// <summary>
    /// <strong>The spelling that measures the derivation rather than the gate.</strong> Only the
    /// SECOND scenario declares <c>security</c>. The suite-level walk used to read
    /// <c>scenarios[0].Environment</c> alone, so this document reached the divergence gate with an
    /// empty <c>Declared</c> and exited 0 — while the same two files with the security block in the
    /// other one exited 3. A rename of either file flipped a CI build's colour, which is the exact
    /// false negative REQ-018 exists to remove: security declared, never exercised, build green.
    /// </summary>
    [Fact]
    public async Task Row11_SharedEnvironmentDivergence_OnlyTheLaterScenarioSecured_ExitsEnvironmentError()
    {
        var dir = WriteDivergentEnvironmentSuite(
            "r11-mixed", firstSecurity: null, secondSecurity: SecurityBlock());

        var sw = new StringWriter();
        var exitCode = await RunAsync(dir, parallel: null, sw);

        Assert.Contains("must share one topology", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.EnvironmentError, exitCode);
    }
}
