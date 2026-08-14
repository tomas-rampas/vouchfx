// Vouchfx.Engine.Abstractions — IVaultKvClient + VaultSecretConfiguration
// (S08-B-01, §17).
//
// The transport seam between the VaultSecretResolver and a HashiCorp Vault KV v2
// store.  Keeping the resolver's policy (reference parsing, field selection,
// redaction, error mapping) behind this interface means:
//   • the resolver is unit-testable with a fake client (no network, no live Vault);
//   • the resolver — and therefore Vouchfx.Engine.Abstractions — stays a pure-BCL
//     leaf library: the only production implementation (HttpVaultKvClient) uses the
//     in-box System.Net.Http.HttpClient and System.Text.Json, so NO new NuGet
//     dependency is introduced (VaultSharp would drag a transitive tree into the
//     leaf library; a thin HttpClient against Vault's documented HTTP API is both
//     lighter and keeps the §17 token-handling fully under our control).
//
// §17 token handling (non-negotiable): the Vault TOKEN is a reference/config read
// from the run environment (VAULT_TOKEN), NEVER a YAML literal.  It is held only to
// be placed on the X-Vault-Token request header at resolution time; it is never
// echoed by ToString(), never embedded in an exception message, and never written to
// the event stream.

using System;

namespace Vouchfx.Engine.Abstractions.Secrets.Vault;

/// <summary>
/// Reads the key/value pairs stored at a single HashiCorp Vault KV v2 path (§17).
/// </summary>
/// <remarks>
/// <para>
/// Implementations resolve <strong>at run time, never at compile time</strong>; no read
/// happens at compile time, so no secret value is ever baked into the emitted IL. Which
/// run-time moment is the calling field's property, not this interface's — see
/// <see cref="Vouchfx.Engine.Abstractions.Secrets.SecretReference"/>'s own remarks.
/// </para>
/// <para>
/// The interface lives in <c>Vouchfx.Engine.Abstractions</c> so the
/// <see cref="VaultSecretResolver"/> can mint a <see cref="SecretString"/> (whose
/// constructor is internal to this assembly) directly from the returned map — keeping
/// redaction enforced at the source.
/// </para>
/// </remarks>
public interface IVaultKvClient
{
    /// <summary>
    /// Reads every key/value pair stored at <paramref name="kvPath"/> (the logical
    /// KV v2 path, e.g. <c>secret/myapp/db</c>; the client maps it to the underlying
    /// <c>/v1/&lt;mount&gt;/data/&lt;path&gt;</c> data API).
    /// </summary>
    /// <param name="kvPath">The logical KV path, with no <c>#field</c> selector.</param>
    /// <returns>The field map stored at the path (Vault's <c>data.data</c> object).</returns>
    /// <exception cref="SecretResolutionException">
    /// Thrown when the path is absent, the request is unauthorised, or the transport
    /// fails.  The message names the path but never a value or the Vault token (§17).
    /// </exception>
    System.Collections.Generic.IReadOnlyDictionary<string, string> ReadKeyValues(string kvPath);
}

/// <summary>
/// The connection parameters for a Vault KV v2 store (§17): the server address, the
/// access token, and the KV v2 mount name.  All come from the run <em>environment</em>
/// (a reference/config), never from a <c>.e2e.yaml</c> literal.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Token"/> is deliberately a plain <see langword="string"/> reachable
/// only from inside the secrets subsystem (the property is <see langword="internal"/>):
/// it is the request credential, not a resolved secret VALUE, and it is never written
/// anywhere observable.  <see cref="ToString"/> is overridden to redact it so a config
/// object dumped into a log or exception cannot leak the token.
/// </para>
/// </remarks>
public sealed class VaultSecretConfiguration
{
    /// <summary>
    /// The environment variable carrying the Vault server address (Vault's own
    /// convention).
    /// </summary>
    public const string AddressVariable = "VAULT_ADDR";

