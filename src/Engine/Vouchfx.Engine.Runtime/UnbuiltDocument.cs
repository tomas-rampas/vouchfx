// Vouchfx.Engine.Runtime — UnbuiltDocument (issue #411).
//
// WHAT A DOCUMENT THAT NEVER BECAME A SCENARIO CONTRIBUTES TO THE SUITE'S ONE SECURITY ASSURANCE.
//
// A document that PARSED and was then refused by `AstBuilder.Build` is absent from either runner's
// `scenarios` list — the CLI dropped it into its parse-failure list — and yet it declares as loudly
// as any sibling that made it through. This record carries the two things needed to say what it
// declared, and the ONE method that says it, so both run paths answer the question identically
// rather than each spelling it for itself.
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Runtime;

/// <summary>
/// A document the CALLER discovered, parsed, and then refused before either runner saw it —
/// present in neither runner's <c>scenarios</c> list, and still a source of evidence about what
/// the suite declared (issue #411).
/// </summary>
/// <param name="YamlText">
/// The document's verbatim text, as the caller read it.
/// </param>
/// <param name="Document">
/// The document the caller BOUND from that text — <c>YamlDocumentParser.Parse</c>'s own output,
/// never a reconstruction. <see cref="Environment"/> is projected from it, so the two cannot name
/// different documents: the alternative shape took the environment as a second constructor
/// parameter, which let a caller pair one document's text with another's environment and left the
/// fixture's own doc comment conceding it could only promise they agreed "in the fixture".
/// Construction-level is where that belongs.
/// </param>
/// <remarks>
/// <para>
/// <strong>Both fields, because each answers a question the other cannot.</strong> The bound
/// environment answers "which targets did this document declare a <c>security</c> block for", by
/// the canonical walk. The raw text answers the one question that walk provably cannot: a
/// <c>security</c> node that is not a mapping — <c>security: mtls</c>, the profile name written
/// where the block belongs, or a bare <c>security:</c> whose children are commented out — binds NO
/// <c>SecuritySpec</c> (<c>YamlDocumentParser.ParseSecurity</c> returns <see langword="null"/> for
/// anything that is not a mapping), so the walk reports nothing declared for a document that
/// plainly declares. That is the shape <see cref="SecurityAbortKind.SecurityDeclarationRejected"/>
/// exists for, and the schema door is the engine's only spelling of it.
/// </para>
/// <para>
/// <strong>Carrying the text is not the second spelling <c>SecuredTargets</c>' header forbids.</strong>
/// What that header rules out is a bespoke raw-YAML scan for a <c>security:</c> key, invented here
/// and free to disagree with the canonical walk. <see cref="Assure"/> invents nothing: it runs
/// <c>DocumentValidator.Validate</c> and <c>ScenarioRunner.RejectsASecurityDeclaration</c> — the
/// SAME two calls, in the same order, that the schema door in
/// <c>ScenarioRunner.RunSuiteAsync</c>'s compilation loop makes for every document that DID become
/// a scenario. The only difference is which documents reach it, and that difference is the defect:
/// an unbuilt document is by construction absent from <c>scenarios</c>, so the schema door's own
/// loop never iterates it, and before this it was the ONE class of document on which the engine
/// declined to ask a question it asks of every other.
/// </para>
/// <para>
/// This carries no disclosure that a <c>ScenarioAst</c> does not: the record's generated
/// <c>ToString()</c> could expand a declared <c>clientKeyPassword</c> exactly as a parsed
/// scenario's environment already could. Nothing interpolates it; the guard that matters is
/// downstream, where <see cref="SecurityAssurance"/> keeps a declared target's NAME and a one-way
/// digest of what it asserted — a <c>SecuredTargetIdentity</c> — rather than the
/// <c>SecuritySpec</c> values they came from (issue #408, narrowed by issue #415 from the name
/// alone to the name plus that digest; the digest never carries a declared
/// <c>clientKeyPassword</c>'s text unless one whole <c>${secret:}</c> token spans it, and reaches
/// no report, event or log either way).
/// </para>
/// </remarks>
public sealed record UnbuiltDocument(string YamlText, E2eDocument Document)
{
    /// <summary>
    /// The bound document, guarded at construction: <see cref="Environment"/> dereferences it, so a
    /// <see langword="null"/> here would surface as a <see cref="NullReferenceException"/> from a
    /// property rather than as a named argument fault at the call site.
    /// </summary>
    public E2eDocument Document { get; init; } =
        Document ?? throw new ArgumentNullException(nameof(Document));

