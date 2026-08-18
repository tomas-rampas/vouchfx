// Vouchfx.Engine.Authoring — SecuredTargets (authenticated-infrastructure-mtls, slice E —
// m5, peer review fix round three).
//
// ONE spelling of "which declared targets carry a `security` block, and in what order".
//
// It had grown to three, in three assemblies: `SuiteTopology.DeclaresSecurity` (the
// no-accessor guard), `EnvironmentSecurityValidator.DeclaresSecurity` (the cheap
// does-this-suite-claim-anything question), and `SecuredEndpointProbe.EnumerateSecuredTargets`
// (the walk the probe actually confirms). All three read the same two dictionaries in the same
// services-then-dependencies order, and the probe's own remarks and the guard's own remarks
// each assert — in prose — that they agree with the others. This branch's own
// `SecurityArtifactPath` header argues exactly the opposite discipline for exactly this shape:
// "two spellings of one security rule is how the two drift". Three is worse.
//
// `Vouchfx.Engine.Authoring` is the same home that argument chose for `IsContainedWithin`, for
// the same reason: Orchestration and Runtime both reference it and neither references the other,
// and it is where `SecuritySpec`/`EnvironmentSpec` already live.
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Vouchfx.Engine.Abstractions.Secrets;

namespace Vouchfx.Engine.Authoring.Model;

/// <summary>
/// One declared target carrying a <c>security</c> block.
/// </summary>
/// <param name="Name">The declared service or dependency name.</param>
/// <param name="Kind">
/// <c>"service"</c> for a declared service, or the dependency's declared <c>type</c>
/// (e.g. <c>"kafka"</c>) — the same discriminator the security-profile registry's
/// <c>AppliesTo</c> takes.
/// </param>
/// <param name="Security">The declared block itself, never <see langword="null"/>.</param>
public readonly record struct SecuredTarget(string Name, string Kind, SecuritySpec Security);

