using System.Linq;
using System.Text.Json;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Schema validation reads each plain or tagged scalar as text first. Text that .NET cannot
/// turn into a finite number, or a value its explicit tag cannot hold, is accepted as the
/// string written rather than stopping the whole suite with a generic parse failure.
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

    /// <summary>An integer tag on text that is not a decimal integer is accepted as text.</summary>
    [Theory]
    [InlineData("!!int 0o17")]
    [InlineData("!!int abc")]
    [InlineData("!!float NaN")]
    [InlineData("!!bool yes")]
    public void TaggedValueThatDoesNotParse_ValidatesAsText(string value)
    {
        var result = DocumentValidator.Validate(StepWithValue(value), s_registry);

        Assert.True(result.IsValid,
            string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}")));
    }

    /// <summary>
    /// Parity matrix. Each row records what the bridge produced before the typing change
    /// (measured on the unmodified code): a scalar that parsed keeps its JSON value, and one
    /// that threw is now the text written. The JSON written for a non-finite float
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
    public void ScalarThatParsedBefore_KeepsItsJsonValue_AndThrowingOnesBecomeText(string value, string expectedJson)
    {
        var converted = ConvertedValue(value);

        Assert.Equal(expectedJson, converted.GetRawText());
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
