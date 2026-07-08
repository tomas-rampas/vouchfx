// S02-C-01 — JSON Schema: four top-level sections.
//
// These tests prove the root-language JSON Schema (draft 2020-12) correctly
// validates the four top-level sections of a `.e2e.yaml` file, and that
// <see cref="YamlSchemaValidator"/> surfaces useful, located error messages.
using System.Linq;
using Platform.Engine.Compilation.Schema;
using Xunit;

namespace Platform.Engine.Compilation.Tests;

/// <summary>
/// S02-C-01: Root-language JSON Schema acceptance tests.
/// </summary>
public sealed class RootSchemaTests
{
    // -------------------------------------------------------------------------
    // Minimal valid document
    // -------------------------------------------------------------------------

    /// <summary>
    /// A document containing only the mandatory <c>steps</c> section with one
    /// step that carries <c>id</c> and <c>type</c> must pass schema validation.
    /// </summary>
    [Fact]
    public void Validate_MinimalValidYaml_IsValid()
    {
        const string yaml = """
            steps:
              - id: s1
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid, $"Expected valid but got errors: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
        Assert.Empty(result.Errors);
    }

    // -------------------------------------------------------------------------
    // Missing required 'steps' section
    // -------------------------------------------------------------------------

    /// <summary>
    /// A document with only <c>metadata</c> and no <c>steps</c> section must be
    /// rejected; at least one error must reference the root location or mention
    /// <c>steps</c>, so the author understands what is missing.
    /// </summary>
    [Fact]
    public void Validate_MissingSteps_IsRejectedWithUsefulMessage()
    {
        const string yaml = """
            metadata:
              name: "incomplete test"
              owner: "payments-team"
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);

        // At least one error must mention "steps" or sit at the root pointer.
        var mentionsStepsOrRoot = result.Errors.Any(e =>
            e.Message.Contains("steps", System.StringComparison.OrdinalIgnoreCase) ||
            e.InstanceLocation == "" ||
            e.InstanceLocation == "/");

        Assert.True(mentionsStepsOrRoot,
            $"Expected an error referencing 'steps' or the root, got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    // -------------------------------------------------------------------------
    // Step missing required 'id' field
    // -------------------------------------------------------------------------

    /// <summary>
    /// A step that carries <c>type</c> but omits the required <c>id</c> field must
    /// be rejected; the error location must point at the offending step entry
    /// (i.e. somewhere under <c>/steps/0</c>).
    /// </summary>
    [Fact]
    public void Validate_StepMissingId_IsRejected()
    {
        const string yaml = """
            steps:
              - type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);

        // At least one error must be located at or under /steps/0.
        var pointsAtStep = result.Errors.Any(e =>
            e.InstanceLocation.StartsWith("/steps/0", System.StringComparison.Ordinal) ||
            e.InstanceLocation.StartsWith("/steps", System.StringComparison.Ordinal));

        Assert.True(pointsAtStep,
            $"Expected an error at /steps/0 or /steps, got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    // -------------------------------------------------------------------------
    // Invalid verifyMode enum value
    // -------------------------------------------------------------------------

    /// <summary>
    /// A step that sets <c>verifyMode</c> to an undeclared value (not
    /// <c>IMMEDIATE</c> or <c>RETRY</c>) must be rejected.
    /// </summary>
    [Fact]
    public void Validate_VerifyModeInvalidEnum_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: noop.echo
                verifyMode: SOMETIMES
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    // -------------------------------------------------------------------------
    // Empty or malformed YAML
    // -------------------------------------------------------------------------

    /// <summary>
    /// Passing an empty string or structurally broken YAML must return an invalid
    /// <see cref="SchemaValidationResult"/> rather than propagating an exception.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(": broken: yaml: [unclosed")]
    public void Validate_EmptyOrMalformedYaml_ReturnsInvalidNotThrow(string yaml)
    {
        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    // -------------------------------------------------------------------------
    // Boolean scalar: continueOnFailure must reach the validator as a JSON bool
    // -------------------------------------------------------------------------

    /// <summary>
    /// A step with <c>continueOnFailure: true</c> (an unquoted YAML boolean)
    /// must be accepted.  Before the fix, the <see cref="YamlScalarTypeResolver"/>
    /// left booleans as strings, causing the JSON Schema <c>type: boolean</c>
    /// constraint to reject the document with "[type] Value is 'string' but
    /// should be 'boolean'".
    /// </summary>
    [Fact]
    public void Validate_ContinueOnFailureTrue_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: s1
                type: noop.echo
                continueOnFailure: true
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got errors: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// Likewise, <c>continueOnFailure: false</c> must be accepted.
    /// </summary>
    [Fact]
    public void Validate_ContinueOnFailureFalse_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: s1
                type: noop.echo
                continueOnFailure: false
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got errors: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// A <em>quoted</em> <c>"true"</c> is a YAML string; the schema constrains
    /// <c>continueOnFailure</c> to <c>type: boolean</c>, so a quoted value must
    /// be rejected.  This confirms the resolver only coerces plain scalars and
    /// does not affect quoted strings.
    /// </summary>
    [Fact]
    public void Validate_ContinueOnFailureQuotedString_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: noop.echo
                continueOnFailure: "true"
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            "A quoted \"true\" string must be rejected for a boolean field.");
        Assert.NotEmpty(result.Errors);
    }

    // -------------------------------------------------------------------------
    // Provider-specific extra fields are allowed
    // -------------------------------------------------------------------------

    /// <summary>
    /// A step with provider-specific fields (such as <c>method: GET</c> for an
    /// <c>http.rest</c> step) must not be rejected, because the step schema sets
    /// <c>additionalProperties: true</c>.  This protects the provider composition
    /// task that narrows the schema in a future sprint.
    /// </summary>
    [Fact]
    public void Validate_ProviderSpecificExtraFields_AreAllowed()
    {
        const string yaml = """
            steps:
              - id: s1
                type: http.rest
                method: GET
                path: /health
                expect:
                  status: 200
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got errors: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
        Assert.Empty(result.Errors);
    }

    // -------------------------------------------------------------------------
    // M3: Step id pattern constraint
    // -------------------------------------------------------------------------

    /// <summary>
    /// A step whose <c>id</c> contains a space (or other character outside the
    /// allowed pattern) must be rejected by the schema.  The pattern is
    /// <c>^[A-Za-z_][A-Za-z0-9_-]*$</c>.
    /// </summary>
    [Fact]
    public void Validate_IdWithIllegalCharacter_IsRejected()
    {
        const string yaml = """
            steps:
              - id: "bad id!"
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            "Expected schema validation to reject an id containing spaces and '!'.");
        Assert.NotEmpty(result.Errors);
    }

    /// <summary>
    /// A step id that starts with a digit must be rejected (the pattern requires
    /// the first character to be a letter or underscore).
    /// </summary>
    [Fact]
    public void Validate_IdStartingWithDigit_IsRejected()
    {
        const string yaml = """
            steps:
              - id: "1step"
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            "Expected schema validation to reject an id starting with a digit.");
        Assert.NotEmpty(result.Errors);
    }

