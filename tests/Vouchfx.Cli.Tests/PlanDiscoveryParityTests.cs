// Vouchfx.Cli.Tests — PlanDiscoveryParityTests (M3 Planner, flag F-6, flag F-19). No Docker.
//
// F-6: Vouchfx.Engine.Planning.Ingest.SuiteSetLoader is a behaviour-parity CLONE of
// ScenarioDiscovery — the public library API cannot reference ScenarioDiscovery directly (it
// is internal to Vouchfx.Cli, exposed only to Vouchfx.Cli.Tests, and lives above the engine
// libraries). This test proves both return the same ordered suite path list for the same
// root, so the clone cannot silently drift from the original (a directory glob, a single-file
// root, and a nested path).
//
// F-19: SuiteSetLoader's derived ScenarioId is the sole event-history correlation key
// (REQ-008, given ScenarioStartedEvent.File is never emitted — flag F-1) and MUST equal
// RunCommand.ScenarioName's derivation character-for-character. A divergence here would
// silently make every suite read as never-run, with no other test failing — so this is a
// table-driven parity gate across every case that could plausibly diverge: metadata.name
// present / absent / empty / whitespace-only, `.e2e.yaml` vs `.E2E.YAML`, and a nested path.
//
// Only Vouchfx.Cli.Tests can see both originals (ScenarioDiscovery / RunCommand, internal to
// Vouchfx.Cli) and the Planner's public PlanExport surface, so this parity gate lives here,
// not in Vouchfx.Engine.Planning.Tests.

using Vouchfx.Engine.Planning;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class PlanDiscoveryParityTests : IDisposable
{
    private readonly string _root;

    public PlanDiscoveryParityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vouchfx-plan-discovery-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the test.
        }
    }

    private const string MinimalStep =
        "steps:\n"
        + "  - id: call-api\n"
        + "    type: http.rest\n";

    [Fact]
    public void OrderedPathList_MatchesScenarioDiscovery_ForADirectoryRoot()
    {
        var registry = ProviderRegistryFactory.BuildCoreRegistry();

        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "a.e2e.yaml"), MinimalStep);
        File.WriteAllText(Path.Combine(_root, "sub", "b.e2e.yaml"), MinimalStep);
        File.WriteAllText(Path.Combine(_root, "c.e2e.yaml"), MinimalStep);
        // A malformed suite (N3 fix-round): ScenarioDiscovery.Discover still reports it as a
        // DiscoveredScenario (Ast == null, per its own EDGE-003 handling), so the parity
        // check must compare against the UNION of the Planner's Suites and
        // UnanalysableSuites — comparing against Suites alone previously passed only because
        // every fixture happened to parse, silently leaving EDGE-003 parity unguarded.
        File.WriteAllText(Path.Combine(_root, "bad.e2e.yaml"), "steps:\n  - type: http.rest\n");

        var discovered = ScenarioDiscovery.Discover(_root, registry);
        var expected = discovered
            .Select(d => ToRelative(d.AbsolutePath))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var report = PlanExport.BuildPlan(new PlanRequest(_root), registry);
        var actual = report.Inventory.Suites
            .Select(s => s.Path)
            .Concat(report.Inventory.UnanalysableSuites.Select(u => u.Path))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(4, expected.Count);
        Assert.Equal(expected, actual);

        // The malformed file itself must parse-fail on both sides, not merely be present.
        Assert.Contains(discovered, d => d.Failed && ToRelative(d.AbsolutePath) == "bad.e2e.yaml");
        Assert.Contains(report.Inventory.UnanalysableSuites, u => u.Path == "bad.e2e.yaml");
    }

    [Fact]
    public void OrderedPathList_MatchesScenarioDiscovery_ForASingleFileRoot()
    {
        var registry = ProviderRegistryFactory.BuildCoreRegistry();
        var file = Path.Combine(_root, "only.e2e.yaml");
        File.WriteAllText(file, MinimalStep);

        var discovered = ScenarioDiscovery.Discover(file, registry);
        Assert.Single(discovered);

        var report = PlanExport.BuildPlan(new PlanRequest(file), registry);
        var suite = Assert.Single(report.Inventory.Suites);
        Assert.Equal(Path.GetFileName(file), suite.Path);
    }

    [Theory]
    [MemberData(nameof(ScenarioIdCases))]
    public void ScenarioId_MatchesRunCommandScenarioName(string relativePath, string yamlText)
    {
        var registry = ProviderRegistryFactory.BuildCoreRegistry();
        var fullPath = Path.Combine(_root, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, yamlText);

        var discovered = ScenarioDiscovery.ParseFile(fullPath, registry);
        var expected = RunCommand.ScenarioName(discovered);

        var report = PlanExport.BuildPlan(new PlanRequest(fullPath), registry);
        var actual = Assert.Single(report.Inventory.Suites).ScenarioId;

        Assert.Equal(expected, actual);
    }

    public static IEnumerable<object[]> ScenarioIdCases()
    {
        yield return new object[]
        {
            "metadata-name-present.e2e.yaml",
            "metadata:\n  name: named-scenario\n" + MinimalStep,
        };
        yield return new object[]
        {
            "metadata-name-absent.e2e.yaml",
            MinimalStep,
        };
        yield return new object[]
        {
            "metadata-name-empty.e2e.yaml",
            "metadata:\n  name: \"\"\n" + MinimalStep,
        };
        yield return new object[]
        {
            "metadata-name-whitespace.e2e.yaml",
            "metadata:\n  name: \"   \"\n" + MinimalStep,
        };
        yield return new object[]
        {
            "uppercase-extension.E2E.YAML",
            MinimalStep,
        };
        yield return new object[]
        {
            Path.Combine("nested", "deep", "scenario.e2e.yaml"),
            MinimalStep,
        };
    }

    private string ToRelative(string absolutePath) =>
        Path.GetRelativePath(_root, absolutePath).Replace('\\', '/');
}