/// <summary>
/// The identity of ONE declaration: its target name, plus a digest of everything the
/// <c>security</c> block itself declares. Two declarations of the same name whose <c>security</c>
/// blocks differ have different identities.
/// <para>
/// The narrower wording is deliberate and replaces "everything that declaration ASSERTS", which was
/// a categorical claim this digest does not support: the block's own text is hashed, the declared
/// shape it is RESOLVED AGAINST is not. <see cref="SecuredTargets.IdentityOf"/>'s remarks state both
/// scope limits — that one and the suite-directory one — and what a widening caller must do about
/// them.
/// </para>
/// </summary>
/// <param name="Name">
/// The declared service or dependency name — the same value <see cref="SecuredTarget.Name"/>
/// carries, retained so a diagnostic built from an identity can still say WHICH target it is
/// about without re-deriving it.
/// <para>
/// <strong>Non-nullable, with one documented exception.</strong>
/// <see cref="SecuredTargets.IdentityOf"/> deliberately digests a <c>default(SecuredTarget)</c>
/// rather than throwing, and that struct's own <c>Name</c> is <see langword="null"/> — so an
/// identity derived from one carries a null here and renders as <c>@&lt;hex&gt;</c>. Every
/// ordinary producer goes through <see cref="SecuredTargets.Enumerate"/>, which yields a name
/// off a dictionary key and can never do that; the annotation is left non-nullable rather than
/// widened to <c>string?</c> because widening it would ripple a null check through every
/// consumer for an input only a direct engine embedder can construct. A caller that DOES hand
/// this type a default struct must not dereference <c>Name</c>.
/// </para>
/// </param>
/// <param name="Digest">
/// An uppercase hex SHA-256 over the declaration's canonical framing (see
/// <see cref="SecuredTargets.IdentityOf"/>). Machine-facing: it is compared, never read.
/// <para>
/// <strong>PROCESS-LOCAL. Never persist this value, transmit it, or compare it against a digest
/// computed elsewhere.</strong> The framing hashes each string's UTF-16 code units in the PLATFORM'S
/// OWN byte order, so the same declaration digests differently on a big-endian runtime; and the
/// framing itself covers whatever fields <see cref="SecuredTargets.IdentityOf"/> appends today, so
/// it moves when a field joins the <c>security</c> block. This is a value to compare against another
/// digest produced by the same call in the same process — it is not a stable identifier and carries
/// no compatibility promise. Stated on this SHIPPED member rather than only beside the private
/// framing helper that forces it, because an external consumer of this packable assembly reads only
/// the public surface, and "compared, never read" alone does not tell them not to store one.
/// </para>
/// </param>
/// <remarks>
/// <para>
/// <strong>Why the name alone was not enough (issue #415).</strong> A declared target used to count
/// as confirmed when SOMETHING confirmed a target of the same name, rather than when a probe
/// confirmed THAT declaration. Two documents in one suite can each declare <c>api</c> — one
/// asserting <c>profile: mtls</c> on port 9093 with a client certificate, the other asserting
/// <c>profile: tls</c> on 8443 with none — and the confirmation of either satisfied both. The
/// name is the target; the identity is the ASSERTION about it, and the assertion is what a run
/// vouches for.
/// </para>
/// <para>
/// <strong>Structural equality is the whole mechanism.</strong> This is a record struct precisely
/// so that <c>==</c>, <c>Contains</c> and <c>Distinct</c> compare both members with no comparer
/// argument and no chance of a call site comparing one member by accident — which is the shape the
/// defect took.
/// </para>
/// </remarks>
public readonly record struct SecuredTargetIdentity(string Name, string Digest)
{
    /// <summary>
    /// Renders the name and the digest, and NOTHING ELSE — never the declaration the digest was
    /// derived from.
    /// </summary>
    /// <remarks>
    /// Written out rather than left to the compiler so the disclosure boundary is pinned in code:
    /// this type is shaped so that a diagnostic COULD safely interpolate it, which is only true
    /// while its two members are the only things it prints. Nothing does today, and the residue
    /// argument elsewhere in this file rests on that FACT ("compared, never rendered") rather than
    /// on this shape — the shape is what would keep a future diagnostic honest, not what makes the
    /// digest safe now. §17 and <c>SecuritySpec</c>'s own
    /// header forbid rendering a declared <c>clientKeyPassword</c>, and this member renders neither
    /// it nor anything derived from it: <see cref="Digest"/> hashes the field's text only when
    /// <c>SecretReference.TryParse</c> says one whole <c>${secret:}</c> token spans the value —
    /// which is NOT proof the value is a pointer — and a digest is a value to COMPARE, never one to
    /// print (see <see cref="SecuredTargets.IdentityOf"/> for the full argument).
    /// </remarks>
    public override string ToString() => $"{Name}@{Digest}";
}

/// <summary>
/// Enumerates the declared targets that carry a <c>security</c> block.
/// </summary>
public static class SecuredTargets
{
    /// <summary>
    /// The target kind reported for a declared service, as opposed to a dependency's own
    /// declared <c>type</c>.
    /// </summary>
    public const string ServiceKind = "service";

    /// <summary>
    /// The document segment naming the services map in a field path:
    /// <c>environment.<strong>services</strong>.&lt;name&gt;.security.…</c>.
    /// </summary>
    public const string ServicesFieldSegment = "services";

    /// <summary>
    /// The document segment naming the dependencies map in a field path:
    /// <c>environment.<strong>dependencies</strong>.&lt;name&gt;.security.…</c>.
    /// </summary>
    public const string DependenciesFieldSegment = "dependencies";

