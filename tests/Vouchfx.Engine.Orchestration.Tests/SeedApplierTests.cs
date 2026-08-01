// Non-docker tests for S05-A-01: SeedApplier — failure paths that never need a
// live database.  These prove the §12.1 contract (a failed seed is an
// Environment error, never a Fail) and the path-resolution behaviour, all
// without standing up a topology.
//
// The happy-path (a real Postgres dependency + valid seed SQL applied before
// step 1) is covered by SeedApplierDockerTests with [Trait("requires","docker")].

using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Non-docker tests for <see cref="SeedApplier.ApplyAsync"/> (S05-A-01).
/// </summary>
/// <remarks>
/// Every test here exercises a failure path that is detected <em>before</em> a
/// connection is opened, so no Docker daemon is required.  They assert that the
/// failure is an <see cref="OrchestrationException"/> with kind
/// <see cref="OrchestrationErrorKind.Provision"/>, and that the verdict-mapping
/// contract holds (OrchestrationException ⇒ <see cref="Verdict.EnvironmentError"/>).
/// </remarks>
public sealed class SeedApplierTests
{
    // A dummy connection string: a postgres-shaped value that is never actually
    // dialled, because the file-existence / unknown-dependency checks precede any
    // connection open.
    private const string DummyConnString =
        "Host=localhost;Port=1;Database=db;Username=u;Password=p";

    private const string DepName = "orders-db";

    private static Dictionary<string, object> Discovered(
        string name = DepName, string conn = DummyConnString) =>
        new(StringComparer.Ordinal) { [name] = conn };

    // Default dependency-type map: the seeded dependency is postgres so the SQL
    // dispatch path is exercised (A-02 type-dispatch).
    private static Dictionary<string, string> Types(
        string name = DepName, string type = "postgres") =>
        new(StringComparer.Ordinal) { [name] = type };

    private static SeedSpec SeedWith(string depName, params string[] sqlFiles) =>
        new(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
        {
            [depName] = new DependencySeed(sqlFiles),
        });

    // Thin wrapper over the ApplyAsync signature so the tests below stay focused
    // on behaviour, not the type-map parameter.
    private static Task Apply(
        SeedSpec? seed,
        IReadOnlyDictionary<string, object> discovered,
        IReadOnlyDictionary<string, string> types,
        string baseDir) =>
        SeedApplier.ApplyAsync(
            seed,
            discovered,
            types,
            baseDir,
            ct: CancellationToken.None);

    // ── Null / empty seed is a no-op ──────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_NullSeed_IsNoOp()
    {
        // No exception even with empty discovered services.
        await Apply(
            seed: null,
            discovered: new Dictionary<string, object>(),
            types: new Dictionary<string, string>(),
            baseDir: Path.GetTempPath());
    }

    // ── Missing SQL file → Provision error, no DB needed ─────────────────────

    [Fact]
    public async Task ApplyAsync_MissingSqlFile_ThrowsProvisionNamingPath_WithoutDatabase()
    {
        // Arrange — a base directory that exists but a SQL file that does not.
        var baseDir = Path.GetTempPath();
        var missing = "does-not-exist-" + Guid.NewGuid().ToString("n") + ".sql";
        var seed = SeedWith(DepName, missing);

        // Act + Assert — throws BEFORE opening any connection (DummyConnString is
        // never dialled), so this completes without a Docker daemon.
        var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
            Apply(seed, Discovered(), Types(), baseDir));

        Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
        // Detail names the missing resolved path.
        Assert.Contains(missing, ex.Info.Detail, StringComparison.Ordinal);
        Assert.Equal(DepName, ex.Info.ResourceName);

