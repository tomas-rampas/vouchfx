// Vouchfx.Engine.Runtime — SecurityAssurance (security-assurance-derivation, REQ-001/REQ-003).
//
// WHAT THIS REPLACES, AND WHY THE SHAPE IS THE POINT.
//
// This type replaces a `bool SecurityConfirmationFailed` that nine door-local sites across two run
// paths each set for themselves. Those doors are mutually exclusive early returns, so they never
// all fire — and door ORDER therefore decided an exit code: a document with two faults reported
// whichever door happened to be reached first, and three adjacent pre-topology doors gave three
// different answers to the same question about the same class of document. Four separate masking
// instances were found and patched one at a time inside one feature's review cycle; each patch
// closed its instance and the class reappeared at the next door along.
//
// Carrying EVIDENCE rather than a conclusion is what stops that recurring. A door records only
// WHICH door refused; nothing about the exit code is decided there. The verdict-assembly site —
// which holds the parsed AST unconditionally and therefore cannot be skipped by any early return
// inside the run — supplies what the document DECLARED. The predicate below reads both.
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;

namespace Vouchfx.Engine.Runtime;

/// <summary>
/// Which door aborted a run, for the purposes of deciding whether a declared <c>security</c> block
/// went unconfirmed.
/// </summary>
/// <remarks>
/// This enum names a CAUSE, never a verdict and never an exit code. Three of its members can raise
/// REQ-018's non-zero exit — <see cref="AuthoringFault"/> only in conjunction with a declared target
/// left unconfirmed, the other two unconditionally — and the fourth deliberately cannot. Every one
/// of those decisions lives in <see cref="SecurityAssurance.Unconfirmed"/> and nowhere else.
/// </remarks>
public enum SecurityAbortKind
{
    /// <summary>
    /// A scenario was refused on an AUTHORING fault, and <strong>that refusal started no
    /// container</strong>: a schema rejection, a parse failure, a provider-pipeline refusal (the
    /// security preflight among them), a bad secret reference, a protocol conflict, a
    /// base-directory divergence, or <c>EnvironmentMapper.Map</c>'s eager
    /// <c>ArgumentException</c> (<c>${conn:typo}</c>, an unknown dependency, a secret in an
    /// <c>env:</c> block).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>It says "that refusal started no container", not "the run started none".</strong> A
    /// mixed suite refuses ONE scenario here and runs its siblings, so containers may well be up —
    /// measured, the engine's own <c>Current State: Running</c> diagnostic prints beside this
    /// classification. What this member asserts is that nothing downstream of THIS refusal ever ran
    /// to confirm the declaration, which is the fact the predicate needs and the only one it has.
    /// </para>
    /// <para>
    /// <strong>The boundary is "no container started", not "before the <c>StartAsync</c> call".</strong>
    /// <c>Map</c> runs as Step 1 of <c>SuiteTopology.StartAsync</c>, eagerly and before DCP is ever
    /// reached, so its refusal starts nothing — and a document refused there is exactly as
    /// unconfirmed as one refused a line earlier. Siting the boundary at the call would have made
    /// an implementation detail of where a validation happens to live decide a build's colour.
    /// </para>
    /// <para>
    /// <strong>The wide reading is deliberate and overturns a written decision.</strong> The
    /// suite-level protocol-conflict guard used to argue in its own comment that "a protocol
    /// conflict is an authoring error, not a failure to confirm a security assertion", and declined
    /// to raise. The schema door, elsewhere in the same method, argued the opposite and widened.
    /// Both were sound in isolation and they cannot both be the rule. The wide one wins: which door
    /// refused is not consulted, because nothing downstream of the refusal ever runs to confirm the
    /// declaration.
    /// </para>
    /// <para>
    /// <strong>"Whatever refused it" is the door's identity, not the predicate.</strong> An earlier
    /// wording said a document aborting before any container starts is unconfirmable whatever
    /// aborted it. That is wider than <see cref="SecurityAssurance.Unconfirmed"/>: this member
    /// raises only in conjunction with a declared target left unconfirmed, so a secured document
    /// whose YAML cannot be read or parsed at all binds no declaration and does not raise here (its
    /// rows assert the notice is absent — and beside a parseable sibling that still costs an exit 0,
    /// which is the residual of issue #411), and a refusal beside siblings whose probe confirmed
    /// every declared target does not either. The door is irrelevant; the predicate is not.
    /// </para>
    /// <para>
    /// <strong>The refused-for-its-contents half of that hole is CLOSED for a document that
    /// declares a block the canonical walk can SEE, and this member is where that half
    /// lands.</strong> A document whose YAML parses and which is then refused by
    /// <c>AstBuilder.Build</c> — an unknown step type, a duplicate step id — HAS a bound
    /// <c>environment</c> block, so the CLI hands it to the runner (<c>RunSuiteAsync</c> /
    /// <c>RunParallelAsync</c>'s <c>unbuiltDocuments</c>), whose one canonical walk folds its
    /// declaration into <c>Declared</c> and records THIS kind for it — "a parse failure" being one
    /// of the refusals this member's own summary already names. The OTHER half — a document whose
    /// <c>security</c> node the schema rejects, which binds nothing for that walk to find — lands
    /// on <see cref="SecurityDeclarationRejected"/> instead, by the same schema door and for the
    /// same reason it does for a document that became a scenario. Both are decided in
    /// <c>UnbuiltDocument.Assure</c>, once, for both run paths.
    /// It is not a new door and decides no
    /// outcome — but "the PAIR it contributes" is true of the parallel construction only, and the
    /// unqualified form over-claimed. <c>ParallelSuiteRunner.UnbuiltAssurance</c> does build one
    /// assurance per document, so there is a pair and the predicate reads it. The sequential path
    /// holds ONE suite-wide assurance whose <see cref="SecurityAssurance.Declared"/> is a union over
    /// other documents, so there is no pair to contribute: it stamps this kind onto that shared
    /// value, and the guard that keeps that sound is that <c>Assure</c> contributes NO refusal for
    /// a document that declared nothing — unguarded it paired an unsecured document's refusal with
    /// a sibling's declaration and overrode <see cref="TopologyUnavailable"/>'s fence. The union
    /// also means such a document's declaration is matched to a confirmation by target NAME alone
    /// and bypasses the shared-<c>environment</c> divergence guard, which is issue #415 and is open.
    /// Only the three failure classes that bind nothing at all remain, deliberately: those
    /// documents never parsed, so neither the walk nor the schema door has anything to read, and
    /// recovering a declaration there would need a raw-YAML scan for a <c>security:</c> key — a
    /// second spelling of "does this document declare security" that can disagree with the
    /// canonical walk.
    /// </para>
    /// </remarks>
    AuthoringFault,

