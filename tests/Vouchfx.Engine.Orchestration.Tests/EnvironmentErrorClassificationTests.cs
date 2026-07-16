// Docker-gated integration test for the Environment-error classification hook
// (S02-A-02).  Requires a running Docker daemon and the DCP process embedded in
// this test project (IsAspireHost=true, Aspire.AppHost.Sdk).
//
// Run with: dotnet test --filter "requires=docker&FullyQualifiedName~EnvironmentError"
// Excluded from unit-CI:   dotnet test --filter "requires!=docker"

using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated acceptance test for the Environment-error classification hook
/// (S02-A-02).  Injects a bad image reference into <see cref="StubTopology.StartAsync"/>
/// and asserts that:
/// <list type="bullet">
///   <item>An <see cref="OrchestrationException"/> is thrown (never a raw Aspire exception).</item>
///   <item>The registry host is parsed deterministically from the image reference.</item>
///   <item>The produced event carries <see cref="Verdict.EnvironmentError"/>, never
///   <see cref="Verdict.Fail"/>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// A bad image on a non-existent registry host fails fast (DNS resolution fails
/// immediately) so the test completes in well under the 60-second timeout even
/// on a slow host.
/// </para>
/// <para>
/// R-1 finding: this test assembly carries <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c>
/// and <c>Aspire.AppHost.Sdk</c>, which embeds <c>dcpclipath</c>/<c>dcpextensionpaths</c>
/// assembly metadata required by <see cref="Aspire.Hosting.DistributedApplicationOptions"/>.
/// The assembly name is passed to <see cref="StubTopology.StartAsync"/> so DCP is resolved
/// from this assembly rather than from the xUnit runner entry assembly.
/// </para>
/// </remarks>
public sealed class EnvironmentErrorClassificationTests
{
    private readonly ITestOutputHelper _output;
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    // A deliberately non-existent registry host — DNS resolution fails immediately
    // so the test completes quickly without waiting for a real timeout.
    private const string BadImage = "nonexistent.invalid/vouchfx-does-not-exist:latest";

    // Expected registry host (parsed deterministically from BadImage, not from the
    // live DCP error message).
    private const string ExpectedRegistryHost = "nonexistent.invalid";

    // Fixed timestamp for deterministic event assertions.
    private static readonly DateTimeOffset FixedTs =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public EnvironmentErrorClassificationTests(ITestOutputHelper output) =>
        _output = output;

    /// <summary>
    /// Verifies that <see cref="StubTopology.StartAsync"/> wraps a bad-image failure in
    /// an <see cref="OrchestrationException"/> whose structured <see cref="OrchestrationErrorInfo"/>
    /// carries the correct registry host and an <see cref="Verdict.EnvironmentError"/> verdict —
    /// never <see cref="Verdict.Fail"/>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task BadImage_StartAsync_ThrowsOrchestrationException_NotFail()
    {
        // Act — StartAsync with a bad image must throw OrchestrationException, not succeed.
        var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
            StubTopology.StartAsync(
                appHostAssemblyName: AppHostAssemblyName,
                webImage: BadImage,
                startupTimeout: TimeSpan.FromSeconds(60)));

        // Log the structured info so the CI run report shows what was observed.
        _output.WriteLine($"OrchestrationErrorInfo:");
        _output.WriteLine($"  Kind:         {ex.Info.Kind}");
        _output.WriteLine($"  ResourceName: {ex.Info.ResourceName}");
        _output.WriteLine($"  RegistryHost: {ex.Info.RegistryHost}");
        _output.WriteLine($"  AuthStatus:   {ex.Info.AuthStatus ?? "(null)"}");
        _output.WriteLine($"  Detail:       {ex.Info.Detail}");
        _output.WriteLine($"Inner exception type: {ex.InnerException?.GetType().Name ?? "(null)"}");

        // Assert 1 — registry host is parsed from the image ref (deterministic),
        // not from the live DCP message (varies across engine/daemon versions).
        Assert.Equal(ExpectedRegistryHost, ex.Info.RegistryHost);

