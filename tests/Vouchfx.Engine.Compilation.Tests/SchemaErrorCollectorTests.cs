// Issue #259 — direct unit tests for SchemaErrorCollector.IsIfDiscriminatorNoise,
// plus an end-to-end nested if/then proof that the noise filter is depth-independent
// (§8.2, §13.6).
//
// SchemaErrorCollectionAtScaleTests exercises the filter through the real
// 25-Core-provider composed schema, but only ever at the TOP level of the
// discriminator allOf (one level of nesting). This file complements it with:
//   • direct, white-box Theory coverage of IsIfDiscriminatorNoise's own
//     path-matching logic (the Compilation project's InternalsVisibleTo already
//     exposes it to this test assembly) — including shapes that never arise in
//     the real 25-provider schema (a SECOND level of nesting, and the boundary
//     "no i+2 segment available" cases) — so the matching rule itself is pinned
//     down independent of any particular schema's shape; and
//   • a black-box, end-to-end proof, via a tiny test-only [StepProvider] whose
//     own SchemaFragment nests an 'allOf' of if/then pairs INSIDE its 'then'
//     branch, that the filter is depth-independent through the REAL
//     SchemaComposer.Validate evaluation pipeline — not merely by construction
//     of a path string.
using System;
using System.Linq;
using System.Text.Json;
using Json.Schema;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.CacheAssert.Redis;
using Vouchfx.Steps.Script.Csharp;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Issue #259: direct and end-to-end coverage of
/// <see cref="SchemaErrorCollector.IsIfDiscriminatorNoise"/>. Also covers the
/// combined composite-branch-noise (<c>oneOf</c>/<c>anyOf</c>) and
/// <c>[enum]</c>-enrichment findings from the #337 peer review + gatekeeper
/// pass (feat/close-remaining-surfaces) — see SchemaErrorCollector's own
/// class remarks.
/// </summary>
public sealed class SchemaErrorCollectorTests
{
    // ── Composite-branch noise: COUNTING (not flagging) branch satisfaction ────
    //
    // A second-round gatekeeper finding (CRITICAL-1): the original fix marked
    // a oneOf/anyOf group "satisfied" the moment ANY branch was individually
    // valid. Correct for anyOf (>= 1 match IS satisfaction), but wrong for
    // oneOf, whose OWN semantics require EXACTLY one match — two or more
    // matching branches make the oneOf itself invalid, yet the old code
    // still flagged the group satisfied and suppressed a genuinely-failing
    // third branch, with NOTHING replacing it (JsonSchema.Net attaches no
    // message to a failing oneOf node itself — see the probes behind the
    // original fix). These drive SchemaErrorCollector.CollectErrors directly
    // against a synthetic schema — no provider fragment needed, and none of
    // today's two real composites (a 2-branch anyOf, a 2-branch oneOf) can
    // exercise a 3-way oneOf's "too many matches" case at all.

    private static readonly EvaluationOptions s_listOptions = new() { OutputFormat = OutputFormat.List };

    private static EvaluationResults Evaluate(string schemaJson, string instanceJson)
    {
        var schema = JsonSchema.FromText(schemaJson);
        using var doc = JsonDocument.Parse(instanceJson);
        // Clone the root element: 'doc' is disposed at the end of this method,
        // but EvaluationResults retains references into the underlying
        // JsonDocument buffer for InstanceLocation resolution in some
        // JsonSchema.Net code paths — cloning avoids a use-after-dispose.
        return schema.Evaluate(doc.RootElement.Clone(), s_listOptions);
    }

    /// <summary>
    /// Parses the same schema text <see cref="Evaluate"/> compiled, as the
    /// <c>JsonElement</c> form <see cref="SchemaErrorCollector.CollectErrors"/>'s
    /// <c>schema</c> parameter expects — required by any test that exercises
    /// <see cref="SchemaErrorCollector.FormatTooManyOneOfMatchesError"/>'s
    /// name resolution (<c>TryReadRequiredFieldNames</c> reads a matching
    /// branch's own <c>required</c> member from THIS tree, via the failing
    /// node's <c>SchemaLocation</c> pointer) since that resolution is a no-op
    /// whenever <c>schema</c> is <see langword="null"/> — exactly how
    /// production actually calls it (<c>SchemaComposer</c>/
    /// <c>YamlSchemaValidator</c> both thread the composed schema through).
    /// Cloned for the same use-after-dispose reason as <see cref="Evaluate"/>.
    /// </summary>
    private static JsonElement ParseSchemaElement(string schemaJson)
    {
        using var doc = JsonDocument.Parse(schemaJson);
        return doc.RootElement.Clone();
    }

    private const string ThreeBranchOneOfSchema = """
        {
          "type": "object",
          "oneOf": [
            { "required": ["a"] },
            { "required": ["b"] },
            { "required": ["c"] }
          ]
        }
        """;

    /// <summary>
    /// Two branches match ('a' and 'b' both present) — the oneOf's "exactly
    /// one" invariant is violated, so the composite is NOT satisfied. The
    /// third, genuinely-failing branch ('c' required, absent) must survive:
    /// with the pre-fix code, both matching branches independently flagged
    /// the group "satisfied", so the third branch's error was dropped and
    /// NOTHING replaced it (reproduced: the document would report zero
    /// errors despite being genuinely invalid).
    /// </summary>
    [Fact]
    public void ThreeBranchOneOf_TwoBranchesMatch_ThirdBranchErrorSurvives()
    {
        var results = Evaluate(ThreeBranchOneOfSchema, """{"a": "x", "b": "y"}""");

        Assert.False(results.IsValid, "Two matching branches violate oneOf's 'exactly one' rule.");

        var errors = SchemaErrorCollector.CollectErrors(results);

        Assert.Contains(errors, e => e.Message.Contains("\"c\"", StringComparison.Ordinal));
        // Must be a GENUINE, located error — not the synthetic root-level
        // fallback CollectErrors emits when every real error was suppressed.
        Assert.DoesNotContain(errors, e =>
            e.Message.Contains("no detailed error messages", StringComparison.Ordinal));
    }

