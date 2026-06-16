// Platform.Engine.Telemetry — OutboxForget (S12-G-01, Phase A).
//
// The engine-level orchestration for the "forget on disable" criterion (3, client half):
// when the user opts OUT and the HTTP transport is configured and an install id exists,
// fire a best-effort backend forget BEFORE the local delete.  Crucially this is fully
// fail-silent and short-timeout, so the caller's local opt-out (deleting the install id +
// clearing the outbox) ALWAYS proceeds even if the endpoint is offline.
//
// Lives in the engine (not the CLI) so it is directly unit-testable over a mock
// IOutboxHttpClient with no HttpClient and no CLI plumbing.  The CLI's `telemetry disable`
// delegates here, then performs the local delete regardless of the result.

namespace Platform.Engine.Telemetry;

/// <summary>
/// Best-effort backend "forget" issued when telemetry is disabled (S12-G-01).
/// </summary>
/// <remarks>
/// Every entry point is fail-silent: a missing install id, an unconfigured transport, or a
/// transport fault all resolve to "did not forget" WITHOUT throwing, so the caller's local
/// opt-out is never blocked by the network (criterion 3, client half).
/// </remarks>
public static class OutboxForget
{
    /// <summary>
    /// Fires a best-effort forget for <paramref name="installId"/> via
    /// <paramref name="client"/>.  No-op (returns <see langword="false"/>) when there is no
    /// install id or no client.  Swallows any transport fault.
    /// </summary>
    /// <param name="client">
    /// The HTTP transport, or <see langword="null"/> when telemetry is unconfigured.
    /// </param>
    /// <param name="installId">
    /// The install id to forget, or <see langword="null"/> when none exists.
    /// </param>
    /// <param name="cancellationToken">Propagated to the forget send.</param>
    /// <returns>
    /// <see langword="true"/> when a forget request was issued without faulting;
    /// <see langword="false"/> when it was skipped (no id / no client) or a fault was
    /// swallowed.  The caller proceeds with the local opt-out either way.
    /// </returns>
    public static async Task<bool> TryForgetAsync(
        IOutboxHttpClient? client,
        Guid? installId,
        CancellationToken cancellationToken = default)
    {
        if (client is null || installId is not { } id)
        {
            return false;
        }

        try
        {
            await client.ForgetAsync(id, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Honour a caller-requested cancellation rather than masking it as a skipped
            // forget — the local delete still runs in the caller's path.
            throw;
        }
        catch (Exception ex) when (
            ex is System.Net.Http.HttpRequestException
                or InvalidOperationException
                or OperationCanceledException
                or System.Net.Sockets.SocketException)
        {
            // Offline / timed-out / faulted: the forget is advisory, so swallow and let the
            // local opt-out proceed.  No token/endpoint/install-id detail is surfaced here.
            return false;
        }
    }
}
