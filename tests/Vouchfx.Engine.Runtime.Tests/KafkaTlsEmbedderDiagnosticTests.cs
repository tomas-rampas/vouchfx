using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Security;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.Kafka;
using Vouchfx.Steps.MqPublish.Kafka;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class KafkaTlsEmbedderDiagnosticTests
{
    private const string MissingKey = "without a matching 'clientKey'";
    private const string UnexpectedCaRead = "unexpected CA read before embedder refusal";

    public static IEnumerable<object[]> Cases()
    {
        foreach (var publish in new[] { true, false })
            foreach (var avro in new[] { true, false })
                foreach (var fault in new[] { "tls-passphrase", "tls-full-pair", "unknown-profile", "mtls-passphrase" })
                    yield return new object[] { publish, avro, fault };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EmittedProvider_RefusesBeforeClientBuild_WithProfileSpecificDiagnosticPriority(
        bool publish, bool avro, string fault)
    {
        // The TLS-only case uses the production lazy passphrase getter, bypassing schema and
        // preflight exactly as an embedding host can. The remaining cases exercise precedence.
        var realSecurity = SecurityConfigurationAccessor.Build(PassphraseOnlyAst(),
            Path.GetTempPath(), NullSecretAccessor.Instance, pathDisclosures: null);
        try
        {
            var profile = fault == "unknown-profile" ? "future.sasl" : fault == "mtls-passphrase" ? "mtls" : "tls";
            var material = new ProbeMaterial(fault == "tls-full-pair",
                fault == "tls-passphrase" ? realSecurity.For("broker")!.Certificates : null);
            var security = new StubAccessor(profile, material);
            var node = new YamlMappingNode
            {
                { "target", "broker" }, { "topic", "orders" }, { "payload", "payload" },
            };
            if (avro)
                node.Add("avro", new YamlMappingNode
                {
                    { "schemaRegistry", "broker" }, { "subject", "orders-value" },
                    { "schema", "{\"type\":\"record\",\"name\":\"Order\",\"fields\":[]}" },
                    { "record", new YamlMappingNode() },
                });
            CsxFragment fragment;
            ICompileReferenceContributor references;
            if (publish)
            {
                var provider = new MqPublishKafkaProvider();
                fragment = provider.Emit(provider.Bind(node, new BindingContext()), new CompileContext());
                references = provider;
            }
            else
            {
                var provider = new MqExpectKafkaProvider();
                fragment = provider.Emit(provider.Bind(node, new BindingContext()), new CompileContext());
                references = provider;
            }
            // Compile the provider's actual fragment, including its inlined SDK helper body.
            var assembled = CsxAssembler.Assemble(new[] { ("step", fragment) });
            Assert.Contains("static class KafkaSecurity_V2_Helpers", assembled.CsxSource, StringComparison.Ordinal);
            var paths = ScenarioRunner.BclReferencePaths().Concat(
                references.CompileReferenceAssemblies.Select(assembly => assembly.Location));
            var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource, additionalReferencePaths: paths.ToArray());
            var vars = new Dictionary<string, object?>
            {
                [VarKeys.Connection("broker")] = "127.0.0.1:1",
                [VarKeys.Service("broker-sr")] = "http://127.0.0.1:1",
            };
            var globals = new ScriptGlobalVariables(vars, new Dictionary<string, object>(), NullSecretAccessor.Instance,
                Vouchfx.Engine.Abstractions.Webhooks.NullWebhookCaptureAccessor.Instance,
                Vouchfx.Engine.Abstractions.Traces.NullTraceCaptureAccessor.Instance, stepEvents: null, security);
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);
            var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome("step")]);
            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            using var observation = JsonDocument.Parse(outcome.Observation!);
            var error = observation.RootElement.GetProperty("error").GetString()!;
            var expected = fault switch
            {
                "tls-full-pair" => "presents no client identity",
                "unknown-profile" => "has no defined Kafka client behaviour",
                _ => MissingKey,
            };
            Assert.Contains(expected, error, StringComparison.Ordinal);
            Assert.DoesNotContain(UnexpectedCaRead, error, StringComparison.Ordinal);
            Assert.Equal(fault is "tls-full-pair" or "unknown-profile" ? 0 : 1, material.PasswordReads);
            Assert.Equal(0, material.CaReads);
        }
        finally
        {
            (realSecurity as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [InlineData("tls-passphrase")]
    [InlineData("tls-full-pair")]
    [InlineData("unknown-profile")]
    [InlineData("mtls-passphrase")]
    public async Task PublicV2Source_RefusesWithProfileSpecificDiagnosticPriority(string fault)
    {
        var realSecurity = SecurityConfigurationAccessor.Build(PassphraseOnlyAst(),
            Path.GetTempPath(), NullSecretAccessor.Instance, pathDisclosures: null);
        try
        {
            var profile = fault == "unknown-profile" ? "future.sasl" : fault == "mtls-passphrase" ? "mtls" : "tls";
            var material = new ProbeMaterial(fault == "tls-full-pair",
                fault == "tls-passphrase" ? realSecurity.For("broker")!.Certificates : null);
            var security = new StubAccessor(profile, material);
            var fragment = new CsxFragment(Array.Empty<string>(), new[] { KafkaSecurityV2Helper.Source },
                "{ KafkaSecurity_V2_Helpers.ConfigureClient(Security, \"broker\", new Confluent.Kafka.ProducerConfig()); }");
            var assembled = CsxAssembler.Assemble(new[] { ("custom", fragment) });
            var paths = ScenarioRunner.BclReferencePaths().Append(typeof(Confluent.Kafka.ProducerConfig).Assembly.Location);
            var compiled = RoslynScriptCompiler.CompileOnce(assembled.CsxSource, additionalReferencePaths: paths.ToArray());
            var globals = new ScriptGlobalVariables(new Dictionary<string, object?>(), new Dictionary<string, object>(),
                NullSecretAccessor.Instance, Vouchfx.Engine.Abstractions.Webhooks.NullWebhookCaptureAccessor.Instance,
                Vouchfx.Engine.Abstractions.Traces.NullTraceCaptureAccessor.Instance, stepEvents: null, security);
            var error = await Assert.ThrowsAsync<SecurityMaterialException>(
                () => RoslynScriptCompiler.RunIsolatedAsync(compiled, globals));
            var expected = fault switch
            {
                "tls-full-pair" => "presents no client identity",
                "unknown-profile" => "has no defined Kafka client behaviour",
                _ => MissingKey,
            };
            Assert.Contains(expected, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(UnexpectedCaRead, error.Message, StringComparison.Ordinal);
            Assert.Equal(fault is "tls-full-pair" or "unknown-profile" ? 0 : 1, material.PasswordReads);
            Assert.Equal(0, material.CaReads);
        }
        finally
        {
            (realSecurity as IDisposable)?.Dispose();
        }
    }

    private static ScenarioAst PassphraseOnlyAst() => new(
        Metadata: null,
        Environment: new EnvironmentSpec(new Dictionary<string, ServiceSpec>
        {
            ["broker"] = new ServiceSpec("acme/broker:1.0", null, null, null, null)
            {
                Security = new SecuritySpec("tls", "9093", null, null, null, null)
                { ClientKeyPassword = "${secret:env/UNRESOLVED_EMBEDDER_REFERENCE}" },
            },
        }, null, null, null, null),
        Variables: new Dictionary<string, string>(), Steps: Array.Empty<StepNode>());

    private sealed class BindingContext : IBindingContext { }

    private sealed class CompileContext : ICompileContext
    {
        public string StepId => "step";
        public string SuiteNamespace => "Embedder396";
        public string SuiteDirectory => Path.GetTempPath();
        public IReadOnlyDictionary<string, string> Captures { get; } = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } = new Dictionary<string, CaptureExpr>();
    }

    private sealed class StubAccessor : ISecurityConfigurationAccessor, ISecurityConfiguration
    {
        internal StubAccessor(string profile, ISecurityCertificateMaterial material)
        {
            Profile = profile;
            Certificates = material;
        }
        public ISecurityConfiguration? For(string targetName) => targetName == "broker" ? this : null;
        public string Profile { get; }
        public ISecurityCertificateMaterial? Certificates { get; }
    }

    private sealed class ProbeMaterial : ISecurityCertificateMaterial
    {
        private readonly bool _fullPair;
        private readonly ISecurityCertificateMaterial? _actual;
        internal ProbeMaterial(bool fullPair, ISecurityCertificateMaterial? actual)
        {
            _fullPair = fullPair;
            _actual = actual;
        }
        internal int PasswordReads { get; private set; }
        internal int CaReads { get; private set; }
        public string? ClientCertificatePath => _fullPair ? "client.pem" : null;
        public string? ClientKeyPath => _fullPair ? "key.pem" : null;
        public string? CaCertificatePath
        {
            get
            {
                // Abort before native-client construction even when the regression is present.
                CaReads++;
                throw new SecurityMaterialException(UnexpectedCaRead);
            }
        }
        public SecretString? ClientKeyPassword
        {
            get
            {
                PasswordReads++;
                return _actual is null ? throw new SecurityMaterialException(MissingKey) : _actual.ClientKeyPassword;
            }
        }
        public X509Certificate2? CaCertificate => null;
        public X509Certificate2? ClientCertificate => null;
        public bool TrustsRemoteCertificate(X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors) => false;
    }
}