    /// <summary>
    /// Nit n1: when the synthesised <c>[oneOf]</c> "too many matches" error
    /// coexists with an unrelated GENUINE error (here, branch 'c's own
    /// missing-required failure — same fixture as
    /// <see cref="ThreeBranchOneOf_TwoBranchesMatch_ThirdBranchErrorSurvives"/>,
    /// this time with the schema threaded through so the named synthesis
    /// fires), the synthesised message — naming the actual defect directly —
    /// must be FIRST, not merely present somewhere in the list. Pins
    /// <c>CollectErrors</c>' <c>InsertRange(0, …)</c> against a regression
    /// back to appending.
    /// </summary>
    [Fact]
    public void ThreeBranchOneOf_TwoBranchesMatchWithSurvivingThirdError_SynthesisedMessageIsFirst()
    {
        using var schemaDoc = JsonDocument.Parse(ThreeBranchOneOfSchema);
        var results = Evaluate(ThreeBranchOneOfSchema, """{"a": "x", "b": "y"}""");

        Assert.False(results.IsValid, "Two matching branches violate oneOf's 'exactly one' rule.");

        var errors = SchemaErrorCollector.CollectErrors(results, schema: schemaDoc.RootElement.Clone());

        Assert.Equal(2, errors.Count);
        Assert.Contains("[oneOf]", errors[0].Message, StringComparison.Ordinal);
        Assert.Contains("\"c\"", errors[1].Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Exactly one branch matches — the oneOf IS satisfied, so the other two
    /// (genuinely non-matching) branches' errors are noise and must be
    /// dropped, exactly as the original fix intended for the 2-branch case.
    /// </summary>
    /// <remarks>
    /// The oneOf sits under a wrapper object carrying an UNRELATED, always-
    /// missing required field ('other'), so the overall document is invalid
    /// (matching how <see cref="SchemaErrorCollector.CollectErrors"/> is
    /// ACTUALLY invoked in production — both real call sites,
    /// SchemaComposer.Validate and YamlSchemaValidator.Validate, check
    /// <c>results.IsValid</c> and never call this method at all when it is
    /// true). Calling it directly on an ACTUALLY-valid result, as an earlier
    /// draft of this test did, is unrepresentative: <c>CollectErrorsRecursive</c>
    /// early-returns at the (valid) root without ever registering the oneOf's
    /// own branch satisfaction, and the resulting empty collection then hits
    /// <c>CollectErrors</c>'s "no detailed error messages" synthetic
    /// fallback — a real but SEPARATE, dead-in-production code path this
    /// test must not conflate with the composite-branch-noise fix.
    /// </remarks>
    [Fact]
    public void ThreeBranchOneOf_ExactlyOneBranchMatches_OtherTwoAreSuppressed()
    {
        const string wrapped = """
            {
              "type": "object",
              "required": ["other"],
              "properties": {
                "service": {
                  "oneOf": [
                    { "required": ["a"] },
                    { "required": ["b"] },
                    { "required": ["c"] }
                  ]
                }
              }
            }
            """;

        var results = Evaluate(wrapped, """{"service": {"a": "x"}}""");

        Assert.False(results.IsValid, "'other' is required at the root and absent.");

        var errors = SchemaErrorCollector.CollectErrors(results);

        Assert.Contains(errors, e => e.Message.Contains("\"other\"", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Message.Contains("\"b\"", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Message.Contains("\"c\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Zero branches match — oneOf genuinely fails (not merely "too many"),
    /// so every branch's error is genuine and all three must survive.
    /// </summary>
    [Fact]
    public void ThreeBranchOneOf_NoBranchMatches_AllThreeErrorsSurvive()
    {
        var results = Evaluate(ThreeBranchOneOfSchema, "{}");

        Assert.False(results.IsValid);

        var errors = SchemaErrorCollector.CollectErrors(results);

        Assert.Contains(errors, e => e.Message.Contains("\"a\"", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("\"b\"", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("\"c\"", StringComparison.Ordinal));
    }

    // ── Composite-branch noise: DEPTH-INDEPENDENCE (MAJOR-4) ────────────────────
    //
    // The original fix only recognised a branch's error as suppressible when
    // the FAILING node's own EvaluationPath terminated EXACTLY at
    // '.../anyOf/<N>' — a losing branch with its OWN nested sub-schema (e.g.
    // "type":"object" + "properties") fails at a DEEPER path instead (e.g.
    // '.../anyOf/1/properties/b'), which the original check never matched,
    // so it always survived regardless of whether the composite validated.
    // Safe direction (extra noise, never a hidden genuine defect) but breaks
    // the CHANGELOG's general "no composite-branch noise" guarantee. Mirrors
    // IsIfDiscriminatorNoise's OWN deliberate depth-independence (see that
    // method's remarks) — this is the same principle applied to oneOf/anyOf.

    private const string NestedAnyOfSchema = """
        {
          "type": "object",
          "anyOf": [
            { "required": ["a"] },
            {
              "type": "object",
              "properties": { "b": { "type": "string" } },
              "required": ["b"]
            }
          ]
        }
        """;

    /// <summary>
    /// (a) 'a' is present (branch 0 valid -> anyOf satisfied); 'b' is ALSO
    /// present but the wrong type, so branch 1 fails DEEPLY, at
    /// '.../anyOf/1/properties/b', not at '.../anyOf/1' itself. Since the
    /// composite is satisfied, this deep failure is exploration noise and
    /// must be dropped, exactly like a shallow losing branch would be.
    /// </summary>
    /// <remarks>
    /// As with <see cref="ThreeBranchOneOf_ExactlyOneBranchMatches_OtherTwoAreSuppressed"/>:
    /// a satisfied anyOf does not, by itself, make the wrapping document
    /// invalid (branch 0 already satisfies "at least one"), so the anyOf is
    /// wrapped in a container with an unrelated always-missing required
    /// field to reach <see cref="SchemaErrorCollector.CollectErrors"/> the
    /// way production actually does — called only once the OVERALL result is
    /// already invalid.
    /// </remarks>
    [Fact]
    public void NestedAnyOf_SatisfiedComposite_DeepLosingBranchFailure_IsDropped()
    {
        const string wrapped = """
            {
              "type": "object",
              "required": ["other"],
              "properties": {
                "service": {
                  "type": "object",
                  "anyOf": [
                    { "required": ["a"] },
                    {
                      "type": "object",
                      "properties": { "b": { "type": "string" } },
                      "required": ["b"]
                    }
                  ]
                }
              }
            }
            """;

        var results = Evaluate(wrapped, """{"service": {"a": "x", "b": 123}}""");

        Assert.False(results.IsValid, "'other' is required at the root and absent.");

        var errors = SchemaErrorCollector.CollectErrors(results);

        Assert.Contains(errors, e => e.Message.Contains("\"other\"", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.InstanceLocation.StartsWith("/service", StringComparison.Ordinal));
    }

    /// <summary>
    /// (b) The converse: 'a' is ABSENT (branch 0 fails) and 'b' is present
    /// but wrong-typed (branch 1 ALSO fails, deeply). Neither branch
    /// matches, so the anyOf composite is genuinely UNSATISFIED — the deep
    /// failure under branch 1 is now a genuine, independently-reportable
    /// defect and must survive, alongside branch 0's shallow failure.
    /// </summary>
    [Fact]
    public void NestedAnyOf_UnsatisfiedComposite_DeepBranchFailure_Survives()
    {
        var results = Evaluate(NestedAnyOfSchema, """{"b": 123}""");

        Assert.False(results.IsValid);

        var errors = SchemaErrorCollector.CollectErrors(results);

        Assert.Contains(errors, e => e.Message.Contains("\"a\"", StringComparison.Ordinal));
        Assert.Contains(errors, e =>
            e.InstanceLocation == "/b" && e.Message.Contains("[type]", StringComparison.Ordinal));
    }

    /// <summary>
    /// (c) The reviewer's two-STEPS analogue of the existing two-services
    /// laundering test: two <c>script.csharp</c> steps share a BYTE-IDENTICAL
    /// EvaluationPath through the SAME provider discriminator clause (both
    /// steps are the same type), so only InstanceLocation (<c>/steps/0</c> vs
    /// <c>/steps/1</c>) tells them apart. Step 0 is fully valid; step 1 is
    /// genuinely broken (neither 'code' nor 'file'). Verified by the
    /// reviewer to already behave correctly; pinned here so the CRITICAL-1 /
    /// MAJOR-4 rewrite cannot silently regress it.
    /// </summary>
    [Fact]
    public void TwoStepsSameType_OneValidOneGenuinelyBroken_EachJudgedIndependently()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(ScriptCsharpProvider).Assembly });

        const string yaml = """
            steps:
              - id: good
                type: script.csharp
                code: "// noop"
              - id: bad
                type: script.csharp
            """;

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.False(result.IsValid, "Step 'bad' sets neither 'code' nor 'file' and must be rejected.");

        // Step 0 ('good') contributes nothing at all.
        Assert.DoesNotContain(result.Errors, e => e.InstanceLocation.StartsWith("/steps/0", StringComparison.Ordinal));

        // Step 1's genuine defect survives in full: BOTH branches of its own
        // unsatisfied oneOf are genuine, independently-reportable errors.
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/steps/1" && e.Message.Contains("\"code\"", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/steps/1" && e.Message.Contains("\"file\"", StringComparison.Ordinal));
    }

    // ── Composite-branch noise: the frozen script.csharp provider fragment ─────
    //
    // script.csharp's oneOf (code XOR file — see ScriptCsharpProvider.SchemaFragment,
    // a frozen provider fragment this change must NOT edit) is the worked
    // example the combined findings named directly: a valid 'code:' plus a
    // typo'd 'taget:' used to report ONLY the phantom "[required] file"
    // branch noise, actively misleading (adding 'file:' produces a DIFFERENT
    // error) and hiding the genuine unevaluatedProperties/'taget' message
    // behind the cascade suppression (SuppressUnevaluatedPropertiesCascade
    // saw a false "other error" on the step). This is the exact acceptance
    // case named in the objective; SchemaStepSurfaceClosureTests' 25-provider
    // Theory (SingleTypo_YieldsExactlyOneError_NotOnePerProvider) proves the
    // same fix generalises to every Core provider, script.csharp included.

    [Fact]
    public void ScriptCsharp_ValidCodePlusTypo_ReportsOnlyTheTypo_NotThePhantomFileRequiredBranch()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(ScriptCsharpProvider).Assembly });

        const string yaml = """
            steps:
              - id: s1
                type: script.csharp
                code: "// noop"
                taget: oops
            """;

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.False(result.IsValid);

        var onlyError = Assert.Single(result.Errors);
        Assert.Equal("/steps/0/taget", onlyError.InstanceLocation);
        Assert.Contains("taget", onlyError.Message, StringComparison.Ordinal);
        Assert.Contains("script.csharp", onlyError.Message, StringComparison.Ordinal);

        // The phantom branch message must never appear at all, under ANY
        // location — not merely "not as the only error".
        Assert.DoesNotContain(result.Errors, e =>
            e.Message.Contains("\"file\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// The genuine converse: neither 'code' nor 'file' set means the oneOf
    /// composite ITSELF never validates (no branch matched), so both
    /// branches' "required" errors are genuine, independently-reportable
    /// defects and must survive untouched — the composite-branch filter must
    /// never suppress a TRULY failed composite, only a satisfied one's
    /// exploratory noise.
    /// </summary>
    [Fact]
    public void ScriptCsharp_NeitherCodeNorFile_BothRequiredErrorsSurvive()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(ScriptCsharpProvider).Assembly });

        const string yaml = """
            steps:
              - id: s1
                type: script.csharp
            """;

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Message.Contains("\"code\"", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Message.Contains("\"file\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// MINOR-8 (second-round gatekeeper finding): BOTH 'code' and 'file' set
    /// means the oneOf's "exactly one" invariant is violated by TWO matches,
    /// not zero — a THIRD, genuinely distinct failure mode from the
    /// "neither" case above. Before this fix: since JsonSchema.Net attaches
    /// no message to a 'oneOf' failing via "too many matches" (verified
    /// empirically — neither branch carries an error, both are individually
    /// valid), and the CRITICAL-1 count fix correctly leaves the group
    /// UNSATISFIED (count == 2, not 1), the fragment's own 'properties'
    /// annotation never propagates to unevaluatedProperties either — so the
    /// ONLY symptom was two actively-wrong "Unknown property 'code'"/"'file'"
    /// messages for the two canonical, correctly-spelled fields. This
    /// synthesises the genuine defect directly ("[oneOf] Exactly one of
    /// 'code', 'file' may be set — both are present."), reading the
    /// branches' own 'required' members from the schema. Also proves the
    /// interplay with the EXISTING unevaluatedProperties cascade
    /// suppression: once this genuine error is present, the two misleading
    /// "unknown property" entries must be dropped automatically, not merely
    /// coexist alongside the real one.
    /// </summary>
    /// <remarks>
    /// script.csharp's oneOf happens to be the shape M1's guard was written
    /// to keep working: two branches, each a single-field <c>required</c>,
    /// so <c>ValidBranchFieldNames.Count == ValidBranchCount</c> holds and
    /// the named message fires. It does NOT generalise to every provider's
    /// oneOf unconditionally — see TypeShapedOneOf_*,
    /// MixedRequiredAndTypeOnlyOneOf_*, and MultiRequiredPairOneOf_* below
    /// for the shapes where naming individual fields would be wrong, and
    /// SchemaErrorCollector's own "Third-round gatekeeper findings" remarks
    /// for why.
    /// </remarks>
    [Fact]
    public void ScriptCsharp_BothCodeAndFile_SynthesisesGenuineOneOfError_AndSuppressesMisleadingUnknownPropertyNoise()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(ScriptCsharpProvider).Assembly });

        const string yaml = """
            steps:
              - id: s1
                type: script.csharp
                code: "// noop"
                file: helper.csx
            """;

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.False(result.IsValid, "'code' and 'file' are mutually exclusive — the oneOf's 'exactly one' rule is violated by two matches.");

        var onlyError = Assert.Single(result.Errors);
        Assert.Equal("/steps/0", onlyError.InstanceLocation);
        Assert.Contains("[oneOf]", onlyError.Message, StringComparison.Ordinal);
        Assert.Contains("'code'", onlyError.Message, StringComparison.Ordinal);
        Assert.Contains("'file'", onlyError.Message, StringComparison.Ordinal);

        // The misleading "unknown property" noise (both fields are
        // correctly spelled and genuinely declared by the fragment) must
        // never appear alongside — or instead of — the genuine defect.
        Assert.DoesNotContain(result.Errors, e => e.Message.Contains("Unknown property", StringComparison.Ordinal));
    }

    // ── Composite-branch noise: the too-many-matches NAMING guard (M1) ──────────
    //
    // Third-round gatekeeper finding: the MINOR-8 synthesis above assumed
    // every matching oneOf branch contributes exactly one name to
    // ValidBranchFieldNames — true for script.csharp's code/file (each a
    // single-field 'required') but not in general. These three Facts
    // reproduce the reviewer's three measured failure shapes directly
    // against SchemaErrorCollector.CollectErrors, mirroring the
    // ThreeBranchOneOf_* trio's no-wrapper-needed pattern above (a oneOf's
    // own "too many matches" failure makes the whole schema invalid on its
    // own, so there is no need to nest it under an unrelated always-missing
    // required field just to reach CollectErrors the way production does).

    private const string TypeShapedOneOfSchema = """
        {
          "type": "string",
          "oneOf": [
            { "minLength": 1 },
            { "maxLength": 100 }
          ]
        }
        """;

    /// <summary>
    /// Neither branch declares a 'required' member — both are bare scalar
    /// constraints on the string itself — so a value satisfying both (any
    /// short, non-empty string) leaves ValidBranchFieldNames null: zero
    /// names for two matches. Pre-fix, the unconditional
    /// <c>fieldNames is { Count: &gt; 0 }</c> check was simply never true, so
    /// the synthesis silently produced NOTHING — reproducing the exact
    /// CRITICAL-1 symptom (a genuine "too many matches" failure with no
    /// message at all, falling through to the synthetic "no detailed error
    /// messages" root fallback) for a shape CRITICAL-1's own fix never
    /// covered.
    /// </summary>
    [Fact]
    public void TypeShapedOneOf_TwoBranchesMatch_FallsBackToHonestCountMessage_NeverSilent()
    {
        var results = Evaluate(TypeShapedOneOfSchema, "\"hi\"");

        Assert.False(results.IsValid, "\"hi\" satisfies both minLength:1 and maxLength:100 — oneOf's 'exactly one' rule is violated.");

        var errors = SchemaErrorCollector.CollectErrors(results, schema: ParseSchemaElement(TypeShapedOneOfSchema));

        Assert.Contains(errors, e => e.Message.Contains("[oneOf]", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e =>
            e.Message.Contains("no detailed error messages", StringComparison.Ordinal));
    }

    private const string MixedRequiredAndTypeOnlyOneOfSchema = """
        {
          "type": "object",
          "oneOf": [
            { "required": ["a"] },
            { "type": "object" },
            { "required": ["zzz"] }
          ]
        }
        """;

    /// <summary>
    /// Branch 0 ('a' required) and branch 1 (bare 'type: object', no
    /// 'required' at all) both match {"a": "x"} — two matches, but only ONE
    /// of them contributes a name ('a'). Pre-fix, the unconditional call
    /// treated that partial, coincidental single-name list as if it were
    /// complete, fabricating "[oneOf] Exactly one of 'a' may be set — 1 are
    /// present." — wrong on two counts: 'a' is not what makes branch 1
    /// match (branch 1 doesn't mention it at all), and "1 are present" is
    /// ungrammatical on top of being misleading. Branch 2 ('zzz' required,
    /// absent) is the group's genuinely losing branch (the group is NOT
    /// satisfied — two matches, oneOf needs exactly one) and must still
    /// survive untouched.
    /// </summary>
    [Fact]
    public void MixedRequiredAndTypeOnlyOneOf_TwoBranchesMatch_DoesNotFabricateASingleFieldName()
    {
        var results = Evaluate(MixedRequiredAndTypeOnlyOneOfSchema, """{"a": "x"}""");

        Assert.False(results.IsValid, "Branch 0 ('a') and branch 1 (bare type:object) both match — oneOf's 'exactly one' rule is violated.");

        var errors = SchemaErrorCollector.CollectErrors(
            results, schema: ParseSchemaElement(MixedRequiredAndTypeOnlyOneOfSchema));

        Assert.DoesNotContain(errors, e =>
            e.Message.Contains("Exactly one of 'a' may be set", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("\"zzz\"", StringComparison.Ordinal));
    }

    private const string MultiRequiredPairOneOfSchema = """
        {
          "type": "object",
          "oneOf": [
            { "required": ["a", "b"] },
            { "required": ["c", "d"] }
          ]
        }
        """;

    /// <summary>
    /// Each branch requires a PAIR, not a single discriminator field. When
    /// all four fields are present, both pairs match — two matches — but
    /// each branch contributes TWO names, so the flat list ('a','b','c','d')
    /// coincidentally still has as many entries as there are matches only
    /// by chance of arithmetic (2 branches x 2 names = 4 = 2 x 2), while the
    /// actual correspondence (each branch names TWO fields, not one) is
    /// broken. Pre-fix, this produced "[oneOf] Exactly one of 'a', 'b', 'c',
    /// 'd' may be set — 4 are present." — advice to remove individual
    /// fields, when the real choice is between the two PAIRS as a whole.
    /// </summary>
    [Fact]
    public void MultiRequiredPairOneOf_BothPairsPresent_DoesNotAdviseRemovingIndividualFields()
    {
        var results = Evaluate(MultiRequiredPairOneOfSchema, """{"a": 1, "b": 2, "c": 3, "d": 4}""");

        Assert.False(results.IsValid, "Both two-field pairs are present, violating oneOf's 'exactly one' rule.");

        var errors = SchemaErrorCollector.CollectErrors(
            results, schema: ParseSchemaElement(MultiRequiredPairOneOfSchema));

        Assert.DoesNotContain(errors, e =>
            e.Message.Contains("Exactly one of 'a', 'b', 'c', 'd' may be set", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("[oneOf]", StringComparison.Ordinal));
    }

    private const string ThreeMixedShapesOneOfSchema = """
        {
          "type": "object",
          "oneOf": [
            { "required": ["a"] },
            { "required": ["b", "c"] },
            { "type": "object" }
          ]
        }
        """;

    /// <summary>
    /// M1-r (fourth-round gatekeeper re-review): count-equality ALONE
    /// (M1's guard) is necessary but not sufficient. Here branch 0
    /// contributes ONE name ('a'), branch 1 contributes TWO ('b','c' — a
    /// pair, one match), and branch 2 (a bare 'type: object') contributes
    /// ZERO — but 1 + 2 + 0 = 3 happens to equal the branch count (3
    /// matches), so the flat <c>ValidBranchFieldNames.Count ==
    /// ValidBranchCount</c> check alone was satisfied by coincidence of
    /// arithmetic. Pre-M1-r this fired "Exactly one of 'a', 'b', 'c' may be
    /// set" — advice that can NEVER be satisfied, because branch 2 matches
    /// every object unconditionally regardless of a/b/c: even with only
    /// 'a' set (removing 'b'/'c'), branch 2 still matches alongside branch
    /// 0, still "too many". <see cref="SchemaErrorCollector"/>'s
    /// <c>HasUnattributableBranch</c> tracks each branch's own contribution
    /// in isolation, not merely the running total, and closes this gap.
    /// </summary>
    [Fact]
    public void ThreeMixedShapesOneOf_CoincidentalCountMatch_FallsBackToHonestCountMessage_NeverUnachievableAdvice()
    {
        var results = Evaluate(ThreeMixedShapesOneOfSchema, """{"a": 1, "b": 2, "c": 3}""");

        Assert.False(results.IsValid, "All three branches match (a; b+c; the bare type:object) — oneOf's 'exactly one' rule is violated.");

        var errors = SchemaErrorCollector.CollectErrors(
            results, schema: ParseSchemaElement(ThreeMixedShapesOneOfSchema));

        Assert.DoesNotContain(errors, e =>
            e.Message.Contains("Exactly one of 'a', 'b', 'c' may be set", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("[oneOf]", StringComparison.Ordinal));
    }

    // ── [enum] enrichment through a PROVIDER fragment (Part C) ──────────────────
    //
    // Proves the SAME generic SchemaErrorCollector mechanism that enriches
    // root-schema enums (dependency 'type', 'imagePullPolicy', 'verifyMode' —
    // see EnvironmentSchemaTests / RootSchemaTests) also reaches an enum
    // declared INSIDE a provider's own spliced JsonSchemaFragment, reached
    // via the composer's dynamically-injected allOf/if/then — not merely a
    // $ref/$defs indirection in the static root schema.

    [Fact]
    public void CacheAssertRedis_OperationWrongCase_IsRejectedWithActionableMessage()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(CacheAssertRedisProvider).Assembly });

        const string yaml = """
            steps:
              - id: s1
                type: cache-assert.redis
                target: cache
                key: orders:1
                operation: GET
                expect:
                  value: "1"
            """;

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.False(result.IsValid, "Redis 'operation' values are case-sensitive; 'GET' must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/steps/0/operation" &&
            e.Message.Contains("[enum]", StringComparison.Ordinal) &&
            e.Message.Contains("'GET'", StringComparison.Ordinal) &&
            e.Message.Contains("get", StringComparison.Ordinal) &&
            e.Message.Contains("write 'get'", StringComparison.Ordinal));
    }

    // ── [enum] enrichment: bounded offending-value echo (SECURITY finding) ─────
    //
    // FormatAllowedValuesList already caps the ACCEPTED-values side at
    // MaxListedEnumValues, but FormatEnumError spliced the OFFENDING scalar
    // itself unbounded — an author (or an attacker crafting a suite fed to a
    // shared CI runner) supplying an arbitrarily large string in an enum
    // position inflates the resulting SchemaValidationError.Message without
    // limit, which flows into the §14 JSON Lines event stream every renderer
    // (and the Healer agent) consumes. Bounded to mirror the in-file
    // discipline FormatAllowedValuesList already applies.

    [Fact]
    public void FormatEnumError_MultiKilobyteOffendingValue_IsTruncatedInTheMessage()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(CacheAssertRedisProvider).Assembly });

        var hugeValue = new string('x', 5000);
        var yaml = $$"""
            steps:
              - id: s1
                type: cache-assert.redis
                target: cache
                key: orders:1
                operation: "{{hugeValue}}"
                expect:
                  value: "1"
            """;

        var result = DocumentValidator.Validate(yaml, registry);

        Assert.False(result.IsValid);
        var onlyError = Assert.Single(result.Errors, e => e.InstanceLocation == "/steps/0/operation");

        // The message must never contain the full 5000-character value...
        Assert.DoesNotContain(hugeValue, onlyError.Message, StringComparison.Ordinal);
        // ...but must still name a truncated PREFIX of it and the true total length,
        // so the message stays actionable rather than merely silent about the value.
        Assert.Contains(new string('x', 50), onlyError.Message, StringComparison.Ordinal);
        Assert.Contains("5000 chars total", onlyError.Message, StringComparison.Ordinal);
        // The message as a whole stays a small, bounded size — not O(scalar).
        Assert.True(onlyError.Message.Length < 1000,
            $"Expected a bounded message, got {onlyError.Message.Length} characters.");
    }

