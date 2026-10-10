using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ExternalEnvironmentScaffoldTests
{
    [Theory]
    [InlineData("\"source\": {\"driver\":\"compose\",\"config\":\"environment.compose.yaml\"}")]
    [InlineData("\"environment\": {\"source\":{\"driver\":\"compose\",\"config\":\"environment.compose.yaml\"}}")]
    public void ExternalSourceIntentIsExplicitlyRefusedWithoutEmittingInlineYaml(string source)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-source-scaffold-").FullName;
        var intent = Path.Combine(directory, "intent.json");
        File.WriteAllText(intent, "{\"steps\":[{\"id\":\"check\",\"type\":\"script.csharp\"}]," + source + "}");
        var output = new StringWriter();
        var errors = new StringWriter();
        Assert.Equal(2, ScaffoldCommand.Execute(intent, null, output, errors));
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("not scaffolded", errors.ToString(), StringComparison.Ordinal);
    }
}
