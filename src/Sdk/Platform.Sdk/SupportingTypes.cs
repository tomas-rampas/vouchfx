// Platform.Sdk — provider-authoring contract surface (§13).
// This file declares the supporting value types consumed by the provider interfaces.
namespace Platform.Sdk;

/// <summary>
/// Wraps a JSON Schema fragment (a JSON string) that a binder contributes
/// to the aggregate step-kind schema used for YAML validation and IDE
/// completions (§8, §13.3).
/// </summary>
/// <param name="Json">
/// A valid JSON string representing the schema fragment for this provider's
/// step model fields.
/// </param>
public sealed record JsonSchemaFragment(string Json);

/// <summary>
/// Carries the outcome of a provider validation pass, including any
/// human-readable error messages surfaced to the test author.
/// </summary>
/// <param name="IsValid">
/// <see langword="true"/> when validation passed with no errors;
/// <see langword="false"/> when at least one error was found.
/// </param>
/// <param name="Errors">
/// The list of error messages describing validation failures.
/// Empty when <see cref="IsValid"/> is <see langword="true"/>.
/// </param>
public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    /// <summary>
    /// A pre-built <see cref="ValidationResult"/> representing a successful
    /// validation with no errors.
    /// </summary>
    public static readonly ValidationResult Success =
        new(IsValid: true, Errors: Array.Empty<string>());

    /// <summary>
    /// Creates a failed <see cref="ValidationResult"/> from one or more
    /// error messages.
    /// </summary>
    /// <param name="errors">
    /// One or more non-empty error messages describing why validation failed.
    /// </param>
    /// <returns>
    /// A <see cref="ValidationResult"/> with <see cref="IsValid"/> set to
    /// <see langword="false"/> and <see cref="Errors"/> populated from
    /// <paramref name="errors"/>.
    /// </returns>
    public static ValidationResult Failure(params string[] errors) =>
        new(IsValid: false, Errors: errors);
}

/// <summary>
/// Declares an infrastructure resource required by a provider step, such as
/// a database or message-broker instance.  The engine uses this information
/// when constructing the Aspire topology for a test suite (§4, §13.4).
/// </summary>
/// <param name="Family">
/// The resource family, e.g. <c>"postgres"</c>, <c>"kafka"</c>.
/// </param>
/// <param name="Name">
/// A logical name for this resource instance, unique within the test suite
/// topology.
/// </param>
/// <param name="Image">
/// An optional Docker image reference (including tag) to use for the
/// resource container.  When <see langword="null"/> the engine selects its
/// default image for the given family.
/// </param>
public sealed record ResourceRequirement(string Family, string Name, string? Image);

/// <summary>
/// Describes a runtime service that a provider wishes to register with the
/// engine's internal DI container.
/// </summary>
/// <remarks>
/// This is <strong>Platform.Sdk</strong>'s own type.  Do <em>not</em> import
/// <c>Microsoft.Extensions.DependencyInjection</c> into the SDK surface.
/// The name <c>ProviderServiceDescriptor</c> is intentional: it avoids
/// collision with <c>Microsoft.Extensions.DependencyInjection.ServiceDescriptor</c>,
/// which the engine host references alongside this type.
/// </remarks>
/// <param name="Name">
/// A logical name uniquely identifying this service registration.
/// </param>
/// <param name="ServiceType">
/// The <see cref="Type"/> of the service to register.
/// </param>
public sealed record ProviderServiceDescriptor(string Name, Type ServiceType);
