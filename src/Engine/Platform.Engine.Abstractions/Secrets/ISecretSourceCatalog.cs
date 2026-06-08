// Platform.Engine.Abstractions — ISecretSourceCatalog + SecretSourceCatalog
// (S05-B-02, §17).
//
// The catalog indexes the run's resolvers by source identifier. It is built once
// per run in the Default ALC (by ScenarioRunner) and passed by reference into the
// ScriptGlobalVariables boundary via the accessor — never via a static. Adding
// Vault in Sprint 8 means adding one more ISecretResolver to the catalog's
// constructor input; no boundary or interface change is required.

using System;
using System.Collections.Generic;

namespace Platform.Engine.Abstractions.Secrets;

/// <summary>
/// The set of secret <see cref="ISecretResolver"/>s configured for a single run,
/// indexed by source identifier (§17).
/// </summary>
public interface ISecretSourceCatalog
{
    /// <summary>
    /// Looks up the resolver for <paramref name="source"/> (ordinal comparison).
    /// </summary>
    /// <param name="source">The source identifier (e.g. <c>env</c>).</param>
    /// <param name="resolver">
    /// On success the matching resolver; otherwise <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a resolver is registered for the source.
    /// </returns>
    bool TryGet(string source, out ISecretResolver? resolver);

    /// <summary>
    /// The source identifiers known to this catalog, in registration order.
    /// </summary>
    IReadOnlyCollection<string> Sources { get; }
}

/// <summary>
/// The default <see cref="ISecretSourceCatalog"/>: indexes a fixed set of resolvers
/// by their <see cref="ISecretResolver.Source"/> using ordinal comparison.
/// </summary>
/// <remarks>
/// A per-run instance with no static state (§5). Constructed by the runner in the
/// Default ALC; passed into the boundary by reference.
/// </remarks>
public sealed class SecretSourceCatalog : ISecretSourceCatalog
{
    private readonly Dictionary<string, ISecretResolver> _bySource;
    private readonly List<string> _orderedSources;

    /// <summary>
    /// Initialises a new catalog from <paramref name="resolvers"/>.
    /// </summary>
    /// <param name="resolvers">
    /// The resolvers to register; must not be <see langword="null"/>. The first
    /// resolver registered for a given source wins; later duplicates are ignored.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="resolvers"/> is <see langword="null"/>.
    /// </exception>
    public SecretSourceCatalog(IEnumerable<ISecretResolver> resolvers)
    {
        ArgumentNullException.ThrowIfNull(resolvers);

        _bySource = new Dictionary<string, ISecretResolver>(StringComparer.Ordinal);
        _orderedSources = new List<string>();

        foreach (var resolver in resolvers)
        {
            if (resolver is null)
            {
                continue;
            }

            if (_bySource.TryAdd(resolver.Source, resolver))
            {
                _orderedSources.Add(resolver.Source);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> Sources => _orderedSources;

    /// <inheritdoc />
    public bool TryGet(string source, out ISecretResolver? resolver)
    {
        if (source is not null && _bySource.TryGetValue(source, out var found))
        {
            resolver = found;
            return true;
        }

        resolver = null;
        return false;
    }
}