        // Verdict-mapping contract: OrchestrationException ⇒ EnvironmentError, never Fail.
        var evt = EnvironmentErrorEvents.Create(ex.Info, "run", DateTimeOffset.UnixEpoch);
        Assert.Equal(Verdict.EnvironmentError, evt.Verdict);
        Assert.NotEqual(Verdict.Fail, evt.Verdict);
    }

    // ── Unknown dependency → Provision error ─────────────────────────────────

    [Fact]
    public async Task ApplyAsync_UnknownDependency_ThrowsProvision()
    {
        // Arrange — the seed names a postgres dependency that is declared (so type
        // dispatch reaches the SQL path) but absent from discovered services (so the
        // connection-string lookup fails).
        var seed = SeedWith("absent-db", "a.sql");

        // Act + Assert
        var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
            Apply(seed, Discovered(), Types("absent-db"), Path.GetTempPath()));

        Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
        Assert.Contains("unknown dependency", ex.Info.Detail, StringComparison.Ordinal);
        Assert.Contains("absent-db", ex.Info.Detail, StringComparison.Ordinal);

        var evt = EnvironmentErrorEvents.Create(ex.Info, "run", DateTimeOffset.UnixEpoch);
        Assert.Equal(Verdict.EnvironmentError, evt.Verdict);
    }

    // ── Path resolution against the seed base directory ──────────────────────

    [Fact]
    public async Task ApplyAsync_RelativePath_ResolvesAgainstSeedBaseDirectory()
    {
        // Arrange — a temp base directory containing a SUBDIRECTORY with a SQL file.
        // We point the seed at a relative path under that base directory and prove
        // it resolves there by observing that a file PRESENT in the base dir does
        // NOT trip the missing-file guard (the connection open then fails — proving
        // resolution succeeded past the file check).
        var baseDir = Path.Combine(Path.GetTempPath(), "vouchfx-seed-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var relative = Path.Combine("fixtures", "present.sql");
            var full = Path.Combine(baseDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, "SELECT 1;");

            var seed = SeedWith(DepName, relative);

            // Act — the file EXISTS at the resolved path, so the missing-file guard
            // is NOT hit.  Execution then proceeds to open the dummy connection,
            // which fails (Port=1, nothing listening) → Provision error whose detail
            // mentions connecting, NOT a missing file.  This proves the relative path
            // resolved against seedBaseDirectory.
            var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
                Apply(seed, Discovered(), Types(), baseDir));

            Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
            // Not a "file not found" failure — the file resolved and existed.
            Assert.DoesNotContain("not found", ex.Info.Detail, StringComparison.OrdinalIgnoreCase);
            // It got far enough to attempt a connection.
            Assert.Contains("connection", ex.Info.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_RelativePath_MissingUnderBaseDir_NamesResolvedAbsolutePath()
    {
        // Arrange — a relative path that does NOT exist under the base directory.
        var baseDir = Path.Combine(Path.GetTempPath(), "vouchfx-seed-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(baseDir);
        try
        {
            var relative = Path.Combine("fixtures", "absent.sql");
            var expectedFull = Path.GetFullPath(Path.Combine(baseDir, relative));
            var seed = SeedWith(DepName, relative);

            // Act + Assert — the detail names the RESOLVED absolute path (proving the
            // relative path was combined with seedBaseDirectory).
            var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
                Apply(seed, Discovered(), Types(), baseDir));

            Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
            Assert.Contains(expectedFull, ex.Info.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    // ── A dependency with no SQL files is skipped ────────────────────────────

    [Fact]
    public async Task ApplyAsync_DependencyWithNullSql_IsSkipped()
    {
        // Arrange — a seed entry with no seed data (null sql, the only seed kind) is a
        // no-op for that dep, even when the dependency is unknown AND has no declared
        // type: the empty-seed guard returns before any type lookup.
        var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
        {
            ["absent-db"] = new DependencySeed(Sql: null),
        });

        // Act — no exception: a dependency with no seed data is skipped before the
        // type-dispatch and unknown-dependency checks.
        await Apply(
            seed,
            new Dictionary<string, object>(),
            new Dictionary<string, string>(),
            Path.GetTempPath());
    }
}
