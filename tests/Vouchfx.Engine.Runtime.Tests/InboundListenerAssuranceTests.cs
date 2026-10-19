using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Orchestration.Environments;
using Vouchfx.Engine.Orchestration.HostResources;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Vouchfx.Steps.WebhookListen.Http;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

#pragma warning disable CA1861 // Small independent scenario tables stay beside their expected identities.

/// <summary>Inbound assurance at real runner doors, with an in-memory source lease instead of DCP.</summary>
public sealed class InboundListenerAssuranceTests : IDisposable
{
    private static readonly Assembly[] Providers = { typeof(ScriptCsharpProvider).Assembly, typeof(WebhookListenHttpProvider).Assembly };
    private static readonly StepKindRegistry Registry = StepKindRegistry.BuildAndFreeze(Providers);
    private readonly string _directory = Directory.CreateTempSubdirectory("vouchfx-inbound-assurance-").FullName;
    private readonly string _key;
    private readonly string _encryptedKey;
    private const string Password = "inbound-password-canary";
    private const string Listener = """
        listeners:
          callbacks:
            kind: webhook-listener
            security:
              profile: mtls
              serverCert: server.pem
              serverKey: key.pem
              clientCaCert: ca.pem
        """;
    private const string Steps = """
        steps:
          - id: must-not-run
            type: script.csharp
            code: throw new Exception("STEP_EXECUTED");
          - id: callback
            type: webhook-listen.http
            listener: callbacks
            match: { path: /event }
        """;
    private const string Source = """
        environment:
          source:
            driver: inbound-test
            config: unused.yaml
        """;
    private static string Yaml => Source + "\n" + Listener + "\n" + Steps;

    public InboundListenerAssuranceTests()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=inbound-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("host.docker.internal");
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(Path.Combine(_directory, "server.pem"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(_directory, "ca.pem"), certificate.ExportCertificatePem());
        _key = rsa.ExportPkcs8PrivateKeyPem();
        _encryptedKey = rsa.ExportEncryptedPkcs8PrivateKeyPem(Password,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000));
        File.WriteAllText(Path.Combine(_directory, "key.pem"), _key);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Loader_AcceptsEncryptedServerKeyWithCorrectPassword()
    {
        File.WriteAllText(Path.Combine(_directory, "key.pem"), _encryptedKey);
        using var configuration = ListenerTlsConfiguration.Load(Path.Combine(_directory, "server.pem"),
            Path.Combine(_directory, "key.pem"), Path.Combine(_directory, "ca.pem"), Password, true);
        Assert.True(configuration.RequireClientCertificate);
    }

