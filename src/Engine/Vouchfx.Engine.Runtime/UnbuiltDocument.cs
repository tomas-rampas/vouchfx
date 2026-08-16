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
/// downstream, where <see cref="SecurityAssurance"/> keeps declared target NAMES rather than the
/// specs they came from (issue #408).
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
    /// <see cref="SecurityAssurance.Refusal"/>, so names on their own leave the predicate false and
    /// the hole open with a green build. A document refused before it could be built is an
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
    /// declaration</strong>, and that is load-bearing rather than tidy. Both callers fold this
    /// value into an assurance whose <c>Declared</c> may belong to OTHER documents, so an
    /// unconditional refusal here would pair an unsecured file's fault with a sibling's
    /// declaration. Measured, when the sequential stamp was unconditional: a directory pairing an
    /// unsecured unbuildable file with a secured sibling whose topology fails exited 3 under
    /// <c>run</c> and 0 under <c>run --parallel 1</c> — a divergence between the two paths, and a
    /// silent override of issue #390, whose whole content is that a health-gate failure must not
    /// raise.
    /// </para>
    /// </remarks>
    internal SecurityAssurance Assure(StepKindRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var declared = SecuredTargets.Enumerate(Environment).ToArray();

        // The schema door's own two calls, applied to a document that door never iterates.
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
}
