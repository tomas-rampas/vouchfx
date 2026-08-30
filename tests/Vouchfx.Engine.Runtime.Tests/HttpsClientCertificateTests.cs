// REQ-024 — the HTTP family presents client certificates and trusts a declared CA
// (authenticated-infrastructure-mtls, slice D).
//
// These are EXECUTION tests, not schema tests, and that is the point. The lesson this slice
// inherits is that a suite validating is no evidence a provider can execute it: everything
// here drives the real provider through Emit -> CsxAssembler.Assemble ->
// RoslynScriptCompiler.CompileOnce -> RunIsolatedAsync against a real TLS listener that really
// demands a client certificate, and reads the StepOutcome the emitted script actually wrote.
//
// The listener reproduces the behaviour MEASURED against nginx with `ssl_verify_client on`,
// because it is the behaviour that decides how the negative control must be written: with NO
// client certificate presented, the TLS handshake COMPLETES IN FULL and the server answers
// `400 Bad Request` at the HTTP layer. `curl` exits 0 against it; a .NET client returns a
// normal HttpResponseMessage. A provider judging only "did the handshake throw" would read a
// rejection as an ordinary response — so the assertion below is on the STATUS CODE, and the
// listener is built to make that the observable fact rather than a handshake exception.
//
// Non-Docker: the certificate authority, the server certificate and the client certificate are
// all generated in-process (Vouchfx.TestSupport.TestCertificateAuthority), and the listener is
// a raw TcpListener + SslStream. Nothing here needs a container.
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Security;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.Http.Soap;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.MetricsAssert.Prometheus;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// REQ-024 execution tests for <c>http.rest</c> against a real mutual-TLS listener.
/// </summary>
public sealed class HttpsClientCertificateTests
{
    private const string TargetName = "payments";

    // ── Compile-context plumbing (mirrors HttpRestExecutionTests) ─────────────────────────

    private sealed class StubCompileContext : ICompileContext
    {
        public StubCompileContext(string stepId) => StepId = stepId;

        public string SuiteDirectory => Directory.GetCurrentDirectory();

        public string StepId { get; }

        public string SuiteNamespace => "Generated";

        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    private static readonly string[] s_additionalRefs = new[]
    {
        typeof(System.Net.Http.HttpClient).Assembly.Location,
        typeof(HttpStatusCode).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.Json.Nodes.JsonNode).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(Uri).Assembly.Location,
        typeof(Json.Path.JsonPath).Assembly.Location,
        typeof(System.Xml.XmlDocument).Assembly.Location,

        // REQ-024's own additions: the emitted Security_Helpers class names
        // System.Net.Http.ClientCertificateOption, X509Certificate2/X509Chain and
        // SslPolicyErrors. Production needs no equivalent — ScenarioRunner compiles against
        // the FULL TRUSTED_PLATFORM_ASSEMBLIES set — but this harness supplies an explicit
        // subset, so the subset has to grow with what the helper references.
        typeof(X509Certificate2).Assembly.Location,
        typeof(SslPolicyErrors).Assembly.Location,
    };

    private static SecuritySpec MtlsSecurity() =>
        new(
            Profile: "mtls",
            Endpoint: "8443",
            CaCert: TestCertificateAuthority.CaFileName,
            ClientCert: TestCertificateAuthority.ClientCertFileName,
            ClientKey: TestCertificateAuthority.ClientKeyFileName,
            ServerArtifacts: null);

    private static ScenarioAst AstWith(SecuritySpec? security) =>
        new(
            Metadata: null,
            Environment: new EnvironmentSpec(
                Services: new Dictionary<string, ServiceSpec>(StringComparer.Ordinal)
                {
                    [TargetName] = new ServiceSpec("acme/payments:1.2", null, null, null, null)
                    {
                        Security = security,
                    },
                },
                Dependencies: null,
                Seed: null,
                ImageRegistry: null,
                ImagePullPolicy: null),
            Variables: new Dictionary<string, string>(StringComparer.Ordinal),
            Steps: Array.Empty<StepNode>());

    /// <summary>
    /// <c>SecurityConfigurationAccessor.Build</c> for this class, none of whose suites declare a
    /// <c>clientKeyPassword</c>: the <see langword="null"/> secret accessor is stated once here
    /// rather than at every call site. See the same helper in
    /// <c>SecurityConfigurationAccessorTests</c> for why the parameter itself stays required.
    /// </summary>
    private static ISecurityConfigurationAccessor BuildWithNoSecretAccessor(
        ScenarioAst ast, string? suiteDirectory) =>
        SecurityConfigurationAccessor.Build(ast, suiteDirectory, secrets: null);

