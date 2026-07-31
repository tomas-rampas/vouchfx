// Non-docker unit tests for ImageReferenceParser (feat/dependency-image-override).
//
// These pin the repository/tag/digest split that `environment.dependencies[].image`
// feeds into Aspire's `WithImage(repository, tag)`. The two rows that matter most —
// and the two a naive IndexOf(':')/Split(':') implementation gets wrong — are the
// registry-port cases: "nexus.corp.local:5000/platform/mongo[:8.0]", where the ':5000'
// is a PORT, not a tag separator, because it appears BEFORE the last '/'. Every test
// here is pure string parsing with no Docker or Aspire dependency and runs in the
// standard unit-CI gate (dotnet test --filter "requires!=docker").

using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Unit tests for <see cref="ImageReferenceParser.Parse"/>.
/// </summary>
public sealed class ImageReferenceTests
{
    // -----------------------------------------------------------------------
    // Parse table (CLAUDE-task spec) — every row, pinned exactly.
    // -----------------------------------------------------------------------

    [Fact]
    public void Parse_BareName_ReturnsRepositoryOnly()
    {
        // Pins: "mongo" -> Repository "mongo", no tag, no digest.
        var result = ImageReferenceParser.Parse("mongo");

        Assert.Equal("mongo", result.Repository);
        Assert.Null(result.Tag);
        Assert.Null(result.Digest);
    }

    [Fact]
    public void Parse_NameWithTag_SplitsOnColon()
    {
        // Pins: "mongo:8.0" -> Repository "mongo", Tag "8.0".
        var result = ImageReferenceParser.Parse("mongo:8.0");

        Assert.Equal("mongo", result.Repository);
        Assert.Equal("8.0", result.Tag);
        Assert.Null(result.Digest);
    }

    [Fact]
    public void Parse_NamespacedName_TreatsWholePathAsRepository()
    {
        // Pins: "library/mongo" -> Repository "library/mongo", no tag.
        var result = ImageReferenceParser.Parse("library/mongo");

        Assert.Equal("library/mongo", result.Repository);
        Assert.Null(result.Tag);
        Assert.Null(result.Digest);
    }

    [Fact]
    public void Parse_RegistryHostNoPortWithTag_SplitsTagOffLastSegment()
    {
        // Pins: "nexus.corp.local/platform/mongo:8.0" ->
        // Repository "nexus.corp.local/platform/mongo", Tag "8.0".
        var result = ImageReferenceParser.Parse("nexus.corp.local/platform/mongo:8.0");

        Assert.Equal("nexus.corp.local/platform/mongo", result.Repository);
        Assert.Equal("8.0", result.Tag);
        Assert.Null(result.Digest);
    }

    [Fact]
    public void Parse_RegistryHostWithPortAndTag_PortColonIsNotTagSeparator()
    {
        // Pins the case a naive IndexOf(':')/Split(':') gets wrong: the FIRST colon in
        // "nexus.corp.local:5000/platform/mongo:8.0" is the registry port, which lies
        // BEFORE the last '/'. Only the colon in the last path segment ("mongo:8.0")
        // may introduce a tag.
        var result = ImageReferenceParser.Parse("nexus.corp.local:5000/platform/mongo:8.0");

        Assert.Equal("nexus.corp.local:5000/platform/mongo", result.Repository);
        Assert.Equal("8.0", result.Tag);
        Assert.Null(result.Digest);
    }

    [Fact]
    public void Parse_RegistryHostWithPortNoTag_PortColonIsNotTagSeparator()
    {
        // Pins the sibling case: same registry port, but with no tag at all — the port
        // colon must still not be mistaken for a (trailing, tag-less) separator.
        var result = ImageReferenceParser.Parse("nexus.corp.local:5000/platform/mongo");

        Assert.Equal("nexus.corp.local:5000/platform/mongo", result.Repository);
        Assert.Null(result.Tag);
        Assert.Null(result.Digest);
    }

    [Fact]
    public void Parse_NameWithDigest_ExtractsDigestIncludingAlgorithmPrefix()
    {
        // Pins: "mongo@sha256:abc..." -> Repository "mongo", Digest "sha256:abc..."
        // (the digest's own "sha256:" prefix is retained verbatim, not stripped).
        var result = ImageReferenceParser.Parse("mongo@sha256:abcdef0123456789");

        Assert.Equal("mongo", result.Repository);
        Assert.Null(result.Tag);
        Assert.Equal("sha256:abcdef0123456789", result.Digest);
    }

