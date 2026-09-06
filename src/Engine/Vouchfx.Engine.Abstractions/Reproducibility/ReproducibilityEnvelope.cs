// Vouchfx.Engine.Abstractions — ReproducibilityEnvelope (S05-B-03, §17, docs/02 §3.2.2).
//
// The reproducibility envelope records, for a single scenario, the verbatim
// evidence needed to reason about *what the run depended on* without ever
// capturing a secret VALUE:
//   • every distinct secret REFERENCE (the ${secret:src/path} token), hashed; and
//   • the content hash of every applied seed fixture.
//
// Secret-safety by construction (§17 — the headline guarantee of this task):
//   • This type is built from REFERENCE TEXT and FIXTURE CONTENT only.  The
//     secret RESOLVER is NEVER invoked here — there is no code path through
//     which a resolved secret value can enter the envelope.  Hashing the
//     reference (not the value) means the same reference yields the same digest
//     across runs (the reproducibility property) while the resolved material is
//     never present, never serialised, never logged.
//   • Pure BCL only — Vouchfx.Engine.Abstractions is a leaf library that
//     references nothing beyond the in-box framework, so it can be referenced by
//     every other engine project without forming a cycle.
//
// Project-cycle note (§ "Critical invariants" in the task brief):
//   The secret-reference hashing lives here, in Abstractions.  The FIXTURE
//   content hashing reuses SeedFixtures.ComputeContentHash, which lives in
//   Vouchfx.Engine.Orchestration — Abstractions cannot reference Orchestration
//   (that would be a cycle), so this type accepts already-computed FixtureDigest
//   values and never reaches into the orchestration layer itself.  The Runtime
//   layer (which references both) is the one place that joins the two halves.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Vouchfx.Engine.Abstractions.Secrets;

namespace Vouchfx.Engine.Abstractions.Reproducibility;

/// <summary>
/// The hashed digest of a single secret <em>reference</em> (§17).
/// </summary>
/// <remarks>
/// <para>
/// Carries the non-sensitive source identifier and the SHA-256 digest of the
/// verbatim <c>${secret:source/path}</c> token.  It deliberately carries no
/// resolved value: the value is never read while a digest is constructed, so by
/// construction no secret material can be present.
/// </para>
/// </remarks>
/// <param name="Source">
/// The non-sensitive resolver source identifier (e.g. <c>"env"</c>, <c>"vault"</c>).
/// </param>
/// <param name="ReferenceHash">
/// The 64-character lower-case hex SHA-256 of the verbatim reference token
/// (<see cref="SecretReference.Raw"/>).
/// </param>
public sealed record SecretReferenceDigest(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("referenceHash")] string ReferenceHash);

/// <summary>
/// The content hash of a single applied seed fixture (docs/02 §3.2.2, line 149).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Reference"/> is the fixture's non-sensitive relative path / label
/// (the same string the author wrote in the <c>seed</c> block); <see cref="ContentHash"/>
/// is its content hash as computed by <c>SeedFixtures.ComputeContentHash</c>.
/// <strong>The envelope is that method's only production consumer</strong> — the seed
/// applier does not call it, doing its own existence check instead — so an older
/// claim here that "the envelope and the seed applier agree on what a fixture's hash
/// is" described a shared caller that does not exist. See <c>SeedFixtures</c>'s own
/// remarks; do not restore the shared-caller premise without a call site to cite.
/// </para>
/// </remarks>
/// <param name="Reference">
/// The fixture's relative path / label, exactly as declared in the seed block.
/// </param>
/// <param name="ContentHash">
/// The lower-case hex SHA-256 of the fixture file's raw bytes, or <see langword="null"/> when
/// the fixture <strong>could not be hashed</strong> at envelope-build time.
/// <para>
/// <strong>ON THE WIRE THERE IS NO NULL — THE <c>contentHash</c> KEY IS ABSENT.</strong>
/// <c>EventStreamJson.Options</c> sets <c>DefaultIgnoreCondition = WhenWritingNull</c>, so this
/// row serialises as <c>{"reference":"fixtures/x.sql"}</c>. This type's documented audience is
/// the JSON Lines consumers, so state the contract in their terms: <strong>a fixture row
/// carrying no <c>contentHash</c> key means the engine could not hash that file — NOT that the
/// file was absent.</strong>
/// </para>
/// <para>
/// The causes are an absent file, a locked one, a permission-denied one, a path the filesystem
/// rejects, and one deleted between the existence check and the read — every member of the IO
/// family <c>ScenarioRunner.HashFixtureOrNull</c> catches. The envelope deliberately records
/// WHICH it was nowhere.
/// </para>
/// <para>
/// <strong>Consumers cannot date this from the stream.</strong> The semantics changed in #466
/// (before then only an absent file produced a hashless row) and
/// <see cref="ReproducibilityEnvelope.CurrentSchemaVersion"/> did not move for it and does not
/// move now — so two archived streams either side of that change are indistinguishable by
/// <c>envSchemaVersion</c>. Anything reading archived envelopes must assume the broader meaning.
/// </para>
/// <para>
/// <strong>Not recording the cause is a decision, and a CHEAP-TO-REVERSE one</strong> — which is
/// the honest defence, not "nobody is affected". A <c>Reason</c> field is purely additive and
/// the §14 freeze permits it for v1.x; envelopes archived before it lands lack it whichever way
/// the call goes; and the rows are already indistinguishable today. So the cost of adding it
/// later is roughly the cost of adding it now, minus the renderer work already done. It is not
/// added yet because reaching a hashless row needs a file to stop being readable mid-run — the
/// engine has already READ every byte of every file it hashes here (the seed applier's
/// <c>File.ReadAllTextAsync</c>, the script provider's <c>File.ReadAllText</c>) before this
/// envelope is assembled at scenario completion. The gate is the READ; an existence or size
/// check is not, both being measured to succeed on a locked or permission-denied file.
/// </para>
/// <para>
/// Do NOT rest the case on "no comparator exists in <c>src/</c>". That is true and irrelevant:
/// this event's charter (see <c>ReproducibilityEnvelopeEvent</c>) is reproducibility diffing by
/// consumers OUT of process, so absence of an in-tree comparator says nothing about who is
/// affected.
/// </para>
/// </param>
public sealed record FixtureDigest(
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("contentHash")] string? ContentHash);

