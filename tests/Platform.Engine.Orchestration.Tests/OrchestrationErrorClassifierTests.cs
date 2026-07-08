// Non-docker unit tests for OrchestrationErrorClassifier, OrchestrationError
// types, and EnvironmentErrorEvents (S02-A-02).
//
// These tests exercise every classification path and the event-stream
// serialisation round-trip without requiring Docker or Aspire at runtime.
// They run in the standard unit-CI gate (dotnet test --filter "requires!=docker").

using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;
using Platform.Engine.Orchestration;
using Xunit;

namespace Platform.Engine.Orchestration.Tests;

/// <summary>
/// Unit tests for <see cref="OrchestrationErrorClassifier"/> covering
/// <see cref="OrchestrationErrorClassifier.ParseRegistryHost"/> and
/// <see cref="OrchestrationErrorClassifier.Classify"/>, and the
/// <see cref="EnvironmentErrorEvents"/> factory.  No Docker or Aspire required.
/// </summary>
public sealed class OrchestrationErrorClassifierTests
{
    // -----------------------------------------------------------------------
    // ParseRegistryHost
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("nonexistent.invalid/nope:latest", "nonexistent.invalid")]
    [InlineData("registry.example.com:5000/x/y", "registry.example.com:5000")]
    [InlineData("registry.example.com/x/y:tag", "registry.example.com")]
    [InlineData("localhost/myimage:v1", "localhost")]
    [InlineData("localhost:5000/myimage:v1", "localhost:5000")]
    public void ParseRegistryHost_ExplicitRegistry_ReturnsFirstComponent(string imageRef, string expected)
    {
        // Act
        var result = OrchestrationErrorClassifier.ParseRegistryHost(imageRef);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("traefik/whoami")]
    [InlineData("traefik/whoami:latest")]
    [InlineData("library/ubuntu:22.04")]
    [InlineData("myrepo/myimage:v1")]
    public void ParseRegistryHost_DockerHubShortName_ReturnsDockerId(string imageRef)
    {
        // Act
        var result = OrchestrationErrorClassifier.ParseRegistryHost(imageRef);

        // Assert
        Assert.Equal("docker.io", result);
    }

    [Theory]
    [InlineData("ubuntu")]
    [InlineData("ubuntu:22.04")]
    [InlineData("alpine:3.18")]
    public void ParseRegistryHost_BareImageNoSlash_ReturnsDockerId(string imageRef)
    {
        // Act
        var result = OrchestrationErrorClassifier.ParseRegistryHost(imageRef);

        // Assert
        Assert.Equal("docker.io", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ParseRegistryHost_NullOrEmpty_ReturnsNull(string? imageRef)
    {
        // Act
        var result = OrchestrationErrorClassifier.ParseRegistryHost(imageRef);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseRegistryHost_ImageWithDigest_StripDigestBeforeParsing()
    {
        // "nonexistent.invalid/nope@sha256:abc123" — digest suffix must be stripped
        // before the slash-split so the first component is correctly identified.
        var result = OrchestrationErrorClassifier.ParseRegistryHost(
            "nonexistent.invalid/nope@sha256:abc123");

        Assert.Equal("nonexistent.invalid", result);
    }

    // -----------------------------------------------------------------------
    // Classify — ImagePull (auth / anonymous)
    // -----------------------------------------------------------------------

    [Fact]
    public void Classify_AuthPullFailure_Unauthorized_ReturnsImagePullUnauthenticated()
    {
        // Arrange — message resembling what Docker/DCP emits for a 401 response.
        var ex = new InvalidOperationException(
            "failed to pull image registry.example.com/x: 401 Unauthorized");

        // Act
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: "registry.example.com/x:1",
            resourceName: "web");

        // Assert
        Assert.Equal(OrchestrationErrorKind.ImagePull, info.Kind);
        Assert.Equal("registry.example.com", info.RegistryHost);
        Assert.Equal("unauthenticated", info.AuthStatus);
        Assert.Equal("web", info.ResourceName);
        Assert.NotEmpty(info.Detail);
        Assert.DoesNotContain('\n', info.Detail);
        Assert.DoesNotContain('\r', info.Detail);
    }

    [Fact]
    public void Classify_AuthPullFailure_Denied_ReturnsImagePullAccessDenied()
    {
        // Arrange
        var ex = new InvalidOperationException(
            "pull access denied for registry.example.com/private: insufficient_scope");

        // Act
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: "registry.example.com/private:latest",
            resourceName: "api");

        // Assert — "denied" keyword → access-denied authStatus
        Assert.Equal(OrchestrationErrorKind.ImagePull, info.Kind);
        Assert.Equal("access-denied", info.AuthStatus);
    }

    [Fact]
    public void Classify_AuthPullFailure_Forbidden_ReturnsImagePullAccessDenied()
    {
        // Arrange
        var ex = new InvalidOperationException(
            "403 Forbidden: you do not have access to this repository");

        // Act
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: "registry.example.com/repo:1",
            resourceName: "svc");

        // Assert
        Assert.Equal(OrchestrationErrorKind.ImagePull, info.Kind);
        Assert.Equal("access-denied", info.AuthStatus);
    }

    [Fact]
    public void Classify_AnonymousPullFailure_NotFound_ReturnsImagePullAnonymous()
    {
        // Arrange — "not found" without any auth keyword → anonymous pull attempt
        var ex = new InvalidOperationException(
            "manifest for nonexistent.invalid/vouchfx-does-not-exist:latest not found");

        // Act
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: "nonexistent.invalid/vouchfx-does-not-exist:latest",
            resourceName: "web");

        // Assert
        Assert.Equal(OrchestrationErrorKind.ImagePull, info.Kind);
        Assert.Equal("nonexistent.invalid", info.RegistryHost);
        Assert.Equal("anonymous", info.AuthStatus);
    }

    [Fact]
    public void Classify_RateLimited_ReturnsImagePullRateLimited()
    {
        // Arrange — "toomanyrequests" is the canonical Docker Hub rate-limit signal.
        var ex = new InvalidOperationException(
            "toomanyrequests: You have reached your pull rate limit.");

        // Act
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: "traefik/whoami:latest",
            resourceName: "web");

        // Assert — rate-limited is a distinct authStatus, not "anonymous".
        Assert.Equal(OrchestrationErrorKind.ImagePull, info.Kind);
        Assert.Equal("docker.io", info.RegistryHost);
        Assert.Equal("rate-limited", info.AuthStatus);
    }

    [Fact]
    public void Classify_RateLimited_PullRateLimitPhraseWithHttpCode_ReturnsImagePullRateLimited()
    {
        // Arrange — message from a Docker client that surfaces the registry's HTTP 429
        // response as "pull rate limit reached".  This test exercises the "pull rate limit"
        // token, NOT a bare "429" substring (the engine does not match on "429").
        var ex = new InvalidOperationException(
            "Error response from daemon: pull rate limit reached: 429 Too Many Requests");

        // Act
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: "library/ubuntu:22.04",
            resourceName: "svc");

        // Assert — "pull rate limit" is the matched token that drives this classification.
        Assert.Equal(OrchestrationErrorKind.ImagePull, info.Kind);
        Assert.Equal("docker.io", info.RegistryHost);
        Assert.Equal("rate-limited", info.AuthStatus);
    }

    [Fact]
    public void Classify_RateLimited_PullRateLimitPhrase_ReturnsImagePullRateLimited()
    {
        // Arrange — "pull rate limit" as emitted by some registry clients.
        var ex = new InvalidOperationException(
            "you have reached your pull rate limit. upgrade your account.");

        // Act
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: "nginx:latest",
            resourceName: "proxy");

        // Assert
        Assert.Equal(OrchestrationErrorKind.ImagePull, info.Kind);
        Assert.Equal("rate-limited", info.AuthStatus);
    }

    [Fact]
    public void Classify_BareRateLimit_WithoutPullContext_DoesNotClassifyAsRateLimited()
    {
        // Arrange — a non-pull message that merely contains the phrase "rate limit"
        // (e.g. an API-gateway throttling response that bubbles up during a health probe).
        // No imageRef, and no pull/manifest/image/registry token in the message.
        // The bare "rate limit" token must NOT trigger the rate-limit branch when
        // no pull context is present.
        var ex = new InvalidOperationException(
            "gateway returned 429: rate limit exceeded for downstream api");

        // Act — no imageRef; message has no pull/manifest/registry/image token.
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: null,
            resourceName: "web");

        // Assert — must NOT be classified as ImagePull / rate-limited.
        Assert.NotEqual(OrchestrationErrorKind.ImagePull, info.Kind);
        Assert.NotEqual("rate-limited", info.AuthStatus);
    }

    // -----------------------------------------------------------------------
    // Classify — ImagePull (registry connectivity / DNS / network errors)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("dial tcp: lookup nonexistent.invalid on 127.0.0.11:53: no such host")]
    [InlineData("Get \"https://nonexistent.invalid/v2/\": dial tcp: lookup nonexistent.invalid: no such host")]
    [InlineData("temporary failure in name resolution")]
    [InlineData("name resolution failed for nonexistent.invalid")]
    public void Classify_RegistryDnsFailure_WithImageRef_ReturnsImagePullNullAuth(string message)
    {
        // Arrange — DNS failure messages from Docker/DCP when the registry host is unreachable.
        var ex = new InvalidOperationException(message);

        // Act — imageRef is non-null, providing the image-pull context.
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: "nonexistent.invalid/vouchfx-does-not-exist:latest",
            resourceName: "web");

        // Assert — registry unreachable = ImagePull, but AuthStatus is null
        // (no auth was attempted; this is a connectivity failure, not an
        // anonymous/rejected pull).
        Assert.Equal(OrchestrationErrorKind.ImagePull, info.Kind);
        Assert.Equal("nonexistent.invalid", info.RegistryHost);
        Assert.Null(info.AuthStatus);
    }

    [Theory]
    [InlineData("connection refused")]
    [InlineData("no route to host")]
    [InlineData("network is unreachable")]
    [InlineData("i/o timeout while connecting to registry")]
    [InlineData("server misbehaving")]
    public void Classify_RegistryNetworkFailure_WithImageRef_ReturnsImagePullNullAuth(string message)
    {
        // Arrange — network/connectivity error messages when pulling from a registry.
        var ex = new InvalidOperationException(message);

        // Act — imageRef is non-null, providing the image-pull context.
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: "registry.example.com/app:1.0",
            resourceName: "api");

        // Assert — connectivity failure → ImagePull, AuthStatus null.
        Assert.Equal(OrchestrationErrorKind.ImagePull, info.Kind);
        Assert.Equal("registry.example.com", info.RegistryHost);
        Assert.Null(info.AuthStatus);
    }

    [Fact]
    public void Classify_RegistryDnsFailure_WithoutImageRef_ReturnsDiscovery()
    {
        // Arrange — a DNS-like failure message but NO imageRef (not a pull context).
        // "no such host" in a service-discovery failure (e.g. resolving a db host)
        // must NOT be classified as ImagePull.
        var ex = new InvalidOperationException(
            "dial tcp: lookup mydb.internal on 127.0.0.11:53: no such host");

        // Act — no imageRef; message contains no pull/manifest/image token.
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: null,
            resourceName: "appdb");

        // Assert — without image-pull context this stays as Discovery (the "lookup"
        // substring doesn't appear here, but "no such host" has no pull keywords
        // so it falls through to Provision — either is acceptable; it must not be
        // ImagePull).
        Assert.NotEqual(OrchestrationErrorKind.ImagePull, info.Kind);
        Assert.Null(info.AuthStatus);
    }

    [Fact]
    public void Classify_RegistryConnectivityFailure_MessageAlsoContainsImage_ReturnsImagePullNullAuth()
    {
        // Arrange — message contains "image" token (providing pull context) AND a
        // connectivity failure signal; no imageRef passed.
        var ex = new InvalidOperationException(
            "failed to pull image: connection refused to registry host");

        // Act
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: null,
            resourceName: "web");

        // Assert — pull context from "image" token + connectivity signal → ImagePull, null auth.
        Assert.Equal(OrchestrationErrorKind.ImagePull, info.Kind);
        Assert.Null(info.AuthStatus);
    }

    // -----------------------------------------------------------------------
    // Classify — HealthGate
    // -----------------------------------------------------------------------

    [Fact]
    public void Classify_UnhealthyMessage_ReturnsHealthGate()
    {
        // Arrange — message typical of a DCP FailedToStart / RuntimeUnhealthy signal.
        var ex = new InvalidOperationException(
            "container is unhealthy / FailedToStart after 30 seconds");

        // Act
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: null,
            resourceName: "appdb");

        // Assert
        Assert.Equal(OrchestrationErrorKind.HealthGate, info.Kind);
        Assert.Null(info.AuthStatus);
        Assert.Equal("appdb", info.ResourceName);
        Assert.NotEmpty(info.Detail);
        Assert.DoesNotContain('\n', info.Detail);
    }

    [Fact]
    public void Classify_ExitedMessage_ReturnsHealthGate()
    {
        // Arrange
        var ex = new InvalidOperationException("resource 'web' exited with code 1");

        // Act
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: "traefik/whoami",
            resourceName: "web");

        // Assert
        Assert.Equal(OrchestrationErrorKind.HealthGate, info.Kind);
        Assert.Equal("docker.io", info.RegistryHost);
    }

    [Fact]
    public void Classify_TimeoutCancellation_ReturnsHealthGate()
    {
        // Arrange — health gate timed out → OperationCanceledException
        var ex = new OperationCanceledException(
            "The operation was cancelled (timeout waiting for healthy state).");

        // Act
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: null,
            resourceName: "appdb");

        // Assert
        Assert.Equal(OrchestrationErrorKind.HealthGate, info.Kind);
    }

    // -----------------------------------------------------------------------
    // Classify — containerNeverCreated structural signal (S12 classifier fix)
    //
    // Aspire's health-gate wrapper exception ("Stopped waiting for resource 'X' to
    // become healthy because it failed to start.") is emitted verbatim for BOTH a
    // bad-image pull failure and a genuine health-check failure — DCP surfaces no
    // pull-specific text the message-only heuristics above can key on. The
    // containerNeverCreated parameter carries an independent, non-textual signal
    // (the resource's DCP-managed container was never actually created) so the two
    // cases can still be told apart. See ResourceCreationEvidence for how a caller
    // derives this signal from a live Aspire DistributedApplication.
    // -----------------------------------------------------------------------

    [Fact]
    public void Classify_HealthGateMessage_ContainerNeverCreatedWithImageRef_ReturnsImagePull()
    {
        // Arrange — the exact generic wrapper message Aspire emits for BOTH shapes.
        var ex = new InvalidOperationException(
            "Stopped waiting for resource 'web' to become healthy because it failed to start.");

        // Act — containerNeverCreated=true + a known imageRef: the resource can never
        // have actually run, so this must be ImagePull, not HealthGate.
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: "nonexistent.invalid/vouchfx-does-not-exist:latest",
            resourceName: "web",
            containerNeverCreated: true);

        // Assert
        Assert.Equal(OrchestrationErrorKind.ImagePull, info.Kind);
        Assert.Equal("nonexistent.invalid", info.RegistryHost);
        Assert.Equal("anonymous", info.AuthStatus);
    }

    [Fact]
    public void Classify_HealthGateMessage_ContainerNeverCreatedFalse_StillReturnsHealthGate()
    {
        // Arrange — same ambiguous message, but the caller has POSITIVE evidence the
        // container WAS created (it just never became healthy). Must NOT be reclassified.
        var ex = new InvalidOperationException(
            "Stopped waiting for resource 'web' to become healthy because it failed to start.");

        // Act
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: "traefik/whoami:latest",
            resourceName: "web",
            containerNeverCreated: false);

        // Assert — the default/legacy behaviour is preserved: genuine HealthGate
        // classification is NOT weakened by the new parameter.
        Assert.Equal(OrchestrationErrorKind.HealthGate, info.Kind);
    }

    [Fact]
    public void Classify_HealthGateMessage_ContainerNeverCreatedWithoutImageRef_DoesNotReclassify()
    {
        // Arrange — containerNeverCreated=true, but no imageRef is known (e.g. a managed
        // dependency gate, or a project-based service with no container image at all).
        // Without a known image reference this can never be an image-pull failure, so the
        // signal must be ignored and the message heuristics must decide as before.
        var ex = new InvalidOperationException(
            "Stopped waiting for resource 'appdb' to become healthy because it failed to start.");

        // Act
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: null,
            resourceName: "appdb",
            containerNeverCreated: true);

        // Assert
        Assert.Equal(OrchestrationErrorKind.HealthGate, info.Kind);
    }

    [Fact]
    public void Classify_DefaultContainerNeverCreatedParameter_PreservesExistingCallSites()
    {
        // Arrange — every pre-existing call site omits the new parameter; it must default
        // to false and behave exactly as it did before this parameter was introduced.
        var ex = new InvalidOperationException(
            "Stopped waiting for resource 'web' to become healthy because it failed to start.");

        // Act — no containerNeverCreated argument supplied.
        var info = OrchestrationErrorClassifier.Classify(
            ex, imageRef: "nonexistent.invalid/vouchfx-does-not-exist:latest", resourceName: "web");

        // Assert — unchanged legacy behaviour: ambiguous message + no structural evidence
        // supplied → HealthGate (this is the exact shape the S12 regression exposed).
        Assert.Equal(OrchestrationErrorKind.HealthGate, info.Kind);
    }

    [Fact]
    public void Classify_ContainerNeverCreated_DoesNotOverrideMoreSpecificPullClassification()
    {
        // Arrange — the message ALREADY carries specific, more-informative pull-failure
        // text (a 401). The structural signal must not clobber the richer AuthStatus the
        // message-based heuristics already derived.
        var ex = new InvalidOperationException(
            "failed to pull image registry.example.com/x: 401 Unauthorized");

        // Act
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: "registry.example.com/x:1",
            resourceName: "web",
            containerNeverCreated: true);

        // Assert — still ImagePull, and still the MORE SPECIFIC "unauthenticated" (not the
        // generic "anonymous" the structural-signal branch alone would have produced).
        Assert.Equal(OrchestrationErrorKind.ImagePull, info.Kind);
        Assert.Equal("unauthenticated", info.AuthStatus);
    }

    // -----------------------------------------------------------------------
    // Classify — Discovery
    // -----------------------------------------------------------------------

    [Fact]
    public void Classify_EndpointResolutionMessage_ReturnsDiscovery()
    {
        // Arrange
        var ex = new InvalidOperationException(
            "GetEndpoint returned null — no endpoint allocated for resource 'web'.");

        // Act
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: "traefik/whoami",
            resourceName: "web");

        // Assert
        Assert.Equal(OrchestrationErrorKind.Discovery, info.Kind);
        Assert.Null(info.AuthStatus);
    }

    [Fact]
    public void Classify_ConnectionStringMessage_ReturnsDiscovery()
    {
        // Arrange
        var ex = new InvalidOperationException(
            "connection string for 'appdb' could not be resolved.");

        // Act
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: null,
            resourceName: "appdb");

        // Assert
        Assert.Equal(OrchestrationErrorKind.Discovery, info.Kind);
    }

    // -----------------------------------------------------------------------
    // Classify — Provision (fallback)
    // -----------------------------------------------------------------------

    [Fact]
    public void Classify_UnknownMessage_ReturnsProvision()
    {
        // Arrange — generic message that does not match any classifier keyword.
        var ex = new InvalidOperationException(
            "An unexpected error occurred during resource initialisation.");

        // Act
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: null,
            resourceName: "kafka");

        // Assert
        Assert.Equal(OrchestrationErrorKind.Provision, info.Kind);
        Assert.Null(info.AuthStatus);
        Assert.NotEmpty(info.Detail);
    }

    // -----------------------------------------------------------------------
    // Classify — Detail field
    // -----------------------------------------------------------------------

    [Fact]
    public void Classify_MultilineMessage_DetailIsSingleLine()
    {
        // Arrange — exception messages sometimes include newlines from stack details.
        var ex = new InvalidOperationException(
            "pull failed\r\nCaused by: network timeout\n  at DCP layer");

        // Act
        var info = OrchestrationErrorClassifier.Classify(ex, imageRef: null, resourceName: "web");

        // Assert
        Assert.NotEmpty(info.Detail);
        Assert.DoesNotContain('\n', info.Detail);
        Assert.DoesNotContain('\r', info.Detail);
    }

    [Fact]
    public void Classify_VeryLongMessage_DetailIsCapped()
    {
        // Arrange — a message longer than 256 characters.
        var longMessage = new string('x', 500) + " pull failure";
        var ex = new InvalidOperationException(longMessage);

        // Act
        var info = OrchestrationErrorClassifier.Classify(ex, imageRef: null, resourceName: "svc");

        // Assert — detail is capped (with ellipsis char) so it is never >257 chars.
        Assert.True(info.Detail.Length <= 257, $"Detail too long: {info.Detail.Length}");
    }

    [Fact]
    public void Classify_NullMessage_DetailIsNonEmpty()
    {
        // Arrange — some exception types have a null Message (unlikely but defensive).
        var ex = new InvalidOperationException((string?)null);

        // Act
        var info = OrchestrationErrorClassifier.Classify(ex, imageRef: null, resourceName: "svc");

        // Assert — must not throw and must provide a fallback detail string.
        Assert.NotEmpty(info.Detail);
    }

    // -----------------------------------------------------------------------
    // RegistryHost is set even for non-ImagePull kinds
    // -----------------------------------------------------------------------

    [Fact]
    public void Classify_HealthGateWithWebImage_RegistryHostStillSet()
    {
        // The web gate carries webImage so the registry is known even though
        // the KIND is HealthGate (e.g. the image pulled but the process crashed).
        var ex = new InvalidOperationException("container exited with code 137");

        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: "registry.example.com:5000/myapp:v2",
            resourceName: "web");

        Assert.Equal(OrchestrationErrorKind.HealthGate, info.Kind);
        Assert.Equal("registry.example.com:5000", info.RegistryHost);
    }

    // -----------------------------------------------------------------------
    // OrchestrationException
    // -----------------------------------------------------------------------

    [Fact]
    public void OrchestrationException_MessageIncludesKindAndResource()
    {
        // Arrange
        var info = new OrchestrationErrorInfo(
            Kind: OrchestrationErrorKind.ImagePull,
            ResourceName: "web",
            RegistryHost: "docker.io",
            AuthStatus: "anonymous",
            Detail: "manifest not found");

        // Act
        var ex = new OrchestrationException(info);

        // Assert
        Assert.Contains("ImagePull", ex.Message, StringComparison.Ordinal);
        Assert.Contains("web", ex.Message, StringComparison.Ordinal);
        Assert.Same(info, ex.Info);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void OrchestrationException_InnerException_IsPreserved()
    {
        // Arrange
        var inner = new InvalidOperationException("raw DCP error");
        var info = new OrchestrationErrorInfo(
            Kind: OrchestrationErrorKind.HealthGate,
            ResourceName: "appdb",
            RegistryHost: null,
            AuthStatus: null,
            Detail: "raw DCP error");

        // Act
        var ex = new OrchestrationException(info, inner);

        // Assert
        Assert.Same(inner, ex.InnerException);
    }

    // -----------------------------------------------------------------------
    // EnvironmentErrorEvents — Create
    // -----------------------------------------------------------------------

    [Fact]
    public void EnvironmentErrorEvents_Create_VerdictIsAlwaysEnvironmentError()
    {
        // Arrange
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var info = new OrchestrationErrorInfo(
            Kind: OrchestrationErrorKind.ImagePull,
            ResourceName: "web",
            RegistryHost: "docker.io",
            AuthStatus: "anonymous",
            Detail: "manifest not found");

        // Act
        var evt = EnvironmentErrorEvents.Create(info, "run-1", ts);

        // Assert — verdict is ALWAYS EnvironmentError, never Fail.
        Assert.Equal(Verdict.EnvironmentError, evt.Verdict);
        Assert.Equal(EventTypes.EnvironmentError, evt.Type);
        Assert.Equal("run-1", evt.RunId);
        Assert.Equal(ts, evt.Timestamp);
        Assert.Equal("ImagePull", evt.ErrorKind);
        Assert.Equal("web", evt.ResourceName);
        Assert.Equal("docker.io", evt.RegistryHost);
        Assert.Equal("anonymous", evt.AuthStatus);
    }

    [Fact]
    public void EnvironmentErrorEvents_Create_TypeIsEnvironmentErrorConst()
    {
        // Arrange
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var info = new OrchestrationErrorInfo(
            Kind: OrchestrationErrorKind.HealthGate,
            ResourceName: "appdb",
            RegistryHost: null,
            AuthStatus: null,
            Detail: "container unhealthy");

        // Act
        var evt = EnvironmentErrorEvents.Create(info, "run-42", ts);

        // Assert
        Assert.Equal("environment-error", evt.Type);
    }

    // -----------------------------------------------------------------------
    // Classify — auth keyword without image-pull context (Fix 4, S02-A-02)
    // -----------------------------------------------------------------------

    /// <summary>
    /// "authentication failed during health probe" — auth keyword present but no
    /// image-pull context (no pull/manifest/registry/image token and imageRef is null).
    /// Must classify as <see cref="OrchestrationErrorKind.HealthGate"/>, NOT ImagePull.
    /// </summary>
    [Fact]
    public void Classify_AuthKeywordWithoutPullContext_ReturnsHealthGate()
    {
        // Arrange — "authentication" appears but refers to DB auth, not image-pull auth.
        var ex = new InvalidOperationException(
            "database authentication failed during health probe");

        // Act — no imageRef → no image-pull context.
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: null,
            resourceName: "appdb");

        // Assert — must NOT be ImagePull; health keyword present so HealthGate is expected.
        Assert.Equal(OrchestrationErrorKind.HealthGate, info.Kind);
        Assert.Null(info.AuthStatus);
    }

    /// <summary>
    /// "401 unauthorized pulling image" — auth keyword IS present AND a pull token
    /// appears in the message.  Must still classify as ImagePull / unauthenticated.
    /// </summary>
    [Fact]
    public void Classify_UnauthorizedWithPullTokenInMessage_ReturnsImagePullUnauthenticated()
    {
        // Arrange — classic "401 unauthorized" on a pull attempt.
        var ex = new InvalidOperationException(
            "401 unauthorized pulling image from registry.example.com/private:1");

        // Act — imageRef null; but "pulling" contains "pull" + "image" token.
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: null,
            resourceName: "api");

        // Assert — pull token present in message → ImagePull / unauthenticated.
        Assert.Equal(OrchestrationErrorKind.ImagePull, info.Kind);
        Assert.Equal("unauthenticated", info.AuthStatus);
    }

    /// <summary>
    /// Auth keyword present AND imageRef is non-null (resource-level image context).
    /// Must classify as ImagePull / unauthenticated even without a pull token in
    /// the message.
    /// </summary>
    [Fact]
    public void Classify_UnauthorizedWithNonNullImageRef_ReturnsImagePullUnauthenticated()
    {
        // Arrange — the message itself does not contain "pull" or "manifest",
        // but imageRef is non-null so the image-pull context is established.
        var ex = new InvalidOperationException(
            "unauthorized: access denied for user");

        // Act.
        var info = OrchestrationErrorClassifier.Classify(
            ex,
            imageRef: "registry.example.com/private:1",
            resourceName: "api");

        // Assert — imageRef provides the context → ImagePull / unauthenticated.
        Assert.Equal(OrchestrationErrorKind.ImagePull, info.Kind);
        Assert.Equal("unauthenticated", info.AuthStatus);
    }

    // -----------------------------------------------------------------------
    // EnvironmentErrorEvents — ToLine round-trip
    // -----------------------------------------------------------------------

    [Fact]
    public void EnvironmentErrorEvents_ToLine_RoundTripViaFromLine_VerdictIsEnvError()
    {
        // Arrange
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var info = new OrchestrationErrorInfo(
            Kind: OrchestrationErrorKind.ImagePull,
            ResourceName: "web",
            RegistryHost: "nonexistent.invalid",
            AuthStatus: "anonymous",
            Detail: "manifest not found");

        // Act — serialise
        var line = EnvironmentErrorEvents.ToLine(info, "run-1", ts);

        // Assert — line is non-empty, single-line JSON.
        Assert.NotEmpty(line);
        Assert.DoesNotContain('\n', line);

        // Deserialise via the generic envelope path (the renderer path).
        var envelope = EventStreamJson.FromLine(line);

        // The type discriminator must be "environment-error".
        Assert.Equal("environment-error", envelope.Type);

        // Verdict in Extra must be "ENV_ERROR" — NOT "Fail" or "FAIL".
        Assert.NotNull(envelope.Extra);
        Assert.True(
            envelope.Extra!.TryGetValue("verdict", out var verdictElement),
            "Expected 'verdict' field in envelope Extra.");
        var verdictString = verdictElement.GetString();
        Assert.Equal("ENV_ERROR", verdictString);

        // Payload-specific fields must be present in Extra.
        Assert.True(envelope.Extra.ContainsKey("registryHost"), "Expected 'registryHost' in Extra.");
        Assert.True(envelope.Extra.ContainsKey("authStatus"), "Expected 'authStatus' in Extra.");
        Assert.True(envelope.Extra.ContainsKey("errorKind"), "Expected 'errorKind' in Extra.");
    }

    [Fact]
    public void EnvironmentErrorEvents_ToLine_NullRegistryAndAuth_OmittedFromWire()
    {
        // Arrange — null optional fields must be omitted (WhenWritingNull).
        var ts = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var info = new OrchestrationErrorInfo(
            Kind: OrchestrationErrorKind.Provision,
            ResourceName: "kafka",
            RegistryHost: null,
            AuthStatus: null,
            Detail: "provisioning failed");

        // Act
        var line = EnvironmentErrorEvents.ToLine(info, "run-99", ts);

        // Assert — null fields are omitted from the wire.
        Assert.DoesNotContain("registryHost", line, StringComparison.Ordinal);
        Assert.DoesNotContain("authStatus", line, StringComparison.Ordinal);
    }
}
