// Platform.Engine.Abstractions — ISecretAccessor + SecretAccessor + NullSecretAccessor
// (S05-B-02, §17).
//
// ISecretAccessor is THE type the emitted CSX names at runtime: the script reaches
// the secrets subsystem only through ScriptGlobalVariables.Secrets (an instance),
// and that property's type is ISecretAccessor. RoslynScriptCompiler references
// Platform.Engine.Abstractions, so the emitted script can name ISecretAccessor and
// SecretString directly — which is why these types live here, not in the Sdk.
//
// The accessor takes a reference token (raw "${secret:env/API}" or bare "env/API"),
// parses it via SecretReference, routes to the catalog's resolver, and returns a
// redacted SecretString. Resolution happens ONLY here, at execution time (§17).

using System;
using System.Collections.Generic;

namespace Platform.Engine.Abstractions.Secrets;

/// <summary>
/// The execution-time entry point the emitted script uses to resolve a secret
/// reference into a redacted <see cref="SecretString"/> (§17).
/// </summary>
/// <remarks>
/// Reached from the script only via the instance property
/// <c>ScriptGlobalVariables.Secrets</c>; never via a static (which would root the
/// collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, §5).
/// </remarks>
public interface ISecretAccessor
{
    /// <summary>
    /// Resolves <paramref name="reference"/> into a redacted <see cref="SecretString"/>.
    /// </summary>
    /// <param name="reference">
    /// Either a raw token <c>${secret:source/path}</c> or a bare <c>source/path</c>.
    /// </param>
    /// <returns>The resolved value, redacted at the source.</returns>
    /// <exception cref="SecretResolutionException">
    /// Thrown when the reference is malformed, names an unknown source, or the value
    /// is absent at the source.
    /// </exception>
    SecretString Resolve(string reference);
}

/// <summary>
/// The default <see cref="ISecretAccessor"/>: parses a reference and routes it to
/// the matching resolver in an <see cref="ISecretSourceCatalog"/> (§17).
/// </summary>
/// <remarks>
/// A per-run instance built in the Default ALC and passed into the boundary by
/// reference. No static state (§5).
/// </remarks>
public sealed class SecretAccessor : ISecretAccessor
{
    private readonly ISecretSourceCatalog _catalog;

    /// <summary>
    /// Initialises a new accessor over <paramref name="catalog"/>.
    /// </summary>
    /// <param name="catalog">The run's source catalog; must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="catalog"/> is <see langword="null"/>.
    /// </exception>
    public SecretAccessor(ISecretSourceCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <inheritdoc />
    public SecretString Resolve(string reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            throw new SecretResolutionException(
                string.Empty,
                string.Empty,
                "an empty string was passed where a secret reference " +
                "'${secret:source/path}' was expected.");
        }

        // Accept both the raw token "${secret:env/API}" and the bare "env/API".
        // TryParse handles a complete, standalone token; a bare "source/path" is
        // normalised into a token before parsing so the single grammar (S05-B-01)
        // is the only place the syntax is defined.
        var token = reference.StartsWith(SecretReference.Sigil, StringComparison.Ordinal)
            ? reference
            : $"{SecretReference.Sigil}{reference}}}";

        if (!SecretReference.TryParse(token, out var parsed) || parsed is null)
        {
            throw new SecretResolutionException(
                string.Empty,
                string.Empty,
                $"the secret reference '{reference}' is malformed; the expected form is " +
                "'${secret:source/path}' (for example '${secret:env/API_TOKEN}').");
        }

        if (!_catalog.TryGet(parsed.Source, out var resolver) || resolver is null)
        {
            throw new SecretResolutionException(
                parsed.Source,
                parsed.Path,
                $"the secret reference '{parsed.Raw}' names an unknown source " +
                $"'{parsed.Source}'; configured sources are: {FormatSources(_catalog.Sources)}.");
        }

        return resolver.Resolve(parsed.Path);
    }

    private static string FormatSources(IReadOnlyCollection<string> sources)
        => sources.Count == 0 ? "(none configured)" : string.Join(", ", sources);
}

/// <summary>
/// An <see cref="ISecretAccessor"/> for runs with no secret sources configured: any
/// attempt to resolve a secret throws <see cref="SecretResolutionException"/>.
/// </summary>
/// <remarks>
/// Backs the legacy <c>ScriptGlobalVariables</c> constructors so existing call sites
/// and tests keep compiling and behave safely — a scenario that references a secret
/// without a configured source fails loudly rather than silently exposing nothing.
/// </remarks>
public sealed class NullSecretAccessor : ISecretAccessor
{
    /// <summary>
    /// A shared, stateless instance for the legacy <c>ScriptGlobalVariables</c>
    /// constructors to reuse instead of allocating a new accessor per call.
    /// </summary>
    /// <remarks>
    /// This static lives on <see cref="NullSecretAccessor"/> (a plain abstraction
    /// type), NOT on the host↔script boundary type <c>ScriptGlobalVariables</c>: the
    /// boundary must declare no static state, or it would root the collectible
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> and defeat the memory
    /// model (§5). <see cref="NullSecretAccessor"/> is itself stateless, so a shared
    /// instance is safe.
    /// </remarks>
    public static readonly NullSecretAccessor Instance = new();

    /// <inheritdoc />
    /// <exception cref="SecretResolutionException">Always.</exception>
    public SecretString Resolve(string reference)
        => throw new SecretResolutionException(
            string.Empty,
            string.Empty,
            "no secret sources are configured for this run; a step referenced a " +
            "'${secret:source/path}' value but the engine was started without a " +
            "secret source catalogue.");
}
