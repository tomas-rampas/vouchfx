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
/// This enum names a CAUSE, never a verdict and never an exit code. Two of its members can raise
/// REQ-018's non-zero exit and one deliberately cannot; that decision lives in
/// <see cref="SecurityAssurance.Unconfirmed"/> and nowhere else.
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
    /// to raise. The schema door, forty lines away in the same method, argued the opposite and
    /// widened. Both were sound in isolation and they cannot both be the rule. The wide one wins: a
    /// secured document that aborts before any container starts is unconfirmable <em>whatever</em>
    /// aborted it, because nothing downstream of the refusal ever runs to confirm it.
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
    /// in the declaration itself); the single predicate in
    /// <see cref="SecurityAssurance.Unconfirmed"/> still makes the whole decision. The alternative
    /// — a second spelling of "does this document declare security" that reads the raw YAML rather
    /// than the AST — is exactly what <c>SecuredTargets</c>' own header exists to prevent.
    /// </para>
    /// </remarks>
    SecurityDeclarationRejected,

    /// <summary>
    /// The topology failed to start for a reason that is NOT a security-confirmation failure — an
    /// unhealthy container, an image that cannot be pulled, a seed failure, a health-gate timeout.
    /// </summary>
    /// <remarks>
    /// <strong>This member never raises, and that is the fence rather than an oversight.</strong> A
    /// secured suite that reaches the topology and fails the health gate still exits 0 by default;
    /// that is issue #390 and it is deliberately out of scope here. It needs a resource-scoped
    /// narrowing plus an endpoint-resolvability preflight, and its own blast-radius measurement —
    /// closing it from inside this derivation would be one clause in the same expression and would
    /// redden every suite whose unrelated container was slow to come up.
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
/// below compares what was declared against what was confirmed — so the specs are not carried at
/// all rather than carried carefully.
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
    public SecurityAssurance Declaring(IReadOnlyList<SecuredTarget> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        return this with { Declared = Distinct(declared.Select(target => target.Name)) };
    }

    /// <summary>Attaches what REQ-005's probe confirmed once the topology was up, by NAME.</summary>
    /// <param name="confirmed">The topology's own <c>SecurityConfirmations</c>.</param>
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
