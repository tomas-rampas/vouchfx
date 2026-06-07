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
}
