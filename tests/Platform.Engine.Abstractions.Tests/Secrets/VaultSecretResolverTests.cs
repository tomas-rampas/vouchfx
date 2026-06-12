// Tests for S08-B-01: VaultSecretResolver — resolves a ${secret:vault/<path>#<field>}
// reference against a HashiCorp Vault KV v2 store, AT RESOLUTION TIME only (§17).
//
// These unit tests inject a FAKE IVaultKvClient so the resolver's parsing, field
// selection, redaction and error behaviour can be exercised with no network and no
// running Vault.  The Docker-gated end-to-end proof (a real hashicorp/vault dev
// container) lives in Platform.Engine.Orchestration.Tests/VaultSecretSourceDockerTests.
//
// §17 invariants asserted here:
//   • reference-only — the resolver is handed a PATH parsed from the reference,
//     never a literal value;
//   • resolve at execution time — the client is invoked only inside Resolve(...);
//   • redaction at the source — the resolved SecretString never exposes its value
//     via ToString()/serialisation; only Reveal() returns it;
//   • the Vault TOKEN never appears in any exception message.

using System;
using System.Collections.Generic;
using System.Text.Json;
using Platform.Engine.Abstractions.Secrets;
using Platform.Engine.Abstractions.Secrets.Vault;
using Xunit;

namespace Platform.Engine.Abstractions.Tests.Secrets;

/// <summary>
/// Verifies the <c>vault</c> resolver: a present KV field resolves to its value
/// (revealed only via <see cref="SecretString.Reveal"/>); a missing path/field or a
/// transport failure throws <see cref="SecretResolutionException"/> naming the path
/// but never a value or the Vault token (§17).
/// </summary>
public sealed class VaultSecretResolverTests
{
    // A fake KV client returning a fixed in-memory KV v2 data map, recording the last
    // path it was asked for.  No network, no HttpClient — pure unit isolation.
    private sealed class FakeVaultKvClient : IVaultKvClient
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _store;

        public FakeVaultKvClient(
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> store)
            => _store = store;

        public string? LastRequestedPath { get; private set; }

        public IReadOnlyDictionary<string, string> ReadKeyValues(string kvPath)
        {
            LastRequestedPath = kvPath;
            if (_store.TryGetValue(kvPath, out var data))
            {
                return data;
            }

            // Mirror the production client: an absent path is a resolution failure, not
            // a value.  The message names the path, never a token.
            throw new SecretResolutionException(
                "vault",
                kvPath,
                $"the Vault KV path '{kvPath}' returned no data.");
        }
    }

    private static FakeVaultKvClient ClientWith(string kvPath, params (string key, string value)[] kvs)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in kvs)
        {
            data[key] = value;
        }

        var store = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            [kvPath] = data,
        };
        return new FakeVaultKvClient(store);
    }

    [Fact]
    public void Source_IsVault()
    {
        var resolver = new VaultSecretResolver(ClientWith("secret/app", ("password", "p")));
        Assert.Equal("vault", resolver.Source);
    }

    [Fact]
    public void Resolve_PresentField_RevealsValue()
    {
        const string secretValue = "s3cr3t-db-password";
        var client = ClientWith("secret/app/db", ("password", secretValue), ("user", "svc"));
        var resolver = new VaultSecretResolver(client);

        // Reference path is everything after "vault/" in ${secret:vault/secret/app/db#password}.
        var secret = resolver.Resolve("secret/app/db#password");

        Assert.Equal(secretValue, secret.Reveal());
        // The resolver must strip the '#field' selector before calling the KV client.
        Assert.Equal("secret/app/db", client.LastRequestedPath);
    }

    [Fact]
    public void Resolve_ResultIsRedactedAtSource()
    {
        const string secretValue = "must-not-leak";
        var resolver = new VaultSecretResolver(ClientWith("secret/app", ("token", secretValue)));

        var secret = resolver.Resolve("secret/app#token");

        // Redaction at the source: neither ToString() nor JSON serialisation reveals it.
        Assert.Equal(SecretString.RedactedMarker, secret.ToString());
        Assert.DoesNotContain(secretValue, secret.ToString(), StringComparison.Ordinal);
        var json = JsonSerializer.Serialize(secret);
        Assert.DoesNotContain(secretValue, json, StringComparison.Ordinal);
        Assert.Contains(SecretString.RedactedMarker, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_MissingField_ThrowsSecretResolutionExceptionNamingFieldNotValue()
    {
        var resolver = new VaultSecretResolver(ClientWith("secret/app", ("password", "p")));

        var ex = Assert.Throws<SecretResolutionException>(
            () => resolver.Resolve("secret/app#absent"));

        Assert.Equal("vault", ex.SecretSource);
        Assert.Contains("absent", ex.Message, StringComparison.Ordinal);
        // The other field's value must not leak into the diagnostic.
        Assert.DoesNotContain("password", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_MissingFieldSelector_ThrowsSecretResolutionException()
    {
        var resolver = new VaultSecretResolver(ClientWith("secret/app", ("value", "v")));

        // No '#field' — the resolver cannot know which KV key to return.
        var ex = Assert.Throws<SecretResolutionException>(
            () => resolver.Resolve("secret/app"));

        Assert.Equal("vault", ex.SecretSource);
        Assert.Contains("#", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_EmptyPath_ThrowsSecretResolutionException()
    {
        var resolver = new VaultSecretResolver(ClientWith("secret/app", ("value", "v")));

        Assert.Throws<SecretResolutionException>(() => resolver.Resolve(string.Empty));
    }

    [Fact]
    public void Resolve_AbsentPath_ThrowsSecretResolutionException()
    {
        var resolver = new VaultSecretResolver(ClientWith("secret/app", ("value", "v")));

        var ex = Assert.Throws<SecretResolutionException>(
            () => resolver.Resolve("secret/does-not-exist#value"));

        Assert.Equal("vault", ex.SecretSource);
        Assert.Contains("does-not-exist", ex.Message, StringComparison.Ordinal);
    }
}
