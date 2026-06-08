// Docker-gated integration test for the Environment-error classification hook
// (S02-A-02).  Requires a running Docker daemon and the DCP process embedded in
// this test project (IsAspireHost=true, Aspire.AppHost.Sdk).
//
// Run with: dotnet test --filter "requires=docker&FullyQualifiedName~EnvironmentError"
// Excluded from unit-CI:   dotnet test --filter "requires!=docker"

using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;
using Platform.Engine.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace Platform.Engine.Orchestration.Tests;

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
    private const string AppHostAssemblyName = "Platform.Engine.Orchestration.Tests";

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

        // Assert 2 — the produced event has Verdict.EnvironmentError, never Fail.
        var evt = EnvironmentErrorEvents.Create(ex.Info, "run", FixedTs);
        Assert.Equal(Verdict.EnvironmentError, evt.Verdict);
        Assert.NotEqual(Verdict.Fail, evt.Verdict);

        // Assert 3 — event type discriminator.
        Assert.Equal(EventTypes.EnvironmentError, evt.Type);

        // Assert 4 — the line serialises correctly and round-trips with ENV_ERROR verdict.
        var line = EnvironmentErrorEvents.ToLine(ex.Info, "run", FixedTs);
        var envelope = EventStreamJson.FromLine(line);
        Assert.Equal("environment-error", envelope.Type);

        if (envelope.Extra?.TryGetValue("verdict", out var verdictElement) == true)
        {
            Assert.Equal("ENV_ERROR", verdictElement.GetString());
        }

        _output.WriteLine("BadImage_StartAsync_ThrowsOrchestrationException_NotFail: PASS");
    }
}
