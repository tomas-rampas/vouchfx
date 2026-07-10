// Tests for DbAssertDynamodbProvider's IStepDiffRenderer implementation.
//
// The provider computes a faithful expected-vs-observed diff at RENDER time from a step's
// structured observation. These tests exercise CanRender / RenderDiff directly against each
// Fail-observation JSON shape the provider's helper emits:
//   • {"field":"<name>","expected":"<e>","actual":"<a>"}   → field diff table
//   • {"exists":false}                        (Fail shape)  → CanRender false
//   • {"exists":true}                                        → CanRender false
//   • {"error":"<message>"}                                  → CanRender false
//
// All tests are non-docker.
using System.Text.Json;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Dynamodb;
using Xunit;

namespace Vouchfx.Steps.DbAssert.Dynamodb.Tests;

public sealed class DbAssertDynamodbDiffRendererTests
{
    private readonly DbAssertDynamodbProvider _provider = new();

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
    public void CanRender_FieldObservation_IsTrue()
    {
        var obs = Parse("""{"field":"status","expected":"SHIPPED","actual":"PENDING"}""");
        Assert.True(_provider.CanRender(obs));
    }

    [Fact]
    public void RenderDiff_FieldObservation_ContainsFieldAndValues()
    {
        var obs = Parse("""{"field":"status","expected":"SHIPPED","actual":"PENDING"}""");
        var diff = _provider.RenderDiff(obs);

        Assert.NotNull(diff);
        Assert.Contains("status", diff, StringComparison.Ordinal);
        Assert.Contains("SHIPPED", diff, StringComparison.Ordinal);
        Assert.Contains("PENDING", diff, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"exists":false}""")]
    [InlineData("""{"exists":true}""")]
    [InlineData("""{"error":"connection string not found for key 'conn::orders'"}""")]
    public void CanRender_NonFieldShapes_IsFalse(string json)
    {
        var obs = Parse(json);
        Assert.False(_provider.CanRender(obs));
        Assert.Null(_provider.RenderDiff(obs));
    }
}
