namespace Vouchfx.Engine.Orchestration.Environments;

/// <summary>Caller lifecycle: bounded/idempotent cleanup never erases a primary verdict.</summary>
internal sealed class PlannedTopologyLease : IKeptTopology
{
    private readonly IKeptTopology _inner;
    private readonly TimeSpan _timeout;
    private readonly Action<string>? _reportCleanup;
    private readonly object _disposeLock = new();
    private Task? _disposal;

    internal PlannedTopologyLease(IKeptTopology inner, EnvironmentStatePolicy policy,
        TimeSpan timeout, Action<string>? reportCleanup)
    {
        _inner = inner;
        StatePolicy = policy;
        _timeout = timeout;
        _reportCleanup = reportCleanup;
    }

    public EnvironmentStatePolicy StatePolicy { get; }
    public IReadOnlyList<SecurityConfirmation> SecurityConfirmations => _inner.SecurityConfirmations;
    public IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices => _inner.EndpointSelectionNotices;
    public IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices => _inner.EndpointTrustNotices;
    public IReadOnlyDictionary<string, object> DiscoveredServices => _inner.DiscoveredServices;
    public IReadOnlyList<string> DependencyNames => _inner.DependencyNames;
    public IReadOnlyDictionary<string, string> DependencyTypes => _inner.DependencyTypes;
    public Task ReseedAsync(CancellationToken cancellationToken)
        => StatePolicy.CanReset ? _inner.ReseedAsync(cancellationToken)
            : throw new InvalidOperationException("This environment requires recreation and does not permit reset/seed.");
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) return new ValueTask(_disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        try { await _inner.DisposeAsync().AsTask().WaitAsync(_timeout).ConfigureAwait(false); }
        catch (Exception)
        {
            const string diagnostic = "Environment cleanup failed or exceeded its deadline. The primary scenario result is retained; inspect the ownership journal and run explicit recovery before reusing this machine.";
            if (_reportCleanup is null) throw new OrchestrationException(new OrchestrationErrorInfo(
                OrchestrationErrorKind.Provision, "environment", null, null, diagnostic));
            _reportCleanup(diagnostic);
        }
    }
}
