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
using System.Reflection;
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

    /// <summary>
    /// The IDENTITY the assurance actually carries for that target (issue #415) — derived, never
    /// spelled out, so this fixture cannot drift from what <c>Declaring</c> stores.
    /// </summary>
    private static readonly SecuredTargetIdentity[] s_oneDeclaredIdentity =
    {
        SecuredTargets.IdentityOf(s_oneDeclaredTarget[0]),
    };

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
            Detail: "confirmed",

            // #415: the confirmation carries the identity of the declaration it tested, and this
            // fixture stands for a probe that tested THE declared target above — so the identity is
            // taken from that target rather than restated, exactly as the probe takes it from the
            // SecuredTarget its own walk yielded.
            Identity: SecuredTargets.IdentityOf(s_oneDeclaredTarget[0])),
    };

    /// <summary>A suite that DECLARES security, with the supplied refusal (or none).</summary>
    private static SecurityAssurance Secured(SecurityAbortKind? refusal) =>
        new(s_oneDeclaredIdentity, Array.Empty<SecuredTargetIdentity>(), refusal);

    /// <summary>A suite that declares NO security — the control for every row that has one.</summary>
    private static SecurityAssurance Unsecured(SecurityAbortKind? refusal) =>
        new(Array.Empty<SecuredTargetIdentity>(), Array.Empty<SecuredTargetIdentity>(), refusal);

    /// <summary>
    /// The identity of an ordinary <c>profile: tls</c> declaration of <paramref name="name"/> —
    /// the fixture for every row whose subject is WHICH targets a run declared or confirmed rather
    /// than what any of them asserted.
    /// </summary>
    private static SecuredTargetIdentity IdentityFor(string name) =>
        SecuredTargets.IdentityOf(new SecuredTarget(
            name,
            SecuredTargets.ServiceKind,
            new SecuritySpec("tls", "8443", null, null, null, null)));

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
    /// suite whose ONLY refusal is a topology that reached the health gate and failed it exits 0 —
    /// unchanged, deliberately, and OUT OF SCOPE for this derivation. An implementation that reddens
    /// this row has built something the spec forbids.
    /// <para>
    /// The only-refusal qualification is what this row measures and is not decoration:
    /// <c>Refusing</c> keeps the higher precedence, so a suite that recorded an
    /// <see cref="SecurityAbortKind.AuthoringFault"/> before the gate failed keeps THAT refusal and
    /// raises on it. This row therefore says nothing about such a suite, and the doc surfaces
    /// stating the fence carry the same qualification.
    /// </para>
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
    /// <strong>The other half of the fence: a health-gate failure does not FORGIVE a refusal.</strong>
    /// <see cref="SecurityAssurance.Refusing"/> keeps the higher precedence and
    /// <see cref="SecurityAbortKind.TopologyUnavailable"/> ranks lowest, so a secured suite that
    /// recorded an authoring refusal and THEN failed its health gate still carries the authoring
    /// refusal and raises. The behaviour is correct and pre-existing; what was wrong was four doc
    /// surfaces stating the fence unqualified ("that suite still exits 0"), which is true of the
    /// enum member and false of the suite. Pinned here so the qualification has a measurement behind
    /// it rather than only prose.
    /// <para>
    /// Measured end to end on the built CLI: a two-scenario secured suite whose first scenario omits
    /// a required <c>method:</c> and whose topology then fails to provision exits 3, where the same
    /// pair with no <c>security</c> block exits 0.
    /// </para>
    /// </summary>
    [Fact]
    public void Unconfirmed_HealthGateFailureAfterAnAuthoringRefusal_StillRaises()
    {
        var refusedThenUnhealthy = Secured(SecurityAbortKind.AuthoringFault)
            .Refusing(SecurityAbortKind.TopologyUnavailable);

        Assert.Equal(SecurityAbortKind.AuthoringFault, refusedThenUnhealthy.Refusal);
        Assert.True(refusedThenUnhealthy.Unconfirmed);
        Assert.Equal(
            ExitCodes.EnvironmentError,
            ExitFor(Verdict.EnvironmentError, refusedThenUnhealthy));
    }

    /// <summary>
    /// <strong>The shape the predicate cannot see, pinned as the known hole it is.</strong> All
    /// three disjuncts of <see cref="SecurityAssurance.Unconfirmed"/> require a specific non-null
    /// <c>Refusal</c>, so a run that reaches normal completion with a declared target missing from
    /// <c>Confirmed</c> and NO refusal recorded does not raise and exits 0.
    /// <para>
    /// That is safe today for a reason living in ANOTHER ASSEMBLY and asserted by nothing in the
    /// derivation: <c>SecuredEndpointProbe.ConfirmAsync</c> walks
    /// <c>SecuredTargets.Enumerate</c> and emits exactly one confirmation per declared target or
    /// throws. The convention is pinned where it is produced, by
    /// <c>SecuredEndpointProbeTests.Confirm_TwoDeclaredTargets_ConfirmsEachOfThem</c>; this row
    /// pins what it costs if that ever stops holding, so the pair reads as one fact rather than as
    /// an accident nobody wrote down.
    /// </para>
    /// </summary>
    [Fact]
    public void Unconfirmed_DeclaredShortfallWithNoRefusal_DoesNotRaise()
    {
        // #415 retyped these two lists from names to identities. The shortfall this row is about is
        // a WHOLE DECLARATION missing from Confirmed, so both entries are built through the same
        // IdentityFor helper and the row measures exactly what it measured before.
        var shortfall = new SecurityAssurance(
            Declared: new[] { IdentityFor("api"), IdentityFor("broker") },
            Confirmed: new[] { IdentityFor("api") },
            Refusal: null);

        Assert.False(shortfall.Unconfirmed);
        Assert.Equal(ExitCodes.Success, ExitFor(Verdict.Pass, shortfall));
    }

    /// <summary>
    /// <see cref="SecurityAssurance.Declaring"/> and <see cref="SecurityAssurance.Confirming"/>
    /// REPLACE what an earlier call attached; they do not accumulate. <c>ParallelSuiteRunner</c>
    /// depends on it — it re-attaches a slot's own declaration over the assurance the core already
    /// returned, and an accumulating projection would union one scenario's declaration into
    /// another's — and nothing pinned it, which is how a rename to <c>WithDeclared</c> would have
    /// looked like the only way to say so.
    /// </summary>
    [Fact]
    public void Declaring_And_Confirming_ReplaceRatherThanAccumulate()
    {
        var twice = SecurityAssurance.None
            .Declaring(s_oneDeclaredTarget)
            .Declaring(Array.Empty<SecuredTarget>())
            .Confirming(s_oneConfirmation)
            .Confirming(Array.Empty<SecurityConfirmation>());

        Assert.Empty(twice.Declared);
        Assert.Empty(twice.Confirmed);
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
        // #415 retyped these two lists from names to identities; "one of two confirmed" is the same
        // claim about whole declarations, so both entries come from the same IdentityFor helper and
        // the assertion keeps its original strength.
        var partial = new SecurityAssurance(
            Declared: new[] { IdentityFor("api"), IdentityFor("broker") },
            Confirmed: new[] { IdentityFor("api") },
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
    /// <strong>The record carries a NAME and a DIGEST, so no diagnostic built from it can disclose a
    /// passphrase.</strong> <c>SecuredTarget</c> is a record struct holding the whole
    /// <c>SecuritySpec</c>, and when this row was written the compiler-generated <c>ToString()</c>
    /// of both printed <c>ClientKeyPassword</c> — measured, verbatim. Both have since been guarded
    /// (#408 at the holder, the root itself on 2026-08-27), so this row no longer stands on the
    /// absence of a guard elsewhere. It stands on the record's own shape: an assurance holding an
    /// ARRAY of specs would carry more than the predicate reads, and the next site to render it
    /// would not know it was crossing a line.
    /// <para>
    /// <strong>Renamed for issue #415</strong> from
    /// <c>Declaring_KeepsTargetNamesOnly_SoNoDeclaredSecretCanBeRendered</c>: the record stopped
    /// carrying bare names when <c>Declared</c> was retyped to <see cref="SecuredTargetIdentity"/>,
    /// and the old name asserted the thing that stopped being true rather than the property this row
    /// still pins.
    /// </para>
    /// <para>
    /// Joining the collection is the shape any such diagnostic would take, so that is what is
    /// asserted — not the record's own <c>ToString()</c>, which prints an array's type name and
    /// would have passed unchanged while the payload was still there to be found. That reasoning
    /// governs the assertions below too: the <c>assurance.ToString()</c> line an earlier draft added
    /// has been DROPPED rather than kept, because this paragraph rejects exactly that surface and a
    /// doc block cannot hold both positions.
    /// </para>
    /// <para>
    /// <strong>REQ-003, extended for issue #415:</strong> the record now carries identities rather
    /// than bare names, and an identity carries a DIGEST of the declaration. The digest is not a
    /// second route to the same disclosure, but NOT because the passphrase text is excluded from it:
    /// <c>SecuredTargets.IdentityOf</c> HASHES that text whenever
    /// <c>SecretReference.TryParse</c> proves one whole <c>${secret:}</c> token spans the value — a
    /// reference, whose hashing §17.1.1 sanctions outright. What is excluded is anything that could
    /// ONLY be the passphrase itself: a value <c>TryParse</c> refuses contributes only its presence bit,
    /// because a SHA-256 of a low-entropy passphrase is brute-forceable by anyone who obtains it.
    /// The canary fixture is such a value, so the identity it produces cannot contain a derivative
    /// of it at all — which is what
    /// <see cref="IdentityOf_ThePassphraseText_IsAnInputOnlyWhenItIsAWholeSecretReference"/> arm two
    /// pins as a falsifiable property.
    /// </para>
    /// </summary>
    [Fact]
    public void Declaring_KeepsOnlyANameAndADigest_SoNoDeclaredSecretCanBeRendered()
    {
        var assurance = SecurityAssurance.None.Declaring(s_oneDeclaredTarget);

        // The name survives the retyping to identities — asserted through the projection, since
        // Declared is now a list of SecuredTargetIdentity and the claim is about its Name member.
        Assert.Equal(new[] { "api" }, assurance.Declared.Select(identity => identity.Name));

        // THREE DECLARED TRIPWIRES, not live checks — and the joined assertion is one of them, which
        // its old label ("the original assertion, unchanged") stopped describing. It READ AS a live
        // check while Declared held raw target names — it was already only a tripwire against a
        // retype-back even then, since a name would have had to BE the canary for it to fail; what
        // #415's retype changed is what it walks, not its class. string.Join calls
        // SecuredTargetIdentity.ToString(), which renders `name@<uppercase hex>`, so for ANY
        // implementation of the digest neither that string nor either member can contain the canary,
        // and none of the three can fail today. REQ-003's rule is that such an assertion says so or
        // is dropped; all three are KEPT, because they still go red if the RENDERING is ever widened
        // or if Declared is retyped back to something carrying a SecuritySpec. They are not
        // independent — the two below read the members string.Join already walked — so they add
        // localisation rather than reach: the joined line is the shape any diagnostic would take,
        // and the two below say WHICH member carried the leak.
        Assert.DoesNotContain(
            PassphraseCanary,
            string.Join(", ", assurance.Declared),
            StringComparison.Ordinal);

        var identity = Assert.Single(assurance.Declared);
        Assert.DoesNotContain(PassphraseCanary, identity.Digest, StringComparison.Ordinal);
        Assert.DoesNotContain(PassphraseCanary, identity.ToString(), StringComparison.Ordinal);
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

    // ── Issue #415: a declaration is matched by its IDENTITY, never by its name ───────────
    //
    // The defect these rows close: `SomeDeclaredTargetWentUnconfirmed` compared two lists of target
    // NAMES, so a declared target counted as confirmed when SOMETHING confirmed a target of the
    // same name — not when the probe confirmed THAT declaration. Two declarations sharing a name
    // and asserting different things cross-satisfied each other, and one suite can hold both.

    /// <summary>
    /// <strong>REQ-002: a confirmation of a DIFFERENT declaration of the same name confirms
    /// nothing.</strong> A document declares <c>api</c> asserting <c>profile: mtls</c> on endpoint
    /// 9093; the run holds a confirmation of <c>api</c> asserting <c>profile: tls</c> on 8443. Those
    /// are two different assertions about one target, and confirming the weaker one says nothing
    /// whatever about the stronger — yet under the name-only comparison it satisfied it, and a suite
    /// asserting mutual TLS exited 0 on the strength of a plain-TLS confirmation.
    /// <para>
    /// The second half is the control that stops this row passing for the trivial reason
    /// "nothing ever matches": the SAME declaration confirmed against ITSELF does not raise.
    /// </para>
    /// </summary>
    [Fact]
    public void Unconfirmed_ConfirmationOfADifferentDeclarationOfTheSameName_StillRaises()
    {
        var declaredMtls = new SecuredTarget(
            "api",
            SecuredTargets.ServiceKind,
            new SecuritySpec("mtls", "9093", null, "./client.pem", "./client.key", null));

        var confirmedTls = new SecuredTarget(
            "api",
            SecuredTargets.ServiceKind,
            new SecuritySpec("tls", "8443", null, null, null, null));

        var declaring = new[] { declaredMtls };

        var crossSatisfied = SecurityAssurance.None
            .Declaring(declaring)
            .Confirming(new[] { ConfirmationOf(confirmedTls) })
            .Refusing(SecurityAbortKind.AuthoringFault);

        Assert.True(crossSatisfied.Unconfirmed);
        Assert.Equal(ExitCodes.Inconclusive, ExitFor(Verdict.Inconclusive, crossSatisfied));

        // The control: confirmed against ITSELF, the same declaration, and the row goes quiet.
        var genuinelyConfirmed = SecurityAssurance.None
            .Declaring(declaring)
            .Confirming(new[] { ConfirmationOf(declaredMtls) })
            .Refusing(SecurityAbortKind.AuthoringFault);

        Assert.False(genuinelyConfirmed.Unconfirmed);
        Assert.Equal(ExitCodes.Success, ExitFor(Verdict.Inconclusive, genuinelyConfirmed));
    }

    /// <summary>
    /// <strong>REQ-002, one field at a time.</strong> Every member of the declaration is an input to
    /// the identity, so two declarations differing in exactly one of them are two declarations. A
    /// digest that dropped any of them would let a suite declaring one trust anchor, one profile or
    /// one endpoint be satisfied by a probe that confirmed another.
    /// <para>
    /// One field per row rather than one row varying several: a digest that omitted
    /// <c>clientKey</c> alone would still pass a combined row on the strength of the other fields —
    /// which is exactly what
    /// <see cref="Unconfirmed_ConfirmationOfADifferentDeclarationOfTheSameName_StillRaises"/> does,
    /// varying four members at once, so it survives the loss of any one of them.
    /// </para>
    /// <para>
    /// <strong>Every hashed input reachable from a <c>SecuredTarget</c> has a row.</strong> The
    /// earlier three-row set left <c>Profile</c>, <c>Endpoint</c>, <c>Kind</c> and
    /// <c>ServerArtifacts</c> unvaried, and that was MEASURED to be a hole rather than argued:
    /// deleting <c>AppendText(hash, spec.Profile)</c> from <c>SecuredTargets.DigestOf</c> left the
    /// whole suite green. With the rows below the same deletion reddens the <c>profile</c> row, and
    /// deleting <c>AppendText(hash, target.Kind)</c> reddens the <c>kind</c> row.
    /// </para>
    /// <para>
    /// The left/right values live in the row rather than in the test body so each field varies by
    /// something it could plausibly hold — a profile by a profile name, an endpoint by a port — and
    /// the digest is indifferent to which, since it hashes the declared text either way.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("caCert", "./left.pem", "./right.pem")]
    [InlineData("clientCert", "./left.pem", "./right.pem")]
    [InlineData("clientKey", "./left.key", "./right.key")]
    [InlineData("profile", "mtls", "tls")]
    [InlineData("endpoint", "9093", "8443")]
    [InlineData("kind", SecuredTargets.ServiceKind, "kafka")]
    [InlineData("serverArtifacts", "./left-keystore.jks", "./right-keystore.jks")]
    public void IdentityOf_TwoDeclarationsDifferingInOneFieldOnly_DifferByIdentity(
        string field,
        string leftValue,
        string rightValue)
    {
        var left = SecuredTargets.IdentityOf(TargetVarying(field, leftValue));
        var right = SecuredTargets.IdentityOf(TargetVarying(field, rightValue));

        Assert.NotEqual(left, right);
        Assert.NotEqual(left.Digest, right.Digest);

        // The name is deliberately equal on EVERY row, which is the whole point: this is the pair
        // the name-only comparison could not tell apart. Without this guard a row could pass for
        // the wrong reason — because the helper had varied the name instead of the field.
        Assert.Equal(left.Name, right.Name);
    }

    /// <summary>
    /// <strong>REQ-003: the passphrase text is hashed IFF one whole <c>${secret:}</c> token spans
    /// the value.</strong> All three arms of that rule in one row, because they are one rule and a
    /// test pinning any two of them would leave the third free to move.
    /// <para>
    /// A whole <c>${secret:source/path}</c> token ORDINARILY names a passphrase rather than being
    /// one — but the parse does not PROVE that, and this test does not assert it: a path terminates
    /// at the first closing brace, so <c>${secret:env/PASS${secret:hunter2}</c> parses whole and its
    /// literal tail IS hashed. The rule stands anyway because we are HASHING, not quoting, §17.1.1
    /// sanctions hashing a reference — the reproducibility envelope already hashes reference text —
    /// and the digest is COMPARED, never rendered.
    /// So two DIFFERENT references are two declarations: the presence-bit-only framing this
    /// replaces collapsed <c>${secret:vault/prod-key}</c> onto <c>${secret:env/DEV_KEY}</c>, which
    /// is the #415 cross-satisfaction surviving in the one field the fix had excluded.
    /// </para>
    /// <para>
    /// Anything that is NOT a whole token still collapses to the presence bit. On a
    /// non-schema-validated path the property can hold a literal passphrase — the parser is
    /// deliberately lenient and
    /// <c>SecuritySpecBindingTests.Parse_ClientKeyPasswordLiteral_IsStillBound_ParserStaysLenient</c>
    /// pins it — and a digest of a low-entropy secret is brute-forceable by anyone who obtains it.
    /// Hashing it would put a crackable derivative of a passphrase on a value the record was
    /// narrowed (issue #408) precisely so it could be rendered.
    /// </para>
    /// <para>
    /// The third arm — declared versus undeclared — is asserted here for completeness and in both
    /// directions by <see cref="IdentityOf_DeclaringAPassphraseAtAll_ChangesTheIdentity"/>.
    /// </para>
    /// </summary>
    [Fact]
    public void IdentityOf_ThePassphraseText_IsAnInputOnlyWhenItIsAWholeSecretReference()
    {
        // Arm one: two whole-token references naming two different passphrases — two declarations.
        var vaultKey = SecuredTargets.IdentityOf(
            TargetWithPassword("${secret:vault/prod-key}"));
        var envKey = SecuredTargets.IdentityOf(
            TargetWithPassword("${secret:env/DEV_KEY}"));

        Assert.NotEqual(vaultKey, envKey);
        Assert.NotEqual(vaultKey.Digest, envKey.Digest);

        // Arm two: two values that are NOT whole-token references — either could BE the secret, so
        // both collapse to the presence bit and share one identity. §17's protection, preserved.
        var literal = SecuredTargets.IdentityOf(TargetWithPassword("one-passphrase"));
        var otherLiteral = SecuredTargets.IdentityOf(
            TargetWithPassword("a-completely-different-one"));

        Assert.Equal(literal, otherLiteral);
        Assert.Equal(literal.Digest, otherLiteral.Digest);

        // A reference with literal text around it is not a whole token either — TryParse requires
        // the match to span the value — so it lands in arm two beside the bare literals.
        var embedded = SecuredTargets.IdentityOf(
            TargetWithPassword("prefix-${secret:vault/prod-key}"));

        Assert.Equal(literal, embedded);

        // Arm three: declaring a passphrase at all is a different assertion from declaring none,
        // whichever arm the declared value falls in.
        var undeclared = SecuredTargets.IdentityOf(TargetWithPassword(null));

        Assert.NotEqual(vaultKey, undeclared);
        Assert.NotEqual(literal, undeclared);
    }

    /// <summary>
    /// <strong>A lone surrogate is a distinct declaration.</strong> The framing hashes UTF-16 code
    /// units rather than <c>Encoding.UTF8.GetBytes</c>, whose replacement fallback maps an unpaired
    /// surrogate to U+FFFD — under which <c>ca\uD800.pem</c> and <c>ca\uFFFD.pem</c> produced the
    /// same bytes of the same length and therefore ONE identity, while NTFS stores UTF-16 filenames
    /// and .NET's path APIs pass both through, so both can name distinct files on disk. The remarks
    /// on <c>IdentityOf</c> claim injectivity whatever the field values contain; this row is the
    /// value that made that claim false.
    /// </summary>
    [Fact]
    public void IdentityOf_ACaCertDifferingOnlyByALoneSurrogate_DiffersByIdentity()
    {
        var loneSurrogate = SecuredTargets.IdentityOf(TargetVarying("caCert", "./ca\uD800.pem"));
        var replacementChar = SecuredTargets.IdentityOf(TargetVarying("caCert", "./ca\uFFFD.pem"));

        Assert.NotEqual(loneSurrogate, replacementChar);
        Assert.NotEqual(loneSurrogate.Digest, replacementChar.Digest);
    }

    /// <summary>
    /// <strong>The drift guard over the digest's inputs.</strong> <c>SecuredTargets.DigestOf</c>
    /// enumerates the members of <see cref="SecuredTarget"/>, <see cref="SecuritySpec"/> and
    /// <see cref="SecurityServerArtifactSpec"/> BY HAND, so a new property added to any of them is
    /// silently excluded from the identity and #415 returns for that field with no test red. This
    /// row makes the addition red instead.
    /// <para>
    /// <c>SchemaSecuritySurfaceClosureTests</c> already censuses <see cref="SecuritySpec"/>, but its
    /// failure message talks about the schema and the parser: an author reconciles it and never
    /// learns <c>DigestOf</c> exists. That test is left alone; this message does the job it cannot,
    /// and it names <see cref="SecurityServerArtifactSpec"/>, which had no tripwire at all.
    /// </para>
    /// <para>
    /// <see cref="SecuredTarget"/> is here for the same reason and had even less: nothing in
    /// <c>src/</c> or <c>tests/</c> named the type in a census. It is a packable record whose
    /// permitted additive shape is an init-only property — the precedent
    /// <see cref="SecuritySpec.ClientKeyPassword"/> itself set — so its member list is exposed to
    /// exactly the drift this row exists to catch, on the OUTERMOST of the three types.
    /// </para>
    /// </summary>
    [Fact]
    public void DigestOf_TheMembersItEnumeratesByHand_AreStillTheOnlyMembersDeclared()
    {
        AssertMembersAre(typeof(SecuredTarget), "Kind", "Name", "Security");

        AssertMembersAre(
            typeof(SecuritySpec),
            "CaCert",
            "ClientCert",
            "ClientKey",
            "ClientKeyPassword",
            "Endpoint",
            "Profile",
            "ServerArtifacts");

        AssertMembersAre(typeof(SecurityServerArtifactSpec), "Source", "Target");
    }

    /// <summary>
    /// Asserts that <paramref name="record"/>'s public instance properties — less the
    /// compiler-generated <c>EqualityContract</c> — are exactly <paramref name="expected"/>, with a
    /// message that sends the reader to the hand-written enumeration rather than to the schema.
    /// </summary>
    /// <remarks>
    /// Written as an <c>Assert.True</c> over a sequence comparison rather than an
    /// <c>Assert.Equal</c> over two arrays for one reason: only the former takes a message, and the
    /// message IS the fix here. A bare collection mismatch tells an author their new property is
    /// missing from a list in a test file and nothing about why that list exists.
    /// </remarks>
    private static void AssertMembersAre(Type record, params string[] expected)
    {
        var declared = record
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Where(name => name != "EqualityContract")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            declared.SequenceEqual(expected, StringComparer.Ordinal),
            record.Name + " gained or lost a member: it now declares "
            + string.Join(", ", declared) + ", this census expects "
            + string.Join(", ", expected) + ". SecuredTargets.DigestOf enumerates its inputs BY "
            + "HAND — add the field there (and a row to the identity tests in this file), or two "
            + "declarations differing only in it share one identity (#415).");
    }

    /// <summary>
    /// <strong>REQ-003, the other half: the passphrase's PRESENCE is an input.</strong> "This key is
    /// encrypted" and "this key is not" are different assertions about the declared client identity
    /// and must not cross-satisfy, so declaring a <c>clientKeyPassword</c> at all changes the
    /// identity — and does so INDEPENDENTLY of whether the declared text is one the digest may
    /// hash. The value here is a literal canary, i.e. the arm the digest deliberately reduces to
    /// the presence bit (pinned above), so this row cannot pass on the strength of the text. Both
    /// directions are asserted: the theory data supplies the declared value on either side of the
    /// comparison.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IdentityOf_DeclaringAPassphraseAtAll_ChangesTheIdentity(bool declaredIsFirst)
    {
        var withPassword = SecuredTargets.IdentityOf(TargetWithPassword(PassphraseCanary));
        var withoutPassword = SecuredTargets.IdentityOf(TargetWithPassword(null));

        var (left, right) = declaredIsFirst
            ? (withPassword, withoutPassword)
            : (withoutPassword, withPassword);

        Assert.NotEqual(left, right);
        Assert.NotEqual(left.Digest, right.Digest);
    }

    /// <summary>
    /// <strong>An undeclared field is not an empty field.</strong> REQ-004(b) requires an undeclared
    /// <c>caCert</c> to be treated as ABSENT rather than as a missing-but-implied value, so
    /// <c>caCert: null</c> and <c>caCert: ""</c> are two declarations and get two identities. A
    /// framing that wrote nothing for a null would conflate them — which is the same class of
    /// collision the length prefix exists to prevent, reached through the shortest possible value.
    /// </summary>
    [Fact]
    public void IdentityOf_AnUndeclaredFieldAndAnEmptyOne_DifferByIdentity()
    {
        var undeclared = SecuredTargets.IdentityOf(new SecuredTarget(
            "api",
            SecuredTargets.ServiceKind,
            new SecuritySpec("tls", "8443", null, null, null, null)));

        var empty = SecuredTargets.IdentityOf(new SecuredTarget(
            "api",
            SecuredTargets.ServiceKind,
            new SecuritySpec("tls", "8443", string.Empty, null, null, null)));

        Assert.NotEqual(undeclared, empty);
        Assert.NotEqual(undeclared.Digest, empty.Digest);
    }

    /// <summary>
    /// <strong>REQ-007: the property survives a gap in the shared-<c>environment</c> divergence
    /// guard's coverage.</strong> That guard walks <c>scenarios</c> alone, so a document it never
    /// iterates — an unbuilt one (issue #411), a document reaching the assurance by some later
    /// route — can contribute a declaration diverging from a sibling's without the guard ever
    /// seeing the pair. This row asserts the record refuses the cross-satisfaction ITSELF, with no
    /// runner and no guard anywhere in the construction: the two entries are handed to the record
    /// directly.
    /// <para>
    /// Which is why it is worth a row of its own rather than being folded into the runner tests. A
    /// guard is a check that can be bypassed by a path nobody enumerated; the identity is a
    /// property of the comparison, and a comparison cannot be routed around.
    /// </para>
    /// </summary>
    [Fact]
    public void Unconfirmed_DivergentDeclarationsSharingAName_RaiseWithNoRunnerAndNoGuardInvolved()
    {
        var declared = SecuredTargets.IdentityOf(new SecuredTarget(
            "api",
            SecuredTargets.ServiceKind,
            new SecuritySpec("mtls", "9093", "./ca.pem", "./client.pem", "./client.key", null)));

        var confirmed = SecuredTargets.IdentityOf(new SecuredTarget(
            "api",
            SecuredTargets.ServiceKind,
            new SecuritySpec("mtls", "9093", "./other-ca.pem", "./client.pem", "./client.key", null)));

        var divergent = new SecurityAssurance(
            Declared: new[] { declared },
            Confirmed: new[] { confirmed },
            Refusal: SecurityAbortKind.AuthoringFault);

        // Same target, same profile, same endpoint, same client identity — a DIFFERENT trust anchor.
        Assert.Equal(declared.Name, confirmed.Name);
        Assert.True(divergent.Unconfirmed);
        Assert.Equal(ExitCodes.Inconclusive, ExitFor(Verdict.Inconclusive, divergent));
    }

    /// <summary>
    /// A declaration of <c>api</c> whose <paramref name="field"/> holds <paramref name="value"/>
    /// and which is otherwise fixed — so a pair built from it differs in exactly one member.
    /// </summary>
    /// <remarks>
    /// The <c>Name</c> is fixed and is NOT a selectable field: every consumer of this helper asserts
    /// that the two identities share a name, which is the property that makes them the pair the
    /// name-only comparison could not tell apart. The <c>serverArtifacts</c> case varies one
    /// artifact's <c>Source</c> against a fixed <c>Target</c>, so a digest that hashed the list's
    /// presence but not its contents fails that row.
    /// </remarks>
    private static SecuredTarget TargetVarying(string field, string value) =>
        new(
            "api",
            field == "kind" ? value : SecuredTargets.ServiceKind,
            new SecuritySpec(
                Profile: field == "profile" ? value : "mtls",
                Endpoint: field == "endpoint" ? value : "9093",
                CaCert: field == "caCert" ? value : "./ca.pem",
                ClientCert: field == "clientCert" ? value : "./client.pem",
                ClientKey: field == "clientKey" ? value : "./client.key",
                ServerArtifacts: field == "serverArtifacts"
                    ? new[] { new SecurityServerArtifactSpec(value, "/etc/kafka/secrets/keystore.jks") }
                    : null));

    /// <summary>
    /// A declaration of <c>api</c> carrying <paramref name="password"/> as its
    /// <c>clientKeyPassword</c>, or declaring none when it is <see langword="null"/>.
    /// </summary>
    private static SecuredTarget TargetWithPassword(string? password) =>
        new(
            "api",
            SecuredTargets.ServiceKind,
            new SecuritySpec("mtls", "9093", null, "./client.pem", "./client.key", null)
            {
                ClientKeyPassword = password,
            });

    /// <summary>
    /// A confirmation standing for "the probe tested THIS declaration and it held" — the identity
    /// taken from <paramref name="target"/> through the one derivation, exactly as the probe takes
    /// it from the <c>SecuredTarget</c> its own walk yielded.
    /// </summary>
    private static SecurityConfirmation ConfirmationOf(SecuredTarget target) =>
        new(
            TargetName: target.Name,
            TargetKind: target.Kind,
            DeclaredProfile: target.Security.Profile ?? string.Empty,
            DeclaredEndpoint: target.Security.Endpoint ?? string.Empty,
            ObservedAddress: "localhost:9093",
            ObservedProtocol: "Tls13",
            ClientIdentityResolved: target.Security.ClientCert is not null,
            Level: SecurityConfirmationLevel.AuthenticatedRoundTrip,
            Detail: "confirmed",
            Identity: SecuredTargets.IdentityOf(target));

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

    /// <summary>
    /// <strong>Issue #415's own shape, at the tier that decides it.</strong> A suite whose probe
    /// CONFIRMED <c>api</c>, folded against an unbuilt document declaring <c>api</c> and confirming
    /// nothing, still raises. The sequential runner used to union that document's declaration into
    /// the suite's <c>Declared</c> and keep only its <see cref="SecurityAssurance.Refusal"/>, so the
    /// suite's own confirmation satisfied the document's declaration and a broken secured file among
    /// siblings that come up exited 0 under <c>run</c> — while <c>run --parallel N</c>, which always
    /// folded whole values, exited non-zero. A flag flipped the answer.
    /// <para>
    /// The fold is what refuses it: an unbuilt document's <see cref="SecurityAssurance.Confirmed"/>
    /// is empty BY CONSTRUCTION, nothing downstream of it ran, and no guard ever proved its
    /// environment is the one the topology started from — so its declaration is never confirmed,
    /// whatever a sibling's probe measured.
    /// </para>
    /// <para>
    /// Both argument orders, because order-independence in <see cref="SecurityAssurance.Unconfirmed"/>
    /// and <see cref="SecurityAssurance.Refusal"/> is the invariant <c>Worse</c>'s own remarks state,
    /// and a runner folds in whichever order it walks its documents.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Worse_SecuredUnbuiltDocumentBesideASuiteThatConfirmedTheSameTarget_StillRaises(
        bool suiteIsFirst)
    {
        // The suite's own probe confirmed the very declaration it made: nothing here raises alone.
        var suite = new SecurityAssurance(
            Declared: s_oneDeclaredIdentity,
            Confirmed: s_oneDeclaredIdentity,
            Refusal: null);
        var unbuilt = Secured(SecurityAbortKind.AuthoringFault);

        Assert.False(suite.Unconfirmed);
        Assert.True(unbuilt.Unconfirmed);

        var folded = suiteIsFirst
            ? SecurityAssurance.Worse(suite, unbuilt)
            : SecurityAssurance.Worse(unbuilt, suite);

        Assert.True(folded.Unconfirmed);
        Assert.Equal(SecurityAbortKind.AuthoringFault, folded.Refusal);
        Assert.Equal(ExitCodes.Inconclusive, ExitFor(Verdict.Inconclusive, folded));
    }

    /// <summary>
    /// …and the fence in the same fold (EDGE-001): an unbuilt document that declared NOTHING
    /// contributes <see cref="SecurityAssurance.None"/>, which is the identity of the fold, so the
    /// confirmed suite beside it still exits 0. This is the half of issue #390 that must not move —
    /// an unsecured broken file cannot redden a suite by proxy.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Worse_UnsecuredUnbuiltDocumentBesideAConfirmedSuite_DoesNotRaise(bool suiteIsFirst)
    {
        var suite = new SecurityAssurance(
            Declared: s_oneDeclaredIdentity,
            Confirmed: s_oneDeclaredIdentity,
            Refusal: null);

        var folded = suiteIsFirst
            ? SecurityAssurance.Worse(suite, SecurityAssurance.None)
            : SecurityAssurance.Worse(SecurityAssurance.None, suite);

        Assert.False(folded.Unconfirmed);
        Assert.Null(folded.Refusal);
        Assert.Equal(ExitCodes.Success, ExitFor(Verdict.Pass, folded));
    }

    /// <summary>
    /// <strong>REQ-004 at the record tier: the RETIRED union shape is the one that exits 0, and the
    /// shape both run paths now ship raises.</strong> Issue #415's exit 0 was not an arithmetic slip
    /// in a fold — it was a DIFFERENT VALUE reaching <c>ExitCodes.FromVerdict</c>: one
    /// <see cref="SecurityAssurance.Declared"/> union across the suite AND the unbuilt document,
    /// carrying the suite's own confirmations, with only the document's
    /// <see cref="SecurityAssurance.Refusal"/> kept. That value's declaration is satisfied by a
    /// SIBLING's confirmation, so <see cref="SecurityAssurance.Unconfirmed"/> answers false and a
    /// broken secured file exits 0. Both shapes are constructed here as values, and the row goes red
    /// if the union ever comes back.
    /// <para>
    /// <strong>Why this is NOT written as "the sequential shape and the parallel shape agree".</strong>
    /// It cannot be, at this tier, and the earlier drafting of this row that claimed it was
    /// tautological. <see cref="SecurityAssurance.Worse"/> SELECTS one of its two arguments — every
    /// return path is a bare <c>left</c> or <c>right</c>, never a <c>with</c> — so whenever the
    /// unbuilt document is the only raiser the fold returns THAT DOCUMENT'S OWN OBJECT, whatever the
    /// arity of the fold and whatever the slots contained. A hand-built "parallel shape" and a
    /// hand-built "sequential shape" are then reference-identical, and any assertion comparing them
    /// compares an object with itself. What survives such a comparison is
    /// <see cref="Worse_SecuredUnbuiltDocumentBesideASuiteThatConfirmedTheSameTarget_StillRaises"/>,
    /// which already runs it in both argument orders. The two paths now reach the same
    /// <c>UnbuiltDocument.AssureAll</c> + <c>Worse</c> composition, which is exactly what made the
    /// agreement stop being falsifiable here.
    /// </para>
    /// <para>
    /// <strong>What this pins, and what it does not.</strong> It pins the value shape, not either
    /// runner: both values are built by hand, and nothing in this file executes <c>RunSuiteAsync</c>
    /// or <c>RunParallelAsync</c>. The end-to-end agreement — a topology up, a probe that CONFIRMED,
    /// and the same non-zero exit from <c>run</c> and from <c>run --parallel N</c> — is measured by
    /// <c>Vouchfx.Engine.Runtime.Tests.KafkaSecurityConfirmationDrillDockerTests
    /// .SecuredUnbuiltSiblingBesideAConfirmedProbe_ExitsNonZeroOnBothRunPaths</c>, which needs a
    /// container.
    /// </para>
    /// </summary>
    [Fact]
    public void Worse_TheRetiredUnionShape_ExitsZeroWhereTheFoldedShapeRaises()
    {
        // The RETIRED sequential shape, constructed explicitly: ONE `Declared` union across the
        // suite AND the document — the two declarations are byte-identical, so the union holds one
        // entry — the suite's own confirmations, and only the document's refusal kept. This is issue
        // #415's exit 0, reproduced as a value.
        var retiredUnion = new SecurityAssurance(
            Declared: s_oneDeclaredIdentity,
            Confirmed: s_oneDeclaredIdentity,
            Refusal: SecurityAbortKind.AuthoringFault);

        Assert.False(retiredUnion.Unconfirmed);
        Assert.Equal(ExitCodes.Success, ExitFor(Verdict.Inconclusive, retiredUnion));

        // A scenario whose own probe confirmed the very declaration it made: nothing here raises
        // alone.
        var confirmedScenario = new SecurityAssurance(
            Declared: s_oneDeclaredIdentity,
            Confirmed: s_oneDeclaredIdentity,
            Refusal: null);

        // The unbuildable secured sibling: it declares the SAME target, and its `Confirmed` is empty
        // by construction because nothing downstream of it ran.
        var unbuilt = Secured(SecurityAbortKind.AuthoringFault);

        // The shape both run paths ship: WHOLE values folded, so the declaration and the refusal
        // stay paired and the sibling's confirmation cannot reach the document's declaration.
        var folded = SecurityAssurance.Worse(confirmedScenario, unbuilt);

        Assert.False(confirmedScenario.Unconfirmed);
        Assert.True(folded.Unconfirmed);
        Assert.Equal(SecurityAbortKind.AuthoringFault, folded.Refusal);
        Assert.Equal(ExitCodes.Inconclusive, ExitFor(Verdict.Inconclusive, folded));

        // THE SELECTION SEMANTICS the paragraph above argues from, asserted instead of asserted in
        // prose: `Worse` returns one of its ARGUMENTS — reference-identical — never a construction.
        // A merged return (the raising side's `Declared` beside the other's `Confirmed`, say) would
        // leave every example row in this file green while
        // `Worse_UnconfirmedIsTheDisjunctionOfItsArguments` goes red.
        Assert.Same(unbuilt, folded);
    }

    // ── The fold's monotonicity, pinned as a PROPERTY rather than by example ──────────────

    /// <summary>
    /// The (<c>Declared</c>, <c>Confirmed</c>) pairings a <see cref="SecurityAssurance"/> can take
    /// that the predicate can tell apart: nothing declared, a declaration the probe confirmed, a
    /// declaration whose confirmation tested a DIFFERENT assertion of the same name (issue #415's
    /// own shape), and two declared targets with one confirmed (the shortfall REQ-003's
    /// "every declared target" clause exists for).
    /// </summary>
    private static readonly string[] s_foldShapes =
    {
        "nothing-declared",
        "declaration-confirmed",
        "declaration-mismatched",
        "declared-shortfall",
    };

    /// <summary>Every door, plus the run that reached none.</summary>
    private static readonly SecurityAbortKind?[] s_foldRefusals =
    {
        null,
        SecurityAbortKind.AuthoringFault,
        SecurityAbortKind.SecurityDeclarationRejected,
        SecurityAbortKind.TopologyUnavailable,
        SecurityAbortKind.ProbeUnconfirmed,
    };

    /// <summary>The whole product space, one row per assurance shape.</summary>
    public static TheoryData<string, SecurityAbortKind?> FoldShapeRows
    {
        get
        {
            var rows = new TheoryData<string, SecurityAbortKind?>();

            foreach (var shape in s_foldShapes)
            {
                foreach (var refusal in s_foldRefusals)
                {
                    rows.Add(shape, refusal);
                }
            }

            return rows;
        }
    }

    /// <summary>One assurance of the named shape, carrying the supplied refusal.</summary>
    private static SecurityAssurance ShapeOf(string shape, SecurityAbortKind? refusal)
    {
        var declaration = SecuredTargets.IdentityOf(TargetVarying("caCert", "./ca.pem"));
        var otherAssertion = SecuredTargets.IdentityOf(TargetVarying("caCert", "./other-ca.pem"));

        return shape switch
        {
            "nothing-declared" => new SecurityAssurance(
                Array.Empty<SecuredTargetIdentity>(),
                Array.Empty<SecuredTargetIdentity>(),
                refusal),
            "declaration-confirmed" => new SecurityAssurance(
                new[] { declaration }, new[] { declaration }, refusal),
            "declaration-mismatched" => new SecurityAssurance(
                new[] { declaration }, new[] { otherAssertion }, refusal),
            "declared-shortfall" => new SecurityAssurance(
                new[] { declaration, IdentityFor("worker") }, new[] { declaration }, refusal),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown fold shape."),
        };
    }

    /// <summary>
    /// <strong>The property the whole security gate rests on, and which nothing asserted:
    /// <c>Worse(a, b).Unconfirmed == a.Unconfirmed || b.Unconfirmed</c>.</strong> A suite raises
    /// exactly when one of the values folded into it raises — no fold can invent a raise, and no
    /// fold can lose one.
    /// <para>
    /// It holds today only because <see cref="SecurityAssurance.Worse"/> SELECTS one of its
    /// arguments: every return path is a bare <c>left</c> or <c>right</c>, never a <c>with</c>, so
    /// the returned <see cref="SecurityAssurance.Unconfirmed"/> is one of the two inputs' own. That
    /// is an argument from reading the method, made independently by three reviewers and pinned by
    /// nothing.
    /// </para>
    /// <para>
    /// <strong>What this row buys, stated from a MEASUREMENT rather than from the guess that was
    /// written here first.</strong> The original justification claimed that merging the raising
    /// side's <c>Declared</c> beside the other's <c>Confirmed</c> would break the disjunction "while
    /// every example row in this file stays green". That was measured and is FALSE: that mutation
    /// reddens three example rows as well as six of this theory's —
    /// <c>Worse_SecuredUnbuiltDocumentBesideASuiteThatConfirmedTheSameTarget_StillRaises</c> and
    /// <c>Worse_TheRetiredUnionShape_ExitsZeroWhereTheFoldedShapeRaises</c> among them. The claim was
    /// left standing after being disproved, which is the failure this file keeps having to correct.
    /// </para>
    /// <para>
    /// The shape no example row covers is a merge in the branch where NEITHER side raises and both
    /// carry a refusal: <c>declaration-confirmed/AuthoringFault</c> against
    /// <c>nothing-declared/TopologyUnavailable</c>, under the same merge, yields
    /// <c>{[declared], [], AuthoringFault}</c> — <see cref="SecurityAssurance.Unconfirmed"/> TRUE
    /// where both inputs are false. The fold INVENTING a raise, from two assurances that each
    /// exit 0. Every example row in this file fixes both sides of a pair where at least one raises;
    /// this theory sweeps the neither-raises pairs with differing <c>Confirmed</c>, and that is the
    /// coverage it uniquely holds. A fold that can invent a raise is as much a defect as one that
    /// can drop one — it reddens a suite whose declaration was confirmed.
    /// </para>
    /// <para>
    /// <strong>Rows are the LEFT shape and the sweep over right shapes is in the body</strong>,
    /// which is a deliberate trade. The exhaustive form — one row per ORDERED PAIR — is 400 rows for
    /// one property, a 75% inflation of this assembly's visible test count; the failure message
    /// below carries the same localisation (both shapes, both refusals, both orders, expected and
    /// actual), so the only thing given up is per-pair re-runnability.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(FoldShapeRows))]
    public void Worse_UnconfirmedIsTheDisjunctionOfItsArguments(
        string shape,
        SecurityAbortKind? refusal)
    {
        var left = ShapeOf(shape, refusal);

        foreach (var otherShape in s_foldShapes)
        {
            foreach (var otherRefusal in s_foldRefusals)
            {
                var right = ShapeOf(otherShape, otherRefusal);
                var expected = left.Unconfirmed || right.Unconfirmed;

                var forward = SecurityAssurance.Worse(left, right).Unconfirmed;
                var reversed = SecurityAssurance.Worse(right, left).Unconfirmed;

                var pair =
                    $"{Describe(shape, refusal)} against {Describe(otherShape, otherRefusal)}: "
                    + $"expected {expected}";

                Assert.True(forward == expected, $"Worse(left, right) — {pair}, got {forward}.");
                Assert.True(reversed == expected, $"Worse(right, left) — {pair}, got {reversed}.");
            }
        }

        static string Describe(string described, SecurityAbortKind? door) =>
            $"{described}/{door?.ToString() ?? "no-refusal"}";
    }

    /// <summary>
    /// <strong>EDGE-005: two scenarios declaring the same target name.</strong> The
    /// shared-`environment` divergence guard forces every scenario's environment byte-identical, so
    /// the two declarations produce ONE identity and <see cref="SecurityAssurance.Declaring"/>'s
    /// deduplication collapses them to a single entry — the suite behaves exactly as it does with
    /// one scenario, which is why this edge changes nothing.
    /// <para>
    /// The second half is what makes the first half a statement about DISTINCT declarations rather
    /// than about names: two declarations sharing a name and differing in what they assert are two
    /// entries, not one. A deduplication keyed on the name would collapse them and REQ-002's whole
    /// mechanism would be inert — so both directions are asserted in one row, because they are one
    /// property.
    /// </para>
    /// </summary>
    [Fact]
    public void Declaring_TwoScenariosDeclaringOneTargetName_KeepsOneEntryPerDistinctDeclaration()
    {
        var declaration = s_oneDeclaredTarget[0];

        // Byte-identical, which is what the divergence guard forces across a sequential suite's
        // scenarios: one entry, and the identity is the one either scenario alone would produce.
        var bothScenarios = SecurityAssurance.None
            .Declaring(new[] { declaration, declaration });

        Assert.Single(bothScenarios.Declared);
        Assert.Equal(SecuredTargets.IdentityOf(declaration), bothScenarios.Declared[0]);

        // The same NAME asserting something else is a second declaration, not a duplicate.
        var differentAssertion = new SecuredTarget(
            declaration.Name,
            SecuredTargets.ServiceKind,
            new SecuritySpec("tls", "8443", null, null, null, null));

        var twoDeclarations = SecurityAssurance.None
            .Declaring(new[] { declaration, differentAssertion });

        Assert.Equal(2, twoDeclarations.Declared.Count);
        Assert.Equal(
            declaration.Name,
            Assert.Single(twoDeclarations.Declared.Select(d => d.Name).Distinct()));
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
