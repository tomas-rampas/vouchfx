using System.Linq;
using System.Text.Json;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Schema validation types a plain scalar from its text; a tagged plain scalar whose text
/// reads as a number or a boolean is typed from that text alone, except that a <c>!!str</c>
/// tag gives a string. Text that .NET cannot turn into a finite number, or a tagged
/// value (plain, quoted or block) that its tag cannot hold, is accepted as the string
/// written rather than stopping the whole suite with a generic parse failure.
/// </summary>
public sealed class SchemaYamlScalarTypingTests
{
    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(new[] { typeof(HttpRestProvider).Assembly });

    private static string StepWithValue(string value) =>
        "steps:\n"
        + "  - id: typed\n"
        + "    type: http.rest\n"
        + "    target: svc\n"
        + "    method: POST\n"
        + "    path: /x\n"
        + "    body:\n"
        + "      v: " + value + "\n";

    private static JsonElement ConvertedValue(string value)
    {
        using var json = SchemaResources.ConvertYamlToJsonDocument(StepWithValue(value));
        return json.RootElement.GetProperty("steps")[0].GetProperty("body").GetProperty("v").Clone();
    }

    /// <summary>A non-finite plain number, or an explicit string tag, validates as text.</summary>
    [Theory]
    [InlineData("NaN")]
    [InlineData("nan")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("INFINITY")]
    [InlineData("!!str NaN")]
    [InlineData("1e400")]
    public void NonFiniteOrStringTaggedValue_InBody_ValidatesAsText(string value)
    {
        var result = DocumentValidator.Validate(StepWithValue(value), s_registry);

        Assert.True(result.IsValid,
            string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}")));
    }

    /// <summary>The same values reach the schema as JSON strings, not numbers.</summary>
    [Theory]
    [InlineData("NaN")]
    [InlineData("-Infinity")]
    [InlineData("INFINITY")]
    [InlineData("1e400")]
    public void NonFinitePlainValue_ConvertsToString(string value)
    {
        var converted = ConvertedValue(value);

        Assert.Equal(JsonValueKind.String, converted.ValueKind);
        Assert.Equal(value, converted.GetString());
    }

    /// <summary>
    /// A core <c>!!int</c>, <c>!!float</c> or <c>!!bool</c> tag on a value YamlDotNet cannot
    /// read as that type, quoted or not, is accepted as the text written.
    /// </summary>
    [Theory]
    [InlineData("!!int 0o17")]
    [InlineData("!!int abc")]
    [InlineData("!!float NaN")]
    [InlineData("!!bool maybe")]
    [InlineData("!!int \"abc\"")]
    [InlineData("!!int \"0o17\"")]
    [InlineData("!!float \"NaN\"")]
    [InlineData("!!bool 'maybe'")]
    public void TaggedValueThatDoesNotParse_ValidatesAsText(string value)
    {
        var result = DocumentValidator.Validate(StepWithValue(value), s_registry);

        Assert.True(result.IsValid,
            string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}")));
    }

