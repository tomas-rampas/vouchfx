// Pre-GA schema tightening — give 'capture' a real value shape
// (root-language-schema.json).
//
// Before this change, a step's 'capture' block was typed only "type": "object"
// — no constraint on its VALUES at all. YamlDocumentParser.ParseCaptureMap /
// ParseCaptureEntry already enforce the real grammar (a bare scalar JSONPath
// expression, or a single-key mapping selecting 'jsonpath'/'xpath' explicitly)
// and already THROW on every malformed shape below; this change teaches the
// SCHEMA the same grammar, so an author gets a located authoring-time error
// (and editor completion for 'jsonpath'/'xpath') instead of only a parser
// exception discovered at compile time. AstBuilder's reserved-prefix guard
// (svc::, conn::, __outcome::, __capture_status::, __attempts:: — see VarKeys,
// the authoritative source for this list) is likewise now expressed via
// 'propertyNames' so a reserved-prefix capture key is caught here too.
//
// These tests exercise the ROOT schema only (YamlSchemaValidator, no provider
// fragments) — 'capture' is a common step field declared directly on
// $defs/step, so no provider clause is needed to evaluate it. Mirrors the
// EnvironmentSchemaTests.cs convention (bare 'id'/'type' step bodies; see that
// file's own header remarks on why the root-only path needs no provider
// fields). See SchemaAcceptedCorpusTests / SchemaRejectedCorpusTests for the
// corpus-level safety net (via DocumentValidator, the composed path an
// author's suite actually hits) that these unit tests complement — in
// particular Corpus/Accepted/surface-capture-bare-and-mapping-forms.e2e.yaml,
// which must keep validating unchanged by this narrowing.
using System.Linq;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation.Schema;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Root-schema unit tests for the pre-GA <c>capture</c> value-shape tightening.
/// </summary>
public sealed class CaptureSchemaTests
{
    // ── Accepted forms ───────────────────────────────────────────────────────

    [Fact]
    public void Capture_BareScalar_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: noop
                type: noop.echo
                capture:
                  newUserId: "$.id"
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    [Fact]
    public void Capture_ExplicitJsonPathMapping_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: noop
                type: noop.echo
                capture:
                  customerId: { jsonpath: "$.customer.id" }
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    [Fact]
    public void Capture_ExplicitXPathMapping_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: noop
                type: noop.echo
                capture:
                  orderId: { xpath: "//order/@id" }
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    [Fact]
    public void Capture_MultipleEntriesMixingAllThreeForms_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: noop
                type: noop.echo
                capture:
                  bareJsonPath: "$.hostname"
                  explicitJsonPath: { jsonpath: "$.hostname" }
                  explicitXPath: { xpath: "//hostname" }
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    // ── Rejected shapes ──────────────────────────────────────────────────────

    [Fact]
    public void Capture_BothJsonPathAndXPath_IsRejected()
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
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation.StartsWith("/steps/0/capture/ambiguous", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// Strengthened (feat/close-remaining-surfaces, Part A1): the schema's
    /// two-branch <c>anyOf</c> ('at least one of jsonpath/xpath') was
    /// replaced with <c>minProperties: 1</c> — a single keyword, one error,
    /// no branch-exploration noise. Also pins the corpus fixture
    /// Corpus/Rejected/capture-empty-mapping.e2e.yaml's own header, which
    /// must name the SAME new keyword.
    /// </summary>
    [Fact]
    public void Capture_EmptyMapping_IsRejected()
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
        Assert.Contains("[minProperties]", onlyError.Message, System.StringComparison.Ordinal);
    }

    // ── A1: captureEntry de-branching (composite-branch noise, prong 1) ────────

    /// <summary>
    /// A perfectly valid <c>{ jsonpath: ... }</c> capture entry must never
    /// contribute a spurious "[required] jsonpath"/"[required] xpath" entry
    /// merely because the document fails elsewhere — the OLD two-branch
    /// <c>anyOf</c>'s own noise (see the class header). Now impossible by
    /// construction: <c>minProperties: 1</c> has no second branch to leak
    /// from at all.
    /// </summary>
    [Fact]
    public void Capture_ValidJsonPathEntry_InDocumentFailingElsewhere_NoSpuriousRequiredError()
    {
        const string yaml = """
            steps:
              - id: noop
                type: noop.echo
                capture:
                  orderId: { jsonpath: "$.id" }
              - type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid, "The second step is missing 'id' and must still be rejected.");
        Assert.DoesNotContain(result.Errors, e => e.InstanceLocation.StartsWith("/steps/0/capture/orderId", System.StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.InstanceLocation == "/steps/1");
    }

    [Fact]
    public void Capture_UnknownKey_IsRejected()
    {
        const string yaml = """
            steps:
              - id: noop
                type: noop.echo
                capture:
                  typo: { jsonpat: "$.id" }
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            "A capture mapping with an unrecognised key (e.g. a 'jsonpath' typo) must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation.StartsWith("/steps/0/capture/typo", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Capture_NonScalarExpressionValue_IsRejected()
    {
        const string yaml = """
            steps:
              - id: noop
                type: noop.echo
                capture:
                  bad:
                    jsonpath:
                      nested: true
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            "A capture entry whose 'jsonpath' value is a mapping (not a scalar expression string) must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/steps/0/capture/bad/jsonpath");
    }

    [Fact]
    public void Capture_ValueIsSequence_IsRejected()
    {
        const string yaml = """
            steps:
              - id: noop
                type: noop.echo
                capture:
                  bad: [ "$.id" ]
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            "A capture entry whose value is a sequence (neither a scalar nor a mapping) must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/steps/0/capture/bad");
    }