    /// <summary>
    /// The environment variable carrying the Vault access token (Vault's own
    /// convention).  Read as a reference; never logged (§17).
    /// </summary>
    public const string TokenVariable = "VAULT_TOKEN";

    /// <summary>
    /// The environment variable selecting the KV v2 mount.  Optional; defaults to
    /// <see cref="DefaultMount"/> (<c>secret</c>, Vault dev mode's default mount).
    /// </summary>
    public const string MountVariable = "VAULT_KV_MOUNT";

    /// <summary>The default KV v2 mount when <see cref="MountVariable"/> is unset.</summary>
    public const string DefaultMount = "secret";

    /// <summary>
    /// Initialises a new configuration.
    /// </summary>
    /// <param name="address">The Vault server base address.</param>
    /// <param name="token">The Vault access token (a reference/config; never a literal).</param>
    /// <param name="mount">The KV v2 mount name.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="address"/>, <paramref name="token"/> or
    /// <paramref name="mount"/> is <see langword="null"/>.
    /// </exception>
    public VaultSecretConfiguration(Uri address, string token, string mount)
    {
        Address = address ?? throw new ArgumentNullException(nameof(address));
        Token = token ?? throw new ArgumentNullException(nameof(token));
        Mount = mount ?? throw new ArgumentNullException(nameof(mount));
    }

    /// <summary>The Vault server base address (e.g. <c>http://127.0.0.1:8200</c>).</summary>
    public Uri Address { get; }

    /// <summary>The KV v2 mount name (e.g. <c>secret</c>).</summary>
    public string Mount { get; }

    /// <summary>
    /// The Vault access token.  <see langword="internal"/> by design: it is reachable
    /// only by the secrets subsystem (and, via <c>InternalsVisibleTo</c>, its tests),
    /// so no general caller can read it and accidentally log it (§17).
    /// </summary>
    internal string Token { get; }

    /// <summary>
    /// Builds a configuration from the run environment (§17): <see cref="AddressVariable"/>,
    /// <see cref="TokenVariable"/>, and the optional <see cref="MountVariable"/>.
    /// </summary>
    /// <param name="configuration">
    /// On success the populated configuration; otherwise <see langword="null"/>.
    /// </param>
    /// <param name="error">
    /// On failure an actionable British-English message naming the missing variable;
    /// otherwise <see langword="null"/>.  The message never contains a token value.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when both the address and token are present and the
    /// address is a valid absolute URI; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryFromEnvironment(out VaultSecretConfiguration? configuration, out string? error)
    {
        configuration = null;
        error = null;

        var address = Environment.GetEnvironmentVariable(AddressVariable);
        if (string.IsNullOrWhiteSpace(address))
        {
            error =
                $"the Vault address environment variable '{AddressVariable}' is not set; " +
                $"set it (for example '{AddressVariable}=http://127.0.0.1:8200') before " +
                "running a suite that references '${secret:vault/...}'.";
            return false;
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            error =
                $"the Vault address '{AddressVariable}' is not a valid absolute URI; " +
                "the expected form is 'http://host:8200'.";
            return false;
        }

        var token = Environment.GetEnvironmentVariable(TokenVariable);
        if (string.IsNullOrEmpty(token))
        {
            // Name the variable only — NEVER echo any partial token (§17).
            error =
                $"the Vault token environment variable '{TokenVariable}' is not set; " +
                "provide a token reference in the run environment (never a YAML literal) " +
                "before running a suite that references '${secret:vault/...}'.";
            return false;
        }

        var mount = Environment.GetEnvironmentVariable(MountVariable);
        if (string.IsNullOrWhiteSpace(mount))
        {
            mount = DefaultMount;
        }

        configuration = new VaultSecretConfiguration(uri, token, mount);
        return true;
    }

    /// <summary>
    /// Returns a redacted description that never contains the token (§17).
    /// </summary>
    public override string ToString()
        => $"Vault(address={Address}, mount={Mount}, token=***REDACTED***)";
}
