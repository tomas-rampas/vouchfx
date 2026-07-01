// Tests for DbAssertMongodbProvider's IStepDiffRenderer implementation.
//
// The provider computes a faithful expected-vs-observed diff at RENDER time from a
// step's structured observation.  These tests exercise CanRender / RenderDiff directly
// against each Fail-observation JSON shape the provider's helper emits:
//   • {"field":"<name>","expected":"<e>","actual":"<a>"}      → field diff table
//   • {"count":{"expected":<n>,"actual":<m>}}                 → count diff table
//   • {"count":<n>}              (pass shape)                  → CanRender false
//   • {"error":"<message>"}                                    → CanRender false
//
// All tests are non-docker.  No topology is started.
using System.Text.Json;
using Platform.Sdk;
using Platform.Steps.DbAssert.Mongodb;
using Xunit;

namespace Platform.Steps.DbAssert.Mongodb.Tests;

/// <summary>
/// Non-docker unit tests for the <see cref="IStepDiffRenderer"/> implementation on
/// <see cref="DbAssertMongodbProvider"/>.
/// </summary>
public sealed class DbAssertMongodbDiffRendererTests
{
    private readonly DbAssertMongodbProvider _provider = new();

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ── The provider implements the optional contract ──────────────────────────

    /// <summary>
    /// The provider must implement <see cref="IStepDiffRenderer"/> so the renderer's
    /// diff-lookup delegate can resolve it from the registry.
    /// </summary>
    [Fact]
    public void Provider_Implements_IStepDiffRenderer()
    {
        Assert.IsAssignableFrom<IStepDiffRenderer>(_provider);
    }

    // ── Field-mismatch observation ────────────────────────────────────────────────

    /// <summary>
    /// A field-mismatch observation is renderable.
    /// </summary>
    [Fact]
    public void CanRender_FieldObservation_IsTrue()
    {
        var obs = Parse("""{"field":"status","expected":"SHIPPED","actual":"PENDING"}""");
        Assert.True(_provider.CanRender(obs));
    }

    /// <summary>
    /// Rendering a field-mismatch observation produces a relational table that names
    /// the field and contains both the expected and the actual values.
    /// </summary>
    [Fact]
    public void RenderDiff_FieldObservation_ContainsFieldExpectedActual()
    {
        var obs = Parse("""{"field":"status","expected":"SHIPPED","actual":"PENDING"}""");

        var diff = _provider.RenderDiff(obs);

        Assert.NotNull(diff);
        Assert.Contains("field", diff!, StringComparison.Ordinal);
        Assert.Contains("expected", diff, StringComparison.Ordinal);
        Assert.Contains("actual", diff, StringComparison.Ordinal);
        Assert.Contains("status", diff, StringComparison.Ordinal);
        Assert.Contains("SHIPPED", diff, StringComparison.Ordinal);
        Assert.Contains("PENDING", diff, StringComparison.Ordinal);
    }

    // ── Count-mismatch observation ────────────────────────────────────────────────

    /// <summary>
    /// A count-mismatch observation (object-valued count) is renderable.
    /// </summary>
    [Fact]
    public void CanRender_CountObjectObservation_IsTrue()
    {
        var obs = Parse("""{"count":{"expected":1,"actual":0}}""");
        Assert.True(_provider.CanRender(obs));
    }

    /// <summary>
    /// Rendering a count-mismatch observation produces a table containing both the
    /// expected and the actual counts.
    /// </summary>
    [Fact]
    public void RenderDiff_CountObjectObservation_ContainsExpectedActualCounts()
    {
        var obs = Parse("""{"count":{"expected":3,"actual":7}}""");

        var diff = _provider.RenderDiff(obs);

        Assert.NotNull(diff);
        Assert.Contains("count", diff!, StringComparison.Ordinal);
        Assert.Contains("expected", diff, StringComparison.Ordinal);
        Assert.Contains("actual", diff, StringComparison.Ordinal);
        Assert.Contains("3", diff, StringComparison.Ordinal);
        Assert.Contains("7", diff, StringComparison.Ordinal);
    }

    // ── Pass / non-diff shapes ─────────────────────────────────────────────────────

    /// <summary>
    /// The pass-shape observation (scalar count) is NOT a diff: CanRender is false
    /// and RenderDiff returns null.
    /// </summary>
    [Fact]
    public void CanRender_ScalarCountObservation_IsFalse()
    {
        var obs = Parse("""{"count":5}""");

        Assert.False(_provider.CanRender(obs));
        Assert.Null(_provider.RenderDiff(obs));
    }

    /// <summary>
    /// An EnvironmentError observation ({"error":...}) is not a diff shape.
    /// </summary>
    [Fact]
    public void CanRender_ErrorObservation_IsFalse()
    {
        var obs = Parse("""{"error":"connection refused"}""");

        Assert.False(_provider.CanRender(obs));
        Assert.Null(_provider.RenderDiff(obs));
    }

    /// <summary>
    /// A wholly unrelated JSON object is not renderable.
    /// </summary>
    [Fact]
    public void CanRender_UnrelatedObservation_IsFalse()
    {
        var obs = Parse("""{"foo":"bar"}""");

        Assert.False(_provider.CanRender(obs));
        Assert.Null(_provider.RenderDiff(obs));
    }

    /// <summary>
    /// A non-object JSON value (e.g. a bare array) is not renderable and never throws.
    /// </summary>
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
}
