// Vouchfx.Engine.Telemetry.Tests — LocalFileTelemetrySink (S10-G-04).
//
// Proves the local sink writes well-formed JSONL — one compact line per event, appended,
// UTF-8 without a BOM — to the injected outbox path (never the real %APPDATA%).

using System.Text;
using System.Text.Json;
using Xunit;

namespace Vouchfx.Engine.Telemetry.Tests;

public sealed class LocalFileTelemetrySinkTests
{
    [Fact]
    public async Task SendAsync_AppendsOneCompactJsonLinePerEvent()
    {
        using var paths = new TempPaths();
        var sink = new LocalFileTelemetrySink(paths);

        await sink.SendAsync(SampleEvent(1));
        await sink.SendAsync(SampleEvent(2));

        var lines = await File.ReadAllLinesAsync(paths.OutboxPath);

        // One line per event (appends, not overwrites).
        Assert.Equal(2, lines.Length);

        // Each line is a single compact JSON object (no embedded newline, parses cleanly).
        foreach (var line in lines)
        {
            Assert.DoesNotContain('\n', line);
            using var doc = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }

        // Field values survive the round-trip.
        using var doc1 = JsonDocument.Parse(lines[0]);
        Assert.Equal(1, doc1.RootElement.GetProperty("scenarioCount").GetInt32());
        using var doc2 = JsonDocument.Parse(lines[1]);
        Assert.Equal(2, doc2.RootElement.GetProperty("scenarioCount").GetInt32());
    }

    [Fact]
    public async Task SendAsync_WritesUtf8WithoutBom()
    {
        using var paths = new TempPaths();
        var sink = new LocalFileTelemetrySink(paths);

        await sink.SendAsync(SampleEvent(1));

        var bytes = await File.ReadAllBytesAsync(paths.OutboxPath);
        // No UTF-8 BOM (EF BB BF) at the start.
        Assert.False(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "Outbox must be UTF-8 without a BOM.");

        // Decodes as UTF-8 and ends with the line terminator.
        var text = Encoding.UTF8.GetString(bytes);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_CreatesTheConfigDirectoryWhenMissing()
    {
        using var paths = new TempPaths();
        // The vouchfx subdirectory does not exist yet.
        var dir = Path.GetDirectoryName(paths.OutboxPath)!;
        Assert.False(Directory.Exists(dir));

        await new LocalFileTelemetrySink(paths).SendAsync(SampleEvent(1));

        Assert.True(File.Exists(paths.OutboxPath));
    }

    private static TelemetryEvent SampleEvent(int scenarioCount) => new()
    {
        SchemaVersion = TelemetryEventBuilder.CurrentSchemaVersion,
        Timestamp = DateTimeOffset.UtcNow,
        InstallId = Guid.NewGuid(),
        ToolVersion = "1.0.0",
        EngineVersion = "1.0.0",
        DotnetVersion = ".NET 8.0.7",
        RunCount = 1,
        ScenarioCount = scenarioCount,
        StepVerdicts = new TelemetryVerdictCounts(),
        ScenarioVerdicts = new TelemetryVerdictCounts(),
        StepFamilies = new Dictionary<string, int>(),
        StepProviders = new Dictionary<string, int>(),
        StartupMs = 0,
        TimeToFirstTestMs = 0,
    };
}