    /// <summary>
    /// The field-path segment for the map <paramref name="target"/> was declared in:
    /// <see cref="ServicesFieldSegment"/> for a service, <see cref="DependenciesFieldSegment"/>
    /// otherwise.
    /// </summary>
    /// <param name="target">A target yielded by <see cref="Enumerate"/>.</param>
    /// <remarks>
    /// <para>
    /// Every diagnostic naming a <c>security</c> field spells the same path
    /// (<c>environment.&lt;plural&gt;.&lt;name&gt;.security.&lt;field&gt;</c>). This member exists
    /// so the DERIVATION of the plural from a <see cref="SecuredTarget"/> is written once:
    /// <c>ScenarioRunner</c>'s secret-reference scan is its consumer, and
    /// <c>EnvironmentSecurityValidator</c>'s own walk uses the two segment constants above.
    /// </para>
    /// <para>
    /// It is NOT yet the single home of the plural across the engine. Several producers that walk
    /// services and dependencies themselves still pass the literals —
    /// <c>SecurityProfileWiringValidator</c>, <c>SecurityConfigurationAccessor</c> and
    /// <c>EnvironmentMapper</c> among them — and routing those is filed separately rather than
    /// done here. Claiming otherwise would be the kind of universal this file's own header warns
    /// about, asserted in prose and false in the tree.
    /// </para>
    /// <para>
    /// <see cref="SecuredTarget.Kind"/> is the fixed <see cref="ServiceKind"/> sentinel for a
    /// service and the dependency's own declared <c>type</c> otherwise, so a dependency typed
    /// literally <c>"service"</c> would map to the services segment. That is unreachable from
    /// YAML twice over: <c>$defs/dependency</c>'s <c>type</c> is a closed enum with no
    /// <c>service</c> member, and a <c>security</c> block is forbidden on any dependency whose
    /// type is not <c>kafka</c>.
    /// </para>
    /// </remarks>
    public static string PluralFor(SecuredTarget target) =>
        target.Kind == ServiceKind ? ServicesFieldSegment : DependenciesFieldSegment;

    /// <summary>
    /// Yields every declared target carrying a <c>security</c> block: <strong>services first,
    /// then dependencies</strong>, each in declaration order.
    /// </summary>
    /// <param name="environment">The suite's environment declaration, or <see langword="null"/>.</param>
    /// <returns>The secured targets, in the fixed order above; empty when none is declared.</returns>
    /// <remarks>
    /// The order is part of the contract, not an implementation detail: the pre-topology
    /// validator and the post-topology probe both walk it, so a suite with two faults reports
    /// the same one at both stages.
    /// </remarks>
    public static IEnumerable<SecuredTarget> Enumerate(EnvironmentSpec? environment)
    {
        if (environment?.Services is { } services)
        {
            foreach (var (name, spec) in services)
            {
                if (spec.Security is { } declared)
                {
                    yield return new SecuredTarget(name, ServiceKind, declared);
                }
            }
        }

        if (environment?.Dependencies is { } dependencies)
        {
            foreach (var (name, spec) in dependencies)
            {
                if (spec.Security is { } declared)
                {
                    yield return new SecuredTarget(name, spec.Type, declared);
                }
            }
        }
    }

    /// <summary>
    /// True when any declared service or dependency carries a <c>security</c> block.
    /// </summary>
    /// <param name="environment">The suite's environment declaration, or <see langword="null"/>.</param>
    /// <remarks>
    /// The cheap question "does this suite claim any security at all", asked by callers that
    /// must apply a security-only rule without paying for the full validation walk. Defined in
    /// terms of <see cref="Enumerate"/> rather than beside it, so the two cannot answer
    /// differently.
    /// </remarks>
    public static bool Any(EnvironmentSpec? environment) => Enumerate(environment).Any();