    // ── [enum] enrichment: case-hint requires a UNIQUE match (MINOR-6) ──────────
    //
    // FormatEnumError's own XML doc promises a suggestion only "when the
    // offending value is a case-insensitive match for EXACTLY ONE accepted
    // value" — the original implementation used FirstOrDefault, which
    // silently picks an arbitrary winner when TWO OR MORE accepted values
    // case-insensitively match (never arises in today's real enums, all of
    // which are case-fold-unique, but the code must actually implement what
    // its own doc comment claims). Driven directly against
    // SchemaErrorCollector.CollectErrors with a synthetic ambiguous enum —
    // no real provider needed, since none of the shipped vocabularies can
    // exercise this.

    private const string AmbiguousEnumSchema = """
        {
          "type": "object",
          "properties": {
            "kind": { "type": "string", "enum": ["Foo", "FOO", "bar"] }
          }
        }
        """;

    [Fact]
    public void FormatEnumError_TwoCaseInsensitiveMatches_NoSuggestionIsFabricated()
    {
        var results = Evaluate(AmbiguousEnumSchema, """{"kind": "foo"}""");

        Assert.False(results.IsValid, "'foo' matches neither 'Foo' nor 'FOO' by ordinal comparison.");

        using var doc = JsonDocument.Parse("""{"kind": "foo"}""");
        using var schemaDoc = JsonDocument.Parse(AmbiguousEnumSchema);
        var errors = SchemaErrorCollector.CollectErrors(results, doc.RootElement, schemaDoc.RootElement);

        var onlyError = Assert.Single(errors);
        Assert.Contains("[enum]", onlyError.Message, StringComparison.Ordinal);
        Assert.Contains("'foo'", onlyError.Message, StringComparison.Ordinal);
        // 'Foo' and 'FOO' both match case-insensitively — ambiguous, so NEITHER
        // may be fabricated as "the" suggestion.
        Assert.DoesNotContain("write '", onlyError.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The converse, over the SAME ambiguous enum: a value matching only ONE
    /// member case-insensitively (not both) still gets the suggestion — the
    /// uniqueness check must not become so conservative it suppresses the
    /// genuinely unambiguous case.
    /// </summary>
    [Fact]
    public void FormatEnumError_OneCaseInsensitiveMatchAmongMany_SuggestionSurvives()
    {
        var results = Evaluate(AmbiguousEnumSchema, """{"kind": "BAR"}""");

        Assert.False(results.IsValid);

        using var doc = JsonDocument.Parse("""{"kind": "BAR"}""");
        using var schemaDoc = JsonDocument.Parse(AmbiguousEnumSchema);
        var errors = SchemaErrorCollector.CollectErrors(results, doc.RootElement, schemaDoc.RootElement);

        var onlyError = Assert.Single(errors);
        Assert.Contains("write 'bar'", onlyError.Message, StringComparison.Ordinal);
    }


    // ── Direct, white-box path-matching coverage ───────────────────────────────

    [Theory]
    // Top-level shape, as produced by the real composed schema (§13.6): a
    // non-matching provider clause's own 'if' sub-evaluation is noise.
    [InlineData("/allOf/3/if/properties/type", true)]
    // A clause's genuine 'then'-branch failure is never noise.
    [InlineData("/allOf/3/then/required", false)]
    // Depth-independence (security MINOR-b): the SAME shape, nested one level
    // deeper inside another clause's 'then' branch, is still noise — an 'if'
    // keyword's own failure is never diagnostic at any nesting depth (see the
    // extended remarks on IsIfDiscriminatorNoise).
    [InlineData("/allOf/1/then/allOf/0/if/properties/mode", true)]
    // ...and the corresponding nested GENUINE failure (a real 'then' branch,
    // just one level deeper) is still kept.
    [InlineData("/allOf/1/then/allOf/0/then/required", false)]
    // Root / empty paths never match — there is nothing to filter at the
    // document root, and the loop must not misbehave on a degenerate input.
    [InlineData("", false)]
    [InlineData("/", false)]
    // Boundary: 'allOf' as the final segment, or with only one segment after
    // it, leaves no room for an 'i + 2' index at all. The loop condition
    // (i + 2 < segments.Length) must never be satisfied here, so these must
    // never be treated as noise (and must never index out of bounds).
    [InlineData("/allOf", false)]
    [InlineData("/allOf/3", false)]
    public void IsIfDiscriminatorNoise_MatchesExpectedShape(string evaluationPath, bool expectedNoise)
    {
        Assert.Equal(expectedNoise, SchemaErrorCollector.IsIfDiscriminatorNoise(evaluationPath));
    }

    // ── End-to-end: nested if/then inside a provider's OWN fragment ────────────

    /// <summary>
    /// A provider whose <see cref="JsonSchemaFragment"/> nests its OWN
    /// <c>allOf</c> of if/then pairs inside its <c>then</c> branch must, when
    /// evaluated through the real <see cref="SchemaComposer.Validate"/>
    /// pipeline, have its nested "if"-discriminator noise filtered exactly as
    /// the top-level (provider-vs-provider) discriminator noise is — proving
    /// <see cref="SchemaErrorCollector.IsIfDiscriminatorNoise"/>'s
    /// depth-independence end-to-end, not merely by construction of a path
    /// string (the Theory above).
    /// </summary>
    /// <remarks>
    /// The step declares <c>mode: special</c>: this matches the fragment's
    /// nested first if/then pair (which then genuinely fails — the required
    /// <c>specialField</c> is absent) and mismatches its second nested if/then
    /// pair (whose <c>if</c> fails, contributing nested noise if unfiltered).
    /// Only the genuine nested failure may survive: the outer fragment's own
    /// <c>properties: {"mode": ...}</c> annotation would, in isolation, never
    /// propagate to $defs/step's <c>unevaluatedProperties</c> either (its
    /// nested <c>allOf</c> genuinely fails, so the whole fragment counts as
    /// failed — see <c>SchemaErrorCollector</c>'s class remarks), which would
    /// otherwise ALSO report <c>mode</c> as a spurious unevaluated property
    /// alongside the genuine <c>specialField</c> violation.
    /// <see cref="SchemaErrorCollector"/>'s cascade suppression is what keeps
    /// that spurious second error out: this step already has a genuine,
    /// non-unevaluatedProperties defect (the nested "required" violation), so
    /// its unevaluatedProperties entries are dropped — proving the
    /// suppression end-to-end through the real evaluation pipeline, not
    /// merely at the flat top-level discriminator shape
    /// <c>SchemaErrorCollectionAtScaleTests</c> exercises.
    /// </remarks>
    [Fact]
    public void NestedConditionalFragment_AtEndToEndScale_YieldsOnlyTheGenuineNestedError()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(NestedConditionalTestProvider).Assembly });

