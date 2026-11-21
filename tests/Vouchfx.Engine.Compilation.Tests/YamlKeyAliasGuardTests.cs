// The key-alias pre-scan (a private copy linked into Compilation) and the validator seam that
// uses it. Every refused document below would overflow the stack or take exponential time inside
// YamlStream.Load, so the tests drive the scan directly and never load them.
using System.Diagnostics;
using System.Text;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class YamlKeyAliasGuardTests
{
    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(new[] { typeof(HttpRestProvider).Assembly });

    public static TheoryData<string, string, int, int> Refused => new()
    {
        { "steps: []\na: &k [ *k ]\nb: { ? *k : v }\n", "k", 3, 8 },
        { "steps: []\na: &k [ *k ]\n? [ *k ] : v\n", "k", 3, 5 },
        { "steps: []\na: &k [ *k ]\nb: { *k : v }\n", "k", 3, 6 },
        { "steps: []\na: &k [ *k ]\n*k : v\n", "k", 3, 1 },
        { "steps: []\na: &x [ &y [ *x ] ]\nb: { ? *y : v }\n", "y", 3, 8 },
        { "steps: []\na: &w [ &k [ *k ], 1 ]\nb: { ? *w : v }\n", "w", 3, 8 },
        { "steps: []\na: &k [ x ]\nb: &k [ *k ]\nc: { ? *k : v }\n", "k", 4, 8 },
        { "steps: []\na: &k !!seq [ *k ]\nb: { ? !!seq [ *k ] : v }\n", "k", 3, 16 },
        { "steps: []\na: &k !!seq [ *k ]\n? !!seq [ *k ] : v\n", "k", 3, 11 },
        { "steps: []\na: &k [ *k ]\nb: &j [ *j ]\nc: { ? *k : v, ? *j : w }\n", "k", 4, 8 },
        { "steps: []\nbody: { ? &k [ *k ] : v }\n", "k", 2, 16 },
        { "steps: []\nbody: { ? &k { x: *k } : v }\n", "k", 2, 19 },
        { "? &k [ *k ] : v\n", "k", 1, 8 },
        // The consequence of the rule: an acyclic, completed collection is refused too.
        { "steps: []\na: &k [ 1 ]\nb: { ? *k : v }\n", "k", 3, 8 },
        { "steps: []\na: &m { x: 1 }\nb: { ? [ *m ] : v }\n", "m", 3, 10 },
        // A scalar anchor redefined as a collection before use.
        { "steps: []\na: &k one\nb: &k [ two ]\nc: { ? *k : v }\n", "k", 4, 8 },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void TryFindCollectionAliasInKey_RefusesAliasToACollection(string yaml, string anchor, int line, int column)
    {
        Assert.True(YamlKeyAliasGuard.TryFindCollectionAliasInKey(yaml, out var found, out var l, out var c));
        Assert.Equal(anchor, found);
        Assert.Equal(line, l);
        Assert.Equal(column, c);
    }

    [Theory]
    [InlineData("steps: []\nvariables:\n  base: &s text\n  ? *s : v\n")]
    [InlineData("steps: []\nvariables:\n  ? [ &s one, *s ] : v\n")]
    [InlineData("steps: []\nvariables:\n  ? [a, b] : v\n")]
    [InlineData("steps: []\nvariables:\n  ? { a: b } : v\n")]
    [InlineData("steps: []\na: &k [ one ]\nb: &k two\nc: { ? *k : v }\n")]
    [InlineData("steps: []\na: &k [ *k ]\nb: *k\nc: [ *k ]\n")]
    [InlineData("steps: []\nbase: &base { x: 1 }\nm:\n  <<: *base\n  y: 2\n")]
    [InlineData("steps: []\nbase: &base { x: 1 }\nm:\n  <<: [ *base ]\n")]
    [InlineData("steps: []\nbody: { ? &k scalar : v }\n")]
    public void TryFindCollectionAliasInKey_AllowsScalarAliasesInlineCollectionsAndValueAliases(string yaml)
    {
        Assert.False(YamlKeyAliasGuard.TryFindCollectionAliasInKey(yaml, out _, out _, out _));
    }

    [Fact]
    public void TryFindCollectionAliasInKey_AnchorsDoNotCarryAcrossDocuments()
    {
        const string yaml = "a: &k [ x ]\n---\nb: { ? *k : v }\n";
        Assert.False(YamlKeyAliasGuard.TryFindCollectionAliasInKey(yaml, out _, out _, out _));
    }

    [Fact]
    public void TryFindCollectionAliasInKey_MalformedDocument_ReturnsFalseWithoutThrowing()
    {
        Assert.False(YamlKeyAliasGuard.TryFindCollectionAliasInKey("a: [unclosed\n", out _, out _, out _));
    }

    [Fact]
    public void TryFindCollectionAliasInKey_DeepAliasChainUsedAsKey_IsRefusedBeforeLoad()
    {
        var yaml = BuildAliasChainKey(60_000);
        var watch = Stopwatch.StartNew();
        Assert.True(YamlKeyAliasGuard.TryFindCollectionAliasInKey(yaml, out var anchor, out var line, out var column));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"scan took {watch.Elapsed}");
        Assert.Equal("c59999", anchor);
        Assert.Equal(3, line);
        Assert.Equal(5, column);
    }

    [Fact]
    public void TryFindCollectionAliasInKey_BillionLaughsKey_IsRefusedPromptly()
    {
        var yaml = BuildBillionLaughsKey();
        var watch = Stopwatch.StartNew();
        Assert.True(YamlKeyAliasGuard.TryFindCollectionAliasInKey(yaml, out var anchor, out var line, out _));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"scan took {watch.Elapsed}");
        Assert.Equal("l8", anchor);
        Assert.Equal(11, line);
    }

    [Fact]
    public void Message_IsAsciiAndTruncatesALongAliasName()
    {
        var message = YamlKeyAliasGuard.Message(new string('x', 400_000), 3, 5);
        Assert.True(message.Length < 300);
        Assert.All(message, ch => Assert.True(ch < 128));
        Assert.StartsWith("YAML parse error at line 3, column 5: alias '", message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Validate_RefusedKeyAlias_IsInvalidWithLocatedMessage_AndDoesNotCrash(string yaml, string anchor, int line, int column)
    {
        var result = DocumentValidator.Validate(yaml, s_registry);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.StartsWith($"YAML parse error at line {line}, column {column}: alias '{anchor}'", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("recursion", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_BillionLaughsKey_IsInvalidPromptly()
    {
        var watch = Stopwatch.StartNew();
        var result = DocumentValidator.Validate(BuildBillionLaughsKey(), s_registry);
        Assert.False(result.IsValid);
        Assert.Contains("a key may refer by alias only to a scalar", Assert.Single(result.Errors).Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"validate took {watch.Elapsed}");
    }

    internal static string BuildAliasChainKey(int links)
    {
        var sb = new StringBuilder("variables:\n  c: [");
        for (var i = 0; i < links; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('&').Append('c').Append(i).Append(" [").Append(i == 0 ? "x" : "*c" + (i - 1)).Append(']');
        }

        sb.Append("]\n  ? *c").Append(links - 1).Append(" : v\nsteps: []\n");
        return sb.ToString();
    }

    internal static string BuildBillionLaughsKey()
    {
        var sb = new StringBuilder("variables:\n  l0: &l0 [a, b]\n");
        for (var level = 1; level <= 8; level++)
        {
            sb.Append("  l").Append(level).Append(": &l").Append(level).Append(" [");
            sb.Append(string.Join(", ", Enumerable.Repeat("*l" + (level - 1), 10)));
            sb.Append("]\n");
        }

        sb.Append("  ? *l8 : v\nsteps: []\n");
        return sb.ToString();
    }
}
