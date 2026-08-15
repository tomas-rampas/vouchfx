// REQ-018 — a security-confirmation failure exits non-zero unconditionally
// (authenticated-infrastructure-mtls, slice E), rewritten over the SecurityAssurance record
// (security-assurance-derivation, REQ-001/REQ-003).
//
// The requirement is a CARVE-OUT from "only Fail breaks CI by default", and the whole engineering
// risk is that a carve-out widens. So the tests below are written in two halves and both matter
// equally:
//
//   • the carve-out WORKS   — an unconfirmed security assurance exits non-zero with no flag;
//   • the carve-out is NARROW — every other cause of the same verdict is untouched, which is what
//     CliLogicTests.FromVerdict_MapsPerTaxonomy already pins and which this file re-states as an
//     explicit anti-regression rather than leaving implied.
//
// AND, NEW HERE: this file is the RECORD-LEVEL TIER of REQ-005's matrix. The rows the CLI tier
// (SecurityAssuranceMatrixTests) cannot reach without a container — a failed health gate, a failed
// probe, a topology that came up and then ran steps — are reachable HERE, by constructing the
// assurance directly and asking for the exit-code decision. That is the coverage hole #401 names,
// and it is the reason the spec chose a record over a boolean: a boolean carries no evidence to
// construct.
using Vouchfx.Cli;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Runtime;
using Xunit;

namespace Vouchfx.Cli.Tests;

