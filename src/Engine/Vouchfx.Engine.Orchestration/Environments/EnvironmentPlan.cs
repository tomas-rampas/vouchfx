using Vouchfx.Engine.Abstractions.Security;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring.Model;

namespace Vouchfx.Engine.Orchestration.Environments;

public enum EnvironmentReusePolicy { Reuse, Recreate }
public enum EnvironmentOwnership { Owned, Borrowed }

/// <summary>Run-owned execution capabilities; secret resolution records into the existing scrub ledger.</summary>
public sealed record EnvironmentStartContext(TopologyRequest Request,
    ISecurityConfigurationAccessor? Security, SecurityPathDisclosureLedger? Paths, ISecretAccessor Secrets);

/// <summary>State permissions are separate from logical dependency identity.</summary>
public sealed record EnvironmentStatePolicy(
    EnvironmentOwnership Ownership,
    EnvironmentReusePolicy Reuse,
    bool CanReset)
{
    public static EnvironmentStatePolicy Inline { get; } = new(
        EnvironmentOwnership.Owned, EnvironmentReusePolicy.Reuse, true);
    public static EnvironmentStatePolicy FreshOwned { get; } = new(
        EnvironmentOwnership.Owned, EnvironmentReusePolicy.Recreate, false);
}

/// <summary>A pure bound plan; only StartAsync may resolve secrets or perform external operations.</summary>
public sealed class EnvironmentPlan
{
    private readonly Func<EnvironmentStartContext, CancellationToken, Task<IKeptTopology>> _start;
    internal bool PreserveInnerLease { get; init; }

    public EnvironmentPlan(
        EnvironmentDeclarations declarations,
        string sourceIdentity,
        EnvironmentStatePolicy statePolicy,
        Func<EnvironmentStartContext, CancellationToken, Task<IKeptTopology>> start,
        TimeSpan? cleanupTimeout = null,
        IReadOnlyList<SecuredTarget>? declaredSecurity = null,
        IReadOnlyList<string>? inputFiles = null)
    {
        Declarations = declarations;
        SourceIdentity = sourceIdentity;
        StatePolicy = statePolicy;
        _start = start;
        CleanupTimeout = cleanupTimeout ?? TimeSpan.FromSeconds(30);
        DeclaredSecurity = Array.AsReadOnly(declaredSecurity?.ToArray() ?? Array.Empty<SecuredTarget>());
        InputFiles = Array.AsReadOnly(inputFiles?.ToArray() ?? Array.Empty<string>());
        if (CleanupTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
    }

    public EnvironmentDeclarations Declarations { get; }
    public string SourceIdentity { get; }
    public EnvironmentStatePolicy StatePolicy { get; }
    public TimeSpan CleanupTimeout { get; }
    public IReadOnlyList<SecuredTarget> DeclaredSecurity { get; }
    /// <summary>Authoring files whose changes invalidate bindings and trigger generic watch acquisition.</summary>
    public IReadOnlyList<string> InputFiles { get; }

    public async Task<IKeptTopology> StartAsync(TopologyRequest request,
        ISecurityConfigurationAccessor? security, SecurityPathDisclosureLedger? ledger,
        CancellationToken cancellationToken, Action<string>? reportCleanup = null,
        ISecretAccessor? secrets = null)
    {
        var topology = await _start(new EnvironmentStartContext(request, security, ledger,
            secrets ?? NullSecretAccessor.Instance), cancellationToken).ConfigureAwait(false);
        if (PreserveInnerLease) return topology;
        return new PlannedTopologyLease(topology, StatePolicy, CleanupTimeout, reportCleanup);
    }
}

/// <summary>Bind reads authoring inputs only: no subprocess, daemon or secret resolution.</summary>
public interface IEnvironmentDriver
{
    string Id { get; }
    EnvironmentPlan Bind(EnvironmentSourceSpec source, string suiteDirectory);
}
