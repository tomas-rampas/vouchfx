using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.Kafka;
using Vouchfx.Steps.MqPublish.Kafka;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class KafkaMixedVersionHelperTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompiledLegacyAndPublicV2Providers_AndBothCoreProviders_AssembleAndCompileTogether(bool avro)
    {
        var legacySource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "kafka-security-helper.v1.csx"));
        Assert.Equal("ba35bdff6ad5103a55cbf7ebb6e0f989506d314a470af75c231bf55df1299fd0",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacySource))).ToLowerInvariant());
        var legacy = EmitCompiledProvider(legacySource, publicV2: false);
        var publicSource = (string)typeof(KafkaSecurityV2Helper).GetField(nameof(KafkaSecurityV2Helper.Source))!.GetRawConstantValue()!;
        var customV2 = EmitCompiledProvider(publicSource, publicV2: true);
        var publish = new MqPublishKafkaProvider();
        var expect = new MqExpectKafkaProvider();
        var plans = new[]
        {
            ("legacy", legacy),
            ("custom-v2", customV2),
            ("publish", publish.Emit(publish.Bind(Node(avro), new BindingContext()), new CompileContext("publish"))),
            ("expect", expect.Emit(expect.Bind(Node(avro), new BindingContext()), new CompileContext("expect"))),
        };

        // Use the real assembler and compiler, including both current providers' own fragments.
        var assembled = CsxAssembler.Assemble(plans);
        var references = ScenarioRunner.BclReferencePaths()
            .Concat(publish.CompileReferenceAssemblies.Select(assembly => assembly.Location))
            .Concat(expect.CompileReferenceAssemblies.Select(assembly => assembly.Location));
        var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource, additionalReferencePaths: references.Distinct().ToArray());
        Assert.NotEmpty(compiled.Image);
        var currentSource = Assert.Single(plans[2].Item2.RequiredHelpers,
            helper => helper.StartsWith("static class KafkaSecurity_V2_Helpers", StringComparison.Ordinal));
        Assert.Equal(publicSource, currentSource);
        Assert.Equal(publicSource, Assert.Single(customV2.RequiredHelpers));
        // This fingerprint belongs to emitted identity V2. Future behavior needs a new identity.
        Assert.Equal("780484a478c04266bd1a4fa89ced518c98e1d8ac8af881835213b7c22c7e2945",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(currentSource))).ToLowerInvariant());
        Assert.Equal(1, assembled.CsxSource.Split("static class KafkaSecurity_Helpers", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, assembled.CsxSource.Split("static class KafkaSecurity_V2_Helpers", StringSplitOptions.None).Length - 1);
        Assert.Equal(legacySource, typeof(KafkaSecurityHelper).GetField(nameof(KafkaSecurityHelper.Source))!.GetRawConstantValue());
    }

    private static CsxFragment EmitCompiledProvider(string helperSource, bool publicV2)
    {
        // Compile each provider into its own DLL: the opt-in uses the public SDK constant;
        // the legacy consumer carries the historical literal with today's SDK interfaces.
        // This does not claim to use a published historical SDK or provider DLL.
        var helperExpression = publicV2 ? "KafkaSecurityV2Helper.Source" : JsonSerializer.Serialize(helperSource);
        var helperIdentity = publicV2 ? "KafkaSecurity_V2_Helpers" : "KafkaSecurity_Helpers";
        var generation = publicV2 ? "PublicV2" : "Legacy";
        var source = $$"""
            using Vouchfx.Sdk;
            namespace External.Kafka;
            public sealed class CustomModel : IStepModel { }
            public sealed class CustomProvider : IStepCompiler<CustomModel>
            {
                public CsxFragment Emit(CustomModel model, ICompileContext context) => new CsxFragment(
                    System.Array.Empty<string>(),
                    new[] { {{helperExpression}} },
                    "{ {{helperIdentity}}.ConfigureClient(Security, \"legacy\", new Confluent.Kafka.ProducerConfig()); }");
            }
            """;
        var references = ScenarioRunner.BclReferencePaths().Append(typeof(CsxFragment).Assembly.Location)
            .Distinct().Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("External.Kafka." + generation + "Provider",
            new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        image.Position = 0;
        var context = new AssemblyLoadContext(generation + "-kafka-provider", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(image);
            var providerType = assembly.GetType("External.Kafka.CustomProvider", throwOnError: true)!;
            var model = Activator.CreateInstance(assembly.GetType("External.Kafka.CustomModel", throwOnError: true)!);
            var fragment = Assert.IsType<CsxFragment>(providerType.GetMethod("Emit")!.Invoke(
                Activator.CreateInstance(providerType), new[] { model, new CompileContext("legacy") }));
            Assert.Equal(helperSource, Assert.Single(fragment.RequiredHelpers));
            return fragment;
        }
        finally
        {
            context.Unload();
        }
    }

    private static YamlMappingNode Node(bool avro)
    {
        var node = new YamlMappingNode { { "target", "broker" }, { "topic", "orders" }, { "payload", "payload" } };
        if (avro)
            node.Add("avro", new YamlMappingNode
            {
                { "schemaRegistry", "broker" }, { "subject", "orders-value" },
                { "schema", "{\"type\":\"record\",\"name\":\"Order\",\"fields\":[]}" },
                { "record", new YamlMappingNode() },
            });
        return node;
    }

    private sealed class BindingContext : IBindingContext { }

    private sealed class CompileContext : ICompileContext
    {
        internal CompileContext(string stepId) => StepId = stepId;
        public string StepId { get; }
        public string SuiteNamespace => "MixedKafkaVersions";
        public string SuiteDirectory => Path.GetTempPath();
        public IReadOnlyDictionary<string, string> Captures { get; } = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } = new Dictionary<string, CaptureExpr>();
    }
}
