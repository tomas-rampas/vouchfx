using System.Text.RegularExpressions;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using YamlDotNet.RepresentationModel;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class SchemaAuthorDiagnosticsTests
{
    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(new[] { typeof(ScriptCsharpProvider).Assembly });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicComposedValidation_IndependentFaultsRetainExplanationsAndLocations(bool withLines)
    {
        const string yaml = """
            environment:
              dependencies:
                cache:
                  type: redis
                  security:
                    profile: tls
            steps:
              - id: check
                type: script.csharp
                code: "// no execution"
                taget: cache
            """;

        var result = withLines
            ? DocumentValidator.Validate(yaml, s_registry)
            : SchemaComposer.Validate(s_registry, yaml);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
        var forbidden = Assert.Single(result.Errors, error =>
            error.InstanceLocation == "/environment/dependencies/cache/security");
        Assert.Contains("Dependency 'cache' (type 'redis') declares 'security'", forbidden.Message, StringComparison.Ordinal);
        Assert.Contains("no security profile is wired", forbidden.Message, StringComparison.Ordinal);
        var misspelling = Assert.Single(result.Errors, error => error.InstanceLocation == "/steps/0/taget");
        Assert.Contains("Unknown property 'taget' on step type 'script.csharp'", misspelling.Message, StringComparison.Ordinal);
        AssertAuthorMessages(result);
        if (withLines)
        {
            Assert.StartsWith("(line 6) ", forbidden.Message, StringComparison.Ordinal);
            Assert.StartsWith("(line 11) ", misspelling.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PublicRootValidation_IndependentFaultsRetainPropertyAndAcceptedValues()
    {
        const string yaml = """
            environment:
              imagePullPolicy: never-ever
              dependencies:
                cache:
                  type: redis
                  security:
                    profile: tls
            steps:
              - id: check
                type: script.csharp
            """;

        var result = YamlSchemaValidator.Validate(yaml);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, error => error.InstanceLocation == "/environment/imagePullPolicy"
            && error.Message.Contains("accepted values", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.InstanceLocation == "/environment/dependencies/cache/security"
            && error.Message.Contains("Dependency 'cache' (type 'redis') declares 'security'", StringComparison.Ordinal));
        AssertAuthorMessages(result);
    }

    [Fact]
    public void DocumentValidation_UnknownTypeStillSuppressesOnlyItsOwnPropertyCascade()
    {
        const string yaml = """
            steps:
              - id: unknown
                type: unknown.provider
                target: nowhere
                options: { extra: true }
              - id: known
                type: script.csharp
                code: "// no execution"
                taget: cache
            """;

        var result = DocumentValidator.Validate(yaml, s_registry);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
        var unknown = Assert.Single(result.Errors, error => error.InstanceLocation == "/steps/0/type");
        Assert.Contains("unknown step type 'unknown.provider'", unknown.Message, StringComparison.Ordinal);
        var independent = Assert.Single(result.Errors, error => error.InstanceLocation == "/steps/1/taget");
        Assert.Contains("Unknown property 'taget' on step type 'script.csharp'", independent.Message, StringComparison.Ordinal);
        AssertAuthorMessages(result);
    }

    [Fact]
    public void DocumentValidation_LongAuthorNameKeepsDisplayBoundAndFullPointer()
    {
        var name = new string('x', 500);
        var yaml = "steps:\n  - id: check\n    type: script.csharp\n    code: '// no execution'\n    " + name + ": true\n";

        var result = DocumentValidator.Validate(yaml, s_registry);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("/steps/0/" + name, error.InstanceLocation);
        Assert.Contains(new string('x', 200) + "... (500 chars total)", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(name, error.Message, StringComparison.Ordinal);
        AssertAuthorMessages(result);
    }

    [Fact]
    public void PublicComposedValidation_BracketedAuthorDataRemainsInTheExplanation()
    {
        const string yaml = """
            steps:
              - id: check
                type: script.csharp
                code: "// no execution"
                "[properties]": true
            """;

        var result = SchemaComposer.Validate(s_registry, yaml);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("/steps/0/[properties]", error.InstanceLocation);
        Assert.Equal("Unknown property '[properties]' on step type 'script.csharp'", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicComposedValidation_FalseProviderFragmentRetainsEachFaultWithoutEmptyMarkers(bool withLines)
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            (IReadOnlyCollection<IStepProvider>)new IStepProvider[] { new FalseSchemaProvider() });
        const string yaml = """
            steps:
              - id: first
                type: validation.rejected
              - id: second
                type: validation.rejected
            """;

        var result = withLines
            ? DocumentValidator.Validate(yaml, registry)
            : SchemaComposer.Validate(registry, yaml);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
        for (var i = 0; i < 2; i++)
        {
            var error = Assert.Single(result.Errors, error => error.InstanceLocation == $"/steps/{i}");
            var expected = "All values fail against the false schema";
            Assert.Equal(withLines ? $"(line {2 + i * 2}) {expected}" : expected, error.Message);
        }
        AssertAuthorMessages(result);
    }

    private sealed record FalseSchemaModel : IStepModel;

    // Explicit-instance registration only: the fragment is schema metadata,
    // and binding must never run when the schema has already refused it.
    private sealed class FalseSchemaProvider : IStepProvider, IStepBinder<FalseSchemaModel>
    {
        private static readonly ProviderMetadata s_metadata =
            new("1.0.0", "1.0.0", "Apache-2.0", new[] { "schema-regression" });

        public StepKindId Kind => new("validation", "rejected");
        public ProviderMetadata Metadata => s_metadata;
        public JsonSchemaFragment SchemaFragment => new("false");
        public FalseSchemaModel Bind(YamlNode node, IBindingContext ctx) =>
            throw new InvalidOperationException("Schema rejection must precede provider binding.");
    }

    private static void AssertAuthorMessages(SchemaValidationResult result)
    {
        Assert.All(result.Errors, error =>
        {
            Assert.False(Regex.IsMatch(error.Message, @"^(?:\(line \d+\) )?\[[A-Za-z$]*\] "), error.Message);
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
        });
    }
}
