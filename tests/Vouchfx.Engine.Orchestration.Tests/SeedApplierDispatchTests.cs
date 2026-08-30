// Non-docker tests for SeedApplier's type-dispatch (NIT-1): a seed kind that does
// not match its dependency's declared type is rejected as a Provision error
// before any I/O against a live resource.
//
// `sql` is the only seed kind in the v1 language. This file previously also
// covered the `publish` (broker warm-up) and `documents` (document-store
// fixture) wired-but-deferred seams — both read+hashed a referenced fixture and
// recorded the intent through a sink, without ever performing a real broker
// publish or document-store write. Neither was used anywhere in the repo and
// both were removed before general availability (see SeedSpec.cs's header
// remarks); the seam-specific tests that exercised IBrokerWarmupSink /
// IDocumentSeedSink were removed along with the feature. What remains here is
// the NIT-1 dispatch behaviour for the surviving `sql` kind.

using System.Collections.Frozen;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Non-docker tests for <see cref="SeedApplier.ApplyAsync"/> type-dispatch: a
/// seed kind that does not match its dependency's declared type (NIT-1) is
/// rejected as a Provision error, and <c>sql</c> is accepted (dispatch-only,
/// past the file-existence check) on every relational kind.
/// </summary>
public sealed class SeedApplierDispatchTests
{
    private const string DummyConnString =
        "Host=localhost;Port=1;Database=db;Username=u;Password=p";

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-seed-dispatch-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── NIT-1: a seed kind that does not match the dependency type → Provision ─

    [Fact]
    public async Task ApplyAsync_SqlUnderKafkaDependency_ThrowsMismatchProvision()
    {
        var dir = NewTempDir();
        try
        {
            // Arrange — 'sql' declared under a kafka dependency (the NIT-1 case:
            // without dispatch the applier would dial Npgsql with a Kafka conn string).
            await File.WriteAllTextAsync(Path.Combine(dir, "a.sql"), "SELECT 1;");
            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                ["events"] = new DependencySeed(Sql: new List<string> { "a.sql" }),
            });

