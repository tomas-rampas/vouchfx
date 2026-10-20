using System;
using System.Text.Json;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class LanguageV2SchemaTests
{
    [Theory]
    [InlineData("schemaVersion: v2\n  telemetry: false", true)]
    [InlineData("schemaVersion: v2\n  telemetry: true", true)]
    [InlineData("schemaVersion: v2\n  telemetry: no", false)]
    [InlineData("schemaVersion: v1\n  telemetry: false", false)]
    [InlineData("telemetry: false", false)]
    [InlineData("schemaVersion: v3", false)]
    public void VersionSelection_UsesDistinctContract(string metadata, bool valid)
    {
        var yaml = "metadata:\n  " + metadata + "\nsteps:\n  - id: one\n    type: script.csharp\n";
        var registry = StepKindRegistry.BuildAndFreeze(Array.Empty<System.Reflection.Assembly>());
        Assert.Equal(valid, YamlSchemaValidator.Validate(yaml).IsValid);
        Assert.Equal(valid, SchemaComposer.Validate(registry, yaml).IsValid);
    }

    [Fact]
    public void Export_DefaultV1Unchanged_V2HasSeparateIdAndRequiresVersion()
    {
        var registry = StepKindRegistry.BuildAndFreeze(Array.Empty<System.Reflection.Assembly>());
        Assert.Equal(SchemaComposer.ComposeSchemaJson(registry), SchemaComposer.ComposeVersionedSchemaJson(registry, "v1"));
        using var v2 = JsonDocument.Parse(SchemaComposer.ComposeVersionedSchemaJson(registry, "v2"));
        Assert.Equal("https://schemas.vouchfx.io/e2e/v2/root-language-schema.json", v2.RootElement.GetProperty("$id").GetString());
        Assert.Equal("v2", v2.RootElement.GetProperty("x-vouchfx-schema-version").GetString());
        Assert.Throws<ArgumentOutOfRangeException>(() => SchemaComposer.ComposeVersionedSchemaJson(registry, "v3"));
    }
}
