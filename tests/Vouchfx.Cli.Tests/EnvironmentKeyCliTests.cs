using System.Text.Json;
using Vouchfx.Cli.Selection;
using Vouchfx.Engine.Orchestration.Environments;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class EnvironmentKeyCliTests
{
    [Theory]
    [InlineData("s3", "RUSTFS_SECRET_KEY")]
    [InlineData("minio", "MINIO_ROOT_PASSWORD")]
    [InlineData("service", "UNRESERVED")]
    public void Validate_NormalCli_RejectsEqualsSuffixWithoutCanaryLeak(string kind, string prefix)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-env607-cli-").FullName;
        var path = Path.Combine(directory, "probe.e2e.yaml");
        File.WriteAllText(path, Scenario(kind, prefix + "=credential_canary_607"));
        var output = new StringWriter();
        Assert.Equal(4, ValidateCommand.Execute(path, json: true, output, new StringWriter()));
        Assert.DoesNotContain("credential_canary_607", output.ToString(), StringComparison.Ordinal);
        using var report = JsonDocument.Parse(output.ToString());
        Assert.False(report.RootElement.GetProperty("valid").GetBoolean());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Run_InvalidName_IsAuthoringRefusalBeforeAcquisition(int? parallel, bool sibling)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-env607-run-").FullName;
        File.WriteAllText(Path.Combine(directory, "z-refused.e2e.yaml"), Scenario("s3", "RUSTFS_SECRET_KEY=credential_canary_607"));
        var driver = new AuthoringRefusalExitTests.TestDriver();
        if (sibling)
        {
            File.WriteAllText(Path.Combine(directory, "environment.yaml"), "test-owned");
            File.WriteAllText(Path.Combine(directory, "a-pass.e2e.yaml"), """
                environment:
                  source: { driver: test-owned, config: ./environment.yaml }
                steps:
                  - id: pass-step
                    type: script.csharp
                    code: 'return;'
                """);
        }
        var output = new StringWriter();
        var events = Path.Combine(directory, "events.jsonl");
        Assert.Equal(4, await RunCommand.ExecuteAsync(directory, SelectionCriteria.None, parallel, watch: false,
            failOnEnvironmentError: false, failOnInconclusive: false, htmlReportPath: null, junitReportPath: null,
            eventsReportPath: events, eventsStreamPath: null, decorate: false, output, telemetryHook: null,
            CancellationToken.None, environmentDrivers: new EnvironmentDriverRegistry(new[] { driver })));
        Assert.Equal(sibling ? 1 : 0, driver.StartCount);
        if (sibling) Assert.Equal(1, Assert.Single(driver.Leases).DisposeCount);
        // A document rejected during discovery alone produces no event stream; do not
        // invent executed steps or require a report from that existing early return.
        if (!sibling) Assert.False(File.Exists(events));
        var eventText = sibling ? await File.ReadAllTextAsync(events) : string.Empty;
        Assert.DoesNotContain("credential_canary_607", output + eventText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stepId\":\"refused-step\"", eventText, StringComparison.Ordinal);
        if (sibling)
        {
            Assert.Contains("PASS", output.ToString(), StringComparison.Ordinal);
            var lines = (await File.ReadAllLinesAsync(events)).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.Contains(lines, line => line.RootElement.GetProperty("type").GetString() == "step-completed"
                    && line.RootElement.GetProperty("stepId").GetString() == "pass-step");
            }
            finally { foreach (var line in lines) line.Dispose(); }
        }
    }

    private static string Scenario(string kind, string key)
    {
        var env = kind == "service"
            ? new Dictionary<string, object> { ["services"] = new Dictionary<string, object> { ["app"] = new { image = "app", env = new Dictionary<string, string> { [key] = "value_canary_607" } } } }
            : new Dictionary<string, object> { ["dependencies"] = new Dictionary<string, object> { ["store"] = new { type = kind, env = new Dictionary<string, string> { [key] = "value_canary_607" } } } };
        return JsonSerializer.Serialize(new { environment = env, steps = new[] { new { id = "refused-step", type = "script.csharp", code = "return;" } } });
    }
}
