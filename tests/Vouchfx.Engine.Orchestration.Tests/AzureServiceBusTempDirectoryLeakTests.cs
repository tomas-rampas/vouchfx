// Regression tests for #438: the azureservicebus dependency's Config.json temp directory used
// to be created EAGERLY inside EnvironmentMapper's Build() lambda, which runs for every
// Map()+Configure() caller whether or not a container is ever started — including every
// non-Docker EnvironmentMapperTests case that inspects builder.Resources without Docker.
// Measured: +3 leaked vouchfx-asb-<guid> directories per Vouchfx.Engine.Orchestration.Tests run.
//
// A follow-up fix replaced the plain List<string> + `lock (list)` this file originally pinned
// with TempDirectoryLedger (see that type's own header comment for the full account). The lock
// made a teardown SNAPSHOT atomic, but could not stop a start hook from creating a directory
// AFTER the snapshot had already been taken and deleted from: per Aspire 13.4.2's source, a
// cancelled StartAsync can return while DCP is still creating containers in the background, and
// that background work can still publish BeforeResourceStartedEvent and run the hook.
// TempDirectoryLedger closes that gap by making "take the snapshot" and "refuse anything staged
// later" one atomic operation, under one lock.
//
// Four parts, none needing Docker:
//   A. Configure(builder) alone (no StartAsync) must never touch the filesystem — the directory
//      + Config.json write is now deferred to Aspire's own OnBeforeResourceStarted hook, which
//      fires only during a genuine DistributedApplication.StartAsync.
//   B. HeadlessTopology.DisposeAsync — the single teardown chokepoint (§4.5) — must close the
//      ledger it is told about via tempDirectoriesToClean and remove every directory the closing
//      snapshot names, after StopAsync returns. Exercised against a Build()-built but
//      never-StartAsync()-ed DistributedApplication via the ForTestingDisposal seam, so no
//      DCP/Docker is required.
//   C. A StartAsync that throws never hands the ledger to a topology, so SuiteTopology closes
//      it itself: the shared helper directly, and a census of the catch that calls it (the hook
//      only runs against a real container).
//   D. TempDirectoryLedger's own close/stage race, pinned directly: staging after close refuses
//      and creates nothing; DeleteEngineOwnedTempDirectories itself closes the ledger, so a later
//      Stage sees the same refusal; a populate callback that throws still leaves its directory
//      recorded for cleanup; ~32 Stage calls racing one close leave no directory behind under any
//      interleaving (deterministic, not merely likely); and — the one test here that drives the
//      REAL Aspire hook rather than the ledger alone — publishing BeforeResourceStartedEvent
//      directly against the builder's own eventing fires EnvironmentMapper's actual hook, without
//      Docker or DCP.
//
// Host temp-directory names are never printed (existence-only assertions), per this repo's
// testing conventions.
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Non-Docker regression tests pinning #438: an azureservicebus dependency's temp Config.json
/// directory must be created only when a real container is about to start, and removed by
/// <see cref="HeadlessTopology"/>'s single teardown chokepoint once it stops — via the
/// <see cref="TempDirectoryLedger"/> that makes "no hook creates one after teardown" a guarantee.
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

    private static EnvironmentSpec CreateAzureServiceBusEnvironment() => new(
        Services: null,
        Dependencies: new Dictionary<string, DependencySpec>
        {
            ["bus"] = new DependencySpec(Type: "azureservicebus", Version: null, Extra: null),
        },
        Seed: null,
        ImageRegistry: null,
        ImagePullPolicy: null);

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
        var mapped = EnvironmentMapper.Map(CreateAzureServiceBusEnvironment());
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
        Assert.Empty(mapped.AsbTempDirectoriesCreated.Snapshot());
    }

    // ── B. HeadlessTopology.DisposeAsync removes tracked directories ─────────

    /// <summary>
    /// <see cref="HeadlessTopology.DisposeAsync"/> must close the ledger it is handed and remove
    /// every directory the closing snapshot names, after <c>StopAsync</c> returns. Exercised via
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
        var ledger = new TempDirectoryLedger();

        try
        {
            // Stage() creates the directory itself; a separate Directory.CreateDirectory before
            // it would be a redundant no-op (documented BCL behaviour).
            ledger.Stage(tempDir, dir => File.WriteAllText(Path.Combine(dir, "Config.json"), "{}"));

            var app = CreateBuilder().Build();
            var topology = HeadlessTopology.ForTestingDisposal(app, ledger);

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
    /// (#438 review): a ledger entry that is not a direct child of the system temp directory named
    /// with the engine prefix survives disposal, whatever the ledger holds. Two such entries, both
    /// of which a careless future caller could plausibly stage: a temp-root child without the
    /// prefix, and a prefixed directory one level down.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_LeavesAnyDirectoryThatIsNotEngineOwnedInPlace()
    {
        var unprefixed = Path.Combine(Path.GetTempPath(), $"not-engine-owned-{Guid.NewGuid():N}");
        var nestedParent = Path.Combine(Path.GetTempPath(), $"not-engine-owned-{Guid.NewGuid():N}");
        var nested = Path.Combine(nestedParent, $"vouchfx-asb-{Guid.NewGuid():N}");
        var ledger = new TempDirectoryLedger();

        try
        {
            ledger.Stage(unprefixed, _ => { });
            ledger.Stage(nested, _ => { });

            var app = CreateBuilder().Build();
            var topology = HeadlessTopology.ForTestingDisposal(app, ledger);

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
    /// is treated as an empty, open ledger, not as a null-reference fault in the cleanup loop.
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
        var ledger = new TempDirectoryLedger();

        try
        {
            ledger.Stage(owned, dir => File.WriteAllText(Path.Combine(dir, "Config.json"), "{}"));
            ledger.Stage(notOwned, _ => { });

            HeadlessTopology.DeleteEngineOwnedTempDirectories(ledger);

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
    /// own half-built topology on the way out, and that topology never received the ledger, so
    /// this catch is the only place left to close it.
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

    // ── D. TempDirectoryLedger's own close/stage race ─────────────────────────

    /// <summary>(a) A <see cref="TempDirectoryLedger.Stage"/> call made after <see cref="TempDirectoryLedger.Close"/> refuses and creates nothing.</summary>
    [Fact]
    public void Stage_AfterClose_ThrowsAndCreatesNothing()
    {
        var ledger = new TempDirectoryLedger();
        ledger.Close();

        var path = Path.Combine(Path.GetTempPath(), $"vouchfx-asb-{Guid.NewGuid():N}");

        try
        {
            var exception = Record.Exception(
                () => ledger.Stage(path, dir => File.WriteAllText(Path.Combine(dir, "Config.json"), "{}")));

            Assert.IsType<InvalidOperationException>(exception);
            Assert.False(
                Directory.Exists(path),
                "Stage created a directory although the ledger was already closed.");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    /// <summary>
    /// (b) <see cref="HeadlessTopology.DeleteEngineOwnedTempDirectories"/> closes the ledger it is
    /// handed (it calls <see cref="TempDirectoryLedger.Close"/> to take its snapshot), so a
    /// <see cref="TempDirectoryLedger.Stage"/> call made afterwards refuses and creates nothing —
    /// exactly the shape a start hook that fires after <see cref="HeadlessTopology.DisposeAsync"/>
    /// (or the <c>SuiteTopology</c> failure path) hits.
    /// </summary>
    [Fact]
    public void DeleteEngineOwnedTempDirectories_ClosesTheLedger_SoALaterStageThrowsAndCreatesNothing()
    {
        var ledger = new TempDirectoryLedger();
        HeadlessTopology.DeleteEngineOwnedTempDirectories(ledger);

        var path = Path.Combine(Path.GetTempPath(), $"vouchfx-asb-{Guid.NewGuid():N}");

        try
        {
            var exception = Record.Exception(
                () => ledger.Stage(path, dir => File.WriteAllText(Path.Combine(dir, "Config.json"), "{}")));

            Assert.IsType<InvalidOperationException>(exception);
            Assert.False(
                Directory.Exists(path),
                "Stage created a directory after DeleteEngineOwnedTempDirectories had closed the ledger.");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    /// <summary>
    /// (c) A <c>populate</c> callback that throws still leaves its directory recorded — cleanup
    /// removes it exactly as it would a directory whose populate succeeded.
    /// </summary>
    [Fact]
    public void Stage_WhenPopulateThrows_StillRecordsTheDirectory_AndCleanupRemovesIt()
    {
        var ledger = new TempDirectoryLedger();
        var path = Path.Combine(Path.GetTempPath(), $"vouchfx-asb-{Guid.NewGuid():N}");

        try
        {
            var stageException = Record.Exception(
                () => ledger.Stage(path, _ => throw new InvalidDataException("populate failed")));

            Assert.IsType<InvalidDataException>(stageException);
            Assert.True(
                Directory.Exists(path),
                "A directory must still be recorded for cleanup even when its populate callback throws.");

            HeadlessTopology.DeleteEngineOwnedTempDirectories(ledger);

            Assert.False(
                Directory.Exists(path),
                "Cleanup did not remove a directory whose populate callback had thrown.");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    /// <summary>
    /// (d) ~32 parallel <see cref="TempDirectoryLedger.Stage"/> calls for distinct engine-owned
    /// paths race one <see cref="HeadlessTopology.DeleteEngineOwnedTempDirectories"/> call.
    /// Whichever <c>Stage</c> calls lose the race throw <see cref="InvalidOperationException"/> and
    /// create nothing; whichever win are included in the close snapshot and are then deleted. So
    /// after every task finishes, NONE of the paths exists — deterministically, for any
    /// interleaving, because <c>Stage</c> and <c>Close</c> share one lock (see
    /// <see cref="TempDirectoryLedger"/>'s own remarks).
    /// </summary>
    [Fact]
    public async Task Stage_RacingOneClose_NeverLeavesADirectoryBehind_ForAnyInterleaving()
    {
        var ledger = new TempDirectoryLedger();
        var paths = Enumerable.Range(0, 32)
            .Select(_ => Path.Combine(Path.GetTempPath(), $"vouchfx-asb-{Guid.NewGuid():N}"))
            .ToArray();

        try
        {
            var stagingTasks = paths.Select(path => Task.Run(() =>
            {
                try
                {
                    ledger.Stage(path, dir => File.WriteAllText(Path.Combine(dir, "Config.json"), "{}"));
                }
                catch (InvalidOperationException)
                {
                    // Expected for whichever calls lose the race against Close(): the directory
                    // this call would have created must not exist either, asserted below for
                    // every path regardless of which outcome it hit.
                }
            }));

            var cleanupTask = Task.Run(() => HeadlessTopology.DeleteEngineOwnedTempDirectories(ledger));

            await Task.WhenAll(stagingTasks.Append(cleanupTask));

            foreach (var path in paths)
            {
                Assert.False(
                    Directory.Exists(path),
                    "A directory staged concurrently with cleanup survived the race.");
            }
        }
        finally
        {
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
        }
    }

    /// <summary>
    /// (e) The REAL hook, exercised without Docker or DCP: publishing
    /// <see cref="BeforeResourceStartedEvent"/> directly against the builder's own eventing
    /// infrastructure fires the exact same subscription <c>OnBeforeResourceStarted</c> registers —
    /// Aspire's own extension resolves it by resource identity, not by which code publishes it —
    /// so this drives EnvironmentMapper's actual <see cref="TempDirectoryLedger.Stage"/> call
    /// rather than a hand-rolled substitute for it.
    /// </summary>
    [Fact]
    public async Task OnBeforeResourceStartedHook_StagesTheRealDirectory_ThenRefusesAfterCleanup()
    {
        var mapped = EnvironmentMapper.Map(CreateAzureServiceBusEnvironment());
        var builder = CreateBuilder();
        mapped.Configure(builder);

        var resource = builder.Resources.Single(r => r.Name == "bus");
        var mount = resource.Annotations.OfType<ContainerMountAnnotation>().Single();
        var mountDirectory = Path.GetDirectoryName(mount.Source);
        Assert.NotNull(mountDirectory);

        // The hook EnvironmentMapper registers discards this parameter entirely ((_, _, _) => ...),
        // so any IServiceProvider satisfies BeforeResourceStartedEvent's constructor here.
        var services = new ServiceCollection().BuildServiceProvider();

        try
        {
            await builder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(resource, services),
                CancellationToken.None);

            Assert.True(
                Directory.Exists(mountDirectory),
                "Publishing BeforeResourceStartedEvent did not create the bind-mount directory.");
            Assert.True(
                File.Exists(mount.Source),
                "Publishing BeforeResourceStartedEvent did not write the bind-mounted Config.json.");
            // Existence-only with a fixed message: Assert.Contains would print the host path.
            Assert.True(
                mapped.AsbTempDirectoriesCreated.Snapshot().Contains(mountDirectory!),
                "The directory the real hook created is not in the ledger's snapshot.");

            HeadlessTopology.DeleteEngineOwnedTempDirectories(mapped.AsbTempDirectoriesCreated);

            Assert.False(
                Directory.Exists(mountDirectory),
                "DeleteEngineOwnedTempDirectories did not remove the directory the real hook created.");

            var exception = await Record.ExceptionAsync(() => builder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(resource, services),
                CancellationToken.None));

            Assert.IsType<InvalidOperationException>(exception);
            Assert.False(
                Directory.Exists(mountDirectory),
                "A start hook that fired after cleanup must not recreate the directory.");
        }
        finally
        {
            if (mountDirectory is not null && Directory.Exists(mountDirectory))
            {
                Directory.Delete(mountDirectory, recursive: true);
            }
        }
    }
}
