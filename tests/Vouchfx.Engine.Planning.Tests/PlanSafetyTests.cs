// Vouchfx.Engine.Planning.Tests — PlanSafetyTests (M3 Planner, REQ-013, fix-round B3).
//
// T1 SCOPE: only REQ-013's "a test asserts the suite fixture folder is byte-identical after
// an analysis" acceptance criterion — the Planner never writes, modifies, or deletes any
// suite file. A later todo (T6) extends this same file with EDGE-006 (no secret/observation
// leakage into the report) and the "no model SDK on the code path" review-grep check, per the
// original T0 architecture note's file manifest.

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Xunit;

namespace Vouchfx.Engine.Planning.Tests;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "xUnit test methods use Given_When_Then underscore convention.")]
public sealed class PlanSafetyTests
{
    [Fact]
    public void Analysis_LeavesTheSuiteFixtureFolderByteIdentical()
    {
        var suiteRoot = PlannerTestFixtures.FixtureRoot("ingest/basic-suites");
        var eventsRoot = PlannerTestFixtures.FixtureRoot("ingest/basic-suites-events");

        var before = SnapshotDirectory(suiteRoot);

        _ = PlannerTestFixtures.Plan(suiteRoot, eventsRoot);

        var after = SnapshotDirectory(suiteRoot);

        Assert.Equal(
            before.Keys.OrderBy(k => k, StringComparer.Ordinal),
            after.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var (relativePath, hashBefore) in before)
        {
            Assert.True(
                after.TryGetValue(relativePath, out var hashAfter),
                $"File '{relativePath}' disappeared from the suite fixture folder after analysis.");
            Assert.Equal(hashBefore, hashAfter);
        }
    }

    private static Dictionary<string, string> SnapshotDirectory(string root)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var bytes = File.ReadAllBytes(file);
            snapshot[relative] = Convert.ToHexString(SHA256.HashData(bytes));
        }

        return snapshot;
    }
}
