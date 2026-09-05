// Issue #473 — the seed applier records (resolved seed SQL path → the author's declared text) into
// the run's SecurityPathDisclosureLedger, so the BCL's and the driver's own messages stop carrying
// an absolute host path into the archived artefacts.
//
// THE HALF THE EXISTING CODE ALREADY GOT RIGHT, AND THE HALF IT DID NOT. SeedApplier has carried
// the declared name alongside the resolved path since #357, and every diagnostic it composes names
// the declared one — the not-found message is the standing example and is regression-guarded
// below. What #357 did not cover is that two of those diagnostics splice `TrimDetail(ex.Message)`
// onto the end:
//
//     "seed could not read SQL file '<declared>': <ex.Message>"
//     "seed SQL file '<declared>' failed against dependency '<dep>': <ex.Message>"
//
// The second half is written by the BCL and by the ADO.NET driver, and the BCL quotes the path it
// was actually given — which is the resolved absolute one, because that is what
// File.ReadAllTextAsync must be handed. A ProvisionError's detail becomes an
// OrchestrationException message, reaches the §14 environment-error event and, on the suite path,
// is stamped onto every scenario's ScenarioCompletedEvent.message: the event stream, the JUnit
// `message` attribute and the HTML report.
//
// WHAT IS MEASURED HERE, WITHOUT DOCKER, AND WHAT IS NOT.
//
//   MEASURED: the real SeedApplier.ApplyAsync, over a real temp suite directory with a real seed
//     SQL file, records the pair — and the text the substitution is then applied to is the REAL
//     message the BCL builds when that same resolved path cannot be read. No message is written by
//     hand anywhere in this file.
//
//   NOT MEASURED: that the applier's own read-failure catch fires against a live database. Reaching
//     it needs an OPEN connection first (ApplyDependencyAsync opens before it reads), which needs a
//     real relational dependency — a docker-lane concern. What is pinned instead is the pair either
//     side of it: the applier records at resolution, and the ledger substitutes out of the exact
//     BCL text that catch would splice. Standing up a fake DbConnection to observe the catch would
//     measure the fake.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// A resolved <c>environment.seed</c> SQL path is substitutable out of the BCL's own diagnostic
/// text (#473).
/// </summary>
public sealed class SeedPathDisclosureTests : IDisposable
{
    private const string DepName = "orders-db";

    /// <summary>The author's own text — this half is NOT a disclosure and must SURVIVE.</summary>
    private const string DeclaredSqlFile = "seed/orders.sql";

    /// <summary>
    /// Postgres-shaped, unreachable, and bounded. Port 1 refuses immediately on every platform this
    /// suite runs on, and <c>Timeout=1</c> caps the case where something does listen — the applier
    /// must fail to OPEN, having already resolved and recorded the file paths, and it must do so
    /// without a container.
    /// </summary>
    private const string UnreachableConnString =
        "Host=127.0.0.1;Port=1;Database=db;Username=u;Password=p;Timeout=1";

    private readonly string _suiteDirectory;
    private readonly string _resolvedSqlPath;

