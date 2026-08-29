// Issue #259 — filter spurious "if"-discriminator errors out of schema
// validation error collection (§8.2, §13.6).
//
// Shared by SchemaComposer (composed schema: root language schema + every
// registered provider's if/then discriminator clause) and YamlSchemaValidator
// (root schema only, no provider clauses) so the flat-Details-walk logic is
// written exactly once.
//
// Closed-step-surface diagnostics (unevaluatedProperties): $defs/step now
// closes with "unevaluatedProperties": false instead of its old
// "additionalProperties": true (the typo-closing change). JsonSchema.Net
// evaluates that per-offending-property against the bare boolean schema
// `false`, whose own failure carries no keyword name — the node's Errors
// dictionary is keyed by an EMPTY STRING with the generic message "All
// values fail against the false schema". FormatError recognises this shape
// (via EvaluationPath's terminal segment, since the per-node keyword is
// blank) and substitutes a message that names the offending property — and,
// when the original instance is available, the step's own `type` — so an
// author sees "Unknown property 'taget' on step type 'http.rest'" instead of
// the technically-correct-but-useless raw text.
//
// Follow-up regression (closed here): unevaluatedProperties only collects a
// subschema's annotations when that subschema's application succeeds AS A
// WHOLE. When a step is already invalid for an unrelated reason (a missing
// required field, a bad enum value, a `type` that matches no if/then clause),
// the matching provider fragment's own failure means NONE of its
// `properties` annotations propagate — every one of that step's otherwise
// legitimate fields is then reported as a spurious unevaluatedProperties
// "unknown property" error alongside the genuine defect.
// SuppressUnevaluatedPropertiesCascade drops those: same class of problem as
// IsIfDiscriminatorNoise (schema-machinery artefacts that are correct by the
// evaluator's rules and wrong for a human), same treatment.
//
// Generalisation (environment.services / environment.dependencies closure):
// the SAME blank-keyword shape recurs everywhere the schema rejects a value
// via a bare boolean `false` subschema, not only at $defs/step's
// unevaluatedProperties — namely `additionalProperties: false` (an unknown
// key under $defs/service, $defs/dependency, $defs/metadata, the document
// root, a topics[] item, or a provider's own nested block such as
// db-assert.postgres's `expect`) and a per-field `"<name>": false` planted
// inside an if/then `then` clause (the dependency-kind exclusions —
// `schemaRegistry` on a non-kafka kind, `queues`/`topics` off
// azureservicebus — and $defs/service's `image`/`project` mutual exclusion).
// Verified empirically against the real composed schema: every one of these
// shapes carries the identical blank keyword and generic "All values fail
// against the false schema" message; only EvaluationPath's terminal
// segment(s) distinguish which construct produced it. FormatError therefore
// recognises THREE blank-keyword shapes, not one — unevaluatedProperties
// (unchanged), additionalProperties (new), and the per-field `properties/
// <name>: false` shape (new) — each substituting a message that names the
// offending property and, wherever the InstanceLocation pointer or the
// instance itself lets it be resolved without guessing, the property's
// container. See FormatAdditionalPropertiesError / FormatForbiddenPropertyError.
//
// Composite-branch noise (feat/close-remaining-surfaces, combined peer-review +
// gatekeeper findings on #337): OutputFormat.List's flat reporting (see the
// class remarks below) means a non-selected `oneOf`/`anyOf` branch's own
// failing sub-evaluation is reported exactly like IsIfDiscriminatorNoise's
// non-matching `if` clause — a schema-machinery artefact, not a document
// defect — whenever the composite ITSELF is satisfied (some branch matched).
// Unlike the `if`/`then` discriminator, this cannot be recognised from the
// EvaluationPath string alone: a `oneOf`/`anyOf` composite that genuinely
// fails (no branch matches, e.g. a service with neither 'image' nor
// 'project') must keep every branch's error, since each one is then a
// genuine, independently-reportable defect (the schema's own $defs/service
// description relies on exactly this to report both missing fields). The
// fix therefore tracks, per WALK rather than per path-string, which
// (EvaluationPath-prefix, InstanceLocation) branch GROUPS were satisfied,
// and drops every non-matching sibling's error in a satisfied group.
// InstanceLocation is part of the group key, not merely EvaluationPath,
// because a schema reached via `additionalProperties` (e.g. $defs/service,
// applied uniformly to every entry in environment.services) gives every map
// entry the IDENTICAL EvaluationPath — only InstanceLocation tells two
// sibling services' `anyOf` branches apart, and grouping on path alone would
// let ONE valid service silently launder another, genuinely broken,
// service's real defect. See CompositeGroupState / IsCompositeBranchRoot /
// SuppressSatisfiedCompositeBranchNoise, and SchemaErrorCollectorTests for
// the disambiguation proof.
//
// Second-round gatekeeper findings (same branch, follow-up fix — CRITICAL-1
// and MAJOR-4, both addressed by the SAME rewrite):
//
//   CRITICAL-1 — satisfaction must be COUNTED, not merely flagged. The first
//   cut marked a group "satisfied" the instant ANY single branch validated —
//   correct for `anyOf` (>= 1 match IS satisfaction) but WRONG for `oneOf`,
//   whose own semantics require EXACTLY one match: two or more matching
//   branches make the `oneOf` itself invalid, a genuine defect, yet the old
//   code still flagged the group satisfied on the FIRST match and suppressed
//   any OTHER, genuinely-failing branch too — with nothing replacing it
//   (JsonSchema.Net attaches no message to a failing `oneOf` node itself; see
//   $defs/service's own description for the same finding, applied there to
//   justify avoiding `oneOf` altogether). Unreachable via today's two real
//   composites (both exactly 2-branch), live the moment any provider ships a
//   3-or-more-way `oneOf` (a public extension point). Fixed by COUNTING
//   valid branches per group during the walk and computing satisfaction
//   afterwards: `oneOf` needs count == 1, `anyOf` needs count >= 1. See
//   CompositeGroupState.IsSatisfied.
//
//   MAJOR-4 — suppression must be depth-independent, mirroring
//   IsIfDiscriminatorNoise's OWN deliberate depth-independence (see that
//   method's remarks). The first cut only recognised a branch's error when
//   the FAILING node's own EvaluationPath terminated EXACTLY at
//   `.../oneOf/<N>` or `.../anyOf/<N>` — a losing branch with its own nested
//   sub-schema fails at a DEEPER path instead (e.g.
//   `.../anyOf/1/properties/x`, not `.../anyOf/1` itself), which the
//   original check never matched, so it always survived as noise regardless
//   of whether the composite validated. Safe direction (extra noise, never a
//   hidden genuine defect) but broke the general "no composite-branch noise"
//   guarantee the CHANGELOG promises. Fixed by scanning the FULL
//   EvaluationPath for every `oneOf`/`anyOf`-then-index occurrence, at any
//   depth — not only the path's own final two segments — via
//   FindCompositeBranchOccurrences, exactly as IsIfDiscriminatorNoise scans
//   its own full path for `allOf`/`if` occurrences. Over-suppression is the
//   dangerous direction, so every new suppression this generalisation adds
//   is paired with a survives-test: a deep failure under a LOSING branch of
//   a SATISFIED composite is dropped, but the SAME shape under an
//   UNSATISFIED composite (the count rule says "not satisfied") survives —
//   see SchemaErrorCollectorTests' NestedAnyOf_* pair.
//
// Third-round gatekeeper findings (same branch, second follow-up — M1):
//
//   M1 — the too-many-matches synthesis above (MINOR-8) assumed every
//   matching oneOf branch contributes exactly one name to
//   ValidBranchFieldNames — true only for the two real composites that
//   motivated it (script.csharp's code/file, each a single-field
//   `required`). A matching branch with no `required` member at all (a
//   bare `type`/scalar constraint) contributes NO name, so whenever such a
//   branch is one of the matches the whole synthesis went dark — the exact
//   CRITICAL-1 symptom (a genuine "too many matches" failure with no
//   message at all), reproduced by TypeShapedOneOf_*. A branch whose
//   `required` lists MORE than one name (a paired requirement, e.g.
//   `required: ["a","b"]`) contributes several names for ONE match,
//   equally breaking the implicit one-name-per-branch correspondence the
//   old, unconditional call assumed — producing either a false single-name
//   accusation when a type-only sibling's silence coincidentally leaves
//   exactly one name behind (MixedRequiredAndTypeOnlyOneOf_*) or advice to
//   remove individual fields from what is actually a two-branch,
//   paired-field choice (MultiRequiredPairOneOf_*). Fixed (at the time) by
//   gating the named synthesis on `ValidBranchFieldNames.Count ==
//   ValidBranchCount` and falling back to an honest, field-name-free count
//   message (FormatUnnamedTooManyOneOfMatchesError) otherwise. This guard
//   is unconditional for every oneOf in the language, Core or Community:
//   oneOf/anyOf are public extension points a provider fragment can shape
//   however it needs, so the generic path must be correct on its own
//   rather than merely correct for the SDK surfaces reviewed so far — the
//   same reasoning CRITICAL-1's own count fix already rests on. See
//   SchemaErrorCollectorTests' TypeShapedOneOf_*,
//   MixedRequiredAndTypeOnlyOneOf_*, and MultiRequiredPairOneOf_* for the
//   three measured failure modes this closes.
//
//   M1-r (fourth-round re-review, same finding) — count-equality ALONE is
//   necessary but was claimed above as sufficient ("if and only if"),
//   which is FALSE: measured counter-example `oneOf
//   [{required:["a"]},{required:["b","c"]},{type:"object"}]` against
//   `{a,b,c}` — three branches all match (required:["a"]; required:["b","c"]
//   with both present; the bare type:object, which matches any object
//   unconditionally), contributing 1 + 2 + 0 = 3 names for 3 matches. The
//   flat count coincidentally equals the branch count, so the old guard
//   passed and fired "Exactly one of 'a', 'b', 'c' may be set" — advice
//   that can never be satisfied, since the third branch matches every
//   object regardless of a/b/c. Fixed by judging each branch's OWN
//   contribution in isolation during the walk, not merely the running
//   total afterwards: CompositeGroupState.HasUnattributableBranch is set
//   the instant any single matching branch contributes anything other than
//   exactly one name, and the synthesis gates on `!HasUnattributableBranch
//   && Count == ValidBranchCount` — NOW a true "if and only if" (see
//   FormatTooManyOneOfMatchesError's own remarks for the two-directions
//   argument). See SchemaErrorCollectorTests'
//   ThreeMixedShapesOneOf_CoincidentalCountMatch_* for the counter-example
//   pinned as a regression test.
//
// [enum] enrichment (same change): a wrong-case or misspelled enum value
// (dependency `type`, `imagePullPolicy`, `verifyMode`, cache-assert.redis's
// `operation`) used to surface only the generic, un-actionable "[enum] Value
// should match one of the values specified by the enum" — the CHANGELOG's
// "the fix is in the message" promise (case-sensitivity, #335) was true only
// of EnvironmentMapper's OWN runtime message, never reachable because schema
// validation always runs first. FormatEnumError resolves the LIVE allowed-
// value list from the composed schema JSON via the failing node's own
// SchemaLocation (a JSON Pointer into the schema text, stable across $ref/
// $defs/additionalProperties indirection — verified empirically) rather than
// hardcoding any vocabulary, so the message stays correct if a provider ever
// adds/removes an enum member. Never fabricates: an offending value or an
// enum array this cannot resolve degrades to a generic (but still
// `[enum]`-tagged) message rather than guessing.
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Json.Schema;

namespace Vouchfx.Engine.Compilation.Schema;

/// <summary>
/// Shared error-collection logic for <see cref="EvaluationResults"/> trees
/// produced by <see cref="OutputFormat.List"/> evaluation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Background (§13.6).</b> <see cref="SchemaComposer"/> injects one
/// unconditional <c>if</c>/<c>then</c> clause per registered provider into
/// <c>$defs/step/allOf</c>, keyed on the step's <c>type</c> const. With
/// <see cref="OutputFormat.List"/>, JsonSchema.Net reports every node it
/// evaluates as a flat sibling under the top-level result — including each
/// non-matching clause's own <c>if</c> sub-evaluation, which is legitimately
/// invalid (the <c>type</c> const does not equal that clause's key) even
/// though this has no bearing on the compound <c>if</c>/<c>then</c> clause's
/// overall validity (a clause whose <c>if</c> does not match is vacuously
/// satisfied, per the JSON Schema 2020-12 <c>if</c>/<c>then</c>/<c>else</c>
/// semantics). For a single invalid step, this surfaces one spurious
/// <c>"Expected \"&lt;other-provider-type&gt;\""</c> entry per non-matching
/// provider — up to 24 at the full 25-provider Core catalogue — none of
/// which describe an actual problem with the document.
/// </para>
/// <para>
/// <see cref="IsIfDiscriminatorNoise"/> recognises and drops these: a node
/// contributes noise if and only if its <see cref="EvaluationResults.EvaluationPath"/>
/// contains an <c>allOf/&lt;N&gt;/if</c> segment triple (verified empirically —
/// see the class remarks on the <c>SchemaErrorCollectionAtScaleTests</c> test
/// fixture). A clause's genuine <c>then</c>-branch failure (reached only once
/// its <c>if</c> genuinely matched) always has an evaluation path of the form
/// <c>allOf/&lt;N&gt;/then/...</c> and is therefore never matched by this check.
/// </para>
/// </remarks>
internal static class SchemaErrorCollector
{
    /// <summary>
    /// Walks the flat <c>Details</c> list produced by the <c>List</c> output
    /// format and collects every leaf node that is invalid, carries at least
    /// one keyword error, and is not "if"-discriminator noise (see remarks).
    /// </summary>
    /// <param name="results">The top-level evaluation results.</param>
    /// <param name="instance">
    /// The original document instance the schema was evaluated against, when
    /// available. Never consulted for filtering or validity — only to enrich
    /// an <c>unevaluatedProperties</c> violation's message with the offending
    /// step's own <c>type</c> value, and an <c>enum</c> violation's message
    /// with the offending value itself (see the class remarks). Passing
    /// <see langword="null"/> degrades gracefully: the property name is still
    /// reported, just without the enrichment.
    /// </param>
    /// <param name="schema">
    /// The composed schema being evaluated against, as a parsed
    /// <see cref="JsonElement"/> of its own JSON text, when available. Used
    /// only to resolve an <c>enum</c> violation's live list of accepted
    /// values via the failing node's own <c>SchemaLocation</c> (see
    /// <see cref="FormatEnumError"/>). Passing <see langword="null"/>
    /// degrades gracefully: the offending value is still named, without the
    /// accepted-values list.
    /// </param>
    /// <returns>
    /// A non-empty list of <see cref="SchemaValidationError"/> entries. When
    /// every genuine error was filtered out as noise but the schema still
    /// reports failure overall (e.g. a Flag-level result or an exotic nested
    /// <c>$ref</c> chain with no detailed errors at all), a single synthetic
    /// error at the root location is returned so the caller always receives
    /// at least one actionable message.
    /// </returns>
    internal static List<SchemaValidationError> CollectErrors(
        EvaluationResults results, JsonElement? instance = null, JsonElement? schema = null)
    {
        var collected = new List<CollectedError>();
        var groupStates = new Dictionary<string, CompositeGroupState>(StringComparer.Ordinal);
        CollectErrorsRecursive(results, collected, groupStates, instance, schema);

        // Synthesise the genuine defect a oneOf's "too many matches" failure
        // represents (MINOR-8): JsonSchema.Net attaches NO message of its
        // own to this case (verified empirically — every matching branch is
        // individually valid, so none carries an error), and the CRITICAL-1
        // count fix correctly leaves the group unsatisfied, so without this
        // the ONLY symptom would be every matched branch's own fields wrongly
        // reported as "unknown" by the unevaluatedProperties cascade below.
        // Inserted at the FRONT of 'collected' (nit n1 — this names the
        // actual defect directly and should be the first thing an author
        // reads, not buried after whatever the recursive walk happened to
        // visit first) but still BEFORE both suppression passes run so this
        // genuine "other error" correctly participates in — and is itself
        // correctly exempt from — both: SuppressSatisfiedCompositeBranchNoise
        // never touches it (its EvaluationPath is the GROUP's own prefix,
        // e.g. '.../oneOf', with no trailing branch index, so
        // FindCompositeBranchOccurrences finds nothing to match against —
        // safe by construction, not merely because the group is unsatisfied
        // anyway), while SuppressUnevaluatedPropertiesCascade correctly DOES
        // drop the two misleading unevaluatedProperties entries once this
        // genuine error registers the step as having "another error".
        //
        // M1/M1-r guard: only NAME the matched branches' fields when EVERY
        // matching branch individually contributed exactly one name
        // (!HasUnattributableBranch) AND the running total still equals the
        // branch count (ValidBranchFieldNames.Count == ValidBranchCount) —
        // see the class remarks' "Third-round gatekeeper findings" and
        // HasUnattributableBranch's own remarks for why count-equality
        // alone is necessary but not sufficient. Any other shape falls back
        // to FormatUnnamedTooManyOneOfMatchesError's honest count-only
        // message: never silent, never a fabricated or unachievable claim.
        List<CollectedError>? synthesisedOneOfErrors = null;
        foreach (var state in groupStates.Values)
        {
            if (!state.HasTooManyOneOfMatches)
            {
                continue;
            }

            var fieldNames = state.ValidBranchFieldNames;
            var message = !state.HasUnattributableBranch &&
                          fieldNames is { Count: > 0 } && fieldNames.Count == state.ValidBranchCount
                ? FormatTooManyOneOfMatchesError(fieldNames)
                : FormatUnnamedTooManyOneOfMatchesError(state.ValidBranchCount);

            synthesisedOneOfErrors ??= new List<CollectedError>();
            synthesisedOneOfErrors.Add(new CollectedError(
                state.InstanceLocation, state.Prefix, message, IsUnevaluatedProperties: false,
                Keyword: "oneOf"));
        }

        if (synthesisedOneOfErrors is not null)
        {
            collected.InsertRange(0, synthesisedOneOfErrors);
        }

        // Composite-branch suppression MUST run before the cascade below: a
        // step whose only "other error" was itself composite-branch noise
        // (e.g. script.csharp's oneOf, see the class remarks) must not have
        // its genuine unevaluatedProperties entry hidden by a phantom sibling
        // that is about to be dropped anyway.
        var afterCompositeSuppression = SuppressSatisfiedCompositeBranchNoise(collected, groupStates);
        var afterEnumConstDedup = SuppressRedundantConstWhenEnumPresent(afterCompositeSuppression);
        var afterForbiddenContainerSubsumption = SuppressErrorsInsideForbiddenContainer(afterEnumConstDedup);

        // QUESTION-1 (peer review, fix round 4) — why SuppressErrorsInsideForbiddenContainer
        // running BEFORE the cascade cannot change the cascade's decision. The cascade drops a
        // step's unevaluatedProperties entries only when that step still carries some OTHER
        // error, so a pass that removes errors ahead of it could in principle flip that. It
        // cannot here, and the reason is STRUCTURAL rather than a happy accident of today's
        // schema:
        //
        //   • The two scopes DO overlap already, so "they cannot interact" is not the argument.
        //     Measured: $defs/captureEntry's own 'jsonpath' present => '"xpath": false' clause
        //     plants a forbidden container at /steps/<N>/capture/<var>/xpath, and provider
        //     fragments plant more (storage-assert.s3's exists:false content fields,
        //     db-assert.dynamodb's expect.item) — all inside the cascade's /steps/<N> scope.
        //   • SuppressErrorsInsideForbiddenContainer drops an error E only when some
        //     forbidden-shape error F is E's own location or a strict ancestor of it, and F
        //     itself is short-circuited into the survivor list before the containment test — so
        //     F always reaches the cascade.
        //   • F is never an unevaluatedProperties error: IsForbiddenPropertyShape requires the
        //     evaluation path's penultimate segment to be 'properties', IsUnevaluatedPropertiesShape
        //     requires its final segment to be 'unevaluatedProperties'. F therefore always counts
        //     as an "other error" for the cascade.
        //   • TryGetStepScope reads only the first two pointer segments, and F is a
        //     segment-boundary prefix of E. For F to fall OUTSIDE E's step scope it would have to
        //     sit at /steps/<N>, /steps, or the root — but a forbidden-property shape's location
        //     is always an object property's own pointer, and /steps/<N> is an array element
        //     (reached via 'items', never 'properties'), so no 'properties/<name>: false' clause
        //     can produce one.
        //
        // Net: every error this pass removes from a step leaves behind a surviving,
        // non-unevaluatedProperties error in that SAME step scope, so membership of the cascade's
        // stepsWithOtherErrors set is preserved exactly. The invariant that carries the argument
        // (F's short-circuit) lives in another method, so it is pinned by test rather than left to
        // this comment — see
        // SchemaErrorCollectorTests.StepScopedForbiddenContainer_StillSuppressesTheCascade.
        var survivors = SuppressUnevaluatedPropertiesCascade(afterForbiddenContainerSubsumption);

        var errors = new List<SchemaValidationError>(survivors.Count);
        foreach (var error in survivors)
        {
            errors.Add(new SchemaValidationError(error.InstanceLocation, error.Message));
        }

        if (errors.Count == 0)
        {
            errors.Add(new SchemaValidationError(
                results.InstanceLocation.ToString(),
                "Schema validation failed with no detailed error messages."));
        }

        return errors;
    }

