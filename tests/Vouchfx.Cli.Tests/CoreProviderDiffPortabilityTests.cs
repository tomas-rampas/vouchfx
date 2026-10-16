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

    [Fact]
    public void RegisteredDiffRenderers_AllHaveAnExplicitPortabilityCase()
    {
        // Nine authored-value tables, Elasticsearch's two Markdown shapes, the
        // integer-only mail table and trace's integer-only summary. New renderers
        // must be assigned a real output case instead of silently escaping this suite.
        var covered = new List<string> { "cache-assert.elasticsearch", "mail-expect.smtp", "trace-expect.otlp" };
        foreach (string kind in TableProviders)
            covered.Add(kind);
        var registered = ProviderRegistryFactory.BuildCoreRegistry().All
            .Where(provider => provider.Instance is IStepDiffRenderer)
            .Select(provider => provider.Kind.Family + "." + provider.Kind.Provider).Order(StringComparer.Ordinal);
        Assert.Equal(registered, covered.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ElasticsearchMarkdownCells_PreserveBothLayoutsAndEscapeAuthoredTerminators(bool withField)
    {
        const string text = "Příliš 東京 │😀\r\n\t\v\f\u0085\u2028\u2029|fin";
        const string escaped = "Příliš 東京 │😀\\r\\n\\t\\u000b\\u000c\\u0085\\u2028\\u2029\\u007cfin";
        Assert.True(ProviderRegistryFactory.BuildCoreRegistry().TryGet("cache-assert.elasticsearch", out var provider));
        var renderer = Assert.IsAssignableFrom<IStepDiffRenderer>(provider!.Instance);
        object observation = withField
            ? new { matched = false, field = text, expected = text, actual = text }
            : new { matched = false, expected = text, actual = text };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(observation));
        Assert.True(renderer.CanRender(json.RootElement));
        var diff = renderer.RenderDiff(json.RootElement);
        Assert.NotNull(diff);
        var rows = diff!.Split('\n');

        Assert.Equal(new[]
        {
            withField ? $"| field | {escaped} |" : "| | Value |",
            "|---|---|",
            $"| expected | `{escaped}` |",
            $"| actual | `{escaped}` |",
        }, rows);
        Assert.All(rows, row => Assert.Equal(4, row.Split('|').Length));
    }

    [Fact]
    public void MailCountTable_HasOnlyIntegerCells_AndAsciiFormatting()
    {
        Assert.True(ProviderRegistryFactory.BuildCoreRegistry().TryGet("mail-expect.smtp", out var provider));
        var renderer = Assert.IsAssignableFrom<IStepDiffRenderer>(provider!.Instance);
        using var json = JsonDocument.Parse("""{"matched":false,"expected":2147483647,"actual":0}""");
        Assert.True(renderer.CanRender(json.RootElement));
        var diff = renderer.RenderDiff(json.RootElement);
        Assert.NotNull(diff);
        Assert.All(diff!, character => Assert.InRange(character, '\0', '\x7f'));
        Assert.Contains("2147483647", diff, StringComparison.Ordinal);
        Assert.Equal(4, diff.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

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
    [MemberData(nameof(TableProviders))]
    public void CellTerminators_AreEscapedInEveryAuthoredCell_AndUnicodeIsPreserved(string kind)
    {
        const string text = "Příliš 東京 │😀\r\n\t\v\f\u0085\u2028\u2029|fin";
        const string escaped = "Příliš 東京 │😀\\r\\n\\t\\u000b\\u000c\\u0085\\u2028\\u2029\\u007cfin";
        var rows = Render(kind, text, text, text);

        Assert.All("\r\n\t\v\f\u0085\u2028\u2029", character => Assert.DoesNotContain(character, rows[2]));
        var cells = rows[2].Split('|');
        Assert.Equal(3, cells.Length);
        Assert.Contains(escaped, cells[1], StringComparison.Ordinal);
        Assert.Contains(escaped, cells[2], StringComparison.Ordinal);
        if (kind is not ("cache-assert.redis" or "metrics-assert.prometheus"))
            Assert.Contains(escaped, cells[0], StringComparison.Ordinal);
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

    private static string[] Render(string kind, string expected, string actual, string? authoredLabel = null)
    {
        Assert.True(ProviderRegistryFactory.BuildCoreRegistry().TryGet(kind, out var provider));
        var renderer = Assert.IsAssignableFrom<IStepDiffRenderer>(provider!.Instance);
        object observation = kind switch
        {
            "http.rest" => new { body = new { failed = 1, of = 1, first = new { assertion = authoredLabel ?? "bodyContains", reason = authoredLabel is null ? "notFound" : actual, expected } } },
            "db-assert.postgres" or "db-assert.mysql" or "db-assert.sqlserver" => new { column = authoredLabel ?? "status", expected, actual },
            "db-assert.mongodb" or "db-assert.dynamodb" or "storage-assert.s3" => new { field = authoredLabel ?? "status", expected, actual },
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
