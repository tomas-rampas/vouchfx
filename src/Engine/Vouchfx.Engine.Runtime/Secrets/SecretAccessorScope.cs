// Vouchfx.Engine.Runtime — SecretAccessorScope (client-key-password, REQ-009).
//
// A secret accessor bundled with the resolvers it routes to, and the disposal of those
// resolvers. It exists because REQ-009 gave the accessor a SECOND consumer: the topology
// probe's SecurityConfigurationAccessor, built before a scenario's own accessor exists, needs
// something to resolve `clientKeyPassword` through on both the run path and the `--watch` path.
// Without this type each of those three sites would spell "build the resolvers, wrap them in a
// catalogue, wrap that in an accessor, and remember to dispose the resolvers on every exit
// path" for itself, and the Vault resolver's HttpClient leaks the first time one of them forgets.
using System;
using Vouchfx.Engine.Abstractions.Secrets;

namespace Vouchfx.Engine.Runtime.Secrets;

/// <summary>
/// A per-use <see cref="SecretAccessor"/> together with ownership of the
/// <see cref="ISecretResolver"/> instances it resolves through (§17).
/// </summary>
/// <remarks>
/// <para>
/// Constructing this touches no environment variable and opens no connection — see
/// <c>ScenarioRunner.BuildSecretResolvers</c>'s own remarks and
/// <c>EnvironmentConfiguredVaultKvClient</c>, whose <see cref="System.Net.Http.HttpClient"/> is
/// created on first resolve. A scope built for a run that resolves nothing therefore costs two
/// object allocations.
/// </para>
/// <para>
/// <strong>Each scope carries its OWN <see cref="SecretAccessor.ResolvedSecrets"/> ledger</strong>,
/// which is the open question REQ-010 owns and this type deliberately does not answer. The probe
/// path builds its scope before a scenario's own accessor exists, so a passphrase resolved for
/// the probe is recorded in a ledger the step path's scrubbers never read. The shape that closes
/// it — one scope shared by both paths, or one ledger registered into by both — is a change to
/// where the scope is CONSTRUCTED, not to this type.
/// </para>
/// </remarks>
internal sealed class SecretAccessorScope : IDisposable
{
    private readonly ISecretResolver[] _resolvers;
    private bool _disposed;

    /// <summary>
    /// Initialises a scope over <paramref name="resolvers"/>, taking ownership of their disposal.
    /// </summary>
    /// <param name="resolvers">The run's resolvers, as built by a single shared factory.</param>
    internal SecretAccessorScope(ISecretResolver[] resolvers)
    {
        _resolvers = resolvers;
        Accessor = new SecretAccessor(new SecretSourceCatalog(resolvers));
    }

    /// <summary>The accessor every resolution in this scope must go through (§17, REQ-010).</summary>
    internal SecretAccessor Accessor { get; }

    /// <summary>
    /// Disposes any resolver that owns disposable state (the Vault resolver's client owns an
    /// <see cref="System.Net.Http.HttpClient"/>). Stateless resolvers, such as the <c>env</c> one,
    /// are skipped. Idempotent, and never throws into the verdict path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The no-throw property is IMPLEMENTED here, not merely claimed.</strong> Written as a
    /// bare loop the sentence above was false twice over: a resolver whose <c>Dispose</c> threw
    /// propagated out of this method AND skipped every resolver after it in the array, so one bad
    /// implementor both leaked the rest and raised a fault of its own. That matters because of
    /// WHERE this now runs — every call site disposes it from a <c>finally</c>, several of them
    /// while an exception is already in flight, and an exception thrown from a <c>finally</c>
    /// REPLACES the in-flight one. The engine would then report a resolver's disposal fault in
    /// place of the failure the run actually had.
    /// </para>
    /// <para>
    /// So each resolver is disposed inside its own <c>try</c> and any fault is DISCARDED. There is
    /// nothing better to do with it: disposal happens after the run's verdict is decided, the
    /// resolvers are being abandoned regardless, and no caller can act on the news. The one thing
    /// that must not happen — a leaked <see cref="System.Net.Http.HttpClient"/> because an earlier
    /// resolver misbehaved — is what the isolation buys.
    /// </para>
    /// <para>
    /// <see cref="OutOfMemoryException"/> and <see cref="StackOverflowException"/> are not
    /// special-cased, and the filter is deliberately broad rather than a type list: a resolver is
    /// third-party code (<c>ISecretResolver</c> is public), so enumerating what it may throw is a
    /// guess, and a guess that turns out short reintroduces exactly the defect above.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var resolver in _resolvers)
        {
            if (resolver is not IDisposable disposable)
            {
                continue;
            }

            try
            {
                disposable.Dispose();
            }
#pragma warning disable CA1031 // See the remarks above: a disposal fault here has no consumer and
            catch (Exception)   // must not displace the verdict this scope is being torn down for.
#pragma warning restore CA1031
            {
                // Deliberately swallowed — one resolver's fault must not leak the next one's
                // client, nor replace an in-flight exception from the `finally` that called this.
            }
        }
    }
}