    [Theory]
    [InlineData("wrong-password")]
    [InlineData("empty-password")]
    [InlineData("superfluous-password")]
    [InlineData("key-mismatch")]
    [InlineData("invalid-ca")]
    [InlineData("deleted-key")]
    [InlineData("replaced-key")]
    public async Task StartupMaterialFailure_HasNoStepOrConfirmationAndDoesNotDiscloseMaterial(string fault)
    {
        var variable = "VOUCHFX_INBOUND_" + Guid.NewGuid().ToString("N");
        var password = fault == "empty-password" ? "" : Password;
        Environment.SetEnvironmentVariable(variable, password);
        var yaml = Yaml;
        if (fault.EndsWith("password", StringComparison.Ordinal))
        {
            yaml = yaml.Replace("serverKey: key.pem", "serverKey: key.pem\n      serverKeyPassword: ${secret:env/" + variable + "}", StringComparison.Ordinal);
        }
        var driver = new TestDriver(_ =>
        {
            switch (fault)
            {
                case "wrong-password":
                    File.WriteAllText(Path.Combine(_directory, "key.pem"), _encryptedKey);
                    Environment.SetEnvironmentVariable(variable, "wrong-password-canary");
                    break;
                case "empty-password": File.WriteAllText(Path.Combine(_directory, "key.pem"), _encryptedKey); break;
                case "key-mismatch":
                    using (var other = RSA.Create(2048)) File.WriteAllText(Path.Combine(_directory, "key.pem"), other.ExportPkcs8PrivateKeyPem());
                    break;
                case "invalid-ca": File.WriteAllText(Path.Combine(_directory, "ca.pem"), "not a CA"); break;
                case "deleted-key": File.Delete(Path.Combine(_directory, "key.pem")); break;
                case "replaced-key": File.WriteAllText(Path.Combine(_directory, "key.pem"), "not a key"); break;
            }
        });
        try
        {
            var ast = Parse(yaml);
            var expected = SecurityDeclarations.Enumerate(ast, _directory);
            var result = await ScenarioRunner.RunScenarioOwningTopologyAsync(Registry, yaml, "startup-failure", expected,
                null, new StringWriter(), _directory, null, default, new EnvironmentDriverRegistry(new[] { driver }));
            Assert.Equal(1, driver.Acquisitions); // mutation happened AFTER validation, at acquisition.
            Assert.Equal(Verdict.EnvironmentError, result.Verdict);
            Assert.Equal(expected, result.Assurance.Declared);
            Assert.Empty(result.Assurance.Confirmed);
            Assert.True(result.Assurance.Unconfirmed);
            var events = string.Join("\n", result.Buffer);
            Assert.DoesNotContain("step-started", events, StringComparison.Ordinal);
            Assert.DoesNotContain("STEP_EXECUTED", events, StringComparison.Ordinal);
            Assert.DoesNotContain("http://", events, StringComparison.Ordinal);
            Assert.DoesNotContain(_directory, events, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Password, events, StringComparison.Ordinal);
            Assert.DoesNotContain("wrong-password-canary", events, StringComparison.Ordinal);
            Assert.Contains("listeners.callbacks.security", events, StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task StartupPasswordResolution_RecordsIntoRunLedgerAndScrubsBothFailureEvents()
    {
        // Make the resolved password also occur in the otherwise safe declared location.
        // This forces the real startup catch's two emission sites to use the shared ledger.
        const string canary = "inboundledgercanary";
        var variable = "VOUCHFX_INBOUND_LEDGER_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, canary);
        try
        {
            var yaml = (Listener + "\n" + Steps)
                .Replace("callbacks", canary, StringComparison.Ordinal)
                .Replace("serverKey: key.pem", "serverKey: key.pem\n      serverKeyPassword: ${secret:env/" + variable + "}", StringComparison.Ordinal);
            var ast = Parse(yaml);
            var pipeline = ProviderPipeline.Compile(ast, Registry, "listener-ledger", _directory);
            Assert.Null(pipeline.Failure);
            File.WriteAllText(Path.Combine(_directory, "key.pem"), _encryptedKey);
            var ledger = new ResolvedSecretLedger();
            var buffer = new List<string>();
            var outcome = await ScenarioRunner.RunScenarioExecutionAgainstTopologyAsync(ast, "listener-ledger", "run-ledger",
                new EmptyTopology(), pipeline.Assembled!, pipeline.CompileReferencePaths, pipeline.HostResourcePlan, buffer,
                new NullScenarioIsolation(), new StringWriter(), _directory, default, sharedLedger: ledger);

            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            Assert.Empty(outcome.InboundConfirmations);
            // The listener scope has been disposed, but its resolved value belongs to the run.
            Assert.Equal(SecretString.RedactedMarker, ledger.Scrub(canary));
            var events = string.Join("\n", buffer);
            Assert.DoesNotContain("step-started", events, StringComparison.Ordinal);
            foreach (var kind in new[] { "environment-error", "scenario-completed" })
            {
                var line = Assert.Single(buffer, line => line.Contains("\"" + kind + "\"", StringComparison.Ordinal));
                using var parsed = JsonDocument.Parse(line);
                // Public resource identifiers retain their spelling; the diagnostic text is scrubbed.
                var diagnostic = parsed.RootElement.GetProperty(kind == "environment-error" ? "detail" : "message").GetString();
                Assert.DoesNotContain(canary, diagnostic, StringComparison.Ordinal);
                Assert.Contains(SecretString.RedactedMarker, diagnostic, StringComparison.Ordinal);
            }
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task PartialListenerFailure_PreservesAlreadyStartedIdentity()
    {
        var ast = Parse(Listener + "\n" + Steps);
        ast = ast with
        {
            Listeners = new Dictionary<string, InboundListenerSpec>(ast.Listeners!)
            {
                ["later"] = ast.Listeners!["callbacks"],
            },
        };
        var pipeline = ProviderPipeline.Compile(Parse(Listener + "\n" + Steps), Registry, "PartialListenerFailure", _directory);
        Assert.Null(pipeline.Failure);
        // Fail continuation of the start plan after a real secure Kestrel endpoint starts.
        // The same catch handles a later bind/start exception; no flaky socket exhaustion
        // or test-only listener factory is needed to pin its exact evidence set.
        var plan = new FailingContinuationPlan(Assert.Single(pipeline.HostResourcePlan));
        var buffer = new List<string>();
        var outcome = await ScenarioRunner.RunScenarioExecutionAgainstTopologyAsync(ast, "partial", "run-partial",
            new EmptyTopology(), pipeline.Assembled!, pipeline.CompileReferencePaths, plan, buffer,
            new NullScenarioIsolation(), new StringWriter(), _directory, default);
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        var evidence = Assert.Single(outcome.InboundConfirmations);
        Assert.Equal(SecurityDeclarations.Inbound("callbacks", ast.Listeners!["callbacks"], _directory), evidence.Identity);
        var assurance = SecurityAssurance.None.Declaring(SecurityDeclarations.Enumerate(ast, _directory))
            .Confirming(outcome.InboundConfirmations);
        Assert.Equal(2, assurance.Declared.Count);
        Assert.Single(assurance.Confirmed);
        Assert.True(assurance.Unconfirmed);
        Assert.DoesNotContain(buffer, line => line.Contains("step-started", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SequentialScenarios_DoNotShareInboundConfirmation(bool failedFirst)
    {
        var driver = new TestDriver(index => File.WriteAllText(Path.Combine(_directory, "key.pem"),
            (index == 1) == failedFirst ? "invalid key" : _key));
        var ast = Parse(Yaml);
        var result = await ScenarioRunner.RunSuiteAsync(new[] { ast, ast }, new[] { "first", "second" },
            new[] { Yaml, Yaml }, Providers, null, new StringWriter(), seedBaseDirectory: _directory,
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        Assert.Equal(2, driver.Acquisitions);
        Assert.Contains(result.ScenarioVerdicts, item => item.Verdict == Verdict.Fail); // first step ran only in the started slot.
        Assert.Contains(result.ScenarioVerdicts, item => item.Verdict == Verdict.EnvironmentError);
        Assert.Equal(SecurityDeclarations.Enumerate(ast, _directory), result.Assurance.Declared);
        Assert.Empty(result.Assurance.Confirmed);
        Assert.True(result.Assurance.Unconfirmed);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("material")]
    [InlineData("acquisition")]
    public async Task SequentialEarlyDoors_RetainInboundCensus(string door)
    {
        var yaml = door == "schema" ? Yaml.Replace("match: { path: /event }", "match: { unknown: true }", StringComparison.Ordinal) : Yaml;
        if (door == "material") File.Delete(Path.Combine(_directory, "key.pem"));
        var driver = new TestDriver(_ => throw new IOException("acquisition-canary"));
        var ast = Parse(yaml);
        var result = await ScenarioRunner.RunSuiteAsync(new[] { ast }, new[] { "early" }, new[] { yaml },
            Providers, null, new StringWriter(), seedBaseDirectory: _directory,
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        Assert.Equal(door == "acquisition" ? 1 : 0, driver.Acquisitions);
        Assert.Equal(SecurityDeclarations.Enumerate(ast, _directory), result.Assurance.Declared);
        Assert.Empty(result.Assurance.Confirmed);
        Assert.True(result.Assurance.Unconfirmed);
    }

    [Fact]
    public async Task SharedSequentialTopology_DoesNotReusePriorScenariosInboundConfirmation()
    {
        var yaml = Yaml.Replace("code: throw new Exception(\"STEP_EXECUTED\");",
            "code: System.IO.File.WriteAllText(" + System.Text.Json.JsonSerializer.Serialize(Path.Combine(_directory, "key.pem"))
                + ", \"invalid key\"); throw new Exception(\"STEP_EXECUTED\");", StringComparison.Ordinal);
        var driver = new TestDriver(_ => { }) { Reuse = true };
        var ast = Parse(yaml);
        var result = await ScenarioRunner.RunSuiteAsync(new[] { ast, ast }, new[] { "started", "unstarted" },
            new[] { yaml, yaml }, Providers, null, new StringWriter(), seedBaseDirectory: _directory,
            environmentDrivers: new EnvironmentDriverRegistry(new[] { driver }));
        Assert.Equal(1, driver.Acquisitions);
        Assert.Equal(Verdict.Fail, result.ScenarioVerdicts[0].Verdict);
        Assert.Equal(Verdict.EnvironmentError, result.ScenarioVerdicts[1].Verdict);
        Assert.Equal(SecurityDeclarations.Enumerate(ast, _directory), result.Assurance.Declared);
        Assert.Empty(result.Assurance.Confirmed);
        Assert.True(result.Assurance.Unconfirmed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoScenarios_UnbuiltInboundDeclarationOrMalformedBlockStillRaises(bool malformed)
    {
        var yaml = malformed ? Listener.Replace("security:\n      profile: mtls\n      serverCert: server.pem\n      serverKey: key.pem\n      clientCaCert: ca.pem", "security: mtls", StringComparison.Ordinal) + "\n" + Steps : Listener + "\n" + Steps;
        var unbuilt = new UnbuiltDocument(yaml, YamlDocumentParser.Parse(yaml));
        var sequential = await ScenarioRunner.RunSuiteAsync(Array.Empty<ScenarioAst>(), Array.Empty<string>(), Array.Empty<string>(),
            Providers, null, new StringWriter(), unbuiltDocuments: new[] { unbuilt });
        var parallel = await ParallelSuiteRunner.RunParallelCoreAsync(Registry, Array.Empty<ScenarioAst>(), Array.Empty<string>(),
            Array.Empty<string>(), null, new StringWriter(), (_, _) => null, 1,
            (_, _, _, _, _, _, _, _, _) => throw new InvalidOperationException("No slot should run"), null,
            unbuiltDocuments: new[] { unbuilt });
        Assert.True(sequential.Assurance.Unconfirmed);
        Assert.True(parallel.Assurance.Unconfirmed);
        Assert.Empty(sequential.Assurance.Confirmed);
        Assert.Empty(parallel.Assurance.Confirmed);
        if (malformed) Assert.Equal(SecurityAbortKind.SecurityDeclarationRejected, sequential.Assurance.Refusal);
        else Assert.Equal(SecurityDeclarations.Enumerate(unbuilt.Document, null), sequential.Assurance.Declared);
    }

    private static ScenarioAst Parse(string yaml) => AstBuilder.Build(YamlDocumentParser.Parse(yaml), Registry);

    [Theory]
    [InlineData("pre-start", false)]
    [InlineData("pre-start", true)]
    [InlineData("cancel", false)]
    [InlineData("cancel", true)]
    [InlineData("escape", false)]
    [InlineData("escape", true)]
    public async Task ParallelSlots_RetainMixedDirectionCensusWithoutCrossConfirming(string failure, bool reverse)
    {
        var mixed = Parse("""
            environment:
              services:
                callbacks:
                  image: example/service
                  security: { profile: tls, endpoint: http }
            """ + "\n" + Listener + "\n" + Steps);
        var plain = Parse(Steps);
        var asts = reverse ? new[] { plain, mixed } : new[] { mixed, plain };
        var names = reverse ? new[] { "plain", "secured" } : new[] { "secured", "plain" };
        using var cancellation = new CancellationTokenSource();
        if (failure == "pre-start") cancellation.Cancel();
        var calls = 0;
        var result = await ParallelSuiteRunner.RunParallelCoreAsync(Registry, asts, names, new[] { "", "" }, null,
            new StringWriter(), (_, _) => null, 1, (_, _, name, declared, _, _, _, _, _) =>
            {
                calls++;
                if (name == "secured")
                {
                    Assert.Equal(2, declared.Count);
                    Assert.Equal(2, declared.Select(identity => identity.Direction).Distinct().Count());
                    Assert.All(declared, identity => Assert.Equal("callbacks", identity.Name));
                    if (failure == "cancel") throw new OperationCanceledException();
                    throw new IOException("slot-escape");
                }
                return Task.FromResult(new ScenarioCoreResult(Verdict.Pass, new List<string>())
                {
                    Assurance = SecurityAssurance.None.Declaring(declared),
                });
            }, _directory, ct: cancellation.Token);
        Assert.Equal(failure == "pre-start" ? 0 : 2, calls);
        Assert.Equal(SecurityDeclarations.Enumerate(mixed, _directory), result.Assurance.Declared);
        Assert.Empty(result.Assurance.Confirmed);
        Assert.True(result.Assurance.Unconfirmed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelConfirmedSibling_CannotConfirmUnstartedSamePolicyScenario(bool reverse)
    {
        var ast = Parse(Listener + "\n" + Steps);
        var names = reverse ? new[] { "failed", "confirmed" } : new[] { "confirmed", "failed" };
        var result = await ParallelSuiteRunner.RunParallelCoreAsync(Registry, new[] { ast, ast }, names,
            new[] { "", "" }, null, new StringWriter(), (_, _) => null, 2,
            (_, _, name, declared, _, _, _, _, _) => Task.FromResult(new ScenarioCoreResult(
                name == "confirmed" ? Verdict.Pass : Verdict.EnvironmentError, new List<string>())
            {
                Assurance = name == "confirmed"
                    ? SecurityAssurance.None.Declaring(declared).Confirming(declared.Select(identity => new SecurityConfirmationEvidence(identity)).ToArray())
                    : SecurityAssurance.None.Declaring(declared),
            }), _directory);
        Assert.Equal(SecurityDeclarations.Enumerate(ast, _directory), result.Assurance.Declared);
        Assert.Empty(result.Assurance.Confirmed);
        Assert.True(result.Assurance.Unconfirmed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedDivergentListener_PreservesItsOwnPolicyAndBase(bool reverse)
    {
        var baseline = Parse(Listener + "\n" + Steps);
        var rejected = Parse((Listener + "\n" + Steps).Replace("key.pem", "other.pem", StringComparison.Ordinal));
        var scenarios = reverse ? new[] { rejected, baseline } : new[] { baseline, rejected };
        var folded = ScenarioRunner.FoldRejectedDivergent(scenarios,
            reverse ? new[] { false, true } : new[] { true, false },
            reverse ? new SecurityAbortKind?[] { SecurityAbortKind.AuthoringFault, null }
                : new SecurityAbortKind?[] { null, SecurityAbortKind.AuthoringFault },
            reverse ? 1 : 0);
        Assert.Equal(SecurityDeclarations.Enumerate(rejected, null), folded.Declared);
        Assert.Empty(folded.Confirmed);
        Assert.True(folded.Unconfirmed);
    }

    [Fact]
    public async Task OutboundOnlyEscapedInfrastructureSlot_PreservesOptionalExitPolicy()
    {
        var ast = Parse("""
            environment:
              services:
                callbacks:
                  image: example/service
                  security: { profile: tls, endpoint: http }
            """ + "\n" + Steps);
        var result = await ParallelSuiteRunner.RunParallelCoreAsync(Registry, new[] { ast }, new[] { "outbound" },
            new[] { "" }, null, new StringWriter(), (_, _) => null, 1,
            (_, _, _, _, _, _, _, _, _) => throw new IOException("infrastructure"), _directory);
        Assert.Single(result.Assurance.Declared);
        Assert.Empty(result.Assurance.Confirmed);
        Assert.Equal(SecurityAbortKind.TopologyUnavailable, result.Assurance.Refusal);
        Assert.False(result.Assurance.Unconfirmed);
    }

    private sealed class TestDriver : IEnvironmentDriver
    {
        private readonly Action<int> _acquired;
        public TestDriver(Action<int> acquired) => _acquired = acquired;
        public string Id => "inbound-test";
        public bool Reuse { get; init; }
        public int Acquisitions { get; private set; }
        public EnvironmentPlan Bind(EnvironmentSourceSpec source, string suiteDirectory) => new(
            new EnvironmentDeclarations(new Dictionary<string, DeclaredServiceInfo>(), new Dictionary<string, string>()),
            "inbound-test", Reuse ? EnvironmentStatePolicy.Inline : EnvironmentStatePolicy.FreshOwned, (_, _) =>
            {
                _acquired(++Acquisitions);
                return Task.FromResult<IKeptTopology>(new EmptyTopology());
            });
    }

    private sealed class FailingContinuationPlan : IReadOnlyList<HostResourcePlanEntry>
    {
        private readonly HostResourcePlanEntry _first;
        public FailingContinuationPlan(HostResourcePlanEntry first) => _first = first;
        public int Count => 2;
        public HostResourcePlanEntry this[int index] => index == 0 ? _first : throw new IOException("later start failure");
        public IEnumerator<HostResourcePlanEntry> GetEnumerator()
        {
            yield return _first;
            throw new IOException("later start failure");
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class EmptyTopology : IKeptTopology
    {
        public IReadOnlyList<SecurityConfirmation> SecurityConfirmations => Array.Empty<SecurityConfirmation>();
        public IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices => Array.Empty<EndpointSelectionNotice>();
        public IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices => Array.Empty<EndpointTrustNotice>();
        public IReadOnlyDictionary<string, object> DiscoveredServices { get; } = new Dictionary<string, object>();
        public IReadOnlyList<string> DependencyNames => Array.Empty<string>();
        public IReadOnlyDictionary<string, string> DependencyTypes { get; } = new Dictionary<string, string>();
        public Task ReseedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
