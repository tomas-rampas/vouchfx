// Regression tests for #438: the azureservicebus dependency's Config.json temp directory used
// to be created EAGERLY inside EnvironmentMapper's Build() lambda, which runs for every
// Map()+Configure() caller whether or not a container is ever started — including every
// non-Docker EnvironmentMapperTests case that inspects builder.Resources without Docker.
// Measured: +3 leaked vouchfx-asb-<guid> directories per Vouchfx.Engine.Orchestration.Tests run.
//
// Two halves, two tests, neither needing Docker:
//   A. Configure(builder) alone (no StartAsync) must never touch the filesystem — the directory
//      + Config.json write is now deferred to Aspire's own OnBeforeResourceStarted hook, which
//      fires only during a genuine DistributedApplication.StartAsync.
//   B. HeadlessTopology.DisposeAsync — the single teardown chokepoint (§4.5) — must remove
//      every directory it is told about via tempDirectoriesToClean, after StopAsync returns.
//      Exercised against a Build()-built but never-StartAsync()-ed DistributedApplication via
//      the ForTestingDisposal seam, so no DCP/Docker is required.
//
// Host temp-directory names are never printed (existence-only assertions), per this repo's
// testing conventions.
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Authoring.Model;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Non-Docker regression tests pinning #438: an azureservicebus dependency's temp Config.json
/// directory must be created only when a real container is about to start, and removed by
/// <see cref="HeadlessTopology"/>'s single teardown chokepoint once it stops.
/// </summary>
public sealed class AzureServiceBusTempDirectoryLeakTests
{
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    private static IDistributedApplicationBuilder CreateBuilder()
    {
        var options = new DistributedApplicationOptions
        {
            DisableDashboard = true,
            Args = Array.Empty<string>(),
            AssemblyName = AppHostAssemblyName,
        };
        return DistributedApplication.CreateBuilder(options);
    }

    // ── A. Configure() alone never touches the filesystem ────────────────────

    /// <summary>
    /// A bare <c>Map()</c> + <c>Configure(builder)</c> call — exactly what every non-Docker
    /// <see cref="EnvironmentMapperTests"/> case already does — must not create the
    /// azureservicebus Config.json directory. The bind-mount SOURCE path is read back from the
    /// resource's own <see cref="ContainerMountAnnotation"/> (not reconstructed or globbed), so
    /// this pins the EXACT path the emulator container would read, not merely "some
    /// vouchfx-asb-* directory somewhere" — safe under this shared machine's concurrent agents.
    /// </summary>
    [Fact]
    public void Configure_AzureServiceBusDependency_WithoutStartingTheApp_NeverTouchesTheFilesystem()
    {
        var env = new EnvironmentSpec(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                ["bus"] = new DependencySpec(Type: "azureservicebus", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        var mapped = EnvironmentMapper.Map(env);
        var builder = CreateBuilder();

        // The call under test — no StartAsync anywhere in this test.
        mapped.Configure(builder);

        var mount = builder.Resources
            .Single(r => r.Name == "bus")
            .Annotations.OfType<ContainerMountAnnotation>()
            .Single();

        // Existence-only: never assert on, or print, the path value itself.
        Assert.False(File.Exists(mount.Source));
        var mountDirectory = Path.GetDirectoryName(mount.Source);
        Assert.NotNull(mountDirectory);
        Assert.False(Directory.Exists(mountDirectory));

        // Nothing was created, so there is nothing HeadlessTopology.DisposeAsync would need to
        // clean up on this path either.
        Assert.Empty(mapped.AsbTempDirectoriesCreated);
    }

    // ── B. HeadlessTopology.DisposeAsync removes tracked directories ─────────

    /// <summary>
    /// <see cref="HeadlessTopology.DisposeAsync"/> must remove every directory named in
    /// <c>tempDirectoriesToClean</c>, after <c>StopAsync</c> returns. Exercised via
    /// <see cref="HeadlessTopology.ForTestingDisposal"/> against a <c>Build()</c>-built but
    /// never-started <see cref="DistributedApplication"/>, so this needs no DCP process and no
    /// Docker: an un-started host's own <c>StopAsync</c>/<c>DisposeAsync</c> are reached exactly
    /// as a started one's would be (both calls are unconditionally guarded already), so this
    /// isolates the cleanup loop itself rather than DCP orchestration.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_RemovesEveryTrackedTempDirectory_ForAnUnstartedApp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"vouchfx-asb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "Config.json"), "{}");

        var app = CreateBuilder().Build();
        var topology = HeadlessTopology.ForTestingDisposal(app, new[] { tempDir });

        await topology.DisposeAsync();

        // Existence-only: never print the path value itself.
        Assert.False(Directory.Exists(tempDir));
    }

    /// <summary>
    /// A caller that passes no <c>tempDirectoriesToClean</c> at all (every non-azureservicebus
    /// topology) must still dispose cleanly — the parameter is optional and <see langword="null"/>
    /// is treated as empty, not as a null-reference fault in the cleanup loop.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_WithNoTempDirectoriesToClean_DisposesCleanly()
    {
        var app = CreateBuilder().Build();
        var topology = HeadlessTopology.ForTestingDisposal(app, tempDirectoriesToClean: null);

        var exception = await Record.ExceptionAsync(() => topology.DisposeAsync().AsTask());

        Assert.Null(exception);
    }
}
