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
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == "/steps/0/capture/neither");
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

    [Theory]
    [InlineData("svc::orders-db")]
    [InlineData("conn::orders-db")]
    [InlineData("__outcome::s1")]
    [InlineData("__capture_status::s1")]
    [InlineData("__attempts::s1")]
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
        Assert.Contains(result.Errors, e =>
            e.InstanceLocation == $"/steps/0/capture/{reservedKey}" &&
            e.Message.Contains("[pattern]", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Capture_NonReservedKeyThatSharesAPrefixSubstring_IsAccepted()
    {
        // A key that merely CONTAINS a reserved substring, but does not START
        // with it, must not be caught by the propertyNames pattern — the guard
        // is a prefix check, not a substring ban.
        const string yaml = """
            steps:
              - id: noop
                type: noop.echo
                capture:
                  mySvcName: "$.id"
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }
}
