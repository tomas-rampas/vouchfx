using System.Text.Json;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Planning;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class PlanAdvisoryRunCountTests
{
    [Theory]
    [InlineData(EventTypes.TransportNotice)]
    [InlineData(EventTypes.SuiteStarted)]
    [InlineData(EventTypes.SuiteCompleted)]
    public void Execute_Json_SharedAdvisoryIdsDoNotInflateScenarioCount(string type)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vouchfx-cli-plan-advice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "sample.e2e.yaml"), """
                metadata:
                  name: sample-suite
                steps:
                  - id: get-root
                    type: http.rest
                    target: api
                    method: GET
                    path: /
                """);
            var historyPath = Path.Combine(directory, "history.jsonl");
            File.WriteAllLines(historyPath, new[]
            {
                """{"type":"scenario-started","ts":"2026-01-02T00:00:00Z","runId":"scenario","scenarioId":"sample-suite"}""",
                JsonSerializer.Serialize(new { type, ts = "2026-01-02T00:00:15Z", runId = "shared-first" }),
                """{"type":"step-completed","ts":"2026-01-02T00:01:00Z","runId":"scenario","stepId":"get-root","verdict":"PASS","durationMs":10}""",
                JsonSerializer.Serialize(new { type, ts = "2026-01-02T00:00:30Z", runId = "shared-second" }),
            });
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = PlanCommand.Execute(directory, historyPath, json: true, outputPath: null,
                failOnGap: false, PlanThresholds.Defaults, stdout, stderr);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stderr.ToString());
            using var report = JsonDocument.Parse(stdout.ToString());
            var inventory = report.RootElement.GetProperty("inventory");
            Assert.Equal(1, inventory.GetProperty("runCount").GetInt32());
            Assert.Equal(0, inventory.GetProperty("skippedEventLines").GetInt32());
            Assert.Equal(0, inventory.GetProperty("unmatchedObservations").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