/// <summary>
/// REQ-018 tests: the one non-<see cref="Verdict.Fail"/> outcome that breaks CI without an opt-in.
/// </summary>
public sealed class SecurityConfirmationExitCodeTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A declared target carrying a passphrase LITERAL, so the disclosure test below has something
    /// to look for. The value is a canary, not a credential.
    /// </summary>
    private const string PassphraseCanary = "P@ssw0rd-LEAK-CANARY";

    private static readonly SecuredTarget[] s_oneDeclaredTarget =
    {
        new("api", SecuredTargets.ServiceKind, new SecuritySpec(
            Profile: "mtls",
            Endpoint: "8443",
            CaCert: null,
            ClientCert: "./client.pem",
            ClientKey: "./client.key",
            ServerArtifacts: null)
        {
            ClientKeyPassword = PassphraseCanary,
        }),
    };

    /// <summary>The NAME the assurance actually carries for that target.</summary>
    private static readonly string[] s_oneDeclaredName = { "api" };

    private static readonly SecurityConfirmation[] s_oneConfirmation =
    {
        new(
            TargetName: "api",
            TargetKind: SecuredTargets.ServiceKind,
            DeclaredProfile: "mtls",
            DeclaredEndpoint: "8443",
            ObservedAddress: "localhost:8443",
            ObservedProtocol: "Tls13",
            ClientIdentityResolved: true,
            Level: SecurityConfirmationLevel.AuthenticatedRoundTrip,
            Detail: "confirmed"),
    };

    /// <summary>A suite that DECLARES security, with the supplied refusal (or none).</summary>
    private static SecurityAssurance Secured(SecurityAbortKind? refusal) =>
        new(s_oneDeclaredName, Array.Empty<string>(), refusal);

    /// <summary>A suite that declares NO security — the control for every row that has one.</summary>
    private static SecurityAssurance Unsecured(SecurityAbortKind? refusal) =>
        new(Array.Empty<string>(), Array.Empty<string>(), refusal);

    private static int ExitFor(Verdict verdict, SecurityAssurance? assurance) =>
        ExitCodes.FromVerdict(
            verdict, failOnEnvironmentError: false, failOnInconclusive: false, assurance);

    // ── The carve-out works ───────────────────────────────────────────────────────────────

    /// <summary>
    /// REQ-018(a) at the mapping level: an <see cref="Verdict.EnvironmentError"/> caused by an
    /// unconfirmable security assertion exits <see cref="ExitCodes.EnvironmentError"/> (3) with
    /// NO <c>--fail-on-env-error</c> — the shape EDGE-004 (a plaintext port behind the declared
    /// endpoint) and EDGE-005 (a broker that came up with no SSL listener) both produce.
    /// </summary>
    [Fact]
    public void FromVerdict_SecurityConfirmationFailure_ExitsNonZeroWithoutTheFlag()
    {
        Assert.Equal(
            ExitCodes.EnvironmentError,
            ExitFor(Verdict.EnvironmentError, Secured(SecurityAbortKind.ProbeUnconfirmed)));
    }

    /// <summary>
    /// EDGE-010(a): a security PREFLIGHT rejection — the declared <c>clientCert</c> file deleted,
    /// a path escaping the suite directory, a <c>(profile, kind)</c> pair with no wiring — aborts
    /// before any container starts and is <see cref="Verdict.Inconclusive"/>, not
    /// EnvironmentError. It must still exit non-zero, and with the code its own verdict names.
    /// </summary>
    [Fact]
    public void FromVerdict_SecurityPreflightRejection_ExitsInconclusiveWithoutTheFlag()
    {
        Assert.Equal(
            ExitCodes.Inconclusive,
            ExitFor(Verdict.Inconclusive, Secured(SecurityAbortKind.AuthoringFault)));
    }

    /// <summary>
    /// The flag never DOWNGRADES an outcome: a genuine <see cref="Verdict.Fail"/> still exits
    /// <see cref="ExitCodes.TestFailure"/> (1) whatever the security signal says.
    /// </summary>
    [Fact]
    public void FromVerdict_FailStaysTestFailure_EvenWithTheSecuritySignal()
    {
        Assert.Equal(
            ExitCodes.TestFailure,
            ExitFor(Verdict.Fail, Secured(SecurityAbortKind.ProbeUnconfirmed)));
    }

    /// <summary>
    /// The unreachable arm, pinned as FAIL-CLOSED rather than left to chance. Verdict precedence
    /// (<c>EnvironmentError &gt; Fail &gt; Inconclusive &gt; Pass</c>) means a scenario carrying a
    /// security failure always elevates the aggregate above <see cref="Verdict.Pass"/>, so this
    /// combination cannot arise today. A combination that cannot occur is exactly the one a later
    /// refactor makes occur, and reporting 0 for it would be the false assurance the whole
    /// requirement exists to destroy.
    /// </summary>
    [Fact]
    public void FromVerdict_PassWithASecurityFailure_StillExitsNonZero()
    {
        Assert.NotEqual(
            ExitCodes.Success,
            ExitFor(Verdict.Pass, Secured(SecurityAbortKind.ProbeUnconfirmed)));
    }

    // ── The carve-out is narrow ───────────────────────────────────────────────────────────

    /// <summary>
    /// REQ-018(b): every OTHER cause of an environment error — an unhealthy container, an image
    /// that cannot be pulled, a seed failure unrelated to security — still exits
    /// <see cref="ExitCodes.Success"/> by default. This is the proof the carve-out did not widen
    /// into "environment errors now break CI".
    /// </summary>
    [Theory]
    [InlineData(Verdict.EnvironmentError, ExitCodes.Success)]
    [InlineData(Verdict.Inconclusive, ExitCodes.Success)]
    [InlineData(Verdict.Pass, ExitCodes.Success)]
    [InlineData(Verdict.Fail, ExitCodes.TestFailure)]
    public void FromVerdict_WithoutTheSecuritySignal_IsUnchanged(Verdict verdict, int expected)
    {
        Assert.Equal(
            expected,
            ExitCodes.FromVerdict(
                verdict, failOnEnvironmentError: false, failOnInconclusive: false));

        // …and identically when the parameter is passed explicitly, so the DEFAULT and an
        // explicitly-supplied "nothing happened" assurance cannot drift apart.
        Assert.Equal(expected, ExitFor(verdict, SecurityAssurance.None));
    }

    /// <summary>
    /// REQ-003's predicate is a CONJUNCTION on the authoring arm, and this is the half that keeps
    /// it narrow: an authoring refusal in a document that declared NO <c>security</c> raises
    /// nothing. Without it the derivation would redden every unsecured suite with a typo in it.
    /// </summary>
    [Theory]
    [InlineData(Verdict.Inconclusive)]
    [InlineData(Verdict.EnvironmentError)]
    public void FromVerdict_AuthoringRefusalWithNothingDeclared_ExitsSuccess(Verdict verdict)
    {
        Assert.Equal(
            ExitCodes.Success, ExitFor(verdict, Unsecured(SecurityAbortKind.AuthoringFault)));
    }

    // ── REQ-005's matrix, the rows that need no container ─────────────────────────────────
    //
    // Everything below is a row of the spec's own table whose "now" column is a POST-topology
    // outcome. Reaching them through the CLI would need Docker; reaching them through the record
    // needs nothing, which is precisely what carrying evidence rather than a conclusion buys.

    /// <summary>
    /// <strong>THE FENCE (#390), stated as a test rather than as a comment.</strong> A secured
    /// suite that reaches the topology and fails the health gate exits 0 — unchanged, deliberately,
    /// and OUT OF SCOPE for this derivation. An implementation that reddens this row has built
    /// something the spec forbids.
    /// <para>
    /// Both spellings of the row are here: the unhealthy resource being the declared secured target
    /// and it being an unrelated one. The engine cannot tell them apart at this seam — the
    /// discriminator is <c>OrchestrationErrorKind</c>, which yields
    /// <see cref="SecurityAbortKind.TopologyUnavailable"/> either way — and that indiscriminacy is
    /// exactly what #390 is about. Closing it needs a resource-scoped narrowing plus an
    /// endpoint-resolvability preflight, with its own blast-radius measurement.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FromVerdict_SecuredSuiteWhoseHealthGateFailed_StillExitsSuccess(bool secured)
    {
        var assurance = secured
            ? Secured(SecurityAbortKind.TopologyUnavailable)
            : Unsecured(SecurityAbortKind.TopologyUnavailable);

        Assert.False(assurance.Unconfirmed);
        Assert.Equal(ExitCodes.Success, ExitFor(Verdict.EnvironmentError, assurance));
    }

    /// <summary>
    /// The probe row: the topology came up, REQ-005's probe measured the declared block NOT to
    /// hold, and the run aborts with <see cref="Verdict.EnvironmentError"/> → exit 3.
    /// </summary>
    [Fact]
    public void FromVerdict_ProbeFailed_ExitsEnvironmentError()
    {
        Assert.Equal(
            ExitCodes.EnvironmentError,
            ExitFor(Verdict.EnvironmentError, Secured(SecurityAbortKind.ProbeUnconfirmed)));
    }

    /// <summary>
    /// The three "topology up, probe confirmed, then a STEP outcome" rows: 1 for a Fail, 0 for an
    /// Inconclusive, 0 for an EnvironmentError. A confirmed assurance changes nothing about the
    /// taxonomy — which is the whole mechanism clause of REQ-018.
    /// </summary>
    [Theory]
    [InlineData(Verdict.Fail, ExitCodes.TestFailure)]
    [InlineData(Verdict.Inconclusive, ExitCodes.Success)]
    [InlineData(Verdict.EnvironmentError, ExitCodes.Success)]
    [InlineData(Verdict.Pass, ExitCodes.Success)]
    public void FromVerdict_TopologyUpAndProbeConfirmed_MapsPerTaxonomy(Verdict verdict, int expected)
    {
        // Built through the two projections rather than by hand, so this row also pins that a
        // declaration and its confirmation land on comparable values.
        var confirmed = SecurityAssurance.None
            .Declaring(s_oneDeclaredTarget)
            .Confirming(s_oneConfirmation);

        Assert.False(confirmed.Unconfirmed);
        Assert.Equal(expected, ExitFor(verdict, confirmed));
    }

    /// <summary>
    /// <strong>The predicate reads <c>Confirmed</c>, and "some" is not "all".</strong> Two declared
    /// targets, ONE of them confirmed, beside an authoring refusal: this must still raise. The
    /// weaker predicate a review proposed — <c>Confirmed.Count == 0</c> — would go quiet here, and
    /// a suite would exit 0 having confirmed half of what it asserted. A run vouches for a
    /// declaration only when it confirmed ALL of it.
    /// </summary>
    [Fact]
    public void Unconfirmed_OneOfTwoDeclaredTargetsConfirmed_StillRaises()
    {
        var partial = new SecurityAssurance(
            Declared: new[] { "api", "broker" },
            Confirmed: new[] { "api" },
            Refusal: SecurityAbortKind.AuthoringFault);

        Assert.True(partial.Unconfirmed);
        Assert.Equal(ExitCodes.Inconclusive, ExitFor(Verdict.Inconclusive, partial));
    }

    /// <summary>
    /// …and the direction that made reading <c>Confirmed</c> necessary: a shared-topology suite
    /// where ONE scenario was refused at a compile-time door while the topology came up and
    /// REQ-005's probe confirmed every declared target. Judged on the declaration alone this suite
    /// was told its security assertion "was never confirmed" while the run holds the probe's own
    /// confirmation of it — a false positive on the surface whose only value is that its answers
    /// can be trusted.
    /// </summary>
    [Fact]
    public void Unconfirmed_AuthoringRefusalBesideAFullyConfirmedProbe_DoesNotRaise()
    {
        var refusedButConfirmed = SecurityAssurance.None
            .Declaring(s_oneDeclaredTarget)
            .Confirming(s_oneConfirmation)
            .Refusing(SecurityAbortKind.AuthoringFault);

        Assert.False(refusedButConfirmed.Unconfirmed);
        Assert.Equal(ExitCodes.Success, ExitFor(Verdict.Inconclusive, refusedButConfirmed));
    }

    /// <summary>
    /// <strong>The record carries NAMES, so no diagnostic built from it can disclose a
    /// passphrase.</strong> <c>SecuredTarget</c> is a record struct holding the whole
    /// <c>SecuritySpec</c>, whose compiler-generated <c>ToString()</c> prints
    /// <c>ClientKeyPassword</c> — measured, verbatim. <c>SecuritySpec</c>'s own header states the
    /// rule ("never interpolate a <c>SecuritySpec</c> whole into a diagnostic, event or report"),
    /// and an assurance holding an ARRAY of them was exactly that with no guard: no site leaks it
    /// today, and the next site to render this record would not know it was crossing a line.
    /// <para>
    /// Joining the collection is the shape any such diagnostic would take, so that is what is
    /// asserted — not the record's own <c>ToString()</c>, which prints an array's type name and
    /// would have passed unchanged while the payload was still there to be found.
    /// </para>
    /// </summary>
    [Fact]
    public void Declaring_KeepsTargetNamesOnly_SoNoDeclaredSecretCanBeRendered()
    {
        var assurance = SecurityAssurance.None.Declaring(s_oneDeclaredTarget);

        Assert.Equal(new[] { "api" }, assurance.Declared);
        Assert.DoesNotContain(
            PassphraseCanary,
            string.Join(", ", assurance.Declared),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The one shape <c>Declared</c> cannot see: <c>security: mtls</c>, a profile name written
    /// where the block belongs. The schema rejects it AT the <c>security</c> node and the AST binds
    /// no <c>SecuritySpec</c>, so the canonical walk reports nothing declared — yet the document
    /// plainly asserts security and must not go green.
    /// <para>
    /// <see cref="SecurityAbortKind.SecurityDeclarationRejected"/> is why it does not, and it is a
    /// deliberate deviation from REQ-003's two-clause formula rather than an accident. Measured:
    /// deriving from <c>Declared</c> alone takes this shape from a shipped exit 4 to exit 0.
    /// </para>
    /// </summary>
    [Fact]
    public void FromVerdict_RefusalInsideADeclarationThatBoundNothing_StillExitsNonZero()
    {
        var nothingBound = Unsecured(SecurityAbortKind.SecurityDeclarationRejected);

        Assert.True(nothingBound.Unconfirmed);
        Assert.Equal(ExitCodes.Inconclusive, ExitFor(Verdict.Inconclusive, nothingBound));
    }

    // ── The fold, which is the other reader of the same predicate ─────────────────────────

    /// <summary>
    /// <see cref="SecurityAssurance.Worse"/> keeps whichever scenario RAISES, and this is the case
    /// that makes a field-by-field fold wrong: an UNSECURED scenario's authoring refusal beside a
    /// SECURED scenario that was never refused. Unioning <c>Declared</c> across the two would pair
    /// one scenario's declaration with another's refusal and redden a suite neither reddens alone.
    /// <para>
    /// Reachable only under <c>--parallel</c>, where scenarios need not share an environment block.
    /// </para>
    /// </summary>
    [Fact]
    public void Worse_UnsecuredRefusalBesideACleanSecuredScenario_DoesNotRaise()
    {
        var folded = SecurityAssurance.Worse(
            Unsecured(SecurityAbortKind.AuthoringFault),
            Secured(refusal: null));

        Assert.False(folded.Unconfirmed);
        Assert.Equal(ExitCodes.Success, ExitFor(Verdict.Inconclusive, folded));
    }

    /// <summary>
    /// …and the direction that must still fire: one raising scenario anywhere in the fold, in
    /// either position, carries the whole suite.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Worse_OneRaisingScenarioAnywhere_CarriesTheSuite(bool raisingIsFirst)
    {
        var raising = Secured(SecurityAbortKind.AuthoringFault);
        var quiet = Unsecured(SecurityAbortKind.AuthoringFault);

        var folded = raisingIsFirst
            ? SecurityAssurance.Worse(raising, quiet)
            : SecurityAssurance.Worse(quiet, raising);

        Assert.True(folded.Unconfirmed);
        Assert.Equal(ExitCodes.Inconclusive, ExitFor(Verdict.Inconclusive, folded));
    }

    /// <summary>
    /// <strong>The fold and the sequential rule must agree, in BOTH argument orders.</strong> When
    /// two scenarios each raise, <see cref="SecurityAssurance.Worse"/> used to return the LEFT one
    /// — and the fold walks slots in declaration order, so "left" means "the alphabetically earlier
    /// file". Measured against the built assembly:
    /// <c>Worse(authoringFault, probeUnconfirmed).Refusal = AuthoringFault</c> while
    /// <c>Worse(probeUnconfirmed, authoringFault).Refusal = ProbeUnconfirmed</c>, and
    /// <c>authoringFault.Refusing(ProbeUnconfirmed).Refusal = ProbeUnconfirmed</c> — one rule,
    /// spelled two ways, disagreeing.
    /// <para>
    /// It was user-visible rather than internal: <c>RunCommand</c> suppresses the security notice
    /// for <see cref="SecurityAbortKind.ProbeUnconfirmed"/>, so under <c>--parallel 2</c> two suites
    /// with byte-identical content printed different output depending on whether the broken scenario
    /// was named <c>a.e2e.yaml</c> or <c>z.e2e.yaml</c>. A FILENAME decided whether the security
    /// notice appeared.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(SecurityAbortKind.AuthoringFault, SecurityAbortKind.ProbeUnconfirmed)]
    [InlineData(SecurityAbortKind.AuthoringFault, SecurityAbortKind.SecurityDeclarationRejected)]
    [InlineData(SecurityAbortKind.SecurityDeclarationRejected, SecurityAbortKind.ProbeUnconfirmed)]
    public void Worse_BothScenariosRaising_AgreesWithRefusingInEitherOrder(
        SecurityAbortKind one,
        SecurityAbortKind other)
    {
        var left = Secured(one);
        var right = Secured(other);

        Assert.True(left.Unconfirmed);
        Assert.True(right.Unconfirmed);

        // The sequential rule: ONE run that reached both doors, resolved by Precedence.
        var sequential = Secured(one).Refusing(other).Refusal;
        Assert.Equal(sequential, Secured(other).Refusing(one).Refusal);

        Assert.Equal(sequential, SecurityAssurance.Worse(left, right).Refusal);
        Assert.Equal(sequential, SecurityAssurance.Worse(right, left).Refusal);
    }

    // ── Threaded through the run command's own decision ───────────────────────────────────

    /// <summary>
    /// The signal survives <c>RunCommand.ComputeExitCode</c>, which is the function the process
    /// actually returns from — a carve-out implemented only in <see cref="ExitCodes"/> and dropped
    /// one layer up would pass every test above and still exit 0.
    /// </summary>
    [Fact]
    public void ComputeExitCode_SecurityConfirmationFailure_ExitsNonZeroWithoutTheFlag()
    {
        var code = RunCommand.ComputeExitCode(
            parsedCount: 1,
            parseFailureCount: 0,
            suiteVerdict: Verdict.EnvironmentError,
            failOnEnvironmentError: false,
            failOnInconclusive: false,
            securityAssurance: Secured(SecurityAbortKind.ProbeUnconfirmed));

        Assert.Equal(ExitCodes.EnvironmentError, code);
    }

    /// <summary>
    /// The same invocation for an ORDINARY environment error still exits 0 — REQ-018(b) at the
    /// command level.
    /// </summary>
    [Fact]
    public void ComputeExitCode_OrdinaryEnvironmentError_StillExitsZeroWithoutTheFlag()
    {
        var code = RunCommand.ComputeExitCode(
            parsedCount: 1,
            parseFailureCount: 0,
            suiteVerdict: Verdict.EnvironmentError,
            failOnEnvironmentError: false,
            failOnInconclusive: false);

        Assert.Equal(ExitCodes.Success, code);
    }

    /// <summary>
    /// The runner's own assurance defaults to <see cref="SecurityAssurance.None"/>, so every
    /// existing <c>SuiteResult</c> construction — and every embedding caller — keeps its current
    /// exit code.
    /// </summary>
    [Fact]
    public void SuiteResult_DefaultsTheAssuranceToNothingKnown()
    {
        var result = new SuiteResult(
            Verdict.EnvironmentError, Array.Empty<(string, Verdict)>());

        Assert.Same(SecurityAssurance.None, result.Assurance);
        Assert.False(result.Assurance.Unconfirmed);
        Assert.Empty(result.Assurance.Declared);
        Assert.Empty(result.Assurance.Confirmed);
        Assert.Null(result.Assurance.Refusal);
    }
}
