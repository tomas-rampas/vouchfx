// S03-B-03 — Pre-compilation schema validation pass with line context (§8, §13.6).
//
// DocumentValidator is a thin pre-compile gate that delegates to
// SchemaComposer.Validate (the composed-schema path, provider fragments included)
// and then enriches each error's message with a best-effort YAML line number
// derived by walking the JSON-Pointer InstanceLocation against a
// YamlDotNet RepresentationModel parse of the same document.
using System.Linq;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Postgres;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// S03-B-03: <see cref="DocumentValidator"/> pre-compilation validation pass tests.
/// </summary>
public sealed class DocumentValidatorTests
{
    // Re-used registry: built once per test class instance (xUnit creates one
    // instance per test method, so this is effectively per-test but avoids
    // repeating the build call in every test body).
    private static readonly StepKindRegistry _registry =
        StepKindRegistry.BuildAndFreeze(new[] { typeof(HttpRestProvider).Assembly });

    // A second, small multi-provider registry used by the #265 unknown-step-type
    // tests below, so "a known type" can be exercised across more than one
    // registered provider without pulling in the full 25-Core-provider catalogue
    // (that scale is already covered by SchemaFreezeTests/SchemaErrorCollectionAtScaleTests).
    private static readonly StepKindRegistry _multiProviderRegistry =
        StepKindRegistry.BuildAndFreeze(new[]
        {
            typeof(HttpRestProvider).Assembly,
            typeof(DbAssertPostgresProvider).Assembly,
        });

    // ── Valid document ─────────────────────────────────────────────────────────

    /// <summary>
    /// A minimal valid <c>http.rest</c> document must pass the composed-schema
    /// validation and return no errors.
    /// </summary>
    [Fact]
    public void Validate_MinimalValidHttpRestDoc_IsValid()
    {
        const string yaml = """
            steps:
              - id: call-api
                type: http.rest
                target: orders-api
                method: GET
                path: /health
            """;

        var result = DocumentValidator.Validate(yaml, _registry);

        Assert.True(result.IsValid,
            $"Expected valid but got: {FormatErrors(result)}");
        Assert.Empty(result.Errors);
    }

    // ── Missing steps section ──────────────────────────────────────────────────

    /// <summary>
    /// A document that contains only a <c>metadata</c> section and omits the
    /// required <c>steps</c> section must be rejected with at least one error
    /// that mentions <c>steps</c> or sits at the root instance location.
    /// </summary>
    [Fact]
    public void Validate_MissingSteps_IsRejected_WithMessage()
    {
        const string yaml = """
            metadata:
              name: "incomplete test"
              owner: "payments-team"
            """;

        var result = DocumentValidator.Validate(yaml, _registry);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);

        var mentionsStepsOrRoot = result.Errors.Any(e =>
            e.Message.Contains("steps", System.StringComparison.OrdinalIgnoreCase) ||
            e.InstanceLocation == string.Empty ||
            e.InstanceLocation == "/");