    /// <summary>
    /// A scenario was refused as <see cref="AuthoringFault"/> is, AND the reported fault is located
    /// AT, or inside, a declared <c>security</c> block — the schema door's
    /// <c>LocatesADeclaredSecurityBlock</c> shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This member exists because of a measured gap in the derived rule, and the gap is
    /// worth stating rather than hiding.</strong> REQ-003's predicate is
    /// <c>Declared.Any() ∧ authoring fault</c>, and <c>Declared</c> comes from
    /// <c>SecuredTargets.Enumerate</c> — a walk over BOUND <c>SecuritySpec</c> values. The realistic
    /// typo <c>security: mtls</c> (the profile name written where the block belongs) is a schema
    /// error AT the <c>security</c> node that binds NO <c>SecuritySpec</c> at all, so the canonical
    /// walk yields nothing and the predicate above answers false. That shape exits 4 today and is
    /// pinned by <c>ExecuteAsync_SecurityKeyThatBindsNoBlock_ExitsNonZeroWithTheNotice</c>; deriving
    /// the answer from <c>Declared</c> alone would have taken it to 0 — a document asserting
    /// security, green.
    /// </para>
    /// <para>
    /// It is NOT a tenth door. A door still records only WHAT IT REFUSED (here: something located
    /// in the declaration itself), and no door decides the OUTCOME. The alternative — a BESPOKE
    /// second spelling of "does this document declare security", scanning the raw YAML for a
    /// <c>security:</c> key and free to disagree with the AST walk — is exactly what
    /// <c>SecuredTargets</c>' own header exists to prevent.
    /// </para>
    /// <para>
    /// <strong>Which is why this kind, and not a scan, is what closes the same shape for a document
    /// that never became a scenario.</strong> An unbuilt document (issue #411) is absent from
    /// <c>scenarios</c>, so the schema door's own loop never iterates it — and before that gap was
    /// closed, <c>security: mtls</c> in such a file exited 4 ALONE (through issue #278's
    /// all-parse-failure rule) and 0 beside one sibling that parses, so ADDING an unrelated fault
    /// to a suite made the pipeline greener. <c>UnbuiltDocument.Assure</c> closes it by running
    /// <c>DocumentValidator.Validate</c> and <c>ScenarioRunner.RejectsASecurityDeclaration</c> —
    /// the door's OWN two calls, shared rather than re-derived — over the text discovery already
    /// retained. Reading raw YAML is not the thing forbidden above; inventing a second answer to
    /// the question is, and this invents none.
    /// </para>
    /// <para>
    /// <strong>What is NOT claimed: that the predicate derives every outcome.</strong> This kind and
    /// <see cref="TopologyUnavailable"/> are written into
    /// <see cref="SecurityAssurance.Unconfirmed"/> by name — one always raising, one never — so for
    /// those two the answer is hard-coded rather than derived from <c>Declared</c> against
    /// <c>Confirmed</c>. Both are named exceptions carrying their reasons (this one's is the
    /// paragraph above; the other's is on its own member). What the derivation delivers is narrower
    /// and is the whole of what it should be read as promising: for the DERIVED case — an ordinary
    /// authoring refusal, which is every door but these two — no door decides the outcome, and door
    /// order therefore cannot decide an exit code.
    /// </para>
    /// </remarks>
    SecurityDeclarationRejected,

