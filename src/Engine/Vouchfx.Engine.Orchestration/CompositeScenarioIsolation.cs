// Vouchfx.Engine.Orchestration — CompositeScenarioIsolation (state-reset generalisation).
//
// Fans BeginScenarioAsync / EndScenarioAsync out to N child IScenarioIsolation
// instances — one per resettable dependency — so a suite declaring more than one
// mutable relational dependency (e.g. Postgres AND SQL Server) has EVERY one of them
// reset between scenarios, not just the first one ScenarioIsolationFactory happens to
// find.
//
// Design notes:
//   • Children run SEQUENTIALLY, in the order supplied by the caller (dependency
//     declaration order via SuiteTopology.DependencyNames), so composite behaviour is
//     deterministic and no two connections are opened concurrently.
//   • Fail-fast: the first child whose Begin/End throws an OrchestrationException
//     aborts the remaining children for that call. The suite aborts the run on any
//     reset failure anyway (§12.1: EnvironmentError), so there is no benefit to
//     continuing — and the escaping exception's ResourceName already names the
//     failing dependency.
//   • DisposeAsync is best-effort across ALL children even when one throws: every
//     child gets a chance to release its connection, and the FIRST exception
//     encountered is rethrown after every child has been given that chance (no
//     existing multi-resource disposal-aggregation pattern exists elsewhere in this
//     codebase to follow, so this is the simplest correct behaviour).

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// Composes multiple <see cref="IScenarioIsolation"/> children into one, so every
/// resettable dependency in a topology is reset between scenarios (S04 generalisation).
/// </summary>
public sealed class CompositeScenarioIsolation : IScenarioIsolation, IAsyncDisposable
{
    private readonly IReadOnlyList<IScenarioIsolation> _children;
    private bool _disposed;

    /// <summary>
    /// The ordered children, exposed for tests (InternalsVisibleTo) so the factory's
    /// declaration-order guarantee can be asserted rather than assumed.
    /// </summary>
    internal IReadOnlyList<IScenarioIsolation> Children => _children;

    /// <summary>
    /// Initialises a new <see cref="CompositeScenarioIsolation"/> over the given
    /// children, invoked in the supplied order.
    /// </summary>
    /// <param name="children">
    /// The ordered child isolations (dependency declaration order). Must not be
    /// <see langword="null"/>.
    /// </param>
    public CompositeScenarioIsolation(IReadOnlyList<IScenarioIsolation> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        _children = children;
    }

    // ── IScenarioIsolation ────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Invokes each child's <see cref="IScenarioIsolation.BeginScenarioAsync"/>
    /// sequentially, in declaration order. Fails fast on the first
    /// <see cref="OrchestrationException"/> — later children are not invoked.
    /// </remarks>
    public async Task BeginScenarioAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var child in _children)
        {
            await child.BeginScenarioAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Invokes each child's <see cref="IScenarioIsolation.EndScenarioAsync"/>
    /// sequentially, in declaration order. Fails fast on the first
    /// <see cref="OrchestrationException"/> — later children are not invoked, so a
    /// suite with (say) Postgres + SQL Server dependencies never resets SQL Server
    /// after Postgres has already reported a failure.
    /// </remarks>
    public async Task EndScenarioAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (var child in _children)
        {
            await child.EndScenarioAsync(ct).ConfigureAwait(false);
        }
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    /// <summary>
    /// Disposes every child that implements <see cref="IAsyncDisposable"/>,
    /// best-effort: a child's disposal failure does not prevent the remaining
    /// children from being disposed. Safe to call multiple times.
    /// </summary>
    /// <exception cref="Exception">
    /// Rethrows the FIRST exception encountered across all children's disposal,
    /// once every child has had a chance to dispose.
    /// </exception>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Exception? firstFailure = null;

        foreach (var child in _children)
        {
            if (child is not IAsyncDisposable disposable)
            {
                continue;
            }

            try
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }
        }

        if (firstFailure is not null)
        {
            throw firstFailure;
        }
    }
}