        Assert.True(mentionsStepsOrRoot,
            $"Expected an error referencing 'steps' or the root location, got: {FormatErrors(result)}");
    }

    // ── Step missing 'id' — line context ──────────────────────────────────────

    /// <summary>
    /// A step that omits the required <c>id</c> field must be rejected; the
    /// enriched error message must contain a <c>(line N)</c> prefix whose line
    /// number is greater than zero, and the <c>InstanceLocation</c> must point at
    /// or under <c>/steps/0</c>.
    /// </summary>
    [Fact]
    public void Validate_StepMissingId_IsRejected_WithLine()
    {
        // Line 1: steps:
        // Line 2:   - type: noop.echo
        const string yaml = """
            steps:
              - type: noop.echo
            """;

        var result = DocumentValidator.Validate(yaml, _registry);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);

        // At least one error must sit at or under /steps/0.
        var stepError = result.Errors.FirstOrDefault(e =>
            e.InstanceLocation.StartsWith("/steps/0", System.StringComparison.Ordinal) ||
            e.InstanceLocation.StartsWith("/steps", System.StringComparison.Ordinal));

        Assert.NotNull(stepError);
        Assert.Contains("(line ", stepError!.Message, System.StringComparison.Ordinal);

        // Extract the line number and verify it is > 0.
        var lineNumber = ExtractLineNumber(stepError.Message);
        Assert.True(lineNumber > 0,
            $"Expected a positive line number in message: '{stepError.Message}'");
    }

    // ── Bad HTTP method — provider fragment enum fires ─────────────────────────

    /// <summary>
    /// An <c>http.rest</c> step with <c>method: TELEPORT</c> must be rejected by
    /// the composed schema (the provider fragment's <c>enum</c> constraint fires).
    /// The enriched error message must contain a <c>(line N)</c> prefix where N is
    /// greater than zero — proving that the pointer-to-line resolver successfully
    /// addressed the <c>method</c> property node.
    /// </summary>
    [Fact]
    public void Validate_HttpRestBadMethod_IsRejected_WithLine()
    {
        const string yaml = """
            steps:
              - id: bad-step
                type: http.rest
                target: orders-api
                method: TELEPORT
                path: /warp
            """;

        var result = DocumentValidator.Validate(yaml, _registry);

        Assert.False(result.IsValid,
            "Expected validation to fail: method TELEPORT is not in the http.rest enum.");
        Assert.NotEmpty(result.Errors);

        // At least one error message must contain a (line N) prefix with N > 0.
        var errorWithLine = result.Errors.FirstOrDefault(e =>
            e.Message.Contains("(line ", System.StringComparison.Ordinal));

        Assert.NotNull(errorWithLine);

        var lineNumber = ExtractLineNumber(errorWithLine!.Message);
        Assert.True(lineNumber > 0,
            $"Expected a positive resolved line number in message: '{errorWithLine.Message}'");
    }

    // ── Boolean scalar: continueOnFailure ─────────────────────────────────────

    /// <summary>
    /// An <c>http.rest</c> document with <c>continueOnFailure: true</c> on a step
    /// must pass the composed-schema validation.  Before the fix, the YAML scalar
    /// resolver left the plain token <c>true</c> as a string, causing the JSON
    /// Schema <c>type: boolean</c> constraint to reject valid documents.
    /// </summary>
    [Fact]
    public void Validate_HttpRestDoc_ContinueOnFailureTrue_IsValid()
    {
        const string yaml = """
            steps:
              - id: call-api
                type: http.rest
                target: orders-api
                method: GET
                path: /health
                continueOnFailure: true
            """;

        var result = DocumentValidator.Validate(yaml, _registry);

        Assert.True(result.IsValid,
            $"Expected valid but got: {FormatErrors(result)}");
        Assert.Empty(result.Errors);
    }

    // ── Bad verifyMode enum ────────────────────────────────────────────────────

    /// <summary>
    /// A step with <c>verifyMode: SOMETIMES</c> (not in the declared enum) must be
    /// rejected; the result must be invalid with at least one error.
    /// </summary>
    [Fact]
    public void Validate_BadVerifyMode_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: noop.echo
                verifyMode: SOMETIMES
            """;

        var result = DocumentValidator.Validate(yaml, _registry);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    // ── Multi-step: pointer resolves to the correct step ──────────────────────

    /// <summary>
    /// In a two-step document where the SECOND step is invalid, the resolved line
    /// number in the error must be greater than the line on which the first step
    /// begins.  This proves that the JSON-Pointer→YAML-node walk addresses the
    /// correct sequence element rather than always returning the first node.
    /// </summary>
    [Fact]
    public void Validate_LineResolver_PointsAtCorrectStep()
    {
        // Carefully laid out so that the line numbers are deterministic.
        // Line 1:  steps:
        // Line 2:    - id: first-step
        // Line 3:      type: noop.echo
        // Line 4:    - type: noop.echo      ← second step, missing id (invalid)
        const string yaml = """
            steps:
              - id: first-step
                type: noop.echo
              - type: noop.echo
            """;

        var result = DocumentValidator.Validate(yaml, _registry);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);

        // Find the error that points at /steps/1 (the second, invalid step).
        var secondStepError = result.Errors.FirstOrDefault(e =>
            e.InstanceLocation.StartsWith("/steps/1", System.StringComparison.Ordinal));

        Assert.NotNull(secondStepError);
        Assert.Contains("(line ", secondStepError!.Message, System.StringComparison.Ordinal);

        var errorLine = ExtractLineNumber(secondStepError.Message);

        // The first step starts on line 2; the second step must be on a later line.
        Assert.True(errorLine > 2,
            $"Expected the second-step error line to be > 2 (after the first step), " +
            $"but got line {errorLine} in message: '{secondStepError.Message}'");
    }

    // ── Issue #265: unknown step type is not caught by the composed schema ────

    /// <summary>
    /// A step whose ONLY defect is a <c>type</c> that matches no registered
    /// provider must be rejected. Before the fix, the composed schema's
    /// <c>$defs.step.allOf</c> if/then clauses have no <c>else</c> and no closed
    /// enumeration of known types, so a non-matching <c>type</c> satisfies every
    /// clause VACUOUSLY and the document passes schema validation with zero
    /// errors. The rejection message must name the offending type and carry the
    /// same <c>(line N)</c> prefix as every other schema error.
    /// </summary>
    [Fact]
    public void Validate_UnknownStepType_OnlyDefect_IsRejected_WithLineAndMessage()
    {
        const string yaml = """
            steps:
              - id: call-unknown
                type: no-such.provider
                target: orders-api
            """;

        var result = DocumentValidator.Validate(yaml, _registry);

        Assert.False(result.IsValid,
            "A step whose type matches no registered provider must be rejected (#265).");
        Assert.NotEmpty(result.Errors);

        var unknownTypeError = result.Errors.FirstOrDefault(e =>
            e.Message.Contains("no-such.provider", System.StringComparison.Ordinal));

        Assert.NotNull(unknownTypeError);
        Assert.Contains("unknown step type", unknownTypeError!.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(line ", unknownTypeError.Message, System.StringComparison.Ordinal);

        var lineNumber = ExtractLineNumber(unknownTypeError.Message);
        Assert.True(lineNumber > 0,
            $"Expected a positive resolved line number in message: '{unknownTypeError.Message}'");
    }

    /// <summary>
    /// A document whose steps all use KNOWN, registered types and are otherwise
    /// valid must remain valid — the unknown-step-type cross-check must never
    /// produce a false positive against a real provider. Exercised across two
    /// distinct registered kinds (<c>http.rest</c> and <c>db-assert.postgres</c>)
    /// so the check is proven to key off the registry, not a single hardcoded name.
    /// </summary>
    [Fact]
    public void Validate_KnownStepTypes_AcrossMultipleProviders_IsValid()
    {
        const string yaml = """
            steps:
              - id: call-api
                type: http.rest
                target: orders-api
                method: GET
                path: /health
              - id: check-row
                type: db-assert.postgres
                target: orders-db
                query: SELECT 1
                expect:
                  rowCount: 1
            """;

        var result = DocumentValidator.Validate(yaml, _multiProviderRegistry);

        Assert.True(result.IsValid,
            $"Expected valid but got: {FormatErrors(result)}");
        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// A document containing BOTH an unknown step type and an unrelated, genuine
    /// schema violation must report both — the unknown-type cross-check must not
    /// mask or replace a real schema error, and vice versa.
    /// </summary>
    [Fact]
    public void Validate_UnknownStepTypeAndOtherViolation_BothReported()
    {
        const string yaml = """
            steps:
              - id: bad-type-step
                type: no-such.provider
              - id: bad-method-step
                type: http.rest
                target: orders-api
                method: TELEPORT
                path: /warp
            """;

        var result = DocumentValidator.Validate(yaml, _registry);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);

        var unknownTypeError = result.Errors.FirstOrDefault(e =>
            e.Message.Contains("no-such.provider", System.StringComparison.Ordinal));
        Assert.NotNull(unknownTypeError);
        Assert.Contains("(line ", unknownTypeError!.Message, System.StringComparison.Ordinal);

        var badMethodError = result.Errors.FirstOrDefault(e =>
            e.InstanceLocation.StartsWith("/steps/1", System.StringComparison.Ordinal));
        Assert.NotNull(badMethodError);
        Assert.Contains("(line ", badMethodError!.Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// In a three-step document where only the MIDDLE step has an unknown type,
    /// exactly one error must be reported, and it must resolve to the middle
    /// step's line — proving the unknown-type walk correctly addresses the step
    /// at its own index rather than flagging every step or the wrong one.
    /// </summary>
    [Fact]
    public void Validate_MultiStep_OnlyUnknownTypeFlagged_WithCorrectLine()
    {
        // Line 1:  steps:
        // Line 2:    - id: first-step
        // Line 3:      type: http.rest
        // Line 4:      target: orders-api
        // Line 5:      method: GET
        // Line 6:      path: /health
        // Line 7:    - id: second-step
        // Line 8:      type: no-such.provider      ← unknown type (invalid)
        // Line 9:    - id: third-step
        // Line 10:     type: http.rest
        // Line 11:     target: orders-api
        // Line 12:     method: GET
        // Line 13:     path: /health
        const string yaml = """
            steps:
              - id: first-step
                type: http.rest
                target: orders-api
                method: GET
                path: /health
              - id: second-step
                type: no-such.provider
              - id: third-step
                type: http.rest
                target: orders-api
                method: GET
                path: /health
            """;

        var result = DocumentValidator.Validate(yaml, _registry);

        Assert.False(result.IsValid);
        var onlyError = Assert.Single(result.Errors);

        Assert.Equal("/steps/1/type", onlyError.InstanceLocation);
        Assert.Contains("no-such.provider", onlyError.Message, System.StringComparison.Ordinal);

        var lineNumber = ExtractLineNumber(onlyError.Message);
        Assert.Equal(8, lineNumber);
    }

    /// <summary>
    /// A step whose <c>type</c> is a BARE family name (e.g. <c>http</c>, with no
    /// dotted provider) violates the root schema's own
    /// <c>^[a-z0-9-]+\.[a-z0-9-]+$</c> pattern — a realistic authoring mistake,
    /// since bare family aliases were retired pre-v1.0 and migrating authors may
    /// still write them. The schema pass already reports a <c>[pattern]</c> error
    /// at <c>/steps/0/type</c>; the unknown-step-type cross-check must defer to
    /// it rather than emitting a SECOND error at the same instance location.
    /// </summary>
    [Fact]
    public void Validate_BareFamilyTypeName_IsRejectedOnce_ByPatternOnly_NotDoubleReported()
    {
        const string yaml = """
            steps:
              - id: call-api
                type: http
                target: orders-api
            """;

        var result = DocumentValidator.Validate(yaml, _registry);

        Assert.False(result.IsValid);

        // Exactly one error in the whole document — the schema's own
        // [pattern] violation — never a second, duplicate unknown-step-type
        // finding at the same /steps/0/type pointer.
        var onlyError = Assert.Single(result.Errors);
        Assert.Equal("/steps/0/type", onlyError.InstanceLocation);
        Assert.Contains("pattern", onlyError.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unknown step type", onlyError.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A step whose <c>type</c> is wrongly cased (e.g. <c>HTTP.REST</c>) violates
    /// the same root-schema pattern (lowercase alphanumerics and hyphens only) —
    /// must be reported exactly once, by the schema, never duplicated by the
    /// unknown-step-type cross-check.
    /// </summary>
    [Fact]
    public void Validate_WronglyCasedType_IsRejectedOnce_ByPatternOnly_NotDoubleReported()
    {
        const string yaml = """
            steps:
              - id: call-api
                type: HTTP.REST
                target: orders-api
            """;

        var result = DocumentValidator.Validate(yaml, _registry);

        Assert.False(result.IsValid);

        var onlyError = Assert.Single(result.Errors);
        Assert.Equal("/steps/0/type", onlyError.InstanceLocation);
        Assert.Contains("pattern", onlyError.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unknown step type", onlyError.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static string FormatErrors(SchemaValidationResult result) =>
        string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"));

    /// <summary>
    /// Extracts the integer from a <c>(line N)</c> prefix in a message string.
    /// Returns -1 if the prefix is absent or the number cannot be parsed.
    /// </summary>
    private static int ExtractLineNumber(string message)
    {
        // Expected prefix format: "(line N) …"
        const string prefix = "(line ";
        var start = message.IndexOf(prefix, System.StringComparison.Ordinal);
        if (start < 0)
            return -1;

        var numStart = start + prefix.Length;
        var closing = message.IndexOf(')', numStart);
        if (closing < 0)
            return -1;

        var numStr = message[numStart..closing];
        return int.TryParse(numStr, out var n) ? n : -1;
    }
}
