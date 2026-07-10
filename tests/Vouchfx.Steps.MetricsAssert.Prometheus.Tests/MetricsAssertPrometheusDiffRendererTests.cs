// Tests for MetricsAssertPrometheusProvider's IStepDiffRenderer implementation.
//
// The provider computes a faithful expected-vs-observed diff at RENDER time from a
// step's structured observation.  These tests exercise CanRender / RenderDiff directly
// against each Fail-observation JSON shape the provider's helper emits:
//   • {"value":{"expected":<n>,"actual":<m>}} → value diff table
//   • {"min":{"expected":<n>,"actual":<m>}}    → min diff table
//   • {"max":{"expected":<n>,"actual":<m>}}    → max diff table
//   • {"found":false,...}          (no-match shape)   → CanRender false
//   • {"ambiguous":true,...}       (ambiguous shape)  → CanRender false
//   • {"status":404}               (non-200 shape)    → CanRender false
//   • {"metric":...,"value":...}   (pass shape)       → CanRender false
//   • {"error":"…"}                (env-error shape)  → CanRender false
//
// All tests are non-docker.  No topology is started.
using System.Text.Json;
using Vouchfx.Sdk;
using Vouchfx.Steps.MetricsAssert.Prometheus;
using Xunit;

namespace Vouchfx.Steps.MetricsAssert.Prometheus.Tests;

/// <summary>
/// Non-docker unit tests for the <see cref="IStepDiffRenderer"/> implementation on
/// <see cref="MetricsAssertPrometheusProvider"/>.
/// </summary>
public sealed class MetricsAssertPrometheusDiffRendererTests
{
    private readonly MetricsAssertPrometheusProvider _provider = new();

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

    // ── Value-mismatch observation ─────────────────────────────────────────────

    [Fact]
    public void CanRender_ValueObservation_IsTrue()
    {
        var obs = Parse("""{"value":{"expected":1,"actual":5}}""");
        Assert.True(_provider.CanRender(obs));
    }

    [Fact]
    public void RenderDiff_ValueObservation_ContainsExpectedActual()
    {
        var obs = Parse("""{"value":{"expected":1,"actual":5}}""");

        var diff = _provider.RenderDiff(obs);

        Assert.NotNull(diff);
        Assert.Contains("value", diff!, StringComparison.Ordinal);
        Assert.Contains("expected", diff, StringComparison.Ordinal);
        Assert.Contains("actual", diff, StringComparison.Ordinal);
        Assert.Contains("1", diff, StringComparison.Ordinal);
        Assert.Contains("5", diff, StringComparison.Ordinal);
    }

    // ── Min-mismatch observation ───────────────────────────────────────────────

    [Fact]
    public void CanRender_MinObservation_IsTrue()
    {
        var obs = Parse("""{"min":{"expected":10,"actual":5}}""");

        Assert.True(_provider.CanRender(obs));
        var diff = _provider.RenderDiff(obs);
        Assert.NotNull(diff);
        Assert.Contains("min", diff!, StringComparison.Ordinal);
    }

    // ── Max-mismatch observation ───────────────────────────────────────────────

    [Fact]
    public void CanRender_MaxObservation_IsTrue()
    {
        var obs = Parse("""{"max":{"expected":10,"actual":15}}""");

        Assert.True(_provider.CanRender(obs));
        var diff = _provider.RenderDiff(obs);
        Assert.NotNull(diff);
        Assert.Contains("max", diff!, StringComparison.Ordinal);
    }

    // ── Non-diff shapes ────────────────────────────────────────────────────────

    [Fact]
    public void CanRender_NotFoundObservation_IsFalse()
    {
        var obs = Parse("""{"found":false,"metric":"orders_total","labelsProvided":0}""");

        Assert.False(_provider.CanRender(obs));
        Assert.Null(_provider.RenderDiff(obs));
    }

    [Fact]
    public void CanRender_AmbiguousObservation_IsFalse()
    {
        var obs = Parse("""{"ambiguous":true,"metric":"orders_total","count":2}""");

        Assert.False(_provider.CanRender(obs));
        Assert.Null(_provider.RenderDiff(obs));
    }

    [Fact]
    public void CanRender_StatusObservation_IsFalse()
    {
        var obs = Parse("""{"status":404}""");

        Assert.False(_provider.CanRender(obs));
        Assert.Null(_provider.RenderDiff(obs));
    }

    [Fact]
    public void CanRender_PassObservation_IsFalse()
    {
        var obs = Parse("""{"metric":"orders_total","value":42.5,"labels":{"status":"ok"}}""");

        Assert.False(_provider.CanRender(obs));
        Assert.Null(_provider.RenderDiff(obs));
    }

    [Fact]
    public void CanRender_ErrorObservation_IsFalse()
    {
        var obs = Parse("""{"error":"Invalid URI: The URI is empty."}""");

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

    [Fact]
    public void CanRender_ScalarKeyedObservation_IsFalse()
    {
        // "value" present but scalar (not an {expected,actual} object) → not renderable.
        var obs = Parse("""{"value":42.5}""");

        Assert.False(_provider.CanRender(obs));
        Assert.Null(_provider.RenderDiff(obs));
    }
}
