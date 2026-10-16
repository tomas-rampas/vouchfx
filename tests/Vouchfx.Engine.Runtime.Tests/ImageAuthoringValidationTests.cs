using System.Text.Json;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class ImageAuthoringValidationTests
{
    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(new[] { typeof(ScriptCsharpProvider).Assembly });

    public static TheoryData<string> InvalidServiceImages => new()
    {
        "nginx", "registry.example:5000/team/nginx", "   ", " nginx:1.27", "nginx:1.27 ",
        "/nginx:1.27", "team//nginx:1.27", "team/", "nginx:1.27:extra", "nginx:",
        "nginx@", "nginx@sha256:", "nginx@sha256:nothex", "nginx@sha512:abcd", "nginx@abcd",
    };

    public static TheoryData<string, string?> InvalidDependencyImages => new()
    {
        { "redis", null }, { "registry.example:5000/team/redis", null }, { "   ", null },
        { " redis:7", null }, { "redis:7 ", null }, { "/redis:7", null }, { "team//redis:7", null },
        { "team/", null }, { "redis:7:extra", null }, { "redis:", null }, { "redis@", null },
        { "redis@sha256:", null }, { "redis@sha256:nothex", null }, { "redis@sha512:abcd", null },
        { "redis@abcd", null }, { "redis:7", "7" }, { "redis@sha256:abcd", "7" },
    };

    public static TheoryData<string> ValidServiceImages => new()
    {
        "nginx:1.27", "nginx:latest", "team/nginx:1.27", "registry.example:5000/team/nginx:1.27",
        "localhost:5000/team/nginx:1.27", "nginx@sha256:abcdef", "nginx:1.27@sha256:abcdef",
    };

    public static TheoryData<string?, string?> ValidDependencyImages => new()
    {
        { null, null }, { "", null }, { "", "7" }, { "redis", "7" },
        { "registry.example:5000/team/redis", "7" }, { "redis:7", null }, { "redis:7", "" },
        { "redis:latest", null }, { "redis@sha256:abcdef", null }, { "redis:7@sha256:abcdef", null },
    };

    [Theory]
    [MemberData(nameof(InvalidServiceImages))]
    public void Map_InvalidServiceImage_RejectsBeforeConfigure(string image)
    {
        var environment = ServiceEnvironment(image);
        Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(environment));
    }

    [Theory]
    [MemberData(nameof(InvalidServiceImages))]
    public void Validate_InvalidServiceImage_RefusesAtPipelineWithTheMappingDiagnostic(string image)
    {
        AssertPureRefusal(ServiceEnvironment(image), ServiceYaml(image));
    }

    [Theory]
    [MemberData(nameof(InvalidDependencyImages))]
    public void Validate_InvalidDependencyImage_RefusesAtPipelineWithTheMappingDiagnostic(string image, string? version)
    {
        AssertPureRefusal(DependencyEnvironment(image, version), DependencyYaml(image, version));
    }

    [Theory]
    [MemberData(nameof(ValidServiceImages))]
    public void Validate_PinnedServiceImage_AgreesWithMappingAndPreservesRegistryResolution(string image)
    {
        var environment = ServiceEnvironment(image);
        Assert.NotNull(EnvironmentMapper.Map(environment));
        Assert.NotNull(EnvironmentDriverRegistry.BuiltIn.Bind(environment, null));
        var result = ScenarioValidator.ValidateScenario(ServiceYaml(image), "valid-image.e2e.yaml", s_registry);
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Empty(result.Diagnostics);

        var qualified = image.StartsWith("registry.example:", StringComparison.Ordinal)
            || image.StartsWith("localhost:", StringComparison.Ordinal);
        Assert.Equal(qualified ? image : "mirror.example/" + image,
            EnvironmentMapper.ResolveImage(image, "mirror.example"));
    }

    [Theory]
    [MemberData(nameof(ValidDependencyImages))]
    public void Validate_DependencyDefaultOrPinnedOverride_AgreesWithMapping(string? image, string? version)
    {
        var environment = DependencyEnvironment(image, version);
        Assert.NotNull(EnvironmentMapper.Map(environment));
        Assert.NotNull(EnvironmentDriverRegistry.BuiltIn.Bind(environment, null));
        var result = ScenarioValidator.ValidateScenario(DependencyYaml(image, version), "valid-image.e2e.yaml", s_registry);
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData("")]
    [InlineData("~")]
    [InlineData("null")]
    public void Bind_ParsedAbsentServiceImage_RefusesBeforeTopology(string value)
    {
        var document = YamlDocumentParser.Parse($"environment:\n  services:\n    api:\n      image: {value}\n");
        Assert.Null(document.Environment!.Services!["api"].Image);
        var bind = Assert.Throws<ArgumentException>(() => EnvironmentDriverRegistry.BuiltIn.Bind(document.Environment, null));
        var map = Assert.Throws<ArgumentException>(() => EnvironmentMapper.Map(document.Environment));
        Assert.Equal(map.Message, bind.Message);
        Assert.Contains("neither 'image' nor 'project'", bind.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("~")]
    [InlineData("null")]
    public void Validate_ServiceNullImage_StillRefusesAtSchema(string value)
    {
        var yaml = $"environment:\n  services:\n    api:\n      image: {value}\nsteps:\n  - id: noop\n    type: script.csharp\n    code: var x = 1;\n";
        var result = ScenarioValidator.ValidateScenario(yaml, "null-image.e2e.yaml", s_registry);
        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Stage == ValidationStage.Schema);
    }

    [Fact]
    public void Bind_ParsedProjectWithDanglingImage_UsesProject()
    {
        var document = YamlDocumentParser.Parse("environment:\n  services:\n    api:\n      image:\n      project: api.csproj\n");
        Assert.Null(document.Environment!.Services!["api"].Image);
        Assert.NotNull(EnvironmentDriverRegistry.BuiltIn.Bind(document.Environment, null));
        Assert.NotNull(EnvironmentMapper.Map(document.Environment));
    }

    [Fact]
    public void Bind_DirectApiServiceWithoutImageOrProject_RefusesBeforeTopology()
    {
        var environment = new EnvironmentSpec(new Dictionary<string, ServiceSpec>
        {
            ["api"] = new(null, null, null, null, null),
        }, null, null, null, null);
        Assert.Throws<ArgumentException>(() => EnvironmentDriverRegistry.BuiltIn.Bind(environment, null));
    }

    [Fact]
    public void Bind_ProjectService_DoesNotRequireAnImage()
    {
        var environment = new EnvironmentSpec(
            new Dictionary<string, ServiceSpec> { ["worker"] = new(null, "worker.csproj", null, null, null) },
            null, null, null, null);
        Assert.NotNull(EnvironmentDriverRegistry.BuiltIn.Bind(environment, null));
        Assert.NotNull(EnvironmentMapper.Map(environment));
    }

    [Fact]
    public void Bind_ExternalSource_KeepsTheDriverSelectionRefusal()
    {
        var environment = new EnvironmentSpec(null, null, null, null, null)
        { Source = new EnvironmentSourceSpec("unregistered", "driver-owned.yaml") };
        var error = Assert.Throws<ArgumentException>(() => EnvironmentDriverRegistry.BuiltIn.Bind(environment, null));
        Assert.Contains("unknown driver 'unregistered'", error.Message, StringComparison.Ordinal);
    }

    private static void AssertPureRefusal(EnvironmentSpec environment, string yaml)
    {
        // Gather each real seam before asserting, so the unchanged-source RED control
        // measures validate/compile accepting a reference independently of Map/Bind.
        var mappingException = Record.Exception(() => EnvironmentMapper.Map(environment));
        var bindingException = Record.Exception(() => EnvironmentDriverRegistry.BuiltIn.Bind(environment, null));
        var result = ScenarioValidator.ValidateScenario(yaml, "bad-image.e2e.yaml", s_registry);
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), s_registry);
        var compile = ProviderPipeline.Compile(ast, s_registry, "ImageAuthoringRegression");

        Assert.False(result.IsValid);
        var mappingError = Assert.IsType<ArgumentException>(mappingException);
        var bindingError = Assert.IsType<ArgumentException>(bindingException);
        Assert.Equal(mappingError.Message, bindingError.Message);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ValidationStage.Pipeline, diagnostic.Stage);
        Assert.Equal(mappingError.Message, diagnostic.Message);
        Assert.NotNull(compile.Failure);
        Assert.Equal(mappingError.Message, compile.Failure.Message);
        Assert.Null(compile.Assembled);
        Assert.Null(compile.EnvironmentPlan);
        Assert.Empty(compile.ResourcePlan);
        Assert.Empty(compile.HostResourcePlan);
        Assert.Empty(compile.CompileReferencePaths);
    }

    private static EnvironmentSpec ServiceEnvironment(string image) => new(
        new Dictionary<string, ServiceSpec> { ["svc"] = new(image, null, null, null, null) },
        null, null, null, null);

    private static EnvironmentSpec DependencyEnvironment(string? image, string? version) => new(
        null, new Dictionary<string, DependencySpec> { ["cache"] = new("redis", version, null) { Image = image } },
        null, null, null);

    private static string ServiceYaml(string image) => $$"""
        environment:
          services:
            svc:
              image: {{JsonSerializer.Serialize(image)}}
        steps:
          - id: noop
            type: script.csharp
            code: "var value = 1;"
        """;

    private static string DependencyYaml(string? image, string? version) => $$"""
        environment:
          dependencies:
            cache:
              type: redis
              image: {{JsonSerializer.Serialize(image)}}
              version: {{JsonSerializer.Serialize(version)}}
        steps:
          - id: noop
            type: script.csharp
            code: "var value = 1;"
        """;
}