    /// <summary>
    /// One collected, already-formatted error, plus enough of its own shape
    /// for the two suppression passes to judge it without re-parsing the
    /// message text it is about to hand back to the caller:
    /// <c>IsUnevaluatedProperties</c> (see <see cref="IsUnevaluatedPropertiesShape"/>)
    /// for <see cref="SuppressUnevaluatedPropertiesCascade"/>, and the raw
    /// <c>EvaluationPath</c> (re-scanned lazily by
    /// <see cref="SuppressSatisfiedCompositeBranchNoise"/> — see
    /// <see cref="FindCompositeBranchOccurrences"/> — rather than
    /// pre-computed here, since it is only ever needed when at least one
    /// composite group turned out satisfied).
    /// </summary>
    /// <param name="InstanceLocation">The failing node's own instance-location pointer.</param>
    /// <param name="EvaluationPath">
    /// The failing node's own evaluation-path pointer, re-scanned lazily by
    /// <see cref="SuppressSatisfiedCompositeBranchNoise"/> (see
    /// <see cref="FindCompositeBranchOccurrences"/>) rather than pre-computed here, since it
    /// is only ever needed when at least one composite group turned out satisfied.
    /// </param>
    /// <param name="Message">The already-formatted, author-facing message text.</param>
    /// <param name="IsUnevaluatedProperties">
    /// See <see cref="IsUnevaluatedPropertiesShape"/> — consulted by
    /// <see cref="SuppressUnevaluatedPropertiesCascade"/>.
    /// </param>
    /// <param name="Keyword">
    /// The raw JSON Schema keyword this error was reported against (e.g. <c>"enum"</c>,
    /// <c>"const"</c>, <c>"required"</c>) — never re-derived from <c>Message</c>'s
    /// <c>[keyword]</c> display tag, which exists for the AUTHOR to read, not for this
    /// class to re-parse. Consulted by <see cref="SuppressRedundantConstWhenEnumPresent"/>
    /// (m8 fix, fix round 2).
    /// </param>
    private readonly record struct CollectedError(
        string InstanceLocation, string EvaluationPath, string Message, bool IsUnevaluatedProperties,
        string Keyword);

    /// <summary>
    /// Per-(EvaluationPath-prefix, InstanceLocation) tally for one
    /// <c>oneOf</c>/<c>anyOf</c> application, built up during the walk and
    /// consulted afterwards by <see cref="SuppressSatisfiedCompositeBranchNoise"/>
    /// and (for a <c>oneOf</c> specifically) by the too-many-matches
    /// synthesis in <see cref="CollectErrors"/> (MINOR-8).
    /// </summary>
    /// <remarks>
    /// <see cref="IsSatisfied"/> encodes the CRITICAL-1 fix: <c>oneOf</c>
    /// requires EXACTLY one matching branch (two or more is itself a genuine
    /// violation of "exactly one" — nothing here is noise), while
    /// <c>anyOf</c> only ever requires AT LEAST one. A mutable class, not a
    /// struct: the walk increments <see cref="ValidBranchCount"/> in place
    /// via repeated dictionary lookups, which a value type would make
    /// awkward (get, mutate, re-store) for no benefit — these instances never
    /// outlive one <see cref="CollectErrors"/> call.
    /// </remarks>
    private sealed class CompositeGroupState
    {
        public required string Prefix { get; init; }
        public required string InstanceLocation { get; init; }
        public required bool IsOneOf { get; init; }
        public int ValidBranchCount { get; set; }

        /// <summary>
        /// The <c>required</c> field name(s) of every branch that validated,
        /// in encounter order — <c>oneOf</c> groups only (see
        /// <see cref="CollectErrorsRecursive"/>'s populating call), read
        /// directly from the schema so <see cref="FormatTooManyOneOfMatchesError"/>
        /// never hardcodes a provider's own field names. Null until the first
        /// valid oneOf branch is seen; used only when
        /// <see cref="ValidBranchCount"/> ends up >= 2 (JsonSchema.Net
        /// attaches no message of its own to that "too many matches" case —
        /// see the class remarks — so this is the only source for one). Not,
        /// by itself, a safe 1:1 map of names to matching branches even when
        /// its own <c>Count</c> happens to equal <see cref="ValidBranchCount"/>
        /// — see <see cref="HasUnattributableBranch"/> for why count equality
        /// alone (M1) is necessary but not sufficient (M1-r), and
        /// <see cref="FormatTooManyOneOfMatchesError"/>'s remarks for the
        /// caller's full guard.
        /// </summary>
        public List<string>? ValidBranchFieldNames { get; set; }

        /// <summary>
        /// Set once any matching <c>oneOf</c> branch's own
        /// <c>TryReadRequiredFieldNames</c> call yields anything other than
        /// EXACTLY one name — zero (no <c>required</c> member at all, e.g. a
        /// bare <c>type</c> constraint) or two-or-more (a paired
        /// <c>required</c>, one match contributing several names at once)
        /// (M1-r, third-round gatekeeper re-review).
        /// </summary>
        /// <remarks>
        /// M1's own count-equality check (<c>ValidBranchFieldNames.Count ==
        /// ValidBranchCount</c>) is necessary but NOT sufficient on its own:
        /// a group with one single-name branch plus one two-name branch plus
        /// one zero-name branch can coincidentally sum to the same count as
        /// the branch total (1 + 2 + 0 = 3 matches, 3 names) while the
        /// per-branch correspondence the named message relies on is still
        /// broken — see the class remarks' M1-r addendum for the measured
        /// counter-example. This flag is set the instant ANY branch breaks
        /// the correspondence, independent of what the running total
        /// happens to add up to afterwards, closing exactly that gap.
        /// </remarks>
        public bool HasUnattributableBranch { get; set; }

        public bool IsSatisfied => IsOneOf ? ValidBranchCount == 1 : ValidBranchCount >= 1;

        public bool HasTooManyOneOfMatches => IsOneOf && ValidBranchCount >= 2;
    }

    private static void CollectErrorsRecursive(
        EvaluationResults node,
        List<CollectedError> sink,
        Dictionary<string, CompositeGroupState> groupStates,
        JsonElement? instance,
        JsonElement? schema)
    {
        var evaluationPath = node.EvaluationPath.ToString();
        var location = node.InstanceLocation.ToString();

        // Tally composite (oneOf/anyOf) branch validity regardless of THIS
        // node's own validity — a branch that satisfies its composite is, by
        // definition, itself VALID, so this must run before the early-return
        // below or the walk would never see it (see the class remarks).
        // Scoped to a BRANCH ROOT only (IsCompositeBranchRoot: the node's OWN
        // EvaluationPath terminates exactly at oneOf/<N> or anyOf/<N>) — this
        // tally needs the branch's own AGGREGATE validity (does its entire
        // nested sub-schema pass?), never a deeper descendant's.
        if (node.IsValid && IsCompositeBranchRoot(evaluationPath, out var branchPrefix, out var branchIsOneOf))
        {
            var key = branchPrefix + "|" + location;
            if (!groupStates.TryGetValue(key, out var state))
            {
                state = new CompositeGroupState { Prefix = branchPrefix, InstanceLocation = location, IsOneOf = branchIsOneOf };
                groupStates[key] = state;
            }

            state.ValidBranchCount++;

            // Only ever consulted for a oneOf that ends up with 2+ matches
            // (MINOR-8) — reading it for anyOf groups too would be wasted
            // work, since HasTooManyOneOfMatches is oneOf-only by definition.
            if (branchIsOneOf && schema is { } schemaRootForFieldNames)
            {
                var fieldNames = TryReadRequiredFieldNames(schemaRootForFieldNames, node.SchemaLocation);

                // M1-r: judge THIS branch's own contribution in isolation —
                // exactly one name is attributable to exactly one branch;
                // zero or two-or-more breaks that correspondence for the
                // WHOLE group, permanently, regardless of what the running
                // total happens to add up to once every branch has been
                // seen (see HasUnattributableBranch's remarks for the
                // measured counter-example this closes).
                if (fieldNames is { Count: 1 })
                {
                    state.ValidBranchFieldNames ??= new List<string>();
                    state.ValidBranchFieldNames.AddRange(fieldNames);
                }
                else
                {
                    state.HasUnattributableBranch = true;
                }
            }
        }

        if (node.IsValid)
            return;

        // Skip "if"-discriminator noise (issue #259): a non-matching provider
        // clause's own 'if' sub-evaluation is legitimately invalid but describes
        // no real document problem — see the class remarks.
        if (node.Errors is { Count: > 0 } && !IsIfDiscriminatorNoise(evaluationPath))
        {
            var schemaLocation = node.SchemaLocation;

            foreach (var (keyword, message) in node.Errors)
            {
                sink.Add(new CollectedError(
                    location,
                    evaluationPath,
                    FormatError(keyword, message, evaluationPath, location, instance, schema, schemaLocation),
                    IsUnevaluatedPropertiesShape(keyword, evaluationPath),
                    Keyword: keyword));
            }
        }

        if (node.Details is { Count: > 0 })
        {
            foreach (var child in node.Details)
            {
                CollectErrorsRecursive(child, sink, groupStates, instance, schema);
            }
        }
    }

    /// <summary>
    /// Drops a composite-branch error whose EvaluationPath passes through a
    /// SATISFIED <c>oneOf</c>/<c>anyOf</c> group's branch, at ANY depth (see
    /// <see cref="FindCompositeBranchOccurrences"/> and the class remarks —
    /// MAJOR-4's depth-independence fix). When a group has NO satisfied
    /// branch (the composite genuinely failed — e.g. a service with neither
    /// 'image' nor 'project' — or a <c>oneOf</c> with two-or-more matches,
    /// CRITICAL-1's fix), every branch's error is a genuine,
    /// independently-reportable defect and survives untouched.
    /// </summary>
    private static List<CollectedError> SuppressSatisfiedCompositeBranchNoise(
        List<CollectedError> errors, Dictionary<string, CompositeGroupState> groupStates)
    {
        List<CompositeGroupState>? satisfiedGroups = null;
        foreach (var state in groupStates.Values)
        {
            if (state.IsSatisfied)
            {
                satisfiedGroups ??= new List<CompositeGroupState>();
                satisfiedGroups.Add(state);
            }
        }

        // Fast path: nothing in this document satisfied any composite at
        // all, so there is nothing to suppress — mirrors
        // SuppressUnevaluatedPropertiesCascade's own fast path.
        if (satisfiedGroups is null)
            return errors;

        var survivors = new List<CollectedError>(errors.Count);
        foreach (var error in errors)
        {
            if (IsUnderAnySatisfiedGroup(error.EvaluationPath, error.InstanceLocation, satisfiedGroups))
            {
                continue;
            }

            survivors.Add(error);
        }

        return survivors;
    }

