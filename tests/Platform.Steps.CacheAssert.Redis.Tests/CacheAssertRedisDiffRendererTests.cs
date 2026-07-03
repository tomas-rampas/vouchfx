// Tests for CacheAssertRedisProvider's IStepDiffRenderer implementation.
//
// The provider computes a faithful expected-vs-observed diff at RENDER time from a step's
// structured observation.  These tests exercise CanRender / RenderDiff directly against
// each Fail-observation JSON shape the provider's helper emits:
//   • {"value":{"expected":"<e>","actual":"<a>"}}   → value diff table
//   • {"exists":{"expected":<bool>,"actual":<bool>}} → exists diff table
//   • {"ttl":{"expected":<bool>,"actual":<bool>}}    → ttl diff table
//   • {"length":{"expected":<n>,"actual":<m>}}       → length diff table
//   • {"matched":true,"op":"get"}   (pass shape)     → CanRender false
//   • {"error":"…"}                 (env-error shape)→ CanRender false
//
// All tests are non-docker.  No topology is started.
using System.Text.Json;
using Platform.Sdk;
using Platform.Steps.CacheAssert.Redis;
using Xunit;

namespace Platform.Steps.CacheAssert.Redis.Tests;

/// <summary>
/// Non-docker unit tests for the <see cref="IStepDiffRenderer"/> implementation on
/// <see cref="CacheAssertRedisProvider"/>.
/// </summary>
public sealed class CacheAssertRedisDiffRendererTests
{
    private readonly CacheAssertRedisProvider _provider = new();

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ── The provider implements the optional contract ──────────────────────────

    [Fact]
    public void Provider_Implements_IStepDiffRenderer()
    {
        Assert.IsAssignableFrom<IStepDiffRenderer>(_provider);
    }

    // ── Value-mismatch observation ─────────────────────────────────────────────

    [Fact]
    public void CanRender_ValueObservation_IsTrue()
    {
        var obs = Parse("""{"value":{"expected":"active","actual":"inactive"}}""");
        Assert.True(_provider.CanRender(obs));
    }

    [Fact]
    public void RenderDiff_ValueObservation_ContainsExpectedActual()
    {
        var obs = Parse("""{"value":{"expected":"active","actual":"inactive"}}""");

        var diff = _provider.RenderDiff(obs);

        Assert.NotNull(diff);
        Assert.Contains("value", diff!, StringComparison.Ordinal);
        Assert.Contains("expected", diff, StringComparison.Ordinal);
        Assert.Contains("actual", diff, StringComparison.Ordinal);
        Assert.Contains("active", diff, StringComparison.Ordinal);
        Assert.Contains("inactive", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderDiff_ValueObservationWithNullActual_ShowsNull()
    {
        var obs = Parse("""{"value":{"expected":"active","actual":null}}""");

        Assert.True(_provider.CanRender(obs));
        var diff = _provider.RenderDiff(obs);

        Assert.NotNull(diff);
        Assert.Contains("null", diff!, StringComparison.Ordinal);
    }

    // ── Exists-mismatch observation ────────────────────────────────────────────

    [Fact]
    public void CanRender_ExistsObservation_IsTrue()
    {
        var obs = Parse("""{"exists":{"expected":true,"actual":false}}""");
        Assert.True(_provider.CanRender(obs));
    }

    [Fact]
    public void RenderDiff_ExistsObservation_ContainsBooleans()
    {
        var obs = Parse("""{"exists":{"expected":true,"actual":false}}""");

        var diff = _provider.RenderDiff(obs);

        Assert.NotNull(diff);
        Assert.Contains("exists", diff!, StringComparison.Ordinal);
        Assert.Contains("true", diff, StringComparison.Ordinal);
        Assert.Contains("false", diff, StringComparison.Ordinal);
    }

    // ── TTL-mismatch observation ───────────────────────────────────────────────

    [Fact]
    public void CanRender_TtlObservation_IsTrue()
    {
        var obs = Parse("""{"ttl":{"expected":true,"actual":false}}""");

        Assert.True(_provider.CanRender(obs));
        var diff = _provider.RenderDiff(obs);
        Assert.NotNull(diff);
        Assert.Contains("ttl", diff!, StringComparison.Ordinal);
    }

    // ── Length-mismatch observation ────────────────────────────────────────────

    [Fact]
    public void CanRender_LengthObservation_IsTrue()
    {
        var obs = Parse("""{"length":{"expected":3,"actual":7}}""");
        Assert.True(_provider.CanRender(obs));
    }

    [Fact]
    public void RenderDiff_LengthObservation_ContainsCounts()
    {
        var obs = Parse("""{"length":{"expected":3,"actual":7}}""");

        var diff = _provider.RenderDiff(obs);

        Assert.NotNull(diff);
        Assert.Contains("length", diff!, StringComparison.Ordinal);
        Assert.Contains("3", diff, StringComparison.Ordinal);
        Assert.Contains("7", diff, StringComparison.Ordinal);
    }

    // ── Pass / non-diff shapes ─────────────────────────────────────────────────

    [Fact]
    public void CanRender_PassObservation_IsFalse()
    {
        var obs = Parse("""{"matched":true,"op":"get"}""");

        Assert.False(_provider.CanRender(obs));
        Assert.Null(_provider.RenderDiff(obs));
    }

    [Fact]
    public void CanRender_ErrorObservation_IsFalse()
    {
        var obs = Parse("""{"error":"No connection is available"}""");

        Assert.False(_provider.CanRender(obs));
        Assert.Null(_provider.RenderDiff(obs));
    }

    [Fact]
    public void CanRender_UnrelatedObservation_IsFalse()
    {
        var obs = Parse("""{"foo":"bar"}""");

        Assert.False(_provider.CanRender(obs));
        Assert.Null(_provider.RenderDiff(obs));
    }

    [Fact]
    public void CanRender_NonObservationValue_IsFalseAndDoesNotThrow()
    {
        var obs = Parse("""[1,2,3]""");

        var ex = Record.Exception(() =>
        {
            Assert.False(_provider.CanRender(obs));
            Assert.Null(_provider.RenderDiff(obs));
        });

        Assert.Null(ex);
    }

    // ── A scalar-valued key (not a diff object) is not a diff ──────────────────

    [Fact]
    public void CanRender_ScalarKeyedObservation_IsFalse()
    {
        // "value" present but scalar (not an {expected,actual} object) → not renderable.
        var obs = Parse("""{"value":"active"}""");

        Assert.False(_provider.CanRender(obs));
        Assert.Null(_provider.RenderDiff(obs));
    }
}
