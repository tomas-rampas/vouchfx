// The nesting limit at the suite-parsing entry point. A document nested tens of thousands of
// levels deep overflows the stack inside YamlStream.Load, which no catch can stop, so every
// refused document below is refused by the pre-scan before the load; if the scan were unwired
// the 200,000-level rows would crash the test host instead of failing.
using System.Diagnostics;
using Vouchfx.Tests;
using Xunit;

namespace Vouchfx.Engine.Authoring.Tests;

public sealed class YamlDepthGuardTests
{
    private const string DepthText = "nested more than 64 levels deep";
    private static readonly TimeSpan s_bound = TimeSpan.FromSeconds(5);

    private static YamlParseException Refused(string yaml)
    {
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("64", ex.Message, StringComparison.Ordinal);
        Assert.Contains(DepthText, ex.Message, StringComparison.Ordinal);
        Assert.Contains($"line {ex.Line}, column {ex.Column}", ex.Message, StringComparison.Ordinal);
        return ex;
    }

    private static void AssertNotRefusedByDepth(string yaml)
    {
        // The document may still be refused for another reason (it has no mapping root, or no
        // metadata); the depth check must not be that reason.
        var ex = Record.Exception(() => YamlDocumentParser.Parse(yaml));
        Assert.DoesNotContain(DepthText, ex?.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_65NestedFlowSequences_IsRefusedWithLimitLineAndColumn_EvenWhenTheInnermostIsEmpty()
    {
        var ex = Refused(DeepYamlShapes.ClosedFlowSequences(DeepYamlShapes.Limit + 1));
        Assert.Equal(1, ex.Line);
        Assert.Equal(65, ex.Column);
    }

    [Fact]
    public void Parse_65NestedBlockMappings_IsRefusedWithLimitLineAndColumn()
    {
        var ex = Refused(DeepYamlShapes.BlockMappings(DeepYamlShapes.Limit + 1));
        Assert.Equal(65, ex.Line);
        Assert.Equal(65, ex.Column);
    }

    [Fact]
    public void Parse_65NestedBlockSequencesOnOneLine_IsRefusedAtTheSixtyFifthDash()
    {
        var ex = Refused(DeepYamlShapes.OneLineBlockSequences(DeepYamlShapes.Limit + 1));
        Assert.Equal(1, ex.Line);
        Assert.Equal(129, ex.Column);
    }

    [Fact]
    public void Parse_64NestedFlowSequences_IsNotRefusedByTheDepthCheck()
        => AssertNotRefusedByDepth(DeepYamlShapes.ClosedFlowSequences(DeepYamlShapes.Limit));

    [Fact]
    public void Parse_64NestedBlockMappings_IsNotRefusedByTheDepthCheck()
        => AssertNotRefusedByDepth(DeepYamlShapes.BlockMappings(DeepYamlShapes.Limit));

    [Fact]
    public void Parse_AValidSuiteWithAShallowStructure_StillParses()
    {
        var doc = YamlDocumentParser.Parse(
            "metadata: { name: shallow }\nsteps:\n  - { id: s1, type: http.rest, request: { method: GET, url: 'http://x' } }\n");
        Assert.Equal("shallow", doc.Metadata?.Name);
    }

    [Theory]
    [InlineData("flow", 200_000)]
    [InlineData("flow", DeepYamlShapes.OneMiB)]
    [InlineData("block", 200_000)]
    [InlineData("block", DeepYamlShapes.OneMiB)]
    [InlineData("closed-flow", DeepYamlShapes.OneMiB)]
    public void Parse_VeryDeepInput_IsRefusedInTimeBoundedByTheLimit(string shape, int characters)
    {
        var yaml = DeepYamlShapes.Large(shape, characters);
        var clock = Stopwatch.StartNew();
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        clock.Stop();
        Assert.Contains(DepthText, ex.Message, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < s_bound, $"refusal took {clock.Elapsed}");
    }

    [Fact]
    public void Parse_SecondDocumentIs65Deep_IsRefused_DepthIsCountedPerDocument()
    {
        var yaml = "metadata: { name: first }\n---\n" + DeepYamlShapes.ClosedFlowSequences(DeepYamlShapes.Limit + 1);
        var ex = Refused(yaml);
        Assert.Equal(3, ex.Line);
        Assert.Equal(65, ex.Column);
    }

    [Fact]
    public void Parse_TwoDocumentsEach64Deep_AreNotRefusedByTheDepthCheck()
    {
        // The depth restarts at the second document; it is not added to the first.
        var one = DeepYamlShapes.ClosedFlowSequences(DeepYamlShapes.Limit);
        AssertNotRefusedByDepth(one + "\n---\n" + one);
    }

    [Fact]
    public void Parse_Unterminated200000Brackets_IsRefusedByTheDepthCheckNotAtEndOfInput()
    {
        var yaml = DeepYamlShapes.UnterminatedFlowSequences(200_000);
        var clock = Stopwatch.StartNew();
        var ex = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        clock.Stop();
        Assert.Contains("64", ex.Message, StringComparison.Ordinal);
        Assert.Contains(DepthText, ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, ex.Line);
        Assert.Equal(65, ex.Column);
        Assert.True(clock.Elapsed < s_bound, $"refusal took {clock.Elapsed}");
    }
}