    public SeedPathDisclosureTests()
    {
        _suiteDirectory = Path.Combine(
            Path.GetTempPath(), "vouchfx-473-seed-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(_suiteDirectory, "seed"));

        _resolvedSqlPath = Path.Combine(_suiteDirectory, "seed", "orders.sql");
        File.WriteAllText(_resolvedSqlPath, "CREATE TABLE IF NOT EXISTS orders (id int);");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_suiteDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    /// <summary>
    /// After a seed run, the ledger substitutes the author's declared file back into the REAL
    /// message the BCL builds when that resolved path cannot be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The absence is asserted BEFORE the presence</strong>, so a break fails on the leak
    /// rather than on a missing phrase — the convention <c>SecurityDiagnosticPathDisclosureTests</c>
    /// established, and the reason the shared
    /// <see cref="HostPathDisclosure.AssertNoAbsoluteHostPath"/> is reused rather than a third
    /// variant written — there were two before #473, and they had already diverged.
    /// </para>
    /// <para>
    /// <strong>The premise is asserted too:</strong> the raw message really does carry the resolved
    /// absolute path. Without that line the absence assertion would pass for text that never had
    /// one, and would keep passing with the recording deleted.
    /// </para>
    /// <para>
    /// The seed itself is EXPECTED to fail — the connection cannot be opened — and that failure is
    /// what proves the recording happens where it is claimed to: at path resolution, which runs
    /// before any connection is dialled. A run that got as far as reading the file would not
    /// distinguish the two.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ResolvedSeedPath_IsSubstitutedOutOfTheBclsOwnReadFailure()
    {
        var ledger = new SecurityPathDisclosureLedger();

        // The seed cannot complete: nothing is listening on port 1. That is the point — the
        // resolution (and therefore the recording) precedes the connection open.
        await Assert.ThrowsAsync<OrchestrationException>(
            () => SeedApplier.ApplyAsync(
                SeedWith(DeclaredSqlFile),
                Discovered(),
                Types(),
                seedBaseDirectory: _suiteDirectory,
                pathDisclosures: ledger,
                ct: CancellationToken.None));

        var raw = RealUnreadableFileMessage();

        // The premise: this really is BCL text quoting the resolved absolute path.
        Assert.Contains(_resolvedSqlPath, raw, StringComparison.Ordinal);

        var scrubbed = ledger.Scrub(raw)!;

        HostPathDisclosure.AssertNoAbsoluteHostPath(
            "the substituted seed read-failure detail", scrubbed, _suiteDirectory);
        Assert.Contains(DeclaredSqlFile, scrubbed, StringComparison.Ordinal);
    }

    /// <summary>
    /// The applier's OWN not-found diagnostic still names the declared file and nothing else — the
    /// half #357 already got right, guarded so this change does not quietly cost it.
    /// </summary>
    /// <remarks>
    /// This path is detected before any connection is opened, so it is the one seed diagnostic that
    /// is fully observable without a database. It is also the one that would silently regress if a
    /// future edit reached for <c>resolvedPath</c> because it happened to be in scope.
    /// </remarks>
    [Fact]
    public async Task MissingSeedFile_DiagnosticNamesTheDeclaredFileOnly()
    {
        var ledger = new SecurityPathDisclosureLedger();

        var ex = await Assert.ThrowsAsync<OrchestrationException>(
            () => SeedApplier.ApplyAsync(
                SeedWith("seed/absent.sql"),
                Discovered(),
                Types(),
                seedBaseDirectory: _suiteDirectory,
                pathDisclosures: ledger,
                ct: CancellationToken.None));

        HostPathDisclosure.AssertNoAbsoluteHostPath(
            "the seed not-found detail", ex.Info.Detail ?? string.Empty, _suiteDirectory);
        Assert.Contains("seed/absent.sql", ex.Info.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <see langword="null"/> ledger records nothing and changes nothing — the shape every
    /// pre-existing seed test and every embedding caller takes.
    /// </summary>
    [Fact]
    public async Task NoLedger_IsUnaffected()
    {
        await Assert.ThrowsAsync<OrchestrationException>(
            () => SeedApplier.ApplyAsync(
                SeedWith(DeclaredSqlFile),
                Discovered(),
                Types(),
                seedBaseDirectory: _suiteDirectory,
                pathDisclosures: null,
                ct: CancellationToken.None));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The message the BCL itself builds when the resolved seed SQL file cannot be read — produced
    /// by a real failure on the real path, never written by hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows enforces <see cref="FileShare.None"/> and raises <see cref="IOException"/> naming the
    /// absolute path ("used by another process"), which is the sharing-violation shape a seed run
    /// hits when something else holds the fixture. POSIX does not enforce it, so the second shape —
    /// the file REMOVED between the applier's existence check and its read — stands in; the BCL
    /// names the same absolute path either way, which is the only property this helper claims, and
    /// the arm consuming it asserts that property before concluding anything.
    /// </para>
    /// </remarks>
    private string RealUnreadableFileMessage()
    {
        try
        {
            using var exclusive = new FileStream(
                _resolvedSqlPath, FileMode.Open, FileAccess.Read, FileShare.None);

            _ = File.ReadAllText(_resolvedSqlPath);
        }
        catch (IOException ex) when (ex.Message.Contains(_resolvedSqlPath, StringComparison.Ordinal))
        {
            return ex.Message;
        }

        File.Delete(_resolvedSqlPath);
        try
        {
            _ = File.ReadAllText(_resolvedSqlPath);
        }
        catch (IOException ex) when (ex.Message.Contains(_resolvedSqlPath, StringComparison.Ordinal))
        {
            // FileNotFoundException derives from IOException, so this catches both shapes.
            return ex.Message;
        }

        throw new InvalidOperationException(
            "No real BCL message quoting the resolved seed path could be produced on this "
            + "platform, so the arm consuming this would assert nothing. Re-point it rather than "
            + "leave it green.");
    }

    private static Dictionary<string, object> Discovered() =>
        new(StringComparer.Ordinal) { [DepName] = UnreachableConnString };

    private static Dictionary<string, string> Types() =>
        new(StringComparer.Ordinal) { [DepName] = "postgres" };

    private static SeedSpec SeedWith(params string[] sqlFiles) =>
        new(new Dictionary<string, DependencySeed>(StringComparer.Ordinal)
        {
            [DepName] = new DependencySeed(sqlFiles),
        });
}
