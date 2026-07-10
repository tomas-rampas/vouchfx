// Tests for DbAssertMysqlProvider's IStepDiffRenderer implementation (S07-G-01).
//
// The provider computes a faithful expected-vs-observed diff at RENDER time from a
// step's structured observation.  These tests exercise CanRender / RenderDiff directly
// against each Fail-observation JSON shape the provider's helper emits:
//   • {"column":"<name>","expected":"<e>","actual":"<a>"}      → column diff table
//   • {"rowCount":{"expected":<n>,"actual":<m>}}               → rowCount diff table
//   • {"rowCount":<n>}            (pass shape)                  → CanRender false
//
// All tests are non-docker.  No topology is started.
using System.Text.Json;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Mysql;
using Xunit;

namespace Vouchfx.Steps.DbAssert.Mysql.Tests;

/// <summary>
/// Non-docker unit tests for the <see cref="IStepDiffRenderer"/> implementation on
/// <see cref="DbAssertMysqlProvider"/>.
/// </summary>
public sealed class DbAssertMysqlDiffRendererTests
{
    private readonly DbAssertMysqlProvider _provider = new();

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

    // ── Column-mismatch observation ────────────────────────────────────────────

    /// <summary>
    /// A column-mismatch observation is renderable.
    /// </summary>
    [Fact]
    public void CanRender_ColumnObservation_IsTrue()
    {
        var obs = Parse("""{"column":"status","expected":"SHIPPED","actual":"PENDING"}""");
        Assert.True(_provider.CanRender(obs));
    }

    /// <summary>
    /// Rendering a column-mismatch observation produces a relational table that names
    /// the column and contains both the expected and the actual values.
    /// </summary>
    [Fact]
    public void RenderDiff_ColumnObservation_ContainsColumnExpectedActual()
    {
        var obs = Parse("""{"column":"status","expected":"SHIPPED","actual":"PENDING"}""");

        var diff = _provider.RenderDiff(obs);

        Assert.NotNull(diff);
        Assert.Contains("column", diff!, StringComparison.Ordinal);
        Assert.Contains("expected", diff, StringComparison.Ordinal);
        Assert.Contains("actual", diff, StringComparison.Ordinal);
        Assert.Contains("status", diff, StringComparison.Ordinal);
        Assert.Contains("SHIPPED", diff, StringComparison.Ordinal);
        Assert.Contains("PENDING", diff, StringComparison.Ordinal);
    }

    // ── Row-count-mismatch observation ─────────────────────────────────────────

    /// <summary>
    /// A row-count-mismatch observation (object-valued rowCount) is renderable.
    /// </summary>
    [Fact]
    public void CanRender_RowCountObjectObservation_IsTrue()
    {
        var obs = Parse("""{"rowCount":{"expected":1,"actual":0}}""");
        Assert.True(_provider.CanRender(obs));
    }

    /// <summary>
    /// Rendering a row-count-mismatch observation produces a table containing both the
    /// expected and the actual counts.
    /// </summary>
    [Fact]
    public void RenderDiff_RowCountObjectObservation_ContainsExpectedActualCounts()
    {
        var obs = Parse("""{"rowCount":{"expected":3,"actual":7}}""");

        var diff = _provider.RenderDiff(obs);

        Assert.NotNull(diff);
        Assert.Contains("rowCount", diff!, StringComparison.Ordinal);
        Assert.Contains("expected", diff, StringComparison.Ordinal);
        Assert.Contains("actual", diff, StringComparison.Ordinal);
        Assert.Contains("3", diff, StringComparison.Ordinal);
        Assert.Contains("7", diff, StringComparison.Ordinal);
    }

    // ── Pass / non-diff shapes ─────────────────────────────────────────────────

    /// <summary>
    /// The pass-shape observation (scalar rowCount) is NOT a diff: CanRender is false
    /// and RenderDiff returns null.
    /// </summary>
    [Fact]
    public void CanRender_ScalarRowCountObservation_IsFalse()
    {
        var obs = Parse("""{"rowCount":5}""");

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
