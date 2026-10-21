using System.Reflection;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;

namespace Vouchfx.Cli;

// An immutable invocation snapshot: discovery, catalogue and execution see identical providers.
internal sealed class ProviderRegistryScope : IDisposable
{
    private static readonly AsyncLocal<ProviderRegistryScope?> s_current = new();
    private readonly ProviderRegistryScope? _previous;
    internal static ProviderRegistryScope? Current => s_current.Value;
    internal Assembly[] Assemblies { get; }
    internal StepKindRegistry Registry { get; }

    internal ProviderRegistryScope(IEnumerable<Assembly> additionalAssemblies)
    {
        Assemblies = ProviderRegistryFactory.CoreProviderAssemblies().Concat(additionalAssemblies).Distinct().ToArray();
        try
        {
            Registry = HostProviderRegistry.Build(Assemblies);
        }
        catch (Exception exception)
        {
            var origins = string.Join(Environment.NewLine, Assemblies.Select(assembly => $"  {assembly.FullName}: {assembly.Location}"));
            throw new ProviderLoadException($"Provider registration refused: {exception.Message}{Environment.NewLine}{origins}", exception);
        }
        _previous = s_current.Value;
        s_current.Value = this;
    }

    public void Dispose() => s_current.Value = _previous;
}
