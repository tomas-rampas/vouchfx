// Tests for S08-B-01 hardening: HttpVaultKvClient — the production IVaultKvClient over
// a thin HttpClient against Vault's KV v2 HTTP API (§17).
//
// These unit tests exercise the client's failure-classification and path-validation
// behaviour WITHOUT a live Vault, by injecting a fake HttpMessageHandler (for transport
// faults / timeouts) or by feeding a malformed KV path (rejected before any request is
// built).  The Docker-gated end-to-end proof against a real hashicorp/vault container
// lives in Platform.Engine.Orchestration.Tests/VaultSecretSourceDockerTests.
//
// Hardening invariants asserted here (review-driven, §12.1 / §17):
//   • a request TIMEOUT (TaskCanceledException, a subtype of OperationCanceledException
//     that ESCAPES the HttpRequestException catch) maps to a SecretResolutionException —
//     so a hung/black-holed Vault classifies as an EnvironmentError, not an unhandled
//     escape;
//   • a malformed KV path ('.'/'..'/backslash/leading-'/'/control char) is rejected
//     with a SecretResolutionException BEFORE the request URI is built — turning a
//     silent misroute into a clear authoring error;
//   • no exception message ever contains the Vault token.

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Platform.Engine.Abstractions.Secrets;
using Platform.Engine.Abstractions.Secrets.Vault;
using Xunit;

namespace Platform.Engine.Abstractions.Tests.Secrets;

/// <summary>
/// Verifies the production <see cref="HttpVaultKvClient"/>: a timing-out/faulting
/// transport surfaces a <see cref="SecretResolutionException"/> (EnvironmentError,
/// §12.1) naming the path but never the token, and a malformed KV path is rejected
/// before any request is issued (§17).
/// </summary>
public sealed class HttpVaultKvClientTests
{
    private const string Token = "vault-token-must-never-leak";

    private static VaultSecretConfiguration Config()
        => new(new Uri("http://127.0.0.1:8200"), Token, VaultSecretConfiguration.DefaultMount);

    // A fake handler that fails every send with a supplied exception factory.  Overrides
    // BOTH the synchronous Send (which HttpVaultKvClient calls) and the async SendAsync
    // so the client's path is exercised exactly as in production.
    private sealed class FaultingHandler : HttpMessageHandler
    {
        private readonly Func<Exception> _fault;

        public FaultingHandler(Func<Exception> fault) => _fault = fault;

        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw _fault();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw _fault();
    }

    private static HttpVaultKvClient ClientThatFailsWith(Func<Exception> fault)
        => new(new HttpClient(new FaultingHandler(fault)), Config());

    // ── Fix 1: a TIMEOUT escapes the HttpRequestException catch — map it cleanly ──
    //
    // HttpClient signals a Timeout by cancelling the request, surfacing a
    // TaskCanceledException (a subtype of OperationCanceledException) rather than an
    // HttpRequestException.  The client must catch it and rethrow as a
    // SecretResolutionException so it classifies as an EnvironmentError, not an escape.

    [Fact]
    public void ReadKeyValues_Timeout_TaskCanceled_ThrowsSecretResolutionException()
    {
        // TaskCanceledException is what HttpClient.Send raises on a Timeout.
        var client = ClientThatFailsWith(() => new TaskCanceledException("simulated timeout"));

        var ex = Assert.Throws<SecretResolutionException>(
            () => client.ReadKeyValues("myapp/db"));

        Assert.Equal("vault", ex.SecretSource);
        Assert.Contains("myapp/db", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadKeyValues_Timeout_OperationCanceled_ThrowsSecretResolutionException()
    {
        // The base OperationCanceledException is also caught (defensive: some platforms
        // surface the bare base type).
        var client = ClientThatFailsWith(() => new OperationCanceledException("cancelled"));

        var ex = Assert.Throws<SecretResolutionException>(
            () => client.ReadKeyValues("myapp/db"));

        Assert.Equal("vault", ex.SecretSource);
        Assert.Contains("myapp/db", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadKeyValues_TransportFault_ThrowsSecretResolutionException()
    {
        // The pre-existing transport-fault path still maps to SecretResolutionException.
        var client = ClientThatFailsWith(() => new HttpRequestException("connection refused"));

        var ex = Assert.Throws<SecretResolutionException>(
            () => client.ReadKeyValues("myapp/db"));

        Assert.Equal("vault", ex.SecretSource);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    // ── Fix 2: a malformed KV path is rejected BEFORE the request URI is built ──────
    //
    // A path containing a '.'/'..' segment, a backslash, a control char, or a leading
    // '/' would silently misroute (Uri normalisation collapses dot-segments) or
    // escape the mount.  The client must reject it with a SecretResolutionException
    // naming the path — never the token — and never issue a request (the faulting
    // handler below would throw a DIFFERENT exception type if it were ever reached).

    [Theory]
    [InlineData("../../sys")]
    [InlineData("myapp/../secret")]
    [InlineData("myapp/./db")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("myapp\\db")]
    [InlineData("/myapp/db")]
    [InlineData("myapp/db\n")]
    [InlineData("myapp/\tdb")]
    public void ReadKeyValues_MalformedPath_ThrowsSecretResolutionExceptionBeforeRequest(string badPath)
    {
        // If validation did NOT run first, the faulting handler would throw an
        // InvalidOperationException (not a SecretResolutionException) — so asserting on
        // SecretResolutionException proves the path was rejected before any send.
        var client = ClientThatFailsWith(
            () => new InvalidOperationException("the request must never be issued for a bad path"));

        var ex = Assert.Throws<SecretResolutionException>(
            () => client.ReadKeyValues(badPath));

        Assert.Equal("vault", ex.SecretSource);
        Assert.Equal(badPath, ex.SecretPath);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("myapp/db")]
    [InlineData("secret/app/database/credentials")]
    [InlineData("a")]
    public void ReadKeyValues_WellFormedPath_PassesValidationAndReachesTransport(string goodPath)
    {
        // A well-formed path must NOT be rejected by validation: it should reach the
        // transport, where the faulting handler then throws — proving validation let it
        // through.  (We use a transport fault so the call still terminates without a
        // live Vault; the point is that validation did not short-circuit it.)
        var sentinelReached = false;
        var client = ClientThatFailsWith(() =>
        {
            sentinelReached = true;
            return new HttpRequestException("reached transport");
        });

        Assert.Throws<SecretResolutionException>(() => client.ReadKeyValues(goodPath));
        Assert.True(sentinelReached, "a well-formed path must reach the transport, not be rejected by validation.");
    }
}
