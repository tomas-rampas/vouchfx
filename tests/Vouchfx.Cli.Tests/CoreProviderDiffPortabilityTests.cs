using System.Text.Json;
using Vouchfx.Cli;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class CoreProviderDiffPortabilityTests
{
    public static TheoryData<string> TableProviders => new()
    {
        "http.rest", "db-assert.postgres", "db-assert.mysql", "db-assert.sqlserver",
        "db-assert.mongodb", "db-assert.dynamodb", "cache-assert.redis",
        "metrics-assert.prometheus", "storage-assert.s3",
    };

    [Theory]
    [MemberData(nameof(TableProviders))]
    public void FixedTableFormatting_UsesAsciiAndAlignedColumns(string kind)
    {
        var rows = Render(kind, "SHIPPED", "PENDING");
        Assert.All(rows, row => Assert.All(row, character => Assert.InRange(character, '\0', '\x7f')));
        Assert.Equal(2, rows[0].Count(character => character == '|'));
        Assert.Equal(2, rows[1].Count(character => character == '+'));
        Assert.All(rows[1], character => Assert.True(character is '-' or '+'));
        Assert.Equal(rows[0].Length, rows[1].Length);
        Assert.Equal(rows[0].Length, rows[2].Length);
        Assert.Equal(rows[0].IndexOf('|'), rows[2].IndexOf('|'));
        Assert.Equal(rows[0].LastIndexOf('|'), rows[2].LastIndexOf('|'));
    }

    [Theory]
    [MemberData(nameof(TableProviders))]
    public void UnicodeAuthorValues_ArePreserved_WhileLiteralPipesAreEscaped(string kind)
    {
        var rows = Render(kind, "Příliš 東京 │|café", "été 😀|完了");
        Assert.Contains("Příliš 東京 │\\u007ccafé", rows[2], StringComparison.Ordinal);
        if (kind != "http.rest")
            Assert.Contains("été 😀\\u007c完了", rows[2], StringComparison.Ordinal);
        Assert.Equal(2, rows[2].Count(character => character == '|'));
        Assert.Equal(rows[0].Length, rows[2].Length);
        Assert.Equal(rows[0].IndexOf('|'), rows[2].IndexOf('|'));
        Assert.Equal(rows[0].LastIndexOf('|'), rows[2].LastIndexOf('|'));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void TraceCountFormatting_UsesAscii_IncludingTheRetentionNote(int evicted)
    {
        Assert.True(ProviderRegistryFactory.BuildCoreRegistry().TryGet("trace-expect.otlp", out var provider));
        var renderer = Assert.IsAssignableFrom<IStepDiffRenderer>(provider!.Instance);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            matched = false,
            scanned = 5,
            matchedCriteria = 1,
            totalCriteria = 3,
            evicted,
        }));
        var diff = renderer.RenderDiff(json.RootElement);
        Assert.NotNull(diff);
        Assert.All(diff!, character => Assert.InRange(character, '\0', '\x7f'));
        Assert.Contains("1/3 criteria", diff, StringComparison.Ordinal);
        Assert.Contains("never rendered - section 17", diff, StringComparison.Ordinal);
        Assert.Equal(evicted > 0, diff.Contains("ring buffer saturated -", StringComparison.Ordinal));
    }

    private static string[] Render(string kind, string expected, string actual)
    {
        Assert.True(ProviderRegistryFactory.BuildCoreRegistry().TryGet(kind, out var provider));
        var renderer = Assert.IsAssignableFrom<IStepDiffRenderer>(provider!.Instance);
        object observation = kind switch
        {
            "http.rest" => new { body = new { failed = 1, of = 1, first = new { assertion = "bodyContains", reason = "notFound", expected } } },
            "db-assert.postgres" or "db-assert.mysql" or "db-assert.sqlserver" => new { column = "status", expected, actual },
            "db-assert.mongodb" or "db-assert.dynamodb" or "storage-assert.s3" => new { field = "status", expected, actual },
            _ => new { value = new { expected, actual } },
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(observation));
        Assert.True(renderer.CanRender(json.RootElement));
        var diff = renderer.RenderDiff(json.RootElement);
        Assert.NotNull(diff);
        var rows = diff!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, rows.Length);
        return rows;
    }
}
