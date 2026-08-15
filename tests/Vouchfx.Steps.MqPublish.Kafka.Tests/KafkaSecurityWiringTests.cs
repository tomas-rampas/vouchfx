// REQ-015 — Kafka provider mutual-TLS wiring (authenticated-infrastructure-mtls, slice E).
//
// Two levels, both needed, because REQ-015's acceptance turns on a distinction that only one of
// them can see:
//
//   1. EMIT — the assembled CSX threads the Security accessor and the step's own target name into
//      the helper, splices ONE copy of KafkaSecurity_Helpers, and bakes NO certificate path (§17:
//      security material is resolved at step-execution time, never at compile time).
//
//   2. RUNTIME — the emitted helper source is COMPILED AND RUN against a real ProducerConfig, and
//      the resulting configuration is read back. This is the only level at which acceptance (b)
//      is checkable at all.
//
// WHY (b) IS PROVEN ON THE CONFIGURATION AND NOT ON THE EMITTED TEXT. REQ-015(b) asks for
// SslCaLocation "absent from the emitted ProducerConfig initialiser, not merely set to null" when
// caCert is undeclared. An emit-time branch on "did the author declare caCert" is not available:
// ICompileContext carries no `environment` security surface at all. (Corrected 2026-08-04: it now
// also exposes DeclaredServices, added with a DEFAULT implementation so no existing stand-in
// broke. That member answers "is this target a service", which decides the Vars KEY the step
// reads, and says nothing about what the author declared under `security` — so the argument here
// is unchanged.) Both variants therefore share one emitted text and differ at run time — which is
// also what §17 requires, since a compile-time branch would bake the declaration into the
// compiled-once script.
//
// The property that actually protects an author is that librdkafka never receives ssl.ca.location,
// and that IS directly observable: Confluent.Kafka's ClientConfig is a keyed property bag, and
// MEASURED against the pinned 2.14.2, assigning null REMOVES the key while assigning "" ADDS it
// with an empty value (an empty path is a file librdkafka opens and fails on, not a fallback).
// The tests below assert on the key's presence in that bag.
using Confluent.Kafka;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Security;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqPublish.Kafka;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqPublish.Kafka.Tests;

/// <summary>
/// REQ-015 tests: what the two Kafka providers emit, and what the emitted helper actually does to
/// a client configuration.
/// </summary>
public sealed class KafkaSecurityWiringTests
{
    private const string CaPath = @"/suite/certs/ca.pem";
    private const string ClientCertPath = @"/suite/certs/client.pem";
    private const string ClientKeyPath = @"/suite/certs/client-key.pem";

    // Deliberately distinctive so the §17 emit assertion below cannot pass by accident against
    // some unrelated substring of the assembled script.
    private const string KeyPassphrase = "correct-horse-battery-staple-7f3a";

    private static readonly IReadOnlyList<string> s_kafkaRefs = new[]
    {
        typeof(ProducerConfig).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
    };

    // ── Compile context / model plumbing ──────────────────────────────────────────────────

    private sealed class StubCompileContext : ICompileContext
    {
        internal StubCompileContext(string stepId) => StepId = stepId;

        public string StepId { get; }

        public string SuiteNamespace => "VouchfxSuite";

        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    private static MqPublishKafkaModel PublishModel(string target = "events") =>
        new MqPublishKafkaProvider().Bind(
            (YamlMappingNode)new YamlStream
            {
                Documents =
                {
                    new YamlDocument(new YamlMappingNode
                    {
                        { "target", target },
                        { "topic", "orders" },
                        { "payload", "{\"id\":1}" },
                    }),
                },
            }.Documents[0].RootNode,
            new StubBindingContext());