    /// <summary>
    /// Parity matrix. Each row records what the bridge produced before the typing change
    /// (measured on the unmodified code): a scalar that parsed keeps its JSON value, and one
    /// that threw is now the text written (a core-tagged scalar, quoted or block included).
    /// The one deliberate change to a scalar that parsed is <c>!!str</c>, which now gives a
    /// string except on a null spelling. The JSON written for a non-finite float
    /// (<c>!!float .inf</c>) is the string YamlDotNet's JSON-compatible serialiser emits.
    /// </summary>
    [Theory]
    [InlineData("!!int 0x1F", "31")]
    [InlineData("!!int 0X1F", "\"0X1F\"")]
    [InlineData("!!int 0b101", "5")]
    [InlineData("!!int 1_000", "1000")]
    [InlineData("!!int +12", "12")]
    [InlineData("!!int -7", "-7")]
    [InlineData("!!int 0123", "83")]
    [InlineData("!!int 0o17", "\"0o17\"")]
    [InlineData("!!int abc", "\"abc\"")]
    [InlineData("!!int 0b", "\"0b\"")]
    [InlineData("!!int 0_", "\"0_\"")]
    [InlineData("!!map abc", "\"abc\"")]
    [InlineData("!!timestamp abc", "\"abc\"")]
    [InlineData("!!float 1.5", "1.5")]
    [InlineData("!!float .5", "0.5")]
    [InlineData("!!float 1e3", "1000")]
    [InlineData("!!float 1_000.5", "1000.5")]
    [InlineData("!!float .inf", "\"Infinity\"")]
    [InlineData("!!float -.inf", "\"-Infinity\"")]
    [InlineData("!!float .nan", "\"NaN\"")]
    [InlineData("!!float NaN", "\"NaN\"")]
    [InlineData("!!bool true", "true")]
    [InlineData("!!bool True", "true")]
    [InlineData("!!bool yes", "true")]
    [InlineData("!!bool no", "false")]
    [InlineData("!!bool on", "true")]
    [InlineData("!!bool off", "false")]
    [InlineData("!!bool y", "true")]
    [InlineData("!!bool n", "false")]
    [InlineData("!!bool maybe", "\"maybe\"")]
    [InlineData("0x1F", "\"0x1F\"")]
    [InlineData("yes", "\"yes\"")]
    [InlineData(".inf", "\".inf\"")]
    [InlineData("1_000", "\"1_000\"")]
    [InlineData("!!int 1.5", "1.5")]
    [InlineData("!!bool 1", "1")]
    [InlineData("!!int 0x1FFFFFFFFF", "\"0x1FFFFFFFFF\"")]
    [InlineData("!money 5", "5")]
    [InlineData("!money NaN", "\"NaN\"")]
    [InlineData("!foo 17", "17")]
    [InlineData("! 17", "17")]
    [InlineData("! NaN", "\"NaN\"")]
    [InlineData("!!timestamp 2001-01-01", "\"2001-01-01T00:00:00.0000000\"")]
    [InlineData("!!str True", "\"True\"")]
    [InlineData("!!str False", "\"False\"")]
    [InlineData("!!str true", "\"true\"")]
    [InlineData("!!str ~", "null")]
    [InlineData("!!str null", "null")]
    [InlineData("!!str", "null")]
    [InlineData("!!int \"abc\"", "\"abc\"")]
    [InlineData("!!int \"0o17\"", "\"0o17\"")]
    [InlineData("!!int ''", "\"\"")]
    [InlineData("!!float \"NaN\"", "\"NaN\"")]
    [InlineData("!!float 'abc'", "\"abc\"")]
    [InlineData("!!bool \"maybe\"", "\"maybe\"")]
    [InlineData("!!int \"17\"", "17")]
    [InlineData("!!int '0x1F'", "31")]
    [InlineData("!!float \"1.5\"", "1.5")]
    [InlineData("!!bool \"yes\"", "true")]
    [InlineData("!!str \"true\"", "\"true\"")]
    public void ScalarThatParsedBefore_KeepsItsJsonValue_AndThrowingOnesBecomeText(string value, string expectedJson)
    {
        var converted = ConvertedValue(value);

        Assert.Equal(expectedJson, converted.GetRawText());
    }

    /// <summary>
    /// The same rule in every other position a scalar can take: a mapping key, a sequence
    /// or flow item, and an anchor with its alias. Each row threw before (an unlocated parse
    /// failure) and is now the text written.
    /// </summary>
    [Theory]
    [InlineData("NaN: 1", "{\"NaN\":1}")]
    [InlineData("!!int 0o17: 1", "{\"0o17\":1}")]
    [InlineData("!!int abc: 1", "{\"abc\":1}")]
    [InlineData("1e400: x", "{\"1e400\":\"x\"}")]
    [InlineData("v: !!int |\n  0o17", "{\"v\":\"0o17\\n\"}")]
    [InlineData("v: !!int >\n  abc", "{\"v\":\"abc\\n\"}")]
    [InlineData("v: !!int |\n  17", "{\"v\":17}")]
    [InlineData("v: &a NaN\nw: *a", "{\"v\":\"NaN\",\"w\":\"NaN\"}")]
    [InlineData("v: &a !!int 0o17\nw: *a", "{\"v\":\"0o17\",\"w\":\"0o17\"}")]
    [InlineData("v: &a !!int \"abc\"\nw: *a", "{\"v\":\"abc\",\"w\":\"abc\"}")]
    [InlineData("v: [NaN, 1, !!int 0o17, !!str 5]", "{\"v\":[\"NaN\",1,\"0o17\",\"5\"]}")]
    [InlineData("v: { a: NaN }", "{\"v\":{\"a\":\"NaN\"}}")]
    public void ScalarInAnyPosition_BecomesTheTextWritten(string document, string expectedJson)
    {
        using var json = SchemaResources.ConvertYamlToJsonDocument(document + "\n");

        Assert.Equal(expectedJson, json.RootElement.GetRawText());
    }