    /// <summary>
    /// The topology failed to start for a reason that is NOT a security-confirmation failure — an
    /// unhealthy container, an image that cannot be pulled, a seed failure, a health-gate timeout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This member never raises, and that is the fence rather than an oversight.</strong> A
    /// secured suite whose ONLY fault is a topology that came up and failed its health gate exits 0
    /// by default; that is issue #390 and it is deliberately out of scope here. It needs a
    /// resource-scoped narrowing plus an endpoint-resolvability preflight, and its own blast-radius
    /// measurement — closing it from inside this derivation would be one clause in the same
    /// expression and would redden every suite whose unrelated container was slow to come up.
    /// </para>
    /// <para>
    /// <strong>"Never raises" is a property of this MEMBER, not of the run that recorded it — and
    /// the unqualified form was wrong on four doc surfaces.</strong>
    /// <see cref="SecurityAssurance.Refusing"/> keeps
    /// the HIGHER precedence and this kind ranks lowest, so a scenario already refused as
    /// <see cref="AuthoringFault"/> keeps that refusal when the shared topology then fails its gate,
    /// and the suite raises on it. Measured on the built CLI: a two-scenario secured suite whose
    /// first scenario omits a required <c>method:</c> and whose topology then fails to provision
    /// exits 3, where the identical pair with no <c>security</c> block exits 0. Every surface
    /// stating this fence states it with the only-fault qualification for that reason.
    /// </para>
    /// </remarks>
    TopologyUnavailable,

    /// <summary>
    /// REQ-005's post-health-gate probe raised an <c>OrchestrationException</c> whose
    /// <c>Info.Kind</c> is <c>OrchestrationErrorKind.SecurityConfirmation</c>: the topology came up
    /// and the declared block was measured NOT to hold.
    /// </summary>
    /// <remarks>
    /// The discriminator is the classified kind on the exception, unchanged from what shipped — not
    /// the verdict and not the message. This member raises unconditionally: the probe only runs for
    /// a declared block, so it cannot fire for a document that declared nothing.
    /// </remarks>
    ProbeUnconfirmed,
}

