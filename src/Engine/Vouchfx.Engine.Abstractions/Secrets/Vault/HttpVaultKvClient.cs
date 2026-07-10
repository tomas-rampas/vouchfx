// Vouchfx.Engine.Abstractions — HttpVaultKvClient (S08-B-01, §17).
//
// The production IVaultKvClient: a thin HttpClient against HashiCorp Vault's KV v2
// HTTP API.  Chosen over VaultSharp deliberately —
//   • Vouchfx.Engine.Abstractions is a pure-BCL leaf library every other engine
//     project references; pulling VaultSharp (and its transitive tree) into it would
//     break that property.  System.Net.Http.HttpClient and System.Text.Json are
//     in-box, so this adds NO NuGet dependency.
//   • the read path is one documented GET, so a hand-rolled client is small and keeps
//     §17 token handling (header-only, never logged) entirely under our control.
//
// KV v2 read: GET {address}/v1/{mount}/data/{path}  with the token on X-Vault-Token.
// Response body: { "data": { "data": { <field>: <value>, ... }, "metadata": {...} } }.
//
// §17 token handling: the token is placed ONLY on the X-Vault-Token request header.
// It is never written to a log, an exception message, or the event stream.  No
// exception thrown here echoes the token, the response body, or any field value.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Vouchfx.Engine.Abstractions.Secrets.Vault;

/// <summary>
/// An <see cref="IVaultKvClient"/> backed by <see cref="HttpClient"/> against Vault's
/// KV v2 HTTP API (§17).  Synchronous by design so it slots behind the synchronous
/// <see cref="ISecretResolver.Resolve"/> contract without sync-over-async bridging.
/// </summary>
public sealed class HttpVaultKvClient : IVaultKvClient
{
    private readonly HttpClient _httpClient;
    private readonly VaultSecretConfiguration _configuration;