    /// <summary>
    /// The <see cref="SecuredTargetIdentity"/> of one declared target: its name, plus a digest over
    /// everything that declaration asserts.
    /// </summary>
    /// <param name="target">A target yielded by <see cref="Enumerate"/>.</param>
    /// <remarks>
    /// <para>
    /// <strong>ONE derivation, called by both ends of the comparison.</strong>
    /// <c>SecurityAssurance.Declaring</c> derives the declared side from the walk above;
    /// <c>SecuredEndpointProbe.ConfirmAsync</c> derives the confirmed side from the SAME walk and
    /// hands it to the confirmation it emits. Neither recomputes a digest by hand. A second
    /// spelling of "what makes two declarations the same declaration" is the drift this file's own
    /// header exists to prevent, and here it would be worse than drift: the two sides disagreeing
    /// makes every secured suite in the product raise.
    /// </para>
    /// <para>
    /// <strong>The framing is EXPLICIT LENGTH-PREFIXING, not JSON, and the choice is
    /// load-bearing.</strong> The digest must be injective over the field tuple: naive
    /// concatenation with a separator collides — <c>caCert: "a/b"</c> against
    /// <c>caCert: "a", clientCert: "b"</c> is the canonical example — so every field is written as
    /// a one-byte presence marker, then (when present) a four-byte big-endian byte count, then the
    /// value's UTF-16 code units. Prefixing the length makes the concatenation unambiguously
    /// parseable and therefore injective over the field tuple.
    /// </para>
    /// <para>
    /// <strong>The encoding is UTF-16 code units, and that too is load-bearing.</strong> An earlier
    /// framing wrote <c>Encoding.UTF8.GetBytes</c>, whose replacement fallback maps an unpaired
    /// surrogate to U+FFFD — so <c>caCert: "ca\uD800.pem"</c> and <c>caCert: "ca\uFFFD.pem"</c>
    /// produced identical bytes of identical length and therefore ONE identity, while NTFS stores
    /// UTF-16 filenames and .NET's path APIs pass both through, so both can name distinct files on
    /// disk. Hashing the code units directly is total over every <see langword="string"/>, so the
    /// injectivity claimed above holds whatever the field values contain, and it adds no throw
    /// path, so the digest still cannot fail open.
    /// </para>
    /// <para>
    /// <strong>An identity is scoped to ONE suite directory.</strong> Path-valued fields are hashed
    /// as the DECLARED RELATIVE TEXT, never as a resolved path, so two documents in different suite
    /// directories declaring a byte-identical <c>security</c> block share one identity while
    /// potentially naming different files on disk. Identities derived under different suite
    /// directories must therefore not be compared. Today that is not a hazard — the shared-topology
    /// accessor roots every scenario at <c>compilations[0].ScenarioBaseDirectory</c>, so every
    /// scenario in a run resolves against one directory regardless of where its document lives —
    /// but any future caller that widens the comparison across directories must revisit this.
    /// </para>
    /// <para>
    /// <strong>And an identity covers the <c>security</c> BLOCK, not the declared shape that block
    /// is resolved against.</strong> The second scope limit, and the reason the summary on
    /// <see cref="SecuredTargetIdentity"/> no longer claims the digest covers everything a
    /// declaration asserts. <c>ServiceEndpointNaming.ResolveSecuredPort</c> accepts a NAMED
    /// <c>endpoint</c> selector (<c>http</c>, <c>tcp-&lt;port&gt;</c>) and resolves it against the
    /// service's own <c>ports:</c>/<c>httpPort:</c>/<c>project:</c> declaration — none of which is an
    /// input here, since <see cref="DigestOf"/> hashes the selector's TEXT and never its resolution.
    /// So two documents declaring a byte-identical <c>security: { profile: mtls, endpoint: tcp-9093,
    /// … }</c> on services whose <c>ports:</c> differ assert mutual TLS on DIFFERENT ports and share
    /// one identity. It is nonetheless strictly stronger than the target-NAME matching it replaced,
    /// and it is not reachable today: both sides of every comparison the engine makes are derived
    /// from ONE <c>environment</c> block — the shared-<c>environment</c> divergence guard forces a
    /// sequential suite's scenarios byte-identical, a parallel slot compares one scenario's
    /// declaration against its own topology's confirmations, and an unbuilt document, which bypasses
    /// that guard, carries an EMPTY <c>Confirmed</c>, so nothing of its is ever matched. Any future
    /// caller that compares identities derived from two environments the guard did not hold together
    /// must revisit this: the fix is to hash the RESOLVED port beside the selector, which needs the
    /// whole <c>ServiceSpec</c> rather than the <c>SecuritySpec</c> this walk yields.
    /// </para>
    /// <para>
    /// JSON was the alternative and is rejected for two reasons. Its determinism would rest on
    /// <c>JsonSerializerOptions</c> defaults and on reflected property order — properties of the
    /// serialiser rather than of this file, free to move under a runtime upgrade and taking every
    /// stored digest with them. And this is a <c>IsPackable</c> assembly, so a reflection-based
    /// serialiser would put trim/AOT analysis warnings on a shipped surface in order to hash a
    /// tuple this file already writes by hand. <strong>"Six strings" is what this sentence used to
    /// say and it was never right, so the count is not restated here — the inputs are whatever
    /// <see cref="DigestOf"/> appends, in that order</strong>: the target's name and kind, the
    /// spec's own presence, then (when present) <c>profile</c>, <c>endpoint</c>, <c>caCert</c>,
    /// <c>clientCert</c>, <c>clientKey</c>, the <c>clientKeyPassword</c> declaration — whose text is
    /// CONDITIONAL, so the arity is not even fixed — and the <c>serverArtifacts</c> list, itself a
    /// presence bit, a count and two strings per entry. A number written here is a second spelling
    /// of that method, free to go stale the next time a field joins it, and it did.
    /// </para>
    /// <para>
    /// <strong>A <see langword="null"/> field and an empty-string field differ</strong> — the
    /// presence marker is written for both and the length only for the second — because an
    /// undeclared <c>caCert</c> is not the same declaration as <c>caCert: ""</c>. REQ-004(b)
    /// already requires an undeclared <c>caCert</c> to be treated as absent rather than as a
    /// missing-but-implied field, and a digest that conflated the two would let those two
    /// declarations cross-satisfy each other.
    /// </para>
    /// <para>
    /// <strong><see cref="SecuritySpec.ClientKeyPassword"/> enters the digest as its PRESENCE
    /// always, and as its TEXT only when <c>SecretReference.TryParse</c> says one whole
    /// <c>${secret:}</c> token spans it.</strong> On any schema-validated path that property holds a
    /// <c>${secret:}</c> reference, but the parser is deliberately lenient and a literal passphrase
    /// binds regardless — from AUTHORED YAML as well as from a direct engine embedder, because this
    /// walk runs BEFORE the schema refuses the document (the paragraph below measures where) —
    /// <c>SecuritySpec</c>'s own header states this and
    /// <c>SecuritySpecBindingTests.Parse_ClientKeyPasswordLiteral_IsStillBound_ParserStaysLenient</c>
    /// pins it. §17 governs a literal: a digest is one-way but not confidential, a passphrase is
    /// low-entropy, and a SHA-256 of a low-entropy secret is brute-forceable by anyone who obtains
    /// it — so a bare literal, which <c>TryParse</c> refuses, never enters the hash at all.
    /// </para>
    /// <para>
    /// A whole <c>${secret:source/path}</c> token is a different thing, and §17.1.1 sanctions
    /// hashing a REFERENCE outright — the reproducibility envelope already hashes the reference text
    /// and never the resolved value. So the text is hashed IFF <c>SecretReference.TryParse</c>
    /// returns true, which requires one whole token to span the value with no surrounding literal
    /// text.
    /// </para>
    /// <para>
    /// <strong>That is NOT proof the value is a pointer, and this file no longer claims it is.</strong>
    /// A reference path terminates at the first closing brace and is otherwise unrestricted, so a
    /// SECOND <c>${secret:</c> lead-in is swallowed INTO the path and everything after it is
    /// arbitrary author text: <c>${secret:env/PASS${secret:hunter2}</c> satisfies <c>TryParse</c>
    /// AND the schema's anchored <c>pattern</c>, so it is reachable from authored YAML, and its
    /// swallowed literal tail IS hashed. <c>SecuritySpec</c>'s own header names that very inference
    /// ("<c>if (TryParse(v)) quote(v);</c>") as a reproduced disclosure defect, and
    /// <c>SecretReference.WithheldValueMessage</c> spells the swallowing out. The decision here
    /// stands anyway, but for a reason that is NOT "the value is proven safe": we are HASHING, not
    /// quoting, §17.1.1 sanctions hashing a reference, and the digest is a value this engine COMPARES
    /// and never renders. <c>ValidateSecretBearingField</c>, the stronger proof, needs a runtime
    /// secret-source list, and threading one into a pure identity function on a packable assembly is
    /// not a signature this type will take.
    /// </para>
    /// <para>
    /// <strong>What that argument does and does not say about the presence-bit-only collapse: no
    /// schema-VALID document reaches it, but an authored document CAN.</strong> The schema's
    /// <c>clientKeyPassword</c> <c>pattern</c> — <c>^\$\{secret:[A-Za-z0-9_-]+/[^}]+\}(?![\s\S])</c>
    /// in <c>root-language-schema.json</c>'s <c>$defs/security</c> — is the ANCHORED form of
    /// <c>TryParse</c>'s own grammar, held in step by <c>SecretReferencePatternParityTests</c>, so a
    /// declaration the schema ACCEPTS always takes the hashed arm. A declaration it REFUSES still
    /// reaches the collapse, and on the ordinary <c>run</c> path rather than only in an embedder:
    /// the identity walk runs at <c>ScenarioRunner</c>'s <c>Declaring</c> call BEFORE that file's
    /// <c>DocumentValidator.Validate</c>, and unconditionally in <c>UnbuiltDocument.Assure</c>,
    /// while <c>YamlDocumentParser</c> binds a literal into the property —
    /// <c>SecuritySpecBindingTests.Parse_ClientKeyPasswordLiteral_IsStillBound_ParserStaysLenient</c>
    /// pins that. An authored <c>clientKeyPassword: hunter2</c> is therefore digested through this
    /// branch, before the schema refuses the document.
    /// </para>
    /// <para>
    /// The security residue of that is nil for ENGINE reasons, NOT because an author cannot reach
    /// the branch. The document is refused, so nothing it declared is ever confirmed; its identity
    /// cannot be satisfied by a whole-token declaration's, because the second marker
    /// <see cref="AppendPassphraseDeclaration"/> writes differs and the digests diverge with it; and
    /// <c>SecurityAssurance.Worse</c> SELECTS one whole assurance rather than unioning one
    /// document's <c>Declared</c> against another's <c>Confirmed</c>. And on that input the rule
    /// does exactly its job: the literal passphrase is the one thing NOT hashed.
    /// </para>
    /// <para>
    /// Both bits are needed, and the earlier presence-only framing was not enough. It kept the
    /// discrimination between "this declaration says the key is encrypted" and "this one says it is
    /// not" — but it ALSO collapsed <c>clientKeyPassword: ${secret:vault/prod-key}</c> onto
    /// <c>clientKeyPassword: ${secret:env/DEV_KEY}</c>: two different references naming two
    /// different passphrases, sharing one identity, which is precisely the cross-satisfaction
    /// #415 exists to close. What now holds: two declarations whose passphrase references differ get
    /// different identities; two declarations whose passphrase values are NOT whole-token references
    /// still collapse to the presence bit, because either might be the secret itself.
    /// </para>
    /// </remarks>
    public static SecuredTargetIdentity IdentityOf(SecuredTarget target) =>
        new(target.Name, DigestOf(target));

