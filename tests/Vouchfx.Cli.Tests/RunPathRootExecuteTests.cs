// Vouchfx.Cli.Tests — ExecuteAsync path-root behaviour (single-file discovery root). No Docker.
//
// Exercises RunCommand.ExecuteAsync's Docker-free early paths for the <path> positional:
// a missing root and a wrong-extension file root map to a usage error (exit 2) with the
// discovery message written verbatim; a valid single-file root flows through selection
// ("of 1 discovered") and the parse-failure fold.  A single-file root that fails to parse
// is, by construction, an ENTIRELY-parse-failure set (parsedCount == 0) — issue #278 makes
// that unconditionally Inconclusive (exit 4), regardless of --fail-on-inconclusive; see
// RunCommand.ComputeExitCode.  Every case returns before the runner block, so no topology
// is ever started (this file needs no Docker).

using Vouchfx.Cli;
using Vouchfx.Cli.Selection;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class RunPathRootExecuteTests : IDisposable
{
    private readonly string _root;

    public RunPathRootExecuteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vouchfx-cli-tests-" + Guid.NewGuid().ToString("n"));
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

    /// <summary>
    /// The security notice's text, SPELLED OUT here rather than read from
    /// <see cref="RunCommand.SecurityUnconfirmableNotice"/>. The independent copy is the whole
    /// assertion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserting <c>Contains(RunCommand.SecurityUnconfirmableNotice, rendered)</c> compares the
    /// output against the constant that produced it, so it holds for ANY value of that constant —
    /// setting it to the empty string passes every such assertion. That is the same failure mode,
    /// one turn further on, as the truncated assertion recorded below: an assertion that stops
    /// pinning the sentence is where the next falsehood ships. MEASURED: with the constant set to
    /// <c>""</c> the three tests below go red on this copy and green on the self-referential one.
    /// </para>
    /// <para>
    /// The cost of the copy is that a deliberate rewording touches two files. That is the intended
    /// cost: this line is user-facing prose whose every clause must be true on every path that
    /// reaches the print, and changing it should be a decision, not a rename.
    /// </para>
    /// </remarks>
    private const string ExpectedSecurityNotice =
        "This suite declares a 'security' block that this run could not confirm, so it exits "
        + "non-zero whatever the fault reported above was: a run that cannot confirm a declared "
        + "security assertion cannot vouch for it. Each door reports only the faults it reached, so "
        + "what is reported above need not be the last fix before a run can confirm this suite's "
        + "security block.";

    private const string MinimalValidScenario =
        "metadata:\n" +
        "  name: minimal\n" +
        "  tags: [smoke]\n" +
        "steps:\n" +
        "  - id: call-api\n" +
        "    type: http.rest\n";

    private static Task<int> ExecuteAsync(
        string path,
        SelectionCriteria? criteria,
        TextWriter output,
        bool failOnInconclusive = false)
        => RunCommand.ExecuteAsync(
            path: path,
            criteria: criteria ?? SelectionCriteria.None,
            parallel: null,
            watch: false,
            failOnEnvironmentError: false,
            failOnInconclusive: failOnInconclusive,
            htmlReportPath: null,
            junitReportPath: null,
            eventsReportPath: null,
            eventsStreamPath: null,
            decorate: false,
            output: output,
            telemetryHook: null,
            cancellationToken: default);

    [Fact]
    public async Task ExecuteAsync_MissingRoot_ReturnsUsageError_AndWritesNotFoundMessage()
    {
        var sw = new StringWriter();
        var missing = Path.Combine(_root, "does-not-exist");

        var exitCode = await ExecuteAsync(missing, criteria: null, sw);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains("does not exist", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WrongExtensionFileRoot_ReturnsUsageError_AndWritesSuffixMessage()
    {
        var sw = new StringWriter();
        var wrong = Path.Combine(_root, "scenario.txt");
        File.WriteAllText(wrong, MinimalValidScenario);

        var exitCode = await ExecuteAsync(wrong, criteria: null, sw);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains(".e2e.yaml", sw.ToString(), StringComparison.Ordinal);
    }

    // Copilot review finding #1 parity (#260 follow-up): RunCommand.ExecuteAsync's discovery
    // catch was broadened in lock-step with ValidateCommand.Execute's (see
    // ValidateCommandTests.Execute_PathContainsNullCharacter_* for the full rationale). A NUL
    // character is the deterministic, cross-platform trigger: Path.GetFullPath unconditionally
    // rejects it (ArgumentException) on every .NET platform/OS.
    [Fact]
    public async Task ExecuteAsync_PathContainsNullCharacter_ReturnsUsageError_WithoutUnhandledException()
    {
        var sw = new StringWriter();
        var badPath = "bad\0path";

        var exitCode = await ExecuteAsync(badPath, criteria: null, sw);

        Assert.Equal(ExitCodes.UsageError, exitCode);
    }

    [Fact]
    public async Task ExecuteAsync_FileRoot_TagFilterMatchesNothing_ReturnsSuccessWithMessage()
    {
        var sw = new StringWriter();
        var file = Path.Combine(_root, "one.e2e.yaml");
        File.WriteAllText(file, MinimalValidScenario);

        var criteria = SelectionCriteria.None with { Tags = new[] { "no-such-tag" } };
        var exitCode = await ExecuteAsync(file, criteria, sw);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("of 1 discovered", sw.ToString(), StringComparison.Ordinal);
    }

    // Issue #278: a single-file root that fails to parse is — by construction — an ENTIRELY
    // parse-failure set (nothing parsed, nothing ran), so it must exit Inconclusive (4)
    // UNCONDITIONALLY, regardless of --fail-on-inconclusive. This replaces the prior
    // "exit 0 by default, exit 4 only when --fail-on-inconclusive" pair of tests: that
    // default-exit-0 behaviour was exactly the bug (a CI pipeline keying on `run`'s exit code
    // treated a fully-unparseable suite as passing).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_FileRoot_AllParseFailure_ReturnsInconclusive_RegardlessOfFailOnInconclusiveFlag(
        bool failOnInconclusive)
    {
        var sw = new StringWriter();
        var bad = Path.Combine(_root, "broken.e2e.yaml");
        File.WriteAllText(bad, "steps:\n  - id: x\n    type: not-a-real-provider\n");

        var exitCode = await ExecuteAsync(bad, criteria: null, sw, failOnInconclusive: failOnInconclusive);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        Assert.Contains("(Inconclusive)", sw.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #266, Item 4: <c>AstBuilder</c>'s unknown-step-type message splices the
    /// declared, dotted step <c>type:</c> straight from the document verbatim
    /// (<c>"unknown step type '{raw}' — no registered provider"</c>), and this is the MOST
    /// reachable human-terminal surface for hostile suite content — a plain <c>vouchfx run</c>
    /// on a malformed suite reaches <see cref="RunCommand"/>'s parse-failure loop with no
    /// flag needed. The file on disk stays plain ASCII (a YAML double-quoted scalar's
    /// <c>\x1B</c> escape is the spec-compliant way to encode a C0 control character — an
    /// UNESCAPED control byte is not valid YAML content and would fail at the YAML-syntax
    /// stage before ever reaching AstBuilder); YamlDotNet decodes the escape back to the raw
    /// ESC character during parsing, which is exactly the point at which the hostile text
    /// must already be inert by the time it reaches the terminal.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_FileRoot_ParseFailure_HostileTypeWithAnsiSequence_RendersInert()
    {
        var sw = new StringWriter();
        var bad = Path.Combine(_root, "hostile-type.e2e.yaml");
        File.WriteAllText(
            bad,
            "steps:\n"
            + "  - id: call-api\n"
            + "    type: \"hostile.provider\\x1B[31mHACKED\\x1B[0m\"\n");

        var exitCode = await ExecuteAsync(bad, criteria: null, sw);

        // Issue #278: this is ALSO an entirely-parse-failure single-file set, so it is now
        // unconditionally Inconclusive (4), not Success (0).
        Assert.Equal(ExitCodes.Inconclusive, exitCode);

        var rendered = sw.ToString();
        Assert.Contains("(Inconclusive)", rendered, StringComparison.Ordinal);
        // The surrounding diagnostic text survives sanitisation intact...
        Assert.Contains("HACKED", rendered, StringComparison.Ordinal);
        Assert.Contains("no registered provider", rendered, StringComparison.Ordinal);
        // ...but no raw ESC byte reaches the terminal.
        Assert.DoesNotContain((char)0x1B, rendered);
    }

    // ── REQ-018 on the `run` path: a security-block fault exits non-zero with NO flag (#387/#390) ──
    //
    // These pin the carve-out where it actually lives. `vouchfx validate` already exits 4 for any
    // invalid document, so a validate-path test cannot distinguish "carved out" from "invalid";
    // `run` can, and MEASURED before the fix it did:
    //
    //   REQ-011 refusal in clientKey ............................. exit 4
    //   EDGE-003 unknown source in clientKeyPassword ............. exit 0   ← the defect
    //   BOTH faults in one document ............................. exit 0   ← and it MASKED the 4
    //
    // The masking is the reason the third case is here as its own test rather than as a corollary:
    // on this path the secret-reference scan runs BEFORE ProviderPipeline.Compile, so it short-
    // circuits the REQ-011 refusal that had been setting the flag. Adding a fault made the build
    // greener, which is the failure mode REQ-018 exists to prevent.
    //
    // No topology is started for any of them: every scenario carries an early verdict, so
    // RunSuiteAsync's all-early guard returns before the Aspire build (this file needs no Docker).

    private const string SecuredScenarioHead =
        "environment:\n" +
        "  services:\n" +
        "    api:\n" +
        "      image: myorg/api:1.0\n" +
        "      security:\n" +
        "        profile: mtls\n" +
        "        endpoint: 8443\n";

    private const string SecuredScenarioTail =
        "steps:\n" +
        "  - id: call\n" +
        "    type: http.rest\n" +
        "    target: api\n" +
        "    method: GET\n" +
        "    path: /health\n";

    /// <summary>Writes a suite whose <c>security</c> block carries the supplied field lines.</summary>
    private string WriteSecuredScenario(string fileName, string securityFieldLines)
    {
        // The path-valued fields must name real files unless the test's own fault is one of them.
        File.WriteAllText(Path.Combine(_root, "client.pem"), "placeholder");
        File.WriteAllText(Path.Combine(_root, "client.key"), "placeholder");

        var file = Path.Combine(_root, fileName);
        File.WriteAllText(file, SecuredScenarioHead + securityFieldLines + SecuredScenarioTail);
        return file;
    }

    /// <summary>
    /// Drives <see cref="RunCommand.ExecuteAsync"/> at a chosen <c>--parallel</c> value. The two
    /// run paths (<c>RunSuiteAsync</c> and <c>ParallelSuiteRunner</c>) reach these pre-topology
    /// doors differently, and the first BLOCKER in this series was a masking defect on ONE path,
    /// so every case below is exercised on both.
    /// </summary>
    private static Task<int> ExecuteAtParallelismAsync(string path, int? parallel, TextWriter output) =>
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

    /// <summary>
    /// EDGE-003 alone. Before the fix this exited 0 — a suite that verified nothing, reported as a
    /// green build.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task ExecuteAsync_UnknownSecretSourceInClientKeyPassword_ExitsNonZeroWithoutAnyFlag(
        int? parallel)
    {
        var sw = new StringWriter();
        var file = WriteSecuredScenario(
            "edge003.e2e.yaml",
            "        clientCert: ./client.pem\n" +
            "        clientKey: ./client.key\n" +
            "        clientKeyPassword: \"${secret:nosuchsource/KEY_PASS}\"\n");

        var exitCode = await ExecuteAtParallelismAsync(file, parallel, sw);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        Assert.Contains(
            "environment.services.api.security.clientKeyPassword:", sw.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// REQ-011 alone. This arm exited 4 before the fix too, but only by inheriting
    /// <c>ValidationFailure.IsSecurityPreflight</c> through the preflight door — nothing tested it,
    /// so nothing would have caught its loss.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task ExecuteAsync_SecretReferenceInClientKey_ExitsNonZeroWithoutAnyFlag(int? parallel)
    {
        var sw = new StringWriter();
        var file = WriteSecuredScenario(
            "req011.e2e.yaml",
            "        clientCert: ./client.pem\n" +
            "        clientKey: \"${secret:env/CLIENT_KEY}\"\n");

        var exitCode = await ExecuteAtParallelismAsync(file, parallel, sw);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);

        // The refusal reaches the terminal with its containment claim and the sigil it names
        // intact — the run path renders ValidationFailure.Message verbatim, so a message that
        // over-claimed here would over-claim to the author. "…is a secret reference" is asserted
        // absent because the guard behind this text is a `Contains` test, and the identity claim
        // is false for a path that merely carries a token.
        var rendered = sw.ToString();
        Assert.Contains("uses secret-reference syntax ('${secret:')", rendered, StringComparison.Ordinal);
        Assert.Contains("whole or embedded in a longer path", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("is a secret reference", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both faults in one document. The anti-masking case: adding the EDGE-003 fault to the
    /// REQ-011 suite must not make the run greener than the REQ-011 suite alone.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task ExecuteAsync_BothSecurityFaultsInOneDocument_StillExitsNonZero(int? parallel)
    {
        var sw = new StringWriter();
        var file = WriteSecuredScenario(
            "both.e2e.yaml",
            "        clientCert: ./client.pem\n" +
            "        clientKey: \"${secret:env/CLIENT_KEY}\"\n" +
            "        clientKeyPassword: \"${secret:nosuchsource/KEY_PASS}\"\n");

        var exitCode = await ExecuteAtParallelismAsync(file, parallel, sw);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    /// <summary>
    /// The carve-out stays NARROW: the same bad reference in a STEP is an ordinary authoring
    /// error, exits 0 without <c>--fail-on-inconclusive</c>, and 4 with it. Without this control
    /// the three tests above would equally pass an implementation that flagged every secret-
    /// reference failure.
    /// </summary>
    [Theory]
    [InlineData(false, ExitCodes.Success, null)]
    [InlineData(true, ExitCodes.Inconclusive, null)]
    [InlineData(false, ExitCodes.Success, 1)]
    [InlineData(true, ExitCodes.Inconclusive, 1)]
    public async Task ExecuteAsync_UnknownSecretSourceInAStep_IsNotCarvedOut(
        bool failOnInconclusive, int expectedExitCode, int? parallel)
    {
        var sw = new StringWriter();
        var file = Path.Combine(_root, "step-secret.e2e.yaml");
        File.WriteAllText(file, StepFaultScenario);

        var exitCode = await RunCommand.ExecuteAsync(
            path: file,
            criteria: SelectionCriteria.None,
            parallel: parallel,
            watch: false,
            failOnEnvironmentError: false,
            failOnInconclusive: failOnInconclusive,
            htmlReportPath: null,
            junitReportPath: null,
            eventsReportPath: null,
            eventsStreamPath: null,
            decorate: false,
            output: sw,
            telemetryHook: null,
            cancellationToken: default);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Contains("step 'call'", sw.ToString(), StringComparison.Ordinal);
    }

    private const string StepFaultScenario =
        "environment:\n" +
        "  services:\n" +
        "    api:\n" +
        "      image: myorg/api:1.0\n" +
        "steps:\n" +
        "  - id: call\n" +
        "    type: http.rest\n" +
        "    target: api\n" +
        "    method: GET\n" +
        "    path: /health\n" +
        "    headers:\n" +
        "      Authorization: \"Bearer ${secret:nosuchsource/TOKEN}\"\n";

    // ── A STEP fault must not MASK a SECURITY fault (#387/#390 follow-up) ──────────────────
    //
    // The four combinations, because the defect lived in exactly one of them and three-quarters
    // coverage found nothing. MEASURED before the fix, real CLI:
    //
    //   step-only ......... exit 0   (correct — an ordinary authoring error)
    //   security-only ..... exit 4   (correct)
    //   step + security ... exit 0   ← the defect: adding a fault made the build GREENER
    //
    // Cause: the secret-reference pass returned on the FIRST fault, so a step fault short-
    // circuited the security walk and the REQ-018 flag was never computed. The flag now answers
    // "does this document contain a security-declaration secret fault?" — a property of the
    // document — so the search order cannot change it.
    //
    // Both `--parallel` values are exercised: the two run paths reach these doors differently,
    // and the original BLOCKER in this series was a masking defect on one path only.

    private const string StepAndSecurityFaultSecurityLines =
        "        clientCert: ./client.pem\n" +
        "        clientKey: ./client.key\n" +
        "        clientKeyPassword: \"${secret:nosuchsource/PASS}\"\n";

    private const string StepFaultHeaderLines =
        "    headers:\n" +
        "      Authorization: \"Bearer ${secret:nosuchsource/STEP_TOKEN}\"\n";

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task ExecuteAsync_StepFaultDoesNotMaskASecurityFault(int? parallel)
    {
        File.WriteAllText(Path.Combine(_root, "client.pem"), "placeholder");
        File.WriteAllText(Path.Combine(_root, "client.key"), "placeholder");
        var file = Path.Combine(_root, "step-and-security.e2e.yaml");
        File.WriteAllText(
            file,
            SecuredScenarioHead + StepAndSecurityFaultSecurityLines
            + SecuredScenarioTail + StepFaultHeaderLines);

        var sw = new StringWriter();
        var exitCode = await ExecuteAtParallelismAsync(file, parallel, sw);
        var rendered = sw.ToString();

        Assert.Equal(ExitCodes.Inconclusive, exitCode);

        // BOTH faults are reported, not just the one that happened to be found first. Before this,
        // two documents differing only in the presence of the security fault produced BYTE-
        // IDENTICAL output and differed only in exit code — the fault that turned the build red
        // was never printed, so the red was unexplained.
        Assert.Contains("step 'call'", rendered, StringComparison.Ordinal);
        Assert.Contains(
            "environment.services.api.security.clientKeyPassword:", rendered, StringComparison.Ordinal);
    }

    // ── A STEP fault vs a security PREFLIGHT fault — the row #399 was filed for, now FIXED ──
    //
    // The combination the matrix above never reached. Everything above pairs a step fault with a
    // security-block SECRET fault, which both run paths compute in the SAME pass; this pairs it
    // with a security PREFLIGHT fault (a clientCert file that does not exist, REQ-003/REQ-004),
    // which only ProviderPipeline.Compile computes.
    //
    // RETRACTED — this block used to end by stating, in the present tense, that the two run paths
    // called Compile at different points relative to the step secret pass (--parallel: Compile
    // first; the default `run` path: secret pass first) and that on the default path "the step
    // fault `continue`s before the preflight fault is ever computed, and the refusal is neither
    // reported nor counted". That was the measured DEFECT, and it is fixed: the two passes are now
    // ONE door on both paths, running both and reporting both, so both faults print and both paths
    // exit 4. The account of the pre-fix behaviour lives on the test below, in the past tense where
    // it belongs; it is deleted from here because a present-tense claim of a repaired defect twelve
    // lines above the paragraph announcing the repair is a contradiction a reader has to arbitrate.

    private const string PreflightFaultScenario =
        "environment:\n" +
        "  services:\n" +
        "    api:\n" +
        "      image: myorg/api:1.0\n" +
        "      security:\n" +
        "        profile: mtls\n" +
        "        endpoint: 8443\n" +
        // The security fault: a clientCert naming a file that is not there. Inside the suite
        // directory, so this is REQ-004's existence refusal (IsSecurityPreflight), not REQ-003's
        // containment one — the door is ProviderPipeline.Compile, NOT the secret pass.
        "        clientCert: ./no-such-cert.pem\n" +
        "        clientKey: ./client.key\n" +
        "steps:\n" +
        "  - id: call\n" +
        "    type: http.rest\n" +
        "    target: api\n" +
        "    method: GET\n" +
        "    path: /health\n" +
        // The step fault, in the same document.
        "    headers:\n" +
        "      Authorization: \"Bearer ${secret:nosuchsource/STEP_TOKEN}\"\n";

    /// <summary>
    /// <strong>This test used to assert a DEFECT.</strong> It recorded the then-measured behaviour
    /// of {step fault} × {security PREFLIGHT fault} on both run paths, because they disagreed and
    /// one of them was wrong. Filed as <strong>#399</strong>, and fixed — what follows is the
    /// account of what it caught, kept because a reader meeting the row needs to know why it is
    /// written the way it is.
    /// <para>
    /// MEASURED BEFORE THE FIX, real CLI, this exact document:
    /// </para>
    /// <list type="table">
    /// <item><description>preflight fault alone, no <c>--parallel</c> ....... exit 4, refusal printed</description></item>
    /// <item><description>preflight fault alone, <c>--parallel 1</c> ........ exit 4, refusal printed</description></item>
    /// <item><description>plus a step fault, <c>--parallel 1</c> ............ exit 4, refusal printed</description></item>
    /// <item><description>plus a step fault, no <c>--parallel</c> (DEFAULT) . <strong>exit 0, refusal NEVER printed</strong></description></item>
    /// </list>
    /// <para>
    /// The last row is the defect in its plainest form: ADDING a fault turns a red build green, on
    /// the path the CLI takes by default. It is the same masking shape already fixed at the schema
    /// door (see <c>ExecuteAsync_SecurityFaultPlusUnrelatedSchemaError_StillExitsNonZero</c>) and at
    /// the security-secret door (<c>ExecuteAsync_StepFaultDoesNotMaskASecurityFault</c>), surviving
    /// at a third.
    /// </para>
    /// <para>
    /// <strong>Why the current behaviour is asserted rather than the arm being <c>Skip</c>ped.</strong>
    /// A skipped arm executes nothing, so when #399 is fixed nothing goes red to say this row must
    /// change — and this file already carries the scar of a test that pinned a decision by not
    /// exercising it (see the vacuous-tag-filter note on
    /// <c>ExecuteAsync_SecuredSuiteWithNoSecretFault_IsNotCarvedOut</c>). Asserted, the row is a
    /// TRIPWIRE: the fix makes it fail here, at a named line, with the required end state written
    /// out below.
    /// </para>
    /// <para>
    /// <strong>#399 IS FIXED, AND THE PER-PATH BRANCH IS DELETED</strong> — the end state this
    /// test's own instructions named: BOTH arms expect <c>ExitCodes.Inconclusive</c> and BOTH
    /// report BOTH faults, exactly as <c>ExecuteAsync_StepFaultDoesNotMaskASecurityFault</c>
    /// already does for the secret-fault pairing. The <c>null</c> arm is KEPT, deliberately:
    /// narrowing the row to the parallel path is how this gap escaped four review gates in the
    /// first place, and it is the arm that carried the defect.
    /// </para>
    /// <para>
    /// What changed underneath: the two pre-topology authoring passes (provider-pipeline compile
    /// and the secret-reference walk) were two sequential doors that the two run paths ran in
    /// OPPOSITE orders, each stopping at its first fault. They are now one door that runs both
    /// passes and reports both faults, so which fault an author is shown is a property of the
    /// document rather than of which path they invoked.
    /// </para>
    /// <para>
    /// The MESSAGE is asserted on both arms, in both directions. Several doors on this surface
    /// produce exit 4, and a malformed fixture produces 4 via the all-parse-failure rule, so an exit
    /// code alone would not prove which door ran — and on the failing arm the exit code is 0, where
    /// the only evidence of the masking is the refusal's ABSENCE from the output.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task ExecuteAsync_StepFaultMasksASecurityPreflightFault_Issue399(int? parallel)
    {
        // clientCert is deliberately NOT created; clientKey is, so the preflight refusal names
        // the cert and nothing else.
        File.WriteAllText(Path.Combine(_root, "client.key"), "placeholder");
        var file = Path.Combine(_root, "step-and-preflight.e2e.yaml");
        File.WriteAllText(file, PreflightFaultScenario);

        var sw = new StringWriter();
        var exitCode = await ExecuteAtParallelismAsync(file, parallel, sw);
        var rendered = sw.ToString();

        // ── The end state, identical on both paths ─────────────────────────────────────────
        // The document has two faults; both are computed, both are reported, and the exit code no
        // longer depends on which pass ran first.
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        Assert.Contains("no-such-cert.pem", rendered, StringComparison.Ordinal);
        Assert.Contains("step 'call'", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control for the row above: the SAME security preflight fault with NO step fault exits 4
    /// on BOTH paths. Without it, the failing arm's exit 0 could be misread as "a missing clientCert
    /// simply does not redden a build", when the measured cause is the step fault masking it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task ExecuteAsync_SecurityPreflightFaultAlone_ExitsNonZeroOnBothPaths(int? parallel)
    {
        File.WriteAllText(Path.Combine(_root, "client.key"), "placeholder");
        var file = Path.Combine(_root, "preflight-only.e2e.yaml");
        File.WriteAllText(
            file,
            PreflightFaultScenario.Replace(
                "    headers:\n"
                + "      Authorization: \"Bearer ${secret:nosuchsource/STEP_TOKEN}\"\n",
                string.Empty,
                StringComparison.Ordinal));

        var sw = new StringWriter();
        var exitCode = await ExecuteAtParallelismAsync(file, parallel, sw);
        var rendered = sw.ToString();

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        Assert.Contains("no-such-cert.pem", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("step 'call'", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fourth combination — a secured suite with NO secret fault — so the cases above cannot be
    /// satisfied by an implementation that simply exits non-zero for any secured suite.
    /// <para>
    /// An earlier form of this test used a tag filter matching nothing, and was VACUOUS: selection
    /// short-circuits before compilation, so zero scenarios ran and no door was reached — the same
    /// filter applied to a document with a genuine EDGE-003 fault also exits 0. It is instead
    /// driven with a document whose scenario carries a NON-security early verdict (an unresolvable
    /// <c>script.csharp</c> <c>file:</c> reference), so the pre-topology doors DO run, the flag
    /// stays false, and no topology is built.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task ExecuteAsync_SecuredSuiteWithNoSecretFault_IsNotCarvedOut(int? parallel)
    {
        File.WriteAllText(Path.Combine(_root, "client.pem"), "placeholder");
        File.WriteAllText(Path.Combine(_root, "client.key"), "placeholder");
        var file = Path.Combine(_root, "clean-secured.e2e.yaml");
        File.WriteAllText(
            file,
            SecuredScenarioHead +
            "        clientCert: ./client.pem\n" +
            "        clientKey: ./client.key\n" +
            "        clientKeyPassword: \"${secret:env/CLIENT_KEY_PASS}\"\n" +
            "steps:\n" +
            "  - id: helper\n" +
            "    type: script.csharp\n" +
            "    file: no-such-helper.csx\n");

        var sw = new StringWriter();
        var exitCode = await ExecuteAtParallelismAsync(file, parallel, sw);
        var rendered = sw.ToString();

        // The doors genuinely ran: the pipeline reported the missing file, which only happens
        // downstream of the schema and secret-reference doors this suite is testing.
        Assert.Contains("no-such-helper.csx", rendered, StringComparison.Ordinal);

        // …and the flag stayed false, so REQ-018's carve-out did NOT fire: exit 0 without
        // --fail-on-inconclusive, despite `security` being declared.
        Assert.Equal(ExitCodes.Success, exitCode);
    }

    /// <summary>
    /// The FIFTH combination, and the one that shipped broken: a security fault plus an UNRELATED
    /// schema error. MEASURED before the fix — deleting one required key from a step (so the schema
    /// error lands at <c>/steps/0</c>, outside the <c>security</c> block) took this suite from exit
    /// 4 to exit 0, because the schema door <c>continue</c>s past both the secret pass and the
    /// provider pipeline, and the located-inside-the-block test answers false for <c>/steps/0</c>.
    /// <para>
    /// For a single-file secured suite — the common shape — one typo anywhere in the document was
    /// enough to turn every security refusal green.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task ExecuteAsync_SecurityFaultPlusUnrelatedSchemaError_StillExitsNonZero(int? parallel)
    {
        File.WriteAllText(Path.Combine(_root, "client.pem"), "placeholder");
        File.WriteAllText(Path.Combine(_root, "client.key"), "placeholder");
        var file = Path.Combine(_root, "sec-plus-schema.e2e.yaml");
        File.WriteAllText(
            file,
            SecuredScenarioHead +
            "        clientCert: ./client.pem\n" +
            "        clientKey: ./client.key\n" +
            "        clientKeyPassword: \"${secret:nosuchsource/KEY_PASS}\"\n" +
            // 'method' omitted — a REQUIRED key, so the schema error is located at /steps/0,
            // deliberately OUTSIDE the security block.
            "steps:\n" +
            "  - id: call\n" +
            "    type: http.rest\n" +
            "    target: api\n" +
            "    path: /health\n");

        var sw = new StringWriter();
        var exitCode = await ExecuteAtParallelismAsync(file, parallel, sw);

        // The MESSAGE is asserted as well as the code: three different doors on this surface all
        // produce exit 4, and an all-parse-failure suite produces it too, so a bare code assertion
        // would pass against a fixture that never reached the door under test.
        Assert.Contains("method", sw.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    /// <summary>
    /// The behaviour change this broadening makes, asserted as an intended decision rather than
    /// left to be inferred from a comment about masking: a secured suite with NO security fault at
    /// all — valid <c>clientKeyPassword</c>, real certificate files — now exits non-zero when the
    /// document carries an INNOCENT schema error elsewhere. It exited 0 before.
    /// <para>
    /// That is REQ-018's documented meaning applied literally ("a declared <c>security</c> block
    /// could not be confirmed"): the document is rejected before any container starts, so the
    /// declared assertion is never confirmed, whatever the rejection was about. It is deliberately
    /// "declares security at all" and NOT "a security refusal was masked" — the narrower predicate
    /// is exactly what let a schema error at <c>/steps/0</c> turn every security refusal green, and
    /// there is no way to tell a masked refusal from an unmasked one at that door without running
    /// the very passes the rejection has already skipped.
    /// </para>
    /// <para>
    /// User-visible: one typo anywhere in a secured suite now reddens a build that was green. Both
    /// run paths, and both directions — the unsecured control below proves it is not collateral.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task ExecuteAsync_SecuredSuiteWithInnocentSchemaError_ExitsNonZero(int? parallel)
    {
        File.WriteAllText(Path.Combine(_root, "client.pem"), "placeholder");
        File.WriteAllText(Path.Combine(_root, "client.key"), "placeholder");
        var file = Path.Combine(_root, "innocent-schema.e2e.yaml");
        File.WriteAllText(
            file,
            SecuredScenarioHead +
            "        clientCert: ./client.pem\n" +
            "        clientKey: ./client.key\n" +
            "        clientKeyPassword: \"${secret:env/CLIENT_KEY_PASS}\"\n" +
            // The ONLY defect: 'method' omitted. Nothing about security is wrong here.
            "steps:\n" +
            "  - id: call\n" +
            "    type: http.rest\n" +
            "    target: api\n" +
            "    path: /health\n");

        var sw = new StringWriter();
        var exitCode = await ExecuteAtParallelismAsync(file, parallel, sw);
        var rendered = sw.ToString();

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        Assert.Contains("method", rendered, StringComparison.Ordinal);

        // …and the exit code is EXPLAINED. Without this the run exited 4 showing only the schema
        // error, so the reason for the non-zero exit was invisible — an unexplained red.
        // The FULL wording, not truncated one clause early. An earlier form of this assertion
        // stopped at "exits non-zero", which is exactly where the sentence stopped being pinned —
        // and the next clause then shipped a falsehood for an in-block schema error.
        //
        // RETRACTED WORDING, recorded rather than silently replaced (security-assurance-derivation).
        // Until this change the notice read:
        //
        //     "…so it exits non-zero even though nothing reported above lies inside that block:
        //      the document was rejected before its security declaration could be validated at
        //      all…"
        //
        // Both halves are now false where the notice fires. The out-of-block clause distinguished
        // two SCHEMA-door cases, and this is no longer a schema-door line — one notice is emitted
        // at the single site that reads the assurance, and it covers doors that are not the schema
        // one. The "before its security declaration could be validated at all" clause was
        // deliberately narrow BECAUSE two later doors (the provider pipeline and the step secret
        // pass) also refused a secured suite before any container started and exited 0; both now
        // exit non-zero, so the broad statement is the true one. The rule the test pins is
        // unchanged: a secured suite refused pre-topology exits non-zero and says why.
        //
        // A SECOND RETRACTION, of a clause that was measured false rather than merely narrow: the
        // notice used to open "was refused before any container started". With no flags and exit 3
        // the engine printed `Current State: Running` with a `Start Time` immediately above it, and
        // under --parallel a sibling slot's containers can be up and past their health gate. The
        // line now states the property the assurance record can actually vouch for.
        Assert.Contains(ExpectedSecurityNotice, rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The schema error INSIDE the <c>security</c> block. The notice must not then claim the error
    /// "is not itself inside that block" — measured saying exactly that for
    /// <c>profile: bogusprofile</c>, whose error is located in the block by definition.
    /// <para>
    /// The defect this pins is now structurally impossible rather than conditionally avoided: there
    /// is ONE notice, and it makes no claim about where the reported fault sits. The
    /// <c>DoesNotContain</c> below is therefore what carries the test — it would still catch a
    /// later reintroduction of the clause.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task ExecuteAsync_SchemaErrorInsideTheSecurityBlock_NoticeDoesNotClaimItIsOutside(
        int? parallel)
    {
        File.WriteAllText(Path.Combine(_root, "client.pem"), "placeholder");
        File.WriteAllText(Path.Combine(_root, "client.key"), "placeholder");
        var file = Path.Combine(_root, "in-block-schema.e2e.yaml");
        File.WriteAllText(
            file,
            "environment:\n" +
            "  services:\n" +
            "    api:\n" +
            "      image: myorg/api:1.0\n" +
            "      security:\n" +
            // The defect is IN the security block: an unregistered profile name.
            "        profile: bogusprofile\n" +
            "        endpoint: 8443\n" +
            "steps:\n" +
            "  - id: call\n" +
            "    type: http.rest\n" +
            "    target: api\n" +
            "    method: GET\n" +
            "    path: /health\n");

        var sw = new StringWriter();
        var exitCode = await ExecuteAtParallelismAsync(file, parallel, sw);
        var rendered = sw.ToString();

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        Assert.Contains("bogusprofile", rendered, StringComparison.Ordinal);

        // The notice still appears — the exit code still needs explaining…
        Assert.Contains(ExpectedSecurityNotice, rendered, StringComparison.Ordinal);

        // …but WITHOUT the clause that would misdescribe the author's own document.
        Assert.DoesNotContain("lies inside that block", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// A schema error located AT the <c>security</c> node that binds NO <c>SecuritySpec</c>:
    /// <c>security: mtls</c>, the realistic typo of writing the profile name where the block
    /// belongs. The flag fires (the error is inside the block) but <c>SecuredTargets.Any</c> is
    /// false, so gating the notice on "declares security" alone produced exit 4 in silence —
    /// the unexplained red this series has fixed three times, in its third disguise.
    /// <para>
    /// The notice and the flag are now driven by ONE disjunction, so this shape cannot exit
    /// non-zero without saying why. The opening clause stays true: the document does carry a
    /// <c>security</c> key, whatever it failed to bind to.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task ExecuteAsync_SecurityKeyThatBindsNoBlock_ExitsNonZeroWithTheNotice(int? parallel)
    {
        var file = Path.Combine(_root, "security-scalar.e2e.yaml");
        File.WriteAllText(
            file,
            "environment:\n" +
            "  services:\n" +
            "    api:\n" +
            "      image: myorg/api:1.0\n" +
            // A scalar where the block belongs: schema error AT the node, no SecuritySpec bound.
            "      security: mtls\n" +
            "steps:\n" +
            "  - id: call\n" +
            "    type: http.rest\n" +
            "    target: api\n" +
            "    method: GET\n" +
            "    path: /health\n");

        var sw = new StringWriter();
        var exitCode = await ExecuteAtParallelismAsync(file, parallel, sw);
        var rendered = sw.ToString();

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        Assert.Contains("should be \"object\"", rendered, StringComparison.Ordinal);

        // The exit code is explained. The wording no longer varies with where the fault sits —
        // there is one notice, at the one site that reads the assurance — so what this arm still
        // pins is that the SecurityDeclarationRejected shape reaches it at all.
        Assert.Contains(ExpectedSecurityNotice, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("lies inside that block", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other direction, so the broadening is provably not collateral damage: the SAME innocent
    /// schema error in an UNSECURED suite still exits 0 without <c>--fail-on-inconclusive</c>, and
    /// prints no security notice.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task ExecuteAsync_UnsecuredSuiteWithTheSameSchemaError_StillExitsZero(int? parallel)
    {
        var file = Path.Combine(_root, "unsecured-schema.e2e.yaml");
        File.WriteAllText(
            file,
            "environment:\n" +
            "  services:\n" +
            "    api:\n" +
            "      image: myorg/api:1.0\n" +
            "steps:\n" +
            "  - id: call\n" +
            "    type: http.rest\n" +
            "    target: api\n" +
            "    path: /health\n");

        var sw = new StringWriter();
        var exitCode = await ExecuteAtParallelismAsync(file, parallel, sw);
        var rendered = sw.ToString();

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("method", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("declares a 'security' block", rendered, StringComparison.Ordinal);
    }
}
