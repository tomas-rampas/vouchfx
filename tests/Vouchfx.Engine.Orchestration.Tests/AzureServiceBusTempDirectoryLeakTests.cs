// Regression tests for #438: the azureservicebus dependency's Config.json temp directory used
// to be created EAGERLY inside EnvironmentMapper's Build() lambda, which runs for every
// Map()+Configure() caller whether or not a container is ever started — including every
// non-Docker EnvironmentMapperTests case that inspects builder.Resources without Docker.
// Measured: +3 leaked vouchfx-asb-<guid> directories per Vouchfx.Engine.Orchestration.Tests run.
//
// Three parts, none needing Docker:
//   A. Configure(builder) alone (no StartAsync) must never touch the filesystem — the directory
//      + Config.json write is now deferred to Aspire's own OnBeforeResourceStarted hook, which
//      fires only during a genuine DistributedApplication.StartAsync.
//   B. HeadlessTopology.DisposeAsync — the single teardown chokepoint (§4.5) — must remove
//      every directory it is told about via tempDirectoriesToClean, after StopAsync returns.
//      Exercised against a Build()-built but never-StartAsync()-ed DistributedApplication via
//      the ForTestingDisposal seam, so no DCP/Docker is required.
//   C. A StartAsync that throws never hands the list to a topology, so SuiteTopology removes
//      what the start hook created itself: the shared helper directly, and a census of the
//      catch that calls it (the hook only runs against a real container).
//
// Host temp-directory names are never printed (existence-only assertions), per this repo's
// testing conventions.
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.TestSupport;
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

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Config.json"), "{}");

            var app = CreateBuilder().Build();
            var topology = HeadlessTopology.ForTestingDisposal(app, new[] { tempDir });

            await topology.DisposeAsync();

            // Existence-only, with a fixed message: never print the path value itself.
            Assert.False(
                Directory.Exists(tempDir),
                "DisposeAsync left a tracked engine-owned temp directory in place.");
        }
        finally
        {
            // A failure above must not leave behind the very leak this file exists to prevent.
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    /// <summary>
    /// <see cref="HeadlessTopology.DisposeAsync"/> deletes only engine-owned temp directories
    /// (#438 review): a list entry that is not a direct child of the system temp directory named
    /// with the engine prefix survives disposal, whatever the list holds. Two such entries, both of
    /// which a careless future caller could plausibly pass: a temp-root child without the prefix,
    /// and a prefixed directory one level down.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_LeavesAnyDirectoryThatIsNotEngineOwnedInPlace()
    {
        var unprefixed = Path.Combine(Path.GetTempPath(), $"not-engine-owned-{Guid.NewGuid():N}");
        var nestedParent = Path.Combine(Path.GetTempPath(), $"not-engine-owned-{Guid.NewGuid():N}");
        var nested = Path.Combine(nestedParent, $"vouchfx-asb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(unprefixed);
        Directory.CreateDirectory(nested);

        try
        {
            var app = CreateBuilder().Build();
            var topology = HeadlessTopology.ForTestingDisposal(app, new[] { unprefixed, nested });

            await topology.DisposeAsync();

            // Existence-only, with fixed messages: never print the path value itself.
            Assert.True(
                Directory.Exists(unprefixed),
                "DisposeAsync deleted a temp-root directory that does not carry the engine prefix.");
            Assert.True(
                Directory.Exists(nested),
                "DisposeAsync deleted an engine-prefixed directory that is not a direct child of the temp root.");
        }
        finally
        {
            if (Directory.Exists(unprefixed))
            {
                Directory.Delete(unprefixed, recursive: true);
            }

            if (Directory.Exists(nestedParent))
            {
                Directory.Delete(nestedParent, recursive: true);
            }
        }
    }

    /// <summary>The ownership rule itself, one row per shape (#438 review).</summary>
    [Fact]
    public void IsEngineOwnedTempDirectory_AcceptsOnlyAPrefixedDirectChildOfTheTempRoot()
    {
        var tempRoot = Path.GetTempPath();

        Assert.True(
            HeadlessTopology.IsEngineOwnedTempDirectory(Path.Combine(tempRoot, "vouchfx-asb-0123")),
            "A prefixed direct child of the temp root must count as engine-owned.");
        Assert.True(
            HeadlessTopology.IsEngineOwnedTempDirectory(
                Path.Combine(tempRoot, "vouchfx-asb-0123") + Path.DirectorySeparatorChar),
            "A trailing separator must not change the answer.");
        Assert.False(
            HeadlessTopology.IsEngineOwnedTempDirectory(Path.Combine(tempRoot, "other-0123")),
            "An unprefixed temp-root child must not count as engine-owned.");
        Assert.False(
            HeadlessTopology.IsEngineOwnedTempDirectory(Path.Combine(tempRoot, "other", "vouchfx-asb-0123")),
            "A prefixed directory below a non-engine directory must not count as engine-owned.");
        Assert.False(
            HeadlessTopology.IsEngineOwnedTempDirectory(Path.Combine(tempRoot, "vouchfx-asb-0123", "..", "..")),
            "A path that normalises outside the temp root must not count as engine-owned.");
        Assert.False(
            HeadlessTopology.IsEngineOwnedTempDirectory(tempRoot),
            "The temp root itself must never count as engine-owned.");
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

    // ── C. A StartAsync that throws still removes what a start hook created ──

    /// <summary>
    /// The helper both cleanup paths share (#438) deletes an engine-owned directory and leaves any
    /// other entry in place, exactly as <see cref="HeadlessTopology.DisposeAsync"/> always did.
    /// </summary>
    [Fact]
    public void DeleteEngineOwnedTempDirectories_RemovesEngineOwnedEntriesAndNothingElse()
    {
        var owned = Path.Combine(Path.GetTempPath(), $"vouchfx-asb-{Guid.NewGuid():N}");
        var notOwned = Path.Combine(Path.GetTempPath(), $"not-engine-owned-{Guid.NewGuid():N}");
        Directory.CreateDirectory(owned);
        Directory.CreateDirectory(notOwned);

        try
        {
            File.WriteAllText(Path.Combine(owned, "Config.json"), "{}");

            HeadlessTopology.DeleteEngineOwnedTempDirectories(new List<string> { owned, notOwned });

            // Existence-only, with fixed messages: never print the path value itself.
            Assert.False(
                Directory.Exists(owned),
                "The helper left an engine-owned temp directory in place.");
            Assert.True(
                Directory.Exists(notOwned),
                "The helper deleted a directory that is not engine-owned.");
        }
        finally
        {
            foreach (var dir in new[] { owned, notOwned })
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
    }

    /// <summary>
    /// When <c>HeadlessTopology.StartAsync</c> throws, <c>SuiteTopology</c> removes what the
    /// azureservicebus start hook may already have created (#438). <c>StartAsync</c> disposes its
    /// own half-built topology on the way out, and that topology never received the list, so this
    /// catch is the only place left to do it.
    /// </summary>
    /// <remarks>
    /// A census rather than a behavioural test, because the hook runs only once DCP starts a real
    /// emulator container, and a start that then fails needs a genuine Docker host. The check is
    /// structural: the innermost <c>try</c> around the <c>StartAsync</c> call has exactly one
    /// <c>catch</c>, which passes <c>mapped.AsbTempDirectoriesCreated</c> to
    /// <see cref="HeadlessTopology.DeleteEngineOwnedTempDirectories"/> and then rethrows, so the
    /// classification below it is unchanged.
    /// </remarks>
    [Fact]
    public void SuiteTopology_RemovesStartHookDirectories_WhenStartAsyncThrows()
    {
        var path = Path.Combine(
            RepoRoot.Resolve(), "src", "Engine", "Vouchfx.Engine.Orchestration", "SuiteTopology.cs");
        Assert.True(File.Exists(path), "SuiteTopology.cs is not where this census expects it.");

        var root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree
            .ParseText(File.ReadAllText(path))
            .GetRoot();

        var startCalls = root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString() == "HeadlessTopology.StartAsync")
            .ToList();
        Assert.True(
            startCalls.Count == 1,
            "Expected exactly one HeadlessTopology.StartAsync call in SuiteTopology.cs.");

        var innermostTry = startCalls[0].Ancestors()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TryStatementSyntax>()
            .FirstOrDefault(t => t.Block.Span.Contains(startCalls[0].Span));
        Assert.True(
            innermostTry is not null,
            "The HeadlessTopology.StartAsync call is not inside a try block.");
        Assert.True(
            innermostTry!.Catches.Count == 1,
            "The try around HeadlessTopology.StartAsync should have exactly one catch.");

        var catchClause = innermostTry.Catches[0];
        var cleanup = catchClause.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString() == "HeadlessTopology.DeleteEngineOwnedTempDirectories")
            .ToList();
        Assert.True(
            cleanup.Count == 1
                && cleanup[0].ArgumentList.Arguments.Count == 1
                && cleanup[0].ArgumentList.Arguments[0].ToString() == "mapped.AsbTempDirectoriesCreated",
            "The catch around HeadlessTopology.StartAsync must pass mapped.AsbTempDirectoriesCreated "
            + "to HeadlessTopology.DeleteEngineOwnedTempDirectories.");
        Assert.True(
            catchClause.Block.Statements.LastOrDefault()
                is Microsoft.CodeAnalysis.CSharp.Syntax.ThrowStatementSyntax { Expression: null },
            "The catch around HeadlessTopology.StartAsync must end in a bare `throw;`, so the "
            + "classification below it still sees the original exception.");
    }
}