    /// <summary>
    /// True when <paramref name="evaluationPath"/> passes through some
    /// SATISFIED group's branch and <paramref name="instanceLocation"/> is
    /// that same group's own instance location or a descendant of it — i.e.
    /// this error belongs to a branch that lost only because a SIBLING
    /// branch of the SAME satisfied composite won.
    /// </summary>
    private static bool IsUnderAnySatisfiedGroup(
        string evaluationPath, string instanceLocation, List<CompositeGroupState> satisfiedGroups)
    {
        foreach (var (prefix, _) in FindCompositeBranchOccurrences(evaluationPath))
        {
            foreach (var group in satisfiedGroups)
            {
                if (string.Equals(prefix, group.Prefix, StringComparison.Ordinal) &&
                    IsPathOrDescendant(group.InstanceLocation, instanceLocation))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Drops a <c>const</c> error whenever an <c>enum</c> error is ALSO reported at the
    /// SAME <see cref="CollectedError.InstanceLocation"/> (m8 fix, fix round 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The composed schema's ports-only-service conditional (<c>$defs/service</c>'s own
    /// <c>if</c>/<c>then</c>: <c>ports</c> present, no sibling <c>httpPort</c> ⇒
    /// <c>healthCheck.type</c> is conditionally pinned to <c>const: "tcp"</c>) sits ALONGSIDE
    /// <c>healthCheck.type</c>'s own unconditional <c>enum: ["tcp","http"]</c>. For a
    /// wrong-CASE value (e.g. <c>type: TCP</c>), BOTH fail at the exact same
    /// <c>InstanceLocation</c> — the enum member because <c>"TCP"</c> is not
    /// case-identical to either accepted spelling, the conditional const because
    /// <c>"TCP"</c> is not identical to the one value the ports-only shape permits — even
    /// though they describe the SAME single typo. The enum message already names the exact
    /// fix ("write 'tcp'"); the const message beside it is pure noise for THIS document
    /// (measured: <c>service-healthcheck-type-wrong-case.e2e.yaml</c> emitted both before
    /// this fix). A GENUINELY ports-only-conditional rejection with no case typo (e.g.
    /// <c>type: http</c>, a valid enum member that still fails the narrower conditional) has
    /// no enum sibling at all and is untouched by this pass — see
    /// <see cref="FormatConstError"/>'s own <c>healthCheck.type</c> branch for that surviving
    /// case's improved message.
    /// </para>
    /// <para>
    /// Scoped narrowly to (<c>const</c> dropped, <c>enum</c> kept) specifically, never the
    /// reverse and never any other keyword pair: this is the ONE shape in the composed
    /// schema today where an unconditional <c>enum</c> and a conditional <c>const</c> can
    /// both fail at identical <see cref="CollectedError.InstanceLocation"/> — a future
    /// schema addition producing a different keyword collision is unaffected by this pass
    /// (falls through both untouched, exactly like every other keyword pair today).
    /// </para>
    /// </remarks>
    private static List<CollectedError> SuppressRedundantConstWhenEnumPresent(List<CollectedError> errors)
    {
        HashSet<string>? locationsWithEnumError = null;
        foreach (var error in errors)
        {
            if (string.Equals(error.Keyword, "enum", StringComparison.Ordinal))
            {
                locationsWithEnumError ??= new HashSet<string>(StringComparer.Ordinal);
                locationsWithEnumError.Add(error.InstanceLocation);
            }
        }

        // Fast path: no enum error anywhere in this document, so there is nothing a const
        // error could be redundant with — mirrors the other suppression passes' own
        // early-out shape.
        if (locationsWithEnumError is null)
            return errors;

        var survivors = new List<CollectedError>(errors.Count);
        foreach (var error in errors)
        {
            if (string.Equals(error.Keyword, "const", StringComparison.Ordinal) &&
                locationsWithEnumError.Contains(error.InstanceLocation))
            {
                continue;
            }

            survivors.Add(error);
        }

        return survivors;
    }

    /// <summary>
    /// Drops every error located strictly INSIDE an object that a boolean <c>false</c> subschema
    /// has already rejected outright (H-A, peer review — held item, fix round 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>properties/&lt;name&gt;: false</c> idiom this schema uses for every conditional
    /// exclusion (<c>$defs/dependency</c>'s per-kind <c>schemaRegistry</c>/<c>queues</c>/<c>topics</c>
    /// clauses and, since M1, its <c>security</c> clause; <c>$defs/security</c>'s own
    /// <c>clientCert</c>/<c>clientKey</c> pair; <c>$defs/serviceHealthCheck</c>'s
    /// <c>port</c>/<c>path</c> pair; storage-assert.s3's and db-assert.dynamodb's
    /// <c>exists: false</c> shapes) rejects the property REGARDLESS of its contents. Every other
    /// keyword still evaluates that same subtree independently, so a forbidden OBJECT-valued
    /// property reports its own rejection PLUS one error per defect inside it — a
    /// <c>required</c> field it omits, a <c>pattern</c> its discriminator fails, an unrecognised
    /// key its own closure rejects. All of those are moot: the container may not be there at
    /// all, so what is wrong INSIDE it cannot be the author's next action.
    /// </para>
    /// <para>
    /// Measured before this pass existed: <c>profile: TLS</c> on a redis dependency emitted TWO
    /// errors (the <c>[pattern]</c> miss and the per-kind narrowing's own finding) where the same
    /// value on a SERVICE — which carries no per-kind narrowing — emitted ONE. Three review
    /// rounds passed over the asymmetry because both the corpus fixture and the unit assertion
    /// for a wrong-cased profile exercised it only on a service. The gatekeeper's original remedy
    /// (mirror <see cref="SuppressRedundantConstWhenEnumPresent"/>, keyed on <c>pattern</c>
    /// instead of <c>enum</c>) was written against the OLD schema, where the second error was a
    /// sibling <c>const</c> at the identical location; M1 replaced that <c>const</c> with a
    /// boolean <c>false</c> one level UP, so a same-location keyword-pair rule no longer matches
    /// the shape at all. Subsumption by container is the rule that does — and it is strictly more
    /// general, covering the <c>required</c>, <c>unevaluatedProperties</c> and
    /// <c>minLength</c> children the keyword-pair rule never reached.
    /// </para>
    /// <para>
    /// Scoped by CONTAINMENT, not by container name: an error survives unless some OTHER error in
    /// the same document rejected its own location, or a strict ancestor of it, via this exact
    /// shape. Containment is <see cref="IsPathOrDescendant"/> — RFC 6901 SEGMENT boundaries, not a
    /// raw character prefix; a sibling property whose name merely begins with the forbidden
    /// container's (<c>securityExtra</c> beside a forbidden <c>security</c>) is NOT inside it and
    /// must survive. <see cref="IsForbiddenPropertyShape"/> is re-used verbatim as the predicate, so this
    /// pass recognises exactly the shapes <see cref="FormatForbiddenPropertyError"/> renders and
    /// nothing else. A scalar-valued forbidden property has no CHILDREN to subsume, but it does
    /// have same-location SIBLINGS: any keyword of the property's OWN base schema that the value
    /// also fails reports at the identical pointer as this rule's boolean-<c>false</c> rejection,
    /// so this pass is not a no-op on a scalar. An earlier version of this paragraph called that
    /// shape a no-op, which was wrong outright rather than merely out of date — it has been
    /// reachable for as long as the <c>tls</c> branch has forbidden a scalar with any base-schema
    /// constraint. MEASURED against the composed schema, each of these documents makes the
    /// evaluator report TWO errors at one pointer, which the same-location clause below reduces to
    /// the ONE an author must act on (delete the field; what its value would have had to look like
    /// is moot): <c>profile: tls</c> with <c>clientCert: ""</c> (<c>minLength</c> +
    /// boolean-<c>false</c>), with <c>clientCert: 123</c> (<c>type</c> + boolean-<c>false</c>), and
    /// with a literal <c>clientKeyPassword: "hunter2"</c> (<c>pattern</c> + boolean-<c>false</c>).
    /// What <c>clientKeyPassword</c> is genuinely first at is narrower: it is the first
    /// forbidden-under-<c>tls</c> scalar carrying a <c>pattern</c> (measured against the composed
    /// schema, <c>clientCert</c> and <c>clientKey</c> carry <c>minLength</c> and <c>type</c> only),
    /// and the first for which the test corpus exercises the shape at all — the <c>clientCert</c>
    /// fixtures elsewhere declare a valid path, which passes <c>minLength</c> and leaves the
    /// boolean-<c>false</c> rejection alone at the pointer. Pinned by
    /// <c>SchemaSecuritySurfaceClosureTests.SingleEnvironmentSecurityDefect_YieldsExactlyOneError</c>,
    /// which carries the <c>clientKeyPassword</c> document as a case.
    /// </para>
    /// <para>
    /// SAME-LOCATION errors are subsumed too, not only strictly-nested ones, because
    /// <c>required</c> always reports against the CONTAINER missing the property rather than
    /// against the property itself: a forbidden <c>security</c> block that also omits its required
    /// <c>endpoint</c> produces both errors at the identical pointer, and the <c>required</c> one
    /// is exactly as moot as a nested one. A forbidden-shape error is never dropped by this rule
    /// (it would otherwise subsume itself), so two forbidden properties rejected at the same
    /// location both survive.
    /// </para>
    /// <para>
    /// MINOR-2 (peer review, fix round 4). The paragraph above used to end "…and two NESTED
    /// forbidden containers collapse to the outermost — the one an author must act on". The code
    /// does the opposite, and always has: the short-circuit is unconditional, so a forbidden-shape
    /// error nested INSIDE another forbidden container is never subsumed by it and both are
    /// reported. That is a known limitation of this rule rather than a behaviour it implements —
    /// only the outermost is actionable, and the inner one is redundant beside it — and the case
    /// is REACHABLE today, contrary to the reading that the <c>security</c> block is the only
    /// object-valued forbidden property. Measured: a <c>project</c>-form service forbids
    /// <c>healthCheck</c> outright, while <c>$defs/serviceHealthCheck</c>'s own <c>type: http</c>
    /// clause forbids <c>port</c> INSIDE it, so
    /// <c>{ project: …, healthCheck: { type: http, port: 8080 } }</c> reports BOTH
    /// <c>/environment/services/app/healthCheck</c> and
    /// <c>/environment/services/app/healthCheck/port</c> — pinned by
    /// <c>EnvironmentSchemaTests.Service_NestedForbiddenContainers_BothSurvive</c>. Collapsing
    /// them would mean exempting a STRICTLY-nested forbidden error from the short-circuit, a
    /// behaviour change this round deliberately does not make.
    /// </para>
    /// </remarks>
    private static List<CollectedError> SuppressErrorsInsideForbiddenContainer(List<CollectedError> errors)
    {
        HashSet<string>? forbiddenLocations = null;
        foreach (var error in errors)
        {
            if (IsForbiddenPropertyShape(error.Keyword, error.EvaluationPath))
            {
                forbiddenLocations ??= new HashSet<string>(StringComparer.Ordinal);
                forbiddenLocations.Add(error.InstanceLocation);
            }
        }

        // Fast path: nothing in this document was rejected by a boolean 'false' subschema, so
        // there is no container for anything to be inside of — mirrors the other suppression
        // passes' own early-out shape.
        if (forbiddenLocations is null)
            return errors;

        var survivors = new List<CollectedError>(errors.Count);
        foreach (var error in errors)
        {
            // A forbidden-shape error is the SUBSUMING one, never the subsumed one.
            if (IsForbiddenPropertyShape(error.Keyword, error.EvaluationPath))
            {
                survivors.Add(error);
                continue;
            }

            var subsumed = false;
            foreach (var forbiddenLocation in forbiddenLocations)
            {
                // MAJOR-1 (peer review, fix round 3): the shared helper, not an inline
                // equality-or-StartsWith pair re-derived here. The two were equivalent, but the
                // helper is where the segment-boundary trap is DOCUMENTED ('/steps/10' is not a
                // descendant of '/steps/1'), and a rule whose rationale lives on a method it does
                // not call is one edit away from losing it. It also drops a string allocation per
                // (error × container) pair. Pinned in the survives direction by
                // EnvironmentSchemaTests.Dependency_Security_ForbiddenContainer_DoesNotMuteSiblingsOrPrefixSharingProperties,
                // which is the ONLY test in the repository that fails if this containment check
                // degrades to a raw prefix test (measured: 1 failure in 723).
                if (IsPathOrDescendant(forbiddenLocation, error.InstanceLocation))
                {
                    subsumed = true;
                    break;
                }
            }

            if (!subsumed)
            {
                survivors.Add(error);
            }
        }

        return survivors;
    }

    /// <summary>
    /// True when <paramref name="evaluationPath"/>'s FINAL two segments are a
    /// <c>oneOf</c>/<c>anyOf</c> keyword followed by a bare array index (e.g.
    /// <c>.../anyOf/1</c>) — i.e. this node IS one specific branch's own
    /// aggregate result, not merely something nested inside one. Used only
    /// for the walk-time valid-branch TALLY (<see cref="CollectErrorsRecursive"/>),
    /// which needs a branch's own aggregate <c>IsValid</c>; the (separate,
    /// depth-independent) suppression check is
    /// <see cref="FindCompositeBranchOccurrences"/>, deliberately not this
    /// method — see the class remarks on why the two must differ (MAJOR-4).
    /// Deliberately excludes <c>allOf</c>: an <c>allOf</c> branch is never
    /// exploration noise (EVERY branch must pass, so a failing one is always
    /// diagnostic) — that is exactly why the existing <c>if</c>/<c>then</c>
    /// discriminator pattern uses <c>allOf</c>, not <c>anyOf</c>/<c>oneOf</c>,
    /// and why <see cref="IsIfDiscriminatorNoise"/> and this method target
    /// disjoint shapes.
    /// </summary>
    private static bool IsCompositeBranchRoot(string evaluationPath, out string prefix, out bool isOneOf)
    {
        var segments = evaluationPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 &&
            (segments[^2] == "oneOf" || segments[^2] == "anyOf") &&
            int.TryParse(segments[^1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            isOneOf = segments[^2] == "oneOf";
            prefix = "/" + string.Join('/', segments[..^1]);
            return true;
        }

        prefix = string.Empty;
        isOneOf = false;
        return false;
    }

    /// <summary>
    /// Scans EVERY position in <paramref name="evaluationPath"/> — not only
    /// its own final two segments — for a <c>oneOf</c>/<c>anyOf</c> keyword
    /// immediately followed by a branch index, yielding the prefix (up to and
    /// including the keyword) at each occurrence found. Mirrors
    /// <see cref="IsIfDiscriminatorNoise"/>'s OWN deliberate depth-independent
    /// full-path scan (see that method's remarks for why depth-independence
    /// is CORRECT, not merely convenient, for a composite/discriminator
    /// keyword's own pass/fail): a losing branch's failure can sit arbitrarily
    /// deep inside that branch's own nested sub-schema (e.g.
    /// <c>.../anyOf/1/properties/x</c>, not merely <c>.../anyOf/1</c> itself),
    /// and every such descendant belongs to that SAME branch for suppression
    /// purposes (MAJOR-4).
    /// </summary>
    /// <remarks>
    /// Segment-based reconstruction (split, then rejoin the prefix with a
    /// single leading slash) rather than a raw substring cut: JsonSchema.Net
    /// evaluation paths are always <c>/</c>-rooted RFC 6901 pointers with no
    /// empty segments in practice (schema keywords never have empty names),
    /// so this reconstruction is byte-identical to the original text for
    /// every prefix it produces — verified against
    /// <see cref="IsCompositeBranchRoot"/>'s own (independently written, for
    /// the shallow case only) prefix computation.
    /// </remarks>
    private static IEnumerable<(string Prefix, bool IsOneOf)> FindCompositeBranchOccurrences(string evaluationPath)
    {
        var segments = evaluationPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i + 1 < segments.Length; i++)
        {
            if ((segments[i] == "oneOf" || segments[i] == "anyOf") &&
                int.TryParse(segments[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                yield return ("/" + string.Join('/', segments[..(i + 1)]), segments[i] == "oneOf");
            }
        }
    }

    /// <summary>
    /// True when <paramref name="descendant"/> is <paramref name="ancestor"/>
    /// itself, or a proper descendant of it under RFC 6901 JSON Pointer
    /// segment boundaries (e.g. <c>/steps/1</c> is a descendant of
    /// <c>/steps</c>, but <c>/steps/10</c> is deliberately NOT a descendant of
    /// <c>/steps/1</c> — a raw <see cref="string.StartsWith(string)"/> would
    /// wrongly accept that second case).
    /// </summary>
    private static bool IsPathOrDescendant(string ancestor, string descendant)
    {
        if (string.Equals(ancestor, descendant, StringComparison.Ordinal))
            return true;

        return descendant.Length > ancestor.Length &&
               descendant.StartsWith(ancestor, StringComparison.Ordinal) &&
               descendant[ancestor.Length] == '/';
    }

    /// <summary>
    /// Drops a step's <c>unevaluatedProperties</c> entries when that SAME step
    /// also carries at least one error of a different kind — the cascade
    /// described in the class remarks. A step's fields can only be judged
    /// "unevaluated" relative to whichever if/then clause matched its
    /// <c>type</c>; once that clause has already failed for an unrelated
    /// reason (a missing required field, a bad enum, …), every one of its
    /// <c>properties</c> annotations is withheld too, so its otherwise
    /// legitimate fields present as false positives. Suppressing them here
    /// trades completeness for correctness: if a step carries BOTH a genuine
    /// defect and an unrelated typo, the typo is hidden this round, the
    /// author fixes the reported defect, re-runs, and sees the typo next —
    /// standard iterative convergence, and preferable to asserting a false
    /// "unknown property" alongside a true one. When the ONLY thing wrong
    /// with a step is an unevaluated property, nothing here touches it — that
    /// is the feature <c>unevaluatedProperties: false</c> exists for.
    /// </summary>
    /// <remarks>
    /// Grouping is by the step's OWN instance path (<c>/steps/&lt;N&gt;</c>),
    /// derived from each error's <see cref="EvaluationResults.InstanceLocation"/>
    /// rather than assumed from list position, so two steps in one document
    /// are judged independently — see <see cref="TryGetStepScope"/>. An error
    /// whose location does not fall under <c>/steps/&lt;N&gt;</c> at all (e.g.
    /// a document-level violation such as a missing <c>steps</c> section) has
    /// no step scope and is therefore never touched by this method — the rule
    /// is deliberately confined to the step surface.
    /// </remarks>
    private static List<CollectedError> SuppressUnevaluatedPropertiesCascade(List<CollectedError> errors)
    {
        HashSet<string>? stepsWithOtherErrors = null;

        foreach (var error in errors)
        {
            if (!error.IsUnevaluatedProperties && TryGetStepScope(error.InstanceLocation, out var scope))
            {
                stepsWithOtherErrors ??= new HashSet<string>(StringComparer.Ordinal);
                stepsWithOtherErrors.Add(scope);
            }
        }

        // Fast path: no step in this document carries a non-unevaluatedProperties
        // error at all, so there is nothing to cascade from — every collected
        // error survives unchanged, in original tree-walk order.
        if (stepsWithOtherErrors is null)
            return errors;

        var survivors = new List<CollectedError>(errors.Count);
        foreach (var error in errors)
        {
            if (error.IsUnevaluatedProperties &&
                TryGetStepScope(error.InstanceLocation, out var scope) &&
                stepsWithOtherErrors.Contains(scope))
            {
                continue;
            }

            survivors.Add(error);
        }

        return survivors;
    }

    /// <summary>
    /// Extracts the owning step's own instance path (<c>/steps/&lt;N&gt;</c>)
    /// from an error's <c>InstanceLocation</c>, e.g. <c>/steps/0/target</c> or
    /// <c>/steps/0</c> itself both yield <c>/steps/0</c>. Returns
    /// <see langword="false"/> for any location that does not sit under a
    /// numbered <c>steps</c> element at all — the document-missing-`steps`
    /// case, or any future non-step top-level section — so callers never
    /// accidentally scope a suppression decision to something outside the
    /// step surface.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> rather than <c>private</c>: <see cref="DocumentValidator"/>
    /// reuses this to scope its OWN unknown-step-type cross-check (#265) the
    /// same way — a step whose <c>type</c> matches no registered provider at
    /// all can never have any if/then fragment claim its properties either, so
    /// the identical cascade (an unrelated, step-level defect masking every
    /// other field as spuriously "unevaluated") recurs there. That defect
    /// lives outside the composed schema (a registry lookup, not a JSON Schema
    /// constraint) so <see cref="SuppressUnevaluatedPropertiesCascade"/> alone
    /// cannot see it; sharing the pointer-parsing logic keeps both call sites
    /// agreeing on what "the owning step" means without duplicating it.
    /// </remarks>
    internal static bool TryGetStepScope(string instanceLocation, out string stepScope)
    {
        var segments = instanceLocation.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 &&
            segments[0] == "steps" &&
            int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            stepScope = $"/steps/{segments[1]}";
            return true;
        }

        stepScope = string.Empty;
        return false;
    }

    /// <summary>
    /// Formats a single keyword/message pair into the text an author sees,
    /// substituting a named, actionable message for each of the three
    /// blank-keyword false-schema shapes (see the class remarks —
    /// <c>unevaluatedProperties</c>, <c>additionalProperties</c>, and a
    /// per-field <c>properties/&lt;name&gt;: false</c>), naming the owning
    /// dependency/service for a <c>required</c> violation inside a
    /// <c>security</c> block (REQ-002 — e.g. the missing-<c>endpoint</c>
    /// case), enriching an <c>enum</c> violation with the offending value
    /// and the live accepted list (see <see cref="FormatEnumError"/>),
    /// enriching a <c>const</c> violation similarly (m4 — see
    /// <see cref="FormatConstError"/>), and falling back to the original
    /// <c>[{keyword}] {message}</c> format for every other, genuinely-named
    /// keyword — including a <c>required</c> violation anywhere OUTSIDE a
    /// <c>security</c> block, which stays exactly as before.
    /// </summary>
    private static string FormatError(
        string keyword, string message, string evaluationPath, string instanceLocation,
        JsonElement? instance, JsonElement? schema, Uri schemaLocation)
    {
        if (IsUnevaluatedPropertiesShape(keyword, evaluationPath))
        {
            // REQ-020 (authenticated-infrastructure-mtls, slice C): $defs/security now closes
            // with 'unevaluatedProperties: false' instead of 'additionalProperties: false', so
            // an unrecognised key directly inside a security block now reaches THIS branch
            // rather than FormatAdditionalPropertiesError's — measured regression, not a design
            // choice: an
            // unqualified fall-through to the step-only FormatUnevaluatedPropertiesError below
            // would silently DROP the "on dependency '<name>'"/"in dependency '<name>' (at
            // security...)" attribution FormatAdditionalPropertiesError already gives every
            // OTHER environment-surface unknown-key rejection, since $defs/security's own
            // instance has no 'type' field for TryResolveContainerType to read (a dependency's
            // 'type' lives two levels up, on the OWNING dependency, not on the security object
            // itself). Checking TryResolveEnvironmentContainer FIRST — exactly the same check
            // FormatAdditionalPropertiesError makes — closes that gap; a step's own
            // unevaluatedProperties violation (InstanceLocation always rooted at '/steps/...')
            // never matches this environment-rooted check and falls through to the step-naming
            // formatter unchanged.
            //
            // MINOR-2 (fix round 4): the first sentence above used to read "directly inside a
            // security block (or one of its own serverArtifacts[] entries)". The parenthetical is
            // false and is dropped — serverArtifacts items $ref $defs/securityServerArtifact,
            // which retains 'additionalProperties: false', so an unknown key on an artifact entry
            // still lands in FormatAdditionalPropertiesError's branch. Measured: the corpus
            // fixture security-serverartifacts-unknown-key.e2e.yaml pins 'expected-keyword:
            // additionalProperties' and is green.
            if (TryResolveEnvironmentContainer(
                    instanceLocation, out var unevalContainerKind, out var unevalContainerName,
                    out var unevalIsNestedBelowSecurity, allowNestedSecurity: true))
            {
                var unevalPropertyName = LastPointerSegment(instanceLocation);
                return unevalIsNestedBelowSecurity
                    ? $"[unevaluatedProperties] Unknown property '{unevalPropertyName}' in {unevalContainerKind} " +
                      $"'{unevalContainerName}' (at {BuildSecuritySubPath(instanceLocation)})"
                    : $"[unevaluatedProperties] Unknown property '{unevalPropertyName}' on {unevalContainerKind} " +
                      $"'{unevalContainerName}'";
            }

            return FormatUnevaluatedPropertiesError(instanceLocation, instance);
        }

        if (IsAdditionalPropertiesShape(keyword, evaluationPath))
        {
            return FormatAdditionalPropertiesError(instanceLocation);
        }

        if (IsForbiddenPropertyShape(keyword, evaluationPath))
        {
            return FormatForbiddenPropertyError(instanceLocation, instance, schema, schemaLocation);
        }

        // REQ-002 / gatekeeper NOTE-1 (refined by MINOR-6, honest nested attribution): a
        // 'required' violation AT the dependency's/service's own 'security' sub-object
        // itself — the missing-'endpoint' case above all, and mtls's paired
        // 'clientCert'/'clientKey' — names the owning dependency/service with "on",
        // exactly like the additionalProperties/properties shapes above already do at
        // the same depth: the security block IS a property of that container, so "on
        // dependency/service '<name>'" is a literally accurate attribution. A 'required'
        // violation BELOW the security object — a serverArtifacts[] entry's own missing
        // 'source'/'target' — is a DIFFERENT container one level further in (one of
        // potentially SEVERAL serverArtifacts entries), so naming only the outer
        // dependency/service would be honest but imprecise about WHICH entry; this uses
        // "in <kind> '<name>' (at <subpath>)" instead, with the dotted sub-path (see
        // BuildSecuritySubPath) locating the specific entry, e.g. "in dependency
        // 'events-kafka' (at security.serverArtifacts[0])". A 'required' violation
        // anywhere ELSE (a step's own missing fields, a missing dependency 'type', …) is
        // untouched: TryResolveEnvironmentContainer only resolves outside a security
        // context at exactly depth 4 (any field directly on the dependency/service).
        //
        // CORRECTION (G1, gatekeeper MAJOR-1): the paragraph above used to end "…which no
        // OTHER 'required' clause in this schema currently reaches" — FALSE once
        // $defs/serviceHealthCheck shipped. Its own 'required: ["type"]', and its
        // conditional 'required: ["port"]' for a 'type: tcp' healthCheck, ALSO instantiate
        // at exactly depth 4 (/environment/services/<name>/healthCheck — a 'required'
        // violation always reports the CONTAINER missing the property, never the property
        // itself, so this is depth 4 regardless of which of the two clauses fired). Left to
        // TryResolveEnvironmentContainer alone this produced the misleading "Required
        // properties [\"type\"] are not present on service '<name>'" — naming the SERVICE
        // itself when the healthCheck BLOCK is what is actually incomplete, and inviting
        // confusion with a DEPENDENCY's own required 'type' field (an unrelated, sibling
        // concept). TryResolveHealthCheckContainer, checked FIRST below, now intercepts
        // exactly this shape and renders the honest nested form instead — "in service
        // '<name>' (at healthCheck)" — mirroring the serverArtifacts nested form above
        // rather than reusing the security block's "on" form.
        if (keyword == "required")
        {
            if (TryResolveHealthCheckContainer(instanceLocation, out var healthCheckContainerName))
            {
                return $"[required] {message} in service '{healthCheckContainerName}' (at healthCheck)";
            }

            if (TryResolveEnvironmentContainer(
                instanceLocation, out var requiredContainerKind, out var requiredContainerName,
                out var requiredIsNestedBelowSecurity, allowNestedSecurity: true))
            {
                return requiredIsNestedBelowSecurity
                    ? $"[required] {message} in {requiredContainerKind} '{requiredContainerName}' " +
                      $"(at {BuildSecuritySubPath(instanceLocation)})"
                    : $"[required] {message} on {requiredContainerKind} '{requiredContainerName}'";
            }
        }

        if (keyword == "enum")
        {
            return FormatEnumError(instanceLocation, instance, schema, schemaLocation);
        }

        if (keyword == "const")
        {
            return FormatConstError(instanceLocation, instance, schema, schemaLocation);
        }

        if (keyword == "minProperties")
        {
            return FormatMinPropertiesError(message, instanceLocation, schema, schemaLocation);
        }

        if (IsPropertyNamesPatternShape(keyword, evaluationPath))
        {
            return FormatReservedPrefixError(instanceLocation);
        }

        return $"[{keyword}] {message}";
    }

    /// <summary>
    /// True for the blank-keyword <c>unevaluatedProperties: false</c> shape
    /// (see the class remarks) — shared by <see cref="FormatError"/> (which
    /// keyword decides how to render the message) and
    /// <see cref="CollectErrorsRecursive"/> (which tags each collected error
    /// so <see cref="SuppressUnevaluatedPropertiesCascade"/> can group on it
    /// without re-parsing rendered text).
    /// </summary>
    private static bool IsUnevaluatedPropertiesShape(string keyword, string evaluationPath) =>
        keyword.Length == 0 && EndsWithSegment(evaluationPath, "unevaluatedProperties");

    /// <summary>
    /// True for the blank-keyword <c>additionalProperties: false</c> shape —
    /// an unknown key rejected by a plain object closure (<c>$defs/metadata</c>,
    /// <c>$defs/service</c>, <c>$defs/dependency</c>, the document root, a
    /// <c>topics[]</c> item, or a provider's own nested block such as
    /// <c>db-assert.postgres</c>'s <c>expect</c>) rather than the step-level
    /// <c>unevaluatedProperties</c> closure. Same generic underlying message
    /// as <see cref="IsUnevaluatedPropertiesShape"/>, distinguished only by
    /// EvaluationPath's terminal segment, exactly as that shape is.
    /// </summary>
    private static bool IsAdditionalPropertiesShape(string keyword, string evaluationPath) =>
        keyword.Length == 0 && EndsWithSegment(evaluationPath, "additionalProperties");

    /// <summary>
    /// True for the blank-keyword per-field <c>"&lt;name&gt;": false</c>
    /// shape used to forbid one specific, already-declared property —
    /// $defs/dependency's per-kind exclusions (<c>schemaRegistry</c> off a
    /// non-kafka kind, <c>queues</c>/<c>topics</c> off everything but
    /// azureservicebus) and $defs/service's <c>image</c>/<c>project</c>
    /// mutual exclusion. Recognised structurally: JsonSchema.Net only ever
    /// produces a leaf node whose EvaluationPath terminates EXACTLY at
    /// <c>properties/&lt;name&gt;</c> (nothing deeper) when the subschema
    /// mapped to that property is a bare boolean — a normal (non-boolean)
    /// subschema's own failing keywords always add at least one more path
    /// segment beyond the property name. This holds for every
    /// <c>properties/&lt;name&gt;: false</c> declaration in the current
    /// schema (verified empirically); it is not, and does not need to be,
    /// specific to the dependency/service definitions by name.
    /// </summary>
    private static bool IsForbiddenPropertyShape(string keyword, string evaluationPath)
    {
        if (keyword.Length != 0)
        {
            return false;
        }

        var segments = evaluationPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && segments[^2] == "properties";
    }

    /// <summary>
    /// True for a <c>propertyNames</c> sub-schema's own <c>pattern</c>
    /// keyword failure — the shape both <c>capture</c>'s and top-level
    /// <c>variables</c>'s reserved-bookkeeping-prefix guard produce (a
    /// negative-lookahead regex, see VarKeys). Unlike the blank-keyword
    /// shapes above, this node's OWN keyword is genuinely <c>pattern</c> (a
    /// real, non-boolean sub-schema), so EvaluationPath is what disambiguates
    /// this SPECIFIC pattern failure from any other <c>pattern</c> keyword
    /// elsewhere in the schema (a step's own <c>id</c>/<c>type</c> pattern,
    /// httpPort's range pattern, …).
    /// </summary>
    /// <remarks>
    /// Every <c>propertyNames</c> declaration in the current schema exists
    /// SOLELY for the reserved-prefix guard, so this shape is treated as
    /// exactly that without needing to inspect the regex text itself — see
    /// <see cref="FormatReservedPrefixError"/>'s own generic-but-specific
    /// message, which deliberately never hardcodes the prefix list (MINOR-7:
    /// that list already lives in one place, VarKeys, and the schema's own
    /// 'description' — repeating it here would be a THIRD place to keep in
    /// sync).
    /// </remarks>
    private static bool IsPropertyNamesPatternShape(string keyword, string evaluationPath)
    {
        // Verified empirically (JsonSchema.Net 9.2.1): the 'pattern' failure
        // is reported ON the propertyNames node itself — EvaluationPath ends
        // AT '.../propertyNames', not a deeper '.../propertyNames/pattern' —
        // so this checks the FINAL segment, mirroring EndsWithSegment's other
        // callers, not a "second-to-last" check like IsForbiddenPropertyShape.
        return keyword == "pattern" && EndsWithSegment(evaluationPath, "propertyNames");
    }

    /// <summary>
    /// True when <paramref name="message"/> is one this class produced via
    /// <see cref="FormatUnevaluatedPropertiesError"/> — recognised by its
    /// fixed <c>"[unevaluatedProperties] "</c> prefix, the only place that
    /// literal is ever written. Exposed for <see cref="DocumentValidator"/>,
    /// which only ever sees the final formatted <see cref="SchemaValidationError.Message"/>
    /// (never the raw keyword/evaluation-path pair <see cref="IsUnevaluatedPropertiesShape"/>
    /// needs) — see <see cref="TryGetStepScope"/>'s remarks for why it needs
    /// this at all.
    /// </summary>
    internal static bool IsUnevaluatedPropertiesMessage(string message) =>
        message.StartsWith("[unevaluatedProperties] ", StringComparison.Ordinal);

    /// <summary>
    /// Builds the actionable message for an <c>unevaluatedProperties: false</c>
    /// rejection: names the offending property (the last segment of
    /// <paramref name="instanceLocation"/>) and, when <paramref name="instance"/>
    /// is supplied and the property's containing object carries a string
    /// <c>type</c> field (true of every step — the schema requires it), the
    /// step's own type — e.g. <c>Unknown property 'taget' on step type
    /// 'http.rest'</c>. The "on step type '…'" suffix is omitted, not
    /// fabricated, when the type cannot be resolved (no instance supplied, or
    /// the containing object/its 'type' is missing or non-string).
    /// </summary>
    private static string FormatUnevaluatedPropertiesError(string instanceLocation, JsonElement? instance)
    {
        var propertyName = LastPointerSegment(instanceLocation);
        var stepType = instance is { } root ? TryResolveContainerType(instanceLocation, root) : null;

        return stepType is null
            ? $"[unevaluatedProperties] Unknown property '{propertyName}'"
            : $"[unevaluatedProperties] Unknown property '{propertyName}' on step type '{stepType}'";
    }

    /// <summary>
    /// Builds the actionable message for an <c>additionalProperties: false</c>
    /// rejection: names the offending property and, when
    /// <paramref name="instanceLocation"/> matches the pointer shape
    /// <c>/environment/(services|dependencies)/&lt;name&gt;/&lt;property&gt;</c>, OR is
    /// at-or-below that same container's own <c>security</c> sub-object — e.g.
    /// <c>.../security/&lt;bogus&gt;</c> or a nested
    /// <c>.../security/serverArtifacts/0/&lt;bogus&gt;</c> (REQ-002; see
    /// <see cref="TryResolveEnvironmentContainer"/>'s <c>allowNestedSecurity</c>) — or a
    /// captureEntry's own <c>/steps/&lt;N&gt;/capture/&lt;varName&gt;/&lt;property&gt;</c>
    /// (n-b, fourth-round gatekeeper re-review — see
    /// <see cref="TryResolveCaptureEntryContainer"/>), the container's own
    /// kind and map key — e.g. <c>Unknown property 'imagePullPolcy' on
    /// service 'orders-api'</c>, <c>Unknown property 'bogus' on dependency
    /// 'cache'</c> for an unrecognised key directly inside that dependency's own
    /// <c>security</c> block, or <c>Unknown property 'badkey' on capture
    /// entry 'orderId'</c>. An unrecognised key BELOW the security object — inside one
    /// of its own <c>serverArtifacts[]</c> entries — instead reads <c>Unknown property
    /// 'bogus' IN dependency 'events-kafka' (at security.serverArtifacts[0].bogus)</c>
    /// (MINOR-6, honest nested attribution): the entry is a distinct container one level
    /// further in, so the sub-path also locates WHICH one — see the <c>required</c>
    /// branch in <see cref="FormatError"/> for the identical on/in split, and
    /// <see cref="BuildSecuritySubPath"/> for the sub-path rendering. Every other
    /// unknown-key surface this shape covers (<c>metadata</c>, the document root, a
    /// <c>topics[]</c> item, a provider's own nested block) degrades to the bare
    /// property name rather than guessing a container that cannot be reliably named
    /// from the pointer alone — see the class remarks and
    /// <see cref="FormatUnevaluatedPropertiesError"/>'s own no-fabrication
    /// rule, which this mirrors.
    /// </summary>
    private static string FormatAdditionalPropertiesError(string instanceLocation)
    {
        var displayProperty = TruncateForDisplay(LastPointerSegment(instanceLocation));

        if (TryResolveEnvironmentContainer(
            instanceLocation, out var containerKind, out var containerName,
            out var isNestedBelowSecurity, allowNestedSecurity: true))
        {
            var displayContainer = TruncateForDisplay(containerName);

            return isNestedBelowSecurity
                ? $"[additionalProperties] Unknown property '{displayProperty}' in {containerKind} '{displayContainer}' " +
                  $"(at {TruncateForDisplay(BuildSecuritySubPath(instanceLocation))})"
                : $"[additionalProperties] Unknown property '{displayProperty}' on {containerKind} '{displayContainer}'";
        }

        if (TryResolveCaptureEntryContainer(instanceLocation, out var variableName))
        {
            return $"[additionalProperties] Unknown property '{displayProperty}' on capture entry " +
                   $"'{TruncateForDisplay(variableName)}'";
        }

        return $"[additionalProperties] Unknown property '{displayProperty}'";
    }

    /// <summary>
    /// Builds the actionable message for the per-field
    /// <c>"&lt;name&gt;": false</c> rejection: names the offending property
    /// and the rule it broke.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On a <c>dependency</c> container (see
    /// <see cref="TryResolveEnvironmentContainer"/>), reuses
    /// <see cref="TryResolveContainerType"/> — exactly the helper
    /// <see cref="FormatUnevaluatedPropertiesError"/> already uses to read a
    /// step's own <c>type</c> — to read the dependency's own <c>type</c> and
    /// name it: <c>Property 'schemaRegistry' is not valid on a 'postgres'
    /// dependency</c>. When the kind cannot be resolved (no instance, or the
    /// container's own <c>type</c> is missing/non-string — should not arise
    /// in practice since <c>type</c> is required, but this is a best-effort
    /// diagnostic, not a second validation pass) this degrades to naming the
    /// dependency by its own map key instead, never a guessed kind.
    /// </para>
    /// <para>
    /// On a <c>service</c> container, TWO of the per-field exclusions
    /// $defs/service declares are named directly here, and the branches below
    /// are the whole of that list: <c>project</c> forbidden once <c>image</c>
    /// is set (see that definition's own description), and <c>httpPort</c>
    /// forbidden once <c>project</c> is set. Each is a single, fixed rule whose
    /// remedy is more than "delete the field" — the first tells an author the
    /// two keys are alternatives, the second names the field they actually
    /// wanted — so each gets bespoke wording rather than a generalisation. The
    /// <c>project</c> branch's offending <c>then</c> is reachable only via a
    /// sibling <c>if: required:["image"]</c>, and the <c>httpPort</c> branch's
    /// only via a sibling <c>if: required:["project"]</c>, so the other key's
    /// presence is a schema invariant of each branch, not an assumption this
    /// method makes about the instance. EVERY OTHER per-field service exclusion, whatever
    /// the roster is at any given time (read it off $defs/service's own
    /// <c>allOf</c>), is deliberately NOT named individually: each falls
    /// through to the generic "Property '&lt;name&gt;' is not valid on service
    /// '&lt;name&gt;'" text below, which already tells an author the one thing
    /// they must do (delete the field). Stated as that property rather than as
    /// a list on purpose: this paragraph has twice carried a claim about the
    /// roster's contents instead — first that the image/project rule was the
    /// only such exclusion in the schema at all, then a hand-enumeration of the
    /// rest. The first was already false by the time anyone read it; the second
    /// was accurate when written and had the same failure mode waiting, because
    /// no gate proves the roster either way.
    /// </para>
    /// <para>
    /// On a <c>captureEntry</c> container (m6 — see
    /// <see cref="TryResolveCaptureEntryContainer"/>), names BOTH keys:
    /// the offending one and the one already present that makes it
    /// forbidden, the latter read from the LIVE schema's own sibling
    /// <c>properties</c> (see <see cref="TryReadSiblingPropertyNames"/>) —
    /// unlike the service case above, never hardcoded to 'jsonpath'/'xpath'
    /// specifically.
    /// </para>
    /// <para>
    /// n-a (fourth-round gatekeeper re-review): that name-lookup genericity
    /// does NOT make this branch correct for an arbitrary number of
    /// extractor formats — only for TODAY's exactly two (both declared in
    /// this engine-owned <c>$defs/captureEntry</c> definition, not a
    /// provider extension point the way step types are). The message
    /// template's own grammar ("cannot be combined with {otherNames} …
    /// exactly one of the two is required") assumes exactly one OTHER name;
    /// a hypothetical third extractor would leave
    /// <see cref="TryReadSiblingPropertyNames"/> returning TWO remaining
    /// names for a two-of-three violation, rendering both the cardinality
    /// ("the two") and the implied "already present" reading of
    /// <c>otherNames</c> wrong (it would name a sibling property merely
    /// DECLARED in the schema, not necessarily one the author actually set).
    /// Revisit this branch's wording, not merely its tests, if a third
    /// extractor format is ever added.
    /// </para>
    /// <para>
    /// M5 (gatekeeper, feat/fragment-completeness — gatekeeper dissent
    /// accepted over the original "measured, degrade-don't-fabricate is
    /// enough" call): a FOURTH, fully generic branch covers every
    /// <c>allOf</c>/<c>if</c>/<c>then</c>-declared per-field exclusion
    /// OUTSIDE the three named containers above — storage-assert.s3's
    /// <c>size</c>/<c>minSize</c> mutual exclusion and its (and
    /// db-assert.dynamodb's) <c>exists: false</c>-forbids-content-fields
    /// shapes, and any future provider using either pattern. Reads the
    /// offending node's OWN sibling <c>if</c> clause from the live schema
    /// (<see cref="TryReadSiblingIfClause"/>) and classifies its shape:
    /// <list type="bullet">
    ///   <item>
    ///     <c>if: {required:[x], properties:{x:{const:v}}}</c> (a CONDITION —
    ///     storage-assert.s3's and db-assert.dynamodb's <c>exists: false</c>
    ///     shapes) → <c>Property 'item' is not valid when 'exists' is
    ///     false</c> (<see cref="TryReadSingleConstCondition"/>).
    ///   </item>
    ///   <item>
    ///     <c>if: {required:[x]}</c> and NOTHING else (a plain co-presence
    ///     exclusion — storage-assert.s3's <c>size</c>/<c>minSize</c> shape)
    ///     → <c>Property 'minSize' cannot be combined with 'size' — exactly
    ///     one of the two may be set</c>
    ///     (<see cref="TryReadSoleRequiredNoOtherContent"/>).
    ///   </item>
    /// </list>
    /// Both helpers mirror <see cref="EngineExport.TryReadSingleRequiredGroupNames"/>'s
    /// OWN "exactly one property, no other content" strictness (independently
    /// implemented — this class has no dependency on
    /// <c>Vouchfx.Engine.Compilation.Schema.EngineExport</c>'s internals) —
    /// never widened to guess at a more complex condition.
    /// </para>
    /// <para>
    /// Any future <c>properties/&lt;name&gt;: false</c> declaration whose
    /// sibling <c>if</c> matches NEITHER shape above (outside all three named
    /// containers, plus this fourth generic case) degrades to naming the
    /// property alone — never fabricating a rule this method was not told
    /// about.
    /// </para>
    /// </remarks>
    private static string FormatForbiddenPropertyError(
        string instanceLocation, JsonElement? instance, JsonElement? schema, Uri schemaLocation)
    {
        var propertyName = LastPointerSegment(instanceLocation);
        var displayProperty = TruncateForDisplay(propertyName);

        if (TryResolveEnvironmentContainer(instanceLocation, out var containerKind, out var containerName, out _))
        {
            var displayContainer = TruncateForDisplay(containerName);

            if (containerKind == DependencyContainerKind)
            {
                var dependencyKind = instance is { } root ? TryResolveContainerType(instanceLocation, root) : null;
                var displayKind = dependencyKind is null ? null : TruncateForDisplay(dependencyKind);

                // REQ-021, as tightened by M1 (peer review, fix round 2): 'security' is forbidden
                // on every dependency kind except kafka, and the generic "Property 'x' is not
                // valid on a 'redis' dependency" wording above is the WRONG diagnosis for it. An
                // author reading that concludes they wrote the block in the wrong PLACE, or under
                // the wrong KEY; the truth is narrower and more useful — the block is understood
                // perfectly well, and no security profile is wired for that dependency kind IN
                // THIS RELEASE. This is also why the clause is a boolean 'false' on the whole
                // block rather than a pin on 'security.profile': pinning the profile can only
                // ever say "you picked the wrong value", which would be a lie — no value works.
                //
                // MAJOR-1 (peer review, fix round 4): this message used to end "declare it under
                // 'environment.services' instead, where every registered profile is wired", and
                // that remedy is a dead end for every kind the message can fire on. Measured
                // against the 25 Core providers: 17 resolve 'target' only through
                // IProjectContext.DeclaredDependencies and reject a target that is not a declared
                // dependency of their own kind (DbAssertPostgresProvider.Validate,
                // CacheAssertRedisProvider.Validate, MqPublishRabbitmqProvider.Validate, …),
                // and those 17 cover all twelve excluded kinds — so an author who follows
                // that advice for a TLS-secured postgres trades this rejection for a
                // step-validation rejection and still has no working suite. Only http.rest,
                // http.soap and metrics-assert.prometheus resolve 'target' through
                // DeclaredServices (they read DeclaredDependencies solely to phrase a better
                // rejection), so the service form works solely for an HTTP system under test —
                // never for the twelve kinds this branch fires on. (Kafka is the one kind
                // that reaches a security block through BOTH surfaces, and it is already legal as
                // a dependency, so this message never fires for it at all. Its service-target path
                // is a WORKING one, not a stub: REQ-023 has both Kafka providers accept a declared
                // service and emit a lookup of the svc::<name> bootstrap authority the engine
                // stages for it — MqPublishKafkaProvider.Emit and MqExpectKafkaProvider.Emit pick
                // the svc:: key over conn:: from the same DeclaredServices map Validate reconciled
                // against. That changes nothing here, but it changes the REASON: kafka needs no
                // substitute declaration because this clause never refuses it, NOT because no
                // working alternative exists for it.)
                // The message therefore states the accepted surface and the release position, and
                // offers no substitute declaration, because for the twelve kinds it fires on none
                // exists. REQ-013 widens the set in 1.1 — said explicitly so an author reads a
                // roadmap position, not a permanent refusal. This text is about to enter a frozen
                // surface: verify any future edit against what a provider does at step-execution
                // time, not what the schema accepts.
                //
                // That guard is not decorative, and it has already been proved by its own breach:
                // the Kafka parenthetical above used to assert the opposite of what it now says —
                // that the service-target path staged nothing and aborted at run time as an
                // environment error. True when written, false from the moment REQ-023 landed the
                // svc:: staging, and it survived anyway: here, in the frozen surface, and in the
                // schema's mirrored $comment, through the very review round that shipped REQ-023.
                // Nothing caught it, because every gate over this text proves only that the SCHEMA
                // still composes byte-identically, and not one of them can tell whether a sentence
                // about RUN-TIME behaviour is still true. The instruction was right and went
                // unapplied. Applying it is the point: re-read this block against the providers,
                // not against the goldens. (The retracted sentence is deliberately NOT quoted
                // verbatim, here or in the schema — a grep for its distinctive phrasing has to stay
                // a detector for the real thing, not a search that trips over its own obituary.)
                if (propertyName == "security")
                {
                    // NIT-1 (peer review, fix round 3): the kind is named as "dependency kind
                    // '<x>'", never "a '<x>' dependency" — the article cannot agree with a value
                    // that is DATA (a 'azureservicebus' / a 'elasticsearch'), and picking it from
                    // the next character's vowel-ness would still be wrong for a kind such as
                    // 'unified'. Dropping the article is the robust fix, not a cleverer test.
                    //
                    // Reachable, and measured: a NON-SCALAR 'type' (e.g. 'type: [redis, postgres]')
                    // still satisfies the narrowing clause's own 'not: { const: "kafka" }'
                    // condition, so 'security' is rejected while TryResolveContainerType has no
                    // scalar to read a kind from. Pinned by
                    // EnvironmentSchemaTests.Dependency_Security_NonScalarType_UsesTheUnqualifiedMessage.
                    if (displayKind is null)
                    {
                        return $"[properties] Dependency '{displayContainer}' declares 'security', but no " +
                               "security profile is wired for this dependency kind in this release — " +
                               "only a 'kafka' dependency, or a declared service, can carry a 'security' " +
                               "block today. Transport security for the remaining dependency kinds " +
                               "arrives in 1.1.";
                    }

                    return $"[properties] Dependency '{displayContainer}' (type '{displayKind}') declares " +
                           $"'security', but no security profile is wired for dependency kind " +
                           $"'{displayKind}' in this release — only a 'kafka' dependency, or a declared " +
                           "service, can carry a 'security' block today. Transport security for the " +
                           "remaining dependency kinds arrives in 1.1.";
                }

                return displayKind is null
                    ? $"[properties] Property '{displayProperty}' is not valid on dependency '{displayContainer}'"
                    : $"[properties] Property '{displayProperty}' is not valid on a '{displayKind}' dependency";
            }

            if (containerKind == ServiceContainerKind && propertyName == "project")
            {
                return $"[properties] Property 'project' cannot be combined with 'image' on service " +
                       $"'{displayContainer}' — exactly one of the two is required (DSL §3.2)";
            }

            // The generic text below ("Property 'httpPort' is not valid on service 'app'") is the
            // WRONG diagnostic for the one field on this container whose refusal is a BREAKING
            // CHANGE. `httpPort` was accepted on a project-form service and silently ignored;
            // refusing it reddens suites that ran green, and the author of such a suite meets this
            // message first — before any prose, in the output of the run that broke. "Not valid
            // here" tells them to delete a line they believed was doing work, and leaves them to
            // guess what replaces it. The remedy is a DIFFERENT FIELD, so the message has to name
            // it: `endpoint`, which selects among the listeners the project's own launch profile
            // declares. Naming it is also what distinguishes the two fields for good — `httpPort`
            // names a container PORT, `endpoint` names a LISTENER, and the field that looks like
            // the selector is not the one that is.
            //
            // Reachability is a schema invariant, not an assumption: `httpPort: false` appears in
            // exactly one place in $defs/service — the `if: required:["project"]` clause's `then`,
            // beside `ports` and `healthCheck` — so this branch can only fire on a project-form
            // service and may say so unconditionally. It is NOT reachable from `run`/`validate`'s
            // sibling in EnvironmentMapper, which throws its own longer message on the schema-free
            // `--watch` seam; the two are separate texts for separate seams, and this one is the
            // author-facing default because schema validation short-circuits first.
            if (containerKind == ServiceContainerKind && propertyName == "httpPort")
            {
                return $"[properties] Property 'httpPort' is not valid on the 'project'-form " +
                       $"service '{displayContainer}' — a project's endpoints are discovered from its " +
                       "own launch profile, and 'httpPort' names a container port rather than a " +
                       "listener. Remove the line; to choose WHICH discovered endpoint this " +
                       "service is addressed on, use 'endpoint' (DSL §3.2)";
            }

            return $"[properties] Property '{displayProperty}' is not valid on {containerKind} '{displayContainer}'";
        }

        if (TryResolveCaptureEntryContainer(instanceLocation, out var variableName))
        {
            var siblingNames = schema is { } schemaRoot
                ? TryReadSiblingPropertyNames(schemaRoot, schemaLocation, excludePropertyName: propertyName)
                : null;

            var displayVariable = TruncateForDisplay(variableName);

            if (siblingNames is { Count: > 0 })
            {
                var otherNames = string.Join(", ", siblingNames.Select(n => $"'{n}'"));
                return $"[properties] Property '{displayProperty}' cannot be combined with {otherNames} on " +
                       $"capture entry '{displayVariable}' — exactly one of the two is required (DSL §6.1)";
            }

            return $"[properties] Property '{displayProperty}' is not valid on capture entry '{displayVariable}'";
        }

        // M5: the generic allOf/if/then exclusion case (storage-assert.s3's
        // size/minSize and exists:false shapes; db-assert.dynamodb's exists:false
        // shape) — read the sibling 'if' condition directly from the live schema.
        if (schema is { } schemaRootForIf)
        {
            var ifClause = TryReadSiblingIfClause(schemaRootForIf, schemaLocation);
            if (ifClause is { } ifElement)
            {
                if (TryReadSingleConstCondition(ifElement, out var conditionField, out var conditionValueDisplay))
                {
                    return $"[properties] Property '{displayProperty}' is not valid when " +
                           $"'{conditionField}' is {conditionValueDisplay}";
                }

                if (TryReadSoleRequiredNoOtherContent(ifElement, out var otherField))
                {
                    return $"[properties] Property '{displayProperty}' cannot be combined with " +
                           $"'{otherField}' — exactly one of the two may be set";
                }
            }
        }

        return $"[properties] Property '{displayProperty}' is not valid here";
    }

    /// <summary>
    /// Resolves the sibling <c>if</c> clause of the <c>allOf/&lt;N&gt;/then</c>
    /// branch a forbidden-property node's own <paramref name="schemaLocation"/>
    /// sits inside — the SAME <c>allOf</c> index, navigated to <c>/if</c>
    /// instead of <c>/then/...</c>. Returns <see langword="null"/> when the
    /// pointer carries no <c>/allOf/&lt;N&gt;/</c> segment, or the resolved
    /// node is not an object (never guessed).
    /// </summary>
    /// <remarks>
    /// Uses the LAST <c>/allOf/</c> occurrence in the pointer, not the first:
    /// a nested provider field (e.g. db-assert.dynamodb's <c>expect.item</c>)
    /// sits inside BOTH the step-level <c>$defs/step/allOf/&lt;N&gt;</c>
    /// type-discriminator (matching <c>type: db-assert.dynamodb</c>) AND the
    /// provider's own NESTED <c>expect.allOf/0</c> — the first (outer)
    /// occurrence resolves to the type discriminator's <c>if</c>
    /// (<c>{"required":["type"],"properties":{"type":{"const":"db-assert.dynamodb"}}}</c>),
    /// producing the wrong, misleading condition ("is not valid when 'type'
    /// is 'db-assert.dynamodb'" — true of every field on the type, not the
    /// actual <c>exists: false</c> rule). Verified empirically (this exact
    /// wrong output was observed before the fix — see
    /// <c>DbAssertDynamodb_ExistsFalse_WithItem_IsRejected</c> /
    /// <c>StorageAssertS3_*</c> in <c>SchemaValidateConstraintsTests</c>); the
    /// LAST occurrence is always the innermost, most-specific <c>allOf</c> —
    /// the one that actually produced this forbidden-property node.
    /// </remarks>
    private static JsonElement? TryReadSiblingIfClause(JsonElement schemaRoot, Uri schemaLocation)
    {
        var fragment = schemaLocation.Fragment;
        if (fragment.Length == 0)
        {
            return null;
        }

        var pointer = Uri.UnescapeDataString(fragment.TrimStart('#'));
        const string allOfMarker = "/allOf/";
        var allOfIndex = pointer.LastIndexOf(allOfMarker, StringComparison.Ordinal);
        if (allOfIndex < 0)
        {
            return null;
        }

        var afterAllOf = pointer[(allOfIndex + allOfMarker.Length)..];
        var slashIndex = afterAllOf.IndexOf('/');
        var indexSegment = slashIndex < 0 ? afterAllOf : afterAllOf[..slashIndex];
        var ifPointer = pointer[..allOfIndex] + allOfMarker + indexSegment + "/if";

        return TryResolvePointer(schemaRoot, ifPointer, out var ifNode) && ifNode.ValueKind == JsonValueKind.Object
            ? ifNode
            : null;
    }

    /// <summary>
    /// True when <paramref name="ifClause"/> is shaped EXACTLY
    /// <c>{"required": ["field"], "properties": {"field": {"const": value}}}</c>
    /// — a condition on ONE field's exact value (storage-assert.s3's and
    /// db-assert.dynamodb's <c>exists: false</c> shapes) — and, when so,
    /// outputs the condition field's name and a display-ready rendering of
    /// its <c>const</c> value (<c>true</c>/<c>false</c> bare, a string
    /// single-quoted, a number verbatim). Requires EXACTLY one
    /// <c>const</c>-bearing property under <c>properties</c> — two or more
    /// is ambiguous and degrades (returns <see langword="false"/>) rather
    /// than guessing which one is "the" condition.
    /// </summary>
    private static bool TryReadSingleConstCondition(
        JsonElement ifClause, out string fieldName, out string constDisplay)
    {
        fieldName = string.Empty;
        constDisplay = string.Empty;

        // Exact-shape check, part one: the if-clause itself carries EXACTLY the
        // two members the doc contract names — 'required' and 'properties' —
        // and nothing else. A review of the first version of this guard found
        // its comment invoking the strict-shape doctrine while the code
        // tolerated arbitrary extra keywords (the dependency-kind clauses pass
        // through here only because the named-container branch intercepts them
        // first — an ordering accident this check no longer relies on).
        var memberCount = 0;
        foreach (var member in ifClause.EnumerateObject())
        {
            memberCount++;
            if (memberCount > 2 ||
                (!string.Equals(member.Name, "required", StringComparison.Ordinal) &&
                 !string.Equals(member.Name, "properties", StringComparison.Ordinal)))
            {
                return false;
            }
        }

        if (memberCount != 2)
        {
            return false;
        }

        if (!ifClause.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // Part two: 'properties' holds exactly one member, and it bears the
        // const. A second property — const-bearing or not — is outside the
        // contract shape and degrades.
        JsonProperty? conditionProperty = null;
        foreach (var property in properties.EnumerateObject())
        {
            if (conditionProperty is not null)
            {
                return false;
            }

            if (property.Value.ValueKind != JsonValueKind.Object || !property.Value.TryGetProperty("const", out _))
            {
                return false;
            }

            conditionProperty = property;
        }

        if (conditionProperty is not { } found)
        {
            return false;
        }

        // The doc contract for this method is the EXACT shape
        // {"required":["field"],"properties":{"field":{"const":...}}} — and the
        // 'required' half is load-bearing for the message's truth, not
        // decoration. Without 'required', the if-clause ALSO matches when the
        // field is ABSENT ('properties' is a no-op on a missing key), so the
        // then-branch's exclusion applies in the absent case too and
        // "not valid when 'field' is X" would be a fabricated half-truth.
        // Every shipped shape carries the required pair (dynamodb/s3
        // exists:false); a future fragment with an optional-const if-clause
        // must degrade to the generic message, not inherit a conditional it
        // does not have. Same strict-shape doctrine as
        // TryReadSoleRequiredNoOtherContent and EngineExport's group reader.
        if (!ifClause.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Array ||
            required.GetArrayLength() != 1)
        {
            return false;
        }

        var soleRequired = required[0];
        if (soleRequired.ValueKind != JsonValueKind.String ||
            !string.Equals(soleRequired.GetString(), found.Name, StringComparison.Ordinal))
        {
            return false;
        }

        var constElement = found.Value.GetProperty("const");
        fieldName = found.Name;
        constDisplay = constElement.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.String => $"'{constElement.GetString()}'",
            JsonValueKind.Number => constElement.GetRawText(),
            _ => constElement.GetRawText(),
        };

        return true;
    }

    /// <summary>
    /// True when <paramref name="ifClause"/> is shaped EXACTLY
    /// <c>{"required": ["field"]}</c> — a plain co-presence exclusion with NO
    /// other content (storage-assert.s3's <c>size</c>/<c>minSize</c> shape) —
    /// mirrors <c>EngineExport.TryReadSingleRequiredGroupNames</c>'s own
    /// "exactly one property, no other content" strictness (M1-style
    /// tightening applied here too, not only in the catalogue exporter).
    /// </summary>
    private static bool TryReadSoleRequiredNoOtherContent(JsonElement ifClause, out string fieldName)
    {
        fieldName = string.Empty;

        var propertyCount = 0;
        JsonElement? requiredArray = null;
        foreach (var property in ifClause.EnumerateObject())
        {
            propertyCount++;
            if (propertyCount > 1 || !string.Equals(property.Name, "required", StringComparison.Ordinal))
            {
                return false;
            }

            requiredArray = property.Value;
        }

        if (requiredArray is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() != 1)
        {
            return false;
        }

        var only = array[0];
        if (only.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(only.GetString()))
        {
            return false;
        }

        fieldName = only.GetString()!;
        return true;
    }

    /// <summary>
    /// Builds the actionable message for a <c>propertyNames</c>
    /// reserved-prefix rejection (MINOR-7): names the offending key and
    /// explains WHY, generically — never hardcoding the actual prefix list
    /// (that authoritative list lives in VarKeys and the schema's own
    /// 'description'; hardcoding it a third time here is exactly the
    /// drift risk the class remarks warn against elsewhere in this file).
    /// </summary>
    private static string FormatReservedPrefixError(string instanceLocation)
    {
        var propertyName = LastPointerSegment(instanceLocation);

        return $"[pattern] Property name '{propertyName}' begins with an engine-reserved bookkeeping " +
               "prefix — see this field's own description for the reserved prefix list.";
    }

    /// <summary>
    /// Builds the actionable message for a <c>oneOf</c> that failed via
    /// "too many matches" (MINOR-8) — e.g. script.csharp's <c>code</c>/
    /// <c>file</c> both set. Named generically from
    /// <paramref name="matchedFieldNames"/> (each matching branch's own
    /// <c>required</c> member, read from the live schema — see
    /// <see cref="TryReadRequiredFieldNames"/>).
    /// </summary>
    /// <remarks>
    /// Callers MUST only reach this when <paramref name="matchedFieldNames"/>
    /// holds exactly one name per matching branch — true if and only if
    /// <c>!HasUnattributableBranch &amp;&amp; ValidBranchFieldNames.Count ==
    /// ValidBranchCount</c> (M1/M1-r), checked once in
    /// <see cref="CollectErrors"/>, not re-verified here. Count-equality
    /// ALONE (M1) is necessary but not sufficient: a branch with no
    /// <c>required</c> member, or one whose <c>required</c> lists more than
    /// one name, can still leave the running total coincidentally equal to
    /// the branch count (M1-r's measured counter-example: a one-name branch
    /// + a two-name branch + a zero-name branch summing to the same total
    /// as three matches) while the per-branch correspondence this method
    /// depends on is broken. <see cref="CompositeGroupState.HasUnattributableBranch"/>
    /// is set the instant any branch breaks that correspondence, closing
    /// the gap; a caller whose group sets it must instead use
    /// <see cref="FormatUnnamedTooManyOneOfMatchesError"/> — see the class
    /// remarks' "Third-round gatekeeper findings" for the measured shapes
    /// this guard exists to catch.
    /// </remarks>
    private static string FormatTooManyOneOfMatchesError(List<string> matchedFieldNames)
    {
        var quotedNames = string.Join(", ", matchedFieldNames.Select(n => $"'{n}'"));

        return matchedFieldNames.Count == 2
            ? $"[oneOf] Exactly one of {quotedNames} may be set — both are present."
            : $"[oneOf] Exactly one of {quotedNames} may be set — " +
              $"{matchedFieldNames.Count.ToString(CultureInfo.InvariantCulture)} are present.";
    }

    /// <summary>
    /// Builds the honest fallback for a <c>oneOf</c> "too many matches"
    /// failure (M1) when the matching branches' own <c>required</c> names
    /// cannot be safely attributed one-for-one to the branches that matched
    /// (see <see cref="FormatTooManyOneOfMatchesError"/>'s remarks for the
    /// guard). Names no field and fabricates nothing beyond the count of
    /// alternative forms that matched — true regardless of how any
    /// individual branch is shaped, so this is always safe to emit.
    /// </summary>
    private static string FormatUnnamedTooManyOneOfMatchesError(int matchedBranchCount) =>
        $"[oneOf] {matchedBranchCount.ToString(CultureInfo.InvariantCulture)} of the alternative " +
        "forms declared here are satisfied — exactly one may be.";

    /// <summary>
    /// The maximum number of accepted values <see cref="FormatEnumError"/>
    /// lists inline before truncating with a "… and N more" tail. Chosen so
    /// the real 13-member dependency-<c>type</c> enum (the largest in the
    /// current schema) exercises truncation, proving the cap is live rather
    /// than merely theoretical.
    /// </summary>
    private const int MaxListedEnumValues = 8;

    /// <summary>
    /// Builds the actionable message for an <c>enum</c> rejection: names the
    /// offending value, lists the accepted values (truncated past
    /// <see cref="MaxListedEnumValues"/>), and — when the offending value is a
    /// case-insensitive match for exactly one accepted value — says so
    /// directly, e.g. <c>[enum] Value 'Postgres' is not one of the accepted
    /// values for 'type': postgres, sqlserver, … — write 'postgres'</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Generic by construction: the accepted-values list is read from the
    /// LIVE composed schema via <paramref name="schemaLocation"/> (see
    /// <see cref="TryReadEnumArray"/>), never hardcoded, so this one
    /// mechanism enriches every enum in the language — dependency
    /// <c>type</c>, <c>imagePullPolicy</c> (environment- and service-level),
    /// step <c>verifyMode</c>, and cache-assert.redis's <c>operation</c> —
    /// without knowing any of their vocabularies in advance, and stays
    /// correct if a provider ever adds or removes a member.
    /// </para>
    /// <para>
    /// Degrades to a generic (but still <c>[enum]</c>-tagged) message,
    /// exactly the no-fabrication discipline every other formatter in this
    /// class follows, when either the offending value or the accepted list
    /// cannot be resolved (no <paramref name="instance"/>/<paramref name="schema"/>
    /// supplied, or an exotic pointer shape this best-effort walk cannot
    /// follow) — never a guess.
    /// </para>
    /// </remarks>
    private static string FormatEnumError(
        string instanceLocation, JsonElement? instance, JsonElement? schema, Uri schemaLocation)
    {
        var offendingValue = instance is { } instanceRoot
            ? TryReadScalarText(instanceLocation, instanceRoot)
            : null;
        var allowedValues = schema is { } schemaRoot
            ? TryReadEnumArray(schemaRoot, schemaLocation)
            : null;

        if (offendingValue is null || allowedValues is null || allowedValues.Count == 0)
        {
            return "[enum] Value does not match any of the values accepted here.";
        }

        var propertyName = LastPointerSegment(instanceLocation);
        var allowedList = FormatAllowedValuesList(allowedValues);

        // Uniqueness, not merely existence (MINOR-6): the suggestion below
        // claims to name THE correct spelling, so it must never be offered
        // when two-or-more accepted values case-insensitively match the
        // offending value — an arbitrary pick (the original FirstOrDefault)
        // would name a value the author may not have meant at all. Computed
        // against the FULL, untruncated value — never the display-bounded
        // one below — so a long offending value's truncation can never
        // change which (if any) match is found.
        var caseInsensitiveMatches = allowedValues.Where(v =>
            !string.Equals(v, offendingValue, StringComparison.Ordinal) &&
            string.Equals(v, offendingValue, StringComparison.OrdinalIgnoreCase)).ToList();

        var suggestion = caseInsensitiveMatches.Count == 1
            ? $" — write '{caseInsensitiveMatches[0]}'"
            : string.Empty;

        return $"[enum] Value '{TruncateForDisplay(offendingValue)}' is not one of the accepted values for " +
               $"'{propertyName}': {allowedList}{suggestion}";
    }

    /// <summary>
    /// The maximum number of characters of an author-controlled value this class
    /// echoes verbatim into a diagnostic before <see cref="TruncateForDisplay"/>
    /// truncates it.
    /// </summary>
    /// <remarks>
    /// Security finding (feat/close-remaining-surfaces, second round): unlike
    /// <see cref="FormatAllowedValuesList"/> (bounded by
    /// <see cref="MaxListedEnumValues"/>, a fixed, schema-controlled count),
    /// the OFFENDING value is author/attacker-controlled document content
    /// with no length bound of its own — a multi-kilobyte string in an enum
    /// position previously inflated <see cref="SchemaValidationError.Message"/>
    /// without limit, and that message is serialised verbatim into
    /// <c>vouchfx validate --json</c>'s golden-pinned
    /// <c>ValidateJsonDiagnostic(Stage, Message)</c> document (and written to
    /// stdout). Bounded here to keep every error message O(1) in the size of
    /// the offending value,
    /// mirroring the discipline <see cref="FormatAllowedValuesList"/> already
    /// applies to the accepted side.
    /// </remarks>
    private const int MaxOffendingValueChars = 200;

    /// <summary>
    /// Truncates <paramref name="value"/> to <see cref="MaxOffendingValueChars"/>
    /// with a "… (N chars total)" tail when it exceeds that bound, returning
    /// it unchanged otherwise. Never splits a UTF-16 surrogate pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One bound, one reason, recorded HERE rather than restated at each call site. Two classes
    /// of value pass through it. The first is the offending SCALAR of an <c>enum</c> or
    /// <c>const</c> rejection, for the serialisation reason on
    /// <see cref="MaxOffendingValueChars"/>. The second is the author's own YAML map keys that
    /// <see cref="FormatAdditionalPropertiesError"/> and
    /// <see cref="FormatForbiddenPropertyError"/> name back to them — a service or dependency
    /// key, a dependency's declared <c>type</c>, a capture variable, the rejected property
    /// itself. Those keys carry no length limit of their own, so one pasted at kilobyte scale
    /// turns a one-line rejection into a screenful and buries the sentence saying what to do.
    /// For that second class the bound is LEGIBILITY and nothing more: the value is the author's
    /// own key rendered back to the author, on the author's own terminal, through the same sinks
    /// every other diagnostic in this class already reaches. Anyone tempted to write a stronger
    /// justification at a call site should put it here instead, or not at all: the
    /// <see cref="FormatForbiddenPropertyError"/> <c>security</c> branch carried this reasoning
    /// alone, in a comment whose closing sentence scoped it to that one branch and deferred
    /// widening to "a separate change" — and nothing propagated it until this became that change.
    /// Not every interpolation in this class goes through this bound yet; grep before assuming
    /// a given message is O(1) in the value it complains about.
    /// </para>
    /// <para>
    /// SEC-2 (peer review, fix round 3): the cut index is a CHAR index, so a naive
    /// <c>value[..MaxOffendingValueChars]</c> slice can land exactly between a high surrogate
    /// and its low-surrogate partner when the author-controlled value contains an astral-plane
    /// character — measured with <c>ESC + 198×'a' + U+1F600</c> (length 201), where the cut fell
    /// between the two halves and <c>System.Text.Json</c> wrote U+FFFD. A lone surrogate is
    /// invalid UTF-16 and can throw when later serialised or transcoded, which would turn a
    /// length-limit safeguard into a new crash vector. Backing off mirrors
    /// <c>SuiteSetLoader.TruncateParseErrorMessage</c>, which already made exactly this fix for
    /// the planner's own bounded field — the same idiom, deliberately, so the two do not drift.
    /// The loop (rather than a single decrement) also survives malformed input carrying
    /// consecutive unpaired high surrogates.
    /// </para>
    /// </remarks>
    private static string TruncateForDisplay(string value)
    {
        if (value.Length <= MaxOffendingValueChars)
        {
            return value;
        }

        var cut = MaxOffendingValueChars;
        while (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        var totalChars = value.Length.ToString(CultureInfo.InvariantCulture);
        return $"{value[..cut]}… ({totalChars} chars total)";
    }

    /// <summary>
    /// Renders an accepted-values list for <see cref="FormatEnumError"/>,
    /// truncating past <see cref="MaxListedEnumValues"/> with a "… and N
    /// more" tail rather than dumping an arbitrarily long enum inline.
    /// </summary>
    private static string FormatAllowedValuesList(List<string> allowedValues)
    {
        if (allowedValues.Count <= MaxListedEnumValues)
        {
            return string.Join(", ", allowedValues);
        }

        var shown = string.Join(", ", allowedValues.Take(MaxListedEnumValues));
        var remaining = (allowedValues.Count - MaxListedEnumValues).ToString(CultureInfo.InvariantCulture);
        return $"{shown}, … and {remaining} more";
    }

    /// <summary>
    /// Builds the actionable message for a <c>const</c> rejection (m4) —
    /// e.g. <c>metadata.schemaVersion: v2</c>, previously surfacing raw
    /// JsonSchema.Net library text with literal Unicode escape sequences
    /// around the expected value rather than an actual quote mark: measured
    /// verbatim (see <c>RootSchemaTests.Validate_MetadataSchemaVersionV2_IsRejected</c>)
    /// as the six characters backslash-u-0-0-2-2 on each side of 'v1'.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The composed schema has TWO user-facing <c>const</c> shapes (m8 fix, fix round 2
    /// corrects this remark — it previously claimed exactly one; that was already wrong
    /// the day <c>$defs/service</c>'s ports-only conditional shipped, and the second
    /// branch below closes the gap): <c>metadata.schemaVersion</c>, and
    /// <c>environment.services.&lt;name&gt;.healthCheck.type</c>'s conditional pin to
    /// <c>"tcp"</c> on a ports-only service (no sibling <c>httpPort</c> — a <c>type: http</c>
    /// probe has no HTTP endpoint to target there). Both are named directly below, mirroring
    /// <see cref="FormatForbiddenPropertyError"/>'s own fixed-rule-vs-generic-fallback split
    /// (see that method's remarks for the same reasoning applied to <c>image</c>/<c>project</c>).
    /// A wrong-CASE value at the healthCheck.type location (e.g. <c>type: TCP</c>) ALSO fails
    /// the field's own unconditional <c>enum</c> at the identical location; that redundant
    /// pairing is suppressed upstream by <see cref="SuppressRedundantConstWhenEnumPresent"/>
    /// before this method is ever reached for it, so this method only ever sees a
    /// healthCheck.type <c>const</c> that has no enum sibling — a valid enum member (e.g.
    /// <c>"http"</c>) failing the narrower ports-only conditional.
    /// </para>
    /// <para>
    /// H-B (peer review, held item — corrected in fix round 2, and the correction is now
    /// self-enforcing). This paragraph used to assert that every OTHER <c>const</c> in the
    /// composed schema belongs to a discriminator clause "which <see cref="IsIfDiscriminatorNoise"/>
    /// filters out before this method is ever reached". That was false while REQ-021's own
    /// narrowing pinned <c>security.profile</c> to <c>const: "tls"</c>: that <c>const</c> sat in
    /// a <c>then</c>, not an <c>if</c>, so it was NOT filtered and did reach this method — which
    /// is exactly why this method carried a third, <c>security.profile</c>-specific branch.
    /// M1's schema tightening deleted that clause (a non-kafka dependency now rejects the whole
    /// <c>security</c> block via a boolean <c>false</c> subschema, a <c>properties</c> shape
    /// handled by <see cref="FormatForbiddenPropertyError"/>, never a <c>const</c>), and the
    /// branch went with it. The claim is therefore true again TODAY — measured, not assumed:
    /// <c>SchemaErrorCollectorTests.ComposedSchema_DeclaresNoThenClauseConst_OutsideTheTwoNamedShapes</c>
    /// walks the live composed schema and fails the moment a third <c>then</c>-clause
    /// <c>const</c> appears, so the next one to be added is caught here rather than silently
    /// inheriting the generic message.
    /// </para>
    /// <para>
    /// A future provider-authored <c>const</c> falls through to the generic branch below rather
    /// than borrowing either named case's specific advice.
    /// </para>
    /// <para>
    /// The expected value is still read from the LIVE composed schema via
    /// <paramref name="schemaLocation"/> (see <see cref="TryReadConstValue"/>),
    /// never hardcoded to <c>"v1"</c>/<c>"tcp"</c>, so both stay correct the day the engine
    /// recognises a 'v2', or the ports-only conditional's own required value ever changes.
    /// Degrades to a generic (but still <c>[const]</c>-tagged) message when either the
    /// offending or the expected value cannot be resolved — never a guess.
    /// </para>
    /// </remarks>
    private static string FormatConstError(
        string instanceLocation, JsonElement? instance, JsonElement? schema, Uri schemaLocation)
    {
        var offendingValue = instance is { } instanceRoot
            ? TryReadScalarText(instanceLocation, instanceRoot)
            : null;
        var expectedValue = schema is { } schemaRoot
            ? TryReadConstValue(schemaRoot, schemaLocation)
            : null;

        if (offendingValue is null || expectedValue is null)
        {
            return "[const] Value does not match the value expected here.";
        }

        if (LastPointerSegment(instanceLocation) == "schemaVersion")
        {
            return $"[const] '{TruncateForDisplay(offendingValue)}' is not a language schema version this " +
                   $"engine recognises — write '{expectedValue}', or omit the field.";
        }

        if (TryResolveHealthCheckTypeContainer(instanceLocation, out var serviceName))
        {
            return $"[const] Service '{serviceName}' declares 'healthCheck: {{ type: " +
                   $"{TruncateForDisplay(offendingValue)} }}', but its 'ports' declaration has no " +
                   "sibling 'httpPort' — no HTTP endpoint exists there for anything but a 'tcp' " +
                   $"probe to target. Add 'httpPort:' to expose one, or write 'healthCheck: {{ " +
                   $"type: {expectedValue} }}'.";
        }

        return $"[const] '{TruncateForDisplay(offendingValue)}' does not match the value required here: " +
               $"'{expectedValue}'.";
    }

    /// <summary>
    /// Builds the actionable message for a captureEntry <c>minProperties: 1</c>
    /// rejection (m6, the empty-mapping case — <c>capture: {{ x: {{}} }}</c>).
    /// Before this, the raw library default ("Value should have at least 1
    /// properties") named neither the field nor the choice an author must
    /// make. Names the accepted keys read from the LIVE schema's own
    /// sibling <c>properties</c> (see <see cref="TryReadSiblingPropertyNames"/>,
    /// generic, never hardcoded to 'jsonpath'/'xpath' specifically) —
    /// bringing the empty-mapping shape up to the same standard as
    /// <see cref="FormatForbiddenPropertyError"/>'s captureEntry branch (the
    /// both-set shape) for the same container.
    /// </summary>
    /// <remarks>
    /// <c>minProperties</c> appears exactly once in the composed root
    /// schema today (captureEntry's own), but this still confirms the
    /// pointer resolves to captureEntry's shape via
    /// <see cref="TryResolveCaptureEntryContainer"/> before offering the
    /// specific wording — degrading to the original library <paramref name="message"/>,
    /// merely re-tagged, for any other <c>minProperties</c> a future schema
    /// change might add elsewhere, exactly the no-fabrication discipline
    /// every other formatter in this class follows.
    /// </remarks>
    private static string FormatMinPropertiesError(
        string message, string instanceLocation, JsonElement? schema, Uri schemaLocation)
    {
        if (!TryResolveCaptureEntryContainer(instanceLocation, out var variableName))
        {
            return $"[minProperties] {message}";
        }

        var choices = schema is { } schemaRoot
            ? TryReadSiblingPropertyNames(schemaRoot, schemaLocation, excludePropertyName: null)
            : null;

        if (choices is not { Count: > 0 })
        {
            return $"[minProperties] {message}";
        }

        var quotedChoices = string.Join(", ", choices.Select(c => $"'{c}'"));
        return $"[minProperties] Capture entry '{variableName}' must set at least one of {quotedChoices} — " +
               "none is present (DSL §6.1).";
    }

    /// <summary>
    /// Reads the <c>enum</c> array a failing node's own <c>SchemaLocation</c>
    /// points at, from a parsed copy of the composed schema's JSON text.
    /// </summary>
    /// <remarks>
    /// <see cref="EvaluationResults.SchemaLocation"/>'s fragment is a JSON
    /// Pointer into the schema document, from ITS root, ending at the schema
    /// object that owns the failing <c>enum</c> keyword — verified
    /// empirically against JsonSchema.Net 9.2.1, including across a
    /// <c>$ref</c>/<c>$defs</c>/<c>additionalProperties</c> indirection chain
    /// (e.g. <c>environment.dependencies.&lt;name&gt;.type</c>, reached via
    /// <c>$ref</c>): the pointer always resolves against the composed
    /// schema's OWN root, never a separately-registered sub-resource, so a
    /// single parsed copy of the composed schema text is sufficient for
    /// every enum in the language, including one nested inside a provider's
    /// own spliced fragment (e.g. cache-assert.redis's <c>operation</c>).
    /// </remarks>
    private static List<string>? TryReadEnumArray(JsonElement schemaRoot, Uri schemaLocation)
    {
        var fragment = schemaLocation.Fragment;
        if (fragment.Length == 0)
        {
            return null;
        }

        var pointer = Uri.UnescapeDataString(fragment.TrimStart('#'));

        if (!TryResolvePointer(schemaRoot, pointer, out var schemaNode) ||
            schemaNode.ValueKind != JsonValueKind.Object ||
            !schemaNode.TryGetProperty("enum", out var enumArray) ||
            enumArray.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<string>(enumArray.GetArrayLength());
        foreach (var item in enumArray.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
            {
                values.Add(s);
            }
        }

        return values.Count > 0 ? values : null;
    }

    /// <summary>
    /// Reads the <c>const</c> value a failing node's own <c>SchemaLocation</c>
    /// points at, from a parsed copy of the composed schema's JSON text — the
    /// <c>const</c> analogue of <see cref="TryReadEnumArray"/>, same pointer
    /// resolution, same schema-is-the-only-source-of-truth discipline (m4).
    /// Only a string-typed <c>const</c> is read — the language's one
    /// user-facing <c>const</c> today, <c>metadata.schemaVersion</c>,
    /// constrains a string; a non-string <c>const</c> degrades to
    /// <see langword="null"/> rather than guessing at a display form.
    /// </summary>
    private static string? TryReadConstValue(JsonElement schemaRoot, Uri schemaLocation)
    {
        var fragment = schemaLocation.Fragment;
        if (fragment.Length == 0)
        {
            return null;
        }

        var pointer = Uri.UnescapeDataString(fragment.TrimStart('#'));

        if (!TryResolvePointer(schemaRoot, pointer, out var schemaNode) ||
            schemaNode.ValueKind != JsonValueKind.Object ||
            !schemaNode.TryGetProperty("const", out var constNode) ||
            constNode.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return constNode.GetString();
    }

    /// <summary>
    /// Reads a container schema's own sibling <c>properties</c> names (m6),
    /// optionally excluding one — used both by
    /// <see cref="FormatForbiddenPropertyError"/>'s captureEntry branch
    /// (exclude the offending property, name the other one that makes it
    /// forbidden) and <see cref="FormatMinPropertiesError"/> (exclude
    /// nothing, list every accepted choice).
    /// </summary>
    /// <remarks>
    /// The failing node's own <c>SchemaLocation</c> points at different
    /// places for the two callers: <see cref="FormatMinPropertiesError"/>'s
    /// <c>minProperties</c> keyword is owned directly by the container
    /// schema itself (e.g. <c>#/$defs/captureEntry</c>), while
    /// <see cref="FormatForbiddenPropertyError"/>'s per-field <c>false</c>
    /// subschema lives several segments DEEPER, inside the mutual-exclusion
    /// <c>allOf/&lt;N&gt;/then/.../properties/&lt;name&gt;</c> branch (with a
    /// JsonSchema.Net synthetic array index after <c>then</c> — see the
    /// class remarks on <c>if</c>/<c>then</c>/<c>else</c>). Both are handled
    /// uniformly by truncating the pointer at its FIRST <c>/allOf/</c>
    /// segment when one is present (recovering the container definition the
    /// <c>allOf</c> branch hangs off) and using the pointer as-is otherwise
    /// — verified empirically against both real shapes (see
    /// SchemaErrorCollectorTests' capture-entry diagnostics) rather than
    /// assumed from the schema text alone.
    /// </remarks>
    private static List<string>? TryReadSiblingPropertyNames(
        JsonElement schemaRoot, Uri schemaLocation, string? excludePropertyName)
    {
        var fragment = schemaLocation.Fragment;
        if (fragment.Length == 0)
        {
            return null;
        }

        var pointer = Uri.UnescapeDataString(fragment.TrimStart('#'));
        var allOfIndex = pointer.IndexOf("/allOf/", StringComparison.Ordinal);
        var containerPointer = allOfIndex < 0 ? pointer : pointer[..allOfIndex];

        if (!TryResolvePointer(schemaRoot, containerPointer, out var containerNode) ||
            containerNode.ValueKind != JsonValueKind.Object ||
            !containerNode.TryGetProperty("properties", out var propertiesNode) ||
            propertiesNode.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var names = new List<string>();
        foreach (var property in propertiesNode.EnumerateObject())
        {
            if (excludePropertyName is null ||
                !string.Equals(property.Name, excludePropertyName, StringComparison.Ordinal))
            {
                names.Add(property.Name);
            }
        }

        return names.Count > 0 ? names : null;
    }

    /// <summary>
    /// Reads the <c>required</c> array of the schema object a <c>oneOf</c>
    /// BRANCH's own <c>SchemaLocation</c> points at directly (unlike
    /// <see cref="TryReadEnumArray"/>, no further descent is needed — a
    /// branch root's SchemaLocation already IS that branch's own schema
    /// object, e.g. <c>{"required":["code"]}</c>) — MINOR-8's too-many-
    /// matches synthesis, so the field name(s) named in the synthesised
    /// message are read from the LIVE schema rather than hardcoded to any
    /// one provider's own vocabulary (e.g. script.csharp's 'code'/'file').
    /// </summary>
    private static List<string>? TryReadRequiredFieldNames(JsonElement schemaRoot, Uri schemaLocation)
    {
        var fragment = schemaLocation.Fragment;
        if (fragment.Length == 0)
        {
            return null;
        }

        var pointer = Uri.UnescapeDataString(fragment.TrimStart('#'));

        if (!TryResolvePointer(schemaRoot, pointer, out var branchSchemaNode) ||
            branchSchemaNode.ValueKind != JsonValueKind.Object ||
            !branchSchemaNode.TryGetProperty("required", out var requiredArray) ||
            requiredArray.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = new List<string>(requiredArray.GetArrayLength());
        foreach (var item in requiredArray.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
            {
                names.Add(s);
            }
        }

        return names.Count > 0 ? names : null;
    }

    /// <summary>
    /// Reads the raw textual form of the scalar at <paramref name="pointer"/>
    /// within <paramref name="root"/> — the offending value <see cref="FormatEnumError"/>
    /// names. Every enum in the current schema constrains a string, but this
    /// degrades gracefully (rather than throwing) for any other JSON scalar
    /// kind, and returns <see langword="null"/> for a non-scalar or an
    /// unresolvable pointer — never fabricated.
    /// </summary>
    private static string? TryReadScalarText(string pointer, JsonElement root)
    {
        if (!TryResolvePointer(root, pointer, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    /// <summary>
    /// Walks EVERY segment of a JSON Pointer (RFC 6901) against
    /// <paramref name="root"/>, unlike <see cref="TryResolveContainerType"/>
    /// (which deliberately stops one segment short). Returns
    /// <see langword="false"/> at the first sign the walk cannot proceed (an
    /// out-of-range index, a missing key, a non-container node) rather than
    /// throwing — shared, generic navigation for both
    /// <see cref="TryReadScalarText"/> (over the document instance) and
    /// <see cref="TryReadEnumArray"/> (over the schema text).
    /// </summary>
    /// <remarks>
    /// <b>Tolerates a JsonSchema.Net 9.2.1 SchemaLocation quirk (verified
    /// empirically):</b> a single-schema applicator keyword — <c>if</c>,
    /// <c>then</c>, <c>else</c> — is represented internally as if it held a
    /// LIST of subschemas, even though its JSON text is a bare object, not an
    /// array. SchemaLocation reflects that internal shape: a keyword nested
    /// inside a provider's <c>then</c> branch (e.g. cache-assert.redis's
    /// <c>operation</c> enum) resolves to a fragment like
    /// <c>.../allOf/0/then/0/properties/operation</c> — note the synthetic
    /// <c>/0</c> directly after <c>then</c>, which does not exist anywhere in
    /// the actual composed schema JSON (there is no array to index; <c>then</c>
    /// maps straight to the fragment object). When the current node is an
    /// OBJECT and a bare-digit segment does not exist as a literal property on
    /// it, this is that synthetic index — skipped, staying on the same node,
    /// rather than failing the walk over an artefact of the internal model
    /// that was never written into the schema text. A genuinely array-valued
    /// keyword (<c>allOf</c>/<c>anyOf</c>/<c>oneOf</c>) is unaffected: its
    /// node's <c>ValueKind</c> is <c>Array</c>, so it is always handled by the
    /// branch below instead, which indexes for real.
    /// </remarks>
    private static bool TryResolvePointer(JsonElement root, string pointer, out JsonElement result)
    {
        var current = root;

        foreach (var rawSegment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = DecodePointerSegment(rawSegment);

            if (current.ValueKind == JsonValueKind.Object)
            {
                if (current.TryGetProperty(segment, out var next))
                {
                    current = next;
                    continue;
                }

                if (int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    // Synthetic if/then/else index (see remarks) — not a real
                    // property; skip it and stay on the current node.
                    continue;
                }

                result = default;
                return false;
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                    index < 0 || index >= current.GetArrayLength())
                {
                    result = default;
                    return false;
                }

                current = current[index];
            }
            else
            {
                result = default;
                return false;
            }
        }

        result = current;
        return true;
    }

    /// <summary>The container-kind label <see cref="FormatForbiddenPropertyError"/> and <see cref="FormatAdditionalPropertiesError"/> use for a service.</summary>
    private const string ServiceContainerKind = "service";

    /// <summary>The container-kind label <see cref="FormatForbiddenPropertyError"/> and <see cref="FormatAdditionalPropertiesError"/> use for a dependency.</summary>
    private const string DependencyContainerKind = "dependency";

    /// <summary>
    /// Recognises the pointer shape
    /// <c>/environment/(services|dependencies)/&lt;name&gt;/&lt;property&gt;</c>
    /// in <paramref name="instanceLocation"/> and extracts the container's
    /// kind (<see cref="ServiceContainerKind"/> or
    /// <see cref="DependencyContainerKind"/>) and its own map key — the two
    /// blank-keyword-producing surfaces this class can name with confidence
    /// (see the class remarks). Pointer-only, deliberately: a service or
    /// dependency's own name is the YAML map key an author wrote, directly
    /// present in the pointer, never something that needs reading back out
    /// of the instance the way a dependency's <c>type</c> does.
    /// </summary>
    /// <param name="instanceLocation">The failing node's own instance-location pointer.</param>
    /// <param name="containerKind">
    /// Set to <see cref="ServiceContainerKind"/> or <see cref="DependencyContainerKind"/>
    /// on a successful resolve; <see cref="string.Empty"/> otherwise.
    /// </param>
    /// <param name="containerName">
    /// Set to the resolved container's own map key on a successful resolve;
    /// <see cref="string.Empty"/> otherwise.
    /// </param>
    /// <param name="isNestedBelowSecurity">
    /// Set <see langword="true"/> when the resolved pointer sits BELOW the security
    /// object itself — specifically, inside one of its <c>serverArtifacts[]</c> entries
    /// (<c>.../security/serverArtifacts/&lt;N&gt;</c> or deeper) — and
    /// <see langword="false"/> for every other successful resolve, including the
    /// security object itself (<c>.../security</c>, depth 4) and an unrecognised key
    /// directly inside it (<c>.../security/&lt;bogus&gt;</c>, depth 5): both of those are
    /// still accurately described as "on" the dependency/service, since the security
    /// block IS a property of that container (MINOR-6, honest nested attribution — see
    /// <see cref="FormatError"/>'s own <c>required</c> branch and
    /// <see cref="FormatAdditionalPropertiesError"/> for how callers use this to choose
    /// between "on &lt;kind&gt; '&lt;name&gt;'" and "in &lt;kind&gt; '&lt;name&gt;' (at
    /// &lt;subpath&gt;)"). <see langword="false"/> whenever this method returns
    /// <see langword="false"/> too.
    /// </param>
    /// <param name="allowNestedSecurity">
    /// When <see langword="true"/>, ALSO resolves a pointer AT OR BELOW that same
    /// dependency's/service's own <c>security</c> sub-object — i.e.
    /// <c>/environment/(services|dependencies)/&lt;name&gt;/security</c> itself (already
    /// matched even when this is <see langword="false"/>, being exactly depth 4) or
    /// anything nested under it, such as an unrecognised key directly inside the block
    /// (<c>.../security/&lt;bogus&gt;</c>) or inside one of its own nested
    /// <c>serverArtifacts[]</c> entries (<c>.../security/serverArtifacts/0/&lt;bogus&gt;</c>).
    /// Defaults to <see langword="false"/> so every EXISTING caller — in particular
    /// <see cref="FormatForbiddenPropertyError"/>, whose own generic "sibling if-clause"
    /// branch already names WHY a security field is forbidden (e.g. "is not valid when
    /// 'profile' is 'tls'") for exactly this nested shape — keeps its current behaviour
    /// unchanged; only <see cref="FormatAdditionalPropertiesError"/> and
    /// <see cref="FormatError"/>'s own <c>required</c> branch opt in.
    /// </param>
    private static bool TryResolveEnvironmentContainer(
        string instanceLocation, out string containerKind, out string containerName,
        out bool isNestedBelowSecurity, bool allowNestedSecurity = false)
    {
        var segments = instanceLocation.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var isDirectField = segments.Length == 4;
        var isNestedSecurityField = allowNestedSecurity && segments.Length > 4 && segments[3] == "security";

        if ((isDirectField || isNestedSecurityField) && segments[0] == "environment" &&
            (segments[1] == "services" || segments[1] == "dependencies"))
        {
            containerKind = segments[1] == "services" ? ServiceContainerKind : DependencyContainerKind;
            containerName = DecodePointerSegment(segments[2]);
            // segments[4] is guarded by the &&'s short-circuit ORDERING, not
            // unconditionally safe: isNestedSecurityField, evaluated first, guarantees
            // segments.Length > 4 — reordering the two operands would throw for a
            // length-4 pointer (the security object itself).
            isNestedBelowSecurity = isNestedSecurityField && segments[4] == "serverArtifacts";
            return true;
        }

        containerKind = string.Empty;
        containerName = string.Empty;
        isNestedBelowSecurity = false;
        return false;
    }

    /// <summary>
    /// Recognises the pointer shape <c>/environment/services/&lt;name&gt;/healthCheck</c> —
    /// exactly depth 4, SERVICE-only (see the Authoring model's own <c>HealthCheckSpec</c>
    /// remarks: a dependency's health gate is the engine's own concern, never
    /// author-declarable, so <c>healthCheck</c> never appears under
    /// <c>environment.dependencies</c>) — and extracts the owning service's own map key
    /// (G1, gatekeeper MAJOR-1).
    /// </summary>
    /// <remarks>
    /// Checked BEFORE <see cref="TryResolveEnvironmentContainer"/> in <see cref="FormatError"/>'s
    /// own <c>required</c> branch. Without this, a healthCheck-level <c>required</c> violation
    /// (a missing <c>type</c>, or a <c>type: tcp</c> healthCheck missing its conditionally
    /// required <c>port</c>) is INDISTINGUISHABLE, by depth alone, from the service's OWN
    /// <c>security</c> block reaching the identical depth 4 — producing the misleading
    /// "Required properties [&quot;type&quot;] are not present on service '&lt;name&gt;'",
    /// which both names the wrong object (the healthCheck block, not the service itself) and
    /// invites confusion with a DEPENDENCY's own required <c>type</c> field (an entirely
    /// different, sibling concept). <c>required</c> is the only keyword this needs to cover:
    /// every OTHER healthCheck rejection (an unrecognised key, a forbidden <c>port</c>/<c>path</c>,
    /// a bad <c>type</c> enum value) already lands one segment deeper
    /// (<c>.../healthCheck/&lt;field&gt;</c>) where the existing additionalProperties/properties/
    /// enum formatters resolve correctly without needing this helper at all.
    /// </remarks>
    /// <param name="instanceLocation">The failing node's own instance-location pointer.</param>
    /// <param name="containerName">
    /// Set to the resolved service's own map key on a successful resolve;
    /// <see cref="string.Empty"/> otherwise.
    /// </param>
    private static bool TryResolveHealthCheckContainer(string instanceLocation, out string containerName)
    {
        var segments = instanceLocation.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 4 &&
            segments[0] == "environment" &&
            segments[1] == "services" &&
            segments[3] == "healthCheck")
        {
            containerName = DecodePointerSegment(segments[2]);
            return true;
        }

        containerName = string.Empty;
        return false;
    }

    /// <summary>
    /// Resolves <c>environment/services/&lt;name&gt;/healthCheck/type</c> — ONE segment
    /// deeper than <see cref="TryResolveHealthCheckContainer"/>'s own <c>healthCheck</c>
    /// container match — for <see cref="FormatConstError"/>'s ports-only-conditional
    /// <c>healthCheck.type</c> branch (m8 fix, fix round 2). Every OTHER healthCheck field
    /// rejection at this depth (an unrecognised key, a forbidden <c>port</c>/<c>path</c>)
    /// already lands correctly via the additionalProperties/properties formatters and does
    /// not need this helper; only the <c>type</c> field's conditional <c>const</c> does.
    /// </summary>
    /// <param name="instanceLocation">The failing node's own instance-location pointer.</param>
    /// <param name="serviceName">
    /// Set to the resolved service's own map key on a successful resolve;
    /// <see cref="string.Empty"/> otherwise.
    /// </param>
    private static bool TryResolveHealthCheckTypeContainer(string instanceLocation, out string serviceName)
    {
        var segments = instanceLocation.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 5 &&
            segments[0] == "environment" &&
            segments[1] == "services" &&
            segments[3] == "healthCheck" &&
            segments[4] == "type")
        {
            serviceName = DecodePointerSegment(segments[2]);
            return true;
        }

        serviceName = string.Empty;
        return false;
    }

    /// <summary>
    /// Renders <paramref name="instanceLocation"/> from its <c>security</c> segment
    /// (index 3) onward as a dotted/bracketed sub-path (MINOR-6) — e.g.
    /// <c>/environment/dependencies/events-kafka/security/serverArtifacts/0</c> becomes
    /// <c>security.serverArtifacts[0]</c>, and
    /// <c>.../security/serverArtifacts/0/bogus</c> becomes
    /// <c>security.serverArtifacts[0].bogus</c>. Applied UNIFORMLY regardless of which
    /// keyword produced the failure: a <c>required</c> violation's own
    /// <c>instanceLocation</c> is always the CONTAINER missing the property (so the
    /// rendered sub-path naturally stops at the entry, e.g. <c>serverArtifacts[0]</c>,
    /// since the missing field itself never appears in the pointer), while an
    /// <c>additionalProperties</c> violation's own <c>instanceLocation</c> is always the
    /// offending property itself (so the sub-path naturally extends one segment further,
    /// e.g. <c>serverArtifacts[0].bogus</c>) — both are the SAME transformation applied
    /// to two structurally different pointer shapes, not two different rules.
    /// </summary>
    /// <remarks>
    /// A numeric segment renders as a bracketed index appended to the PRECEDING segment
    /// (no leading dot, e.g. <c>[0]</c>); every other segment is dot-joined and
    /// pointer-decoded (<see cref="DecodePointerSegment"/>), mirroring
    /// <see cref="LastPointerSegment"/>'s own decoding. Callers guarantee
    /// <paramref name="instanceLocation"/> has more than 4 segments with segment[3] ==
    /// <c>"security"</c> (see <see cref="TryResolveEnvironmentContainer"/>'s own
    /// <c>isNestedSecurityField</c> guard, which every caller of this method has already
    /// passed) — this method does not re-validate that shape.
    /// </remarks>
    private static string BuildSecuritySubPath(string instanceLocation)
    {
        var segments = instanceLocation.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var subPath = DecodePointerSegment(segments[3]);

        for (var i = 4; i < segments.Length; i++)
        {
            subPath = int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                ? $"{subPath}[{segments[i]}]"
                : $"{subPath}.{DecodePointerSegment(segments[i])}";
        }

        return subPath;
    }

    /// <summary>
    /// Recognises the pointer shape <c>/steps/&lt;N&gt;/capture/&lt;varName&gt;</c>
    /// — either the capture entry itself (the <c>minProperties</c> shape) or
    /// with one trailing property segment, e.g.
    /// <c>/steps/&lt;N&gt;/capture/&lt;varName&gt;/xpath</c> (the forbidden-
    /// property shape) — and extracts the capture variable's own map key
    /// (m6). Same pointer-only extraction principle as
    /// <see cref="TryResolveEnvironmentContainer"/>: a capture entry's
    /// variable name is the YAML map key an author wrote, directly present
    /// in the pointer, never something read back out of the instance.
    /// </summary>
    private static bool TryResolveCaptureEntryContainer(string instanceLocation, out string variableName)
    {
        var segments = instanceLocation.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length is 4 or 5 &&
            segments[0] == "steps" &&
            int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
            segments[2] == "capture")
        {
            variableName = DecodePointerSegment(segments[3]);
            return true;
        }

        variableName = string.Empty;
        return false;
    }

    /// <summary>
    /// Resolves the <c>type</c> string property of the object that directly
    /// contains the pointer's final segment — i.e. walks every segment except
    /// the last (the offending property itself) and reads <c>type</c> off
    /// whatever object that reaches. Returns <see langword="null"/> at the
    /// first sign the walk cannot proceed (an out-of-range index, a missing
    /// key, a non-container node) rather than throwing — this is a
    /// best-effort diagnostic enrichment, never a validation concern.
    /// </summary>
    private static string? TryResolveContainerType(string instanceLocation, JsonElement root)
    {
        var segments = instanceLocation.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        var current = root;

        // Walk every segment except the last: that final segment names the
        // unevaluated property itself, not a step down into it.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = DecodePointerSegment(segments[i]);

            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(segment, out var next))
                    return null;

                current = next;
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                // NumberStyles.None + InvariantCulture, matching TryGetStepScope: a JSON
                // Pointer array index is a bare run of digits, so a leading sign, embedded
                // whitespace or a culture-specific group separator is malformed, not a
                // number to be helpfully coerced. Bare int.TryParse accepts all three and
                // would silently resolve " 1", "+1" or "-1" to an element — different
                // behaviour from the sibling helper walking the same pointers.
                if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                    index < 0 || index >= current.GetArrayLength())
                    return null;

                current = current[index];
            }
            else
            {
                return null;
            }
        }

        if (current.ValueKind != JsonValueKind.Object)
            return null;

        return current.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;
    }

    /// <summary>
    /// The final <c>/</c>-separated segment of a JSON Pointer, RFC 6901-decoded
    /// (<c>~1</c> → <c>/</c>, <c>~0</c> → <c>~</c>, tilde-one first to avoid
    /// double-decoding). Returns the pointer unchanged if it carries no
    /// segments at all (defensive; every real <c>unevaluatedProperties</c>
    /// InstanceLocation names a property, so this never fires in practice).
    /// </summary>
    private static string LastPointerSegment(string pointer)
    {
        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? pointer : DecodePointerSegment(segments[^1]);
    }

    private static string DecodePointerSegment(string segment) =>
        segment.Replace("~1", "/", StringComparison.Ordinal)
               .Replace("~0", "~", StringComparison.Ordinal);

    /// <summary>
    /// True when the last <c>/</c>-separated segment of an evaluation path
    /// equals <paramref name="keyword"/> exactly — used to recognise the
    /// <c>unevaluatedProperties</c> shape from <see cref="EvaluationResults.EvaluationPath"/>
    /// even though the node's own (per-property) keyword is blank.
    /// </summary>
    private static bool EndsWithSegment(string evaluationPath, string keyword)
    {
        var segments = evaluationPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments[^1] == keyword;
    }

    /// <summary>
    /// Recognises an evaluation node that is only invalid because one of the
    /// composed schema's discriminator clauses' own <c>if</c> keyword did not
    /// match this step's <c>type</c> — see the class remarks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Such a node's <see cref="EvaluationResults.EvaluationPath"/> contains an
    /// <c>allOf/&lt;N&gt;/if</c> segment sequence (confirmed against real
    /// JsonSchema.Net 9.2.1 output — e.g.
    /// <c>.../allOf/3/if/properties/type</c>); a clause's genuine <c>then</c>
    /// failure always has <c>allOf/&lt;N&gt;/then/...</c> instead, and is never
    /// matched here. <see cref="YamlSchemaValidator"/>'s root-only schema (no
    /// provider <c>allOf</c> clauses) can never produce a path shaped like this,
    /// so sharing this check with it is a no-op there, not a behaviour change.
    /// </para>
    /// <para>
    /// The match is deliberately <b>depth-independent</b>: the loop scans every
    /// <c>allOf/&lt;N&gt;/if</c> triple anywhere in the path, not only at the
    /// top level. This is correct, not merely convenient, because of the JSON
    /// Schema 2020-12 semantics of <c>if</c>/<c>then</c>/<c>else</c>: an
    /// <c>if</c> subschema's own pass/fail is <em>never</em> diagnostic of a
    /// document defect at any nesting depth — it only gates whether the
    /// sibling <c>then</c> (or <c>else</c>) applies. Consequently, a node that
    /// carries a genuine error can only ever sit on a <c>then</c>-side (or
    /// <c>else</c>-side) path; a path segment sequence ending directly under an
    /// <c>if</c> is categorically noise, however deeply it is nested (e.g. a
    /// provider fragment that nests its own conditional <c>allOf</c> inside its
    /// <c>then</c> branch — see <c>SchemaErrorCollectorTests</c>'s end-to-end
    /// nested-provider proof).
    /// </para>
    /// <para>
    /// This is also why the check is safe against untrusted document content:
    /// <see cref="EvaluationResults.EvaluationPath"/> is derived entirely from
    /// the <em>schema's</em> own structure — which providers, and this
    /// composer, control — never from the instance being validated. Nothing an
    /// author writes in a <c>.e2e.yaml</c> document can steer a genuine error
    /// onto a path this filter treats as noise; the instance only ever
    /// influences <see cref="EvaluationResults.InstanceLocation"/>, a wholly
    /// separate pointer this method never inspects.
    /// </para>
    /// </remarks>
    /// <param name="evaluationPath">
    /// The evaluation node's <see cref="EvaluationResults.EvaluationPath"/>,
    /// rendered as its JSON Pointer string (e.g. <c>/allOf/3/if/properties/type</c>).
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the path names an <c>if</c> keyword directly
    /// under an <c>allOf</c> array index — i.e. this node is discriminator
    /// noise, not a real document defect.
    /// </returns>
    internal static bool IsIfDiscriminatorNoise(string evaluationPath)
    {
        var segments = evaluationPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i + 2 < segments.Length; i++)
        {
            if (segments[i] == "allOf" && segments[i + 2] == "if")
            {
                return true;
            }
        }

        return false;
    }
}
