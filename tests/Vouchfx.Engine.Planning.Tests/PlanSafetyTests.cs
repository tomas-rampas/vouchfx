// Vouchfx.Engine.Planning.Tests — PlanSafetyTests (M3 Planner, REQ-013, EDGE-006,
// fix-round B3, T6 scope).
//
// T1 authored only REQ-013's "a test asserts the suite fixture folder is byte-identical after
// an analysis" acceptance criterion below. T6 EXTENDS this same file (never recreates it) with
// two more safety proofs, kept in one file because both are about the Planner's promise to
// never leak or mutate anything beyond its own report:
//   - EDGE-006: a fake secret value planted inside a fixture event's `observation` payload and
//     inside a malformed suite's own raw content must never appear, un-bounded, anywhere in
//     the serialised report.
//   - REQ-013's "no model SDK on the code path" review-grep check: the Planner's own source
//     (and its CLI wiring in PlanCommand.cs) must contain no reference to a model SDK,
//     endpoint, or credential shape.
//
// MINOR fix-round: this file previously ALSO planted the secret as the value of a process
// environment variable, mutated for the duration of the test. That mutation was removed —
// not merely restored-in-a-finally — because the Planner never reads an environment variable
// anywhere on its code path (confirmed by grep; PlannerSourceAndCliWiring_
// ReferenceNoModelSdkOrCredential below scans the same source tree for a different purpose,
// but the same tree). A mutation of process-global state that cannot possibly affect the
// system under test buys no coverage while adding a real (if narrow) risk under xUnit's
// parallel test-class execution; removing it is strictly better than the alternative of
// justifying it, since there is nothing to justify.

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
    // The fake secret literal is deliberately distinctive (a GUID-shaped, obviously synthetic
    // token) so a false-positive match against unrelated report content is not plausible. It
    // is planted verbatim inside the committed Fixtures/acceptance/secret-safety-events/
    // history.jsonl fixture's step-completed `observation` payload — see that file — and
    // (MINOR fix-round) inside Fixtures/acceptance/secret-safety/
    // malformed-with-planted-secret.e2e.yaml's deliberately garbage step `type` value, padded
    // past SuiteSetLoader.MaxParseErrorMessageChars. The observation-payload channel is NOT
    // real on its own (the Planner never reads a step-completed event's `observation` payload
    // — only its verdict/timestamp/runId/stepId — so absence there only ever proved "content
    // the Planner never touches doesn't appear in its output"); it is kept as a defence-in-depth
    // regression guard, not as this test's load-bearing proof. The malformed-suite channel IS
    // real: AstBuilder.ResolveKind echoes an unrecognised step `type` verbatim into its
    // exception message, which SuiteSetLoader.ParseFile captures into
    // PlanUnanalysableSuite.Error — genuinely reachable suite-file content flowing into the
    // report. This fixture proves the length bound added in the same fix-round actually
    // truncates it away, rather than merely asserting a channel that was never exercised.
    private const string FakeSecretValue = "sk-vfx-test-9f2b6d41-e006-4c2a-8c58-4b6a9a3d0e21";

    /// <summary>
    /// Tokens that would indicate a model SDK, model endpoint, or model-credential shape on
    /// the Planner's code path (REQ-013: "the Planner MUST NOT call a model API"). Kept
    /// conservative (specific product/package/hostname strings, never a bare generic word
    /// like "model") so this test cannot false-positive against legitimate engine
    /// vocabulary (e.g. <c>StepKindRegistry</c>, <c>PlanReportDocument</c>).
    /// </summary>
    private static readonly string[] ForbiddenModelSdkTokens =
    {
        "Anthropic",
        "OpenAI",
        "Microsoft.Extensions.AI",
        "Azure.AI.OpenAI",
        "SemanticKernel",
        "ChatCompletion",
        "api.anthropic.com",
        "api.openai.com",
    };

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

    // ── EDGE-006: a secret planted in an observation payload AND a malformed suite's own raw
    // content never reaches the serialised report un-bounded. ──────────────────────────────

    [Fact]
    public void SerialisedReport_NeverContainsASecretPlantedInAnObservationPayloadOrAMalformedSuite()
    {
        var report = PlannerTestFixtures.Plan(
            PlannerTestFixtures.FixtureRoot("acceptance/secret-safety"),
            PlannerTestFixtures.FixtureRoot("acceptance/secret-safety-events"));

        var json = PlanExport.SerializePlan(report);

        Assert.DoesNotContain(FakeSecretValue, json, StringComparison.Ordinal);

        // MINOR fix-round: prove the malformed-suite channel is genuinely exercised — not
        // merely absent because the file failed to be discovered/scanned at all — by
        // asserting the report DOES carry a bounded, truncated stand-in for it. A regression
        // that stopped bounding ex.Message (or stopped scanning the fixture at all) would
        // make one of these two assertions fail.
        var malformed = Assert.Single(
            report.Inventory.UnanalysableSuites,
            u => u.Path == "malformed-with-planted-secret.e2e.yaml");
        Assert.Contains("unknown step type", malformed.Error, StringComparison.Ordinal);
        Assert.Contains("... (truncated)", malformed.Error, StringComparison.Ordinal);
    }

    // ── REQ-013: no model SDK reference anywhere on the Planner code path (engine library +
    // its CLI wiring). ────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlannerSourceAndCliWiring_ReferenceNoModelSdkOrCredential()
    {
        var repoRoot = FindRepoRoot();
        var planningSourceRoot = Path.Combine(repoRoot, "src", "Engine", "Vouchfx.Engine.Planning");
        var planCommandFile = Path.Combine(repoRoot, "src", "Cli", "Vouchfx.Cli", "PlanCommand.cs");

        Assert.True(Directory.Exists(planningSourceRoot), $"Expected directory not found: '{planningSourceRoot}'.");
        Assert.True(File.Exists(planCommandFile), $"Expected file not found: '{planCommandFile}'.");

        var filesToScan = Directory.EnumerateFiles(planningSourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutputPath(f))
            .Concat(Directory.EnumerateFiles(planningSourceRoot, "*.csproj", SearchOption.TopDirectoryOnly))
            .Append(planCommandFile)
            .ToList();

        // Sanity check on the scan itself: a typo'd path that silently enumerated zero files
        // would make every assertion below vacuously true.
        Assert.True(filesToScan.Count > 5, "Expected to scan more than 5 files on the Planner code path.");

        foreach (var file in filesToScan)
        {
            var content = File.ReadAllText(file);
            foreach (var token in ForbiddenModelSdkTokens)
            {
                Assert.False(
                    content.Contains(token, StringComparison.OrdinalIgnoreCase),
                    $"'{file}' contains forbidden model-SDK token '{token}' — REQ-013 forbids "
                    + "the Planner code path from calling a model API or embedding model credentials.");
            }
        }
    }

    private static bool IsBuildOutputPath(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (no ancestor of "
            + $"'{AppContext.BaseDirectory}' contains 'vouchfx.sln').");
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
