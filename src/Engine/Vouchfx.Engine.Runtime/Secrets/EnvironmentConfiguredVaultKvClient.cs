// Vouchfx.Engine.Runtime — EnvironmentConfiguredVaultKvClient (S08-B-01, §17).
//
// The runtime glue that lets the engine register the 'vault' secret source WITHOUT
// requiring Vault to be configured at registry-build / validation time:
//   • the SOURCE 'vault' is always known (so ${secret:vault/...} validates at compile
//     time regardless of whether VAULT_ADDR/VAULT_TOKEN happen to be set then);
//   • the CONNECTION is resolved LAZILY, on first read at step-execution time, from
//     the run environment (§17 — a reference/config, never a YAML literal).
//
// If a scenario references a vault secret but the environment is not configured, the
// first read throws SecretResolutionException naming the missing variable — a clean
// EnvironmentError (§12.1), never a crash and never a leaked value/token.
//
// Lifetime: this client owns the HttpClient it creates and disposes it.  The scenario
// runner disposes the resolver (and hence this client) at scenario end, so no
// HttpClient leaks across the per-scenario boundary.

using System.Net.Http;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Secrets.Vault;

namespace Vouchfx.Engine.Runtime.Secrets;

/// <summary>
/// An <see cref="IVaultKvClient"/> that builds its Vault connection lazily from the run
/// environment (<c>VAULT_ADDR</c> / <c>VAULT_TOKEN</c> / <c>VAULT_KV_MOUNT</c>) on first
/// read, delegating to an <see cref="HttpVaultKvClient"/> (§17).
/// </summary>
/// <remarks>
/// Lazy by design: the <c>vault</c> source is registered for every run so references
/// validate, but the connection is only required if a step actually resolves a vault
/// secret.  A missing variable surfaces as a <see cref="SecretResolutionException"/>
/// (EnvironmentError) at that point, naming the variable but never a value or token.
/// </remarks>
internal sealed class EnvironmentConfiguredVaultKvClient : IVaultKvClient, IDisposable
{
    /// <summary>
    /// Bounded per-request timeout for the Vault read (security S2).  Without an
    /// explicit bound, <see cref="HttpClient"/> defaults to 100 s, so a hung or
    /// black-holed Vault would stall the step for that whole window before the read
    /// fails.  15 s is generous for a single KV v2 GET on a healthy server yet fails
    /// fast against an unreachable one; the timeout surfaces as a
    /// <see cref="Vouchfx.Engine.Abstractions.Secrets.SecretResolutionException"/>
    /// (EnvironmentError, §12.1) from <see cref="HttpVaultKvClient"/>.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private HttpClient? _httpClient;
    private HttpVaultKvClient? _inner;
    private bool _disposed;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> ReadKeyValues(string kvPath)
        => GetInner().ReadKeyValues(kvPath);

    private HttpVaultKvClient GetInner()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_inner is not null)
            {
                return _inner;
            }

            if (!VaultSecretConfiguration.TryFromEnvironment(out var config, out var error))
            {
                // Missing/invalid config is an environment problem (§12.1).  The error
                // names the variable, never a value (§17).
                throw new SecretResolutionException("vault", string.Empty, error!);
            }

            // Bound the per-request time (security S2): a hung/black-holed Vault must
            // fail the step fast rather than stall it for HttpClient's 100 s default.
            _httpClient = new HttpClient { Timeout = RequestTimeout };
            _inner = new HttpVaultKvClient(_httpClient, config!);
            return _inner;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _httpClient?.Dispose();
            _httpClient = null;
            _inner = null;
        }
    }
}
