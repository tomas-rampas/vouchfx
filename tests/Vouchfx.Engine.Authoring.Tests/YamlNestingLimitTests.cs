using System.Text;
using Vouchfx.Engine.Authoring;
using Xunit;

namespace Vouchfx.Engine.Authoring.Tests;

/// <summary>
/// A deeply nested document must be refused before <c>YamlStream.Load</c> recurses
/// far enough to overflow the process stack.
/// </summary>
public sealed class YamlNestingLimitTests
{
    [Fact]
    public void Parse_OrdinarySuite_StillParses()
    {
        const string yaml = """
            metadata:
              name: ordinary
            steps:
              - id: read
                type: http.rest
                target: api
                method: GET
                path: /orders
            """;

        var document = YamlDocumentParser.Parse(yaml);

        Assert.Equal("ordinary", document.Metadata?.Name);
        Assert.Single(document.Steps);
    }

    [Fact]
    public void Parse_QuotedBrackets_DoNotCountAsNesting()
    {
        var yaml = "note: \"" + new string('[', 200) + "\"\n";

        var document = YamlDocumentParser.Parse(yaml);

        Assert.NotNull(document);
    }

    [Fact]
    public void Parse_FlowNestingAtTheLimit_StillParses()
    {
        var document = YamlDocumentParser.Parse(Flow(63));

        Assert.NotNull(document);
    }

    [Fact]
    public void Parse_BlockNestingAtTheLimit_StillParses()
    {
        var document = YamlDocumentParser.Parse(Block(64));

        Assert.NotNull(document);
    }

    [Fact]
    public void Parse_FlowNestingPastTheLimit_RefusesBeforeLoad()
    {
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(Flow(64)));

        Assert.Contains("nests more than 64 collections", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Line >= 1);
    }

    [Fact]
    public void Parse_BlockNestingPastTheLimit_RefusesBeforeLoad()
    {
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(Block(65)));

        Assert.Contains("nests more than 64 collections", ex.Message, StringComparison.Ordinal);
        Assert.Contains("line ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NestingFarPastTheCrashDepth_Refuses()
    {
        // A few hundred levels still load. The crash is tens of thousands of levels,
        // and a missing guard must fail this assertion rather than kill the test process.
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(Flow(200)));

        Assert.Contains("nests more than 64 collections", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A root mapping whose value is <paramref name="sequences"/> nested flow sequences.</summary>
    private static string Flow(int sequences) =>
        "k: " + new string('[', sequences) + "1" + new string(']', sequences) + "\n";

    /// <summary>A block mapping nested <paramref name="levels"/> collections deep, including the root.</summary>
    private static string Block(int levels)
    {
        var builder = new StringBuilder();
        for (var level = 0; level < levels - 1; level++)
        {
            builder.Append(' ', level * 2).Append("k:\n");
        }

        builder.Append(' ', (levels - 1) * 2).Append("v: 1\n");
        return builder.ToString();
    }
}