    /// <summary>
    /// The <c>environment</c> block <see cref="Document"/> bound, or <see langword="null"/> when it
    /// declared none — exactly what <c>SecuredTargets.Enumerate</c> consumes for a scenario that
    /// did build.
    /// </summary>
    public EnvironmentSpec? Environment => Document.Environment;

    /// <summary>
    /// What this document contributes to a suite's security assurance: what it DECLARED, paired
    /// with the refusal that means nothing ever confirmed it — or
    /// <see cref="SecurityAssurance.None"/> when it declared nothing at all.
    /// </summary>
    /// <param name="registry">
    /// The frozen provider registry the composed schema is built from — the same one the caller's
    /// own scenarios are validated against.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>BOTH halves, because either alone closes nothing.</strong> Every disjunct of
    /// <see cref="SecurityAssurance.Unconfirmed"/> requires a non-null
    /// <see cref="SecurityAssurance.Refusal"/>, so a declaration on its own leaves the predicate
    /// false and the hole open with a green build. A document refused before it could be built is an
    /// authoring fault that started no container — precisely what
    /// <see cref="SecurityAbortKind.AuthoringFault"/>'s own summary already names ("a parse
    /// failure").
    /// </para>
    /// <para>
    /// <strong>The schema arm is checked FIRST and is not conditional on the walk, because the two
    /// disagree exactly where it matters.</strong> A rejected <c>security</c> node binds no
    /// <c>SecuritySpec</c>, so <c>Declared</c> is empty and the authoring arm — which is the
    /// conjunction <c>declared ∧ refused</c> — cannot raise for it. That is why the outcome is
    /// <see cref="SecurityAbortKind.SecurityDeclarationRejected"/>, which raises unconditionally,
    /// and it is the same deviation, for the same measured reason, that the schema door takes for a
    /// document that did become a scenario.
    /// </para>
    /// <para>
    /// <strong>NOTHING is contributed by a document that neither declares nor is refused AT a
    /// declaration</strong>, and that is load-bearing rather than tidy — but NOT for the reason
    /// this paragraph used to give, which is RETIRED and is written out rather than quietly
    /// deleted. It used to argue that both callers fold this value into an assurance whose
    /// <c>Declared</c> may belong to OTHER documents, so an unconditional refusal here would pair an
    /// unsecured file's fault with a sibling's declaration. That was true of the UNION the
    /// sequential path built, and it measured a real divergence at the time (a directory pairing an
    /// unsecured unbuildable file with a secured sibling whose topology fails exited 3 under
    /// <c>run</c> and 0 under <c>run --parallel 1</c>). It is now STRUCTURALLY IMPOSSIBLE: both
    /// callers reach this through <see cref="AssureAll"/>, and <see cref="SecurityAssurance.Worse"/>
    /// SELECTS a whole assurance — every return path is a bare argument, never a <c>with</c> — so a
    /// declaration and a refusal from two different documents can no longer occupy one record. That
    /// is the same fact the fold's own remarks below state, and <c>ScenarioRunner</c>'s fold note
    /// labels this claim retired in as many words. A maintainer checking the old reason would find
    /// it false and conclude the guard is removable; it is not, for the reason below.
    /// </para>
    /// <para>
    /// <strong>WHAT THE GUARD DOES STILL DO, measured by removing it and rebuilding.</strong> It
    /// keeps <see cref="SecurityAssurance.None"/> the FOLD'S IDENTITY ELEMENT rather than a refusal
    /// looking for a declaration, and it therefore governs which refusal and which declaration the
    /// suite REPORTS. Unguarded, <see cref="SecurityAbortKind.AuthoringFault"/> outranks
    /// <see cref="SecurityAbortKind.TopologyUnavailable"/> in <see cref="SecurityAssurance.Worse"/>,
    /// so an unsecured document's empty-declaration value wins the fold outright and displaces the
    /// suite's own evidence:
    /// <c>RunSuiteAsyncTests.RunSuiteAsync_UnsecuredUnbuiltDocument_LeavesAFailedTopologyExitingZero</c>
    /// fails with the SCENARIO's declaration gone
    /// (<c>Assert.Contains() … Collection: [] Not found: "api"</c>), and
    /// <c>RunSuiteAsyncTests.RunSuiteAsync_NoScenariosBesideAnUnsecuredUnbuiltDocument_ContributesNothing</c>
    /// fails with <c>Expected: null Actual: AuthoringFault</c>. NO <see cref="SecurityAssurance.Unconfirmed"/>
    /// assertion moved in either run — which is the whole of the retraction above, stated as a
    /// measurement, and it is also why issue #390's fence is not what this guard buys.
    /// </para>
    /// </remarks>
    internal SecurityAssurance Assure(StepKindRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var declared = SecuredTargets.Enumerate(Environment).ToArray();

        // The schema door's own two calls, applied to a document that door never iterates.
        //
        // COST, measured rather than assumed, and stated because the obvious reassurance is
        // wrong. `DocumentValidator.Validate` recomposes the schema from every provider
        // fragment: ~23.7 ms per call in Debug, ~13.7 ms of it composition. The bound is
        // O(unbuilt documents) — NOT "unreachable because the all-parse-failure rule returns
        // first". That rule short-circuits only when NO document parsed; a single working
        // sibling removes it and every unbuilt document in the directory then pays. Fifty of
        // them is ~1.2 s, which is noise beside a topology start, and a suite carrying fifty
        // broken files has a larger problem than latency. Left uncached deliberately: the
        // compilation loop already pays the same per scenario, so caching belongs there or
        // nowhere, not bolted onto this seam.
        //
        // AND IT IS UNCONDITIONAL RATHER THAN GATED ON `Environment is not null`, WHICH WAS
        // MEASURED, NOT ASSUMED (Copilot, PR #416). The proposed skip rests on an anchoring
        // property — that `RejectsASecurityDeclaration` can only match under an `environment`
        // mapping, which `ParseEnvironment` would then have bound — and the property is FALSE.
        // The two front-ends are different parsers: the walk reads YamlDotNet's
        // RepresentationModel (`YamlDocumentParser`), the schema door converts through
        // YamlDotNet's DESERIALISER (`SchemaResources.ConvertYamlToJsonDocument`), and a
        // RepresentationModel key lookup compares a scalar's TAG as well as its value. So a
        // document whose root key carries an explicit tag — `!!str environment:` above an
        // otherwise ordinary block — binds NO environment for the walk (`Environment is null`)
        // while the schema still reports `/environment/services/api/security`, and this call
        // returns `SecurityDeclarationRejected`. Skipping it there would answer
        // `SecurityAssurance.None` — nothing declared, nothing refused — so beside one sibling
        // that parses the suite exits 0 on a rejected security declaration: the exact shape issue
        // #411 closed, where adding an unrelated fault to a suite made the pipeline greener.
        // Pinned by
        // `RunSuiteAsyncTests.Assure_TaggedEnvironmentKeyBindingNoEnvironment_StillRecordsTheSchemaRefusal`.
        if (ScenarioRunner.RejectsASecurityDeclaration(
                DocumentValidator.Validate(YamlText, registry).Errors))
        {
            return SecurityAssurance.None
                .Declaring(declared)
                .Refusing(SecurityAbortKind.SecurityDeclarationRejected);
        }

        return declared.Length == 0
            ? SecurityAssurance.None
            : SecurityAssurance.None
                .Declaring(declared)
                .Refusing(SecurityAbortKind.AuthoringFault);
    }