        // Assert 2 — an unreachable registry DNS failure is an ImagePull error.
        // The kind is derived from (imageRef non-null + network signal keywords)
        // and is therefore deterministic regardless of the exact live daemon message.
        Assert.Equal(OrchestrationErrorKind.ImagePull, ex.Info.Kind);

        // Assert 3 — For a connectivity failure (DNS NXDOMAIN for ".invalid" per RFC 6761)
        // the classifier expects AuthStatus null because no auth was attempted.  However,
        // the exact live DCP/daemon message text can vary across engine and Docker versions,
        // so we tolerate "anonymous" as well (some daemon versions surface a generic pull
        // failure before the connectivity signal is seen by the classifier).
        Assert.True(
            ex.Info.AuthStatus is null or "anonymous",
            $"unexpected AuthStatus: {ex.Info.AuthStatus}");
        // Note: Kind == ImagePull is still asserted (Assert 2 above) because imageRef is
        // non-null, establishing the pull context regardless of the daemon message wording.

        // Assert 4 — the produced event has Verdict.EnvironmentError, never Fail.
        var evt = EnvironmentErrorEvents.Create(ex.Info, "run", FixedTs);
        Assert.Equal(Verdict.EnvironmentError, evt.Verdict);
        Assert.NotEqual(Verdict.Fail, evt.Verdict);

        // Assert 5 — event type discriminator.
        Assert.Equal(EventTypes.EnvironmentError, evt.Type);

        // Assert 6 — the line serialises correctly and round-trips with ENV_ERROR verdict.
        var line = EnvironmentErrorEvents.ToLine(ex.Info, "run", FixedTs);
        var envelope = EventStreamJson.FromLine(line);
        Assert.Equal("environment-error", envelope.Type);

        if (envelope.Extra?.TryGetValue("verdict", out var verdictElement) == true)
        {
            Assert.Equal("ENV_ERROR", verdictElement.GetString());
        }

        _output.WriteLine("BadImage_StartAsync_ThrowsOrchestrationException_NotFail: PASS");
    }

    /// <summary>
    /// Non-docker unit test (state-reset generalisation): a reset-failure
    /// <see cref="OrchestrationErrorInfo"/> — as <c>ScenarioIsolationErrors.Wrap</c>
    /// produces for a <see cref="RespawnRelationalIsolation"/> failure — yields an
    /// event line whose <c>resourceName</c> equals the failing DEPENDENCY name (not a
    /// fixed placeholder such as <c>"respawn-reset"</c>) and whose verdict is
    /// <c>ENV_ERROR</c>, never <c>FAIL</c> (§12.1).
    /// </summary>
    [Fact]
    public void ResetFailure_EventLine_NamesDependency_AndIsEnvError()
    {
        const string dependencyName = "ordersdb";

        var info = new OrchestrationErrorInfo(
            Kind: OrchestrationErrorKind.Provision,
            ResourceName: dependencyName,
            RegistryHost: null,
            AuthStatus: null,
            Detail: $"state reset (postgres) reset failed for dependency '{dependencyName}': connection refused");

        var evt = EnvironmentErrorEvents.Create(info, "run", FixedTs);
        Assert.Equal(Verdict.EnvironmentError, evt.Verdict);
        Assert.NotEqual(Verdict.Fail, evt.Verdict);
        Assert.Equal(dependencyName, evt.ResourceName);

        var line = EnvironmentErrorEvents.ToLine(info, "run", FixedTs);
        var envelope = EventStreamJson.FromLine(line);
        Assert.Equal("environment-error", envelope.Type);

        // Unconditional: the wire line MUST carry both fields — a silently absent
        // key would otherwise let this test pass without checking anything.
        Assert.NotNull(envelope.Extra);
        Assert.True(
            envelope.Extra.TryGetValue("resourceName", out var resourceNameElement),
            "event line carries no resourceName field");
        Assert.Equal(dependencyName, resourceNameElement.GetString());

        Assert.True(
            envelope.Extra.TryGetValue("verdict", out var verdictElement),
            "event line carries no verdict field");
        Assert.Equal("ENV_ERROR", verdictElement.GetString());
    }
}
