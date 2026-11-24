// The key-alias pre-scan (a private copy linked into Compilation) and the validator seam that
// uses it. Most refused documents below would overflow the stack, or take exponential or
// quadratic time, inside YamlStream.Load, so the scan tests drive the scan directly and never
// load them. The validator tests do reach a load if the scan is not wired in, so each of them
// first proves the wiring with an acyclic document that Load would survive: a wiring
// regression then fails that assertion cleanly instead of crashing the test host. The canary
// proves only that the scan is wired in: a regression in the scan itself that stopped refusing
// one shape would still crash the host on that shape's validator row.
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
    // Theory rows carry the refusal kind by name: the enum is internal, test methods are public.
    private const string Collection = nameof(YamlKeyAliasGuard.RefusalKind.CollectionAnchor);
    private const string Forward = nameof(YamlKeyAliasGuard.RefusalKind.AnchorNotDefinedBefore);

    // Refused by the scan, but loads without harm: an alias to a completed, acyclic collection.
    private const string WiringCanary = "steps: []\na: &k [ 1 ]\nb: { ? *k : v }\n";

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(new[] { typeof(HttpRestProvider).Assembly });

    public static TheoryData<string, string, int, int, string> Refused => new()
    {
        // An alias to a collection anchor that is already defined.
        { "steps: []\na: &k [ *k ]\nb: { ? *k : v }\n", "k", 3, 8, Collection },
        { "steps: []\na: &k [ *k ]\n? [ *k ] : v\n", "k", 3, 5, Collection },
        { "steps: []\na: &k [ *k ]\nb: { *k : v }\n", "k", 3, 6, Collection },
        { "steps: []\na: &k [ *k ]\n*k : v\n", "k", 3, 1, Collection },
        { "steps: []\na: &x [ &y [ *x ] ]\nb: { ? *y : v }\n", "y", 3, 8, Collection },
        { "steps: []\na: &w [ &k [ *k ], 1 ]\nb: { ? *w : v }\n", "w", 3, 8, Collection },
        { "steps: []\na: &k [ x ]\nb: &k [ *k ]\nc: { ? *k : v }\n", "k", 4, 8, Collection },
        { "steps: []\na: &k !!seq [ *k ]\nb: { ? !!seq [ *k ] : v }\n", "k", 3, 16, Collection },
        { "steps: []\na: &k !!seq [ *k ]\n? !!seq [ *k ] : v\n", "k", 3, 11, Collection },
        { "steps: []\na: &k [ *k ]\nb: &j [ *j ]\nc: { ? *k : v, ? *j : w }\n", "k", 4, 8, Collection },
        { "steps: []\nbody: { ? &k [ *k ] : v }\n", "k", 2, 16, Collection },
        { "steps: []\nbody: { ? &k { x: *k } : v }\n", "k", 2, 19, Collection },
        { "? &k [ *k ] : v\n", "k", 1, 8, Collection },
        // The consequence of the rule: an acyclic, completed collection is refused too.
        { "steps: []\na: &k [ 1 ]\nb: { ? *k : v }\n", "k", 3, 8, Collection },
        { "steps: []\na: &m { x: 1 }\nb: { ? [ *m ] : v }\n", "m", 3, 10, Collection },
        // A scalar anchor redefined as a collection before use.
        { "steps: []\na: &k one\nb: &k [ two ]\nc: { ? *k : v }\n", "k", 4, 8, Collection },
        // An alias whose anchor is not defined before it, whatever the anchor turns out to be:
        // a scalar, an acyclic or cyclic sequence, a mapping, or nothing at all.
        { "steps: []\n? *k : v\nb: &k x\n", "k", 2, 3, Forward },
        { "steps: []\n? *k : v\nb: &k [ x ]\n", "k", 2, 3, Forward },
        { "steps: []\n? *k : v\nb: &k { x: y }\n", "k", 2, 3, Forward },
        { "steps: []\n? *k : v\nb: &k [ *k ]\n", "k", 2, 3, Forward },
        { "steps: []\na: { ? *k : v }\nb: &k [ *k ]\n", "k", 2, 8, Forward },
        { "steps: []\na: { *k : v }\nb: &k [ *k ]\n", "k", 2, 6, Forward },
        // A key inside a key, and flow pairs inside a sequence.
        { "steps: []\n? { ? *k : 1 } : 2\nb: &k [ *k ]\n", "k", 2, 7, Forward },
        { "steps: []\na: [ ? *k : v ]\nb: &k [ *k ]\n", "k", 2, 8, Forward },
        { "steps: []\na: [ *k : v ]\nb: &k [ *k ]\n", "k", 2, 6, Forward },
        // Nested in a key, with a forward alias as that pair's value.
        { "steps: []\n? [ *k ] : *k\nb: &k [ *k ]\n", "k", 2, 5, Forward },
        // Never defined, or defined only in an earlier document of the stream.
        { "steps: []\nb: { ? *nowhere : v }\n", "nowhere", 2, 8, Forward },
        { "a: &k [ x ]\n---\nb: { ? *k : v }\n", "k", 3, 8, Forward },
        { "a: &k x\n---\nb: { ? *k : v }\n", "k", 3, 8, Forward },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void TryFindRefusedKeyAlias_RefusesAliasToACollectionOrToAnAnchorNotDefinedBefore(
        string yaml, string anchor, int line, int column, string kind)
    {
        Assert.True(YamlKeyAliasGuard.TryFindRefusedKeyAlias(yaml, out var refusal));
        Assert.Equal(kind, refusal.Kind.ToString());
        Assert.Equal(anchor, refusal.Anchor);
        Assert.Equal(line, refusal.Line);
        Assert.Equal(column, refusal.Column);
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
    // A forward alias outside a key is left to the schema step, which refuses it itself.
    [InlineData("steps: []\na: *k\nb: &k [ *k ]\n")]
    [InlineData("steps: []\nc: { ? x : *k }\nb: &k [ *k ]\n")]
    [InlineData("steps: []\na: [ *k ]\nb: &k x\n")]
    public void TryFindRefusedKeyAlias_AllowsScalarAliasesInlineCollectionsAndValueAliases(string yaml)
    {
        Assert.False(YamlKeyAliasGuard.TryFindRefusedKeyAlias(yaml, out _));
    }

    [Fact]
    public void TryFindRefusedKeyAlias_MalformedDocument_ReturnsFalseWithoutThrowing()
    {
        Assert.False(YamlKeyAliasGuard.TryFindRefusedKeyAlias("a: [unclosed\n", out _));
    }

    [Fact]
    public void TryFindRefusedKeyAlias_DeepAliasChainUsedAsKey_IsRefusedBeforeLoad()
    {
        var yaml = BuildAliasChainKey(60_000);
        var watch = Stopwatch.StartNew();
        Assert.True(YamlKeyAliasGuard.TryFindRefusedKeyAlias(yaml, out var refusal));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"scan took {watch.Elapsed}");
        Assert.Equal(Collection, refusal.Kind.ToString());
        Assert.Equal("c59999", refusal.Anchor);
        Assert.Equal(3, refusal.Line);
        Assert.Equal(5, refusal.Column);
    }

    [Fact]
    public void TryFindRefusedKeyAlias_BillionLaughsKey_IsRefusedPromptly()
    {
        var yaml = BuildBillionLaughsKey();
        var watch = Stopwatch.StartNew();
        Assert.True(YamlKeyAliasGuard.TryFindRefusedKeyAlias(yaml, out var refusal));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"scan took {watch.Elapsed}");
        Assert.Equal(Collection, refusal.Kind.ToString());
        Assert.Equal("l8", refusal.Anchor);
        Assert.Equal(11, refusal.Line);
    }

    [Fact]
    public void TryFindRefusedKeyAlias_ForwardBillionLaughsKey_IsRefusedPromptly()
    {
        var yaml = BuildForwardBillionLaughsKey(9);
        var watch = Stopwatch.StartNew();
        Assert.True(YamlKeyAliasGuard.TryFindRefusedKeyAlias(yaml, out var refusal));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"scan took {watch.Elapsed}");
        Assert.Equal(Forward, refusal.Kind.ToString());
        Assert.Equal("a9", refusal.Anchor);
        Assert.Equal(2, refusal.Line);
        Assert.Equal(7, refusal.Column);
    }

    // A scalar alias is hashed over the scalar's whole text at every use, so the text that
    // aliases bring into keys is capped per document, counted again for each enclosing key.
    [Fact]
    public void TryFindRefusedKeyAlias_ScalarTextRepeatedInKeys_IsCappedPerDocument()
    {
        Assert.False(YamlKeyAliasGuard.TryFindRefusedKeyAlias(BuildRepeatedScalarKeys(100_000, 10), out _));

        Assert.True(YamlKeyAliasGuard.TryFindRefusedKeyAlias(BuildRepeatedScalarKeys(100_000, 11), out var refusal));
        Assert.Equal(YamlKeyAliasGuard.RefusalKind.KeyAliasTextBudget, refusal.Kind);
        Assert.Equal("a", refusal.Anchor);
        Assert.Equal(12, refusal.Line);
        Assert.Equal(2, refusal.Column);

        var scalar = "a: &a " + new string('x', 600_000) + "\n";
        Assert.False(YamlKeyAliasGuard.TryFindRefusedKeyAlias(scalar + "[ *a ]: 1\n", out _));
        Assert.True(YamlKeyAliasGuard.TryFindRefusedKeyAlias(scalar + "{ [ *a ]: 1 }: 2\n", out refusal));
        Assert.Equal(YamlKeyAliasGuard.RefusalKind.KeyAliasTextBudget, refusal.Kind);
        Assert.Equal(2, refusal.Line);
        Assert.Equal(5, refusal.Column);

        // The budget is per document.
        Assert.False(YamlKeyAliasGuard.TryFindRefusedKeyAlias(
            BuildRepeatedScalarKeys(100_000, 10) + "---\n" + BuildRepeatedScalarKeys(100_000, 10), out _));
    }

    // The cap is inclusive. One use of an alias inside one key is charged its value length + 1,
    // so a value of MaxKeyAliasText - 1 characters lands exactly on the cap and is accepted,
    // and one more character is the first refused length.
    [Fact]
    public void TryFindRefusedKeyAlias_ScalarTextExactlyAtTheCap_IsAcceptedAndOneMoreIsRefused()
    {
        var atCap = (int)YamlKeyAliasGuard.MaxKeyAliasText - 1;
        Assert.Equal(1_048_575, atCap);

        Assert.False(YamlKeyAliasGuard.TryFindRefusedKeyAlias(BuildSingleScalarKey(atCap), out _));

        Assert.True(YamlKeyAliasGuard.TryFindRefusedKeyAlias(BuildSingleScalarKey(atCap + 1), out var refusal));
        Assert.Equal(YamlKeyAliasGuard.RefusalKind.KeyAliasTextBudget, refusal.Kind);
        Assert.Equal("a", refusal.Anchor);
        Assert.Equal(2, refusal.Line);
        Assert.Equal(3, refusal.Column);
    }

    private static string BuildSingleScalarKey(int valueLength) =>
        "a: &a " + new string('x', valueLength) + "\n[ *a ]: 1\n";

    // YamlDotNet hashes a scalar's tag together with its value, so a short value under a long
    // tag costs as much to hash at every use as a long value: the cap counts the tag too. Each
    // use below is charged 1 (the value) + 100,000 (the tag) + 1, so ten uses fit under the cap
    // and the eleventh passes it.
    [Fact]
    public void TryFindRefusedKeyAlias_LongTagRepeatedInKeys_CountsTowardsTheCap()
    {
        var underCap = BuildRepeatedTaggedScalarKeys(100_000, 10);
        Assert.False(YamlKeyAliasGuard.TryFindRefusedKeyAlias(underCap, out _));
        var watch = Stopwatch.StartNew();
        new YamlDotNet.RepresentationModel.YamlStream().Load(new StringReader(underCap));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"load took {watch.Elapsed}");

        Assert.True(YamlKeyAliasGuard.TryFindRefusedKeyAlias(BuildRepeatedTaggedScalarKeys(100_000, 11), out var refusal));
        Assert.Equal(YamlKeyAliasGuard.RefusalKind.KeyAliasTextBudget, refusal.Kind);
        Assert.Equal("a", refusal.Anchor);
        Assert.Equal(12, refusal.Line);
        Assert.Equal(2, refusal.Column);
    }

    [Fact]
    public void TryFindRefusedKeyAlias_QuadraticTaggedScalarKeyDocument_IsRefusedPromptly()
    {
        // A one-character value under a 500,000-character tag, used in 50,000 keys: two uses
        // fit under the cap, the third (line 4) passes it.
        var yaml = BuildRepeatedTaggedScalarKeys(500_000, 50_000);
        var watch = Stopwatch.StartNew();
        Assert.True(YamlKeyAliasGuard.TryFindRefusedKeyAlias(yaml, out var refusal));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"scan took {watch.Elapsed}");
        Assert.Equal(YamlKeyAliasGuard.RefusalKind.KeyAliasTextBudget, refusal.Kind);
        Assert.Equal(4, refusal.Line);
        Assert.Equal(2, refusal.Column);
    }

    // YamlDotNet 16.3.0's scanner throws exceptions that are not YamlException for some
    // malformed input: ArgumentOutOfRangeException for a \U escape above U+10FFFF,
    // ArgumentNullException for a %TAG directive with an empty prefix, and ArgumentException
    // for an empty verbatim tag. The scan must leave such a document to the load that follows.
    public static TheoryData<string> ScannerThrowsANonYamlException => new()
    {
        "steps: []\na: \"\\UFFFFFFFF\"\n",
        "%TAG ! \n---\nsteps: []\n",
        "steps: []\na: !<> b\n",
    };

    [Theory]
    [MemberData(nameof(ScannerThrowsANonYamlException))]
    public void TryFindRefusedKeyAlias_ScannerThrowsANonYamlException_ReturnsFalse(string yaml)
    {
        Assert.False(YamlKeyAliasGuard.TryFindRefusedKeyAlias(yaml, out _));
    }

    [Theory]
    [MemberData(nameof(ScannerThrowsANonYamlException))]
    public void Validate_ScannerThrowsANonYamlException_IsInvalidWithoutThrowing(string yaml)
    {
        var result = DocumentValidator.Validate(yaml, s_registry);

        Assert.False(result.IsValid);
        Assert.StartsWith("Failed to parse YAML: ", Assert.Single(result.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusal_Default_ToStringDoesNotThrow()
    {
        Assert.Contains("Kind = CollectionAnchor", default(YamlKeyAliasGuard.Refusal).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TryFindRefusedKeyAlias_QuadraticScalarKeyDocument_IsRefusedPromptly()
    {
        // Measured on the pinned YamlDotNet 16.3.0: YamlStream.Load takes about 14 s on this
        // 1.2 MB document, hashing a 500,000-character scalar once for each of 50,000 keys.
        var yaml = BuildRepeatedScalarKeys(500_000, 50_000);
        var watch = Stopwatch.StartNew();
        Assert.True(YamlKeyAliasGuard.TryFindRefusedKeyAlias(yaml, out var refusal));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"scan took {watch.Elapsed}");
        Assert.Equal(YamlKeyAliasGuard.RefusalKind.KeyAliasTextBudget, refusal.Kind);
        Assert.Equal(4, refusal.Line);
    }

    [Theory]
    [InlineData(Collection, "refers to a mapping or sequence; a key may refer by alias only to a scalar.")]
    [InlineData(Forward, "refers to an anchor that is not defined before it; a key may refer by alias only to a scalar anchor defined earlier in the same document.")]
    [InlineData(nameof(YamlKeyAliasGuard.RefusalKind.KeyAliasTextBudget), "brings the scalar text repeated through aliases in keys, tags included, past 1048576 characters")]
    public void Message_IsAsciiAndTruncatesALongAliasName(string kind, string cause)
    {
        var message = YamlKeyAliasGuard.Message(Enum.Parse<YamlKeyAliasGuard.RefusalKind>(kind), new string('x', 400_000), 3, 5);
        Assert.True(message.Length < 400);
        Assert.All(message, ch => Assert.True(ch < 128));
        Assert.StartsWith("YAML parse error at line 3, column 5: alias '" + new string('x', 64) + "...' inside a mapping key ", message, StringComparison.Ordinal);
        Assert.Contains(cause, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Message_DoesNotSplitASurrogatePairWhenTruncating()
    {
        // 63 ASCII characters, then U+1F600 as a surrogate pair across the 64-unit cap.
        var name = new string('x', 63) + "\U0001F600" + "tail";
        var message = YamlKeyAliasGuard.Message(YamlKeyAliasGuard.RefusalKind.CollectionAnchor, name, 1, 1);
        Assert.Contains("alias '" + new string('x', 63) + "...'", message, StringComparison.Ordinal);
        Assert.False(message.Any(char.IsSurrogate), message);
    }

    [Fact]
    public void Validate_AcyclicCollectionAliasInKey_IsInvalidWithLocatedMessage()
    {
        AssertValidatorRefuses(WiringCanary, "k", 3, 8);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Validate_RefusedKeyAlias_IsInvalidWithLocatedMessage_AndDoesNotCrash(
        string yaml, string anchor, int line, int column, string kind)
    {
        AssertValidatorRefuses(WiringCanary, "k", 3, 8);

        var error = AssertValidatorRefuses(yaml, anchor, line, column);
        Assert.Equal(
            YamlKeyAliasGuard.Message(Enum.Parse<YamlKeyAliasGuard.RefusalKind>(kind), anchor, line, column), error.Message);
    }

    [Fact]
    public void Validate_BillionLaughsKey_IsInvalidPromptly()
    {
        AssertValidatorRefuses(WiringCanary, "k", 3, 8);

        var watch = Stopwatch.StartNew();
        var result = DocumentValidator.Validate(BuildBillionLaughsKey(), s_registry);
        Assert.False(result.IsValid);
        Assert.Contains("a key may refer by alias only to a scalar", Assert.Single(result.Errors).Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"validate took {watch.Elapsed}");
    }

    [Fact]
    public void Validate_ForwardBillionLaughsKey_IsInvalidPromptly()
    {
        AssertValidatorRefuses(WiringCanary, "k", 3, 8);

        var watch = Stopwatch.StartNew();
        var error = AssertValidatorRefuses(BuildForwardBillionLaughsKey(9), "a9", 2, 7);
        Assert.Contains("not defined before it", error.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"validate took {watch.Elapsed}");
    }

    [Fact]
    public void Validate_QuadraticScalarKeyDocument_IsInvalidPromptly()
    {
        AssertValidatorRefuses(WiringCanary, "k", 3, 8);

        var watch = Stopwatch.StartNew();
        var error = AssertValidatorRefuses(BuildRepeatedScalarKeys(500_000, 50_000), "a", 4, 2);
        Assert.Contains("past 1048576 characters", error.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"validate took {watch.Elapsed}");
    }

    [Fact]
    public void Validate_LongTagRepeatedInKeys_IsInvalidAtTheCap()
    {
        var error = AssertValidatorRefuses(BuildRepeatedTaggedScalarKeys(100_000, 11), "a", 12, 2);
        Assert.Contains("tags included, past 1048576 characters", error.Message, StringComparison.Ordinal);
    }

    private static SchemaValidationError AssertValidatorRefuses(string yaml, string anchor, int line, int column)
    {
        var result = DocumentValidator.Validate(yaml, s_registry);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.StartsWith(
            $"YAML parse error at line {line}, column {column}: alias '{anchor}' inside a mapping key ",
            error.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("recursion", error.Message, StringComparison.OrdinalIgnoreCase);
        return error;
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

    // The key, on line 2, names the top anchor before any anchor is defined; the levels follow.
    internal static string BuildForwardBillionLaughsKey(int levels)
    {
        var sb = new StringBuilder("variables:\n");
        sb.Append("  ? [ *a").Append(levels).Append(" ] : *a").Append(levels).Append('\n');
        sb.Append("  a0: &a0 [x, y]\n");
        for (var level = 1; level <= levels; level++)
        {
            sb.Append("  a").Append(level).Append(": &a").Append(level).Append(" [");
            sb.Append(string.Join(", ", Enumerable.Repeat("*a" + (level - 1), 10)));
            sb.Append("]\n");
        }

        sb.Append("steps: []\n");
        return sb.ToString();
    }

    // Line 1 anchors a scalar of the given length; lines 2 onwards each hold one key
    // `[*a, i]: v` (the alias at column 2). The block form `? [*a, i] : v` would differ: there
    // the explicit key is the compact mapping `{ [*a, i]: v }`, a key nested in a key.
    internal static string BuildRepeatedScalarKeys(int scalarLength, int keys)
    {
        var sb = new StringBuilder("a: &a ").Append('x', scalarLength).Append('\n');
        for (var i = 0; i < keys; i++)
        {
            sb.Append("[*a, ").Append(i).Append("]: v\n");
        }

        return sb.ToString();
    }

    // As BuildRepeatedScalarKeys, but the anchored scalar is the one character `x` under a local
    // tag `!ttt...` whose text (the tag YamlDotNet reports, `!` included) is tagLength long.
    internal static string BuildRepeatedTaggedScalarKeys(int tagLength, int keys)
    {
        var sb = new StringBuilder("a: &a !").Append('t', tagLength - 1).Append(" x\n");
        for (var i = 0; i < keys; i++)
        {
            sb.Append("[*a, ").Append(i).Append("]: v\n");
        }

        return sb.ToString();
    }
}
