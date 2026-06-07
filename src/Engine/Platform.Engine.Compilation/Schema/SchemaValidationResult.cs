// S02-C-01 — JSON Schema validation result types (§8).
namespace Platform.Engine.Compilation.Schema;

/// <summary>
/// The outcome of validating a <c>.e2e.yaml</c> document against the root language schema.
/// </summary>
/// <param name="IsValid">
/// <see langword="true"/> when the document satisfies all schema constraints;
/// <see langword="false"/> otherwise.
/// </param>
/// <param name="Errors">
/// The located validation errors, empty when <paramref name="IsValid"/> is
/// <see langword="true"/>.
/// </param>
public sealed record SchemaValidationResult(bool IsValid, IReadOnlyList<SchemaValidationError> Errors)
{
    /// <summary>
    /// A pre-allocated result representing a fully valid document.
    /// </summary>
    public static SchemaValidationResult Valid { get; } =
        new(true, Array.Empty<SchemaValidationError>());

    /// <summary>
    /// Creates an invalid result containing the supplied <paramref name="errors"/>.
    /// </summary>
    /// <param name="errors">One or more located validation errors.</param>
    /// <returns>An invalid <see cref="SchemaValidationResult"/>.</returns>
    public static SchemaValidationResult Invalid(params SchemaValidationError[] errors) =>
        new(false, errors);
}

/// <summary>
/// A single located validation error produced by schema evaluation.
/// </summary>
/// <param name="InstanceLocation">
/// A JSON Pointer (RFC 6901) that identifies the node in the document which
/// failed validation, for example <c>/steps/0</c>.  The empty string denotes
/// the document root.
/// </param>
/// <param name="Message">
/// A human-readable description of the constraint that was violated.
/// </param>
public sealed record SchemaValidationError(string InstanceLocation, string Message);
