// Vouchfx.Engine.Compilation — Known dependency kinds for suite scaffold (Spec B).
//
// Mirrors EnvironmentMapper.s_dependencyRegistry keys in Vouchfx.Engine.Orchestration.
// Kept as a hardcoded public set here so Compilation (scaffold) does not take a
// ProjectReference on Orchestration; when a new dependency kind is added to the
// mapper, update this list in the same change.

namespace Vouchfx.Engine.Compilation.Scaffold;

/// <summary>
/// Dependency kinds the engine's topology mapper supports (and therefore scaffold accepts).
/// </summary>
/// <remarks>
/// Values are the lowercase <c>environment.dependencies.&lt;name&gt;.type</c> tokens.
/// Lookup is ordinal-ignore-case via <see cref="Contains"/>.
/// </remarks>
public static class KnownDependencyKinds
{
    /// <summary>
    /// Canonical ordered list of supported dependency kinds (matches EnvironmentMapper).
    /// </summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        "postgres",
        "sqlserver",
        "mysql",
        "mongodb",
        "redis",
        "elasticsearch",
        "rabbitmq",
        "nats",
        "kafka",
        "mailpit",
        "azureservicebus",
        "dynamodb",
        "minio",
    };

    private static readonly HashSet<string> s_set =
        new(All, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="kind"/> is a supported
    /// dependency type token.
    /// </summary>
    public static bool Contains(string? kind) =>
        !string.IsNullOrWhiteSpace(kind) && s_set.Contains(kind);
}
