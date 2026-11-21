using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.YamlSafety;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// The schema bridge deserialises YAML by recursion. The same nesting that crashes
/// <c>YamlStream.Load</c> must be a validation diagnostic here too.
/// </summary>
public sealed class YamlNestingLimitSchemaTests
{
    [Fact]
    public void Validate_NestingPastTheLimit_IsAParseDiagnosticFromBothEntries()
    {
        var yaml = "k: " + new string('[', YamlNestingLimit.MaxDepth) + "1" + new string(']', YamlNestingLimit.MaxDepth) + "\n";
        var registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(HttpRestProvider).Assembly });

        foreach (var result in new[]
        {
            YamlSchemaValidator.Validate(yaml),
            SchemaComposer.Validate(registry, yaml),
        })
        {
            Assert.False(result.IsValid);
            var message = Assert.Single(result.Errors).Message;
            Assert.StartsWith("Failed to parse YAML: the document nests more than 64 collections", message, StringComparison.Ordinal);
        }
    }
}