        const string yaml = """
            steps:
              - id: nested-step
                type: test-nested.conditional
                mode: special
            """;

        var result = SchemaComposer.Validate(registry, yaml);

        Assert.False(result.IsValid, "Expected invalid: the nested 'specialField' required violation must fire.");

        // The mismatched nested clause's discriminator value ("other") must
        // never leak into the collected errors — that would be nested noise.
        Assert.All(result.Errors, e =>
            Assert.DoesNotContain("other", e.Message, StringComparison.Ordinal));

        Assert.True(result.Errors.Count == 1,
            $"Expected exactly 1 genuine (nested) error but got {result.Errors.Count}:{Environment.NewLine}" +
            string.Join(Environment.NewLine, result.Errors.Select(e => $"  loc='{e.InstanceLocation}' msg='{e.Message}'")));

        var genuine = result.Errors[0];
        Assert.Contains("required", genuine.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("specialField", genuine.Message, StringComparison.Ordinal);
    }

    // ── Test-only provider: nests its own conditional allOf inside 'then' ──────

    /// <summary>
    /// Test-only step model for <see cref="NestedConditionalTestProvider"/>.
    /// Carries no fields: this provider exists purely to exercise schema
    /// composition/validation, never binding, compilation, or execution.
    /// </summary>
    private sealed record NestedConditionalTestModel : IStepModel { }

