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
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Issue #259: direct and end-to-end coverage of
/// <see cref="SchemaErrorCollector.IsIfDiscriminatorNoise"/>.
/// </summary>
public sealed class SchemaErrorCollectorTests
{
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
    /// <para>
    /// The step declares <c>mode: special</c>: this matches the fragment's
    /// nested first if/then pair (which then genuinely fails — the required
    /// <c>specialField</c> is absent) and mismatches its second nested if/then
    /// pair (whose <c>if</c> fails, contributing nested noise if unfiltered).
    /// </para>
    /// <para>
    /// TWO genuine errors survive, not one — this is a real, separate
    /// consequence of the typo-closing change ($defs/step's
    /// <c>unevaluatedProperties: false</c>), not IsIfDiscriminatorNoise
    /// regressing: per JSON Schema 2020-12, a subschema's annotations
    /// (here, the outer fragment's own <c>properties: {"mode": ...}</c>,
    /// which would otherwise mark <c>mode</c> as evaluated) are only
    /// collected when that subschema application succeeds as a whole. Since
    /// the outer fragment's nested <c>allOf</c> genuinely fails (the missing
    /// <c>specialField</c>), the ENTIRE outer fragment counts as failed, so
    /// its <c>properties</c> annotation for <c>mode</c> never propagates to
    /// $defs/step's <c>unevaluatedProperties</c> — <c>mode</c> is
    /// consequently ALSO reported as unevaluated, alongside the genuine
    /// <c>specialField</c> violation. Both are asserted explicitly below so
    /// this compounding is pinned, not silently tolerated; only the
    /// mismatched clause's own discriminator noise ("other") must still never
    /// leak.
    /// </para>
    /// </remarks>
    [Fact]
    public void NestedConditionalFragment_AtEndToEndScale_YieldsOnlyTheGenuineErrors()
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

        Assert.True(result.Errors.Count == 2,
            $"Expected exactly 2 genuine errors (the nested 'specialField' violation plus the " +
            $"consequently-unevaluated 'mode' — see remarks) but got {result.Errors.Count}:{Environment.NewLine}" +
            string.Join(Environment.NewLine, result.Errors.Select(e => $"  loc='{e.InstanceLocation}' msg='{e.Message}'")));

        var requiredError = Assert.Single(result.Errors, e => e.Message.Contains("required", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("specialField", requiredError.Message, StringComparison.Ordinal);

        var unevaluatedError = Assert.Single(result.Errors, e => e.Message.Contains("unevaluatedProperties", StringComparison.Ordinal));
        Assert.Equal("/steps/0/mode", unevaluatedError.InstanceLocation);
        Assert.Contains("mode", unevaluatedError.Message, StringComparison.Ordinal);
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
}
