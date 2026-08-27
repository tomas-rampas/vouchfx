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
    /// <param name="path">The discovery root.</param>
    /// <param name="parallel">The <c>--parallel</c> value; <see langword="null"/> for the
    /// sequential shared-topology path.</param>
    /// <param name="output">The run's terminal.</param>
    /// <param name="criteria">
    /// The selection filter. Defaults to <see cref="SelectionCriteria.None"/> — every row but
    /// 09e's pair is about what a suite reports, not about which of its files were chosen.
    /// </param>
    private static Task<int> RunAsync(
        string path, int? parallel, TextWriter output, SelectionCriteria? criteria = null) =>
        RunCommand.ExecuteAsync(
            path: path,
            criteria: criteria ?? SelectionCriteria.None,
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
    //   secured 4 → 4    unsecured 0 → 4 (#369: nothing executed; NOT a security refusal)

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
    public async Task Row01_SchemaErrorAnywhere_Unsecured_ExitsInconclusiveWithNoSecurityNotice(int? parallel)
    {
        var file = WriteSuite(
            $"r01-plain-{Tag(parallel)}",
            Suite(securityBlock: null, string.Empty, SchemaErrorStep));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);

        Assert.Contains("method", sw.ToString(), StringComparison.Ordinal);
        // #369: exit 4, not 0 — and the reason is NOT security. A suite refused before any
        // topology was built executed nothing, and a run in which nothing executed is never a
        // clean pass. The security notice below is what still proves the carve-out did not reach
        // this unsecured suite.
        //
        // THIS ROW'S DISCRIMINATOR MOVED, DELIBERATELY. The pair used to read "secured 4,
        // unsecured 0" and carried its whole proof in the exit code; with both now 4 that proof
        // would have evaporated silently, leaving a row that passes while testing nothing. The
        // notice is the better signal anyway: it asserts the MECHANISM (did the security
        // derivation refuse this run) rather than a proxy for it.
        Assert.DoesNotContain(
            "declares a 'security' block", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
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
    //   secured ○ → 4    unsecured 0 → 4 (#369: nothing executed; NOT a security refusal)

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
    public async Task Row05_StepSecretFaultAlone_Unsecured_ExitsInconclusiveWithNoSecurityNotice(int? parallel)
    {
        var file = WriteSuite(
            $"r05-plain-{Tag(parallel)}",
            Suite(securityBlock: null, string.Empty, StepSecretFaultStep));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);

        Assert.Contains("step 'call'", sw.ToString(), StringComparison.Ordinal);
        // #369: exit 4, not 0 — and the reason is NOT security. A suite refused before any
        // topology was built executed nothing, and a run in which nothing executed is never a
        // clean pass. The security notice below is what still proves the carve-out did not reach
        // this unsecured suite.
        //
        // THIS ROW'S DISCRIMINATOR MOVED, DELIBERATELY. The pair used to read "secured 4,
        // unsecured 0" and carried its whole proof in the exit code; with both now 4 that proof
        // would have evaporated silently, leaving a row that passes while testing nothing. The
        // notice is the better signal anyway: it asserts the MECHANISM (did the security
        // derivation refuse this run) rather than a proxy for it.
        Assert.DoesNotContain(
            "declares a 'security' block", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    // ── Row 6: an unresolvable `script.csharp file:` ALONE ────────────────────────────────
    //   secured ○ → 4    unsecured 0 → 4 (#369: nothing executed; NOT a security refusal)
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
    // declaration — and which door refused is not consulted. (Not "whatever refused it": Row 09c's
    // MALFORMED-YAML secured document is refused before any container starts and does NOT raise,
    // its own row asserting the notice is absent, because nothing bound and so nothing was
    // declared. The identity of the door is irrelevant; the predicate is not.) An unresolvable
    // `script.csharp file:` is one such refusal. The
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
    public async Task Row06_UnresolvableScriptFile_Unsecured_ExitsInconclusiveWithNoSecurityNotice(int? parallel)
    {
        var file = WriteSuite(
            $"r06-plain-{Tag(parallel)}",
            Suite(securityBlock: null, string.Empty, MissingScriptStep));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);

        Assert.Contains("no-such-helper.csx", sw.ToString(), StringComparison.Ordinal);
        // #369: exit 4, not 0 — and the reason is NOT security. A suite refused before any
        // topology was built executed nothing, and a run in which nothing executed is never a
        // clean pass. The security notice below is what still proves the carve-out did not reach
        // this unsecured suite.
        //
        // THIS ROW'S DISCRIMINATOR MOVED, DELIBERATELY. The pair used to read "secured 4,
        // unsecured 0" and carried its whole proof in the exit code; with both now 4 that proof
        // would have evaporated silently, leaving a row that passes while testing nothing. The
        // notice is the better signal anyway: it asserts the MECHANISM (did the security
        // derivation refuse this run) rather than a proxy for it.
        Assert.DoesNotContain(
            "declares a 'security' block", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    // ── Row 7: `${conn:typo}` — EnvironmentMapper.Map's ArgumentException ─────────────────
    //   secured ○ → 4    unsecured 0 → 4 (#369: nothing executed; NOT a security refusal)
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
    public async Task Row07_UnknownConnReference_Unsecured_ExitsInconclusiveWithNoSecurityNotice(int? parallel)
    {
        var file = WriteSuite(
            $"r07-plain-{Tag(parallel)}",
            Suite(securityBlock: null, UnknownConnReferenceLines, CleanStep));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);

        Assert.Contains("unknown dependency", sw.ToString(), StringComparison.Ordinal);
        // #369: exit 4, not 0 — and the reason is NOT security. A suite refused before any
        // topology was built executed nothing, and a run in which nothing executed is never a
        // clean pass. The security notice below is what still proves the carve-out did not reach
        // this unsecured suite; the exit code no longer discriminates, so the mechanism does.
        //
        // This row also measured the sequential/parallel divergence the same change closed: the
        // `${conn:typo}` fault exited 0 under a bare run and 4 under `--parallel 1`, because the
        // parallel runner derived "nothing executed" from its event buffers while the sequential
        // ArgumentException catch returned a bare SuiteResult that said nothing. Both now route
        // through the one without-topology completion path.
        Assert.DoesNotContain(
            "declares a 'security' block", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    // ── Row 8a: a protocol conflict inside ONE scenario ───────────────────────────────────
    //   secured ○ → 4    unsecured 0 → 4 (#369: nothing executed; NOT a security refusal)
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
    public async Task Row08a_ProtocolConflictInOneScenario_Unsecured_ExitsInconclusiveWithNoSecurityNotice(int? parallel)
    {
        var file = WriteSuite(
            $"r08a-plain-{Tag(parallel)}",
            Suite(securityBlock: null, "      ports: [9093]\n", ProtocolConflictSteps));

        var sw = new StringWriter();
        var exitCode = await RunAsync(file, parallel, sw);

        Assert.Contains("one endpoint value per target", sw.ToString(), StringComparison.Ordinal);
        // #369: exit 4, not 0 — and the reason is NOT security. A suite refused before any
        // topology was built executed nothing, and a run in which nothing executed is never a
        // clean pass. The security notice below is what still proves the carve-out did not reach
        // this unsecured suite.
        //
        // THIS ROW'S DISCRIMINATOR MOVED, DELIBERATELY. The pair used to read "secured 4,
        // unsecured 0" and carried its whole proof in the exit code; with both now 4 that proof
        // would have evaporated silently, leaving a row that passes while testing nothing. The
        // notice is the better signal anyway: it asserts the MECHANISM (did the security
        // derivation refuse this run) rather than a proxy for it.
        Assert.DoesNotContain(
            "declares a 'security' block", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    // ── Row 8b: the SUITE-LEVEL protocol conflict ─────────────────────────────────────────
    //   secured ○ → 4    unsecured 0 → 4 (#369: nothing executed; NOT a security refusal)
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
    public async Task Row08b_SuiteLevelProtocolConflict_Unsecured_ExitsInconclusiveWithNoSecurityNotice()
    {
        var dir = WriteSplitFamilySuite("r08b-plain", securityBlock: null);

        var sw = new StringWriter();
        var exitCode = await RunAsync(dir, parallel: null, sw);

        Assert.Contains("one endpoint value per target", sw.ToString(), StringComparison.Ordinal);
        // #369: exit 4, not 0 — and the reason is NOT security. A suite refused before any
        // topology was built executed nothing, and a run in which nothing executed is never a
        // clean pass. The security notice below is what still proves the carve-out did not reach
        // this unsecured suite.
        //
        // THIS ROW'S DISCRIMINATOR MOVED, DELIBERATELY. The pair used to read "secured 4,
        // unsecured 0" and carried its whole proof in the exit code; with both now 4 that proof
        // would have evaporated silently, leaving a row that passes while testing nothing. The
        // notice is the better signal anyway: it asserts the MECHANISM (did the security
        // derivation refuse this run) rather than a proxy for it.
        Assert.DoesNotContain(
            "declares a 'security' block", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    // ── Row 9: a document that fails discovery, ALONE ─────────────────────────────────────
    //   4 → 4, on BOTH the secured and the unsecured spelling — and via issue #278's
    //   all-parse-failure rule, NOT via this signal. With nothing parsed the runner is never
    //   called at all, so no assurance exists to raise: `RunCommand` short-circuits on
    //   `parsed.Count == 0` before either run path. That reason is the row's, and it is NOT
    //   "the document could not be shown to declare anything" — since issue #411 closed, a
    //   document refused by AstBuilder DOES declare into the runner's walk (Row 09b). Here there
    //   is simply no runner to declare into.
    //
    // Both spellings are WRITTEN, not merely claimed: the sentence above asserted a pair and only
    // the secured half existed, which is the shape of claim this file's own header rejects
    // everywhere else.
    //
    // AND THE RESCUE IS WHAT USED TO MAKE THIS ROW GREEN — put the same secured unparseable file
    // beside a parseable sibling and #278's rule no longer applies. Row 09b below used to pin the
    // exit 0 that resulted (issue #411); it now pins the non-zero exit that closing #411 produced
    // for the ONE recoverable class, and Row 09c pins the classes that remain open.

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(null, false)]
    [InlineData(1, false)]
    public async Task Row09_DiscoveryFailureAlone_ExitsInconclusiveViaTheParseFailureRule(
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

        // …and NOT through the security signal: nothing parsed, so neither run path was entered
        // and no assurance was ever produced to raise. Put the SAME file beside one parseable
        // sibling and the secured spelling does raise — Row 09b.
        Assert.DoesNotContain("declares a 'security' block", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes the mixed two-file suite Rows 09b and 09c share: ONE broken file (whose
    /// <paramref name="brokenSteps"/> decide WHICH failure class it lands in, and whose
    /// <paramref name="securityBlock"/> decides whether it declares anything) beside ONE UNSECURED
    /// sibling that parses and is then refused on an ordinary step-level authoring fault.
    /// </summary>
    /// <remarks>
    /// The sibling is what makes the row interesting AND what keeps it Docker-free: it defeats
    /// issue #278's all-parse-failure rescue (so the exit code is the suite's own rather than #278's
    /// unconditional 4), and its own refusal aborts the run before any container starts.
    /// </remarks>
    private string WriteMixedBrokenSuite(string caseName, string? securityBlock, string brokenSteps)
    {
        var dir = Path.Combine(_root, caseName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "client.pem"), "placeholder");
        File.WriteAllText(Path.Combine(dir, "client.key"), "placeholder");

        File.WriteAllText(
            Path.Combine(dir, "a.e2e.yaml"),
            Suite(securityBlock, string.Empty, brokenSteps));

        File.WriteAllText(
            Path.Combine(dir, "b.e2e.yaml"),
            Suite(securityBlock: null, string.Empty, StepSecretFaultStep));

        return dir;
    }

    /// <summary>A <c>--tag</c>-only selection filter.</summary>
    private static SelectionCriteria TagCriteria(string tag) =>
        SelectionCriteria.None with { Tags = new[] { tag } };

    /// <summary>
    /// Row 09e's suite: <see cref="WriteMixedBrokenSuite"/>'s pair with a <c>metadata</c> block
    /// carrying the tag <c>smoke</c> prepended to BOTH files.
    /// </summary>
    /// <remarks>
    /// The BROKEN file must carry the tag itself, and that is the point of the row rather than a
    /// fixture convenience: matching it on the sibling's metadata would be the fail-open behaviour
    /// the fix removes. Its tag is only visible because the <c>metadata</c> block bound before
    /// <c>AstBuilder</c> refused the document.
    /// </remarks>
    private string WriteTaggedMixedBrokenSuite(string caseName, string? securityBlock)
    {
        const string Tagged = "metadata:\n  tags: [smoke]\n";

        var dir = Path.Combine(_root, caseName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "client.pem"), "placeholder");
        File.WriteAllText(Path.Combine(dir, "client.key"), "placeholder");

        File.WriteAllText(
            Path.Combine(dir, "a.e2e.yaml"),
            Tagged + Suite(securityBlock, string.Empty, UnknownStepTypeSteps));

        File.WriteAllText(
            Path.Combine(dir, "b.e2e.yaml"),
            Tagged + Suite(securityBlock: null, string.Empty, StepSecretFaultStep));

        return dir;
    }

    /// <summary>An unknown step type: the document BINDS, then <c>AstBuilder.Build</c> refuses it —
    /// failure class 4, the one class issue #411 closed.</summary>
    private const string UnknownStepTypeSteps =
        "steps:\n  - id: x\n    type: not-a-real-provider\n";

    /// <summary>An unterminated quoted scalar: <c>YamlDocumentParser.Parse</c> itself throws, so
    /// NOTHING binds — failure class 3, which issue #411 leaves open (see Row 09c).</summary>
    private const string MalformedYamlSteps =
        "steps:\n  - id: x\n    type: \"http.rest\n";

    /// <summary>
    /// <strong>Row 09's mixed spelling — the shape issue #411 named, now closed for failure class
    /// 4.</strong> The single-file row above exits 4 through issue #278's all-parse-failure rule,
    /// which rescues it. Put the same secured file BESIDE a parseable sibling and that rescue does
    /// not apply, and the suite used to exit 0 with no security notice while a file in it plainly
    /// asserted <c>mtls</c>: <c>RunCommand</c> dropped the file into its <c>failures</c> list before
    /// the runner was called, so the declaration never reached <c>SecuredTargets.Enumerate</c>.
    /// <para>
    /// It reaches it now. The document BOUND — <c>YamlDocumentParser.Parse</c> succeeded and only
    /// <c>AstBuilder.Build</c> threw — so <c>ScenarioDiscovery</c> retains that bound environment,
    /// the runner folds it into the same canonical walk that fills <c>Declared</c>, and records
    /// <see cref="Vouchfx.Engine.Runtime.SecurityAbortKind.AuthoringFault"/>. Both halves are
    /// required: names alone leave <c>Refusal</c> null and every disjunct of
    /// <c>SecurityAssurance.Unconfirmed</c> needs a non-null one.
    /// </para>
    /// <para>
    /// The exit code is the VERDICT's own opt-in code, not a fixed one: the sibling's refusal and
    /// the parse failure both fold in as <see cref="Vouchfx.Engine.Abstractions.Verdict.Inconclusive"/>,
    /// so REQ-018's carve-out returns <see cref="ExitCodes.Inconclusive"/>. What REQ-018 requires is
    /// only that it is non-zero, which is asserted separately and first.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row09b_SecuredUnbuildableBesideAParseableSibling_ExitsNonZeroWithTheNotice(
        int? parallel)
    {
        var dir = WriteMixedBrokenSuite(
            $"r09b-secured-{Tag(parallel)}", SecurityBlock(), UnknownStepTypeSteps);

        var sw = new StringWriter();
        var exitCode = await RunAsync(dir, parallel, sw);
        var rendered = sw.ToString();

        // Both faults are still reported — the run saw both files, exactly as before.
        Assert.Contains("no registered provider", rendered, StringComparison.Ordinal);
        Assert.Contains("step 'call'", rendered, StringComparison.Ordinal);

        // …and now the declaration is accounted for: the notice, and a non-zero build.
        Assert.Contains("declares a 'security' block", rendered, StringComparison.Ordinal);
        Assert.NotEqual(ExitCodes.Success, exitCode);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    /// <summary>
    /// <strong>Row 09b's unsecured control. The exit code no longer discriminates here — the
    /// NOTICE does.</strong> Byte-identical to the row above but for the <c>security</c> block.
    /// <para>
    /// This row used to assert <c>Success</c>, and its purpose was that a suite reddening without a
    /// declaration would prove the declaration was not what reddened it. #425 then made any parse
    /// failure never-clean, so both arms sit at 4 and the code cannot tell them apart. The proof
    /// moved rather than disappeared: <c>Assert.DoesNotContain("declares a 'security' block")</c>
    /// below is what now carries it, and it is the assertion to protect. A change that reddens this
    /// row's exit code is expected; one that makes the security notice print here is the defect
    /// this row exists to catch.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row09b_UnsecuredUnbuildableBesideAParseableSibling_ExitsInconclusiveOnTheUnreadFile(int? parallel)
    {
        var dir = WriteMixedBrokenSuite(
            $"r09b-plain-{Tag(parallel)}", securityBlock: null, UnknownStepTypeSteps);

        var sw = new StringWriter();
        var exitCode = await RunAsync(dir, parallel, sw);
        var rendered = sw.ToString();

        Assert.Contains("no registered provider", rendered, StringComparison.Ordinal);
        Assert.Contains("step 'call'", rendered, StringComparison.Ordinal);

        Assert.DoesNotContain("declares a 'security' block", rendered, StringComparison.Ordinal);
        // #425: exit 4, not 0. The unread file is what reddens this, NOT the security question
        // — the "declares a 'security' block" notice is still absent, and the assertion above
        // pins that. A document the engine could not read is never reported as clean, whether or
        // not anything in the run mentions security.
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    /// <summary>
    /// A <c>security</c> node that is NOT a mapping. <c>YamlDocumentParser.ParseSecurity</c> returns
    /// <see langword="null"/> for any such node, so the document binds with NO
    /// <c>SecuritySpec</c> — the canonical walk sees nothing, whatever the author wrote.
    /// </summary>
    /// <param name="spelling">
    /// <c>scalar</c> — <c>security: mtls</c>, the profile name written where the block belongs;
    /// <c>bare</c> — <c>security:</c> with nothing under it (its children commented out, a
    /// bisecting author's most ordinary edit); <c>empty</c> — <c>security: {}</c>, which IS a
    /// mapping and therefore the control: it binds, so it is the one spelling the walk can see.
    /// </param>
    private static string UnbindableSecurityBlock(string spelling) => spelling switch
    {
        "scalar" => "      security: mtls\n",
        "bare" => "      security:\n",
        "empty" => "      security: {}\n",
        _ => throw new ArgumentOutOfRangeException(nameof(spelling), spelling, "Unknown spelling."),
    };

    /// <summary>
    /// <strong>Row 09d: the same hole reached through a <c>security</c> node that binds
    /// NOTHING — the shape Row 09b's fix alone did not close, and the one an author is most
    /// likely to write.</strong> Row 09b recovers the unbuilt document's bound environment and
    /// folds it into <c>SecuredTargets.Enumerate</c>. That walk reads bound <c>SecuritySpec</c>
    /// values, so for <c>security: mtls</c> — or a bare <c>security:</c> whose children are
    /// commented out — it yields nothing, <c>Declared</c> stays empty, and the conjunction
    /// <c>declared ∧ refused</c> answers false.
    /// <para>
    /// MEASURED before the schema arm was added, on the built CLI with no gating flags: the scalar
    /// spelling beside one parsing sibling exited 0 with no notice on BOTH run paths, while the
    /// same typo <em>alone</em> exited 4 through issue #278's all-parse-failure rule. The exit code
    /// was therefore NON-MONOTONE in the number of faults — adding an unrelated broken file to the
    /// suite turned the pipeline green.
    /// </para>
    /// <para>
    /// <c>UnbuiltDocument.Assure</c> closes it with the engine's own spelling rather than a new
    /// one: <c>DocumentValidator.Validate</c> plus <c>ScenarioRunner.RejectsASecurityDeclaration</c>
    /// — the two calls the schema door already makes for every document that DID become a scenario,
    /// applied to a document that door never iterates — recording
    /// <see cref="Vouchfx.Engine.Runtime.SecurityAbortKind.SecurityDeclarationRejected"/>, which
    /// raises unconditionally because there is by construction no declaration for it to be
    /// conjoined with.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null, "scalar")]
    [InlineData(1, "scalar")]
    [InlineData(null, "bare")]
    [InlineData(1, "bare")]
    [InlineData(null, "empty")]
    [InlineData(1, "empty")]
    public async Task Row09d_UnbindableSecurityInAnUnbuildableDocument_ExitsNonZeroWithTheNotice(
        int? parallel, string spelling)
    {
        var dir = WriteMixedBrokenSuite(
            $"r09d-{spelling}-{Tag(parallel)}",
            UnbindableSecurityBlock(spelling),
            UnknownStepTypeSteps);

        var sw = new StringWriter();
        var exitCode = await RunAsync(dir, parallel, sw);
        var rendered = sw.ToString();

        // Both files were still seen and both faults still reported.
        Assert.Contains("no registered provider", rendered, StringComparison.Ordinal);
        Assert.Contains("step 'call'", rendered, StringComparison.Ordinal);

        // …and the declaration is accounted for, on BOTH run paths.
        Assert.Contains("declares a 'security' block", rendered, StringComparison.Ordinal);
        Assert.NotEqual(ExitCodes.Success, exitCode);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    /// <summary>
    /// <strong>Row 09d's control, for the SCHEMA arm specifically.</strong> The same unbuildable
    /// file with NO <c>security</c> node at all still carries a schema error — it names a step type
    /// no provider registers — and that error is located at <c>/steps/0/type</c>, not in a
    /// declaration.
    /// <para>
    /// This row used to say it "must stay at 0", to stop the schema arm degenerating into "any
    /// unbuildable document reddens". #425 IS that degeneration, taken deliberately: a document the
    /// engine could not read cannot be reported clean whatever it might have asserted. So the exit
    /// code is 4 on both arms and no longer separates them; what this row still proves is that the
    /// schema arm does not print a security notice for a document that declared nothing.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Row 09b's unsecured control asserts the same exit for the same suite; this one asserts it
    /// against the SCHEMA arm specifically, which is a different mechanism and would not be
    /// exercised by a fix that only widened the walk.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row09d_SchemaErrorOutsideAnyDeclaration_ExitsInconclusiveOnTheUnreadFile(int? parallel)
    {
        var dir = WriteMixedBrokenSuite(
            $"r09d-control-{Tag(parallel)}", securityBlock: null, UnknownStepTypeSteps);

        var sw = new StringWriter();
        var exitCode = await RunAsync(dir, parallel, sw);
        var rendered = sw.ToString();

        Assert.Contains("no registered provider", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("declares a 'security' block", rendered, StringComparison.Ordinal);
        // #425: exit 4, not 0. The unread file is what reddens this, NOT the security question
        // — the "declares a 'security' block" notice is still absent, and the assertion above
        // pins that. A document the engine could not read is never reported as clean, whether or
        // not anything in the run mentions security.
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    /// <summary>
    /// <strong>Row 09e: a <c>--tag</c> filter no longer discards the recovered document.</strong>
    /// Selection runs in <c>RunCommand</c> BEFORE the split that hands unbuilt documents to the
    /// runner and matched on <c>Ast?.Metadata</c>, which every parse failure lacks — so every
    /// metadata filter excluded an unbuildable file, it contributed no declaration, and its parse
    /// error was not even printed. Measured on the built CLI: this suite exited 4 with the notice
    /// under a bare <c>run</c> and 0, silently, with <c>--tag smoke</c>.
    /// <para>
    /// The cause is the same catch as #411's: it recovered <c>doc.Environment</c> and discarded
    /// <c>doc.Metadata</c>, bound by the same <c>Parse</c> call. Both are recovered now and the
    /// selector answers from what the document actually says.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row09e_TagFilterMatchingTheUnbuildableDocument_StillExitsNonZero(int? parallel)
    {
        var dir = WriteTaggedMixedBrokenSuite($"r09e-{Tag(parallel)}", SecurityBlock());

        var sw = new StringWriter();
        var exitCode = await RunAsync(dir, parallel, sw, TagCriteria("smoke"));
        var rendered = sw.ToString();

        // The file is reported at all — the half of the defect that was pure silence.
        Assert.Contains("no registered provider", rendered, StringComparison.Ordinal);

        Assert.Contains("declares a 'security' block", rendered, StringComparison.Ordinal);
        Assert.NotEqual(ExitCodes.Success, exitCode);
    }

    /// <summary>
    /// <strong>Row 09e's control: a filter the recovered metadata genuinely does not satisfy still
    /// excludes the document, and that is correct rather than residual.</strong> Recovering the
    /// metadata makes the selector able to answer; it does not make every filter select. A user who
    /// asks for <c>--tag nosuchtag</c> has said which files to run, and a file carrying neither the
    /// tag nor any tag at all is not one of them.
    /// </summary>
    /// <remarks>
    /// This row is why the fix is "recover the metadata" rather than "exempt unbuildable documents
    /// from selection": the latter would also have made Row 09e pass, and would have made
    /// <c>--tag</c> mean something different for broken files than for working ones.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row09e_TagFilterMatchingNeitherFile_ExitsSuccess(int? parallel)
    {
        var dir = WriteTaggedMixedBrokenSuite($"r09e-miss-{Tag(parallel)}", SecurityBlock());

        var sw = new StringWriter();
        var exitCode = await RunAsync(dir, parallel, sw, TagCriteria("nosuchtag"));
        var rendered = sw.ToString();

        Assert.DoesNotContain("declares a 'security' block", rendered, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

    /// <summary>
    /// <strong>Row 09c pinned #425's gap, and now pins its closure.</strong> A malformed
    /// <c>.e2e.yaml</c> that plainly asserts <c>mtls</c>, beside a parseable sibling, used to exit
    /// <c>0</c> with no security notice: the YAML binds nothing, so no declaration exists to fold
    /// into the assurance, and the ordinary mixed-set path mapped the resulting Inconclusive to
    /// Success because <c>--fail-on-inconclusive</c> was not passed.
    /// <para>
    /// <strong>What closed it is not what this row predicted, and the difference is the point.</strong>
    /// This test used to argue the only two available fixes were both unacceptable: a raw-YAML scan
    /// for a <c>security:</c> key — a second spelling of "does this document declare security",
    /// forbidden by <c>SecuredTargets</c>' own header and by
    /// <see cref="Vouchfx.Engine.Runtime.SecurityAbortKind.SecurityDeclarationRejected"/>'s
    /// remarks — or failing closed, "which would redden every unsecured suite that merely contains
    /// an unreadable file". Both readings assumed the fix had to answer the SECURITY question.
    /// </para>
    /// <para>
    /// It did not. <c>RunCommand.ComputeExitCode</c> now treats any parse failure as never-clean,
    /// so this file reddens the run because it could not be READ — a fact available without
    /// parsing it, without scanning it, and without asking what it declared. The security notice
    /// is still absent and this row still asserts that, because nothing here confirms or refuses a
    /// declaration; the assurance machinery is untouched. The consequence the old rationale called
    /// unacceptable — an unsecured suite containing an unreadable file now reddens — was accepted
    /// deliberately: an unread file is a deterministic authoring fault, and #278 already held that
    /// CI must never see an unparseable suite reported as clean. Rows 09b and 09d are that same
    /// consequence, pinned on the unsecured and schema arms.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task Row09c_SecuredMalformedYamlBesideAParseableSibling_ExitsInconclusiveOnTheUnreadFile(
        int? parallel)
    {
        var dir = WriteMixedBrokenSuite(
            $"r09c-{Tag(parallel)}", SecurityBlock(), MalformedYamlSteps);

        var sw = new StringWriter();
        var exitCode = await RunAsync(dir, parallel, sw);
        var rendered = sw.ToString();

        // The YAML scanner's own refusal — NOT AstBuilder's, which never ran.
        Assert.Contains("Parse / AST error", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("no registered provider", rendered, StringComparison.Ordinal);
        Assert.Contains("step 'call'", rendered, StringComparison.Ordinal);

        // Still NO security notice: the fix does not answer the security question, and claiming
        // it did would be the second spelling this row's own rationale forbids.
        Assert.DoesNotContain("declares a 'security' block", rendered, StringComparison.Ordinal);

        // …and no longer a green build. This is the #425 assertion.
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
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