    // ── 1. EMIT ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The emitted call site threads the <c>Security</c> accessor and the step's own bare
    /// <c>target</c> name — the two arguments the helper needs to resolve this step's declared
    /// configuration at run time.
    /// </summary>
    [Fact]
    public void CompiledCsx_ForAKafkaPublishStep_ThreadsTheSecurityAccessorAndTargetName()
    {
        var fragment = new MqPublishKafkaProvider().Emit(
            PublishModel("broker"), new StubCompileContext("publish-order"));
        var csx = CsxAssembler.Assemble(new[] { ("publish-order", fragment) }).CsxSource;

        Assert.Contains("MqPublishKafka_Helpers.PublishAsync(", csx, StringComparison.Ordinal);
        Assert.Contains("Security,", csx, StringComparison.Ordinal);
        Assert.Contains("\"broker\"", csx, StringComparison.Ordinal);
        Assert.Contains("KafkaSecurity_Helpers.ConfigureClient(security, targetName, config)", csx, StringComparison.Ordinal);
    }

    /// <summary>
    /// §17, applied to certificates: no declared certificate path is baked into the compiled
    /// script. The helper reads them from the accessor at step-execution time, so compile-once
    /// stays valid and the reproducibility envelope is not corrupted.
    /// </summary>
    [Fact]
    public void CompiledCsx_ForAKafkaStep_ContainsNoDeclaredCertificatePath()
    {
        var fragment = new MqPublishKafkaProvider().Emit(
            PublishModel(), new StubCompileContext("publish-order"));
        var csx = CsxAssembler.Assemble(new[] { ("publish-order", fragment) }).CsxSource;

        Assert.DoesNotContain(CaPath, csx, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientCertPath, csx, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientKeyPath, csx, StringComparison.Ordinal);
    }