    /// <summary>
    /// Test-only provider (issue #259) whose <see cref="SchemaFragment"/>
    /// nests its own <c>allOf</c> of two if/then pairs INSIDE its <c>then</c>
    /// branch — i.e. a SECOND level of if/then/allOf nesting below the outer
    /// discriminator clause <see cref="SchemaComposer"/> injects for every
    /// registered provider. Used only by
    /// <see cref="NestedConditionalFragment_AtEndToEndScale_YieldsOnlyTheGenuineNestedError"/>
    /// to prove the noise filter is depth-independent through the real
    /// evaluation pipeline. Never shipped, never registered outside this test
    /// assembly.
    /// </summary>
    [StepProvider]
    private sealed class NestedConditionalTestProvider
        : IStepProvider, IStepBinder<NestedConditionalTestModel>
    {
        // Activator.CreateInstance (StepKindRegistry's discovery path)
        // requires a PUBLIC parameterless constructor even though the class
        // itself is a private nested type — see StepKindRegistry.InstantiateProvider.
        public NestedConditionalTestProvider() { }

        /// <inheritdoc />
        public StepKindId Kind { get; } = new StepKindId("test-nested", "conditional");

        /// <inheritdoc />
        public ProviderMetadata Metadata { get; } = new ProviderMetadata(
            Version: "0.0.0-test",
            MinEngineVersion: "1.0.0",
            License: "Apache-2.0",
            Authors: new[] { "test-only" });

        /// <inheritdoc />
        /// <remarks>
        /// The nested <c>allOf</c> mirrors exactly the shape
        /// <see cref="SchemaComposer.BuildIfThenClauses"/> generates one level
        /// up — two unconditional if/then pairs keyed on a discriminator field
        /// (<c>mode</c>) — so evaluating a step against this fragment exercises
        /// the identical "one clause's if matches, the other's doesn't" shape
        /// as the top-level provider discriminator, purely one level deeper.
        /// </remarks>
        public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
            """
            {
              "type": "object",
              "properties": {
                "mode": { "type": "string" }
              },
              "allOf": [
                {
                  "if": { "properties": { "mode": { "const": "special" } }, "required": ["mode"] },
                  "then": { "required": ["specialField"] }
                },
                {
                  "if": { "properties": { "mode": { "const": "other" } }, "required": ["mode"] },
                  "then": { "required": ["otherField"] }
                }
              ]
            }
            """);

