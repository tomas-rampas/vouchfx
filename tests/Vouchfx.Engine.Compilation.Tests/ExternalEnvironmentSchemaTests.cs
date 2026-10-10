using System;
using Vouchfx.Engine.Compilation.Schema;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class ExternalEnvironmentSchemaTests
{
    [Fact]
    public void SourceOnly_UsesNeutralDriverGrammar()
    {
        var result = YamlSchemaValidator.Validate("""
            environment:
              source: {driver: another-driver, config: ./driver.yaml}
            steps: [{id: noop, type: noop.echo}]
            """);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("services: {}")]
    [InlineData("dependencies: {}")]
    [InlineData("seed: {}")]
    [InlineData("imageRegistry: registry.example")]
    [InlineData("imagePullPolicy: Missing")]
    public void SourceWithInlineDeclaration_IsRefused_EvenWhenEmpty(string declaration)
    {
        var result = YamlSchemaValidator.Validate($$"""
            environment:
              source: {driver: compose, config: ./driver.yaml}
              {{declaration}}
            steps: [{id: noop, type: noop.echo}]
            """);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.InstanceLocation == "/environment"
            && error.Message.Contains("maxProperties", StringComparison.Ordinal));
    }

    [Fact]
    public void SourceExclusivity_DoesNotSuppressUnrelatedDocumentErrors()
    {
        var result = YamlSchemaValidator.Validate("""
            environment:
              source: {driver: compose, config: ./driver.yaml}
              services: {}
            metadata:
              invalidMetadataKey: true
            steps: [{id: noop, type: noop.echo}]
            """);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.InstanceLocation == "/environment");
        Assert.Contains(result.Errors, error => error.InstanceLocation == "/metadata/invalidMetadataKey");
    }
}