    /// <summary>
    /// The one assurance standing for a whole list of unbuilt documents: each document's
    /// <see cref="Assure"/> value folded in WHOLE by <see cref="SecurityAssurance.Worse"/>
    /// (declaration-confirmation-matching, REQ-001).
    /// </summary>
    /// <param name="documents">
    /// The documents; <see langword="null"/> or empty yields <see cref="SecurityAssurance.None"/>,
    /// which is the identity of the fold and the value that costs a caller nothing.
    /// </param>
    /// <param name="registry">
    /// The frozen provider registry, passed through to <see cref="Assure"/> for the schema-door half
    /// of each answer.
    /// <para>
    /// NOT null-guarded here, deliberately and asymmetrically: an empty
    /// <paramref name="documents"/> returns before the registry is ever read, so
    /// <c>AssureAll(null, null!)</c> answers <see cref="SecurityAssurance.None"/> while
    /// <c>AssureAll(oneDocument, null!)</c> throws from <see cref="Assure"/>'s own
    /// <c>ThrowIfNull</c>. Demanding a registry for a call that does no work would reject a
    /// meaningful one — the same reason <c>ScenarioRunner.AssureUnbuiltDocumentsAlone</c> declines
    /// to BUILD one on that path — and the shape is unchanged from the per-runner helper this
    /// replaced, so it is not a regression this change introduced.
    /// </para>
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>ONE PLACE, BECAUSE BOTH RUN PATHS ASK THIS AND THEY USED TO ANSWER
    /// DIFFERENTLY.</strong> <c>ParallelSuiteRunner</c> folded whole values here;
    /// <c>ScenarioRunner.RunSuiteAsync</c> instead unioned each document's <c>Declared</c> into the
    /// suite's and took only its <see cref="SecurityAssurance.Refusal"/>, so an unbuilt document's
    /// declaration was satisfied by a SIBLING's probe confirmation of the same target — measured on
    /// the built CLI as a broken secured file exiting 0 under <c>run</c> and non-zero under
    /// <c>run --parallel N</c>, a flag flipping the answer (issue #415). Two spellings of one
    /// security rule is exactly the drift <see cref="SecurityAssurance"/> exists to remove, so there
    /// is now one, and it lives beside <see cref="Assure"/> rather than inside either runner.
    /// </para>
    /// <para>
    /// <strong>An unbuilt document's declaration is never confirmed, and that is a decision rather
    /// than a limitation.</strong> Each contributed value's <see cref="SecurityAssurance.Confirmed"/>
    /// is empty by construction: nothing downstream of such a document ran, and no guard ever proved
    /// its <c>environment</c> is the one the topology started from — the shared-<c>environment</c>
    /// divergence guard walks <c>scenarios</c>, and an unbuilt document is absent from that list by
    /// construction. The fail-closed direction was taken deliberately: its worst case is a suite
    /// containing a broken secured file exiting non-zero, against the alternative's green CI on an
    /// <c>mtls</c> assertion nothing exercised.
    /// </para>
    /// <para>
    /// ONE assurance PER DOCUMENT, so a document's declaration is never paired with a DIFFERENT
    /// document's refusal — the same reason the parallel path's slot fold keeps whole assurances.
    /// </para>
    /// <para>
    /// Called from FOUR sites: each runner's gather, and each runner's empty-<c>scenarios</c> arm,
    /// which returns before its gather and used to discard its documents (Copilot, PR #416). The CLI
    /// can reach neither empty arm with unbuilt documents — it builds them inside its own
    /// <c>parsed.Count &gt; 0</c> block, and issue #278's all-parse-failure rule owns the
    /// no-scenario case and exits 4 ahead of both runners — so those two are about what the runners
    /// ANSWER a non-CLI caller, not about an exit code. Nothing here decides one, so #278's question
    /// still has exactly one answer in one place.
    /// </para>
    /// </remarks>
    internal static SecurityAssurance AssureAll(
        IReadOnlyList<UnbuiltDocument>? documents, StepKindRegistry registry) =>
        documents is not { Count: > 0 }
            ? SecurityAssurance.None
            : documents.Aggregate(
                SecurityAssurance.None,
                (accumulated, document) => SecurityAssurance.Worse(
                    accumulated, document.Assure(registry)));
}