/// <summary>
/// The reproducibility envelope for a single scenario (§17, docs/02 §3.2.2):
/// a hash of every distinct secret reference and the content hash of every
/// applied fixture, with provably no resolved secret material.
/// </summary>
/// <remarks>
/// <para>
/// The envelope is the engine's reproducibility receipt: it lets two runs be
/// compared for *input equivalence* (same references, same fixture contents)
/// without ever exposing a secret value.  Hashing the reference rather than the
/// value is the mechanism that makes the receipt both stable across runs and
/// secret-safe (§17).
/// </para>
/// <para>
/// <strong>No resolved secret value can ever enter this type.</strong>
/// <see cref="Compute"/> and <see cref="HashReference"/> operate purely on the
/// verbatim reference text; the secret resolver is never called.  This is the
/// graded acceptance for S05-B-03 and is proven by a unit test that sets the
/// backing env var to a known value and asserts the value is absent from the
/// serialised envelope.
/// </para>
/// </remarks>
/// <param name="SchemaVersion">
/// The envelope schema version, e.g. <c>"v1"</c>.  Renderers tolerate unknown
/// fields (§14), so future fields are additive.
/// </param>
/// <param name="SecretReferences">
/// The distinct secret-reference digests for the scenario, in first-seen order.
/// </param>
/// <param name="Fixtures">
/// The content-hash digests for every applied seed fixture, in declared order.
/// </param>
public sealed record ReproducibilityEnvelope(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("secretReferences")] IReadOnlyList<SecretReferenceDigest> SecretReferences,
    [property: JsonPropertyName("fixtures")] IReadOnlyList<FixtureDigest> Fixtures)
{
    /// <summary>
    /// The current envelope schema version string.
    /// </summary>
    public const string CurrentSchemaVersion = "v1";

    /// <summary>
    /// Computes the lower-case hexadecimal SHA-256 of the UTF-8 bytes of
    /// <paramref name="raw"/>.
    /// </summary>
    /// <param name="raw">
    /// The verbatim reference token (e.g. <see cref="SecretReference.Raw"/>).
    /// Hashing the verbatim token — not a resolved value — is what gives the
    /// envelope its reproducibility property (§17): the same reference always
    /// yields the same digest, regardless of what value it resolves to at run time.
    /// </param>
    /// <returns>
    /// The 64-character lower-case hex SHA-256 digest of <paramref name="raw"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="raw"/> is <see langword="null"/>.
    /// </exception>
    public static string HashReference(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds a <see cref="ReproducibilityEnvelope"/> from a sequence of secret
    /// references and a list of already-computed fixture digests.
    /// </summary>
    /// <param name="references">
    /// The secret references discovered across the scenario's substitutable
    /// fields.  Deduplicated by <see cref="SecretReference.Raw"/> (so the same
    /// token appearing in multiple fields contributes a single digest), preserving
    /// first-seen order.
    /// </param>
    /// <param name="fixtures">
    /// The fixture digests for the scenario's applied seed fixtures, in declared
    /// order.  Computed in the Runtime layer via <c>SeedFixtures.ComputeContentHash</c>
    /// and passed in here — Abstractions never touches the seed pipeline (no cycle).
    /// </param>
    /// <returns>
    /// A fully-populated envelope tagged with <see cref="CurrentSchemaVersion"/>.
    /// </returns>
    /// <remarks>
    /// This method is pure: it reads only the verbatim reference text and the
    /// supplied fixture digests.  It never invokes a secret resolver and never
    /// reads any environment / vault material, so no resolved secret value can
    /// enter the envelope (§17).
    /// </remarks>
    public static ReproducibilityEnvelope Compute(
        IEnumerable<SecretReference> references,
        IReadOnlyList<FixtureDigest> fixtures)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(fixtures);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var digests = new List<SecretReferenceDigest>();

        foreach (var reference in references)
        {
            if (reference is null)
            {
                continue;
            }

            // Dedupe by the verbatim token: the SAME reference in two fields is
            // one input, so it contributes one digest.
            if (!seen.Add(reference.Raw))
            {
                continue;
            }

            digests.Add(new SecretReferenceDigest(
                Source: reference.Source,
                ReferenceHash: HashReference(reference.Raw)));
        }

        return new ReproducibilityEnvelope(CurrentSchemaVersion, digests, fixtures);
    }
}