    // ── Reserved-prefix key rejection (mirrors AstBuilder.CheckReservedPrefix) ──

    /// <summary>
    /// Derived from <see cref="VarKeys"/>'s own constants (feat/close-remaining-surfaces,
    /// Part D) rather than repeated string literals — a const-string
    /// concatenation is itself a compile-time constant, so this is usable
    /// directly in <c>[InlineData]</c>. If a prefix ever changes in
    /// <see cref="VarKeys"/>, this Theory's cases move with it instead of
    /// silently testing a stale value.
    /// </summary>
    [Theory]
    [InlineData(VarKeys.ServicesPrefix + "orders-db")]
    [InlineData(VarKeys.ConnectionsPrefix + "orders-db")]
    [InlineData(VarKeys.OutcomePrefix + "s1")]
    [InlineData(VarKeys.CaptureStatusPrefix + "s1")]
    [InlineData(VarKeys.AttemptsPrefix + "s1")]
    public void Capture_ReservedPrefixKey_IsRejected(string reservedKey)
    {
        var yaml = $$"""
            steps:
              - id: noop
                type: noop.echo
                capture:
                  {{reservedKey}}: "$.id"
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            $"A capture key beginning with an engine-reserved prefix ('{reservedKey}') must be rejected by 'propertyNames' — AstBuilder rejects the same name at build time (see VarKeys).");
        // JsonSchema.Net 9.2.1 attaches the 'propertyNames' subschema's OWN failing
        // keyword ('pattern', from the negative-lookahead regex — not a literal
        // '[propertyNames]' tag) and reports it at the property's own instance
        // location (the value's pointer, not a synthetic key-only pointer).
        // MINOR-7 (second-round gatekeeper finding): the raw JsonSchema.Net
        // message ("The string value is not a match for the indicated
        // regular expression") is unactionable — SchemaErrorCollector now
        // enriches this exact shape, naming the offending key and explaining
        // WHY, generically (no hardcoded prefix list — see
        // FormatReservedPrefixError).
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == $"/steps/0/capture/{reservedKey}" &&
            e.Message.Contains("[pattern]", System.StringComparison.Ordinal) &&
            e.Message.Contains($"'{reservedKey}'", System.StringComparison.Ordinal) &&
            e.Message.Contains("engine-reserved", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// The IDENTICAL guard applies to top-level <c>variables:</c> keys (Part
    /// D symmetry fix) — pinned independently since it lives at a different
    /// schema location (<c>properties/variables/propertyNames</c>, not
    /// <c>$defs/step/properties/capture/propertyNames</c>) and no prior test
    /// exercised it at all.
    /// </summary>
    [Theory]
    [InlineData(VarKeys.ServicesPrefix + "orders-db")]
    [InlineData(VarKeys.AttemptsPrefix + "s1")]
    public void Variables_ReservedPrefixKey_IsRejectedWithActionableMessage(string reservedKey)
    {
        var yaml = $$"""
            variables:
              {{reservedKey}}: "some-value"
            steps:
              - id: noop
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            $"A variables key beginning with an engine-reserved prefix ('{reservedKey}') must be rejected.");
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == $"/variables/{reservedKey}" &&
            e.Message.Contains("[pattern]", System.StringComparison.Ordinal) &&
            e.Message.Contains($"'{reservedKey}'", System.StringComparison.Ordinal) &&
            e.Message.Contains("engine-reserved", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// A key that CONTAINS a reserved prefix, but not at position 0, must not
    /// be caught by the anchored (<c>^(?!prefix)...</c>) negative-lookahead
    /// pattern — the guard is a PREFIX check, not a substring ban.
    /// </summary>
    /// <remarks>
    /// Strengthened (feat/close-remaining-surfaces, Part D): the original
    /// fixture ('mySvcName') contains no reserved prefix substring AT ALL —
    /// 'Svc' differs in case from 'svc::' and carries no '::' — so it never
    /// exercised the anchoring the test claims to prove. These two keys
    /// genuinely embed a full reserved prefix starting at a NON-ZERO offset
    /// ('orders_' + 'conn::', 'x' + '__attempts::'), verified against the
    /// regex to still match (and therefore be accepted) precisely because the
    /// lookaheads are anchored to the start of the string.
    /// </remarks>
    [Theory]
    [InlineData("orders_conn::db")]
    [InlineData("x__attempts::s1")]
    public void Capture_NonReservedKeyThatEmbedsAPrefixOffPositionZero_IsAccepted(string key)
    {
        var yaml = $$"""
            steps:
              - id: noop
                type: noop.echo
                capture:
                  {{key}}: "$.id"
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }
}
