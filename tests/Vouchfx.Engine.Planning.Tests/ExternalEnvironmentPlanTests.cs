using Vouchfx.Engine.Planning.Ingest;
using Xunit;

namespace Vouchfx.Engine.Planning.Tests;

public sealed class ExternalEnvironmentPlanTests
{
    [Fact]
    public void ExternalSourceIsReportedUnanalysableInsteadOfEmptyInlineTopology()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-external-plan-").FullName;
        File.WriteAllText(Path.Combine(directory, "external.e2e.yaml"), """
            environment:
              source:
                driver: compose
                config: ./environment.compose.yaml
            steps:
              - id: check
                type: script.csharp
                code: "return;"
            """);
        var result = SuiteSetLoader.Load(directory, PlannerTestFixtures.BuildCoreRegistry());
        Assert.Empty(result.Suites);
        Assert.Contains("environment.source", Assert.Single(result.UnanalysableSuites).Error, StringComparison.Ordinal);
        Assert.Contains("not supported", Assert.Single(result.UnanalysableSuites).Error, StringComparison.Ordinal);
    }
}
