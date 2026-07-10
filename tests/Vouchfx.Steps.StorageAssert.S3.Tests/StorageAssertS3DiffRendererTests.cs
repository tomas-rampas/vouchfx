// Tests for StorageAssertS3Provider's IStepDiffRenderer implementation.
//
// The provider computes a faithful expected-vs-observed diff at RENDER time from a step's
// structured observation. These tests exercise CanRender / RenderDiff directly against each
// Fail-observation JSON shape the provider's helper emits:
//   • {"field":"<name>","expected":"<e>","actual":"<a>"}   → field diff table (size/minSize/
//     contentType/metadata.*/sha256 mismatches all share this shape).
//   • {"exists":false} / {"exists":true}                    → CanRender false (no field/expected/actual).
//   • {"field":"contentContains","matched":false,"bodyLength":N} → CanRender false (no "expected"/"actual").
//   • {"field":"bodyCap","capBytes":N,"actualBytes":M}       → CanRender false.
//   • {"error":"<type>"}                                    → CanRender false.
//
// All tests are non-docker.
using System.Text.Json;
using Vouchfx.Sdk;
using Vouchfx.Steps.StorageAssert.S3;
using Xunit;

namespace Vouchfx.Steps.StorageAssert.S3.Tests;

public sealed class StorageAssertS3DiffRendererTests
{
    private readonly StorageAssertS3Provider _provider = new();

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Provider_Implements_IStepDiffRenderer()
    {
        Assert.IsAssignableFrom<IStepDiffRenderer>(_provider);
    }

    [Fact]
    public void CanRender_SizeFieldObservation_IsTrue()
    {
        var obs = Parse("""{"field":"size","expected":"128","actual":"64"}""");
        Assert.True(_provider.CanRender(obs));
    }

    [Fact]
    public void RenderDiff_SizeFieldObservation_ContainsFieldAndValues()
    {
        var obs = Parse("""{"field":"size","expected":"128","actual":"64"}""");
        var diff = _provider.RenderDiff(obs);

        Assert.NotNull(diff);
        Assert.Contains("size", diff, StringComparison.Ordinal);
        Assert.Contains("128", diff, StringComparison.Ordinal);
        Assert.Contains("64", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void CanRender_MetadataFieldObservation_IsTrue()
    {
        var obs = Parse("""{"field":"metadata.source","expected":"orders-api","actual":"null"}""");
        Assert.True(_provider.CanRender(obs));
    }

    [Fact]
    public void CanRender_Sha256FieldObservation_IsTrue()
    {
        var obs = Parse("""{"field":"sha256","expected":"aaaa","actual":"bbbb"}""");
        Assert.True(_provider.CanRender(obs));
    }

    [Theory]
    [InlineData("""{"exists":false}""")]
    [InlineData("""{"exists":true}""")]
    [InlineData("""{"field":"contentContains","matched":false,"bodyLength":42}""")]
    [InlineData("""{"field":"bodyCap","capBytes":16777216,"actualBytes":20000000}""")]
    [InlineData("""{"error":"HttpRequestException"}""")]
    public void CanRender_NonFieldDiffShapes_IsFalse(string json)
    {
        var obs = Parse(json);
        Assert.False(_provider.CanRender(obs));
        Assert.Null(_provider.RenderDiff(obs));
    }
}
