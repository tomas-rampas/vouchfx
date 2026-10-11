using System.Collections.Frozen;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Orchestration.Environments;

/// <summary>Explicit immutable source selection, shared by validation, run and watch.</summary>
public sealed class EnvironmentDriverRegistry
{
    private readonly FrozenDictionary<string, IEnvironmentDriver> _drivers;
    private readonly Func<EnvironmentStartContext, CancellationToken, Task<IKeptTopology>>? _inlineStart;

    public EnvironmentDriverRegistry(IEnumerable<IEnvironmentDriver> drivers)
        => _drivers = drivers.ToFrozenDictionary(d => d.Id, StringComparer.Ordinal);

    // Test-only internal seam: retain normal inline validation/declarations without launching DCP.
    internal EnvironmentDriverRegistry(IEnumerable<IEnvironmentDriver> drivers,
        Func<EnvironmentStartContext, CancellationToken, Task<IKeptTopology>> inlineStart) : this(drivers)
        => _inlineStart = inlineStart;

    public static EnvironmentDriverRegistry BuiltIn { get; } = new(new IEnvironmentDriver[] { new ComposeEnvironmentDriver() });

    public EnvironmentPlan Bind(EnvironmentSpec? environment, string? suiteDirectory)
    {
        if (environment?.Source is not { } source)
        {
            EnvironmentImageValidator.Validate(environment);
            var services = environment?.Services?.ToDictionary(p => p.Key,
                p => new DeclaredServiceInfo(ServiceEndpointNaming.DeclaredEndpointNames(p.Value)), StringComparer.Ordinal)
                ?? new Dictionary<string, DeclaredServiceInfo>();
            var dependencies = environment?.Dependencies?.ToDictionary(p => p.Key, p => p.Value.Type, StringComparer.Ordinal)
                ?? new Dictionary<string, string>();
            return new EnvironmentPlan(new EnvironmentDeclarations(services, dependencies), "inline",
                EnvironmentStatePolicy.Inline,
                _inlineStart ?? (async (context, ct) => await context.Request.StartAsync(context.Security, context.Paths, ct).ConfigureAwait(false)),
                declaredSecurity: SecuredTargets.Enumerate(environment).ToArray())
            { PreserveInnerLease = true };
        }

        if (environment.Services is { Count: > 0 } || environment.Dependencies is { Count: > 0 }
            || environment.Seed is not null || environment.ImageRegistry is not null || environment.ImagePullPolicy is not null)
        {
            throw new ArgumentException("environment.source cannot be combined with inline services, dependencies, seed or image settings. Configure the selected driver in its own file.");
        }

        if (!_drivers.TryGetValue(source.Driver, out var driver))
        {
            throw new ArgumentException($"environment.source.driver: unknown driver '{source.Driver}'. Registered drivers: {string.Join(", ", _drivers.Keys.Order())}.");
        }

        var plan = driver.Bind(source, suiteDirectory ?? Directory.GetCurrentDirectory());
        if (plan.DeclaredSecurity.Count != 0)
            throw new EnvironmentBindingException("environment.source: external-driver security declarations are unsupported in this slice. Use an inline environment for confirmed TLS/mTLS; no environment was started.", plan.InputFiles);
        if (plan.StatePolicy.Ownership != EnvironmentOwnership.Owned)
            throw new EnvironmentBindingException("environment.source: borrowed infrastructure is unsupported in this slice. Select an owned isolated test environment; no environment was started.", plan.InputFiles);
        return plan;
    }
}
