using System.Text.Json;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation.Schema;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class YamlDecimalIntegerParityTests
{
    [Theory]
    [InlineData("007", 7)]
    [InlineData("010", 10)]
    [InlineData("0123", 123)]
    [InlineData("0777", 777)]
    [InlineData("08080", 8080)]
    [InlineData("09", 9)]
    [InlineData("0177777", 177777)]
    [InlineData("0000000008080", 8080)]
    [InlineData("65535", 65535)]
    [InlineData("65536", 65536)]
    public void Bridge_PlainInteger_UsesDecimalValue(string scalar, long expected)
    {
        using var json = SchemaResources.ConvertYamlToJsonDocument($"value: {scalar}");
        Assert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("value").ValueKind);
        Assert.Equal(expected, json.RootElement.GetProperty("value").GetInt64());
    }

    [Theory]
    [InlineData("0123", 123)]
    [InlineData("08080", 8080)]
    [InlineData("09", 9)]
    [InlineData("00001", 1)]
    [InlineData("065535", 65535)]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    [InlineData("\"0123\"", 123)]
    [InlineData("\"08080\"", 8080)]
    [InlineData("\"65535\"", 65535)]
    public void HttpPort_ValidDecimal_SchemaAndParserAgree(string scalar, int expected)
    {
        var yaml = ServiceYaml(scalar);
        var validation = YamlSchemaValidator.Validate(yaml);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(e => e.Message)));
        Assert.Equal(expected, YamlDocumentParser.Parse(yaml).Environment!.Services!["app"].HttpPort);
    }

    [Theory]
    [InlineData("0", "at least")]
    [InlineData("00000", "at least")]
    [InlineData("65536", "at most")]
    [InlineData("0177777", "at most")]
    [InlineData("070000", "at most")]
    [InlineData("\"0\"", "regular expression")]
    [InlineData("\"0177777\"", "regular expression")]
    [InlineData("\"65536\"", "regular expression")]
    public void HttpPort_InvalidDecimal_IsRefusedAtTheField(string scalar, string explanation)
    {
        var result = YamlSchemaValidator.Validate(ServiceYaml(scalar));
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("/environment/services/app/httpPort", error.InstanceLocation);
        Assert.Contains(explanation, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed to parse YAML", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bridge_DecimalIntegers_DoNotChangeOtherScalarOrAliasSemantics()
    {
        const string yaml = """
            quoted: "0123"
            singleQuoted: '08080'
            literal: |
              0123
            boolean: true
            falseBoolean: FALSE
            yaml11Word: yes
            number: 1.25
            exponential: 1e2
            missing: null
            explicitString: !!str 0123
            explicitInteger: !!int 0123
            anchored: &port 0123
            alias: *port
            """;
        using var json = SchemaResources.ConvertYamlToJsonDocument(yaml);
        var root = json.RootElement;
        Assert.Equal("0123", root.GetProperty("quoted").GetString());
        Assert.Equal("08080", root.GetProperty("singleQuoted").GetString());
        Assert.Equal("0123\n", root.GetProperty("literal").GetString());
        Assert.True(root.GetProperty("boolean").GetBoolean());
        Assert.False(root.GetProperty("falseBoolean").GetBoolean());
        Assert.Equal("yes", root.GetProperty("yaml11Word").GetString());
        Assert.Equal(1.25, root.GetProperty("number").GetDouble());
        Assert.Equal(100, root.GetProperty("exponential").GetDouble());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("missing").ValueKind);
        // The existing type resolver also infers tagged scalars; keep that policy.
        Assert.Equal(83, root.GetProperty("explicitString").GetInt64());
        // Explicit tags retain YamlDotNet's existing interpretation; the bridge
        // changes only its own implicit decimal-integer inference.
        Assert.Equal(83, root.GetProperty("explicitInteger").GetInt64());
        Assert.Equal(123, root.GetProperty("anchored").GetInt64());
        Assert.Equal(123, root.GetProperty("alias").GetInt64());
    }

    [Theory]
    [InlineData("0123", null)]
    [InlineData("08080", null)]
    [InlineData("\"08080\"", null)]
    [InlineData("8443", 8443)]
    [InlineData("\"8443\"", 8443)]
    [InlineData("http", 8080)]
    public void SecurityEndpoint_DecimalBridge_PreservesCanonicalSelectorPolicy(string scalar, int? expected)
    {
        var yaml = $$"""
            environment:
              services:
                app:
                  image: myorg/app:1.0
                  httpPort: 8080
                  security:
                    profile: tls
                    endpoint: {{scalar}}
            steps:
              - id: noop
                type: noop.echo
            """;
        var result = YamlSchemaValidator.Validate(yaml);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
        var service = YamlDocumentParser.Parse(yaml).Environment!.Services!["app"];
        Assert.Equal(expected, ServiceEndpointNaming.ResolveSecuredPort(service));
    }

    [Theory]
    [InlineData("0123")]
    [InlineData("08080")]
    [InlineData("\"0123\"")]
    [InlineData("\"08080\"")]
    public void Ports_LeadingZeroSpelling_StillHasLocatedParserRefusal(string scalar)
    {
        var yaml = ServiceYaml("8080").Replace("httpPort: 8080", $"ports: [{scalar}]", StringComparison.Ordinal);
        var error = Assert.Throws<YamlParseException>(() => YamlDocumentParser.Parse(yaml));
        Assert.Contains("leading zero", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(error.Line > 0);
        Assert.True(error.Column > 0);
    }

    private static string ServiceYaml(string scalar) => $$"""
        environment:
          services:
            app:
              image: myorg/app:1.0
              httpPort: {{scalar}}
        steps:
          - id: noop
            type: noop.echo
        """;
}
