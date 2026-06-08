// S03-B-03 — Pre-compilation schema validation pass with line context (§8, §13.6).
//
// DocumentValidator is a thin pre-compile gate that delegates to
// SchemaComposer.Validate (the composed-schema path, provider fragments included)
// and then enriches each error's message with a best-effort YAML line number
// derived by walking the JSON-Pointer InstanceLocation against a
// YamlDotNet RepresentationModel parse of the same document.
using System.Linq;
using Platform.Engine.Compilation.Schema;
using Platform.Sdk;
using Platform.Steps.HttpRest;
using Xunit;

namespace Platform.Engine.Compilation.Tests;

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