    /// <summary>
    /// Only a scalar that threw changes. A tag the YAML library cannot resolve at all
    /// (<c>!money abc</c>, <c>!!null ~</c>, <c>!!binary</c>) is still refused, as before.
    /// </summary>
    [Theory]
    [InlineData("v: !money abc")]
    [InlineData("v: !money \"5\"")]
    [InlineData("v: !!null ~")]
    [InlineData("v: !!null abc")]
    [InlineData("v: !!binary aGk=")]
    public void UnresolvedTag_IsStillRefused(string document)
    {
        var failure = Assert.Throws<YamlDotNet.Core.YamlException>(
            () => SchemaResources.ConvertYamlToJsonDocument(document + "\n"));

        Assert.Contains("unresolved tag", failure.Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The text fallback covers only the conversion of the tagged scalar itself. Reading
    /// past that scalar scans the next token, and the YAML scanner throws an
    /// <see cref="System.ArgumentOutOfRangeException"/> (not a YAML syntax error) for a
    /// double-quoted lone surrogate escape. That failure belongs to the malformed text that
    /// follows, so the document is still refused, as it was before the fallback existed.
    /// </summary>
    [Theory]
    [InlineData("a: [!!int 0o17, \"\\uDC00\\uDC00, z]")]
    [InlineData("a: [!!int 5, \"\\uDC00\\uDC00, z]")]
    public void MalformedTokenAfterATaggedScalar_IsStillRefused(string document)
    {
        Assert.ThrowsAny<System.Exception>(
            () => SchemaResources.ConvertYamlToJsonDocument(document + "\n"));
    }

    [Fact]
    public void MalformedTokenAfterATaggedScalar_InABody_FailsValidation()
    {
        var result = DocumentValidator.Validate(StepWithValue("[!!int 0o17, \"\\uDC00\\uDC00, z]"), s_registry);

        Assert.False(result.IsValid);
    }

    /// <summary>
    /// A <c>!!str</c> tag on a number or boolean now gives a string, so a field that requires
    /// a boolean is refused, with its location, where it used to pass.
    /// </summary>
    [Fact]
    public void StringTaggedBoolean_InABooleanField_IsRefusedWithItsLocation()
    {
        var yaml = "steps:\n"
            + "  - id: typed\n"
            + "    type: http.rest\n"
            + "    target: svc\n"
            + "    method: GET\n"
            + "    path: /x\n"
            + "    continueOnFailure: !!str true\n";

        var result = DocumentValidator.Validate(yaml, s_registry);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.InstanceLocation.Contains("continueOnFailure", System.StringComparison.Ordinal));
    }

    [Fact]
    public void IntTaggedOctalText_ConvertsToString()
    {
        var converted = ConvertedValue("!!int 0o17");

        Assert.Equal(JsonValueKind.String, converted.ValueKind);
        Assert.Equal("0o17", converted.GetString());
    }

    [Fact]
    public void IntTaggedDecimal_StaysInteger()
    {
        var converted = ConvertedValue("!!int 200");

        Assert.Equal(JsonValueKind.Number, converted.ValueKind);
        Assert.Equal(200L, converted.GetInt64());
    }

    [Fact]
    public void FloatTaggedDecimal_StaysDouble()
    {
        var converted = ConvertedValue("!!float 1.5");

        Assert.Equal(JsonValueKind.Number, converted.ValueKind);
        Assert.Equal(1.5, converted.GetDouble());
    }

    /// <summary>An explicit string tag wins over the number the text would otherwise read as.</summary>
    [Fact]
    public void StringTaggedNumber_ConvertsToString()
    {
        var converted = ConvertedValue("!!str 17");

        Assert.Equal(JsonValueKind.String, converted.ValueKind);
        Assert.Equal("17", converted.GetString());
    }
}
