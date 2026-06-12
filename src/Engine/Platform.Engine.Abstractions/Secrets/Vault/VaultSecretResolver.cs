// Platform.Engine.Abstractions — VaultSecretResolver (S08-B-01, §17).
//
// The 'vault' secret source: resolves a ${secret:vault/<kvPath>#<field>} reference
// against a HashiCorp Vault KV v2 store at STEP-EXECUTION TIME, returning a redacted
// SecretString.  It is the second MVP secret source, joining 'env' (S05) through the
// pluggable-source seam (ISecretResolver) with no change to that contract.
//
// Reference shape (the PATH segment, i.e. everything after "vault/"):
//   <kvPath>#<field>
//     <kvPath> : the logical KV v2 path, e.g. "secret/myapp/db" (may contain '/').
//     <field>  : the key WITHIN that path's data object to return, e.g. "password".
// The '#field' selector is REQUIRED: a Vault KV path holds an object of named fields,
// so the reference must say which one it wants (there is no sensible default).
//
// §17 invariants honoured here:
//   • reference-only      — the resolver receives the parsed PATH, never a value.
//   • resolve-at-execution — the IVaultKvClient is invoked only inside Resolve(...).
//   • redaction-at-source  — the returned SecretString exposes its value solely via
//                            Reveal(); ToString()/serialisation emit ***REDACTED***.
//   • token never leaks    — the resolver never touches the token; it lives only on
//                            the client, which places it on a request header.

using System;
using System.Collections.Generic;

namespace Platform.Engine.Abstractions.Secrets.Vault;

/// <summary>
/// Resolves secrets from a HashiCorp Vault KV v2 store — the MVP <c>vault</c> source
/// (§17).  Resolution happens at step-execution time via the injected
/// <see cref="IVaultKvClient"/>; the resolved value is wrapped in a redacted
/// <see cref="SecretString"/> minted here at the source.
/// </summary>
/// <remarks>
/// The resolver owns the lifetime of its <see cref="IVaultKvClient"/>: disposing the
/// resolver disposes the client when the client is <see cref="IDisposable"/> (the
/// production client owns an <see cref="System.Net.Http.HttpClient"/>).  A stateless
/// fake client passed in a unit test is simply not <see cref="IDisposable"/> and so is
/// never disposed.
/// </remarks>
public sealed class VaultSecretResolver : ISecretResolver, IDisposable
{
    /// <summary>
    /// The character separating the KV path from the field selector in a vault
    /// reference path: <c>secret/app/db<b>#</b>password</c>.
    /// </summary>
    public const char FieldSeparator = '#';

    private readonly IVaultKvClient _client;

    /// <summary>
    /// Initialises a new resolver over <paramref name="client"/>.
    /// </summary>
    /// <param name="client">The KV v2 transport; must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="client"/> is <see langword="null"/>.
    /// </exception>
    public VaultSecretResolver(IVaultKvClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public string Source => "vault";

    /// <inheritdoc />
    /// <exception cref="SecretResolutionException">
    /// Thrown when the reference path is empty, omits the required <c>#field</c>
    /// selector, the KV path is absent at the source, or the named field is not stored
    /// at that path.  The message names the path/field but never a value or the token
    /// (a missing secret is an environment/configuration problem; §12.1, §17).
    /// </exception>
    public SecretString Resolve(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new SecretResolutionException(
                Source,
                path ?? string.Empty,
                "the secret reference '${secret:vault/}' is missing a lookup path; the " +
                "expected form is '${secret:vault/<kvPath>#<field>}' (for example " +
                "'${secret:vault/secret/myapp/db#password}').");
        }

        var separatorIndex = path.IndexOf(FieldSeparator);
        if (separatorIndex < 0)
        {
            throw new SecretResolutionException(
                Source,
                path,
                $"the Vault secret reference '${{secret:vault/{path}}}' omits the required " +
                "'#<field>' selector; a Vault KV path stores an object of named fields, so " +
                "the reference must name the field to return (for example " +
                "'${secret:vault/secret/myapp/db#password}').");
        }

        var kvPath = path.Substring(0, separatorIndex);
        var field = path.Substring(separatorIndex + 1);

        if (kvPath.Length == 0)
        {
            throw new SecretResolutionException(
                Source,
                path,
                $"the Vault secret reference '${{secret:vault/{path}}}' has an empty KV path " +
                "before the '#'; the expected form is '${secret:vault/<kvPath>#<field>}'.");
        }

        if (field.Length == 0)
        {
            throw new SecretResolutionException(
                Source,
                path,
                $"the Vault secret reference '${{secret:vault/{path}}}' has an empty field " +
                "name after the '#'; name the KV field to return (for example " +
                "'#password').");
        }

        // Resolution happens HERE, at step-execution time — never at compile time (§17).
        // The client throws SecretResolutionException (path-only message) on an absent
        // path / unauthorised / transport failure.
        IReadOnlyDictionary<string, string> data = _client.ReadKeyValues(kvPath);

        if (!data.TryGetValue(field, out var value) || value is null)
        {
            // Name the requested FIELD and PATH; never enumerate or echo any other
            // field's value (§17).
            throw new SecretResolutionException(
                Source,
                path,
                $"the Vault KV path '{kvPath}' does not contain a field named '{field}' " +
                $"(referenced by '${{secret:vault/{path}}}'); check the field name and that " +
                "the secret has been written to that path.");
        }

        // Redaction at the source: mint the SecretString here (its constructor is
        // internal to this assembly), so the value is wrapped the instant it leaves the
        // transport and can never be observed except via the auditable Reveal().
        return new SecretString(value);
    }

    /// <summary>
    /// Disposes the underlying <see cref="IVaultKvClient"/> when it owns disposable
    /// state (e.g. an <see cref="System.Net.Http.HttpClient"/>).  Idempotent in
    /// practice — disposing the client twice is the client's own concern.
    /// </summary>
    public void Dispose()
    {
        if (_client is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