    /// <summary>
    /// Initialises a new client over <paramref name="httpClient"/> and
    /// <paramref name="configuration"/>.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client used for the read.  Ownership is NOT taken — the caller owns its
    /// lifetime (the resolver is built per-run and disposes the client it created).
    /// </param>
    /// <param name="configuration">The Vault address, token and KV mount.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="httpClient"/> or <paramref name="configuration"/> is
    /// <see langword="null"/>.
    /// </exception>
    public HttpVaultKvClient(HttpClient httpClient, VaultSecretConfiguration configuration)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> ReadKeyValues(string kvPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(kvPath);

        // §17 / security S1: reject a malformed KV path BEFORE it is spliced into the
        // request URI.  Without this, a path such as "../../sys" collapses under
        // Uri's RFC 3986 dot-segment normalisation when combined with the base
        // address, silently misrouting the read away from the intended KV mount.  A
        // leading '/', a backslash, or a control character would likewise produce a
        // request other than the author wrote.  Surfacing these as an authoring error
        // (named by path, never by token) turns a silent misroute into a clear failure.
        ValidateKvPath(kvPath);

        // KV v2 reads go through the /data/ sub-path: /v1/{mount}/data/{path}.
        // Build it relative to the configured absolute address.  Uri.EscapeDataString
        // is NOT applied to the whole path because KV paths legitimately contain '/'
        // segment separators; Vault accepts the path verbatim after the mount.
        var requestUri = new Uri(
            _configuration.Address,
            $"/v1/{_configuration.Mount}/data/{kvPath}");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        // §17: the token is sent ONLY here, on the request header — never logged.
        request.Headers.TryAddWithoutValidation("X-Vault-Token", _configuration.Token);

        HttpResponseMessage response;
        try
        {
            // Synchronous send (in-box since .NET 5) keeps the resolver path free of
            // sync-over-async bridging.
            response = _httpClient.Send(request);
        }
        catch (HttpRequestException ex)
        {
            // Transport failure → an environment problem (§12.1).  Message names the
            // path only; the inner exception carries no secret value.
            throw new SecretResolutionException(
                "vault",
                kvPath,
                $"could not reach the Vault server at '{_configuration.Address}' to read " +
                $"the KV path '{kvPath}'; check the address and that Vault is running.",
                ex);
        }
        catch (OperationCanceledException ex)
        {
            // A request that exceeds HttpClient.Timeout surfaces as a TaskCanceledException
            // (a subtype of OperationCanceledException), NOT an HttpRequestException — so it
            // would otherwise escape the catch above and bubble out unclassified.  A hung or
            // black-holed Vault must classify as an EnvironmentError (§12.1) just like any
            // other transport failure, so map it to a path-only SecretResolutionException
            // (the message names the path, never the token; the inner exception carries no
            // secret value).
            throw new SecretResolutionException(
                "vault",
                kvPath,
                $"the request to the Vault server at '{_configuration.Address}' to read the " +
                $"KV path '{kvPath}' timed out; check the address, network reachability, and " +
                "that Vault is responsive.",
                ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new SecretResolutionException(
                    "vault",
                    kvPath,
                    $"the Vault KV path '{kvPath}' does not exist (HTTP 404); check the " +
                    "mount and path, and that the secret has been written.");
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                // NEVER include the token or the response body — either could leak.
                throw new SecretResolutionException(
                    "vault",
                    kvPath,
                    $"access to the Vault KV path '{kvPath}' was denied (HTTP " +
                    $"{(int)response.StatusCode}); check the token's policy grants read on " +
                    "this path.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SecretResolutionException(
                    "vault",
                    kvPath,
                    $"reading the Vault KV path '{kvPath}' failed with HTTP " +
                    $"{(int)response.StatusCode}.");
            }

            string body;
            try
            {
                body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch (HttpRequestException ex)
            {
                throw new SecretResolutionException(
                    "vault",
                    kvPath,
                    $"reading the response body for Vault KV path '{kvPath}' failed.",
                    ex);
            }

            return ParseKvV2Data(kvPath, body);
        }
    }

    // Rejects a malformed KV path before it is spliced into the request URI (security S1).
    // The author holds the Vault token, so this is not a trust-boundary crossing — but a
    // path that escapes its mount (via "." / ".." dot-segments collapsed by Uri
    // normalisation), starts with '/', contains a backslash, or carries a control
    // character is a footgun that silently misroutes the read.  We surface it as an
    // authoring error (SecretResolutionException) naming the path, never the token (§17).
    private static void ValidateKvPath(string kvPath)
    {
        // A leading '/' would make the relative reference absolute against the host root,
        // discarding the "/v1/{mount}/data/" prefix entirely.
        if (kvPath[0] == '/')
        {
            throw new SecretResolutionException(
                "vault",
                kvPath,
                $"the Vault KV path '{kvPath}' must not begin with '/'; give the path " +
                "relative to the mount (for example 'myapp/db', not '/myapp/db').");
        }

        foreach (var ch in kvPath)
        {
            // A backslash is not a valid KV separator and can confuse path handling; a
            // control character (including embedded CR/LF) has no place in a KV path and
            // could enable request smuggling against a permissive proxy.  A '?' would start
            // a URI query string and a '#' a URI fragment once the path is spliced into the
            // request URI — either silently truncates the read to a shorter path than the
            // author wrote (e.g. 'myapp/db?x=y' reads 'myapp/db'), the same silent-misroute
            // footgun this method exists to prevent.  A '%' is rejected outright because a
            // percent-encoded sequence (e.g. '%2e%2e' or '%2E%2E') reads as the literal
            // string '%2e%2e' here — slipping past the literal '.'/'..' dot-segment guard
            // below — yet a permissive proxy or Vault's own decoding may collapse it back to
            // a '.'/'..' segment and escape the intended mount (e.g. 'myapp/%2e%2e/sys' →
            // '.../data/myapp/../sys' → '.../sys').  KV v2 logical paths have no legitimate
            // use for '%', so rejecting it here COMPLEMENTS (does not replace) the literal
            // dot-segment guard, closing the encoded-traversal variant the literal check
            // cannot see.
            if (ch == '\\' || ch == '?' || ch == '#' || ch == '%' || char.IsControl(ch))
            {
                throw new SecretResolutionException(
                    "vault",
                    kvPath,
                    $"the Vault KV path '{kvPath}' contains an illegal character; a KV path " +
                    "may not contain a backslash, a control character, a '?'/'#' (which " +
                    "would start a URI query string or fragment and truncate the read), or a " +
                    "'%' (a percent-encoded sequence such as '%2e%2e' could smuggle a " +
                    "'.'/'..' segment past the dot-segment check and escape the mount once " +
                    "decoded).");
            }
        }

        // Reject any '.' or '..' segment: combined with the base address, Uri's RFC 3986
        // dot-segment normalisation collapses these, escaping the intended mount and
        // silently misrouting the read (e.g. ".../data/../../sys" → ".../sys").
        foreach (var segment in kvPath.Split('/'))
        {
            if (segment is "." or "..")
            {
                throw new SecretResolutionException(
                    "vault",
                    kvPath,
                    $"the Vault KV path '{kvPath}' contains a '.' or '..' segment; a KV path " +
                    "may not navigate relative segments (they would escape the mount).");
            }
        }
    }

    // Extracts the data.data field map from a KV v2 read response.  Operates only on
    // the parsed JSON; on a malformed body it throws a path-only message and never
    // echoes the body (which contains the secret values).
    private static Dictionary<string, string> ParseKvV2Data(string kvPath, string body)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new SecretResolutionException(
                "vault",
                kvPath,
                $"the Vault response for KV path '{kvPath}' was not valid JSON.",
                ex);
        }

        if (!root.TryGetProperty("data", out var outer)
            || outer.ValueKind != JsonValueKind.Object
            || !outer.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            // A KV v1 read (or an unexpected shape) lacks the data.data envelope.
            throw new SecretResolutionException(
                "vault",
                kvPath,
                $"the Vault response for KV path '{kvPath}' did not have the expected KV v2 " +
                "'data.data' shape; ensure the mount is a KV version 2 secrets engine.");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
        {
            // Vault KV values are strings in the common case; coerce other scalars to
            // their raw text so numeric/boolean fields are still resolvable.
            result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }

        return result;
    }
}
