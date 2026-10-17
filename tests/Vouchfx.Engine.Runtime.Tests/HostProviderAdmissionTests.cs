using System.Reflection;
using System.Reflection.Emit;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class HostProviderAdmissionTests
{
    [Theory]
    [InlineData("1.0.2", "1.0.7", true)]
    [InlineData("1.0.7", "1.0.7+source", true)]
    [InlineData("1.0.8", "1.0.7", false)]
    [InlineData("1.0.7", "1.0.7-rc.1", false)]
    [InlineData("1.0.7-rc.2", "1.0.7-rc.10", true)]
    [InlineData("1.0.7-rc.10", "1.0.7-rc.2", false)]
    [InlineData("1.0.7-rc.1", "1.0.7", true)]
    [InlineData("not-a-version", "1.0.7", false)]
    [InlineData("1.0", "1.0.7", false)]
    [InlineData("1.00.0", "1.0.7", false)]
    [InlineData("1.0.7-01", "1.0.7", false)]
    public void Discovery_EnforcesSemVerBeforeReadingProviderSchema(string minimum, string actual, bool compatible)
    {
        var assemblies = new[] { CreateModuleAssembly(minimum, rejectSchema: !compatible) };
        if (compatible)
            Assert.Single(HostProviderRegistry.Build(assemblies, actual).All);
        else
        {
            var exception = Assert.Throws<InvalidOperationException>(() => HostProviderRegistry.Build(assemblies, actual));
            Assert.Contains("Provider 'admission.fixture'", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SCHEMA EXECUTED", exception.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("1.0")]
    public void MissingOrInvalidHostVersion_IsNeverAnUncheckedBypass(string actual) =>
        Assert.Throws<ArgumentException>(() => HostProviderRegistry.Build(Array.Empty<Assembly>(), actual));

    [Fact]
    public void RunningEngine_UsesItsOwnCapabilityStamp()
    {
        Assert.NotEqual("1.0.0-0.local", HostProviderRegistry.EngineVersion);
        _ = EngineSemVersion.Parse(HostProviderRegistry.EngineVersion);
        Assert.Single(HostProviderRegistry.Build(new[] { CreateModuleAssembly("1.0.2", rejectSchema: false) }).All);
    }

    [Theory]
    [InlineData("single")]
    [InlineData("suite")]
    [InlineData("parallel")]
    public async Task DirectEmbeddingEntryPoints_RefuseBeforeSchemaCompilationOrTopology(string path)
    {
        var assemblies = new[] { CreateModuleAssembly("999999.0.0", rejectSchema: true) };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (path == "single")
                await ScenarioRunner.RunAsync("steps: []", "admission", assemblies, null, TextWriter.Null);
            else if (path == "suite")
                await ScenarioRunner.RunSuiteAsync(Array.Empty<ScenarioAst>(), Array.Empty<string>(), Array.Empty<string>(),
                    assemblies, null, TextWriter.Null);
            else
                await ParallelSuiteRunner.RunParallelAsync(Array.Empty<ScenarioAst>(), Array.Empty<string>(), Array.Empty<string>(),
                    assemblies, null, TextWriter.Null);
        });
        Assert.Contains("requires engine 999999.0.0", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SCHEMA EXECUTED", exception.ToString(), StringComparison.Ordinal);
    }

    // A real discovered module in an isolated assembly avoids mutable global test registration.
    private static AssemblyBuilder CreateModuleAssembly(string minimum, bool rejectSchema)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Admission" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
        var type = assembly.DefineDynamicModule("main").DefineType("AdmissionModule", TypeAttributes.Public, typeof(AdmissionModule));
        var constructor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, minimum);
        il.Emit(rejectSchema ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Call, typeof(AdmissionModule).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, new[] { typeof(string), typeof(bool) }, modifiers: null)!);
        il.Emit(OpCodes.Ret);
        _ = type.CreateType();
        return assembly;
    }
}

public abstract class AdmissionModule : IProviderModule
{
    private readonly string _minimum;
    private readonly bool _rejectSchema;
    protected AdmissionModule(string minimum, bool rejectSchema) => (_minimum, _rejectSchema) = (minimum, rejectSchema);
    public IEnumerable<IStepProvider> Providers() => new[] { new AdmissionProvider(_minimum, _rejectSchema) };
}

file sealed class AdmissionProvider : IStepProvider, IStepBinder<AdmissionModel>
{
    private static readonly string[] s_authors = { "test" };
    private readonly string _minimum;
    private readonly bool _rejectSchema;
    internal AdmissionProvider(string minimum, bool rejectSchema) => (_minimum, _rejectSchema) = (minimum, rejectSchema);
    public StepKindId Kind => new("admission", "fixture");
    public ProviderMetadata Metadata => new("1.0.0", _minimum, "Apache-2.0", s_authors);
    public JsonSchemaFragment SchemaFragment => _rejectSchema ? throw new InvalidOperationException("SCHEMA EXECUTED") : new("{}");
    public AdmissionModel Bind(YamlDotNet.RepresentationModel.YamlNode node, IBindingContext ctx) => new();
}

file sealed record AdmissionModel : IStepModel;
