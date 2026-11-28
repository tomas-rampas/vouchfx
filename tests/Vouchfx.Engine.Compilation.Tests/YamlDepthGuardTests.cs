// The nesting limit at the schema-validation entry points: DocumentValidator.Validate (the
// authoring path) and the two public validators that reach the YAML-to-JSON bridge directly.
// Nesting this deep overflows the stack inside YamlStream.Load and the bridge's deserialiser,
// which no catch can stop, so every refused document is refused before either runs; if the
// check were unwired the 200,000-level rows would crash the test host instead of failing.
using System.Diagnostics;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Tests;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class YamlDepthGuardTests
{
    private const string DepthText = "nested more than 64 levels deep";
    private static readonly TimeSpan s_bound = TimeSpan.FromSeconds(5);

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(new[] { typeof(HttpRestProvider).Assembly });

    // Entry points are passed by name: the delegates are not xunit-serialisable.
    public static TheoryData<string> EntryPoints => new() { "document", "yaml", "composer" };

    private static SchemaValidationResult Run(string entry, string yaml) => entry switch
    {
        "document" => DocumentValidator.Validate(yaml, s_registry),
        "yaml" => YamlSchemaValidator.Validate(yaml),
        "composer" => SchemaComposer.Validate(s_registry, yaml),
        _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, null),
    };

    private static string DepthMessage(SchemaValidationResult result)
    {
        Assert.False(result.IsValid);
        var message = Assert.Single(result.Errors).Message;
        Assert.Contains("64", message, StringComparison.Ordinal);
        Assert.Contains(DepthText, message, StringComparison.Ordinal);
        return message;
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public void Validate_65NestedFlowSequences_IsRefusedWithLimitLineAndColumn(string entry)
    {
        var message = DepthMessage(Run(entry, DeepYamlShapes.ClosedFlowSequences(DeepYamlShapes.Limit + 1)));
        Assert.Contains("line 1, column 65", message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public void Validate_65NestedBlockMappings_IsRefusedWithLimitLineAndColumn(string entry)
    {
        var message = DepthMessage(Run(entry, DeepYamlShapes.BlockMappings(DeepYamlShapes.Limit + 1)));
        Assert.Contains("line 65, column 65", message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public void Validate_64NestedFlowSequences_IsNotRefusedByTheDepthCheck(string entry)
    {
        // Invalid for other reasons: the root is not a mapping, and the schema bridge's own
        // serialiser refuses 50 or more nested collections ("Too much recursion"). The depth
        // check is not one of them.
        var result = Run(entry, DeepYamlShapes.ClosedFlowSequences(DeepYamlShapes.Limit));
        Assert.DoesNotContain(result.Errors, error => error.Message.Contains(DepthText, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public void Validate_64NestedBlockMappings_IsNotRefusedByTheDepthCheck(string entry)
    {
        var result = Run(entry, DeepYamlShapes.BlockMappings(DeepYamlShapes.Limit));
        Assert.DoesNotContain(result.Errors, error => error.Message.Contains(DepthText, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public void Validate_SecondDocumentIs65Deep_IsRefused_DepthIsCountedPerDocument(string entry)
    {
        var yaml = "metadata: { name: first }\n---\n" + DeepYamlShapes.ClosedFlowSequences(DeepYamlShapes.Limit + 1);
        Assert.Contains("line 3, column 65", DepthMessage(Run(entry, yaml)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("document", "flow", 200_000)]
    [InlineData("document", "flow", DeepYamlShapes.OneMiB)]
    [InlineData("document", "block", 200_000)]
    [InlineData("document", "block", DeepYamlShapes.OneMiB)]
    [InlineData("yaml", "flow", 200_000)]
    [InlineData("yaml", "flow", DeepYamlShapes.OneMiB)]
    [InlineData("yaml", "block", DeepYamlShapes.OneMiB)]
    [InlineData("composer", "flow", 200_000)]
    [InlineData("composer", "flow", DeepYamlShapes.OneMiB)]
    [InlineData("composer", "block", DeepYamlShapes.OneMiB)]
    public void Validate_VeryDeepInput_IsRefusedInTimeBoundedByTheLimit(string entry, string shape, int characters)
    {
        var yaml = DeepYamlShapes.Large(shape, characters);
        var clock = Stopwatch.StartNew();
        var result = Run(entry, yaml);
        clock.Stop();
        DepthMessage(result);
        Assert.True(clock.Elapsed < s_bound, $"refusal took {clock.Elapsed}");
    }
}