    /// <summary>
    /// The uppercase hex SHA-256 over the declaration's canonical framing — see
    /// <see cref="IdentityOf"/> for the framing rules this implements and why they are those rules.
    /// </summary>
    private static string DigestOf(SecuredTarget target)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendText(hash, target.Name);
        AppendText(hash, target.Kind);

        // The spec's own presence, so a `default(SecuredTarget)` — which the record struct permits
        // even though Enumerate never yields one — digests rather than throwing.
        var spec = target.Security;
        AppendPresence(hash, spec is not null);

        if (spec is not null)
        {
            AppendText(hash, spec.Profile);
            AppendText(hash, spec.Endpoint);
            AppendText(hash, spec.CaCert);
            AppendText(hash, spec.ClientCert);
            AppendText(hash, spec.ClientKey);

            AppendPassphraseDeclaration(hash, spec.ClientKeyPassword);

            var artifacts = spec.ServerArtifacts;
            AppendPresence(hash, artifacts is not null);

            if (artifacts is not null)
            {
                AppendCount(hash, artifacts.Count);

                foreach (var artifact in artifacts)
                {
                    AppendPresence(hash, artifact is not null);

                    if (artifact is not null)
                    {
                        AppendText(hash, artifact.Source);
                        AppendText(hash, artifact.Target);
                    }
                }
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// Writes the declared <c>clientKeyPassword</c> under §17: its presence, then whether one whole
    /// <c>${secret:}</c> token spans its text, then — only when one does — the text itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three-part, UNIQUELY-DECODABLE, PREFIX-DETERMINED framing (presence bit, hashable bit, then
    /// the length-prefixed text only when the hashable bit is set), so the injectivity argument in
    /// <see cref="IdentityOf"/> still holds: the two markers are always written, and the second one
    /// decides unambiguously whether a length-prefixed run follows. The framing is two parts or
    /// three, so it is NOT fixed-arity; what carries the injectivity is that the decision is
    /// deterministic from the prefix already read, not that the run has a constant length.
    /// </para>
    /// <para>
    /// <c>SecretReference.TryParse</c> returns true only when the value is exactly ONE whole
    /// <c>${secret:source/path}</c> token with no surrounding literal text, and §17.1.1 sanctions
    /// hashing a reference (the reproducibility envelope already hashes the reference text, never
    /// the resolved value). When it returns false the value may be a literal passphrase, so nothing
    /// but the presence bit is recorded.
    /// </para>
    /// <para>
    /// A true from <c>TryParse</c> is NOT proof the value is a pointer: a path terminates at the
    /// first closing brace, so a second <c>${secret:</c> lead-in is swallowed into it and its tail
    /// is arbitrary author text that gets hashed with the rest — see <c>SecuritySpec</c>'s header
    /// and <c>SecretReference.WithheldValueMessage</c>. That residual is ACCEPTED here because we
    /// are HASHING rather than quoting and the digest is compared and never rendered, not because
    /// the value has been shown to be safe. <c>ValidateSecretBearingField</c>, which applies the
    /// remaining rule, is deliberately NOT used: it needs a runtime secret-source list, which this
    /// pure identity function on a packable assembly will not take as a parameter. See
    /// <see cref="IdentityOf"/>'s remarks for the full argument.
    /// </para>
    /// </remarks>
    private static void AppendPassphraseDeclaration(IncrementalHash hash, string? declared)
    {
        AppendPresence(hash, declared is not null);

        var isWholeReference = declared is not null && SecretReference.TryParse(declared, out _);
        AppendPresence(hash, isWholeReference);

        if (isWholeReference)
        {
            AppendChars(hash, declared!);
        }
    }

    /// <summary>
    /// Writes one field: its presence, then — only when present — its length-prefixed text. The
    /// length prefix is what makes the concatenation injective (see <see cref="IdentityOf"/>).
    /// </summary>
    private static void AppendText(IncrementalHash hash, string? value)
    {
        AppendPresence(hash, value is not null);

        if (value is not null)
        {
            AppendChars(hash, value);
        }
    }

    /// <summary>
    /// Writes one non-null string as a four-byte big-endian byte count followed by its UTF-16 code
    /// units.
    /// </summary>
    /// <remarks>
    /// The code units, NOT <c>Encoding.UTF8.GetBytes</c>: the UTF-8 encoder's replacement fallback
    /// maps an unpaired surrogate to U+FFFD, so <c>"ca\uD800.pem"</c> and <c>"ca\uFFFD.pem"</c>
    /// hashed to the same bytes of the same length — two declarations, one identity, and NTFS
    /// stores UTF-16 filenames so both can name distinct files on disk. Reinterpreting the chars is
    /// total over every <see langword="string"/> and adds no throw path for any string a document
    /// can carry, so the digest keeps its fail-closed property. NOT "no throw path" unqualified,
    /// which is a universal this call does not satisfy: <c>MemoryMarshal.AsBytes</c>
    /// computes <c>checked(span.Length * sizeof(char))</c> and throws an
    /// <see cref="OverflowException"/> above <c>int.MaxValue / 2</c> chars. No document-borne value
    /// reaches that — the CLI refuses a file above <c>ScenarioDiscovery.MaxDocumentSizeBytes</c>
    /// (1 MiB) before parsing it — so the shortfall is in the sentence, not in the guarantee.
    /// <para>
    /// Endianness is irrelevant here and must not be "fixed" for portability: these digests are
    /// compared within ONE process and are never persisted, transmitted, or compared against a
    /// digest computed elsewhere. That constraint binds callers, not just this method, so it is
    /// stated on the shipped <see cref="SecuredTargetIdentity.Digest"/> member as well — this one is
    /// private and an external consumer cannot read it.
    /// </para>
    /// </remarks>
    private static void AppendChars(IncrementalHash hash, string value)
    {
        var bytes = MemoryMarshal.AsBytes(value.AsSpan());
        AppendCount(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    /// <summary>Writes the one-byte declared/undeclared marker that precedes every field.</summary>
    private static void AppendPresence(IncrementalHash hash, bool present)
    {
        Span<byte> marker = stackalloc byte[1];
        marker[0] = present ? (byte)1 : (byte)0;
        hash.AppendData(marker);
    }

    /// <summary>Writes a four-byte big-endian count — a byte length or an element count.</summary>
    private static void AppendCount(IncrementalHash hash, int count)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, count);
        hash.AppendData(buffer);
    }
}
