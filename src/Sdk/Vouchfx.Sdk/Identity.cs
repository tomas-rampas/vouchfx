// Vouchfx.Sdk — provider-authoring contract surface (§13).
// This file declares the identity and metadata records for a step provider.
namespace Vouchfx.Sdk;

/// <summary>
/// Identifies the step kind a provider implements, composed of a family (intent)
/// and a provider (technology), e.g. <c>Family="db-assert"</c>,
/// <c>Provider="postgres"</c>.
/// </summary>
/// <remarks>
/// <para>
/// Step kind strings take the form <c>&lt;family&gt;.&lt;provider&gt;</c>
/// as defined in the YAML DSL (§5) and the provider architecture (§13).
/// </para>
/// <para>
/// <see cref="StepKindId"/> is the validated identity token for the registry.
/// Broader semantic validation — duplicate detection, alias resolution, and
/// family-level constraints — lives in the <c>StepKindRegistry</c> (Sprint 2).
/// </para>
/// </remarks>
public sealed record StepKindId
{
    /// <summary>
    /// The step family, representing the intent or capability class,
    /// e.g. <c>db-assert</c>, <c>mq-publish</c>.
    /// </summary>
    public string Family { get; }

    /// <summary>
    /// The technology implementation of the family,
    /// e.g. <c>postgres</c>, <c>kafka</c>.
    /// </summary>
    public string Provider { get; }

    /// <summary>
    /// Initialises a new <see cref="StepKindId"/> with the given family and
    /// provider strings.
    /// </summary>
    /// <param name="family">
    /// The step family.  Must not be <see langword="null"/> or whitespace.
    /// The language schema enforces that the step <c>type</c> field matches
    /// <c>^[a-z0-9-]+\.[a-z0-9-]+$</c> — lowercase alphanumerics and hyphens
    /// only; a kind outside that charset (uppercase letters, underscores,
    /// etc.) would register but never be reachable from YAML. That pattern
    /// alone still permits leading, trailing, or repeated hyphens: the
    /// stricter rule that family and provider names use single, interior
    /// hyphens as word separators is a naming convention (blueprint §13),
    /// not something this schema pattern enforces.
    /// </param>
    /// <param name="provider">
    /// The technology provider.  Must not be <see langword="null"/> or whitespace.
    /// The same schema-enforced charset and §13 hyphen convention as
    /// <paramref name="family"/> apply.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="family"/> or <paramref name="provider"/> is
    /// <see langword="null"/> or whitespace.
    /// </exception>
    public StepKindId(string family, string provider)
    {
        if (string.IsNullOrWhiteSpace(family))
            throw new ArgumentException("Family must not be null or whitespace.", nameof(family));
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Provider must not be null or whitespace.", nameof(provider));

        Family = family;
        Provider = provider;
    }
}

/// <summary>
/// Descriptive metadata emitted by a provider to allow the engine and
/// tooling to surface version compatibility information and attribution.
/// </summary>
/// <param name="Version">
/// The semantic version of the provider itself, e.g. <c>"1.0.0"</c>.
/// </param>
/// <param name="MinEngineVersion">
/// The minimum engine version required to host this provider,
/// e.g. <c>"1.0.0"</c>.
/// </param>
/// <param name="License">
/// The SPDX licence identifier for the provider, e.g. <c>"Apache-2.0"</c>.
/// </param>
/// <param name="Authors">
/// One or more author strings identifying the maintainers of this provider.
/// Must not be <see langword="null"/> or empty.
/// </param>
public sealed record ProviderMetadata(
    string Version,
    string MinEngineVersion,
    string License,
    IReadOnlyList<string> Authors);