/// <summary>
/// What a run established about the <c>security</c> blocks a suite declared: what was DECLARED,
/// what was CONFIRMED, and which door (if any) REFUSED.
/// </summary>
/// <param name="Declared">
/// The NAMES of the declared targets carrying a <c>security</c> block, from
/// <c>SecuredTargets.Enumerate</c> — the one canonical walk, in its fixed
/// services-then-dependencies order, deduplicated.
/// </param>
/// <param name="Confirmed">
/// The names of the targets REQ-005's probe confirmed, from
/// <c>SuiteTopology.SecurityConfirmations</c>; empty when no topology was reached. A confirmation's
/// <c>TargetName</c> comes from the SAME <c>SecuredTargets.Enumerate</c> walk
/// (<c>SecuredEndpointProbe.ConfirmAsync</c>), so these names are comparable with
/// <paramref name="Declared"/> by construction rather than by convention.
/// </param>
/// <param name="Refusal">Which door aborted the run, or <see langword="null"/> when none did.</param>
/// <remarks>
/// <para>
/// <strong>The two halves are filled at different sites, on purpose.</strong> A door knows which
/// door it is and nothing else; it records <paramref name="Refusal"/>. The verdict-assembly site
/// holds the parsed ASTs as a PARAMETER — <c>ScenarioRunner.RunSuiteAsync</c> and
/// <c>ParallelSuiteRunner.RunParallelCoreAsync</c> both take them — so it can fill
/// <paramref name="Declared"/> on every path, including the ones that abort before the runner
/// itself has parsed anything. That is what removed the speculative re-parse the schema door used
/// to need in order to answer "does this document declare security" at the door.
/// </para>
/// <para>
/// <strong>NAMES, not the <c>SecuritySpec</c> values they carry — and the narrowing is a
/// disclosure boundary, not a taste.</strong> <c>SecuredTarget</c> is a record struct holding the
/// whole <c>SecuritySpec</c>, so its compiler-generated <c>ToString()</c> expands a declared
/// <c>clientKeyPassword</c> literal verbatim — measured:
/// <c>SecuredTarget { Name = api, …, ClientKeyPassword = … }</c>. <c>SecuritySpec</c>'s own header
/// states the rule in as many words ("never interpolate a <c>SecuritySpec</c> whole into a
/// diagnostic, event or report"), and a record that holds an array of them is exactly that with no
/// guard: nothing today interpolates this record, and the next diagnostic to interpolate it would
/// not know it was crossing a line. Nothing here ever needed more than the names — the predicate
/// below compares what was declared against what was confirmed on its authoring arm — so the specs
/// are not carried at all rather than carried carefully.
/// </para>
/// <para>
/// Neither this record nor <c>ScenarioCoreResult</c>/<c>SuiteResult</c> is golden-gated, and
/// <c>Vouchfx.Engine.Runtime</c> is not packable (<c>IsPackable</c> is <c>false</c> for every
/// project that does not opt in, and this one does not), so the shape is free to be the useful one
/// rather than the compatible one.
/// </para>
/// </remarks>
public sealed record SecurityAssurance(
    IReadOnlyList<string> Declared,
    IReadOnlyList<string> Confirmed,
    SecurityAbortKind? Refusal)
{
    /// <summary>
    /// Nothing declared, nothing confirmed, nothing refused — the value every path that has not yet
    /// learned anything starts from, and the value a caller that never asked about security gets.
    /// </summary>
    public static SecurityAssurance None { get; } = new(
        Array.Empty<string>(), Array.Empty<string>(), null);

    /// <summary>
    /// <strong>REQ-003's predicate, written once.</strong> The document declared a <c>security</c>
    /// block and the engine could not establish that it holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>raise = (aborted on an authoring fault ∧ some declared target went unconfirmed)
    /// ∨ (probe raised OrchestrationErrorKind.SecurityConfirmation)
    /// ∨ (the refusal was located in the declaration itself)</c>.
    /// </para>
    /// <para>
    /// <strong>The first disjunct reads <see cref="Confirmed"/>, and reading only
    /// <see cref="Declared"/> was wrong in a way the record's own shape invited.</strong> A
    /// shared-topology suite can record an authoring refusal for ONE scenario and still bring the
    /// topology up and have REQ-005's probe confirm every declared target — a scenario refused at a
    /// compile-time door sits beside siblings that run. Judged on
    /// <c>Declared.Count &gt; 0</c> alone that suite was told its declared assertion "was never
    /// confirmed" while the run holds the probe's own confirmation of it, which is a false positive
    /// in exactly the surface whose value is that its answers are trustworthy.
    /// </para>
    /// <para>
    /// <strong>It is "every declared target was confirmed", NOT "anything was confirmed".</strong>
    /// The weaker form (<c>Confirmed.Count == 0</c>) opens a fresh hole a suite can walk through: two
    /// secured targets, one confirmed and one not, would stop raising and exit 0 — the same class of
    /// false negative, reached one step later. A run vouches for a declaration only when it
    /// confirmed ALL of it.
    /// </para>
    /// <para>
    /// <strong>The comparison lives INSIDE the first disjunct, not above the three, and that is
    /// what the safety of this expression actually rests on.</strong> All three disjuncts require a
    /// specific non-null <see cref="Refusal"/>, so a run that reaches normal completion with a
    /// declared target missing from <see cref="Confirmed"/> and NO refusal recorded does not raise
    /// and exits 0. Nothing in this expression prevents that shape; it is unreachable today only
    /// because <c>SecuredEndpointProbe.ConfirmAsync</c> emits exactly one confirmation per declared
    /// target or throws — a convention in ANOTHER ASSEMBLY. It is therefore pinned where it is
    /// produced, by <c>SecuredEndpointProbeTests.Confirm_TwoDeclaredTargets_ConfirmsEachOfThem</c>,
    /// and its exit-0 consequence is pinned at the record tier by
    /// <c>SecurityConfirmationExitCodeTests.Unconfirmed_DeclaredShortfallWithNoRefusal_DoesNotRaise</c>,
    /// so a probe change that skipped a target turns a test red rather than a pipeline green.
    /// </para>
    /// <para>
    /// The third disjunct is a DEVIATION from REQ-003's two-clause formula, taken deliberately and
    /// measured: <c>security: mtls</c> binds no <c>SecuritySpec</c>, so the canonical walk reports
    /// nothing declared and the first two clauses take a shipped exit 4 to 0. See
    /// <see cref="SecurityAbortKind.SecurityDeclarationRejected"/> for the full account.
    /// </para>
    /// <para>
    /// <strong>Why the predicate lives on the record and not solely in <c>ExitCodes</c>.</strong>
    /// The exit-code decision is not its only reader: the parallel runner must fold N per-scenario
    /// assurances into one, and that fold is only correct if it keeps whichever scenario RAISES —
    /// scenarios under <c>--parallel</c> need not share an environment, so a naive union of
    /// <see cref="Declared"/> across slots would let an unsecured scenario's authoring fault pair
    /// with a sibling's declaration and redden a suite neither of them reddens alone. Spelling the
    /// predicate a second time in the fold is exactly the two-spellings-of-one-rule shape this
    /// codebase has watched drift repeatedly. <c>ExitCodes</c> holds the POLICY (which code a
    /// verdict maps to when the assurance is unconfirmed); this holds the PREDICATE, and there is
    /// one of each.
    /// </para>
    /// <para>
    /// <see cref="SecurityAbortKind.TopologyUnavailable"/> is absent from all three disjuncts
    /// deliberately — see its own remarks; that is #390's fence.
    /// </para>
    /// </remarks>
    public bool Unconfirmed =>
        (Refusal == SecurityAbortKind.AuthoringFault && SomeDeclaredTargetWentUnconfirmed)
        || Refusal == SecurityAbortKind.ProbeUnconfirmed
        || Refusal == SecurityAbortKind.SecurityDeclarationRejected;

    /// <summary>
    /// At least one declared target is missing from what the probe confirmed — including the
    /// ordinary pre-topology case, where nothing was confirmed because no topology was reached.
    /// </summary>
    /// <remarks>
    /// False for a document that declared nothing, which is why the first disjunct above no longer
    /// needs its own <c>Declared.Count &gt; 0</c> clause: an empty declaration has no unconfirmed
    /// member. That is the same fact stated once instead of twice.
    /// </remarks>
    private bool SomeDeclaredTargetWentUnconfirmed =>
        Declared.Any(name => !Confirmed.Contains(name, StringComparer.Ordinal));

    /// <summary>
    /// Records a refusal, keeping the more consequential one when a run has already recorded a
    /// different door.
    /// </summary>
    /// <param name="kind">The door that refused.</param>
    /// <remarks>
    /// A suite can reach more than one door: a scenario refused at compile time sits beside a
    /// sibling that runs, and the shared topology can then fail its own way. Precedence is
    /// <see cref="SecurityAbortKind.ProbeUnconfirmed"/> &gt;
    /// <see cref="SecurityAbortKind.SecurityDeclarationRejected"/> &gt;
    /// <see cref="SecurityAbortKind.AuthoringFault"/> &gt;
    /// <see cref="SecurityAbortKind.TopologyUnavailable"/> — most specific evidence about the
    /// declaration first — so a topology that merely failed to come up can never overwrite a
    /// recorded authoring refusal, and a measured probe failure always wins.
    /// </remarks>
    public SecurityAssurance Refusing(SecurityAbortKind kind) =>
        Refusal is null || Precedence(kind) > Precedence(Refusal.Value)
            ? this with { Refusal = kind }
            : this;

    /// <summary>Attaches the declared targets a verdict-assembly site walked, by NAME.</summary>
    /// <param name="declared">
    /// The result of <c>SecuredTargets.Enumerate</c> for this scenario — or, for a shared-topology
    /// suite, for every scenario in it concatenated. Only the names are kept, and duplicates
    /// collapse, so a caller may pass the same declaration once per scenario without inflating
    /// anything the predicate reads.
    /// </param>
    /// <remarks>
    /// <strong>REPLACES what an earlier call attached; it does not accumulate</strong> — and
    /// <c>ParallelSuiteRunner</c> depends on that, re-attaching a slot's own declaration over the
    /// assurance its core already returned rather than expecting the two to union. Pinned by
    /// <c>SecurityConfirmationExitCodeTests.Declaring_And_Confirming_ReplaceRatherThanAccumulate</c>,
    /// because a rename to <c>WithDeclared</c> this late would move a member on a type the CLI and
    /// both runners already read.
    /// </remarks>
    public SecurityAssurance Declaring(IReadOnlyList<SecuredTarget> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        return this with { Declared = Distinct(declared.Select(target => target.Name)) };
    }

    /// <summary>Attaches what REQ-005's probe confirmed once the topology was up, by NAME.</summary>
    /// <param name="confirmed">The topology's own <c>SecurityConfirmations</c>.</param>
    /// <remarks>Replaces rather than accumulates, exactly as <see cref="Declaring"/> does.</remarks>
    public SecurityAssurance Confirming(IReadOnlyList<SecurityConfirmation> confirmed)
    {
        ArgumentNullException.ThrowIfNull(confirmed);

        return this with { Confirmed = Distinct(confirmed.Select(c => c.TargetName)) };
    }

    /// <summary>
    /// Folds two per-scenario assurances into the one a suite reports, keeping whichever carries
    /// the strongest evidence — the SAME <see cref="Precedence"/> ordering
    /// <see cref="Refusing"/> applies when a single run reaches two doors.
    /// </summary>
    /// <param name="left">The accumulated assurance (declaration order).</param>
    /// <param name="right">The next scenario's assurance.</param>
    /// <returns>
    /// When exactly one raises (<see cref="Unconfirmed"/>), that one — so a suite is unconfirmed
    /// exactly when one of its scenarios is. Otherwise the one whose refusal ranks higher by
    /// <see cref="Precedence"/>, ties going to <paramref name="left"/>; failing that, whichever
    /// carries a refusal at all, or whichever declared anything.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The fold is over WHOLE assurances rather than field-by-field precisely because
    /// <see cref="Unconfirmed"/> is not distributive over a union of <see cref="Declared"/>: one
    /// scenario's declaration must never be paired with another scenario's refusal.
    /// </para>
    /// <para>
    /// <strong>The both-raise branch used to be left-biased, and a left bias here reintroduced the
    /// defect this whole type exists to remove.</strong> The fold walks slots in DECLARATION order,
    /// so "keep the left one" means "keep the alphabetically earlier file's refusal" — and
    /// <c>RunCommand</c> suppresses the security notice for
    /// <see cref="SecurityAbortKind.ProbeUnconfirmed"/>, so under <c>--parallel</c> two suites with
    /// byte-identical content printed different output depending on whether the broken scenario was
    /// called <c>a.e2e.yaml</c> or <c>z.e2e.yaml</c>. A FILENAME decided whether a security notice
    /// appeared, which is the same class of accident — an ordering deciding an answer — that door
    /// order used to be.
    /// </para>
    /// <para>
    /// <strong>The invariant that buys is order-independence in <see cref="Unconfirmed"/> and
    /// <see cref="Refusal"/> — named, because it does NOT hold of the record's identity.</strong>
    /// Those two projections are the whole of what a folded suite assurance is read for (the exit
    /// code and the notice), and neither depends on argument order. The record itself does: two
    /// non-raising <see cref="SecurityAbortKind.AuthoringFault"/> assurances differing only in
    /// <see cref="Declared"/> tie on both tests below, the tie goes to <paramref name="left"/>, and
    /// <c>Worse(a, b)</c> and <c>Worse(b, a)</c> therefore return DIFFERENT records carrying the
    /// same answer. Nothing reads <see cref="Declared"/> off a folded assurance — measured across
    /// <c>src/</c> — so the weaker invariant is the one to state and the one the tests assert.
    /// </para>
    /// </remarks>
    public static SecurityAssurance Worse(SecurityAssurance left, SecurityAssurance right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        // RAISING BEATS NON-RAISING FIRST, and only then does precedence decide. The two orders are
        // not interchangeable: a raising AuthoringFault beside a non-raising one (a sibling whose
        // declaration the probe confirmed) ties on precedence, and a tie resolved by position would
        // drop the raise.
        if (left.Unconfirmed != right.Unconfirmed)
        {
            return left.Unconfirmed ? left : right;
        }

        // Equal on raising, so compare the evidence itself — the SAME ordering the sequential rule
        // uses, applied whether both raise or neither does, so nothing here is decided by position
        // except a genuine tie.
        if (left.Refusal is { } leftKind && right.Refusal is { } rightKind)
        {
            return Precedence(rightKind) > Precedence(leftKind) ? right : left;
        }

        if (left.Refusal is not null)
        {
            return left;
        }

        if (right.Refusal is not null)
        {
            return right;
        }

        return left.Declared.Count > 0 ? left : right;
    }

    /// <summary>
    /// The distinct names, in first-occurrence order, as a materialised array — never a deferred
    /// query, since this value is stored on an immutable record and read repeatedly.
    /// </summary>
    private static string[] Distinct(IEnumerable<string> names) =>
        names.Distinct(StringComparer.Ordinal).ToArray();

    private static int Precedence(SecurityAbortKind kind) => kind switch
    {
        SecurityAbortKind.ProbeUnconfirmed => 3,
        SecurityAbortKind.SecurityDeclarationRejected => 2,
        SecurityAbortKind.AuthoringFault => 1,
        _ => 0,
    };
}
