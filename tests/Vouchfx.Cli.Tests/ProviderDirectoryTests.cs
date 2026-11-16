using System.Collections.Immutable;
using System.CommandLine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vouchfx.Cli;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ProviderDirectoryTests
{
    internal static string Temp() => Directory.CreateTempSubdirectory("vouchfx-provider-").FullName;
    private static string Unique() => "Provider" + Guid.NewGuid().ToString("N");
    private const string Provider = """
        using Vouchfx.Sdk;
        namespace Community.Fixture;
        [StepProvider]
        public sealed class Probe : IStepProvider {
            public StepKindId Kind => new("probe", "external");
            public ProviderMetadata Metadata => new("1.0.0", "1.0.0", "Apache-2.0", new[] { "test" });
        }
        """;

    [Fact]
    public void Search_order_deduplicates_explicit_environment_and_default_paths()
    {
        var home = Temp();
        var defaultPath = Directory.CreateDirectory(Path.Combine(home, ".vouchfx", "providers")).FullName;
        var first = Temp(); var second = Temp();
        Assert.Equal(new[] { first, second, defaultPath }, ProviderOptions.ResolveDirectories(
            new[] { first }, first + Path.PathSeparator + second, false, home));
        Assert.Equal(new[] { first }, ProviderOptions.ResolveDirectories(new[] { first }, null, true, home));
        Assert.Empty(ProviderOptions.ResolveDirectories(Array.Empty<string>(), null, false, Temp()));
    }

    [Fact]
    public void Missing_explicit_directory_fails_and_nonassemblies_are_ignored()
    {
        var directory = Temp();
        File.WriteAllText(Path.Combine(directory, "notes.dll"), "not a managed image");
        File.Copy(Environment.ProcessPath!, Path.Combine(directory, "native.dll"));
        Assert.Empty(ProviderDirectoryScanner.Load(new[] { directory }));
        Assert.Contains("does not exist", Assert.Throws<ProviderLoadException>(() =>
            ProviderDirectoryScanner.Load(new[] { Path.Combine(directory, "missing") })).Message);
        File.WriteAllBytes(Path.Combine(directory, "broken.dll"), new byte[] { 77, 90, 1, 2, 3 });
        Assert.Contains("broken.dll", Assert.Throws<ProviderLoadException>(() => ProviderDirectoryScanner.Load(new[] { directory })).Message);
    }

    [Fact]
    public void Unreadable_file_refuses_startup_with_path()
    {
        var directory = Temp();
        var path = Path.Combine(directory, "locked.dll");
        using var locked = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Assert.Contains(path, Assert.Throws<ProviderLoadException>(() => ProviderDirectoryScanner.Load(new[] { directory })).Message);
    }

    [Theory]
    [InlineData("Vouchfx.Engine.Hijack")]
    [InlineData("Vouchfx.Steps.Hijack")]
    [InlineData("Vouchfx.Engine")]
    public void Reserved_namespaces_are_rejected_before_loading(string ns)
    {
        var directory = Temp();
        var path = Compile(directory, Unique(), $"namespace {ns} {{ public class Bad {{ }} }}");
        Assert.Contains(ns, Assert.Throws<ProviderLoadException>(() => ProviderDirectoryScanner.Load(new[] { directory })).Message);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => !assembly.IsDynamic && assembly.Location == path);
    }

    [Fact]
    public void Reserved_identity_is_rejected_even_without_reserved_types()
    {
        var directory = Temp();
        Compile(directory, "Vouchfx.Engine." + Unique(), "public class Harmless { }");
        Assert.Contains("impersonates", Assert.Throws<ProviderLoadException>(() => ProviderDirectoryScanner.Load(new[] { directory })).Message);
    }

    [Fact]
    public void Missing_and_conflicting_private_dependencies_fail_before_load()
    {
        var dependencyDirectory = Temp(); var directory = Temp(); var name = Unique();
        var dependency = Compile(dependencyDirectory, name, "public class PrivateDependency { }");
        var consumer = Compile(directory, Unique(), "public class Consumer : PrivateDependency { }", new[] { dependency });
        Assert.Contains("missing dependency", Assert.Throws<ProviderLoadException>(() => ProviderDirectoryScanner.Load(new[] { directory })).Message);
        Compile(directory, name, "[assembly:System.Reflection.AssemblyVersion(\"2.0.0.0\")] public class PrivateDependency { }");
        Assert.Contains("conflicting", Assert.Throws<ProviderLoadException>(() => ProviderDirectoryScanner.Load(new[] { directory })).Message);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => !assembly.IsDynamic && assembly.Location == consumer);
    }

    [Fact]
    public void Duplicate_private_assemblies_fail_with_both_paths()
    {
        var directory = Temp(); var name = Unique();
        var first = Directory.CreateDirectory(Path.Combine(directory, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(directory, "second")).FullName;
        Compile(first, name, "public class First { }"); Compile(second, name, "public class Second { }");
        var failure = Assert.Throws<ProviderLoadException>(() => ProviderDirectoryScanner.Load(new[] { directory }));
        Assert.Contains(first, failure.Message); Assert.Contains(second, failure.Message);
    }

    [Fact]
    public void Partial_type_load_is_a_startup_failure_instead_of_silent_discovery_loss()
    {
        var directory = Temp(); var dependencyDirectory = Temp(); var name = Unique();
        var dependency = Compile(dependencyDirectory, name, "public class RequiredBase { }");
        var providerPath = Compile(directory, Unique(), Provider + " public class Broken : RequiredBase { }", new[] { dependency });
        Compile(directory, name, "public class DifferentApi { }");
        var failure = Assert.Throws<ProviderLoadException>(() => ProviderDirectoryScanner.Load(new[] { directory }));
        Assert.Contains(providerPath, failure.Message);
        Assert.NotNull(failure.InnerException);
    }

    [Fact]
    public void Unsupported_sdk_major_is_rejected_before_provider_code()
    {
        var sdkDirectory = Temp(); var directory = Temp();
        var sdk = Compile(sdkDirectory, "Vouchfx.Sdk", "[assembly:System.Reflection.AssemblyVersion(\"2.0.0.0\")] namespace Vouchfx.Sdk { public class FutureContract {} }", includeSdk: false);
        Compile(directory, Unique(), "public class FutureProvider : Vouchfx.Sdk.FutureContract { }", new[] { sdk }, includeSdk: false);
        Assert.Contains("unsupported SDK", Assert.Throws<ProviderLoadException>(() => ProviderDirectoryScanner.Load(new[] { directory })).Message);
    }

    [Fact]
    public void Module_discovery_and_dependency_resolution_are_complete_and_repeatable()
    {
        var directory = Temp(); var name = Unique();
        var dependency = Compile(directory, name, "public class PrivateDependency { }");
        Compile(directory, Unique(), Provider.Replace("[StepProvider]", "").Replace(" : IStepProvider", " : PrivateDependency, IStepProvider") + """
            public sealed class Module : Vouchfx.Sdk.IProviderModule {
                public System.Collections.Generic.IEnumerable<Vouchfx.Sdk.IStepProvider> Providers() => new[] { new Probe() };
            }
            """, new[] { dependency });
        var first = ProviderDirectoryScanner.Load(new[] { directory });
        Assert.Single(first);
        using var scope = new ProviderRegistryScope(first);
        Assert.True(ProviderRegistryFactory.BuildRegistry().TryGet("probe.external", out _));
        Assert.Equal(first, ProviderDirectoryScanner.Load(new[] { directory }));
    }

    [Fact]
    public async Task Private_contributed_reference_compiles_in_worker_and_resolves_in_collectible_execution()
    {
        var directory = Temp(); var name = Unique();
        var dependency = Compile(directory, name, "namespace PrivateFixture { public static class Value { public static int Answer => 42; } }");
        var source = Provider.Replace(" : IStepProvider", " : IStepProvider, ICompileReferenceContributor")
            .Replace("public StepKindId Kind", "public System.Collections.Generic.IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies => new[] { typeof(PrivateFixture.Value).Assembly }; public StepKindId Kind");
        Compile(directory, Unique(), source, new[] { dependency });
        using var scope = new ProviderRegistryScope(ProviderDirectoryScanner.Load(new[] { directory }));
        Assert.True(scope.Registry.TryGet("probe.external", out var provider));
        var references = ((ICompileReferenceContributor)provider!.Instance).CompileReferenceAssemblies.Select(assembly => assembly.Location).ToArray();
        Assert.Contains(dependency, references);
        var compiled = await CompileWorkerMode.CompileAsync("Vars[\"answer\"] = PrivateFixture.Value.Answer;", references, CancellationToken.None);
        var values = new Dictionary<string, object?>();
        await Vouchfx.Engine.Compilation.RoslynScriptCompiler.RunIsolatedAsync(compiled,
            new Vouchfx.Engine.Abstractions.ScriptGlobalVariables(values));
        Assert.Equal(42, values["answer"]);
    }

    [Fact]
    public void Duplicate_kind_failure_names_external_and_core_assemblies()
    {
        var directory = Temp();
        Compile(directory, Unique(), Provider.Replace("new(\"probe\", \"external\")", "new(\"http\", \"rest\")"));
        var failure = Assert.Throws<ProviderLoadException>(() => new ProviderRegistryScope(ProviderDirectoryScanner.Load(new[] { directory })));
        Assert.Contains(directory, failure.Message); Assert.Contains("Vouchfx.Steps.Core.HttpRest", failure.Message);
        Assert.Contains("Duplicate step kind", failure.Message);
    }

    [Fact]
    public void Minimum_engine_version_is_enforced_before_schema_contribution()
    {
        var directory = Temp();
        Compile(directory, Unique(), Provider.Replace("\"1.0.0\", \"1.0.0\"", "\"1.0.0\", \"99.0.0\""));
        var failure = Assert.Throws<ProviderLoadException>(() => new ProviderRegistryScope(ProviderDirectoryScanner.Load(new[] { directory })));
        Assert.Contains("requires engine 99.0.0", failure.Message);
    }

    [Fact]
    public void All_catalogue_consumers_accept_repeatable_provider_options()
    {
        var commands = new[] { RunCommand.Build(), ValidateCommand.Build(), ListCommand.Build(), SchemaCommand.Build(), ScaffoldCommand.Build(), PlanCommand.Build() };
        foreach (var command in commands)
        {
            var arguments = command.Name == "scaffold" ? new[] { "--intent", "intent.json" } : command.Name == "plan" ? new[] { "." } : Array.Empty<string>();
            var parsed = command.Parse(arguments.Concat(new[] { "--providers", "one", "--providers", "two", "--no-default-providers" }).ToArray());
            Assert.Empty(parsed.Errors);
        }
    }

    // Shared-framework assemblies outside Microsoft.NETCore.App (Microsoft.AspNetCore.App carries Microsoft.Extensions.*)
    // follow the same "requested <= host" rule as the runtime directory; app-local assemblies need an exact match.
    private static readonly Type s_aspNetCoreFrameworkType = typeof(Microsoft.Extensions.Logging.ILogger);
    private static readonly Type s_appLocalType = typeof(Microsoft.Extensions.AI.AIFunction);

    [Fact]
    public void Host_assembly_fixtures_come_from_the_expected_locations()
    {
        var runtimeDirectory = Path.GetFullPath(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory())
            .TrimEnd(Path.DirectorySeparatorChar);
        var frameworkAssembly = s_aspNetCoreFrameworkType.Assembly.Location;
        Assert.NotEqual(runtimeDirectory, Path.GetDirectoryName(frameworkAssembly));
        Assert.Contains("Microsoft.AspNetCore.App", frameworkAssembly);
        Assert.Equal(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
            Path.GetDirectoryName(s_appLocalType.Assembly.Location));
    }

    [Theory]
    [InlineData(0, true)]   // same version as the host's shared-framework copy
    [InlineData(-1, true)]  // lower: what a provider built against the previous framework packages carries
    [InlineData(1, false)]  // higher than the host: still refused
    public void Shared_framework_reference_outside_the_runtime_directory_may_not_exceed_the_host(int delta, bool admitted)
    {
        var directory = Temp();
        var reference = ReferenceVersion(s_aspNetCoreFrameworkType, delta);
        var path = CompileAgainstForgedReference(directory, Unique(), s_aspNetCoreFrameworkType.Assembly, reference);
        var load = () => ProviderDirectoryScanner.Load(new[] { directory });
        var compatible = ProviderDirectoryScanner.CreateHostCompatibilityCheck()(directory);
        Assert.Equal(admitted, compatible);
        if (admitted)
            Assert.Empty(load());
        else
        {
            var failure = Assert.Throws<ProviderLoadException>(load);
            Assert.Contains(path, failure.Message);
            Assert.Contains("conflicting with host", failure.Message);
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)] // app-local assemblies keep needing an exact match
    [InlineData(1, false)]
    public void App_local_reference_needs_an_exact_match(int delta, bool admitted)
    {
        var directory = Temp();
        var reference = ReferenceVersion(s_appLocalType, delta);
        var path = CompileAgainstForgedReference(directory, Unique(), s_appLocalType.Assembly, reference);
        var load = () => ProviderDirectoryScanner.Load(new[] { directory });
        Assert.Equal(admitted, ProviderDirectoryScanner.CreateHostCompatibilityCheck()(directory));
        if (admitted)
            Assert.Empty(load());
        else
            Assert.Contains(path, Assert.Throws<ProviderLoadException>(load).Message);
    }

    private static Version ReferenceVersion(Type host, int delta)
    {
        var version = host.Assembly.GetName().Version!;
        return delta switch
        {
            0 => version,
            < 0 => new Version(version.Major - 2, 0, 0, 0),
            _ => new Version(version.Major + 1, 0, 0, 0),
        };
    }

    // Compiles a provider-less assembly whose only reference to the host assembly carries the given version and the
    // host assembly's real public key, by compiling against a delay-signed stand-in of that identity.
    private static string CompileAgainstForgedReference(string directory, string name, System.Reflection.Assembly host, Version version)
    {
        var identity = host.GetName();
        var stub = CSharpCompilation.Create(identity.Name,
            new[] { CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{version}\")] namespace ForgedHost {{ public interface IMarker {{ }} }}") },
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => Path.GetFileNameWithoutExtension(path) is "System.Runtime" or "System.Private.CoreLib")
                .Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithCryptoPublicKey(identity.GetPublicKey()!.ToImmutableArray()).WithDelaySign(true));
        using var stubImage = new MemoryStream();
        var emitted = stub.Emit(stubImage, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(metadataOnly: true));
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => Path.GetFileNameWithoutExtension(path) != identity.Name)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromImage(stubImage.ToArray()));
        var compilation = CSharpCompilation.Create(name,
            new[] { CSharpSyntaxTree.ParseText("public sealed class UsesHost { public ForgedHost.IMarker? Marker; }") }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var path = Path.Combine(directory, name + ".dll");
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }

    private static string Compile(string directory, string name, string source, string[]? extraReferences = null, bool includeSdk = true)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => includeSdk || Path.GetFileNameWithoutExtension(path) != "Vouchfx.Sdk")
            .Concat(includeSdk ? new[] { typeof(IStepProvider).Assembly.Location } : Array.Empty<string>())
            .Concat(extraReferences ?? Array.Empty<string>()).Distinct().Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var path = Path.Combine(directory, name + ".dll");
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }
}