    [Fact]
    public void Parse_RegistryHostWithPortAndDigest_NoTag()
    {
        // Pins: "nexus.corp.local:5000/platform/mongo@sha256:abc..." ->
        // Repository "nexus.corp.local:5000/platform/mongo", Digest "sha256:abc...",
        // no tag. The digest split (on '@') must happen independently of, and before,
        // the port-vs-tag colon scan.
        var result = ImageReferenceParser.Parse(
            "nexus.corp.local:5000/platform/mongo@sha256:abcdef0123456789");

        Assert.Equal("nexus.corp.local:5000/platform/mongo", result.Repository);
        Assert.Null(result.Tag);
        Assert.Equal("sha256:abcdef0123456789", result.Digest);
    }

    [Fact]
    public void Parse_NameWithTagAndDigest_PopulatesAllThreeComponents()
    {
        // Pins: "mongo:8.0@sha256:abc..." -> Repository "mongo", Tag "8.0",
        // Digest "sha256:abc...". The tag is retained even though Docker treats the
        // digest as authoritative for pulling — this parser is a pure string split,
        // not a pull-precedence decision.
        var result = ImageReferenceParser.Parse("mongo:8.0@sha256:abcdef0123456789");

        Assert.Equal("mongo", result.Repository);
        Assert.Equal("8.0", result.Tag);
        Assert.Equal("sha256:abcdef0123456789", result.Digest);
    }

    // -----------------------------------------------------------------------
    // Degenerate input — documented decision: throw ArgumentException naming the
    // offending reference rather than return an ambiguous/best-effort result. The
    // caller is validating author YAML (`environment.dependencies[].image`) and wants
    // a loud, specific failure over a silently wrong image.
    // -----------------------------------------------------------------------

    [Fact]
    public void Parse_Null_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => ImageReferenceParser.Parse(null!));

        Assert.Equal("reference", ex.ParamName);
    }

    [Fact]
    public void Parse_Empty_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => ImageReferenceParser.Parse(string.Empty));

        Assert.Equal("reference", ex.ParamName);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Parse_Whitespace_ThrowsArgumentException(string reference)
    {
        var ex = Assert.Throws<ArgumentException>(() => ImageReferenceParser.Parse(reference));

        Assert.Equal("reference", ex.ParamName);
    }

    [Fact]
    public void Parse_TrailingColonNoTag_ThrowsArgumentExceptionNamingReference()
    {
        // "mongo:" — a bare trailing ':' with nothing after it is degenerate author
        // input, not an empty-but-valid tag.
        const string reference = "mongo:";

        var ex = Assert.Throws<ArgumentException>(() => ImageReferenceParser.Parse(reference));

        Assert.Equal("reference", ex.ParamName);
        Assert.Contains(reference, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_TrailingColonNoTagWithRegistryPort_ThrowsArgumentException()
    {
        // The trailing-colon check applies to the LAST segment specifically, not just
        // the end of the whole string: "nexus.corp.local:5000/platform/mongo:" has a
        // registry port earlier AND a trailing, tag-less colon in the last segment.
        const string reference = "nexus.corp.local:5000/platform/mongo:";

        var ex = Assert.Throws<ArgumentException>(() => ImageReferenceParser.Parse(reference));

        Assert.Equal("reference", ex.ParamName);
    }

    [Fact]
    public void Parse_TrailingAtNoDigest_ThrowsArgumentExceptionNamingReference()
    {
        // "mongo@" — a bare trailing '@' with nothing after it is degenerate author
        // input, not an empty-but-valid digest.
        const string reference = "mongo@";

        var ex = Assert.Throws<ArgumentException>(() => ImageReferenceParser.Parse(reference));

        Assert.Equal("reference", ex.ParamName);
        Assert.Contains(reference, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DigestOnlyNoRepository_ThrowsArgumentException()
    {
        // "@sha256:abc..." — the digest is well-formed, but there is no repository
        // name before the '@' at all.
        var ex = Assert.Throws<ArgumentException>(
            () => ImageReferenceParser.Parse("@sha256:abcdef0123456789"));

        Assert.Equal("reference", ex.ParamName);
    }

    [Fact]
    public void Parse_TagOnlyNoRepository_ThrowsArgumentException()
    {
        // ":8.0" — a leading colon with nothing before it in the last segment leaves
        // an empty repository, which is rejected the same way as the digest-only case.
        var ex = Assert.Throws<ArgumentException>(() => ImageReferenceParser.Parse(":8.0"));

        Assert.Equal("reference", ex.ParamName);
    }
}