            // Act + Assert
            var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
                SeedApplier.ApplyAsync(
                    seed,
                    discoveredServices: new Dictionary<string, object> { ["events"] = DummyConnString },
                    dependencyTypes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["events"] = "kafka",
                    },
                    seedBaseDirectory: dir,
                    ct: CancellationToken.None));

            Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
            // Names the dependency, the seed kind, and the declared type.
            Assert.Contains("events", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains("sql", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains("kafka", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Equal("events", ex.Info.ResourceName);

            // Environment error, never Fail.
            var evt = EnvironmentErrorEvents.Create(ex.Info, "run", DateTimeOffset.UnixEpoch);
            Assert.Equal(Verdict.EnvironmentError, evt.Verdict);
            Assert.NotEqual(Verdict.Fail, evt.Verdict);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── sql seeding extended beyond Postgres to every relational db-assert kind ─
    //
    // (SQL Server and MySQL join Postgres — RespawnRelationalIsolation already
    // resets all three via the same Npgsql/SqlClient/MySqlConnector adapters, so
    // the seed applier's `sql` dispatch is generalised to match.)
    //
    // These tests never open a real connection: ApplySqlSeedAsync's file-existence
    // check runs BEFORE any connection is opened (see the design note at the top
    // of SeedApplier.cs), so an intentionally-missing fixture proves "accepted for
    // dispatch" (the SQL-specific code path was reached) without a database, and
    // the resulting failure classification is asserted the same way the existing
    // NIT-1 mismatch tests above do.

    /// <summary>
    /// Syntactically-valid connection strings for each relational kind. The
    /// host/port pair is never dialled by these tests — only present so the
    /// discoveredServices lookup succeeds before the file-existence check runs.
    /// </summary>
    // FrozenDictionary, not Dictionary (CA1859 + immutability). CA1859 rejects the
    // IReadOnlyDictionary this field used to declare — the interface buys only an indirection on
    // a field whose value never changes — but swapping in a bare Dictionary would have traded a
    // compile-time no-mutation guarantee for a mutable map SHARED by every test in this class,
    // where one stray write would contaminate the rest of the run in test-order-dependent ways.
    // FrozenDictionary is concrete (so the analyser is satisfied) AND immutable (so the guarantee
    // is kept), which is why it is preferred here over either alternative.
    private static readonly FrozenDictionary<string, string> RelationalConnStrings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["postgres"] = "Host=localhost;Port=1;Database=db;Username=u;Password=p",
            ["sqlserver"] = "Server=localhost,1;Database=db;User Id=u;Password=p;TrustServerCertificate=True",
            ["mysql"] = "Server=localhost;Port=1;Database=db;Uid=u;Pwd=p",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    public async Task ApplyAsync_SqlOnRelationalKind_IsAccepted_MissingFixtureClassifiedAsEnvironmentError(
        string relationalType)
    {
        var dir = NewTempDir();
        try
        {
            var missing = Path.GetFullPath(Path.Combine(dir, "seed.sql"));
            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                ["orders-db"] = new DependencySeed(Sql: new List<string> { "seed.sql" }),
            });

            var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
                SeedApplier.ApplyAsync(
                    seed,
                    discoveredServices: new Dictionary<string, object>
                    {
                        ["orders-db"] = RelationalConnStrings[relationalType],
                    },
                    dependencyTypes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["orders-db"] = relationalType,
                    },
                    seedBaseDirectory: dir,
                    ct: CancellationToken.None));

            // Accepted, not rejected: the error is the SQL-specific "file not found"
            // detail (reached only once dispatch has matched `relationalType` to a
            // relational kind) — never the NIT-1 "not supported" mismatch message.
            Assert.DoesNotContain("is not supported for its declared type", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains("seed SQL file not found", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains("'seed.sql'", ex.Info.Detail, StringComparison.Ordinal);

            // BLOCKER B2: this detail becomes an OrchestrationException message, reaches the §14
            // environment-error event (built four lines below) and, on the suite path, is stamped
            // onto every scenario's ScenarioCompletedEvent.message. The declared name is the
            // actionable half; the resolved absolute path is a host-layout disclosure into an
            // archived artefact, so the assertion that it is PRESENT was inverted rather than
            // dropped.
            Assert.DoesNotContain(missing, ex.Info.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
            Assert.Equal("orders-db", ex.Info.ResourceName);

            // §12.1: a broken/missing fixture is an Environment error, never a Fail —
            // for every relational kind, not just Postgres.
            var evt = EnvironmentErrorEvents.Create(ex.Info, "run", DateTimeOffset.UnixEpoch);
            Assert.Equal(Verdict.EnvironmentError, evt.Verdict);
            Assert.NotEqual(Verdict.Fail, evt.Verdict);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The accepted set must not accidentally widen to everything: a broker, a
    /// cache and two document stores must all still reject `sql` with the NIT-1
    /// mismatch, exactly as the existing kafka case above does.
    /// </summary>
    [Theory]
    [InlineData("kafka")]
    [InlineData("redis")]
    [InlineData("mongodb")]
    [InlineData("elasticsearch")]
    public async Task ApplyAsync_SqlUnderNonRelationalDependency_ThrowsMismatchProvision(string nonRelationalType)
    {
        var dir = NewTempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.sql"), "SELECT 1;");
            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                ["dep"] = new DependencySeed(Sql: new List<string> { "a.sql" }),
            });

            var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
                SeedApplier.ApplyAsync(
                    seed,
                    discoveredServices: new Dictionary<string, object> { ["dep"] = DummyConnString },
                    dependencyTypes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["dep"] = nonRelationalType,
                    },
                    seedBaseDirectory: dir,
                    ct: CancellationToken.None));

            Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
            Assert.Contains("sql", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains(nonRelationalType, ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains("is not supported for its declared type", ex.Info.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── Case-sensitivity: reset and seed must agree (feat/case-sensitive-kinds) ──────
    //
    // ScenarioIsolationFactory.MapRelationalKind is the single shared definition of "which
    // dependency types count as relational" (see the header remark above MapRelationalKind
    // itself). A wrong-case relational type must be treated as non-relational HERE exactly as
    // ScenarioIsolationFactoryTests.TypeMatching_IsCaseSensitive_WrongCaseIsNotResettable proves
    // for the reset path — never relational for one and not the other, which would let a suite
    // seed rows into a store the runner then never resets between scenarios (or vice versa).

    /// <summary>
    /// A relational type spelled with the wrong case (e.g. <c>Postgres</c>) is not treated as
    /// relational by the seed dispatcher: a <c>sql</c> seed under it throws the same NIT-1
    /// mismatch as any other non-relational type — proving the seed and reset paths agree via
    /// the shared <c>MapRelationalKind</c>, rather than one silently accepting the wrong case
    /// while the other rejects it.
    /// </summary>
    [Theory]
    [InlineData("Postgres")]
    [InlineData("POSTGRES")]
    [InlineData("SqlServer")]
    [InlineData("MySql")]
    public async Task ApplyAsync_SqlUnderWrongCaseRelationalType_ThrowsMismatchProvision(
        string wrongCaseType)
    {
        var dir = NewTempDir();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.sql"), "SELECT 1;");
            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                ["orders-db"] = new DependencySeed(Sql: new List<string> { "a.sql" }),
            });

            var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
                SeedApplier.ApplyAsync(
                    seed,
                    discoveredServices: new Dictionary<string, object> { ["orders-db"] = DummyConnString },
                    dependencyTypes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["orders-db"] = wrongCaseType,
                    },
                    seedBaseDirectory: dir,
                    ct: CancellationToken.None));

            Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
            Assert.Contains("is not supported for its declared type", ex.Info.Detail, StringComparison.Ordinal);
            Assert.Contains(wrongCaseType, ex.Info.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Ordering guarantee: sql files are resolved in declared order. Neither file
    /// exists here, so the exception names whichever one the resolution loop
    /// reaches FIRST — proving declared order is respected rather than, say,
    /// reversed or resolved via an unordered structure.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_SqlFiles_ResolvedInDeclaredOrder_FirstMissingFileReportedFirst()
    {
        var dir = NewTempDir();
        try
        {
            var seed = new SeedSpec(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
            {
                ["orders-db"] = new DependencySeed(
                    Sql: new List<string> { "first-missing.sql", "second-missing.sql" }),
            });

            var ex = await Assert.ThrowsAsync<OrchestrationException>(() =>
                SeedApplier.ApplyAsync(
                    seed,
                    discoveredServices: new Dictionary<string, object> { ["orders-db"] = DummyConnString },
                    dependencyTypes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["orders-db"] = "postgres",
                    },
                    seedBaseDirectory: dir,
                    ct: CancellationToken.None));

            Assert.Contains("first-missing.sql", ex.Info.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("second-missing.sql", ex.Info.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