    /// <summary>
    /// A step id that uses only letters, digits, underscores, and hyphens —
    /// and begins with a letter — must be accepted.
    /// </summary>
    [Fact]
    public void Validate_ValidIdWithHyphen_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: call-api-v2
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid id 'call-api-v2' to be accepted; errors: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// A step id that starts with an underscore must be accepted (underscores are
    /// valid as the leading character per the pattern).
    /// </summary>
    [Fact]
    public void Validate_IdStartingWithUnderscore_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: _internal_step
                type: noop.echo
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected id '_internal_step' to be accepted; errors: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
        Assert.Empty(result.Errors);
    }

    // -------------------------------------------------------------------------
    // Phase 0 (retire-bare-aliases): step 'type' pattern constraint
    // -------------------------------------------------------------------------

    /// <summary>
    /// A bare (non-dotted) step type must be rejected by the schema's
    /// <c>pattern</c> constraint on <c>type</c>. Bare family aliases were
    /// retired pre-v1.0 (schema still unpublished): the dotted
    /// <c>family.provider</c> form is the only accepted step type.
    /// </summary>
    [Fact]
    public void Validate_BareTypeWithoutDot_IsRejected()
    {
        const string yaml = """
            steps:
              - id: s1
                type: http
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid,
            "Expected schema validation to reject a bare (non-dotted) step type.");
        Assert.NotEmpty(result.Errors);

        // At least one error must be located at the step's 'type' property and
        // reference the 'pattern' keyword, so this test cannot pass on some
        // unrelated schema failure (e.g. a missing 'id').
        var pointsAtTypePattern = result.Errors.Any(e =>
            e.InstanceLocation.EndsWith("/type", System.StringComparison.Ordinal) &&
            e.Message.Contains("[pattern]", System.StringComparison.Ordinal));

        Assert.True(pointsAtTypePattern,
            $"Expected an error at .../type referencing the 'pattern' keyword, got: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
    }

    /// <summary>
    /// The dotted <c>family.provider</c> form must continue to pass schema
    /// validation (the pattern requires, and this satisfies, exactly one dot
    /// separating two lowercase-alphanumeric-hyphen segments).
    /// </summary>
    [Fact]
    public void Validate_DottedType_IsAccepted()
    {
        const string yaml = """
            steps:
              - id: s1
                type: http.rest
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.True(result.IsValid,
            $"Expected valid but got errors: {string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"))}");
        Assert.Empty(result.Errors);
    }
}
