// #558 — HttpRestProvider's IStepDiffRenderer: the expected-vs-observed row for a failing
// response-body assertion.
//
// The renderer recognises ONLY the `body` member the body assertions add, so every observation
// http.rest emitted before #558 still renders as it did — with no diff. The actual column is
// never response text (the observation carries none): it names a JSON kind, a node count or a
// fixed phrase.
using System.Text.Json;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// #558: <see cref="HttpRestProvider"/> as an <see cref="IStepDiffRenderer"/>.
/// </summary>
public sealed class HttpRestDiffRendererTests
{
    private static readonly HttpRestProvider s_renderer = new();

    /// <summary>
    /// The provider IS a diff renderer — the optional v1 interface the engine's render-time
    /// lookup discovers by type.
    /// </summary>
    [Fact]
    public void Provider_ImplementsTheOptionalDiffRendererInterface()
    {
        Assert.IsAssignableFrom<IStepDiffRenderer>(new HttpRestProvider());
    }

    /// <summary>
    /// Every observation shape http.rest emitted before #558 — pass, status mismatch, capture
    /// miss, timeout, transport and secret errors — plus the new unset-placeholder shape and
    /// non-object JSON, is NOT renderable, so each renders exactly as before.
    /// </summary>
    [Theory]
    [InlineData("{\"status\":200,\"expected\":200}")]
    [InlineData("{\"status\":404,\"expected\":200}")]
    [InlineData("{\"status\":500,\"expected\":null}")]
    [InlineData("{\"captureUnmet\":\"orderId\"}")]
    [InlineData("{\"placeholderUnmet\":\"orderId\"}")]
    [InlineData("{\"timeout\":true}")]
    [InlineData("{\"error\":\"Connection refused\"}")]
    [InlineData("{\"secretError\":\"secret resolution failed\",\"source\":\"env\",\"path\":\"X\"}")]
    [InlineData("{\"reason\":\"retry-timeout\",\"attempts\":4}")]
    [InlineData("{\"status\":200,\"expected\":200,\"body\":\"not an object\"}")]
    [InlineData("{\"status\":200,\"expected\":200,\"body\":{\"failed\":1,\"of\":1}}")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    public void CanRender_IsFalse_ForEveryShapeThatIsNotABodyFailure(string observation)
    {
        var element = Parse(observation);

        Assert.False(s_renderer.CanRender(element));
        Assert.Null(s_renderer.RenderDiff(element));
    }

    /// <summary>
    /// A mismatch renders one box-drawn row: the assertion with its path, the author's
    /// expected text, and the KIND of the node that was found.
    /// </summary>
    [Fact]
    public void RenderDiff_Mismatch_RendersPathExpectedAndKind()
    {
        var element = Parse(
            "{\"status\":200,\"expected\":200,\"body\":{\"failed\":1,\"of\":1,\"first\":{\"assertion\":\"json\"," +
            "\"path\":\"$.status\",\"reason\":\"mismatch\",\"expected\":\"SHIPPED\",\"actualKind\":\"string\"}}}");

        Assert.True(s_renderer.CanRender(element));
        var diff = s_renderer.RenderDiff(element);

        Assert.Equal(
            " assertion     │ expected │ actual   \n" +
            "───────────────┼──────────┼──────────\n" +
            " json $.status │ SHIPPED  │ (string) \n",
            diff);
    }

    /// <summary>
    /// Each reason renders its own fixed actual-column phrase, and the exists forms render
    /// their claim as (present) / (absent) rather than an expected value.
    /// </summary>
    [Theory]
    [InlineData("{\"assertion\":\"json\",\"path\":\"$.id\",\"reason\":\"missing\",\"exists\":true}", "(present)", "(no node)")]
    [InlineData("{\"assertion\":\"json\",\"path\":\"$..pw\",\"reason\":\"present\",\"exists\":false,\"count\":1}", "(absent)", "(1 node)")]
    [InlineData("{\"assertion\":\"json\",\"path\":\"$..pw\",\"reason\":\"present\",\"exists\":false,\"count\":3}", "(absent)", "(3 nodes)")]
    [InlineData("{\"assertion\":\"json\",\"path\":\"$.a[*]\",\"reason\":\"multipleNodes\",\"expected\":\"x\",\"count\":2}", "x", "(2 nodes)")]
    [InlineData("{\"assertion\":\"json\",\"path\":\"$.id\",\"reason\":\"missing\",\"expected\":\"x\"}", "x", "(no node)")]
    [InlineData("{\"assertion\":\"json\",\"path\":\"$.id\",\"reason\":\"notJson\",\"expected\":\"x\"}", "x", "(body is not JSON)")]
    [InlineData("{\"assertion\":\"json\",\"path\":\"$[?@.n > 1]\",\"reason\":\"unevaluable\",\"exists\":true}", "(present)", "(path could not be evaluated)")]
    [InlineData("{\"assertion\":\"bodyContains\",\"reason\":\"notFound\",\"expected\":\"Hostname: {hostname}\"}", "Hostname: {hostname}", "(not found)")]
    [InlineData("{\"assertion\":\"json\",\"path\":\"$.id\",\"reason\":\"somethingNew\",\"expected\":\"x\"}", "x", "(somethingNew)")]
    public void RenderDiff_EachReason_RendersItsOwnCells(string first, string expected, string actual)
    {
        var diff = s_renderer.RenderDiff(Parse(
            "{\"status\":200,\"expected\":200,\"body\":{\"failed\":1,\"of\":1,\"first\":" + first + "}}"));

        Assert.NotNull(diff);
        var row = diff!.Split('\n')[2];
        var cells = row.Split('│').Select(c => c.Trim()).ToArray();
        Assert.Equal(3, cells.Length);
        Assert.Equal(expected, cells[1]);
        Assert.Equal(actual, cells[2]);
    }

    /// <summary>
    /// The assertion cell names the JSONPath for a json entry and the key alone for
    /// bodyContains.
    /// </summary>
    [Fact]
    public void RenderDiff_BodyContains_NamesTheAssertionAlone()
    {
        var diff = s_renderer.RenderDiff(Parse(
            "{\"status\":200,\"expected\":200,\"body\":{\"failed\":1,\"of\":1,\"first\":" +
            "{\"assertion\":\"bodyContains\",\"reason\":\"notFound\",\"expected\":\"abc\"}}}"));

        Assert.StartsWith(" bodyContains │", diff!.Split('\n')[2], StringComparison.Ordinal);
    }

    /// <summary>
    /// Further failing assertions are counted on one line under the table, in the singular
    /// or the plural.
    /// </summary>
    [Theory]
    [InlineData(1, null)]
    [InlineData(2, "(+1 more failing body assertion)")]
    [InlineData(3, "(+2 more failing body assertions)")]
    public void RenderDiff_CountsFurtherFailures(int failed, string? line)
    {
        var diff = s_renderer.RenderDiff(Parse(
            "{\"status\":200,\"expected\":200,\"body\":{\"failed\":" + failed + ",\"of\":3,\"first\":" +
            "{\"assertion\":\"json\",\"path\":\"$.a\",\"reason\":\"missing\",\"expected\":\"x\"}}}"));

        var lines = diff!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (line is null)
            Assert.Equal(3, lines.Length);
        else
            Assert.Equal(line, lines[3]);
    }

    /// <summary>
    /// A mismatch on a number or a boolean adds the fixed spelling note — the one trap a
    /// kind-only diff would otherwise hide — and a string mismatch does not.
    /// </summary>
    [Theory]
    [InlineData("number", true)]
    [InlineData("boolean", true)]
    [InlineData("string", false)]
    [InlineData("object", false)]
    public void RenderDiff_NumberOrBooleanMismatch_AddsTheSpellingNote(string kind, bool noted)
    {
        var diff = s_renderer.RenderDiff(Parse(
            "{\"status\":200,\"expected\":200,\"body\":{\"failed\":1,\"of\":1,\"first\":" +
            "{\"assertion\":\"json\",\"path\":\"$.n\",\"reason\":\"mismatch\",\"expected\":\"2\",\"actualKind\":\"" + kind + "\"}}}"));

        Assert.Equal(noted, diff!.Contains("exact JSON spelling", StringComparison.Ordinal));
    }

    /// <summary>
    /// A line break or tab in author text stays inside its cell — escaped, so the table keeps
    /// exactly one value row.
    /// </summary>
    [Fact]
    public void RenderDiff_LineBreakInAuthorText_StaysOnOneRow()
    {
        var diff = s_renderer.RenderDiff(Parse(
            "{\"status\":200,\"expected\":200,\"body\":{\"failed\":1,\"of\":1,\"first\":" +
            "{\"assertion\":\"bodyContains\",\"reason\":\"notFound\",\"expected\":\"line one\\nline\\ttwo\"}}}"));

        var lines = diff!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Contains("line one\\nline\\ttwo", lines[2], StringComparison.Ordinal);
    }

    /// <summary>
    /// Fields the renderer does not know are tolerated (§14: renderers tolerate unknown
    /// fields), and a <c>body</c> without the counters still renders its row.
    /// </summary>
    [Fact]
    public void RenderDiff_ToleratesUnknownAndMissingFields()
    {
        var diff = s_renderer.RenderDiff(Parse(
            "{\"status\":200,\"expected\":200,\"future\":1,\"body\":{\"first\":" +
            "{\"assertion\":\"json\",\"path\":\"$.a\",\"reason\":\"missing\",\"expected\":\"x\",\"extra\":[1]}}}"));

        Assert.NotNull(diff);
        Assert.Equal(3, diff!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