        /// <inheritdoc />
        /// <remarks>
        /// Never exercised by this file's test (which only validates schema,
        /// never compiles or binds) — implemented only to satisfy the frozen
        /// <see cref="IStepBinder{TModel}"/> contract.
        /// </remarks>
        public NestedConditionalTestModel Bind(YamlNode node, IBindingContext ctx) => new();
    }

    // ── captureEntry pair-message parity with the service standard (m6) ────────
    //
    // Third-round gatekeeper finding: capture's 'both set' and 'empty
    // mapping' rejections read as generic, un-actionable "[properties]
    // Property 'xpath' is not valid here" / raw library minProperties text
    // — unlike $defs/service's own image/project mutual exclusion, which
    // names both fields and the rule. FormatForbiddenPropertyError and a
    // new FormatMinPropertiesError now bring captureEntry up to the same
    // standard, reading the sibling field name(s) from the LIVE schema
    // (TryReadSiblingPropertyNames) rather than hardcoding 'jsonpath'/'xpath'.

    /// <summary>
    /// Both 'jsonpath' and 'xpath' set — the message now names the rule
    /// (mutual exclusion) and BOTH fields, not merely the one JsonSchema.Net
    /// happened to evaluate against the per-field 'false' subschema.
    /// </summary>
    [Fact]
    public void Capture_BothJsonPathAndXPath_MessageNamesBothFieldsAndTheRule()
    {
        const string yaml = """
            steps:
              - id: noop
                type: noop.echo
                capture:
                  ambiguous: { jsonpath: "$.id", xpath: "//id" }
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            "A capture entry declaring both 'jsonpath' and 'xpath' must be rejected — the format is ambiguous.");
        var onlyError = Assert.Single(result.Errors);
        Assert.Equal("/steps/0/capture/ambiguous/xpath", onlyError.InstanceLocation);
        Assert.Contains("[properties]", onlyError.Message, StringComparison.Ordinal);
        Assert.Contains("'xpath'", onlyError.Message, StringComparison.Ordinal);
        Assert.Contains("'jsonpath'", onlyError.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be combined with", onlyError.Message, StringComparison.Ordinal);
        Assert.Contains("'ambiguous'", onlyError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("is not valid here", onlyError.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Neither 'jsonpath' nor 'xpath' set — the message now names both
    /// accepted choices, not merely the raw library minProperties count
    /// text.
    /// </summary>
    [Fact]
    public void Capture_EmptyMapping_MessageNamesBothChoices()
    {
        const string yaml = """
            steps:
              - id: noop
                type: noop.echo
                capture:
                  neither: {}
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            "A capture entry declaring neither 'jsonpath' nor 'xpath' must be rejected.");
        var onlyError = Assert.Single(result.Errors, e => e.InstanceLocation == "/steps/0/capture/neither");
        Assert.Contains("[minProperties]", onlyError.Message, StringComparison.Ordinal);
        Assert.Contains("'jsonpath'", onlyError.Message, StringComparison.Ordinal);
        Assert.Contains("'xpath'", onlyError.Message, StringComparison.Ordinal);
        Assert.Contains("'neither'", onlyError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("at least 1 properties", onlyError.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// n-b (fourth-round gatekeeper re-review): the third captureEntry
    /// failure shape — an unrecognised key, e.g. 'badkey' typo'd for
    /// 'jsonpath' — is an <c>additionalProperties: false</c> rejection, not
    /// a forbidden-property or minProperties one, so it went through
    /// <see cref="SchemaErrorCollector"/>'s OTHER blank-keyword formatter
    /// (<c>FormatAdditionalPropertiesError</c>) — previously bare ("Unknown
    /// property 'badkey'" with no container at all), even though
    /// <c>TryResolveCaptureEntryContainer</c> already existed for the sibling
    /// m6 formatters. Now named exactly like a service/dependency container
    /// already is.
    /// </summary>
    [Fact]
    public void Capture_UnknownKey_MessageNamesTheCaptureEntry()
    {
        const string yaml = """
            steps:
              - id: noop
                type: noop.echo
                capture:
                  orderId: { badkey: "y" }
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "A capture entry with an unrecognised key must be rejected.");
        var onlyError = Assert.Single(result.Errors, e => e.InstanceLocation == "/steps/0/capture/orderId/badkey");
        Assert.Contains("[additionalProperties]", onlyError.Message, StringComparison.Ordinal);
        Assert.Contains("'badkey'", onlyError.Message, StringComparison.Ordinal);
        Assert.Contains("capture entry 'orderId'", onlyError.Message, StringComparison.Ordinal);
    }

    // ── TryReadSingleConstCondition: the 'required' half is load-bearing ─────
    //
    // Review finding: the method's doc contract is the EXACT shape
    // {"required":["field"],"properties":{"field":{"const":...}}} but the
    // implementation never checked 'required'. Without it, the if-clause ALSO
    // matches when the field is ABSENT ('properties' is a no-op on a missing
    // key), so "not valid when 'field' is X" would be a fabricated half-truth
    // for an optional-const if-clause. Unreachable through the 25 shipped
    // fragments (every shipped shape carries the pair); pinned here so a
    // Community fragment with an optional-const if-clause degrades to the
    // generic message instead of inheriting a conditional it does not have.

    private const string OptionalConstIfClauseSchema = """
        {
          "type": "object",
          "properties": {
            "block": {
              "type": "object",
              "allOf": [
                {
                  "if": { "properties": { "mode": { "const": "strict" } } },
                  "then": { "properties": { "extra": false } }
                }
              ]
            }
          }
        }
        """;

    private const string RequiredConstIfClauseSchema = """
        {
          "type": "object",
          "properties": {
            "block": {
              "type": "object",
              "allOf": [
                {
                  "if": { "required": ["mode"], "properties": { "mode": { "const": "strict" } } },
                  "then": { "properties": { "extra": false } }
                }
              ]
            }
          }
        }
        """;

    /// <summary>
    /// An if-clause whose const-bearing property is NOT required must degrade
    /// to the generic forbidden-property message — the conditional form would
    /// be false for the field-absent case the same clause also matches.
    /// </summary>
    [Fact]
    public void ForbiddenProperty_OptionalConstIfClause_DegradesToGenericMessage()
    {
        var results = Evaluate(OptionalConstIfClauseSchema, """{"block": {"mode": "strict", "extra": 1}}""");
        Assert.False(results.IsValid);

        var errors = SchemaErrorCollector.CollectErrors(
            results, schema: ParseSchemaElement(OptionalConstIfClauseSchema));

        var forbidden = Assert.Single(errors, e => e.Message.Contains("'extra'", StringComparison.Ordinal));
        Assert.Contains("is not valid here", forbidden.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("when 'mode'", forbidden.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The positive control: with the required pair present — the exact shape
    /// the shipped dynamodb/s3 clauses use — the conditional form still fires.
    /// </summary>
    [Fact]
    public void ForbiddenProperty_RequiredConstIfClause_KeepsConditionalMessage()
    {
        var results = Evaluate(RequiredConstIfClauseSchema, """{"block": {"mode": "strict", "extra": 1}}""");
        Assert.False(results.IsValid);

        var errors = SchemaErrorCollector.CollectErrors(
            results, schema: ParseSchemaElement(RequiredConstIfClauseSchema));

        var forbidden = Assert.Single(errors, e => e.Message.Contains("'extra'", StringComparison.Ordinal));
        Assert.Contains("is not valid when 'mode' is 'strict'", forbidden.Message, StringComparison.Ordinal);
    }
}
