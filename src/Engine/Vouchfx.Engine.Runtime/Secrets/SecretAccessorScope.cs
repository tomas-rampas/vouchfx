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
/// <strong>REQ-010's answer: the LEDGER is shared across a run, the SCOPE is not.</strong> Pass a
/// run-scoped <see cref="ResolvedSecretLedger"/> to the constructor and every scope built from it —
/// the probe's and each scenario's — records into one net, so a passphrase resolved for the probe
/// is scrubbable from text emitted on the step path and vice versa. Sharing the SCOPE instead was
/// rejected because the two lifetimes genuinely differ: the probe scope is per-TOPOLOGY and a
/// scenario scope is per-SCENARIO, so a shared-topology multi-suite run is one probe scope against
/// N scenario scopes and no single scope object can carry both. Sharing only the ledger decouples
/// "which values must be scrubbed" (run-scoped) from "who owns the Vault
/// <see cref="System.Net.Http.HttpClient"/>" (unchanged, per-scope, disposed at each scope's own
/// end). Omit the argument and the scope keeps a private ledger — the pre-REQ-010 behaviour.
/// </para>
/// <para>
/// <strong>The <c>--watch</c> call site shares a SESSION-scoped ledger (EDGE-007), and the wider
/// scope is forced by CAPTURE ORDER, not merely by breadth.</strong> <c>WatchRunner.RunAsync</c>
/// builds one ledger for the whole watch session and hands it both to the probe scope in its build
/// seam and to <c>ScenarioRunner.RunScenarioAgainstKeptTopologyAsync</c>. The narrower scope worth
/// ruling out is the BUILD SEAM's, and the precise reason is not "the seam runs per save" — it does
/// not; <c>WatchSession.OnChangeAsync</c> reaches it only when the environment hash changes, so a
/// seam-scoped ledger would already be shared by every reusing save. It is that the watch loop's
/// error sinks capture the ledger BY VALUE before the seam runs, so on a REBUILD save the probe
/// would resolve into a ledger the catch receiving its failure has never seen. Sharing one
/// session-scoped instance is what makes the probe's value scrubbable both from that catch and
/// from later saves' step text — the same cross-path gap REQ-010 closed for <c>vouchfx run</c>.
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
    /// <param name="sharedLedger">
    /// The run-scoped ledger this scope's accessor records revealed values into (REQ-010), or
    /// <see langword="null"/> to give the accessor a ledger private to this scope.
    /// </param>
    internal SecretAccessorScope(ISecretResolver[] resolvers, ResolvedSecretLedger? sharedLedger = null)
    {
        _resolvers = resolvers;
        var catalog = new SecretSourceCatalog(resolvers);
        Accessor = sharedLedger is null
            ? new SecretAccessor(catalog)
            : new SecretAccessor(catalog, sharedLedger);
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