    /// <summary>
    /// §17, applied to the client-key passphrase (client-key-password spec, REQ-008): the
    /// compiled script contains no passphrase. Interpolating one at emit time would bake a
    /// secret into the emitted IL — the failure mode §17 exists to prevent — and would also
    /// defeat compile-once, since the script is compiled before any secret is resolved. The
    /// helper unwraps the value at step-execution time instead, which is why the emitted text
    /// names only the property.
    /// </summary>
    [Fact]
    public void CompiledCsx_ForAKafkaStep_BakesNoClientKeyPassphrase()
    {
        var fragment = new MqPublishKafkaProvider().Emit(
            PublishModel(), new StubCompileContext("publish-order"));
        var csx = CsxAssembler.Assemble(new[] { ("publish-order", fragment) }).CsxSource;

        Assert.DoesNotContain(KeyPassphrase, csx, StringComparison.Ordinal);

        // …and the read that replaces it IS present, so this assertion is about a value that was
        // deliberately left out rather than about a feature that was never wired.
        //
        // The CODE is asserted, not the identifier. `ClientKeyPassword` also appears in the
        // emitted source's own comments, and CsxAssembler splices RequiredHelpers verbatim with no
        // comment stripping — so an identifier-only assertion would stay green with the read
        // deleted and the comment left standing.
        Assert.Contains("certificates?.ClientKeyPassword?.Reveal()", csx, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two Kafka steps in one suite carry exactly ONE copy of the shared helper — the §13.3.1
    /// dedup rule, which is why the source is a byte-identical constant with no per-step
    /// interpolation.
    /// </summary>
    [Fact]
    public void CompiledCsx_ForTwoKafkaSteps_CarriesOneSharedSecurityHelper()
    {
        var provider = new MqPublishKafkaProvider();
        var plans = new[]
        {
            ("first", provider.Emit(PublishModel(), new StubCompileContext("first"))),
            ("second", provider.Emit(PublishModel(), new StubCompileContext("second"))),
        };

        var csx = CsxAssembler.Assemble(plans).CsxSource;

        var declarations = csx.Split("static class KafkaSecurity_Helpers", StringSplitOptions.None).Length - 1;
        Assert.Equal(1, declarations);
    }

    /// <summary>
    /// The pre-feature shape is gone: the emitted producer is no longer a bare
    /// <c>new ProducerConfig { BootstrapServers = bootstrap }</c> with nothing else — REQ-015's
    /// own closing acceptance clause.
    /// </summary>
    [Fact]
    public void CompiledCsx_ForAKafkaStep_NoLongerResemblesTheBareProducerConfig()
    {
        var fragment = new MqPublishKafkaProvider().Emit(
            PublishModel(), new StubCompileContext("publish-order"));
        var csx = CsxAssembler.Assemble(new[] { ("publish-order", fragment) }).CsxSource;

        var initialiserIndex = csx.IndexOf(
            "new Confluent.Kafka.ProducerConfig { BootstrapServers = bootstrap }", StringComparison.Ordinal);
        Assert.True(initialiserIndex >= 0, "the plain producer path's initialiser is missing entirely");

        // The security call follows it, in the same guarded region, before the producer is built.
        var configureIndex = csx.IndexOf("KafkaSecurity_Helpers.ConfigureClient", StringComparison.Ordinal);
        var buildIndex = csx.IndexOf("new Confluent.Kafka.ProducerBuilder", StringComparison.Ordinal);
        Assert.InRange(configureIndex, initialiserIndex, buildIndex);
    }

    // ── 2. RUNTIME — what the emitted helper does to a real ClientConfig ───────────────────

    /// <summary>
    /// Compiles the emitted helper source and runs <c>ConfigureClient</c> against a real
    /// <see cref="ProducerConfig"/>, returning that configuration's own key set and values.
    /// </summary>
    private static async Task<ProducerConfig> ConfigureAsync(
        string profile,
        string? caCert,
        string? clientCert,
        string? clientKey,
        string stepTarget = "events",
        string declaredTarget = "events",
        SecretString? clientKeyPassword = null,
        string? clientKeyPasswordThrows = null)
    {
        var csx =
            KafkaSecurityHelper.Source + "\n" +
            "var config = new Confluent.Kafka.ProducerConfig { BootstrapServers = \"h:9092\" };\n" +
            $"KafkaSecurity_Helpers.ConfigureClient(Security, \"{stepTarget}\", config);\n" +
            // TEST-ONLY, AND NOT A PRECEDENT: this parks a ClientConfig that may carry a live
            // ssl.key.password in Vars so the assertions below can read the bag back. A PROVIDER
            // MUST NEVER DO THIS. It is safe only because of two things measured here and true of
            // nothing else: ClientConfig does not override ToString() (the declaring type is
            // System.Object), and CapturedVar carries no value — so no renderer or §14 event can
            // reach the string. A configuration object holding a revealed secret is exactly the
            // shape §17 keeps out of Vars; the revealed value belongs in its sink and nowhere else.
            "Vars[\"config\"] = config;";

        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: s_kafkaRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(
            vars,
            new Dictionary<string, object>(StringComparer.Ordinal),
            NullSecretAccessor.Instance,
            Vouchfx.Engine.Abstractions.Webhooks.NullWebhookCaptureAccessor.Instance,
            Vouchfx.Engine.Abstractions.Traces.NullTraceCaptureAccessor.Instance,
            stepEvents: null,
            security: new StubAccessor(
                declaredTarget,
                profile,
                caCert,
                clientCert,
                clientKey,
                clientKeyPassword,
                clientKeyPasswordThrows));

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        return (ProducerConfig)vars["config"]!;
    }

    private static bool HasKey(ClientConfig config, string key) =>
        config.Any(kv => string.Equals(kv.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// REQ-015(a): <c>profile: mtls</c> with an explicit <c>caCert</c> sets
    /// <c>SecurityProtocol = Ssl</c>, a non-null <c>SslCaLocation</c> resolved from
    /// <c>caCert</c>, and non-null <c>SslCertificateLocation</c>/<c>SslKeyLocation</c>.
    /// </summary>
    [Fact]
    public async Task Mtls_WithDeclaredCa_SetsProtocolAnchorAndClientIdentity()
    {
        var config = await ConfigureAsync("mtls", CaPath, ClientCertPath, ClientKeyPath);

        Assert.Equal(SecurityProtocol.Ssl, config.SecurityProtocol);
        Assert.Equal(CaPath, config.SslCaLocation);
        Assert.Equal(ClientCertPath, config.SslCertificateLocation);
        Assert.Equal(ClientKeyPath, config.SslKeyLocation);
    }

    /// <summary>
    /// REQ-015(b): the identical suite with <c>caCert</c> OMITTED keeps the same protocol and
    /// client identity, and <c>ssl.ca.location</c> is <strong>never assigned at all</strong> —
    /// absent from the configuration's own key set, not merely null and not an empty string.
    /// librdkafka then falls back to the declared client material's own trust or the system store.
    /// </summary>
    [Fact]
    public async Task Mtls_WithNoDeclaredCa_LeavesSslCaLocationEntirelyUnset()
    {
        var config = await ConfigureAsync("mtls", caCert: null, ClientCertPath, ClientKeyPath);

        Assert.Equal(SecurityProtocol.Ssl, config.SecurityProtocol);
        Assert.Equal(ClientCertPath, config.SslCertificateLocation);
        Assert.Equal(ClientKeyPath, config.SslKeyLocation);

        Assert.Null(config.SslCaLocation);
        Assert.False(
            HasKey(config, "ssl.ca.location"),
            "ssl.ca.location is present in the emitted configuration despite no caCert being declared");
    }

    /// <summary>
    /// Mints a <see cref="SecretString"/> the only way a test assembly outside
    /// <c>Vouchfx.Engine.Abstractions.Tests</c> can: through a public resolver. The constructor
    /// is <see langword="internal"/> and <c>InternalsVisibleTo</c> is not granted here, which is
    /// the point — the load path cannot fabricate one either.
    /// </summary>
    /// <remarks>
    /// The name is GENERATED rather than taken from the caller, and the variable is set and
    /// cleared inside this method, so nothing outlives the call and no two callers can collide.
    /// Environment variables are process-global and xUnit runs test classes in parallel, so a
    /// caller-supplied literal makes uniqueness a convention a reader has to check rather than a
    /// property of the code — and the failure it permits is intermittent, not outright. Same
    /// construction as <c>TestCertificateAuthority.UniqueClientKeyPassphraseVariableName()</c> and
    /// <c>MqPublishKafkaEmitTests</c>'s own missing-secret name; neither project references
    /// <c>Vouchfx.TestSupport</c>, and one string does not justify a project reference.
    /// </remarks>
    private static SecretString MintSecret(string value)
    {
        var variableName = "VOUCHFX_TEST_CKP_" + Guid.NewGuid().ToString("N");

        Environment.SetEnvironmentVariable(variableName, value);
        try
        {
            return new EnvironmentSecretResolver().Resolve(variableName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    /// <summary>
    /// client-key-password REQ-008: a target declaring <c>clientKeyPassword</c> reaches librdkafka
    /// as <c>ssl.key.password</c>, so an ENCRYPTED client private key works. librdkafka accepts a
    /// passphrase only as characters — there is no object form — which is why the value is
    /// unwrapped at all.
    /// </summary>
    [Fact]
    public async Task Mtls_WithDeclaredClientKeyPassword_SetsSslKeyPassword()
    {
        var config = await ConfigureAsync(
            "mtls",
            CaPath,
            ClientCertPath,
            ClientKeyPath,
            clientKeyPassword: MintSecret(KeyPassphrase));

        Assert.Equal(SecurityProtocol.Ssl, config.SecurityProtocol);
        Assert.Equal(ClientCertPath, config.SslCertificateLocation);
        Assert.Equal(ClientKeyPath, config.SslKeyLocation);
        Assert.Equal(KeyPassphrase, config.SslKeyPassword);
    }

    /// <summary>
    /// The REGRESSION GUARD REQ-008 names explicitly: with no <c>clientKeyPassword</c> declared,
    /// <c>ssl.key.password</c> is absent from the configuration's own key set. Every suite that
    /// predates this feature is in this arm, so a non-null default here would change all of them.
    /// </summary>
    /// <remarks>
    /// <strong>What this pins is the OUTCOME, not the mechanism.</strong> Measured: removing the
    /// helper's <c>!= null</c> guard and assigning unconditionally leaves this test GREEN, because
    /// <c>ClientConfig</c> is a keyed property bag and assigning null REMOVES the key. The guard is
    /// therefore insurance against that library behaviour CHANGING, which no unit test written
    /// against the current library can pre-empt — stated here so the guard is not read as something
    /// this test enforces, and not covered by adding a test that cannot exist.
    /// </remarks>
    [Fact]
    public async Task Mtls_WithNoDeclaredClientKeyPassword_LeavesSslKeyPasswordEntirelyUnset()
    {
        var config = await ConfigureAsync("mtls", CaPath, ClientCertPath, ClientKeyPath);

        Assert.Equal(SecurityProtocol.Ssl, config.SecurityProtocol);
        Assert.Equal(ClientCertPath, config.SslCertificateLocation);
        Assert.Equal(ClientKeyPath, config.SslKeyLocation);

        Assert.Null(config.SslKeyPassword);
        Assert.False(
            HasKey(config, "ssl.key.password"),
            "ssl.key.password is present in the emitted configuration despite no clientKeyPassword "
            + "being declared");
    }

    /// <summary>
    /// <strong>THIS TEST IS A GATE, not an illustration.</strong> REQ-008 makes the passphrase read
    /// UNCONDITIONAL, and nothing else can enforce that: the emitted text lives in a
    /// <c>const string</c> whose literal the v1 SDK contract golden does not pin, so
    /// <c>SdkContractFreezeTests</c> is blind to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why the read has to happen even when the caller wants nothing from it: on the production
    /// material <c>ClientKeyPassword</c> is lazy, and every check the accessor performs on a
    /// declared passphrase — the refusal of one against an unencrypted key, a null accessor, an
    /// unresolvable reference, an empty resolved value — runs on FIRST READ of that property.
    /// Reading <c>ClientKeyPath</c> does not trigger any of it. So a helper that reads only the
    /// paths, or that guards the passphrase read behind a condition on something else, leaves
    /// librdkafka outside all of them.
    /// </para>
    /// <para>
    /// The stub's getter therefore throws while <c>clientCert</c> and <c>clientKey</c> are both
    /// present and valid — the arm in which a helper could plausibly decide it had everything it
    /// needed.
    /// </para>
    /// <para>
    /// The mutation this test uniquely catches is the read being made CONDITIONAL — guarded behind
    /// a test on some other field, or on whether the caller looks like it wants a passphrase. An
    /// outright deletion also reddens
    /// <see cref="Mtls_WithDeclaredClientKeyPassword_SetsSslKeyPassword"/>, since deleting the read
    /// forces deleting the assignment that consumes it; an earlier version of this remark claimed
    /// sole coverage of that case and was wrong.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Mtls_WhenReadingTheClientKeyPasswordThrows_TheHelperPropagatesIt()
    {
        // The message models REQ-006's real refusal: a passphrase declared against a key that
        // turns out not to be encrypted.
        var ex = await Assert.ThrowsAsync<SecurityMaterialException>(
            () => ConfigureAsync(
                "mtls",
                CaPath,
                ClientCertPath,
                ClientKeyPath,
                clientKeyPasswordThrows:
                    "environment.security.broker.certificates: 'clientKeyPassword' is declared, but "
                    + "the key at 'clientKey' is not encrypted."));

        Assert.Contains("is not encrypted", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>THE PLACEMENT GATE.</strong> REQ-008 requires the passphrase read to sit BEFORE the
    /// client-identity guard, and the sibling gate above does not pin that — measured: moving the
    /// read below the guard leaves the whole assembly green. This test closes that hole.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The arm is a passphrase declared with NO <c>clientKey</c> — the accessor's EDGE-004 case,
    /// which reaches the helper only through a direct engine embedder, the schema requiring the
    /// <c>clientCert</c>/<c>clientKey</c> pair under <c>mtls</c>. Both diagnostics are available
    /// and they are not equally useful: the accessor's names the offending field and says what to
    /// do about it, where the helper's generic "requires a client identity" reports only that
    /// something is missing. Reading the passphrase first is what decides which one an embedder
    /// sees.
    /// </para>
    /// <para>
    /// The assertion is therefore two-sided on purpose. The positive half alone would pass if the
    /// helper somehow raised both; the negative half is what fails the instant the read moves below
    /// the guard, because the guard then wins and never reaches the property at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Mtls_WithAPassphraseAndNoClientKey_SurfacesThePassphraseDiagnostic()
    {
        var ex = await Assert.ThrowsAsync<SecurityMaterialException>(
            () => ConfigureAsync(
                "mtls",
                CaPath,
                ClientCertPath,
                clientKey: null,
                clientKeyPasswordThrows:
                    "environment.security.broker.certificates: 'clientKeyPassword' is declared "
                    + "without a matching 'clientKey'."));

        Assert.Contains("without a matching 'clientKey'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("requires a client identity", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>profile: tls</c> sets <c>SecurityProtocol = Ssl</c> and presents NO client identity —
    /// neither <c>ssl.certificate.location</c> nor <c>ssl.key.location</c> reaches the client.
    /// </summary>
    [Fact]
    public async Task Tls_SetsProtocolAndPresentsNoClientIdentity()
    {
        var config = await ConfigureAsync("tls", CaPath, clientCert: null, clientKey: null);

        Assert.Equal(SecurityProtocol.Ssl, config.SecurityProtocol);
        Assert.Equal(CaPath, config.SslCaLocation);
        Assert.False(HasKey(config, "ssl.certificate.location"));
        Assert.False(HasKey(config, "ssl.key.location"));
    }

    /// <summary>
    /// A target that declares NO security block leaves the configuration byte-for-byte the
    /// pre-feature plaintext one: no protocol, no locations, nothing.
    /// </summary>
    [Fact]
    public async Task NoDeclaredSecurity_LeavesTheConfigurationUntouched()
    {
        // The suite declares security on "events"; THIS step targets a different name, so the
        // accessor resolves null for it and the helper must configure nothing at all.
        var config = await ConfigureAsync(
            "mtls", CaPath, ClientCertPath, ClientKeyPath, stepTarget: "other", declaredTarget: "events");

        Assert.False(HasKey(config, "security.protocol"));
        Assert.False(HasKey(config, "ssl.ca.location"));
        Assert.False(HasKey(config, "ssl.certificate.location"));
        Assert.False(HasKey(config, "ssl.key.location"));
    }

    // ── Fail-closed arms ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>profile: mtls</c> with no client identity resolved throws rather than connecting
    /// unauthenticated over SSL. Against a broker whose listener requests but does not require a
    /// client certificate, connecting would be a green suite that authenticated nothing.
    /// </summary>
    [Fact]
    public async Task Mtls_WithNoResolvedClientIdentity_Throws()
    {
        var ex = await Assert.ThrowsAsync<SecurityMaterialException>(
            () => ConfigureAsync("mtls", CaPath, clientCert: null, clientKey: null));

        Assert.Contains("requires a client identity", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>profile: tls</c> declared alongside client material is refused rather than
    /// reinterpreted: the two readings differ by exactly whether the run authenticates.
    /// </summary>
    [Fact]
    public async Task Tls_WithContradictoryClientMaterial_Throws()
    {
        var ex = await Assert.ThrowsAsync<SecurityMaterialException>(
            () => ConfigureAsync("tls", CaPath, ClientCertPath, ClientKeyPath));

        Assert.Contains("presents no client identity", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unrecognised profile FAILS CLOSED. REQ-019 makes the discriminator open, so a later
    /// profile — SASL, Kerberos, OAuth bearer, each of which Kafka reaches through a different
    /// <c>SecurityProtocol</c> value than <c>Ssl</c> — must not inherit mutual-TLS semantics from
    /// a which-fields-are-set test.
    /// </summary>
    [Fact]
    public async Task UnknownProfile_Throws()
    {
        var ex = await Assert.ThrowsAsync<SecurityMaterialException>(
            () => ConfigureAsync("acme.sasl-scram", CaPath, ClientCertPath, ClientKeyPath));

        Assert.Contains("has no defined Kafka client behaviour", ex.Message, StringComparison.Ordinal);
    }

    // ── Stub accessor ─────────────────────────────────────────────────────────────────────

    private sealed class StubAccessor : ISecurityConfigurationAccessor
    {
        private readonly string _target;
        private readonly ISecurityConfiguration _configuration;

        internal StubAccessor(
            string target,
            string profile,
            string? caCert,
            string? clientCert,
            string? clientKey,
            SecretString? clientKeyPassword = null,
            string? clientKeyPasswordThrows = null)
        {
            _target = target;
            var material = caCert is null && clientCert is null && clientKey is null
                ? null
                : new StubMaterial(
                    caCert, clientCert, clientKey, clientKeyPassword, clientKeyPasswordThrows);
            _configuration = new StubConfiguration(profile, material);
        }

        public ISecurityConfiguration? For(string targetName) =>
            string.Equals(targetName, _target, StringComparison.Ordinal) ? _configuration : null;
    }

    private sealed record StubConfiguration(string Profile, StubMaterial? Material) : ISecurityConfiguration
    {
        ISecurityCertificateMaterial? ISecurityConfiguration.Certificates => Material;
    }

    private sealed class StubMaterial : ISecurityCertificateMaterial
    {
        private readonly SecretString? _clientKeyPassword;
        private readonly string? _clientKeyPasswordThrows;

        internal StubMaterial(
            string? caCert,
            string? clientCert,
            string? clientKey,
            SecretString? clientKeyPassword = null,
            string? clientKeyPasswordThrows = null)
        {
            CaCertificatePath = caCert;
            ClientCertificatePath = clientCert;
            ClientKeyPath = clientKey;
            _clientKeyPassword = clientKeyPassword;
            _clientKeyPasswordThrows = clientKeyPasswordThrows;
        }

        public string? CaCertificatePath { get; }

        public string? ClientCertificatePath { get; }

        public string? ClientKeyPath { get; }

        /// <summary>
        /// Modelled as a PROPERTY WITH A BODY, not an auto-property, because the production
        /// implementation is one: <c>SecurityConfigurationAccessor.ResolveClientKeyPassword</c>
        /// runs on first read, opens the declared key file and can throw. A stub that only
        /// returned a stored value would let a consumer that never reads the property look
        /// identical to one that does.
        /// </summary>
        /// <remarks>
        /// The throwing arm carries the CALLER'S message rather than one fixed here, because the
        /// two tests that use it model two DIFFERENT accessor refusals and one of them asserts on
        /// which message surfaces. A single canned message would make that assertion vacuous.
        /// </remarks>
        public SecretString? ClientKeyPassword =>
            _clientKeyPasswordThrows is null
                ? _clientKeyPassword
                : throw new SecurityMaterialException(_clientKeyPasswordThrows);

        // The OBJECT view is deliberately never populated here: librdkafka accepts only paths, so
        // a helper that reached for a certificate object would fail this test rather than quietly
        // working against a fixture that supplied both views.
        public System.Security.Cryptography.X509Certificates.X509Certificate2? CaCertificate => null;

        public System.Security.Cryptography.X509Certificates.X509Certificate2? ClientCertificate => null;

        public bool TrustsRemoteCertificate(
            System.Security.Cryptography.X509Certificates.X509Certificate2? remoteCertificate,
            System.Security.Cryptography.X509Certificates.X509Chain? platformBuiltChain,
            System.Net.Security.SslPolicyErrors sslPolicyErrors) => false;
    }
}
