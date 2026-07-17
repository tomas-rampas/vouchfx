using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated integration tests for <see cref="HeadlessTopology"/>.
/// Run with: <c>dotnet test --filter "requires=docker"</c>.
/// Excluded from the unit-CI job via: <c>dotnet test --filter "requires!=docker"</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>R-1 finding:</b> <c>Aspire.Hosting</c> alone does not supply the DCP binary.
/// This test project carries <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c> and
/// <c>Aspire.AppHost.Sdk</c>, which causes the MSBuild targets from
/// <c>Aspire.Hosting.AppHost</c> to embed <c>dcpclipath</c> / <c>dcpextensionpaths</c>
/// assembly metadata attributes pointing at the <c>Aspire.Hosting.Orchestration.win-x64</c>
/// DCP binaries.  The tests pass that assembly name to <see cref="HeadlessTopology.StartAsync"/>
/// so <see cref="Aspire.Hosting.DistributedApplicationOptions"/> resolves the DCP binary
/// from the correct assembly rather than from the xUnit runner entry assembly.
/// </para>
/// </remarks>
public sealed class HeadlessTopologyTests
{
    // The short name of *this* test assembly — the one whose AssemblyInfo carries dcpclipath.
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>
    /// A1 — builds a headless <see cref="DistributedApplication"/> (DisableDashboard=true),
    /// asserts it starts without throwing, then disposes cleanly.
    /// A trivial container (traefik/whoami) is added to prove the host can manage a real container.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task StartAsync_WithTrivialContainer_StartsAndDisposeCleanly()
    {
        // Arrange — add a lightweight container to prove the DCP-managed lifecycle works.
        await using var topology = await HeadlessTopology.StartAsync(
            appHostAssemblyName: AppHostAssemblyName,
            configureResources: b => b.AddContainer("whoami", "traefik/whoami"));

        // Assert — if we reach this line the host started without throwing.
        Assert.NotNull(topology.Application);
    }

    /// <summary>
    /// A1 (empty topology) — asserts the host can start with no resources at all,
    /// confirming the minimal headless path works independently of Docker image pulls.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task StartAsync_WithNoResources_StartsAndDisposesCleanly()
    {
        // Act + Assert — must not throw.
        await using var topology = await HeadlessTopology.StartAsync(
            appHostAssemblyName: AppHostAssemblyName);

        Assert.NotNull(topology.Application);
    }

    /// <summary>
    /// A2 — asserts that no <c>Microsoft.Extensions.Diagnostics.HealthChecks</c> log entry
    /// below <see cref="LogLevel.Warning"/> is emitted during topology startup.
    /// Also indirectly asserts that the dashboard is suppressed (no dashboard-startup log noise).
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task StartAsync_HealthChecksLogsFiltered_NoBelowWarningEntries()
    {
        // Arrange
        var collector = new CapturingLoggerProvider();

        await using var topology = await HeadlessTopology.StartAsync(
            appHostAssemblyName: AppHostAssemblyName,
            configureResources: b =>
            {
                // Register the capturing provider so we can inspect what was logged.
                b.Services.AddLogging(lb => lb.AddProvider(collector));
            });

        // Assert — no HealthChecks entry below Warning must have been captured.
        var violations = collector.Entries
            .Where(e =>
                e.Category.StartsWith(
                    "Microsoft.Extensions.Diagnostics.HealthChecks",
                    StringComparison.OrdinalIgnoreCase)
                && e.Level < LogLevel.Warning)
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// A2 sibling — asserts that Aspire's lifecycle banners (the
    /// <c>Aspire.Hosting.DistributedApplication</c> category: version banner,
    /// "Distributed application starting/started", and "Application host directory is:
    /// &lt;build-machine path&gt;") are filtered below <see cref="LogLevel.Warning"/>.
    /// The second assertion pins the customer-visible symptom directly — no sub-Warning
    /// entry of ANY category may carry the "Application host directory" message — so the
    /// gate survives the message moving category on a future Aspire bump.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task StartAsync_AspireLifecycleLogsFiltered_NoBelowWarningEntries()
    {
        // Arrange
        var collector = new CapturingLoggerProvider();

        await using var topology = await HeadlessTopology.StartAsync(
            appHostAssemblyName: AppHostAssemblyName,
            configureResources: b =>
            {
                b.Services.AddLogging(lb => lb.AddProvider(collector));
            });

        // Guard against a vacuous pass: the collector must have received log traffic, and
        // the deliberately-unfiltered Aspire.Hosting.Dcp.DcpHost category must still be
        // visible — proving the capture pipeline is live AND pinning the decision that DCP
        // diagnostics (this machine's resolved DCP path) survive the lifecycle filter.
        Assert.NotEmpty(collector.Entries);
        Assert.Contains(collector.Entries, e =>
            e.Category.StartsWith("Aspire.Hosting.Dcp", StringComparison.OrdinalIgnoreCase));

        // Assert — no lifecycle-banner entry below Warning in the filtered category.
        var categoryViolations = collector.Entries
            .Where(e =>
                e.Category.StartsWith(
                    "Aspire.Hosting.DistributedApplication",
                    StringComparison.OrdinalIgnoreCase)
                && e.Level < LogLevel.Warning)
            .ToList();

        Assert.Empty(categoryViolations);

        // Assert — the baked build-machine path line is gone regardless of category.
        var symptomViolations = collector.Entries
            .Where(e =>
                e.Level < LogLevel.Warning
                && e.Message.Contains(
                    "Application host directory",
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(symptomViolations);
    }
}
