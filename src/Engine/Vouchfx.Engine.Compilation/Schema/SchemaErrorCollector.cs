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
        // Added to 'collected' BEFORE both suppression passes run so this
        // genuine "other error" correctly participates in — and is itself
        // correctly exempt from — both: SuppressSatisfiedCompositeBranchNoise
        // never touches it (its EvaluationPath is the GROUP's own prefix,
        // e.g. '.../oneOf', with no trailing branch index, so
        // FindCompositeBranchOccurrences finds nothing to match against —
        // safe by construction, not merely because the group is unsatisfied
        // anyway), while SuppressUnevaluatedPropertiesCascade correctly DOES
        // drop the two misleading unevaluatedProperties entries once this
        // genuine error registers the step as having "another error".
        foreach (var state in groupStates.Values)
        {
            if (state.HasTooManyOneOfMatches && state.ValidBranchFieldNames is { Count: > 0 } fieldNames)
            {
                collected.Add(new CollectedError(
                    state.InstanceLocation,
                    state.Prefix,
                    FormatTooManyOneOfMatchesError(fieldNames),
                    IsUnevaluatedProperties: false));
            }
        }

        // Composite-branch suppression MUST run before the cascade below: a
        // step whose only "other error" was itself composite-branch noise
        // (e.g. script.csharp's oneOf, see the class remarks) must not have
        // its genuine unevaluatedProperties entry hidden by a phantom sibling
        // that is about to be dropped anyway.
        var afterCompositeSuppression = SuppressSatisfiedCompositeBranchNoise(collected, groupStates);
        var survivors = SuppressUnevaluatedPropertiesCascade(afterCompositeSuppression);

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
    private readonly record struct CollectedError(
        string InstanceLocation, string EvaluationPath, string Message, bool IsUnevaluatedProperties);

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
        /// see the class remarks — so this is the only source for one).
        /// </summary>
        public List<string>? ValidBranchFieldNames { get; set; }

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
                if (fieldNames is { Count: > 0 })
                {
                    state.ValidBranchFieldNames ??= new List<string>();
                    state.ValidBranchFieldNames.AddRange(fieldNames);
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
                    IsUnevaluatedPropertiesShape(keyword, evaluationPath)));
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
    /// per-field <c>properties/&lt;name&gt;: false</c>), enriching an
    /// <c>enum</c> violation with the offending value and the live accepted
    /// list (see <see cref="FormatEnumError"/>), and falling back to the
    /// original <c>[{keyword}] {message}</c> format for every other,
    /// genuinely-named keyword.
    /// </summary>
    private static string FormatError(
        string keyword, string message, string evaluationPath, string instanceLocation,
        JsonElement? instance, JsonElement? schema, Uri schemaLocation)
    {
        if (IsUnevaluatedPropertiesShape(keyword, evaluationPath))
        {
            return FormatUnevaluatedPropertiesError(instanceLocation, instance);
        }

        if (IsAdditionalPropertiesShape(keyword, evaluationPath))
        {
            return FormatAdditionalPropertiesError(instanceLocation);
        }

        if (IsForbiddenPropertyShape(keyword, evaluationPath))
        {
            return FormatForbiddenPropertyError(instanceLocation, instance);
        }

        if (keyword == "enum")
        {
            return FormatEnumError(instanceLocation, instance, schema, schemaLocation);
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
    /// <c>/environment/(services|dependencies)/&lt;name&gt;/&lt;property&gt;</c>
    /// (see <see cref="TryResolveEnvironmentContainer"/>), the container's own
    /// kind and map key — e.g. <c>Unknown property 'imagePullPolcy' on
    /// service 'orders-api'</c>. Every other unknown-key surface this shape
    /// covers (<c>metadata</c>, the document root, a <c>topics[]</c> item, a
    /// provider's own nested block) degrades to the bare property name rather
    /// than guessing a container that cannot be reliably named from the
    /// pointer alone — see the class remarks and
    /// <see cref="FormatUnevaluatedPropertiesError"/>'s own no-fabrication
    /// rule, which this mirrors.
    /// </summary>
    private static string FormatAdditionalPropertiesError(string instanceLocation)
    {
        var propertyName = LastPointerSegment(instanceLocation);

        return TryResolveEnvironmentContainer(instanceLocation, out var containerKind, out var containerName)
            ? $"[additionalProperties] Unknown property '{propertyName}' on {containerKind} '{containerName}'"
            : $"[additionalProperties] Unknown property '{propertyName}'";
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
    /// On a <c>service</c> container, the only per-field exclusion the
    /// schema declares is <c>project</c> forbidden once <c>image</c> is set
    /// (see $defs/service's own description) — a single, fixed, frozen rule,
    /// so it is named directly rather than generalised: the offending
    /// <c>then</c> branch is reachable only via a sibling
    /// <c>if: required:["image"]</c>, so 'image' being present is a schema
    /// invariant of this branch, not an assumption this method makes about
    /// the instance.
    /// </para>
    /// <para>
    /// Any future <c>properties/&lt;name&gt;: false</c> declaration outside
    /// both known containers (or a service-level exclusion other than
    /// 'project') degrades to naming the property alone — never fabricating
    /// a rule this method was not told about.
    /// </para>
    /// </remarks>
    private static string FormatForbiddenPropertyError(string instanceLocation, JsonElement? instance)
    {
        var propertyName = LastPointerSegment(instanceLocation);

        if (!TryResolveEnvironmentContainer(instanceLocation, out var containerKind, out var containerName))
        {
            return $"[properties] Property '{propertyName}' is not valid here";
        }

        if (containerKind == DependencyContainerKind)
        {
            var dependencyKind = instance is { } root ? TryResolveContainerType(instanceLocation, root) : null;

            return dependencyKind is null
                ? $"[properties] Property '{propertyName}' is not valid on dependency '{containerName}'"
                : $"[properties] Property '{propertyName}' is not valid on a '{dependencyKind}' dependency";
        }

        if (containerKind == ServiceContainerKind && propertyName == "project")
        {
            return $"[properties] Property 'project' cannot be combined with 'image' on service " +
                   $"'{containerName}' — exactly one of the two is required (DSL §3.2)";
        }

        return $"[properties] Property '{propertyName}' is not valid on {containerKind} '{containerName}'";
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
    /// <see cref="TryReadRequiredFieldNames"/>), so this reads correctly for
    /// ANY provider's alternative-field <c>oneOf</c>, not merely
    /// script.csharp specifically.
    /// </summary>
    private static string FormatTooManyOneOfMatchesError(List<string> matchedFieldNames)
    {
        var quotedNames = string.Join(", ", matchedFieldNames.Select(n => $"'{n}'"));

        return matchedFieldNames.Count == 2
            ? $"[oneOf] Exactly one of {quotedNames} may be set — both are present."
            : $"[oneOf] Exactly one of {quotedNames} may be set — " +
              $"{matchedFieldNames.Count.ToString(CultureInfo.InvariantCulture)} are present.";
    }

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
    /// The maximum number of characters of an offending SCALAR VALUE
    /// <see cref="FormatEnumError"/> echoes verbatim before truncating.
    /// </summary>
    /// <remarks>
    /// Security finding (feat/close-remaining-surfaces, second round): unlike
    /// <see cref="FormatAllowedValuesList"/> (bounded by
    /// <see cref="MaxListedEnumValues"/>, a fixed, schema-controlled count),
    /// the OFFENDING value is author/attacker-controlled document content
    /// with no length bound of its own — a multi-kilobyte string in an enum
    /// position previously inflated <see cref="SchemaValidationError.Message"/>
    /// without limit, and that message flows into the §14 JSON Lines event
    /// stream every renderer (and the Healer agent) consumes. Bounded here to
    /// keep every error message O(1) in the size of the offending value,
    /// mirroring the discipline <see cref="FormatAllowedValuesList"/> already
    /// applies to the accepted side.
    /// </remarks>
    private const int MaxOffendingValueChars = 200;

    /// <summary>
    /// Truncates <paramref name="value"/> to <see cref="MaxOffendingValueChars"/>
    /// with a "… (N chars total)" tail when it exceeds that bound, returning
    /// it unchanged otherwise.
    /// </summary>
    private static string TruncateForDisplay(string value)
    {
        if (value.Length <= MaxOffendingValueChars)
        {
            return value;
        }

        var totalChars = value.Length.ToString(CultureInfo.InvariantCulture);
        return $"{value[..MaxOffendingValueChars]}… ({totalChars} chars total)";
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
    private static bool TryResolveEnvironmentContainer(string instanceLocation, out string containerKind, out string containerName)
    {
        var segments = instanceLocation.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 4 && segments[0] == "environment" &&
            (segments[1] == "services" || segments[1] == "dependencies"))
        {
            containerKind = segments[1] == "services" ? ServiceContainerKind : DependencyContainerKind;
            containerName = DecodePointerSegment(segments[2]);
            return true;
        }

        containerKind = string.Empty;
        containerName = string.Empty;
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