    private static HttpRestModel GetModel(int? expectedStatus = null) =>
        new(
            Target: TargetName,
            Method: "GET",
            Path: "/",
            Headers: null,
            Body: null,
            Expect: expectedStatus is { } status ? new HttpExpect(status) : null);

    /// <summary>
    /// Drives the real provider through the real compile+run pipeline for the given steps and
    /// returns the <c>Vars</c> the emitted script mutated.
    /// </summary>
    private static async Task<Dictionary<string, object?>> RunStepsAsync(
        (string StepId, HttpRestModel Model)[] steps,
        string baseUrl,
        ISecurityConfigurationAccessor security)
    {
        var provider = new HttpRestProvider();
        var plans = new List<(string, CsxFragment)>(steps.Length);
        foreach (var (stepId, model) in steps)
        {
            plans.Add((stepId, provider.Emit(model, new StubCompileContext(stepId))));
        }

        var assembled = CsxAssembler.Assemble(plans);
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Service(TargetName)] = baseUrl,
        };

        var globals = new ScriptGlobalVariables(
            vars,
            new Dictionary<string, object>(StringComparer.Ordinal),
            Vouchfx.Engine.Abstractions.Secrets.NullSecretAccessor.Instance,
            Vouchfx.Engine.Abstractions.Webhooks.NullWebhookCaptureAccessor.Instance,
            Vouchfx.Engine.Abstractions.Traces.NullTraceCaptureAccessor.Instance,
            stepEvents: null,
            security);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);
        return vars;
    }

    private static StepOutcome OutcomeOf(Dictionary<string, object?> vars, string stepId) =>
        Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(CsxFragment.SanitiseId(stepId))]);

    // ── The requirement, executed ─────────────────────────────────────────────────────────

    /// <summary>
    /// The positive case: a step targeting a service with an <c>mtls</c> profile completes the
    /// handshake against a server whose certificate chains to a PRIVATE CA, presents the
    /// declared client certificate, and passes. The listener records the client subject it
    /// actually received, so "presented a certificate" is observed at the server rather than
    /// inferred from a green verdict.
    /// </summary>
    [Fact]
    public async Task Execute_MtlsProfile_PresentsTheClientCertificateAndTrustsThePrivateCa()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        using var responder = MtlsResponder.Start(bed.ServerCertificate);

        var accessor = BuildWithNoSecretAccessor(AstWith(MtlsSecurity()), bed.SuiteDirectory);
        try
        {
            var vars = await RunStepsAsync(
                new[] { ("call-api", GetModel(expectedStatus: 200)) }, responder.BaseUrl, accessor);

            var outcome = OutcomeOf(vars, "call-api");
            Assert.Equal(Verdict.Pass, outcome.Verdict);

            // Observed at the SERVER: a client certificate really arrived, and it is the one
            // the suite declared.
            Assert.Equal(
                TestCertificateAuthority.ClientSubjectCommonName, responder.LastClientCertificateCommonName);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// REQ-005's acceptance, EXECUTED: the same suite with its client key encrypted at rest and a
    /// <c>clientKeyPassword</c> declared completes the mutual-TLS handshake and presents the same
    /// identity. The arm above is the control — the only differences are the encryption of the key
    /// on disk and the one extra declared field.
    /// </summary>
    /// <remarks>
    /// Written HERE, against a real listener, and not only as a non-null certificate in
    /// <c>SecurityConfigurationAccessorTests</c>, because REQ-005 asks for a HANDSHAKE and this
    /// repo has already measured the difference: a certificate straight out of the PEM loader
    /// reports <c>HasPrivateKey=true</c> and then FAILS TLS client authentication on Windows,
    /// because SChannel cannot use its ephemeral key. Only a handshake proves the PKCS#12 round
    /// trip in <c>LoadClient</c> ran on the ENCRYPTED branch too — a branch that skipped it would
    /// pass every assertion in that class and still be unusable on the platform half this project's
    /// pilots run on.
    /// </remarks>
    [Fact]
    public async Task Execute_MtlsProfileWithEncryptedClientKey_CompletesTheHandshakeUsingThePassphrase()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        using var responder = MtlsResponder.Start(bed.ServerCertificate);

        const string passphrase = "pilot-passphrase";
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, passphrase);

        var variable = TestCertificateAuthority.UniqueClientKeyPassphraseVariableName();
        Environment.SetEnvironmentVariable(variable, passphrase);
        try
        {
            var security = MtlsSecurity() with
            {
                ClientKeyPassword = "${secret:env/" + variable + "}",
            };

            // A REAL SecretAccessor over a REAL resolver: SecretString's constructor is internal to
            // Vouchfx.Engine.Abstractions and this project holds no InternalsVisibleTo grant, so a
            // passphrase reaches the accessor here exactly the way a production one does.
            var accessor = SecurityConfigurationAccessor.Build(
                AstWith(security),
                bed.SuiteDirectory,
                new SecretAccessor(
                    new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() })));
            try
            {
                var vars = await RunStepsAsync(
                    new[] { ("call-api", GetModel(expectedStatus: 200)) }, responder.BaseUrl, accessor);

                var outcome = OutcomeOf(vars, "call-api");
                Assert.Equal(Verdict.Pass, outcome.Verdict);

                // Observed at the SERVER, as in the control: the encrypted key really produced an
                // identity the listener accepted, and it is the declared one.
                Assert.Equal(
                    TestCertificateAuthority.ClientSubjectCommonName,
                    responder.LastClientCertificateCommonName);
            }
            finally
            {
                (accessor as IDisposable)?.Dispose();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    /// <summary>
    /// EDGE-002, EXECUTED: a well-formed <c>clientKeyPassword</c> reference that CANNOT BE RESOLVED
    /// yields an <see cref="Verdict.EnvironmentError"/> — never a <see cref="Verdict.Fail"/> — and
    /// the secret subsystem's own message survives into the observation an author reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The taxonomy distinction is the requirement, not a detail of it (§12.1). An unset secret is
    /// an environment/configuration fault: the suite never got to test anything, and only
    /// <c>Fail</c> breaks CI by default, so misfiling this as a defect is what "conflating an env
    /// error with a defect destroys trust in the tool" names. Asserted BOTH ways — the verdict is
    /// <c>EnvironmentError</c> AND is explicitly not <c>Fail</c> — because a single equality would
    /// go green against a future taxonomy edit that renamed rather than reclassified.
    /// </para>
    /// <para>
    /// Written HERE, driving the real provider through Emit → Assemble → CompileOnce →
    /// RunIsolatedAsync, rather than as an exception-type assertion against the accessor: the
    /// accessor throwing is not the requirement — the VERDICT the throw becomes, three layers down
    /// through the provider's guarded region, is. The environment variable is deliberately never
    /// set, and its name is unique per run, so nothing on the host can make this pass by accident.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Execute_MtlsProfileWithAnUnresolvablePassphrase_IsAnEnvironmentErrorNotAFail()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        using var responder = MtlsResponder.Start(bed.ServerCertificate);

        // The key IS encrypted, so REQ-006's contradiction guard passes and the failure under test
        // is the RESOLUTION, not the declaration.
        TestCertificateAuthority.EncryptClientKeyInPlace(bed.SuiteDirectory, "pilot-passphrase");

        // Never set. Unique per run, so no leftover value on the host can satisfy it.
        var variable = TestCertificateAuthority.UniqueClientKeyPassphraseVariableName();
        Assert.Null(Environment.GetEnvironmentVariable(variable));

        var security = MtlsSecurity() with
        {
            ClientKeyPassword = "${secret:env/" + variable + "}",
        };

        // A REAL environment secret accessor, as in the positive arm — the point is that the
        // subsystem's own resolution failure is what reaches the verdict.
        var accessor = SecurityConfigurationAccessor.Build(
            AstWith(security),
            bed.SuiteDirectory,
            new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() })));
        try
        {
            var vars = await RunStepsAsync(
                new[] { ("call-api", GetModel(expectedStatus: 200)) }, responder.BaseUrl, accessor);

            var outcome = OutcomeOf(vars, "call-api");

            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            Assert.NotEqual(Verdict.Fail, outcome.Verdict);

            // The engine's own framing: which field, and that resolution is what failed.
            Assert.Contains("clientKeyPassword", outcome.Observation, StringComparison.Ordinal);
            Assert.Contains("could not be resolved", outcome.Observation, StringComparison.Ordinal);

            // THE RESOLVER'S OWN MESSAGE SURVIVES, which is what makes the diagnostic actionable —
            // it names the variable an author has to define. Asserted on a fragment carrying no
            // apostrophes, because the quoting helper escapes the delimiter inside nested text.
            Assert.Contains(
                "is not set; define it in the run environment",
                outcome.Observation,
                StringComparison.Ordinal);
            Assert.Contains(variable, outcome.Observation, StringComparison.Ordinal);

            // Fail-closed: nothing was presented. An unresolvable passphrase must not degrade to an
            // anonymous connection against a listener that requests but does not enforce a client
            // certificate.
            Assert.Null(responder.LastClientCertificateCommonName);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The negative control, written the way the MEASURED behaviour demands. Removing the
    /// client identity (the <c>tls</c> profile — the schema forbids dropping
    /// <c>clientCert</c>/<c>clientKey</c> while keeping <c>profile: mtls</c>, so this is what
    /// "the same suite with the client certificate removed" actually is) does NOT fail the
    /// handshake: the handshake completes and the server answers 400. The step therefore fails
    /// on the STATUS CODE, and this test pins that — a provider that judged only "did the
    /// handshake throw" would have reported this as an ordinary response.
    /// </summary>
    [Fact]
    public async Task Execute_TlsProfileWithNoClientCertificate_Fails400AtTheHttpLayerNotTheHandshake()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        using var responder = MtlsResponder.Start(bed.ServerCertificate);

        var tlsOnly = new SecuritySpec(
            Profile: "tls",
            Endpoint: "8443",
            CaCert: TestCertificateAuthority.CaFileName,
            ClientCert: null,
            ClientKey: null,
            ServerArtifacts: null);

        var accessor = BuildWithNoSecretAccessor(AstWith(tlsOnly), bed.SuiteDirectory);
        try
        {
            var vars = await RunStepsAsync(
                new[] { ("call-api", GetModel(expectedStatus: 200)) }, responder.BaseUrl, accessor);

            var outcome = OutcomeOf(vars, "call-api");

            // Fail, NOT EnvironmentError: the transport worked, the server rejected the
            // request. That distinction is the whole finding.
            Assert.Equal(Verdict.Fail, outcome.Verdict);
            Assert.Contains("\"status\":400", outcome.Observation, StringComparison.Ordinal);

            // And the server saw no client certificate at all.
            Assert.Null(responder.LastClientCertificateCommonName);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The CA half of the requirement, isolated: with NO <c>security</c> block the platform's
    /// own trust store applies, the private CA is unknown to it, and the handshake fails —
    /// reported as an <see cref="Verdict.EnvironmentError"/>. This is what proves the declared
    /// <c>caCert</c> in the test above is doing real work rather than riding on a machine store
    /// that already trusted the anchor.
    /// </summary>
    [Fact]
    public async Task Execute_NoSecurityBlock_FailsTheHandshakeAgainstAPrivateCa()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        using var responder = MtlsResponder.Start(bed.ServerCertificate);

        var vars = await RunStepsAsync(
            new[] { ("call-api", GetModel()) },
            responder.BaseUrl,
            NullSecurityConfigurationAccessor.Instance);

        var outcome = OutcomeOf(vars, "call-api");
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
    }

    /// <summary>
    /// REQ-024's fourth clause, single-shot: the SAME suite with only <c>caCert</c> omitted
    /// fails as an <see cref="Verdict.EnvironmentError"/>.
    /// </summary>
    /// <remarks>
    /// A one-line variant of the positive test above, deliberately. The clause was previously
    /// met only by COMPOSING three separate measurements — the positive case passing, the
    /// no-security case erroring, and the accessor leaving both CA views null — which
    /// establishes the conclusion but leaves no single test that fails if the behaviour
    /// regresses. Here the ONLY difference from
    /// <see cref="Execute_MtlsProfile_PresentsTheClientCertificateAndTrustsThePrivateCa"/> is
    /// the dropped <c>caCert</c>: the client identity is still declared and still presented, so
    /// what this isolates is the trust anchor and nothing else.
    /// </remarks>
    [Fact]
    public async Task Execute_MtlsProfileWithNoCaCert_FailsTheHandshakeAsAnEnvironmentError()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        using var responder = MtlsResponder.Start(bed.ServerCertificate);

        var accessor = BuildWithNoSecretAccessor(
            AstWith(MtlsSecurity() with { CaCert = null }), bed.SuiteDirectory);
        try
        {
            var vars = await RunStepsAsync(
                new[] { ("call-api", GetModel(expectedStatus: 200)) }, responder.BaseUrl, accessor);

            var outcome = OutcomeOf(vars, "call-api");

            // EnvironmentError, not Fail: the transport never came up, so there is no server
            // response to judge (§12.1). With no declared anchor the private CA is unknown to
            // the platform's own trust store and the handshake is refused.
            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    // ── The profile gate (fix round three, MINOR-2) ───────────────────────────────────────

    /// <summary>
    /// A profile this engine has no HTTPS behaviour for is REFUSED, even when the target's
    /// certificate material would otherwise have been usable. The client certificate is
    /// declared and loadable here — under a which-fields-are-set test it would simply have been
    /// presented — so what this pins is that the DECLARED PROFILE decides, not the fields.
    /// </summary>
    /// <remarks>
    /// REQ-019 makes the profile discriminator open, and slice C's registry decides only which
    /// profiles are declarable at all. A later profile wired for some other technology, carrying
    /// certificate paths as most transports do, must not acquire mutual-TLS semantics from these
    /// three providers by accident. Unreachable from YAML today (the registry rejects an
    /// unregistered profile before any step runs), exactly like the accessor's half-pair check —
    /// and for the same reason: the layer between an author and an unintended client identity
    /// must not be one the runtime never consults.
    /// </remarks>
    [Fact]
    public async Task Execute_UnknownProfile_IsRefusedAsAnEnvironmentErrorRatherThanPresentingWhateverIsDeclared()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        using var responder = MtlsResponder.Start(bed.ServerCertificate);

        var accessor = BuildWithNoSecretAccessor(
            AstWith(MtlsSecurity() with { Profile = "kerberos" }), bed.SuiteDirectory);
        try
        {
            var vars = await RunStepsAsync(
                new[] { ("call-api", GetModel(expectedStatus: 200)) }, responder.BaseUrl, accessor);

            var outcome = OutcomeOf(vars, "call-api");
            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            Assert.Contains("kerberos", outcome.Observation, StringComparison.Ordinal);

            // The decisive half: nothing was presented. A step that errored only AFTER opening a
            // mutual-TLS connection would satisfy the verdict assertion above while having done
            // the very thing this gate exists to prevent.
            Assert.Null(responder.LastClientCertificateCommonName);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// <c>profile: tls</c> declared ALONGSIDE a client certificate and key is a contradiction,
    /// and is refused rather than resolved in either direction.
    /// </summary>
    /// <remarks>
    /// The schema forbids the combination outright, so this is reachable only by an embedder
    /// that bypasses it. The two readings differ by exactly whether the run authenticates —
    /// "they meant <c>mtls</c>" presents an identity the profile denies, "they meant no client
    /// identity" silently drops material the author declared and, against a listener that
    /// requests but does not require a certificate, passes having authenticated nothing. That is
    /// the same measured failure mode the accessor's half-pair check closes, reached from the
    /// other side.
    /// </remarks>
    [Fact]
    public async Task Execute_TlsProfileDeclaringClientMaterial_IsRefusedRatherThanSilentlyIgnored()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        using var responder = MtlsResponder.Start(bed.ServerCertificate);

        var accessor = BuildWithNoSecretAccessor(
            AstWith(MtlsSecurity() with { Profile = "tls" }), bed.SuiteDirectory);
        try
        {
            var vars = await RunStepsAsync(
                new[] { ("call-api", GetModel(expectedStatus: 200)) }, responder.BaseUrl, accessor);

            var outcome = OutcomeOf(vars, "call-api");
            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            Assert.Contains("clientCert", outcome.Observation, StringComparison.Ordinal);
            Assert.Null(responder.LastClientCertificateCommonName);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// <c>profile: mtls</c> that resolves to NO client identity is refused, rather than
    /// connecting with none. The complement of the test above, and the case with teeth: this
    /// listener requests a client certificate without requiring one, so the pre-gate behaviour
    /// was a green suite that presented nothing.
    /// </summary>
    [Fact]
    public async Task Execute_MtlsProfileWithNoClientMaterial_IsRefusedRatherThanConnectingUnauthenticated()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        using var responder = MtlsResponder.Start(bed.ServerCertificate);

        // Declared `mtls`, but only a trust anchor — so the accessor's own half-pair check
        // (which needs one of the two client fields present) never fires, and the certificate
        // view resolves with no client identity at all.
        var mtlsWithoutClientIdentity = new SecuritySpec(
            Profile: "mtls",
            Endpoint: "8443",
            CaCert: TestCertificateAuthority.CaFileName,
            ClientCert: null,
            ClientKey: null,
            ServerArtifacts: null);

        var accessor = BuildWithNoSecretAccessor(
            AstWith(mtlsWithoutClientIdentity), bed.SuiteDirectory);
        try
        {
            var vars = await RunStepsAsync(
                new[] { ("call-api", GetModel(expectedStatus: 200)) }, responder.BaseUrl, accessor);

            var outcome = OutcomeOf(vars, "call-api");
            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            Assert.Null(responder.LastClientCertificateCommonName);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// A two-tier CA validates end to end: the suite declares the OFFLINE ROOT as its
    /// <c>caCert</c>, the server sends its leaf plus the issuing intermediate, and the step
    /// passes.
    /// </summary>
    /// <remarks>
    /// This is the shape a real enterprise CA takes, and it is why the trust decision takes the
    /// peer-supplied chain at all. Measured before that: <see langword="false"/> — the callback
    /// received the intermediate and threw it away, so the leaf could never reach the declared
    /// root. It fails CLOSED, which is why it was first rated low, but the failure mode
    /// (<c>EnvironmentError, unknown ca</c>) pushes an author towards declaring the intermediate
    /// as their anchor or dropping <c>caCert</c> altogether — both of which weaken their setup.
    /// Driven through the real compile-and-run pipeline rather than against the accessor
    /// directly, so what is pinned is that the intermediate survives the whole route: platform
    /// callback → emitted CSX → accessor → rebuilt chain.
    /// </remarks>
    [Fact]
    public async Task Execute_TwoTierCertificateAuthority_ValidatesAgainstTheDeclaredOfflineRoot()
    {
        using var bed = TestCertificateAuthority.CreateTwoTierSuiteDirectory();
        using var responder = MtlsResponder.Start(
            bed.ServerLeafWithKey, new X509Certificate2Collection { bed.IntermediateCertificate });

        var tlsOnly = new SecuritySpec(
            Profile: "tls",
            Endpoint: "8443",
            CaCert: TestCertificateAuthority.CaFileName,
            ClientCert: null,
            ClientKey: null,
            ServerArtifacts: null);

        var accessor = BuildWithNoSecretAccessor(AstWith(tlsOnly), bed.SuiteDirectory);
        try
        {
            // The listener answers 400 when no client certificate arrives, and this is a `tls`
            // profile, so 400 IS the success signal for this test: reaching an HTTP status at
            // all means the TLS handshake completed, which is the whole question.
            var vars = await RunStepsAsync(
                new[] { ("call-api", GetModel(expectedStatus: 400)) }, responder.BaseUrl, accessor);

            var outcome = OutcomeOf(vars, "call-api");
            Assert.Equal(Verdict.Pass, outcome.Verdict);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// The borrowing contract, executed: two steps against the same target both succeed. Each
    /// step builds its own <c>HttpClient(handler, disposeHandler: true)</c> and disposes it, so
    /// a second step would fail outright if handler disposal invalidated the accessor-owned
    /// certificate the first step borrowed.
    /// </summary>
    [Fact]
    public async Task Execute_TwoStepsAgainstTheSameSecuredTarget_BothSucceed()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        using var responder = MtlsResponder.Start(bed.ServerCertificate);

        var accessor = BuildWithNoSecretAccessor(AstWith(MtlsSecurity()), bed.SuiteDirectory);
        try
        {
            var vars = await RunStepsAsync(
                new[]
                {
                    ("first-call", GetModel(expectedStatus: 200)),
                    ("second-call", GetModel(expectedStatus: 200)),
                },
                responder.BaseUrl,
                accessor);

            Assert.Equal(Verdict.Pass, OutcomeOf(vars, "first-call").Verdict);
            Assert.Equal(Verdict.Pass, OutcomeOf(vars, "second-call").Verdict);
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// REQ-014's second acceptance, executed rather than asserted structurally: after a step
    /// that used the material, NO <c>Vars</c> key or value exposes a certificate path. The
    /// emitted script HAS the accessor in hand — it is the one place that could write the
    /// material into the reported and §14 event surface — so running it and enumerating the
    /// resulting dictionary is the measurement that matters.
    /// </summary>
    [Fact]
    public async Task Execute_SecuredStep_LeavesNoCertificateMaterialInVars()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        using var responder = MtlsResponder.Start(bed.ServerCertificate);

        var accessor = BuildWithNoSecretAccessor(AstWith(MtlsSecurity()), bed.SuiteDirectory);
        try
        {
            var vars = await RunStepsAsync(
                new[] { ("call-api", GetModel(expectedStatus: 200)) }, responder.BaseUrl, accessor);

            Assert.Equal(Verdict.Pass, OutcomeOf(vars, "call-api").Verdict);

            var forbidden = new[]
            {
                TestCertificateAuthority.CaFileName,
                TestCertificateAuthority.ClientCertFileName,
                TestCertificateAuthority.ClientKeyFileName,
                bed.SuiteDirectory,
            };

            foreach (var (key, value) in vars)
            {
                var rendered = key + " " + (value?.ToString() ?? string.Empty);
                foreach (var needle in forbidden)
                {
                    Assert.DoesNotContain(needle, rendered, StringComparison.OrdinalIgnoreCase);
                }

                Assert.DoesNotContain("sec::", key, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            (accessor as IDisposable)?.Dispose();
        }
    }

    // ── The compiled artefact ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The compiled CSX carries the handler configuration and threads the step's own target
    /// name and the <c>Security</c> accessor into it — the two facts that make the runtime
    /// behaviour above possible. Asserted against the ASSEMBLED CSX the compiler consumes, not
    /// against the provider's source text.
    /// </summary>
    [Fact]
    public void CompiledCsx_ForAnHttpRestStep_ThreadsTheSecurityAccessorAndTargetName()
    {
        var provider = new HttpRestProvider();
        var fragment = provider.Emit(GetModel(), new StubCompileContext("call-api"));
        var csx = CsxAssembler.Assemble(new[] { ("call-api", fragment) }).CsxSource;

        // The step block passes the accessor and its own target name.
        Assert.Contains("Security,", csx, StringComparison.Ordinal);
        Assert.Contains("\"payments\"", csx, StringComparison.Ordinal);

        // The handler configuration itself: the trust anchor and the client-certificate
        // assignment, both present in the spliced helper.
        Assert.Contains(
            "Security_Helpers.ConfigureHandler(security, targetName, handler)", csx, StringComparison.Ordinal);
        Assert.Contains("handler.ClientCertificates.Add(clientCertificate)", csx, StringComparison.Ordinal);
        Assert.Contains("handler.ServerCertificateCustomValidationCallback", csx, StringComparison.Ordinal);

        // The callback's `chain` argument is FORWARDED, not discarded: it carries the
        // intermediates the peer sent, which a two-tier PKI needs for path building. Pinned as
        // a string because dropping it back to a two-argument call is a silent regression that
        // only a two-tier topology would otherwise reveal.
        Assert.Contains(
            "certificates.TrustsRemoteCertificate(remoteCertificate, chain, sslPolicyErrors)",
            csx,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The helper is spliced ONCE for a suite whose steps span all three HTTP-family providers,
    /// which is what <c>CsxFragment</c>'s byte-identical-helper rule buys: three copies of a
    /// transport-security decision would be three places for one to be weakened. All three call
    /// it, which is the part that makes REQ-024's "all three providers" claim measured rather
    /// than asserted.
    /// </summary>
    [Fact]
    public void CompiledCsx_AcrossTheWholeHttpFamily_CarriesOneHelperCalledByAllThreeProviders()
    {
        var plans = new List<(string, CsxFragment)>
        {
            ("rest", new HttpRestProvider().Emit(GetModel(), new StubCompileContext("rest"))),
            ("soap", EmitSoap("soap")),
            ("metrics", EmitPrometheus("metrics")),
        };

        var csx = CsxAssembler.Assemble(plans).CsxSource;

        var definitions = csx.Split("static class Security_Helpers", StringSplitOptions.None).Length - 1;
        Assert.Equal(1, definitions);

        var callSites = csx.Split(
            "Security_Helpers.ConfigureHandler(security, targetName, handler)", StringSplitOptions.None).Length - 1;
        Assert.Equal(3, callSites);
    }

    /// <summary>
    /// §17's rule, applied to certificate material: no declared path is baked into the emitted
    /// script. The path reaches the script only through the accessor, at step-execution time —
    /// interpolating it at compile time would defeat compile-once and corrupt the
    /// reproducibility envelope.
    /// </summary>
    [Fact]
    public void CompiledCsx_ForASecuredSuite_ContainsNoDeclaredCertificatePath()
    {
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        var provider = new HttpRestProvider();
        var fragment = provider.Emit(GetModel(), new StubCompileContext("call-api"));
        var csx = CsxAssembler.Assemble(new[] { ("call-api", fragment) }).CsxSource;

        Assert.DoesNotContain(TestCertificateAuthority.CaFileName, csx, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestCertificateAuthority.ClientCertFileName, csx, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestCertificateAuthority.ClientKeyFileName, csx, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(bed.SuiteDirectory, csx, StringComparison.OrdinalIgnoreCase);
    }

    private static CsxFragment EmitSoap(string stepId) =>
        new HttpSoapProvider().Emit(
            new HttpSoapModel(
                Target: TargetName,
                Path: "/soap",
                Action: null,
                Envelope: "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body/></s:Envelope>",
                Expect: null),
            new StubCompileContext(stepId));

    private static CsxFragment EmitPrometheus(string stepId) =>
        new MetricsAssertPrometheusProvider().Emit(
            new MetricsAssertPrometheusModel(
                Target: TargetName,
                Path: "/metrics",
                Metric: "http_requests_total",
                Labels: null,
                Expect: new MetricsExpectation(Value: null, Min: null, Max: null)),
            new StubCompileContext(stepId));

    // ── The in-process mutual-TLS responder ───────────────────────────────────────────────

    /// <summary>
    /// A raw <see cref="TcpListener"/> + <see cref="SslStream"/> HTTPS responder that REQUESTS a
    /// client certificate and answers <c>200</c> when one arrived, <c>400</c> when none did —
    /// the behaviour measured against nginx with <c>ssl_verify_client on</c>.
    /// </summary>
    /// <remarks>
    /// The validation callback accepts whatever the client presents, deliberately: this
    /// listener is a REJECTION SURFACE for the engine's client-side behaviour, not an authority
    /// on client identity. Transport mutual TLS proves a trust chain, never authorisation —
    /// measured on the test bed, two client certificates from the same CA, one nominally
    /// authorised and one not, both verified SUCCESS — so a listener pretending to authorise
    /// would be modelling something no TLS layer does.
    /// </remarks>
    private sealed class MtlsResponder : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly X509Certificate2 _serverCertificate;
        private readonly SslStreamCertificateContext? _certificateContext;
        private bool _disposed;

        private MtlsResponder(
            TcpListener listener,
            X509Certificate2 serverCertificate,
            SslStreamCertificateContext? certificateContext,
            int port)
        {
            _listener = listener;
            _serverCertificate = serverCertificate;
            _certificateContext = certificateContext;
            BaseUrl = $"https://localhost:{port}";
        }

        public string BaseUrl { get; }

        /// <summary>
        /// The simple name of the client certificate presented on the most recent accepted
        /// connection, or <see langword="null"/> when none was presented.
        /// </summary>
        public string? LastClientCertificateCommonName { get; private set; }

        /// <param name="additionalCertificates">
        /// Intermediates to send ALONGSIDE the server certificate, as a real two-tier server
        /// does. When supplied the listener authenticates through an
        /// <see cref="SslStreamCertificateContext"/>, which is the only way to control what the
        /// server actually puts on the wire; <c>offline: true</c> keeps the fixture from
        /// attempting its own AIA fetches while building that context.
        /// </param>
        public static MtlsResponder Start(
            X509Certificate2 serverCertificate, X509Certificate2Collection? additionalCertificates = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var context = additionalCertificates is null
                ? null
                : SslStreamCertificateContext.Create(serverCertificate, additionalCertificates, offline: true);

            var responder = new MtlsResponder(listener, serverCertificate, context, port);
            _ = Task.Run(responder.AcceptLoopAsync);
            return responder;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Cancel();
            try
            {
                _listener.Stop();
            }
            catch (SocketException)
            {
                // Already stopped.
            }

            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                    when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                try
                {
                    await ServeAsync(client).ConfigureAwait(false);
                }
                catch (Exception ex)
                    when (ex is IOException or AuthenticationException or ObjectDisposedException
                        or SocketException or OperationCanceledException)
                {
                    // A client that abandons the handshake (the no-trust-anchor test does
                    // exactly that) is a normal outcome for this listener, not a fault.
                }
                finally
                {
                    client.Dispose();
                }
            }
        }

        [SuppressMessage(
            "Security",
            "CA5359:Do Not Disable Certificate Validation",
            Justification =
                "This is a SERVER-side SslServerAuthenticationOptions callback, not a client " +
                "validating a server. CA5359 cannot tell the two apart. The callback deliberately " +
                "accepts whatever the client presents — including nothing — because that is what " +
                "makes the handshake complete with no client certificate, so the rejection is " +
                "delivered at the HTTP layer as a 400, exactly as the mutual-TLS test bed's nginx " +
                "does. That behaviour is the fixture's whole purpose; validating here would " +
                "replace the measured behaviour with a different one.")]
        private async Task ServeAsync(TcpClient client)
        {
            var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                var authenticationOptions = new SslServerAuthenticationOptions
                {
                    ClientCertificateRequired = true,
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true,

                    // TLS 1.2, pinned. On TLS 1.3 the client certificate arrives
                    // post-handshake, so SslStream.RemoteCertificate is not reliably
                    // populated by the time this listener writes its response — an artefact
                    // of this fixture, not of the engine. The distinction the tests need
                    // (handshake completes, HTTP layer rejects) holds identically on both
                    // versions; pinning removes a race the fixture cannot otherwise win.
                    EnabledSslProtocols = SslProtocols.Tls12,
                };

                // A certificate CONTEXT when intermediates must go on the wire (the two-tier
                // test), the bare certificate otherwise — the pre-existing single-tier path,
                // unchanged.
                if (_certificateContext is not null)
                {
                    authenticationOptions.ServerCertificateContext = _certificateContext;
                }
                else
                {
                    authenticationOptions.ServerCertificate = _serverCertificate;
                }

                await stream.AuthenticateAsServerAsync(authenticationOptions, _cts.Token).ConfigureAwait(false);

                var presented = stream.RemoteCertificate is { } raw ? new X509Certificate2(raw) : null;
                try
                {
                    LastClientCertificateCommonName =
                        presented?.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

                    // Drain the request head so the client is not left writing into a closed pipe.
                    await ReadRequestHeadAsync(stream).ConfigureAwait(false);

                    var body = presented is null
                        ? "{\"error\":\"No required SSL certificate was sent\"}"
                        : "{\"status\":\"ok\"}";
                    var status = presented is null ? "400 Bad Request" : "200 OK";
                    var bytes = Encoding.UTF8.GetBytes(
                        $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\n" +
                        $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");

                    await stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                    await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    presented?.Dispose();
                }
            }
            finally
            {
                // Explicit Dispose in a finally rather than `using var`, matching the emitted
                // CSX discipline this file is testing.
                stream.Dispose();
            }
        }

        private async Task ReadRequestHeadAsync(SslStream stream)
        {
            var buffer = new byte[1024];
            var seen = new StringBuilder();
            while (seen.Length < 8192)
            {
                var read = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    return;
                }

                seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
                if (seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    return;
                }
            }
        }
    }
}
