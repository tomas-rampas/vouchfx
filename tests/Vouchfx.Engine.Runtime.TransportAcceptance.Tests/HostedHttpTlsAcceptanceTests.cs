using System.Collections.Concurrent;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Security;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Runtime.TransportAcceptance.Tests;

public sealed class HostedHttpTlsAcceptanceTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Descriptive xUnit names; HostedTlsFact derives Xunit.FactAttribute.")]
    [HostedTlsFact]
    public async Task DeclaredSecurity_OmittedServerIssuer_NoAutomaticAiaFetch()
    {
        AssertFramingControls();
        await using var aia = new OwnedAiaServer();
        var controlUrl = aia.IssuerUrl(Guid.NewGuid().ToString("N"));
        bool controlBuilt;
        ServerObservation controlObservation;
        object controlCertificates;
        using (var control = HostedHttpTlsCertificateBed.CreateAiaServer(controlUrl))
        {
            aia.Publish(controlUrl, control.Issuer.RawData);
            await aia.ProveAvailableAsync(controlUrl, control.Issuer.RawData);
            var beginControlHandshake = aia.DeferAutomaticResponsesUntilServerHandshake(controlUrl);
            await using var controlServer = new OwnedTlsServer(control.Server, control.Root, clientRequired: false, beginControlHandshake);
            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
                DisableCertificateDownloads = false,
                UrlRetrievalTimeout = TimeSpan.FromSeconds(5),
            };
            policy.CustomTrustStore.Add(control.Root);
            policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
            handler.SslOptions.CertificateChainPolicy = policy;
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await client.GetAsync(controlServer.Url);
            controlBuilt = response.IsSuccessStatusCode;
            controlObservation = await controlServer.Completed;
            controlCertificates = new
            {
                rootDer = Convert.ToBase64String(control.Root.RawData),
                issuerDer = Convert.ToBase64String(control.Issuer.RawData),
                serverDer = Convert.ToBase64String(control.Server.RawData),
            };
            // Same setup phase, leaf-only wire and live AIA endpoint as production.
            // This must fetch successfully after setup: a cached 404 cannot be proof.
            Assert.True(controlBuilt && controlObservation.Authenticated);
            Assert.Equal(0x0303, controlObservation.Sent.ServerHelloVersion);
            Assert.Equal(control.Server.RawData, Assert.Single(controlObservation.Sent.Certificates));
            Assert.True(aia.AutomaticRequests(controlUrl) > 0);
        }
        var url = aia.IssuerUrl(Guid.NewGuid().ToString("N"));
        using var bed = HostedHttpTlsCertificateBed.CreateAiaServer(url);
        aia.Publish(url, bed.Issuer.RawData);
        await aia.ProveAvailableAsync(url, bed.Issuer.RawData);
        var beginProductionHandshake = aia.DeferAutomaticResponsesUntilServerHandshake(url);
        await using var server = new OwnedTlsServer(bed.Server, bed.Root, clientRequired: false, beginProductionHandshake);
        using var security = BuildAccessor(bed, mtls: false);
        var outcome = await RunProviderAsync(server.Url, bed.SuiteDirectory, security);
        var observation = await server.Completed;
        await aia.ProveAvailableAsync(url, bed.Issuer.RawData);
        var publicEvidence = Evidence(nameof(DeclaredSecurity_OmittedServerIssuer_NoAutomaticAiaFetch), bed, observation, security, outcome,
            new
            {
                nativeControlBuilt = controlBuilt,
                nativeControlAiaPath = new Uri(controlUrl).AbsolutePath,
                productionAiaPath = new Uri(url).AbsolutePath,
                nativeControlAuthenticated = controlObservation.Authenticated,
                nativeControlServerHelloVersion = controlObservation.Sent.ServerHelloVersion,
                nativeControlProtocol = controlObservation.Protocol?.ToString(),
                nativeControlCertificates = controlCertificates,
                nativeControlServerCertificateMessage = Convert.ToBase64String(controlObservation.Sent.CertificateMessage!),
                nativeControlAutomaticFetches = aia.AutomaticRequests(controlUrl),
                nativeControlServerCredentialRequests = aia.ServerCredentialRequests(controlUrl),
                productionAutomaticFetches = aia.AutomaticRequests(url),
                productionServerCredentialRequests = aia.ServerCredentialRequests(url),
                endpointControlResponses = aia.Requests.Count(request => request.Control && request.Status == 200),
                requests = aia.Requests.ToArray(),
            });
        WriteEvidence(publicEvidence);
        Assert.True(controlBuilt && aia.AutomaticRequests(controlUrl) > 0,
            "The independent native AIA control could not fetch/build on " + RuntimeInformation.OSDescription + "; no suppression proof is claimed for this platform.");
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.False(observation.Authenticated);
        Assert.Equal(0x0303, observation.Sent.ServerHelloVersion);
        Assert.Equal(bed.Server.RawData, Assert.Single(observation.Sent.Certificates));
        Assert.DoesNotContain(observation.Sent.Certificates, certificate => certificate.AsSpan().SequenceEqual(bed.Issuer.RawData));
        Assert.Equal(0, aia.AutomaticRequests(url));
        Assert.Equal(3, aia.Requests.Count(request => request.Control && request.Status == 200));
        var decision = Assert.Single(security.Decisions);
        Assert.False(decision.Trusted);
        Assert.Equal(SslPolicyErrors.RemoteCertificateChainErrors, decision.Errors);
        Assert.Equal(Sha(bed.Server.RawData), decision.LeafSha256);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Descriptive xUnit names; HostedTlsFact derives Xunit.FactAttribute.")]
    [HostedTlsFact]
    public Task ClientBundle_SuppliedIntermediate_IsPresentOnTls12Wire() => ClientArmAsync("supplied", expectedPass: true, nameof(ClientBundle_SuppliedIntermediate_IsPresentOnTls12Wire));
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Descriptive xUnit names; HostedTlsFact derives Xunit.FactAttribute.")]
    [HostedTlsFact]
    public Task ClientBundle_OmittedIntermediate_RefusesOnTls12Wire() => ClientArmAsync("omitted", expectedPass: false, nameof(ClientBundle_OmittedIntermediate_RefusesOnTls12Wire));
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Descriptive xUnit names; HostedTlsFact derives Xunit.FactAttribute.")]
    [HostedTlsFact]
    public Task ClientBundle_MismatchedIntermediate_RefusesOnTls12Wire() => ClientArmAsync("mismatched", expectedPass: false, nameof(ClientBundle_MismatchedIntermediate_RefusesOnTls12Wire));

    // Pure framing controls use literal synthetic payloads; no certificate parsing,
    // minting/import, store access, TLS or socket is reachable from this method.
    private static void AssertFramingControls()
    {
        byte[] certificate = { 11, 0, 0, 8, 0, 0, 5, 0, 0, 2, 48, 0 };
        byte[] fragmented = { 22, 3, 3, 0, 5, 11, 0, 0, 8, 0, 22, 3, 3, 0, 7, 0, 5, 0, 0, 2, 48, 0 };
        var capture = new Tls12CertificateCapture();
        foreach (var value in fragmented) { capture.Append(new[] { value }); }
        Assert.Equal(certificate, capture.CertificateMessage);
        Assert.Equal(new byte[] { 48, 0 }, Assert.Single(capture.Certificates));
        capture.Append(new byte[] { 20, 3, 3, 0, 1, 1, 22, 3, 3, 0, 1, 255 });
        capture.Append(new byte[] { 255 });
        Assert.Equal(certificate, capture.CertificateMessage);
        var empty = new Tls12CertificateCapture();
        empty.Append(new byte[] { 22, 3, 3, 0, 7, 11, 0, 0, 3, 0, 0, 0 });
        Assert.NotNull(empty.CertificateMessage);
        Assert.Empty(empty.Certificates);
        Assert.Throws<InvalidDataException>(() => new Tls12CertificateCapture().Append(new byte[] { 22, 3, 3, 0, 7, 11, 0, 0, 3, 0, 0, 1 }));
        Assert.Throws<InvalidDataException>(() => new Tls12CertificateCapture().Append(new byte[256 * 1024 + 1]));
    }

    private static async Task ClientArmAsync(string candidate, bool expectedPass, string fact)
    {
        using var bed = HostedHttpTlsCertificateBed.CreateClientChain(candidate);
        await using var server = new OwnedTlsServer(bed.Server, bed.Root, clientRequired: true);
        using var security = BuildAccessor(bed, mtls: true);
        var outcome = await RunProviderAsync(server.Url, bed.SuiteDirectory, security);
        var observation = await server.Completed;
        WriteEvidence(Evidence(fact, bed, observation, security, outcome, new { candidate }));
        Assert.Equal(0x0303, observation.Sent.ServerHelloVersion);
        Assert.NotNull(observation.Received.CertificateMessage);
        Assert.NotEmpty(security.Decisions);
        Assert.All(security.Decisions, decision => Assert.True(decision.Trusted));
        if (expectedPass)
        {
            Assert.Equal(Verdict.Pass, outcome.Verdict);
            Assert.True(observation.Authenticated);
            Assert.True(Assert.IsType<ClientTrustDecision>(observation.ClientTrust).Trusted);
            Assert.Equal(SslProtocols.Tls12, observation.Protocol);
            Assert.Equal(bed.Client!.RawData, observation.Received.Certificates[0]);
            Assert.Contains(observation.Received.Certificates, certificate => certificate.AsSpan().SequenceEqual(bed.Issuer.RawData));
        }
        else
        {
            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            Assert.False(observation.Authenticated);
            Assert.Contains(observation.FailureType, new[] { nameof(AuthenticationException), nameof(IOException) });
            var clientTrust = Assert.IsType<ClientTrustDecision>(observation.ClientTrust);
            Assert.False(clientTrust.Trusted);
            Assert.DoesNotContain(observation.Received.Certificates, certificate => certificate.AsSpan().SequenceEqual(bed.Issuer.RawData));
            if (observation.Received.Certificates.Count > 0)
            {
                Assert.Equal(bed.Client!.RawData, observation.Received.Certificates[0]);
                Assert.Contains(nameof(X509ChainStatusFlags.PartialChain), clientTrust.ChainErrors);
            }
            else { Assert.Contains("RemoteCertificateMissing", clientTrust.ChainErrors); }
        }
    }

    private static RecordingAccessor BuildAccessor(HostedHttpTlsCertificateBed bed, bool mtls)
    {
        var specification = new SecuritySpec(mtls ? "mtls" : "tls", "8443", "ca.pem", mtls ? "client.pem" : null, mtls ? "client-key.pem" : null, null);
        var ast = new ScenarioAst(null,
            new EnvironmentSpec(new Dictionary<string, ServiceSpec> { ["sut"] = new("fixture:never-started", null, null, null, null) { Security = specification } }, null, null, null, null),
            new Dictionary<string, string>(), Array.Empty<StepNode>());
        return new RecordingAccessor(SecurityConfigurationAccessor.Build(ast, bed.SuiteDirectory, secrets: null, pathDisclosures: null));
    }

    private static async Task<StepOutcome> RunProviderAsync(string url, string directory, ISecurityConfigurationAccessor security)
    {
        var fragment = new HttpRestProvider().Emit(new HttpRestModel("sut", "GET", "/", null, null, new HttpExpect(200)), new Context(directory));
        Assert.Contains(HttpTlsHelper.Source, fragment.RequiredHelpers);
        var source = CsxAssembler.Assemble(new[] { ("request", fragment) }).CsxSource;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Append(typeof(Json.Path.JsonPath).Assembly.Location).ToArray();
        var compiled = RoslynScriptCompiler.CompileOnce(source, additionalReferencePaths: references);
        var vars = new Dictionary<string, object?> { [VarKeys.Service("sut")] = url };
        var globals = new ScriptGlobalVariables(vars, new Dictionary<string, object>(),
            Vouchfx.Engine.Abstractions.Secrets.NullSecretAccessor.Instance,
            Vouchfx.Engine.Abstractions.Webhooks.NullWebhookCaptureAccessor.Instance,
            Vouchfx.Engine.Abstractions.Traces.NullTraceCaptureAccessor.Instance, stepEvents: null, security);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals, cancellationToken: timeout.Token);
        return Assert.IsType<StepOutcome>(vars[VarKeys.Outcome("request")]);
    }

    private static string Sha(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static object Evidence(string fact, HostedHttpTlsCertificateBed bed, ServerObservation observation, RecordingAccessor security, StepOutcome outcome, object controls) => new
    {
        schemaVersion = 2,
        fact,
        platform = RuntimeInformation.OSDescription,
        fixtureNonce = bed.Nonce,
        scope = "Owned loopback TLS1.2; no general no-network/store/private-key claim",
        rootDer = Convert.ToBase64String(bed.Root.RawData),
        issuerDer = Convert.ToBase64String(bed.Issuer.RawData),
        serverDer = Convert.ToBase64String(bed.Server.RawData),
        clientDer = bed.Client is null ? null : Convert.ToBase64String(bed.Client.RawData),
        clientCertificateMessage = observation.Received.CertificateMessage is null ? null : Convert.ToBase64String(observation.Received.CertificateMessage),
        serverCertificateMessage = observation.Sent.CertificateMessage is null ? null : Convert.ToBase64String(observation.Sent.CertificateMessage),
        serverHelloVersion = observation.Sent.ServerHelloVersion,
        authenticated = observation.Authenticated,
        protocol = observation.Protocol?.ToString(),
        failureType = observation.FailureType,
        failurePhase = observation.FailureType is null ? null : observation.Authenticated ? "http" : "authentication",
        clientTrust = observation.ClientTrust,
        verdict = outcome.Verdict.ToString(),
        decisions = security.Decisions.ToArray(),
        controls,
    };
    private static void WriteEvidence(object evidence)
    {
        var directory = Environment.GetEnvironmentVariable("VOUCHFX_TLS_EVIDENCE_DIRECTORY") ?? throw new InvalidOperationException("Hosted evidence directory is required.");
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(evidence, s_jsonOptions);
        using var document = JsonDocument.Parse(json);
        var fact = document.RootElement.GetProperty("fact").GetString()!;
        File.WriteAllText(Path.Combine(directory, fact + ".json"), json);
    }

    private sealed class Context : ICompileContext
    {
        internal Context(string directory) => SuiteDirectory = directory;
        public string SuiteDirectory { get; }
        public string StepId => "request";
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } = new Dictionary<string, CaptureExpr>();
    }

    internal sealed record TrustDecision(SslPolicyErrors Errors, bool Trusted, string? LeafSha256);
    // Observation-only forwarders. Every material/property/verdict comes from the real
    // accessor; this does not supply certificates, set policies or invent a trust result.
    private sealed class RecordingAccessor : ISecurityConfigurationAccessor, IDisposable
    {
        private readonly ISecurityConfigurationAccessor _inner;
        internal RecordingAccessor(ISecurityConfigurationAccessor inner) => _inner = inner;
        internal ConcurrentQueue<TrustDecision> Decisions { get; } = new();
        public ISecurityConfiguration? For(string targetName)
        {
            var configuration = _inner.For(targetName);
            return configuration is null ? null : new Configuration(configuration, this);
        }
        public void Dispose() => ((IDisposable)_inner).Dispose();
        private sealed class Configuration : ISecurityConfiguration
        {
            private readonly ISecurityConfiguration _inner;
            internal Configuration(ISecurityConfiguration inner, RecordingAccessor owner) { _inner = inner; Certificates = inner.Certificates is null ? null : new Material(inner.Certificates, owner); }
            public string Profile => _inner.Profile;
            public ISecurityCertificateMaterial? Certificates { get; }
        }
        private sealed class Material : ISecurityCertificateMaterial
        {
            private readonly ISecurityCertificateMaterial _inner;
            private readonly RecordingAccessor _owner;
            internal Material(ISecurityCertificateMaterial inner, RecordingAccessor owner) { _inner = inner; _owner = owner; }
            public string? CaCertificatePath => _inner.CaCertificatePath;
            public string? ClientCertificatePath => _inner.ClientCertificatePath;
            public string? ClientKeyPath => _inner.ClientKeyPath;
            public X509Certificate2? CaCertificate => _inner.CaCertificate;
            public X509Certificate2? ClientCertificate => _inner.ClientCertificate;
            public IReadOnlyList<X509Certificate2> ClientCertificateIntermediates => _inner.ClientCertificateIntermediates;
            public Vouchfx.Engine.Abstractions.Secrets.SecretString? ClientKeyPassword => _inner.ClientKeyPassword;
            public bool TrustsRemoteCertificate(X509Certificate2? remote, X509Chain? chain, SslPolicyErrors errors)
            {
                var trusted = _inner.TrustsRemoteCertificate(remote, chain, errors);
                _owner.Decisions.Enqueue(new TrustDecision(errors, trusted, remote is null ? null : Sha(remote.RawData)));
                return trusted;
            }
        }
    }
}
