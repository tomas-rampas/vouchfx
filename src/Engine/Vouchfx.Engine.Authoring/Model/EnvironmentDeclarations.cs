using System.Collections.Frozen;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Authoring.Model;

/// <summary>Logical compile-time targets, independent of a provisioning format or catalogue.</summary>
public sealed class EnvironmentDeclarations
{
    /// <summary>Snapshots declarations without retaining a driver's mutable model.</summary>
    public EnvironmentDeclarations(
        IReadOnlyDictionary<string, DeclaredServiceInfo> services,
        IReadOnlyDictionary<string, string> dependencies)
    {
        Services = services.ToFrozenDictionary(p => p.Key,
            p => new DeclaredServiceInfo(Array.AsReadOnly(p.Value.EndpointNames.ToArray())), StringComparer.Ordinal);
        Dependencies = dependencies.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Logical services and the endpoint names accepted by providers.</summary>
    public IReadOnlyDictionary<string, DeclaredServiceInfo> Services { get; }
    /// <summary>Logical dependency names mapped to explicitly declared technology.</summary>
    public IReadOnlyDictionary<string, string> Dependencies { get; }
}
